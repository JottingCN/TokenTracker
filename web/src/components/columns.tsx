import { Tooltip } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { CallRow, ModelRow, SessionRow } from '../types';
import { translate } from '../ui';
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

function sessionName(title: string | null | undefined, short: string): string {
  return title?.trim() || `${translate('未命名', 'Unnamed')} · ${short}`;
}

function callSessionName(row: CallRow): string {
  if (!row.sessionId) {
    return translate('辅助调用', 'Utility call');
  }
  return sessionName(row.title, row.sessionShort);
}

function periodLabel(row: CallRow): string {
  if (row.costCny === null) {
    return '-';
  }
  switch (row.pricingPeriod) {
    case 'peak':
      return translate('高峰', 'Peak');
    case 'idle':
      return translate('空闲', 'Idle');
    case 'flat':
      return translate('固定', 'Flat');
    default:
      return '-';
  }
}

export function modelColumns(): ColumnsType<ModelRow> {
  return [
    {
      title: translate('模型', 'Model'),
      dataIndex: 'model',
      key: 'model',
      width: 200,
      ellipsis: true,
    },
    {
      title: translate('调用', 'Calls'),
      dataIndex: 'callsText',
      key: 'calls',
      width: 70,
      align: 'right',
    },
    {
      title: translate('输入 / 缓存', 'Input / Cached'),
      dataIndex: 'tokensText',
      key: 'tokens',
      width: 180,
      align: 'right',
    },
    {
      title: translate('命中率', 'Hit rate'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 90,
      align: 'right',
    },
    {
      title: translate('估算费用', 'Estimated cost'),
      key: 'cost',
      width: 120,
      align: 'right',
      render: (_, row) => formatCost(row.costCny, row.unpricedCalls),
    },
  ];
}

export function sessionColumns(
  showModel: boolean,
  onOpenSession: (sessionId: string) => void,
): ColumnsType<SessionRow> {
  const columns: ColumnsType<SessionRow> = [
    {
      title: translate('标题', 'Title'),
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
      title: translate('模型', 'Model'),
      dataIndex: 'model',
      key: 'model',
      width: 160,
      ellipsis: true,
    });
  }

  columns.push(
    {
      title: translate('调用', 'Calls'),
      dataIndex: 'callsText',
      key: 'calls',
      width: 60,
      align: 'right',
    },
    {
      title: translate('输入', 'Input'),
      key: 'input',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.inputText, formatMoney(row.uncachedCostCny)),
    },
    {
      title: translate('缓存', 'Cached'),
      key: 'cache',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.cacheText, formatMoney(row.cacheCostCny)),
    },
    {
      title: translate('命中', 'Hit'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 70,
      align: 'right',
    },
    {
      title: (
        <Tooltip
          title={translate(
            '输出 token 包含思考 token，费用按输出单价计算',
            'Output tokens include reasoning tokens and are priced at the output rate.',
          )}
        >
          {translate('输出', 'Output')}
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
          title={translate(
            '思考 token 是输出 token 的一部分，对应费用已计入输出费用，不重复累加',
            'Reasoning tokens are part of output; their cost is already included.',
          )}
        >
          {translate('思考', 'Reasoning')}
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
      title: translate('估算费用', 'Estimated cost'),
      key: 'cost',
      width: 100,
      align: 'right',
      render: (_, row) => formatCost(row.costCny, row.unpricedCalls),
    },
    {
      title: translate('最近', 'Last seen'),
      dataIndex: 'lastSeenText',
      key: 'lastSeen',
      width: 100,
      align: 'right',
    },
  );

  return columns;
}

export function callColumns(
  showTitle: boolean,
  onOpenSession?: (sessionId: string) => void,
): ColumnsType<CallRow> {
  const columns: ColumnsType<CallRow> = [
    {
      title: translate('时间', 'Time'),
      dataIndex: 'timeText',
      key: 'time',
      width: 120,
    },
  ];

  if (showTitle) {
    columns.push({
      title: translate('标题', 'Title'),
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
    });
  }

  columns.push(
    {
      title: translate('模型', 'Model'),
      dataIndex: 'model',
      key: 'model',
      width: 180,
      ellipsis: true,
    },
    {
      title: translate('输入', 'Input'),
      key: 'input',
      width: 110,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.inputText, formatMoney(row.uncachedCostCny)),
    },
    {
      title: translate('缓存', 'Cached'),
      key: 'cache',
      width: 110,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.cacheText, formatMoney(row.cacheCostCny)),
    },
    {
      title: translate('输出', 'Output'),
      key: 'output',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.outputText, formatMoney(row.outputCostCny)),
    },
    {
      title: translate('命中', 'Hit'),
      dataIndex: 'hitRateText',
      key: 'hitRate',
      width: 80,
      align: 'right',
    },
    {
      title: (
        <Tooltip
          title={translate(
            '上方为按价格表估算的费用；下方为 Copilot 返回的实际 AIC（仅扩展管线提供）',
            'Top is the estimated cost from the price table; bottom is the actual AIC reported by Copilot (extension pipeline only).',
          )}
        >
          {translate('估算费用', 'Estimated cost')}
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
      title: translate('时段', 'Period'),
      key: 'period',
      width: 90,
      align: 'right',
      render: (_, row) => periodLabel(row),
    },
  );

  return columns;
}

export const callRowKey = (row: CallRow) =>
  `${row.startTimeMs}-${row.sessionId ?? 'none'}-${row.model}`;
