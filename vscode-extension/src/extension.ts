import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { httpPost } from './httpClient';
import { tr } from './i18n';
import { readMoneySettings } from './money';
import { cleanupOldRuntimes, prepareRuntime } from './runtimeManager';
import {
  ensureServer,
  RunningServer,
  serverUrl,
  stopOwnedServer,
} from './serverManager';
import { StatusBarController } from './statusBar';

let statusBar: StatusBarController | undefined;
let fallbackItem: vscode.StatusBarItem | undefined;
let server: RunningServer | undefined;
let output: vscode.OutputChannel | undefined;
let logFile: string | undefined;

const LOG_MAX_BYTES = 1024 * 1024;

function rotateLogIfNeeded(): void {
  if (!logFile) {
    return;
  }

  try {
    if (fs.statSync(logFile).size < LOG_MAX_BYTES) {
      return;
    }
  } catch {
    return;
  }

  try {
    fs.rmSync(`${logFile}.1`, { force: true });
    fs.renameSync(logFile, `${logFile}.1`);
  } catch {
    try {
      fs.truncateSync(logFile, 0);
    } catch {
      // Diagnostics only.
    }
  }
}

function log(message: string): void {
  output?.appendLine(message);
  if (!logFile) {
    return;
  }
  try {
    rotateLogIfNeeded();
    fs.appendFileSync(logFile, `${new Date().toISOString()} ${message}\n`, 'utf8');
  } catch {
    // Diagnostics only.
  }
}

function resolveStatsRange(value: string): StatsRange {
  switch (value) {
    case 'today':
      return { index: 0, label: tr('当天', 'Today') };
    case '30d':
      return { index: 3, label: tr('近 30 天', 'Last 30 days') };
    case 'all':
      return { index: 4, label: tr('全部', 'All') };
    default:
      return { index: 2, label: tr('近 7 天', 'Last 7 days') };
  }
}

function currentWorkspaceRoots(): string[] {
  return (vscode.workspace.workspaceFolders ?? []).map((folder) => folder.uri.fsPath);
}

function defaultAgentHostDirectories(): string[] {
  const home = os.homedir();
  if (process.platform === 'win32') {
    const base = process.env.APPDATA ?? path.join(home, 'AppData', 'Roaming');
    return ['Code', 'Code - Insiders'].map((product) =>
      path.join(base, product, 'User', 'globalStorage'),
    );
  }
  if (process.platform === 'darwin') {
    const base = path.join(home, 'Library', 'Application Support');
    return ['Code', 'Code - Insiders'].map((product) =>
      path.join(base, product, 'User', 'globalStorage'),
    );
  }

  const base = process.env.XDG_CONFIG_HOME ?? path.join(home, '.config');
  return ['Code', 'Code - Insiders'].map((product) =>
    path.join(base, product, 'User', 'globalStorage'),
  );
}

const METADATA_DEBOUNCE_MS = 500;
const AGENT_HOST_THROTTLE_MS = 15_000;

interface LocalImportResult {
  scanned: number;
  matched: number;
  inserted: number;
  skipped: number;
  errors: string[];
}

function localTraceDatabases(): string[] {
  const storageDirectories = defaultAgentHostDirectories();
  const candidates = [
    ...storageDirectories.map((directory) =>
      path.join(directory, 'GitHub.copilot-chat', 'agent-traces.db'),
    ),
    ...storageDirectories.map((directory) =>
      path.join(
        path.dirname(path.dirname(directory)),
        'agent-host',
        'otel',
        'agent-host-traces.db',
      ),
    ),
  ];

  return [...new Set(candidates)].filter((candidate) => fs.existsSync(candidate));
}

async function notifyMetadataChanged(): Promise<void> {
  const url = server?.url;
  if (url) {
    try {
      const response = await httpPost(`${url}/api/metadata-changed`, 1500);
      if (response.status >= 200 && response.status < 300) {
        return;
      }
    } catch {
      // Fall back to resubscribing below.
    }
  }

  statusBar?.refresh();
}

