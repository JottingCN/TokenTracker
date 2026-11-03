import { useEffect, useMemo, useState } from 'react';
import {
  FolderOutlined,
  HistoryOutlined,
  HomeOutlined,
  InfoCircleOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import {
  Badge,
  Button,
  DatePicker,
  Descriptions,
  Layout,
  Menu,
  Modal,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
} from 'antd';
import type { MenuProps } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import dayjs from 'dayjs';
import { ModelIcon } from './components/ModelIcon';
import { PriceSettingsDrawer } from './components/PriceSettingsDrawer';
import { SystemInfoDrawer } from './components/SystemInfoDrawer';
import { CallsPage } from './pages/CallsPage';
import { DashboardPage } from './pages/DashboardPage';
import { ModelPage } from './pages/ModelPage';
import { SessionPage } from './pages/SessionPage';
import { useRealtime } from './useRealtime';
import type {
  AhpSessionInfo,
  DashboardPayload,
} from './types';
import { translate, useUi } from './ui';
import { providerOf, shortModelName } from './utils/model';

const { Header, Sider, Content } = Layout;
const SIDER_KEY = 'tokentracker.siderCollapsed';

function loadSiderCollapsed(): boolean {
  try {
    const value = localStorage.getItem(SIDER_KEY);
    return value === null ? true : value === '1';
  } catch {
    return true;
  }
}

type Route =
  | { view: 'dashboard' }
  | { view: 'calls' }
  | { view: 'model'; id: string }
  | { view: 'session'; id: string; fromModel?: string };

function parseRoute(): Route {
  const hash = location.hash.replace(/^#/, '');
  const [path, query] = hash.split('?');
  const parts = path.split('/').filter(Boolean);

  if (parts[0] === 'calls') {
    return { view: 'calls' };
  }
  if (parts[0] === 'model' && parts[1]) {
    return { view: 'model', id: decodeURIComponent(parts[1]) };
  }
  if (parts[0] === 'session' && parts[1]) {
    const params = new URLSearchParams(query ?? '');
    const fromModel = params.get('model') ?? undefined;
    return { view: 'session', id: decodeURIComponent(parts[1]), fromModel };
  }
  return { view: 'dashboard' };
}

function buildMenu(dashboard: DashboardPayload | null): MenuProps['items'] {
  const items: NonNullable<MenuProps['items']> = [
    { key: 'home', icon: <HomeOutlined />, label: translate('主页', 'Home') },
    {
      key: 'calls',
      icon: <HistoryOutlined />,
      label: translate('调用明细', 'Call details'),
    },
  ];
  if (!dashboard) {
    return items;
  }

  const groups = new Map<string, string[]>();
  for (const row of dashboard.models) {
    const provider = providerOf(row.model);
    const list = groups.get(provider) ?? [];
    list.push(row.model);
    groups.set(provider, list);
  }

  for (const [provider, models] of [...groups.entries()].sort((a, b) =>
    a[0].localeCompare(b[0]),
  )) {
    const sortedModels = models.sort((a, b) => a.localeCompare(b));
    items.push({
      key: `provider:${provider}`,
      icon: sortedModels[0] ? (
        <span
          style={{
            display: 'inline-flex',
            marginInlineEnd: 6,
          }}
        >
          <ModelIcon model={sortedModels[0]} size={16} tooltip={false} />
        </span>
      ) : (
        <FolderOutlined />
      ),
      label: <span style={{ marginInlineStart: 2 }}>{provider}</span>,
      children: sortedModels.map((model) => ({
        key: `model:${model}`,
        label: (
          <span
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
            }}
          >
            <ModelIcon model={model} size={14} />
            <span>{shortModelName(model)}</span>
          </span>
        ),
      })),
    });
  }

  return items;
}

type RangeMode = number | 'custom';

