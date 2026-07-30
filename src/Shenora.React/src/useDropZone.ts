import { useEffect, useRef, useState } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import { debounce, randomId } from './internal.js';

/** The reserved module the drop-zone stack speaks (host: `DropZoneManager`/`DropZoneFacade`). */
export const DROP_ZONE_MODULE = 'DROP_ZONE';

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
 * loading, not on the handshake, so this hook's `REGISTER` cannot be wiped by a reset that arrives
 * after it. (It could, until the reset moved off the handshake — React runs CHILD effects before
 * PARENT effects, which made losing the registration the default outcome rather than bad luck.)
 */
export interface UseDropZoneOptions {
  /** The element the native overlay tracks. */
  targetRef: React.RefObject<HTMLElement | null>;
  /** Called with the dropped OS file paths. */
  onDrop: (files: string[], drop: DropZoneFileDrop) => void;
  /** False = zone torn down (same as unmount). Default true. */
  enabled?: boolean;
  /** Stable zone id; default: generated per mount. */
  zoneId?: string;
  /**
   * Class toggled on the element while a file drag hovers the zone. UNSTYLED — headless (D13):
   * the library ships no CSS; style it in the app. Default `"shenora-drop-hover"`.
   */
  dropClassName?: string;
  /** The bridge to speak over. Default: the shared default bridge. */
  bridge?: ShenoraBridge;
  /** The event bus host notifications arrive on. Default: the shared bus. */
  bus?: ShenoraEventBus;
}

const newZoneId = (): string => randomId('drop-zone-');

/**
 * Sync a native drop-zone overlay to a page element, ported from the primary desktop sibling
 * (its fix-history comments kept below): the host positions a transparent WinForms overlay
 * over the element to capture REAL OS file paths — including drags started while the app is in
 * the background. Bounds re-sync (debounced) on resize/scroll/intersection changes; the host
 * converts the CSS rect to physical pixels per-monitor.
 *
 * How the visibility dance works: mouse leaves the element → SHOW (overlay up, ready to catch a
 * drag); mouse enters → the host hides the overlay (hover effects keep working); an inactive
 * window always shows overlays (background drag-drop); while the overlay is visible the host
 * emits DRAG_ENTER/DRAG_LEAVE for CSS feedback.
 */
export function useDropZone(options: UseDropZoneOptions): void {
  const { targetRef, enabled = true } = options;
  const zoneIdRef = useRef(options.zoneId ?? newZoneId());
  const dropClassRef = useRef(options.dropClassName ?? 'shenora-drop-hover');
  const onDropRef = useRef(options.onDrop);
  onDropRef.current = options.onDrop;
  const bridgeRef = useRef(options.bridge);
  bridgeRef.current = options.bridge;
  const bus = options.bus ?? defaultEventBus;

  // Make the ref's CONTENT reactive (P5.5 H2). `targetRef` is a stable object, so effects keyed on it
  // run exactly once — and if `targetRef.current` was null on that run (a conditionally-rendered
  // target, or any order where the ref is attached after the first commit) the effect bailed out and
  // NEVER re-ran: the zone was silently dead for the component's whole life, with no error anywhere.
  // A ref mutation triggers no render, so this effect deliberately has NO dependency array — it
  // observes `current` after every commit. `setElement` with an unchanged value is a no-op in React,
  // so this cannot loop.
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
  // Teardown epoch: the REGISTER ack must not apply after its zone was torn down — under
  // StrictMode's mount-unmount-remount, a stale ack marked the DESTROYED zone "registered" and
  // the overlay silently never existed again (found in review). Cleanup bumps the epoch; acks
  // from an older epoch are ignored.
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
          (error: unknown) => console.error('[shenora] drop-zone REGISTER failed:', error),
        )
        .finally(() => {
          if (epochRef.current === epoch) registeringRef.current = false;
        });
    } else if (changed) {
      lastBoundsRef.current = bounds;
      bridge
        .invoke(DROP_ZONE_MODULE, 'UPDATE', { payload: { zoneId: zoneIdRef.current, ...bounds } })
        .catch((error: unknown) => console.error('[shenora] drop-zone UPDATE failed:', error));
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
        .catch((error: unknown) => console.error('[shenora] drop-zone SHOW failed:', error));
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

      // Unregister whenever this effect tears down — on unmount OR when `enabled` flips false —
      // unconditionally (not gated on the REGISTER ack) so an in-flight REGISTER is also torn
      // down. The host's UnregisterZone no-ops if the overlay isn't there yet, and the ordered
      // IPC channel processes the earlier REGISTER before this UNREGISTER (create-then-destroy,
      // no orphan).
      if (attemptedRef.current) {
        (bridgeRef.current ?? getBridge())
          .invoke(DROP_ZONE_MODULE, 'UNREGISTER', { payload: { zoneId: zoneIdRef.current } })
          .catch((error: unknown) => console.error('[shenora] drop-zone UNREGISTER failed:', error));
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
