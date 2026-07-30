// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { useShenora, useShenoraEvent, useShenoraQuery } from './hooks.js';
import type { ShenoraTransport } from './transport.js';
import type { IpcRequest } from './types.js';

class FakeTransport implements ShenoraTransport {
  posted: IpcRequest[] = [];
  private listener?: (message: string) => void;

  post(message: string): void {
    this.posted.push(JSON.parse(message) as IpcRequest);
  }

  subscribe(listener: (message: string) => void): () => void {
    this.listener = listener;
    return () => {
      this.listener = undefined;
    };
  }

  respond(id: string, data: unknown): void {
    this.listener?.(JSON.stringify({ category: 'ipc', id, success: true, data }));
  }

  fail(id: string, code: string): void {
    this.listener?.(JSON.stringify({ category: 'ipc', id, success: false, error: { code } }));
  }
}

describe('useShenora', () => {
  it('reports availability and hands out the bridge', () => {
    const { result } = renderHook(() => useShenora());

    expect(result.current.bridge).toBeInstanceOf(ShenoraBridge);
    expect(result.current.isAvailable).toBe(false); // jsdom has no chrome.webview
  });
});

describe('useShenoraEvent', () => {
  it('subscribes for the component lifetime and always calls the LATEST handler', () => {
    const bus = new ShenoraEventBus();
    const received: string[] = [];

    const { rerender, unmount } = renderHook(
      ({ label }: { label: string }) =>
        useShenoraEvent<number>('APP', 'TICK', (payload) => received.push(`${label}:${payload}`), { bus }),
      { initialProps: { label: 'first' } },
    );

    act(() => bus.emit({ module: 'APP', type: 'TICK', payload: 1 }));
    rerender({ label: 'second' }); // handler identity changed — must NOT resubscribe, must use the new one
    act(() => bus.emit({ module: 'APP', type: 'TICK', payload: 2 }));

    expect(received).toEqual(['first:1', 'second:2']);
    expect(bus.getSubscriptionCount('APP', 'TICK')).toBe(1);

    unmount();
    expect(bus.getSubscriptionCount('APP', 'TICK')).toBe(0);
  });
});

describe('useShenoraQuery', () => {
  function createBridge() {
    const transport = new FakeTransport();
    const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
    return { transport, bridge };
  }

  it('loads data and refetches on demand', async () => {
    const { transport, bridge } = createBridge();
    const { result } = renderHook(() => useShenoraQuery<{ n: number }>('APP', 'GET', { bridge }));

    expect(result.current.loading).toBe(true);
    act(() => transport.respond(transport.posted[0]!.id, { n: 1 }));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.data).toEqual({ n: 1 });

    act(() => result.current.refetch());
    await waitFor(() => expect(transport.posted.length).toBe(2));
    act(() => transport.respond(transport.posted[1]!.id, { n: 2 }));
    await waitFor(() => expect(result.current.data).toEqual({ n: 2 }));
  });

  it('keeps the previous data when a refetch fails', async () => {
    // It used to set `data: undefined` on any error, so one transient host hiccup blanked data the UI
    // was already showing correctly — turning a recoverable error into an empty screen (P5.5 H2). The
    // caller gets both fields and decides: stale data with an error banner, or hide it.
    const { transport, bridge } = createBridge();
    const { result } = renderHook(() => useShenoraQuery<{ n: number }>('APP', 'GET', { bridge }));

    act(() => transport.respond(transport.posted[0]!.id, { n: 1 }));
    await waitFor(() => expect(result.current.data).toEqual({ n: 1 }));

    act(() => result.current.refetch());
    await waitFor(() => expect(transport.posted.length).toBe(2));
    act(() => transport.fail(transport.posted[1]!.id, 'GET_FAILED'));

    await waitFor(() => expect(result.current.error).toMatchObject({ code: 'GET_FAILED' }));
    expect(result.current.data).toEqual({ n: 1 }); // still there
    expect(result.current.loading).toBe(false);
  });

  it('surfaces structured errors', async () => {
    const { transport, bridge } = createBridge();
    const { result } = renderHook(() => useShenoraQuery('APP', 'GET', { bridge }));

    act(() => transport.fail(transport.posted[0]!.id, 'GET_FAILED'));

    await waitFor(() => expect(result.current.error).toMatchObject({ code: 'GET_FAILED' }));
    expect(result.current.data).toBeUndefined();
    expect(result.current.loading).toBe(false);
  });

  it('does not fetch while disabled', () => {
    const { transport, bridge } = createBridge();
    renderHook(() => useShenoraQuery('APP', 'GET', { bridge, enabled: false }));

    expect(transport.posted).toHaveLength(0);
  });

  it('clears loading when enabled flips false mid-flight', async () => {
    const { bridge } = createBridge();
    const { result, rerender } = renderHook(
      ({ enabled }: { enabled: boolean }) => useShenoraQuery('APP', 'GET', { bridge, enabled }),
      { initialProps: { enabled: true } },
    );
    expect(result.current.loading).toBe(true);

    rerender({ enabled: false }); // fetch still in flight — must not leave a forever-spinner

    await waitFor(() => expect(result.current.loading).toBe(false));
  });
});
