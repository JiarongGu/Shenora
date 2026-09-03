// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ShenoraBridge } from './bridge.js';
import { MEDIA_PLAYER_STATUS, MediaPlayerCommands } from './mediaPlayer.js';
import { useMediaTransport, type MediaTransportStatus } from './mediaTransport.js';

/**
 * The host is faked at the BRIDGE, which is the seam that matters: everything this hook does is decide
 * WHICH answers to believe, and that decision is made entirely from the order asks and commands were
 * issued in. A real host would add nothing to the question.
 */
type Answer = Partial<MediaTransportStatus> | null;

interface FakeHost {
  bridge: ShenoraBridge;
  /** Every (module, type) the hook asked for, oldest first. */
  calls: string[];
  /** What the next STATUS ask resolves to; a function lets a test defer or vary it. */
  status: () => Promise<Answer>;
  /** What a drive command resolves to. */
  command: () => Promise<Answer>;
}

function createHost(overrides: Partial<Pick<FakeHost, 'status' | 'command'>> = {}): FakeHost {
  const host: FakeHost = {
    calls: [],
    status: () => Promise.resolve({ state: 'Paused', position: 1, duration: 10, rate: 1 }),
    command: () => Promise.resolve({ state: 'Playing', position: 1, duration: 10, rate: 1 }),
    ...overrides,
    bridge: undefined as unknown as ShenoraBridge,
  };
  host.bridge = {
    invoke: (_module: string, type: string) => {
      host.calls.push(type);
      return type === MEDIA_PLAYER_STATUS ? host.status() : host.command();
    },
  } as unknown as ShenoraBridge;
  return host;
}

describe('useMediaTransport', () => {
  it('reports what the host answers', async () => {
    const host = createHost();

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));

    await waitFor(() => expect(result.current.status?.state).toBe('Paused'));
    expect(result.current.status?.position).toBe(1);
    expect(result.current.unanswered).toBe(false);
  });

  /**
   * ⚠ A live stream has no duration and an opening one does not know it yet. A UI that reads a missing
   * duration as 0 puts the playhead at the END of something that has just started.
   */
  it('keeps a missing duration null rather than zero', async () => {
    const host = createHost({ status: () => Promise.resolve({ state: 'Playing', position: 3 }) });

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));

    await waitFor(() => expect(result.current.status).not.toBeNull());
    expect(result.current.status?.duration).toBeNull();
  });

  /**
   * 🔴 THE ONE THIS HOOK EXISTS FOR. A status ask issued before a command returns after it, describing a
   * player that has since been told to do something else. Reported rather than dropped, it undoes the
   * command's own answer — and the next sample undoes that, which is what "the play/pause button is out
   * of sync" looks like from the outside.
   */
  it('drops a status answer that was asked for before a command', async () => {
    let release: ((a: Answer) => void) | undefined;
    const host = createHost({
      // The first STATUS ask hangs until the test releases it, so a command can land in between.
      status: () => new Promise<Answer>((resolve) => { release = resolve; }),
      command: () => Promise.resolve({ state: 'Playing', position: 42, duration: 100, rate: 1 }),
    });

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));
    await waitFor(() => expect(release).toBeDefined());

    await act(async () => { await result.current.play(); });
    expect(result.current.status?.state).toBe('Playing');   // the command's own answer, applied at once

    // The in-flight status ask now answers with what the player was doing BEFORE the play.
    await act(async () => { release!({ state: 'Paused', position: 0, duration: 100, rate: 1 }); });

    expect(result.current.status?.state).toBe('Playing');
    expect(result.current.status?.position).toBe(42);
  });

  /** The same reading is BELIEVED when no command intervened, or the test above would pass on a hook
   * that ignores every status answer it ever receives. */
  it('believes a status answer when no command intervened', async () => {
    const host = createHost({ status: () => Promise.resolve({ state: 'Buffering', position: 7 }) });

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));

    await waitFor(() => expect(result.current.status?.state).toBe('Buffering'));
    expect(result.current.status?.position).toBe(7);
  });

  /**
   * 🔴 A DEAD POLL HAS NO SYMPTOM OF ITS OWN. The callback stops running, the scrubber keeps its last
   * value, and nothing says the transport is gone — it has to be diagnosed from an ABSENCE otherwise.
   */
  it('says so when the host stops answering, and recovers when it comes back', async () => {
    let alive = false;
    const host = createHost({
      status: () => (alive
        ? Promise.resolve<Answer>({ state: 'Playing', position: 2 })
        : Promise.reject(new Error('no host'))),
    });

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));

    await waitFor(() => expect(result.current.unanswered).toBe(true));

    alive = true;
    await waitFor(() => expect(result.current.unanswered).toBe(false));
    expect(result.current.status?.state).toBe('Playing');
  });

  it('asks for nothing while disabled', async () => {
    const host = createHost();

    renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5, enabled: false }));
    await new Promise((r) => setTimeout(r, 30));

    expect(host.calls).toEqual([]);
  });

  it('stops asking once unmounted', async () => {
    const host = createHost();

    const { unmount } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));
    await waitFor(() => expect(host.calls.length).toBeGreaterThan(0));
    unmount();
    const after = host.calls.length;
    await new Promise((r) => setTimeout(r, 40));

    expect(host.calls.length).toBe(after);
  });

  /** A command the host refuses is the host's answer, not a reason to throw at the render. */
  it('does not throw when a command is refused', async () => {
    const host = createHost({ command: () => Promise.reject(new Error('MEDIA_PLAYER_UNAVAILABLE')) });

    const { result } = renderHook(() => useMediaTransport({ bridge: host.bridge, intervalMs: 5 }));

    await expect(result.current.play()).resolves.toBeUndefined();
  });

  it('sends the payloads the host routes expect', async () => {
    const seen: Array<{ type: string; payload?: unknown }> = [];
    const bridge = {
      invoke: (_m: string, type: string, options?: { payload?: unknown }) => {
        seen.push({ type, payload: options?.payload });
        return Promise.resolve(null);
      },
    } as unknown as ShenoraBridge;

    const { result } = renderHook(() => useMediaTransport({ bridge, enabled: false }));
    await act(async () => {
      await result.current.load('file:///a.mkv');
      await result.current.seek(12.5);
      await result.current.setRate(1.5);
    });

    expect(seen).toEqual([
      { type: MediaPlayerCommands.load, payload: { uri: 'file:///a.mkv' } },
      { type: MediaPlayerCommands.seek, payload: { position: 12.5 } },
      { type: MediaPlayerCommands.rate, payload: { rate: 1.5 } },
    ]);
  });
});
