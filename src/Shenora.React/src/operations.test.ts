import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { createOperationsStore } from './operations.js';
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
  const store = createOperationsStore({ bridge, bus: realBus, ...storeOptions });

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

const info = (over: Partial<{ id: string; status: string; progress: number }>) => ({
  id: 'op-1', module: 'DEPLOY', kind: 'PUSH', status: 'running', ...over,
});

describe('operations store', () => {
  it('loads the snapshot on first subscribe', async () => {
    const { store, bridge } = harness([info({}), info({ id: 'op-2' })]);
    store.subscribe(() => {});
    await bridge.settled();

    expect(Object.keys(store.getState().byId)).toEqual(['op-1', 'op-2']);
  });

  it('folds an update by id — last write wins', async () => {
    const { store, bus } = harness([info({})]);
    store.subscribe(() => {});

    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: 40 }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: 80 }));

    expect(store.getState().byId['op-1']!.progress).toBe(80);
    expect(Object.keys(store.getState().byId)).toHaveLength(1);
  });

  it('exposes running work separately from finished history', async () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({}));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));

    expect(store.getState().running.map((o) => o.id)).toEqual(['op-1']);
  });

  it('binds to a renamed module for both the snapshot request and the event subscription', () => {
    // The default-bound store can never reach a host that renamed OperationRegistryOptions.ModuleName
    // (review finding): `module` must flow into both halves, not just be accepted and ignored.
    const { store, transport, bus } = harness([], { module: 'MY_OPS' });
    store.subscribe(() => {});

    expect(transport.lastRequest().module).toBe('MY_OPS');

    // A delta on the DEFAULT module must not reach a store bound to the renamed one, and vice versa.
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'wrong-module' }));
    bus.emit('MY_OPS', 'OPERATION_UPDATED', info({ id: 'op-2' }));

    expect(store.getState().byId['wrong-module']).toBeUndefined();
    expect(store.getState().byId['op-2']).toBeDefined();
  });

  it('threads scope into the LIST snapshot payload, not just the subscription', () => {
    // OperationsFacade reads its scope filter from the PAYLOAD (see RouteMessageAsync), not the
    // envelope — asserting on the resulting state would pass even if the payload were empty, since
    // an unfiltered LIST returns a superset that just happens to still contain the scoped rows.
    const { store, transport } = harness([], { scope: 'tenant-1' });
    store.subscribe(() => {});

    const request = transport.lastRequest<{ scope?: string }>();
    expect(request.scope).toBe('tenant-1');
    expect(request.payload).toEqual({ scope: 'tenant-1' });
  });

  it('clearFinished prunes terminal entries from local state immediately, optimistically', () => {
    // FINDING 3 (Important, whole-branch review): the host removes finished entries on
    // CLEAR_FINISHED but never emits a removal delta (OPERATION_UPDATED only ever adds/updates an
    // id), so without a local prune a mounted panel kept rendering the cleared rows until every
    // subscriber unmounted and the store was rebuilt from a fresh LIST.
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));

    store.actions.clearFinished();

    expect(Object.keys(store.getState().byId)).toEqual(['op-1']);
    expect(store.getState().finished).toEqual([]);
  });

  it('resume drops the resumed id from local state immediately, optimistically', () => {
    // Same shape as clearFinished: RequestResume removes the offer host-side but emits no delta for
    // it either, so the offer stayed clickable in a mounted store until unmount — a second click on
    // it silently did nothing.
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'interrupted' }));

    store.actions.resume('op-1');

    expect(store.getState().byId['op-1']).toBeUndefined();
  });

  it('keeps an interrupted operation out of both running and finished', () => {
    // `finished` deliberately excludes `interrupted` (a pending resume offer, not terminal history) —
    // an undocumented carve-out with no coverage is how a later cleanup silently changes behaviour.
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'interrupted' }));

    expect(store.getState().running).toEqual([]);
    expect(store.getState().finished).toEqual([]);
    expect(store.getState().byId['op-1']).toBeDefined();
  });
});
