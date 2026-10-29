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
exports.StatusBarController = void 0;
const vscode = __importStar(require("vscode"));
const httpClient_1 = require("./httpClient");
const serverManager_1 = require("./serverManager");
const BEIJING_OFFSET_MS = 8 * 60 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;
const numberFormatter = new Intl.NumberFormat('zh-CN');
class StatusBarController {
    port;
    rangeIndex;
    rangeLabel;
    onSnapshot;
    item;
    socket;
    retryTimer;
    pollTimer;
    disposed = false;
    workspaceRoots;
    breakdownCache = new Map();
    pendingKey;
    currentSession;
    constructor(port, rangeIndex, rangeLabel, workspaceRoots, onSnapshot) {
        this.port = port;
        this.rangeIndex = rangeIndex;
        this.rangeLabel = rangeLabel;
        this.onSnapshot = onSnapshot;
        this.workspaceRoots = workspaceRoots.map(normalizePath);
        this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
        this.item.name = 'Token Tracker';
        this.item.command = 'tokentracker.openDashboard';
        this.item.text = '$(pulse) -';
        this.item.tooltip = 'Token Tracker：正在连接本地服务…';
        this.item.hide();
    }
    setWorkspaceRoots(roots) {
        this.workspaceRoots = roots.map(normalizePath);
        this.refresh();
    }
    start() {
        this.connect();
    }
    refresh() {
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = undefined;
        }
        if (typeof WebSocket !== 'undefined' && this.socket?.readyState === WebSocket.OPEN) {
            this.subscribe();
        }
        else if (typeof WebSocket === 'undefined') {
            void this.poll();
        }
        else {
            this.connect();
        }
    }
    dispose() {
        this.disposed = true;
        if (this.retryTimer) {
            clearTimeout(this.retryTimer);
        }
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
        }
        this.breakdownCache.clear();
        this.socket?.close();
        this.socket = undefined;
        this.item.dispose();
    }
    connect() {
        if (this.disposed) {
            return;
        }
        if (typeof WebSocket === 'undefined') {
            void this.poll();
            return;
        }
        try {
            const socket = new WebSocket(`ws://127.0.0.1:${this.port}/ws`);
            this.socket = socket;
            socket.onopen = () => {
                this.subscribe();
            };
            socket.onmessage = (event) => {
                try {
                    const message = JSON.parse(String(event.data));
                    if (message.type === 'snapshot' && message.data?.summary) {
                        this.render(message.data);
                    }
                }
                catch {
                    // Ignore malformed frames.
                }
            };
            socket.onclose = () => {
                this.socket = undefined;
                this.scheduleReconnect();
            };
            socket.onerror = () => {
                socket.close();
            };
        }
        catch {
            this.scheduleReconnect();
        }
    }
    subscribe() {
        this.socket?.send(JSON.stringify({
            type: 'subscribe',
            view: 'dashboard',
            range: this.rangeIndex,
            utility: false,
            client: 'vscode',
        }));
    }
    scheduleReconnect() {
        if (this.disposed) {
            return;
        }
        if (this.retryTimer) {
            clearTimeout(this.retryTimer);
        }
        this.retryTimer = setTimeout(() => this.connect(), 3000);
    }
    async poll() {
        if (this.disposed) {
            return;
        }
        const data = await (0, httpClient_1.httpGetJson)(`${(0, serverManager_1.serverUrl)(this.port)}/api/dashboard?range=${this.rangeIndex}`, 2000);
        if (data?.summary) {
            this.render(data);
        }
        else {
            this.item.text = '$(warning) Token Tracker: 服务不可用';
            this.item.show();
        }
        if (!this.disposed) {
            if (this.pollTimer) {
                clearTimeout(this.pollTimer);
            }
            this.pollTimer = setTimeout(() => void this.poll(), 5000);
        }
    }
    render(data) {
        const latest = this.resolveLatest(data);
        const session = latest.session;
        if (!session ||
            session.inputTokens + session.outputTokens + session.cacheReadTokens <= 0) {
            this.currentSession = undefined;
            this.item.hide();
            this.onSnapshot?.(`hidden: no tokens for current workspace (range=${this.rangeLabel})`);
            return;
        }
        this.item.show();
        this.currentSession = session;
        const rateText = latest.hitRate === null ? '-' : `${(latest.hitRate * 100).toFixed(2)}%`;
        const costText = latest.hasCost ? ` · ¥${latest.costCny.toFixed(2)}` : '';
        this.item.text = `$(pulse) ${rateText}${costText}`;
        const key = this.breakdownKey(session);
        const cached = this.breakdownCache.get(key);
        this.updateTooltip(session, cached);
        if (cached) {
            this.touchCache(key, cached);
        }
        else {
            void this.ensureBreakdown(session);
        }
        this.onSnapshot?.(`${this.item.text} | range=${this.rangeLabel} ` +
            `| session=${session.sessionId} ` +
            `| models=${cached?.length ?? '?'} ` +
            `| input=${session.inputText} ` +
            `| cost=${session.costText}`);
    }
    updateTooltip(session, breakdown) {
        const tooltip = new vscode.MarkdownString();
        tooltip.supportThemeIcons = true;
        tooltip.appendMarkdown(`**Token Tracker - ${escapeMarkdown(session.sessionDisplay)}**\n\n`);
        const modelText = breakdown ? ` · $(symbol-class) ${breakdown.length} 个模型` : '';
        tooltip.appendMarkdown(`$(calendar) ${this.rangeLabel}${modelText}\n\n`);
        if (breakdown && breakdown.length > 0) {
            breakdown.forEach((row, index) => {
                if (index > 0) {
                    tooltip.appendMarkdown('\n---\n\n');
                }
                tooltip.appendMarkdown(`**${escapeMarkdown(row.model)}**\n\n`);
                tooltip.appendMarkdown(`${modelMeta(row)}\n\n`);
                tooltip.appendCodeblock(modelTable(row.inputTokens, row.cacheReadTokens, row.outputTokens, row.reasoningTokens, row.uncachedCostCny, row.cacheCostCny, row.outputCostCny, row.reasoningCostCny), 'text');
            });
        }
        else {
            tooltip.appendMarkdown('**会话汇总**\n\n');
            tooltip.appendMarkdown(`${numberFormatter.format(session.calls)} 次 · ` +
                `命中率 ${formatRate(session.hitRate)} · ` +
                `费用 ${formatCost(session.costCny, session.unpricedCalls)}\n\n`);
            tooltip.appendCodeblock(tableFromStrings([
                ['输入', session.inputText, session.uncachedCostText],
                ['缓存', session.cacheText, session.cacheCostText],
                ['输出', session.outputText, session.outputCostText],
                [
                    '思考',
                    session.reasoningText,
                    session.reasoningTokens > 0 ? session.reasoningCostText : '-',
                ],
            ]), 'text');
        }
        this.item.tooltip = tooltip;
    }
    async ensureBreakdown(session) {
        const key = this.breakdownKey(session);
        if (this.breakdownCache.has(key) || this.pendingKey === key) {
            return;
        }
        this.pendingKey = key;
        const data = await (0, httpClient_1.httpGetJson)(`${(0, serverManager_1.serverUrl)(this.port)}/api/session/${encodeURIComponent(session.sessionId)}`, 3000);
        if (this.pendingKey === key) {
            this.pendingKey = undefined;
        }
        if (this.disposed || !data?.calls) {
            return;
        }
        const { fromMs, toMs } = rangeBounds(this.rangeIndex);
        const breakdown = aggregateModels(data.calls.filter((call) => call.isMain && call.startTimeMs >= fromMs && call.startTimeMs < toMs));
        if (breakdown.length === 0) {
            return;
        }
        this.breakdownCache.set(key, breakdown);
        if (this.breakdownCache.size > 24) {
            const oldest = this.breakdownCache.keys().next().value;
            if (oldest !== undefined && oldest !== key) {
                this.breakdownCache.delete(oldest);
            }
        }
        if (this.currentSession && this.breakdownKey(this.currentSession) === key) {
            this.updateTooltip(this.currentSession, breakdown);
        }
    }
    touchCache(key, value) {
        this.breakdownCache.delete(key);
        this.breakdownCache.set(key, value);
    }
    breakdownKey(session) {
        return `${session.sessionId}|${session.lastSeenMs}|${this.rangeIndex}`;
    }
    resolveLatest(data) {
        const sessions = data.sessions ?? [];
        const filtered = this.workspaceRoots.length === 0
            ? sessions
            : sessions.filter((session) => {
                if (!session.workspacePath) {
                    return false;
                }
                const path = normalizePath(session.workspacePath);
                return this.workspaceRoots.some((root) => path === root || path.startsWith(root + '\\'));
            });
        if (filtered.length === 0) {
            return { hitRate: null, costCny: 0, hasCost: false };
        }
        const latest = filtered.reduce((current, session) => session.lastSeenMs > current.lastSeenMs ? session : current);
        return {
            session: latest,
            hitRate: latest.hitRate,
            costCny: latest.costCny,
            hasCost: sessionHasCost(latest),
        };
    }
}
exports.StatusBarController = StatusBarController;
function aggregateModels(calls) {
    const models = new Map();
    for (const call of calls) {
        const model = call.model || '-';
        let row = models.get(model);
        if (!row) {
            row = {
                model,
                calls: 0,
                inputTokens: 0,
                cacheReadTokens: 0,
                outputTokens: 0,
                reasoningTokens: 0,
                costCny: 0,
                unpricedCalls: 0,
                cacheCostCny: 0,
                uncachedCostCny: 0,
                outputCostCny: 0,
                reasoningCostCny: 0,
            };
            models.set(model, row);
        }
        row.calls += 1;
        row.inputTokens += call.inputTokens;
        row.cacheReadTokens += call.cacheReadTokens;
        row.outputTokens += call.outputTokens;
        row.reasoningTokens += call.reasoningTokens;
        row.costCny += call.costCny ?? 0;
        if (call.costCny === null) {
            row.unpricedCalls += 1;
        }
        row.cacheCostCny += call.cacheCostCny;
        row.uncachedCostCny += call.uncachedCostCny;
        row.outputCostCny += call.outputCostCny;
        row.reasoningCostCny += call.reasoningCostCny;
    }
    return [...models.values()].sort((a, b) => b.costCny - a.costCny ||
        b.inputTokens - a.inputTokens ||
        a.model.localeCompare(b.model));
}
function modelMeta(row) {
    const hitRate = row.inputTokens > 0 ? row.cacheReadTokens / row.inputTokens : null;
    return (`$(zap) ${numberFormatter.format(row.calls)} 次 · ` +
        `$(pulse) ${formatRate(hitRate)} · ` +
        `$(credit-card) ${formatCost(row.costCny, row.unpricedCalls)}`);
}
function formatCost(cost, unpricedCalls) {
    return unpricedCalls > 0
        ? `¥${cost.toFixed(6)} + ${unpricedCalls} 次未定价`
        : `¥${cost.toFixed(6)}`;
}
function modelTable(inputTokens, cacheReadTokens, outputTokens, reasoningTokens, uncachedCost, cacheCost, outputCost, reasoningCost) {
    return tableFromStrings([
        ['输入', numberFormatter.format(inputTokens), `¥${uncachedCost.toFixed(6)}`],
        ['缓存', numberFormatter.format(cacheReadTokens), `¥${cacheCost.toFixed(6)}`],
        ['输出', numberFormatter.format(outputTokens), `¥${outputCost.toFixed(6)}`],
        [
            '思考',
            numberFormatter.format(reasoningTokens),
            reasoningTokens > 0 ? `¥${reasoningCost.toFixed(6)}` : '-',
        ],
    ]);
}
function tableFromStrings(rows) {
    return rows
        .map(([label, tokens, cost]) => `${label}  ${tokens.padStart(10, ' ')}  ${cost.padStart(11, ' ')}`)
        .join('\n');
}
function formatRate(rate) {
    return rate === null ? '-' : `${(rate * 100).toFixed(2)}%`;
}
function sessionHasCost(session) {
    return session.costCny > 0 || session.unpricedCalls === 0;
}
function rangeBounds(rangeIndex) {
    const now = Date.now();
    const beijingNow = now + BEIJING_OFFSET_MS;
    const todayStart = Math.floor(beijingNow / DAY_MS) * DAY_MS - BEIJING_OFFSET_MS;
    switch (rangeIndex) {
        case 0:
            return { fromMs: todayStart, toMs: Number.MAX_SAFE_INTEGER };
        case 1:
            return { fromMs: todayStart - DAY_MS, toMs: todayStart };
        case 2:
            return { fromMs: now - 7 * DAY_MS, toMs: Number.MAX_SAFE_INTEGER };
        case 3:
            return { fromMs: now - 30 * DAY_MS, toMs: Number.MAX_SAFE_INTEGER };
        default:
            return { fromMs: 0, toMs: Number.MAX_SAFE_INTEGER };
    }
}
function normalizePath(value) {
    let path = value.replace(/\//g, '\\');
    while (path.endsWith('\\')) {
        path = path.slice(0, -1);
    }
    return path.toLowerCase();
}
function escapeMarkdown(value) {
    return value.replace(/[\\`*_\[\]<>]/g, (character) => `\\${character}`);
}
//# sourceMappingURL=statusBar.js.map