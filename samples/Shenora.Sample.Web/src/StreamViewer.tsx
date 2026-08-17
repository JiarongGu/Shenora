import { getBridge, useShenoraEvent } from '@shenora/react';
import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * THE SEAM TEST for the kit's StreamingSession (P5.5 H9.5 / D21).
 *
 * The kit ships an off-screen browser that streams frames out and accepts synthetic input. It ships
 * no transport, no viewer, and no opinion about what that is for. So this file — in the SAMPLE, not
 * the library — is the product: a co-browse pane. Nothing here reaches into the kit; the whole
 * contract is the four lifecycle points (started / navigated / frames / ended) plus typed input.
 *
 * The transport being the app's job is the visible part: frames are JPEG BYTES and this bridge is
 * JSON, so the host base64s them into notifications and this decodes them into a data URL. A
 * server-backed profile would push the same bytes down a WebSocket and render them from a blob URL
 * instead — the session would not know the difference.
 *
 * Input goes back in the kit's LEGACY wire shape on purpose: it exercises the documented adoption
 * shim (`SessionInput.TryParseLegacyJson`), which is the path a real migrating consumer takes. A
 * greenfield app would send its own shape and build `SessionInput` records host-side.
 */
interface Frame {
  bytes: string;
  format: string;
  width: number;
  height: number;
}

interface Ended {
  reason: string;
  detail?: string;
}

export function StreamViewer({ hosted }: { hosted: boolean }) {
  const [frame, setFrame] = useState<Frame>();
  const [ended, setEnded] = useState<Ended>();
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string>();
  const imgRef = useRef<HTMLImageElement>(null);
  const held = useRef(false);

  useShenoraEvent<Frame>('STREAM', 'FRAME', setFrame);
  useShenoraEvent<Ended>('STREAM', 'ENDED', (e) => {
    setEnded(e);
    setRunning(false);
  });

  // Coordinates cross as FRACTIONS of the displayed image, which is what makes the protocol
  // independent of how big this pane happens to be — the host maps them to CSS px using the
  // viewport it emulates.
  const send = useCallback((input: unknown) => {
    getBridge()
      .invoke('STREAM', 'INPUT', { payload: { input: JSON.stringify(input) } })
      .catch((e: unknown) => setError(String(e)));
  }, []);

  const fractions = (event: React.PointerEvent | React.WheelEvent) => {
    const rect = imgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0 || rect.height === 0) return null;
    return { fx: (event.clientX - rect.left) / rect.width, fy: (event.clientY - rect.top) / rect.height };
  };

  const start = async () => {
    setError(undefined);
    setEnded(undefined);
    try {
      // This app's OWN origin — loopback while the dev server serves, the virtual host once
      // packaged. The host's NavigationGuard refuses anything else, which is the SSRF seam
      // demonstrated rather than described; the host also hands the session its bundle, so the
      // packaged origin actually renders (E1).
      await getBridge().invoke('STREAM', 'START', { payload: { url: window.location.origin + '/' } });
      setRunning(true);
      // Mirror this pane's box 1:1 into the page. Sent once here; a real viewer would also send it
      // from a ResizeObserver.
      const rect = imgRef.current?.getBoundingClientRect();
      send({ type: 'viewport', width: Math.round(rect?.width || 640), height: Math.round(rect?.height || 420),
             dpr: window.devicePixelRatio });
    } catch (e: unknown) {
      setError(String(e));
    }
  };

  const stop = async () => {
    try { await getBridge().invoke('STREAM', 'STOP'); } catch (e: unknown) { setError(String(e)); }
    setRunning(false);
  };

  // Stop the host-side session when this component goes away. Found by RUNNING it: a page reload
  // (Vite HMR, F5, a navigation) tears down the client while the session keeps streaming into a
  // channel nobody reads, and the next START then answers STREAM_ALREADY_RUNNING. The host cannot
  // see a reload — only the page knows, so only the page can say so.
  useEffect(() => () => {
    getBridge().invoke('STREAM', 'STOP').catch(() => {});
  }, []);

  return (
    // An EXPLICIT width, not just max-width. Found by running it: this sits in a centered,
    // shrink-to-fit column, and before the first frame the <img> has no src — so the box collapsed,
    // start() measured ~0, and the viewport it sent was clamped up to the kit's 320x240 MINIMUM. The
    // stream then rendered at 320x240 no matter how big the pane was.
    <div style={{ width: '32rem', maxWidth: '100%', margin: '1rem auto 0' }}>
      <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center', marginBottom: '0.5rem' }}>
        <button type="button" onClick={start} disabled={!hosted || running} data-testid="stream-start">
          start stream
        </button>
        <button type="button" onClick={stop} disabled={!hosted || !running} data-testid="stream-stop">
          stop
        </button>
      </div>

      <img
        ref={imgRef}
        data-testid="stream-frame"
        alt="streamed page"
        src={frame ? `data:image/${frame.format};base64,${frame.bytes}` : undefined}
        style={{
          display: 'block', // an inline <img> with no src does not honour the height below
          width: '100%', height: '14rem', objectFit: 'contain',
          border: '1px dashed #555', borderRadius: 8, background: '#161616',
          // The pane must not fight the page it is mirroring for gestures.
          touchAction: 'none', cursor: running ? 'crosshair' : 'default',
        }}
        onPointerDown={(e) => {
          const p = fractions(e);
          if (!running || !p) return;
          held.current = true;
          e.currentTarget.setPointerCapture(e.pointerId);
          send({ type: 'mouse', event: 'pressed', ...p });
        }}
        onPointerMove={(e) => {
          const p = fractions(e);
          // Moves are only interesting while the stream is live; a held button carries through so
          // the host can emit buttons:1 and drags actually work.
          if (running && p) send({ type: 'mouse', event: 'moved', ...p });
        }}
        onPointerUp={(e) => {
          const p = fractions(e);
          if (!running || !p) return;
          held.current = false;
          send({ type: 'mouse', event: 'released', ...p });
        }}
        onWheel={(e) => {
          const p = fractions(e);
          if (running && p) send({ type: 'wheel', ...p, dy: e.deltaY });
        }}
      />

      <p style={{ margin: '0.4rem 0 0', fontSize: '0.85rem', color: '#9a9a9a' }}>
        {error
          ? <span style={{ color: '#d1907f' }}>stream error: {error}</span>
          : ended
            // The lifecycle hook earning its keep: the frame channel completing says "no more
            // frames", the reason says whether that was a stop or a crash.
            ? <>ended: <b>{ended.reason}</b>{ended.detail ? ` (${ended.detail})` : ''}</>
            : frame
              ? <>streaming {frame.width}&times;{frame.height} &mdash; click and scroll it</>
              : running ? 'waiting for the first frame…' : 'not streaming'}
      </p>
    </div>
  );
}
