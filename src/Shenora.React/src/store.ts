import { useCallback, useDebugValue, useRef, useSyncExternalStore } from 'react';
import { getBridge, type ShenoraBridge, type PostOptions } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import type { EventMessage } from './types.js';

/** The IPC handles + local-state seam a store's `actions` are built over. */
export interface ShenoraStoreIo<TState = unknown> {
  /** Fire-and-forget send to this store's module. Returns the request id. */
  post: <TPayload = unknown>(type: string, options?: PostOptions<TPayload>) => string;
  /** Correlated request to this store's module — for calls that are quick AND UI-thread-safe. */
  invoke: <TData = unknown, TPayload = unknown>(
    type: string,
    options?: { payload?: TPayload; timeoutMs?: number },
  ) => Promise<TData>;
  /** Current state — for an action that needs to compute an OPTIMISTIC update from it. */
  getState: () => TState;
  /**
   * Apply a local state change with NO host round trip and NO wire event — an optimistic update an
   * action can fully decide by itself. The host stays authoritative for everything a `snapshot`/`on`
   * reducer covers; this is for state the ACTION already knows and the host would only echo back.
   *
   * 🔴 **Reach for it only when the host has no event to tell you.** This used to be documented with
   * the kit's own `clearFinished` doing an optimistic prune, and that example was WITHDRAWN: the
   * moment `REQUEST_REMOVED` existed, the local prune became a second thing deciding which rows are
   * gone, able to disagree with the host about it. If the host emits an event for the change, let the
   * reducer own it — an optimistic path beside a wire event is a divergence waiting to happen, not a
   * latency win.
   */
  setState: (reduce: (state: TState) => TState) => void;
}

/** How a store loads the state that already exists before anyone was watching. */
export interface ShenoraStoreSnapshot<TState> {
  /** The request type to `invoke` on this store's module. */
  type: string;
  payload?: unknown;
  /** Fold the response into state. */
  apply: (state: TState, data: unknown) => TState;
}

/** Inputs for {@link createShenoraStore}. */
/**
 * Is the fresh selector result the same VALUE as the previous one, for re-render purposes?
 *
 * `Object.is` plus ONE level of own-key comparison. The shallow step is what lets an inline selector
 * that derives a new object work — `s => ({ count: s.lines.length })` builds a different object every
 * call, and without this every call would look like a change and loop.
 *
 * ⚠ Deliberately one level. Deep equality would make an unchanged NESTED value hide a changed outer
 * one only by cost, and a store whose selectors need deep comparison is selecting too much.
 */
function equivalent(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  // Arrays and plain objects only — anything exotic (Map, Date, a class) falls back to identity above.
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const aKeys = Object.keys(a as object);
  const bKeys = Object.keys(b as object);
  if (aKeys.length !== bKeys.length) return false;
  return aKeys.every((k) =>
    Object.prototype.hasOwnProperty.call(b, k)
    && Object.is((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]));
}

export interface ShenoraStoreOptions<TState, TActions> {
  /** State before anything has arrived. */
  initial: TState;

  /**
   * Load the CURRENT state when the first component subscribes.
   *
   * Not optional in spirit, even though it is in the type: a component that mounts while work is
   * already in flight has MISSED the events, and a stream cannot be replayed. Snapshot-then-deltas
   * is the contract; deltas alone silently work only for whoever was watching from the start.
   */
  snapshot?: ShenoraStoreSnapshot<TState>;

  /** Event type (within this module) → PURE reducer over state. */
  on?: Record<string, (state: TState, payload: never, event: EventMessage) => TState>;

  /** Fire-and-forget senders / requests, exposed on the returned hook. */
  actions?: (io: ShenoraStoreIo<TState>) => TActions;

  /** Optional app-defined routing scope, applied to both the subscriptions and the sends. */
  scope?: string;

  /** Test/multi-transport seams. Default: the shared bridge and event bus. */
  bridge?: ShenoraBridge;
  bus?: ShenoraEventBus;

  /** Where a reducer or snapshot failure is reported. Default: `console.error`. */
  onError?: (error: unknown, context: { module: string; type: string }) => void;
}

