import { describe, expect, it } from 'vitest';
import { decodeMediaPayload, encodeMediaPayload, mediaUrl } from './media.js';

describe('mediaUrl', () => {
  it('is RELATIVE, so each shell supplies its own scheme', () => {
    const url = mediaUrl({ src: 'clip.mp4' });

    // The single most important property. A leading `/` resolves against the page's own origin, which is
    // app:// on iOS and https:// on Android — both fixed forms fail on exactly one platform.
    expect(url.startsWith('/')).toBe(true);
    expect(url).not.toContain('://');
  });

  it('defaults to a `media` route, not a `video` one', () => {
    // Serving is kind-agnostic: audio, images and documents go the same way. Only the playability PLANNER
    // is specific to playable media.
    expect(mediaUrl({ src: 'a.mp4' }).startsWith('/media?')).toBe(true);
  });

  it('lets the app choose the route, with or without a leading slash', () => {
    expect(mediaUrl({ src: 'a' }, 'thumb').startsWith('/thumb?')).toBe(true);
    expect(mediaUrl({ src: 'a' }, '/thumb').startsWith('/thumb?')).toBe(true);
  });

  it('round-trips an arbitrary payload, because the kit does not own its shape', () => {
    const payload = { src: 'clip.mp4', at: 42, prefer: ['mp4', 'webm'], nested: { cache: 'abc' } };

    // `?` is guaranteed present by mediaUrl, but the index signature is still `string | undefined` under
    // strict TS — asserting it here rather than loosening the check.
    const encoded = mediaUrl(payload).split('?')[1];
    expect(encoded).toBeDefined();

    expect(decodeMediaPayload(encoded!)).toEqual(payload);
  });

  it('survives non-ASCII, which plain btoa would throw on', () => {
    // The reason TextEncoder is in there: a title or path with CJK or accents must not fail at the call
    // site, which is a poor way to discover an encoding choice.
    const payload = { src: '影片/日本語 file — ümlaut.mp4' };

    expect(decodeMediaPayload(encodeMediaPayload(payload))).toEqual(payload);
  });

  it('emits base64URL, so no character needs escaping in a query string', () => {
    // Many payloads to make a `+` or `/` in standard base64 overwhelmingly likely.
    for (let i = 0; i < 200; i++) {
      const encoded = encodeMediaPayload({ src: `clip-${i}.mp4`, n: i * 7919, pad: '?'.repeat(i % 5) });
      expect(encoded).not.toMatch(/[+/=]/);
      expect(encodeURIComponent(encoded)).toBe(encoded);   // nothing left for a URL to mangle
    }
  });

  it('refuses an empty route rather than producing `/?…`', () => {
    expect(() => mediaUrl({ src: 'a' }, '')).toThrow(/non-empty/);
  });

  it('encodes null and primitives, not just objects', () => {
    // The payload is opaque, so it is not this package's business whether it is an object.
    expect(decodeMediaPayload(encodeMediaPayload('just-an-id'))).toBe('just-an-id');
    expect(decodeMediaPayload(encodeMediaPayload(42))).toBe(42);
    expect(decodeMediaPayload(encodeMediaPayload(null))).toBe(null);
  });
});
