import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as https from 'node:https';
import * as path from 'node:path';
import { URL } from 'node:url';

export interface ServerArchiveInfo {
  rid: string;
  archivePath: string;
}

export interface ServerDownloadOptions {
  storagePath: string;
  version: string;
  baseUrl: string;
  onProgress?: (message: string) => void;
}

class HttpError extends Error {
  constructor(readonly status: number) {
    super(`HTTP ${status}`);
  }
}

const REDIRECT_CODES = new Set([301, 302, 303, 307, 308]);

export function resolveServerRid(): string {
  if (process.platform === 'win32' && process.arch === 'x64') {
    return 'win-x64';
  }
  if (process.platform === 'linux' && process.arch === 'x64') {
    return 'linux-x64';
  }
  if (process.platform === 'darwin' && process.arch === 'x64') {
    return 'osx-x64';
  }
  if (process.platform === 'darwin' && process.arch === 'arm64') {
    return 'osx-arm64';
  }

  throw new Error(
    `当前平台暂不支持自动下载服务端：${process.platform}/${process.arch}`,
  );
}

export async function ensureServerArchive(
  options: ServerDownloadOptions,
): Promise<ServerArchiveInfo> {
  const rid = resolveServerRid();
  const extension = rid.startsWith('win-') ? 'zip' : 'tar.gz';
  const asset = `tokentracker-server-${options.version}-${rid}.${extension}`;
  const url = `${options.baseUrl.replace(/\/+$/, '')}/${asset}`;

  const directory = path.join(options.storagePath, 'downloads');
  fs.mkdirSync(directory, { recursive: true });
  const archivePath = path.join(directory, asset);

  options.onProgress?.(`正在下载 ${asset}`);
  await downloadFile(url, archivePath);
  await verifyChecksum(url, archivePath, options.onProgress);

  return { rid, archivePath };
}

async function verifyChecksum(
  url: string,
  archivePath: string,
  onProgress?: (message: string) => void,
): Promise<void> {
  let checksum: string;
  try {
    checksum = await downloadString(`${url}.sha256`);
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) {
      onProgress?.('未找到校验文件，跳过校验');
      return;
    }
    throw error;
  }

  const match = /([a-f0-9]{64})/i.exec(checksum);
  if (!match) {
    return;
  }

  const actual = createHash('sha256')
    .update(fs.readFileSync(archivePath))
    .digest('hex');
  if (actual.toLowerCase() !== match[1].toLowerCase()) {
    fs.rmSync(archivePath, { force: true });
    throw new Error('服务端下载校验失败，请重试');
  }
}

function downloadFile(
  url: string,
  destination: string,
  redirects = 0,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      { headers: { 'User-Agent': 'TokenTracker-VSCode' } },
      (response) => {
        const status = response.statusCode ?? 0;
        if (REDIRECT_CODES.has(status) && response.headers.location) {
          response.resume();
          if (redirects >= 5) {
            reject(new Error('服务端下载重定向过多'));
            return;
          }
          resolve(
            downloadFile(
              new URL(response.headers.location, parsed).toString(),
              destination,
              redirects + 1,
            ),
          );
          return;
        }

        if (status !== 200) {
          response.resume();
          reject(new HttpError(status));
          return;
        }

        const stream = fs.createWriteStream(destination);
        response.pipe(stream);
        stream.on('finish', () => {
          stream.close((error) => (error ? reject(error) : resolve()));
        });
        stream.on('error', reject);
      },
    );

    request.setTimeout(120_000, () => {
      request.destroy(new Error('服务端下载超时'));
    });
    request.on('error', reject);
  });
}

function downloadString(url: string, redirects = 0): Promise<string> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      { headers: { 'User-Agent': 'TokenTracker-VSCode' } },
      (response) => {
        const status = response.statusCode ?? 0;
        if (REDIRECT_CODES.has(status) && response.headers.location) {
          response.resume();
          if (redirects >= 5) {
            reject(new Error('校验文件重定向过多'));
            return;
          }
          resolve(
            downloadString(
              new URL(response.headers.location, parsed).toString(),
              redirects + 1,
            ),
          );
          return;
        }

        if (status !== 200) {
          response.resume();
          reject(new HttpError(status));
          return;
        }

        let body = '';
        response.setEncoding('utf8');
        response.on('data', (chunk: string) => {
          body += chunk;
        });
        response.on('end', () => resolve(body));
      },
    );

    request.setTimeout(30_000, () => {
      request.destroy(new Error('校验文件下载超时'));
    });
    request.on('error', reject);
  });
}
