import { useEffect, type RefObject } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';

/**
 * The module the player speaks on. Matches `MediaPlayerOptions.Access.Module` on the host, which
 * defaults to the same string — change one and you must change the other.
 *
 * ⚠ The `SHENORA.` prefix is RESERVED for the kit's own modules (D64), so your app stays free to own
 * a module called plainly `MEDIA`.
 */
export const MEDIA_PLAYER_MODULE = 'SHENORA.MEDIA';

/**
 * Commands the host sends. **A wire contract**: these strings are duplicated in C# as
 * `MediaPlayerEvents`, and the two halves agree by string or not at all.
 */
export const MediaPlayerCommands = {
  load: 'PLAYER_LOAD',
  play: 'PLAYER_PLAY',
  pause: 'PLAYER_PAUSE',
  seek: 'PLAYER_SEEK',
  rate: 'PLAYER_RATE',
  unload: 'PLAYER_UNLOAD',
} as const;

/** The one message the page sends back. The host turns it into `MediaPlayer.Report(...)`. */
export const MEDIA_PLAYER_REPORT = 'PLAYER_REPORT';

/** What the page asks to read the host player's current status (`MediaPlayerModule.StatusType`). */
export const MEDIA_PLAYER_STATUS = 'PLAYER_STATUS';

/**
 * How long after the page goes hidden an unasked-for pause is taken for the platform's own, and after it is shown
 * again an unasked-for play. Measured on Android: the pause ~80 ms after hiding, the resume ~10 ms after showing;
 * the margin is wide because a person cannot press a media key or a play button in that time.
 */
const PLATFORM_PAUSE_WINDOW_MS = 1000;

/**
 * A CONVERSION's events, on the same module. **A wire contract** mirrored from C#
 * `MediaConversionEvents` — a conversion outlives the request that started it, so the page learns from
 * these rather than from a response.
 */
export const MediaConversionEvents = {
  /** Fraction complete: `{ source, progress }`. Throttle in the app if the engine is chatty. */
  sourceProgress: 'SOURCE_PROGRESS',
  /** The converted file is servable: `{ source }`. Set the element's `src` now. */
  ready: 'READY',
  /** Failed: `{ source, reason }`, plus `dropped` when `reason` is {@link MediaConversionErrorCodes}. */
  failed: 'FAILED',
} as const;

/**
 * Stable `reason` tokens on {@link MediaConversionEvents.failed}. Anything else is a TYPE name from an
 * unexpected fault — never exception text.
 */
export const MediaConversionErrorCodes = {
  /**
   * The output would have lost a stream, so nothing was cached; the event carries `dropped`, the codecs.
   * ⚠ It means "not playable HERE", not always "not supported" — a run configured with no conversion
   * seam never asked the platform.
   */
  unsupportedCodec: 'UNSUPPORTED_CODEC',
} as const;

/**
 * What the element is doing, in the host's vocabulary (`MediaPlayerState`).
 *
 * ⚠ `Opening` and `Buffering` are distinct: opening is "no position yet", buffering is "had one and it
 * stopped moving". Collapsing them makes a UI extrapolate a position that is not advancing.
 */
export type MediaPlayerReportState =
  | 'Empty' | 'Opening' | 'Paused' | 'Playing' | 'Buffering' | 'Ended' | 'Failed';

/** One state report, sent on TRANSITIONS only. */
export interface MediaPlayerReport {
  state: MediaPlayerReportState;
  /** Seconds. */
  position: number;
  /** Seconds, or null for a live stream / not yet known. */
  duration: number | null;
  /** A short reason when `state` is `Failed`; never the platform's raw text. */
  error?: string;
  /**
   * Set when the platform has STOPPED the element of a hidden page, a pause the hook does not report as one (the
   * user's `Playing` stands). The host then holds this position rather than carrying it forward until the page
   * reports again. Never set while a hidden element plays on — a desktop window — whose position must keep moving.
   */
  hidden?: boolean;
}

