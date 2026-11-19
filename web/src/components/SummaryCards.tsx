import { useState } from 'react';
import { Button, Card, theme, Tooltip, Typography } from 'antd';
import { DownOutlined, UpOutlined } from '@ant-design/icons';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import { formatMoney, useMoney } from '../utils/money';
import { accentBarStyle } from '../utils/modelColors';
import { useGridColumns } from '../utils/useGridColumns';
import { SummaryUsageChart, SERIES_COLORS } from './SummaryUsageChart';

interface SummaryCardsProps {
  summary: Summary | null;
  range: number;
  fromMs?: number;
  toMs?: number;
  model?: string;
  sessionId?: string;
  // Model accent: paints the card's top bar so a scoped card is recognisable at
  // a glance without a tag next to the title.
  accent?: string;
}

export function SummaryCards({
  summary,
  range,
  fromMs,
  toMs,
  model,
  sessionId,
  accent,
}: SummaryCardsProps) {
  const { locale, t } = useUi();
  const { token } = theme.useToken();
  const [detailsOpen, setDetailsOpen] = useState(true);
  useMoney();
  const number = new Intl.NumberFormat(intlLocale(locale));
  const [gridRef, columns] = useGridColumns(4, 170);
  const metricColumns = Math.min(columns, 2);
  const sectionColumns = columns > 2 ? 2 : 1;
  const inputCacheTokens = summary
    ? summary.cacheReadTokens + summary.cacheCreationTokens
    : 0;
  const inputUncachedTokens = summary
    ? Math.max(0, summary.inputTokens - inputCacheTokens)
    : 0;
  const outputReasoningTokens = summary
    ? Math.min(summary.reasoningTokens, summary.outputTokens)
    : 0;
  const outputRegularTokens = summary
    ? Math.max(0, summary.outputTokens - outputReasoningTokens)
    : 0;

  return (
    <div ref={gridRef} style={{ width: '100%' }}>
      <Card
        size="small"
        title={t('token.breakdown')}
        style={accentBarStyle(accent)}
        extra={
          <Button
            size="small"
            type="text"
            aria-expanded={detailsOpen}
            aria-label={detailsOpen ? t('collapse') : t('expand')}
            icon={detailsOpen ? <UpOutlined /> : <DownOutlined />}
            onClick={() => setDetailsOpen((open) => !open)}
          >
            {detailsOpen ? t('collapse') : t('expand')}
          </Button>
        }
        styles={detailsOpen ? undefined : { body: { display: 'none' } }}
      >
        {detailsOpen && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <Card
              size="small"
              style={{
                background: token.colorFillQuaternary,
                borderColor: token.colorBorderSecondary,
              }}
              styles={{ body: { padding: '14px 16px' } }}
            >
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: `repeat(${metricColumns}, minmax(0, 1fr))`,
                  columnGap: 20,
                  rowGap: 12,
                }}
              >
                <HeroStat
                  label={t('total.tokens')}
                  tooltip={t('total.tokens.includes.input.and.output')}
                  value={
                    summary
                      ? number.format(summary.inputTokens + summary.outputTokens)
                      : '-'
                  }
                  note={
                    summary
                      ? `${t('input')} ${number.format(summary.inputTokens)} · ${t('output')} ${number.format(summary.outputTokens)}`
                      : '-'
                  }
                />
                <HeroStat
                  label={t('estimated.cost')}
                  tooltip={t('unpriced.calls.included.as.count')}
                  value={summary ? formatMoney(summary.costCny) : '-'}
                  note={
                    summary
                      ? summary.unpricedCalls > 0
                        ? t('unpriced.calls.count', {
                            count: number.format(summary.unpricedCalls),
                          })
                        : t('all.calls.priced')
                      : '-'
                  }
                  divided={metricColumns > 1}
                />
              </div>
            </Card>

            <div
              style={{
                display: 'grid',
                gridTemplateColumns: `repeat(${sectionColumns}, minmax(0, 1fr))`,
                gap: 12,
                alignItems: 'stretch',
              }}
            >
              <BreakdownCard
                title={t('input')}
                badge={
                  summary && summary.hitRate !== null
                    ? `${t('hit.rate')} ${(summary.hitRate * 100).toFixed(1)}%`
                    : undefined
                }
                totalLabel={t('input.tokens')}
                totalValue={
                  summary ? number.format(summary.inputTokens) : '-'
                }
                totalCost={
                  summary
                    ? formatMoney(
                        summary.uncachedCostCny + summary.cacheCostCny,
                      )
                    : '-'
                }
                totalCostTooltip={t(
                  'input.tokens.and.the.cost.shown.are.totals.including',
                )}
                bars={[
                  {
                    label: t('tokens'),
                    segments: [
                      { value: inputUncachedTokens, color: SERIES_COLORS[0] },
                      { value: inputCacheTokens, color: SERIES_COLORS[1] },
                    ],
                  },
                  {
                    label: t('amount'),
                    segments: [
                      {
                        value: summary?.uncachedCostCny ?? 0,
                        color: SERIES_COLORS[0],
                      },
                      {
                        value:
                          (summary?.cacheReadCostCny ?? 0) +
                          (summary?.cacheWriteCostCny ?? 0),
                        color: SERIES_COLORS[1],
                      },
                    ],
                  },
                ]}
                rows={[
                  {
                    label: t('uncached.input'),
                    color: SERIES_COLORS[0],
                    value: summary
                      ? number.format(inputUncachedTokens)
                      : '-',
                    cost: summary
                      ? formatMoney(summary.uncachedCostCny)
                      : '-',
                  },
                  {
                    label: t('cached.tokens'),
                    color: SERIES_COLORS[1],
                    value: summary ? number.format(inputCacheTokens) : '-',
                    cost: summary
                      ? formatMoney(
                          summary.cacheReadCostCny +
                            summary.cacheWriteCostCny,
                        )
                      : '-',
                  },
                ]}
              />
              <BreakdownCard
                title={t('output')}
                badge={
                  summary && summary.outputTokens > 0
                    ? `${t('reasoning.share')} ${((outputReasoningTokens / summary.outputTokens) * 100).toFixed(1)}%`
                    : undefined
                }
                totalLabel={t('output.tokens')}
                totalValue={
                  summary ? number.format(summary.outputTokens) : '-'
                }
                totalCost={summary ? formatMoney(summary.outputCostCny) : '-'}
                totalCostTooltip={t(
                  'output.tokens.include.reasoning.tokens.and.are.priced',
                )}
                bars={[
                  {
                    label: t('tokens'),
                    segments: [
                      { value: outputRegularTokens, color: SERIES_COLORS[2] },
                      {
                        value: outputReasoningTokens,
                        color: SERIES_COLORS[3],
                      },
                    ],
                  },
                  {
                    label: t('amount'),
                    segments: [
                      {
                        value: summary
                          ? Math.max(
                              0,
                              summary.outputCostCny - summary.reasoningCostCny,
                            )
                          : 0,
                        color: SERIES_COLORS[2],
                      },
                      {
                        value: summary
                          ? Math.min(
                              summary.reasoningCostCny,
                              summary.outputCostCny,
                            )
                          : 0,
                        color: SERIES_COLORS[3],
                      },
                    ],
                  },
                ]}
                rows={[
                  {
                    label: t('reasoning.tokens'),
                    color: SERIES_COLORS[3],
                    value: summary ? summary.reasoningTokensText : '-',
                    cost:
                      summary && summary.reasoningTokens > 0
                        ? formatMoney(summary.reasoningCostCny)
                        : '-',
                  },
                  {
                    label: t('non.reasoning.output'),
                    color: SERIES_COLORS[2],
                    value: summary
                      ? number.format(
                          Math.max(
                            0,
                            summary.outputTokens - summary.reasoningTokens,
                          ),
                        )
                      : '-',
                    cost: summary
                      ? formatMoney(
                          Math.max(
                            0,
                            summary.outputCostCny - summary.reasoningCostCny,
                          ),
                        )
                      : '-',
                  },
                ]}
              />
            </div>

            <SummaryUsageChart
              summary={summary}
              range={range}
              fromMs={fromMs}
              toMs={toMs}
              model={model}
              sessionId={sessionId}
            />
          </div>
        )}
      </Card>
    </div>
  );
}

