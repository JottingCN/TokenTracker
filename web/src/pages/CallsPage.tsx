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
import { providerOf, shortModelName } from '../utils/model';
import { useMoney } from '../utils/money';
import { useAutoTableHeight } from '../utils/useAutoTableHeight';

interface CallsPageProps {
  range: number;
  fromMs?: number;
  toMs?: number;
  dashboard: DashboardPayload | null;
  onOpenSession: (sessionId: string) => void;
}

export function CallsPage({
  range,
  fromMs,
  toMs,
  dashboard,
  onOpenSession,
}: CallsPageProps) {
  useMoney();
  const { tr } = useUi();
  const [model, setModel] = useState<string | undefined>();
  const [kind, setKind] = useState<'all' | 'main' | 'utility'>('all');
  const [priced, setPriced] = useState<'all' | 'priced' | 'unpriced'>('all');
  const [period, setPeriod] = useState<'all' | 'peak' | 'idle' | 'flat'>('all');
  const [errorFilter, setErrorFilter] = useState<'all' | 'error' | 'ok'>('all');
  const [agent, setAgent] = useState('');
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [tableHostRef, tableHeightPx] = useAutoTableHeight(60);

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
              {providerOf(row.model)} · {shortModelName(row.model)}
            </span>
          </span>
        ),
      })),
    [dashboard],
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
        title={`${tr('检索结果', 'Results')}(${total})`}
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
            placeholder={tr(
              '搜索标题 / 会话 ID / 模型',
              'Search title / session / model',
            )}
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
            placeholder={tr('全部模型', 'All models')}
            style={{ width: 180 }}
            options={modelOptions}
            onChange={setModel}
          />
          <Select
            value={kind}
            style={{ width: 140 }}
            options={[
              { value: 'all', label: tr('全部调用', 'All calls') },
              { value: 'main', label: tr('主调用', 'Main calls') },
              { value: 'utility', label: tr('辅助调用', 'Utility calls') },
            ]}
            onChange={setKind}
          />
          <Select
            value={priced}
            style={{ width: 140 }}
            options={[
              { value: 'all', label: tr('全部价格', 'All pricing') },
              { value: 'priced', label: tr('已定价', 'Priced') },
              { value: 'unpriced', label: tr('未定价', 'Unpriced') },
            ]}
            onChange={setPriced}
          />
          <Select
            value={period}
            style={{ width: 130 }}
            options={[
              { value: 'all', label: tr('全部时段', 'All periods') },
              { value: 'peak', label: tr('高峰', 'Peak') },
              { value: 'idle', label: tr('空闲', 'Idle') },
              { value: 'flat', label: tr('固定', 'Flat') },
            ]}
            onChange={setPeriod}
          />
          <Select
            value={errorFilter}
            style={{ width: 130 }}
            options={[
              { value: 'all', label: tr('全部状态', 'All status') },
              { value: 'ok', label: tr('成功', 'OK') },
              { value: 'error', label: tr('错误', 'Error') },
            ]}
            onChange={setErrorFilter}
          />
          <Input.Search
            allowClear
            value={agent}
            placeholder={tr('Agent 名称', 'Agent name')}
            style={{ width: 180 }}
            onChange={(event) => setAgent(event.target.value)}
            onSearch={() => setPage(1)}
          />
          <Button onClick={resetFilters}>{tr('重置', 'Reset')}</Button>
        </Space>

        {data && data.calls.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={tr('暂无数据', 'No data')}
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
              dataSource={data?.calls ?? []}
              loading={!data}
              pagination={false}
              scroll={{ x: 1800, y: tableHeightPx }}
            />
          </div>
        )}
      </Card>
    </Space>
  );
}
