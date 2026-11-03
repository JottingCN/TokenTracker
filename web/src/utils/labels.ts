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
