import { afterEach, describe, expect, it, vi } from 'vitest';
import { bindSegmentStream, SegmentBinderError } from './segmentBinder.js';

/**
 * 🔴 **The imperative half, tested without a browser — which the module said could not be done.**
 *
 * "jsdom has no MediaSource and both real implementations live on devices" was true of `segmentStream.ts`
 * and became a reason not to write this at all. It stops being true the moment the source and the fetch
 * are injectable: the fakes below drive every branch that can stall playback, including the two the real
 * platforms disagree on (attachment, and the streaming gate).
 *
 * ⚠ What a fake still cannot say is whether a REAL implementation accepts these bytes. That is measured on
 * hardware and recorded in `docs/design/media.md`; this file covers the decisions, not the codecs.
 */

// ── fakes ──────────────────────────────────────────────────────────────────────────────────────────

class FakeTarget {
  private readonly listeners = new Map<string, Set<(e?: unknown) => void>>();

  addEventListener(type: string, fn: (e?: unknown) => void, opts?: { once?: boolean }) {
    const wrapped = opts?.once
      ? (e?: unknown) => { this.removeEventListener(type, wrapped); fn(e); }
      : fn;
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type)!.add(wrapped);
  }

  removeEventListener(type: string, fn: (e?: unknown) => void) {
    this.listeners.get(type)?.delete(fn);
  }

  emit(type: string) {
    for (const fn of [...(this.listeners.get(type) ?? [])]) fn();
  }

  countFor(type: string) { return this.listeners.get(type)?.size ?? 0; }
}

class FakeSourceBuffer extends FakeTarget {
  readonly appends: number[] = [];
  updating = false;

  /** Fail every append through the buffer's `error` event — an undecodable init, a quota refusal. */
  static failAppends = false;

  appendBuffer(bytes: Uint8Array) {
    this.appends.push(bytes.byteLength);
    // Asynchronous, exactly like the real thing: an implementation that completed synchronously would
    // hide a re-entrancy bug rather than expose one.
    queueMicrotask(() => this.emit(FakeSourceBuffer.failAppends ? 'error' : 'updateend'));
  }
}

class FakeMediaSource extends FakeTarget {
  static openImmediately = false;
  readyState = 'closed';
  streaming = true;
  buffers: FakeSourceBuffer[] = [];
  endedWith = 0;
  mimes: string[] = [];

  /** The most recently constructed instance, so a test can reach the SourceBuffer the binder made. */
  static last: FakeMediaSource | null = null;

  /** CLOSE instead of opening — the shape that used to hang the binder forever. */
  static closesInstead = false;

  constructor() {
    super();
    FakeMediaSource.last = this;
    if (FakeMediaSource.closesInstead) {
      // Queued exactly as the open path is, so it lands AFTER the binder has attached its listeners —
      // the real ordering, rather than a race the test would have to win.
      queueMicrotask(() => { this.readyState = 'closed'; this.emit('sourceclose'); });
      return;
    }
    if (FakeMediaSource.openImmediately) { this.readyState = 'open'; return; }
    queueMicrotask(() => { this.readyState = 'open'; this.emit('sourceopen'); });
  }

  /** Refuse the codecs string outright — the real implementations throw NotSupportedError here. */
  static refusesSourceBuffer = false;

  addSourceBuffer(mime: string) {
    if (FakeMediaSource.refusesSourceBuffer) throw new TypeError(`codecs refused: ${mime}`);
    this.mimes.push(mime);
    const b = new FakeSourceBuffer();
    this.buffers.push(b);
    return b as unknown as SourceBuffer;
  }

  endOfStream() { this.endedWith++; this.readyState = 'ended'; }
}

class FakeElement extends FakeTarget {
  currentTime = 0;
  src = '';
  private _srcObject: unknown = null;
  refuseSrcObject = false;

  get srcObject() { return this._srcObject; }
  set srcObject(v: unknown) {
    // The Chromium behaviour, exactly: a MediaSource is refused with a TypeError.
    if (this.refuseSrcObject) throw new TypeError("The provided value is not of type '(MediaSourceHandle or MediaStream)'");
    this._srcObject = v;
  }

  buffered = { length: 0, start: () => 0, end: () => 0 } as unknown as TimeRanges;
}

