import dayjs from 'dayjs';
import { t, type MessageKey } from '../i18n';

export function operationLabel(operation: string): string {
  switch (operation) {
    case 'chat':
      return t('model.call');
    case 'execute_tool':
      return t('tool.call');
    case 'invoke_agent':
      return t('agent.invocation');
    case 'create_agent':
      return t('create.agent');
    case 'execute_hook':
      return t('hook.execution');
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

const SYSTEM_PATH_LABELS: Record<string, MessageKey> = {
  'session-state': 'session.state.directory',
  'agent-host-db': 'agent.host.title.store',
  'chat-title-index': 'chat.title.index',
  'workspace-title-index': 'workspace.title.index.directory',
  'copilot-trace-db': 'copilot.chat.local.trace.store',
  'ahp-endpoint-dir': 'ahp.local.endpoint.directory',
  'agent-host-trace-db': 'agent.host.local.trace.store',
};

const SYSTEM_PATH_PURPOSES: Record<string, MessageKey> = {
  'session-state': 'reads.workspace.yaml',
  'agent-host-db': 'reads.agent.host.db',
  'chat-title-index': 'reads.chat.title.index',
  'workspace-title-index': 'reads.workspace.title.index',
  'copilot-trace-db': 'read.on.otlp.import',
  'ahp-endpoint-dir': 'reads.ahp.endpoint.descriptors',
  'agent-host-trace-db': 'read.on.otlp.import',
};

const SYSTEM_MONITOR_LABELS: Record<string, MessageKey> = {
  watching: 'watched.by.extension',
  'paused-ahp': 'paused.while.ahp.connected',
  polling: 'polling.ahp.endpoints',
  'import-only': 'read.only.on.import',
  'on-query': 'read.on.query',
  unused: 'not.used',
};

const SYSTEM_ACCESS_LABELS: Record<string, MessageKey> = {
  'holiday-api': 'holiday.api',
  'otlp-endpoint': 'otlp.receiver.endpoint',
  'ahp-title-sync': 'ahp.title.sync',
};

const AHP_STATE_LABELS: Record<string, MessageKey> = {
  connected: 'connected',
  connecting: 'connecting',
  searching: 'searching.endpoints',
  disabled: 'unavailable',
};

function localize(table: Record<string, MessageKey>, key: string): string {
  const messageKey = table[key];
  return messageKey ? t(messageKey) : key;
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
