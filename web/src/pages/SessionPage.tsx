import { useEffect, useState } from 'react';
import {
  DownOutlined,
  FilterOutlined,
  FullscreenOutlined,
  ReloadOutlined,
  UpOutlined,
} from '@ant-design/icons';
import {
  Button,
  Card,
  Empty,
  Modal,
  Space,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { OutcomeCard } from '../components/OutcomeCard';
import { SignalCard } from '../components/SignalCard';
import { LatencyCards } from '../components/LatencyCards';
import { SummaryCards } from '../components/SummaryCards';
import { TraceTree } from '../components/TraceTree';
import { useRealtime } from '../useRealtime';
import type {
  AgentRunRow,
  HookSummaryRow,
  SessionEventRow,
  SessionPayload,
  ToolCallRow,
} from '../types';
import { useUi } from '../ui';
import { intlLocale } from '../i18n';
import {
  formatLatency,
  formatTime,
  operationLabel,
} from '../utils/labels';
import { useMoney } from '../utils/money';
import { useViewportHeight } from '../utils/useViewportHeight';

interface SessionPageProps {
  sessionId: string;
  range: number;
  onMeta?: (meta: { title: string; subtitle?: string }) => void;
  onOpenCalls?: (sessionId: string) => void;
}

export function SessionPage({
  sessionId,
  range,
  onMeta,
  onOpenCalls,
}: SessionPageProps) {
  useMoney();
  const { locale, t } = useUi();
  const number = new Intl.NumberFormat(intlLocale(locale));
  const shortId =
    sessionId.length > 20 ? `${sessionId.slice(0, 8)}…` : sessionId;
  const listHeight = useViewportHeight(0.3, 240, 420);
  const [traceExpanded, setTraceExpanded] = useState(false);
  const [activeScope, setActiveScope] = useState('all');
  const [openPanels, setOpenPanels] = useState<string[]>([]);
  const { data, refresh } = useRealtime<SessionPayload>({
    view: 'session',
    id: sessionId,
    range,
    utility: false,
    // The page renders aggregates only; the raw call list would add ~800 KB
    // per snapshot, so it is requested by the VS Code tip instead.
    includeCalls: false,
  });
  const eventTotal =
    data?.events.reduce((total, row) => total + row.count, 0) ?? 0;
  const toolTotal =
    data?.tools.reduce((total, row) => total + row.calls, 0) ?? 0;
  const modelScopes = data?.models ?? [];
  const scopeSummary =
    activeScope === 'all'
      ? data?.summary
      : modelScopes.find((row) => row.model === activeScope)?.summary;
  const summary = scopeSummary;
  // The trace is fetched separately, so it needs its own signal to re-read after
  // new spans arrive over the WebSocket. The call list is not requested on this
  // page, so the summary count and the event/tool totals drive the refresh.
  const traceVersion = data
    ? [
        data.summary.calls,
        data.events.reduce(
          (max, row) => Math.max(max, row.lastSeenMs),
          0,
        ),
        data.events.reduce((total, row) => total + row.count, 0),
        data.tools.reduce((total, row) => total + row.calls, 0),
        data.agentRuns.length,
      ].join('|')
    : '';
  const pageTitle =
    data?.title?.trim() || `${t('unnamed')} · ${shortId}`;
  const pageSubtitle = data?.workspacePath || sessionId;

  useEffect(() => {
    onMeta?.({ title: pageTitle, subtitle: pageSubtitle });
  }, [onMeta, pageTitle, pageSubtitle]);

  useEffect(() => {
    setActiveScope('all');
  }, [sessionId]);

  // Switching the time range can drop the selected model from the payload; fall
  // back to the combined tab instead of rendering dashes everywhere.
  useEffect(() => {
    if (
      activeScope !== 'all' &&
      !modelScopes.some((row) => row.model === activeScope)
    ) {
      setActiveScope('all');
    }
  }, [activeScope, modelScopes]);

  const togglePanel = (panel: string) => {
    setOpenPanels((current) =>
      current.includes(panel)
        ? current.filter((key) => key !== panel)
        : [...current, panel],
    );
  };

  const eventColumns: ColumnsType<SessionEventRow> = [
    {
      title: t('event'),
      key: 'operation',
      width: 130,
      render: (_, row) => operationLabel(row.operation),
    },
    {
      title: t('count'),
      key: 'count',
      width: 80,
      align: 'right',
      render: (_, row) => number.format(row.count),
    },
    {
      title: t('avg.duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.avgDurationMs),
    },
    {
      title: t('errors'),
      key: 'errors',
      width: 70,
      align: 'right',
      render: (_, row) =>
        row.errorCount > 0 ? (
          <Typography.Text type="danger">
            {number.format(row.errorCount)}
          </Typography.Text>
        ) : (
          '0'
        ),
    },
    {
      title: t('last.seen'),
      key: 'lastSeen',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.lastSeenMs),
    },
  ];

  const toolColumns: ColumnsType<ToolCallRow> = [
    {
      title: t('tool'),
      key: 'tool',
      width: 180,
      ellipsis: true,
      render: (_, row) => (
        <Tooltip title={row.tool}>
          <span>{row.toolDisplayText}</span>
        </Tooltip>
      ),
    },
    {
      title: t('type'),
      key: 'toolType',
      width: 110,
      ellipsis: true,
      render: (_, row) => row.toolTypeText,
    },
    {
      title: t('details'),
      key: 'toolDetail',
      width: 190,
      render: (_, row) => {
        const tags = [
          ...row.editTypes.map((value) => ({
            key: `edit:${value}`,
            color: 'blue',
            label: `${t('edit')}: ${value}`,
          })),
          ...row.skillNames.map((value) => ({
            key: `skill:${value}`,
            color: 'purple',
            label: `${t('skill')}: ${value}`,
          })),
        ];
        return tags.length === 0 ? (
          '-'
        ) : (
          <Space size={4} wrap>
            {tags.map((tag) => (
              <Tag key={tag.key} color={tag.color}>
                {tag.label}
              </Tag>
            ))}
          </Space>
        );
      },
    },
    {
      title: t('mcp.server'),
      key: 'mcpServer',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.mcpServerHashText,
    },
    {
      title: t('mcp.tool'),
      key: 'mcpTool',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.mcpToolNameText,
    },
    {
      title: t('error'),
      key: 'errorType',
      width: 130,
      ellipsis: true,
      render: (_, row) => row.errorTypeText,
    },
    {
      title: t('agent'),
      key: 'agentName',
      width: 150,
      ellipsis: true,
      render: (_, row) => row.agentNameText,
    },
    {
      title: t('calls'),
      key: 'calls',
      width: 90,
      align: 'right',
      render: (_, row) => number.format(row.calls),
    },
    {
      title: t('avg.duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.avgDurationMs),
    },
    {
      title: t('max.duration'),
      key: 'maxDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.maxDurationMs),
    },
    {
      title: t('errors'),
      key: 'errors',
      width: 70,
      align: 'right',
      render: (_, row) =>
        row.errorCount > 0 ? (
          <Typography.Text type="danger">
            {number.format(row.errorCount)}
          </Typography.Text>
        ) : (
          '0'
        ),
    },
    {
      title: t('last.seen'),
      key: 'lastSeen',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.lastSeenMs),
    },
  ];

  const agentRunColumns: ColumnsType<AgentRunRow> = [
    {
      title: t('scope'),
      key: 'scope',
      width: 90,
      render: (_, row) =>
        row.isRoot ? (
          <Tag>{t('root')}</Tag>
        ) : (
          <Tag color="purple">{t('subagent')}</Tag>
        ),
    },
    {
      title: t('agent'),
      key: 'agent',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.agentText,
    },
    {
      title: t('type'),
      key: 'agentType',
      width: 100,
      ellipsis: true,
      render: (_, row) => row.agentTypeText,
    },
    {
      title: t('provider'),
      key: 'provider',
      width: 120,
      ellipsis: true,
      render: (_, row) => row.providerText,
    },
    {
      title: t('model'),
      key: 'model',
      width: 180,
      ellipsis: true,
      render: (_, row) => row.modelText,
    },
    {
      title: t('turns'),
      key: 'turns',
      width: 70,
      align: 'right',
      render: (_, row) => row.turnText,
    },
    {
      title: t('input.cache.output'),
      key: 'tokens',
      width: 190,
      align: 'right',
      render: (_, row) =>
        `${row.inputText} / ${row.cacheText} / ${row.outputText}`,
    },
    {
      title: t('repo.branch'),
      key: 'repo',
      width: 220,
      ellipsis: true,
      render: (_, row) => {
        const parts = [row.gitRepository, row.gitBranch].filter(
          (value): value is string => Boolean(value),
        );
        if (parts.length === 0) {
          return '-';
        }

        const details = [
          row.gitRepository ? `repo: ${row.gitRepository}` : null,
          row.gitBranch ? `branch: ${row.gitBranch}` : null,
          row.gitCommit ? `commit: ${row.gitCommit}` : null,
          row.githubOrg ? `org: ${row.githubOrg}` : null,
        ].filter((value): value is string => Boolean(value));
        return (
          <Tooltip
            title={
              <span style={{ whiteSpace: 'pre-line' }}>
                {details.join('\n')}
              </span>
            }
          >
            <span>{parts.join(' / ')}</span>
          </Tooltip>
        );
      },
    },
    {
      title: t('duration'),
      key: 'duration',
      width: 100,
      align: 'right',
      render: (_, row) => row.durationText,
    },
    {
      title: t('start'),
      key: 'start',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.startTimeMs),
    },
    {
      title: t('error'),
      key: 'error',
      width: 120,
      ellipsis: true,
      render: (_, row) =>
        row.isError ? (
          <Typography.Text type="danger">{row.errorText}</Typography.Text>
        ) : (
          '-'
        ),
    },
  ];

  const hookColumns: ColumnsType<HookSummaryRow> = [
    {
      title: t('hook'),
      key: 'hookType',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.hookTypeText,
    },
    {
      title: t('decision'),
      key: 'decision',
      width: 100,
      render: (_, row) => row.decisionText,
    },
    {
      title: t('result'),
      key: 'result',
      width: 140,
      ellipsis: true,
      render: (_, row) => row.resultKindText,
    },
    {
      title: t('calls'),
      key: 'count',
      width: 80,
      align: 'right',
      render: (_, row) => row.countText,
    },
    {
      title: t('avg.duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => row.avgDurationText,
    },
    {
      title: t('errors'),
      key: 'errors',
      width: 70,
      align: 'right',
      render: (_, row) =>
        row.errorCount > 0 ? (
          <Typography.Text type="danger">
            {number.format(row.errorCount)}
          </Typography.Text>
        ) : (
          '0'
        ),
    },
    {
      title: t('tools'),
      key: 'tools',
      width: 220,
      ellipsis: true,
      render: (_, row) => row.toolNamesText,
    },
    {
      title: t('last.seen'),
      key: 'lastSeen',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.lastSeenMs),
    },
  ];

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 12,
          flexWrap: 'wrap',
        }}
      >
        <Space size={6}>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('session.id')}：
          </Typography.Text>
          <Typography.Text code copyable style={{ fontSize: 12 }}>
            {sessionId}
          </Typography.Text>
        </Space>
        <Space size={6}>
          <Tooltip title={t('filter.call.details.by.this.session')}>
            <Button
              size="small"
              icon={<FilterOutlined />}
              disabled={!onOpenCalls}
              onClick={() => onOpenCalls?.(sessionId)}
            >
              {t('view.calls')}
            </Button>
          </Tooltip>
          <Tooltip title={t('refresh.title.and.metadata')}>
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={refresh}
            >
              {t('refresh')}
            </Button>
          </Tooltip>
        </Space>
      </div>

      {modelScopes.length > 1 && (
        <Tabs
          size="small"
          activeKey={activeScope}
          onChange={setActiveScope}
          items={[
            {
              key: 'all',
              label: `${t('all.models')}(${modelScopes.length})`,
            },
            ...modelScopes.map((row) => ({
              key: row.model,
              label: row.model,
            })),
          ]}
        />
      )}

      <SummaryCards
        summary={summary ?? null}
        lastCard={{
          label: t('events'),
          value: data ? number.format(eventTotal) : '-',
        }}
      />

      <OutcomeCard outcomes={data?.outcomes} />

      <SignalCard signalHealth={data?.signalHealth} />

      <LatencyCards
        summary={summary}
        extra={
          <Card size="small" style={{ height: '100%' }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {t('tool.calls')}
            </Typography.Text>
            <div
              style={{
                fontSize: 24,
                fontWeight: 500,
                fontVariantNumeric: 'tabular-nums',
                lineHeight: 1.3,
              }}
            >
              {data ? number.format(toolTotal) : '-'}
            </div>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              {data ? t('tools.count', { count: number.format(data.tools.length) }) : '-'}
            </Typography.Text>
          </Card>
        }
      />

      <Card
        size="small"
        styles={collapsedBodyStyle(openPanels.includes('agents'))}
        title={`${t('agent.runs')}(${data?.agentRuns.length ?? 0})`}
        extra={
          <Space size={8}>
            {data && data.agentRuns.length > 0 && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('root.tokens.excluded.from.totals')}
              </Typography.Text>
            )}
            <CollapseToggle
              panel="agents"
              open={openPanels.includes('agents')}
              onToggle={togglePanel}
            />
          </Space>
        }
      >
        {openPanels.includes('agents') &&
          (data && data.agentRuns.length === 0 ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t('no.agent.runs.yet')}
            />
          ) : (
            <Table
              size="small"
              rowKey={(row) => `${row.traceId}:${row.spanId}`}
              columns={agentRunColumns}
              dataSource={data?.agentRuns ?? []}
              loading={!data}
              pagination={false}
              scroll={{ x: 1500, y: listHeight }}
            />
          ))}
      </Card>

      <Card
        size="small"
        styles={collapsedBodyStyle(openPanels.includes('hooks'))}
        title={`${t('hook.executions')}(${data?.hooks.length ?? 0})`}
        extra={
          <CollapseToggle
            panel="hooks"
            open={openPanels.includes('hooks')}
            onToggle={togglePanel}
          />
        }
      >
        {openPanels.includes('hooks') &&
          (data && data.hooks.length === 0 ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t('no.hook.data.yet')}
            />
          ) : (
            <Table
              size="small"
              rowKey={(row) =>
                `${row.hookType}:${row.decision}:${row.resultKind}`
              }
              columns={hookColumns}
              dataSource={data?.hooks ?? []}
              loading={!data}
              pagination={false}
              scroll={{ x: 1200, y: listHeight }}
            />
          ))}
      </Card>

      <Card
        size="small"
        styles={collapsedBodyStyle(openPanels.includes('trace'))}
        title={t('trace.topology')}
        extra={
          <Space size={8}>
            <Tooltip title={t('open.full.page')}>
              <Button
                size="small"
                type="text"
                icon={<FullscreenOutlined />}
                onClick={() => setTraceExpanded(true)}
              />
            </Tooltip>
            <CollapseToggle
              panel="trace"
              open={openPanels.includes('trace')}
              onToggle={togglePanel}
            />
          </Space>
        }
      >
        {openPanels.includes('trace') && (
          <TraceTree sessionId={sessionId} version={traceVersion} />
        )}
      </Card>

      <Modal
        title={t('trace.topology')}
        open={traceExpanded}
        onCancel={() => setTraceExpanded(false)}
        footer={null}
        width="100vw"
        style={{ top: 0, maxWidth: '100vw', padding: 0 }}
        styles={{ body: { height: 'calc(100vh - 55px)', padding: 20 } }}
      >
        <TraceTree sessionId={sessionId} version={traceVersion} expanded />
      </Modal>

      <Card
        size="small"
        title={`${t('event.statistics')}(${eventTotal})`}
      >
            {data && data.events.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={t('no.data')}
              />
            ) : (
              <Table
                size="small"
                rowKey="operation"
                columns={eventColumns}
                dataSource={data?.events ?? []}
                loading={!data}
                pagination={false}
                scroll={{ x: 630, y: listHeight }}
              />
            )}
      </Card>

      <Card
        size="small"
        title={`${t('tool.call.statistics')}(${toolTotal})`}
      >
            {data && data.tools.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={t('no.tool.call.data.yet.requires.execute.tool.spans')}
              />
            ) : (
              <Table
                size="small"
                rowKey={(row) => row.tool}
                columns={toolColumns}
                dataSource={data?.tools ?? []}
                loading={!data}
                pagination={false}
                scroll={{ x: 1800, y: listHeight }}
              />
            )}
      </Card>
    </Space>
  );
}

// A collapsed section should read as a single title row; without this the empty
// card body still adds padding and breaks the rhythm of the page.
function collapsedBodyStyle(open: boolean) {
  return open ? undefined : { body: { display: 'none' } };
}

// Collapsible sections keep the session page short: the title row stays visible
// with the counts, and the heavy tables/tree render only when expanded.
function CollapseToggle({
  panel,
  open,
  onToggle,
}: {
  panel: string;
  open: boolean;
  onToggle: (panel: string) => void;
}) {
  const { t } = useUi();
  return (
    <Button
      size="small"
      type="text"
      aria-expanded={open}
      aria-label={open ? t('collapse') : t('expand')}
      icon={open ? <UpOutlined /> : <DownOutlined />}
      onClick={() => onToggle(panel)}
    >
      {open ? t('collapse') : t('expand')}
    </Button>
  );
}
