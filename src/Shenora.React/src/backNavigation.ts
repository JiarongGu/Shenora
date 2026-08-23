/**
 * The page's half of the host's `SHENORA.BACK` module — Android's system back gesture, offered to this
 * page before the platform acts on it. Mirrors `Shenora.Modules.Platform.BackNavigationModule`, pinned
 * by `WireMirrorTests`.
 *
 * 🔴 **This is the one shell primitive whose absence is a BROKEN APP rather than a missing feature.**
 * Unhandled, Android's back finishes the activity from whatever screen the user is on — so a user two
 * levels into your UI is dumped to the home screen instead of going back one step. There is no web API
 * for it: `popstate` fires only for history the page itself pushed, and it cannot tell you that the
 * press would otherwise EXIT.
 *
 * ⚠ **Every press must be answered**, including the ones you do not want. An unanswered press is held
 * for the host's timeout and then goes to the platform — so a page that stops answering does not freeze
 * back, it silently reverts to quitting the app.
 */
import { useEffect, useRef } from 'react';
import type { ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import { useShellInfo } from './hooks.js';
import { BaseModuleService } from './moduleService.js';
import { ShellCapabilities } from './types.js';

/** The module a back press is published under, and the one this page answers on. */
export const BACK_MODULE = 'SHENORA.BACK';

/** The event type carrying one press. */
export const BACK_PRESSED = 'PRESSED';

/** The payload of a {@link BACK_PRESSED} event. */
export interface BackNavigationEvent {
  /**
   * Identifies this press. Return it verbatim — an answer naming a press that already timed out is
   * refused rather than applied to the one after it.
   */
  token: string;
}

/** What `RESOLVE` answers with. */
export interface BackNavigationResult {
  /**
   * False when the press was no longer waiting — it timed out, or was already answered.
   *
   * ⚠ Not an error; the platform has already taken the press. But seeing it repeatedly means this
   * page's back handling is not running at all, and the user is getting the platform default.
   */
  accepted: boolean;
}

interface BackRequests {
  INTERCEPT: { enabled: boolean };
  RESOLVE: { token: string; handled: boolean };
}

/**
 * Typed client for the host's `SHENORA.BACK` module (`BackNavigationModule`).
 *
 * ⚠ On a shell with no system back gesture — iOS, desktop — {@link intercept} is accepted and no press
 * ever arrives, because there is nothing to intercept. Harmless, but indistinguishable from a broken
 * handler, so use {@link useBackNavigation}'s `supported` to decide what to RENDER.
 */
export class BackNavigationAccess extends BaseModuleService<BackRequests> {
  constructor(bridge?: ShenoraBridge) {
    super(BACK_MODULE, bridge);
  }

  /**
   * Take or release responsibility for the back gesture.
   *
   * ⚠ Ask again after a navigation that replaces the document: the host cannot know the new page wanted
   * it, and asking twice is harmless.
   */
  intercept(enabled: boolean): Promise<void> {
    return this.send('INTERCEPT', { payload: { enabled } });
  }

  /**
   * Answer one press. `handled: true` consumes it; `false` sends it to the platform, which is how a page
   * at the root of its own history lets the user leave.
   */
  resolve(token: string, handled: boolean): Promise<BackNavigationResult> {
    return this.send('RESOLVE', { payload: { token, handled } });
  }
}

/** What {@link useBackNavigation} reports back. */
export interface BackNavigationHandle {
  /** The typed client. Stable across renders. */
  back: BackNavigationAccess;
  /**
   * This shell has a system back gesture at all. False on iOS and the desktop — read from the ready
   * handshake rather than sniffed from the user agent (D36).
   */
  supported: boolean;
}

/**
 * Handle the system back gesture for as long as the component is mounted.
 *
 * Your handler returns `true` if it consumed the press and `false` to let the user leave. The
 * subscription, the answer and the release on unmount are all done for you — which matters, because
 * every one of those is a way to silently end up back at "the back button quits the app".
 *
 * ```tsx
 * const { supported } = useBackNavigation(() => {
 *   if (playerExpanded) { collapsePlayer(); return true; }   // consumed
 *   if (history.length > 1) { history.back(); return true; } // consumed
 *   return false;                                            // at the root — let them exit
 * });
 * ```
 *
 * ⚠ **The handler may be async**, for a page that has to ask something before deciding — but the host
 * gives it a bounded window (2 seconds by default) and then hands the press to the platform. Do not put
 * a confirmation dialog behind it.
 *
 * ⚠ A THROWING handler answers `false`, so a bug in your own code leaves the user able to leave rather
 * than trapping them in an app whose back button does nothing.
 *
 * ⚠ **Several components may use this at once**, which is the layered case above: the most recently
 * mounted handler is asked FIRST — a modal before the player behind it — and the first to return `true`
 * claims the press. The host is told once, however many components are listening.
 *
 * ⚠ **Ask again after a navigation that replaces the document.** The host has no document-lifecycle
 * signal to reset on, so a new page that does not re-register leaves the old interception standing and
 * every press waits the full timeout before reaching the platform.
 */
export function useBackNavigation(
  onBack: () => boolean | Promise<boolean>,
  options: { client?: BackNavigationAccess; eventBus?: ShenoraEventBus } = {},
): BackNavigationHandle {
  const { client, eventBus = defaultEventBus } = options;
  const shell = useShellInfo();
  const supported = shell?.capabilities?.includes(ShellCapabilities.backNavigation) ?? false;
  const back = client ?? defaultClient();

  // 🔴 The handler is read through a ref rather than depended on. An inline arrow — which is how this
  // hook will always be called — is a new function every render, so depending on it would unsubscribe
  // and resubscribe on every render, and a press landing in that gap is answered by NOBODY. It then
  // sits until the host's timeout and goes to the platform, i.e. the app quits mid-interaction.
  const handler = useRef(onBack);
  handler.current = onBack;

  // 🔴 INTERCEPT UNLESS THE SHELL IS KNOWN NOT TO HAVE IT — never "only when known to have it".
  // `useShellInfo` is a synchronous cache read that does NOT re-render when the handshake lands later,
  // and child effects run before parent effects — so a page that calls `notifyReady()` from a root
  // effect mounts every child while `shell` is still undefined. Gating on `supported` there would
  // silently never intercept, for the whole session, and back would quit the app from every screen:
  // the exact defect this hook exists to prevent, arriving from the most ordinary bootstrap. Asking to
  // intercept on a shell that turns out to have no back gesture costs nothing — no press ever arrives.
  const declined = shell !== undefined && !supported;

  useEffect(() => {
    if (declined) return;
    return register(back, eventBus, handler);
  }, [back, eventBus, declined, handler]);

  return { back, supported };
}

/**
 * The live handlers, newest LAST — module scope on purpose.
 *
 * 🔴 **Two components may use the hook at once, and that is the shape D79 describes** ("close the
 * expanded player, then walk the history"). Per-component `intercept(true)/(false)` calls would make
 * the *first* unmount switch interception off for everyone still mounted, and back would quit the app
 * with every remaining handler still subscribed and looking healthy. So the host is told once, on
 * 0→1, and told again only on 1→0.
 */
const handlers: Array<{ current: () => boolean | Promise<boolean> }> = [];
let release: (() => void) | undefined;

function register(
  back: BackNavigationAccess,
  eventBus: ShenoraEventBus,
  handler: { current: () => boolean | Promise<boolean> },
): () => void {
  handlers.push(handler);
  if (handlers.length === 1) release = subscribe(back, eventBus);

  return () => {
    const at = handlers.indexOf(handler);
    if (at >= 0) handlers.splice(at, 1);
    if (handlers.length === 0) {
      release?.();
      release = undefined;
    }
  };
}

function subscribe(back: BackNavigationAccess, eventBus: ShenoraEventBus): () => void {
  // ⚠ A rejection HERE means interception was never established — the adopter did half the host-side
  // pair, or a payload key drifted. Silence would leave `supported` true, the handlers subscribed and
  // back quitting the app, which is the failure the kit's own registration doc lectures about. The
  // teardown call below is the opposite case and is correctly silent.
  back.intercept(true).catch((error: unknown) => {
    console.warn(
      '[shenora] the host refused to hand over the back gesture, so back will quit the app. Check that '
        + 'the host called AddShenoraBackNavigation() AND constructed MobileBackNavigation.',
      error,
    );
  });

  const unsubscribe = eventBus.subscribe<BackNavigationEvent>(BACK_MODULE, BACK_PRESSED, async (message) => {
    const token = message.payload?.token;
    if (typeof token !== 'string') return;

    // Innermost first: the most recently mounted component is the one on top of the user's screen, so
    // a modal gets the press before the player behind it. The first to claim it wins.
    // Snapshot: a handler may unmount another while deciding, and splicing the live array mid-walk
    // would skip its neighbour.
    let handled = false;
    for (const entry of [...handlers].reverse()) {
      if (handled) break;
      try {
        handled = await entry.current();
      } catch {
        // Answering false is the safe direction: the user can still leave. Swallowing the press here
        // would be a back button that does nothing, which is not recoverable from the outside.
        handled = false;
      }
    }

    try {
      const answer = await back.resolve(token, handled);
      if (answer && answer.accepted === false) {
        // The host had already given the press to the platform. Not an error — but a page seeing this
        // is a page whose back handling is not running, and this is the only place that is visible
        // without a device attached.
        console.warn('[shenora] a back press was answered too late — the platform already took it.');
      }
    } catch {
      // The host went away mid-answer; the press is the platform's now, which is the safe direction.
    }
  });

  return () => {
    unsubscribe();
    // Releasing is what stops a page whose handlers have all unmounted from holding every press for
    // the host's whole timeout before the platform gets it. Silent: the one moment this reliably
    // rejects is a host that has already gone, which is exactly when a page unmounts.
    back.intercept(false).catch(() => {});
  };
}

let shared: BackNavigationAccess | undefined;
function defaultClient(): BackNavigationAccess {
  shared ??= new BackNavigationAccess();
  return shared;
}
