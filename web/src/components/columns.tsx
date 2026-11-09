import { Tag, Tooltip } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { CallRow, ModelRow, SessionRow } from '../types';
import { ModelIcon } from './ModelIcon';
import { t } from '../i18n';
import {
  agentLabel,
  formatLatency,
  formatSpeed,
} from '../utils/labels';
import { formatCost, formatMoney } from '../utils/money';

function tokenWithCost(tokenText: string, costText: string) {
  return (
    <div style={{ lineHeight: '1.25' }}>
      <div>{tokenText}</div>
      <div className="tt-secondary" style={{ fontSize: 11 }}>
        {costText}
      </div>
    </div>
  );
}

function ModelCell({ model }: { model: string }) {
  return (
    <span
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        maxWidth: '100%',
        minWidth: 0,
      }}
    >
      <ModelIcon model={model} />
      <span
        style={{
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {model}
      </span>
    </span>
  );
}

function sessionName(title: string | null | undefined, short: string): string {
  return title?.trim() || `${t('unnamed')} · ${short}`;
}

function callSessionName(row: CallRow): string {
  if (!row.sessionId) {
    return t('utility.call');
  }
  return sessionName(row.title, row.sessionShort);
}

function periodLabel(row: CallRow): string {
  if (row.costCny === null) {
    return '-';
  }
  switch (row.pricingPeriod) {
    case 'peak':
      return t('peak');
    case 'idle':
      return t('idle');
    case 'flat':
      return t('flat');
    default:
      return '-';
  }
}

export function modelColumns(
  onOpenModel?: (model: string) => void,
): ColumnsType<ModelRow> {
  return [
    {
      title: t('model'),
      key: 'model',
      width: 200,
      ellipsis: true,
      render: (_, row) =>
        onOpenModel ? (
          <a onClick={() => onOpenModel(row.model)}>
            <ModelCell model={row.model} />
          </a>
        ) : (
          <ModelCell model={row.model} />
        ),
    },
    {
      title: t('calls'),
      dataIndex: 'callsText',
      key: 'calls',
      width: 70,
      align: 'right',
    },
    {
      title: t('input'),
      key: 'input',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(
          row.inputText,
          formatMoney(row.uncachedCostCny + row.cacheCostCny),
        ),
    },
    {
      title: t('cached'),
      key: 'cache',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.cacheText, formatMoney(row.cacheCostCny)),
    },
    {
      title: t('hit'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 70,
      align: 'right',
    },
    {
      title: (
        <Tooltip
          title={t('output.tokens.include.reasoning.tokens.and.are.priced')}
        >
          {t('output')}
        </Tooltip>
      ),
      key: 'output',
      width: 90,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.outputText, formatMoney(row.outputCostCny)),
    },
    {
      title: (
        <Tooltip
          title={t('reasoning.tokens.are.part.of.output.their.cost.is.2')}
        >
          {t('reasoning')}
        </Tooltip>
      ),
      key: 'reasoning',
      width: 90,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(
          row.reasoningText,
          row.reasoningTokens > 0
            ? formatMoney(row.reasoningCostCny)
            : '-',
        ),
    },
    {
      title: (
        <Tooltip
          title={t('time.to.first.token.ttft.from.request.to.the.first')}
        >
          {t('ttft')}
        </Tooltip>
      ),
      key: 'ttft',
      width: 96,
      align: 'right',
      render: (_, row) => formatLatency(row.avgTtftMs),
    },
    {
      title: (
        <Tooltip
          title={t('generation.speed.output.tokens.total.duration.ttft')}
        >
          {t('gen.speed')}
        </Tooltip>
      ),
      key: 'speed',
      width: 104,
      align: 'right',
      render: (_, row) => formatSpeed(row.tokensPerSecond),
    },
    {
      title: t('estimated.cost'),
      key: 'cost',
      width: 110,
      align: 'right',
      render: (_, row) => formatCost(row.costCny, row.unpricedCalls),
    },
  ];
}

