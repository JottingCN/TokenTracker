import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Card,
  Empty,
  Space,
  Table,
} from 'antd';
import { CardPagination } from '../components/CardPagination';
import { LatencyCards } from '../components/LatencyCards';
import { SummaryCards } from '../components/SummaryCards';
import { sessionColumns } from '../components/columns';
import { useRealtime } from '../useRealtime';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';
import {
  TABLE_ROW_DOUBLE_PX,
  clampTableHeight,
} from '../utils/tableMetrics';
import { useAutoTableHeight } from '../utils/useAutoTableHeight';
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
  const { t } = useUi();
  const { data } = useRealtime<ModelPayload>({
    view: 'model',
    id: modelId,
    range,
    utility: true,
      fromMs,
    toMs,
  });
  const summary = data?.summary;
  const sessions = data?.sessions ?? [];
  const [sessionTableRef, sessionTableAvailable] = useAutoTableHeight(60);
  const [page, setPage] = useState(1);
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(sessions.length / pageSize));
  const pageRows = sessions.slice((page - 1) * pageSize, page * pageSize);
  // A short list must not stretch to the viewport: the body is sized to the rows.
  const sessionTableHeight = clampTableHeight(
    sessionTableAvailable,
    pageRows.length,
    TABLE_ROW_DOUBLE_PX,
  );

  useEffect(() => {
    if (page > pageCount) {
      setPage(1);
    }
  }, [page, pageCount]);

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={summary ?? null} />

      <LatencyCards summary={summary} />

      <Card
        size="small"
        title={`${t('sessions.using.this.model.main.calls')}(${sessions.length})`}
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
          <div
            ref={sessionTableRef}
            style={
              {
                '--tt-table-body-height': `${sessionTableHeight}px`,
              } as CSSProperties
            }
          >
            <Table
              className="tt-fill-table"
              size="small"
              rowKey="sessionId"
              columns={sessionColumns(false, onOpenSession, true)}
              dataSource={pageRows}
              loading={!data}
              pagination={false}
              scroll={{ x: 1340, y: sessionTableHeight }}
            />
          </div>
        )}
      </Card>
    </Space>
  );
}
