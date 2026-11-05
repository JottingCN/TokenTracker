import { useEffect, useMemo, useState } from 'react';
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

export function TraceTree({ sessionId, expanded = false, version }: TraceTreeProps) {
  const { locale, t } = useUi();
  const [nodes, setNodes] = useState<TraceNode[] | null>(null);
  const [loadedFor, setLoadedFor] = useState<string>();
  const loading = loadedFor !== sessionId;
  const [hostRef, hostHeight] = useAutoTableHeight(0);
  const fillHeight = useViewportHeight(0.82, 400, 2000);

  useEffect(() => {
    let cancelled = false;
    // Only a new session shows a spinner; later refreshes keep the old tree
    // visible so the panel does not flicker on every snapshot.
    fetch(`/api/session/${encodeURIComponent(sessionId)}/trace`)
      .then((response) => (response.ok ? response.json() : null))
      .then((payload: TracePayload | null) => {
        if (!cancelled) {
          setNodes(payload?.nodes ?? []);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setNodes([]);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoadedFor(sessionId);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [sessionId, version]);

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
      </Typography.Text>
      <Tree
        treeData={treeData}
        blockNode
        selectable={false}
        virtual
        itemHeight={30}
        height={treeHeight}
      />
    </div>
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
