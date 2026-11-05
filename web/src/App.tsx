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
import { ModelSettingsDrawer } from './components/ModelSettingsDrawer';
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
import { useUi } from './ui';
import { LOCALES, t } from './i18n';
import { leafModelName, providerOf, shortModelName } from './utils/model';
import { useModelProfiles } from './utils/modelProfiles';

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
  | { view: 'calls'; query?: string }
  | { view: 'model'; id: string }
  | { view: 'session'; id: string; fromModel?: string };

function parseRoute(): Route {
  const hash = location.hash.replace(/^#/, '');
  const [path, query] = hash.split('?');
  const parts = path.split('/').filter(Boolean);

  if (parts[0] === 'calls') {
    const params = new URLSearchParams(query ?? '');
    const search = params.get('q')?.trim();
    return { view: 'calls', query: search || undefined };
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

function buildMenu(
  dashboard: DashboardPayload | null,
  profiles: Record<string, { provider: string | null }>,
): MenuProps['items'] {
  const items: NonNullable<MenuProps['items']> = [
    { key: 'home', icon: <HomeOutlined />, label: t('home') },
    {
      key: 'calls',
      icon: <HistoryOutlined />,
      label: t('call.details'),
    },
  ];
  if (!dashboard) {
    return items;
  }

  const groups = new Map<string, string[]>();
  for (const row of dashboard.models) {
    const provider = providerOf(
      row.model,
      profiles[row.model.toLowerCase()]?.provider,
    );
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
  const { locale, themeMode, setThemeMode, languageMode, setLanguageMode, t } =
    useUi();
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
  const { profiles } = useModelProfiles();

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
      ? t('ahp.connected.sessions', { sessions: dashboard.ahp.sessions })
      : dashboard.ahp.state === 'connecting'
        ? t('ahp.connecting')
        : dashboard.ahp.state === 'disabled'
          ? t('ahp.unavailable')
          : t('ahp.not.connected')
    : null;
  const ahpStateText = dashboard?.ahp
    ? dashboard.ahp.connected
      ? t('connected')
      : dashboard.ahp.state === 'connecting'
        ? t('connecting')
        : dashboard.ahp.state === 'disabled'
          ? t('unavailable')
          : t('not.connected')
    : '-';
  const ahpSessionColumns: ColumnsType<AhpSessionInfo> = [
    {
      title: t('title'),
      dataIndex: 'title',
      key: 'title',
      width: 220,
      ellipsis: true,
    },
    {
      title: t('provider'),
      dataIndex: 'provider',
      key: 'provider',
      width: 100,
      ellipsis: true,
    },
    {
      title: t('status'),
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: string) => {
        if (status === 'in_progress') {
          return <Tag color="processing">{t('in.progress')}</Tag>;
        }
        if (status === 'input_needed') {
          return <Tag color="warning">{t('needs.input')}</Tag>;
        }
        if (status === 'error') {
          return <Tag color="error">{t('error')}</Tag>;
        }
        if (status === 'idle') {
          return <Tag>{t('idle')}</Tag>;
        }
        return <Tag>{t('unknown')}</Tag>;
      },
    },
    {
      title: t('workspace'),
      dataIndex: 'workspacePath',
      key: 'workspace',
      width: 240,
      ellipsis: true,
      render: (workspacePath: string | null) => workspacePath || '-',
    },
    {
      title: t('activity'),
      dataIndex: 'activity',
      key: 'activity',
      width: 140,
      ellipsis: true,
      render: (activity: string | null) => activity || '-',
    },
    {
      title: t('modified'),
      dataIndex: 'modifiedAt',
      key: 'modified',
      width: 120,
      render: (modifiedAt: string) => {
        const parsed = dayjs(modifiedAt);
        return parsed.isValid() ? parsed.format('MM-DD HH:mm') : '-';
      },
    },
    {
      title: t('chats'),
      dataIndex: 'chats',
      key: 'chats',
      width: 70,
      align: 'right',
    },
  ];

  const menuItems = useMemo(
    () => buildMenu(dashboard, profiles),
    [dashboard, profiles, locale],
  );

  useEffect(() => {
    if (siderCollapsed || !dashboard) {
      return;
    }

    setOpenKeys((current) =>
      current.length > 0
        ? current
        : [
            ...new Set(
              dashboard.models.map(
                (row) =>
                  `provider:${providerOf(
                    row.model,
                    profiles[row.model.toLowerCase()]?.provider,
                  )}`,
              ),
            ),
          ],
    );
  }, [dashboard, profiles, siderCollapsed]);

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

  const openCallsForSession = (sessionId: string) => {
    location.hash = `#/calls?q=${encodeURIComponent(sessionId)}`;
  };

  const openModelPage = (model: string) => {
    location.hash = `#/model/${encodeURIComponent(model)}`;
  };

  const activeSessionId = route.view === 'session' ? route.id : null;

  useEffect(() => {
    setSessionMeta(null);
  }, [activeSessionId]);

  const pageMeta =
    route.view === 'dashboard'
      ? {
          title: t('home'),
          subtitle: t('overview'),
        }
      : route.view === 'calls'
        ? {
            title: t('call.details'),
            subtitle: t('search.and.filters'),
          }
        : route.view === 'model'
          ? {
              // The full leaf id matches the tables, while the short name would
              // collapse names such as "glm-5" to a bare version number.
              title: `${providerOf(
                route.id,
                profiles[route.id.toLowerCase()]?.provider,
              )} · ${leafModelName(route.id)}`,
              subtitle: route.id,
            }
          : {
              title:
                sessionMeta?.title ??
                `${t('session')} · ${
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
                    ? t('expand.navigation')
                    : t('collapse.navigation')
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
                    ? t('live')
                    : t('disconnected')
                }
              >
                <span>
                  <Badge
                    status={connected ? 'processing' : 'error'}
                    text={
                      siderCollapsed
                        ? undefined
                        : connected
                          ? t('live')
                          : t('disconnected')
                    }
                  />
                </span>
              </Tooltip>
              {ahpText && (
                <Tooltip
                  placement="right"
                  title={t('ahp.syncs.live.agent.host.session.titles.click.for')}
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
                    label: t('system.info'),
                  },
                  {
                    key: 'settings',
                    icon: <SettingOutlined />,
                    label: t('settings'),
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
                  { value: 0, label: t('today') },
                  { value: 1, label: t('yesterday') },
                  { value: 2, label: t('last.7.days') },
                  { value: 3, label: t('last.30.days') },
                  { value: 4, label: t('all') },
                  { value: 'custom', label: t('custom') },
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
                  { value: 'auto', label: t('theme.auto') },
                  { value: 'light', label: t('light') },
                  { value: 'dark', label: t('dark') },
                ]}
              />
              <Select
                value={languageMode}
                onChange={setLanguageMode}
                style={{ width: 120 }}
                options={[
                  { value: 'auto', label: t('language.auto') },
                  ...LOCALES.map((locale) => ({
                    value: locale.id,
                    label: locale.label,
                  })),
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
                  onOpenModel={openModelPage}
                />
              )}
              {route.view === 'calls' && (
                <CallsPage
                  key={route.query ?? ''}
                  range={numericRange}
                  fromMs={customFrom}
                  toMs={customTo}
                  dashboard={dashboard}
                  initialQuery={route.query}
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
                  onOpenCalls={openCallsForSession}
                />
              )}
            </div>
          </Content>


        </Layout>
      </Layout>

      <ModelSettingsDrawer
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
      />

      <SystemInfoDrawer
        open={systemOpen}
        onClose={() => setSystemOpen(false)}
      />

      <Modal
        title={t('ahp.connection.details')}
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
              label: t('status'),
              children: ahpStateText,
            },
            {
              key: 'sessions',
              label: t('sessions'),
              children: dashboard?.ahp?.sessions ?? '-',
            },
            {
              key: 'transport',
              label: t('transport'),
              children: t('local.named.pipe.unix.socket.tcp'),
            },
            {
              key: 'purpose',
              label: t('purpose'),
              children: t('syncs.live.agent.host.session.titles.and.workspaces'),
            },
            {
              key: 'fallback',
              label: t('fallback'),
              children: t('falls.back.to.agent.host.db.and.the.chatsessionstore'),
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
