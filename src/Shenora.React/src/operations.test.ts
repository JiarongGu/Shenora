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
function harness(initialList: unknown[]) {
  const transport = new FakeTransport();
  const realBus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: realBus });
  const store = createOperationsStore({ bridge, bus: realBus });

  return {
    store,
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
});
