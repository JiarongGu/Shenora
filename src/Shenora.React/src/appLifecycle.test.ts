// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ShenoraEventBus } from './eventBus.js';
import {
  LIFECYCLE_MODULE,
  LIFECYCLE_RESUMED,
  LIFECYCLE_STOPPED,
  useAppLifecycle,
} from './appLifecycle.js';

function emit(bus: ShenoraEventBus, type: string, payload?: unknown) {
  act(() => {
    bus.emit({ module: LIFECYCLE_MODULE, type, payload });
  });
}

describe('useAppLifecycle', () => {
  it('reports how long the app was away', () => {
    const bus = new ShenoraEventBus();
    const onResumed = vi.fn();
    renderHook(() => useAppLifecycle({ onResumed }, { eventBus: bus }));

    emit(bus, LIFECYCLE_RESUMED, { backgroundMilliseconds: 42_000 });

    expect(onResumed).toHaveBeenCalledWith({ backgroundMilliseconds: 42_000 });
  });

  it('keeps a MISSING duration as null rather than collapsing it to zero', () => {
    // 🔴 The distinction the host is careful about must survive the client too. A page whose rule is
    // `away > 30s → reconnect` and that receives 0 for "nothing to measure" skips the reconnect
    // exactly at startup — the one moment its socket certainly does not exist.
    const bus = new ShenoraEventBus();
    const onResumed = vi.fn();
    renderHook(() => useAppLifecycle({ onResumed }, { eventBus: bus }));

    emit(bus, LIFECYCLE_RESUMED, { backgroundMilliseconds: null });
    emit(bus, LIFECYCLE_RESUMED, {});
    emit(bus, LIFECYCLE_RESUMED);

    expect(onResumed).toHaveBeenCalledTimes(3);
    for (const call of onResumed.mock.calls) {
      expect(call[0]).toEqual({ backgroundMilliseconds: null });
    }
  });

  it('reports the stop as well, with no payload to read', () => {
    const bus = new ShenoraEventBus();
    const onStopped = vi.fn();
    renderHook(() => useAppLifecycle({ onStopped }, { eventBus: bus }));

    emit(bus, LIFECYCLE_STOPPED);

    expect(onStopped).toHaveBeenCalledOnce();
  });

  it('uses the LATEST handler without resubscribing', () => {
    // Same reason as the back gesture's: an inline arrow is a new function every render, so depending
    // on it would tear the subscription down and rebuild it — losing any transition in the gap.
    const bus = new ShenoraEventBus();
    const first = vi.fn();
    const second = vi.fn();
    let handler = first;
    const view = renderHook(() => useAppLifecycle({ onStopped: () => handler() }, { eventBus: bus }));

    handler = second;
    view.rerender();
    emit(bus, LIFECYCLE_STOPPED);

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledOnce();
  });

  it('stops listening on unmount', () => {
    const bus = new ShenoraEventBus();
    const onStopped = vi.fn();
    const view = renderHook(() => useAppLifecycle({ onStopped }, { eventBus: bus }));

    view.unmount();
    emit(bus, LIFECYCLE_STOPPED);

    expect(onStopped).not.toHaveBeenCalled();
  });

  it('survives a page that supplied no handler at all', () => {
    // `onStopped?.()` — a page listening for only one of the two must not throw on the other.
    const bus = new ShenoraEventBus();
    renderHook(() => useAppLifecycle({}, { eventBus: bus }));

    expect(() => {
      emit(bus, LIFECYCLE_STOPPED);
      emit(bus, LIFECYCLE_RESUMED, { backgroundMilliseconds: 1 });
    }).not.toThrow();
  });
});
