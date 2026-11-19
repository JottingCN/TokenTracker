import { Card, theme, Tooltip } from 'antd';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import { formatMoney, useMoney } from '../utils/money';
import { useGridColumns } from '../utils/useGridColumns';

interface SummaryCardsProps {
  summary: Summary | null;
  lastCard?: { label: string; value: string | number } | null;
}

interface CardDefinition {
  label: string;
  value: string | number;
}

export function SummaryCards({ summary, lastCard }: SummaryCardsProps) {
  const { locale, t } = useUi();
  const { token } = theme.useToken();
  useMoney();
  const number = new Intl.NumberFormat(intlLocale(locale));

  const cards: CardDefinition[] = [
    {
      label: t('calls'),
      value: summary ? number.format(summary.calls) : '-',
    },
    {
      label: t('estimated.cost'),
      value: summary ? formatMoney(summary.costCny) : '-',
    },
    ...(lastCard === null
      ? []
      : [
          lastCard ?? {
            label: t('sessions'),
            value: summary ? number.format(summary.sessions) : '-',
          },
        ]),
    {
      label: t('unpriced.calls'),
      value: summary ? number.format(summary.unpricedCalls) : '-',
    },
  ];
  const [primaryGridRef, primaryColumns] = useGridColumns(2, 320);
  const [gridRef, columns] = useGridColumns(cards.length);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div
        ref={primaryGridRef}
        style={{
          display: 'grid',
          gridTemplateColumns: `repeat(${primaryColumns}, minmax(0, 1fr))`,
          gap: 12,
        }}
      >
        <Card
          size="small"
          title={
            <Tooltip title={t('input.tokens.and.the.cost.shown.are.totals.including')}>
              <span>{t('input.tokens')}</span>
            </Tooltip>
          }
          style={{ minWidth: 0 }}
          styles={{ body: { minHeight: 118 } }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              flexWrap: 'wrap',
              gap: 12,
              lineHeight: 1.3,
            }}
          >
            <span
              style={{
                fontSize: 'clamp(24px, 2.4vw, 36px)',
                fontWeight: 600,
                fontVariantNumeric: 'tabular-nums',
              }}
            >
              {summary ? number.format(summary.inputTokens) : '-'}
            </span>
            <span style={{ color: token.colorTextSecondary }}>
              {summary
                ? formatMoney(summary.uncachedCostCny + summary.cacheCostCny)
                : '-'}
            </span>
          </div>
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
              gap: 8,
              marginTop: 16,
            }}
          >
            <BreakdownMetric
              label={t('cached.tokens')}
              value={summary ? number.format(summary.cacheReadTokens) : '-'}
              detail={summary ? formatMoney(summary.cacheReadCostCny) : '-'}
            />
            <BreakdownMetric
              label={t('cache.write')}
              value={summary ? number.format(summary.cacheCreationTokens) : '-'}
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
        <Card
          size="small"
          title={
            <Tooltip title={t('output.tokens.include.reasoning.tokens.and.are.priced')}>
              <span>{t('output.tokens')}</span>
            </Tooltip>
          }
          style={{ minWidth: 0 }}
          styles={{ body: { minHeight: 118 } }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              flexWrap: 'wrap',
              gap: 12,
              lineHeight: 1.3,
            }}
          >
            <span
              style={{
                fontSize: 'clamp(24px, 2.4vw, 36px)',
                fontWeight: 600,
                fontVariantNumeric: 'tabular-nums',
              }}
            >
              {summary ? number.format(summary.outputTokens) : '-'}
            </span>
            <span style={{ color: token.colorTextSecondary }}>
              {summary ? formatMoney(summary.outputCostCny) : '-'}
            </span>
          </div>
          <div style={{ marginTop: 16 }}>
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
            title={card.label}
            styles={{ body: { minHeight: 28 } }}
          >
            <div
              style={{
                display: 'flex',
                alignItems: 'baseline',
                flexWrap: 'wrap',
                columnGap: 8,
                rowGap: 2,
                lineHeight: 1.3,
                minWidth: 0,
              }}
            >
              <span
                style={{
                  fontSize: 'clamp(16px, 1.5vw, 22px)',
                  fontWeight: 500,
                  fontVariantNumeric: 'tabular-nums',
                  whiteSpace: 'nowrap',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  maxWidth: '100%',
                }}
              >
                {card.value}
              </span>
            </div>
          </Card>
        ))}
      </div>
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
