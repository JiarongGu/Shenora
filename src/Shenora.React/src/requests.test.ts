import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { createRequestsStore, IpcRequestStates } from './requests.js';
import { FakeTransport } from './testing/fakeTransport.js';

/**
 * Fake bridge/bus wiring, following `store.test.ts`'s `fixture()` pattern — reuse the shipped
 * `ShenoraBridge`/`ShenoraEventBus`/`FakeTransport`, no new transport double. Unlike `store.test.ts`
 * this store is exercised directly (`subscribe`/`getState`), never through a rendered component, so
 * there is no `act()` to wrap: `ShenoraStore` supports non-React callers by design.
 */
function harness(initialList: unknown[], storeOptions: { module?: string; scope?: string } = {}) {
  const transport = new FakeTransport();
  const realBus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: realBus });
  const store = createRequestsStore({ bridge, bus: realBus, ...storeOptions });

  return {
    store,
    /** Raw access for asserting on what the fake bridge actually RECEIVED, not just resulting state. */
    transport,
    bus: {
      /** Emit straight to the bus, bypassing the wire — the store only cares that it subscribed. */
      emit: (module: string, type: string, payload: unknown): void => {
        realBus.emit({ module, type, payload });
      },
    },
    bridge: {
      /**
       * Answer the pending `LIST` snapshot request with `initialList`, then wait for the store to
       * fold the response. Deterministic on the microtask queue, not a fixed timer: the snapshot
       * request is posted SYNCHRONOUSLY inside `subscribe` (P5.5 H2's double-mount guard runs
       * before any await), so by the time this runs the request is already in `transport.posted`.
       */
      settled: (): Promise<void> =>
        new Promise((resolve) => {
          queueMicrotask(() => {
            const request = transport.posted.find((r) => r.type === 'LIST');
            if (request) transport.respond(request.id, initialList);
            // One more hop: `respond` resolves the snapshot's `invoke` promise, and that promise's
            // `.then` (which folds the response into state) runs as its OWN microtask — scheduled
            // before this one, since it was enqueued first in this same synchronous turn.
            queueMicrotask(resolve);
          });
        }),
    },
  };
}

const info = (over: Partial<{ id: string; state: string; progress: { value: number; total?: number; unit?: string } }>) => ({
  id: 'op-1', module: 'DEPLOY', type: 'PUSH', state: 'running', ...over,
});

