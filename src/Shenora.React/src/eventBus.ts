import type { EventMessage } from './types.js';

/** Optional narrowing for {@link ShenoraEventBus.subscribe}. */
export interface SubscribeOptions {
  /**
   * Only receive events carrying this app-defined scope. Omit to receive EVERY scope.
   *
   * The semantics mirror the host's `EventBus` exactly, and both halves matter: an unscoped
   * subscription sees every scope, AND a scope-less (global) event still reaches scoped subscribers —
   * so an app-wide announcement is not swallowed by a per-scope listener.
   */
  scope?: string;
}

interface Subscription {
  handler: (event: EventMessage) => void;
  scope?: string;
}

/**
 * The client-side event hub, ported from the primary desktop sibling: host notifications are
 * unbundled into it by the bridge, and app code (or the dev interceptor) can emit locally.
 * Subscriptions key on an exact (module, type) pair — event enums/maps are app schema, so apps layer
 * their own typed wrappers on top (headless per D13). A throwing handler is isolated: it never breaks
 * the other subscribers or the emitter.
 */
export class ShenoraEventBus {
  private handlers = new Map<string, Set<Subscription>>();

  /**
   * Subscribe to one (module, type), optionally narrowed to a scope; returns the cleanup function
   * (React-effect friendly).
   */
  subscribe<TPayload = unknown>(
    module: string,
    type: string,
    handler: (event: EventMessage<TPayload>) => void,
    options: SubscribeOptions = {},
  ): () => void {
    const key = eventKey(module, type);
    let set = this.handlers.get(key);
    if (!set) {
      set = new Set();
      this.handlers.set(key, set);
    }
    const subscription: Subscription = {
      handler: handler as (event: EventMessage) => void,
      scope: options.scope,
    };
    set.add(subscription);
    return () => {
      const current = this.handlers.get(key);
      if (!current) return;
      current.delete(subscription);
      if (current.size === 0) this.handlers.delete(key);
    };
  }

  /** Emit to every matching subscriber (see {@link SubscribeOptions.scope} for the scope rule). */
  emit(event: EventMessage): void {
    const set = this.handlers.get(eventKey(event.module, event.type));
    if (!set || set.size === 0) return;
    for (const subscription of [...set]) {
      if (!scopeMatches(subscription.scope, event.scope)) continue;
      try {
        subscription.handler(event);
      } catch (error) {
        // One subscriber's failure must not break the others.
        console.error(`[shenora] event handler failed for ${event.module}/${event.type}:`, error);
      }
    }
  }

  /** Remove every subscription (tests). */
  clear(): void {
    this.handlers.clear();
  }

  /** Subscription count, total or per (module, type) (diagnostics). */
  getSubscriptionCount(module?: string, type?: string): number {
    if (module && type) return this.handlers.get(eventKey(module, type))?.size ?? 0;
    let total = 0;
    for (const set of this.handlers.values()) total += set.size;
    return total;
  }
}

/**
 * `'\0'`-joined, NOT `.`-joined (P5.5 H6).
 *
 * Module and type are both arbitrary app-defined strings, so a `.` separator makes
 * `("APP", "TASK.DONE")` and `("APP.TASK", "DONE")` the same key — one app's events silently delivered
 * to another's subscribers. The host's `EventBus` fixed exactly this and documented it; the client kept
 * the colliding form, so the two halves of one contract disagreed. `'\0'` cannot appear in a JS string
 * literal a developer types by accident, which is what makes it safe as a separator.
 */
function eventKey(module: string, type: string): string {
  return `${module}\0${type}`;
}

/**
 * The host's rule, restated: no subscriber scope = every scope; no event scope = a global event that
 * reaches scoped subscribers too. Otherwise they must be equal.
 */
function scopeMatches(subscriptionScope: string | undefined, eventScope: string | undefined): boolean {
  if (!subscriptionScope) return true;
  if (!eventScope) return true;
  return subscriptionScope === eventScope;
}

/** The shared event bus the default bridge unbundles into. */
export const eventBus = new ShenoraEventBus();