/** What {@link createShenoraStore} returns: a hook, plus the store handles for non-React callers. */
export interface ShenoraStore<TState, TActions> {
  /** Subscribe this component. With no selector you get the whole state. */
  (): TState;
  <TSelected>(selector: (state: TState) => TSelected): TSelected;
  /** Current state without subscribing (event handlers, actions, tests). */
  getState: () => TState;
  /** Subscribe outside React; returns an unsubscribe. */
  subscribe: (listener: () => void) => () => void;
  /** The declared actions. */
  actions: TActions;
  /** Test seam: drop state back to `initial` and forget that the snapshot ran. */
  reset: () => void;
}

/**
 * A store fed by one module's host event stream, shared by every component that reads it.
 *
 * This is the shape a desktop app needs and the one three sibling apps each hand-built before it
 * existed here — see `.claude/knowledge/generic-library.md`'s worked example for that survey. It exists
 * because status- and progress-driven UI is inherently MANY-WATCHERS: a full panel and a compact
 * progress strip want the same live state, and without a shared store each re-implements the wiring,
 * each opens its own subscription, and each starts empty.
 *
 * What it guarantees:
 * - **One subscription per event type, however many components read it.** Mounting N components does
 *   not open N subscriptions; unmounting the last one tears them down.
 * - **A late mounter sees current state**, via {@link ShenoraStoreOptions.snapshot} on the first
 *   subscription. This is the part that cannot be retrofitted by subscribing harder.
 * - **No state library.** Built on React's `useSyncExternalStore`, which exists for exactly this and
 *   is tearing-free under concurrent rendering. The kit imposes no store dependency (D13's spirit —
 *   apps bring their own); every sibling reached for the same one, and baking that in would have been
 *   solving their stack rather than their problem.
 * - **Reducers are PURE and isolated**: they take state + payload and return state, so a store is
 *   testable with no bridge, and a throwing reducer is reported rather than corrupting shared state.
 *
 * Headless (D13/D21): the kit ships the MECHANISM. What an operation is — its phases, its progress
 * shape, whether it queues — stays in the app; there is deliberately no job/queue/progress type here.
 *
 * @example
 * const useDeploy = createShenoraStore('DEPLOY', {
 *   initial: { status: 'idle' as const, lines: [] as string[] },
 *   snapshot: { type: 'GET_STATE', apply: (s, d) => ({ ...s, ...(d as object) }) },
 *   on: {
 *     PROGRESS: (s, p: { line: string }) => ({ ...s, lines: [...s.lines, p.line] }),
 *     ENDED: (s, p: { ok: boolean }) => ({ ...s, status: p.ok ? 'done' : 'failed' }),
 *   },
 *   actions: ({ post }) => ({ start: (cfg: unknown) => post('START', { payload: cfg }) }),
 * });
 *
 * // in any number of components:
 * const status = useDeploy((s) => s.status);
 * useDeploy.actions.start({ env: 'prod' });
 */
