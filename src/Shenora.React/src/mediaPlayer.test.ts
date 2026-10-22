// @vitest-environment jsdom
import { renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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

  describe('going to the background', () => {
    const setHidden = (hidden: boolean) =>
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => (hidden ? 'hidden' : 'visible') });
    const setPaused = (element: HTMLMediaElement, paused: boolean) =>
      Object.defineProperty(element, 'paused', { configurable: true, get: () => paused });
    afterEach(() => setHidden(false));

    /**
     * 🔴 The background handoff hands over only a page that was PLAYING, so the platform's own pause of a
     * hidden page must not reach the host as the user's. Measured on Android: the page goes hidden with the
     * element still playing, and its `pause` follows ~80 ms later with the page already hidden.
     */
    it("does not report the platform's pause of a hidden page", async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing']);
    });

    it('a page the user paused goes to the background paused', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Paused', 'Paused']);
    });

    /** Should a platform pause the element after hiding the page but before saying so, the report holds. */
    it('reports the visible state even when the platform paused before the page heard it was hidden', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      setHidden(true);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing']);
    });

    /** A pause the HOST asked for — the lock screen, a media key, the app — is the user's, hidden or not. */
    it('reports a pause the host asked for while the page is hidden', async () => {
      const { transport, bus, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      await send(bus, MediaPlayerCommands.pause);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Paused']);
    });

    /** Long after hiding, a pause is not the platform's backgrounding: a media key with the window in the tray. */
    it('reports a pause that comes long after the page was hidden', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      const now = vi.spyOn(performance, 'now').mockReturnValue(1_000);
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      now.mockReturnValue(10_000);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));
      now.mockRestore();

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Paused']);
    });

    /** Back on screen the host hears the element's real state, or it goes on believing a dropped pause away. */
    it('reports what the element is really doing when the page is shown again', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing', 'Paused']);
    });

    /**
     * `load()` pauses with no `pause` event, so a new track does not inherit the last one's `Playing` — and while
     * it opens, hiding sends nothing that would complete its open early.
     */
    it("a new load forgets the previous track's state", async () => {
      const { transport, bus, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      await send(bus, MediaPlayerCommands.load, { uri: 'next.mp4', startAt: 0 });
      setPaused(element, true);

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing']);
    });

    /** A host pause of an element already paused fires no event, so it must not let a later platform pause through. */
    it("a host pause that changed nothing does not excuse a later platform pause", async () => {
      const { transport, bus, element } = createFixture();
      setPaused(element, true);
      await send(bus, MediaPlayerCommands.pause);
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing']);
    });

    /** Only the `pause` event is the platform's: a track that opens as the page hides still completes its open. */
    it('a track opening as the page hides still reports its metadata', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      element.dispatchEvent(new Event('loadedmetadata'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Paused']);
    });

    /** A failure is not a playback state: hiding and showing the page neither repeats it without its reason nor clears it. */
    it('a failed player stays failed through a hide and a show', async () => {
      const { transport, element } = createFixture();
      element.dispatchEvent(new Event('error'));

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.state)).toEqual(['Failed']);
    });

    /**
     * 🔴 The pause it drops is said as the host's own `Playing`, at where the element STOPPED, marked `hidden` so
     * the host holds that position: carried forward it ran ~0.7 s past the pause by `Window.Stopped` (Android).
     */
    it('says where the platform stopped a hidden element, marked so the host holds it', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      element.currentTime = 6.5;
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));

      const last = reports(transport).at(-1);
      expect(reports(transport).map((r) => r.hidden)).toEqual([undefined, undefined, true]);
      expect(last?.state).toBe('Playing');
      expect(last?.position).toBe(6.5);
    });

    /** Paused before the page heard it was hidden, the hidden-time report is past the pause too, and is held. */
    it('marks the hidden-time report when the platform paused first', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      setHidden(true);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.hidden)).toEqual([undefined, true, true]);
    });

    /** A desktop window plays on while hidden — nothing pauses it — so its position must keep moving on the host. */
    it('marks nothing for a hidden page whose film plays on', async () => {
      const { transport, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));

      expect(reports(transport).map((r) => r.hidden)).toEqual([undefined, undefined]);
    });

    /** A page going to the background with its film playing, which the platform then pauses. */
    const playingThenHidden = () => {
      const fixture = createFixture();
      setPaused(fixture.element, false);
      fixture.element.dispatchEvent(new Event('playing'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(fixture.element, true);
      fixture.element.dispatchEvent(new Event('pause'));
      return fixture;
    };

    /**
     * 🔴 Chromium RESUMES a video it paused for a hidden page once the page is shown (measured on Android: ~10 ms
     * after), and the host's pause, arriving while the element was already paused, cannot cancel it. A film that
     * finished natively while the app was away played on from where the page had stopped.
     */
    it('undoes the platform resuming a page the host paused while it was hidden', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(2);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));
      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing', 'Paused', 'Paused']);
    });

    it('undoes a resume that comes before the page hears it is shown', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);

      setHidden(false);
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));

      expect(element.pause).toHaveBeenCalledTimes(2);
      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing']);
    });

    /** No host pause: the film was playing when the app left, so the platform resuming it is what the user had. */
    it("reports the platform's resume when the host said nothing", async () => {
      const { transport, element } = playingThenHidden();

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).not.toHaveBeenCalled();
      expect(reports(transport).map((r) => r.state)).toEqual(['Playing', 'Playing', 'Playing', 'Paused', 'Playing']);
    });

    it('a play the host asks for after its pause is not undone', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));

      await send(bus, MediaPlayerCommands.play);
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });

    /** A media key with the window in the tray plays while the page is still hidden: the user's, not a resume. */
    it('a play while the page is still hidden is not undone', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);

      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });

    /** The host paused a playing film itself, so nothing paused it for hiding and nothing will resume it. */
    it('a play right after showing is not undone when the platform paused nothing', async () => {
      const { transport, bus, element } = createFixture();
      setPaused(element, false);
      element.dispatchEvent(new Event('playing'));
      await send(bus, MediaPlayerCommands.pause);
      setPaused(element, true);
      element.dispatchEvent(new Event('pause'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });

    /** The platform's pause belongs to ONE hide: shown, hidden again with nothing playing, shown — no resume. */
    it('a pause from an earlier hide does not undo a play after a later one', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });

    it('a new load lifts the hold', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);
      await send(bus, MediaPlayerCommands.load, { uri: 'next.mp4', startAt: 0 });

      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });

    /** Long after the page is shown, a play nobody asked for is the user's own, on the element's controls. */
    it('a play long after the page was shown is reported', async () => {
      const { transport, bus, element } = playingThenHidden();
      await send(bus, MediaPlayerCommands.pause);
      const now = vi.spyOn(performance, 'now').mockReturnValue(1_000);
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));

      now.mockReturnValue(10_000);
      setPaused(element, false);
      element.dispatchEvent(new Event('play'));
      now.mockRestore();

      expect(element.pause).toHaveBeenCalledTimes(1);
      expect(reports(transport).at(-1)?.state).toBe('Playing');
    });
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
