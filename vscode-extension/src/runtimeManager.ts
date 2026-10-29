import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  downloadServerArchive,
  resolveServerAsset,
  resolveServerRid,
  ServerAssetInfo,
} from './serverDownloader';

export interface RuntimeInfo {
  buildId: string;
  rid: string;
  releaseTag: string;
  serverVersion: string;
  runtimeDirectory: string;
  executablePath: string;
  dataDirectory: string;
  source: 'bundled' | 'download';
}

export interface PrepareRuntimeOptions {
  baseUrl?: string;
  githubRepo?: string;
  proxyUrl?: string;
  onProgress?: (message: string) => void;
}

interface BuildInfo {
  buildId?: string;
  rid?: string;
  serverVersion?: string;
}

interface RuntimeState {
  rid: string;
  buildId: string;
  serverVersion: string;
  runtimeDirectory: string;
  assetName: string;
  releaseTag: string;
}

export async function prepareRuntime(
  extensionPath: string,
  storagePath: string,
  options: PrepareRuntimeOptions,
): Promise<RuntimeInfo> {
  const executableName = process.platform === 'win32'
    ? 'TokenTracker.Server.exe'
    : 'TokenTracker.Server';
  const runtimeRoot = path.join(storagePath, 'runtime');
  const dataDirectory = path.join(storagePath, 'data');
  fs.mkdirSync(dataDirectory, { recursive: true });
  migratePreviousPublisherStorage(storagePath, dataDirectory);

  if (process.platform === 'win32') {
    migrateLegacyDatabase(dataDirectory);
  }

  const bundledDirectory = path.join(extensionPath, 'server');
  const bundledExecutable = path.join(bundledDirectory, executableName);
  if (
    fs.existsSync(path.join(bundledDirectory, 'build.json')) &&
    fs.existsSync(bundledExecutable)
  ) {
    const build = readBuildInfo(bundledDirectory);
    const buildId = build.buildId ?? fallbackBuildId(bundledDirectory, executableName);
    const runtimeDirectory = path.join(runtimeRoot, buildId);
    ensureRuntimeDirectory(runtimeDirectory, (target) => {
      fs.cpSync(bundledDirectory, target, { recursive: true });
    });

    return {
      buildId,
      rid: build.rid ?? (process.platform === 'win32' ? 'win-x64' : resolveServerRid()),
      releaseTag: 'bundled',
      serverVersion: build.serverVersion ?? 'unknown',
      runtimeDirectory,
      executablePath: path.join(runtimeDirectory, executableName),
      dataDirectory,
      source: 'bundled',
    };
  }

  const rid = resolveServerRid();
  const cached = readRuntimeState(storagePath);
  const cachedReady = Boolean(cached && isRuntimeReady(cached, runtimeRoot, executableName));

  let asset: ServerAssetInfo | null = null;
  let resolveError: unknown;
  try {
    asset = await resolveServerAsset({
      storagePath,
      baseUrl: options.baseUrl,
      githubRepo: options.githubRepo,
      proxyUrl: options.proxyUrl,
      onProgress: options.onProgress,
    });
  } catch (error) {
    resolveError = error;
  }

  if (asset && cachedReady && cached!.assetName === asset.assetName) {
    return runtimeInfoFromState(cached!, dataDirectory, executableName);
  }

  if (!asset && cachedReady) {
    options.onProgress?.('无法检查服务端更新，使用已缓存版本');
    return runtimeInfoFromState(cached!, dataDirectory, executableName);
  }

  if (!asset) {
    throw resolveError ?? new Error('无法获取服务端版本');
  }

  options.onProgress?.(`准备服务端 ${asset.serverVersion}`);
  let archivePath: string;
  try {
    archivePath = await downloadServerArchive(asset, {
      storagePath,
      proxyUrl: options.proxyUrl,
      onProgress: options.onProgress,
    });
  } catch (error) {
    if (cachedReady) {
      options.onProgress?.('服务端下载失败，使用已缓存版本');
      return runtimeInfoFromState(cached!, dataDirectory, executableName);
    }
    throw error;
  }

  const staging = path.join(runtimeRoot, `.staging-${process.pid}-${Date.now()}`);
  fs.rmSync(staging, { recursive: true, force: true });
  fs.mkdirSync(staging, { recursive: true });

  try {
    options.onProgress?.('正在解压服务端');
    const tarExecutable = process.platform === 'win32'
      ? path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe')
      : 'tar';
    execFileSync(tarExecutable, ['-xf', archivePath, '-C', staging], {
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
      fs.writeFileSync(
        path.join(runtimeDirectory, '.ready'),
        new Date().toISOString(),
        'utf8',
      );
    } else {
      fs.rmSync(staging, { recursive: true, force: true });
    }

    if (process.platform !== 'win32') {
      fs.chmodSync(executablePath, 0o755);
    }
    fs.rmSync(archivePath, { force: true });

    const info: RuntimeInfo = {
      buildId,
      rid,
      releaseTag: asset.releaseTag,
      serverVersion: build.serverVersion ?? asset.serverVersion,
      runtimeDirectory,
      executablePath,
      dataDirectory,
      source: 'download',
    };
    writeRuntimeState(storagePath, info, asset.assetName);
    return info;
  } catch (error) {
    fs.rmSync(staging, { recursive: true, force: true });
    fs.rmSync(archivePath, { force: true });
    throw error;
  }
}

export interface RuntimeCleanupResult {
  removed: string[];
  failed: string[];
}

export function cleanupOldRuntimes(
  storagePath: string,
  keepBuildId: string,
): RuntimeCleanupResult {
  const result: RuntimeCleanupResult = { removed: [], failed: [] };
  const runtimeRoot = path.join(storagePath, 'runtime');

  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });
  } catch {
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
      } catch {
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
    } catch {
      // In use by a running server; retry on a later activation.
      result.failed.push(entry.name);
    }
  }

  return result;
}

