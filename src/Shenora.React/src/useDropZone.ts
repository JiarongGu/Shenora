import { useEffect, useRef, useState, type RefObject } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import { debounce, randomId } from './internal.js';

/** The reserved module the drop-zone stack speaks (host: `DropZoneManager`/`DropZoneModule`). */
export const DROP_ZONE_MODULE = 'SHENORA.DROPZONE';

/** A native file drop delivered to a zone. */
export interface DropZoneFileDrop {
  zoneId: string;
  /** REAL OS paths — the whole point: the DOM's drop events only ever see blob URLs. */
  files: string[];
  /** Drop position in the zone's physical pixels. */
  position: { x: number; y: number };
}

/**
 * Inputs for {@link useDropZone}.
 *
 * No ordering constraint against `notifyReady()`: the host clears zones when a new DOCUMENT starts
 * loading, not on the handshake, so this hook's `REGISTER` cannot be wiped by a later reset.
 */
export interface UseDropZoneOptions {
  /** The element the native overlay tracks. */
  // ⚠ `RefObject` is IMPORTED, never `React.RefObject` — that emits a `.d.ts` naming `React` with no
  // import, so it resolves only for a consumer whose program already has `@types/react` globally.
  targetRef: RefObject<HTMLElement | null>;
  /** Called with the dropped OS file paths. */
  onDrop: (files: string[], drop: DropZoneFileDrop) => void;
  /** False = zone torn down (same as unmount). Default true. */
  enabled?: boolean;
  /** Stable zone id; default: generated per mount. ⚠ Read on the FIRST render only — changing it later
   * does not re-register the zone, which is what "stable" means here. */
  zoneId?: string;
  /**
   * Class toggled on the element while a file drag hovers the zone. UNSTYLED — the library ships no
   * CSS; style it in the app. Default `"shenora-drop-hover"`.
   * ⚠ Read on the FIRST render only, like {@link UseDropZoneOptions.zoneId}: a mid-hover change would
   * add one class and remove another, leaving the first stuck on the element. Switch it by remounting.
   */
  dropClassName?: string;
  /** The bridge to speak over. Default: the shared default bridge. */
  bridge?: ShenoraBridge;
  /** The event bus host notifications arrive on. Default: the shared bus. */
  bus?: ShenoraEventBus;
  /**
   * Where a failed REGISTER / UPDATE / SHOW / UNREGISTER is reported. Default: `console.error`.
   *
   * ⚠ Worth routing somewhere real, because a failure here is INVISIBLE in the UI: the page renders
   * exactly as it should and files simply do not drop.
   */
  onError?: (error: unknown, route: string) => void;
}

const newZoneId = (): string => randomId('drop-zone-');

/**
 * **The file-drop API for a Shenora page. Do not use the DOM's own drop event for files —
 * it is not an alternative here, it is the thing this exists to replace.** A page-side `onDrop` gets a
 * `File` whose only accessor is its CONTENT, so every dropped file is copied into the renderer and
 * across the IPC boundary before the app knows whether it wants any of them. This gives you `string[]`
 * OS paths instead — open lazily, stream, hash incrementally, move or link without copying.
 *
 * The host positions a transparent native overlay over the element to capture those paths, including
 * for drags started while the app is in the background. Bounds re-sync (debounced) on
 * resize/scroll/intersection changes; the host converts the CSS rect to physical pixels per-monitor.
 *
 * How the visibility dance works: mouse leaves the element → SHOW (overlay up, ready to catch a
 * drag); mouse enters → the host hides the overlay (hover effects keep working); an inactive
 * window always shows overlays (background drag-drop); while the overlay is visible the host
 * emits DRAG_ENTER/DRAG_LEAVE for CSS feedback.
 */
