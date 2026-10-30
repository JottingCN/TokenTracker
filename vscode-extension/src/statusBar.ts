import * as vscode from 'vscode';
import { httpGetJson } from './httpClient';
import { tr } from './i18n';
import {
  formatCost,
  formatMoney,
  MoneySettings,
} from './money';
import { serverUrl } from './serverManager';

interface SessionSummary {
  sessionId: string;
  sessionDisplay: string;
  title: string | null;
  model: string;
  workspacePath: string | null;
  lastSeenMs: number;
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  costCny: number;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
  unpricedCalls: number;
  hitRate: number | null;
  inputText: string;
  cacheText: string;
  outputText: string;
  reasoningText: string;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  reasoningCostText: string;
  costText: string;
}

interface SessionCall {
  startTimeMs: number;
  model: string;
  isMain: boolean;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  costCny: number | null;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
}

interface SessionPayload {
  sessionId: string;
  calls: SessionCall[];
}

interface DashboardSnapshot {
  summary: {
    hitRate: number | null;
    costCny: number;
  };
  sessions: SessionSummary[];
}

interface ModelBreakdown {
  model: string;
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  costCny: number;
  unpricedCalls: number;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
}

interface LatestScope {
  session?: SessionSummary;
  hitRate: number | null;
  costCny: number;
  hasCost: boolean;
}

const BEIJING_OFFSET_MS = 8 * 60 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;
const numberFormatter = new Intl.NumberFormat('zh-CN');

export class StatusBarController implements vscode.Disposable {
  private readonly item: vscode.StatusBarItem;
  private socket: WebSocket | undefined;
  private retryTimer: NodeJS.Timeout | undefined;
  private pollTimer: NodeJS.Timeout | undefined;
  private disposed = false;
  private workspaceRoots: string[];
  private readonly breakdownCache = new Map<string, ModelBreakdown[]>();
  private pendingKey: string | undefined;
  private currentSession: SessionSummary | undefined;

  constructor(
    private readonly port: number,
    private readonly rangeIndex: number,
    private readonly rangeLabel: string,
    workspaceRoots: string[],
    private money: MoneySettings,
    private readonly onSnapshot?: (detail: string) => void,
  ) {
    this.workspaceRoots = workspaceRoots.map(normalizePath);

    this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    this.item.name = 'Token Tracker';
    this.item.command = 'tokentracker.openDashboard';
    this.item.text = '$(pulse) -';
    this.item.tooltip = tr(
      'Token Tracker：正在连接本地服务…',
      'Token Tracker: connecting to the local server…',
    );
    this.item.hide();
  }

  setWorkspaceRoots(roots: string[]): void {
    this.workspaceRoots = roots.map(normalizePath);
    this.refresh();
  }

  setMoneySettings(settings: MoneySettings): void {
    this.money = settings;
    if (this.currentSession) {
      this.updateTooltip(
        this.currentSession,
        this.breakdownCache.get(this.breakdownKey(this.currentSession)),
      );
      this.renderStatusText(this.currentSession);
    }
  }

  start(): void {
    this.connect();
  }

  refresh(): void {
    if (this.pollTimer) {
      clearTimeout(this.pollTimer);
      this.pollTimer = undefined;
    }
    if (typeof WebSocket !== 'undefined' && this.socket?.readyState === WebSocket.OPEN) {
      this.subscribe();
    } else if (typeof WebSocket === 'undefined') {
      void this.poll();
    } else {
      this.connect();
    }
  }

  dispose(): void {
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

  private connect(): void {
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
          const message = JSON.parse(String(event.data)) as {
            type?: string;
            data?: DashboardSnapshot;
          };
          if (message.type === 'snapshot' && message.data?.summary) {
            this.render(message.data);
          }
        } catch {
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
    } catch {
      this.scheduleReconnect();
    }
  }

  private subscribe(): void {
    this.socket?.send(
      JSON.stringify({
        type: 'subscribe',
        view: 'dashboard',
        range: this.rangeIndex,
        utility: false,
        client: 'vscode',
      }),
    );
  }

  private scheduleReconnect(): void {
    if (this.disposed) {
      return;
    }

    if (this.retryTimer) {
      clearTimeout(this.retryTimer);
    }
    this.retryTimer = setTimeout(() => this.connect(), 3000);
  }

  private async poll(): Promise<void> {
    if (this.disposed) {
      return;
    }

    const data = await httpGetJson<DashboardSnapshot>(
      `${serverUrl(this.port)}/api/dashboard?range=${this.rangeIndex}`,
      2000,
    );
    if (data?.summary) {
      this.render(data);
    } else {
      this.item.text = tr(
        '$(warning) Token Tracker: 服务不可用',
        '$(warning) Token Tracker: service unavailable',
      );
      this.item.show();
    }

    if (!this.disposed) {
      if (this.pollTimer) {
        clearTimeout(this.pollTimer);
      }
      this.pollTimer = setTimeout(() => void this.poll(), 5000);
    }
  }

