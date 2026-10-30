import { ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import { callColumns, callRowKey } from '../components/columns';
import { useRealtime } from '../useRealtime';
import type { SessionPayload } from '../types';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';
import { tableHeight } from '../utils/tableHeight';

interface SessionPageProps {
  sessionId: string;
  range: number;
}

export function SessionPage({ sessionId, range }: SessionPageProps) {
  useMoney();
  const { tr } = useUi();
  const shortId =
    sessionId.length > 20 ? `${sessionId.slice(0, 8)}…` : sessionId;
  const { data, connected, refresh } = useRealtime<SessionPayload>({
    view: 'session',
    id: sessionId,
    range,
    utility: false,
  });

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space size="middle" align="center">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <Space size={6} align="center">
            <Typography.Title level={4} style={{ margin: 0 }}>
              {data?.title?.trim() ||
                `${tr('未命名', 'Unnamed')} · ${shortId}`}
            </Typography.Title>
            <Tooltip title={tr('刷新名称', 'Refresh title')}>
              <Button
                size="small"
                type="text"
                icon={<ReloadOutlined />}
                onClick={refresh}
              />
            </Tooltip>
          </Space>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {sessionId}
          </Typography.Text>
        </div>
        {!connected && (
          <Tag color="error">{tr('连接断开', 'Disconnected')}</Tag>
        )}
      </Space>

      <SummaryCards
        summary={data?.summary ?? null}
        lastCard={{
          label: tr('明细条数', 'Call rows'),
          value: data ? data.calls.length : '-',
        }}
      />

      <Card size="small" title={tr('调用明细', 'Call details')}>
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(false)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 900, y: tableHeight(620) }}
        />
      </Card>
    </Space>
  );
}
