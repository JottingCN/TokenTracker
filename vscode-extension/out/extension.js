"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || function (mod) {
    if (mod && mod.__esModule) return mod;
    var result = {};
    if (mod != null) for (var k in mod) if (k !== "default" && Object.prototype.hasOwnProperty.call(mod, k)) __createBinding(result, mod, k);
    __setModuleDefault(result, mod);
    return result;
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const fs = __importStar(require("node:fs"));
const os = __importStar(require("node:os"));
const path = __importStar(require("node:path"));
const vscode = __importStar(require("vscode"));
const runtimeManager_1 = require("./runtimeManager");
const serverManager_1 = require("./serverManager");
const statusBar_1 = require("./statusBar");
let statusBar;
let fallbackItem;
let server;
let output;
let logFile;
function log(message) {
    output?.appendLine(message);
    if (!logFile) {
        return;
    }
    try {
        fs.appendFileSync(logFile, `${new Date().toISOString()} ${message}\n`, 'utf8');
    }
    catch {
        // Diagnostics only.
    }
}
function resolveStatsRange(value) {
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
function currentWorkspaceRoots() {
    return (vscode.workspace.workspaceFolders ?? []).map((folder) => folder.uri.fsPath);
}
const METADATA_DEBOUNCE_MS = 500;
const AGENT_HOST_THROTTLE_MS = 15_000;
function registerMetadataWatchers(context) {
    const targets = [];
    const sessionStateDirectory = process.env.TOKENTRACKER_SESSION_STATE_DIR ??
        path.join(os.homedir(), '.copilot', 'session-state');
    targets.push([sessionStateDirectory, '**/workspace.yaml', 0]);
    const configuredDatabase = process.env.TOKENTRACKER_AGENT_HOST_DB;
    if (configuredDatabase) {
        targets.push([
            path.dirname(configuredDatabase),
            `${path.basename(configuredDatabase)}*`,
            AGENT_HOST_THROTTLE_MS,
        ]);
    }
    else {
        const appData = process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming');
        for (const product of ['Code', 'Code - Insiders']) {
            const directory = path.join(appData, product, 'User', 'globalStorage');
            if (fs.existsSync(path.join(directory, 'agent-host.db'))) {
                targets.push([directory, 'agent-host.db*', AGENT_HOST_THROTTLE_MS]);
            }
        }
    }
    const schedules = new Map();
    for (const [base, pattern, throttleMs] of targets) {
        try {
            const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(vscode.Uri.file(base), pattern));
            const changed = (uri) => {
                // SQLite shared-memory locks are not metadata changes.
                if (uri.fsPath.endsWith('-shm')) {
                    return;
                }
                const schedule = schedules.get(throttleMs) ?? { lastRefreshAt: 0 };
                schedules.set(throttleMs, schedule);
                if (schedule.timer) {
                    clearTimeout(schedule.timer);
                }
                const wait = Math.max(METADATA_DEBOUNCE_MS, throttleMs - (Date.now() - schedule.lastRefreshAt));
                schedule.timer = setTimeout(() => {
                    schedule.timer = undefined;
                    schedule.lastRefreshAt = Date.now();
                    log(`session metadata changed: ${uri.fsPath}`);
                    statusBar?.refresh();
                }, wait);
            };
            watcher.onDidChange(changed);
            watcher.onDidCreate(changed);
            watcher.onDidDelete(changed);
            context.subscriptions.push(watcher);
        }
        catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            log(`metadata watcher failed: ${base}\\${pattern} (${message})`);
        }
    }
    log(`metadata watchers: ${targets
        .map(([base, pattern, throttle]) => `${base}\\${pattern}${throttle ? ` (${throttle}ms)` : ''}`)
        .join(' | ')}`);
}
function disposeStatusBars() {
    statusBar?.dispose();
    statusBar = undefined;
    fallbackItem?.dispose();
    fallbackItem = undefined;
}
function showStatusItem(text, tooltip, command) {
    fallbackItem?.dispose();
    fallbackItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 99);
    fallbackItem.name = 'Token Tracker';
    fallbackItem.text = text;
    fallbackItem.tooltip = tooltip;
    fallbackItem.command = command;
    fallbackItem.show();
}
async function startServerAndStatus(context, port, autoStart, range, idleExitSeconds) {
    const runtime = (0, runtimeManager_1.prepareRuntime)(context.extensionPath, context.globalStorageUri.fsPath);
    log(`runtime: build=${runtime.buildId} version=${runtime.serverVersion} ` +
        `dir=${runtime.runtimeDirectory} data=${runtime.dataDirectory}`);
    const running = await (0, serverManager_1.ensureServer)({
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
        void vscode.window.showWarningMessage('Token Tracker 服务版本与扩展不一致，且该实例由外部启动，未自动替换。');
    }
    server = running;
    log(`server ready: ${running.url} startedHere=${running.startedHere} ` +
        `version=${running.info?.version ?? 'unknown'} pid=${running.pid ?? 'external'}`);
    const cleanup = (0, runtimeManager_1.cleanupOldRuntimes)(context.globalStorageUri.fsPath, runtime.buildId);
    log(`runtime cleanup: removed=[${cleanup.removed.join(', ')}] ` +
        `failed=[${cleanup.failed.join(', ')}]`);
    const roots = currentWorkspaceRoots();
    log(`workspace roots: ${roots.join(' | ') || '(none)'}`);
    disposeStatusBars();
    let lastSnapshotDetail = '';
    statusBar = new statusBar_1.StatusBarController(port, range.index, range.label, roots, (detail) => {
        if (detail !== lastSnapshotDetail) {
            lastSnapshotDetail = detail;
            log(`status snapshot: ${detail}`);
        }
    });
    statusBar.start();
    context.subscriptions.push(statusBar);
}
async function activate(context) {
    output = vscode.window.createOutputChannel('Token Tracker');
    context.subscriptions.push(output);
    try {
        fs.mkdirSync(context.globalStorageUri.fsPath, { recursive: true });
        logFile = path.join(context.globalStorageUri.fsPath, 'extension.log');
    }
    catch {
        logFile = undefined;
    }
    log('Token Tracker activating...');
    const configuration = vscode.workspace.getConfiguration('tokentracker');
    const port = configuration.get('port', 4318);
    const autoStart = configuration.get('autoStart', true);
    const range = resolveStatsRange(configuration.get('statsRange', '7d'));
    const idleExitSeconds = Math.max(0, configuration.get('idleExitSeconds', 60));
    log(`stats range: ${range.label} (${range.index})`);
    context.subscriptions.push(vscode.commands.registerCommand('tokentracker.openDashboard', () => {
        void vscode.env.openExternal(vscode.Uri.parse((0, serverManager_1.serverUrl)(port)));
    }), vscode.commands.registerCommand('tokentracker.refreshStatus', () => {
        statusBar?.refresh();
    }), vscode.commands.registerCommand('tokentracker.showServerInfo', async () => {
        if (!server) {
            void vscode.window.showWarningMessage('Token Tracker 服务未运行');
            return;
        }
        const version = server.info
            ? `Token Tracker Server ${server.info.version}`
            : 'Token Tracker Server（旧版本，无 /api/version）';
        void vscode.window.showInformationMessage(`${version} @ ${server.url}，内置启动=${server.startedHere}`);
    }), vscode.commands.registerCommand('tokentracker.startServer', async () => {
        try {
            await startServerAndStatus(context, port, true, range, idleExitSeconds);
            log('server started by command');
            void vscode.window.showInformationMessage('Token Tracker 服务已启动');
        }
        catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            log(`start server failed: ${message}`);
            showStatusItem('$(warning) Token Tracker 未运行', message, 'tokentracker.startServer');
            void vscode.window.showErrorMessage(`Token Tracker 启动失败：${message}`);
        }
    }), vscode.commands.registerCommand('tokentracker.stopServer', async () => {
        const result = await (0, serverManager_1.stopOwnedServer)(context.globalStorageUri.fsPath, port);
        if (result.stopped) {
            log(`server stopped (pid ${result.pid})`);
            disposeStatusBars();
            server = undefined;
            showStatusItem('$(debug-stop) Token Tracker: 服务已停止', '点击启动 Token Tracker 服务', 'tokentracker.startServer');
            void vscode.window.showInformationMessage('Token Tracker 服务已停止');
            return;
        }
        if (result.reason === 'external') {
            void vscode.window.showWarningMessage('当前 Token Tracker 服务不是由扩展启动（可能是外部实例），未停止。');
            return;
        }
        if (result.reason === 'not-running') {
            log('stop server: not running');
            disposeStatusBars();
            server = undefined;
            showStatusItem('$(debug-stop) Token Tracker: 服务已停止', '点击启动 Token Tracker 服务', 'tokentracker.startServer');
        }
    }), vscode.workspace.onDidChangeWorkspaceFolders(() => {
        const updated = currentWorkspaceRoots();
        log(`workspace roots changed: ${updated.join(' | ') || '(none)'}`);
        statusBar?.setWorkspaceRoots(updated);
    }));
    registerMetadataWatchers(context);
    try {
        await startServerAndStatus(context, port, autoStart, range, idleExitSeconds);
    }
    catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log(`server unavailable: ${message}`);
        showStatusItem('$(warning) Token Tracker 未运行', message, 'tokentracker.startServer');
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
function deactivate() {
    disposeStatusBars();
    server = undefined;
    // The shared Token Tracker server is intentionally left running for other windows.
}
//# sourceMappingURL=extension.js.map