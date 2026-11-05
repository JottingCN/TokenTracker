import { useEffect, useState } from 'react';
import { Card, Empty, Space, Table } from 'antd';
import { CardPagination } from '../components/CardPagination';
import { OutcomeCard } from '../components/OutcomeCard';
import { SignalCard } from '../components/SignalCard';
import { SummaryCards } from '../components/SummaryCards';
import { modelColumns, sessionColumns } from '../components/columns';
import type { DashboardPayload } from '../types';
import { useUi } from '../ui';
import {
  TABLE_ROW_DOUBLE_PX,
  clampTableHeight,
} from '../utils/tableMetrics';
import { useViewportHeight } from '../utils/useViewportHeight';

interface DashboardPageProps {
  data: DashboardPayload | null;
  onOpenSession: (sessionId: string) => void;
  onOpenModel?: (model: string) => void;
}

export function DashboardPage({
  data,
  onOpenSession,
  onOpenModel,
}: DashboardPageProps) {
  const { t } = useUi();
  const models = data?.models ?? [];
  const sessions = data?.sessions ?? [];
  const [page, setPage] = useState(1);
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(sessions.length / pageSize));
  const modelViewportHeight = useViewportHeight(0.3, 160, 340);
  const sessionViewportHeight = useViewportHeight(0.42, 260, 520);
  const modelHeight = clampTableHeight(
    modelViewportHeight,
    models.length,
    TABLE_ROW_DOUBLE_PX,
  );
  const pageRowCount = Math.min(
    pageSize,
    Math.max(0, sessions.length - (page - 1) * pageSize),
  );
  const sessionHeight = clampTableHeight(
    sessionViewportHeight,
    pageRowCount,
    TABLE_ROW_DOUBLE_PX,
  );

  useEffect(() => {
    if (page > pageCount) {
      setPage(1);
    }
  }, [page, pageCount]);

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={data?.summary ?? null} />

      <SignalCard signalHealth={data?.signalHealth} />

      <OutcomeCard outcomes={data?.outcomes} />

      <Card
        size="small"
        title={`${t('by.model')}(${models.length})`}
      >        {data && models.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={t('no.data')}
          />
        ) : (
          <Table
            size="small"
            rowKey="model"
            columns={modelColumns(onOpenModel)}
            dataSource={models}
            loading={!data}
            pagination={false}
            scroll={{ x: 1030, y: modelHeight }}
          />
        )}
      </Card>

      <Card
        size="small"
        title={`${t('sessions')}(${sessions.length})`}
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
            description={t('no.data')}
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
