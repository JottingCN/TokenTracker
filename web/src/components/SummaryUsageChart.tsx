import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Card, Empty, Select, Space, Spin, theme, Typography } from 'antd';
import type {
  Summary,
  SummaryChartBucket,
  SummaryChartPayload,
} from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import { formatMoney, useMoney } from '../utils/money';

type Grouping = 'hour' | 'day' | 'week' | 'month';
type ChartType = 'bar' | 'pie';
type Metric = 'tokens' | 'cost';

const DAY_MS = 86_400_000;

// The span decides the bucket size: a single day stays readable per hour, while
// quarter-long ranges would produce thousands of daily columns. The dashboard
// range selector only offers fixed windows, so the mapping is stable per range.
function groupingForSpan(spanMs: number): Grouping {
  if (spanMs <= DAY_MS) {
    return 'hour';
  }
  if (spanMs > 90 * DAY_MS) {
    return 'month';
  }
  if (spanMs > 30 * DAY_MS) {
    return 'week';
  }
  return 'day';
}

function resolveGrouping(
  range: number,
  fromMs?: number,
  toMs?: number,
): Grouping {
  if (fromMs !== undefined && toMs !== undefined && toMs > fromMs) {
    return groupingForSpan(toMs - fromMs);
  }
  // RangeTime: 0 today, 1 yesterday, 2 last 7 days, 3 last 30 days, 4 all time.
  if (range <= 1) {
    return 'hour';
  }
  if (range >= 4) {
    return 'month';
  }
  return 'day';
}

interface SummaryUsageChartProps {
  summary: Summary | null;
  range: number;
  fromMs?: number;
  toMs?: number;
  model?: string;
  sessionId?: string;
}

