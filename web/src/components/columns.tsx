import { Tooltip } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { CallRow, ModelRow, SessionRow } from '../types';


function tokenWithCost(tokenText: string, costText: string) {
  return (
    <div style={{ lineHeight: '1.25' }}>
      <div>{tokenText}</div>
      <div style={{ fontSize: 11, color: 'rgba(0, 0, 0, 0.45)' }}>{costText}</div>
    </div>
  );
}

export function modelColumns(): ColumnsType<ModelRow> {
  return [
    { title: '模型', dataIndex: 'model', key: 'model', width: 220, ellipsis: true },
    { title: '调用', dataIndex: 'callsText', key: 'calls', width: 80, align: 'right' },
    { title: '输入 / 缓存', dataIndex: 'tokensText', key: 'tokens', width: 200, align: 'right' },
    { title: '命中率', dataIndex: 'hitRateText', key: 'hitRate', width: 100, align: 'right' },
    { title: '估算费用', dataIndex: 'costText', key: 'cost', width: 130, align: 'right' },
  ];
}

export function sessionColumns(
  showModel: boolean,
  onOpenSession: (sessionId: string) => void,
): ColumnsType<SessionRow> {
  const columns: ColumnsType<SessionRow> = [
    {
      title: '标题',
      dataIndex: 'sessionDisplay',
      key: 'title',
      width: 220,
      ellipsis: true,
      render: (text: string, row) => (
        <a onClick={() => onOpenSession(row.sessionId)}>{text}</a>
      ),
    },
  ];

  if (showModel) {
    columns.push({
      title: '模型',
      dataIndex: 'model',
      key: 'model',
      width: 180,
      ellipsis: true,
    });
  }

  columns.push(
    { title: '调用', dataIndex: 'callsText', key: 'calls', width: 70, align: 'right' },
    {
      title: '输入',
      key: 'input',
      width: 110,
      align: 'right',
      render: (_, row) => tokenWithCost(row.inputText, row.uncachedCostText),
    },
    {
      title: '缓存',
      key: 'cache',
      width: 110,
      align: 'right',
      render: (_, row) => tokenWithCost(row.cacheText, row.cacheCostText),
    },
    { title: '命中', dataIndex: 'hitRateText', key: 'hitRate', width: 80, align: 'right' },
    {
      title: (
        <Tooltip title="输出 token 包含思考 token，费用按输出单价计算">
          输出
        </Tooltip>
      ),
      key: 'output',
      width: 100,
      align: 'right',
      render: (_, row) => tokenWithCost(row.outputText, row.outputCostText),
    },
    {
      title: (
        <Tooltip title="思考 token 是输出 token 的一部分，对应费用已计入输出费用，不重复累加">
          思考
        </Tooltip>
      ),
      key: 'reasoning',
      width: 100,
      align: 'right',
      render: (_, row) =>
        tokenWithCost(row.reasoningText, row.reasoningTokens > 0 ? row.reasoningCostText : '-'),
    },
    { title: '估算费用', dataIndex: 'costText', key: 'cost', width: 120, align: 'right' },
    { title: '最近', dataIndex: 'lastSeenText', key: 'lastSeen', width: 120, align: 'right' },
  );

  return columns;
}

export function callColumns(
  showTitle: boolean,
  onOpenSession?: (sessionId: string) => void,
): ColumnsType<CallRow> {
  const columns: ColumnsType<CallRow> = [
    { title: '时间', dataIndex: 'timeText', key: 'time', width: 120 },
  ];

  if (showTitle) {
    columns.push({
      title: '标题',
      dataIndex: 'sessionDisplay',
      key: 'title',
      width: 200,
      ellipsis: true,
      render: (text: string, row) =>
        row.sessionId && onOpenSession ? (
          <a onClick={() => onOpenSession(row.sessionId!)}>{text}</a>
        ) : (
          text
        ),
    });
  }

  columns.push(
    { title: '模型', dataIndex: 'model', key: 'model', width: 180, ellipsis: true },
    {
      title: '输入',
      key: 'input',
      width: 110,
      align: 'right',
      render: (_, row) => tokenWithCost(row.inputText, row.uncachedCostText),
    },
    {
      title: '缓存',
      key: 'cache',
      width: 110,
      align: 'right',
      render: (_, row) => tokenWithCost(row.cacheText, row.cacheCostText),
    },
    {
      title: '输出',
      key: 'output',
      width: 100,
      align: 'right',
      render: (_, row) => tokenWithCost(row.outputText, row.outputCostText),
    },
    { title: '命中', dataIndex: 'hitRateText', key: 'hitRate', width: 80, align: 'right' },
    { title: '估算费用', dataIndex: 'costText', key: 'cost', width: 120, align: 'right' },
    { title: '时段', dataIndex: 'periodText', key: 'period', width: 80, align: 'right' },
  );

  return columns;
}

export const callRowKey = (row: CallRow) =>
  `${row.startTimeMs}-${row.sessionId ?? 'none'}-${row.model}`;