function HeroStat({
  label,
  tooltip,
  value,
  note,
  divided,
}: {
  label: string;
  tooltip: string;
  value: string;
  note: string;
  divided?: boolean;
}) {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        minWidth: 0,
        paddingInlineStart: divided ? 20 : 0,
        borderInlineStart: divided
          ? `1px solid ${token.colorBorderSecondary}`
          : undefined,
      }}
    >
      <Tooltip title={tooltip}>
        <span
          style={{
            fontSize: 12,
            color: token.colorTextSecondary,
            cursor: 'help',
            whiteSpace: 'nowrap',
          }}
        >
          {label}
        </span>
      </Tooltip>
      <div
        style={{
          marginTop: 2,
          fontSize: 'clamp(24px, 2.2vw, 34px)',
          fontWeight: 600,
          fontVariantNumeric: 'tabular-nums',
          lineHeight: 1.2,
          overflowWrap: 'anywhere',
        }}
      >
        {value}
      </div>
      <div
        style={{
          marginTop: 4,
          minHeight: 16,
          fontSize: 11,
          color: token.colorTextTertiary,
          overflowWrap: 'anywhere',
        }}
      >
        {note}
      </div>
    </div>
  );
}

interface BreakdownRow {
  label: string;
  color: string;
  value: string;
  cost: string;
}

