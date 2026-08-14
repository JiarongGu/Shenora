/**
 * The page half of the host's segment route (D71 piece 4) — reading the manifest it serves, choosing the
 * MediaSource this browser actually has, and deciding what to fetch next.
 *
 * 🔴 **Everything in this module is PURE, and the split is deliberate.** The imperative half — creating a
 * `SourceBuffer`, appending bytes, listening to element events — cannot be verified anywhere this repo can
 * run: jsdom has no MediaSource, and the two real implementations live on devices. So the DECISIONS live
 * here, where a test pins them exactly, and the glue in {@link useSegmentStream} stays thin enough to read.
 * That is the same division `SegmentGrid` makes on the host side, for the same reason.
 */

/** One entry in the playlist the host serves. */
export interface SegmentEntry {
  /** Relative to the manifest — `seg12.m4s`. */
  uri: string;
  /** What the playlist declares this segment lasts, in seconds. */
  seconds: number;
}

/** What a playlist says, reduced to the three things a binder acts on. */
export interface SegmentManifest {
  /**
   * The initialisation segment (`#EXT-X-MAP`), which carries the tracks and their decoder configuration.
   *
   * ⚠ **Null means the playlist declared none, and that is not playable through MediaSource** — a fragment
   * repeats no configuration, so appending one without this produces a silent decode error. The kit's host
   * route always writes it; a foreign playlist may not.
   */
  initUri: string | null;
  /** The longest a segment may be, from `#EXT-X-TARGETDURATION`. */
  targetSeconds: number;
  segments: SegmentEntry[];
}

/**
 * Parse the subset of HLS the host emits. Deliberately NOT a general playlist parser: this reads what
 * `SegmentStream` writes, and anything it does not understand is ignored rather than guessed at.
 *
 * ⚠ Unknown tags are skipped silently BY DESIGN — a playlist gains tags over time and a parser that threw
 * on the first one it had not met would break on a host newer than the page.
 */
export function parseManifest(text: string): SegmentManifest {
  const segments: SegmentEntry[] = [];
  let initUri: string | null = null;
  let targetSeconds = 0;
  let pending: number | null = null;

  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (line.length === 0) continue;

    if (line.startsWith('#EXT-X-MAP:')) {
      // URI="init.mp4" — quoted per the spec, and the quotes are not optional there.
      initUri = /URI="([^"]*)"/.exec(line)?.[1] ?? null;
      continue;
    }
    if (line.startsWith('#EXT-X-TARGETDURATION:')) {
      targetSeconds = Number(line.slice('#EXT-X-TARGETDURATION:'.length)) || 0;
      continue;
    }
    if (line.startsWith('#EXTINF:')) {
      // `#EXTINF:6.000,` — the trailing comma introduces an optional title.
      pending = Number(line.slice('#EXTINF:'.length).split(',')[0]) || 0;
      continue;
    }
    if (line.startsWith('#')) continue;

    // A bare line is a URI, and it belongs to the EXTINF above it.
    segments.push({ uri: line, seconds: pending ?? 0 });
    pending = null;
  }

  return { initUri, targetSeconds, segments };
}

/** Which MediaSource implementation this browser has, if any. */
export type MediaSourceKind = 'managed' | 'standard' | 'none';

/** A window-shaped object, so the pick is testable without a browser. */
export interface MediaSourceGlobals {
  MediaSource?: unknown;
  ManagedMediaSource?: unknown;
}

/**
 * Which MediaSource to use — **`ManagedMediaSource` first where it exists**.
 *
 * 🔴 **The order is the decision, and it is not "newest wins".** iOS on iPhone has only
 * `ManagedMediaSource`; Android has only `MediaSource`. Where BOTH exist the managed one is still preferred,
 * because it is the one that tells the page when the platform actually wants data — a page that streams
 * regardless is what the managed variant was introduced to stop.
 *
 * ✅ **Measured rather than assumed** (iPhone 16 Pro simulator, iOS 26, 2026-08-14): `window.MediaSource` is
 * `false` there and `ManagedMediaSource` is `true`. So on iOS this is not a preference at all — **a binder
 * that only knows `window.MediaSource` does nothing**, and the naming here is what makes one bundle work on
 * both shells.
 *
 * ⚠ **`'managed'` carries an obligation, which is why this returns a KIND rather than just a constructor.**
 * A managed source only wants data between its `startstreaming` and `endstreaming` events, and fetching
 * outside that window is the thing it exists to prevent. A caller that ignores the kind has not merely
 * missed an optimisation.
 */
export function pickMediaSource(globals: MediaSourceGlobals): MediaSourceKind {
  if (typeof globals.ManagedMediaSource === 'function') return 'managed';
  if (typeof globals.MediaSource === 'function') return 'standard';
  return 'none';
}

/** What {@link nextSegment} needs to know about the element and the buffer. */
export interface FetchState {
  /** Where playback is, in seconds. */
  currentTime: number;
  /** How many seconds are already buffered AHEAD of `currentTime`. */
  bufferedAhead: number;
  /** Indices already appended, so a seek back into them costs nothing. */
  appended: ReadonlySet<number>;
  /** False while a managed source has told us to stop — see {@link pickMediaSource}. */
  streaming: boolean;
}

/** How far ahead to keep the buffer, and how much of the playlist there is. */
export interface FetchPolicy {
  segments: readonly SegmentEntry[];
  /** Stop fetching once this many seconds are buffered ahead. */
  targetAheadSeconds: number;
}

/**
 * The next segment index to fetch, or null for "nothing right now".
 *
 * 🔴 **Every branch here is a decision whose failure is SILENT in a browser**, which is why it is a pure
 * function with a test rather than an `if` inside an event handler:
 *
 * - **Not streaming → null.** A managed source that is fetched while it said stop is the exact misuse it
 *   exists to detect, and on iOS the penalty is the platform tearing the source down.
 * - **Enough buffered → null.** Fetching further ahead than the policy asks does not make playback smoother;
 *   it fills a quota, and a `QuotaExceededError` on append arrives as a stall with no obvious cause.
 * - **Otherwise the segment CONTAINING `currentTime`, or the first unappended one after it.** Starting from
 *   "the next index after the last one appended" instead is what breaks seeking: after a jump the last
 *   append is nowhere near where the user is now.
 */
export function nextSegment(state: FetchState, policy: FetchPolicy): number | null {
  if (!state.streaming) return null;
  if (state.bufferedAhead >= policy.targetAheadSeconds) return null;
  if (policy.segments.length === 0) return null;

  // Walk the playlist's own durations rather than assuming a fixed grid: the LAST segment is short, so
  // dividing by the target duration puts the tail index past the end.
  let at = 0;
  let index = 0;
  for (; index < policy.segments.length; index++) {
    const end = at + policy.segments[index]!.seconds;
    if (state.currentTime < end) break;
    at = end;
  }

  for (let i = Math.min(index, policy.segments.length - 1); i < policy.segments.length; i++) {
    if (!state.appended.has(i)) return i;
  }
  return null;
}

/**
 * The MIME type to open a `SourceBuffer` with.
 *
 * ⚠ **The codecs parameter is REQUIRED, not decorative.** `addSourceBuffer('video/mp4')` throws
 * `NotSupportedError` on every implementation — the buffer has to know what it is about to be fed before the
 * init segment arrives. These are the codecs the kit's own engine produces (H.264 baseline-to-high and
 * AAC-LC); an app whose engine emits something else must say so.
 */
export function segmentMimeType(codecs = 'avc1.640028,mp4a.40.2'): string {
  return `video/mp4; codecs="${codecs}"`;
}
