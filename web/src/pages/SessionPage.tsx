import { ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { SummaryCards } from '../components/SummaryCards';
import { callColumns, callRowKey } from '../components/columns';
import { useRealtime } from '../useRealtime';
import type { SessionPayload } from '../types';
import { sessionDisplay } from '../utils/model';

interface SessionPageProps {
  sessionId: string;
  range: number;
}

export function SessionPage({ sessionId, range }: SessionPageProps) {
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
              {sessionDisplay(data?.title ?? null, sessionId)}
            </Typography.Title>
            <Tooltip title="刷新名称">
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
        {!connected && <Tag color="error">连接断开</Tag>}
      </Space>

      <SummaryCards
        summary={data?.summary ?? null}
        lastCard={{
          label: '明细条数',
          value: data ? data.calls.length : '-',
        }}
      />

      <Card size="small" title="调用明细">
        <Table
          size="small"
          rowKey={callRowKey}
          columns={callColumns(false)}
          dataSource={data?.calls ?? []}
          loading={!data}
          pagination={{ pageSize: 20, hideOnSinglePage: true }}
          scroll={{ x: 900, y: 520 }}
        />
      </Card>
    </Space>
  );
}