// A real two-track init segment (H.264 High 2.1 + AAC-LC), the same bytes `segmentStream.test.ts` uses —
// so the codecs this binder opens with are read from something a device actually produced.
const INIT_B64 =
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
  'AAAAAAAAAAEAAAABAAAAAAAAAAAAAAAAAAAAIHRyZXgAAAAAAAAAAgAAAAEAAAAAAAAAAAAAAAA=';

const MANIFEST = [
  '#EXTM3U',
  '#EXT-X-VERSION:7',
  '#EXT-X-TARGETDURATION:6',
  '#EXT-X-MAP:URI="init.mp4"',
  '#EXTINF:6.000,',
  'seg0.m4s',
  '#EXTINF:6.000,',
  'seg1.m4s',
  '#EXT-X-ENDLIST',
].join('\n');

function harness(over: { manifest?: string; missing?: string[] } = {}) {
  const requested: string[] = [];
  const init = Uint8Array.from(atob(INIT_B64), (c) => c.charCodeAt(0));
  const doFetch = vi.fn(async (url: string) => {
    requested.push(url);
    if (over.missing?.some((m) => url.endsWith(m))) {
      return { ok: false, status: 503, arrayBuffer: async () => new ArrayBuffer(0), text: async () => '' };
    }
    if (url.endsWith('.m3u8')) {
      return { ok: true, status: 200, arrayBuffer: async () => new ArrayBuffer(0), text: async () => over.manifest ?? MANIFEST };
    }
    const body = url.endsWith('init.mp4') ? init : new Uint8Array([1, 2, 3, 4]);
    return {
      ok: true, status: 200, text: async () => '',
      arrayBuffer: async () => body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength),
    };
  });
  return { doFetch, requested };
}

const globalsWith = (managed: boolean) => (managed
  ? { ManagedMediaSource: FakeMediaSource }
  : { MediaSource: FakeMediaSource }) as never;

// ── the tests ──────────────────────────────────────────────────────────────────────────────────────

// ⚠ Reset in afterEach, NOT in a try/finally inside the test. A vitest TIMEOUT aborts the test body
// without running its finally — verified while sabotaging the sourceclose listener, where the hang left
// this flag set and took the next test down with it, blaming the wrong code.
afterEach(() => {
  FakeMediaSource.closesInstead = false;
  FakeMediaSource.openImmediately = false;
  FakeMediaSource.refusesSourceBuffer = false;
  FakeSourceBuffer.failAppends = false;
});

