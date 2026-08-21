import { describe, expect, it } from 'vitest';
import {
  codecsFromInitSegment,
  nextSegment,
  parseManifest,
  pickMediaSource,
  segmentMimeType,
  remoteSegmentUrl,
  SEGMENT_REMOTE_PREFIX,
  type SegmentEntry,
} from './segmentStream.js';

/**
 * The page half of the segment route (D71 piece 4). Only the PURE half is here, and that is the point: the
 * imperative MSE glue cannot be verified anywhere this repo runs — jsdom has no MediaSource and the two real
 * implementations live on devices — so the decisions are lifted out to where a test can pin them.
 *
 * Every case below is one whose failure is silent in a browser: a wrong fetch order stalls, a missing
 * `#EXT-X-MAP` decodes to nothing, and fetching while a managed source said stop is torn down by iOS.
 */
describe('parseManifest', () => {
  const manifest = [
    '#EXTM3U',
    '#EXT-X-VERSION:7',
    '#EXT-X-PLAYLIST-TYPE:VOD',
    '#EXT-X-TARGETDURATION:6',
    '#EXT-X-MEDIA-SEQUENCE:0',
    '#EXT-X-MAP:URI="init.mp4"',
    '#EXTINF:6.000,',
    'seg0.m4s',
    '#EXTINF:6.000,',
    'seg1.m4s',
    '#EXTINF:2.000,',
    'seg2.m4s',
    '#EXT-X-ENDLIST',
    '',
  ].join('\n');

  it('reads the init segment, the target duration and every entry', () => {
    const parsed = parseManifest(manifest);

    expect(parsed.initUri).toBe('init.mp4');
    expect(parsed.targetSeconds).toBe(6);
    expect(parsed.segments).toEqual<SegmentEntry[]>([
      { uri: 'seg0.m4s', seconds: 6 },
      { uri: 'seg1.m4s', seconds: 6 },
      { uri: 'seg2.m4s', seconds: 2 },
    ]);
  });

  /**
   * 🔴 The TAIL's real duration is what makes a scrub bar honest. A parser that took the target duration for
   * every entry would overstate the source by up to a whole segment, and seeking to the end would land past
   * it — the same defect the host's own manifest test guards from the other side.
   */
  it('keeps each entry its OWN duration rather than the target', () => {
    const total = parseManifest(manifest).segments.reduce((sum, s) => sum + s.seconds, 0);

    expect(total).toBe(14);
  });

  /**
   * A playlist with no `#EXT-X-MAP` is not playable through MediaSource — a fragment repeats no decoder
   * configuration, so appending one without it is a silent decode error. Null lets the caller say so
   * instead of appending into the dark.
   */
  it('reports a MISSING init segment as null rather than guessing one', () => {
    const parsed = parseManifest('#EXTM3U\n#EXTINF:6.000,\nseg0.m4s\n');

    expect(parsed.initUri).toBeNull();
    expect(parsed.segments).toHaveLength(1);
  });

  /** Unknown tags are skipped: a playlist gains tags over time, and a page older than its host must not break. */
  it('ignores tags it does not understand', () => {
    const parsed = parseManifest(
      '#EXTM3U\n#EXT-X-SOMETHING-NEW:1\n#EXT-X-MAP:URI="i.mp4"\n#EXTINF:1.5,\na.m4s\n');

    expect(parsed.initUri).toBe('i.mp4');
    expect(parsed.segments).toEqual([{ uri: 'a.m4s', seconds: 1.5 }]);
  });

  it('survives an empty or comment-only playlist', () => {
    expect(parseManifest('').segments).toEqual([]);
    expect(parseManifest('#EXTM3U\n').segments).toEqual([]);
  });
});

