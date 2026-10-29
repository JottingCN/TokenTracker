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
    const url = `${options.baseUrl.replace(/\/+$/, '')}/${asset}`;
    const directory = path.join(options.storagePath, 'downloads');
    fs.mkdirSync(directory, { recursive: true });
    const archivePath = path.join(directory, asset);
    options.onProgress?.(`正在下载 ${asset}`);
    await downloadFile(url, archivePath);
    await verifyChecksum(url, archivePath, options.onProgress);
    return { rid, archivePath };
}
async function verifyChecksum(url, archivePath, onProgress) {
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
function downloadFile(url, destination, redirects = 0) {
    return new Promise((resolve, reject) => {
        const parsed = new node_url_1.URL(url);
        const client = parsed.protocol === 'https:' ? https : http;
        const request = client.get(parsed, { headers: { 'User-Agent': 'TokenTracker-VSCode' } }, (response) => {
            const status = response.statusCode ?? 0;
            if (REDIRECT_CODES.has(status) && response.headers.location) {
                response.resume();
                if (redirects >= 5) {
                    reject(new Error('服务端下载重定向过多'));
                    return;
                }
                resolve(downloadFile(new node_url_1.URL(response.headers.location, parsed).toString(), destination, redirects + 1));
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
function downloadString(url, redirects = 0) {
    return new Promise((resolve, reject) => {
        const parsed = new node_url_1.URL(url);
        const client = parsed.protocol === 'https:' ? https : http;
        const request = client.get(parsed, { headers: { 'User-Agent': 'TokenTracker-VSCode' } }, (response) => {
            const status = response.statusCode ?? 0;
            if (REDIRECT_CODES.has(status) && response.headers.location) {
                response.resume();
                if (redirects >= 5) {
                    reject(new Error('校验文件重定向过多'));
                    return;
                }
                resolve(downloadString(new node_url_1.URL(response.headers.location, parsed).toString(), redirects + 1));
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