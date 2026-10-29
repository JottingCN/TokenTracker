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
exports.serverUrl = serverUrl;
exports.isHealthy = isHealthy;
exports.fetchServerInfo = fetchServerInfo;
exports.stopOwnedServer = stopOwnedServer;
exports.ensureServer = ensureServer;
const node_child_process_1 = require("node:child_process");
const fs = __importStar(require("node:fs"));
const path = __importStar(require("node:path"));
const httpClient_1 = require("./httpClient");
function serverUrl(port) {
    return `http://127.0.0.1:${port}`;
}
async function isHealthy(port) {
    try {
        const response = await (0, httpClient_1.httpGet)(`${serverUrl(port)}/healthz`, 1000);
        return response.status === 200;
    }
    catch {
        return false;
    }
}
async function fetchServerInfo(port) {
    const info = await (0, httpClient_1.httpGetJson)(`${serverUrl(port)}/api/version`, 1200);
    return info && info.name === 'TokenTracker.Server' ? info : null;
}
async function stopOwnedServer(storagePath, port) {
    if (!(await isHealthy(port))) {
        removeLock(path.join(storagePath, `server-${port}.lock`));
        return { stopped: false, reason: 'not-running' };
    }
    const lockPath = path.join(storagePath, `server-${port}.lock`);
    const pid = readLockPid(lockPath);
    if (pid === null || !isPidAlive(pid) || !isOurServer(pid)) {
        return { stopped: false, reason: 'external' };
    }
    try {
        process.kill(pid);
    }
    catch {
        return { stopped: false, reason: 'failed' };
    }
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
        if (!(await isHealthy(port))) {
            removeLock(lockPath);
            return { stopped: true, pid };
        }
        await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return { stopped: false, reason: 'failed' };
}
async function ensureServer(options) {
    const url = serverUrl(options.port);
    if (await isHealthy(options.port)) {
        const info = (await fetchServerInfo(options.port)) ?? undefined;
        if (info?.buildId === options.buildId) {
            return {
                url,
                port: options.port,
                startedHere: false,
                info,
            };
        }
        const stopResult = await stopOwnedServer(options.storagePath, options.port);
        if (!stopResult.stopped) {
            return {
                url,
                port: options.port,
                startedHere: false,
                outdated: true,
                info,
            };
        }
    }
    if (!options.autoStart) {
        throw new Error(`TokenTracker.Server 未运行（${url}），且 tokentracker.autoStart 为 false`);
    }
    const executable = options.executablePath;
    const serverDirectory = path.dirname(executable);
    if (!fs.existsSync(executable)) {
        throw new Error(`找不到内置服务端副本：${executable}`);
    }
    fs.mkdirSync(options.storagePath, { recursive: true });
    const lockPath = path.join(options.storagePath, `server-${options.port}.lock`);
    if (!tryAcquireLock(lockPath)) {
        if (await waitForServer(options.port, 25000)) {
            return {
                url,
                port: options.port,
                startedHere: false,
                info: (await fetchServerInfo(options.port)) ?? undefined,
            };
        }
        removeLock(lockPath);
    }
    const child = (0, node_child_process_1.spawn)(executable, [], {
        cwd: serverDirectory,
        env: {
            ...process.env,
            TOKENTRACKER_PORT: String(options.port),
            TOKENTRACKER_DATA_DIR: options.dataDirectory,
            TOKENTRACKER_BUILD_ID: options.buildId,
            ...(options.idleExitSeconds > 0
                ? { TOKENTRACKER_IDLE_EXIT_SECONDS: String(options.idleExitSeconds) }
                : {}),
        },
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
    });
    child.unref();
    if (typeof child.pid === 'number') {
        writeLock(lockPath, child.pid);
    }
    if (await waitForServer(options.port, 25000)) {
        return {
            url,
            port: options.port,
            startedHere: true,
            pid: child.pid,
            info: (await fetchServerInfo(options.port)) ?? undefined,
        };
    }
    removeLock(lockPath);
    throw new Error(`TokenTracker.Server 启动超时（端口 ${options.port}）`);
}
async function waitForServer(port, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (await isHealthy(port)) {
            return true;
        }
        await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return false;
}
function tryAcquireLock(lockPath) {
    if (createLock(lockPath)) {
        return true;
    }
    if (!isLockStale(lockPath)) {
        return false;
    }
    removeLock(lockPath);
    return createLock(lockPath);
}
function createLock(lockPath) {
    try {
        fs.writeFileSync(lockPath, JSON.stringify({ pid: process.pid, createdAt: Date.now() }), { encoding: 'utf8', flag: 'wx' });
        return true;
    }
    catch {
        return false;
    }
}
function writeLock(lockPath, pid) {
    fs.writeFileSync(lockPath, JSON.stringify({ pid, createdAt: Date.now() }), { encoding: 'utf8', flag: 'w' });
}
function removeLock(lockPath) {
    try {
        fs.unlinkSync(lockPath);
    }
    catch {
        // Ignore: another window may have cleaned it up.
    }
}
function readLockPid(lockPath) {
    try {
        const data = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
        return typeof data.pid === 'number' && data.pid > 0 ? data.pid : null;
    }
    catch {
        return null;
    }
}
function isOurServer(pid) {
    try {
        if (process.platform === 'win32') {
            const output = (0, node_child_process_1.execFileSync)('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8', windowsHide: true });
            return output.includes('TokenTracker.Server.exe');
        }
        const output = (0, node_child_process_1.execFileSync)('ps', ['-p', String(pid), '-o', 'args='], { encoding: 'utf8' });
        return output.trim().includes('TokenTracker.Server');
    }
    catch {
        return false;
    }
}
function isLockStale(lockPath) {
    try {
        const raw = fs.readFileSync(lockPath, 'utf8');
        const data = JSON.parse(raw);
        const age = Date.now() - (data.createdAt ?? 0);
        if (age > 120000) {
            return true;
        }
        if (typeof data.pid === 'number' && data.pid > 0) {
            return !isPidAlive(data.pid);
        }
        return false;
    }
    catch {
        return true;
    }
}
function isPidAlive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    }
    catch (error) {
        return error.code === 'EPERM';
    }
}
//# sourceMappingURL=serverManager.js.map