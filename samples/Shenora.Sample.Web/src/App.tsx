import { getBridge, isShenoraAvailable, useShenoraEvent, useShenoraQuery } from '@shenora/react';
import { useEffect, useState } from 'react';

// Injected by the desktop host (WebViewHostOptions.InjectedGlobals — camelCase JSON).
declare global {
  interface Window {
    __SHENORA_SAMPLE__?: { name: string; version: string };
  }
}

/**
 * Shows what the host wired up, so a screenshot proves the whole stack: the page rendered
 * (virtual host or Vite), the bridge transport exists, the injected global arrived, a typed
 * IPC round-trip succeeded, and native events stream in.
 */
export function App() {
  const hosted = isShenoraAvailable();
  const meta = window.__SHENORA_SAMPLE__;
  const mode = import.meta.env.DEV ? 'dev (Vite)' : 'packaged';

  // The ready handshake — the app shell's listeners exist now; the host starts delivering
  // buffered notifications (and the sample starts its tick source).
  useEffect(() => {
    if (hosted) void getBridge().notifyReady();
  }, [hosted]);

  // React → typed .NET handler → typed response.
  const echo = useShenoraQuery<{ echoed: string; length: number }>('SAMPLE', 'ECHO', {
    payload: { text: 'shenora' },
    enabled: hosted,
  });

  // Native event → React: the host emits SAMPLE.TICK on its event bus every second.
  const [tick, setTick] = useState<{ count: number; at: string }>();
  useShenoraEvent<{ count: number; at: string }>('SAMPLE', 'TICK', setTick);

  const row: React.CSSProperties = { margin: '0.25rem 0', fontSize: '1rem' };
  const value: React.CSSProperties = { color: '#7fd18c', fontWeight: 600 };
  const missing: React.CSSProperties = { color: '#d1907f', fontWeight: 600 };

  return (
    <main style={{ display: 'grid', placeItems: 'center', minHeight: '100vh' }}>
      <div style={{ textAlign: 'center' }}>
        <h1 style={{ fontWeight: 300, letterSpacing: '0.15em' }}>神阙 Shenora</h1>
        <p style={row} data-testid="frontend-mode">
          frontend: <span style={value}>{mode}</span>
        </p>
        <p style={row} data-testid="bridge-state">
          bridge:{' '}
          {hosted
            ? <span style={value}>WebView2 host detected</span>
            : <span style={missing}>not available (plain browser)</span>}
        </p>
        <p style={row} data-testid="host-meta">
          host:{' '}
          {meta
            ? <span style={value}>{meta.name} v{meta.version}</span>
            : <span style={missing}>no injected metadata</span>}
        </p>
        <p style={row} data-testid="ipc-state">
          ipc:{' '}
          {!hosted ? <span style={missing}>n/a</span>
            : echo.loading ? <span>calling SAMPLE.ECHO…</span>
            : echo.error ? <span style={missing}>{echo.error.message}</span>
            : <span style={value}>SAMPLE.ECHO(&quot;shenora&quot;) → {echo.data?.echoed} ({echo.data?.length})</span>}
        </p>
        <p style={row} data-testid="event-state">
          events:{' '}
          {tick
            ? <span style={value}>SAMPLE.TICK #{tick.count} at {tick.at}</span>
            : <span style={missing}>none yet</span>}
        </p>
      </div>
    </main>
  );
}
