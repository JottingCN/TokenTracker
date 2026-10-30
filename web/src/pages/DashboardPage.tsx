import { Card, Space, Table, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import { modelColumns, sessionColumns } from '../components/columns';
import type { DashboardPayload } from '../types';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';

interface DashboardPageProps {
  data: DashboardPayload | null;
  onOpenSession: (sessionId: string) => void;
}

export function DashboardPage({
  data,
  onOpenSession,
}: DashboardPageProps) {
  const money = useMoney();
  const { tr } = useUi();
  const retentionText = data
    ? data.retentionDays === 0
      ? tr('永久保留', 'kept forever')
      : tr(
          `保留 ${data.retentionDays} 天`,
          `kept for ${data.retentionDays} days`,
        )
    : tr('保留期未知', 'retention unknown');
  const otlpHost = window.location.host;

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={data?.summary ?? null} />

      <Card size="small" title={tr('按模型', 'By model')}>
        <Table
          size="small"
          rowKey="model"
          columns={modelColumns()}
          dataSource={data?.models ?? []}
          loading={!data}
          pagination={false}
          scroll={{ x: 660, y: 360 }}
        />
      </Card>

      <Card size="small" title={tr('会话', 'Sessions')}>
        <Table
          size="small"
          rowKey="sessionId"
          columns={sessionColumns(true, onOpenSession)}
          dataSource={data?.sessions ?? []}
          loading={!data}
          pagination={{ pageSize: 10, hideOnSinglePage: true }}
          scroll={{ x: 1090, y: 420 }}
        />
      </Card>

      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {tr(
          `费用为基于 DeepSeek、Kimi、小米 MiMo、MiniMax、智谱 GLM 公开价目表的估算（${money.unitLabel}），与 GitHub Copilot 订阅账单无关；命中率下降不一定代表缓存失效。数据来自 Copilot OTel，经本机 ${otlpHost} 接收后存入本地 SQLite，${retentionText}。`,
          `Costs are estimated from public DeepSeek, Kimi, MiMo, MiniMax and GLM pricing (${money.unitLabel}) and are unrelated to GitHub Copilot billing. A lower hit rate does not necessarily mean the cache was invalidated. Data comes from Copilot OTel, is received on ${otlpHost} and stored in local SQLite, ${retentionText}.`,
        )}
      </Typography.Text>
    </Space>
  );
}
