export interface Summary {
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  sessions: number;
  costCny: number;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
  unpricedCalls: number;
  hitRate: number | null;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  reasoningCostText: string;
  reasoningTokensText: string;
}

export interface ModelRow {
  model: string;
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  hitRate: number | null;
  costCny: number;
  unpricedCalls: number;
  callsText: string;
  tokensText: string;
  hitRateText: string;
  costText: string;
  hitRatePercent: number;
}

export interface SessionRow {
  sessionId: string;
  model: string;
  title: string | null;
  calls: number;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  hitRate: number | null;
  costCny: number;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  reasoningCostCny: number;
  unpricedCalls: number;
  lastSeenMs: number;
  sessionShort: string;
  sessionDisplay: string;
  callsText: string;
  inputText: string;
  cacheText: string;
  outputText: string;
  reasoningText: string;
  hitRateText: string;
  costText: string;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  reasoningCostText: string;
  lastSeenText: string;
}

export interface CallRow {
  startTimeMs: number;
  model: string;
  sessionId: string | null;
  title: string | null;
  isMain: boolean;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  hitRate: number | null;
  costCny: number | null;
  cacheCostCny: number;
  uncachedCostCny: number;
  outputCostCny: number;
  pricingPeriod: string | null;
  timeText: string;
  sessionShort: string;
  sessionDisplay: string;
  inputText: string;
  cacheText: string;
  outputText: string;
  hitRateText: string;
  costText: string;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  periodText: string;
  kindText: string;
}

export interface ReceiverInfo {
  endpoint: string;
  running: boolean;
  batches: number;
  spans: number;
  lastError: string | null;
}

export interface DashboardPayload {
  summary: Summary;
  models: ModelRow[];
  sessions: SessionRow[];
  calls: CallRow[];
  receiver: ReceiverInfo;
  updatedAt: string;
  retentionDays: number;
}

export interface SettingsPayload {
  retentionDays: number;
}

export interface ModelPayload {
  summary: Summary;
  sessions: SessionRow[];
  calls: CallRow[];
  model: string;
}

export interface SessionPayload {
  summary: Summary;
  calls: CallRow[];
  sessionId: string;
  title: string | null;
}

export interface Subscription {
  view: 'dashboard' | 'model' | 'session';
  id?: string;
  range: number;
  utility: boolean;
  fromMs?: number;
  toMs?: number;
}

export interface ModelPriceRow {
  model: string;
  customized: boolean;
  hasDefault: boolean;
  peakAware: boolean;
  cachedInput: number;
  uncachedInput: number;
  output: number;
  peakCachedInput: number | null;
  peakUncachedInput: number | null;
  peakOutput: number | null;
}
