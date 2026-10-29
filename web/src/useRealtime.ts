import { useEffect, useRef, useState } from 'react';
import type { Subscription } from './types';

export function useRealtime<T>(subscription: Subscription): {
  data: T | null;
  connected: boolean;
  refresh: () => void;
} {
  const [data, setData] = useState<T | null>(null);
  const [connected, setConnected] = useState(false);
  const socketRef = useRef<WebSocket | null>(null);
  const subRef = useRef(subscription);
  subRef.current = subscription;

  const { view, id, range, utility, fromMs, toMs } = subscription;

  useEffect(() => {
    let disposed = false;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const connect = () => {
      if (disposed) {
        return;
      }

      const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
      const socket = new WebSocket(`${scheme}://${location.host}/ws`);
      socketRef.current = socket;

      socket.onopen = () => {
        setConnected(true);
        send(socket, subRef.current);
      };
      socket.onmessage = (event) => {
        try {
          const message = JSON.parse(event.data as string) as {
            type?: string;
            data?: unknown;
          };
          if (message.type === 'snapshot' && message.data) {
            setData(message.data as T);
          }
        } catch {
          // Ignore malformed frames.
        }
      };
      socket.onclose = () => {
        setConnected(false);
        if (!disposed) {
          retry = setTimeout(connect, 2000);
        }
      };
      socket.onerror = () => socket.close();
    };

    connect();

    return () => {
      disposed = true;
      if (retry) {
        clearTimeout(retry);
      }
      socketRef.current?.close();
      socketRef.current = null;
    };
  }, []);

  useEffect(() => {
    const socket = socketRef.current;
    if (socket && socket.readyState === WebSocket.OPEN) {
      send(socket, subscription);
    }
  }, [view, id, range, utility, fromMs, toMs]);

  useEffect(() => {
    setData(null);
  }, [view, id]);

  const refresh = () => {
    const socket = socketRef.current;
    if (socket && socket.readyState === WebSocket.OPEN) {
      send(socket, subRef.current);
    }
  };

  return { data, connected, refresh };
}

function send(socket: WebSocket, subscription: Subscription) {
  socket.send(
    JSON.stringify({
      type: 'subscribe',
      view: subscription.view,
      id: subscription.id,
      range: subscription.range,
      utility: subscription.utility,
      fromMs: subscription.fromMs,
      toMs: subscription.toMs,
      client: 'web',
    }),
  );
}
