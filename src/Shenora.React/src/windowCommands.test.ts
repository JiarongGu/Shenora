// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import type { IpcRequest } from './types.js';
import { WindowCommands, useWindowMaximized } from './windowCommands.js';
import { FakeTransport } from './testing/fakeTransport.js';

function createCommands() {
  const transport = new FakeTransport();
  const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
  return { transport, commands: new WindowCommands(bridge) };
}

describe('WindowCommands', () => {
  it('sends the reserved WINDOW routes', async () => {
    const { transport, commands } = createCommands();

    void commands.minimize();
    void commands.startDrag();
    void commands.startResize('topLeft');
    void commands.setTheme(false);

    expect(transport.posted.map((r) => `${r.module}.${r.type}`)).toEqual([
      'WINDOW.MINIMIZE',
      'WINDOW.START_DRAG',
      'WINDOW.START_RESIZE',
      'WINDOW.SET_THEME',
    ]);
    expect(transport.posted[2]?.payload).toEqual({ edge: 'topLeft' });
    expect(transport.posted[3]?.payload).toEqual({ dark: false });
  });

  it('isMaximized unwraps the host answer', async () => {
    const { transport, commands } = createCommands();

    const promise = commands.isMaximized();
    transport.respondToLast({ maximized: true });

    await expect(promise).resolves.toBe(true);
  });
});

describe('useWindowMaximized', () => {
  it('queries on mount and re-queries on window resize', async () => {
    const { transport, commands } = createCommands();
    const { result } = renderHook(() => useWindowMaximized(commands));

    expect(result.current).toBe(false);
    act(() => transport.respondToLast({ maximized: true }));
    await waitFor(() => expect(result.current).toBe(true));

    act(() => {
      window.dispatchEvent(new Event('resize')); // maximize/restore always resizes — the resync signal
    });
    await waitFor(() => expect(transport.posted.length).toBe(2));
    act(() => transport.respondToLast({ maximized: false }));
    await waitFor(() => expect(result.current).toBe(false));
  });

  it('coalesces a burst of resize events into one query', async () => {
    // A window drag fires `resize` continuously — roughly 180 events over 3 seconds — and each one used
    // to start a full IPC round-trip, every one arming a 30-second timeout timer (P5.5 H2). The state
    // only changes at the END of a resize, so the trailing edge is also the correct semantics.
    const { transport, commands } = createCommands();
    renderHook(() => useWindowMaximized(commands));
    act(() => transport.respondToLast({ maximized: false }));
    expect(transport.posted).toHaveLength(1); // the initial read is immediate

    act(() => {
      for (let i = 0; i < 50; i++) window.dispatchEvent(new Event('resize'));
    });

    // Still one: every event landed inside the debounce window.
    expect(transport.posted).toHaveLength(1);
    await waitFor(() => expect(transport.posted).toHaveLength(2));
    expect(transport.posted).toHaveLength(2); // exactly one follow-up for the whole burst
  });

  it('unsubscribes the resize listener on unmount', async () => {
    const { transport, commands } = createCommands();
    const { unmount } = renderHook(() => useWindowMaximized(commands));
    act(() => transport.respondToLast({ maximized: false }));

    unmount();
    act(() => {
      window.dispatchEvent(new Event('resize'));
    });

    expect(transport.posted).toHaveLength(1); // no re-query after unmount
  });
});
