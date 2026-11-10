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
  avgTtftMs: number | null;
  ttftSamples: number;
  tokensPerSecond: number | null;
  minTtftMs: number | null;
  maxTtftMs: number | null;
  minTokensPerSecond: number | null;
  maxTokensPerSecond: number | null;
  hitRate: number | null;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  reasoningCostText: string;
  reasoningTokensText: string;
  avgTtftText: string;
  tokensPerSecondText: string;
}

export interface ModelRow {
  model: string;
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
  avgTtftMs: number | null;
  ttftSamples: number;
  tokensPerSecond: number | null;
  callsText: string;
  tokensText: string;
  inputText: string;
  cacheText: string;
  outputText: string;
  reasoningText: string;
  hitRateText: string;
  costText: string;
  hitRatePercent: number;
  avgTtftText: string;
  tokensPerSecondText: string;
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
  firstSeenMs: number;
  events: number;
  avgDurationMs: number | null;
  avgTtftMs: number | null;
  ttftSamples: number;
  tokensPerSecond: number | null;
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
  timeRangeText: string;
  avgDurationText: string;
  avgTtftText: string;
  tokensPerSecondText: string;
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
  nanoAiu: number | null;
  source: string | null;
  durationMs: number | null;
  ttftMs: number | null;
  traceId: string | null;
  parentSpanId: string | null;
  errorType: string | null;
  agentName: string | null;
  subagentName: string | null;
  finishReasons: string | null;
  tokensPerSecond: number | null;
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
  aiuText: string | null;
  cacheCostText: string;
  uncachedCostText: string;
  outputCostText: string;
  periodText: string;
  kindText: string;
  sourceText: string;
  ttftText: string;
  durationText: string;
  tokensPerSecondText: string;
  errorText: string;
  agentText: string;
  finishReasonsText: string;
}


export interface SessionEventRow {
  operation: string;
  source: string | null;
  count: number;
  avgDurationMs: number | null;
  errorCount: number;
  lastSeenMs: number;
  operationText: string;
  countText: string;
  avgDurationText: string;
  lastSeenText: string;
}

export interface ToolCallRow {
  tool: string;
  source: string | null;  calls: number;
  avgDurationMs: number | null;
  medianDurationMs: number | null;
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
  callsText: string;
  toolDisplayText: string;
  avgDurationText: string;
  medianDurationText: string;
  maxDurationText: string;
  lastSeenText: string;
  toolTypeText: string;
  editTypeText: string;
  skillNameText: string;
  mcpServerHashText: string;
  mcpToolNameText: string;
  errorTypeText: string;
  agentNameText: string;
}

export interface AgentRunRow {
  traceId: string;
  spanId: string;
  parentSpanId: string | null;
  isRoot: boolean;
  sessionId: string | null;
  agentName: string | null;
  agentType: string | null;
  providerName: string | null;
  requestModel: string | null;
  responseModel: string | null;
  source: string | null;
  gitRepository: string | null;
  gitBranch: string | null;
  gitCommit: string | null;
  githubOrg: string | null;
  turnCount: number | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  reasoningTokens: number;
  costCny: number | null;
  startTimeMs: number;
  endTimeMs: number | null;
  isError: boolean;
  errorType: string | null;
  isRunning: boolean;
  agentText: string;
  agentTypeText: string;
  providerText: string;
  modelText: string;
  sourceText: string;
  repoText: string;
  branchText: string;
  turnText: string;
  inputText: string;
  cacheText: string;
  outputText: string;
  reasoningText: string;
  timeText: string;
  durationText: string;
  hitRateText: string;
  costText: string;
  errorText: string;
}

export interface HookSummaryRow {
  hookType: string | null;
  decision: string | null;
  resultKind: string | null;
  source: string | null;
  count: number;
  avgDurationMs: number | null;
  errorCount: number;
  toolNames: string[];
  lastSeenMs: number;
  hookTypeText: string;
  decisionText: string;
  resultKindText: string;
  sourceText: string;
  countText: string;
  avgDurationText: string;
  toolNamesText: string;
  lastSeenText: string;
}

export interface SignalHealth {
  agentRuns: number;
  hookEvents: number;
  metricPoints: number;
  lastSignalMs: number;
  lastSignalText: string;
}

export interface OutcomeSummary {
  editAccepted: number;
  editRejected: number;
  chatEditAccepted: number;
  chatEditRejected: number;
  chatEditSaved: number;
  linesAdded: number;
  linesRemoved: number;
  survivalFourGram: number | null;
  survivalNoRevert: number | null;
  userCopy: number;
  userInsert: number;
  userApply: number;
  userFollowup: number;
  feedbackPositive: number;
  feedbackNegative: number;
  agentEditSuccess: number;
  agentEditError: number;
  summarizationApplied: number;
  summarizationFailed: number;
  pullRequests: number;
  cloudSessions: number;
  cloudOperations: number;
  cloudErrors: number;
  cloudOperationAvgMs: number | null;
  toolMetricCalls: number;
  toolMetricAvgMs: number | null;
  editAcceptRate: number | null;
  hasData: boolean;
  editAcceptRateText: string;
  survivalFourGramText: string;
  survivalNoRevertText: string;
  toolMetricAvgText: string;
}

