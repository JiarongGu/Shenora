/**
 * The page half of the host's segment route (D71 piece 4) — reading the manifest it serves, choosing the
 * MediaSource this browser actually has, and deciding what to fetch next.
 *
 * Everything here is PURE: the decisions live in this module and `segmentBinder.ts` holds the
 * imperative half — creating a `SourceBuffer`, appending bytes, listening to element events.
 */

/**
 * The reserved path segment naming a source by the handle the HOST issued for it:
 * `{routePath}${SEGMENT_REMOTE_PREFIX}{handle}/index.m3u8`.
 *
 * 🔴 **A page cannot name a remote url, only a handle.** The host's `MediaSourceRegistry` issues one when
 * the app authorises a source, and the route accepts nothing else. Mirrors
 * `SegmentStreamOptions.RemotePrefix`.
 */
export const SEGMENT_REMOTE_PREFIX = '~remote/';

/**
 * The manifest url for a handle the app handed this page.
 *
 * ⚠ The handle is opaque and is NOT a url — do not build one from it, and do not log it beside anything
 * that identifies the user. It is a capability: whoever holds it can stream that source.
 */
export function remoteSegmentUrl(routePath: string, handle: string, resource = 'index.m3u8'): string {
  const base = routePath.endsWith('/') ? routePath : `${routePath}/`;
  return `${base}${SEGMENT_REMOTE_PREFIX}${encodeURIComponent(handle)}/${resource}`;
}

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
   * repeats no configuration, so appending one without this is a silent decode error. The kit's host route
   * always writes it; a foreign playlist may not.
   */
  initUri: string | null;
  /** The longest a segment may be, from `#EXT-X-TARGETDURATION`. */
  targetSeconds: number;
  segments: SegmentEntry[];
}

/**
 * Parse the subset of HLS the host's `SegmentStream` emits — not a general playlist parser.
 *
 * ⚠ An unknown tag is SKIPPED, never an error, so a host newer than the page still parses.
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
 * 🔴 **iOS has ONLY `ManagedMediaSource` and Android has ONLY `MediaSource`**, so a binder that knows
 * just `window.MediaSource` does nothing at all on iOS. Where both exist the managed one still wins:
 * it is the one that says when the platform actually wants data. (Measured — `docs/design/media.md`.)
 *
 * ⚠ **`'managed'` carries an obligation, which is why this returns a KIND and not a constructor.** A
 * managed source only wants data between its `startstreaming` and `endstreaming` events, and fetching
 * outside that window is the misuse it exists to detect.
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
 * 🔴 **Every branch is a decision whose failure is SILENT in a browser:**
 *
 * - **Not streaming → null.** On iOS the penalty for fetching past `endstreaming` is the platform
 *   tearing the source down.
 * - **Enough buffered → null.** Fetching further ahead fills a quota, and a `QuotaExceededError` on
 *   append arrives as a stall with no obvious cause.
 * - **Otherwise the segment CONTAINING `currentTime`, or the first unappended one after it** — never
 *   "the next index after the last append", which breaks seeking.
 */
