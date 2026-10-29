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
exports.resolveServerRid = resolveServerRid;
exports.ensureServerArchive = ensureServerArchive;
const node_crypto_1 = require("node:crypto");
const fs = __importStar(require("node:fs"));
const http = __importStar(require("node:http"));
const https = __importStar(require("node:https"));
const path = __importStar(require("node:path"));
const node_url_1 = require("node:url");
class HttpError extends Error {
    status;
    constructor(status) {
        super(`HTTP ${status}`);
        this.status = status;
    }
}
const REDIRECT_CODES = new Set([301, 302, 303, 307, 308]);
function resolveServerRid() {
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
    throw new Error(`当前平台暂不支持自动下载服务端：${process.platform}/${process.arch}`);
}
async function ensureServerArchive(options) {
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
    }
    else if (options.githubRepo) {
        const assets = await loadReleaseAssets(options.githubRepo, options.version);
        const archive = assets.find((entry) => entry.name === asset);
        if (!archive) {
            throw new Error(`Release v${options.version} 中缺少 ${asset}`);
        }
        await downloadFile(archive.apiUrl, archivePath, {
            Accept: 'application/octet-stream',
        });
        const checksum = assets.find((entry) => entry.name === `${asset}.sha256`);
        if (checksum) {
            const text = await downloadString(checksum.apiUrl, {
                Accept: 'application/octet-stream',
            });
            verifyHash(text, archivePath);
        }
        else {
            options.onProgress?.('未找到校验文件，跳过校验');
        }
    }
    else {
        throw new Error('未配置服务端下载地址');
    }
    return { rid, archivePath };
}
async function loadReleaseAssets(repositoryUrl, version) {
    const match = /github\.com[/:]([^/]+)\/([^/]+?)(?:\.git)?\/?$/i.exec(repositoryUrl);
    if (!match) {
        throw new Error(`无法解析 GitHub 仓库地址：${repositoryUrl}`);
    }
    const apiUrl = `https://api.github.com/repos/${match[1]}/${match[2]}` +
        `/releases/tags/v${version}`;
    let body;
    try {
        body = await downloadString(apiUrl, {
            Accept: 'application/vnd.github+json',
        });
    }
    catch (error) {
        if (error instanceof HttpError && error.status === 404) {
            throw new Error(`未找到 v${version} 的 GitHub Release`);
        }
        if (error instanceof HttpError &&
            (error.status === 403 || error.status === 429)) {
            throw new Error('GitHub API 限流，请稍后重试或配置镜像地址');
        }
        throw error;
    }
    let release;
    try {
        release = JSON.parse(body);
    }
    catch {
        throw new Error('无法解析 GitHub Release 信息');
    }
    return (release.assets ?? [])
        .filter((entry) => Boolean(entry.name && entry.url))
        .map((entry) => ({ name: entry.name, apiUrl: entry.url }));
}
async function verifyChecksumFromUrl(url, archivePath, onProgress) {
    let checksum;
    try {
        checksum = await downloadString(`${url}.sha256`);
    }
    catch (error) {
        if (error instanceof HttpError && error.status === 404) {
            onProgress?.('未找到校验文件，跳过校验');
            return;
        }
        throw error;
    }
    verifyHash(checksum, archivePath);
}
function verifyHash(checksum, archivePath) {
    const match = /([a-f0-9]{64})/i.exec(checksum);
    if (!match) {
        return;
    }
    const actual = (0, node_crypto_1.createHash)('sha256')
        .update(fs.readFileSync(archivePath))
        .digest('hex');
    if (actual.toLowerCase() !== match[1].toLowerCase()) {
        fs.rmSync(archivePath, { force: true });
        throw new Error('服务端下载校验失败，请重试');
    }
}
function downloadFile(url, destination, headers = {}, redirects = 0) {
    return new Promise((resolve, reject) => {
        const parsed = new node_url_1.URL(url);
        const client = parsed.protocol === 'https:' ? https : http;
        const request = client.get(parsed, {
            headers: {
                'User-Agent': 'TokenTracker-VSCode',
                ...headers,
            },
        }, (response) => {
            const status = response.statusCode ?? 0;
            if (REDIRECT_CODES.has(status) && response.headers.location) {
                response.resume();
                if (redirects >= 5) {
                    reject(new Error('服务端下载重定向过多'));
                    return;
                }
                resolve(downloadFile(new node_url_1.URL(response.headers.location, parsed).toString(), destination, headers, redirects + 1));
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
        });
        request.setTimeout(120_000, () => {
            request.destroy(new Error('服务端下载超时'));
        });
        request.on('error', reject);
    });
}
function downloadString(url, headers = {}, redirects = 0) {
    return new Promise((resolve, reject) => {
        const parsed = new node_url_1.URL(url);
        const client = parsed.protocol === 'https:' ? https : http;
        const request = client.get(parsed, {
            headers: {
                'User-Agent': 'TokenTracker-VSCode',
                ...headers,
            },
        }, (response) => {
            const status = response.statusCode ?? 0;
            if (REDIRECT_CODES.has(status) && response.headers.location) {
                response.resume();
                if (redirects >= 5) {
                    reject(new Error('校验文件重定向过多'));
                    return;
                }
                resolve(downloadString(new node_url_1.URL(response.headers.location, parsed).toString(), headers, redirects + 1));
                return;
            }
            if (status !== 200) {
                response.resume();
                reject(new HttpError(status));
                return;
            }
            let body = '';
            response.setEncoding('utf8');
            response.on('data', (chunk) => {
                body += chunk;
            });
            response.on('end', () => resolve(body));
        });
        request.setTimeout(30_000, () => {
            request.destroy(new Error('校验文件下载超时'));
        });
        request.on('error', reject);
    });
}
//# sourceMappingURL=serverDownloader.js.map