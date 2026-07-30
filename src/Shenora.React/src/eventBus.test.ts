import { describe, expect, it, vi } from 'vitest';
import { ShenoraEventBus } from './eventBus';

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

  it('clear removes everything', () => {
    const bus = new ShenoraEventBus();
    bus.subscribe('APP', 'A', () => {});
    bus.subscribe('APP', 'B', () => {});

    bus.clear();

    expect(bus.getSubscriptionCount()).toBe(0);
  });
});
