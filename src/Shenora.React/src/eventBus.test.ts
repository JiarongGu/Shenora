import { describe, expect, it, vi } from 'vitest';
import { ShenoraEventBus } from './eventBus.js';

describe('ShenoraEventBus', () => {
  it('delivers to exact module.type subscribers only', () => {
    const bus = new ShenoraEventBus();
    const received: unknown[] = [];
    bus.subscribe('APP', 'TICK', (event) => received.push(event.payload));

    bus.emit({ module: 'APP', type: 'TICK', payload: 1 });
    bus.emit({ module: 'APP', type: 'OTHER', payload: 2 });
    bus.emit({ module: 'OTHER', type: 'TICK', payload: 3 });

    expect(received).toEqual([1]);
  });

  it('cleanup unsubscribes and empty keys are pruned', () => {
    const bus = new ShenoraEventBus();
    const off = bus.subscribe('APP', 'TICK', () => {});
    expect(bus.getSubscriptionCount('APP', 'TICK')).toBe(1);

    off();

    expect(bus.getSubscriptionCount('APP', 'TICK')).toBe(0);
    expect(bus.getSubscriptionCount()).toBe(0);
  });

  it('isolates a throwing handler from the others', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const bus = new ShenoraEventBus();
      const received: unknown[] = [];
      bus.subscribe('APP', 'TICK', () => {
        throw new Error('boom');
      });
      bus.subscribe('APP', 'TICK', (event) => received.push(event.payload));

      expect(() => bus.emit({ module: 'APP', type: 'TICK', payload: 1 })).not.toThrow();
      expect(received).toEqual([1]);
      expect(consoleError).toHaveBeenCalled();
    } finally {
      consoleError.mockRestore();
    }
  });

  it('a dot in a module or type name cannot collide two different events', () => {
    // The keys were `.`-joined, so ("APP", "TASK.DONE") and ("APP.TASK", "DONE") were the SAME key —
    // one app's events delivered to another's subscribers. The host's EventBus fixed exactly this with
    // '\0' and documented it; the client kept the colliding form, so the two halves of one contract
    // disagreed (P5.5 H6).
    const bus = new ShenoraEventBus();
    const first: unknown[] = [];
    const second: unknown[] = [];
    bus.subscribe('APP', 'TASK.DONE', (event) => first.push(event.payload));
    bus.subscribe('APP.TASK', 'DONE', (event) => second.push(event.payload));

    bus.emit({ module: 'APP', type: 'TASK.DONE', payload: 'first' });
    bus.emit({ module: 'APP.TASK', type: 'DONE', payload: 'second' });

    expect(first).toEqual(['first']);
    expect(second).toEqual(['second']);
  });

  describe('scope filtering (mirrors the host EventBus rule)', () => {
    it('a scoped subscription only sees its own scope', () => {
      // The wire carries a scope and the host keys on it, but the client had no way to express one — so
      // a component in profile A also woke for profile B's events, with no filter available (P5.5 H6).
      const bus = new ShenoraEventBus();
      const received: unknown[] = [];
      bus.subscribe('APP', 'TICK', (event) => received.push(event.payload), { scope: 's1' });

      bus.emit({ module: 'APP', type: 'TICK', payload: 'mine', scope: 's1' });
      bus.emit({ module: 'APP', type: 'TICK', payload: 'theirs', scope: 's2' });

      expect(received).toEqual(['mine']);
    });

    it('an unscoped subscription sees every scope', () => {
      const bus = new ShenoraEventBus();
      const received: unknown[] = [];
      bus.subscribe('APP', 'TICK', (event) => received.push(event.payload));

      bus.emit({ module: 'APP', type: 'TICK', payload: 1, scope: 's1' });
      bus.emit({ module: 'APP', type: 'TICK', payload: 2, scope: 's2' });
      bus.emit({ module: 'APP', type: 'TICK', payload: 3 });

      expect(received).toEqual([1, 2, 3]);
    });

    it('a global event still reaches a scoped subscription', () => {
      // The half that is easy to get wrong: an app-wide announcement must not be swallowed by a
      // per-scope listener. The host does this too.
      const bus = new ShenoraEventBus();
      const received: unknown[] = [];
      bus.subscribe('APP', 'TICK', (event) => received.push(event.payload), { scope: 's1' });

      bus.emit({ module: 'APP', type: 'TICK', payload: 'global' });

      expect(received).toEqual(['global']);
    });
  });

  it('clear removes everything', () => {
    const bus = new ShenoraEventBus();
    bus.subscribe('APP', 'A', () => {});
    bus.subscribe('APP', 'B', () => {});
    bus.subscribeToModule('APP', () => {});
    bus.subscribeToAll(() => {});

    bus.clear();

    expect(bus.getSubscriptionCount()).toBe(0);
  });

  // P6.4 added the two broad breadths. The host's IEventBus had shipped SubscribeToAll /
  // SubscribeToModule from the start and the client had neither, so an observer that cannot
  // enumerate the event vocabulary up front — an adoption shim keeping a legacy firehose alive, a
  // diagnostics tap, the kit's OWN dev interceptor — had to reach past the API and patch `emit`.
  describe('broad subscriptions', () => {
    it('subscribeToAll receives every event regardless of module or type', () => {
      const bus = new ShenoraEventBus();
      const received: string[] = [];
      bus.subscribeToAll((event) => received.push(`${event.module}/${event.type}`));

      bus.emit({ module: 'APP', type: 'TICK' });
      bus.emit({ module: 'OTHER', type: 'ANYTHING' });
      bus.emit({ module: 'PLUGIN.42', type: 'A-TYPE-NOBODY-DECLARED' });

      expect(received).toEqual(['APP/TICK', 'OTHER/ANYTHING', 'PLUGIN.42/A-TYPE-NOBODY-DECLARED']);
    });

    it('subscribeToModule receives every type from its module and nothing from others', () => {
      const bus = new ShenoraEventBus();
      const received: string[] = [];
      bus.subscribeToModule('APP', (event) => received.push(event.type));

      bus.emit({ module: 'APP', type: 'ONE' });
      bus.emit({ module: 'APP', type: 'TWO' });
      bus.emit({ module: 'OTHER', type: 'THREE' });

      expect(received).toEqual(['ONE', 'TWO']);
    });

    it('delivers narrowest first: exact, then module, then catch-all', () => {
      // Stable order so a broad observer never runs ahead of the feature code it observes.
      const bus = new ShenoraEventBus();
      const order: string[] = [];
      bus.subscribeToAll(() => order.push('all'));
      bus.subscribeToModule('APP', () => order.push('module'));
      bus.subscribe('APP', 'TICK', () => order.push('exact'));

      bus.emit({ module: 'APP', type: 'TICK' });

      expect(order).toEqual(['exact', 'module', 'all']);
    });

    it('the scope rule is identical at every breadth', () => {
      const bus = new ShenoraEventBus();
      const all: unknown[] = [];
      const perModule: unknown[] = [];
      bus.subscribeToAll((event) => all.push(event.payload), { scope: 's1' });
      bus.subscribeToModule('APP', (event) => perModule.push(event.payload), { scope: 's1' });

      bus.emit({ module: 'APP', type: 'TICK', payload: 'mine', scope: 's1' });
      bus.emit({ module: 'APP', type: 'TICK', payload: 'theirs', scope: 's2' });
      bus.emit({ module: 'APP', type: 'TICK', payload: 'global' });

      // Own scope yes, another scope no, and a global announcement still reaches a scoped observer.
      expect(all).toEqual(['mine', 'global']);
      expect(perModule).toEqual(['mine', 'global']);
    });

    it('a module or type literally named "*" is NOT a catch-all', () => {
      // The host matches breadth with a "*" sentinel inside its key; the client uses separate
      // collections precisely so an app string can never become one. Same lesson as the '\0' join —
      // pinned before it could be earned a second time.
      const bus = new ShenoraEventBus();
      const star: unknown[] = [];
      bus.subscribe('*', '*', (event) => star.push(event.payload));

      bus.emit({ module: 'APP', type: 'TICK', payload: 'not yours' });
      bus.emit({ module: '*', type: '*', payload: 'yours' });

      expect(star).toEqual(['yours']);
    });

    it('a throwing broad handler is isolated from the exact subscribers', () => {
      const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
      try {
        const bus = new ShenoraEventBus();
        const received: unknown[] = [];
        bus.subscribeToAll(() => { throw new Error('boom'); });
        bus.subscribe('APP', 'TICK', (event) => received.push(event.payload));

        expect(() => bus.emit({ module: 'APP', type: 'TICK', payload: 1 })).not.toThrow();
        expect(received).toEqual([1]);
        expect(consoleError).toHaveBeenCalled();
      } finally {
        consoleError.mockRestore();
      }
    });

    it('both breadths unsubscribe cleanly', () => {
      const bus = new ShenoraEventBus();
      const offAll = bus.subscribeToAll(() => {});
      const offModule = bus.subscribeToModule('APP', () => {});
      expect(bus.getSubscriptionCount()).toBe(2);

      offAll();
      offModule();

      expect(bus.getSubscriptionCount()).toBe(0);
    });

    it('one event reaches exactly the subscribers that existed when it was emitted', () => {
      // Three breadths means three lookups, so the collections must be snapshotted BEFORE any
      // handler runs. Reading them lazily — after the exact handlers had already fired — would let a
      // handler that subscribes broadly WHILE handling receive the very event it is handling.
      // Copying per-set was enough while there was only one set; it stopped being enough here.
      const bus = new ShenoraEventBus();
      const late: unknown[] = [];
      bus.subscribe('APP', 'TICK', () => {
        bus.subscribeToAll(() => late.push('all'));
        bus.subscribeToModule('APP', () => late.push('module'));
      });

      bus.emit({ module: 'APP', type: 'TICK' });
      expect(late).toEqual([]);

      // ...but they are live for the NEXT event.
      bus.emit({ module: 'APP', type: 'TICK' });
      expect(late).toEqual(['module', 'all']);
    });

    it('the per-pair count answers "how many listeners would receive this"', () => {
      const bus = new ShenoraEventBus();
      bus.subscribe('APP', 'TICK', () => {});
      bus.subscribeToModule('APP', () => {});
      bus.subscribeToAll(() => {});
      bus.subscribe('OTHER', 'TICK', () => {});

      expect(bus.getSubscriptionCount('APP', 'TICK')).toBe(3);
      expect(bus.getSubscriptionCount('OTHER', 'TICK')).toBe(2);
      expect(bus.getSubscriptionCount()).toBe(4);
    });
  });
});
