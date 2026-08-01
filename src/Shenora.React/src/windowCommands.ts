import { useEffect, useRef, useState } from 'react';
import type { ShenoraBridge } from './bridge.js';
import { debounce } from './internal.js';
import { BaseModuleService } from './moduleService.js';

/** The top resize edges — the only ones that exist: the frameless technique keeps the native
 * side/bottom resize borders, so only the top (covered by the WebView) needs page-side help. */
export type WindowResizeEdge = 'top' | 'topLeft' | 'topRight';

/** Which system caption button a page-drawn region stands in for (mirrors the host's enum). */
export type CaptionButtonKind = 'minimize' | 'maximize' | 'close';

/**
 * Where the page drew one caption button, in CSS px relative to the WebView2 — i.e. straight out of
 * `getBoundingClientRect()`. The host converts to physical px using the control's DeviceDpi.
 */
export interface CaptionButtonRect {
  kind: CaptionButtonKind;
  x: number;
  y: number;
  width: number;
  height: number;
}

// A plain interface — NOT `extends Record<string, unknown>` (P5.5 H6). That widened
// `keyof TRequests & string` to `string`, so a mistyped route compiled and every payload collapsed to
// `unknown`: the kit's own service was demonstrating the anti-pattern the base class exists to prevent.
interface WindowRequests {
  MINIMIZE: void;
  TOGGLE_MAXIMIZE: void;
  CLOSE: void;
  IS_MAXIMIZED: void;
  START_DRAG: void;
  START_RESIZE: { edge: WindowResizeEdge };
  SET_THEME: { dark: boolean };
  SET_CAPTION_BUTTONS: { buttons: CaptionButtonRect[] };
}

/**
 * Typed client for the host's `WINDOW` module (`WindowCommandFacade` in Shenora.WebView2) —
 * drive the frameless window's chrome from the page: chrome buttons call
 * `minimize`/`toggleMaximize`/`close`, the header's `onMouseDown` calls `startDrag` (the window
 * then drags natively — snap and multi-monitor included), and a thin strip at the very top
 * calls `startResize` on mousedown. `setTheme` resyncs the native chrome on a runtime
 * light↔dark switch (without it the frame keeps the old theme's border; measured host-side).
 */
export class WindowCommands extends BaseModuleService<WindowRequests> {
  constructor(bridge?: ShenoraBridge) {
    super('WINDOW', bridge);
  }

  minimize(): Promise<void> {
    return this.send('MINIMIZE');
  }

  toggleMaximize(): Promise<void> {
    return this.send('TOGGLE_MAXIMIZE');
  }

  close(): Promise<void> {
    return this.send('CLOSE');
  }

  /** Authoritative maximize state (a frameless manual maximize never shows in the DOM). */
  async isMaximized(): Promise<boolean> {
    const result = await this.send<{ maximized: boolean }>('IS_MAXIMIZED');
    return result.maximized;
  }

  /** Call from the header's `onMouseDown` — hands off to the OS move loop. */
  startDrag(): Promise<void> {
    return this.send('START_DRAG');
  }

  /** Call from the top strip's `onMouseDown` — hands off to the OS size loop. */
  startResize(edge: WindowResizeEdge = 'top'): Promise<void> {
    return this.send('START_RESIZE', { payload: { edge } });
  }

  /** Resync the native chrome to the app theme (host `WindowCommandOptions.ApplyTheme`). */
  setTheme(dark: boolean): Promise<void> {
    return this.send('SET_THEME', { payload: { dark } });
  }

  /**
   * Tell the host where the page drew its caption buttons, so the OS can treat them as the real
   * thing — chiefly so Windows 11 offers **Snap Layouts** on the maximize button, which a page-drawn
   * button never gets otherwise.
   *
   * Two consequences worth knowing before calling this. The host takes over CLICKS in those rects
   * (the OS stops delivering them to the page), so your `onClick` handlers stop firing there — the
   * host performs minimize/maximize/close itself, through the same commands. And CSS `:hover` stops
   * firing too, so subscribe to the host's caption-button state to render hot/pressed; it is also the
   * only way to stay hot while the pointer is over the snap flyout, which is a different window.
   *
   * Re-send on every layout change (a resize, a theme that changes button size): the rectangles are a
   * snapshot, and a stale one moves the hit-test off the button the user can see. Pass an empty array
   * to hand every pixel back to the page.
   */
  setCaptionButtons(buttons: CaptionButtonRect[]): Promise<void> {
    return this.send('SET_CAPTION_BUTTONS', { payload: { buttons } });
  }
}

/**
 * The max/restore-glyph resync pattern from the source app: the authoritative maximize state,
 * re-queried when a resize SETTLES (a maximize/restore always resizes the window, and the DOM has no
 * other signal for the manual work-area maximize). Failures (plain browser, no host) leave it false.
 *
 * Read once immediately, then on the TRAILING edge of a 100 ms debounce — not once per `resize`
 * event, which a window drag fires ~180 times in three seconds. Coalescing is the correct semantics
 * here, not just the cheap one: maximize/restore is a single step, so only the end state matters. Do
 * not build on intermediate values during a drag; there are none.
 */
export function useWindowMaximized(commands?: WindowCommands): boolean {
  const [maximized, setMaximized] = useState(false);
  const defaultCommands = useRef<WindowCommands | undefined>(undefined);

  useEffect(() => {
    const target = commands ?? (defaultCommands.current ??= new WindowCommands());
    let stale = false;
    const query = () => {
      target.isMaximized().then(
        (value) => {
          if (!stale) setMaximized(value);
        },
        () => {},
      );
    };

    // DEBOUNCED (P5.5 H2). `resize` fires continuously while a window is dragged — roughly 180 events
    // over a 3-second drag — and each one used to start a full IPC round-trip, every one of them
    // arming a 30-second timeout timer. The state that matters only changes at the END of a resize
    // (maximize/restore is a single step), so the trailing edge is not just cheaper, it is the correct
    // semantics. 100 ms matches the drop-zone bounds sync.
    const refresh = debounce(query, 100);
    query(); // the initial read is immediate — nothing to coalesce yet
    window.addEventListener('resize', refresh);
    return () => {
      stale = true;
      refresh.cancel(); // a pending timer must not fire against an unmounted component
      window.removeEventListener('resize', refresh);
    };
  }, [commands]);

  return maximized;
}