export function useDropZone(options: UseDropZoneOptions): void {
  const { targetRef, enabled = true } = options;
  // ⚠ LAZY, because `useRef(newZoneId())` evaluates its argument on EVERY render and keeps only the
  // first. The empty string is a safe sentinel: a generated id is never empty, and a caller passing
  // `zoneId: ''` short-circuits the `??` and so still never reaches the generator.
  const zoneIdRef = useRef('');
  if (zoneIdRef.current === '') zoneIdRef.current = options.zoneId ?? newZoneId();

  // Read ONCE, unlike `onDrop`/`bridge` below which track the latest value — see the option's docs.
  const dropClassRef = useRef(options.dropClassName ?? 'shenora-drop-hover');
  const onDropRef = useRef(options.onDrop);
  onDropRef.current = options.onDrop;
  const bridgeRef = useRef(options.bridge);
  bridgeRef.current = options.bridge;
  const bus = options.bus ?? defaultEventBus;

  // Tracks the latest handler (like `onDrop`), so a cleanup that runs long after mount still reports
  // through the sink the app has NOW. Logs only when the app supplied none.
  const onErrorRef = useRef(options.onError);
  onErrorRef.current = options.onError;
  const reportRef = useRef<(error: unknown, route: string) => void>(() => {});
  reportRef.current = (error: unknown, route: string) =>
    onErrorRef.current
      ? onErrorRef.current(error, route)
      : console.error(`[shenora] drop-zone ${route} failed:`, error);

  // 🔴 Make the ref's CONTENT reactive. `targetRef` is a stable object, so an effect keyed on it runs
  // exactly once — and if `targetRef.current` is null on that run (a conditionally-rendered target, or
  // any order where the ref is attached after the first commit) the effect bails out and NEVER re-runs:
  // the zone is silently dead for the component's whole life, with no error anywhere. A ref mutation
  // triggers no render, so this effect has NO dependency array; `setElement` with an unchanged value is
  // a React no-op, so it cannot loop.
  const [element, setElement] = useState<HTMLElement | null>(null);
  useEffect(() => {
    setElement(targetRef.current ?? null);
  });

  const isRegisteredRef = useRef(false);
  // Whether a REGISTER has ever been SENT for this zone (even if not yet acked). The cleanup
  // unregisters on THIS (not on the ack) so a fast unmount before REGISTER resolves still tears
  // the overlay down.
  const attemptedRef = useRef(false);
  // A REGISTER is in flight — guards against sending a duplicate before the first resolves.
  const registeringRef = useRef(false);
  // Teardown epoch: a REGISTER ack must not apply after its zone was torn down. Under StrictMode's
  // mount-unmount-remount a stale ack marks the DESTROYED zone "registered" and the overlay silently
  // never exists again. Cleanup bumps the epoch; acks from an older epoch are ignored.
  const epochRef = useRef(0);
  const lastBoundsRef = useRef({ x: 0, y: 0, width: 0, height: 0 });

  const syncBoundsRef = useRef<() => void>(() => {});
  syncBoundsRef.current = () => {
    const element = targetRef.current;
    if (!element) return;
    const bridge = bridgeRef.current ?? getBridge();
    const rect = element.getBoundingClientRect();
    const bounds = {
      x: Math.round(rect.left),
      y: Math.round(rect.top),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
    };
    const changed =
      !isRegisteredRef.current ||
      bounds.x !== lastBoundsRef.current.x ||
      bounds.y !== lastBoundsRef.current.y ||
      bounds.width !== lastBoundsRef.current.width ||
      bounds.height !== lastBoundsRef.current.height;

    if (!isRegisteredRef.current) {
      if (registeringRef.current) return;
      registeringRef.current = true;
      attemptedRef.current = true;
      lastBoundsRef.current = bounds;
      const epoch = epochRef.current;
      bridge
        .invoke(DROP_ZONE_MODULE, 'REGISTER', { payload: { zoneId: zoneIdRef.current, ...bounds } })
        .then(
          () => {
            if (epochRef.current === epoch) isRegisteredRef.current = true;
          },
          (error: unknown) => reportRef.current(error, 'REGISTER'),
        )
        .finally(() => {
          if (epochRef.current === epoch) registeringRef.current = false;
        });
    } else if (changed) {
      lastBoundsRef.current = bounds;
      bridge
        .invoke(DROP_ZONE_MODULE, 'UPDATE', { payload: { zoneId: zoneIdRef.current, ...bounds } })
        .catch((error: unknown) => reportRef.current(error, 'UPDATE'));
    }
  };

  // Track the element and keep the native overlay in sync.
  useEffect(() => {
    if (!enabled || !element) return;

    // The host's occlusion check finds the element through this attribute.
    element.setAttribute('data-drop-zone-id', zoneIdRef.current);

    const syncBounds = debounce(() => syncBoundsRef.current(), 100);
    syncBoundsRef.current();

    const sendShow = debounce(() => {
      (bridgeRef.current ?? getBridge())
        .invoke(DROP_ZONE_MODULE, 'SHOW', { payload: { zoneId: zoneIdRef.current } })
        .catch((error: unknown) => reportRef.current(error, 'SHOW'));
    }, 100);

    // The element's mouseleave (and the window losing focus) re-arm the overlay — native
    // MouseLeave alone is unreliable through the WebView.
    const onMouseLeave = () => sendShow();
    const onWindowBlur = () => sendShow();
    element.addEventListener('mouseleave', onMouseLeave);
    window.addEventListener('blur', onWindowBlur);

    // Guarded construction: test DOMs (jsdom) lack the observers; real WebView2 always has them.
    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(() => syncBounds())
      : undefined;
    const intersectionObserver = typeof IntersectionObserver !== 'undefined'
      ? new IntersectionObserver(() => syncBounds(), { threshold: [0, 0.1, 0.5, 0.9, 1.0] })
      : undefined;
    resizeObserver?.observe(element);
    intersectionObserver?.observe(element);
    window.addEventListener('scroll', syncBounds, true);
    window.addEventListener('resize', syncBounds);

    return () => {
      sendShow.cancel();
      syncBounds.cancel();
      resizeObserver?.disconnect();
      intersectionObserver?.disconnect();
      window.removeEventListener('scroll', syncBounds, true);
      window.removeEventListener('resize', syncBounds);
      window.removeEventListener('blur', onWindowBlur);
      element.removeEventListener('mouseleave', onMouseLeave);
      element.removeAttribute('data-drop-zone-id');

      // Unregister whenever this effect tears down — on unmount OR when `enabled` flips false — and
      // never gated on the REGISTER ack, so an in-flight REGISTER is torn down too. The host's
      // UnregisterZone no-ops if the overlay isn't there yet, and the ordered IPC channel processes
      // the earlier REGISTER first, so there is no orphan.
      if (attemptedRef.current) {
        (bridgeRef.current ?? getBridge())
          .invoke(DROP_ZONE_MODULE, 'UNREGISTER', { payload: { zoneId: zoneIdRef.current } })
          .catch((error: unknown) => reportRef.current(error, 'UNREGISTER'));
        epochRef.current++; // invalidate any in-flight REGISTER's ack (see epochRef)
        isRegisteredRef.current = false;
        registeringRef.current = false; // a remount must re-send immediately
        attemptedRef.current = false;
      }
    };
  }, [enabled, element]);

  // Drag-hover CSS feedback.
  useEffect(() => {
    if (!enabled || !element) return;
    const dropClass = dropClassRef.current;

    const offEnter = bus.subscribe<{ zoneId: string }>(DROP_ZONE_MODULE, 'DRAG_ENTER', (event) => {
      if (event.payload?.zoneId === zoneIdRef.current) element.classList.add(dropClass);
    });
    const offLeave = bus.subscribe<{ zoneId: string }>(DROP_ZONE_MODULE, 'DRAG_LEAVE', (event) => {
      if (event.payload?.zoneId === zoneIdRef.current) element.classList.remove(dropClass);
    });
    return () => {
      offEnter();
      offLeave();
      element.classList.remove(dropClass);
    };
  }, [enabled, element, bus]);

  // File drops.
  useEffect(() => {
    if (!enabled) return;
    return bus.subscribe<DropZoneFileDrop>(DROP_ZONE_MODULE, 'FILE_DROP', (event) => {
      const drop = event.payload;
      if (!drop || drop.zoneId !== zoneIdRef.current) return;
      targetRef.current?.classList.remove(dropClassRef.current);
      onDropRef.current(drop.files, drop);
    });
  }, [enabled, targetRef, bus]);
}
