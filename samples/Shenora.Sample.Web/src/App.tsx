import { isShenoraAvailable } from '@shenora/react';

// Injected by the desktop host (WebViewHostOptions.InjectedGlobals — camelCase JSON).
declare global {
  interface Window {
    __SHENORA_SAMPLE__?: { name: string; version: string };
  }
}

/**
 * Shows what the host wired up, so a screenshot proves the whole stack: the page rendered
 * (virtual host or Vite), the bridge transport exists, and the injected global arrived.
 */
export function App() {
  const hosted = isShenoraAvailable();
  const meta = window.__SHENORA_SAMPLE__;
  const mode = import.meta.env.DEV ? 'dev (Vite)' : 'packaged';

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
      </div>
    </main>
  );
}
