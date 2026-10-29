import { useEffect, useState } from 'react';
import {
  Alert,
  App as AntApp,
  Button,
  Drawer,
  Input,
  InputNumber,
  Popconfirm,
  Space,
  Switch,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { ModelPriceRow } from '../types';

interface PriceSettingsDrawerProps {
  open: boolean;
  onClose: () => void;
}

type PeriodKind = 'idle' | 'peak' | 'flat';

interface PriceDisplayRow {
  key: string;
  model: string;
  period: PeriodKind;
  rowSpan: number;
}

const periodLabels: Record<PeriodKind, { text: string; color: string }> = {
  idle: { text: '空闲', color: 'green' },
  peak: { text: '高峰', color: 'orange' },
  flat: { text: '固定', color: 'default' },
};

export function PriceSettingsDrawer({ open, onClose }: PriceSettingsDrawerProps) {
  const { message } = AntApp.useApp();
  const [rows, setRows] = useState<ModelPriceRow[]>([]);
  const [drafts, setDrafts] = useState<Record<string, ModelPriceRow>>({});
  const [loading, setLoading] = useState(false);
  const [newModel, setNewModel] = useState('');

  const load = async () => {
    setLoading(true);
    try {
      const response = await fetch('/api/prices');
      if (response.status === 404) {
        throw new Error('服务端版本过旧，请更新并重启 Token Tracker 服务');
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      setRows((await response.json()) as ModelPriceRow[]);
      setDrafts({});
    } catch (error) {
      message.error(`读取价格失败: ${(error as Error).message}`);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (open) {
      void load();
    }
  }, [open]);

  const draftOf = (row: ModelPriceRow) => drafts[row.model] ?? row;

  const update = (row: ModelPriceRow, patch: Partial<ModelPriceRow>) =>
    setDrafts((current) => ({
      ...current,
      [row.model]: { ...(current[row.model] ?? row), ...patch },
    }));

  const save = async (row: ModelPriceRow) => {
    const draft = draftOf(row);
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
        }),
      });
      if (response.status === 404) {
        throw new Error('服务端版本过旧，请更新并重启 Token Tracker 服务');
      }
      if (!response.ok) {
        throw new Error(await response.text());
      }
      message.success(`已保存 ${row.model}，历史费用已重算`);
      await load();
    } catch (error) {
      message.error(`保存失败: ${(error as Error).message}`);
    }
  };

  const remove = async (row: ModelPriceRow) => {
    try {
      const response = await fetch(
        `/api/prices?model=${encodeURIComponent(row.model)}`,
        { method: 'DELETE' },
      );
      if (response.status === 404) {
        throw new Error('服务端版本过旧，请更新并重启 Token Tracker 服务');
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      message.success(
        row.hasDefault
          ? `已恢复 ${row.model} 的内置价格`
          : `已删除 ${row.model} 的自定义价格`,
      );
      await load();
    } catch (error) {
      message.error(`操作失败: ${(error as Error).message}`);
    }
  };

  const addModel = () => {
    const model = newModel.trim();
    if (!model) {
      return;
    }
    if (rows.some((row) => row.model.toLowerCase() === model.toLowerCase())) {
      message.info('该模型已在列表中');
      return;
    }

    setRows((current) => [
      {
        model,
        customized: false,
        hasDefault: false,
        peakAware: false,
        cachedInput: 0,
        uncachedInput: 0,
        output: 0,
        peakCachedInput: null,
        peakUncachedInput: null,
        peakOutput: null,
      },
      ...current,
    ]);
    setNewModel('');
  };

  const rowByModel = new Map(rows.map((row) => [row.model, row]));

  const displayRows: PriceDisplayRow[] = rows.flatMap((row): PriceDisplayRow[] => {
    const draft = draftOf(row);
    return draft.peakAware
      ? [
          { key: `${row.model}|idle`, model: row.model, period: 'idle', rowSpan: 2 },
          { key: `${row.model}|peak`, model: row.model, period: 'peak', rowSpan: 0 },
        ]
      : [
          { key: `${row.model}|flat`, model: row.model, period: 'flat', rowSpan: 1 },
        ];
  });

  const numberInput = (
    row: ModelPriceRow,
    kind: PeriodKind,
    field: 'cachedInput' | 'uncachedInput' | 'output',
  ) => {
    const draft = draftOf(row);
    const peakField =
      field === 'cachedInput'
        ? 'peakCachedInput'
        : field === 'uncachedInput'
          ? 'peakUncachedInput'
          : 'peakOutput';
    const value =
      kind === 'peak'
        ? ((draft[peakField] ?? draft[field]) as number)
        : (draft[field] as number);

    return (
      <InputNumber
        size="small"
        min={0}
        step={0.01}
        precision={4}
        controls={false}
        value={value}
        onChange={(next) =>
          update(row, {
            [kind === 'peak' ? peakField : field]:
              typeof next === 'number' ? next : 0,
          } as Partial<ModelPriceRow>)
        }
        style={{ width: '100%' }}
      />
    );
  };

  const actionCell = (row: ModelPriceRow) => (
    <Space size={4}>
      <Button size="small" type="primary" onClick={() => void save(row)}>
        保存
      </Button>
      {row.hasDefault ? (
        <Popconfirm
          title="恢复内置价格？"
          description="将删除该模型的自定义价格，并重算历史费用。"
          okText="恢复"
          cancelText="取消"
          disabled={!row.customized}
          onConfirm={() => remove(row)}
        >
          <Button size="small" disabled={!row.customized}>
            恢复默认
          </Button>
        </Popconfirm>
      ) : (
        <Popconfirm
          title="删除该模型的自定义价格？"
          description="删除后该模型将无法估算费用。"
          okText="删除"
          cancelText="取消"
          disabled={!row.customized}
          onConfirm={() => remove(row)}
        >
          <Button size="small" danger disabled={!row.customized}>
            删除
          </Button>
        </Popconfirm>
      )}
    </Space>
  );

  const columns: ColumnsType<PriceDisplayRow> = [
    {
      title: '模型',
      key: 'model',
      width: 230,
      fixed: 'left',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        const statusTag = row.customized ? (
          <Tag color="blue" bordered={false}>自定义</Tag>
        ) : row.hasDefault ? (
          <Tag color="green" bordered={false}>内置</Tag>
        ) : (
          <Tag bordered={false}>未定价</Tag>
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
      title: '区分峰谷',
      key: 'peakAware',
      width: 90,
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
      title: '时段',
      key: 'period',
      width: 100,
      align: 'center',
      render: (_, record) => {
        const label = periodLabels[record.period];
        return <Tag color={label.color}>{label.text}</Tag>;
      },
    },
    {
      title: '缓存命中',
      key: 'cachedInput',
      width: 130,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row ? numberInput(row, record.period, 'cachedInput') : null;
      },
    },
    {
      title: '输入（未命中）',
      key: 'uncachedInput',
      width: 150,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row ? numberInput(row, record.period, 'uncachedInput') : null;
      },
    },
    {
      title: '输出',
      key: 'output',
      width: 130,
      align: 'right',
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row ? numberInput(row, record.period, 'output') : null;
      },
    },
    {
      title: '操作',
      key: 'actions',
      width: 190,
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
      title="模型价格设置（元 / 百万 tokens）"
      width={1120}
      open={open}
      onClose={onClose}
      styles={{
        body: {
          padding: 16,
          display: 'flex',
          flexDirection: 'column',
        },
      }}
      extra={
        <Button onClick={() => void load()} loading={loading}>
          刷新
        </Button>
      }
    >
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 12,
          height: '100%',
        }}
      >
        <Space.Compact style={{ width: 460 }}>
          <Input
            placeholder="新增模型 ID，例如 kimi-k3.1-pro"
            value={newModel}
            onChange={(event) => setNewModel(event.target.value)}
            onPressEnter={addModel}
          />
          <Button type="primary" onClick={addModel}>
            新增
          </Button>
        </Space.Compact>

        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          内置价格可直接修改；新增模型可修改或删除。保存后会立即重算该模型的历史费用并同步刷新看板。
        </Typography.Text>

        <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
          <Table
            size="small"
            bordered
            rowKey="key"
            columns={columns}
            dataSource={displayRows}
            loading={loading}
            pagination={false}
            scroll={{ x: 1020 }}
          />
        </div>

        <Alert
          type="info"
          showIcon
          message="峰谷与节假日规则"
          description="开启「区分峰谷」后，该模型显示空闲/高峰两行价格；未开启时只有一行固定价格。峰谷按北京时间判定：工作日 9:00–12:00、14:00–18:00 为高峰，其余时间为空闲；周末与中国法定节假日全天按空闲（调休上班的周末仍按周末处理）。节假日数据优先读取本地数据库，缺失时自动从 apisbo.com 获取并缓存（内置 2025–2026 年数据兜底）。"
        />
      </div>
    </Drawer>
  );
}
