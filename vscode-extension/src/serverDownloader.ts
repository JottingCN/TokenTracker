import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as https from 'node:https';
import * as net from 'node:net';
import * as path from 'node:path';
import * as tls from 'node:tls';
import { URL } from 'node:url';

export interface ServerAssetInfo {
  rid: string;
  assetName: string;
  archiveUrl: string;
  checksumUrl?: string;
  releaseTag: string;
  serverVersion: string;
}

export interface ServerDownloadOptions {
  storagePath: string;
  baseUrl?: string;
  githubRepo?: string;
  proxyUrl?: string;
  onProgress?: (message: string) => void;
}

interface ReleaseCache {
  cacheVersion: 2;
  checkedAt: number;
  rid: string;
  extension: string;
  asset: ServerAssetInfo;
}

interface GitHubRelease {
  tag_name?: string;
  draft?: boolean;
  prerelease?: boolean;
  created_at?: string;
  published_at?: string;
  assets?: Array<{ name?: string; url?: string }>;
}

class HttpError extends Error {
  constructor(readonly status: number) {
    super(`HTTP ${status}`);
  }
}

const REDIRECT_CODES = new Set([301, 302, 303, 307, 308]);
const RELEASE_CACHE_TTL_MS = 30 * 60 * 1000;

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

export async function resolveServerAsset(
  options: ServerDownloadOptions,
): Promise<ServerAssetInfo> {
  const rid = resolveServerRid();
  const extension = rid.startsWith('win-') ? 'zip' : 'tar.gz';

  const baseUrl = options.baseUrl?.trim();
  if (baseUrl) {
    const base = baseUrl.replace(/\/+$/, '');
    const archiveUrl = `${base}-${rid}.${extension}`;
    return {
      rid,
      assetName: archiveUrl,
      archiveUrl,
      checksumUrl: `${archiveUrl}.sha256`,
      releaseTag: 'mirror',
      serverVersion: parseServerVersion(archiveUrl, rid, extension) ?? 'mirror',
    };
  }

  if (!options.githubRepo) {
    throw new Error('未配置服务端下载地址');
  }

  const cached = readReleaseCache(options.storagePath, rid, extension, false);
  if (cached) {
    return cached;
  }

  options.onProgress?.('正在检查服务端版本');
  try {
    const asset = await fetchLatestServerAsset(
      options.githubRepo,
      rid,
      extension,
      options.proxyUrl,
    );
    writeReleaseCache(options.storagePath, rid, extension, asset);
    return asset;
  } catch (error) {
    const stale = readReleaseCache(options.storagePath, rid, extension, true);
    if (stale) {
      options.onProgress?.('检查服务端更新失败，使用缓存版本');
      return stale;
    }
    throw error;
  }
}

export async function downloadServerArchive(
  asset: ServerAssetInfo,
  options: ServerDownloadOptions,
): Promise<string> {
  const extension = asset.rid.startsWith('win-') ? 'zip' : 'tar.gz';
  const directory = path.join(options.storagePath, 'downloads');
  fs.mkdirSync(directory, { recursive: true });
  const archivePath = path.join(
    directory,
    `tokentracker-server-${asset.rid}.${extension}`,
  );

  options.onProgress?.(`正在下载 ${asset.assetName}`);
  const headers: Record<string, string> =
    asset.archiveUrl.includes('api.github.com')
      ? { Accept: 'application/octet-stream' }
      : {};
  await downloadFile(asset.archiveUrl, archivePath, headers, options.proxyUrl);

  if (asset.checksumUrl) {
    try {
      const checksum = await downloadString(
        asset.checksumUrl,
        headers,
        options.proxyUrl,
      );
      verifyHash(checksum, archivePath);
    } catch (error) {
      if (error instanceof HttpError && error.status === 404) {
        options.onProgress?.('未找到校验文件，跳过校验');
      } else {
        fs.rmSync(archivePath, { force: true });
        throw error;
      }
    }
  } else {
    options.onProgress?.('未找到校验文件，跳过校验');
  }

  return archivePath;
}

