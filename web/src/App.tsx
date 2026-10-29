import { useEffect, useMemo, useState } from 'react';
import { FolderOutlined, HomeOutlined, SettingOutlined } from '@ant-design/icons';
import {
  Badge,
  Button,
  DatePicker,
  Layout,
  Menu,
  Select,
  Space,
  Switch,
  Typography,
} from 'antd';
import type { MenuProps } from 'antd';
import dayjs from 'dayjs';
import { PriceSettingsDrawer } from './components/PriceSettingsDrawer';
import { DashboardPage } from './pages/DashboardPage';
import { ModelPage } from './pages/ModelPage';
import { SessionPage } from './pages/SessionPage';
import { useRealtime } from './useRealtime';
import type { DashboardPayload } from './types';
import { providerOf, shortModelName } from './utils/model';

const { Header, Sider, Content, Footer } = Layout;

type Route =
  | { view: 'dashboard' }
  | { view: 'model'; id: string }
  | { view: 'session'; id: string; fromModel?: string };

function parseRoute(): Route {
  const hash = location.hash.replace(/^#/, '');
  const [path, query] = hash.split('?');
  const parts = path.split('/').filter(Boolean);

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
    { key: 'home', icon: <HomeOutlined />, label: '主页' },
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
    items.push({
      key: `provider:${provider}`,
      icon: <FolderOutlined />,
      label: provider,
      children: models
        .sort((a, b) => a.localeCompare(b))
        .map((model) => ({
          key: `model:${model}`,
          label: shortModelName(model),
        })),
    });
  }

  return items;
}

type RangeMode = number | 'custom';

export default function App() {
  const [route, setRoute] = useState<Route>(parseRoute);
  const [rangeMode, setRangeMode] = useState<RangeMode>(2);
  const [customRange, setCustomRange] = useState<[number, number] | null>(null);
  const [utility, setUtility] = useState(false);
  const [openKeys, setOpenKeys] = useState<string[]>([]);
  const [settingsOpen, setSettingsOpen] = useState(false);

  useEffect(() => {
    const handler = () => setRoute(parseRoute());
    window.addEventListener('hashchange', handler);
    return () => window.removeEventListener('hashchange', handler);
  }, []);

  const numericRange = typeof rangeMode === 'number' ? rangeMode : 2;
  const customFrom = rangeMode === 'custom' ? customRange?.[0] : undefined;
  const customTo = rangeMode === 'custom' ? customRange?.[1] : undefined;

  const { data: dashboard, connected } = useRealtime<DashboardPayload>({
    view: 'dashboard',
    range: numericRange,
    utility,
    fromMs: customFrom,
    toMs: customTo,
  });

  const menuItems = useMemo(() => buildMenu(dashboard), [dashboard]);

  useEffect(() => {
    if (!dashboard) {
      return;
    }

    setOpenKeys((current) =>
      current.length > 0
        ? current
        : [...new Set(dashboard.models.map((row) => `provider:${providerOf(row.model)}`))],
    );
  }, [dashboard]);

  const selectedKeys =
    route.view === 'dashboard'
      ? ['home']
      : route.view === 'model'
        ? [`model:${route.id}`]
        : route.fromModel
          ? [`model:${route.fromModel}`]
          : [];

  const onMenuClick: MenuProps['onClick'] = ({ key }) => {
    const value = String(key);
    if (value === 'home') {
      location.hash = '#/';
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

  return (
    <>
      <Layout style={{ height: '100vh', overflow: 'hidden' }}>
        <Sider theme="light" width={230} style={{ borderRight: '1px solid #f0f0f0' }}>
          <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
            <div style={{ padding: '18px 20px 12px', fontSize: 16, fontWeight: 600 }}>
              Token Tracker
            </div>
            <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
              <Menu
                mode="inline"
                items={menuItems}
                selectedKeys={selectedKeys}
                openKeys={openKeys}
                onOpenChange={(keys) => setOpenKeys(keys)}
                onClick={onMenuClick}
                style={{ borderInlineEnd: 'none' }}
              />
            </div>
            <div style={{ padding: 12, borderTop: '1px solid #f0f0f0' }}>
              <Button block icon={<SettingOutlined />} onClick={() => setSettingsOpen(true)}>
                设置
              </Button>
            </div>
          </div>
        </Sider>

        <Layout style={{ height: '100vh', overflow: 'hidden' }}>
          <Header
            style={{
              background: '#fff',
              borderBottom: '1px solid #f0f0f0',
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
            <Space size="middle">
              <Select<RangeMode>
                value={rangeMode}
                onChange={onRangeChange}
                style={{ width: 120 }}
                options={[
                  { value: 0, label: '今天' },
                  { value: 1, label: '昨天' },
                  { value: 2, label: '近 7 天' },
                  { value: 3, label: '近 30 天' },
                  { value: 4, label: '全部' },
                  { value: 'custom', label: '自定义' },
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
              <Space size={6}>
                <Switch checked={utility} onChange={setUtility} />
                <span>显示辅助调用</span>
              </Space>
            </Space>
          </Header>

          <Content style={{ padding: 20, overflow: 'auto', minHeight: 0 }}>
            <div style={{ maxWidth: 1600, margin: '0 auto' }}>
              {route.view === 'dashboard' && (
                <DashboardPage
                  data={dashboard}
                  utility={utility}
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
                <SessionPage key={route.id} sessionId={route.id} range={numericRange} />
              )}
            </div>
          </Content>

          <Footer
            style={{
              padding: '6px 20px',
              background: '#fff',
              borderTop: '1px solid #f0f0f0',
              flex: '0 0 auto',
            }}
          >
            <Space
              size="large"
              wrap
              style={{ width: '100%', justifyContent: 'flex-end' }}
            >
              <Badge
                status={connected ? 'processing' : 'error'}
                text={connected ? '实时连接' : '连接断开'}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {dashboard
                  ? dashboard.updatedAt === '-'
                    ? '暂无数据更新'
                    : `数据更新于 ${dashboard.updatedAt}`
                  : '等待数据'}
              </Typography.Text>
              {dashboard && (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {`监听 ${dashboard.receiver.endpoint} · ${dashboard.receiver.batches} 批 / ${dashboard.receiver.spans} span`}
                </Typography.Text>
              )}
            </Space>
          </Footer>
        </Layout>
      </Layout>

      <PriceSettingsDrawer
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
      />
    </>
  );
}
