import * as vscode from 'vscode';
import { httpGetJson } from './httpClient';
import { tr } from './i18n';
import {
  formatCost,
  formatMoney,
  MoneySettings,
} from './money';
import { serverUrl } from './serverManager';

export const DEFAULT_STATUS_BAR_FORMAT =
  '$(pulse) {hit} · {ttft} · {speed} · {cost}';

export function readStatusBarFormat(): string {
  const value = vscode.workspace
    .getConfiguration('tokentracker')
    .get<string>('statusBarFormat', DEFAULT_STATUS_BAR_FORMAT);
  return normalizeStatusBarFormat(value);
}

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
  firstSeenMs: number;
  avgDurationMs: number | null;
  avgTtftMs: number | null;
  ttftSamples: number;
  tokensPerSecond: number | null;
  events: number;
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
  durationMs: number | null;
  ttftMs: number | null;
  tokensPerSecond: number | null;
  costCny: number | null;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
}

interface SessionEventSummary {
  operation: string;
  count: number;
  avgDurationMs: number | null;
  errorCount: number;
  lastSeenMs: number;
}

interface SessionToolSummary {
  tool: string;
  calls: number;
  avgDurationMs: number | null;
  maxDurationMs: number | null;
  errorCount: number;
  lastSeenMs: number;
  toolTypes: string[];
  editTypes: string[];
  skillNames: string[];
  mcpServerHashes: string[];
  mcpToolNames: string[];
  errorTypes: string[];
  agentNames: string[];
}

interface AgentRunSummary {
  traceId: string;
  spanId: string;
  isRoot: boolean;
  agentText: string;
  agentTypeText: string;
  providerText: string;
  modelText: string;
  repoText: string;
  branchText: string;
  turnCount: number | null;
  turnText: string;
  inputText: string;
  cacheText: string;
  outputText: string;
  startTimeMs: number;
  durationText: string;
  isError: boolean;
  errorText: string;
}

interface HookSummary {
  hookType: string;
  hookTypeText: string;
  decision: string;
  decisionText: string;
  resultKind: string;
  resultKindText: string;
  count: number;
  countText: string;
  avgDurationText: string;
  errorCount: number;
  toolNames: string[];
  toolNamesText: string;
  lastSeenMs: number;
}

interface SignalHealthSummary {
  agentRuns: number;
  hookEvents: number;
  metricPoints: number;
  lastSignalText: string;
}

interface SessionPayload {
  sessionId: string;
  calls: SessionCall[];
  events: SessionEventSummary[];
  tools: SessionToolSummary[];
  agentRuns: AgentRunSummary[];
  hooks: HookSummary[];
  signalHealth: SignalHealthSummary;
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
  ttftMs: number | null;
  tokensPerSecond: number | null;
  firstSeenMs: number;
  lastSeenMs: number;
  totalDurationMs: number;
  durationSamples: number;
  ttftSamples: number;
  generationOutput: number;
  generationMs: number;
}

interface UsageTotals {
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  costCny: number;
  unpricedCalls: number;
  totalDurationMs: number;
  durationSamples: number;
  ttftMs: number | null;
  tokensPerSecond: number | null;
}

interface SessionDetails {
  models: ModelBreakdown[];
  events: SessionEventSummary[];
  tools: SessionToolSummary[];
  agentRuns: AgentRunSummary[];
  hooks: HookSummary[];
  signalHealth: SignalHealthSummary;
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
  private readonly breakdownCache = new Map<string, SessionDetails>();
  private pendingKey: string | undefined;
  private currentSession: SessionSummary | undefined;