describe('pickMediaSource', () => {
  /**
   * 🔴 The order is the decision. iPhone has only `ManagedMediaSource`, Android only `MediaSource` — but
   * where BOTH exist the managed one still wins, because it is the one that says when the platform actually
   * wants data. A page that streams regardless is what it was introduced to stop.
   */
  it('prefers the managed source wherever it exists', () => {
    expect(pickMediaSource({ ManagedMediaSource: class {}, MediaSource: class {} })).toBe('managed');
    expect(pickMediaSource({ ManagedMediaSource: class {} })).toBe('managed');
  });

  it('falls back to the standard source, and reports none honestly', () => {
    expect(pickMediaSource({ MediaSource: class {} })).toBe('standard');
    expect(pickMediaSource({})).toBe('none');
    // A non-callable of the right name is not an implementation — some environments stub globals.
    expect(pickMediaSource({ MediaSource: {}, ManagedMediaSource: null })).toBe('none');
  });
});

describe('nextSegment', () => {
  const segments: SegmentEntry[] = [
    { uri: 'seg0.m4s', seconds: 6 },
    { uri: 'seg1.m4s', seconds: 6 },
    { uri: 'seg2.m4s', seconds: 2 },
  ];
  const policy = { segments, targetAheadSeconds: 12 };
  const base = { currentTime: 0, bufferedAhead: 0, appended: new Set<number>(), streaming: true };

  it('starts at the beginning and walks forward as segments are appended', () => {
    expect(nextSegment(base, policy)).toBe(0);
    expect(nextSegment({ ...base, appended: new Set([0]) }, policy)).toBe(1);
    expect(nextSegment({ ...base, appended: new Set([0, 1]) }, policy)).toBe(2);
    expect(nextSegment({ ...base, appended: new Set([0, 1, 2]) }, policy)).toBeNull();
  });

  /**
   * 🔴 <b>The seek case, and the reason this takes `currentTime` rather than "the last index appended".</b>
   * After a jump the last append is nowhere near the user; a binder that continued from it would fetch the
   * wrong part of the film and stall while the buffer it needed never arrived.
   */
  it('fetches where PLAYBACK is, not where appending stopped', () => {
    // ⚠ The fixture has to leave the EARLIER segments unappended, or the assertion cannot fail: with 0 and 1
    // already buffered, a binder scanning from the start of the playlist reaches 2 as well and the test
    // passes while proving nothing. (Found by sabotage — the first version asserted exactly that.)
    const coldSeek = { ...base, currentTime: 13, appended: new Set<number>() };

    expect(nextSegment(coldSeek, policy)).toBe(2);
  });

  /**
   * The same distinction stated as the failure it prevents: a cold seek into the tail must NOT drag the
   * whole film in from the beginning. That is a stall the user watches — the buffer they need never arrives
   * because the binder is busy fetching the part they skipped.
   */
  it('does not restart from the beginning after a cold seek', () => {
    expect(nextSegment({ ...base, currentTime: 7, appended: new Set<number>() }, policy)).toBe(1);
    expect(nextSegment({ ...base, currentTime: 13.5, appended: new Set<number>() }, policy)).toBe(2);
  });

  /** A seek BACK into already-buffered content needs nothing — the bytes are there. */
  it('asks for nothing when the seek target is already appended', () => {
    expect(nextSegment({ ...base, currentTime: 7, appended: new Set([0, 1, 2]) }, policy)).toBeNull();
  });

  /**
   * 🔴 Fetching while a managed source has said stop is the exact misuse it exists to detect — on iOS the
   * platform tears the source down rather than merely wasting the request.
   */
  it('fetches NOTHING while the source is not streaming', () => {
    expect(nextSegment({ ...base, streaming: false }, policy)).toBeNull();
    // …and resumes on the next event, rather than needing a nudge from elsewhere.
    expect(nextSegment({ ...base, streaming: true }, policy)).toBe(0);
  });

  /**
   * Fetching further ahead than the policy asks does not smooth playback, it fills a quota — and a
   * `QuotaExceededError` on append surfaces as a stall with no obvious cause.
   */
  it('stops once enough is buffered ahead', () => {
    expect(nextSegment({ ...base, bufferedAhead: 12 }, policy)).toBeNull();
    expect(nextSegment({ ...base, bufferedAhead: 11.9 }, policy)).toBe(0);
  });

  /**
   * ⚠ The walk uses each entry's OWN duration. Dividing `currentTime` by the target duration puts the tail
   * index past the end of a playlist whose last segment is short — an off-by-one that only ever appears at
   * the end of a film.
   */
  it('lands on the short final segment rather than past it', () => {
    expect(nextSegment({ ...base, currentTime: 13.5, appended: new Set([0, 1]) }, policy)).toBe(2);
    // Beyond the end there is nothing left to ask for.
    expect(nextSegment({ ...base, currentTime: 99, appended: new Set([0, 1, 2]) }, policy)).toBeNull();
  });

  it('has nothing to say about an empty playlist', () => {
    expect(nextSegment(base, { segments: [], targetAheadSeconds: 12 })).toBeNull();
  });
});

