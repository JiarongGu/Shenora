// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import type { IpcRequest } from './types.js';
import { DROP_ZONE_MODULE, useDropZone } from './useDropZone.js';
import { FakeTransport } from './testing/fakeTransport.js';

function createFixture() {
  const transport = new FakeTransport();
  // These zones must reach the "registered" state, so the fixture acks. This was the local fake's
  // class default; the shared one defaults to OFF (three of the four suites it replaced reply by
  // hand), so it is set explicitly here — the one test that needs acks held back overrides it back
  // to false, and that override is only meaningful if the default here is true.
  transport.autoAck = true;
  const bus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: bus });
  const element = document.createElement('div');
  document.body.appendChild(element);
  const targetRef = { current: element as HTMLElement | null };
  return { transport, bus, bridge, element, targetRef };
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
  document.body.innerHTML = '';
});

async function flush() {
  // Run the 100 ms debounces + the microtask acks.
  await act(async () => {
    vi.advanceTimersByTime(150);
    await Promise.resolve();
  });
}

describe('useDropZone', () => {
  it('registers on mount, tags the element, and unregisters on unmount', async () => {
    const { transport, bus, bridge, element, targetRef } = createFixture();
    const { unmount } = renderHook(() =>
      useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));

    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);
    expect(transport.posted[0]?.payload).toMatchObject({ zoneId: 'z1', x: 0, y: 0 });
    expect(element.getAttribute('data-drop-zone-id')).toBe('z1');

    unmount();
    await flush();
    expect(transport.routes()).toEqual(['REGISTER', 'UNREGISTER']);
    expect(element.hasAttribute('data-drop-zone-id')).toBe(false);
  });

  it('registers a target that mounts AFTER the first effect run', async () => {
    // The effect's deps were [enabled, targetRef], and targetRef is a STABLE object — so the effect ran
    // once, and if targetRef.current was null on that run (a conditionally-rendered target) it bailed
    // out and NEVER re-ran: the zone was silently dead for the component's whole life (P5.5 H2).
    const { transport, bus, bridge } = createFixture();
    const targetRef = { current: null as HTMLElement | null };

    const { rerender } = renderHook(() =>
      useDropZone({ targetRef, onDrop: () => {}, zoneId: 'late', bridge, bus }));

    await flush();
    expect(transport.routes()).toEqual([]); // nothing to track yet — correct

    // The target appears (the conditional branch renders and React attaches the ref).
    const element = document.createElement('div');
    document.body.appendChild(element);
    targetRef.current = element;
    await act(async () => { rerender(); await Promise.resolve(); });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER']);
    expect(element.getAttribute('data-drop-zone-id')).toBe('late');
  });

  it('tears the zone down when the target unmounts', async () => {
    // The mirror case: the element going away must unregister, not leave an orphaned overlay.
    const { transport, bus, bridge, element, targetRef } = createFixture();
    const { rerender } = renderHook(() =>
      useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    element.remove();
    targetRef.current = null;
    await act(async () => { rerender(); await Promise.resolve(); });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER', 'UNREGISTER']);
  });

  it('delivers file drops for its own zone only', async () => {
    const { bus, bridge, targetRef } = createFixture();
    const drops: string[][] = [];
    renderHook(() =>
      useDropZone({ targetRef, onDrop: (files) => drops.push(files), zoneId: 'z1', bridge, bus }));
    await flush();

    act(() => {
      bus.emit({ module: DROP_ZONE_MODULE, type: 'FILE_DROP', payload: { zoneId: 'other', files: ['C:\\x'], position: { x: 0, y: 0 } } });
      bus.emit({ module: DROP_ZONE_MODULE, type: 'FILE_DROP', payload: { zoneId: 'z1', files: ['C:\\a', 'C:\\b'], position: { x: 1, y: 2 } } });
    });

    expect(drops).toEqual([['C:\\a', 'C:\\b']]);
  });

  it('toggles the drop class on drag enter/leave', async () => {
    const { bus, bridge, element, targetRef } = createFixture();
    renderHook(() =>
      useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', dropClassName: 'hovering', bridge, bus }));
    await flush();

    act(() => bus.emit({ module: DROP_ZONE_MODULE, type: 'DRAG_ENTER', payload: { zoneId: 'z1' } }));
    expect(element.classList.contains('hovering')).toBe(true);

    act(() => bus.emit({ module: DROP_ZONE_MODULE, type: 'DRAG_LEAVE', payload: { zoneId: 'z1' } }));
    expect(element.classList.contains('hovering')).toBe(false);
  });

  it('sends SHOW when the mouse leaves the element', async () => {
    const { transport, bus, bridge, element, targetRef } = createFixture();
    renderHook(() => useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));
    await flush();

    act(() => {
      element.dispatchEvent(new Event('mouseleave'));
    });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER', 'SHOW']);
  });

  it('does nothing while disabled', async () => {
    const { transport, bus, bridge, targetRef } = createFixture();
    renderHook(() =>
      useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', enabled: false, bridge, bus }));
    await flush();

    expect(transport.posted).toHaveLength(0);
  });

  it('a failed REGISTER reaches the app\'s onError, and the console when there is none', async () => {
    // A D63 test: the sink is SUPPLIED and asserted USED, because an absent handler and a working one
    // are indistinguishable from the outside — a drop zone that failed to register looks exactly like
    // one nobody has dragged onto yet.
    const { transport, bus, bridge, targetRef } = createFixture();
    transport.autoAck = false;
    const onError = vi.fn();
    renderHook(() => useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus, onError }));
    await flush();

    act(() => transport.fail(transport.lastRequest().id, 'NO_HANDLER'));
    await act(async () => { await Promise.resolve(); });

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0]?.[1]).toBe('REGISTER');
  });

  it('falls back to the console when no onError is supplied, and does NOT double-report', async () => {
    const { transport, bus, bridge, targetRef } = createFixture();
    transport.autoAck = false;
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const onError = vi.fn();
      const { unmount } = renderHook(() =>
        useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));
      await flush();
      act(() => transport.fail(transport.lastRequest().id, 'NO_HANDLER'));
      await act(async () => { await Promise.resolve(); });
      expect(consoleError).toHaveBeenCalledTimes(1);
      unmount();

      // …and a caller that owns its reporting is not ALSO logged, the rule the package's other three
      // sinks follow. Same failure, one more console call would mean the fallback fires either way.
      consoleError.mockClear();
      renderHook(() => useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z2', bridge, bus, onError }));
      await flush();
      act(() => transport.fail(transport.lastRequest().id, 'NO_HANDLER'));
      await act(async () => { await Promise.resolve(); });
      expect(onError).toHaveBeenCalledTimes(1);
      expect(consoleError).not.toHaveBeenCalled();
    } finally {
      consoleError.mockRestore();
    }
  });

  it('a stale REGISTER ack after teardown cannot mark the zone registered', async () => {
    // Regression (StrictMode's mount-unmount-remount): the in-flight REGISTER's ack landed
    // after the cleanup's UNREGISTER and marked the DESTROYED zone "registered" — no re-send,
    // overlay permanently missing. The epoch guard invalidates the stale ack.
    const transport = new FakeTransport();
    transport.autoAck = false; // hold acks so the REGISTER stays in flight through the teardown
    const bus = new ShenoraEventBus();
    const bridge = new ShenoraBridge({ transport, eventBus: bus });
    const element = document.createElement('div');
    document.body.appendChild(element);
    const targetRef = { current: element as HTMLElement | null };

    const { rerender } = renderHook(
      ({ enabled }: { enabled: boolean }) =>
        useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', enabled, bridge, bus }),
      { initialProps: { enabled: true } },
    );
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    rerender({ enabled: false }); // teardown while REGISTER is in flight
    await flush();
    expect(transport.routes()).toEqual(['REGISTER', 'UNREGISTER']);

    act(() => transport.ackAll()); // the stale ack arrives now — must be ignored
    rerender({ enabled: true });
    await flush();

    // The remount RE-SENT its registration instead of trusting the stale ack.
    expect(transport.routes()).toEqual(['REGISTER', 'UNREGISTER', 'REGISTER']);
  });

  it('tears the zone down when enabled flips false', async () => {
    const { transport, bus, bridge, targetRef } = createFixture();
    const { rerender } = renderHook(
      ({ enabled }: { enabled: boolean }) =>
        useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', enabled, bridge, bus }),
      { initialProps: { enabled: true } },
    );
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    rerender({ enabled: false });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER', 'UNREGISTER']);
  });

  /**
   * The zone MOVED, so the native overlay must follow — and it must NOT be told when nothing moved.
   *
   * Both directions are the test. The overlay is a separate native window positioned by these
   * numbers, so a broken UPDATE leaves it at the rectangle the element had on mount: drops land
   * somewhere the user is not pointing, or nowhere, with the page looking entirely correct. And a
   * missing `changed` guard is the opposite failure — this fires from a ResizeObserver, an
   * IntersectionObserver, every scroll (capturing) and every resize, so an unguarded sync posts an
   * IPC message per tick of a scroll.
   */
  it('sends UPDATE when the zone MOVES, and posts nothing when it has not', async () => {
    const { transport, bus, bridge, element, targetRef } = createFixture();
    renderHook(() => useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    // Unmoved: jsdom reports a zero rect, which is what REGISTER already carried.
    act(() => {
      window.dispatchEvent(new Event('resize'));
    });
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    element.getBoundingClientRect = () =>
      ({ left: 40.4, top: 12.6, width: 200, height: 100 }) as DOMRect;
    act(() => {
      window.dispatchEvent(new Event('resize'));
    });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER', 'UPDATE']);
    // Rounded, not truncated — the host places a native window at these pixels.
    expect(transport.posted[1]?.payload).toMatchObject({
      zoneId: 'z1', x: 40, y: 13, width: 200, height: 100,
    });
  });

  /**
   * ⚠ The overlay is re-armed from the PAGE on `mouseleave` and on the window losing focus, because
   * the native `MouseLeave` alone is unreliable through the WebView — the hook's own comment, and a
   * behaviour with no other witness. Lose these two listeners and drag-and-drop works exactly once
   * per page load: the overlay hides on the first hover-out and nothing ever shows it again.
   */
  it('re-arms the overlay on mouseleave and on the window losing focus', async () => {
    const { transport, bus, bridge, element, targetRef } = createFixture();
    renderHook(() => useDropZone({ targetRef, onDrop: () => {}, zoneId: 'z1', bridge, bus }));
    await flush();
    expect(transport.routes()).toEqual(['REGISTER']);

    act(() => {
      element.dispatchEvent(new MouseEvent('mouseleave'));
    });
    await flush();
    expect(transport.routes()).toEqual(['REGISTER', 'SHOW']);

    act(() => {
      window.dispatchEvent(new Event('blur'));
    });
    await flush();
    expect(transport.routes()).toEqual(['REGISTER', 'SHOW', 'SHOW']);
    expect(transport.posted[2]?.payload).toMatchObject({ zoneId: 'z1' });
  });

  it('🔴 generates its zone id ONCE, not on every render', async () => {
    // `useRef(newZoneId())` evaluates its argument on every render and keeps only the first, so the
    // generator ran a crypto.randomUUID() per render of every drop zone — invisible, since the value
    // was correct, and paid forever. Counted through `crypto.randomUUID` because that is where the
    // cost actually is.
    const { bridge, bus, targetRef } = createFixture();
    const randomUUID = vi.spyOn(crypto, 'randomUUID');

    try {
      const { rerender } = renderHook(() => useDropZone({ targetRef, onDrop: () => {}, bridge, bus }));
      await flush();
      const afterMount = randomUUID.mock.calls.length;

      rerender();
      rerender();
      rerender();
      await flush();

      // Re-renders must add nothing. (Not asserting the mount count itself — other ids are minted
      // during a mount, and pinning that number would break for reasons unrelated to this defect.)
      expect(randomUUID.mock.calls.length).toBe(afterMount);
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('the id a later message carries is still the id it REGISTERED under', async () => {
    // ⚠ The behavioural half, and it has to reach a LIVE read to mean anything. An earlier version
    // asserted on `data-drop-zone-id` and the REGISTER count after a rerender — both are written by
    // effects that do not re-run on a plain rerender, so they hold the first id no matter what the ref
    // does. Measured: a "mint a fresh id every render" implementation passed that version untouched.
    //
    // SHOW is different: its handler reads the ref when the mouse leaves, long after the effects ran.
    // So a drifting id surfaces exactly where it would hurt — the host receiving SHOW for a zone it
    // never registered, and simply not showing the overlay.
    const { transport, bridge, bus, targetRef, element } = createFixture();

    const { rerender } = renderHook(() => useDropZone({ targetRef, onDrop: () => {}, bridge, bus }));
    await flush();
    const registered = (transport.posted[0]?.payload as { zoneId: string }).zoneId;

    rerender();
    rerender();
    await flush();

    act(() => {
      element.dispatchEvent(new MouseEvent('mouseleave'));
    });
    await flush();

    expect(transport.routes()).toEqual(['REGISTER', 'SHOW']);
    expect(transport.posted[1]?.payload).toMatchObject({ zoneId: registered });
    expect(element.getAttribute('data-drop-zone-id')).toBe(registered);
  });
});
