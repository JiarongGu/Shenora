import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge';
import { ShenoraEventBus } from './eventBus';
import { BaseModuleService } from './moduleService';
import type { ShenoraTransport } from './transport';
import type { IpcRequest } from './types';

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

    void new TodoService().add('write tests');

    expect(posted[0]?.module).toBe('TODO');
    expect(posted[0]?.type).toBe('ADD');
    expect(posted[0]?.payload).toEqual({ title: 'write tests' });
  });
});