describe('segmentMimeType', () => {
  /**
   * ⚠ The codecs parameter is required rather than decorative: `addSourceBuffer('video/mp4')` throws
   * `NotSupportedError` everywhere, because the buffer must know what it will be fed before the init
   * segment arrives.
   */
  it('always carries a codecs parameter', () => {
    expect(segmentMimeType()).toBe('video/mp4; codecs="avc1.640028,mp4a.40.2"');
    expect(segmentMimeType('avc1.42E01E')).toBe('video/mp4; codecs="avc1.42E01E"');
  });
});

/**
 * 🔴 **These are REAL init segments, produced by the host's own segment engine from a real H.264/AAC
 * file** — not hand-built boxes, which is the whole reason they are worth having. They are the exact
 * bytes measured against Chromium 151: the two-track one plays through the shipped default, and the
 * video-only one FAILS its first append against that same default and plays through what this function
 * returns.
 */
describe('codecsFromInitSegment', () => {
  const bytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));

  /** H.264 High 2.1 picture beside AAC-LC — what essentially every real film copies as (D76). */
  const twoTrack = bytes(
    'AAAAKGZ0eXBpc281AAACAGlzb21pc281aXNvNmF2YzFtcDQxZGFzaAAABEhtb292AAAAbG12aGQAAAAAAAAAAAAAAAAAAAPo' +
    'AAAAAAABAAABAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAA' +
    'AAAAAAAAAAAAAAADAAAB4XRyYWsAAABcdGtoZAAAAAcAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAA' +
    'AAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAB4AAAAQ4AAAAAAX1tZGlhAAAAIG1kaGQAAAAAAAAAAAAAAAAAAAPo' +
    'AAAAAFXEAAAAAAAtaGRscgAAAAAAAAAAdmlkZQAAAAAAAAAAAAAAAFZpZGVvSGFuZGxlcgAAAAEobWluZgAAABR2bWhkAAAA' +
    'AQAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAA6HN0YmwAAACcc3RzZAAAAAAAAAABAAAA' +
    'jGF2YzEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAB4AEOAEgAAABIAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' +
    'AAAAAAAAAAAY//8AAAA2YXZjQwFkABX/4QAaZ2QAFazZQeCP6wEQAAADABAAAAMBQPFi2WABAAVo74IcsP34+AAAAAAQc3R0' +
    'cwAAAAAAAAAAAAAAEHN0c2MAAAAAAAAAAAAAABRzdHN6AAAAAAAAAAAAAAAAAAAAEHN0Y28AAAAAAAAAAAAAAat0cmFrAAAA' +
    'XHRraGQAAAAHAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAA' +
    'AAAAAABAAAAAAAAAAAAAAAAAAAFHbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAAD6AAAAABVxAAAAAAALWhkbHIAAAAAAAAA' +
    'AHNvdW4AAAAAAAAAAAAAAABTb3VuZEhhbmRsZXIAAAAA8m1pbmYAAAAQc21oZAAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAA' +
    'AAAAAAABAAAADHVybCAAAAABAAAAtnN0YmwAAABqc3RzZAAAAAAAAAABAAAAWm1wNGEAAAAAAAAAAQAAAAAAAAAAAAEAEAAA' +
    'AACsRAAAAAAANmVzZHMAAAAAA4CAgCIAAAAEgICAF0AVAAAAAAAAAAAAAAAFgICABRIIVuUABoCAgAECAAAAEHN0dHMAAAAA' +
    'AAAAAAAAABBzdHNjAAAAAAAAAAAAAAAUc3RzegAAAAAAAAAAAAAAAAAAABBzdGNvAAAAAAAAAAAAAABIbXZleAAAACB0cmV4' +
    'AAAAAAAAAAEAAAABAAAAAAAAAAAAAAAAAAAAIHRyZXgAAAAAAAAAAgAAAAEAAAAAAAAAAAAAAAA=',
  );

  /** The same source with its soundtrack dropped — ordinary, and fatal to a fixed two-track default. */
  const videoOnly = bytes(
    'AAAAKGZ0eXBpc281AAACAGlzb21pc281aXNvNmF2YzFtcDQxZGFzaAAAAn1tb292AAAAbG12aGQAAAAAAAAAAAAAAAAAAAPo' +
    'AAAAAAABAAABAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAA' +
    'AAAAAAAAAAAAAAACAAAB4XRyYWsAAABcdGtoZAAAAAcAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAA' +
    'AAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAB4AAAAQ4AAAAAAX1tZGlhAAAAIG1kaGQAAAAAAAAAAAAAAAAAAAPo' +
    'AAAAAFXEAAAAAAAtaGRscgAAAAAAAAAAdmlkZQAAAAAAAAAAAAAAAFZpZGVvSGFuZGxlcgAAAAEobWluZgAAABR2bWhkAAAA' +
    'AQAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAA6HN0YmwAAACcc3RzZAAAAAAAAAABAAAA' +
    'jGF2YzEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAB4AEOAEgAAABIAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' +
    'AAAAAAAAAAAY//8AAAA2YXZjQwFkABX/4QAaZ2QAFazZQeCP6wEQAAADABAAAAMBQPFi2WABAAVo74IcsP34+AAAAAAQc3R0' +
    'cwAAAAAAAAAAAAAAEHN0c2MAAAAAAAAAAAAAABRzdHN6AAAAAAAAAAAAAAAAAAAAEHN0Y28AAAAAAAAAAAAAAChtdmV4AAAA' +
    'IHRyZXgAAAAAAAAAAQAAAAEAAAAAAAAAAAAAAAA=',
  );

  /** Build an ISO box: 4-byte size + fourcc + payload, sizes computed. */
  const box = (type: string, ...parts: (Uint8Array | number[])[]): Uint8Array => {
    const body = parts.flatMap((p) => Array.from(p));
    const size = 8 + body.length;
    return Uint8Array.from([
      (size >>> 24) & 0xff, (size >>> 16) & 0xff, (size >>> 8) & 0xff, size & 0xff,
      ...Array.from(type, (c) => c.charCodeAt(0)),
      ...body,
    ]);
  };

  /**
   * An AAC init segment whose `esds` descriptors use the SHORT length form.
   *
   * 🔴 The kit's own muxer always writes the EXPANDED 4-byte form (`80 80 80 LL`), which is why the real
   * fixture above never exercised this path — the scan failed to match and the fallback happened to give
   * the right answer. MP4Box, Bento4 and Apple emit the short form, which is equally legal, and that is
   * the whole foreign-source route this parser exists for.
   */
  const shortFormEsds = (audioSpecificConfig: number[]) => {
    const dsi = [0x05, audioSpecificConfig.length, ...audioSpecificConfig];
    const decoderConfig = [
      0x04, 13 + dsi.length,
      0x40,             // objectTypeIndication: MPEG-4 Audio
      0x15,             // streamType 5 (audio) << 2 | 1 — the byte that was read AS the object type
      0, 0, 0,          // bufferSizeDB
      0, 0, 0, 0,       // maxBitrate
      0, 0, 0, 0,       // avgBitrate
      ...dsi,
    ];
    const es = [0x03, 3 + decoderConfig.length + 3, 0x00, 0x01, 0x00, ...decoderConfig, 0x06, 0x01, 0x02];
    const esds = box('esds', [0, 0, 0, 0], es);
    const mp4a = box('mp4a', new Array(28).fill(0), esds);
    const stsd = box('stsd', [0, 0, 0, 0, 0, 0, 0, 1], mp4a);
    return box('moov', box('trak', box('mdia', box('minf', box('stbl', stsd)))));
  };

  it('🔴 reads the AUDIO OBJECT TYPE, not the stream type, from a short-form esds', () => {
    // 0x12 = 00010_010 -> audio object type 2, AAC-LC. Reading the byte after objectTypeIndication
    // instead yields 0x15 = 21, and `mp4a.40.21` makes addSourceBuffer throw — nothing plays.
    expect(codecsFromInitSegment(shortFormEsds([0x12, 0x08]))).toBe('mp4a.40.2');
  });

  it('reads HE-AAC (object type 5) rather than defaulting everything to LC', () => {
    // 0x2B = 00101_011 -> object type 5.
    expect(codecsFromInitSegment(shortFormEsds([0x2b, 0x08, 0x00]))).toBe('mp4a.40.5');
  });

  it('reads both tracks, exactly, from a real two-track init segment', () => {
    // 640015 is High (0x64) profile, no constraints, level 2.1 (0x15) — read from the copied `avcC`,
    // NOT the shipped default's level 4.0 guess.
    expect(codecsFromInitSegment(twoTrack)).toBe('avc1.640015,mp4a.40.2');
  });

  it('names ONE track for a video-only source — the case the fixed default kills', () => {
    expect(codecsFromInitSegment(videoOnly)).toBe('avc1.640015');
  });

  it('answers null rather than a default when there is no track to read', () => {
    expect(codecsFromInitSegment(new Uint8Array(0))).toBeNull();
    expect(codecsFromInitSegment(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]))).toBeNull();
    // A truncated box must not walk off the end.
    expect(codecsFromInitSegment(twoTrack.slice(0, 40))).toBeNull();
  });

  it('composes with segmentMimeType', () => {
    expect(segmentMimeType(codecsFromInitSegment(twoTrack)!))
      .toBe('video/mp4; codecs="avc1.640015,mp4a.40.2"');
  });
});

