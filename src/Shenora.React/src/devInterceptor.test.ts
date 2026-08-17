// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { installDevInterceptor, type DevIpcEntry } from './devInterceptor.js';
import { ShenoraEventBus } from './eventBus.js';
import type { ShenoraTransport } from './transport.js';
import { IpcCategories } from './types.js';

interface InterceptorGlobal {
  call: (module: string, type: string, payload?: unknown, scope?: string) => Promise<unknown>;
  waitEvent: (module: string, type: string, timeoutMs?: number) => Promise<unknown>;
  recentIpc: (n?: number) => DevIpcEntry[];
  recentEvents: (n?: number) => unknown[];
  clear: () => void;
}

const GLOBAL_NAME = '__shenoraTest';

function getGlobal(): InterceptorGlobal {
  return (window as unknown as Record<string, InterceptorGlobal>)[GLOBAL_NAME]!;
}

afterEach(() => {
  delete (window as unknown as Record<string, unknown>)[GLOBAL_NAME];
});

describe('installDevInterceptor', () => {
  function createFixture() {
    let listener: ((message: string) => void) | undefined;
    const transport: ShenoraTransport = {
      post: (message) => {
        // Echo success immediately so recorded calls complete.
        const request = JSON.parse(message) as { id: string };
        queueMicrotask(() => listener?.(JSON.stringify({ category: IpcCategories.ipc, id: request.id, success: true, data: 'ok' })));
      },
      subscribe: (l) => {
        listener = l;
        return () => {
          listener = undefined;
        };
      },
    };
    const bus = new ShenoraEventBus();
    const bridge = new ShenoraBridge({ transport, eventBus: bus });
    return { bus, bridge };
  }

  it('records driven calls and emitted events into ring buffers', async () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const info = vi.spyOn(console, 'info').mockImplementation(() => {});
    try {
      const { bus, bridge } = createFixture();
      installDevInterceptor({ globalName: GLOBAL_NAME, bridge, bus });

      await expect(getGlobal().call('APP', 'PING', { n: 1 })).resolves.toBe('ok');
      bus.emit({ module: 'APP', type: 'TICK', payload: 2 });

      const ipc = getGlobal().recentIpc();
      expect(ipc).toHaveLength(1);
      expect(ipc[0]).toMatchObject({ module: 'APP', type: 'PING', ok: true, result: 'ok' });
      expect(getGlobal().recentEvents()).toHaveLength(1);

      getGlobal().clear();
      expect(getGlobal().recentIpc()).toHaveLength(0);
    } finally {
      debug.mockRestore();
      info.mockRestore();
    }
  });

  it('is idempotent and waitEvent resolves on the next emit', async () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const info = vi.spyOn(console, 'info').mockImplementation(() => {});
    try {
      const { bus, bridge } = createFixture();
      installDevInterceptor({ globalName: GLOBAL_NAME, bridge, bus });
      installDevInterceptor({ globalName: GLOBAL_NAME, bridge, bus }); // second install: no re-wrap

      const wait = getGlobal().waitEvent('APP', 'DONE');
      bus.emit({ module: 'APP', type: 'DONE', payload: 42 });

      await expect(wait).resolves.toMatchObject({ payload: 42 });

      // ⚠ THIS ASSERTION USED TO BE VACUOUS, and sabotage found it. Counting the global's buffer cannot
      // see a double-wrap: the second install builds FRESH buffers and replaces the global, so the
      // outer recorder holds exactly one entry whether or not an inner one is also running. What a
      // double-wrap really produces is DOUBLED SIDE EFFECTS — every emit logged twice, through two
      // stacked recorders — so the log is where it is visible.
      expect(getGlobal().recentEvents()).toHaveLength(1);
      expect(debug.mock.calls.filter((c) => String(c[0]).startsWith('[EVT]'))).toHaveLength(1);
    } finally {
      debug.mockRestore();
      info.mockRestore();
    }
  });

  it('🔴 re-wraps when the BRIDGE is replaced, instead of silently watching a dead one', async () => {
    // The idempotency guard used to ask only "is the global set?". But the wrapping mutates a specific
    // bridge INSTANCE, and `configureBridge()` disposes the default bridge and builds a new one — so
    // after that, a second install returned early, the live bridge was never wrapped, and the tool
    // recorded nothing while looking installed. Silence from a recorder reads as "no traffic", which is
    // the worst way for a dev tool to fail.
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const info = vi.spyOn(console, 'info').mockImplementation(() => {});
    try {
      const first = createFixture();
      installDevInterceptor({ globalName: GLOBAL_NAME, bridge: first.bridge, bus: first.bus });

      // The app reconfigures: a brand-new bridge, exactly as configureBridge() produces.
      const second = createFixture();
      installDevInterceptor({ globalName: GLOBAL_NAME, bridge: second.bridge, bus: second.bus });

      // ⚠ Drive the NEW bridge DIRECTLY. An earlier version of this test called `getGlobal().call(…)`
      // and was vacuous: that goes through whichever bridge the global captured, so under the defect it
      // exercised the FIRST bridge — which is wrapped — and recorded a hit either way. Sabotage caught
      // it. The question is whether the LIVE bridge is being watched, so the live bridge has to be the
      // one invoked.
      await second.bridge.invoke('NOTES', 'ADD', { payload: { title: 'x' } });

      expect(getGlobal().recentIpc()).toHaveLength(1);
      expect(getGlobal().recentIpc()[0]).toMatchObject({ module: 'NOTES', type: 'ADD' });
    } finally {
      debug.mockRestore();
      info.mockRestore();
    }
  });
});
