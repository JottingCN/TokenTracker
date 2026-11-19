import { useEffect, useState } from 'react';
import { DownOutlined, UpOutlined } from '@ant-design/icons';
import { Button, Card, Empty, Space, Table, Typography } from 'antd';
import { CardPagination } from '../components/CardPagination';
import { OutcomeCard } from '../components/OutcomeCard';
import { LatencyCards } from '../components/LatencyCards';
import { SummaryCards } from '../components/SummaryCards';
import { modelColumns, sessionColumns } from '../components/columns';
import type { DashboardPayload } from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import {
  TABLE_ROW_DOUBLE_PX,
  clampTableHeight,
} from '../utils/tableMetrics';
import { useViewportHeight } from '../utils/useViewportHeight';

interface DashboardPageProps {
  data: DashboardPayload | null;
  range: number;
  fromMs?: number;
  toMs?: number;
  onOpenSession: (sessionId: string) => void;
  onOpenModel?: (model: string) => void;
}

export function DashboardPage({
  data,
  range,
  fromMs,
  toMs,
  onOpenSession,
  onOpenModel,
}: DashboardPageProps) {
  const { locale, t } = useUi();
  const number = new Intl.NumberFormat(intlLocale(locale));
  const models = data?.models ?? [];
  const sessions = data?.sessions ?? [];
  const [page, setPage] = useState(1);
  const [openLists, setOpenLists] = useState<string[]>([]);
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
  const totalSessionCalls = sessions.reduce((total, row) => total + row.calls, 0);

  useEffect(() => {
    if (page > pageCount) {
      setPage(1);
    }
  }, [page, pageCount]);

  const toggleList = (list: string) => {
    setOpenLists((current) =>
      current.includes(list)
        ? current.filter((value) => value !== list)
        : [...current, list],
    );
  };

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards
        summary={data?.summary ?? null}
        range={range}
        fromMs={fromMs}
        toMs={toMs}
      />

      <LatencyCards summary={data?.summary} />

      <OutcomeCard outcomes={data?.outcomes} />

      <Card
        size="small"
        title={`${t('by.model')}(${models.length})`}
        styles={
          openLists.includes('models')
            ? undefined
            : { body: { display: 'none' } }
        }
        extra={
          <Space size={8}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {`${t('calls')}: ${data ? number.format(data.summary.calls) : '-'} · ${t('model')}: ${number.format(models.length)}`}
            </Typography.Text>
            <ListToggle
              open={openLists.includes('models')}
              onToggle={() => toggleList('models')}
            />
          </Space>
        }
      >
        {openLists.includes('models') &&
          (data && models.length === 0 ? (
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
            scroll={{ x: 1130, y: modelHeight }}
          />
          ))}
      </Card>

      <Card
        size="small"
        title={`${t('sessions')}(${sessions.length})`}
        styles={
          openLists.includes('sessions')
            ? undefined
            : { body: { display: 'none' } }
        }
        extra={
          <Space size={8}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {`${t('calls')}: ${data ? number.format(totalSessionCalls) : '-'} · ${t('total.tokens')}: ${data ? number.format(data.summary.inputTokens + data.summary.outputTokens) : '-'}`}
            </Typography.Text>
            {openLists.includes('sessions') && (
              <CardPagination
                current={page}
                pageSize={pageSize}
                total={sessions.length}
                onChange={(nextPage) => setPage(nextPage)}
              />
            )}
            <ListToggle
              open={openLists.includes('sessions')}
              onToggle={() => toggleList('sessions')}
            />
          </Space>
        }
      >
        {openLists.includes('sessions') && (data && sessions.length === 0 ? (
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
            scroll={{ x: 1190, y: sessionHeight }}
          />
        ))}
      </Card>
    </Space>
  );
}

function ListToggle({
  open,
  onToggle,
}: {
  open: boolean;
  onToggle: () => void;
}) {
  const { t } = useUi();
  return (
    <Button
      size="small"
      type="text"
      aria-expanded={open}
      aria-label={open ? t('collapse') : t('expand')}
      icon={open ? <UpOutlined /> : <DownOutlined />}
      onClick={onToggle}
    >
      {open ? t('collapse') : t('expand')}
    </Button>
  );
}
