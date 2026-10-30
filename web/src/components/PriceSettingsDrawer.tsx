import { useEffect, useState } from 'react';
import {
  Alert,
  App as AntApp,
  Button,
  Drawer,
  Input,
  InputNumber,
  Popconfirm,
  Select,
  Space,
  Switch,
  Table,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { ModelPriceRow, SettingsPayload } from '../types';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';
import { tableHeight } from '../utils/tableHeight';

interface PriceSettingsDrawerProps {
  open: boolean;
  onClose: () => void;
}

type PeriodKind = 'idle' | 'peak' | 'flat';
type PriceSegment = 'default' | 'long';
type PriceField = 'cachedInput' | 'uncachedInput' | 'output' | 'cacheWrite';

interface PriceDisplayRow {
  key: string;
  model: string;
  period: PeriodKind;
  segment: PriceSegment;
  rowSpan: number;
  segmentRowSpan: number;
}

export function PriceSettingsDrawer({ open, onClose }: PriceSettingsDrawerProps) {
  const { message } = AntApp.useApp();
  const money = useMoney();
  const { tr } = useUi();
  const [rows, setRows] = useState<ModelPriceRow[]>([]);
  const [drafts, setDrafts] = useState<Record<string, ModelPriceRow>>({});
  const [loading, setLoading] = useState(false);
  const [newModel, setNewModel] = useState('');
  const [retentionDays, setRetentionDays] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<'prices' | 'data'>('prices');

  const load = async () => {
    setLoading(true);
    try {
      const response = await fetch('/api/prices');
      if (response.status === 404) {
        throw new Error(
          tr(
            '服务端版本过旧，请更新并重启 Token Tracker 服务',
            'The server is too old. Update and restart Token Tracker.',
          ),
        );
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      setRows((await response.json()) as ModelPriceRow[]);
      setDrafts({});
    } catch (error) {
      message.error(
        tr(
          `读取价格失败: ${(error as Error).message}`,
          `Failed to load prices: ${(error as Error).message}`,
        ),
      );
    } finally {
      setLoading(false);
    }
  };

  const loadSettings = async () => {
    try {
      const response = await fetch('/api/settings');
      if (!response.ok) {
        return;
      }
      const data = (await response.json()) as SettingsPayload;
      setRetentionDays(data.retentionDays);
    } catch {
      // Older servers do not expose settings.
    }
  };

  useEffect(() => {
    if (open) {
      void load();
      void loadSettings();
    }
  }, [open]);

  const saveRetention = async () => {
    const days = retentionDays;
    if (days === null) {
      return;
    }

    try {
      const response = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ retentionDays: days }),
      });
      if (!response.ok) {
        throw new Error(await response.text());
      }
      const data = (await response.json()) as SettingsPayload;
      setRetentionDays(data.retentionDays);
      message.success(
        tr('数据保留设置已保存', 'Retention setting saved'),
      );
    } catch (error) {
      message.error(
        tr(
          `保存失败: ${(error as Error).message}`,
          `Save failed: ${(error as Error).message}`,
        ),
      );
    }
  };

  const draftOf = (row: ModelPriceRow) => drafts[row.model] ?? row;

  const update = (row: ModelPriceRow, patch: Partial<ModelPriceRow>) =>
    setDrafts((current) => ({
      ...current,
      [row.model]: { ...(current[row.model] ?? row), ...patch },
    }));

  const save = async (row: ModelPriceRow) => {
    const draft = draftOf(row);
    const longEnabled = (draft.longContextThreshold ?? 0) > 0;
    try {
      const response = await fetch('/api/prices', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model: draft.model,
          peakAware: draft.peakAware,
          cachedInput: draft.cachedInput,
          uncachedInput: draft.uncachedInput,
          output: draft.output,
          peakCachedInput: draft.peakAware
            ? (draft.peakCachedInput ?? draft.cachedInput)
            : null,
          peakUncachedInput: draft.peakAware
            ? (draft.peakUncachedInput ?? draft.uncachedInput)
            : null,
          peakOutput: draft.peakAware ? (draft.peakOutput ?? draft.output) : null,
          longContextThreshold: longEnabled
            ? Math.round(draft.longContextThreshold as number)
            : null,
          longCachedInput: longEnabled
            ? (draft.longCachedInput ?? draft.cachedInput)
            : null,
          longUncachedInput: longEnabled
            ? (draft.longUncachedInput ?? draft.uncachedInput)
            : null,
          longOutput: longEnabled ? (draft.longOutput ?? draft.output) : null,
          longPeakCachedInput:
            longEnabled && draft.peakAware
              ? (draft.longPeakCachedInput ??
                draft.longCachedInput ??
                draft.peakCachedInput ??
                draft.cachedInput)
              : null,
          longPeakUncachedInput:
            longEnabled && draft.peakAware
              ? (draft.longPeakUncachedInput ??
                draft.longUncachedInput ??
                draft.peakUncachedInput ??
                draft.uncachedInput)
              : null,
          longPeakOutput:
            longEnabled && draft.peakAware
              ? (draft.longPeakOutput ?? draft.longOutput ?? draft.peakOutput ?? draft.output)
              : null,
          cacheWrite: draft.cacheWrite ?? draft.uncachedInput,
          peakCacheWrite: draft.peakAware
            ? (draft.peakCacheWrite ??
              draft.cacheWrite ??
              draft.peakUncachedInput ??
              draft.uncachedInput)
            : null,
          longCacheWrite: longEnabled
            ? (draft.longCacheWrite ??
              draft.longUncachedInput ??
              draft.uncachedInput)
            : null,
          longPeakCacheWrite:
            longEnabled && draft.peakAware
              ? (draft.longPeakCacheWrite ??
                draft.longCacheWrite ??
                draft.longUncachedInput ??
                draft.peakUncachedInput ??
                draft.uncachedInput)
              : null,
        }),
      });
      if (response.status === 404) {
        throw new Error(
          tr(
            '服务端版本过旧，请更新并重启 Token Tracker 服务',
            'The server is too old. Update and restart Token Tracker.',
          ),
        );
      }
      if (!response.ok) {
        throw new Error(await response.text());
      }
      message.success(
        tr(
          `已保存 ${row.model}，历史费用已重算`,
          `Saved ${row.model}; historical costs recalculated`,
        ),
      );
      await load();
    } catch (error) {
      message.error(
        tr(
          `保存失败: ${(error as Error).message}`,
          `Save failed: ${(error as Error).message}`,
        ),
      );
    }
  };

  const remove = async (row: ModelPriceRow) => {
    try {
      const response = await fetch(
        `/api/prices?model=${encodeURIComponent(row.model)}`,
        { method: 'DELETE' },
      );
      if (response.status === 404) {
        throw new Error(
          tr(
            '服务端版本过旧，请更新并重启 Token Tracker 服务',
            'The server is too old. Update and restart Token Tracker.',
          ),
        );
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      message.success(
        tr(
          `已删除 ${row.model} 的自定义价格`,
          `Deleted custom pricing for ${row.model}`,
        ),
      );
      await load();
    } catch (error) {
      message.error(
        tr(
          `操作失败: ${(error as Error).message}`,
          `Operation failed: ${(error as Error).message}`,
        ),
      );
    }
  };

  const addModel = () => {
    const model = newModel.trim();
    if (!model) {
      return;
    }
    const existing = rows.find(
      (row) => row.model.toLowerCase() === model.toLowerCase(),
    );
    if (existing) {
      message.info(
        existing.calls > 0
          ? tr(
              `该模型已有 ${existing.calls} 次调用记录，可直接在列表中设置价格`,
              `This model already has ${existing.calls} recorded calls; set its price in the list.`,
            )
          : tr('该模型已在列表中', 'Model is already in the list'),
      );
      return;
    }

    setRows((current) => [
      {
        model,
        customized: false,
        calls: 0,
        peakAware: false,
        cachedInput: 0,
        uncachedInput: 0,
        output: 0,
        peakCachedInput: null,
        peakUncachedInput: null,
        peakOutput: null,
        longContextThreshold: null,
        longCachedInput: null,
        longUncachedInput: null,
        longOutput: null,
        longPeakCachedInput: null,
        longPeakUncachedInput: null,
        longPeakOutput: null,
        cacheWrite: null,
        peakCacheWrite: null,
        longCacheWrite: null,
        longPeakCacheWrite: null,
      },
      ...current,
    ]);
    setNewModel('');
  };

  const rowByModel = new Map(rows.map((row) => [row.model, row]));

  const displayRows: PriceDisplayRow[] = rows.flatMap((row): PriceDisplayRow[] => {
    const draft = draftOf(row);
    const longEnabled = (draft.longContextThreshold ?? 0) > 0;
    const periods: PeriodKind[] = draft.peakAware ? ['idle', 'peak'] : ['flat'];
    const segments: PriceSegment[] = longEnabled
      ? ['default', 'long']
      : ['default'];
    const rowsForModel: PriceDisplayRow[] = [];
    let first = true;
    for (const segment of segments) {
      periods.forEach((period, index) => {
        rowsForModel.push({
          key: `${row.model}|${segment}|${period}`,
          model: row.model,
          period,
          segment,
          rowSpan: first ? periods.length * segments.length : 0,
          segmentRowSpan: index === 0 ? periods.length : 0,
        });
        first = false;
      });
    }
    return rowsForModel;
  });

  const priceField = (
    segment: PriceSegment,
    field: PriceField,
    peak: boolean,
  ): keyof ModelPriceRow => {
    if (segment === 'long') {
      if (field === 'cachedInput') {
        return peak ? 'longPeakCachedInput' : 'longCachedInput';
      }
      if (field === 'uncachedInput') {
        return peak ? 'longPeakUncachedInput' : 'longUncachedInput';
      }
      if (field === 'cacheWrite') {
        return peak ? 'longPeakCacheWrite' : 'longCacheWrite';
      }
      return peak ? 'longPeakOutput' : 'longOutput';
    }

    if (field === 'cachedInput') {
      return peak ? 'peakCachedInput' : 'cachedInput';
    }
    if (field === 'uncachedInput') {
      return peak ? 'peakUncachedInput' : 'uncachedInput';
    }
    if (field === 'cacheWrite') {
      return peak ? 'peakCacheWrite' : 'cacheWrite';
    }
    return peak ? 'peakOutput' : 'output';
  };

  const toggleLongContext = (row: ModelPriceRow, enabled: boolean) => {
    const draft = draftOf(row);
    if (!enabled) {
      update(row, { longContextThreshold: null });
      return;
    }

    update(row, {
      longContextThreshold: draft.longContextThreshold ?? 272000,
      longCachedInput: draft.longCachedInput ?? draft.cachedInput,
      longUncachedInput: draft.longUncachedInput ?? draft.uncachedInput,
      longOutput: draft.longOutput ?? draft.output,
      longPeakCachedInput:
        draft.longPeakCachedInput ?? draft.peakCachedInput ?? draft.cachedInput,
      longPeakUncachedInput:
        draft.longPeakUncachedInput ??
        draft.peakUncachedInput ??
        draft.uncachedInput,
      longPeakOutput:
        draft.longPeakOutput ?? draft.peakOutput ?? draft.output,
    });
  };

  const numberInput = (
    row: ModelPriceRow,
    kind: PeriodKind,
    field: PriceField,
    segment: PriceSegment,
  ) => {
    const draft = draftOf(row);
    const baseField = priceField(segment, field, false);
    const peakField = priceField(segment, field, true);
    const baseFallback =
      field === 'cacheWrite'
        ? segment === 'long'
          ? (draft.longUncachedInput ?? draft.uncachedInput ?? 0)
          : (draft.uncachedInput ?? 0)
        : 0;
    const peakFallback =
      field === 'cacheWrite'
        ? segment === 'long'
          ? (draft.longPeakUncachedInput ??
            draft.longUncachedInput ??
            draft.peakUncachedInput ??
            draft.uncachedInput ??
            0)
          : (draft.peakUncachedInput ?? draft.uncachedInput ?? 0)
        : 0;
    const cnyValue = Number(
      kind === 'peak'
        ? (draft[peakField] ?? draft[baseField] ?? peakFallback)
        : (draft[baseField] ?? baseFallback),
    );

    return (
      <InputNumber
        size="small"
        min={0}
        step={money.settings.unit === 'credit' ? 0.01 : 0.001}
        precision={money.pricePrecision}
        controls={false}
        value={money.toPriceInput(cnyValue)}
        onChange={(next) =>
          update(row, {
            [kind === 'peak' ? peakField : baseField]:
              money.fromPriceInput(typeof next === 'number' ? next : 0),
          } as Partial<ModelPriceRow>)
        }
        style={{ width: '100%' }}
      />
    );
  };

  const actionCell = (row: ModelPriceRow) => (
    <Space size={4}>
      <Button size="small" type="primary" onClick={() => void save(row)}>
        {tr('保存', 'Save')}
      </Button>
      {row.customized && (
        <Popconfirm
          title={tr(
            '删除该模型的自定义价格？',
            'Delete this custom price?',
          )}
          description={tr(
            '删除后该模型将无法估算费用。',
            'This model will no longer have estimated costs.',
          )}
          okText={tr('删除', 'Delete')}
          cancelText={tr('取消', 'Cancel')}
          onConfirm={() => remove(row)}
        >
          <Button size="small" danger>
            {tr('删除', 'Delete')}
          </Button>
        </Popconfirm>
      )}
    </Space>
  );

  const columns: ColumnsType<PriceDisplayRow> = [
    {
      title: tr('模型', 'Model'),
      key: 'model',
      width: 190,
      fixed: 'left',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        const statusTag = row.customized ? (
          <Tag color="blue" bordered={false}>
            {tr('自定义', 'Custom')}
          </Tag>
        ) : (
          <Tag bordered={false}>{tr('未定价', 'Unpriced')}</Tag>
        );

        return (
          <Space size={6} wrap={false} style={{ maxWidth: '100%' }}>
            <Typography.Text
              strong
              ellipsis={{ tooltip: row.model }}
              style={{ maxWidth: 170 }}
            >
              {row.model}
            </Typography.Text>
            {statusTag}
          </Space>
        );
      },
    },
    {
      title: tr('区分峰谷', 'Peak aware'),
      key: 'peakAware',
      width: 78,
      align: 'center',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        return (
          <Switch
            size="small"
            checked={draftOf(row).peakAware}
            onChange={(value) => update(row, { peakAware: value })}
          />
        );
      },
    },
    {
      title: tr('分档计费', 'Tiered'),
      key: 'longContext',
      width: 78,
      align: 'center',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        const enabled = (draftOf(row).longContextThreshold ?? 0) > 0;
        return (
          <Switch
            size="small"
            checked={enabled}
            onChange={(value) => toggleLongContext(row, value)}
          />
        );
      },
    },
    {
      title: tr('输入阈值', 'Input threshold'),
      key: 'longContextThreshold',
      width: 128,
      align: 'right',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        const draft = draftOf(row);
        const enabled = (draft.longContextThreshold ?? 0) > 0;
        return (
          <InputNumber
            size="small"
            min={1}
            max={10_000_000}
            step={1000}
            precision={0}
            controls={false}
            disabled={!enabled}
            placeholder={tr('例如 272000', 'e.g. 272000')}
            addonAfter="tokens"
            value={draft.longContextThreshold ?? undefined}
            onChange={(value) =>
              update(row, {
                longContextThreshold:
                  typeof value === 'number' ? Math.round(value) : null,
              })
            }
            style={{ width: '100%' }}
          />
        );
      },
    },
    {
      title: tr('档位', 'Tier'),
      key: 'segment',
      width: 78,
      align: 'center',
      onCell: (record) => ({ rowSpan: record.segmentRowSpan }),
      render: (_, record) =>
        record.segment === 'long' ? (
          <Tag color="purple">{tr('长上下文', 'Long context')}</Tag>
        ) : (
          <Tag>{tr('默认', 'Default')}</Tag>
        ),
    },
    {
      title: tr('时段', 'Period'),
      key: 'period',
      width: 76,
      align: 'center',
      render: (_, record) => {
        if (record.period === 'idle') {
          return <Tag color="green">{tr('空闲', 'Idle')}</Tag>;
        }
        if (record.period === 'peak') {
          return <Tag color="orange">{tr('高峰', 'Peak')}</Tag>;
        }
        return <Tag>{tr('固定', 'Flat')}</Tag>;
      },
    },
    {
      title: tr('缓存命中', 'Cached input'),
      key: 'cachedInput',
      width: 104,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row
          ? numberInput(row, record.period, 'cachedInput', record.segment)
          : null;
      },
    },
    {
      title: tr('缓存写入', 'Cache write'),
      key: 'cacheWrite',
      width: 108,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row
          ? numberInput(row, record.period, 'cacheWrite', record.segment)
          : null;
      },
    },
    {
      title: tr('输入（未命中）', 'Uncached input'),
      key: 'uncachedInput',
      width: 112,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row
          ? numberInput(row, record.period, 'uncachedInput', record.segment)
          : null;
      },
    },
    {
      title: tr('输出', 'Output'),
      key: 'output',
      width: 104,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row
          ? numberInput(row, record.period, 'output', record.segment)
          : null;
      },
    },
    {
      title: tr('操作', 'Actions'),
      key: 'actions',
      width: 166,
      fixed: 'right',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row ? actionCell(row) : null;
      },
    },
  ];

  return (
    <Drawer
      title={tr('设置', 'Settings')}
      width="min(1400px, 96vw)"
      open={open}
      onClose={onClose}
      styles={{ body: { padding: 16 } }}
      extra={
        <Button onClick={() => void load()} loading={loading}>
          {tr('刷新', 'Refresh')}
        </Button>
      }
    >
      <Tabs
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as 'prices' | 'data')}
        items={[
          {
            key: 'prices',
            label: tr('价格', 'Pricing'),
            children: (
              <div
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 12,
                }}
              >
        <Space wrap size="middle">
          <Space size={6}>
            <Typography.Text>{tr('费用单位', 'Cost unit')}</Typography.Text>
            <Select
              value={money.settings.unit}
              onChange={(unit) => money.setSettings({ unit })}
              style={{ width: 150 }}
              options={[
                { value: 'cny', label: tr('人民币（CNY）', 'CNY') },
                { value: 'usd', label: tr('美元（USD）', 'USD') },
                { value: 'credit', label: tr('积分', 'Credits') },
              ]}
            />
          </Space>
          {money.settings.unit === 'usd' && (
            <InputNumber
              size="small"
              min={0.01}
              step={0.1}
              precision={4}
              addonBefore="1 USD ="
              addonAfter="CNY"
              value={money.settings.usdRate}
              onChange={(value) =>
                money.setSettings({
                  usdRate: typeof value === 'number' ? value : 7.2,
                })
              }
            />
          )}
          {money.settings.unit === 'credit' && (
            <InputNumber
              size="small"
              min={0.0001}
              step={0.001}
              precision={4}
              addonBefore={tr('1 积分 =', '1 credit =')}
              addonAfter="CNY"
              value={money.settings.creditRate}
              onChange={(value) =>
                money.setSettings({
                  creditRate: typeof value === 'number' ? value : 0.01,
                })
              }
            />
          )}
        </Space>

        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {tr(
            '费用单位作用于看板显示与价格输入；价格保存时换算为 CNY 存入数据库。',
            'The unit applies to dashboard values and price inputs; prices are stored in CNY.',
          )}
        </Typography.Text>

        <Space.Compact style={{ width: 460 }}>
          <Input
            placeholder={tr(
              '新增模型 ID，例如 kimi-k3.1-pro',
              'Model ID, e.g. kimi-k3.1-pro',
            )}
            value={newModel}
            onChange={(event) => setNewModel(event.target.value)}
            onPressEnter={addModel}
          />
          <Button type="primary" onClick={addModel}>
            {tr('新增', 'Add')}
          </Button>
        </Space.Compact>

        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {tr(
            '模型来自已有调用记录或手动新增；未定价模型可直接填写价格。保存后会立即重算该模型的历史费用并同步刷新看板。',
            'Models come from existing call records or manual additions. Fill in a price for unpriced models; saving recalculates that model’s historical costs.',
          )}
        </Typography.Text>

        <Table
          size="small"
          bordered
          rowKey="key"
          columns={columns}
          dataSource={displayRows}
          loading={loading}
          pagination={false}
          scroll={{ x: 1230, y: tableHeight(340) }}
        />

        <Alert
          type="info"
          showIcon
          message={tr('峰谷与节假日规则', 'Peak and holiday rules')}
          description={tr(
            '开启「区分峰谷」后，该模型显示空闲/高峰两行价格；未开启时只有一行固定价格。开启「分档计费」后，按单次调用的输入 token 总数（含缓存命中）判断：不超过阈值使用「默认」档，超过阈值整次调用按「长上下文」档计价（两档价格可分别设置）。缓存写入价留空时按未命中输入价计算。峰谷按北京时间判定：工作日 9:00–12:00、14:00–18:00 为高峰，其余时间为空闲；周末与中国法定节假日全天按空闲（调休上班的周末仍按周末处理）。节假日数据优先读取本地数据库，缺失时自动从 apisbo.com 获取并缓存（内置 2025–2026 年数据兜底）。',
            'When peak-aware is enabled, two rows are shown (idle/peak); otherwise one flat price is used. Tiered pricing compares the total input tokens of each call (including cache reads) with the threshold: calls at or below it use Default rates, while the entire call is priced with Long context rates when it exceeds the threshold. Cache write falls back to the uncached input rate when left empty. Peak hours follow Beijing time: weekdays 9:00–12:00 and 14:00–18:00; all other times, weekends and Chinese public holidays are idle. Holiday data is read from the local database first, then fetched from apisbo.com and cached.',
          )}
        />
              </div>
            ),
          },
          {
            key: 'data',
            label: tr('数据', 'Data'),
            children: (
              <Space
                direction="vertical"
                size="middle"
                style={{ width: '100%' }}
              >
                <Space wrap size="middle">
                  <Typography.Text>{tr('数据保留', 'Data retention')}</Typography.Text>
                  <InputNumber
                    min={0}
                    max={36500}
                    precision={0}
                    addonAfter={tr('天', 'days')}
                    value={retentionDays}
                    onChange={(value) =>
                      setRetentionDays(
                        typeof value === 'number' ? Math.max(0, value) : null,
                      )
                    }
                    style={{ width: 160 }}
                  />
                  <Popconfirm
                    title={tr(
                      '保存数据保留设置？',
                      'Save retention setting?',
                    )}
                    description={tr(
                      '保存后会立即删除超过保留期的数据，此操作不可撤销。0 表示永久保留。',
                      'Saving immediately deletes data older than the retention window. This cannot be undone. 0 keeps data forever.',
                    )}
                    okText={tr('保存', 'Save')}
                    cancelText={tr('取消', 'Cancel')}
                    onConfirm={() => void saveRetention()}
                  >
                    <Button disabled={retentionDays === null}>
                      {tr('保存保留设置', 'Save retention')}
                    </Button>
                  </Popconfirm>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {tr('0 = 永久保留', '0 = keep forever')}
                  </Typography.Text>
                </Space>
                <Alert
                  type="info"
                  showIcon
                  message={tr('数据保留', 'Data retention')}
                  description={tr(
                    '保留天数保存在本地 SQLite 数据库；保存后立即清理超期 span，之后每 6 小时自动执行。删除操作不可撤销。',
                    'The retention window is stored in the local SQLite database. Expired spans are deleted immediately on save and every six hours afterwards. Deletion cannot be undone.',
                  )}
                />
              </Space>
            ),
          },
        ]}
      />
    </Drawer>
  );
}
