import { useCallback, useEffect, useRef, useState } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import {
  MEDIA_PLAYER_MODULE,
  MEDIA_PLAYER_STATUS,
  MediaPlayerCommands,
  type MediaPlayerReportState,
} from './mediaPlayer.js';

/**
 * What the HOST's player is doing. The same vocabulary `MediaPlayerStatus` uses on the other side, with
 * seconds where C# has `TimeSpan`.
 */
export interface MediaTransportStatus {
  state: MediaPlayerReportState;
  /** Seconds. */
  position: number;
  /** Seconds, or null for a live stream and — briefly — while opening. */
  duration: number | null;
  /** The rate ASKED FOR; a platform may clamp it. */
  rate: number;
  /** A short reason when `state` is `Failed`; never the platform's raw text. */
  error?: string | null;
}

/** Inputs for {@link useMediaTransport}. */
export interface UseMediaTransportOptions {
  /** How often to sample the position, in ms. Default 250 — the rate a scrubber redraws at. */
  intervalMs?: number;
  /**
   * Poll at all. Default `true`. Set it `false` while no player surface is on screen: a paused player's
   * position does not move, and the host answers every ask.
   */
  enabled?: boolean;
  /** Override the module. Must match the host's `MediaPlayerOptions.Access.Module`. */
  module?: string;
  /** Test seam. */
  bridge?: ShenoraBridge;
}

/**
 * Consecutive failed asks before {@link MediaTransport.unanswered} goes true — about two seconds at the
 * default interval.
 *
 * ⚠ Counted in TICKS rather than elapsed ms so the threshold means the same thing however fast a caller
 * samples: ONE dropped reply is ordinary, eight in a row is a transport that has gone.
 */
const UNANSWERED_AFTER_TICKS = 8;

/** What {@link useMediaTransport} gives you. */
export interface MediaTransport {
  /** The most recent trustworthy reading, or `null` before the first one lands. */
  status: MediaTransportStatus | null;
  /**
   * The host has stopped answering — 2 s of consecutive failures.
   *
   * 🔴 **This exists because a dead poll has NO symptom of its own.** The callback simply stops running:
   * the scrubber keeps its last value, the play button keeps whatever the last press set, and nothing
   * anywhere says the transport is gone. Show it, or at least log it — the alternative is diagnosing it
   * from an ABSENCE, which is the hardest evidence there is to notice.
   */
  unanswered: boolean;

  /** Point the host's player at a source and prepare it. Does not start playback. */
  load(uri: string): Promise<void>;
  play(): Promise<void>;
  pause(): Promise<void>;
  /** Move to an absolute position, in seconds. */
  seek(position: number): Promise<void>;
  /** Set the speed multiplier; 1 is normal. */
  setRate(rate: number): Promise<void>;
  /** Release the source and free the decoder. */
  unload(): Promise<void>;
}

/**
 * Drive the HOST's player and read what it is doing — the companion to {@link useMediaSurface}, for when
 * the shell owns the picture and the page owns the controls.
 *
 * ```tsx
 * const { status, unanswered, play, pause, seek } = useMediaTransport();
 * ```
 *
 * **Use it when the shell is the player.** With the picture on the shell's surface the page's own element
 * is not playing, so its `timeupdate` says nothing and the host is the only clock. Running both is two
 * clocks that disagree.
 *
 * 🔴 **THE COMMANDS ARE HERE, AND NOT BY CONVENIENCE.** A status answer that was asked for BEFORE a
 * command is stale in every field, not just in `state` — so this hook drops it, which it can only do if
 * it knows when a command happened. Calling the routes directly instead re-opens exactly that hole:
 * the poll returns the pre-command reading, the UI flips back to it, and the next poll flips it again.
 *
 * ⚠ **A command's own answer is applied at once.** The host's drive routes return the resulting status,
 * so a press updates the UI without waiting up to `intervalMs` for the next sample.
 *
 * ⚠ **Failures are swallowed, deliberately.** A poll that cannot answer is not worth breaking playback
 * over, and the commands reject rather than throw at the render. Watch {@link MediaTransport.unanswered}
 * for the case that matters.
 */
