import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

export interface RuntimeInfo {
  buildId: string;
  serverVersion: string;
  runtimeDirectory: string;
  executablePath: string;
  dataDirectory: string;
}

interface BuildInfo {
  buildId?: string;
  serverVersion?: string;
}

export function prepareRuntime(
  extensionPath: string,
  storagePath: string,
): RuntimeInfo {
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

function readBuildInfo(sourceDirectory: string): BuildInfo {
  try {
    const raw = fs.readFileSync(path.join(sourceDirectory, 'build.json'), 'utf8');
    return JSON.parse(raw) as BuildInfo;
  } catch {
    return {};
  }
}

function fallbackBuildId(sourceDirectory: string): string {
  try {
    const executable = path.join(sourceDirectory, 'TokenTracker.Server.exe');
    const stat = fs.statSync(executable);
    return `${Math.round(stat.mtimeMs)}-${stat.size}`;
  } catch {
    return 'unknown';
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
