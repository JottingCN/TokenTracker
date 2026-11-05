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

  // Labels need the locale-specific separator so English does not inherit the
  // fullwidth Chinese colon.
  const sep = t('label.separator');

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
            {t('edit.acceptance')}
            {sep}
            {outcomes.editAcceptRateText} · {outcomes.editAccepted}{' '}
            {t('accepted')} / {outcomes.editRejected} {t('rejected')}
          </Typography.Text>
        )}
        {outcomes.chatEditAccepted +
          outcomes.chatEditRejected +
          outcomes.chatEditSaved >
          0 && (
          <Typography.Text>
            {t('chat.edit.outcome')}
            {sep}
            {outcomes.chatEditAccepted} {t('accepted')} ·{' '}
            {outcomes.chatEditRejected} {t('rejected')} ·{' '}
            {outcomes.chatEditSaved} {t('saved')}
          </Typography.Text>
        )}
        {outcomes.summarizationApplied + outcomes.summarizationFailed > 0 && (
          <Typography.Text>
            {t('summarization')}
            {sep}
            {outcomes.summarizationApplied} {t('applied')} /{' '}
            {outcomes.summarizationFailed} {t('failed')}
          </Typography.Text>
        )}
        {outcomes.linesAdded + outcomes.linesRemoved > 0 && (
          <Typography.Text>
            {t('lines')}
            {sep}+{outcomes.linesAdded} / -{outcomes.linesRemoved}
          </Typography.Text>
        )}
        {outcomes.survivalFourGramText !== '-' && (
          <Typography.Text>
            4-gram {t('survival')}
            {sep}
            {outcomes.survivalFourGramText}
          </Typography.Text>
        )}
        {outcomes.survivalNoRevertText !== '-' && (
          <Typography.Text>
            {t('no.revert.survival')}
            {sep}
            {outcomes.survivalNoRevertText}
          </Typography.Text>
        )}
        {outcomes.feedbackPositive + outcomes.feedbackNegative > 0 && (
          <Typography.Text>
            {t('feedback')}
            {sep}+{outcomes.feedbackPositive} / -{outcomes.feedbackNegative}
          </Typography.Text>
        )}
        {outcomes.userCopy +
          outcomes.userInsert +
          outcomes.userApply +
          outcomes.userFollowup >
          0 && (
          <Typography.Text>
            {t('user.actions')}
            {sep}copy {outcomes.userCopy} · insert {outcomes.userInsert} · apply{' '}
            {outcomes.userApply} · followup {outcomes.userFollowup}
          </Typography.Text>
        )}
        {outcomes.pullRequests + outcomes.cloudSessions > 0 && (
          <Typography.Text>
            {t('pr.cloud')}
            {sep}
            {outcomes.pullRequests} / {outcomes.cloudSessions}
          </Typography.Text>
        )}
        {outcomes.cloudOperations + outcomes.cloudErrors > 0 && (
          <Typography.Text>
            {t('cloud.operations')}
            {sep}
            {outcomes.cloudOperations}
            {outcomes.cloudErrors > 0
              ? ` · ${t('failed')} ${outcomes.cloudErrors}`
              : ''}
            {outcomes.cloudOperationAvgMs !== null
              ? ` · ${formatLatency(outcomes.cloudOperationAvgMs)}`
              : ''}
          </Typography.Text>
        )}
        {outcomes.agentEditSuccess + outcomes.agentEditError > 0 && (
          <Typography.Text>
            {t('agent.edits')}
            {sep}
            {outcomes.agentEditSuccess} / {outcomes.agentEditError}
          </Typography.Text>
        )}
        {outcomes.toolMetricCalls > 0 && (
          <Typography.Text>
            {t('tool.metrics')}
            {sep}
            {outcomes.toolMetricCalls} · {outcomes.toolMetricAvgText}
          </Typography.Text>
        )}
      </Space>
    </Card>
  );
}
