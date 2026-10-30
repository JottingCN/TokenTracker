import { Card, Space, Table, Tag, Typography } from 'antd';
import { callColumns, callRowKey } from '../components/columns';
import { useRealtime } from '../useRealtime';
import type { DashboardPayload } from '../types';
import { useMoney } from '../utils/money';

interface CallsPageProps {
  range: number;
  fromMs?: number;
  toMs?: number;
  onOpenSession: (sessionId: string) => void;
}

export function CallsPage({
  range,
  fromMs,
  toMs,
  onOpenSession,
}: CallsPageProps) {
  useMoney();
  const { data, connected } = useRealtime<DashboardPayload>({
    view: 'dashboard',
    range,
    utility: true,
    fromMs,
    toMs,
  });

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space size="middle" align="center">
        <Typography.Title level={4} style={{ margin: 0 }}>
          最近调用
        </Typography.Title>
        {!connected && <Tag color="error">连接断开</Tag>}
      </Space>

      <Card size="small" title={`调用记录（${data?.calls.length ?? 0}）`}>
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(true, onOpenSession)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 1100, y: 560 }}
        />
      </Card>
    </Space>
  );
}
