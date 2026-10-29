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
exports.prepareRuntime = prepareRuntime;
exports.cleanupOldRuntimes = cleanupOldRuntimes;
const node_child_process_1 = require("node:child_process");
const fs = __importStar(require("node:fs"));
const os = __importStar(require("node:os"));
const path = __importStar(require("node:path"));
const serverDownloader_1 = require("./serverDownloader");
async function prepareRuntime(extensionPath, storagePath, options) {
    const executableName = process.platform === 'win32'
        ? 'TokenTracker.Server.exe'
        : 'TokenTracker.Server';
    const runtimeRoot = path.join(storagePath, 'runtime');
    const dataDirectory = path.join(storagePath, 'data');
    fs.mkdirSync(dataDirectory, { recursive: true });
    if (process.platform === 'win32') {
        migrateLegacyDatabase(dataDirectory);
    }
    const bundledDirectory = path.join(extensionPath, 'server');
    const bundledExecutable = path.join(bundledDirectory, executableName);
    if (fs.existsSync(path.join(bundledDirectory, 'build.json')) &&
        fs.existsSync(bundledExecutable)) {
        const build = readBuildInfo(bundledDirectory);
        const buildId = build.buildId ?? fallbackBuildId(bundledDirectory, executableName);
        const runtimeDirectory = path.join(runtimeRoot, buildId);
        ensureRuntimeDirectory(runtimeDirectory, (target) => {
            fs.cpSync(bundledDirectory, target, { recursive: true });
        });
        return {
            buildId,
            rid: build.rid ?? (process.platform === 'win32' ? 'win-x64' : (0, serverDownloader_1.resolveServerRid)()),
            serverVersion: build.serverVersion ?? 'unknown',
            runtimeDirectory,
            executablePath: path.join(runtimeDirectory, executableName),
            dataDirectory,
            source: 'bundled',
        };
    }
    const rid = (0, serverDownloader_1.resolveServerRid)();
    const state = readState(storagePath);
    if (state &&
        state.version === options.version &&
        state.rid === rid &&
        state.runtimeDirectory.startsWith(runtimeRoot + path.sep)) {
        const executablePath = path.join(state.runtimeDirectory, executableName);
        if (fs.existsSync(path.join(state.runtimeDirectory, '.ready')) &&
            fs.existsSync(executablePath)) {
            return {
                buildId: state.buildId,
                rid,
                serverVersion: state.serverVersion,
                runtimeDirectory: state.runtimeDirectory,
                executablePath,
                dataDirectory,
                source: 'download',
            };
        }
    }
    const archive = await (0, serverDownloader_1.ensureServerArchive)({
        storagePath,
        version: options.version,
        baseUrl: options.downloadBaseUrl,
        onProgress: options.onProgress,
    });
    const staging = path.join(runtimeRoot, `.staging-${process.pid}-${Date.now()}`);
    fs.rmSync(staging, { recursive: true, force: true });
    fs.mkdirSync(staging, { recursive: true });
    try {
        options.onProgress?.('正在解压服务端');
        const tarExecutable = process.platform === 'win32'
            ? path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe')
            : 'tar';
        (0, node_child_process_1.execFileSync)(tarExecutable, ['-xf', archive.archivePath, '-C', staging], {
            windowsHide: true,
        });
        const build = readBuildInfo(staging);
        const buildId = build.buildId ?? fallbackBuildId(staging, executableName);
        const runtimeDirectory = path.join(runtimeRoot, buildId);
        const executablePath = path.join(runtimeDirectory, executableName);
        if (!fs.existsSync(path.join(staging, executableName))) {
            throw new Error('服务端包中缺少可执行文件');
        }
        if (!fs.existsSync(path.join(runtimeDirectory, '.ready'))) {
            fs.rmSync(runtimeDirectory, { recursive: true, force: true });
            fs.renameSync(staging, runtimeDirectory);
            fs.writeFileSync(path.join(runtimeDirectory, '.ready'), new Date().toISOString(), 'utf8');
        }
        else {
            fs.rmSync(staging, { recursive: true, force: true });
        }
        if (process.platform !== 'win32') {
            fs.chmodSync(executablePath, 0o755);
        }
        fs.rmSync(archive.archivePath, { force: true });
        const info = {
            buildId,
            rid,
            serverVersion: build.serverVersion ?? 'unknown',
            runtimeDirectory,
            executablePath,
            dataDirectory,
            source: 'download',
        };
        writeState(storagePath, info, options.version);
        return info;
    }
    catch (error) {
        fs.rmSync(staging, { recursive: true, force: true });
        throw error;
    }
}
function cleanupOldRuntimes(storagePath, keepBuildId) {
    const result = { removed: [], failed: [] };
    const runtimeRoot = path.join(storagePath, 'runtime');
    let entries;
    try {
        entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });
    }
    catch {
        return result;
    }
    for (const entry of entries) {
        if (!entry.isDirectory() || entry.name === keepBuildId) {
            continue;
        }
        if (entry.name.startsWith('.staging-')) {
            try {
                fs.rmSync(path.join(runtimeRoot, entry.name), {
                    recursive: true,
                    force: true,
                });
                result.removed.push(entry.name);
            }
            catch {
                result.failed.push(entry.name);
            }
            continue;
        }
        try {
            fs.rmSync(path.join(runtimeRoot, entry.name), {
                recursive: true,
                force: false,
            });
            result.removed.push(entry.name);
        }
        catch {
            // In use by a running server; retry on a later activation.
            result.failed.push(entry.name);
        }
    }
    return result;
}
function ensureRuntimeDirectory(runtimeDirectory, populate) {
    const readyMarker = path.join(runtimeDirectory, '.ready');
    if (fs.existsSync(readyMarker)) {
        return;
    }
    fs.rmSync(runtimeDirectory, { recursive: true, force: true });
    fs.mkdirSync(runtimeDirectory, { recursive: true });
    populate(runtimeDirectory);
    fs.writeFileSync(readyMarker, new Date().toISOString(), 'utf8');
}
function readBuildInfo(directory) {
    try {
        const raw = fs.readFileSync(path.join(directory, 'build.json'), 'utf8');
        return JSON.parse(raw);
    }
    catch {
        return {};
    }
}
function fallbackBuildId(directory, executableName) {
    try {
        const executable = path.join(directory, executableName);
        const stat = fs.statSync(executable);
        return `${Math.round(stat.mtimeMs)}-${stat.size}`;
    }
    catch {
        return 'unknown';
    }
}
function statePath(storagePath) {
    return path.join(storagePath, 'server-runtime.json');
}
function readState(storagePath) {
    try {
        const raw = fs.readFileSync(statePath(storagePath), 'utf8');
        return JSON.parse(raw);
    }
    catch {
        return null;
    }
}
function writeState(storagePath, info, version) {
    const state = {
        version,
        rid: info.rid,
        buildId: info.buildId,
        serverVersion: info.serverVersion,
        runtimeDirectory: info.runtimeDirectory,
    };
    fs.writeFileSync(statePath(storagePath), JSON.stringify(state, null, 2), 'utf8');
}
function migrateLegacyDatabase(dataDirectory) {
    const target = path.join(dataDirectory, 'tokentracker.db');
    if (fs.existsSync(target)) {
        return;
    }
    const legacyDirectory = path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'TokenTracker');
    const source = path.join(legacyDirectory, 'tokentracker.db');
    if (!fs.existsSync(source)) {
        return;
    }
    for (const suffix of ['', '-wal', '-shm']) {
        const from = source + suffix;
        if (!fs.existsSync(from)) {
            continue;
        }
        try {
            fs.copyFileSync(from, target + suffix);
        }
        catch {
            // Best-effort migration: server will create a fresh database if needed.
        }
    }
}
//# sourceMappingURL=runtimeManager.js.map