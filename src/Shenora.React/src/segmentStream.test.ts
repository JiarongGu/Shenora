import { describe, expect, it } from 'vitest';
import {
  nextSegment,
  parseManifest,
  pickMediaSource,
  segmentMimeType,
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
