/**
 * The page half of the host's segment route (D71 piece 4) — reading the manifest it serves, choosing the
 * MediaSource this browser actually has, and deciding what to fetch next.
 *
 * 🔴 **Everything in this module is PURE, and the split is deliberate.** The DECISIONS live here, where a
 * test pins them exactly, and `segmentBinder.ts` holds the imperative half — creating a `SourceBuffer`,
 * appending bytes, listening to element events. That is the same division `SegmentGrid` makes on the host
 * side, for the same reason.
 *
 * ⚠ **"The imperative half cannot be verified anywhere this repo runs" was true and is no longer.** jsdom
 * still has no MediaSource and both real implementations still live on devices — but the binder takes its
 * source and its fetch as PARAMETERS, so a fake drives every branch of it. What a fake cannot say is
 * whether a real implementation accepts the bytes; that is measured on hardware and recorded in
 * `docs/design/media.md`.
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
 * init segment arrives. The default is H.264 High 4.0 plus AAC-LC.
 *
 * 🔴 **And the default is a DEFAULT rather than a guarantee, because the host copies whatever the source
 * already holds** (D76): a segment's picture keeps the profile and level the original encoder chose, and an
 * HEVC source arrives as `hvc1` and not `avc1` at all. The family is what an implementation actually checks,
 * so an H.264 source of any profile plays through the default — **an HEVC one needs its own string**, e.g.
 * `segmentMimeType('hvc1.1.6.L93.B0,mp4a.40.2')`.
 */
export function segmentMimeType(codecs = 'avc1.640028,mp4a.40.2'): string {
  return `video/mp4; codecs="${codecs}"`;
}

/**
 * Read the codecs parameter out of an initialisation segment, so the `SourceBuffer` is opened for the
 * tracks it will actually be fed.
 *
 * 🔴 **The TRACK SET is the part that must be right, and getting it wrong is fatal rather than
 * degraded.** Measured against Chromium 151 on the kit's own segments: a video-only init segment
 * appended to a buffer opened with the two-track default (`avc1.640028,mp4a.40.2`) fails the FIRST
 * append and plays nothing at all — while the same bytes with `avc1.640015` play. A source with no
 * soundtrack is ordinary, so a fixed default cannot serve both.
 *
 * ⚠ **The profile and level, by contrast, are barely checked.** The same measurement fed High 2.1
 * content to a buffer opened as Baseline 3.0 (`avc1.42E01E`) and it played. That is why this returns a
 * precise string when the configuration is there to read and a family default when it is not: precision
 * where it is free, and never a guess about which tracks exist.
 *
 * @param init The bytes of the `#EXT-X-MAP` segment.
 * @returns A codecs string for {@link segmentMimeType}, or null when no track could be read — the caller
 *   should treat that as "do not open a SourceBuffer", not as "use the default".
 */
export function codecsFromInitSegment(init: Uint8Array): string | null {
  const view = new DataView(init.buffer, init.byteOffset, init.byteLength);
  const codecs: string[] = [];

  /** Bytes are read through the view rather than by index: `noUncheckedIndexedAccess` types `init[i]`
   * as possibly-undefined, and a `!` on every read would hide the one that IS out of range. */
  const u8 = (at: number) => view.getUint8(at);
  const fourcc = (at: number) => String.fromCharCode(u8(at), u8(at + 1), u8(at + 2), u8(at + 3));
  const hex2 = (n: number) => n.toString(16).padStart(2, '0');

  /** Walk the direct children of [start, end), calling `visit` with (type, contentStart, contentEnd). */
  const children = (start: number, end: number, visit: (t: string, s: number, e: number) => void) => {
    let at = start;
    while (at + 8 <= end) {
      let size = view.getUint32(at);
      let header = 8;
      if (size === 1) {
        if (at + 16 > end) return;
        size = Number(view.getBigUint64(at + 8));
        header = 16;
      }
      if (size === 0) size = end - at;
      if (size < header || at + size > end) return;
      visit(fourcc(at + 4), at + header, at + size);
      at += size;
    }
  };

  /** The `avcC`/`hvcC`/`esds` inside a sample entry, whose own fields come first. */
  const configuration = (format: string, start: number, end: number): string => {
    // Measured against the host's own init segment: an `avc1` entry holds 132 bytes of content and a
    // 54-byte `avcC`, so the child boxes start 78 in — the 8-byte SampleEntry base (6 reserved + a data
    // reference index) plus VisualSampleEntry's 70. An `mp4a` entry is that base plus 20.
    const visual = format === 'avc1' || format === 'avc3' || format === 'hvc1' || format === 'hev1';
    const at = start + (visual ? 78 : 28);
    let derived = '';

    children(at, end, (type, s) => {
      if ((type === 'avcC') && s + 4 <= end) {
        // configurationVersion, AVCProfileIndication, profile_compatibility, AVCLevelIndication
        derived = `${format}.${hex2(u8(s + 1))}${hex2(u8(s + 2))}${hex2(u8(s + 3))}`;
      } else if (type === 'hvcC' && s + 13 <= end) {
        // ISO 14496-15 §8.3.3: profile_space/tier/idc, then 4 compatibility bytes, 6 constraint bytes,
        // then the level. Rendered in the form Chromium and Safari both accept.
        const space = (u8(s + 1) >> 6) & 0x3;
        const tier = (u8(s + 1) >> 5) & 0x1;
        const idc = u8(s + 1) & 0x1f;
        const compat = view.getUint32(s + 2);
        const level = u8(s + 12);
        const spaces = ['', 'A', 'B', 'C'][space];
        // The compatibility flags travel REVERSED, as a bit string, per the spec's C.1.
        let reversed = 0;
        for (let bit = 0; bit < 32; bit++) reversed |= ((compat >>> bit) & 1) << (31 - bit);
        derived = `${format}.${spaces}${idc}.${(reversed >>> 0).toString(16).toUpperCase()}.` +
                  `${tier ? 'H' : 'L'}${level}`;
      } else if (type === 'esds' && s + 21 <= end) {
        // The object type indication, then the audio object type from the DecoderSpecificInfo's first
        // 5 bits. Only AAC is ever copied here, so the walk stays a search rather than a full parser.
        for (let p = s; p + 2 < end; p++) {
          if (u8(p) === 0x04 && u8(p + 2) === 0x40) {
            derived = `mp4a.40.${u8(p + 3) || 2}`;
            break;
          }
        }
        if (!derived) derived = 'mp4a.40.2';
      }
    });

    if (derived) return derived;
    // No configuration to read: name the FAMILY, which is what an implementation actually checks.
    return format === 'mp4a' ? 'mp4a.40.2' : format;
  };

  const stsd = (start: number, end: number) => {
    // FullBox header (4) + entry_count (4), then the entries.
    let at = start + 8;
    while (at + 8 <= end) {
      const size = view.getUint32(at);
      if (size < 8 || at + size > end) return;
      codecs.push(configuration(fourcc(at + 4), at + 8, at + size));
      at += size;
    }
  };

  const descend = (start: number, end: number) => {
    children(start, end, (type, s, e) => {
      if (type === 'stsd') stsd(s, e);
      else if (type === 'moov' || type === 'trak' || type === 'mdia' || type === 'minf' || type === 'stbl') {
        descend(s, e);
      }
    });
  };

  descend(0, init.byteLength);
  return codecs.length > 0 ? codecs.join(',') : null;
}