async function fetchLatestServerAsset(
  repositoryUrl: string,
  rid: string,
  extension: string,
  proxyUrl?: string,
): Promise<ServerAssetInfo> {
  const repository = parseGitHubRepository(repositoryUrl);
  const url =
    `https://api.github.com/repos/${repository.owner}/${repository.repo}` +
    '/releases?per_page=30';

  let body: string;
  try {
    body = await downloadString(
      url,
      { Accept: 'application/vnd.github+json' },
      proxyUrl,
    );
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) {
      throw new Error('无法读取 GitHub Release，请检查仓库地址');
    }
    if (
      error instanceof HttpError &&
      (error.status === 403 || error.status === 429)
    ) {
      throw new Error('GitHub API 限流，请稍后重试或配置镜像地址');
    }
    throw error;
  }

  let releases: GitHubRelease[];
  try {
    releases = JSON.parse(body) as GitHubRelease[];
  } catch {
    throw new Error('无法解析 GitHub Release 列表');
  }

  releases.sort(
    (left, right) =>
      releaseTime(right) - releaseTime(left),
  );

  const pattern = serverAssetPattern(rid, extension);
  for (const release of releases) {
    if (release.draft || release.prerelease) {
      continue;
    }

    const assets = (release.assets ?? []).filter(
      (asset): asset is { name: string; url: string } =>
        Boolean(asset.name && asset.url),
    );
    const archive = assets.find((asset) => pattern.test(asset.name));
    if (!archive) {
      continue;
    }

    const checksum = assets.find(
      (asset) => asset.name === `${archive.name}.sha256`,
    );
    return {
      rid,
      assetName: archive.name,
      archiveUrl: archive.url,
      checksumUrl: checksum?.url,
      releaseTag: release.tag_name ?? 'unknown',
      serverVersion:
        parseServerVersion(archive.name, rid, extension) ?? 'unknown',
    };
  }

  throw new Error(`未找到包含 ${rid} 服务端的 Release`);
}

function releaseTime(release: GitHubRelease): number {
  const value = release.published_at ?? release.created_at;
  const parsed = value ? Date.parse(value) : 0;
  return Number.isFinite(parsed) ? parsed : 0;
}

function parseGitHubRepository(repositoryUrl: string): {
  owner: string;
  repo: string;
} {
  const match = /github\.com[/:]([^/]+)\/([^/]+?)(?:\.git)?\/?$/i.exec(
    repositoryUrl,
  );
  if (!match) {
    throw new Error(`无法解析 GitHub 仓库地址：${repositoryUrl}`);
  }
  return { owner: match[1], repo: match[2] };
}

function serverAssetPattern(rid: string, extension: string): RegExp {
  return new RegExp(
    `^tokentracker-server-(.+)-${escapeRegExp(rid)}\\.${escapeRegExp(extension)}$`,
  );
}