function registerMetadataWatchers(context: vscode.ExtensionContext): void {
  const targets: Array<[string, string, number]> = [];
  const sessionStateDirectory =
    process.env.TOKENTRACKER_SESSION_STATE_DIR ??
    path.join(os.homedir(), '.copilot', 'session-state');
  targets.push([sessionStateDirectory, '**/workspace.yaml', 0]);

  const configuredDatabase = process.env.TOKENTRACKER_AGENT_HOST_DB;
  if (configuredDatabase) {
    targets.push([
      path.dirname(configuredDatabase),
      `${path.basename(configuredDatabase)}*`,
      AGENT_HOST_THROTTLE_MS,
    ]);
  } else {
    for (const directory of defaultAgentHostDirectories()) {
      if (fs.existsSync(path.join(directory, 'agent-host.db'))) {
        targets.push([directory, 'agent-host.db*', AGENT_HOST_THROTTLE_MS]);
      }
    }
  }

  interface RefreshSchedule {
    timer?: NodeJS.Timeout;
    lastRefreshAt: number;
  }
  const schedules = new Map<number, RefreshSchedule>();

  for (const [base, pattern, throttleMs] of targets) {
    try {
      const watcher = vscode.workspace.createFileSystemWatcher(
        new vscode.RelativePattern(vscode.Uri.file(base), pattern),
      );
      const changed = (uri: vscode.Uri) => {
        // SQLite shared-memory locks are not metadata changes.
        if (uri.fsPath.endsWith('-shm')) {
          return;
        }

        const schedule =
          schedules.get(throttleMs) ?? { lastRefreshAt: 0 };
        schedules.set(throttleMs, schedule);
        if (schedule.timer) {
          clearTimeout(schedule.timer);
        }
        const wait = Math.max(
          METADATA_DEBOUNCE_MS,
          throttleMs - (Date.now() - schedule.lastRefreshAt),
        );
        schedule.timer = setTimeout(() => {
          schedule.timer = undefined;
          schedule.lastRefreshAt = Date.now();
          log(`session metadata changed: ${uri.fsPath}`);
          void notifyMetadataChanged();
        }, wait);
      };

      watcher.onDidChange(changed);
      watcher.onDidCreate(changed);
      watcher.onDidDelete(changed);
      context.subscriptions.push(watcher);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      log(`metadata watcher failed: ${base}\\${pattern} (${message})`);
    }
  }

  log(
    `metadata watchers: ${targets
      .map(([base, pattern, throttle]) => `${base}\\${pattern}${throttle ? ` (${throttle}ms)` : ''}`)
      .join(' | ')}`,
  );
}

function disposeStatusBars(): void {
  statusBar?.dispose();
  statusBar = undefined;
  fallbackItem?.dispose();
  fallbackItem = undefined;
}

function showStatusItem(
  text: string,
  tooltip: string,
  command: string,
): void {
  fallbackItem?.dispose();
  fallbackItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 99);
  fallbackItem.name = 'Token Tracker';
  fallbackItem.text = text;
  fallbackItem.tooltip = tooltip;
  fallbackItem.command = command;
  fallbackItem.show();
}

interface StatsRange {
  index: number;
  label: string;
}

interface DownloadSource {
  baseUrl?: string;
  githubRepo?: string;
}

function resolveProxyUrl(): string {
  const configured = vscode.workspace
    .getConfiguration('http')
    .get<string>('proxy', '')
    .trim();
  if (configured && configured.toLowerCase() !== 'direct') {
    return configured;
  }

  return (
    process.env.HTTPS_PROXY ??
    process.env.https_proxy ??
    process.env.HTTP_PROXY ??
    process.env.http_proxy ??
    ''
  ).trim();
}

function resolveDownloadSource(
  context: vscode.ExtensionContext,
  override: string,
): DownloadSource {
  const trimmed = override.trim();
  if (trimmed) {
    return { baseUrl: trimmed.replace(/\/+$/, '') };
  }

  const repository = context.extension.packageJSON.repository as
    | string
    | { url?: string }
    | undefined;
  const raw = typeof repository === 'string' ? repository : repository?.url;
  const url = raw?.replace(/^git\+/, '').replace(/\.git$/, '').replace(/\/+$/, '');
  if (!url) {
    throw new Error(
      tr(
        '扩展缺少 repository 配置，无法自动下载服务端',
        'The extension has no repository configuration and cannot download the server.',
      ),
    );
  }

  return { githubRepo: url };
}