export function SummaryUsageChart({
  summary,
  range,
  fromMs,
  toMs,
  model,
  sessionId,
}: SummaryUsageChartProps) {
  const { locale, t } = useUi();
  const { token } = theme.useToken();
  useMoney();
  const groupBy = resolveGrouping(range, fromMs, toMs);
  const [chartType, setChartType] = useState<ChartType>('bar');
  const [metric, setMetric] = useState<Metric>('tokens');
  // The payload is tagged with the grouping that produced it: rendering the old
  // keys under a new grouping would format "2026-11-19" as an hour bucket.
  const [chart, setChart] = useState<{
    groupBy: Grouping;
    data: SummaryChartPayload;
  } | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const payload = chart?.groupBy === groupBy ? chart.data : null;
  const number = useMemo(
    () => new Intl.NumberFormat(intlLocale(locale)),
    [locale],
  );
  const refreshKey = summary
    ? `${summary.calls}:${summary.inputTokens}:${summary.outputTokens}:${summary.costCny}`
    : '';

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      setLoading(true);
      setFailed(false);
      setChart(null);
      const params = new URLSearchParams({
        groupBy,
        range: String(range),
      });
      if (fromMs !== undefined && toMs !== undefined) {
        params.set('from', String(fromMs));
        params.set('to', String(toMs));
      }
      if (model) {
        params.set('model', model);
      }
      if (sessionId) {
        params.set('sessionId', sessionId);
      }

      try {
        const response = await fetch(`/api/summary-chart?${params}`, {
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const data = (await response.json()) as SummaryChartPayload;
        setChart({ groupBy, data });
      } catch {
        if (!controller.signal.aborted) {
          setFailed(true);
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => controller.abort();
  }, [fromMs, groupBy, model, range, refreshKey, sessionId, toMs]);

  const buckets = useMemo(() => {
    const source = payload?.buckets ?? [];
    if (groupBy === 'hour') {
      const byKey = new Map(source.map((bucket) => [bucket.key, bucket]));
      return Array.from({ length: 24 }, (_, hour) => {
        const key = String(hour).padStart(2, '0');
        return byKey.get(key) ?? EmptyBucket(key);
      });
    }

    return [...source].sort((a, b) => a.key.localeCompare(b.key));
  }, [groupBy, payload]);

  // Four non-overlapping slices: the stored input already includes cache reads
  // and writes, and reasoning is part of output, so the totals must be split
  // before drawing or the chart would double count them.
  const series: SeriesDef[] = [
    { key: 'input', label: t('input'), color: SERIES_COLORS[0] },
    { key: 'cache', label: t('cached'), color: SERIES_COLORS[1] },
    { key: 'output', label: t('output'), color: SERIES_COLORS[2] },
    { key: 'reasoning', label: t('reasoning'), color: SERIES_COLORS[3] },
  ];
  const chartOptions = [
    { value: 'bar' as const, label: t('chart.type.bar') },
    { value: 'pie' as const, label: t('chart.type.pie') },
  ];
  const metricOptions = [
    { value: 'tokens' as const, label: t('total.tokens') },
    { value: 'cost' as const, label: t('estimated.cost') },
  ];

  const formatValue = (value: number) =>
    metric === 'tokens' ? number.format(value) : formatMoney(value);
  const compactNumber = useMemo(
    () =>
      new Intl.NumberFormat(intlLocale(locale), {
        notation: 'compact',
        maximumFractionDigits: 1,
      }),
    [locale],
  );
  // Axis ticks must fit in a narrow gutter and follow the selected metric, so a
  // six-digit token count never renders as a truncated currency label.
  const formatAxisValue = (value: number, max: number) => {
    if (metric === 'tokens') {
      return compactNumber.format(value);
    }

    // Digits follow the axis maximum so all ticks keep the same precision.
    const digits = max >= 1 ? 2 : max >= 0.01 ? 3 : 4;
    return formatMoney(value, digits);
  };
  const totals = series.map((item) =>
    buckets.reduce(
      (sum, bucket) => sum + bucketSeriesValue(bucket, metric, item.key),
      0,
    ),
  );
  const unpricedCalls = buckets.reduce(
    (total, bucket) => total + bucket.unpricedCalls,
    0,
  );
  const hasData = (payload?.buckets.length ?? 0) > 0;
  // Below this width the two selects no longer fit next to the card title, so
  // they move into the body instead of squeezing the title away.
  const [cardRef, cardWidth] = useChartWidth();
  const stackControls = cardWidth > 0 && cardWidth < 620;
  const controls = (
    <Space size={8} wrap>
      <Select<ChartType>
        aria-label={t('chart.type')}
        size="small"
        value={chartType}
        onChange={setChartType}
        options={chartOptions}
        style={{ width: 104 }}
      />
      <Select<Metric>
        aria-label={t('chart.dimension')}
        size="small"
        value={metric}
        onChange={setMetric}
        options={metricOptions}
        style={{ width: 120 }}
      />
    </Space>
  );

  return (
    <div ref={cardRef}>
      <Card
        size="small"
        title={t('usage.chart')}
        extra={stackControls ? undefined : controls}
      >
        {stackControls && <div style={{ marginBottom: 12 }}>{controls}</div>}
        {failed ? (
        <Alert type="error" showIcon message={t('chart.load.failed')} />
      ) : loading || payload === null ? (
        <div style={{ display: 'flex', justifyContent: 'center', padding: 32 }}>
          <Spin />
        </div>
      ) : !hasData ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={t('chart.no.data')}
        />
      ) : (
        <>
          {chartType === 'pie' ? (
            <UsagePie
              series={series}
              values={totals}
              formatValue={formatValue}
              emptyColor={token.colorFillSecondary}
              centerLabel={
                metric === 'tokens' ? t('total.tokens') : t('estimated.cost')
              }
            />
          ) : (
            <UsageBars
              buckets={buckets}
              metric={metric}
              series={series}
              totals={totals}
              formatValue={formatValue}
              formatAxis={formatAxisValue}
              groupBy={groupBy}
              locale={intlLocale(locale)}
              gridColor={token.colorBorderSecondary}
              textColor={token.colorTextSecondary}
            />
          )}
          <Typography.Text
            type="secondary"
            style={{ display: 'block', marginTop: 8, fontSize: 11 }}
          >
            {t('chart.breakdown.note')}
          </Typography.Text>
          {metric === 'cost' && unpricedCalls > 0 && (
            <Typography.Text
              type="secondary"
              style={{ display: 'block', marginTop: 2, fontSize: 11 }}
            >
              {t('chart.unpriced.calls', {
                count: number.format(unpricedCalls),
              })}
            </Typography.Text>
          )}
        </>
      )}
      </Card>
    </div>
  );
}

export const SERIES_COLORS = ['#1677ff', '#fa8c16', '#52c41a', '#722ed1'];
const SERIES_KEYS = ['input', 'cache', 'output', 'reasoning'] as const;
type SeriesKey = (typeof SERIES_KEYS)[number];

interface SeriesDef {
  key: SeriesKey;
  label: string;
  color: string;
}

function EmptyBucket(key: string): SummaryChartBucket {
  return {
    key,
    inputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    outputTokens: 0,
    reasoningTokens: 0,
    uncachedCostCny: 0,
    cacheReadCostCny: 0,
    cacheWriteCostCny: 0,
    outputCostCny: 0,
    reasoningCostCny: 0,
    unpricedCalls: 0,
  };
}

function bucketSeriesValue(
  bucket: SummaryChartBucket,
  metric: Metric,
  key: SeriesKey,
): number {
  // The dev server can proxy to an older service that predates the cache and
  // reasoning fields, so missing values must degrade to zero instead of NaN.
  const inputTokens = bucket.inputTokens ?? 0;
  const outputTokens = bucket.outputTokens ?? 0;
  const cache = Math.min(
    inputTokens,
    Math.max(0, bucket.cacheReadTokens ?? 0) +
      Math.max(0, bucket.cacheCreationTokens ?? 0),
  );
  const reasoning = Math.min(
    Math.max(0, bucket.reasoningTokens ?? 0),
    Math.max(0, outputTokens),
  );

  if (metric === 'tokens') {
    switch (key) {
      case 'input':
        return Math.max(0, inputTokens - cache);
      case 'cache':
        return cache;
      case 'output':
        return Math.max(0, outputTokens - reasoning);
      default:
        return reasoning;
    }
  }

  const outputCost = Math.max(0, bucket.outputCostCny ?? 0);
  const reasoningCost = Math.min(
    Math.max(0, bucket.reasoningCostCny ?? 0),
    outputCost,
  );
  switch (key) {
    case 'input':
      return Math.max(0, bucket.uncachedCostCny ?? 0);
    case 'cache':
      return Math.max(
        0,
        (bucket.cacheReadCostCny ?? 0) + (bucket.cacheWriteCostCny ?? 0),
      );
    case 'output':
      return Math.max(0, outputCost - reasoningCost);
    default:
      return reasoningCost;
  }
}

function UsagePie({
  series,
  values,
  formatValue,
  emptyColor,
  centerLabel,
}: {
  series: SeriesDef[];
  values: number[];
  formatValue: (value: number) => string;
  emptyColor: string;
  centerLabel: string;
}) {
  const { token } = theme.useToken();
  const total = values.reduce((sum, value) => sum + value, 0);
  let offset = 0;
  const stops = series.map((item, index) => {
    const start = total > 0 ? (offset / total) * 100 : 0;
    offset += values[index];
    const end = total > 0 ? (offset / total) * 100 : 0;
    return `${item.color} ${start}% ${end}%`;
  });
  const background =
    total > 0 ? `conic-gradient(${stops.join(', ')})` : emptyColor;

  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexWrap: 'wrap',
        gap: 32,
        padding: '12px 0',
      }}
    >
      <div
        role="img"
        aria-label={series
          .map((item, index) => `${item.label}: ${formatValue(values[index])}`)
          .join('; ')}
        style={{
          width: 180,
          height: 180,
          borderRadius: '50%',
          background,
          display: 'grid',
          placeItems: 'center',
        }}
      >
        <div
          style={{
            width: 110,
            height: 110,
            borderRadius: '50%',
            background: token.colorBgContainer,
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'center',
            alignItems: 'center',
            textAlign: 'center',
            padding: 8,
          }}
        >
          <Typography.Text type="secondary" style={{ fontSize: 11 }}>
            {centerLabel}
          </Typography.Text>
          <Typography.Text strong style={{ fontVariantNumeric: 'tabular-nums' }}>
            {formatValue(total)}
          </Typography.Text>
        </div>
      </div>
      <SeriesLegend
        series={series}
        values={values}
        formatValue={formatValue}
        minWidth={170}
      />
    </div>
  );
}