  private render(data: DashboardSnapshot): void {
    const latest = this.resolveLatest(data);
    const session = latest.session;

    if (
      !session ||
      session.inputTokens + session.outputTokens + session.cacheReadTokens <= 0
    ) {
      this.currentSession = undefined;
      this.item.hide();
      this.onSnapshot?.(
        `hidden: no tokens for current workspace (range=${this.rangeLabel})`,
      );
      return;
    }

    this.item.show();
    this.currentSession = session;
    this.renderStatusText(session);

    const key = this.breakdownKey(session);
    const cached = this.breakdownCache.get(key);
    this.updateTooltip(session, cached);
    if (cached) {
      this.touchCache(key, cached);
    } else {
      void this.ensureBreakdown(session);
    }

    this.onSnapshot?.(
      `${this.item.text} | range=${this.rangeLabel} ` +
        `| session=${session.sessionId} ` +
        `| models=${cached?.length ?? '?'} ` +
        `| input=${session.inputText} ` +
        `| cost=${session.costText}`,
    );
  }

  private renderStatusText(session: SessionSummary): void {
    const rateText =
      session.hitRate === null
        ? '-'
        : `${(session.hitRate * 100).toFixed(2)}%`;
    const costText = sessionHasCost(session)
      ? ` · ${formatMoney(session.costCny, this.money, 2)}`
      : '';
    this.item.text = `$(pulse) ${rateText}${costText}`;
  }

  private updateTooltip(
    session: SessionSummary,
    breakdown: ModelBreakdown[] | undefined,
  ): void {
    const tooltip = new vscode.MarkdownString();
    tooltip.supportThemeIcons = true;

    tooltip.appendMarkdown(
      `**Token Tracker - ${escapeMarkdown(session.sessionDisplay)}**\n\n`,
    );

    const modelText = breakdown
      ? ` · $(symbol-class) ${breakdown.length} ${tr('个模型', 'models')}`
      : '';
    tooltip.appendMarkdown(
      `$(calendar) ${this.rangeLabel}${modelText}\n\n`,
    );

    if (breakdown && breakdown.length > 0) {
      breakdown.forEach((row, index) => {
        if (index > 0) {
          tooltip.appendMarkdown('\n---\n\n');
        }

        tooltip.appendMarkdown(`**${escapeMarkdown(row.model)}**\n\n`);
        tooltip.appendMarkdown(`${modelMeta(row, this.money)}\n\n`);
        tooltip.appendCodeblock(
          modelTable(
            row.inputTokens,
            row.cacheReadTokens,
            row.outputTokens,
            row.reasoningTokens,
            row.uncachedCostCny,
            row.cacheCostCny,
            row.outputCostCny,
            row.reasoningCostCny,
            this.money,
          ),
          'text',
        );
      });
    } else {
      tooltip.appendMarkdown(`**${tr('会话汇总', 'Session summary')}**\n\n`);
      tooltip.appendMarkdown(
        `${numberFormatter.format(session.calls)} ${tr('次', 'calls')} · ` +
          `${tr('命中率', 'Hit rate')} ${formatRate(session.hitRate)} · ` +
          `${tr('费用', 'Cost')} ${formatCost(session.costCny, session.unpricedCalls, this.money)}\n\n`,
      );
      tooltip.appendCodeblock(
        tableFromStrings([
          [
            tr('输入', 'Input'),
            session.inputText,
            formatMoney(session.uncachedCostCny, this.money),
          ],
          [
            tr('缓存', 'Cached'),
            session.cacheText,
            formatMoney(session.cacheCostCny, this.money),
          ],
          [
            tr('输出', 'Output'),
            session.outputText,
            formatMoney(session.outputCostCny, this.money),
          ],
          [
            tr('思考', 'Reasoning'),
            session.reasoningText,
            session.reasoningTokens > 0
              ? formatMoney(session.reasoningCostCny, this.money)
              : '-',
          ],
        ]),
        'text',
      );
    }

    this.item.tooltip = tooltip;
  }

