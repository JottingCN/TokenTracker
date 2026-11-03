import { Card, Space, Typography } from 'antd';
import type { OutcomeSummary } from '../types';
import { useUi } from '../ui';

export function OutcomeCard({
  outcomes,
}: {
  outcomes: OutcomeSummary | null | undefined;
}) {
  const { tr } = useUi();
  if (!outcomes?.hasData) {
    return null;
  }

  return (
    <Card
      size="small"
      title={tr('编辑与结果指标', 'Edits & outcomes')}
      extra={
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {tr('仅显示已收到的指标', 'Only received metrics are shown')}
        </Typography.Text>
      }
    >
      <Space wrap size="large">
        {outcomes.editAccepted + outcomes.editRejected > 0 && (
          <Typography.Text>
            {tr('编辑接受率', 'Edit acceptance')}：{outcomes.editAcceptRateText}（
            {outcomes.editAccepted}/{outcomes.editRejected}）
          </Typography.Text>
        )}
        {outcomes.linesAdded + outcomes.linesRemoved > 0 && (
          <Typography.Text>
            {tr('代码行数', 'Lines')}：+{outcomes.linesAdded} / -
            {outcomes.linesRemoved}
          </Typography.Text>
        )}
        {outcomes.survivalFourGramText !== '-' && (
          <Typography.Text>
            4-gram {tr('存活率', 'survival')}：{outcomes.survivalFourGramText}
          </Typography.Text>
        )}
        {outcomes.survivalNoRevertText !== '-' && (
          <Typography.Text>
            {tr('无回退存活率', 'No-revert survival')}：
            {outcomes.survivalNoRevertText}
          </Typography.Text>
        )}
        {outcomes.feedbackPositive + outcomes.feedbackNegative > 0 && (
          <Typography.Text>
            {tr('反馈', 'Feedback')}：+{outcomes.feedbackPositive} / -
            {outcomes.feedbackNegative}
          </Typography.Text>
        )}
        {outcomes.userCopy +
          outcomes.userInsert +
          outcomes.userApply +
          outcomes.userFollowup >
          0 && (
          <Typography.Text>
            {tr('用户操作', 'User actions')}：copy {outcomes.userCopy} · insert{' '}
            {outcomes.userInsert} · apply {outcomes.userApply} · followup{' '}
            {outcomes.userFollowup}
          </Typography.Text>
        )}
        {outcomes.pullRequests +
          outcomes.cloudSessions +
          outcomes.cloudPrReady >
          0 && (
          <Typography.Text>
            {tr('PR / 云会话', 'PR / cloud')}：{outcomes.pullRequests} /{' '}
            {outcomes.cloudSessions}
          </Typography.Text>
        )}
        {outcomes.agentEditSuccess + outcomes.agentEditError > 0 && (
          <Typography.Text>
            {tr('Agent 编辑', 'Agent edits')}：{outcomes.agentEditSuccess} /{' '}
            {outcomes.agentEditError}
          </Typography.Text>
        )}
        {outcomes.toolMetricCalls > 0 && (
          <Typography.Text>
            {tr('工具指标', 'Tool metrics')}：{outcomes.toolMetricCalls} ·{' '}
            {outcomes.toolMetricAvgText}
          </Typography.Text>
        )}
      </Space>
    </Card>
  );
}
