import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Empty, Spin, Tag, Tooltip, Tree, Typography } from 'antd';
import dayjs from 'dayjs';
import type { TraceNode, TracePayload } from '../types';
import { useUi } from '../ui';
import { intlLocale, type Locale } from '../i18n';
import { useAutoTableHeight } from '../utils/useAutoTableHeight';
import { useViewportHeight } from '../utils/useViewportHeight';

interface TraceTreeProps {
  sessionId: string;
  expanded?: boolean;
  // Changes whenever the session snapshot changes, so newly ingested spans show
  // up without a manual reload.
  version?: string;
}

const TRACE_ROW_HEIGHT = 30;
// Matches the server default so a block roughly fills the panel.
const TRACE_BLOCK_BUDGET = 400;
const LOAD_MORE_THRESHOLD_PX = 160;

export function TraceTree({ sessionId, expanded = false, version }: TraceTreeProps) {
  const { locale, t } = useUi();
  const [nodes, setNodes] = useState<TraceNode[] | null>(null);
  const [loadedFor, setLoadedFor] = useState<string>();
  const [hasMore, setHasMore] = useState(false);
  const [total, setTotal] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const loadedSession = useRef<string>();
  const cursor = useRef<string | null>(null);
  const scrollerRef = useRef<HTMLElement | null>(null);
  const loading = loadedFor !== sessionId;
  const [hostRef, hostHeight] = useAutoTableHeight(0);
  const fillHeight = useViewportHeight(0.82, 400, 2000);

  useEffect(() => {
    let cancelled = false;
    // The version changes on every new span or event, so a live agent turn would
    // refetch continuously; coalesce bursts and load a new session immediately.
    const firstLoad = loadedSession.current !== sessionId;
    const timer = setTimeout(
      () => {
        fetch(
          `/api/session/${encodeURIComponent(sessionId)}/trace?budget=${TRACE_BLOCK_BUDGET}`,
        )
          .then((response) => (response.ok ? response.json() : null))
          .then((payload: TracePayload | null) => {
            if (cancelled) {
              return;
            }

            // FIFO: the first block is the oldest rows, so a refresh restarts from
            // the beginning instead of leaving a gap in the middle.
            cursor.current = payload?.nextCursor ?? null;
            setHasMore(payload?.hasMore ?? false);
            setTotal(payload?.total ?? 0);
            setNodes(payload?.nodes ?? []);
          })
          .catch(() => {
            if (!cancelled && firstLoad) {
              setNodes([]);
            }
          })
          .finally(() => {
            if (!cancelled) {
              loadedSession.current = sessionId;
              setLoadedFor(sessionId);
            }
          });
      },
      firstLoad ? 0 : 1500,
    );

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [sessionId, version]);

  const loadMore = useCallback(async () => {
    const next = cursor.current;
    if (!next || loadingMore) {
      return;
    }

    setLoadingMore(true);
    try {
      const response = await fetch(
        `/api/session/${encodeURIComponent(sessionId)}/trace` +
          `?budget=${TRACE_BLOCK_BUDGET}&cursor=${encodeURIComponent(next)}`,
      );
      if (!response.ok) {
        return;
      }

      const payload = (await response.json()) as TracePayload;
      cursor.current = payload.nextCursor;
      setHasMore(payload.hasMore);
      if (payload.nodes.length > 0) {
        // Appending at the bottom never moves what the user is looking at.
        setNodes((current) => [...(current ?? []), ...payload.nodes]);
      }
    } catch {
      // Leave the cursor untouched so the next scroll retries.
    } finally {
      setLoadingMore(false);
    }
  }, [loadingMore, sessionId]);

  useEffect(() => {
    const scroller = findScroller(hostRef.current);
    scrollerRef.current = scroller;
    if (!scroller || !hasMore) {
      return;
    }

    const onScroll = () => {
      const remaining =
        scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
      if (remaining <= LOAD_MORE_THRESHOLD_PX) {
        void loadMore();
      }
    };

    scroller.addEventListener('scroll', onScroll);
    return () => scroller.removeEventListener('scroll', onScroll);
  }, [hasMore, loadMore, nodes, hostRef]);

  useEffect(() => {
    const scroller = scrollerRef.current;
    if (!scroller || !hasMore || loadingMore || !nodes) {
      return;
    }

    // A short block leaves no scrollbar, so keep filling until the list scrolls.
    if (scroller.scrollHeight <= scroller.clientHeight) {
      void loadMore();
    }
  }, [nodes, hasMore, loadingMore, loadMore]);

  const treeData = useMemo(
    () =>
      (nodes ?? []).map((node) => ({
        key: node.id,
        title: (
          <TraceNodeTitle
            node={node}
            locale={locale}
            hint={t('context.row.hint')}
          />
        ),
      })),
    [nodes, locale, t],
  );

  if (loading) {
    return (
      <div style={{ padding: 24, textAlign: 'center' }}>
        <Spin />
      </div>
    );
  }

  if (!nodes || nodes.length === 0) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t('no.trace.data')}
      />
    );
  }

  const treeHeight = expanded
    ? fillHeight
    : Math.max(220, Math.min(hostHeight, 480));

  return (
    <div
      ref={hostRef}
      className={`tt-trace-tree${expanded ? ' tt-trace-tree-expanded' : ''}`}
      style={
        {
          overscrollBehavior: 'contain',
          '--tt-trace-height': `${treeHeight}px`,
        } as CSSProperties
      }
    >
      <Typography.Text
        type="secondary"
        style={{ display: 'block', marginBottom: 8, fontSize: 12 }}
      >
        {t('sorted.by.start.time.indentation.shows.parent.links')}
        {` · ${total}`}
        {loadingMore ? ` · ${t('loading.more.nodes')}` : ''}
      </Typography.Text>
      <Tree
        treeData={treeData}
        blockNode
        selectable={false}
        virtual
        itemHeight={TRACE_ROW_HEIGHT}
        height={treeHeight}
      />
    </div>
  );
}

