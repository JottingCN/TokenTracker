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
const fs = __importStar(require("node:fs"));
const os = __importStar(require("node:os"));
const path = __importStar(require("node:path"));
function prepareRuntime(extensionPath, storagePath) {
    const sourceDirectory = path.join(extensionPath, 'server');
    const build = readBuildInfo(sourceDirectory);
    const buildId = build.buildId ?? fallbackBuildId(sourceDirectory);
    const runtimeRoot = path.join(storagePath, 'runtime');
    const runtimeDirectory = path.join(runtimeRoot, buildId);
    const readyMarker = path.join(runtimeDirectory, '.ready');
    if (!fs.existsSync(readyMarker)) {
        fs.rmSync(runtimeDirectory, { recursive: true, force: true });
        fs.mkdirSync(runtimeDirectory, { recursive: true });
        fs.cpSync(sourceDirectory, runtimeDirectory, { recursive: true });
        fs.writeFileSync(readyMarker, new Date().toISOString(), 'utf8');
    }
    const dataDirectory = path.join(storagePath, 'data');
    fs.mkdirSync(dataDirectory, { recursive: true });
    migrateLegacyDatabase(dataDirectory);
    return {
        buildId,
        serverVersion: build.serverVersion ?? 'unknown',
        runtimeDirectory,
        executablePath: path.join(runtimeDirectory, 'TokenTracker.Server.exe'),
        dataDirectory,
    };
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
function readBuildInfo(sourceDirectory) {
    try {
        const raw = fs.readFileSync(path.join(sourceDirectory, 'build.json'), 'utf8');
        return JSON.parse(raw);
    }
    catch {
        return {};
    }
}
function fallbackBuildId(sourceDirectory) {
    try {
        const executable = path.join(sourceDirectory, 'TokenTracker.Server.exe');
        const stat = fs.statSync(executable);
        return `${Math.round(stat.mtimeMs)}-${stat.size}`;
    }
    catch {
        return 'unknown';
    }
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