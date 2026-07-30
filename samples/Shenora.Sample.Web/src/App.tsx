import {
  getBridge,
  isShenoraAvailable,
  useDropZone,
  useShenoraEvent,
  useShenoraQuery,
  useWindowMaximized,
  WindowCommands,
} from '@shenora/react';
import { useEffect, useMemo, useRef, useState } from 'react';

// Injected by the desktop host (WebViewHostOptions.InjectedGlobals — camelCase JSON).
declare global {
  interface Window {
    __SHENORA_SAMPLE__?: { name: string; version: string };
  }
}

const value: React.CSSProperties = { color: '#7fd18c', fontWeight: 600 };
const missing: React.CSSProperties = { color: '#d1907f', fontWeight: 600 };
const row: React.CSSProperties = { margin: '0.25rem 0', fontSize: '1rem' };

/**
 * The frameless window's chrome, rendered by the page (the host removed the OS title bar): the
 * header background drags the window natively, the buttons drive the WINDOW module, and a thin
 * strip at the very top re-adds the top resize edge the WebView covers.
 */
function TitleBar({ hosted, commands }: { hosted: boolean; commands: WindowCommands }) {
  const maximized = useWindowMaximized(commands);
  const button: React.CSSProperties = {
    background: 'none',
    border: 'none',
    color: '#eceaf2',
    width: '2.6rem',
    height: '2rem',
    cursor: 'default',
    fontSize: '0.9rem',
  };
  return (
    <>
      {/* The top resize strip: the host's WM_NCCALCSIZE keeps native side/bottom borders; the
          top is covered by the WebView, so this hands off to the native size loop. */}
      <div
        style={{ position: 'fixed', top: 0, left: 0, right: 0, height: 6, zIndex: 20 }}
        onMouseDown={() => { if (hosted) void commands.startResize('top'); }}
      />
      <header
        data-testid="title-bar"
        style={{
          display: 'flex', alignItems: 'center', justifyContent: 'space-between',
          height: '2rem', paddingLeft: '0.75rem', userSelect: 'none',
          background: '#252525', color: '#9a9a9a', fontSize: '0.85rem',
        }}
        onMouseDown={(e) => { if (e.button === 0 && hosted) void commands.startDrag(); }}
      >
        <span>神阙 Shenora Sample</span>
        <div onMouseDown={(e) => e.stopPropagation()}>
          <button style={button} data-testid="btn-minimize" onClick={() => void commands.minimize()}>─</button>
          <button style={button} data-testid="btn-maximize" onClick={() => void commands.toggleMaximize()}>
            {maximized ? '❐' : '☐'}
          </button>
          <button style={button} data-testid="btn-close" onClick={() => void commands.close()}>✕</button>
        </div>
      </header>
    </>
  );
}

/**
 * Shows what the host wired up, so a screenshot proves the whole stack: page rendered (virtual
 * host or Vite), bridge present, injected global, a typed IPC round-trip, the native event
 * stream, page-driven window chrome, a native drop zone, and a secondary window.
 */
export function App() {
  const hosted = isShenoraAvailable();
  const meta = window.__SHENORA_SAMPLE__;
  const mode = import.meta.env.DEV ? 'dev (Vite)' : 'packaged';
  const commands = useMemo(() => new WindowCommands(), []);

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

  // Native drop zone over this element — dropped files arrive as REAL OS paths.
  const dropRef = useRef<HTMLDivElement>(null);
  const [dropped, setDropped] = useState<string[]>();
  useDropZone({
    targetRef: dropRef,
    onDrop: setDropped,
    zoneId: 'sample-drop',
    dropClassName: 'drop-hover',
    enabled: hosted,
  });

  const [panelOpen, setPanelOpen] = useState(false);
  const refreshPanel = () =>
    getBridge().invoke<{ open: boolean }>('SAMPLE', 'HAS_PANEL').then((r) => setPanelOpen(r.open), () => {});

  return (
    <>
      <TitleBar hosted={hosted} commands={commands} />
      <main style={{ display: 'grid', placeItems: 'center', minHeight: 'calc(100vh - 2rem)' }}>
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
          <div
            ref={dropRef}
            data-testid="drop-zone"
            style={{
              margin: '1rem auto 0', padding: '0.8rem 1.2rem', maxWidth: '30rem',
              border: '1px dashed #555', borderRadius: 8, fontSize: '0.9rem', color: '#9a9a9a',
            }}
          >
            {dropped?.length
              ? <span style={value}>dropped: {dropped.join(', ')}</span>
              : 'drop files here (native OS paths)'}
          </div>
          <p style={{ ...row, marginTop: '1rem' }}>
            <button
              data-testid="btn-panel"
              style={{ background: '#2c2c2c', color: '#eceaf2', border: '1px solid #555', borderRadius: 6, padding: '0.4rem 0.9rem' }}
              onClick={() => {
                void getBridge().invoke('SAMPLE', panelOpen ? 'CLOSE_PANEL' : 'OPEN_PANEL')
                  .then(() => setTimeout(refreshPanel, 300));
              }}
            >
              {panelOpen ? 'close the secondary window' : 'open a secondary window'}
            </button>
            {' '}
            <span data-testid="panel-state" style={panelOpen ? value : { color: '#9a9a9a' }}>
              panel: {panelOpen ? 'open' : 'closed'}
            </span>
          </p>
        </div>
      </main>
    </>
  );
}