export function createShenoraStore<TState, TActions = Record<string, never>>(
  module: string,
  options: ShenoraStoreOptions<TState, TActions>,
): ShenoraStore<TState, TActions> {
  const { initial, snapshot, on = {}, scope } = options;
  const report = options.onError
    ?? ((error: unknown, context: { module: string; type: string }) =>
      console.error(`[shenora] store ${context.module}.${context.type} failed:`, error));

  let state = initial;
  let snapshotLoaded = false;
  // Bumped on detach. A snapshot response from an earlier subscriber epoch resolving late must be
  // DROPPED: the current epoch has (or will have) its own, fresher request, and applying the old
  // body after the new one is a lost update wearing a success path.
  let snapshotEpoch = 0;
  const listeners = new Set<() => void>();
  let unsubscribes: (() => void)[] = [];

  const bridge = () => options.bridge ?? getBridge();
  const bus = () => options.bus ?? defaultEventBus;

  const setState = (next: TState): void => {
    if (Object.is(next, state)) return; // a reducer returning the same state is a no-op, not a render
    state = next;
    for (const listener of [...listeners]) listener();
  };

  const applyEvent = (type: string, event: EventMessage): void => {
    const reduce = on[type];
    if (!reduce) return;
    try {
      setState(reduce(state, event.payload as never, event));
    } catch (error) {
      // A throwing reducer must not corrupt shared state or break the other subscribers — the same
      // guarded-callback rule the host applies to app code (Shenora.AppCallback).
      report(error, { module, type });
    }
  };

  const loadSnapshot = (): void => {
    if (!snapshot || snapshotLoaded) return;
    snapshotLoaded = true; // set BEFORE awaiting: two components mounting in the same tick must not
    // both fire the request (React StrictMode double-invokes effects, which is precisely this case).
    const epoch = snapshotEpoch;
    bridge()
      .invoke<unknown>(module, snapshot.type, { payload: snapshot.payload, scope })
      .then(
        (data) => {
          if (epoch !== snapshotEpoch) return; // a previous epoch's answer — the live one owns state
          try {
            setState(snapshot.apply(state, data));
          } catch (error) {
            report(error, { module, type: snapshot.type });
          }
        },
        (error: unknown) => {
          if (epoch !== snapshotEpoch) return;
          // Allow a later retry: a snapshot that failed because the host was not ready yet should not
          // leave the store permanently empty for the rest of the session.
          snapshotLoaded = false;
          report(error, { module, type: snapshot.type });
        },
      );
  };

  const attach = (): void => {
    unsubscribes = Object.keys(on).map((type) =>
      bus().subscribe(module, type, (event) => applyEvent(type, event), { scope }),
    );
    loadSnapshot();
  };

  const detach = (): void => {
    for (const off of unsubscribes) off();
    unsubscribes = [];
    // The next mount must RE-LOAD: with no bus subscription live, everything the host emits from
    // here on is missed, so the snapshot the flag was guarding is stale the moment this returns.
    // The flag exists to dedupe same-tick double-mounts (StrictMode), not to span subscriber epochs —
    // and the epoch bump orphans any request still in flight, so its late answer cannot clobber the
    // next epoch's fresher one.
    snapshotLoaded = false;
    snapshotEpoch++;
  };

  const subscribe = (listener: () => void): (() => void) => {
    // ONE subscription per event type for the whole store — the property that makes this worth
    // existing. The first listener attaches; the last one to leave detaches.
    if (listeners.size === 0) attach();
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
      if (listeners.size === 0) detach();
    };
  };

  const getState = (): TState => state;

  const io: ShenoraStoreIo<TState> = {
    post: (type, postOptions) => bridge().post(module, type, { scope, ...postOptions }),
    invoke: (type, invokeOptions) => bridge().invoke(module, type, { scope, ...invokeOptions }),
    getState,
    setState: (reduce) => setState(reduce(getState())),
  };

  /**
   * Subscribe to the store, optionally through a selector.
   *
   * 🔴 **The selector is RE-RUN every time and the previous RESULT is reused when equivalent.** It used
   * to be memoized against STATE identity alone, which looked like the careful thing and was wrong: a
   * selector whose closure changed while the state did not returned the PREVIOUS selector's value. A
   * list row doing `useShenoraRequests(s => s.byId[id])` whose `id` prop changes — virtualised reuse, a
   * route change — rendered the previous row's data until some unrelated event replaced the state.
   *
   * ⚠ <b>Two requirements pull against each other and both are met by comparing RESULTS rather than
   * inputs.</b> `useSyncExternalStore` re-renders whenever `getSnapshot` returns something new by
   * `Object.is`, so:
   * <list type="bullet">
   * <item>keying the cache on STATE alone gives stale values when the selector changes — the bug above;</item>
   * <item>keying it on the SELECTOR too loops forever for an inline selector that derives a new object
   * (`s => ({ count: s.lines.length })`) — a fresh identity each render, a fresh object each call.
   * zustand v5 has exactly that behaviour and answers it with an opt-in `useShallow`.</item>
   * </list>
   * Comparing the RESULT shallowly gives both: a derived object that is field-for-field the same reuses
   * the previous reference and does not re-render, while a genuinely different row does.
   */
  function useStore<TSelected>(selector?: (value: TState) => TSelected): TState | TSelected {
    // The last result handed to React. Reused whenever the fresh one is equivalent, which is what keeps
    // getSnapshot stable without pinning it to an input that can go stale.
    const previous = useRef<{ value: unknown } | null>(null);

    const getSelected = useCallback(() => {
      const current = getState();
      if (!selector) return current;
      const next = selector(current);
      if (previous.current !== null && equivalent(previous.current.value, next)) {
        return previous.current.value as TSelected;
      }
      previous.current = { value: next };
      return next;
    }, [selector]);

    const value = useSyncExternalStore(subscribe, getSelected, getSelected);
    useDebugValue(value);
    return value;
  }

  const store = useStore as ShenoraStore<TState, TActions>;
  store.getState = getState;
  store.subscribe = subscribe;
  store.actions = options.actions ? options.actions(io) : ({} as TActions);
  store.reset = (): void => {
    snapshotLoaded = false;
    setState(initial);
  };
  return store;
}
