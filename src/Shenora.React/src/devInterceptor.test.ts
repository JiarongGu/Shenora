// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { installDevInterceptor, type DevIpcEntry } from './devInterceptor.js';
import { ShenoraEventBus } from './eventBus.js';
import type { ShenoraTransport } from './transport.js';

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
        queueMicrotask(() => listener?.(JSON.stringify({ category: 'ipc', id: request.id, success: true, data: 'ok' })));
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
      expect(getGlobal().recentEvents()).toHaveLength(1); // one emit recorded once
    } finally {
      debug.mockRestore();
      info.mockRestore();
    }
  });
});