function niceMax(value: number): number {
  if (!Number.isFinite(value) || value <= 0) {
    return 1;
  }

  const exponent = Math.floor(Math.log10(value));
  const magnitude = Math.pow(10, exponent);
  const normalized = value / magnitude;
  const step = normalized <= 1
    ? 1
    : normalized <= 2
      ? 2
      : normalized <= 5
        ? 5
        : 10;
  return step * magnitude;
}

function UsageBars({
  buckets,
  metric,
  series,
  totals,
  formatValue,
  formatAxis,
  groupBy,
  locale,
  gridColor,
  textColor,
}: {
  buckets: SummaryChartBucket[];
  metric: Metric;
  series: SeriesDef[];
  totals: number[];
  formatValue: (value: number) => string;
  formatAxis: (value: number, max: number) => string;
  groupBy: Grouping;
  locale: string;
  gridColor: string;
  textColor: string;
}) {
  const axisWidth = 46;
  const top = 8;
  const plotHeight = 180;
  const labelHeight = 32;
  const gutterRight = 8;
  const [chartRef, available] = useChartWidth();
  const plotWidth = Math.max(0, available - axisWidth - gutterRight);
  const minGroupWidth = 28;
  const groupWidth = Math.max(minGroupWidth, plotWidth / Math.max(1, buckets.length));
  const width = axisWidth + gutterRight + groupWidth * buckets.length;
  const height = top + plotHeight + labelHeight;
  const bucketValues = (bucket: SummaryChartBucket) =>
    series.map((item) => bucketSeriesValue(bucket, metric, item.key));
  const bucketTotals = buckets.map((bucket) =>
    bucketValues(bucket).reduce((sum, value) => sum + value, 0),
  );
  // The scale is recomputed per metric, so switching to cost rescales the axis
  // instead of reusing the token magnitude.
  const axisMax = niceMax(Math.max(0, ...bucketTotals));
  const labelStep = Math.max(1, Math.ceil(buckets.length / 12));
  const clipPrefix = `tt-bar-${useId().replace(/[\W_]/g, '')}`;
  const formatBucketLabel = (key: string) => {
    if (groupBy === 'hour') {
      return `${key}:00`;
    }
    if (groupBy === 'month') {
      const [year, month] = key.split('-').map(Number);
      const date = new Date(Date.UTC(year, month - 1, 1));
      if (Number.isNaN(date.getTime())) {
        return key;
      }

      return new Intl.DateTimeFormat(locale, {
        year: '2-digit',
        month: 'short',
        timeZone: 'UTC',
      }).format(date);
    }

    const [year, month, day] = key.split('-').map(Number);
    const date = new Date(Date.UTC(year, month - 1, day));
    if (Number.isNaN(date.getTime())) {
      return key;
    }

    return new Intl.DateTimeFormat(locale, {
      month: 'numeric',
      day: 'numeric',
      timeZone: 'UTC',
    }).format(date);
  };
  const gridLines = [0, 0.5, 1].map((fraction) => ({
    fraction,
    y: top + plotHeight * fraction,
    value: axisMax * (1 - fraction),
  }));

  return (
    <div>
      <div ref={chartRef} style={{ overflowX: 'auto' }}>
        <svg
          role="img"
          aria-label={`${series
            .map((item) => item.label)
            .join(', ')} by ${groupBy}`}
          viewBox={`0 0 ${width} ${height}`}
          style={{
            display: 'block',
            width: Math.max(available, width),
            maxWidth: 'none',
          }}
        >
          {gridLines.map((line) => (
            <g key={line.fraction}>
              <line
                x1={axisWidth}
                x2={width - gutterRight}
                y1={line.y}
                y2={line.y}
                stroke={gridColor}
                strokeDasharray={line.fraction === 1 ? undefined : '3 4'}
              />
              <text
                x={axisWidth - 6}
                y={line.y + 3}
                textAnchor="end"
                fill={textColor}
                fontSize="10"
              >
                {formatAxis(line.value, axisMax)}
              </text>
            </g>
          ))}
          {buckets.map((bucket, index) => {
            const values = bucketValues(bucket);
            const groupX = axisWidth + gutterRight + index * groupWidth;
            const barWidth = Math.max(6, Math.min(26, groupWidth * 0.56));
            const center = groupX + groupWidth / 2;
            const tick =
              index % labelStep === 0 || index === buckets.length - 1;
            const label = formatBucketLabel(bucket.key);
            const total = bucketTotals[index];
            const totalHeight = (total / axisMax) * plotHeight;
            const barX = center - barWidth / 2;
            const barY = top + plotHeight - totalHeight;
            const radius = Math.min(3, barWidth / 2, totalHeight / 2);
            // One clip per bar keeps the stack looking like a single column:
            // only the top corners are rounded, the segments stay square.
            let stacked = 0;

            return (
              <g key={bucket.key}>
                {total > 0 && (
                  <defs>
                    <clipPath id={`${clipPrefix}-${index}`}>
                      <path
                        d={
                          `M${barX},${barY + totalHeight} ` +
                          `L${barX},${barY + radius} ` +
                          `Q${barX},${barY} ${barX + radius},${barY} ` +
                          `L${barX + barWidth - radius},${barY} ` +
                          `Q${barX + barWidth},${barY} ${barX + barWidth},${barY + radius} ` +
                          `L${barX + barWidth},${barY + totalHeight} Z`
                        }
                      />
                    </clipPath>
                  </defs>
                )}
                <rect
                  x={barX}
                  y={top}
                  width={barWidth}
                  height={plotHeight}
                  fill="transparent"
                >
                  <title>{`${label} · ${formatValue(total)}`}</title>
                </rect>
                <g
                  clipPath={
                    total > 0 ? `url(#${clipPrefix}-${index})` : undefined
                  }
                >
                  {series.map((item, seriesIndex) => {
                    const value = values[seriesIndex];
                    if (value <= 0) {
                      return null;
                    }

                    const barHeight = (value / axisMax) * plotHeight;
                    const y = top + plotHeight - stacked - barHeight;
                    stacked += barHeight;
                    return (
                      <rect
                        key={item.key}
                        x={barX}
                        y={y}
                        width={barWidth}
                        height={Math.max(barHeight, 1)}
                        fill={item.color}
                      >
                        <title>{`${label} · ${item.label}: ${formatValue(value)}`}</title>
                      </rect>
                    );
                  })}
                </g>
                {tick && (
                  <text
                    x={center}
                    y={top + plotHeight + 18}
                    textAnchor="middle"
                    fill={textColor}
                    fontSize="10"
                  >
                    {label}
                  </text>
                )}
              </g>
            );
          })}
        </svg>
      </div>
      <SeriesLegend
        series={series}
        values={totals}
        formatValue={formatValue}
      />
    </div>
  );
}

function SeriesLegend({
  series,
  values,
  formatValue,
  minWidth,
}: {
  series: SeriesDef[];
  values: number[];
  formatValue: (value: number) => string;
  minWidth?: number;
}) {
  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
        gap: '8px 16px',
        marginTop: 8,
        minWidth,
      }}
    >
      {series.map((item, index) => (
        <LegendItem
          key={item.key}
          label={item.label}
          value={formatValue(values[index] ?? 0)}
          color={item.color}
        />
      ))}
    </div>
  );
}

function useChartWidth() {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) {
      return;
    }

    const update = () => setWidth(element.clientWidth);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return [ref, width] as const;
}

function LegendItem({
  label,
  value,
  color,
}: {
  label: string;
  value?: string;
  color: string;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <span
        aria-hidden
        style={{
          width: 10,
          height: 10,
          borderRadius: 2,
          background: color,
          flex: '0 0 auto',
        }}
      />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {label}
      </Typography.Text>
      {value !== undefined && (
        <Typography.Text style={{ fontVariantNumeric: 'tabular-nums' }}>
          {value}
        </Typography.Text>
      )}
    </div>
  );
}
