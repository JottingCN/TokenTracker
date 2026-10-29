import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { httpPost } from './httpClient';
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
      return { index: 0, label: '当天' };
    case '30d':
      return { index: 3, label: '近 30 天' };
    case 'all':
      return { index: 4, label: '全部' };
    default:
      return { index: 2, label: '近 7 天' };
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

function resolveDownloadBase(
  context: vscode.ExtensionContext,
  override: string,
): string {
  if (override.trim()) {
    return override.trim().replace(/\/+$/, '');
  }

  const repository = context.extension.packageJSON.repository as
    | string
    | { url?: string }
    | undefined;
  const raw = typeof repository === 'string' ? repository : repository?.url;
  const url = raw?.replace(/^git\+/, '').replace(/\.git$/, '').replace(/\/+$/, '');
  if (!url) {
    throw new Error('扩展缺少 repository 配置，无法自动下载服务端');
  }

  return `${url}/releases/download/v${context.extension.packageJSON.version}`;
}

async function startServerAndStatus(
  context: vscode.ExtensionContext,
  port: number,
  autoStart: boolean,
  range: StatsRange,
  idleExitSeconds: number,
): Promise<void> {
  const configuration = vscode.workspace.getConfiguration('tokentracker');
  const downloadBaseUrl = resolveDownloadBase(
    context,
    configuration.get<string>('serverDownloadBaseUrl', ''),
  );

  const runtime = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Window,
      title: 'Token Tracker 正在准备服务端',
      cancellable: false,
    },
    (progress) =>
      prepareRuntime(context.extensionPath, context.globalStorageUri.fsPath, {
        version: context.extension.packageJSON.version,
        downloadBaseUrl,
        onProgress: (message) => progress.report({ message }),
      }),
  );

  log(
    `runtime: build=${runtime.buildId} rid=${runtime.rid} ` +
      `source=${runtime.source} version=${runtime.serverVersion} ` +
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
      'Token Tracker 服务版本与扩展不一致，且该实例由外部启动，未自动替换。',
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
  statusBar = new StatusBarController(port, range.index, range.label, roots, (detail) => {
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
        void vscode.window.showWarningMessage('Token Tracker 服务未运行');
        return;
      }

      const version = server.info
        ? `Token Tracker Server ${server.info.version}`
        : 'Token Tracker Server（旧版本，无 /api/version）';
      void vscode.window.showInformationMessage(
        `${version} @ ${server.url}，内置启动=${server.startedHere}`,
      );
    }),
    vscode.commands.registerCommand('tokentracker.startServer', async () => {
      try {
        await startServerAndStatus(context, port, true, range, idleExitSeconds);
        log('server started by command');
        void vscode.window.showInformationMessage('Token Tracker 服务已启动');
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log(`start server failed: ${message}`);
        showStatusItem('$(warning) Token Tracker 未运行', message, 'tokentracker.startServer');
        void vscode.window.showErrorMessage(`Token Tracker 启动失败：${message}`);
      }
    }),
    vscode.commands.registerCommand('tokentracker.stopServer', async () => {
      const result = await stopOwnedServer(context.globalStorageUri.fsPath, port);
      if (result.stopped) {
        log(`server stopped (pid ${result.pid})`);
        disposeStatusBars();
        server = undefined;
        showStatusItem(
          '$(debug-stop) Token Tracker: 服务已停止',
          '点击启动 Token Tracker 服务',
          'tokentracker.startServer',
        );
        void vscode.window.showInformationMessage('Token Tracker 服务已停止');
        return;
      }

      if (result.reason === 'external') {
        void vscode.window.showWarningMessage(
          '当前 Token Tracker 服务不是由扩展启动（可能是外部实例），未停止。',
        );
        return;
      }

      if (result.reason === 'not-running') {
        log('stop server: not running');
        disposeStatusBars();
        server = undefined;
        showStatusItem(
          '$(debug-stop) Token Tracker: 服务已停止',
          '点击启动 Token Tracker 服务',
          'tokentracker.startServer',
        );
      }
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      const updated = currentWorkspaceRoots();
      log(`workspace roots changed: ${updated.join(' | ') || '(none)'}`);
      statusBar?.setWorkspaceRoots(updated);
    }),
  );

  registerMetadataWatchers(context);

  try {
    await startServerAndStatus(context, port, autoStart, range, idleExitSeconds);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    log(`server unavailable: ${message}`);
    showStatusItem(
      '$(warning) Token Tracker 未运行',
      message,
      'tokentracker.startServer',
    );
    void vscode.window.showWarningMessage(`Token Tracker 服务不可用：${message}`);
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
