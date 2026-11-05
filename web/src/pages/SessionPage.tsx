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
  Col,
  Empty,
  Modal,
  Row,
  Space,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { OutcomeCard } from '../components/OutcomeCard';
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
  const { lang, tr } = useUi();
  const number = new Intl.NumberFormat(lang === 'zh' ? 'zh-CN' : 'en-US');
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
    data?.title?.trim() || `${tr('未命名', 'Unnamed')} · ${shortId}`;
  const pageSubtitle = data?.workspacePath || sessionId;

  useEffect(() => {
    onMeta?.({ title: pageTitle, subtitle: pageSubtitle });
  }, [onMeta, pageTitle, pageSubtitle]);

  useEffect(() => {
    setActiveScope('all');
  }, [sessionId]);

  const togglePanel = (panel: string) => {
    setOpenPanels((current) =>
      current.includes(panel)
        ? current.filter((key) => key !== panel)
        : [...current, panel],
    );
  };

  const eventColumns: ColumnsType<SessionEventRow> = [
    {
      title: tr('事件类型', 'Event'),
      key: 'operation',
      width: 130,
      render: (_, row) => operationLabel(row.operation),
    },
    {
      title: tr('次数', 'Count'),
      key: 'count',
      width: 80,
      align: 'right',
      render: (_, row) => number.format(row.count),
    },
    {
      title: tr('平均耗时', 'Avg duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.avgDurationMs),
    },
    {
      title: tr('失败', 'Errors'),
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
      title: tr('最近', 'Last seen'),
      key: 'lastSeen',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.lastSeenMs),
    },
  ];

  const toolColumns: ColumnsType<ToolCallRow> = [
    {
      title: tr('工具', 'Tool'),
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
      title: tr('类型', 'Type'),
      key: 'toolType',
      width: 110,
      ellipsis: true,
      render: (_, row) => row.toolTypeText,
    },
    {
      title: tr('细分', 'Details'),
      key: 'toolDetail',
      width: 190,
      render: (_, row) => {
        const tags = [
          ...row.editTypes.map((value) => ({
            key: `edit:${value}`,
            color: 'blue',
            label: `${tr('编辑', 'Edit')}: ${value}`,
          })),
          ...row.skillNames.map((value) => ({
            key: `skill:${value}`,
            color: 'purple',
            label: `${tr('技能', 'Skill')}: ${value}`,
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
      title: tr('MCP Server', 'MCP server'),
      key: 'mcpServer',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.mcpServerHashText,
    },
    {
      title: tr('MCP 工具', 'MCP tool'),
      key: 'mcpTool',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.mcpToolNameText,
    },
    {
      title: tr('错误', 'Error'),
      key: 'errorType',
      width: 130,
      ellipsis: true,
      render: (_, row) => row.errorTypeText,
    },
    {
      title: tr('Agent', 'Agent'),
      key: 'agentName',
      width: 150,
      ellipsis: true,
      render: (_, row) => row.agentNameText,
    },
    {
      title: tr('调用次数', 'Calls'),
      key: 'calls',
      width: 90,
      align: 'right',
      render: (_, row) => number.format(row.calls),
    },
    {
      title: tr('平均耗时', 'Avg duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.avgDurationMs),
    },
    {
      title: tr('最长耗时', 'Max duration'),
      key: 'maxDuration',
      width: 110,
      align: 'right',
      render: (_, row) => formatLatency(row.maxDurationMs),
    },
    {
      title: tr('失败', 'Errors'),
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
      title: tr('最近', 'Last seen'),
      key: 'lastSeen',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.lastSeenMs),
    },
  ];

  const agentRunColumns: ColumnsType<AgentRunRow> = [
    {
      title: tr('层级', 'Scope'),
      key: 'scope',
      width: 90,
      render: (_, row) =>
        row.isRoot ? (
          <Tag>{tr('根', 'Root')}</Tag>
        ) : (
          <Tag color="purple">{tr('子代理', 'Subagent')}</Tag>
        ),
    },
    {
      title: tr('Agent', 'Agent'),
      key: 'agent',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.agentText,
    },
    {
      title: tr('类型', 'Type'),
      key: 'agentType',
      width: 100,
      ellipsis: true,
      render: (_, row) => row.agentTypeText,
    },
    {
      title: tr('提供商', 'Provider'),
      key: 'provider',
      width: 120,
      ellipsis: true,
      render: (_, row) => row.providerText,
    },
    {
      title: tr('模型', 'Model'),
      key: 'model',
      width: 180,
      ellipsis: true,
      render: (_, row) => row.modelText,
    },
    {
      title: tr('轮次', 'Turns'),
      key: 'turns',
      width: 70,
      align: 'right',
      render: (_, row) => row.turnText,
    },
    {
      title: tr('输入 / 缓存 / 输出', 'Input / Cache / Output'),
      key: 'tokens',
      width: 190,
      align: 'right',
      render: (_, row) =>
        `${row.inputText} / ${row.cacheText} / ${row.outputText}`,
    },
    {
      title: tr('仓库 / 分支', 'Repo / Branch'),
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
      title: tr('耗时', 'Duration'),
      key: 'duration',
      width: 100,
      align: 'right',
      render: (_, row) => row.durationText,
    },
    {
      title: tr('开始', 'Start'),
      key: 'start',
      width: 120,
      align: 'right',
      render: (_, row) => formatTime(row.startTimeMs),
    },
    {
      title: tr('错误', 'Error'),
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
      title: tr('Hook', 'Hook'),
      key: 'hookType',
      width: 160,
      ellipsis: true,
      render: (_, row) => row.hookTypeText,
    },
    {
      title: tr('决策', 'Decision'),
      key: 'decision',
      width: 100,
      render: (_, row) => row.decisionText,
    },
    {
      title: tr('结果', 'Result'),
      key: 'result',
      width: 140,
      ellipsis: true,
      render: (_, row) => row.resultKindText,
    },
    {
      title: tr('次数', 'Calls'),
      key: 'count',
      width: 80,
      align: 'right',
      render: (_, row) => row.countText,
    },
    {
      title: tr('平均耗时', 'Avg duration'),
      key: 'avgDuration',
      width: 110,
      align: 'right',
      render: (_, row) => row.avgDurationText,
    },
    {
      title: tr('失败', 'Errors'),
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
      title: tr('作用工具', 'Tools'),
      key: 'tools',
      width: 220,
      ellipsis: true,
      render: (_, row) => row.toolNamesText,
    },
    {
      title: tr('最近', 'Last seen'),
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
            {tr('会话 ID', 'Session ID')}：
          </Typography.Text>
          <Typography.Text code copyable style={{ fontSize: 12 }}>
            {sessionId}
          </Typography.Text>
        </Space>
        <Space size={6}>
          <Tooltip title={tr('在调用明细分页中筛选该会话', 'Filter call details by this session')}>
            <Button
              size="small"
              icon={<FilterOutlined />}
              disabled={!onOpenCalls}
              onClick={() => onOpenCalls?.(sessionId)}
            >
              {tr('查看明细', 'View calls')}
            </Button>
          </Tooltip>
          <Tooltip title={tr('刷新标题和元数据', 'Refresh title and metadata')}>
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={refresh}
            >
              {tr('刷新', 'Refresh')}
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
              label: `${tr('全部模型', 'All models')}(${modelScopes.length})`,
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
          label: tr('事件数', 'Events'),
          value: data ? number.format(eventTotal) : '-',
        }}
      />

      <OutcomeCard outcomes={data?.outcomes} />

      <LatencyCards
        summary={summary}
        extra={
          <Card size="small" style={{ height: '100%' }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {tr('工具调用', 'Tool calls')}
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
              {data
                ? tr(
                    `${number.format(data.tools.length)} 种工具`,
                    `${number.format(data.tools.length)} tools`,
                  )
                : '-'}
            </Typography.Text>
          </Card>
        }
      />

      <Card
        size="small"
        title={`${tr('Agent 运行', 'Agent runs')}(${data?.agentRuns.length ?? 0})`}
        extra={
          <Space size={8}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {tr('root tokens 不计入合计', 'Root tokens excluded from totals')}
            </Typography.Text>
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
              description={tr('暂无 Agent 运行数据', 'No agent runs yet')}
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
        title={`${tr('Hook 执行', 'Hook executions')}(${data?.hooks.length ?? 0})`}
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
              description={tr('暂无 Hook 数据', 'No hook data yet')}
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
        title={
          <Space size={2}>
            <Tooltip title={tr('全页查看', 'Open full page')}>
              <Button
                size="small"
                type="text"
                icon={<FullscreenOutlined />}
                onClick={() => setTraceExpanded(true)}
              />
            </Tooltip>
            <span>{tr('Trace 拓扑', 'Trace topology')}</span>
          </Space>
        }
        extra={
          <Space size={8}>
            {data && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {`${tr('指标点', 'Metrics')} ${number.format(
                  data.signalHealth.metricPoints,
                )}`}
              </Typography.Text>
            )}
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
        title={tr('Trace 拓扑', 'Trace topology')}
        open={traceExpanded}
        onCancel={() => setTraceExpanded(false)}
        footer={null}
        width="100vw"
        style={{ top: 0, maxWidth: '100vw', padding: 0 }}
        styles={{ body: { height: 'calc(100vh - 55px)', padding: 20 } }}
      >
        <TraceTree sessionId={sessionId} version={traceVersion} expanded />
      </Modal>

      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <Card
            size="small"
            title={`${tr('事件统计', 'Event statistics')}(${eventTotal})`}
          >
            {data && data.events.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={tr('暂无数据', 'No data')}
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
        </Col>
        <Col xs={24}>
          <Card
            size="small"
            title={`${tr('工具调用统计', 'Tool call statistics')}(${toolTotal})`}
          >
            {data && data.tools.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={tr(
                  '暂无工具调用数据（需接收 execute_tool span）',
                  'No tool call data yet (requires execute_tool spans)',
                )}
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
        </Col>
      </Row>
    </Space>
  );
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
  const { tr } = useUi();
  return (
    <Button
      size="small"
      type="text"
      aria-expanded={open}
      aria-label={open ? tr('收起', 'Collapse') : tr('展开', 'Expand')}
      icon={open ? <UpOutlined /> : <DownOutlined />}
      onClick={() => onToggle(panel)}
    >
      {open ? tr('收起', 'Collapse') : tr('展开', 'Expand')}
    </Button>
  );
}
