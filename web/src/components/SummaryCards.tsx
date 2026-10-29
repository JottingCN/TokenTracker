import { Card, Col, Row } from 'antd';
import type { Summary } from '../types';

interface SummaryCardsProps {
  summary: Summary | null;
  lastCard?: { label: string; value: string | number };
}

const number = new Intl.NumberFormat('zh-CN');

interface CardDefinition {
  label: string;
  value: string | number;
  cost?: string;
}

export function SummaryCards({ summary, lastCard }: SummaryCardsProps) {
  const cards: CardDefinition[] = [
    {
      label: '主调用',
      value: summary ? number.format(summary.calls) : '-',
    },
    {
      label: '输入 tokens',
      value: summary ? number.format(summary.inputTokens) : '-',
      cost: summary?.uncachedCostText ?? '-',
    },
    {
      label: '缓存命中 tokens',
      value: summary ? number.format(summary.cacheReadTokens) : '-',
      cost: summary?.cacheCostText ?? '-',
    },
    {
      label: '命中率',
      value:
        summary && summary.hitRate !== null
          ? `${(summary.hitRate * 100).toFixed(1)}%`
          : '-',
    },
    {
      label: '输出 tokens',
      value: summary ? number.format(summary.outputTokens) : '-',
      cost: summary?.outputCostText ?? '-',
    },
    {
      label: '思考 tokens',
      value: summary ? summary.reasoningTokensText : '-',
      cost:
        summary && summary.reasoningTokens > 0
          ? summary.reasoningCostText
          : '-',
    },
    {
      label: '估算费用 (CNY)',
      value: summary ? `¥${summary.costCny.toFixed(6)}` : '-',
    },
    lastCard ?? {
      label: '会话数',
      value: summary ? number.format(summary.sessions) : '-',
    },
    {
      label: '未定价调用',
      value: summary ? number.format(summary.unpricedCalls) : '-',
    },
  ];

  return (
    <Row gutter={[12, 12]}>
      {cards.map((card) => (
        <Col key={card.label} xs={12} md={8}>
          <Card size="small" style={{ height: '100%' }}>
            <div style={{ fontSize: 12, color: 'rgba(0, 0, 0, 0.45)' }}>
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
                    color: 'rgba(0, 0, 0, 0.45)',
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