  constructor(
    private readonly port: number,
    private readonly rangeIndex: number,
    private readonly rangeLabel: string,
    workspaceRoots: string[],
    private money: MoneySettings,
    private statusBarFormat: string,
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

  setStatusBarFormat(format: string): void {
    this.statusBarFormat = normalizeStatusBarFormat(format);
    if (this.currentSession) {
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
        `| models=${cached?.models.length ?? '?'} ` +
        `| input=${session.inputText} ` +
        `| cost=${session.costText}`,
    );
  }

  private renderStatusText(session: SessionSummary): void {
    const details = this.breakdownCache.get(this.breakdownKey(session));
    this.item.text = renderStatusTemplate(
      this.statusBarFormat,
      session,
      this.rangeLabel,
      this.money,
      details,
    );
  }

  private updateTooltip(
    session: SessionSummary,
    details: SessionDetails | undefined,
  ): void {
    const tooltip = new vscode.MarkdownString();
    tooltip.supportThemeIcons = true;

    const appendSection = (icon: string, title: string, summary?: string) => {
      tooltip.appendMarkdown(`
---

**${icon} ${title}**

`);
      if (summary) {
        tooltip.appendMarkdown(`${summary}

`);
      }
    };

    const appendBullet = (name: string, parts: Array<string | null>) => {
      const detail = parts.filter((part): part is string => Boolean(part))
        .join(' · ');
      tooltip.appendMarkdown(
        `- **${escapeMarkdown(name)}**${detail ? ` · ${detail}` : ''}
`,
      );
    };

    tooltip.appendMarkdown(
      `**Token Tracker · ${escapeMarkdown(session.sessionDisplay)}**

`,
    );
    tooltip.appendMarkdown(
      `$(calendar) ${this.rangeLabel} · $(clock) ` +
        `${formatTimeRange(session.firstSeenMs, session.lastSeenMs)}
`,
    );

    const models = details?.models ?? [];
    if (models.length > 0) {
      for (const row of models) {
        appendSection(
          '$(symbol-method)',
          escapeMarkdown(row.model),
          formatShortTime(row.lastSeenMs),
        );
        tooltip.appendCodeblock(modelTableFor(row, this.money), 'text');
        tooltip.appendMarkdown(
          `
${summaryMarkdown(modelTotals(row), this.money)}
`,
        );
      }
    } else {
      appendSection('$(symbol-method)', tr('Token 明细', 'Token details'));
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
      tooltip.appendMarkdown(
        `
${summaryMarkdown(sessionTotals(session), this.money)}
`,
      );
    }

    if (details && details.tools.length > 0) {
      const totalCalls = details.tools.reduce(
        (total, row) => total + row.calls,
        0,
      );
      appendSection(
        '$(tools)',
        tr('工具调用', 'Tool calls'),
        `${numberFormatter.format(totalCalls)} ${tr('次', 'calls')} · ` +
          `${numberFormatter.format(details.tools.length)} ${tr('种工具', 'tools')}`,
      );
      for (const row of details.tools.slice(0, 5)) {
        appendBullet(row.tool, [
          `${numberFormatter.format(row.calls)} ${tr('次', 'calls')}`,
          `${tr('平均', 'Avg')} ${formatDuration(row.avgDurationMs)}`,
          row.toolTypes.length > 0 ? row.toolTypes.join('/') : null,
          row.mcpToolNames.length > 0
            ? `MCP ${row.mcpToolNames.join('/')}`
            : null,
          row.agentNames.length > 0 ? row.agentNames.join('/') : null,
          row.errorCount > 0 ? `$(error) ${row.errorCount}` : null,
        ]);
      }
    }

    if (details && details.agentRuns.length > 0) {
      const totalTurns = details.agentRuns.reduce(
        (total, row) => total + (row.turnCount ?? 0),
        0,
      );
      appendSection(
        '$(symbol-class)',
        tr('Agent 运行', 'Agent runs'),
        `${numberFormatter.format(details.agentRuns.length)} ${tr('次', 'runs')} · ` +
          `${numberFormatter.format(totalTurns)} ${tr('轮', 'turns')}`,
      );
      for (const row of details.agentRuns.slice(0, 3)) {
        appendBullet(row.agentText, [
          row.agentTypeText,
          row.modelText,
          `${tr('轮次', 'Turns')} ${row.turnText}`,
          `${tr('输入', 'In')} ${row.inputText}`,
          `${tr('缓存', 'Cache')} ${row.cacheText}`,
          `${tr('输出', 'Out')} ${row.outputText}`,
          row.durationText,
          row.isError ? `$(error) ${row.errorText}` : null,
        ]);
      }
    }

    if (details && details.hooks.length > 0) {
      const totalHooks = details.hooks.reduce(
        (total, row) => total + row.count,
        0,
      );
      appendSection(
        '$(symbol-event)',
        tr('Hook 执行', 'Hook executions'),
        `${numberFormatter.format(totalHooks)} ${tr('次', 'calls')} · ` +
          `${numberFormatter.format(details.hooks.length)} ${tr('组', 'groups')}`,
      );
      for (const row of details.hooks.slice(0, 5)) {
        appendBullet(row.hookType, [
          row.decisionText,
          row.resultKindText,
          `${numberFormatter.format(row.count)} ${tr('次', 'calls')}`,
          `${tr('平均', 'Avg')} ${row.avgDurationText}`,
          row.errorCount > 0 ? `$(error) ${row.errorCount}` : null,
        ]);
      }
    }

    if (details) {
      const otherEvents = details.events.filter(
        (row) =>
          row.operation !== 'chat' && row.operation !== 'execute_tool',
      );
      if (otherEvents.length > 0) {
        const totalEvents = otherEvents.reduce(
          (total, row) => total + row.count,
          0,
        );
        appendSection(
          '$(pulse)',
          tr('代理/其他事件', 'Agent & other events'),
          `${numberFormatter.format(totalEvents)} ${tr('次', 'calls')}`,
        );
        for (const row of otherEvents) {
          appendBullet(operationName(row.operation), [
            `${numberFormatter.format(row.count)} ${tr('次', 'calls')}`,
            `${tr('平均', 'Avg')} ${formatDuration(row.avgDurationMs)}`,
            row.errorCount > 0 ? `$(error) ${row.errorCount}` : null,
          ]);
        }
      }
    }

    if (models.length > 1) {
      appendSection('$(sum)', tr('合计', 'Total'));
      tooltip.appendMarkdown(
        `${summaryMarkdown(combineModels(models), this.money)}
`,
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
    const events = (data.events ?? []).filter(
      (row) => row.lastSeenMs >= fromMs && row.lastSeenMs < toMs,
    );
    const tools = (data.tools ?? []).filter(
      (row) => row.lastSeenMs >= fromMs && row.lastSeenMs < toMs,
    );
    const agentRuns = (data.agentRuns ?? []).filter(
      (row) => row.startTimeMs >= fromMs && row.startTimeMs < toMs,
    );
    const hooks = (data.hooks ?? []).filter(
      (row) => row.lastSeenMs >= fromMs && row.lastSeenMs < toMs,
    );
    if (
      breakdown.length === 0 &&
      events.length === 0 &&
      tools.length === 0 &&
      agentRuns.length === 0 &&
      hooks.length === 0
    ) {
      return;
    }

    const details: SessionDetails = {
      models: breakdown,
      events,
      tools,
      agentRuns,
      hooks,
      signalHealth: data.signalHealth ?? {
        agentRuns: 0,
        hookEvents: 0,
        metricPoints: 0,
        lastSignalText: '-',
      },
    };
    this.breakdownCache.set(key, details);
    if (this.breakdownCache.size > 24) {
      const oldest = this.breakdownCache.keys().next().value;
      if (oldest !== undefined && oldest !== key) {
        this.breakdownCache.delete(oldest);
      }
    }

    if (this.currentSession && this.breakdownKey(this.currentSession) === key) {
      this.updateTooltip(this.currentSession, details);
      this.renderStatusText(this.currentSession);
    }
  }

  private touchCache(key: string, value: SessionDetails): void {
    this.breakdownCache.delete(key);
    this.breakdownCache.set(key, value);
  }

  private breakdownKey(session: SessionSummary): string {
    return [
      session.sessionId,
      session.lastSeenMs,
      this.rangeIndex,
      session.calls,
      session.inputTokens,
      session.cacheReadTokens,
      session.outputTokens,
      session.reasoningTokens,
      session.costCny,
      session.unpricedCalls,
      session.avgTtftMs,
      session.tokensPerSecond,
      session.events,
    ].join('|');
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
  interface ModelAccumulator extends ModelBreakdown {
    ttftSum: number;
  }

  const models = new Map<string, ModelAccumulator>();

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
        ttftMs: null,
        tokensPerSecond: null,
        firstSeenMs: call.startTimeMs,
        lastSeenMs: call.startTimeMs,
        totalDurationMs: 0,
        durationSamples: 0,
        ttftSamples: 0,
        generationOutput: 0,
        generationMs: 0,
        ttftSum: 0,
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

    row.firstSeenMs = Math.min(row.firstSeenMs, call.startTimeMs);
    row.lastSeenMs = Math.max(
      row.lastSeenMs,
      call.startTimeMs + (call.durationMs ?? 0),
    );
    if (call.durationMs !== null && call.durationMs > 0) {
      row.totalDurationMs += call.durationMs;
      row.durationSamples += 1;
    }

    if (call.ttftMs !== null && call.ttftMs > 0) {
      row.ttftSum += call.ttftMs;
      row.ttftSamples += 1;
    }

    if (
      call.outputTokens >= 5 &&
      call.durationMs !== null &&
      call.ttftMs !== null
    ) {
      const generationMs = call.durationMs - call.ttftMs;
      if (generationMs >= 50) {
        row.generationOutput += call.outputTokens;
        row.generationMs += generationMs;
      }
    }
  }

  return [...models.values()]
    .map((row): ModelBreakdown => {
      const { ttftSum, ...model } = row;
      return {
        ...model,
        ttftMs: row.ttftSamples > 0 ? ttftSum / row.ttftSamples : null,
        tokensPerSecond:
          row.generationMs > 0
            ? (row.generationOutput * 1000) / row.generationMs
            : null,
      };
    })
    .sort(
      (a, b) =>
        b.costCny - a.costCny ||
        b.inputTokens - a.inputTokens ||
        a.model.localeCompare(b.model),
    );
}

function modelTotals(row: ModelBreakdown): UsageTotals {
  return {
    calls: row.calls,
    inputTokens: row.inputTokens,
    cacheReadTokens: row.cacheReadTokens,
    costCny: row.costCny,
    unpricedCalls: row.unpricedCalls,
    totalDurationMs: row.totalDurationMs,
    durationSamples: row.durationSamples,
    ttftMs: row.ttftMs,
    tokensPerSecond: row.tokensPerSecond,
  };
}

function sessionTotals(session: SessionSummary): UsageTotals {
  return {
    calls: session.calls,
    inputTokens: session.inputTokens,
    cacheReadTokens: session.cacheReadTokens,
    costCny: session.costCny,
    unpricedCalls: session.unpricedCalls,
    totalDurationMs:
      session.avgDurationMs !== null ? session.avgDurationMs * session.calls : 0,
    durationSamples: session.avgDurationMs !== null ? session.calls : 0,
    ttftMs: session.avgTtftMs,
    tokensPerSecond: session.tokensPerSecond,
  };
}

function combineModels(rows: ModelBreakdown[]): UsageTotals {
  let calls = 0;
  let inputTokens = 0;
  let cacheReadTokens = 0;
  let costCny = 0;
  let unpricedCalls = 0;
  let totalDurationMs = 0;
  let durationSamples = 0;
  let ttftWeighted = 0;
  let ttftSamples = 0;
  let generationOutput = 0;
  let generationMs = 0;

  for (const row of rows) {
    calls += row.calls;
    inputTokens += row.inputTokens;
    cacheReadTokens += row.cacheReadTokens;
    costCny += row.costCny;
    unpricedCalls += row.unpricedCalls;
    totalDurationMs += row.totalDurationMs;
    durationSamples += row.durationSamples;
    generationOutput += row.generationOutput;
    generationMs += row.generationMs;
    if (row.ttftMs !== null && row.ttftSamples > 0) {
      ttftWeighted += row.ttftMs * row.ttftSamples;
      ttftSamples += row.ttftSamples;
    }
  }

  return {
    calls,
    inputTokens,
    cacheReadTokens,
    costCny,
    unpricedCalls,
    totalDurationMs,
    durationSamples,
    ttftMs: ttftSamples > 0 ? ttftWeighted / ttftSamples : null,
    tokensPerSecond:
      generationMs > 0 ? (generationOutput * 1000) / generationMs : null,
  };
}

function summaryMarkdown(totals: UsageTotals, money: MoneySettings): string {
  const hitRate = totals.inputTokens > 0
    ? totals.cacheReadTokens / totals.inputTokens
    : null;
  const avgDuration = totals.durationSamples > 0
    ? totals.totalDurationMs / totals.durationSamples
    : null;
  return [
    `- **${tr('总计', 'Totals')}** ${numberFormatter.format(totals.calls)} ` +
      `${tr('次', 'calls')} · ${tr('总用时', 'Total time')} ` +
      `${formatDuration(totals.durationSamples > 0 ? totals.totalDurationMs : null)} · ` +
      `${tr('平均', 'Avg')} ${formatDuration(avgDuration)}`,
    `- **${tr('命中率', 'Hit rate')}** ${formatRate(hitRate)}`,
    `- **${tr('性能', 'Performance')}** ` +
      `TTFT ${formatDuration(totals.ttftMs)} · ` +
      `${tr('速度', 'Speed')} ${formatSpeed(totals.tokensPerSecond)}`,
    `- **${tr('费用', 'Cost')}** ${formatCost(totals.costCny, totals.unpricedCalls, money)}`,
  ].join('\n');
}

function modelTableFor(row: ModelBreakdown, money: MoneySettings): string {
  return modelTable(
    row.inputTokens,
    row.cacheReadTokens,
    row.outputTokens,
    row.reasoningTokens,
    row.uncachedCostCny,
    row.cacheCostCny,
    row.outputCostCny,
    row.reasoningCostCny,
    money,
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
  const widths = [0, 1, 2].map((index) =>
    rows.reduce(
      (max, row) => Math.max(max, displayWidth(row[index])),
      0,
    ),
  );
  return rows
    .map((cells) =>
      cells
        .map((cell, index) =>
          index === 0
            ? padDisplayEnd(cell, widths[index])
            : padDisplayStart(cell, widths[index]),
        )
        .join('  '),
    )
    .join('\n');
}

function displayWidth(value: string): number {
  let width = 0;
  for (const character of value) {
    width += isWideCharacter(character.codePointAt(0) ?? 0) ? 2 : 1;
  }
  return width;
}

function isWideCharacter(code: number): boolean {
  return (
    (code >= 0x1100 && code <= 0x115f) ||
    (code >= 0x2e80 && code <= 0xa4cf && code !== 0x303f) ||
    (code >= 0xac00 && code <= 0xd7a3) ||
    (code >= 0xf900 && code <= 0xfaff) ||
    (code >= 0xfe10 && code <= 0xfe19) ||
    (code >= 0xfe30 && code <= 0xfe6f) ||
    (code >= 0xff00 && code <= 0xff60) ||
    (code >= 0xffe0 && code <= 0xffe6) ||
    (code >= 0x1f300 && code <= 0x1faff)
  );
}

function padDisplayEnd(value: string, width: number): string {
  return value + ' '.repeat(Math.max(0, width - displayWidth(value)));
}

function padDisplayStart(value: string, width: number): string {
  return ' '.repeat(Math.max(0, width - displayWidth(value))) + value;
}

function formatRate(rate: number | null): string {
  return rate === null ? '-' : `${(rate * 100).toFixed(2)}%`;
}

function formatDuration(milliseconds: number | null | undefined): string {
  if (
    milliseconds === null ||
    milliseconds === undefined ||
    !Number.isFinite(milliseconds)
  ) {
    return '-';
  }

  return milliseconds < 1000
    ? `${Math.round(milliseconds)} ms`
    : `${(milliseconds / 1000).toFixed(2)} s`;
}

function formatSpeed(tokensPerSecond: number | null | undefined): string {
  if (
    tokensPerSecond === null ||
    tokensPerSecond === undefined ||
    !Number.isFinite(tokensPerSecond)
  ) {
    return '-';
  }

  return `${tokensPerSecond.toFixed(1)} tok/s`;
}

function normalizeStatusBarFormat(value: string | null | undefined): string {
  const trimmed = (value ?? '').replace(/[\r\n\t]+/g, ' ').trim();
  return trimmed || DEFAULT_STATUS_BAR_FORMAT;
}

function renderStatusTemplate(
  template: string,
  session: SessionSummary,
  rangeLabel: string,
  money: MoneySettings,
  details: SessionDetails | undefined,
): string {
  const rendered = template.replace(
    /\{([a-zA-Z][\w.-]*)(?::([^}]*))?\}/g,
    (_match, name: string, spec: string | undefined) =>
      resolveStatusValue(
        name,
        spec,
        session,
        rangeLabel,
        money,
        details,
      ) ?? '',
  );
  return cleanStatusText(rendered) || '$(pulse) -';
}

function resolveStatusValue(
  rawName: string,
  spec: string | undefined,
  session: SessionSummary,
  rangeLabel: string,
  money: MoneySettings,
  details: SessionDetails | undefined,
): string | null {
  switch (normalizePlaceholder(rawName)) {
    case 'hit':
      return formatPercentMetric(session.hitRate, spec);
    case 'ttft':
      return formatDurationMetric(session.avgTtftMs, spec);
    case 'speed':
      return formatSpeedMetric(session.tokensPerSecond, spec);
    case 'cost':
      return sessionHasCost(session)
        ? formatCostMetric(session.costCny, spec, money)
        : null;
    case 'calls':
      return formatCountMetric(session.calls, spec);
    case 'duration':
      return formatDurationMetric(session.avgDurationMs, spec);
    case 'input':
      return formatCountMetric(session.inputTokens, spec);
    case 'cache':
      return formatCountMetric(session.cacheReadTokens, spec);
    case 'output':
      return formatCountMetric(session.outputTokens, spec);
    case 'reasoning':
      return formatCountMetric(session.reasoningTokens, spec);
    case 'events':
      return formatCountMetric(session.events, spec);
    case 'tools':
      return details
        ? formatCountMetric(
            details.tools.reduce((total, row) => total + row.calls, 0),
            spec,
          )
        : null;
    case 'toolerrors':
      return details
        ? formatCountMetric(
            details.tools.reduce(
              (total, row) => total + row.errorCount,
              0,
            ),
            spec,
          )
        : null;
    case 'agents':
      return details
        ? formatCountMetric(details.agentRuns.length, spec)
        : null;
    case 'turns':
      return details
        ? formatCountMetric(
            details.agentRuns.reduce(
              (total, row) => total + (row.turnCount ?? 0),
              0,
            ),
            spec,
          )
        : null;
    case 'hooks':
      return details
        ? formatCountMetric(
            details.hooks.reduce((total, row) => total + row.count, 0),
            spec,
          )
        : null;
    case 'range':
      return rangeLabel;
    case 'model':
      return session.model || null;
    case 'session':
      return session.sessionDisplay;
    case 'icon':
      return '$(pulse)';
    default:
      return `{${rawName}${spec === undefined ? '' : `:${spec}`}}`;
  }
}

function normalizePlaceholder(name: string): string {
  const key = name.trim().toLowerCase().replace(/[_\-\s]+/g, '');
  switch (key) {
    case 'hit':
    case 'hitrate':
    case 'cachehit':
    case 'cachehitrate':
      return 'hit';
    case 'tf':
    case 'ttft':
    case 'firsttoken':
    case 'firstresponsetime':
      return 'ttft';
    case 'tps':
    case 'speed':
    case 'tokenspersecond':
      return 'speed';
    case 'cost':
    case 'fee':
    case 'price':
      return 'cost';
    case 'calls':
    case 'call':
    case 'count':
    case 'n':
      return 'calls';
    case 'time':
    case 'duration':
    case 'dur':
      return 'duration';
    case 'input':
    case 'in':
    case 'prompt':
      return 'input';
    case 'cache':
    case 'cached':
    case 'cacheread':
      return 'cache';
    case 'output':
    case 'out':
    case 'completion':
      return 'output';
    case 'reasoning':
    case 'think':
      return 'reasoning';
    case 'events':
    case 'event':
      return 'events';
    case 'tools':
    case 'toolcalls':
      return 'tools';
    case 'toolerrors':
    case 'toolfailures':
      return 'toolerrors';
    case 'agents':
    case 'agentruns':
      return 'agents';
    case 'turns':
    case 'turncount':
      return 'turns';
    case 'hooks':
    case 'hookcalls':
      return 'hooks';
    case 'range':
    case 'period':
      return 'range';
    case 'model':
      return 'model';
    case 'session':
    case 'title':
      return 'session';
    case 'icon':
      return 'icon';
    default:
      return key;
  }
}

interface NumericFormat {
  digits: number | null;
  unit: 'ms' | 's' | null;
}

function parseNumericFormat(spec: string | undefined): NumericFormat {
  if (!spec) {
    return { digits: null, unit: null };
  }

  const trimmed = spec.trim();
  if (/^\d+$/.test(trimmed)) {
    return { digits: clampDigits(Number(trimmed)), unit: null };
  }

  const unit: 'ms' | 's' | null = /ms$/i.test(trimmed)
    ? 'ms'
    : /s$/i.test(trimmed)
      ? 's'
      : null;
  const numeric = trimmed
    .replace(/%/g, '')
    .replace(/ms$/i, '')
    .replace(/s$/i, '');
  const match = numeric.match(/0(?:\.(0+))?/);
  return {
    digits: match ? clampDigits(match[1]?.length ?? 0) : null,
    unit,
  };
}

function clampDigits(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Math.min(20, Math.max(0, Math.trunc(value)));
}

function formatPercentMetric(
  value: number | null | undefined,
  spec: string | undefined,
): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '-';
  }
  const digits = parseNumericFormat(spec).digits ?? 2;
  return `${(value * 100).toFixed(digits)}%`;
}

function formatDurationMetric(
  value: number | null | undefined,
  spec: string | undefined,
): string | null {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return null;
  }
  const format = parseNumericFormat(spec);
  if (format.unit === 'ms') {
    return `${formatMetricNumber(value, format.digits ?? 0)} ms`;
  }
  if (format.unit === 's') {
    return `${formatMetricNumber(value / 1000, format.digits ?? 2)} s`;
  }
  if (value < 1000) {
    return `${formatMetricNumber(value, format.digits ?? 0)} ms`;
  }
  return `${formatMetricNumber(value / 1000, format.digits ?? 2)} s`;
}

