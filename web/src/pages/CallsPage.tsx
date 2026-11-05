import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Button,
  Card,
  Empty,
  Input,
  Select,
  Space,
  Table,
} from 'antd';
import { CardPagination } from '../components/CardPagination';
import { callColumns, callRowKey } from '../components/columns';
import { ModelIcon } from '../components/ModelIcon';
import { useRealtime } from '../useRealtime';
import type { CallsPayload, DashboardPayload } from '../types';
import { useUi } from '../ui';
import { leafModelName, providerOf } from '../utils/model';
import { useModelProfiles } from '../utils/modelProfiles';
import { useMoney } from '../utils/money';
import {
  TABLE_ROW_DOUBLE_PX,
  clampTableHeight,
} from '../utils/tableMetrics';
import { useAutoTableHeight } from '../utils/useAutoTableHeight';

interface CallsPageProps {
  range: number;
  fromMs?: number;
  toMs?: number;
  dashboard: DashboardPayload | null;
  initialQuery?: string;
  onOpenSession: (sessionId: string) => void;
}

export function CallsPage({
  range,
  fromMs,
  toMs,
  dashboard,
  initialQuery,
  onOpenSession,
}: CallsPageProps) {
  useMoney();
  const { t } = useUi();
  const [model, setModel] = useState<string | undefined>();
  const [kind, setKind] = useState<'all' | 'main' | 'utility'>('all');
  const [priced, setPriced] = useState<'all' | 'priced' | 'unpriced'>('all');
  const [period, setPeriod] = useState<'all' | 'peak' | 'idle' | 'flat'>('all');
  const [errorFilter, setErrorFilter] = useState<'all' | 'error' | 'ok'>('all');
  const [agent, setAgent] = useState('');
  const [search, setSearch] = useState(initialQuery ?? '');
  const [query, setQuery] = useState(initialQuery ?? '');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [tableHostRef, tableAvailable] = useAutoTableHeight(60);

  const { data } = useRealtime<CallsPayload>({
    view: 'calls',
    range,
    utility: true,
    fromMs,
    toMs,
    model,
    query: query || undefined,
    kind: kind === 'all' ? undefined : kind,
    priced: priced === 'all' ? undefined : priced === 'priced',
    period: period === 'all' ? undefined : period,
    hasError:
      errorFilter === 'all' ? undefined : errorFilter === 'error',
    agent: agent.trim() || undefined,
    offset: (page - 1) * pageSize,
    limit: pageSize,
  });

  useEffect(() => {
    setPage(1);
  }, [model, kind, priced, period, errorFilter, agent, query]);

  // Page size is 50, so clamp to the visible rows to avoid a mostly-empty table.
  const rows = data?.calls ?? [];
  const tableHeightPx = clampTableHeight(
    tableAvailable,
    rows.length,
    TABLE_ROW_DOUBLE_PX,
  );

  const { profiles } = useModelProfiles();
  const modelOptions = useMemo(
    () =>
      (dashboard?.models ?? []).map((row) => ({
        value: row.model,
        label: (
          <span
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
            }}
          >
            <ModelIcon model={row.model} size={14} />
            <span>
              {providerOf(
                row.model,
                profiles[row.model.toLowerCase()]?.provider,
              )}{' '}
              · {leafModelName(row.model)}
            </span>
          </span>
        ),
      })),
    [dashboard, profiles],
  );

  const resetFilters = () => {
    setModel(undefined);
    setKind('all');
    setPriced('all');
    setPeriod('all');
    setErrorFilter('all');
    setAgent('');
    setSearch('');
    setQuery('');
    setPage(1);
  };

  const total = data?.total ?? 0;
  // Before the first snapshot arrives the count is unknown; showing 0 would look
  // like an empty result set right after a deep link such as ?q=<sessionId>.
  const totalLabel = data ? total : '…';
  const onPageChange = (nextPage: number, nextPageSize: number) => {
    if (nextPageSize !== pageSize) {
      setPageSize(nextPageSize);
      setPage(1);
    } else {
      setPage(nextPage);
    }
  };

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card
        size="small"
        title={`${t('results')}(${totalLabel})`}
        extra={
          <CardPagination
            current={page}
            pageSize={pageSize}
            total={total}
            onChange={onPageChange}
            showSizeChanger
          />
        }
      >
        <Space wrap size={8} style={{ marginBottom: 12 }}>
          <Input.Search
            allowClear
            value={search}
            placeholder={t('search.title.session.model')}
            style={{ width: 260 }}
            onChange={(event) => {
              setSearch(event.target.value);
              if (!event.target.value) {
                setQuery('');
              }
            }}
            onSearch={(value) => setQuery(value.trim())}
          />
          <Select
            allowClear
            showSearch
            value={model}
            placeholder={t('all.models')}
            style={{ width: 180 }}
            options={modelOptions}
            onChange={setModel}
          />
          <Select
            value={kind}
            style={{ width: 140 }}
            options={[
              { value: 'all', label: t('all.calls') },
              { value: 'main', label: t('main.calls') },
              { value: 'utility', label: t('utility.calls') },
            ]}
            onChange={setKind}
          />
          <Select
            value={priced}
            style={{ width: 140 }}
            options={[
              { value: 'all', label: t('all.pricing') },
              { value: 'priced', label: t('priced') },
              { value: 'unpriced', label: t('unpriced') },
            ]}
            onChange={setPriced}
          />
          <Select
            value={period}
            style={{ width: 130 }}
            options={[
              { value: 'all', label: t('all.periods') },
              { value: 'peak', label: t('peak') },
              { value: 'idle', label: t('idle') },
              { value: 'flat', label: t('flat') },
            ]}
            onChange={setPeriod}
          />
          <Select
            value={errorFilter}
            style={{ width: 130 }}
            options={[
              { value: 'all', label: t('all.status') },
              { value: 'ok', label: t('ok') },
              { value: 'error', label: t('error') },
            ]}
            onChange={setErrorFilter}
          />
          <Input.Search
            allowClear
            value={agent}
            placeholder={t('agent.name')}
            style={{ width: 180 }}
            onChange={(event) => setAgent(event.target.value)}
            onSearch={() => setPage(1)}
          />
          <Button onClick={resetFilters}>{t('reset')}</Button>
        </Space>

        {data && rows.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={t('no.data')}
          />
        ) : (
          <div
            ref={tableHostRef}
            style={
              {
                '--tt-table-body-height': `${tableHeightPx}px`,
              } as CSSProperties
            }
          >
            <Table
              className="tt-fill-table"
              size="small"
              rowKey={callRowKey}
              columns={callColumns(onOpenSession)}
              dataSource={rows}
              loading={!data}
              pagination={false}
              scroll={{ x: 1900, y: tableHeightPx }}
            />
          </div>
        )}
      </Card>
    </Space>
  );
}
