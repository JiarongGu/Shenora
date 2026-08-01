import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { createOperationsStore, OperationStatuses } from './operations.js';
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

const info = (over: Partial<{ id: string; status: string; progress: { value: number; total?: number; unit?: string } }>) => ({
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

    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: { value: 40 } }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: { value: 80 } }));

    expect(store.getState().byId['op-1']!.progress).toEqual({ value: 80 });
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
   * keeps). `OPERATION_REMOVED` replaces both guesses with one authoritative event: the client folds
   * it by deleting exactly the named ids, regardless of their status — the host decided, not a local
   * status rule.
   */
  it('OPERATION_REMOVED deletes the named ids from local state, regardless of status', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-3', status: 'paused' }));

    bus.emit('OPERATIONS', 'OPERATION_REMOVED', { operationIds: ['op-2', 'op-3'] });

    expect(Object.keys(store.getState().byId)).toEqual(['op-1']);
  });

  /** An id the store never had is a harmless no-op — the same shape as deleting an absent key. */
  it('OPERATION_REMOVED naming an unknown id is a harmless no-op', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({}));

    bus.emit('OPERATIONS', 'OPERATION_REMOVED', { operationIds: ['no-such-id'] });

    expect(Object.keys(store.getState().byId)).toEqual(['op-1']);
  });

  /**
   * `clearFinished`/`resume` no longer locally mutate state at all (Finding 4) — the host's
   * `OPERATION_REMOVED` is the ONLY thing that removes a row now, so calling either action must not
   * change anything by itself, however the host eventually answers.
   */
  it('clearFinished does not locally mutate state — only the hosts OPERATION_REMOVED does', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));

    store.actions.clearFinished();

    expect(Object.keys(store.getState().byId).sort()).toEqual(['op-1', 'op-2']);
  });

  it('resume does not locally mutate state — only the hosts OPERATION_REMOVED does', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'interrupted' }));

    store.actions.resume('op-1');

    expect(store.getState().byId['op-1']).toBeDefined();
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
   * Second-adopter-review finding: `interrupted` fell into NO existing getter — not `running`, not
   * `paused` (matches only the literal `'paused'`), not `finished` (`TERMINAL_STATUSES` deliberately
   * excludes it) — reachable only by hand-filtering `byId`, which the store's own docs discourage.
   * The `interrupted` getter closes that: the other half of the WAITING band (design §5A.2).
   */
  it('exposes interrupted operations separately from paused, running, and finished', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'paused' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-3', status: 'interrupted' }));

    expect(store.getState().interrupted.map((o) => o.id)).toEqual(['op-3']);
    expect(store.getState().paused.map((o) => o.id)).toEqual(['op-2']);
    expect(store.getState().running.map((o) => o.id)).toEqual(['op-1']);
    expect(store.getState().finished).toEqual([]);
  });

  /**
   * `waiting` (design §5A.2) is the band `Dismiss`/`RequestResume` both accept — `paused` ∪
   * `interrupted` — so a status bar can render "needs you" as one bucket without caring whether the
   * process restarted in between. Asserted as an actual union of the two OTHER getters' output, not
   * a second hardcoded list, so this test cannot drift from them independently.
   */
  it('waiting equals paused union interrupted', () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'paused' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'interrupted' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-3', status: 'running' }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-4', status: 'completed' }));

    const waitingIds = store.getState().waiting.map((o) => o.id);
    const unionIds = [
      ...store.getState().paused.map((o) => o.id),
      ...store.getState().interrupted.map((o) => o.id),
    ];

    expect(waitingIds.slice().sort()).toEqual(unionIds.slice().sort());
    expect(waitingIds).toEqual(['op-1', 'op-2']); // byId order, matching `running`'s own convention
  });

  /**
   * The client mirror of the host's `OperationLifecycleInvariantTests` (§5A.1): enumerate the LIVE
   * `OperationStatuses` object — never a hardcoded list — so a status added later with no band shows
   * up as a FAILURE here instead of silently belonging nowhere, which is exactly how `interrupted`
   * went unnoticed before `waiting`/`interrupted` existed. Run against the pre-fix store this fails
   * (see TASKS.md / the task's own notes for the captured RED output); it must stay green for any
   * future status too.
   */
  it('every live status belongs to exactly one band: running, waiting, or finished', () => {
    const statuses = Object.values(OperationStatuses);
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    statuses.forEach((status, i) => {
      bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: `op-${i}`, status }));
    });

    const state = store.getState();
    const bandsOf = (id: string): string[] => {
      const bands: string[] = [];
      if (state.running.some((o) => o.id === id)) bands.push('running');
      if (state.waiting.some((o) => o.id === id)) bands.push('waiting');
      if (state.finished.some((o) => o.id === id)) bands.push('finished');
      return bands;
    };

    statuses.forEach((status, i) => {
      const bands = bandsOf(`op-${i}`);
      expect(bands, `status '${status}' landed in bands [${bands.join(', ')}], expected exactly 1`).toHaveLength(1);
    });
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

  /**
   * FINDING 3 (Important, generic-library audit): the kit shipped RESUME/DISMISS but no client route
   * to ASK the host to pause running work — a real gap for the download-manager/activity-panel shape
   * the kit itself already names as a consumer. `pause` mirrors `dismiss`'s shape exactly: no
   * optimistic local prune, since asking never changes the state by itself (the owning module's own
   * `IOperation.Pause` is what publishes the transition).
   */
  it('pause posts the PAUSE route with the operation id and does not touch local state', () => {
    const { store, transport, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ status: 'running' }));

    store.actions.pause('op-1');

    const request = transport.lastRequest<{ operationId: string }>();
    expect(request.type).toBe('PAUSE');
    expect(request.payload).toEqual({ operationId: 'op-1' });
    expect(store.getState().byId['op-1']?.status).toBe('running');   // unchanged — asking is not acting
  });
});
