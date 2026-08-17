// @vitest-environment jsdom
import { renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { FakeTransport } from './testing/fakeTransport.js';
import {
  MEDIA_PLAYER_MODULE,
  MEDIA_PLAYER_REPORT,
  MediaPlayerCommands,
  useMediaPlayer,
  type MediaPlayerReport,
} from './mediaPlayer.js';

/**
 * jsdom's HTMLMediaElement has no playback engine — `play()` is a stub and `load()` does nothing. That is
 * fine and is what these tests are for: the hook's job is TRANSLATION (host command → element call, element
 * event → host report), and translation is exactly what can be checked without a decoder.
 *
 * ⚠ What this cannot prove: that a real element honours the calls. That is a DEVICE claim, like the native
 * player's.
 */
function createFixture() {
  const transport = new FakeTransport();
  const bus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: bus });
  const element = document.createElement('video');
  document.body.appendChild(element);
  const ref = { current: element as HTMLMediaElement | null };

  // jsdom throws "Not implemented" from these; the hook only cares that it called them.
  element.load = vi.fn();
  element.play = vi.fn().mockResolvedValue(undefined);
  element.pause = vi.fn();

  renderHook(() => useMediaPlayer(ref, { bridge, eventBus: bus }));
  return { transport, bus, element };
}

/** Every one-way message the page sent back, newest last. The fake records raw JSON, so parse it. */
function reports(transport: FakeTransport): MediaPlayerReport[] {
  return transport.raw
    .map((line) => JSON.parse(line) as { module: string; type: string; payload: MediaPlayerReport })
    .filter((m) => m.module === MEDIA_PLAYER_MODULE && m.type === MEDIA_PLAYER_REPORT)
    .map((m) => m.payload);
}

async function send(bus: ShenoraEventBus, type: string, payload?: unknown) {
  await bus.emit({ module: MEDIA_PLAYER_MODULE, type, payload });
}

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('useMediaPlayer', () => {
  it('points the element at what the host resolved', async () => {
    const { bus, element } = createFixture();

    await send(bus, MediaPlayerCommands.load, { uri: 'app://files/song.m4a', startAt: 0 });

    expect(element.src).toContain('app://files/song.m4a');
    // ⚠ load() matters: without it a SECOND load keeps the previous buffer and a seek lands in the old media.
    expect(element.load).toHaveBeenCalled();
  });

  it('a superseded load does not seek the NEXT track to the old position', async () => {
    // 🔴 `{ once: true }` removes a listener only when it FIRES. `element.load()` aborts the previous
    // load, so its `loadedmetadata` never comes and the seek listener survives — then runs on the next
    // track's metadata. Loading A at 10:00 and then B at 0:00 started B ten minutes in, because B sets
    // no listener of its own and A's was still attached.
    const { bus, element } = createFixture();

    await send(bus, MediaPlayerCommands.load, { uri: 'app://files/a.m4a', startAt: 600 });
    await send(bus, MediaPlayerCommands.load, { uri: 'app://files/b.m4a', startAt: 0 });

    // B's metadata arrives; A's abandoned listener must not be here to see it.
    element.dispatchEvent(new Event('loadedmetadata'));

    expect(element.currentTime).toBe(0);
  });

  it('still honours startAt for a load that is NOT superseded', async () => {
    // The positive case — a cancellation that cancels everything is not a fix.
    const { bus, element } = createFixture();

    await send(bus, MediaPlayerCommands.load, { uri: 'app://files/a.m4a', startAt: 42 });
    element.dispatchEvent(new Event('loadedmetadata'));

    expect(element.currentTime).toBe(42);
  });

  it('drives play, pause, seek and rate', async () => {
    const { bus, element } = createFixture();

    await send(bus, MediaPlayerCommands.play);
    await send(bus, MediaPlayerCommands.pause);
    await send(bus, MediaPlayerCommands.seek, { position: 42 });
    await send(bus, MediaPlayerCommands.rate, { rate: 1.5 });

    expect(element.play).toHaveBeenCalled();
    expect(element.pause).toHaveBeenCalled();
    expect(element.currentTime).toBe(42);
    expect(element.playbackRate).toBe(1.5);
  });

  it('reports element transitions back to the host', async () => {
    const { transport, element } = createFixture();

    element.dispatchEvent(new Event('play'));
    element.dispatchEvent(new Event('waiting'));
    element.dispatchEvent(new Event('ended'));

    expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Buffering', 'Ended']);
  });

  /**
   * ⚠ The rule this pins: `timeupdate` fires ~4×/second, and forwarding it would spend battery and IPC
   * telling the host something it can extrapolate from a position and a rate.
   */
  it('never reports on timeupdate', async () => {
    const { transport, element } = createFixture();

    element.dispatchEvent(new Event('timeupdate'));
    element.dispatchEvent(new Event('timeupdate'));

    expect(reports(transport)).toHaveLength(0);
  });

  /**
   * A refused autoplay is a real outcome the host must hear — otherwise it believes playback started and
   * its Now Playing surface says "playing" over silence.
   */
  it('reports a rejected play() instead of swallowing it', async () => {
    const { transport, bus, element } = createFixture();
    element.play = vi.fn().mockRejectedValue(new DOMException('blocked', 'NotAllowedError'));

    await send(bus, MediaPlayerCommands.play);
    await Promise.resolve();
    await Promise.resolve();

    const last = reports(transport).at(-1);
    expect(last?.state).toBe('Failed');
    expect(last?.error).toBe('NotAllowedError');
  });

  /**
   * ⚠ Deliberately NOT `error.message`: browsers put decoder internals and sometimes the full URL there,
   * and this string crosses to the host and can reach a log.
   */
  it('reports a stable error code, not the browser message', async () => {
    const { transport, element } = createFixture();
    Object.defineProperty(element, 'error', {
      value: { code: 4, message: 'DEMUXER_ERROR /Users/someone/private/clip.mkv' },
      configurable: true,
    });

    element.dispatchEvent(new Event('error'));

    const last = reports(transport).at(-1);
    expect(last?.state).toBe('Failed');
    expect(last?.error).toBe('SourceNotSupported');
    expect(JSON.stringify(last)).not.toContain('private');
  });

  it('unloading frees the buffer and says the player is empty', async () => {
    const { transport, bus, element } = createFixture();
    await send(bus, MediaPlayerCommands.load, { uri: 'app://files/song.m4a', startAt: 0 });
    (element.load as ReturnType<typeof vi.fn>).mockClear();

    await send(bus, MediaPlayerCommands.unload);

    expect(element.hasAttribute('src')).toBe(false);
    // ⚠ load() after clearing src is what actually releases the decoded data.
    expect(element.load).toHaveBeenCalled();
    expect(reports(transport).at(-1)?.state).toBe('Empty');
  });

  it('stops driving the element once unmounted', async () => {
    const transport = new FakeTransport();
    const bus = new ShenoraEventBus();
    const bridge = new ShenoraBridge({ transport, eventBus: bus });
    const element = document.createElement('video');
    element.play = vi.fn().mockResolvedValue(undefined);
    const ref = { current: element as HTMLMediaElement | null };

    const { unmount } = renderHook(() => useMediaPlayer(ref, { bridge, eventBus: bus }));
    unmount();

    await send(bus, MediaPlayerCommands.play);
    element.dispatchEvent(new Event('play'));

    expect(element.play).not.toHaveBeenCalled();
    expect(reports(transport)).toHaveLength(0);
  });
});
