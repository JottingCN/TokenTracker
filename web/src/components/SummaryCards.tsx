import { Card, theme, Tooltip } from 'antd';
import type { Summary } from '../types';
import { useUi } from '../ui';
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
  const { lang, tr } = useUi();
  const { token } = theme.useToken();
  useMoney();
  const number = new Intl.NumberFormat(lang === 'zh' ? 'zh-CN' : 'en-US');

  const cards: CardDefinition[] = [
    {
      label: tr('调用', 'Calls'),
      value: summary ? number.format(summary.calls) : '-',
    },
    {
      label: tr('输入 tokens', 'Input tokens'),
      value: summary ? number.format(summary.inputTokens) : '-',
      cost: summary ? formatMoney(summary.uncachedCostCny) : '-',
      hint: tr(
        '输入 tokens 包含缓存命中部分；下方费用仅为未缓存输入费用，缓存费用见“缓存命中”卡片。',
        'Input tokens include cached tokens; the cost shown is uncached input only. Cache cost is shown in the cached card.',
      ),
    },
    {
      label: tr('缓存命中 tokens', 'Cached tokens'),
      value: summary ? number.format(summary.cacheReadTokens) : '-',
      cost: summary ? formatMoney(summary.cacheCostCny) : '-',
      hint: tr(
        '缓存费用包含缓存读取与缓存写入。',
        'Cache cost includes cache reads and cache writes.',
      ),
    },
    {
      label: tr('命中率', 'Hit rate'),
      value:
        summary && summary.hitRate !== null
          ? `${(summary.hitRate * 100).toFixed(1)}%`
          : '-',
    },
    {
      label: tr('输出 tokens', 'Output tokens'),
      value: summary ? number.format(summary.outputTokens) : '-',
      cost: summary ? formatMoney(summary.outputCostCny) : '-',
      hint: tr(
        '输出 token 包含思考 token，费用按输出单价计算。',
        'Output tokens include reasoning tokens and are priced at the output rate.',
      ),
    },
    {
      label: tr('思考 tokens', 'Reasoning tokens'),
      value: summary ? summary.reasoningTokensText : '-',
      cost:
        summary && summary.reasoningTokens > 0
          ? formatMoney(summary.reasoningCostCny)
          : '-',
      hint: tr(
        '思考 token 是输出 token 的一部分，对应费用已计入输出费用，不重复累加。',
        'Reasoning tokens are part of output; their cost is already included in output cost.',
      ),
    },
    {
      label: tr('估算费用', 'Estimated cost'),
      value: summary ? formatMoney(summary.costCny) : '-',
    },
    lastCard ?? {
      label: tr('会话数', 'Sessions'),
      value: summary ? number.format(summary.sessions) : '-',
    },
    {
      label: tr('未定价调用', 'Unpriced calls'),
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
