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
  baseUrl?: string;
  githubRepo?: string;
  onProgress?: (message: string) => void;
}

interface ReleaseAsset {
  name: string;
  apiUrl: string;
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

  const directory = path.join(options.storagePath, 'downloads');
  fs.mkdirSync(directory, { recursive: true });
  const archivePath = path.join(directory, asset);

  options.onProgress?.(`正在下载 ${asset}`);
  if (options.baseUrl) {
    const base = options.baseUrl.replace(/\/+$/, '');
    const url = `${base}/${asset}`;
    await downloadFile(url, archivePath);
    await verifyChecksumFromUrl(url, archivePath, options.onProgress);
  } else if (options.githubRepo) {
    const assets = await loadReleaseAssets(options.githubRepo, options.version);
    const archive = assets.find((entry) => entry.name === asset);
    if (!archive) {
      throw new Error(`Release v${options.version} 中缺少 ${asset}`);
    }

    await downloadFile(archive.apiUrl, archivePath, {
      Accept: 'application/octet-stream',
    });

    const checksum = assets.find(
      (entry) => entry.name === `${asset}.sha256`,
    );
    if (checksum) {
      const text = await downloadString(checksum.apiUrl, {
        Accept: 'application/octet-stream',
      });
      verifyHash(text, archivePath);
    } else {
      options.onProgress?.('未找到校验文件，跳过校验');
    }
  } else {
    throw new Error('未配置服务端下载地址');
  }

  return { rid, archivePath };
}

async function loadReleaseAssets(
  repositoryUrl: string,
  version: string,
): Promise<ReleaseAsset[]> {
  const match = /github\.com[/:]([^/]+)\/([^/]+?)(?:\.git)?\/?$/i.exec(
    repositoryUrl,
  );
  if (!match) {
    throw new Error(`无法解析 GitHub 仓库地址：${repositoryUrl}`);
  }

  const apiUrl =
    `https://api.github.com/repos/${match[1]}/${match[2]}` +
    `/releases/tags/v${version}`;

  let body: string;
  try {
    body = await downloadString(apiUrl, {
      Accept: 'application/vnd.github+json',
    });
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) {
      throw new Error(`未找到 v${version} 的 GitHub Release`);
    }
    if (
      error instanceof HttpError &&
      (error.status === 403 || error.status === 429)
    ) {
      throw new Error('GitHub API 限流，请稍后重试或配置镜像地址');
    }
    throw error;
  }

  let release: { assets?: Array<{ name?: string; url?: string }> };
  try {
    release = JSON.parse(body) as {
      assets?: Array<{ name?: string; url?: string }>;
    };
  } catch {
    throw new Error('无法解析 GitHub Release 信息');
  }

  return (release.assets ?? [])
    .filter(
      (entry): entry is { name: string; url: string } =>
        Boolean(entry.name && entry.url),
    )
    .map((entry) => ({ name: entry.name, apiUrl: entry.url }));
}

async function verifyChecksumFromUrl(
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

  verifyHash(checksum, archivePath);
}

function verifyHash(checksum: string, archivePath: string): void {
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
  headers: Record<string, string> = {},
  redirects = 0,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      {
        headers: {
          'User-Agent': 'TokenTracker-VSCode',
          ...headers,
        },
      },
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
              headers,
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

function downloadString(
  url: string,
  headers: Record<string, string> = {},
  redirects = 0,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      {
        headers: {
          'User-Agent': 'TokenTracker-VSCode',
          ...headers,
        },
      },
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
              headers,
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