async function startServerAndStatus(
  context: vscode.ExtensionContext,
  port: number,
  autoStart: boolean,
  range: StatsRange,
  idleExitSeconds: number,
): Promise<void> {
  const configuration = vscode.workspace.getConfiguration('tokentracker');
  const downloadSource = resolveDownloadSource(
    context,
    configuration.get<string>('serverDownloadBaseUrl', ''),
  );
  const proxyUrl = resolveProxyUrl();
  log(`download proxy: ${proxyUrl ? 'enabled' : 'disabled'}`);

  const runtime = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Window,
      title: tr(
        'Token Tracker 正在准备服务端',
        'Token Tracker is preparing the server',
      ),
      cancellable: false,
    },
    (progress) =>
      prepareRuntime(context.extensionPath, context.globalStorageUri.fsPath, {
        ...downloadSource,
        proxyUrl,
        onProgress: (message) => progress.report({ message }),
      }),
  );

  log(
    `runtime: build=${runtime.buildId} rid=${runtime.rid} ` +
      `source=${runtime.source} release=${runtime.releaseTag} ` +
      `version=${runtime.serverVersion} ` +
      `dir=${runtime.runtimeDirectory} data=${runtime.dataDirectory}`,
  );

  const running = await ensureServer({
    storagePath: context.globalStorageUri.fsPath,
    port,
    autoStart,
    executablePath: runtime.executablePath,
    dataDirectory: runtime.dataDirectory,
    buildId: runtime.buildId,
    idleExitSeconds,
  });

  if (running.outdated) {
    log('server build differs and is externally managed');
    void vscode.window.showWarningMessage(
      tr(
        'Token Tracker 服务版本与扩展不一致，且该实例由外部启动，未自动替换。',
        'The running Token Tracker server version differs and was started externally, so it was not replaced.',
      ),
    );
  }

  server = running;
  log(
    `server ready: ${running.url} startedHere=${running.startedHere} ` +
      `version=${running.info?.version ?? 'unknown'} pid=${running.pid ?? 'external'}`,
  );

  const cleanup = cleanupOldRuntimes(
    context.globalStorageUri.fsPath,
    runtime.buildId,
  );
  log(
    `runtime cleanup: removed=[${cleanup.removed.join(', ')}] ` +
      `failed=[${cleanup.failed.join(', ')}]`,
  );

  const roots = currentWorkspaceRoots();
  log(`workspace roots: ${roots.join(' | ') || '(none)'}`);

  disposeStatusBars();
  let lastSnapshotDetail = '';
  statusBar = new StatusBarController(
    port,
    range.index,
    range.label,
    roots,
    readMoneySettings(),
    (detail) => {
    if (detail !== lastSnapshotDetail) {
      lastSnapshotDetail = detail;
      log(`status snapshot: ${detail}`);
    }
  });
  statusBar.start();
  context.subscriptions.push(statusBar);
}