function parseServerVersion(
  assetName: string,
  rid: string,
  extension: string,
): string | null {
  const match = serverAssetPattern(rid, extension).exec(assetName);
  return match ? match[1] : null;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function releaseCachePath(storagePath: string): string {
  return path.join(storagePath, 'server-release-cache.json');
}

function readReleaseCache(
  storagePath: string,
  rid: string,
  extension: string,
  allowStale: boolean,
): ServerAssetInfo | null {
  try {
    const cache = JSON.parse(
      fs.readFileSync(releaseCachePath(storagePath), 'utf8'),
    ) as ReleaseCache;
    if (
      cache.cacheVersion !== 2 ||
      cache.rid !== rid ||
      cache.extension !== extension
    ) {
      return null;
    }
    if (!allowStale && Date.now() - cache.checkedAt > RELEASE_CACHE_TTL_MS) {
      return null;
    }
    return cache.asset;
  } catch {
    return null;
  }
}

function writeReleaseCache(
  storagePath: string,
  rid: string,
  extension: string,
  asset: ServerAssetInfo,
): void {
  const cache: ReleaseCache = {
    cacheVersion: 2,
    checkedAt: Date.now(),
    rid,
    extension,
    asset,
  };
  try {
    fs.writeFileSync(
      releaseCachePath(storagePath),
      JSON.stringify(cache, null, 2),
      'utf8',
    );
  } catch {
    // Cache is best-effort.
  }
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
  proxyUrl?: string,
  redirects = 0,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      requestOptions(parsed, headers, proxyUrl),
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
              proxyUrl,
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
  proxyUrl?: string,
  redirects = 0,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === 'https:' ? https : http;
    const request = client.get(
      parsed,
      requestOptions(parsed, headers, proxyUrl),
      (response) => {
        const status = response.statusCode ?? 0;
        if (REDIRECT_CODES.has(status) && response.headers.location) {
          response.resume();
          if (redirects >= 5) {
            reject(new Error('服务端信息重定向过多'));
            return;
          }
          resolve(
            downloadString(
              new URL(response.headers.location, parsed).toString(),
              headers,
              proxyUrl,
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
      request.destroy(new Error('服务端信息请求超时'));
    });
    request.on('error', reject);
  });
}

function requestOptions(
  target: URL,
  headers: Record<string, string>,
  proxyUrl?: string,
): https.RequestOptions {
  const options: https.RequestOptions = {
    headers: {
      'User-Agent': 'TokenTracker-VSCode',
      ...headers,
    },
  };

  const agent = createProxyAgent(target, proxyUrl);
  if (agent) {
    options.agent = agent;
  }
  return options;
}

function createProxyAgent(
  target: URL,
  proxyUrl?: string,
): https.Agent | undefined {
  if (!proxyUrl || target.protocol !== 'https:') {
    return undefined;
  }
  if (!shouldProxy(target.hostname)) {
    return undefined;
  }

  let proxy: URL;
  try {
    proxy = new URL(
      proxyUrl.includes('://') ? proxyUrl : `http://${proxyUrl}`,
    );
  } catch {
    throw new Error('代理地址无效');
  }
  if (proxy.protocol !== 'http:') {
    throw new Error(`仅支持 http:// 代理，当前为 ${proxy.protocol}`);
  }

  return buildProxyAgent(proxy);
}

type ProxyCreateConnection = (
  options: {
    host?: string;
    hostname?: string;
    port?: number | string;
    servername?: string;
  },
  callback: (error: Error | null, socket?: tls.TLSSocket) => void,
) => undefined;

function buildProxyAgent(proxy: URL): https.Agent {
  const agent = new https.Agent({ keepAlive: false });

  (agent as unknown as { createConnection: ProxyCreateConnection }).createConnection =
    (options, callback) => {
      const host = String(options.host ?? options.hostname ?? '');
      const port = Number(options.port ?? 443);
      const proxyPort = Number(proxy.port || 80);
      const socket = net.connect(proxyPort, proxy.hostname);

      let settled = false;
      let handshakeTimer: NodeJS.Timeout | undefined;
      const fail = (error: Error) => {
        if (settled) {
          return;
        }
        settled = true;
        if (handshakeTimer) {
          clearTimeout(handshakeTimer);
        }
        socket.destroy();
        callback(error);
      };

      socket.setTimeout(30_000, () => fail(new Error('代理连接超时')));
      socket.on('error', fail);
      socket.on('connect', () => {
        let head = `CONNECT ${host}:${port} HTTP/1.1\r\nHost: ${host}:${port}\r\n`;
        if (proxy.username || proxy.password) {
          const credentials = `${decodeURIComponent(proxy.username)}:` +
            `${decodeURIComponent(proxy.password)}`;
          head += 'Proxy-Authorization: Basic ' +
            `${Buffer.from(credentials).toString('base64')}\r\n`;
        }
        socket.write(`${head}\r\n`);
      });

      let response = '';
      const onData = (chunk: Buffer) => {
        response += chunk.toString('latin1');
        const headerEnd = response.indexOf('\r\n\r\n');
        if (headerEnd < 0) {
          if (response.length > 16 * 1024) {
            fail(new Error('代理响应过大'));
          }
          return;
        }

        socket.removeListener('data', onData);
        const statusLine = response.slice(0, response.indexOf('\r\n'));
        const status = Number(statusLine.split(' ')[1]);
        if (status !== 200) {
          fail(new Error(`代理 CONNECT 失败：${statusLine}`));
          return;
        }

        socket.setTimeout(0);
        handshakeTimer = setTimeout(
          () => fail(new Error('代理 TLS 握手超时')),
          30_000,
        );
        const secure = tls.connect(
          { socket, servername: options.servername ?? host },
          () => {
            if (handshakeTimer) {
              clearTimeout(handshakeTimer);
            }
            if (settled) {
              return;
            }
            settled = true;
            callback(null, secure);
          },
        );
        secure.on('error', fail);
      };
      socket.on('data', onData);

      return undefined;
    };

  return agent;
}

function shouldProxy(hostname: string): boolean {
  const host = hostname.toLowerCase();
  if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
    return false;
  }

  const raw = process.env.NO_PROXY ?? process.env.no_proxy ?? '';
  for (const entry of raw.split(',')) {
    const pattern = entry.trim().toLowerCase().replace(/^\./, '');
    if (!pattern) {
      continue;
    }
    if (pattern === '*' || host === pattern || host.endsWith(`.${pattern}`)) {
      return false;
    }
  }

  return true;
}