function formatSpeedMetric(
  value: number | null | undefined,
  spec: string | undefined,
): string | null {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return null;
  }
  const digits = parseNumericFormat(spec).digits ?? 1;
  return `${formatMetricNumber(value, digits)} tok/s`;
}

function formatCostMetric(
  value: number,
  spec: string | undefined,
  money: MoneySettings,
): string {
  const digits = parseNumericFormat(spec).digits ?? 2;
  return formatMoney(value, money, digits);
}

function formatCountMetric(
  value: number,
  spec: string | undefined,
): string {
  const digits = parseNumericFormat(spec).digits ?? 0;
  return formatMetricNumber(value, digits);
}

function formatMetricNumber(value: number, digits: number): string {
  const clamped = clampDigits(digits);
  return new Intl.NumberFormat('zh-CN', {
    minimumFractionDigits: clamped,
    maximumFractionDigits: clamped,
  }).format(value);
}

function cleanStatusText(value: string): string {
  let result = value
    .replace(/[\r\n\t]+/g, ' ')
    .replace(/\s{2,}/g, ' ')
    .trim();
  result = result.replace(/(?:\s*([·,，|])\s*){2,}/g, ' $1 ');
  result = result.replace(/\s*·\s*/g, ' · ');
  result = result.replace(/\s*，\s*/g, '，');
  result = result.replace(/\s*,\s*/g, ', ');
  result = result.replace(/\s*\|\s*/g, ' | ');
  result = result.replace(/^[\s·,，|]+|[\s·,，|]+$/g, '');
  return result.trim();
}