describe('remoteSegmentUrl — naming a source by its handle', () => {
  it('builds the shape the host route parses', () => {
    // 🔴 Mirrors `SegmentStreamOptions.RemotePrefix`. The two are checked against each other by the
    // wire-reference gate; this pins the SHAPE, which the gate cannot see.
    expect(remoteSegmentUrl('/shenora-hls/', 'abc123'))
      .toBe('/shenora-hls/~remote/abc123/index.m3u8');
    expect(SEGMENT_REMOTE_PREFIX).toBe('~remote/');
  });

  it('tolerates a route path given without its trailing slash', () => {
    // Both spellings are natural to write, and the difference would otherwise be a 404 with no clue —
    // the route matches on a prefix, so `//` or a missing `/` simply fails to parse.
    expect(remoteSegmentUrl('/shenora-hls', 'abc123'))
      .toBe('/shenora-hls/~remote/abc123/index.m3u8');
  });

  it('names another resource under the same handle', () => {
    expect(remoteSegmentUrl('/shenora-hls/', 'abc123', 'seg7.m4s'))
      .toBe('/shenora-hls/~remote/abc123/seg7.m4s');
  });

  it('escapes the handle rather than trusting its shape', () => {
    // The kit issues 32 hex characters, so this can never bite through the kit's own registry — but the
    // handle arrives from the app, and a url builder that assumes its input is safe is the wrong shape
    // to ship regardless of who happens to be calling it today.
    expect(remoteSegmentUrl('/shenora-hls/', 'a/b?c')).toBe('/shenora-hls/~remote/a%2Fb%3Fc/index.m3u8');
  });
});
