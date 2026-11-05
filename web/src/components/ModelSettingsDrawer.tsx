import { useEffect, useState } from 'react';
import {
  Alert,
  App as AntApp,
  AutoComplete,
  Button,
  Card,
  Drawer,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Segmented,
  Select,
  Space,
  Switch,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
  Upload,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { ModelSettingsRow, SettingsPayload } from '../types';
import { useUi } from '../ui';
import { useMoney } from '../utils/money';
import { tableHeight } from '../utils/tableHeight';
import { providerOf } from '../utils/model';
import { MODEL_ICON_COLORS, ModelIcon } from './ModelIcon';
import { useModelProfiles } from '../utils/modelProfiles';

const PROVIDER_SUGGESTIONS = [
  'DeepSeek',
  'OpenAI',
  'Anthropic',
  'Google',
  'Meta',
  'Kimi',
  '小米 MiMo',
  'MiniMax',
  '智谱 GLM',
  'Alibaba',
  'xAI',
  'Mistral',
];

interface ModelSettingsDrawerProps {
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

export function ModelSettingsDrawer({ open, onClose }: ModelSettingsDrawerProps) {
  const { message } = AntApp.useApp();
  const money = useMoney();
  const { t } = useUi();
  const { refresh: refreshProfiles } = useModelProfiles();
  const [rows, setRows] = useState<ModelSettingsRow[]>([]);
  const [drafts, setDrafts] = useState<Record<string, ModelSettingsRow>>({});
  const [loading, setLoading] = useState(false);
  const [newModel, setNewModel] = useState('');
  const [retentionDays, setRetentionDays] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<'models' | 'prices' | 'data'>(
    'models',
  );
  const [editor, setEditor] = useState<ModelSettingsRow | null>(null);
  const [editorProvider, setEditorProvider] = useState('');
  const [editorKind, setEditorKind] = useState<'auto' | 'letter' | 'image'>(
    'auto',
  );
  const [editorLetter, setEditorLetter] = useState('');
  const [editorColor, setEditorColor] = useState(MODEL_ICON_COLORS[0]);
  const [editorImage, setEditorImage] = useState<string | null>(null);
  const [editorSaving, setEditorSaving] = useState(false);

  const load = async (keepDrafts = false) => {
    setLoading(true);
    try {
      const response = await fetch('/api/models');
      if (response.status === 404) {
        throw new Error(
          t('the.server.is.too.old.update.and.restart.token.tracker'),
        );
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as ModelSettingsRow[];
      setRows(data);
      if (keepDrafts) {
        setDrafts((current) => {
          const next: Record<string, ModelSettingsRow> = {};
          for (const [model, draft] of Object.entries(current)) {
            if (data.some((item) => item.model === model)) {
              next[model] = draft;
            }
          }
          return next;
        });
      } else {
        setDrafts({});
      }
    } catch (error) {
      message.error(
        t('failed.to.load.prices', { error: (error as Error).message }),
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
        t('retention.setting.saved'),
      );
    } catch (error) {
      message.error(
        t('save.failed', { error: (error as Error).message }),
      );
    }
  };

  const draftOf = (row: ModelSettingsRow) => drafts[row.model] ?? row;

  const update = (row: ModelSettingsRow, patch: Partial<ModelSettingsRow>) =>
    setDrafts((current) => ({
      ...current,
      [row.model]: { ...(current[row.model] ?? row), ...patch },
    }));

  const priceFields = (draft: ModelSettingsRow) => {
    const longEnabled = (draft.longContextThreshold ?? 0) > 0;
    return {
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
          ? (draft.longPeakOutput ??
            draft.longOutput ??
            draft.peakOutput ??
            draft.output)
          : null,
      cacheWrite: draft.cacheWrite ?? draft.uncachedInput,
      peakCacheWrite: draft.peakAware
        ? (draft.peakCacheWrite ??
          draft.cacheWrite ??
          draft.peakUncachedInput ??
          draft.uncachedInput)
        : null,
      longCacheWrite: longEnabled
        ? (draft.longCacheWrite ?? draft.longUncachedInput ?? draft.uncachedInput)
        : null,
      longPeakCacheWrite:
        longEnabled && draft.peakAware
          ? (draft.longPeakCacheWrite ??
            draft.longCacheWrite ??
            draft.longUncachedInput ??
            draft.peakUncachedInput ??
            draft.uncachedInput)
          : null,
    };
  };

  const profileFields = (
    row: ModelSettingsRow,
    patch: Partial<ModelSettingsRow> = {},
  ) => {
    const merged = { ...row, ...patch };
    return {
      provider: merged.provider,
      iconKind: merged.iconKind,
      iconLetter: merged.iconLetter,
      iconColor: merged.iconColor,
      iconImage: merged.iconImage,
    };
  };

  const persist = async (
    payload: Record<string, unknown>,
    successText: string,
  ) => {
    const response = await fetch('/api/models', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
    if (response.status === 404) {
      throw new Error(
        t('the.server.is.too.old.update.and.restart.token.tracker'),
      );
    }
    if (!response.ok) {
      throw new Error(await response.text());
    }

    const saved = (await response.json()) as ModelSettingsRow;
    // Refresh only this model: reloading the whole list would drop the
    // unsaved edits still held for the other rows.
    setRows((current) => {
      const exists = current.some(
        (item) => item.model.toLowerCase() === saved.model.toLowerCase(),
      );
      return exists
        ? current.map((item) =>
            item.model.toLowerCase() === saved.model.toLowerCase()
              ? saved
              : item,
          )
        : [saved, ...current];
    });
    setDrafts((current) => {
      const next = { ...current };
      delete next[saved.model];
      return next;
    });
    await refreshProfiles();
    message.success(successText);
    return saved;
  };

  const save = async (row: ModelSettingsRow) => {
    const draft = draftOf(row);
    try {
      await persist(
        {
          model: draft.model,
          priced: true,
          ...profileFields(draft),
          ...priceFields(draft),
        },
        t('saved.model.recalculated', { model: row.model }),
      );
    } catch (error) {
      message.error(
        t('save.failed', { error: (error as Error).message }),
      );
    }
  };

  const remove = async (row: ModelSettingsRow) => {
    try {
      const response = await fetch(
        `/api/models?model=${encodeURIComponent(row.model)}`,
        { method: 'DELETE' },
      );
      if (response.status === 404) {
        throw new Error(
          t('the.server.is.too.old.update.and.restart.token.tracker'),
        );
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      message.success(
        t('deleted.model.settings', { model: row.model }),
      );
      setEditor((current) =>
        current?.model.toLowerCase() === row.model.toLowerCase()
          ? null
          : current,
      );
      await load(true);
      await refreshProfiles();
    } catch (error) {
      message.error(
        t('operation.failed', { error: (error as Error).message }),
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
          ? t('model.has.calls', { count: existing.calls })
          : t('model.is.already.in.the.list'),
      );
      return;
    }

    setRows((current) => [
      {
        model,
        priced: false,
        hasProfile: false,
        calls: 0,
        provider: null,
        iconKind: null,
        iconLetter: null,
        iconColor: null,
        iconImage: null,
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

  const openEditor = (row: ModelSettingsRow) => {
    setEditor(row);
    setEditorProvider(row.provider ?? '');
    setEditorKind(
      row.iconKind === 'image' && row.iconImage
        ? 'image'
        : row.iconKind === 'letter' || row.iconLetter
          ? 'letter'
          : 'auto',
    );
    setEditorLetter(row.iconLetter ?? '');
    setEditorColor(row.iconColor ?? MODEL_ICON_COLORS[0]);
    setEditorImage(row.iconImage ?? null);
  };

  const editedProfile = (): Partial<ModelSettingsRow> => ({
    provider: editorProvider.trim() || null,
    iconKind: editorKind === 'auto' ? null : editorKind,
    iconLetter:
      editorKind === 'letter' ? editorLetter.trim().slice(0, 2) || null : null,
    iconColor: editorKind === 'letter' ? editorColor : null,
    iconImage: editorKind === 'image' ? editorImage : null,
  });

  const saveEditor = async () => {
    if (!editor) {
      return;
    }

    if (editorKind === 'letter' && !editorLetter.trim()) {
      message.warning(t('enter.one.character'));
      return;
    }
    if (editorKind === 'image' && !editorImage) {
      message.warning(t('upload.an.icon.image'));
      return;
    }

    const draft = draftOf(editor);
    setEditorSaving(true);
    try {
      await persist(
        {
          model: draft.model,
          priced: draft.priced,
          ...profileFields(draft, editedProfile()),
          ...priceFields(draft),
        },
        t('model.info.saved'),
      );
      setEditor(null);
    } catch (error) {
      message.error(
        t('save.failed', { error: (error as Error).message }),
      );
    } finally {
      setEditorSaving(false);
    }
  };

  // Uploaded icons are rasterised to a fixed square so the database never holds
  // a multi-megabyte image and every view gets a crisp 64px source.
  const readIconFile = async (file: File): Promise<string> => {
    if (file.size > 4 * 1024 * 1024) {
      throw new Error(t('image.must.be.under.4.mb'));
    }

    const bitmap = await createImageBitmap(file);
    try {
      const size = 64;
      const canvas = document.createElement('canvas');
      canvas.width = size;
      canvas.height = size;
      const context = canvas.getContext('2d');
      if (!context) {
        throw new Error(t('cannot.process.the.image'));
      }

      const scale = Math.min(size / bitmap.width, size / bitmap.height);
      const width = bitmap.width * scale;
      const height = bitmap.height * scale;
      context.drawImage(
        bitmap,
        (size - width) / 2,
        (size - height) / 2,
        width,
        height,
      );
      return canvas.toDataURL('image/png');
    } finally {
      bitmap.close();
    }
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
  ): keyof ModelSettingsRow => {
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

  const toggleLongContext = (row: ModelSettingsRow, enabled: boolean) => {
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
    row: ModelSettingsRow,
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
        inputMode="decimal"
        value={money.toPriceInput(cnyValue)}
        onChange={(next) =>
          update(row, {
            [kind === 'peak' ? peakField : baseField]:
              money.fromPriceInput(typeof next === 'number' ? next : 0),
          } as Partial<ModelSettingsRow>)
        }
        style={{ width: '100%' }}
      />
    );
  };

  const actionCell = (row: ModelSettingsRow) => (
    <Space size={0}>
      <Button size="small" type="link" onClick={() => void save(row)}>
        {t('save')}
      </Button>
      {row.priced && (
        <Popconfirm
          title={t('delete.this.custom.price')}
          description={t('this.model.will.no.longer.have.estimated.costs')}
          okText={t('delete')}
          cancelText={t('cancel')}
          onConfirm={() => remove(row)}
        >
          <Button size="small" type="link" danger>
            {t('delete')}
          </Button>
        </Popconfirm>
      )}
    </Space>
  );

  const columns: ColumnsType<PriceDisplayRow> = [
    {
      title: t('model'),
      key: 'model',
      width: 190,
      fixed: 'left',
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        if (!row) {
          return null;
        }

        const statusTag = row.priced ? (
          <Tag color="blue">{t('custom')}</Tag>
        ) : (
          <Tag>{t('unpriced')}</Tag>
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
      title: t('peak.aware'),
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
      title: t('tiered'),
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
      title: t('threshold.tokens'),
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
            inputMode="numeric"
            disabled={!enabled}
            placeholder="272000"
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
      title: t('tier'),
      key: 'segment',
      width: 78,
      align: 'center',
      onCell: (record) => ({ rowSpan: record.segmentRowSpan }),
      render: (_, record) =>
        record.segment === 'long' ? (
          <Tag color="purple">{t('long.context')}</Tag>
        ) : (
          <Tag>{t('default')}</Tag>
        ),
    },
    {
      title: t('period'),
      key: 'period',
      width: 76,
      align: 'center',
      render: (_, record) => {
        if (record.period === 'idle') {
          return <Tag color="green">{t('idle')}</Tag>;
        }
        if (record.period === 'peak') {
          return <Tag color="orange">{t('peak')}</Tag>;
        }
        return <Tag>{t('flat')}</Tag>;
      },
    },
    {
      title: t('cached.input'),
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
      title: t('cache.write'),
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
      title: t('uncached.input'),
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
      title: t('output'),
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
      title: t('actions'),
      key: 'actions',
      width: 166,
      onCell: (record) => ({ rowSpan: record.rowSpan }),
      render: (_, record) => {
        const row = rowByModel.get(record.model);
        return row ? actionCell(row) : null;
      },
    },
  ];

  const priceColumn = (
    labelKey: Parameters<typeof t>[0],
    field: 'uncachedInput' | 'cachedInput' | 'cacheWrite' | 'output',
  ): ColumnsType<ModelSettingsRow>[number] => ({
    title: t(labelKey),
    key: field,
    width: field === 'uncachedInput' ? 112 : 100,
    align: 'right',
    render: (_, row) =>
      row.priced
        ? money.toPriceInput(row[field] ?? 0).toFixed(money.pricePrecision)
        : '-',
  });

  const modelColumns: ColumnsType<ModelSettingsRow> = [
    {
      title: t('icon'),
      key: 'icon',
      width: 64,
      align: 'center',
      render: (_, row) => <ModelIcon model={row.model} size={24} />,
    },
    {
      title: t('model.id'),
      key: 'model',
      width: 280,
      render: (_, row) => (
        <Space size={6} wrap={false} style={{ maxWidth: '100%' }}>
          <Typography.Text
            strong
            ellipsis={{ tooltip: row.model }}
            style={{ maxWidth: 200 }}
          >
            {row.model}
          </Typography.Text>
          {row.priced ? (
            <Tag color="blue">{t('priced')}</Tag>
          ) : (
            <Tag>{t('unpriced')}</Tag>
          )}
        </Space>
      ),
    },
    {
      title: t('provider'),
      key: 'provider',
      width: 180,
      render: (_, row) => (
        <Space size={6}>
          <span>{providerOf(row.model, row.provider)}</span>
          {row.provider && <Tag>{t('custom')}</Tag>}
        </Space>
      ),
    },
    {
      title: t('calls'),
      key: 'calls',
      width: 84,
      align: 'right',
      render: (_, row) => Math.round(row.calls).toLocaleString(),
    },
    {
      title: (
        <Tooltip title={t('base.price.hint')}>
          <span>{t('price.per.million')}</span>
        </Tooltip>
      ),
      key: 'price',
      children: [
        priceColumn('uncached.input', 'uncachedInput'),
        priceColumn('cached.input', 'cachedInput'),
        priceColumn('cache.write', 'cacheWrite'),
        priceColumn('output', 'output'),
      ],
    },
    {
      title: t('actions'),
      key: 'actions',
      width: 150,
      render: (_, row) => (
        <Space size={0}>
          <Button size="small" type="link" onClick={() => openEditor(row)}>
            {t('edit')}
          </Button>
          {row.hasProfile || row.priced ? (
            <Popconfirm
              title={t('delete.this.model.settings.and.pricing')}
              description={t('the.model.becomes.unpriced.and.historical.costs.can.no')}
              okText={t('delete')}
              cancelText={t('cancel')}
              onConfirm={() => void remove(row)}
            >
              <Button size="small" type="link" danger>
                {t('delete')}
              </Button>
            </Popconfirm>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Drawer
      title={t('model.settings')}
      width="min(1400px, 96vw)"
      open={open}
      onClose={onClose}
      styles={{ body: { padding: 16 } }}
      extra={
        <Button onClick={() => void load(true)} loading={loading}>
          {t('refresh')}
        </Button>
      }
    >
      <Tabs
        activeKey={activeTab}
        onChange={(key) =>
          setActiveTab(key as 'models' | 'prices' | 'data')
        }
        items={[
          {
            key: 'models',
            label: t('models'),
            children: (
              <div
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 12,
                }}
              >
                <Space wrap size="middle">
                  <Space.Compact style={{ width: 380 }}>
                    <Input
                      placeholder={t('model.id.e.g.kimi.k3.1.pro')}
                      value={newModel}
                      onChange={(event) => setNewModel(event.target.value)}
                      onPressEnter={addModel}
                    />
                    <Button type="primary" onClick={addModel}>
                      {t('add.model')}
                    </Button>
                  </Space.Compact>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t('the.model.id.is.unique.provider.and.icon.only.affect')}
                  </Typography.Text>
                </Space>
                <Card
                  size="small"
                  title={`${t('models')}(${rows.length})`}
                  styles={{ body: { padding: 0 } }}
                >
                  <Table
                    size="small"
                    rowKey="model"
                    columns={modelColumns}
                    dataSource={[...rows].sort((a, b) =>
                      a.model.localeCompare(b.model),
                    )}
                    loading={loading}
                    pagination={false}
                    scroll={{ x: 1170, y: tableHeight(400) }}
                  />
                </Card>
              </div>
            ),
          },
          {
            key: 'prices',
            label: t('pricing'),
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
                    <Typography.Text>{t('cost.unit')}</Typography.Text>
                    <Select
                      value={money.settings.unit}
                      onChange={(unit) => money.setSettings({ unit })}
                      style={{ width: 150 }}
                      options={[
                        { value: 'cny', label: t('cny') },
                        { value: 'usd', label: t('usd') },
                        { value: 'credit', label: t('credits') },
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
                      addonBefore={t('1.credit')}
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
                  {t('the.unit.applies.to.dashboard.values.and.price.inputs')}
                </Typography.Text>

                <Card
                  size="small"
                  title={`${t('model.prices')}(${rows.length})`}
                  extra={
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {t('set.a.price.for.an.unpriced.model.and.save.to')}
                    </Typography.Text>
                  }
                  styles={{ body: { padding: 0 } }}
                >
                  <Table
                    className="tt-price-table"
                    size="small"
                    rowKey="key"
                    columns={columns}
                    dataSource={displayRows}
                    loading={loading}
                    pagination={false}
                    scroll={{ x: 1230, y: tableHeight(340) }}
                  />
                </Card>

        <Alert
          type="info"
          showIcon
          message={t('peak.and.holiday.rules')}
          description={t('when.peak.aware.is.enabled.two.rows.are.shown.idle.peak')}
        />
              </div>
            ),
          },
          {
            key: 'data',
            label: t('data'),
            children: (
              <Space
                direction="vertical"
                size="middle"
                style={{ width: '100%' }}
              >
                <Space wrap size="middle">
                  <Typography.Text>{t('data.retention')}</Typography.Text>
                  <InputNumber
                    min={0}
                    max={36500}
                    precision={0}
                    addonAfter={t('days')}
                    value={retentionDays}
                    onChange={(value) =>
                      setRetentionDays(
                        typeof value === 'number' ? Math.max(0, value) : null,
                      )
                    }
                    style={{ width: 160 }}
                  />
                  <Popconfirm
                    title={t('save.retention.setting')}
                    description={t('saving.immediately.deletes.data.older.than.the')}
                    okText={t('save')}
                    cancelText={t('cancel')}
                    onConfirm={() => void saveRetention()}
                  >
                    <Button disabled={retentionDays === null}>
                      {t('save.retention')}
                    </Button>
                  </Popconfirm>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t('0.keep.forever')}
                  </Typography.Text>
                </Space>
                <Alert
                  type="info"
                  showIcon
                  message={t('data.retention')}
                  description={t('the.retention.window.is.stored.in.the.local.sqlite')}
                />
              </Space>
            ),
          },
        ]}
      />

      <Modal
        title={editor ? t('model.settings.model', { model: editor.model }) : ''}
        open={editor !== null}
        onCancel={() => setEditor(null)}
        onOk={() => void saveEditor()}
        okText={t('save')}
        cancelText={t('cancel')}
        confirmLoading={editorSaving}
        width={640}
        destroyOnClose
      >
        {editor && (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              <Typography.Text type="secondary">
                {t('model.id.unique')}
              </Typography.Text>
              <Typography.Text code copyable>
                {editor.model}
              </Typography.Text>
            </Space>

            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              <Typography.Text type="secondary">
                {t('provider')}
              </Typography.Text>
              <AutoComplete
                value={editorProvider}
                onChange={setEditorProvider}
                options={PROVIDER_SUGGESTIONS.map((value) => ({ value }))}
                placeholder={t('leave.empty.to.infer.from.the.model.name')}
                style={{ width: '100%' }}
                allowClear
              />
            </Space>

            <Space direction="vertical" size={6} style={{ width: '100%' }}>
              <Typography.Text type="secondary">
                {t('icon')}
              </Typography.Text>
              <Space size="middle" align="start">
                <div style={{ paddingTop: 2 }}>
                  <ModelIcon
                    model={editor.model}
                    profile={{
                      model: editor.model,
                      ...editedProfile(),
                      provider: editedProfile().provider ?? null,
                      iconKind: editedProfile().iconKind ?? null,
                      iconLetter: editedProfile().iconLetter ?? null,
                      iconColor: editedProfile().iconColor ?? null,
                      iconImage: editedProfile().iconImage ?? null,
                    }}
                    size={40}
                    tooltip={false}
                  />
                </div>
                <Space direction="vertical" size={8} style={{ flex: 1 }}>
                  <Segmented
                    value={editorKind}
                    onChange={(value) =>
                      setEditorKind(value as 'auto' | 'letter' | 'image')
                    }
                    options={[
                      { value: 'auto', label: t('default') },
                      { value: 'letter', label: t('character') },
                      { value: 'image', label: t('image') },
                    ]}
                  />
                  {editorKind === 'auto' && (
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {t('color.and.initial.letter.are.derived.from.the.provider')}
                    </Typography.Text>
                  )}
                  {editorKind === 'letter' && (
                    <Space direction="vertical" size={6}>
                      <Space size="middle" wrap>
                        <Input
                          value={editorLetter}
                          onChange={(event) =>
                            setEditorLetter(event.target.value.slice(0, 2))
                          }
                          maxLength={2}
                          placeholder={t('a')}
                          style={{ width: 90 }}
                        />
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          {t('1.2.characters')}
                        </Typography.Text>
                      </Space>
                      <Space size={6} wrap>
                        {MODEL_ICON_COLORS.map((color) => (
                          <button
                            key={color}
                            type="button"
                            aria-label={color}
                            title={color}
                            onClick={() => setEditorColor(color)}
                            style={{
                              width: 24,
                              height: 24,
                              padding: 0,
                              cursor: 'pointer',
                              borderRadius: 6,
                              background: color,
                              border:
                                color.toLowerCase() === editorColor.toLowerCase()
                                  ? '2px solid var(--ant-color-text)'
                                  : '1px solid var(--ant-color-border)',
                            }}
                          />
                        ))}
                        <Tooltip title={t('custom.color')}>
                          <input
                            type="color"
                            aria-label={t('custom.color')}
                            value={editorColor}
                            onChange={(event) =>
                              setEditorColor(event.target.value)
                            }
                            style={{
                              width: 32,
                              height: 24,
                              padding: 0,
                              border: '1px solid var(--ant-color-border)',
                              borderRadius: 6,
                              background: 'transparent',
                              cursor: 'pointer',
                            }}
                          />
                        </Tooltip>
                        <Typography.Text
                          type="secondary"
                          style={{ fontSize: 12 }}
                        >
                          {t('custom')}
                        </Typography.Text>
                      </Space>
                    </Space>
                  )}
                  {editorKind === 'image' && (
                    <Space size="middle" align="center" wrap>
                      <Upload
                        accept="image/png,image/jpeg,image/webp"
                        showUploadList={false}
                        beforeUpload={(file) => {
                          void (async () => {
                            try {
                              setEditorImage(await readIconFile(file));
                            } catch (error) {
                              message.error((error as Error).message);
                            }
                          })();
                          return false;
                        }}
                      >
                        <Button>{t('choose.image')}</Button>
                      </Upload>
                      {editorImage && (
                        <Button
                          type="link"
                          danger
                          onClick={() => setEditorImage(null)}
                        >
                          {t('remove')}
                        </Button>
                      )}
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {t('automatically.cropped.to.a.64.64.png')}
                      </Typography.Text>
                    </Space>
                  )}
                </Space>
              </Space>
            </Space>

            <Alert
              type="info"
              showIcon
              message={t('pricing')}
              description={
                editor.priced
                  ? t('this.model.is.priced.adjust.prices.and.peak.tier.rules')
                  : t('this.model.is.not.priced.yet.set.prices.on.the.pricing')
              }
            />
          </Space>
        )}
      </Modal>
    </Drawer>
  );
}
