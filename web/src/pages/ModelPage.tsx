import { Card, Space, Table, Tag, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import {
  callColumns,
  callRowKey,
  sessionColumns,
} from '../components/columns';
import { useRealtime } from '../useRealtime';
import { useUi } from '../ui';
import { providerOf, shortModelName } from '../utils/model';
import { useMoney } from '../utils/money';
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
  useMoney();
  const { tr } = useUi();
  const { data, connected } = useRealtime<ModelPayload>({
    view: 'model',
    id: modelId,
    range,
    utility: true,
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
        {!connected && (
          <Tag color="error">{tr('连接断开', 'Disconnected')}</Tag>
        )}
      </Space>

      <SummaryCards summary={data?.summary ?? null} />

      <Card
        size="small"
        title={tr(
          '会话（使用该模型，主调用）',
          'Sessions using this model (main calls)',
        )}
      >
        <Table
          size="small"
          rowKey="sessionId"
          columns={sessionColumns(false, onOpenSession)}
          dataSource={data?.sessions ?? []}
          loading={!data}
          pagination={{ pageSize: 10, hideOnSinglePage: true }}
          scroll={{ x: 930, y: 420 }}
        />
      </Card>

      <Card
        size="small"
        title={tr(
          `最近调用（使用该模型，${data?.calls.length ?? 0}）`,
          `Recent calls using this model (${data?.calls.length ?? 0})`,
        )}
      >
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(true, onOpenSession)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 1100, y: 520 }}
        />
      </Card>
    </Space>
  );
}