/** Inputs for {@link useMediaPlayer}. */
export interface UseMediaPlayerOptions {
  /** Override the module. Must match the host's `MediaPlayerOptions.Access.Module`. */
  module?: string;
  /** Test seams. */
  bridge?: ShenoraBridge;
  eventBus?: ShenoraEventBus;
}

/**
 * Bind a `<video>`/`<audio>` element to the HOST's player: the element becomes the display and the sound,
 * and .NET owns the lifecycle (D58).
 *
 * ```tsx
 * const ref = useRef<HTMLVideoElement>(null);
 * useMediaPlayer(ref);
 * return <video ref={ref} playsInline />;
 * ```
 *
 * **That is the whole page-side integration.** No `src`, no play button wiring, no state machine — the app
 * calls `IMediaPlayer.OpenAsync/PlayAsync` in C#, and this drives the element to match. The host half
 * needs nothing from you: the kit's own `MediaPlayerModule` answers the reports posted here.
 *
 * ⚠ **The element must exist when this effect first runs.** `ref.current` is read once, and a `useRef`
 * object is stable, so an element rendered CONDITIONALLY (`{ready && <video ref={ref} />}`) mounts after
 * the effect and never binds — silently. Render it unconditionally and hide it with CSS, or key the
 * component so the hook remounts with it.
 *
 * ⚠ **It reports on TRANSITIONS, never on `timeupdate`**, which fires ~4×/second. For a moving scrubber,
 * read `element.currentTime` in your own render loop.
 *
 * ⚠ **Autoplay policies still apply.** A `PLAYER_PLAY` arriving before any user gesture can be refused by
 * the browser, and the element then reports `Failed` rather than the host believing playback started.
 */
