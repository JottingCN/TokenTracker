import { execFileSync, spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { httpGet, httpGetJson } from './httpClient';

export interface ServerInfo {
  name: string;
  version: string;
  protocol: number;
  port: number;
  startedAt: string;
  buildId?: string | null;
  features: string[];
}

export interface EnsureServerOptions {
  storagePath: string;
  port: number;
  autoStart: boolean;
  executablePath: string;
  dataDirectory: string;
  buildId: string;
  idleExitSeconds: number;
}

export interface RunningServer {
  url: string;
  port: number;
  startedHere: boolean;
  outdated?: boolean;
  pid?: number;
  info?: ServerInfo;
}

export function serverUrl(port: number): string {
  return `http://127.0.0.1:${port}`;
}

export async function isHealthy(port: number): Promise<boolean> {
  try {
    const response = await httpGet(`${serverUrl(port)}/healthz`, 1000);
    return response.status === 200;
  } catch {
    return false;
  }
}

export async function fetchServerInfo(port: number): Promise<ServerInfo | null> {
  const info = await httpGetJson<ServerInfo>(`${serverUrl(port)}/api/version`, 1200);
  return info && info.name === 'TokenTracker.Server' ? info : null;
}

export type StopServerResult =
  | { stopped: true; pid: number }
  | { stopped: false; reason: 'not-running' | 'external' | 'failed' };

export async function stopOwnedServer(
  storagePath: string,
  port: number,
): Promise<StopServerResult> {
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
  } catch {
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

export async function ensureServer(options: EnsureServerOptions): Promise<RunningServer> {
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

  const child = spawn(executable, [], {
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

async function waitForServer(port: number, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await isHealthy(port)) {
      return true;
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  return false;
}

function tryAcquireLock(lockPath: string): boolean {
  if (createLock(lockPath)) {
    return true;
  }

  if (!isLockStale(lockPath)) {
    return false;
  }

  removeLock(lockPath);
  return createLock(lockPath);
}

function createLock(lockPath: string): boolean {
  try {
    fs.writeFileSync(
      lockPath,
      JSON.stringify({ pid: process.pid, createdAt: Date.now() }),
      { encoding: 'utf8', flag: 'wx' },
    );
    return true;
  } catch {
    return false;
  }
}

function writeLock(lockPath: string, pid: number): void {
  fs.writeFileSync(
    lockPath,
    JSON.stringify({ pid, createdAt: Date.now() }),
    { encoding: 'utf8', flag: 'w' },
  );
}

function removeLock(lockPath: string): void {
  try {
    fs.unlinkSync(lockPath);
  } catch {
    // Ignore: another window may have cleaned it up.
  }
}

function readLockPid(lockPath: string): number | null {
  try {
    const data = JSON.parse(fs.readFileSync(lockPath, 'utf8')) as { pid?: number };
    return typeof data.pid === 'number' && data.pid > 0 ? data.pid : null;
  } catch {
    return null;
  }
}

function isOurServer(pid: number): boolean {
  try {
    if (process.platform === 'win32') {
      const output = execFileSync(
        'tasklist',
        ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'],
        { encoding: 'utf8', windowsHide: true },
      );
      return output.includes('TokenTracker.Server.exe');
    }

    const output = execFileSync(
      'ps',
      ['-p', String(pid), '-o', 'args='],
      { encoding: 'utf8' },
    );
    return output.trim().includes('TokenTracker.Server');
  } catch {
    return false;
  }
}

function isLockStale(lockPath: string): boolean {
  try {
    const raw = fs.readFileSync(lockPath, 'utf8');
    const data = JSON.parse(raw) as { pid?: number; createdAt?: number };
    const age = Date.now() - (data.createdAt ?? 0);
    if (age > 120000) {
      return true;
    }
    if (typeof data.pid === 'number' && data.pid > 0) {
      return !isPidAlive(data.pid);
    }
    return false;
  } catch {
    return true;
  }
}

function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === 'EPERM';
  }
}
