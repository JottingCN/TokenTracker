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
import {
  ahpStateLabel,
  systemAccessLabel,
  systemMonitorLabel,
  systemPathLabel,
  systemPathPurpose,
} from '../utils/labels';
import { useUi } from '../ui';

interface SystemInfoDrawerProps {
  open: boolean;
  onClose: () => void;
}

export function SystemInfoDrawer({ open, onClose }: SystemInfoDrawerProps) {
  const { t } = useUi();
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
      title: t('table'),
      dataIndex: 'name',
      key: 'name',
    },
    {
      title: t('rows'),
      dataIndex: 'rows',
      key: 'rows',
      align: 'right',
      render: (value: number) => formatNumber(value),
    },
  ];

  return (
    <Drawer
      title={t('system.info')}
      open={open}
      onClose={onClose}
      width="min(820px, 92vw)"
    >
      {loading ? (
        <div style={{ padding: 24, textAlign: 'center' }}>
          <Spin />
        </div>
      ) : !data ? (
        <Empty description={t('unable.to.read.system.info')} />
      ) : (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Descriptions
            size="small"
            column={1}
            bordered
            styles={{ label: { width: 140, whiteSpace: 'nowrap' } }}
            title={t('server')}
            items={[
              {
                key: 'version',
                label: t('version'),
                children: `${data.server.name} ${data.server.version}`,
              },
              {
                key: 'port',
                label: t('port'),
                children: `${data.server.port}`,
              },
              {
                key: 'started',
                label: t('started'),
                children: data.server.startedAt,
              },
              {
                key: 'build',
                label: 'Build',
                children: data.server.buildId ?? '-',
              },
              {
                key: 'features',
                label: t('features'),
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
                children: `${ahpStateLabel(data.ahp.state)} · ${
                  data.ahp.sessions
                } ${t('sessions.2')}`,
              },
            ]}
          />

          <Descriptions
            size="small"
            column={1}
            bordered
            styles={{ label: { width: 140, whiteSpace: 'nowrap' } }}
            title={t('storage')}
            items={[
              {
                key: 'dataDir',
                label: t('data.directory'),
                children: <Typography.Text copyable>{data.dataDirectory}</Typography.Text>,
              },
              {
                key: 'database',
                label: t('database'),
                children: (
                  <Typography.Text copyable>
                    {data.storage.databasePath}
                  </Typography.Text>
                ),
              },
              {
                key: 'size',
                label: t('total.size'),
                children: formatBytes(
                  data.storage.sizeBytes +
                    data.storage.walBytes +
                    data.storage.shmBytes,
                ),
              },
              {
                key: 'files',
                label: t('db.wal.shm'),
                children: `${formatBytes(data.storage.sizeBytes)} / ${formatBytes(
                  data.storage.walBytes,
                )} / ${formatBytes(data.storage.shmBytes)}`,
              },
              {
                key: 'pages',
                label: t('page.size.count'),
                children: `${formatBytes(data.storage.pageSize)} / ${formatNumber(
                  data.storage.pageCount,
                )} · ${data.storage.journalMode}`,
              },
              {
                key: 'spans',
                label: t('spans'),
                children: `${t('raw')} ${formatNumber(
                  data.storage.rawSpans,
                )} · ${t('combined')} ${formatNumber(
                  data.storage.combinedSpans,
                )}`,
              },
              {
                key: 'outcomes',
                label: t('outcome.metric.points'),
                children: formatNumber(data.storage.outcomeMetricPoints),
              },
              {
                key: 'retention',
                label: t('retention'),
                children:
                  data.retentionDays === 0
                    ? t('forever')
                    : `${data.retentionDays} ${t('days')}`,
              },
              {
                key: 'last',
                label: t('last.received'),
                children: data.lastReceivedText,
              },
            ]}
          />

          {data.storage.outcomeMetricPoints === 0 && (
            <Alert
              type="info"
              showIcon
              message={t('no.outcome.metrics.received.yet')}
              description={t('outcome.metrics.explanation', {
                batches: data.receiver.metricBatches,
                points: data.receiver.metricPoints,
                rejected: data.receiver.metricRejectedBatches,
              })}
            />
          )}

          <div>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {t('read.and.access.paths.read.only')}
            </Typography.Title>
            <Table<SystemPathInfo>
              size="small"
              bordered
              rowKey={(row) => `${row.kind}:${row.path}`}
              pagination={false}
              columns={[
                {
                  title: t('name'),
                  key: 'name',
                  width: 220,
                  render: (_, row) => (
                    <Space direction="vertical" size={2}>
                      <Typography.Text
                        ellipsis
                        title={systemPathLabel(row.key)}
                      >
                        {systemPathLabel(row.key)}
                      </Typography.Text>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {systemPathPurpose(row.key)}
                      </Typography.Text>
                    </Space>
                  ),
                },
                {
                  title: t('path'),
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
                  title: t('status'),
                  key: 'status',
                  width: 160,
                  render: (_, row) => (
                    <Space direction="vertical" size={2}>
                      <Tag
                        color={row.exists ? 'green' : 'default'}
                        style={{ marginInlineEnd: 0 }}
                      >
                        {row.exists
                          ? t('exists')
                          : t('missing')}
                      </Tag>
                      <Typography.Text
                        type="secondary"
                        style={{ fontSize: 11 }}
                      >
                        {systemMonitorLabel(row.monitor)}
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
              bordered
              style={{ marginTop: 8 }}
              rowKey={(row) => `${row.kind}:${row.target}`}
              pagination={false}
              columns={[
                {
                  title: t('access'),
                  key: 'name',
                  width: 220,
                  render: (_, row) => (
                    <Space direction="vertical" size={2}>
                      <Typography.Text
                        ellipsis
                        title={systemAccessLabel(row.key)}
                      >
                        {systemAccessLabel(row.key)}
                      </Typography.Text>
                      <Tag style={{ marginInlineEnd: 0 }}>{row.kind}</Tag>
                    </Space>
                  ),
                },
                {
                  title: t('target'),
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
            styles={{ label: { width: 140, whiteSpace: 'nowrap' } }}
            title={t('receiver')}
            items={[
              {
                key: 'endpoint',
                label: t('endpoint'),
                children: <Typography.Text copyable>{data.receiver.endpoint}</Typography.Text>,
              },
              {
                key: 'batches',
                label: t('batches.spans'),
                children: `${formatNumber(data.receiver.batches)} / ${formatNumber(
                  data.receiver.spans,
                )}`,
              },
              {
                key: 'error',
                label: t('last.error'),
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
              {t('tables')}
            </Typography.Title>
            <Table<StorageTableInfo>
              size="small"
              bordered
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
