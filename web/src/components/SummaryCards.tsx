import { Card, Col, Row, theme } from 'antd';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { formatMoney, useMoney } from '../utils/money';

interface SummaryCardsProps {
  summary: Summary | null;
  lastCard?: { label: string; value: string | number };
}

interface CardDefinition {
  label: string;
  value: string | number;
  cost?: string;
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
    },
    {
      label: tr('缓存命中 tokens', 'Cached tokens'),
      value: summary ? number.format(summary.cacheReadTokens) : '-',
      cost: summary ? formatMoney(summary.cacheCostCny) : '-',
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
    },
    {
      label: tr('思考 tokens', 'Reasoning tokens'),
      value: summary ? summary.reasoningTokensText : '-',
      cost:
        summary && summary.reasoningTokens > 0
          ? formatMoney(summary.reasoningCostCny)
          : '-',
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

  return (
    <Row gutter={[12, 12]}>
      {cards.map((card) => (
        <Col key={card.label} xs={24} sm={12} md={12} lg={8}>
          <Card size="small" style={{ height: '100%' }}>
            <div style={{ fontSize: 12, color: token.colorTextSecondary }}>
              {card.label}
            </div>
            <div
              style={{
                display: 'flex',
                alignItems: 'baseline',
                flexWrap: 'wrap',
                columnGap: 8,
                rowGap: 2,
                lineHeight: 1.3,
              }}
            >
              <span
                style={{
                  fontSize: 22,
                  fontWeight: 500,
                  fontVariantNumeric: 'tabular-nums',
                  whiteSpace: 'nowrap',
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
        </Col>
      ))}
    </Row>
  );
}
