import { useEffect, type RefObject } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';

/**
 * The module the player speaks on. Matches `MediaPlayerOptions.Module` on the host, which defaults to
 * the same string — change one and you must change the other.
 *
 * ⚠ The `SHENORA.` prefix is RESERVED for the kit's own modules (D64), beside the handshake's bare
 * `SHENORA`. It exists so your app stays free to own a module called plainly `MEDIA`.
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

/**
 * What the element is doing, in the host's vocabulary (`MediaPlayerState`).
 *
 * ⚠ `opening` and `buffering` are distinct, matching the host: opening is "no position yet", buffering is
 * "had one and it stopped moving". Collapsing them makes a UI extrapolate a position that is not advancing.
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
}

/** Inputs for {@link useMediaPlayer}. */
export interface UseMediaPlayerOptions {
  /** Override the module. Must match the host's `MediaPlayerOptions.Module`. */
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
 * calls `IMediaPlayer.OpenAsync/PlayAsync` in C#, and this drives the element to match. The page keeps what
 * it is good at (rendering) and gives up what it was never good at (deciding whether a file can be played
 * at all, which needs a probe and a device capability query).
 *
 * 🔴 **⚠ THE HOST HALF NEEDS ONE MORE PIECE, and it is not this one.** The reports posted here are an
 * ordinary IPC message (`PLAYER_REPORT` on {@link MEDIA_PLAYER_MODULE}) and **the kit registers no host
 * facade for it** — the app writes a four-line route that calls `MediaPlayer.Report(...)`. Until it does,
 * `IMediaPlayer.OpenAsync` waits on a report that never arrives: the element loads and plays, and the C#
 * call never returns. See `docs/ADOPTION.md`.
 *
 * ⚠ **The element must exist when this effect first runs.** `ref.current` is read once, and a `useRef`
 * object is stable, so an element rendered CONDITIONALLY (`{ready && <video ref={ref} />}`) mounts after
 * the effect and never binds — silently. Render the element unconditionally and hide it with CSS, or key
 * the component so the hook remounts with it.
 *
 * ⚠ **It reports on TRANSITIONS, never on `timeupdate`.** That event fires ~4×/second and forwarding it
 * would cost battery and IPC to tell the host something it can extrapolate from a position and a rate. If
 * you need a moving scrubber, read `element.currentTime` in your own render loop — locally, at the rate you
 * actually redraw.
 *
 * ⚠ **Autoplay policies still apply.** A `PLAYER_PLAY` arriving before any user gesture can be refused by
 * the browser, and the element reports `Failed`. That is the platform's rule, not the kit's, and the host
 * hears about it rather than silently believing playback started.
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

    // The element is the only clock: the host asks IT for position rather than tracking its own, so a
    // report always carries what the element actually believes.
    const report = (state: MediaPlayerReportState, error?: string) => {
      const duration = Number.isFinite(element.duration) ? element.duration : null;
      const payload: MediaPlayerReport = {
        state,
        position: Number.isFinite(element.currentTime) ? element.currentTime : 0,
        duration,
        ...(error ? { error } : {}),
      };
      link.post(module, MEDIA_PLAYER_REPORT, { payload });
    };

    // ── host → element ────────────────────────────────────────────────────────────────────────────
    const subscriptions = [
      eventBus.subscribe<{ uri: string; startAt: number }>(module, MediaPlayerCommands.load, (message) => {
        const { uri, startAt } = message.payload ?? { uri: '', startAt: 0 };
        element.src = uri;
        // load() rather than trusting the src assignment: a second load on the same element keeps the
        // previous buffer otherwise, and a seek then lands in the OLD media.
        element.load();
        if (startAt > 0) {
          const seek = () => { element.currentTime = startAt; };
          // `loadedmetadata` is the earliest point currentTime is settable — before it, the assignment is
          // silently dropped and the item starts at zero.
          element.addEventListener('loadedmetadata', seek, { once: true });
        }
      }),
      eventBus.subscribe(module, MediaPlayerCommands.play, () => {
        // A rejected play() is an autoplay refusal, and the host must hear it rather than assume success.
        // ⚠ Read `.name` STRUCTURALLY rather than testing `instanceof Error`: the value browsers reject
        // with is a DOMException, which is not an Error subclass everywhere (jsdom's is not), and a page
        // can reject with anything at all. The name is the stable, app-safe part — `NotAllowedError` is
        // what an autoplay block actually says, and it is the one an adopter will want to branch on.
        void element.play().catch((cause: unknown) => {
          const name = (cause as { name?: unknown } | null)?.name;
          report('Failed', typeof name === 'string' && name ? name : 'PlayRejected');
        });
      }),
      eventBus.subscribe(module, MediaPlayerCommands.pause, () => element.pause()),
      eventBus.subscribe<{ position: number }>(module, MediaPlayerCommands.seek, (message) => {
        element.currentTime = message.payload?.position ?? 0;
      }),
      eventBus.subscribe<{ rate: number }>(module, MediaPlayerCommands.rate, (message) => {
        element.playbackRate = message.payload?.rate ?? 1;
      }),
      eventBus.subscribe(module, MediaPlayerCommands.unload, () => {
        element.pause();
        element.removeAttribute('src');
        // ⚠ load() after clearing src is what actually FREES the buffer. Without it the element keeps the
        // decoded data alive, which on a phone is the difference between releasing memory and not.
        element.load();
        report('Empty');
      }),
    ];

    // ── element → host ────────────────────────────────────────────────────────────────────────────
    // Transitions only. `timeupdate` is deliberately absent — see the remarks.
    const listeners: Array<[keyof HTMLMediaElementEventMap, () => void]> = [
      ['loadedmetadata', () => report('Paused')],
      ['canplay', () => report(element.paused ? 'Paused' : 'Playing')],
      ['play', () => report('Playing')],
      ['playing', () => report('Playing')],
      ['pause', () => report(element.ended ? 'Ended' : 'Paused')],
      ['waiting', () => report('Buffering')],
      ['seeked', () => report(element.paused ? 'Paused' : 'Playing')],
      ['ended', () => report('Ended')],
      ['error', () => report('Failed', mediaErrorReason(element))],
    ];
    for (const [event, handler] of listeners) element.addEventListener(event, handler);

    return () => {
      for (const subscription of subscriptions) subscription();
      for (const [event, handler] of listeners) element.removeEventListener(event, handler);
    };
  }, [ref, module, bridge, eventBus]);
}

/**
 * A short, stable reason from `MediaError`.
 *
 * ⚠ Deliberately NOT `error.message`: browsers put decoder internals and sometimes the full URL in it, and
 * this string crosses to the host and can reach a log. The host applies the same rule to platform errors.
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