describe('bindSegmentStream', () => {
  it('reads the codecs out of the init segment rather than assuming them', async () => {
    const { doFetch } = harness();
    const element = new FakeElement();
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(true), fetch: doFetch as never,
    });
    // High 2.1 + AAC-LC, from the avcC and the esds — NOT segmentMimeType()'s level-4.0 default.
    expect(binding.codecs).toBe('avc1.640015,mp4a.40.2');
    binding.dispose();
  });

  it('attaches with srcObject where it is accepted, and falls back to an object URL where it is not', async () => {
    const { doFetch } = harness();

    const ios = new FakeElement();
    const a = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: ios as never,
      globals: globalsWith(true), fetch: doFetch as never,
    });
    expect(a.attachedBy).toBe('srcObject');
    expect(ios.srcObject).not.toBeNull();
    a.dispose();

    // Chromium refuses a MediaSource on srcObject — the exact TypeError, and the exact fallback.
    const chromium = new FakeElement();
    chromium.refuseSrcObject = true;
    const b = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: chromium as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake',
    });
    expect(b.attachedBy).toBe('objectURL');
    expect(chromium.src).toBe('blob:fake');
    b.dispose();
  });

  it('appends the init segment first, then the segments, and ends the stream', async () => {
    const { doFetch, requested } = harness();
    const element = new FakeElement();
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake',
    });

    await vi.waitFor(() => expect(binding.appended.size).toBe(2));
    expect(requested[0]).toBe('/hls/index.m3u8');
    expect(requested[1]).toBe('/hls/init.mp4');
    expect(requested.slice(2)).toEqual(['/hls/seg0.m4s', '/hls/seg1.m4s']);
    binding.dispose();
  });

  it('rejects when the MediaSource CLOSES instead of opening, rather than waiting forever', async () => {
    // 🔴 `error` is not a MediaSource event — the spec fires sourceopen / sourceended / sourceclose — so
    // the only rejection path this wait had could never fire. An attachment that closes rather than
    // opening left `await bindSegmentStream(…)` pending FOREVER: no error, no diagnostic, no binding to
    // dispose, and the object URL never revoked.
    const { doFetch } = harness();
    const element = new FakeElement();
    const revoked: string[] = [];
    element.refuseSrcObject = true;   // the objectURL path — the only one that can leak a URL
    FakeMediaSource.closesInstead = true;
    {
      const pending = bindSegmentStream({
        manifest: '/hls/index.m3u8', element: element as never,
        globals: globalsWith(false), fetch: doFetch as never,
        createObjectURL: () => 'blob:fake',
        revokeObjectURL: (u: string) => revoked.push(u),
      });

      await expect(pending).rejects.toThrow(SegmentBinderError);
      // ⚠ The URL is minted BEFORE the wait, so a failure past that point must revoke it — the caller
      // never gets a binding to dispose, because bindSegmentStream never returned one.
      expect(revoked).toEqual(['blob:fake']);
    }
  });

  it('revokes the object URL when addSourceBuffer refuses the codecs', async () => {
    // The revoke used to cover only the open wait. This throw lands AFTER the mint and BEFORE the
    // caller holds a binding — one of the two gaps the "every failure past the mint revokes" claim
    // missed (the other is the init append below).
    const { doFetch } = harness();
    const element = new FakeElement();
    element.refuseSrcObject = true;   // the objectURL path — the only one that can leak a URL
    FakeMediaSource.refusesSourceBuffer = true;
    const revoked: string[] = [];

    await expect(bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake',
      revokeObjectURL: (u: string) => revoked.push(u),
    })).rejects.toThrow('codecs refused');
    expect(revoked).toEqual(['blob:fake']);
  });

  it('revokes the object URL when the INIT append fails', async () => {
    const { doFetch } = harness();
    const element = new FakeElement();
    element.refuseSrcObject = true;
    FakeSourceBuffer.failAppends = true;
    const revoked: string[] = [];

    await expect(bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake',
      revokeObjectURL: (u: string) => revoked.push(u),
    })).rejects.toThrow(SegmentBinderError);
    expect(revoked).toEqual(['blob:fake']);
  });

  it('reports a failed segment fetch even when the caller supplied no onDiagnostic', async () => {
    // 🔴 `onDiagnostic` is OPTIONAL, and every failure went through it — so with none supplied (the
    // default, and the shape of the public API) a 500 or a QuotaExceededError produced no console
    // output, no rejection and no state change. Playback just stalled with nothing to explain it, while
    // every other error path in this package defaults to console.error.
    const { doFetch } = harness({ missing: ['seg0.m4s'] });
    const element = new FakeElement();
    const errors: unknown[][] = [];
    const spy = vi.spyOn(console, 'error').mockImplementation((...a: unknown[]) => { errors.push(a); });

    try {
      const binding = await bindSegmentStream({
        manifest: '/hls/index.m3u8', element: element as never,
        globals: globalsWith(false), fetch: doFetch as never,
        createObjectURL: () => 'blob:fake',
      });
      await vi.waitFor(() => expect(errors.length).toBeGreaterThan(0));
      expect(String(errors[0]?.[0])).toContain('seg0.m4s');
      binding.dispose();
    } finally {
      spy.mockRestore();
    }
  });

  it('stays quiet on the console when the caller DID supply onDiagnostic', async () => {
    // The other half: a caller that took the seam owns its reporting and must not be double-logged.
    const { doFetch } = harness({ missing: ['seg0.m4s'] });
    const element = new FakeElement();
    const lines: string[] = [];
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});

    try {
      const binding = await bindSegmentStream({
        manifest: '/hls/index.m3u8', element: element as never,
        globals: globalsWith(false), fetch: doFetch as never,
        createObjectURL: () => 'blob:fake',
        onDiagnostic: (l: string) => lines.push(l),
      });
      await vi.waitFor(() => expect(lines.some((l) => l.includes('seg0.m4s'))).toBe(true));
      expect(spy).not.toHaveBeenCalled();
      binding.dispose();
    } finally {
      spy.mockRestore();
    }
  });

  it('sheds its append listeners instead of stacking one per segment', async () => {
    // 🔴 `{ once: true }` removes only the listener that FIRES, and the success path fires `updateend` —
    // so an `error` listener stayed attached for every segment ever appended, each holding a settled
    // reject closure. A two-hour stream at six-second segments accumulates ~1,200 of them on one
    // SourceBuffer, `dispose()` shed none, and a later real error invoked all of them.
    const { doFetch } = harness();
    const element = new FakeElement();
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake',
    });

    await vi.waitFor(() => expect(binding.appended.size).toBe(2));

    // Three appends have happened by now — the init segment and two media segments.
    const buffer = FakeMediaSource.last!.buffers[0]!;
    expect(buffer.countFor('error')).toBe(0);
    expect(buffer.countFor('updateend')).toBe(0);
    binding.dispose();
  });

  /**
   * 🔴 The gate iOS enforces. Fetching after `endstreaming` is the misuse `ManagedMediaSource` exists to
   * detect, and the penalty there is a torn-down source rather than a warning.
   */
  it('stops fetching once a managed source says endstreaming, and resumes on startstreaming', async () => {
    const { doFetch } = harness({ missing: ['seg0.m4s', 'seg1.m4s'] });
    const element = new FakeElement();
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(true), fetch: doFetch as never,
    });

    const source = element.srcObject as unknown as FakeMediaSource;
    source.emit('endstreaming');
    expect(binding.streaming).toBe(false);

    const before = doFetch.mock.calls.length;
    element.emit('timeupdate');
    await Promise.resolve();
    expect(doFetch.mock.calls.length).toBe(before);   // asked for nothing while told to stop

    source.emit('startstreaming');
    expect(binding.streaming).toBe(true);
    binding.dispose();
  });

  it('treats a 503 as "still producing" and stops the round rather than failing', async () => {
    const { doFetch } = harness({ missing: ['seg1.m4s'] });
    const element = new FakeElement();
    const lines: string[] = [];
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: doFetch as never,
      createObjectURL: () => 'blob:fake', onDiagnostic: (l) => lines.push(l),
    });

    await vi.waitFor(() => expect(binding.appended.has(0)).toBe(true));
    expect(binding.appended.has(1)).toBe(false);
    expect(lines.some((l) => l.includes('503'))).toBe(true);
    binding.dispose();
  });

  it('refuses a playlist that cannot be played, naming why', async () => {
    const element = new FakeElement();
    const noMap = harness({ manifest: '#EXTM3U\n#EXTINF:6.000,\nseg0.m4s\n' });
    await expect(bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(false), fetch: noMap.doFetch as never,
    })).rejects.toThrow(SegmentBinderError);

    const empty = harness({ manifest: '#EXTM3U\n#EXT-X-MAP:URI="init.mp4"\n' });
    await expect(bindSegmentStream({
      manifest: '/hls/index.m3u8', element: new FakeElement() as never,
      globals: globalsWith(false), fetch: empty.doFetch as never,
    })).rejects.toThrow(/no segments/);
  });

  it('refuses a browser with no MediaSource at all', async () => {
    const { doFetch } = harness();
    await expect(bindSegmentStream({
      manifest: '/hls/index.m3u8', element: new FakeElement() as never,
      globals: {} as never, fetch: doFetch as never,
    })).rejects.toThrow(/no MediaSource/);
  });

  it('detaches every listener it added', async () => {
    const { doFetch } = harness();
    const element = new FakeElement();
    const binding = await bindSegmentStream({
      manifest: '/hls/index.m3u8', element: element as never,
      globals: globalsWith(true), fetch: doFetch as never,
    });
    const source = element.srcObject as unknown as FakeMediaSource;

    expect(element.countFor('timeupdate')).toBe(1);
    binding.dispose();
    expect(element.countFor('timeupdate')).toBe(0);
    expect(element.countFor('seeking')).toBe(0);
    expect(source.countFor('startstreaming')).toBe(0);
  });
});
