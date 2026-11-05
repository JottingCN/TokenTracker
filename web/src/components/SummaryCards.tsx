import { Card, theme, Tooltip } from 'antd';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import { formatMoney, useMoney } from '../utils/money';
import { useGridColumns } from '../utils/useGridColumns';

interface SummaryCardsProps {
  summary: Summary | null;
  lastCard?: { label: string; value: string | number };
}

interface CardDefinition {
  label: string;
  value: string | number;
  cost?: string;
  hint?: string;
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
      label: t('input.tokens'),
      value: summary ? number.format(summary.inputTokens) : '-',
      cost: summary
        ? formatMoney(summary.uncachedCostCny + summary.cacheCostCny)
        : '-',
      hint: t('input.tokens.and.the.cost.shown.are.totals.including'),
    },
    {
      label: t('cached.tokens'),
      value: summary ? number.format(summary.cacheReadTokens) : '-',
      cost: summary ? formatMoney(summary.cacheCostCny) : '-',
      hint: t('cache.cost.includes.cache.reads.and.cache.writes'),
    },
    {
      label: t('hit.rate'),
      value:
        summary && summary.hitRate !== null
          ? `${(summary.hitRate * 100).toFixed(1)}%`
          : '-',
    },
    {
      label: t('output.tokens'),
      value: summary ? number.format(summary.outputTokens) : '-',
      cost: summary ? formatMoney(summary.outputCostCny) : '-',
      hint: t('output.tokens.include.reasoning.tokens.and.are.priced'),
    },
    {
      label: t('reasoning.tokens'),
      value: summary ? summary.reasoningTokensText : '-',
      cost:
        summary && summary.reasoningTokens > 0
          ? formatMoney(summary.reasoningCostCny)
          : '-',
      hint: t('reasoning.tokens.are.part.of.output.their.cost.is'),
    },
    {
      label: t('estimated.cost'),
      value: summary ? formatMoney(summary.costCny) : '-',
      hint: t('estimated.cost.input.cost.output.cost.cached.and'),
    },
    lastCard ?? {
      label: t('sessions'),
      value: summary ? number.format(summary.sessions) : '-',
    },
    {
      label: t('unpriced.calls'),
      value: summary ? number.format(summary.unpricedCalls) : '-',
    },
  ];
  const [gridRef, columns] = useGridColumns(cards.length);

  return (
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
          styles={{
            body: {
              display: 'flex',
              flexDirection: 'column',
              justifyContent: 'center',
              minHeight: 64,
            },
          }}
        >
          <div style={{ fontSize: 12, color: token.colorTextSecondary }}>
            {card.hint ? (
              <Tooltip title={card.hint}>
                <span style={{ cursor: 'help', borderBottom: '1px dotted currentColor' }}>
                  {card.label}
                </span>
              </Tooltip>
            ) : (
              card.label
            )}
          </div>
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
            {card.cost !== undefined && (
              <span
                style={{
                  fontSize: 12,
                  color: token.colorTextSecondary,
                  fontVariantNumeric: 'tabular-nums',
                  whiteSpace: 'nowrap',
                }}
              >
                {card.cost}
              </span>
            )}
          </div>
        </Card>
      ))}
    </div>
  );
}