function runtimeInfoFromState(
  state: RuntimeState,
  dataDirectory: string,
  executableName: string,
): RuntimeInfo {
  return {
    buildId: state.buildId,
    rid: state.rid,
    releaseTag: state.releaseTag,
    serverVersion: state.serverVersion,
    runtimeDirectory: state.runtimeDirectory,
    executablePath: path.join(state.runtimeDirectory, executableName),
    dataDirectory,
    source: 'download',
  };
}

function isRuntimeReady(
  state: RuntimeState,
  runtimeRoot: string,
  executableName: string,
): boolean {
  return state.runtimeDirectory.startsWith(runtimeRoot + path.sep) &&
    fs.existsSync(path.join(state.runtimeDirectory, '.ready')) &&
    fs.existsSync(path.join(state.runtimeDirectory, executableName));
}

function ensureRuntimeDirectory(
  runtimeDirectory: string,
  populate: (target: string) => void,
): void {
  const readyMarker = path.join(runtimeDirectory, '.ready');
  if (fs.existsSync(readyMarker)) {
    return;
  }

  fs.rmSync(runtimeDirectory, { recursive: true, force: true });
  fs.mkdirSync(runtimeDirectory, { recursive: true });
  populate(runtimeDirectory);
  fs.writeFileSync(readyMarker, new Date().toISOString(), 'utf8');
}

function readBuildInfo(directory: string): BuildInfo {
  try {
    const raw = fs.readFileSync(path.join(directory, 'build.json'), 'utf8');
    return JSON.parse(raw) as BuildInfo;
  } catch {
    return {};
  }
}

function fallbackBuildId(directory: string, executableName: string): string {
  try {
    const executable = path.join(directory, executableName);
    const stat = fs.statSync(executable);
    return `${Math.round(stat.mtimeMs)}-${stat.size}`;
  } catch {
    return 'unknown';
  }
}

function statePath(storagePath: string): string {
  return path.join(storagePath, 'server-runtime.json');
}

function readRuntimeState(storagePath: string): RuntimeState | null {
  try {
    const raw = fs.readFileSync(statePath(storagePath), 'utf8');
    const state = JSON.parse(raw) as Partial<RuntimeState>;
    if (
      typeof state.rid !== 'string' ||
      typeof state.buildId !== 'string' ||
      typeof state.runtimeDirectory !== 'string' ||
      typeof state.assetName !== 'string'
    ) {
      return null;
    }
    return {
      rid: state.rid,
      buildId: state.buildId,
      serverVersion: state.serverVersion ?? 'unknown',
      runtimeDirectory: state.runtimeDirectory,
      assetName: state.assetName,
      releaseTag: state.releaseTag ?? 'unknown',
    };
  } catch {
    return null;
  }
}

function writeRuntimeState(
  storagePath: string,
  info: RuntimeInfo,
  assetName: string,
): void {
  const state: RuntimeState = {
    rid: info.rid,
    buildId: info.buildId,
    serverVersion: info.serverVersion,
    runtimeDirectory: info.runtimeDirectory,
    assetName,
    releaseTag: info.releaseTag,
  };
  fs.writeFileSync(statePath(storagePath), JSON.stringify(state, null, 2), 'utf8');
}

function migratePreviousPublisherStorage(
  storagePath: string,
  dataDirectory: string,
): void {
  const target = path.join(dataDirectory, 'tokentracker.db');
  if (fs.existsSync(target)) {
    return;
  }

  const parent = path.dirname(storagePath);
  const current = path.dirname(target);
  for (const name of [
    'jotting.tokentracker',
    'jottingcn.tokentracker',
    'JottingCN.tokentracker',
  ]) {
    const directory = path.join(parent, name, 'data');
    const same = process.platform === 'win32'
      ? directory.toLowerCase() === current.toLowerCase()
      : directory === current;
    if (same) {
      continue;
    }

    const source = path.join(directory, 'tokentracker.db');
    if (!fs.existsSync(source)) {
      continue;
    }

    for (const suffix of ['', '-wal', '-shm']) {
      const from = source + suffix;
      if (!fs.existsSync(from)) {
        continue;
      }
      try {
        fs.copyFileSync(from, target + suffix);
      } catch {
        // Best-effort migration.
      }
    }
    return;
  }
}

function migrateLegacyDatabase(dataDirectory: string): void {
  const target = path.join(dataDirectory, 'tokentracker.db');
  if (fs.existsSync(target)) {
    return;
  }

  const legacyDirectory = path.join(
    process.env.LOCALAPPDATA ?? os.homedir(),
    'TokenTracker',
  );
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
    } catch {
      // Best-effort migration: server will create a fresh database if needed.
    }
  }
}