export function useMediaPlayer(
  ref: RefObject<HTMLMediaElement | null>,
  options: UseMediaPlayerOptions = {},
): void {
  const { module = MEDIA_PLAYER_MODULE, bridge, eventBus = defaultEventBus } = options;

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const link = bridge ?? getBridge();

    // The state last reported — what the host believes — which the platform's own pause of a hidden page must
    // not overwrite: the background handoff hands over only a page that was playing.
    let shown: MediaPlayerReportState | undefined;
    // When the page last went hidden (`hidden` once the handler has run), and whether the host's own pause is
    // on its way to the element.
    let hidden = false;
    let hiddenAt = 0;
    let pauseRequested = false;
    // Chromium RESUMES a video it paused for a hidden page as soon as the page is shown (measured on Android), and a
    // pause sent while the element was already paused cannot cancel that. So the platform's pause on the way out is
    // noted (`pausedThisHide`) and carried into the next show (`resumeComing`), and that resume is undone when the
    // host's last word since was pause (`holdPaused`, onPlay).
    let pausedThisHide = false;
    let resumeComing = false;
    let holdPaused = false;
    let shownAt = Number.NEGATIVE_INFINITY;
    const forgetHold = () => { pausedThisHide = false; resumeComing = false; holdPaused = false; };

    // The element is the clock: between reports the host only carries a playing position forward.
    const report = (state: MediaPlayerReportState, error?: string, asHidden = false) => {
      shown = state;
      const duration = Number.isFinite(element.duration) ? element.duration : null;
      const payload: MediaPlayerReport = {
        state,
        position: Number.isFinite(element.currentTime) ? element.currentTime : 0,
        duration,
        ...(error ? { error } : {}),
        ...(asHidden ? { hidden: true } : {}),
      };
      link.post(module, MEDIA_PLAYER_REPORT, { payload });
    };

    // The PENDING start-at seek, if any. 🔴 It has to be cancellable: `{ once: true }` removes a
    // listener only when it FIRES, and a second PLAYER_LOAD aborts the first load so its
    // `loadedmetadata` never comes. The stale listener then runs on the NEXT track's metadata — load A
    // at 10:00 then B at 0:00 and B starts ten minutes in.
    let pendingSeek: (() => void) | null = null;
    const cancelPendingSeek = () => {
      if (pendingSeek) element.removeEventListener('loadedmetadata', pendingSeek);
      pendingSeek = null;
    };

    // ── host → element ────────────────────────────────────────────────────────────────────────────
    const subscriptions = [
      eventBus.subscribe<{ uri: string; startAt: number }>(module, MediaPlayerCommands.load, (message) => {
        const { uri, startAt } = message.payload ?? { uri: '', startAt: 0 };
        shown = undefined;   // load() pauses with no `pause` event: the previous track's state is not this one's
        forgetHold();
        element.src = uri;
        // load() rather than trusting the src assignment: a second load on the same element keeps the
        // previous buffer otherwise, and a seek then lands in the OLD media.
        element.load();
        cancelPendingSeek();   // this load supersedes any earlier one — see pendingSeek
        if (startAt > 0) {
          const seek = () => { pendingSeek = null; element.currentTime = startAt; };
          // `loadedmetadata` is the earliest point currentTime is settable — before it, the assignment is
          // silently dropped and the item starts at zero.
          pendingSeek = seek;
          element.addEventListener('loadedmetadata', seek, { once: true });
        }
      }),
      eventBus.subscribe(module, MediaPlayerCommands.play, () => {
        // A rejected play() is an autoplay refusal, and the host must hear it rather than assume success.
        // ⚠ Read `.name` STRUCTURALLY, never via `instanceof Error`: browsers reject with a
        // DOMException, which is not an Error subclass everywhere (jsdom's is not). `NotAllowedError`
        // is what an autoplay block says, and the name an adopter branches on.
        holdPaused = false;
        void element.play().catch((cause: unknown) => {
          const name = (cause as { name?: unknown } | null)?.name;
          report('Failed', typeof name === 'string' && name ? name : 'PlayRejected');
        });
      }),
      eventBus.subscribe(module, MediaPlayerCommands.pause, () => {
        // Asked for — from the lock screen, a media key, the app — so reported even while the page is hidden.
        // Only when the element is playing: pausing a paused one fires nothing, and the flag would then pass a
        // later platform pause through.
        pauseRequested = !element.paused;
        holdPaused = true;
        element.pause();
      }),
      eventBus.subscribe<{ position: number }>(module, MediaPlayerCommands.seek, (message) => {
        element.currentTime = message.payload?.position ?? 0;
      }),
      eventBus.subscribe<{ rate: number }>(module, MediaPlayerCommands.rate, (message) => {
        element.playbackRate = message.payload?.rate ?? 1;
      }),
      eventBus.subscribe(module, MediaPlayerCommands.unload, () => {
        forgetHold();
        element.pause();
        element.removeAttribute('src');
        // ⚠ load() after clearing src is what actually FREES the buffer. Without it the element keeps the
        // decoded data alive, which on a phone is the difference between releasing memory and not.
        element.load();
        report('Empty');
      }),
    ];

    // ── element → host ────────────────────────────────────────────────────────────────────────────
    // Transitions only — no `timeupdate`, see the remarks.
    //
    // A pause nobody asked for, as the page goes hidden, is the PLATFORM's: measured on Android, the element's
    // `pause` follows the hidden event by ~80 ms. It is dropped, so the host keeps what the user had. A pause
    // the host asked for, or one later than PLATFORM_PAUSE_WINDOW_MS after hiding (a media key while the window
    // sits in the tray), is reported.
    const onPause = () => {
      const asked = pauseRequested;
      pauseRequested = false;
      if (element.ended) { report('Ended'); return; }
      if (!asked && document.visibilityState === 'hidden'
          && (!hidden || performance.now() - hiddenAt < PLATFORM_PAUSE_WINDOW_MS)) {
        pausedThisHide = true;
        // Said as what the host already holds, at where the element STOPPED, and marked `hidden` so the host holds
        // that position: it carries a playing one forward, and this pause is the one it will never hear.
        if (shown === 'Playing' || shown === 'Buffering') report(shown, undefined, true);
        return;
      }
      report('Paused');
    };
    // And the platform RESUMING what it paused, as the page is shown again, after the host said pause — a film the
    // user paused natively, or one that finished while the app was away: paused again, and reported as the pause it
    // is. Any other play — a media key while hidden, the user's own tap, the host's — is reported, and lifts the hold.
    const onPlay = () => {
      const platformResume = hidden
        ? document.visibilityState !== 'hidden' && pausedThisHide   // shown, before the handler has heard it
        : resumeComing && performance.now() - shownAt < PLATFORM_PAUSE_WINDOW_MS;
      if (holdPaused && platformResume) {
        if (!element.paused) {
          pauseRequested = true;
          element.pause();
        }
        return;
      }
      holdPaused = false;
      report('Playing');
    };
    const listeners: Array<[keyof HTMLMediaElementEventMap, () => void]> = [
      ['loadedmetadata', () => report('Paused')],
      ['canplay', () => report(element.paused ? 'Paused' : 'Playing')],
      ['play', onPlay],
      ['playing', onPlay],
      ['pause', onPause],
      ['waiting', () => report('Buffering')],
      ['seeked', () => report(element.paused ? 'Paused' : 'Playing')],
      ['ended', () => report('Ended')],
      ['error', () => report('Failed', mediaErrorReason(element))],
    ];
    for (const [event, handler] of listeners) element.addEventListener(event, handler);

    // 🔴 REPORT WHEN THE PAGE IS ABOUT TO BE HIDDEN, or the position `BackgroundPlaybackTransfer` hands over is
    // only the host's estimate — carried forward from the last TRANSITION, and running on past the platform's
    // pause of the hidden element. This gives it the element's own reading; the dropped pause (onPause) then says
    // where the element STOPPED, marked `hidden` so the host holds it — carried forward, a position ran ~0.7 s past
    // that pause when `Window.Stopped` came 782 ms later (measured on Android).
    //
    // ⚠ `visibilitychange` rather than `pagehide`: it fires while the document is still alive and the
    // bridge can still post, and both mobile shells raise it on the way to the background. ONE report
    // per background, not `timeupdate`'s ~4/second. It repeats the playback state the host already holds, with
    // the current position, so a platform pause that came first cannot turn a playing film into a paused one —
    // and says nothing for Empty, Opening or Failed, which a position does not change.
    // Back on screen, a host still believing `Playing` hears what the element really does: a pause dropped while
    // hidden, or a host command that changed nothing (pausing a paused element), would otherwise never correct it.
    const playback = (state: MediaPlayerReportState | undefined) =>
      state === 'Playing' || state === 'Buffering' || state === 'Paused' || state === 'Ended';
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        hidden = true;
        hiddenAt = performance.now();
        resumeComing = false;
        // Marked only when the platform paused the element first (a desktop window plays on while hidden, and
        // its position must keep being carried forward).
        if (playback(shown)) report(shown!, undefined, pausedThisHide);
      } else {
        hidden = false;
        shownAt = performance.now();
        resumeComing = pausedThisHide;
        pausedThisHide = false;
        if ((shown === 'Playing' || shown === 'Buffering') && element.paused) {
          report(element.ended ? 'Ended' : 'Paused');
        }
      }
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      for (const subscription of subscriptions) subscription();
      for (const [event, handler] of listeners) element.removeEventListener(event, handler);
      cancelPendingSeek();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [ref, module, bridge, eventBus]);
}

/**
 * A short, stable reason from `MediaError`.
 *
 * ⚠ Never `error.message`: browsers put decoder internals and sometimes the full URL in it, and this
 * string crosses to the host and can reach a log.
 */
function mediaErrorReason(element: HTMLMediaElement): string {
  switch (element.error?.code) {
    case 1: return 'Aborted';
    case 2: return 'Network';
    case 3: return 'Decode';
    case 4: return 'SourceNotSupported';
    default: return 'Unknown';
  }
}
