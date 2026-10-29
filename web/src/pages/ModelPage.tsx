import { Card, Space, Table, Tag, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import { sessionColumns } from '../components/columns';
import { useRealtime } from '../useRealtime';
import { providerOf, shortModelName } from '../utils/model';
import type { ModelPayload } from '../types';

interface ModelPageProps {
  modelId: string;
  range: number;
  fromMs?: number;
  toMs?: number;
  onOpenSession: (sessionId: string) => void;
}

export function ModelPage({
  modelId,
  range,
  fromMs,
  toMs,
  onOpenSession,
}: ModelPageProps) {
  const { data, connected } = useRealtime<ModelPayload>({
    view: 'model',
    id: modelId,
    range,
    utility: false,
    fromMs,
    toMs,
  });
  const title = `${providerOf(modelId)} ${shortModelName(modelId)}`.trim();

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space size="middle" align="center">
        <div>
          <Typography.Title level={4} style={{ margin: 0 }}>
            {title}
          </Typography.Title>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {modelId}
          </Typography.Text>
        </div>
        {!connected && <Tag color="error">连接断开</Tag>}
      </Space>

      <SummaryCards summary={data?.summary ?? null} />

      <Card size="small" title="会话（使用该模型）">
        <Table
          size="small"
          rowKey="sessionId"
          columns={sessionColumns(false, onOpenSession)}
          dataSource={data?.sessions ?? []}
          loading={!data}
          pagination={{ pageSize: 10, hideOnSinglePage: true }}
          scroll={{ x: 1100 }}
        />
      </Card>
    </Space>
  );
}
