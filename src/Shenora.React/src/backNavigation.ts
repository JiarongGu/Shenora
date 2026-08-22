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
 * ever arrives, because there is nothing to intercept. That is indistinguishable from a broken handler,
 * so branch on {@link useBackNavigation}'s `supported` rather than calling blind.
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

  useEffect(() => {
    if (!supported) return;
    let live = true;

    // ⚠ `.catch`, not `void`. These are fire-and-forget housekeeping, and there is one moment when
    // they reliably reject: a host that has gone away — which is exactly when a page unmounts. A
    // floating rejection there surfaces as an unhandled promise rejection in the adopter's console,
    // blaming the kit for a teardown that went fine.
    back.intercept(true).catch(() => {});
    const unsubscribe = eventBus.subscribe<BackNavigationEvent>(BACK_MODULE, BACK_PRESSED, async (message) => {
      const token = message.payload?.token;
      if (!live || typeof token !== 'string') return;
      let handled = false;
      try {
        handled = await handler.current();
      } catch {
        // Answering false is the safe direction: the user can still leave. Swallowing the press here
        // would be a back button that does nothing, which is not recoverable from the outside.
        handled = false;
      }
      await back.resolve(token, handled).catch(() => {});
    });

    return () => {
      live = false;
      unsubscribe();
      // Releasing is what stops a page that has unmounted its handler from holding every press for the
      // host's whole timeout before the platform gets it.
      back.intercept(false).catch(() => {});
    };
  }, [back, eventBus, supported]);

  return { back, supported };
}

let shared: BackNavigationAccess | undefined;
function defaultClient(): BackNavigationAccess {
  shared ??= new BackNavigationAccess();
  return shared;
}
