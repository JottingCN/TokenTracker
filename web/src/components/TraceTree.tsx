import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { CSSProperties } from 'react';
import { Empty, Spin, Tag, Tree, Typography } from 'antd';
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
// Matches the server default: a block is a run of whole traces near this size.
const TRACE_BLOCK_BUDGET = 400;
const LOAD_OLDER_THRESHOLD_PX = 160;

export function TraceTree({ sessionId, expanded = false, version }: TraceTreeProps) {
  const { locale, t } = useUi();
  const [nodes, setNodes] = useState<TraceNode[] | null>(null);
  const [loadedFor, setLoadedFor] = useState<string>();
  const [hasMore, setHasMore] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const loadedSession = useRef<string>();
  const cursor = useRef<string | null>(null);
  const newestIds = useRef<Set<string>>(new Set());
  const nodesRef = useRef<TraceNode[] | null>(null);
  // The row the viewport is anchored on, so a prepended block does not jump.
  const anchor = useRef<{ id: string; offset: number } | null>(null);
  nodesRef.current = nodes;
  const loading = loadedFor !== sessionId;
  const [hostRef, hostHeight] = useAutoTableHeight(0);
  const fillHeight = useViewportHeight(0.82, 400, 2000);

  useEffect(() => {
    let cancelled = false;
    // Each block is a few hundred rows, and the version changes on every new
    // span, so bursts are coalesced; a new session loads at once.
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

            const fresh = payload?.nodes ?? [];
            const freshIds = new Set(fresh.map((node) => node.id));
            if (firstLoad) {
              newestIds.current = freshIds;
              cursor.current = payload?.nextCursor ?? null;
              setHasMore(payload?.hasMore ?? false);
              setNodes(fresh);
              return;
            }

            // Refresh only the newest block; blocks already scrolled to stay put.
            const previousNewest = newestIds.current;
            newestIds.current = freshIds;
            cursor.current = payload?.nextCursor ?? null;
            setHasMore(payload?.hasMore ?? false);
            setNodes((current) => {
              const older = (current ?? []).filter(
                (node) => !previousNewest.has(node.id),
              );
              return mergeNodes(fresh, older);
            });
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

  const loadOlder = useCallback(async () => {
    const next = cursor.current;
    if (!next || loadingOlder) {
      return;
    }

    const scrollerBefore = findScroller(hostRef.current);
    if (scrollerBefore) {
      const index = Math.round(scrollerBefore.scrollTop / TRACE_ROW_HEIGHT);
      const node = nodesRef.current?.[index];
      anchor.current = node
        ? {
            id: node.id,
            offset: scrollerBefore.scrollTop - index * TRACE_ROW_HEIGHT,
          }
        : null;
    }

    setLoadingOlder(true);
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
        setNodes((current) => mergeNodes(current ?? [], payload.nodes));
      }
    } catch {
      // Leave the cursor untouched so the next scroll retries.
    } finally {
      setLoadingOlder(false);
    }
  }, [loadingOlder, sessionId]);

  useEffect(() => {
    const scroller = findScroller(hostRef.current);
    if (!scroller || !hasMore) {
      return;
    }

    const onScroll = () => {
      if (scroller.scrollTop <= LOAD_OLDER_THRESHOLD_PX) {
        void loadOlder();
      }
    };

    scroller.addEventListener('scroll', onScroll);
    return () => scroller.removeEventListener('scroll', onScroll);
  }, [hasMore, loadOlder, nodes, hostRef]);

  useLayoutEffect(() => {
    const pinned = anchor.current;
    anchor.current = null;
    if (!pinned || !nodes) {
      return;
    }

    // Blocks are anchored on traces, so older rows can interleave instead of all
    // landing above the viewport; re-anchoring on the same row is jump-free.
    const index = nodes.findIndex((node) => node.id === pinned.id);
    const scroller = findScroller(hostRef.current);
    if (index >= 0 && scroller) {
      scroller.scrollTop = index * TRACE_ROW_HEIGHT + pinned.offset;
    }
  }, [nodes, hostRef]);

  const treeData = useMemo(
    () =>
      (nodes ?? []).map((node) => ({
        key: node.id,
        title: <TraceNodeTitle node={node} locale={locale} />,
      })),
    [nodes],
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
  const total = nodes.length;

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
        {loadingOlder ? ` · ${t('loading.older.nodes')}` : ''}
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

function mergeNodes(next: TraceNode[], older: TraceNode[]): TraceNode[] {
  if (older.length === 0) {
    return next;
  }

  const seen = new Set(next.map((node) => node.id));
  const merged = next.slice();
  for (const node of older) {
    if (!seen.has(node.id)) {
      seen.add(node.id);
      merged.push(node);
    }
  }

  return merged.sort((left, right) => {
    const byTime = left.startTimeMs - right.startTimeMs;
    return byTime !== 0 ? byTime : left.id.localeCompare(right.id);
  });
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
}: {
  node: TraceNode;
  locale: Locale;
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
      <Tag color={node.kind === 'chat' ? 'blue' : 'geekblue'}>
        {node.operation}
      </Tag>
      <Typography.Text>{node.label}</Typography.Text>
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