export function nextSegment(state: FetchState, policy: FetchPolicy): number | null {
  if (!state.streaming) return null;
  if (state.bufferedAhead >= policy.targetAheadSeconds) return null;
  if (policy.segments.length === 0) return null;

  // Walk the playlist's own durations rather than assuming a fixed grid: the LAST segment is short,
  // so dividing by the target duration puts the tail index past the end.
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
 * The MIME type to open a `SourceBuffer` with. The default is H.264 High 4.0 plus AAC-LC.
 *
 * ⚠ **The codecs parameter is REQUIRED, not decorative.** `addSourceBuffer('video/mp4')` throws
 * `NotSupportedError` on every implementation — the buffer has to know what it is about to be fed
 * before the init segment arrives.
 *
 * ⚠ The default is not a guarantee: the host copies whatever the source already holds (D76), so an
 * **HEVC source arrives as `hvc1` and needs its own string**, e.g.
 * `segmentMimeType('hvc1.1.6.L93.B0,mp4a.40.2')`.
 */
export function segmentMimeType(codecs = 'avc1.640028,mp4a.40.2'): string {
  return `video/mp4; codecs="${codecs}"`;
}

/**
 * Read the codecs parameter out of an initialisation segment, so the `SourceBuffer` is opened for the
 * tracks it will actually be fed.
 *
 * 🔴 **The TRACK SET must be right, and getting it wrong is fatal rather than degraded**: a video-only
 * init segment appended to a buffer opened with the two-track default fails the FIRST append and plays
 * nothing. A source with no soundtrack is ordinary, so no fixed default serves both. (The profile and
 * level, by contrast, are barely checked — `docs/design/media.md` has the measurements.)
 *
 * @param init The bytes of the `#EXT-X-MAP` segment.
 * @returns A codecs string for {@link segmentMimeType}, or null when no track could be read — the caller
 *   should treat that as "do not open a SourceBuffer", not as "use the default".
 */
export function codecsFromInitSegment(init: Uint8Array): string | null {
  const view = new DataView(init.buffer, init.byteOffset, init.byteLength);
  const codecs: string[] = [];

  /** Read through the view, not by index: a `!` on every `init[i]` would hide the one that IS out of
   * range, where `getUint8` throws. */
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

  /**
   * An ISO 14496-1 "expandable" descriptor length: 1–4 bytes, each with the high bit meaning "continue".
   * Returns the value and the offset just past it.
   */
  const expandable = (at: number): [value: number, next: number] => {
    let value = 0;
    let p = at;
    for (let i = 0; i < 4; i++) {
      const b = u8(p++);
      value = (value << 7) | (b & 0x7f);
      if ((b & 0x80) === 0) break;
    }
    return [value, p];
  };

  /**
   * The AAC audio object type declared inside an `esds`, or null when it cannot be read.
   *
   * Tolerant by design: it finds the DecoderConfigDescriptor rather than parsing the ES_Descriptor's
   * optional fields, then steps its FIXED 13 bytes to the nested DecoderSpecificInfo, whose first 5 bits
   * are the object type (2 = AAC-LC, 5 = HE-AAC, 29 = HE-AACv2, 42 = xHE-AAC).
   */
  const audioObjectType = (from: number, to: number): number | null => {
    for (let p = from; p + 2 < to; p++) {
      if (u8(p) !== 0x04) continue;                            // DecoderConfigDescriptor
      const [, afterLength] = expandable(p + 1);
      if (afterLength >= to || u8(afterLength) !== 0x40) continue;   // MPEG-4 Audio, or not ours
      // objectTypeIndication(1) streamType(1) bufferSizeDB(3) maxBitrate(4) avgBitrate(4)
      const nested = afterLength + 13;
      if (nested + 1 >= to || u8(nested) !== 0x05) continue;    // DecoderSpecificInfo
      const [, config] = expandable(nested + 1);
      if (config >= to) continue;
      const aot = u8(config) >> 3;
      // 31 is the escape for an extended type; not worth decoding for a codec string, and 0 is invalid.
      return aot === 0 || aot === 31 ? null : aot;
    }
    return null;
  };

  /** The `avcC`/`hvcC`/`esds` inside a sample entry, whose own fields come first. */
  const configuration = (format: string, start: number, end: number): string => {
    // The child boxes start after the sample entry's own fields: the 8-byte SampleEntry base
    // (6 reserved + a data reference index) plus VisualSampleEntry's 70, or plus 20 for `mp4a`.
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
      } else if (type === 'esds' && s + 8 <= end) {
        // 🔴 THE AUDIO OBJECT TYPE LIVES IN THE DecoderSpecificInfo, not beside objectTypeIndication.
        // This used to read the byte straight after the `0x40`, which is the STREAM TYPE — for audio
        // that is `(5 << 2) | 1` = 0x15, so every short-form esds produced `mp4a.40.21`,
        // `addSourceBuffer` threw, and nothing played. It also assumed a ONE-byte descriptor length; the
        // length is "expandable" (up to 4 bytes, high bit continues). The kit's own muxer always writes
        // the 4-byte form, so the old scan never matched OUR files and the fallback hid it — the bug was
        // reachable only from a foreign muxer, which is exactly what this parser is for.
        const aot = audioObjectType(s, end);
        derived = `mp4a.40.${aot ?? 2}`;
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
