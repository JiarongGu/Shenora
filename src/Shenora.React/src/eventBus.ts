import type { EventMessage } from './types.js';

/** Optional narrowing for the {@link ShenoraEventBus} subscribe methods. */
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
 * Event enums/maps are app schema, so apps layer their own typed wrappers on top (headless per D13).
 * A throwing handler is isolated: it never breaks the other subscribers or the emitter.
 *
 * Three subscription breadths, mirroring the host's `Shenora.IEventBus`: an exact
 * `(module, type)` pair, a whole {@link subscribeToModule | module}, or
 * {@link subscribeToAll | everything}. The broad two were added in P6.4 — the host had shipped them
 * from the start (`WebViewIpcBridge` itself consumes `SubscribeToAll`), so the client was the
 * asymmetric half of one concept and an observer that cannot enumerate the event vocabulary up front
 * had no supported expression at all.
 */
export class ShenoraEventBus {
  /** Exact `(module, type)` subscriptions, keyed by {@link eventKey}. */
  private exact = new Map<string, Set<Subscription>>();
  /** Whole-module subscriptions, keyed by module. */
  private byModule = new Map<string, Set<Subscription>>();
  /** Catch-all subscriptions. */
  private all = new Set<Subscription>();

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
    return addTo(this.exact, eventKey(module, type), newSubscription(handler, options));
  }

  /**
   * Subscribe to EVERY event from one module, whatever its type; returns the cleanup function.
   *
   * For a feature whose event vocabulary is open — plug-in-contributed types, a module that grows
   * types over time — where enumerating pairs would mean editing the subscriber every time the host
   * gains an event.
   */
  subscribeToModule<TPayload = unknown>(
    module: string,
    handler: (event: EventMessage<TPayload>) => void,
    options: SubscribeOptions = {},
  ): () => void {
    return addTo(this.byModule, module, newSubscription(handler, options));
  }

  /**
   * Subscribe to EVERY event on the bus; returns the cleanup function.
   *
   * The breadth is the point, so use it for cross-cutting observers — a diagnostics overlay, a
   * telemetry tap, a bridge that folds the whole stream into another state library, or an adoption
   * shim keeping a legacy "every host message" handler alive while individual features migrate onto
   * exact pairs. It is NOT the way to consume one feature's events: prefer {@link subscribe}, which
   * says what it listens for and does not wake for unrelated traffic.
   */
  subscribeToAll<TPayload = unknown>(
    handler: (event: EventMessage<TPayload>) => void,
    options: SubscribeOptions = {},
  ): () => void {
    const subscription = newSubscription(handler, options);
    this.all.add(subscription);
    return () => { this.all.delete(subscription); };
  }

  /**
   * Emit to every matching subscriber (see {@link SubscribeOptions.scope} for the scope rule).
   *
   * Delivery order is stable: exact pair, then whole-module, then catch-all — narrowest first, so a
   * broad observer never runs ahead of the feature code it is observing.
   */
  emit(event: EventMessage): void {
    const exact = this.exact.get(eventKey(event.module, event.type));
    const byModule = this.byModule.get(event.module);
    if (!exact?.size && !byModule?.size && this.all.size === 0) return;

    // Snapshot ALL THREE breadths before invoking any handler, not one at a time. A handler may
    // subscribe or unsubscribe during delivery, and one event must reach exactly the subscribers
    // that existed when it was emitted. Reading the broad collections lazily — after the exact
    // handlers had already run — would let a handler that subscribes broadly while handling receive
    // the very event it is handling. Copying per-set was enough while there was only one set.
    for (const subscription of [...(exact ?? []), ...(byModule ?? []), ...this.all]) {
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
    this.exact.clear();
    this.byModule.clear();
    this.all.clear();
  }

  /**
   * Subscription count (diagnostics). With no arguments: every subscription of any breadth. With a
   * `(module, type)`: every subscription that WOULD receive that pair — exact, whole-module and
   * catch-all — because "how many listeners does this event have?" is the question a diagnostic is
   * actually asking. Scope is not applied; a count is not a delivery.
   */
  getSubscriptionCount(module?: string, type?: string): number {
    if (module && type) {
      return (this.exact.get(eventKey(module, type))?.size ?? 0)
        + (this.byModule.get(module)?.size ?? 0)
        + this.all.size;
    }
    let total = this.all.size;
    for (const set of this.exact.values()) total += set.size;
    for (const set of this.byModule.values()) total += set.size;
    return total;
  }

}

/** The one place a handler is widened to the stored shape, so the three breadths cannot drift. */
function newSubscription<TPayload>(
  handler: (event: EventMessage<TPayload>) => void,
  options: SubscribeOptions,
): Subscription {
  return { handler: handler as (event: EventMessage) => void, scope: options.scope };
}

/** Shared add-and-prune for the two keyed collections (an empty key is removed, as it always was). */
function addTo(
  collection: Map<string, Set<Subscription>>,
  key: string,
  subscription: Subscription,
): () => void {
  let set = collection.get(key);
  if (!set) {
    set = new Set();
    collection.set(key, set);
  }
  set.add(subscription);
  return () => {
    const current = collection.get(key);
    if (!current) return;
    current.delete(subscription);
    if (current.size === 0) collection.delete(key);
  };
}

/**
 * `'\0'`-joined, NOT `.`-joined (P5.5 H6).
 *
 * Module and type are both arbitrary app-defined strings, so a `.` separator makes
 * `("APP", "TASK.DONE")` and `("APP.TASK", "DONE")` the same key — one app's events silently delivered
 * to another's subscribers. The host's `EventBus` fixed exactly this and documented it; the client kept
 * the colliding form, so the two halves of one contract disagreed. `'\0'` cannot appear in a JS string
 * literal a developer types by accident, which is what makes it safe as a separator.
 *
 * The broad subscriptions deliberately do NOT reuse this with a `"*"` sentinel the way the host's
 * pattern matcher does: a module or type legitimately named `*` would then silently become a
 * catch-all. Separate collections cannot collide with an app string at all — same lesson as above,
 * applied before it could be earned a second time.
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