export function sessionColumns(
  showModel: boolean,
  onOpenSession: (sessionId: string) => void,
  showPerformance = false,
): ColumnsType<SessionRow> {
  const columns: ColumnsType<SessionRow> = [
    {
      title: t('title'),
      key: 'title',
      width: 220,
      ellipsis: true,
      render: (_, row) => (
        <a onClick={() => onOpenSession(row.sessionId)}>
          {sessionName(row.title, row.sessionShort)}
        </a>
      ),
    },
  ];

  if (showModel) {
    columns.push({
      title: t('model'),
      key: 'model',
      width: 160,
      ellipsis: true,
      render: (_, row) =>
        row.model === '-' ? '-' : <ModelCell model={row.model} />,
    });
  }

  columns.push(
    {
      title: t('calls'),
      key: 'calls',
      width: 76,
      align: 'right',
      render: (_, row) =>
        row.calls === 0 && row.events > 0 ? (
          <Tooltip
            title={t('events.only.no.model.calls')}
          >
            <span>
              {`0 (${row.events} ${t('events.2')})`}
            </span>
          </Tooltip>
        ) : (
          row.callsText
        ),
    },
    {
      title: t('input'),
      key: 'input',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(
          row.inputText,
          formatMoney(row.uncachedCostCny + row.cacheCostCny),
        ),
    },
    {
      title: t('cached'),
      key: 'cache',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.cacheText, formatMoney(row.cacheCostCny)),
    },
    {
      title: t('hit'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 70,
      align: 'right',
    },
    {
      title: (
        <Tooltip
          title={t('output.tokens.include.reasoning.tokens.and.are.priced')}
        >
          {t('output')}
        </Tooltip>
      ),
      key: 'output',
      width: 90,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.outputText, formatMoney(row.outputCostCny)),
    },
    {
      title: (
        <Tooltip
          title={t('reasoning.tokens.are.part.of.output.their.cost.is.2')}
        >
          {t('reasoning')}
        </Tooltip>
      ),
      key: 'reasoning',
      width: 90,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(
          row.reasoningText,
          row.reasoningTokens > 0
            ? formatMoney(row.reasoningCostCny)
            : '-',
        ),
    },
    {
      title: t('estimated.cost'),
      key: 'cost',
      width: 100,
      align: 'right',
      render: (_, row) => formatCost(row.costCny, row.unpricedCalls),
    },
  );

  if (showPerformance) {
    columns.push(
      {
        title: t('avg.duration'),
        dataIndex: 'avgDurationText',
        key: 'avgDuration',
        width: 100,
        align: 'right',
      },
      {
        title: (
          <Tooltip
            title={t('time.to.first.token.ttft.from.request.to.the.first')}
          >
            {t('ttft')}
          </Tooltip>
        ),
        dataIndex: 'avgTtftText',
        key: 'ttft',
        width: 95,
        align: 'right',
      },
      {
        title: (
          <Tooltip
            title={t('generation.speed.output.tokens.call.duration.ttft')}
          >
            {t('gen.speed')}
          </Tooltip>
        ),
        dataIndex: 'tokensPerSecondText',
        key: 'speed',
        width: 100,
        align: 'right',
      },
      {
        title: t('time'),
        dataIndex: 'timeRangeText',
        key: 'timeRange',
        width: 190,
        align: 'right',
      },
    );
  } else {
    columns.push({
      title: t('last.seen'),
      dataIndex: 'lastSeenText',
      key: 'lastSeen',
      width: 100,
      align: 'right',
    });
  }

  return columns;
}

export function callColumns(
  onOpenSession?: (sessionId: string) => void,
): ColumnsType<CallRow> {
  const columns: ColumnsType<CallRow> = [
    {
      title: t('time'),
      dataIndex: 'timeText',
      key: 'time',
      width: 120,
    },
    {
      title: t('title'),
      key: 'title',
      width: 200,
      ellipsis: true,
      render: (_, row) => {
        const text = callSessionName(row);
        return row.sessionId && onOpenSession ? (
          <a onClick={() => onOpenSession(row.sessionId!)}>{text}</a>
        ) : (
          text
        );
      },
    },
    {
      title: t('agent'),
      key: 'agent',
      width: 150,
      ellipsis: true,
      render: (_, row) => agentLabel(row.agentName) ?? '-',
    },
    {
      title: t('subagent'),
      key: 'subagent',
      width: 130,
      ellipsis: true,
      render: (_, row) =>
        row.subagentName ? (
          <Tag color="purple">{row.subagentName}</Tag>
        ) : (
          '-'
        ),
    },
    {
      title: t('error'),
      key: 'error',
      width: 120,
      ellipsis: true,
      render: (_, row) =>
        row.errorType ? (
          <span style={{ color: '#ff4d4f' }} title={row.finishReasonsText}>
            {row.errorText}
          </span>
        ) : (
          '-'
        ),
    },
  ];

  columns.push(
    {
      title: t('model'),
      key: 'model',
      width: 180,
      ellipsis: true,
      render: (_, row) => <ModelCell model={row.model} />,
    },
    {
      title: t('input'),
      key: 'input',
      width: 110,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(
          row.inputText,
          formatMoney(row.uncachedCostCny + row.cacheCostCny),
        ),
    },
    {
      title: t('cached'),
      key: 'cache',
      width: 110,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.cacheText, formatMoney(row.cacheCostCny)),
    },
    {
      title: t('output'),
      key: 'output',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.outputText, formatMoney(row.outputCostCny)),
    },
    {
      title: t('hit'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 80,
      align: 'right',
    },
    {
      title: (
        <Tooltip
          title={t('total.duration.of.this.call.request.to.response')}
        >
          {t('duration')}
        </Tooltip>
      ),
      key: 'duration',
      width: 100,
      align: 'right',
      render: (_, row) => row.durationText,
    },
    {
      title: (
        <Tooltip
          title={t('time.to.first.token.ttft.from.request.to.the.first')}
        >
          {t('ttft')}
        </Tooltip>
      ),
      key: 'ttft',
      width: 95,
      align: 'right',
      render: (_, row) => formatLatency(row.ttftMs),
    },
    {
      title: (
        <Tooltip
          title={t('generation.speed.output.tokens.call.duration.ttft')}
        >
          {t('gen.speed')}
        </Tooltip>
      ),
      key: 'speed',
      width: 100,
      align: 'right',
      render: (_, row) => formatSpeed(row.tokensPerSecond),
    },
    {
      title: (
        <Tooltip
          title={t('top.is.the.estimated.cost.from.the.price.table.bottom')}
        >
          {t('estimated.cost')}
        </Tooltip>
      ),
      key: 'cost',
      width: 130,
      align: 'right',
      render: (_, row) => (
        <div style={{ lineHeight: 1.25 }}>
          <div>{formatCost(row.costCny)}</div>
          {row.aiuText && (
            <div className="tt-secondary" style={{ fontSize: 11 }}>
              {row.aiuText}
            </div>
          )}
        </div>
      ),
    },
    {
      title: t('period'),
      key: 'period',
      width: 90,
      align: 'right',
      render: (_, row) => periodLabel(row),
    },
  );

  return columns;
}

export const callRowKey = (row: CallRow) =>
  `${row.startTimeMs}-${row.sessionId ?? 'none'}-${row.model}-${row.source ?? 'none'}`;