export async function activate(context: vscode.ExtensionContext): Promise<TokenTrackerApi> {
  output = vscode.window.createOutputChannel('Token Tracker');
  context.subscriptions.push(output);

  try {
    fs.mkdirSync(context.globalStorageUri.fsPath, { recursive: true });
    logFile = path.join(context.globalStorageUri.fsPath, 'extension.log');
  } catch {
    logFile = undefined;
  }

  log('Token Tracker activating...');

  const configuration = vscode.workspace.getConfiguration('tokentracker');
  const port = configuration.get<number>('port', 4318);
  const autoStart = configuration.get<boolean>('autoStart', true);
  const range = resolveStatsRange(
    configuration.get<string>('statsRange', '7d'),
  );
  const idleExitSeconds = Math.max(
    0,
    configuration.get<number>('idleExitSeconds', 60),
  );
  log(`stats range: ${range.label} (${range.index})`);

  context.subscriptions.push(
    vscode.commands.registerCommand('tokentracker.openDashboard', () => {
      void vscode.env.openExternal(vscode.Uri.parse(serverUrl(port)));
    }),
    vscode.commands.registerCommand('tokentracker.refreshStatus', () => {
      statusBar?.refresh();
    }),
    vscode.commands.registerCommand('tokentracker.showServerInfo', async () => {
      if (!server) {
        void vscode.window.showWarningMessage(
          tr('Token Tracker 服务未运行', 'Token Tracker server is not running'),
        );
        return;
      }

      const version = server.info
        ? `Token Tracker Server ${server.info.version}`
        : tr(
            'Token Tracker Server（旧版本，无 /api/version）',
            'Token Tracker Server (old build without /api/version)',
          );
      void vscode.window.showInformationMessage(
        tr(
          `${version} @ ${server.url}，内置启动=${server.startedHere}`,
          `${version} @ ${server.url}, started by extension=${server.startedHere}`,
        ),
      );
    }),
    vscode.commands.registerCommand('tokentracker.configureOtel', async () => {
      const currentPort = vscode.workspace
        .getConfiguration('tokentracker')
        .get<number>('port', port);
      const endpoint = `http://127.0.0.1:${currentPort}`;
      const updates: Array<[string, string, string | boolean]> = [
        ['github.copilot.chat.otel', 'enabled', true],
        ['github.copilot.chat.otel', 'exporterType', 'otlp-http'],
        ['github.copilot.chat.otel', 'protocol', 'http/json'],
        ['github.copilot.chat.otel', 'otlpEndpoint', endpoint],
        ['github.copilot.chat.otel', 'captureContent', false],
        ['chat.agentHost.otel', 'enabled', true],
        ['chat.agentHost.otel', 'exporterType', 'otlp-http'],
        ['chat.agentHost.otel', 'otlpProtocol', 'http/json'],
        ['chat.agentHost.otel', 'otlpEndpoint', endpoint],
        ['chat.agentHost.otel', 'captureContent', false],
      ];

      try {
        for (const [section, key, value] of updates) {
          await vscode.workspace
            .getConfiguration(section)
            .update(key, value, vscode.ConfigurationTarget.Global);
        }

        log('copilot OTel settings written for extension host and agent host');
        void vscode.window.showInformationMessage(
          tr(
            '已写入 Token Tracker 的 Copilot OTel 配置（扩展宿主 + Agent Host）。请完整退出并重新打开 VS Code，Agent Host 设置才会生效。',
            'Copilot OTel settings for both the extension host and Agent Host were written. Fully quit and reopen VS Code so Agent Host picks them up.',
          ),
        );
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log(`write OTel settings failed: ${message}`);
        void vscode.window.showErrorMessage(
          tr(
            `写入 OTel 设置失败：${message}`,
            `Failed to write OTel settings: ${message}`,
          ),
        );
      }
    }),
    vscode.commands.registerCommand('tokentracker.importLocalTraces', async () => {
      if (!server) {
        void vscode.window.showWarningMessage(
          tr('Token Tracker 服务未运行', 'Token Tracker server is not running'),
        );
        return;
      }

      const paths = localTraceDatabases();
      if (paths.length === 0) {
        void vscode.window.showInformationMessage(
          tr(
            '未找到本地 Copilot OTel 数据库。请先开启 Copilot 或 Agent Host 的 OTel，并产生一次调用。',
            'No local Copilot OTel database was found. Enable OTel for Copilot or Agent Host and run a call first.',
          ),
        );
        return;
      }

      try {
        const response = await httpPost(
          `${server.url}/api/import-local`,
          120_000,
          { paths },
        );
        if (response.status === 404) {
          throw new Error(
            tr(
              '服务端版本过旧，请更新并重启 Token Tracker 服务',
              'The server is too old. Update and restart Token Tracker.',
            ),
          );
        }
        if (response.status < 200 || response.status >= 300) {
          throw new Error(response.body || `HTTP ${response.status}`);
        }

        const result = JSON.parse(response.body) as LocalImportResult;
        log(
          `local import: files=${paths.length} scanned=${result.scanned} ` +
            `matched=${result.matched} inserted=${result.inserted} skipped=${result.skipped}`,
        );
        void vscode.window.showInformationMessage(
          tr(
            `已导入 ${result.inserted} 条调用（扫描 ${result.scanned}，匹配 ${result.matched}，跳过重复 ${result.skipped}）。`,
            `Imported ${result.inserted} calls (scanned ${result.scanned}, matched ${result.matched}, skipped ${result.skipped} duplicates).`,
          ),
        );
        if (result.errors.length > 0) {
          void vscode.window.showWarningMessage(
            tr(
              `部分数据库导入失败：${result.errors.join('; ')}`,
              `Some databases could not be imported: ${result.errors.join('; ')}`,
            ),
          );
        }
        statusBar?.refresh();
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log(`local import failed: ${message}`);
        void vscode.window.showErrorMessage(
          tr(
            `导入本地 OTel 数据失败：${message}`,
            `Failed to import local OTel data: ${message}`,
          ),
        );
      }
    }),
    vscode.commands.registerCommand('tokentracker.startServer', async () => {
      try {
        await startServerAndStatus(context, port, true, range, idleExitSeconds);
        log('server started by command');
        void vscode.window.showInformationMessage(
          tr('Token Tracker 服务已启动', 'Token Tracker server started'),
        );
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log(`start server failed: ${message}`);
        showStatusItem(
          tr(
            '$(warning) Token Tracker 未运行',
            '$(warning) Token Tracker is not running',
          ),
          message,
          'tokentracker.startServer',
        );
        void vscode.window.showErrorMessage(
          tr(
            `Token Tracker 启动失败：${message}`,
            `Token Tracker failed to start: ${message}`,
          ),
        );
      }
    }),
    vscode.commands.registerCommand('tokentracker.stopServer', async () => {
      const result = await stopOwnedServer(context.globalStorageUri.fsPath, port);
      if (result.stopped) {
        log(`server stopped (pid ${result.pid})`);
        disposeStatusBars();
        server = undefined;
        showStatusItem(
          tr(
            '$(debug-stop) Token Tracker: 服务已停止',
            '$(debug-stop) Token Tracker: server stopped',
          ),
          tr(
            '点击启动 Token Tracker 服务',
            'Click to start the Token Tracker server',
          ),
          'tokentracker.startServer',
        );
        void vscode.window.showInformationMessage(
          tr('Token Tracker 服务已停止', 'Token Tracker server stopped'),
        );
        return;
      }

      if (result.reason === 'external') {
        void vscode.window.showWarningMessage(
          tr(
            '当前 Token Tracker 服务不是由扩展启动（可能是外部实例），未停止。',
            'The running Token Tracker server was not started by this extension (likely external) and was not stopped.',
          ),
        );
        return;
      }

      if (result.reason === 'not-running') {
        log('stop server: not running');
        disposeStatusBars();
        server = undefined;
        showStatusItem(
          tr(
            '$(debug-stop) Token Tracker: 服务已停止',
            '$(debug-stop) Token Tracker: server stopped',
          ),
          tr(
            '点击启动 Token Tracker 服务',
            'Click to start the Token Tracker server',
          ),
          'tokentracker.startServer',
        );
      }
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      const updated = currentWorkspaceRoots();
      log(`workspace roots changed: ${updated.join(' | ') || '(none)'}`);
      statusBar?.setWorkspaceRoots(updated);
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (
        event.affectsConfiguration('tokentracker.costUnit') ||
        event.affectsConfiguration('tokentracker.usdRate') ||
        event.affectsConfiguration('tokentracker.creditRate')
      ) {
        statusBar?.setMoneySettings(readMoneySettings());
      }
    }),
  );

  registerMetadataWatchers(context);

  try {
    await startServerAndStatus(context, port, autoStart, range, idleExitSeconds);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    log(`server unavailable: ${message}`);
    showStatusItem(
      tr(
        '$(warning) Token Tracker 未运行',
        '$(warning) Token Tracker is not running',
      ),
      message,
      'tokentracker.startServer',
    );
    void vscode.window.showWarningMessage(
      tr(
        `Token Tracker 服务不可用：${message}`,
        `Token Tracker server unavailable: ${message}`,
      ),
    );
  }

  return {
    getServerUrl: () => server?.url,
    getVersion: () => server?.info,
    openDashboard: async () => {
      await vscode.commands.executeCommand('tokentracker.openDashboard');
    },
    refresh: () => statusBar?.refresh(),
    startServer: async () => {
      await vscode.commands.executeCommand('tokentracker.startServer');
    },
    stopServer: async () => {
      await vscode.commands.executeCommand('tokentracker.stopServer');
    },
  };
}

export function deactivate(): void {
  disposeStatusBars();
  server = undefined;
  // The shared Token Tracker server is intentionally left running for other windows.
}

export interface TokenTrackerApi {
  getServerUrl(): string | undefined;
  getVersion(): unknown;
  openDashboard(): Promise<void>;
  refresh(): void;
  startServer(): Promise<void>;
  stopServer(): Promise<void>;
}
