import { afterEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge, configureBridge, getBridge } from './bridge.js';
import { ShenoraError } from './errors.js';
import { ShenoraEventBus } from './eventBus.js';
import { IpcCategories, HANDSHAKE_MODULE, HANDSHAKE_TYPE, IpcErrorCodes, type EventMessage, type IpcRequest } from './types.js';
import { FakeTransport } from './testing/fakeTransport.js';

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
    transport.emitFromHost({ category: IpcCategories.ipc, id: transport.lastRequest().id, success: true, data: { count: 2 } });

    await expect(promise).resolves.toEqual({ count: 2 });
  });

  it('rejects a failed response as a structured ShenoraError', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'FAIL');
    transport.emitFromHost({
      category: IpcCategories.ipc,
      id: transport.lastRequest().id,
      success: false,
      error: { code: 'IMPORT_FAILED', parameters: { name: 'x' } },
    });

    const error = await promise.then(
      () => { throw new Error('should have rejected'); },
      (e: unknown) => e as ShenoraError,
    );
    expect(error).toBeInstanceOf(ShenoraError);
    expect(error.code).toBe('IMPORT_FAILED');
    expect(error.parameters).toEqual({ name: 'x' });
  });

  it('rejects UNKNOWN_ERROR when a failed response has no error object', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'FAIL');
    transport.emitFromHost({ category: IpcCategories.ipc, id: transport.lastRequest().id, success: false });

    await expect(promise).rejects.toMatchObject({ code: IpcErrorCodes.unknownError });
  });

  it('times out with the TIMEOUT code, and a late response is ignored', async () => {
    vi.useFakeTimers();
    const { transport, bridge } = createBridge();

    const promise = bridge.invoke('APP', 'SLOW', { timeoutMs: 100 });
    const id = transport.lastRequest().id;
    vi.advanceTimersByTime(101);

    await expect(promise).rejects.toMatchObject({ code: IpcErrorCodes.timeout });
    expect(() => transport.emitFromHost({ category: IpcCategories.ipc, id, success: true })).not.toThrow();
  });

  it('unbundles notification batches into the event bus in order', () => {
    const { transport, bus } = createBridge();
    const received: EventMessage[] = [];
    bus.subscribe('APP', 'TICK', (event) => received.push(event));

    transport.emitFromHost({
      category: IpcCategories.notification,
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
    expect(() => transport.emitFromHost({ category: IpcCategories.ipc })).not.toThrow(); // no id
  });

  it('survives a host message that is a bare JSON value', () => {
    const { transport } = createBridge();

    // `null` is VALID JSON, so it survived the parse and then `parsed.category` threw a TypeError out
    // of a transport listener — an uncaught page error, with no caller to catch it (P5.5 H2). The other
    // primitives never threw (property access on them just yields undefined); null and only null did.
    expect(() => transport.emitFromHost('null')).not.toThrow();
    expect(() => transport.emitFromHost('123')).not.toThrow();
    expect(() => transport.emitFromHost('"a string"')).not.toThrow();
    expect(() => transport.emitFromHost('true')).not.toThrow();
    expect(() => transport.emitFromHost('[]')).not.toThrow();
  });

  it('isAvailable turns false once disposed', () => {
    const { bridge } = createBridge();
    expect(bridge.isAvailable).toBe(true);

    bridge.dispose();

    // It used to keep reporting true while every invoke rejected with NO_TRANSPORT — the exact state a
    // stale reference is in after configureBridge replaced (and disposed) the default.
    expect(bridge.isAvailable).toBe(false);
  });

  it('a fallback that never settles still times out', async () => {
    // This path used to bypass the timeout entirely, so a hanging async fallback (a scripted preview
    // harness is commonly async) hung the caller forever with none of the real path's diagnostics.
    const bridge = new ShenoraBridge({
      transport: null,
      eventBus: new ShenoraEventBus(),
      fallback: () => new Promise(() => { /* never settles */ }),
    });

    await expect(bridge.invoke('NOTES', 'GET_ALL', { timeoutMs: 20 }))
      .rejects.toMatchObject({ code: IpcErrorCodes.timeout });
  });

  it('a synchronous fallback value is returned without waiting', async () => {
    // Only a thenable needs racing — a plain value has already settled, so the timeout must not make
    // this path async or slower.
    const bridge = new ShenoraBridge({
      transport: null,
      eventBus: new ShenoraEventBus(),
      fallback: () => ({ ok: true }),
    });

    await expect(bridge.invoke('NOTES', 'GET_ALL', { timeoutMs: 20 })).resolves.toEqual({ ok: true });
  });

  it('notifyReady sends the reserved handshake route', async () => {
    const { transport, bridge } = createBridge();

    const promise = bridge.notifyReady({ clientId: 'w1' });
    const request = transport.lastRequest<{ clientId: string }>();
    expect(request.module).toBe(HANDSHAKE_MODULE);
    expect(request.type).toBe(HANDSHAKE_TYPE);
    expect(request.payload).toEqual({ clientId: 'w1' });

    transport.emitFromHost({ category: IpcCategories.ipc, id: request.id, success: true });
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

    await expect(promise).rejects.toBeInstanceOf(ShenoraError);
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

  describe('post (one-way)', () => {
    it('sends the SAME envelope as invoke, with no pending call and no timer', () => {
      vi.useFakeTimers();
      const { transport, bridge } = createBridge();

      const id = bridge.post('DEPLOY', 'START', { payload: { env: 'prod' }, scope: 's1' });

      const request = transport.lastRequest<{ env: string }>();
      expect(request).toMatchObject({ id, module: 'DEPLOY', type: 'START', scope: 's1', payload: { env: 'prod' } });
      // No wire change is the whole point: a transport (or the host) cannot tell this from an invoke.
      expect(typeof request.timestamp).toBe('string');

      // Nothing is queued and nothing is scheduled — so there is no leak and no deadline. If a timer
      // had been set, advancing past the default timeout would fire it.
      expect(vi.getTimerCount()).toBe(0);
      vi.advanceTimersByTime(60_000);
    });

    it('reports a FAILED response instead of dropping it silently', () => {
      const onPostError = vi.fn();
      const transport = new FakeTransport();
      const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus(), onPostError });

      const id = bridge.post('DEPLOY', 'START');
      transport.fail(id, 'DEPLOY_REFUSED');

      // There is no promise to reject, and an unmatched response is otherwise dropped by the inbound
      // handler — which is exactly how a feature "just stops working" with nothing to grep for.
      expect(onPostError).toHaveBeenCalledTimes(1);
      expect(onPostError).toHaveBeenCalledWith({
        module: 'DEPLOY',
        type: 'START',
        id,
        error: { code: 'DEPLOY_REFUSED' },
      });
    });

    it('stays quiet on a successful response', () => {
      const onPostError = vi.fn();
      const transport = new FakeTransport();
      const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus(), onPostError });

      const id = bridge.post('DEPLOY', 'START');
      transport.respond(id, { ok: true });

      expect(onPostError).not.toHaveBeenCalled();
    });

    it('caps the ids it remembers so a silent host cannot grow them without bound', () => {
      const onPostError = vi.fn();
      const transport = new FakeTransport();
      const bridge = new ShenoraBridge({
        transport, eventBus: new ShenoraEventBus(), onPostError, maxTrackedPosts: 2,
      });

      const first = bridge.post('DEPLOY', 'START');
      bridge.post('DEPLOY', 'START');
      bridge.post('DEPLOY', 'START'); // evicts `first` (drop-oldest)

      transport.fail(first, 'TOO_LATE');

      // The trade, stated: an evicted id loses its error report. That is strictly better than an
      // unbounded set, and still better than today's behaviour for every call that fits.
      expect(onPostError).not.toHaveBeenCalled();
    });

    it('is a silent no-op with no transport, unlike invoke', () => {
      const bridge = new ShenoraBridge({ transport: null, eventBus: new ShenoraEventBus() });

      // Fire-and-forget has no caller waiting to be told, so a plain browser tab must not throw —
      // `invoke` rejects with NO_TRANSPORT precisely because someone IS waiting.
      expect(() => bridge.post('DEPLOY', 'START')).not.toThrow();
    });
  });
});
