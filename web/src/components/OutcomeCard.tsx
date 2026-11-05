import { Card, Space, Typography } from 'antd';
import type { OutcomeSummary } from '../types';
import { useUi } from '../ui';
import { formatLatency } from '../utils/labels';

export function OutcomeCard({
  outcomes,
}: {
  outcomes: OutcomeSummary | null | undefined;
}) {
  const { t } = useUi();
  if (!outcomes?.hasData) {
    return null;
  }

  return (
    <Card
      size="small"
      title={t('edits.outcomes')}
      extra={
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {t('only.received.metrics.are.shown')}
        </Typography.Text>
      }
    >
      <Space wrap size="large">
        {outcomes.editAccepted + outcomes.editRejected > 0 && (
          <Typography.Text>
            {t('edit.acceptance')}：{outcomes.editAcceptRateText}（
            {outcomes.editAccepted}/{outcomes.editRejected}）
          </Typography.Text>
        )}
        {outcomes.linesAdded + outcomes.linesRemoved > 0 && (
          <Typography.Text>
            {t('lines')}：+{outcomes.linesAdded} / -
            {outcomes.linesRemoved}
          </Typography.Text>
        )}
        {outcomes.survivalFourGramText !== '-' && (
          <Typography.Text>
            4-gram {t('survival')}：{outcomes.survivalFourGramText}
          </Typography.Text>
        )}
        {outcomes.survivalNoRevertText !== '-' && (
          <Typography.Text>
            {t('no.revert.survival')}：
            {outcomes.survivalNoRevertText}
          </Typography.Text>
        )}
        {outcomes.feedbackPositive + outcomes.feedbackNegative > 0 && (
          <Typography.Text>
            {t('feedback')}：+{outcomes.feedbackPositive} / -
            {outcomes.feedbackNegative}
          </Typography.Text>
        )}
        {outcomes.userCopy +
          outcomes.userInsert +
          outcomes.userApply +
          outcomes.userFollowup >
          0 && (
          <Typography.Text>
            {t('user.actions')}：copy {outcomes.userCopy} · insert{' '}
            {outcomes.userInsert} · apply {outcomes.userApply} · followup{' '}
            {outcomes.userFollowup}
          </Typography.Text>
        )}
        {outcomes.pullRequests +
          outcomes.cloudSessions +
          outcomes.cloudPrReady >
          0 && (
          <Typography.Text>
            {t('pr.cloud')}：{outcomes.pullRequests} /{' '}
            {outcomes.cloudSessions}
          </Typography.Text>
        )}
        {outcomes.cloudOperations + outcomes.cloudErrors > 0 && (
          <Typography.Text>
            {t('cloud.operations')}：{outcomes.cloudOperations}
            {outcomes.cloudErrors > 0 ? ` · ${t('failed')} ${outcomes.cloudErrors}` : ''}
            {outcomes.cloudOperationAvgMs !== null
              ? ` · ${formatLatency(outcomes.cloudOperationAvgMs)}`
              : ''}
          </Typography.Text>
        )}
        {outcomes.agentEditSuccess + outcomes.agentEditError > 0 && (
          <Typography.Text>
            {t('agent.edits')}：{outcomes.agentEditSuccess} /{' '}
            {outcomes.agentEditError}
          </Typography.Text>
        )}
        {outcomes.toolMetricCalls > 0 && (
          <Typography.Text>
            {t('tool.metrics')}：{outcomes.toolMetricCalls} ·{' '}
            {outcomes.toolMetricAvgText}
          </Typography.Text>
        )}
      </Space>
    </Card>
  );
}
