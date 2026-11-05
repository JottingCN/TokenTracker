import { Card, Space, Typography } from 'antd';
import type { SignalHealth } from '../types';
import { useUi } from '../ui';

interface SignalCardProps {
  signalHealth: SignalHealth | null | undefined;
}

// Agent runs, hooks and metric points arrive from separate pipelines, so the
// dashboard and the session page both summarise them the same way. The card is
// hidden while every counter is zero instead of showing an empty shell.
export function SignalCard({ signalHealth }: SignalCardProps) {
  const { t } = useUi();
  const sep = t('label.separator');
  const total =
    (signalHealth?.agentRuns ?? 0) +
    (signalHealth?.hookEvents ?? 0) +
    (signalHealth?.metricPoints ?? 0);
  if (!signalHealth || total === 0) {
    return null;
  }

  return (
    <Card size="small" title={t('signals.agents')}>
      <Space wrap size="large">
        <Typography.Text>
          {t('agent.runs')}
          {sep}
          {signalHealth.agentRuns}
        </Typography.Text>
        <Typography.Text>
          {t('hook.events')}
          {sep}
          {signalHealth.hookEvents}
        </Typography.Text>
        <Typography.Text>
          {t('metric.points')}
          {sep}
          {signalHealth.metricPoints}
        </Typography.Text>
        <Typography.Text type="secondary">
          {t('last.signal')}
          {sep}
          {signalHealth.lastSignalText}
        </Typography.Text>
      </Space>
    </Card>
  );
}
