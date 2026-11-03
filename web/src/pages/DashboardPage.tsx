import { useEffect, useState } from 'react';
import { Card, Empty, Space, Table, Typography } from 'antd';
import { CardPagination } from '../components/CardPagination';
import { OutcomeCard } from '../components/OutcomeCard';
import { SummaryCards } from '../components/SummaryCards';
import { modelColumns, sessionColumns } from '../components/columns';
import type { DashboardPayload } from '../types';
import { useUi } from '../ui';
import { useViewportHeight } from '../utils/useViewportHeight';

interface DashboardPageProps {
  data: DashboardPayload | null;
  onOpenSession: (sessionId: string) => void;
}

export function DashboardPage({
  data,
  onOpenSession,
}: DashboardPageProps) {
  const { tr } = useUi();
  const models = data?.models ?? [];
  const sessions = data?.sessions ?? [];
  const [page, setPage] = useState(1);
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(sessions.length / pageSize));
  const modelViewportHeight = useViewportHeight(0.3, 160, 340);
  const sessionViewportHeight = useViewportHeight(0.42, 260, 520);
  const modelHeight = Math.min(
    modelViewportHeight,
    Math.max(120, models.length * 45 + 40),
  );
  const pageRowCount = Math.min(
    pageSize,
    Math.max(0, sessions.length - (page - 1) * pageSize),
  );
  const sessionHeight = Math.min(
    sessionViewportHeight,
    Math.max(180, pageRowCount * 41 + 40),
  );

  useEffect(() => {
    if (page > pageCount) {
      setPage(1);
    }
  }, [page, pageCount]);

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={data?.summary ?? null} />

      {data?.signalHealth &&
        data.signalHealth.agentRuns +
          data.signalHealth.hookEvents +
          data.signalHealth.metricPoints >
          0 && (
          <Card size="small" title={tr('信号与 Agent', 'Signals & agents')}>
            <Space wrap size="large">
              <Typography.Text>
                {tr('Agent 运行', 'Agent runs')}：{data.signalHealth.agentRuns}
              </Typography.Text>
              <Typography.Text>
                {tr('Hook 事件', 'Hook events')}：{data.signalHealth.hookEvents}
              </Typography.Text>
              <Typography.Text>
                {tr('指标点', 'Metric points')}：{data.signalHealth.metricPoints}
              </Typography.Text>
              <Typography.Text type="secondary">
                {tr('最近信号', 'Last signal')}：{data.signalHealth.lastSignalText}
              </Typography.Text>
            </Space>
          </Card>
        )}

      <OutcomeCard outcomes={data?.outcomes} />

      <Card
        size="small"
        title={`${tr('按模型', 'By model')}(${models.length})`}
      >        {data && models.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={tr('暂无数据', 'No data')}
          />
        ) : (
          <Table
            size="small"
            rowKey="model"
            columns={modelColumns()}
            dataSource={models}
            loading={!data}
            pagination={false}
            scroll={{ x: 900, y: modelHeight }}
          />
        )}
      </Card>

      <Card
        size="small"
        title={`${tr('会话', 'Sessions')}(${sessions.length})`}
        extra={
          <CardPagination
            current={page}
            pageSize={pageSize}
            total={sessions.length}
            onChange={(nextPage) => setPage(nextPage)}
          />
        }
      >
        {data && sessions.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={tr('暂无数据', 'No data')}
          />
        ) : (
          <Table
            className="tt-fill-table"
            size="small"
            rowKey="sessionId"
            columns={sessionColumns(true, onOpenSession)}
            dataSource={sessions.slice(
              (page - 1) * pageSize,
              page * pageSize,
            )}
            loading={!data}
            pagination={false}
            scroll={{ x: 1090, y: sessionHeight }}
          />
        )}
      </Card>
    </Space>
  );
}
