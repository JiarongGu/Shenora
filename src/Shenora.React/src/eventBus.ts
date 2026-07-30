import type { EventMessage } from './types';

/**
 * The client-side event hub, ported from the primary desktop sibling: host notifications are
 * unbundled into it by the bridge, and app code (or the dev interceptor) can emit locally.
 * Subscriptions key on exact `module.type` (event enums/maps are app schema — apps layer their
 * own typed wrappers on top, headless per D13). A throwing handler is isolated: it never breaks
 * the other subscribers or the emitter.
 */
export class ShenoraEventBus {
  private handlers = new Map<string, Set<(event: EventMessage) => void>>();

  /** Subscribe to one `module.type`; returns the cleanup function (React-effect friendly). */
  subscribe<TPayload = unknown>(
    module: string,
    type: string,
    handler: (event: EventMessage<TPayload>) => void,
  ): () => void {
    const key = `${module}.${type}`;
    let set = this.handlers.get(key);
    if (!set) {
      set = new Set();
      this.handlers.set(key, set);
    }
    const untyped = handler as (event: EventMessage) => void;
    set.add(untyped);
    return () => {
      const current = this.handlers.get(key);
      if (!current) return;
      current.delete(untyped);
      if (current.size === 0) this.handlers.delete(key);
    };
  }

  /** Emit to all `module.type` subscribers. */
  emit(event: EventMessage): void {
    const set = this.handlers.get(`${event.module}.${event.type}`);
    if (!set || set.size === 0) return;
    for (const handler of [...set]) {
      try {
        handler(event);
      } catch (error) {
        // One subscriber's failure must not break the others.
        console.error(`[shenora] event handler failed for ${event.module}.${event.type}:`, error);
      }
    }
  }

  /** Remove every subscription (tests). */
  clear(): void {
    this.handlers.clear();
  }

  /** Subscription count, total or per `module.type` (diagnostics). */
  getSubscriptionCount(module?: string, type?: string): number {
    if (module && type) return this.handlers.get(`${module}.${type}`)?.size ?? 0;
    let total = 0;
    for (const set of this.handlers.values()) total += set.size;
    return total;
  }
}

/** The shared event bus the default bridge unbundles into. */
export const eventBus = new ShenoraEventBus();
