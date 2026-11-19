import { useState } from 'react';
import { Button, Card, theme, Tooltip } from 'antd';
import { DownOutlined, UpOutlined } from '@ant-design/icons';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import { formatMoney, useMoney } from '../utils/money';
import { useGridColumns } from '../utils/useGridColumns';

interface SummaryCardsProps {
  summary: Summary | null;
  turnCount?: number | null;
}

export function SummaryCards({ summary, turnCount }: SummaryCardsProps) {
  const { locale, t } = useUi();
  const { token } = theme.useToken();
  const [detailsOpen, setDetailsOpen] = useState(true);
  useMoney();
  const number = new Intl.NumberFormat(intlLocale(locale));
  const [gridRef, columns] = useGridColumns(4, 170);
  const detailColumns = Math.min(columns, 2);
  const sectionColumns = columns > 2 ? 2 : 1;

  const cards = [
    {
      label: t('total.tokens'),
      value: summary
        ? number.format(summary.inputTokens + summary.outputTokens)
        : '-',
    },
    {
      label: t('calls'),
      value: summary ? number.format(summary.calls) : '-',
    },
    {
      label: t('ahp.turns'),
      value: turnCount == null ? '-' : number.format(turnCount),
    },
    {
      label: t('estimated.cost'),
      value: summary ? formatMoney(summary.costCny) : '-',
      note: summary
        ? t('unpriced.calls.count', {
            count: number.format(summary.unpricedCalls),
          })
        : '-',
    },
  ];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div
        ref={gridRef}
        style={{
          display: 'grid',
          gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
          gap: 12,
        }}
      >
        {cards.map((card) => (
          <Card
            key={card.label}
            size="small"
            style={{ height: '100%', minWidth: 0 }}
            title={
              card.label === t('ahp.turns') ? (
                <Tooltip title={t('turns.only.available.for.sessions')}>
                  <span>{card.label}</span>
                </Tooltip>
              ) : card.label === t('total.tokens') ? (
                <Tooltip title={t('total.tokens.includes.input.and.output')}>
                  <span>{card.label}</span>
                </Tooltip>
              ) : card.label === t('estimated.cost') ? (
                <Tooltip title={t('unpriced.calls.included.as.count')}>
                  <span>{card.label}</span>
                </Tooltip>
              ) : (
                card.label
              )
            }
          >
            <div
              style={{
                fontSize: 'clamp(20px, 2vw, 30px)',
                fontWeight: 600,
                fontVariantNumeric: 'tabular-nums',
                overflowWrap: 'anywhere',
                lineHeight: 1.3,
              }}
            >
              {card.value}
            </div>
            {'note' in card && (
              <div
                style={{
                  marginTop: 6,
                  fontSize: 11,
                  color: token.colorTextSecondary,
                }}
              >
                {card.note}
              </div>
            )}
          </Card>
        ))}
      </div>

      <Card
        size="small"
        title={t('token.breakdown')}
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
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: `repeat(${sectionColumns}, minmax(0, 1fr))`,
              gap: 12,
            }}
          >
            <Card size="small" title={t('input')}>
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: `repeat(${detailColumns}, minmax(0, 1fr))`,
                  gap: 12,
                }}
              >
                <BreakdownMetric
                  label={t('input.tokens')}
                  value={summary ? number.format(summary.inputTokens) : '-'}
                  detail={
                    summary
                      ? formatMoney(
                          summary.uncachedCostCny + summary.cacheCostCny,
                        )
                      : '-'
                  }
                />
                <BreakdownMetric
                  label={t('cached.tokens')}
                  value={summary ? number.format(summary.cacheReadTokens) : '-'}
                  detail={summary ? formatMoney(summary.cacheReadCostCny) : '-'}
                />
                <BreakdownMetric
                  label={t('cache.write')}
                  value={
                    summary ? number.format(summary.cacheCreationTokens) : '-'
                  }
                  detail={summary ? formatMoney(summary.cacheWriteCostCny) : '-'}
                />
                <BreakdownMetric
                  label={t('hit.rate')}
                  value={
                    summary && summary.hitRate !== null
                      ? `${(summary.hitRate * 100).toFixed(1)}%`
                      : '-'
                  }
                />
              </div>
            </Card>
            <Card size="small" title={t('output')}>
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: `repeat(${detailColumns}, minmax(0, 1fr))`,
                  gap: 12,
                }}
              >
                <BreakdownMetric
                  label={t('output.tokens')}
                  value={summary ? number.format(summary.outputTokens) : '-'}
                  detail={summary ? formatMoney(summary.outputCostCny) : '-'}
                />
                <BreakdownMetric
                  label={t('reasoning.tokens')}
                  value={summary ? summary.reasoningTokensText : '-'}
                  detail={
                    summary && summary.reasoningTokens > 0
                      ? formatMoney(summary.reasoningCostCny)
                      : '-'
                  }
                />
              </div>
            </Card>
          </div>
        )}
      </Card>
    </div>
  );
}

function BreakdownMetric({
  label,
  value,
  detail,
}: {
  label: string;
  value: string;
  detail?: string;
}) {
  const { token } = theme.useToken();

  return (
    <div style={{ minWidth: 0 }}>
      <div
        style={{
          fontSize: 11,
          color: token.colorTextSecondary,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {label}
      </div>
      <div style={{ fontVariantNumeric: 'tabular-nums', overflowWrap: 'anywhere' }}>
        {value}
      </div>
      {detail !== undefined && (
        <div style={{ fontSize: 11, color: token.colorTextSecondary }}>
          {detail}
        </div>
      )}
    </div>
  );
}
