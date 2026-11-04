import { useEffect, useState } from 'react';
import {
  Alert,
  Descriptions,
  Drawer,
  Empty,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type {
  StorageTableInfo,
  SystemAccessInfo,
  SystemInfo,
  SystemPathInfo,
} from '../types';
import { useUi } from '../ui';

interface SystemInfoDrawerProps {
  open: boolean;
  onClose: () => void;
}

export function SystemInfoDrawer({ open, onClose }: SystemInfoDrawerProps) {
  const { tr } = useUi();
  const [data, setData] = useState<SystemInfo | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;
    setLoading(true);
    fetch('/api/system', { cache: 'no-store' })
      .then((response) => (response.ok ? response.json() : null))
      .then((payload: SystemInfo | null) => {
        if (!cancelled) {
          setData(payload);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setData(null);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [open]);

  const columns: ColumnsType<StorageTableInfo> = [
    {
      title: tr('表', 'Table'),
      dataIndex: 'name',
      key: 'name',
    },
    {
      title: tr('行数', 'Rows'),
      dataIndex: 'rows',
      key: 'rows',
      align: 'right',
      render: (value: number) => formatNumber(value),
    },
  ];

  return (
    <Drawer
      title={tr('系统信息', 'System info')}
      open={open}
      onClose={onClose}
      width="min(820px, 92vw)"
    >
      {loading ? (
        <div style={{ padding: 24, textAlign: 'center' }}>
          <Spin />
        </div>
      ) : !data ? (
        <Empty description={tr('无法读取系统信息', 'Unable to read system info')} />
      ) : (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Descriptions
            size="small"
            column={1}
            bordered
            title={tr('服务', 'Server')}
            items={[
              {
                key: 'version',
                label: tr('版本', 'Version'),
                children: `${data.server.name} ${data.server.version}`,
              },
              {
                key: 'port',
                label: tr('端口', 'Port'),
                children: `${data.server.port}`,
              },
              {
                key: 'started',
                label: tr('启动时间', 'Started'),
                children: data.server.startedAt,
              },
              {
                key: 'build',
                label: 'Build',
                children: data.server.buildId ?? '-',
              },
              {
                key: 'features',
                label: tr('功能', 'Features'),
                children: (
                  <Space wrap size={4}>
                    {data.server.features.map((feature) => (
                      <Tag key={feature}>{feature}</Tag>
                    ))}
                  </Space>
                ),
              },
              {
                key: 'ahp',
                label: 'AHP',
                children: `${data.ahp.state} · ${tr('已连接', 'connected')}: ${
                  data.ahp.connected ? tr('是', 'yes') : tr('否', 'no')
                } · ${data.ahp.sessions} ${tr('会话', 'sessions')}`,
              },
            ]}
          />

          <Descriptions
            size="small"
            column={1}
            bordered
            title={tr('数据存储', 'Storage')}
            items={[
              {
                key: 'dataDir',
                label: tr('数据目录', 'Data directory'),
                children: <Typography.Text copyable>{data.dataDirectory}</Typography.Text>,
              },
              {
                key: 'database',
                label: tr('数据库', 'Database'),
                children: (
                  <Typography.Text copyable>
                    {data.storage.databasePath}
                  </Typography.Text>
                ),
              },
              {
                key: 'size',
                label: tr('总大小', 'Total size'),
                children: formatBytes(
                  data.storage.sizeBytes +
                    data.storage.walBytes +
                    data.storage.shmBytes,
                ),
              },
              {
                key: 'files',
                label: tr('DB / WAL / SHM', 'DB / WAL / SHM'),
                children: `${formatBytes(data.storage.sizeBytes)} / ${formatBytes(
                  data.storage.walBytes,
                )} / ${formatBytes(data.storage.shmBytes)}`,
              },
              {
                key: 'pages',
                label: tr('页大小 / 页数', 'Page size / count'),
                children: `${formatBytes(data.storage.pageSize)} / ${formatNumber(
                  data.storage.pageCount,
                )} · ${data.storage.journalMode}`,
              },
              {
                key: 'spans',
                label: tr('Span', 'Spans'),
                children: `${tr('原始', 'raw')} ${formatNumber(
                  data.storage.rawSpans,
                )} · combined ${formatNumber(data.storage.combinedSpans)}`,
              },
              {
                key: 'outcomes',
                label: tr('结果指标点', 'Outcome metric points'),
                children: formatNumber(data.storage.outcomeMetricPoints),
              },
              {
                key: 'retention',
                label: tr('保留天数', 'Retention'),
                children:
                  data.retentionDays === 0
                    ? tr('永久', 'Forever')
                    : `${data.retentionDays} ${tr('天', 'days')}`,
              },
              {
                key: 'last',
                label: tr('最近接收', 'Last received'),
                children: data.lastReceivedText,
              },
            ]}
          />

          {data.storage.outcomeMetricPoints === 0 && (
            <Alert
              type="info"
              showIcon
              message={tr(
                '尚未收到结果类指标',
                'No outcome metrics received yet',
              )}
              description={tr(
                '编辑接受率、代码行数、反馈、PR 等来自 OTel metrics。当前 metric_points 中没有结果类指标，因此 Dashboard 的“编辑与结果指标”卡片不会显示；这不是统计错误。',
                'Edit acceptance, LOC, feedback and PR metrics come from OTel metrics. metric_points currently has no outcome metrics, so the dashboard outcomes card is hidden. This is not a calculation error.',
              )}
            />
          )}

          <div>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {tr('读取与访问路径（只读）', 'Read and access paths (read-only)')}
            </Typography.Title>
            <Table<SystemPathInfo>
              size="small"
              rowKey={(row) => `${row.kind}:${row.path}`}
              pagination={false}
              columns={[
                {
                  title: tr('名称', 'Name'),
                  dataIndex: 'label',
                  key: 'label',
                  width: 190,
                  render: (value: string, row) => (
                    <Space direction="vertical" size={2}>
                      <Typography.Text>{value}</Typography.Text>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {row.purpose}
                      </Typography.Text>
                    </Space>
                  ),
                },
                {
                  title: tr('路径', 'Path'),
                  dataIndex: 'path',
                  key: 'path',
                  render: (value: string) => (
                    <Typography.Text
                      copyable
                      style={{ fontSize: 12, wordBreak: 'break-all' }}
                    >
                      {value}
                    </Typography.Text>
                  ),
                },
                {
                  title: tr('状态', 'Status'),
                  key: 'status',
                  width: 150,
                  render: (_, row) => (
                    <Space direction="vertical" size={2}>
                      <Tag
                        color={row.exists ? 'green' : 'default'}
                        style={{ marginInlineEnd: 0 }}
                      >
                        {row.exists
                          ? tr('存在', 'Exists')
                          : tr('缺失', 'Missing')}
                      </Tag>
                      <Typography.Text
                        type="secondary"
                        style={{ fontSize: 11 }}
                      >
                        {row.monitor}
                      </Typography.Text>
                      {row.lastModifiedText && (
                        <Typography.Text
                          type="secondary"
                          style={{ fontSize: 11 }}
                        >
                          {row.lastModifiedText}
                        </Typography.Text>
                      )}
                    </Space>
                  ),
                },
              ]}
              dataSource={data.watchedPaths}
            />
            <Table<SystemAccessInfo>
              size="small"
              style={{ marginTop: 8 }}
              rowKey={(row) => `${row.kind}:${row.target}`}
              pagination={false}
              columns={[
                {
                  title: tr('访问', 'Access'),
                  dataIndex: 'label',
                  key: 'label',
                  width: 190,
                  render: (value: string, row) => (
                    <Space direction="vertical" size={2}>
                      <Typography.Text>{value}</Typography.Text>
                      <Tag style={{ marginInlineEnd: 0 }}>{row.kind}</Tag>
                    </Space>
                  ),
                },
                {
                  title: tr('目标', 'Target'),
                  dataIndex: 'target',
                  key: 'target',
                  render: (value: string) => (
                    <Typography.Text
                      copyable
                      style={{ fontSize: 12, wordBreak: 'break-all' }}
                    >
                      {value}
                    </Typography.Text>
                  ),
                },
              ]}
              dataSource={data.accesses}
            />
          </div>

          <Descriptions
            size="small"
            column={1}
            bordered
            title={tr('监听', 'Receiver')}
            items={[
              {
                key: 'endpoint',
                label: tr('端点', 'Endpoint'),
                children: <Typography.Text copyable>{data.receiver.endpoint}</Typography.Text>,
              },
              {
                key: 'batches',
                label: tr('批次 / Span', 'Batches / spans'),
                children: `${formatNumber(data.receiver.batches)} / ${formatNumber(
                  data.receiver.spans,
                )}`,
              },
              {
                key: 'error',
                label: tr('最近错误', 'Last error'),
                children: data.receiver.lastError ? (
                  <Typography.Text type="danger">
                    {data.receiver.lastError}
                  </Typography.Text>
                ) : (
                  '-'
                ),
              },
            ]}
          />

          <div>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {tr('数据表', 'Tables')}
            </Typography.Title>
            <Table<StorageTableInfo>
              size="small"
              rowKey="name"
              pagination={false}
              columns={columns}
              dataSource={data.storage.tables}
            />
          </div>
        </Space>
      )}
    </Drawer>
  );
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat().format(value);
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB'];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }

  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}