function formatShortTime(unixMs: number): string {
  if (!unixMs) {
    return '-';
  }

  const date = new Date(unixMs);
  const now = new Date();
  const pad = (value: number) => String(value).padStart(2, '0');
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate()
    ? time
    : `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${time}`;
}

function formatLocalTime(unixMs: number): string {
  if (!unixMs) {
    return '-';
  }

  const date = new Date(unixMs);
  const pad = (value: number) => String(value).padStart(2, '0');
  return (
    `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

function formatTimeRange(firstSeenMs: number, lastSeenMs: number): string {
  if (!firstSeenMs || !lastSeenMs) {
    return '-';
  }

  return firstSeenMs < lastSeenMs
    ? `${formatLocalTime(firstSeenMs)} ~ ${formatLocalTime(lastSeenMs)}`
    : formatLocalTime(lastSeenMs);
}

function operationName(operation: string): string {
  switch (operation) {
    case 'chat':
      return tr('模型调用', 'Model call');
    case 'execute_tool':
      return tr('工具调用', 'Tool call');
    case 'invoke_agent':
      return tr('代理调用', 'Agent invocation');
    case 'create_agent':
      return tr('创建代理', 'Create agent');
    case 'execute_hook':
      return tr('钩子执行', 'Hook');
    default:
      return operation || '-';
  }
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
