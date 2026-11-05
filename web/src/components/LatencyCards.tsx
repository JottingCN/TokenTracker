import { Card, Col, Row, theme, Tooltip, Typography } from 'antd';
import type { CSSProperties, ReactNode } from 'react';
import type { Summary } from '../types';
import { useUi } from '../ui';
import { formatLatency, formatSpeed } from '../utils/labels';

interface LatencyCardsProps {
  summary: Summary | null | undefined;
  // Rendered as an extra card after the latency cards (session page only).
  extra?: ReactNode;
}

// Shared by the session and model pages so both report identical latency
// figures; extremes only consider successful calls, which the server filters.
export function LatencyCards({ summary, extra }: LatencyCardsProps) {
  const { tr } = useUi();

  return (
    <Card size="small" title={tr('响应与速度', 'Latency and speed')}>
      <Row gutter={[12, 12]}>
        <Col xs={24} md={extra ? 8 : 12}>
          <MetricCard
            label={tr('平均首次响应', 'Average TTFT')}
            tooltip={tr(
              '首次响应时间（TTFT）：请求发出到收到首个输出 token 的平均耗时',
              'Average time from request to the first output token.',
            )}
            value={formatLatency(summary?.avgTtftMs)}
            note={
              summary
                ? tr(
                    `${summary.ttftSamples} 次调用包含 TTFT 数据`,
                    `${summary.ttftSamples} calls include TTFT data`,
                  )
                : '-'
            }
            // Lower TTFT is better, so the minimum is the fastest call.
            fastest={formatLatency(summary?.minTtftMs)}
            slowest={formatLatency(summary?.maxTtftMs)}
          />
        </Col>
        <Col xs={24} md={extra ? 8 : 12}>
          <MetricCard
            label={tr('平均生成速度', 'Average generation speed')}
            tooltip={tr(
              '生成速度 = 输出 token 总数 / 生成耗时（总耗时减首 token 等待）',
              'Generation speed = total output tokens / generation time (duration minus TTFT).',
            )}
            value={formatSpeed(summary?.tokensPerSecond)}
            note={tr(
              '有效样本优先；不足时按总耗时',
              'Valid samples first; falls back to total duration',
            )}
            // Higher speed is better, so the maximum is the fastest call.
            fastest={formatSpeed(summary?.maxTokensPerSecond)}
            slowest={formatSpeed(summary?.minTokensPerSecond)}
          />
        </Col>
        {extra && <Col xs={24} md={8}>{extra}</Col>}
      </Row>
    </Card>
  );
}

const VALUE_STYLE: CSSProperties = {
  fontSize: 24,
  fontWeight: 500,
  fontVariantNumeric: 'tabular-nums',
  lineHeight: 1.3,
};

function MetricCard({
  label,
  tooltip,
  value,
  note,
  fastest,
  slowest,
}: {
  label: string;
  tooltip: string;
  value: string;
  note: string;
  fastest: string;
  slowest: string;
}) {
  const { tr } = useUi();
  const { token } = theme.useToken();
  const extremeHint = tr(
    '仅统计正确响应（无错误）的调用',
    'Successful calls only (no errors)',
  );

  return (
    <Card size="small" style={{ height: '100%' }}>
      <Tooltip title={tooltip}>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {label}
        </Typography.Text>
      </Tooltip>
      {/* The extremes sit on the top-right and bottom-right corners of the
          average: the column stretches to the value's line box and
          space-between pins one label to each corner, like a superscript and
          subscript. Plain spans are used because antd's Typography resets the
          margins this layout depends on. */}
      <div style={{ display: 'flex', alignItems: 'stretch', gap: 6 }}>
        <div style={VALUE_STYLE}>{value}</div>
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'space-between',
            paddingTop: 1,
            paddingBottom: 1,
            minWidth: 0,
          }}
        >
          <Tooltip title={extremeHint}>
            <span
              style={{
                color: token.colorTextTertiary,
                fontSize: 10,
                lineHeight: 1,
                cursor: 'help',
                whiteSpace: 'nowrap',
              }}
            >
              {tr('最快', 'Fastest')} {fastest}
            </span>
          </Tooltip>
          <Tooltip title={extremeHint}>
            <span
              style={{
                color: token.colorTextTertiary,
                fontSize: 10,
                lineHeight: 1,
                cursor: 'help',
                whiteSpace: 'nowrap',
              }}
            >
              {tr('最慢', 'Slowest')} {slowest}
            </span>
          </Tooltip>
        </div>
      </div>
      <Typography.Text type="secondary" style={{ fontSize: 11 }}>
        {note}
      </Typography.Text>
    </Card>
  );
}
