/**
 * Hold the window at an orientation. Mirrors `Shenora.Modules.Platform.WindowOrientationModule`, pinned
 * by `WireMirrorTests`.
 *
 * 🔴 **Try `screen.orientation.lock()` first — and know why it usually will not do.** The web API is
 * honoured only while the document is FULLSCREEN, so a page can hold an orientation only by taking over
 * the display; and WKWebView does not implement it at all. This route has neither limitation, because the
 * host asks the platform directly. The common shape — portrait everywhere EXCEPT a media viewer — is
 * exactly the one the web API cannot express.
 *
 * ⚠ **Branch on the `windowOrientation` capability**, which only a shell that can really hold an
 * orientation advertises. Calling where it is absent is refused with `CAPABILITY_NOT_SUPPORTED` rather
 * than silently ignored — but a page that checks first never shows a control that cannot work.
 *
 * ⚠ **There is no "what is it now".** The page already knows: `screen.orientation.type`, or a CSS media
 * query that re-renders for it. An IPC answer would be the same fact, later.
 */
import type { ShenoraBridge } from './bridge.js';
import { BaseModuleService } from './moduleService.js';

/** The orientations a window can be held at — the host's `WindowOrientation` enum, serialized. */
export type WindowOrientationKind = 'portrait' | 'landscape';

// ⚠ A plain interface — NOT `extends Record<string, unknown>`, which widens `keyof TRequests & string`
// back to `string`, so a mistyped route compiles and every payload collapses to `unknown`.
interface WindowOrientationRequests {
  LOCK: { orientation: WindowOrientationKind };
  UNLOCK: void;
}

/**
 * Typed client for the host's `SHENORA.ORIENTATION` module. Two calls and no state: the app decides
 * WHEN, the kit never rotates anything by itself.
 *
 * ```tsx
 * const orientation = new WindowOrientation();
 * useEffect(() => {
 *   if (!capabilities.has('windowOrientation')) return;
 *   orientation.lock('landscape');            // entering the viewer
 *   return () => { void orientation.unlock(); };  // leaving it
 * }, []);
 * ```
 */
export class WindowOrientation extends BaseModuleService<WindowOrientationRequests> {
  constructor(bridge?: ShenoraBridge) {
    super('SHENORA.ORIENTATION', bridge);
  }

  /** Hold the window at `orientation` until {@link unlock}. Idempotent; a second lock replaces the first. */
  lock(orientation: WindowOrientationKind): Promise<void> {
    return this.send('LOCK', { payload: { orientation } });
  }

  /** Let the platform choose again — whatever the device's own rotation setting says. Idempotent. */
  unlock(): Promise<void> {
    return this.send('UNLOCK');
  }
}
