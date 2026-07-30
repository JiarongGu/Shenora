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

    bus.clear();

    expect(bus.getSubscriptionCount()).toBe(0);
  });
});
