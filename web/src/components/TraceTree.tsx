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
  // The topology follows the page range like the other cards; ancestors that
  // started outside it come back as context rows.
  range: number;
  fromMs?: number;
  toMs?: number;
}

const TRACE_ROW_HEIGHT = 30;
// Matches the server default so a block roughly fills the panel.
const TRACE_BLOCK_BUDGET = 400;
const LOAD_MORE_THRESHOLD_PX = 160;

export function TraceTree({
  sessionId,
  expanded = false,
  version,
  range,
  fromMs,
  toMs,
}: TraceTreeProps) {
  const { locale, t } = useUi();
  const [nodes, setNodes] = useState<TraceNode[] | null>(null);
  const [loadedFor, setLoadedFor] = useState<string>();
  const [hasMore, setHasMore] = useState(false);
  const [total, setTotal] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const loadedKey = useRef<string>();
  const cursor = useRef<string | null>(null);
  const hasMoreRef = useRef(false);
  const nodesRef = useRef<TraceNode[] | null>(null);
  const loadingRef = useRef(false);
  const scrollerRef = useRef<HTMLElement | null>(null);
  const windowKey = fromMs && toMs ? `${fromMs}-${toMs}` : `range-${range}`;
  const loadKey = `${sessionId}|${windowKey}`;
  const loading = loadedFor !== loadKey;
  // Multi-day windows show the date; a single day stays compact. Formatting the
  // whole list the same way avoids a jarring mix of widths per row.
  const showDates =
    fromMs && toMs ? toMs - fromMs > 86_400_000 : range >= 2;
  const [hostRef, hostHeight] = useAutoTableHeight(0);
  const fillHeight = useViewportHeight(0.82, 400, 2000);

  const applyNodes = useCallback((next: TraceNode[] | null) => {
    nodesRef.current = next;
    setNodes(next);
  }, []);

  const applyHasMore = useCallback((next: boolean) => {
    hasMoreRef.current = next;
    setHasMore(next);
  }, []);

  // A block can repeat ancestors from the previous block as context rows; the
  // Tree needs unique keys, and the depth is already computed server-side, so a
  // duplicate row is simply dropped.
  const appendNodes = useCallback((incoming: TraceNode[]) => {
    if (incoming.length === 0) {
      return;
    }

    const current = nodesRef.current ?? [];
    const seen = new Set(current.map((node) => node.id));
    const merged = [...current];
    for (const node of incoming) {
      if (seen.has(node.id)) {
        continue;
      }

      seen.add(node.id);
      merged.push(node);
    }

    nodesRef.current = merged;
    setNodes(merged);
  }, []);

  const traceUrl = useCallback(
    (nextCursor?: string | null) => {
      const params = new URLSearchParams({
        budget: String(TRACE_BLOCK_BUDGET),
      });
      if (fromMs && toMs) {
        params.set('from', String(fromMs));
        params.set('to', String(toMs));
      } else {
        params.set('range', String(range));
      }
      if (nextCursor) {
        params.set('cursor', nextCursor);
      }
      return `/api/session/${encodeURIComponent(sessionId)}/trace?${params}`;
    },
    [sessionId, range, fromMs, toMs],
  );

  useEffect(() => {
    let cancelled = false;
    // The version changes on every new span or event, so a live agent turn would
    // refetch continuously; coalesce bursts and load a new session or window
    // immediately.
    const firstLoad = loadedKey.current !== loadKey;
    const timer = setTimeout(
      () => {
        const loaded = nodesRef.current;
        // When every row is already on screen the refresh only has to append the
        // rows that arrived after the last one; reloading the first block would
        // throw the user back to the top of a long trace.
        const appendOnly =
          !firstLoad && !hasMoreRef.current && !!loaded && loaded.length > 0;
        const tail = loaded?.[loaded.length - 1];
        const appendCursor =
          appendOnly && tail ? `${tail.startTimeMs}:${tail.id}` : null;

        fetch(traceUrl(appendCursor))
          .then((response) => (response.ok ? response.json() : null))
          .then((payload: TracePayload | null) => {
            if (cancelled || !payload) {
              return;
            }

            if (appendOnly) {
              appendNodes(payload.nodes);
              cursor.current = payload.nextCursor;
              applyHasMore(payload.hasMore);
              setTotal(payload.total);
              return;
            }

            // FIFO: the first block is the oldest rows, so a full reload restarts
            // from the beginning instead of leaving a gap in the middle.
            cursor.current = payload.nextCursor;
            applyHasMore(payload.hasMore);
            setTotal(payload.total);
            applyNodes(payload.nodes);
          })
          .catch(() => {
            if (!cancelled && firstLoad) {
              applyNodes([]);
            }
          })
          .finally(() => {
            if (!cancelled) {
              loadedKey.current = loadKey;
              setLoadedFor(loadKey);
            }
          });
      },
      // A whole block is ~170 KB, so a short debounce still coalesces the burst of
      // versions one agent turn produces without lagging behind the dialog.
      firstLoad ? 0 : 500,
    );

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [loadKey, version, traceUrl, applyHasMore, applyNodes, appendNodes]);

  const loadMore = useCallback(async () => {
    const next = cursor.current;
    // A ref guard stops two scroll events from requesting the same block twice.
    if (!next || loadingRef.current) {
      return;
    }

    loadingRef.current = true;
    setLoadingMore(true);
    try {
      const response = await fetch(traceUrl(next));
      if (!response.ok) {
        return;
      }

      const payload = (await response.json()) as TracePayload;
      cursor.current = payload.nextCursor;
      applyHasMore(payload.hasMore);
      setTotal(payload.total);
      // Appending at the bottom never moves what the user is looking at.
      appendNodes(payload.nodes);
    } catch {
      // Leave the cursor untouched so the next scroll retries.
    } finally {
      loadingRef.current = false;
      setLoadingMore(false);
    }
  }, [appendNodes, applyHasMore, traceUrl]);

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
            showDates={showDates}
          />
        ),
      })),
    [nodes, locale, t, showDates],
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

// Multi-day ranges need the date; without it a row from 11-04 looks like it ran
// today. The year only appears when the row is not from the current year.
function formatNodeTime(startTimeMs: number, showDates: boolean): string {
  const value = dayjs(startTimeMs);
  if (!showDates) {
    return value.format('HH:mm:ss.SSS');
  }

  return value.format(
    value.year() === dayjs().year() ? 'MM-DD HH:mm:ss' : 'YYYY-MM-DD HH:mm:ss',
  );
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
  showDates,
}: {
  node: TraceNode;
  locale: Locale;
  hint: string;
  showDates: boolean;
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
          node.isContext
            ? 'default'
            : node.kind === 'chat'
              ? 'blue'
              : node.kind === 'permission'
                ? node.isError
                  ? 'red'
                  : 'gold'
                : 'geekblue'
        }
      >
        {node.operation}
      </Tag>
      <Typography.Text type={node.isContext ? 'secondary' : undefined}>
        {node.label}
      </Typography.Text>
      <Typography.Text type="secondary" style={{ marginLeft: 8 }}>
        {formatNodeTime(node.startTimeMs, showDates)}
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
