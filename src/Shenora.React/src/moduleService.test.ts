import { afterEach, describe, expect, it } from 'vitest';
import { ShenoraBridge, configureBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { BaseModuleService } from './moduleService.js';
import type { ShenoraTransport } from './transport.js';
import type { IpcRequest } from './types.js';

function recordingTransport(sink: IpcRequest[]): ShenoraTransport {
  return {
    post: (message) => sink.push(JSON.parse(message) as IpcRequest),
    subscribe: () => () => {},
  };
}

afterEach(() => {
  // These tests touch the shared default bridge — reset it so ordering can't matter.
  configureBridge({ transport: null, eventBus: new ShenoraEventBus() });
});

interface TodoRequests extends Record<string, unknown> {
  GET_ALL: void;
  ADD: { title: string };
}

describe('BaseModuleService', () => {
  it('sends typed requests bound to its module', async () => {
    const posted: IpcRequest[] = [];
    const transport: ShenoraTransport = {
      post: (message) => posted.push(JSON.parse(message) as IpcRequest),
      subscribe: () => () => {},
    };
    const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });

    class TodoService extends BaseModuleService<TodoRequests> {
      constructor() {
        super('TODO', bridge);
      }

      add(title: string) {
        return this.send<{ id: string }>('ADD', { payload: { title } });
      }
    }

    new TodoService().add('write tests').catch(() => {}); // never acked

    expect(posted[0]?.module).toBe('TODO');
    expect(posted[0]?.type).toBe('ADD');
    expect(posted[0]?.payload).toEqual({ title: 'write tests' });
  });

  it('a service built BEFORE configureBridge still speaks over the new default', async () => {
    // The bridge used to be a constructor default (`= getBridge()`), evaluated at CONSTRUCTION — and
    // configureBridge DISPOSES the bridge it replaces. So a module-level service singleton (the normal
    // way to write one) captured a bridge that startup then killed, and every call from it rejected
    // with "Bridge disposed" for the rest of the session (P5.5 H2).
    class TodoService extends BaseModuleService<TodoRequests> {
      constructor() {
        super('TODO'); // no explicit bridge → the shared default
      }

      getAll() {
        return this.send<string[]>('GET_ALL');
      }
    }

    const service = new TodoService(); // built first, on purpose

    const posted: IpcRequest[] = [];
    configureBridge({ transport: recordingTransport(posted), eventBus: new ShenoraEventBus() });

    service.getAll().catch(() => {}); // never acked; the afterEach reset rejects it

    expect(posted).toHaveLength(1);
    expect(posted[0]?.module).toBe('TODO');
    expect(posted[0]?.type).toBe('GET_ALL');
  });

  it('an explicitly supplied bridge is still honoured', async () => {
    // Lazy resolution must not silently ignore an explicit bridge — that is the multi-transport case.
    const explicit: IpcRequest[] = [];
    const bridge = new ShenoraBridge({ transport: recordingTransport(explicit), eventBus: new ShenoraEventBus() });
    const shared: IpcRequest[] = [];
    configureBridge({ transport: recordingTransport(shared), eventBus: new ShenoraEventBus() });

    class TodoService extends BaseModuleService<TodoRequests> {
      constructor() {
        super('TODO', bridge);
      }

      getAll() {
        return this.send<string[]>('GET_ALL');
      }
    }

    // Swallow the rejection: nothing acks these requests, and the afterEach reset disposes the bridge,
    // which rejects everything still pending. Vitest fails the run on an unhandled rejection.
    new TodoService().getAll().catch(() => {});

    expect(explicit).toHaveLength(1);
    expect(shared).toHaveLength(0);
  });
});