export function useMediaTransport(options: UseMediaTransportOptions = {}): MediaTransport {
  const { intervalMs = 250, enabled = true, module = MEDIA_PLAYER_MODULE, bridge } = options;

  const [status, setStatus] = useState<MediaTransportStatus | null>(null);
  const [unanswered, setUnanswered] = useState(false);

  /**
   * How many commands have been issued. A reading asked for while this was lower describes a player that
   * has since been told to do something else.
   *
   * ⚠ A ref, not state: bumping it must not re-render, and every reader needs the value as of NOW rather
   * than as of the render it closed over.
   */
  const commands = useRef(0);
  const live = useRef(true);

  useEffect(() => {
    live.current = true;
    return () => { live.current = false; };
  }, []);

  const send = useCallback(async (type: string, payload?: object): Promise<void> => {
    commands.current += 1;
    try {
      const answer = await (bridge ?? getBridge())
        .invoke<Partial<MediaTransportStatus> | null>(module, type, payload ? { payload } : {});
      // The host answers a drive command with the status it produced — apply it rather than waiting for
      // the next sample. ⚠ Only if this component is still mounted; a resolve after unmount is ordinary.
      if (live.current && answer) setStatus(normalise(answer));
    } catch {
      /* A refused command is the host's answer, not a reason to throw at the page. */
    }
  }, [module, bridge]);

  useEffect(() => {
    if (!enabled) return;

    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let misses = 0;

    const tick = async (): Promise<void> => {
      if (stopped) return;
      // Read BEFORE the ask goes out: everything after this point is a command the answer cannot have seen.
      const asked = commands.current;
      let answer: Partial<MediaTransportStatus> | null = null;
      try {
        answer = await (bridge ?? getBridge())
          .invoke<Partial<MediaTransportStatus> | null>(module, MEDIA_PLAYER_STATUS, {});
      } catch {
        answer = null;
      }
      if (stopped) return;

      if (answer) {
        misses = 0;
        setUnanswered(false);
        // 🔴 DROPPED, not reported-with-a-caveat: a pre-command reading is stale in position as well as
        // state, so there is nothing in it a caller could safely keep.
        if (commands.current === asked) setStatus(normalise(answer));
      } else if (++misses >= UNANSWERED_AFTER_TICKS) {
        setUnanswered(true);
      }

      // ⚠ Scheduled from the ANSWER, never on a bare interval: a slow host stretches the gap instead of
      // queueing asks behind each other.
      timer = setTimeout(() => { void tick(); }, intervalMs);
    };

    void tick();
    return () => { stopped = true; if (timer) clearTimeout(timer); };
  }, [enabled, intervalMs, module, bridge]);

  const load = useCallback((uri: string) => send(MediaPlayerCommands.load, { uri }), [send]);
  const play = useCallback(() => send(MediaPlayerCommands.play), [send]);
  const pause = useCallback(() => send(MediaPlayerCommands.pause), [send]);
  const seek = useCallback((position: number) => send(MediaPlayerCommands.seek, { position }), [send]);
  const setRate = useCallback((rate: number) => send(MediaPlayerCommands.rate, { rate }), [send]);
  const unload = useCallback(() => send(MediaPlayerCommands.unload), [send]);

  return { status, unanswered, load, play, pause, seek, setRate, unload };
}

/**
 * A host answer, with every field given a safe shape.
 *
 * ⚠ `duration` is null for a live stream and while opening, and a UI that reads a missing one as 0 puts
 * the playhead at the end of something that has just started.
 */
function normalise(answer: Partial<MediaTransportStatus>): MediaTransportStatus {
  return {
    state: (answer.state ?? 'Empty') as MediaPlayerReportState,
    position: Number.isFinite(answer.position) ? Number(answer.position) : 0,
    duration: Number.isFinite(answer.duration) ? Number(answer.duration) : null,
    rate: Number.isFinite(answer.rate) ? Number(answer.rate) : 1,
    error: answer.error ?? null,
  };
}