describe('requests store', () => {
  it('loads the snapshot on first subscribe', async () => {
    const { store, bridge } = harness([info({}), info({ id: 'op-2' })]);
    store.subscribe(() => {});
    await bridge.settled();

    expect(Object.keys(store.getState().byId)).toEqual(['op-1', 'op-2']);
  });

  it('folds an update by id — last write wins', async () => {
    const { store, bus } = harness([info({})]);
    store.subscribe(() => {});

    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ progress: { value: 40 } }));
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ progress: { value: 80 } }));

    expect(store.getState().byId['op-1']!.progress).toEqual({ value: 80 });
    expect(Object.keys(store.getState().byId)).toHaveLength(1);
  });

  it('exposes running work separately from finished history', async () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({}));
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: 'op-2', state: 'completed' }));

    expect(store.getState().running.map((o) => o.id)).toEqual(['op-1']);
  });

  it('binds to a renamed module for both the snapshot request and the event subscription', () => {
    // The default-bound store can never reach a host that renamed OperationRegistryOptions.ModuleName
    // (review finding): `module` must flow into both halves, not just be accepted and ignored.
    const { store, transport, bus } = harness([], { module: 'MY_OPS' });
    store.subscribe(() => {});

    expect(transport.lastRequest().module).toBe('MY_OPS');

    // A delta on the DEFAULT module must not reach a store bound to the renamed one, and vice versa.
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: 'wrong-module' }));
    bus.emit('MY_OPS', 'REQUEST_UPDATED', info({ id: 'op-2' }));

    expect(store.getState().byId['wrong-module']).toBeUndefined();
    expect(store.getState().byId['op-2']).toBeDefined();
  });

  it('threads scope into the LIST snapshot payload, not just the subscription', () => {
    // OperationsModule reads its scope filter from the PAYLOAD (see RouteMessageAsync), not the
    // envelope — asserting on the resulting state would pass even if the payload were empty, since
    // an unfiltered LIST returns a superset that just happens to still contain the scoped rows.
    const { store, transport } = harness([], { scope: 'tenant-1' });
    store.subscribe(() => {});

    const request = transport.lastRequest<{ scope?: string }>();
    expect(request.scope).toBe('tenant-1');
    expect(request.payload).toEqual({ scope: 'tenant-1' });
  });

  /**
   * FINDING 1 (Critical, generic-library audit): `clearFinished` used to post `CLEAR_FINISHED` with
   * no payload at all, even though the host route now reads the same `scope` key `LIST` does — so a
   * scope-filtered store's "clear completed" silently cleared every OTHER scope's finished history
   * on the host, even though this store's own local prune only ever touched its own rows.
   */
  it('clearFinished forwards this stores configured scope in the payload', () => {
    const { store, transport } = harness([], { scope: 'tenant-1' });
    store.subscribe(() => {});

    store.actions.clearFinished();

    const request = transport.lastRequest<{ scope?: string }>();
    expect(request.type).toBe('CLEAR_FINISHED');
    expect(request.payload).toEqual({ scope: 'tenant-1' });
  });

  /**
   * FINDING 4 (Important, generic-library audit): removals used to have NO wire event at all
   * (`OPERATION_UPDATED` only ever adds/updates an id), so `clearFinished`/`resume` each carried a
   * hand-written optimistic local prune to guess at what the host had removed — one of which
   * (`resume`) shipped with a bug this very release (pruning a `paused` row the host deliberately
   * keeps). `REQUEST_REMOVED` replaces both guesses with one authoritative event: the client folds
   * it by deleting exactly the named ids, regardless of their status — the host decided, not a local
   * status rule.
   */
  it('REQUEST_REMOVED deletes the named ids from local state, regardless of status', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ state: 'running' }));
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: 'op-2', state: 'completed' }));
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: 'op-3', state: 'cancelled' }));

    bus.emit('SHENORA.REQUESTS', 'REQUEST_REMOVED', { requestIds: ['op-2', 'op-3'] });

    expect(Object.keys(store.getState().byId)).toEqual(['op-1']);
  });

  /** An id the store never had is a harmless no-op — the same shape as deleting an absent key. */
  it('REQUEST_REMOVED naming an unknown id is a harmless no-op', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({}));

    bus.emit('SHENORA.REQUESTS', 'REQUEST_REMOVED', { requestIds: ['no-such-id'] });

    expect(Object.keys(store.getState().byId)).toEqual(['op-1']);
  });

  /**
   * `clearFinished`/`resume` no longer locally mutate state at all (Finding 4) — the host's
   * `REQUEST_REMOVED` is the ONLY thing that removes a row now, so calling either action must not
   * change anything by itself, however the host eventually answers.
   */
  it('clearFinished does not locally mutate state — only the hosts REQUEST_REMOVED does', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ state: 'running' }));
    bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: 'op-2', state: 'completed' }));

    store.actions.clearFinished();

    expect(Object.keys(store.getState().byId).sort()).toEqual(['op-1', 'op-2']);
  });

  it('every request state belongs to exactly one band: in flight or finished', () => {
    const states = Object.values(IpcRequestStates);
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    states.forEach((state, i) => {
      bus.emit('SHENORA.REQUESTS', 'REQUEST_UPDATED', info({ id: `req-${i}`, state }));
    });

    const snapshot = store.getState();
    const bandsOf = (id: string): string[] => {
      const bands: string[] = [];
      if (snapshot.running.some((r: { id: string }) => r.id === id)) bands.push('running');
      if (snapshot.finished.some((r: { id: string }) => r.id === id)) bands.push('finished');
      return bands;
    };

    states.forEach((state, i) => {
      const bands = bandsOf(`req-${i}`);
      expect(bands, `state '${state}' landed in bands [${bands.join(', ')}], expected exactly 1`).toHaveLength(1);
    });
  });

});
