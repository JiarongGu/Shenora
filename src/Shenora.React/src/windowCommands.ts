import { useEffect, useRef, useState } from 'react';
import type { ShenoraBridge } from './bridge.js';
import { debounce } from './internal.js';
import { BaseModuleService } from './moduleService.js';

/** The top resize edges — the only ones that exist: the frameless technique keeps the native
 * side/bottom resize borders, so only the top (covered by the WebView) needs page-side help. */
export type WindowResizeEdge = 'top' | 'topLeft' | 'topRight';

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
}

/**
 * The max/restore-glyph resync pattern from the source app: the authoritative maximize state,
 * re-queried on every window resize (a maximize/restore always resizes the window, and the DOM
 * has no other signal for the manual work-area maximize). Failures (plain browser, no host)
 * leave it false.
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