interface BreakdownBar {
  label: string;
  segments: { value: number; color: string }[];
}

function BreakdownCard({
  title,
  badge,
  totalLabel,
  totalValue,
  totalCost,
  totalCostTooltip,
  bars,
  rows,
}: {
  title: string;
  badge?: string;
  totalLabel: string;
  totalValue: string;
  totalCost: string;
  totalCostTooltip?: string;
  bars: BreakdownBar[];
  rows: BreakdownRow[];
}) {
  const { token } = theme.useToken();

  return (
    <Card
      size="small"
      title={title}
      style={{ height: '100%' }}
      extra={
        badge && (
          <Typography.Text type="secondary" style={{ fontSize: 11 }}>
            {badge}
          </Typography.Text>
        )
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div style={{ minWidth: 0 }}>
          <div style={{ fontSize: 11, color: token.colorTextSecondary }}>
            {totalLabel}
          </div>
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              flexWrap: 'wrap',
              columnGap: 8,
              rowGap: 2,
            }}
          >
            <span
              style={{
                fontSize: 22,
                fontWeight: 600,
                fontVariantNumeric: 'tabular-nums',
                lineHeight: 1.3,
                overflowWrap: 'anywhere',
              }}
            >
              {totalValue}
            </span>
            <span
              style={{
                fontSize: 12,
                color: token.colorTextSecondary,
                fontVariantNumeric: 'tabular-nums',
              }}
            >
              {totalCostTooltip ? (
                <Tooltip title={totalCostTooltip}>
                  <span style={{ cursor: 'help' }}>{totalCost}</span>
                </Tooltip>
              ) : (
                totalCost
              )}
            </span>
          </div>
        </div>

        {/* Two bars per card: tokens and cost are split differently (a cached
            token is cheap, a reasoning token is not), so one bar cannot describe
            both. Segments share the chart colors, so the card, the stacked bars
            and the pie describe the same split. */}
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 6,
            paddingTop: 2,
          }}
        >
          {bars.map((bar) => {
            const total = bar.segments.reduce(
              (sum, segment) => sum + segment.value,
              0,
            );

            return (
              <div
                key={bar.label}
                style={{ display: 'flex', alignItems: 'center', gap: 8 }}
              >
                <span
                  style={{
                    flex: '0 0 auto',
                    width: 34,
                    fontSize: 11,
                    color: token.colorTextTertiary,
                  }}
                >
                  {bar.label}
                </span>
                <div
                  aria-hidden
                  style={{
                    flex: 1,
                    minWidth: 0,
                    display: 'flex',
                    height: 6,
                    borderRadius: 3,
                    overflow: 'hidden',
                    background: token.colorFillSecondary,
                  }}
                >
                  {total > 0 &&
                    bar.segments.map((segment, index) => (
                      <span
                        key={index}
                        style={{
                          width: `${(segment.value / total) * 100}%`,
                          background: segment.color,
                        }}
                      />
                    ))}
                </div>
              </div>
            );
          })}
        </div>

        <div style={{ display: 'flex', flexDirection: 'column' }}>
          {rows.map((row, index) => (
            <div
              key={row.label}
              style={{
                display: 'flex',
                alignItems: 'baseline',
                gap: 8,
                padding: '6px 0',
                borderTop:
                  index === 0
                    ? undefined
                    : `1px solid ${token.colorSplit}`,
              }}
            >
              <span
                aria-hidden
                style={{
                  width: 6,
                  height: 6,
                  borderRadius: 3,
                  background: row.color,
                  flex: '0 0 auto',
                  alignSelf: 'center',
                }}
              />
              <span
                style={{
                  flex: 1,
                  minWidth: 0,
                  fontSize: 12,
                  color: token.colorTextSecondary,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {row.label}
              </span>
              <span
                style={{
                  fontVariantNumeric: 'tabular-nums',
                  whiteSpace: 'nowrap',
                }}
              >
                {row.value}
              </span>
              <span
                style={{
                  width: 'min(96px, 28%)',
                  textAlign: 'right',
                  fontSize: 11,
                  color: token.colorTextTertiary,
                  fontVariantNumeric: 'tabular-nums',
                  whiteSpace: 'nowrap',
                }}
              >
                {row.cost}
              </span>
            </div>
          ))}
        </div>
      </div>
    </Card>
  );
}
