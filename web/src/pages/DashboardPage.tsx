import { Card, Space, Table, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import {
  callColumns,
  callRowKey,
  modelColumns,
  sessionColumns,
} from '../components/columns';
import type { DashboardPayload } from '../types';

interface DashboardPageProps {
  data: DashboardPayload | null;
  onOpenSession: (sessionId: string) => void;
}

export function DashboardPage({ data, onOpenSession }: DashboardPageProps) {
  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={data?.summary ?? null} />

      <Card size="small" title="按模型">
        <Table
          size="small"
          rowKey="model"
          columns={modelColumns()}
          dataSource={data?.models ?? []}
          loading={!data}
          pagination={false}
          scroll={{ x: 760 }}
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
          scroll={{ x: 1180 }}
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
          scroll={{ x: 1000 }}
        />
      </Card>

      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        费用为基于 DeepSeek、Kimi、小米 MiMo、MiniMax、智谱 GLM
        公开价目表的估算（CNY），与 GitHub Copilot
        订阅账单无关；命中率下降不一定代表缓存失效。数据来自 Copilot OTel，经本机
        4318 接收后存入本地 SQLite，保留 90 天。
      </Typography.Text>
    </Space>
  );
}