  private async ensureBreakdown(session: SessionSummary): Promise<void> {
    const key = this.breakdownKey(session);
    if (this.breakdownCache.has(key) || this.pendingKey === key) {
      return;
    }

    this.pendingKey = key;
    const data = await httpGetJson<SessionPayload>(
      `${serverUrl(this.port)}/api/session/${encodeURIComponent(session.sessionId)}`,
      3000,
    );
    if (this.pendingKey === key) {
      this.pendingKey = undefined;
    }
    if (this.disposed || !data?.calls) {
      return;
    }

    const { fromMs, toMs } = rangeBounds(this.rangeIndex);
    const breakdown = aggregateModels(
      data.calls.filter(
        (call) => call.isMain && call.startTimeMs >= fromMs && call.startTimeMs < toMs,
      ),
    );
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

  private touchCache(key: string, value: ModelBreakdown[]): void {
    this.breakdownCache.delete(key);
    this.breakdownCache.set(key, value);
  }

  private breakdownKey(session: SessionSummary): string {
    return `${session.sessionId}|${session.lastSeenMs}|${this.rangeIndex}`;
  }

  private resolveLatest(data: DashboardSnapshot): LatestScope {
    const sessions = data.sessions ?? [];
    const filtered = this.workspaceRoots.length === 0
      ? sessions
      : sessions.filter((session) => {
          if (!session.workspacePath) {
            return false;
          }
          const path = normalizePath(session.workspacePath);
          return this.workspaceRoots.some((root) => isPathWithin(path, root));
        });

    if (filtered.length === 0) {
      return { hitRate: null, costCny: 0, hasCost: false };
    }

    const latest = filtered.reduce((current, session) =>
      session.lastSeenMs > current.lastSeenMs ? session : current,
    );

    return {
      session: latest,
      hitRate: latest.hitRate,
      costCny: latest.costCny,
      hasCost: sessionHasCost(latest),
    };
  }
}

function aggregateModels(calls: SessionCall[]): ModelBreakdown[] {
  const models = new Map<string, ModelBreakdown>();

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

  return [...models.values()].sort(
    (a, b) =>
      b.costCny - a.costCny ||
      b.inputTokens - a.inputTokens ||
      a.model.localeCompare(b.model),
  );
}

function modelMeta(row: ModelBreakdown, money: MoneySettings): string {
  const hitRate =
    row.inputTokens > 0 ? row.cacheReadTokens / row.inputTokens : null;
  return (
    `$(zap) ${numberFormatter.format(row.calls)} ${tr('次', 'calls')} · ` +
    `$(pulse) ${formatRate(hitRate)} · ` +
    `$(credit-card) ${formatCost(row.costCny, row.unpricedCalls, money)}`
  );
}

function modelTable(
  inputTokens: number,
  cacheReadTokens: number,
  outputTokens: number,
  reasoningTokens: number,
  uncachedCost: number,
  cacheCost: number,
  outputCost: number,
  reasoningCost: number,
  money: MoneySettings,
): string {
  return tableFromStrings([
    [
      tr('输入', 'Input'),
      numberFormatter.format(inputTokens),
      formatMoney(uncachedCost, money),
    ],
    [
      tr('缓存', 'Cached'),
      numberFormatter.format(cacheReadTokens),
      formatMoney(cacheCost, money),
    ],
    [
      tr('输出', 'Output'),
      numberFormatter.format(outputTokens),
      formatMoney(outputCost, money),
    ],
    [
      tr('思考', 'Reasoning'),
      numberFormatter.format(reasoningTokens),
      reasoningTokens > 0 ? formatMoney(reasoningCost, money) : '-',
    ],
  ]);
}

function tableFromStrings(rows: Array<[string, string, string]>): string {
  return rows
    .map(
      ([label, tokens, cost]) =>
        `${label}  ${tokens.padStart(10, ' ')}  ${cost.padStart(11, ' ')}`,
    )
    .join('\n');
}

function formatRate(rate: number | null): string {
  return rate === null ? '-' : `${(rate * 100).toFixed(2)}%`;
}

function sessionHasCost(session: SessionSummary): boolean {
  return session.costCny > 0 || session.unpricedCalls === 0;
}

function rangeBounds(rangeIndex: number): { fromMs: number; toMs: number } {
  const now = Date.now();
  const beijingNow = now + BEIJING_OFFSET_MS;
  const todayStart =
    Math.floor(beijingNow / DAY_MS) * DAY_MS - BEIJING_OFFSET_MS;

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

function normalizePath(value: string): string {
  const windows = process.platform === 'win32';
  const separator = windows ? '\\' : '/';
  let result = windows
    ? value.replace(/\//g, '\\')
    : value.replace(/\\/g, '/');
  while (result.length > 1 && result.endsWith(separator)) {
    result = result.slice(0, -1);
  }
  return windows ? result.toLowerCase() : result;
}

function isPathWithin(pathValue: string, root: string): boolean {
  if (pathValue === root) {
    return true;
  }
  return pathValue.startsWith(root + (process.platform === 'win32' ? '\\' : '/'));
}

function escapeMarkdown(value: string): string {
  return value.replace(/[\\`*_\[\]<>]/g, (character) => `\\${character}`);
}
