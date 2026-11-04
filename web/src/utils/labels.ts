import dayjs from 'dayjs';
import { translate } from '../ui';

export function operationLabel(operation: string): string {
  switch (operation) {
    case 'chat':
      return translate('模型调用', 'Model call');
    case 'execute_tool':
      return translate('工具调用', 'Tool call');
    case 'invoke_agent':
      return translate('代理调用', 'Agent invocation');
    case 'create_agent':
      return translate('创建代理', 'Create agent');
    case 'execute_hook':
      return translate('钩子执行', 'Hook execution');
    default:
      return operation || '-';
  }
}

export function formatLatency(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return '-';
  }

  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(2)} s`;
}

export function formatSpeed(tokensPerSecond: number | null | undefined): string {
  if (
    tokensPerSecond === null ||
    tokensPerSecond === undefined ||
    !Number.isFinite(tokensPerSecond)
  ) {
    return '-';
  }

  return `${tokensPerSecond.toFixed(1)} tok/s`;
}

export function formatTime(unixMs: number): string {
  return dayjs(unixMs).format('MM-DD HH:mm');
}

type LocalizedText = readonly [zh: string, en: string];

const SYSTEM_PATH_LABELS: Record<string, LocalizedText> = {
  'session-state': ['会话状态目录', 'Session state directory'],
  'agent-host-db': ['Agent Host 标题库', 'Agent Host title store'],
  'chat-title-index': ['Chat 标题索引', 'Chat title index'],
  'workspace-title-index': [
    '工作区标题索引目录',
    'Workspace title index directory',
  ],
  'copilot-trace-db': ['Copilot Chat 本地 Trace 库', 'Copilot Chat local trace store'],
  'ahp-endpoint-dir': ['AHP 本地端点目录', 'AHP local endpoint directory'],
  'agent-host-trace-db': ['Agent Host 本地 Trace 库', 'Agent Host local trace store'],
};

const SYSTEM_PATH_PURPOSES: Record<string, LocalizedText> = {
  'session-state': [
    '只读 workspace.yaml 的会话标题与工作目录',
    'Reads session titles and working directories from workspace.yaml',
  ],
  'agent-host-db': [
    '只读 agent-host.db 中的会话标题与工作目录',
    'Reads session titles and working directories from agent-host.db',
  ],
  'chat-title-index': [
    '只读 chat.ChatSessionStore.index 的会话标题/工作区',
    'Reads session titles and workspaces from chat.ChatSessionStore.index',
  ],
  'workspace-title-index': [
    '只读各工作区 state.vscdb 中的标题索引',
    'Reads title indexes from every workspace state.vscdb',
  ],
  'copilot-trace-db': [
    '仅在执行“导入本地 OTel 数据”时读取',
    'Read only when importing local OTel data',
  ],
  'ahp-endpoint-dir': [
    '只读端点描述文件，用于连接 AHP 同步标题',
    'Reads endpoint descriptors used to connect to AHP for titles',
  ],
  'agent-host-trace-db': [
    '仅在执行“导入本地 OTel 数据”时读取',
    'Read only when importing local OTel data',
  ],
};

const SYSTEM_MONITOR_LABELS: Record<string, LocalizedText> = {
  watching: ['扩展监控中', 'Watched by the extension'],
  'paused-ahp': ['AHP 在线，暂停监控', 'Paused while AHP is connected'],
  polling: ['AHP 端点轮询中', 'Polling AHP endpoints'],
  'import-only': ['仅导入时读取', 'Read only on import'],
  'on-query': ['查询时读取', 'Read on query'],
  unused: ['未使用', 'Not used'],
};

const SYSTEM_ACCESS_LABELS: Record<string, LocalizedText> = {
  'holiday-api': ['节假日 API', 'Holiday API'],
  'otlp-endpoint': ['OTLP 接收端点', 'OTLP receiver endpoint'],
  'ahp-title-sync': ['AHP 标题同步', 'AHP title sync'],
};

const AHP_STATE_LABELS: Record<string, LocalizedText> = {
  connected: ['已连接', 'Connected'],
  connecting: ['连接中', 'Connecting'],
  searching: ['搜索端点中', 'Searching endpoints'],
  disabled: ['不可用', 'Unavailable'],
};

function localize(table: Record<string, LocalizedText>, key: string): string {
  const text = table[key];
  return text ? translate(text[0], text[1]) : key;
}

export function systemPathLabel(key: string): string {
  return localize(SYSTEM_PATH_LABELS, key);
}

export function systemPathPurpose(key: string): string {
  return localize(SYSTEM_PATH_PURPOSES, key);
}

export function systemMonitorLabel(key: string): string {
  return localize(SYSTEM_MONITOR_LABELS, key);
}

export function systemAccessLabel(key: string): string {
  return localize(SYSTEM_ACCESS_LABELS, key);
}

export function ahpStateLabel(state: string): string {
  return localize(AHP_STATE_LABELS, state);
}