// rc-virtual-list owns the scrollable element; the wrapper class differs between
// antd versions, so both are accepted.
function findScroller(host: HTMLElement | null): HTMLElement | null {
  if (!host) {
    return null;
  }

  return host.querySelector(
    '.ant-tree-list-holder, .rc-virtual-list-holder',
  ) as HTMLElement | null;
}

const compactFormatters = {
  'zh-CN': new Intl.NumberFormat('zh-CN', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }),
  'zh-TW': new Intl.NumberFormat('zh-TW', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }),
  'en-US': new Intl.NumberFormat('en-US', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }),
};

function TraceNodeTitle({
  node,
  locale,
  hint,
}: {
  node: TraceNode;
  locale: Locale;
  hint: string;
}) {
  const tokenFormatter = compactFormatters[intlLocale(locale)];
  return (
    <span style={{ paddingLeft: node.depth * 18 }}>
      {node.depth > 0 && (
        <Typography.Text
          type="secondary"
          style={{ marginRight: 6, fontSize: 12 }}
        >
          ↳
        </Typography.Text>
      )}
      {node.isContext && (
        <Tooltip title={hint}>
          <Typography.Text type="secondary" style={{ marginRight: 6 }}>
            ⋯
          </Typography.Text>
        </Tooltip>
      )}
      <Tag
        color={
          node.isContext ? 'default' : node.kind === 'chat' ? 'blue' : 'geekblue'
        }
      >
        {node.operation}
      </Tag>
      <Typography.Text type={node.isContext ? 'secondary' : undefined}>
        {node.label}
      </Typography.Text>
      <Typography.Text type="secondary" style={{ marginLeft: 8 }}>
        {dayjs(node.startTimeMs).format('HH:mm:ss.SSS')}
      </Typography.Text>
      <Typography.Text type="secondary" style={{ marginLeft: 8 }}>
        {node.durationText}
      </Typography.Text>
      {node.kind === 'chat' &&
        (node.inputTokens > 0 || node.outputTokens > 0) && (
          <Typography.Text
            type="secondary"
            style={{ marginLeft: 8, fontSize: 11 }}
          >
            ↑{tokenFormatter.format(node.inputTokens)} ↓
            {tokenFormatter.format(node.outputTokens)}
            {node.cacheReadTokens > 0
              ? ` · cache ${tokenFormatter.format(node.cacheReadTokens)}`
              : ''}
          </Typography.Text>
        )}
      {node.isError && (
        <Tag color="error" style={{ marginLeft: 8 }}>
          {node.errorType ?? 'error'}
        </Tag>
      )}
    </span>
  );
}
