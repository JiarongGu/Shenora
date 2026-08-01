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

  /**
   * Pin, don't assume (§5A.2, D23 amendment): clearFinished's optimistic local prune uses the
   * TERMINAL set, so it must not be able to remove a `paused` OR `interrupted` entry — both are the
   * WAITING band, "not history" by design, and this is exactly the kind of thing a later
   * "simplification" (e.g. pruning everything that isn't literally 'running') silently breaks.
   */
  it('clearFinished does not remove a paused or interrupted entry — the WAITING band is not history', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-3', status: 'paused' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-4', status: 'interrupted' }));

    store.actions.clearFinished();

    expect(Object.keys(store.getState().byId).sort()).toEqual(['op-3', 'op-4']);
    expect(store.getState().paused.map((o) => o.id)).toEqual(['op-3']);
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

  /**
   * CRITICAL (this batch's review): the host's asymmetry (§5A.4) means `resume` must NOT prune a
   * `paused` row the same way it prunes an `interrupted` one — the host deliberately LEAVES a paused
   * entry in place (the app flips it via its own `IOperation.Resume()` once it has ACTUALLY resumed),
   * and leaving it in place publishes NOTHING (nothing changed host-side). If the client prunes it
   * locally anyway: the row vanishes, the user cannot see the still-paused deploy to click DISMISS
   * on it, and the host still holds the entry until every subscriber unmounts and a fresh LIST runs —
   * a waiting entry with no reachable exit, rebuilt one layer up, in the exact place this feature
   * exists to eliminate.
   */
  it('resume does NOT drop a paused entry from local state — the host deliberately keeps it', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'paused' }));

    store.actions.resume('op-1');

    expect(store.getState().byId['op-1']).toBeDefined();
    expect(store.getState().paused.map((o) => o.id)).toEqual(['op-1']);
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

  /** The `paused` derived getter (§5A.2, D23 amendment) — the WAITING band alongside `running`/`finished`. */
  it('exposes paused operations separately from running and finished', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'paused' }));

    expect(store.getState().paused.map((o) => o.id)).toEqual(['op-2']);
    expect(store.getState().running.map((o) => o.id)).toEqual(['op-1']);
    expect(store.getState().finished).toEqual([]);
  });

  /**
   * `dismiss` (§5A.3, D23 amendment) mirrors `cancel`'s shape — no optimistic local prune, because
   * the host's Dismiss transitions the entry to `cancelled` and publishes an ordinary
   * OPERATION_UPDATED snapshot for it (unlike clearFinished/resume, which remove an entry with no
   * corresponding wire event).
   */
  it('dismiss posts the DISMISS route with the operation id and does not touch local state', () => {
    const { store, transport } = harness([]);
    store.subscribe(() => {});

    store.actions.dismiss('op-1');

    const request = transport.lastRequest<{ operationId: string }>();
    expect(request.type).toBe('DISMISS');
    expect(request.payload).toEqual({ operationId: 'op-1' });
  });
});