export interface TraceNode {
  id: string;
  parentId: string | null;
  kind: string;
  operation: string;
  name: string | null;
  model: string | null;
  tool: string | null;
  source: string | null;
  startTimeMs: number;
  durationMs: number | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  isError: boolean;
  errorType: string | null;
  depth: number;
  isContext: boolean;
  label: string;
  sourceText: string;
  durationText: string;
}

export interface TracePayload {
  nodes: TraceNode[];
  hasMore: boolean;
  nextCursor: string | null;
  total: number;
}

export interface ReceiverInfo {
  endpoint: string;
  running: boolean;
  batches: number;
  spans: number;
  lastError: string | null;
  metricBatches: number;
  metricPoints: number;
  metricRejectedBatches: number;
  metricFilteredNames: string | null;
}

export interface AhpSessionInfo {
  resource: string;
  provider: string;
  title: string;
  status: string;
  activity: string | null;
  workspacePath: string | null;
  modifiedAt: string;
  chats: number;
}

export interface AhpStatus {
  state: 'disabled' | 'searching' | 'connecting' | 'connected' | string;
  connected: boolean;
  sessions: number;
  items: AhpSessionInfo[];
}

export interface DashboardPayload {
  summary: Summary;
  models: ModelRow[];
  sessions: SessionRow[];
  receiver: ReceiverInfo;
  updatedAt: string;
  retentionDays: number;
  signalHealth: SignalHealth;
  outcomes: OutcomeSummary;
  ahp?: AhpStatus;
}

export interface SettingsPayload {
  retentionDays: number;
}

export interface ModelPayload {
  summary: Summary;
  sessions: SessionRow[];
  model: string;
}

export interface SessionModelPayload {
  model: string;
  summary: Summary;
}

export interface SessionPayload {
  summary: Summary;
  models: SessionModelPayload[];
  calls: CallRow[];
  events: SessionEventRow[];
  tools: ToolCallRow[];
  agentRuns: AgentRunRow[];
  hooks: HookSummaryRow[];
  signalHealth: SignalHealth;
  outcomes: OutcomeSummary;
  sessionId: string;
  title: string | null;
  workspacePath: string | null;
}

export interface CallsPayload {
  calls: CallRow[];
  total: number;
  offset: number;
  limit: number;
}

export interface Subscription {
  view: 'dashboard' | 'model' | 'session' | 'calls';
  id?: string;
  range: number;
  utility: boolean;
  fromMs?: number;
  toMs?: number;
  model?: string;
  query?: string;
  kind?: 'main' | 'utility';
  priced?: boolean;
  period?: string;
  hasError?: boolean;
  agent?: string;
  offset?: number;
  limit?: number;
  includeCalls?: boolean;
}

export interface ModelProfile {
  model: string;
  provider: string | null;
  iconKind: 'letter' | 'image' | null;
  iconLetter: string | null;
  iconColor: string | null;
  iconImage: string | null;
}

export interface ModelSettingsRow extends ModelProfile {
  priced: boolean;
  hasProfile: boolean;
  calls: number;
  peakAware: boolean;
  cachedInput: number;
  uncachedInput: number;
  output: number;
  peakCachedInput: number | null;
  peakUncachedInput: number | null;
  peakOutput: number | null;
  longContextThreshold: number | null;
  longCachedInput: number | null;
  longUncachedInput: number | null;
  longOutput: number | null;
  longPeakCachedInput: number | null;
  longPeakUncachedInput: number | null;
  longPeakOutput: number | null;
  cacheWrite: number | null;
  peakCacheWrite: number | null;
  longCacheWrite: number | null;
  longPeakCacheWrite: number | null;
}

export interface StorageTableInfo {
  name: string;
  rows: number;
}

export interface StorageStats {
  databasePath: string;
  sizeBytes: number;
  walBytes: number;
  shmBytes: number;
  pageSize: number;
  pageCount: number;
  journalMode: string;
  rawSpans: number;
  combinedSpans: number;
  outcomeMetricPoints: number;
  tables: StorageTableInfo[];
  schemaVersion: number;
  supportedSchemaVersion: number;
  schemaFromNewerBuild: boolean;
}

export interface SystemInfo {
  server: {
    name: string;
    version: string;
    protocol: number;
    port: number;
    startedAt: string;
    buildId: string | null;
    features: string[];
  };
  storage: StorageStats;
  receiver: {
    endpoint: string;
    running: boolean;
    batches: number;
    spans: number;
    lastError: string | null;
    metricBatches: number;
    metricPoints: number;
    metricRejectedBatches: number;
    metricFilteredNames?: string | null;
  };
  retentionDays: number;
  dataDirectory: string;
  lastReceivedText: string;
  ahp: {
    state: string;
    connected: boolean;
    sessions: number;
  };
  watchedPaths: SystemPathInfo[];
  accesses: SystemAccessInfo[];
}

export interface SystemPathInfo {
  key: string;
  path: string;
  kind: string;
  exists: boolean;
  lastModifiedText: string | null;
  monitor: string;
}

export interface SystemAccessInfo {
  key: string;
  target: string;
  kind: string;
}
