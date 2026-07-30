// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge';
import { ShenoraEventBus } from './eventBus';
import type { ShenoraTransport } from './transport';
import type { IpcRequest } from './types';
import { DROP_ZONE_MODULE, useDropZone } from './useDropZone';

class FakeTransport implements ShenoraTransport {
  posted: IpcRequest[] = [];
  autoAck = true;
  private listener?: (message: string) => void;
  private unacked: string[] = [];

  post(message: string): void {
    const request = JSON.parse(message) as IpcRequest;
    this.posted.push(request);
    if (this.autoAck) {
      queueMicrotask(() =>
        this.listener?.(JSON.stringify({ category: 'ipc', id: request.id, success: true })));
    } else {
      this.unacked.push(request.id);
    }
  }

  ackAll(): void {
    for (const id of this.unacked.splice(0))
      this.listener?.(JSON.stringify({ category: 'ipc', id, success: true }));
  }

  subscribe(listener: (message: string) => void): () => void {
    this.listener = listener;
    return () => {
      this.listener = undefined;
    };
  }

  routes(): string[] {
    return this.posted.map((r) => r.type);
  }
}

function createFixture() {
  const transport = new FakeTransport();
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
});
