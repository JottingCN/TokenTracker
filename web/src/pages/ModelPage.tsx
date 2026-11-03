import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Card,
  Col,
  Empty,
  Row,
  Space,
  Table,
  Tooltip,
  Typography,
} from 'antd';
import { CardPagination } from '../components/CardPagination';
import { SummaryCards } from '../components/SummaryCards';
import { sessionColumns } from '../components/columns';
import { useRealtime } from '../useRealtime';
import { useUi } from '../ui';
import { formatLatency, formatSpeed } from '../utils/labels';
import { useMoney } from '../utils/money';
import { useAutoTableHeight } from '../utils/useAutoTableHeight';
import type { ModelPayload } from '../types';

interface ModelPageProps {
  modelId: string;
  range: number;
  fromMs?: number;
  toMs?: number;
  onOpenSession: (sessionId: string) => void;
}

export function ModelPage({
  modelId,
  range,
  fromMs,
  toMs,
  onOpenSession,
}: ModelPageProps) {
  useMoney();
  const { lang, tr } = useUi();
  const number = new Intl.NumberFormat(lang === 'zh' ? 'zh-CN' : 'en-US');
  const { data } = useRealtime<ModelPayload>({
    view: 'model',
    id: modelId,
    range,
    utility: true,
      fromMs,
    toMs,
  });
  const summary = data?.summary;
  const sessions = data?.sessions ?? [];
  const [sessionTableRef, sessionTableHeight] = useAutoTableHeight(60);
  const [page, setPage] = useState(1);
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(sessions.length / pageSize));

  useEffect(() => {
    if (page > pageCount) {
      setPage(1);
    }
  }, [page, pageCount]);

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <SummaryCards summary={summary ?? null} />

      <Card size="small" title={tr('响应与速度', 'Latency and speed')}>
        <Row gutter={[12, 12]}>
          <Col xs={24} md={12}>
            <Card size="small" style={{ height: '100%' }}>
              <Tooltip
                title={tr(
                  '首次响应时间（TTFT）：请求发出到收到首个输出 token 的平均耗时',
                  'Average time from request to the first output token.',
                )}
              >
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {tr('平均首次响应', 'Average TTFT')}
                </Typography.Text>
              </Tooltip>
              <div
                style={{
                  fontSize: 24,
                  fontWeight: 500,
                  fontVariantNumeric: 'tabular-nums',
                  lineHeight: 1.3,
                }}
              >
                {formatLatency(summary?.avgTtftMs)}
              </div>
              <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                {summary
                  ? tr(
                      `${number.format(summary.ttftSamples)} 次调用包含 TTFT 数据`,
                      `${number.format(summary.ttftSamples)} calls include TTFT data`,
                    )
                  : '-'}
              </Typography.Text>
            </Card>
          </Col>
          <Col xs={24} md={12}>
            <Card size="small" style={{ height: '100%' }}>
              <Tooltip
                title={tr(
                  '生成速度 = 输出 token 总数 / 生成耗时（总耗时减首 token 等待）',
                  'Generation speed = total output tokens / generation time (duration minus TTFT).',
                )}
              >
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {tr('平均生成速度', 'Average generation speed')}
                </Typography.Text>
              </Tooltip>
              <div
                style={{
                  fontSize: 24,
                  fontWeight: 500,
                  fontVariantNumeric: 'tabular-nums',
                  lineHeight: 1.3,
                }}
              >
                {formatSpeed(summary?.tokensPerSecond)}
              </div>
              <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                {tr(
                  '有效样本优先；不足时按总耗时',
                  'Valid samples first; falls back to total duration',
                )}
              </Typography.Text>
            </Card>
          </Col>
        </Row>
      </Card>

      <Card
        size="small"
        title={`${tr(
          '会话（使用该模型，主调用）',
          'Sessions using this model (main calls)',
        )}(${sessions.length})`}
        extra={
          <CardPagination
            current={page}
            pageSize={pageSize}
            total={sessions.length}
            onChange={(nextPage) => setPage(nextPage)}
          />
        }
      >
        {data && sessions.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={tr('暂无数据', 'No data')}
          />
        ) : (
          <div
            ref={sessionTableRef}
            style={
              {
                '--tt-table-body-height': `${sessionTableHeight}px`,
              } as CSSProperties
            }
          >
            <Table
              className="tt-fill-table"
              size="small"
              rowKey="sessionId"
              columns={sessionColumns(false, onOpenSession, true)}
              dataSource={sessions.slice(
                (page - 1) * pageSize,
                page * pageSize,
              )}
              loading={!data}
              pagination={false}
              scroll={{ x: 1340, y: sessionTableHeight }}
            />
          </div>
        )}
      </Card>
    </Space>
  );
}
