import { Card, Descriptions, Space, Table, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import {
  callColumns,
  callRowKey,
  modelColumns,
  sessionColumns,
} from '../components/columns';
import type { DashboardPayload } from '../types';
import { formatMoney, useMoney } from '../utils/money';

const number = new Intl.NumberFormat('zh-CN');

interface DashboardPageProps {
  data: DashboardPayload | null;
  utility: boolean;
  onOpenSession: (sessionId: string) => void;
}

export function DashboardPage({
  data,
  utility,
  onOpenSession,
}: DashboardPageProps) {
  const money = useMoney();
  const utilitySummary = data?.utilitySummary;
  const retentionText = data
    ? data.retentionDays === 0
      ? '永久保留'
      : `保留 ${data.retentionDays} 天`
    : '保留期未知';

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={data?.summary ?? null} />

      {utility && utilitySummary && utilitySummary.calls > 0 && (
        <Card size="small" title="辅助调用">
          <Descriptions
            size="small"
            column={{ xs: 1, sm: 2, md: 3 }}
            items={[
              {
                key: 'calls',
                label: '调用',
                children: number.format(utilitySummary.calls),
              },
              {
                key: 'input',
                label: '输入 tokens',
                children: number.format(utilitySummary.inputTokens),
              },
              {
                key: 'cache',
                label: '缓存命中 tokens',
                children: number.format(utilitySummary.cacheReadTokens),
              },
              {
                key: 'output',
                label: '输出 tokens',
                children: number.format(utilitySummary.outputTokens),
              },
              {
                key: 'cost',
                label: '估算费用',
                children: formatMoney(utilitySummary.costCny),
              },
              {
                key: 'unpriced',
                label: '未定价调用',
                children: number.format(utilitySummary.unpricedCalls),
              },
            ]}
          />
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            辅助调用通常不带会话 ID，无法可靠归属到具体会话；此处单独统计，不并入主调用、模型与会话汇总。
          </Typography.Text>
        </Card>
      )}

      <Card size="small" title="按模型">
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

      <Card size="small" title="会话">
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

      <Card size="small" title="最近调用">
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(true, onOpenSession)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 1100, y: 480 }}
        />
      </Card>

      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        费用为基于 DeepSeek、Kimi、小米 MiMo、MiniMax、智谱 GLM
        公开价目表的估算（{money.unitLabel}），与 GitHub Copilot
        订阅账单无关；命中率下降不一定代表缓存失效。数据来自 Copilot OTel，经本机
        4318 接收后存入本地 SQLite，{retentionText}。
      </Typography.Text>
    </Space>
  );
}