export default function App() {
  const {
    lang,
    tr,
    themeMode,
    setThemeMode,
    languageMode,
    setLanguageMode,
  } = useUi();
  const [route, setRoute] = useState<Route>(parseRoute);
  const [rangeMode, setRangeMode] = useState<RangeMode>(2);
  const [siderCollapsed, setSiderCollapsed] =
    useState<boolean>(loadSiderCollapsed);
  const [customRange, setCustomRange] = useState<[number, number] | null>(null);
  const [openKeys, setOpenKeys] = useState<string[]>([]);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [systemOpen, setSystemOpen] = useState(false);
  const [ahpOpen, setAhpOpen] = useState(false);
  const [sessionMeta, setSessionMeta] = useState<{
    title?: string;
    subtitle?: string;
  } | null>(null);

  useEffect(() => {
    const handler = () => setRoute(parseRoute());
    window.addEventListener('hashchange', handler);
    return () => window.removeEventListener('hashchange', handler);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(SIDER_KEY, siderCollapsed ? '1' : '0');
    } catch {
      // Storage is best-effort.
    }
  }, [siderCollapsed]);

  const numericRange = typeof rangeMode === 'number' ? rangeMode : 2;
  const customFrom = rangeMode === 'custom' ? customRange?.[0] : undefined;
  const customTo = rangeMode === 'custom' ? customRange?.[1] : undefined;

  const { data: dashboard, connected } = useRealtime<DashboardPayload>({
    view: 'dashboard',
    range: numericRange,
    utility: true,
    fromMs: customFrom,
    toMs: customTo,
  });

  const ahpStatus: 'success' | 'processing' | 'default' | 'warning' =
    dashboard?.ahp?.connected
    ? 'success'
    : dashboard?.ahp?.state === 'connecting'
      ? 'processing'
      : dashboard?.ahp?.state === 'disabled'
        ? 'default'
        : 'warning';
  const ahpText = dashboard?.ahp
    ? dashboard.ahp.connected
      ? tr(
          `AHP 已连接 · ${dashboard.ahp.sessions} 会话`,
          `AHP connected · ${dashboard.ahp.sessions} sessions`,
        )
      : dashboard.ahp.state === 'connecting'
        ? tr('AHP 连接中', 'AHP connecting')
        : dashboard.ahp.state === 'disabled'
          ? tr('AHP 不可用', 'AHP unavailable')
          : tr('AHP 未连接', 'AHP not connected')
    : null;
  const ahpStateText = dashboard?.ahp
    ? dashboard.ahp.connected
      ? tr('已连接', 'Connected')
      : dashboard.ahp.state === 'connecting'
        ? tr('连接中', 'Connecting')
        : dashboard.ahp.state === 'disabled'
          ? tr('不可用', 'Unavailable')
          : tr('未连接', 'Not connected')
    : '-';
  const ahpSessionColumns: ColumnsType<AhpSessionInfo> = [
    {
      title: tr('标题', 'Title'),
      dataIndex: 'title',
      key: 'title',
      width: 220,
      ellipsis: true,
    },
    {
      title: tr('来源', 'Provider'),
      dataIndex: 'provider',
      key: 'provider',
      width: 100,
      ellipsis: true,
    },
    {
      title: tr('状态', 'Status'),
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: string) => {
        if (status === 'in_progress') {
          return <Tag color="processing">{tr('进行中', 'In progress')}</Tag>;
        }
        if (status === 'input_needed') {
          return <Tag color="warning">{tr('等待输入', 'Needs input')}</Tag>;
        }
        if (status === 'error') {
          return <Tag color="error">{tr('错误', 'Error')}</Tag>;
        }
        if (status === 'idle') {
          return <Tag>{tr('空闲', 'Idle')}</Tag>;
        }
        return <Tag>{tr('未知', 'Unknown')}</Tag>;
      },
    },
    {
      title: tr('工作区', 'Workspace'),
      dataIndex: 'workspacePath',
      key: 'workspace',
      width: 240,
      ellipsis: true,
      render: (workspacePath: string | null) => workspacePath || '-',
    },
    {
      title: tr('活动', 'Activity'),
      dataIndex: 'activity',
      key: 'activity',
      width: 140,
      ellipsis: true,
      render: (activity: string | null) => activity || '-',
    },
    {
      title: tr('最近修改', 'Modified'),
      dataIndex: 'modifiedAt',
      key: 'modified',
      width: 120,
      render: (modifiedAt: string) => {
        const parsed = dayjs(modifiedAt);
        return parsed.isValid() ? parsed.format('MM-DD HH:mm') : '-';
      },
    },
    {
      title: tr('会话数', 'Chats'),
      dataIndex: 'chats',
      key: 'chats',
      width: 70,
      align: 'right',
    },
  ];

  const menuItems = useMemo(
    () => buildMenu(dashboard),
    [dashboard, lang],
  );

  useEffect(() => {
    if (siderCollapsed || !dashboard) {
      return;
    }

    setOpenKeys((current) =>
      current.length > 0
        ? current
        : [...new Set(dashboard.models.map((row) => `provider:${providerOf(row.model)}`))],
    );
  }, [dashboard, siderCollapsed]);

  const selectedKeys =
    route.view === 'dashboard'
      ? ['home']
      : route.view === 'calls'
        ? ['calls']
        : route.view === 'model'
          ? [`model:${route.id}`]
          : route.fromModel
            ? [`model:${route.fromModel}`]
            : [];

  const onMenuClick: MenuProps['onClick'] = ({ key }) => {
    const value = String(key);
    if (value === 'home') {
      location.hash = '#/';
    } else if (value === 'calls') {
      location.hash = '#/calls';
    } else if (value.startsWith('model:')) {
      location.hash = `#/model/${encodeURIComponent(value.slice('model:'.length))}`;
    }
  };

  const onRangeChange = (value: number | 'custom') => {
    setRangeMode(value);
    if (value === 'custom' && !customRange) {
      const now = Date.now();
      setCustomRange([now - 7 * 24 * 3600 * 1000, now]);
    }
  };

  const openSessionFromDashboard = (sessionId: string) => {
    location.hash = `#/session/${encodeURIComponent(sessionId)}`;
  };

  const openSessionFromModel = (sessionId: string) => {
    const params = route.view === 'model' ? `?model=${encodeURIComponent(route.id)}` : '';
    location.hash = `#/session/${encodeURIComponent(sessionId)}${params}`;
  };

  const activeSessionId = route.view === 'session' ? route.id : null;

  useEffect(() => {
    setSessionMeta(null);
  }, [activeSessionId]);

  const pageMeta =
    route.view === 'dashboard'
      ? {
          title: tr('主页', 'Home'),
          subtitle: tr('汇总与趋势', 'Overview'),
        }
      : route.view === 'calls'
        ? {
            title: tr('调用明细', 'Call details'),
            subtitle: tr('多维度检索', 'Search and filters'),
          }
        : route.view === 'model'
          ? {
              title: `${providerOf(route.id)} · ${shortModelName(route.id)}`,
              subtitle: route.id,
            }
          : {
              title:
                sessionMeta?.title ??
                `${tr('会话', 'Session')} · ${
                  route.id.length > 12 ? `${route.id.slice(0, 8)}…` : route.id
                }`,
              subtitle: sessionMeta?.subtitle ?? route.id,
            };
  const pageTitle = pageMeta.title;

  useEffect(() => {
    document.title = `${pageTitle} · Token Tracker`;
  }, [pageTitle]);

  return (
    <>
      <Layout
        style={{
          height: '100vh',
          overflow: 'hidden',
          background: 'var(--ant-color-bg-layout)',
        }}
      >
        <Sider
          theme="light"
          width={230}
          collapsedWidth={64}
          collapsed={siderCollapsed}
          trigger={null}
          style={{
            background: 'var(--ant-color-bg-container)',
            borderRight: '1px solid var(--ant-color-border-secondary)',
          }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: siderCollapsed ? 'center' : 'space-between',
                minHeight: 52,
                padding: siderCollapsed ? '12px 0' : '18px 16px 12px',
              }}
            >
              {!siderCollapsed && (
                <span
                  style={{
                    fontSize: 16,
                    fontWeight: 600,
                    whiteSpace: 'nowrap',
                  }}
                >
                  Token Tracker
                </span>
              )}
              <Tooltip
                title={
                  siderCollapsed
                    ? tr('展开导航', 'Expand navigation')
                    : tr('收起导航', 'Collapse navigation')
                }
              >
                <Button
                  type="text"
                  size="small"
                  icon={
                    siderCollapsed ? (
                      <MenuUnfoldOutlined />
                    ) : (
                      <MenuFoldOutlined />
                    )
                  }
                  onClick={() => setSiderCollapsed((value) => !value)}
                />
              </Tooltip>
            </div>
            <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
              <Menu
                mode="inline"
                inlineCollapsed={siderCollapsed}
                triggerSubMenuAction="click"
                items={menuItems}
                selectedKeys={selectedKeys}
                openKeys={siderCollapsed ? undefined : openKeys}
                onOpenChange={(keys) => setOpenKeys(keys)}
                onClick={onMenuClick}
                style={{
                  borderInlineEnd: 'none',
                  flex: 1,
                  minHeight: 0,
                  overflow: 'auto',
                }}
              />
            </div>
            <div
              style={{
                padding: siderCollapsed ? '8px 0' : '8px 8px 8px 28px',
                borderTop: '1px solid var(--ant-color-border-secondary)',
                display: 'flex',
                flexDirection: siderCollapsed ? 'row' : 'column',
                alignItems: siderCollapsed ? 'center' : 'flex-start',
                justifyContent: siderCollapsed ? 'center' : 'flex-start',
                gap: siderCollapsed ? 12 : 8,
              }}
            >
              <Tooltip
                placement="right"
                title={
                  connected
                    ? tr('实时连接', 'Live')
                    : tr('连接断开', 'Disconnected')
                }
              >
                <span>
                  <Badge
                    status={connected ? 'processing' : 'error'}
                    text={
                      siderCollapsed
                        ? undefined
                        : connected
                          ? tr('实时连接', 'Live')
                          : tr('连接断开', 'Disconnected')
                    }
                  />
                </span>
              </Tooltip>
              {ahpText && (
                <Tooltip
                  placement="right"
                  title={tr(
                    'AHP 用于同步 Agent Host 的实时会话标题；点击查看详情。',
                    'AHP syncs live Agent Host session titles; click for details.',
                  )}
                >
                  <span
                    role="button"
                    tabIndex={0}
                    onClick={() => setAhpOpen(true)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        setAhpOpen(true);
                      }
                    }}
                    style={{ cursor: 'pointer', textAlign: 'left' }}
                  >
                    <Badge
                      status={ahpStatus}
                      text={siderCollapsed ? undefined : ahpText}
                    />
                  </span>
                </Tooltip>
              )}
            </div>
            <div
              style={{
                padding: '4px 0',
                borderTop: '1px solid var(--ant-color-border-secondary)',
              }}
            >
              <Menu
                mode="inline"
                inlineCollapsed={siderCollapsed}
                selectable={false}
                items={[
                  {
                    key: 'system',
                    icon: <InfoCircleOutlined />,
                    label: tr('系统信息', 'System info'),
                  },
                  {
                    key: 'settings',
                    icon: <SettingOutlined />,
                    label: tr('设置', 'Settings'),
                  },
                ]}
                onClick={({ key }) => {
                  if (key === 'settings') {
                    setSettingsOpen(true);
                  } else if (key === 'system') {
                    setSystemOpen(true);
                  }
                }}
                style={{ borderInlineEnd: 'none' }}
              />
            </div>
          </div>
        </Sider>

        <Layout
          style={{
            height: '100vh',
            overflow: 'hidden',
            flex: 1,
            minWidth: 0,
          }}
        >
          <Header
            style={{
              position: 'relative',
              background: 'var(--ant-color-bg-container)',
              borderBottom: '1px solid var(--ant-color-border-secondary)',
              padding: '8px 20px',
              height: 'auto',
              minHeight: 56,
              lineHeight: 'normal',
              display: 'flex',
              alignItems: 'center',
              columnGap: 16,
              rowGap: 8,
              flex: '0 0 auto',
            }}
          >
            <div
              style={{
                position: 'absolute',
                left: '50%',
                top: '50%',
                transform: 'translate(-50%, -50%)',
                maxWidth: '52%',
                display: 'flex',
                alignItems: 'baseline',
                gap: 8,
                overflow: 'hidden',
                whiteSpace: 'nowrap',
                pointerEvents: 'none',
              }}
            >
              <span
                style={{
                  minWidth: 0,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  fontSize: 15,
                  fontWeight: 600,
                }}
              >
                {pageMeta.title}
              </span>
              {pageMeta.subtitle && (
                <span
                  style={{
                    minWidth: 0,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    fontSize: 11,
                    color: 'var(--ant-color-text-secondary)',
                  }}
                >
                  {pageMeta.subtitle}
                </span>
              )}
            </div>
            <Space size="middle">
              <Select<RangeMode>
                value={rangeMode}
                onChange={onRangeChange}
                style={{ width: 120 }}
                options={[
                  { value: 0, label: tr('今天', 'Today') },
                  { value: 1, label: tr('昨天', 'Yesterday') },
                  { value: 2, label: tr('近 7 天', 'Last 7 days') },
                  { value: 3, label: tr('近 30 天', 'Last 30 days') },
                  { value: 4, label: tr('全部', 'All') },
                  { value: 'custom', label: tr('自定义', 'Custom') },
                ]}
              />
              {rangeMode === 'custom' && (
                <DatePicker.RangePicker
                  showTime={{ format: 'HH:mm' }}
                  format="YYYY-MM-DD HH:mm"
                  allowClear={false}
                  value={
                    customRange
                      ? [dayjs(customRange[0]), dayjs(customRange[1])]
                      : null
                  }
                  onChange={(values) => {
                    if (values && values[0] && values[1]) {
                      setCustomRange([values[0].valueOf(), values[1].valueOf()]);
                    }
                  }}
                  style={{ width: 360 }}
                />
              )}
            </Space>
            <Space size="middle" style={{ marginLeft: 'auto' }}>
              <Select
                value={themeMode}
                onChange={setThemeMode}
                style={{ width: 132 }}
                options={[
                  { value: 'auto', label: tr('主题：自动', 'Theme: Auto') },
                  { value: 'light', label: tr('浅色', 'Light') },
                  { value: 'dark', label: tr('深色', 'Dark') },
                ]}
              />
              <Select
                value={languageMode}
                onChange={setLanguageMode}
                style={{ width: 120 }}
                options={[
                  { value: 'auto', label: tr('语言：自动', 'Language: Auto') },
                  { value: 'zh', label: '中文' },
                  { value: 'en', label: 'English' },
                ]}
              />
            </Space>
          </Header>

          <Content
            style={{
              padding: 20,
              overflow: 'auto',
              minHeight: 0,
              flex: 1,
              minWidth: 0,
            }}
          >
            <div style={{ width: '100%' }}>
              {route.view === 'dashboard' && (
                <DashboardPage
                  data={dashboard}
                  onOpenSession={openSessionFromDashboard}
                />
              )}
              {route.view === 'calls' && (
                <CallsPage
                  range={numericRange}
                  fromMs={customFrom}
                  toMs={customTo}
                  dashboard={dashboard}
                  onOpenSession={openSessionFromDashboard}
                />
              )}
              {route.view === 'model' && (
                <ModelPage
                  key={route.id}
                  modelId={route.id}
                  range={numericRange}
                  fromMs={customFrom}
                  toMs={customTo}
                  onOpenSession={openSessionFromModel}
                />
              )}
              {route.view === 'session' && (
                <SessionPage
                  key={route.id}
                  sessionId={route.id}
                  range={numericRange}
                  onMeta={setSessionMeta}
                />
              )}
            </div>
          </Content>


        </Layout>
      </Layout>

      <PriceSettingsDrawer
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
      />

      <SystemInfoDrawer
        open={systemOpen}
        onClose={() => setSystemOpen(false)}
      />

      <Modal
        title={tr('AHP 连接详情', 'AHP connection details')}
        open={ahpOpen}
        onCancel={() => setAhpOpen(false)}
        footer={null}
        width={980}
      >
        <Descriptions
          column={1}
          size="small"
          bordered
          items={[
            {
              key: 'state',
              label: tr('状态', 'Status'),
              children: ahpStateText,
            },
            {
              key: 'sessions',
              label: tr('会话数', 'Sessions'),
              children: dashboard?.ahp?.sessions ?? '-',
            },
            {
              key: 'transport',
              label: tr('连接方式', 'Transport'),
              children: tr(
                '本地命名管道 / Unix Socket / TCP',
                'Local named pipe / Unix socket / TCP',
              ),
            },
            {
              key: 'purpose',
              label: tr('用途', 'Purpose'),
              children: tr(
                '同步 Agent Host 的实时会话标题与工作区；不读取对话内容。',
                'Syncs live Agent Host session titles and workspaces; conversation content is not read.',
              ),
            },
            {
              key: 'fallback',
              label: tr('降级', 'Fallback'),
              children: tr(
                'AHP 不可用时回退到 agent-host.db 与 ChatSessionStore 索引。',
                'Falls back to agent-host.db and the ChatSessionStore index when unavailable.',
              ),
            },
          ]}
        />
        <div style={{ marginTop: 16 }}>
          <Table<AhpSessionInfo>
            size="small"
            rowKey="resource"
            columns={ahpSessionColumns}
            dataSource={dashboard?.ahp?.items ?? []}
            pagination={false}
            scroll={{ x: 990, y: 300 }}
          />
        </div>
      </Modal>
    </>
  );
}
