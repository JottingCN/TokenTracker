import { Card, Space, Table, Tag, Typography } from 'antd';
import { callColumns, callRowKey } from '../components/columns';
import { useRealtime } from '../useRealtime';
import type { DashboardPayload } from '../types';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';
import { tableHeight } from '../utils/tableHeight';

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
  const { tr } = useUi();
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
          {tr('最近调用', 'Recent calls')}
        </Typography.Title>
        {!connected && (
          <Tag color="error">{tr('连接断开', 'Disconnected')}</Tag>
        )}
      </Space>

      <Card
        size="small"
        title={tr(
          `调用记录（${data?.calls.length ?? 0}）`,
          `Calls (${data?.calls.length ?? 0})`,
        )}
      >
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(true, onOpenSession)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 1100, y: tableHeight(360) }}
        />
      </Card>
    </Space>
  );
}
