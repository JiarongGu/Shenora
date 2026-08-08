import {
  getBridge,
  isShenoraAvailable,
  useDropZone,
  useShenoraEvent,
  useShenoraOperations,
  useShenoraQuery,
  useWindowMaximized,
  WindowCommands,
  type OperationProgress,
  type ShellInfo,
} from '@shenora/react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { StreamViewer } from './StreamViewer';

// Injected by the desktop host (WebViewHostOptions.InjectedGlobals — camelCase JSON).
declare global {
  interface Window {
    __SHENORA_SAMPLE__?: { name: string; version: string };
  }
}

/**
 * `total` present → a ratio, rendered as a rounded percent; otherwise the bare value in its own unit.
 * That division (`value / total`) is the CONSUMER's policy, not the kit's — the kit ships no percent
 * helper (see `@shenora/react`'s README) precisely so this one-liner stays here, not in `src/`.
 *
 * The PARAMETER type is imported, not re-declared. It used to be written out inline
 * (`{ value: number; total?: number; unit?: string }`) — not by choice: `OperationProgress` was
 * missing from the barrel, so the type of `OperationInfo.progress` was unnameable from outside the
 * package, and the sample quietly duplicating the shape was the only visible symptom.
 */
function formatProgress(progress?: OperationProgress): string {
  if (!progress) return 'starting…';
  if (progress.total) return `${Math.round((progress.value / progress.total) * 100)}%`;
  return `${progress.value}${progress.unit ? ` ${progress.unit}` : ''}`;
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
  const buttons = useRef<HTMLDivElement>(null);

  // Report where the buttons are, in CSS px relative to the WebView2. Re-sent on resize because the
  // rects are a snapshot: a stale one moves the hit-test off the button the user can see.
  //
  // WHEN HOSTED, THIS IS ALSO WHAT SIZES THE HOLE (P5.6 hybrid). The host cuts this exact union out
  // of the WebView2's window region and paints the three buttons itself — so the OS finally routes
  // real input to the window and offers Snap Layouts. Whatever we render inside these rects is
  // clipped away and never appears on screen; we keep rendering it purely so an UNHOSTED browser
  // preview still shows a usable title bar.
  useEffect(() => {
    if (!hosted) return;
    const report = () => {
      const host = buttons.current;
      if (!host) return;
      const kinds = ['minimize', 'maximize', 'close'] as const;
      const rects = [...host.children].map((el, i) => {
        const r = el.getBoundingClientRect();
        return { kind: kinds[i], x: r.left, y: r.top, width: r.width, height: r.height };
      });
      void commands.setCaptionButtons(rects);
    };
    report();
    window.addEventListener('resize', report);
    return () => window.removeEventListener('resize', report);
  }, [hosted, commands]);

  // No hover/pressed styling here any more: hosted, these pixels are the host's and ours are
  // invisible; unhosted, there is no window to drive and plain buttons are the honest preview.
  // Headless (D13) still holds — the COLOURS the host paints with come from MainForm, not the kit.
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
        {/* The onClick handlers stay as the fallback for an unhosted browser and for a host that
            has NOT claimed the hit-test; when it has, the host performs the action itself and these
            never fire, because the click is delivered to the window, not to the page. */}
        <div ref={buttons} onMouseDown={(e) => e.stopPropagation()}>
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
 * The one-way path (P6.3a), now on the kit's own operations primitive (D23/0.2.0): `post` starts
 * the SLOW route, `ctx.Run` hands the work off host-side, and `useShenoraOperations` is the
 * shared, HOST-BACKED store that reports its progress — no hand-rolled `SLOW_PROGRESS`/`SLOW_DONE`
 * reducer, no per-app wiring. This is also the sample's adoption example: the supported shape for
 * anything that is not a quick, UI-thread-safe call.
 *
 * ONE subscription is opened no matter how many components read `useShenoraOperations`, and a
 * component mounting mid-run is caught up by the store's `LIST` snapshot. The two buttons drive
 * the SAME route in its two shapes so the difference is visible rather than argued: `block` leaves
 * the work in the route's synchronous segment, which runs on the host's UI thread and freezes the
 * window — watch the tick above stop — while `stream` hands off through `ctx.Run` and reports
 * progress, cancellable mid-flight (the operation was started with `Cancellable = true`).
 */
function SlowPanel({ hosted }: { hosted: boolean }) {
  // The store is shared/module-wide (any op, any module); this pane selects only ITS operation by
  // the module + kind the host route sets (SampleModule's SLOW case: Kind = "SLOW"). `s.running` is
  // the store's own shipped selector for "currently running", not a hand-rolled status check — the
  // supported idiom its own doc recommends.
  const operation = useShenoraOperations((s) => s.running.find((o) => o.module === 'SAMPLE' && o.kind === 'SLOW'));

  // `post`, not `invoke`: the route's own response carries only an echo (mode/ranOnUiThread/
  // operationId) that this pane does not need — the operation store is the source of truth.
  const block = () => getBridge().post('SAMPLE', 'SLOW', { payload: { mode: 'block', ms: 3000 } });
  const stream = () => getBridge().post('SAMPLE', 'SLOW', { payload: { mode: 'stream', ms: 3000 } });

  return (
    <p style={row} data-testid="slow-state">
      <button
        style={{ padding: '0.35rem 0.75rem' }}
        disabled={!hosted}
        data-testid="btn-slow-block"
        onClick={block}
      >
        block the UI thread (3s)
      </button>
      {' '}
      <button
        style={{ padding: '0.35rem 0.75rem' }}
        disabled={!hosted}
        data-testid="btn-slow-stream"
        onClick={stream}
      >
        stream it instead
      </button>
      {' '}
      <span style={operation ? value : { color: '#9a9a9a' }}>
        {operation
          // `detail.text` already carries "onUiThread: False" from the host's Report() call — the
          // proof the work really left the UI thread, rather than the design merely claiming it does.
          ? `${formatProgress(operation.progress)} — ${operation.detail?.text ?? 'starting…'}`
          : 'slow route: idle'}
      </span>
      {operation?.cancellable && (
        <>
          {' '}
          <button
            style={{ padding: '0.2rem 0.6rem' }}
            data-testid="btn-slow-cancel"
            onClick={() => useShenoraOperations.actions.cancel(operation.id)}
          >
            cancel
          </button>
        </>
      )}
    </p>
  );
}

/**
 * The mission scheduler (`Shenora`'s `Missions` layer), reported through the SAME operations store the
 * slow route uses — because the host bound the two with one `IMissionObserver` written in the app. The
 * kit ships no such adapter: execution must not learn what an operation is (D19/D20).
 *
 * Four items are submitted at once. Two contend for ONE path and can never overlap; two touch
 * disjoint paths and can. A lane of capacity 2 caps the whole batch, so the count below settles at
 * `2 running` while the rest wait their turn.
 *
 * ⚠ QUEUED WORK IS NOT LISTED HERE ANY MORE (D66, 2026-08-08). The app used to call `op.Wait("queued")`
 * the instant it opened the operation, and that was the ONLY code anywhere driving the waiting band —
 * which is what settled the decision to cut it: a queued mission is host-initiated work, not a request,
 * and a request is in flight or done. Queue depth belongs to the mission stream the app is already
 * observing (`IMissionObserver`), not to the request list. Showing it again is a sample task, not a
 * kit one.
 */
function SchedulerPanel({ hosted }: { hosted: boolean }) {
  const running = useShenoraOperations((s) => s.running.filter((o) => o.module === 'SAMPLE_LOGIC'));
  const kinds = (list: { kind: string }[]) =>
    list.map((o) => o.kind).sort().join(', ') || '—';

  return (
    <p style={row} data-testid="scheduler-state">
      <button
        style={{ padding: '0.35rem 0.75rem' }}
        disabled={!hosted}
        data-testid="btn-schedule-demo"
        onClick={() => getBridge().post('SAMPLE_LOGIC', 'SCHEDULE_DEMO')}
      >
        schedule 4 items (2 contend)
      </button>
      {' '}
      {/* Two CHAINS whose staging overlaps and whose file landings do not — the composition the
          file-update queue exists for. Same operations store: a chain is one mission. */}
      <button
        style={{ padding: '0.35rem 0.75rem' }}
        disabled={!hosted}
        data-testid="btn-chain-demo"
        onClick={() => getBridge().post('SAMPLE_LOGIC', 'CHAIN_DEMO')}
      >
        2 chains (stage ∥, land 1-at-a-time)
      </button>
      {' '}
      <span style={running.length ? value : { color: '#9a9a9a' }}>
        {running.length
          ? `running ${running.length} [${kinds(running)}]`
          : 'scheduler: idle'}
      </span>
    </p>
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
  //
  // This used to carry an ordering constraint against the useDropZone below, because the host
  // cleared drop zones on the handshake and React runs CHILD effects before PARENT effects. The kit
  // clears them on DOCUMENT CHANGE now (P5.6), so the constraint is gone and this effect no longer
  // has to stay above anything.
  //
  // Not `void`: a rejected handshake (no host, disposed bridge, timeout) became an unhandled
  // promise rejection, which in a WebView2 page is a silent console error — and this is the snippet
  // adopters copy.
  //
  // The handshake RESOLVES to what the host is and can do, so the page renders its shell-specific
  // parts from data instead of sniffing the platform — the one thing that lets this bundle also run
  // on the MAUI shell, which answers the same handshake with a much shorter list. Undefined means
  // "assume nothing" (plain browser dev, or a host that declares none), never "assume desktop".
  const [shell, setShell] = useState<ShellInfo>();
  useEffect(() => {
    if (!hosted) return;
    getBridge()
      .notifyReady()
      .then(setShell)
      .catch((error: unknown) => console.error('[sample] ready handshake failed', error));
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

  // P5: lease a pooled OFF-SCREEN browser session in the host and render this very page in it —
  // the returned title/length come from the offscreen page's LIVE DOM (its JS ran). The host's
  // navigation guard only allows loopback URLs, so the packaged (virtual-host) origin shows the
  // structured refusal instead — the policy seam, demonstrated either way.
  const [probe, setProbe] = useState<string>();
  const runProbe = () => {
    setProbe('leasing an offscreen session…');
    getBridge()
      .invoke<{ length: number; title: string }>('RENDER', 'PROBE', { payload: { url: window.location.origin } })
      .then(
        (r) => setProbe(`offscreen "${r.title}" rendered — ${r.length} chars of live DOM`),
        (e) => setProbe(`refused/failed: ${e.message}`),
      );
  };

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
          <p style={row} data-testid="shell-capabilities">
            shell:{' '}
            {shell
              ? <span style={value}>{shell.name} · {shell.capabilities.join(', ')}</span>
              : <span style={missing}>nothing advertised — assume nothing</span>}
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
          <p style={row}>
            <button
              data-testid="btn-render"
              style={{ background: '#2c2c2c', color: '#eceaf2', border: '1px solid #555', borderRadius: 6, padding: '0.4rem 0.9rem' }}
              onClick={runProbe}
            >
              render this page off-screen
            </button>
            {' '}
            <span data-testid="render-state" style={probe?.startsWith('offscreen') ? value : { color: '#9a9a9a' }}>
              {probe ?? 'sessions: idle'}
            </span>
          </p>
          <SlowPanel hosted={hosted} />
          <SchedulerPanel hosted={hosted} />
          {/* The kit ships the streaming PRIMITIVE; this pane is the product built on it, and it
              lives here in the sample precisely because the library must not decide it (D21/D22). */}
          <StreamViewer hosted={hosted} />
        </div>
      </main>
    </>
  );
}
