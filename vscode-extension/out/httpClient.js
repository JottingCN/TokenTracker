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
exports.httpGet = httpGet;
exports.httpGetJson = httpGetJson;
const http = __importStar(require("node:http"));
function httpGet(url, timeoutMs) {
    return new Promise((resolve, reject) => {
        const request = http.get(url, (response) => {
            let body = '';
            response.setEncoding('utf8');
            response.on('data', (chunk) => {
                body += chunk;
            });
            response.on('end', () => {
                resolve({ status: response.statusCode ?? 0, body });
            });
        });
        request.setTimeout(timeoutMs, () => {
            request.destroy(new Error('request timeout'));
        });
        request.on('error', reject);
    });
}
async function httpGetJson(url, timeoutMs) {
    try {
        const response = await httpGet(url, timeoutMs);
        if (response.status !== 200) {
            return null;
        }
        return JSON.parse(response.body);
    }
    catch {
        return null;
    }
}
//# sourceMappingURL=httpClient.js.map