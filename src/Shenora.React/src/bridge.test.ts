import { afterEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge, configureBridge, getBridge } from './bridge.js';
import { OperationError } from './errors.js';
import { ShenoraEventBus } from './eventBus.js';
import type { ShenoraTransport } from './transport.js';
import { HANDSHAKE_MODULE, HANDSHAKE_TYPE, IpcErrorCodes, type EventMessage, type IpcRequest } from './types.js';

class FakeTransport implements ShenoraTransport {
  posted: string[] = [];
  private listener?: (message: string) => void;

  post(message: string): void {
    this.posted.push(message);
  }

  subscribe(listener: (message: string) => void): () => void {
    this.listener = listener;
    return () => {
      this.listener = undefined;
    };
  }

  get subscribed(): boolean {
    return this.listener !== undefined;
  }

  emitFromHost(message: unknown): void {
    this.listener?.(typeof message === 'string' ? message : JSON.stringify(message));
  }

  lastRequest<T = unknown>(): IpcRequest<T> {
    return JSON.parse(this.posted.at(-1)!) as IpcRequest<T>;
  }
}

function createBridge(options: { fallback?: (request: IpcRequest) => unknown } = {}) {
  const transport = new FakeTransport();
  const bus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: bus, ...options });
  return { transport, bus, bridge };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('ShenoraBridge', () => {
  it('posts the wire shape', () => {
    const { transport, bridge } = createBridge();

    void bridge.invoke('APP', 'PING', { scope: 's1', payload: { name: 'x' } });

    const request = transport.lastRequest<{ name: string }>();
    expect(request.module).toBe('APP');
    expect(request.type).toBe('PING');
    expect(request.scope).toBe('s1');
    expect(request.payload).toEqual({ name: 'x' });
    expect(request.id).toBeTruthy();
    expect(new Date(request.timestamp).getTime()).not.toBeNaN();
  });

  it('resolves with the response data', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke<{ count: number }>('APP', 'PING');
    transport.emitFromHost({ category: 'ipc', id: transport.lastRequest().id, success: true, data: { count: 2 } });

    await expect(promise).resolves.toEqual({ count: 2 });
  });

  it('rejects a failed response as a structured OperationError', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'FAIL');
    transport.emitFromHost({
      category: 'ipc',
      id: transport.lastRequest().id,
      success: false,
      error: { code: 'IMPORT_FAILED', parameters: { name: 'x' } },
    });

    const error = await promise.then(
      () => { throw new Error('should have rejected'); },
      (e: unknown) => e as OperationError,
    );
    expect(error).toBeInstanceOf(OperationError);
    expect(error.code).toBe('IMPORT_FAILED');
    expect(error.parameters).toEqual({ name: 'x' });
  });

  it('rejects UNKNOWN_ERROR when a failed response has no error object', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'FAIL');
    transport.emitFromHost({ category: 'ipc', id: transport.lastRequest().id, success: false });

    await expect(promise).rejects.toMatchObject({ code: IpcErrorCodes.unknownError });
  });

  it('times out with the TIMEOUT code, and a late response is ignored', async () => {
    vi.useFakeTimers();
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'SLOW', { timeoutMs: 100 });
    const id = transport.lastRequest().id;
    vi.advanceTimersByTime(101);

    await expect(promise).rejects.toMatchObject({ code: IpcErrorCodes.timeout });
    expect(() => transport.emitFromHost({ category: 'ipc', id, success: true })).not.toThrow();
  });

  it('unbundles notification batches into the event bus in order', () => {
    const { transport, bus } = createBridge();
    const received: EventMessage[] = [];
    bus.subscribe('APP', 'TICK', (event) => received.push(event));

    transport.emitFromHost({
      category: 'notification',
      id: 'b1',
      timestamp: new Date().toISOString(),
      payload: [
        { module: 'APP', type: 'TICK', payload: 1 },
        { module: 'APP', type: 'TICK', payload: 2, scope: 's1' },
        { module: 'OTHER', type: 'TICK', payload: 3 },
      ],
    });

    expect(received.map((event) => event.payload)).toEqual([1, 2]);
    expect(received[1]?.scope).toBe('s1');
  });

  it('ignores malformed and foreign host messages', () => {
    const { transport } = createBridge();

    expect(() => transport.emitFromHost('not json')).not.toThrow();
    expect(() => transport.emitFromHost({ category: 'something-else', id: 'x' })).not.toThrow();
    expect(() => transport.emitFromHost({ category: 'ipc' })).not.toThrow(); // no id
  });

  it('notifyReady sends the reserved handshake route', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.notifyReady({ clientId: 'w1' });
    const request = transport.lastRequest<{ clientId: string }>();
    expect(request.module).toBe(HANDSHAKE_MODULE);
    expect(request.type).toBe(HANDSHAKE_TYPE);
    expect(request.payload).toEqual({ clientId: 'w1' });

    transport.emitFromHost({ category: 'ipc', id: request.id, success: true });
    await expect(promise).resolves.toBeUndefined();
  });

  it('uses the fallback when no transport exists', async () => {
    const seen: IpcRequest[] = [];
    const bridge = new ShenoraBridge({
      transport: null,
      eventBus: new ShenoraEventBus(),
      fallback: (request) => {
        seen.push(request);
        return { mocked: true };
      },
    });

    expect(bridge.isAvailable).toBe(false);
    await expect(bridge.invoke('APP', 'PING', { payload: 1 })).resolves.toEqual({ mocked: true });
    expect(seen[0]?.module).toBe('APP');
    await expect(bridge.notifyReady()).resolves.toBeUndefined(); // silent no-op without transport
  });

  it('rejects NO_TRANSPORT without transport or fallback', async () => {
    const bridge = new ShenoraBridge({ transport: null, eventBus: new ShenoraEventBus() });

    await expect(bridge.invoke('APP', 'PING')).rejects.toMatchObject({ code: IpcErrorCodes.noTransport });
  });

  it('dispose rejects everything in flight and detaches', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'PING');
    bridge.dispose();

    await expect(promise).rejects.toBeInstanceOf(OperationError);
    expect(transport.subscribed).toBe(false);
  });

  it('invoke after dispose fails fast instead of burning the timeout', async () => {
    const { bridge } = createBridge();
    bridge.dispose();

    // No fake timers on purpose: the rejection must be immediate, not timeout-driven.
    await expect(bridge.invoke('APP', 'PING')).rejects.toMatchObject({ code: IpcErrorCodes.noTransport });
  });

  it('getBridge is a lazy singleton that configureBridge replaces', () => {
    const first = getBridge();
    expect(getBridge()).toBe(first);

    const configured = configureBridge({ transport: null, eventBus: new ShenoraEventBus() });
    expect(getBridge()).toBe(configured);
    expect(getBridge()).not.toBe(first);
  });
});
