/**
 * When the APP went away and came back — and how long it was gone, which is the part this page cannot
 * measure itself. Mirrors `Shenora.Modules.Platform.AppLifecycle`, pinned by `WireMirrorTests`.
 *
 * 🔴 **For "am I on screen", use `document.visibilitychange`.** It fires on both mobile shells and is
 * the web platform's own answer; this would only arrive later and over IPC. What it adds is the two
 * things a hidden document genuinely cannot know:
 *
 * 1. **How long it was away, on a clock that was not throttled.** A backgrounded page's timers are
 *    throttled and its process may be frozen, so a `Date.now()` delta across the gap is unreliable —
 *    and the duration is what the decision actually turns on. Three seconds in the notification shade
 *    needs no reconnect; forty minutes means the socket is dead.
 * 2. **That this was the user leaving the APP**, rather than anything else that can hide a document.
 *
 * ⚠ It reports; it does not act. Reconnecting, re-probing or refetching is yours — the host has no way
 * to know what this page was holding.
 */
import { useEffect, useRef } from 'react';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';

/** The module these events are published under. */
export const LIFECYCLE_MODULE = 'SHENORA.LIFECYCLE';

/** The app left the foreground. No payload. */
export const LIFECYCLE_STOPPED = 'STOPPED';

/** The app came back, carrying an {@link AppLifecycleReport}. */
export const LIFECYCLE_RESUMED = 'RESUMED';

/** What a {@link LIFECYCLE_RESUMED} event carries. */
export interface AppLifecycleReport {
  /**
   * How long the app was away, or `null` when there was no preceding stop to measure from — a first
   * launch, or a shell that reported only the resume.
   *
   * ⚠ **`null` is not `0`.** Zero would be a measurement; this is the absence of one. Treating them
   * alike makes the first resume after launch skip the reconnect every other resume performs.
   */
  backgroundMilliseconds: number | null;
}

/** What {@link useAppLifecycle} watches for. Every handler is optional. */
export interface AppLifecycleHandlers {
  /** The app left the foreground. */
  onStopped?: () => void;
  /**
   * The app came back. `backgroundMilliseconds` is null when there was nothing to measure.
   *
   * ⚠ Branch on a THRESHOLD rather than on the event: reconnecting on every resume makes switching
   * apps expensive, and doing nothing makes a long absence look like a hung page.
   */
  onResumed?: (report: AppLifecycleReport) => void;
}

/**
 * Run something when the app leaves the foreground and when it comes back.
 *
 * ```tsx
 * useAppLifecycle({
 *   onResumed: ({ backgroundMilliseconds }) => {
 *     // A short trip to the notification shade costs nothing; a long one means the socket is gone.
 *     if (backgroundMilliseconds === null || backgroundMilliseconds > 30_000) reconnect();
 *   },
 * });
 * ```
 *
 * ⚠ On a shell that reports no lifecycle — the desktop, or a mobile app that did not compose
 * `MobileAppLifecycle` — neither handler ever runs. There is no capability to branch on, because there
 * is nothing to render differently: an app that also wants a visibility signal should use
 * `document.visibilitychange`, which works everywhere including a plain browser tab.
 */
export function useAppLifecycle(
  handlers: AppLifecycleHandlers,
  options: { eventBus?: ShenoraEventBus } = {},
): void {
  const eventBus = options.eventBus ?? defaultEventBus;
  // Read through a ref so a handler rebuilt every render — the normal case for inline arrows — does not
  // tear down and rebuild the subscription, losing any transition that lands in the gap.
  const latest = useRef(handlers);
  latest.current = handlers;

  useEffect(() => {
    const subscriptions = [
      eventBus.subscribe(LIFECYCLE_MODULE, LIFECYCLE_STOPPED, () => {
        latest.current.onStopped?.();
      }),
      eventBus.subscribe<AppLifecycleReport>(LIFECYCLE_MODULE, LIFECYCLE_RESUMED, (message) => {
        // ⚠ Absent rather than 0 when the host sent nothing usable — the same distinction the payload
        // documents, preserved here so a missing field cannot read as "away for no time".
        const reported = message.payload?.backgroundMilliseconds;
        latest.current.onResumed?.({
          backgroundMilliseconds: typeof reported === 'number' ? reported : null,
        });
      }),
    ];
    return () => subscriptions.forEach((off) => off());
  }, [eventBus]);
}
