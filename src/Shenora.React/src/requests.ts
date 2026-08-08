import type { ShenoraBridge } from './bridge.js';
import type { ShenoraEventBus } from './eventBus.js';
import { createShenoraStore, type ShenoraStore } from './store.js';
import type { IpcError } from './types.js';

/**
 * Mirrors `Shenora.Ipc.IpcRequestState` (design §4.2) — crosses the wire as its camelCase name for
 * free: `IpcJson` already installs a camelCase `JsonStringEnumConverter`, so no per-type wiring is
 * needed on either side. Pinned against the host by
 * `WireMirrorTests.Every_request_state_exists_on_both_sides` — a status added on one side and
 * not the other fails that test by name, not by a green suite that never looked.
 */
export const IpcRequestStates = {
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Cancelled: 'cancelled',
  // NO 'waiting'. A request is IN FLIGHT or DONE — the XHR model this mirrors has no parked state,
  // and neither does the host since D66. Work that parks awaiting a human is host-initiated work
  // (a queued mission), which reports on its own event stream rather than as a request.
} as const;

/** One of {@link IpcRequestStates}. */
export type IpcRequestState = (typeof IpcRequestStates)[keyof typeof IpcRequestStates];

/**
 * Mirrors `Shenora.Ipc.IpcRequestEvents` — pinned against the host by
 * `WireMirrorTests.Request_event_names_match_the_host` (ALSO IN THIS BATCH, whole-branch review):
 * these were bare string literals with nothing comparing them to the host's own constants, so a host
 * rename left the suite green and the client permanently deaf to the renamed event.
 */
export const IpcRequestEventTypes = {
  Updated: 'REQUEST_UPDATED',

  /**
   * One or more request ids left the host with no corresponding `Updated` snapshot — history eviction
   * and `CLEAR_FINISHED`. Payload is `{ requestIds: string[] }`; {@link createRequestsStore} folds it
   * by deleting those ids. One authoritative event, so a client never has to guess what the host
   * removed.
   */
  Removed: 'REQUEST_REMOVED',
} as const;

/**
 * Mirrors the route names `Shenora.Ipc.IpcRequestsModule` switches on (its own
 * `ListType`/`CancelType`/`ClearFinishedType`/`ResumeType` constants) — pinned by
 * `WireMirrorTests.Request_route_names_match_the_hosts_module`, same rationale as
 * {@link IpcRequestEventTypes}.
 */
export const IpcRequestRoutes = {
  List: 'LIST',
  Cancel: 'CANCEL',
  ClearFinished: 'CLEAR_FINISHED',
  // THREE routes — the same three `XMLHttpRequest` offers. RESUME/WAIT/DISMISS went with the waiting
  // band (D66), and the wire-mirror test pins this object's SIZE so a retired name cannot creep back.
} as const;

/**
 * Default `Shenora.Ipc.IpcRequestTrackerOptions.ModuleName` — pinned by
 * `WireMirrorTests.The_default_requests_module_name_matches_the_host`.
 */
export const IpcRequestsModuleName = 'SHENORA.REQUESTS';

/**
 * Mirrors `Shenora.Ipc.IpcLabel` — human-facing text the HOST never renders itself: an
 * untranslated fallback plus an app i18n key and interpolation parameters (headless, D13).
 */
export interface IpcLabel {
  text?: string;
  key?: string;
  parameters?: Record<string, string>;
}

/**
 * Mirrors `Shenora.Ipc.IpcProgress` — how far a tracked operation has gotten, in the APP's own
 * unit, never a kit-assumed percent (generic-library audit, before publish: percent is not the
 * mechanism, it is one way an app happens to measure). `total` is the denominator when one is known;
 * `undefined` means there is NO known total — an absolute count with nothing to divide by (bytes
 * streamed so far off a chunked response, say), never zero. `unit` is app-defined, like `kind`
 * (`'bytes'`, `'files'`, `'percent'`) — the kit never interprets it and ships no percent helper: render
 * a ratio only when `total` is set, e.g. `total ? (value / total) * 100 : undefined` — that division
 * is the consumer's own policy (see the README example).
 */
export interface IpcProgress {
  value: number;
  total?: number;
  unit?: string;
}

/**
 * Mirrors `Shenora.Ipc.IpcRequestStatus` — a full snapshot of one tracked operation. Every lifecycle
 * transition (start, progress, terminal) publishes one of these under `REQUEST_UPDATED`, so the
 * client folds by `id`: last write wins, with no cross-type ordering hazard.
 */
// ⚠ NO `title` and NO `cancellable` any more. Both came from the options record a caller used to pass
// when it STARTED an operation, and D66 deleted that record — a request needs no title (the page sent it
// and knows what it asked for), and every request is abortable, so a flag saying so carried nothing.
// The wire-mirror test caught both still declared here after the host dropped them, which is exactly the
// failure it exists to catch: a client field the host never sends is always `undefined` at runtime.
//
// ⚠ And keep prose like this OUTSIDE the interface body — that same parser reads `word:` inside the
// braces as a field, so a comment containing a colon invents members that do not exist. Met immediately.
export interface IpcRequestStatus {
  id: string;
  module: string;
  type: string;
  scope?: string;
  state: IpcRequestState;
  progress?: IpcProgress;
  detail?: IpcLabel;
  error?: IpcError;
  startedAt: string;
  finishedAt?: string;
}

/** The terminal states — everything that is not in flight. */
const TERMINAL_STATES: ReadonlySet<IpcRequestState> = new Set([
  IpcRequestStates.Completed,
  IpcRequestStates.Failed,
  IpcRequestStates.Cancelled,
]);

/**
 * State behind {@link useShenoraRequests}. TWO bands, because a request is in flight or done:
 *
 * | Band | Getter |
 * |---|---|
 * | In flight | {@link running} |
 * | Finished | {@link finished} — prunable via `clearFinished` |
 *
 * Every getter is DERIVED from `byId` on each read — never a second copy a fold has to remember to
 * keep in sync. `byId` is the only thing any reducer here writes.
 *
 * ⚠ Most requests never appear at all: one that finishes inside the host's grace period is never
 * announced, so this store is a list of work that is actually TAKING A WHILE rather than a log of
 * every call the page made.
 */
export interface RequestsState {
  byId: Record<string, IpcRequestStatus>;
  /** Every currently-running operation, in `byId` order. */
  readonly running: IpcRequestStatus[];
  /** Every operation that reached a terminal status (completed/failed/cancelled). */
  readonly finished: IpcRequestStatus[];
}

/** Fire-and-forget actions exposed on {@link useShenoraRequests}, routed to `IpcRequestsModule`. */
export interface RequestsActions {
  /** `CANCEL { requestId }` — the app-level cancel route `ipc-contracts` prescribes. */
  cancel: (requestId: string) => string;
  /**
   * `CLEAR_FINISHED { scope? }` — drop retained finished history, forwarding this store's own
   * configured scope (generic-library audit finding 1) so a scoped store's "clear completed" cannot
   * wipe another scope's history host-side. No local mutation here: the host's
   * `REQUEST_REMOVED` (finding 4) is the only thing that removes a row from this store now — see
   * {@link IpcRequestEventTypes.Removed}. It used to carry an optimistic local prune of every TERMINAL
   * entry, added because removals had no wire event at all; that guess is retired now that one exists.
   */
  clearFinished: () => string;
}

function index(list: IpcRequestStatus[]): Record<string, IpcRequestStatus> {
  const byId: Record<string, IpcRequestStatus> = {};
  for (const operation of list) byId[operation.id] = operation;
  return byId;
}

/**
 * The one place `running`/`waiting`/`finished` are computed — wrap `byId` here, nowhere else.
 */
function makeState(byId: Record<string, IpcRequestStatus>): RequestsState {
  return {
    byId,
    get running() {
      return Object.values(byId).filter((request) => request.state === IpcRequestStates.Running);
    },
    get finished() {
      return Object.values(byId).filter((request) => TERMINAL_STATES.has(request.state));
    },
  };
}

/** Test/alternate-transport seams, a renamed host module, and an optional scope filter, for {@link createRequestsStore}. */
export interface RequestsStoreOptions {
  /**
   * The request/event module this store talks to. Must match the host's
   * `IpcRequestTrackerOptions.ModuleName` — default `'SHENORA.REQUESTS'` on both sides — when an app
   * renamed it to avoid a collision with one of its own module names (the duplicate-module guard
   * `IpcRequestsModule`'s own docs describe). A store bound to the default name cannot reach a
   * renamed host at all, which is exactly the gap this field closes.
   */
  module?: string;
  /**
   * Optional app-defined scope, applied to THREE places so the store stays internally consistent:
   * the bus subscription (only deltas whose event scope matches are folded), the actions' request
   * envelope, and the initial `LIST` snapshot's payload (`IpcRequestsModule` reads its scope filter
   * from the payload, not the envelope — see `IpcRequestsModule.RouteMessageAsync`). Threading it
   * into only the first two would load every scope on first subscribe and never remove the
   * out-of-scope rows, since no delta for them ever arrives: a silent, permanent leak.
   */
  scope?: string;
  /** Test/multi-transport seams. Default: the shared bridge and event bus. */
  bridge?: ShenoraBridge;
  bus?: ShenoraEventBus;
}

/**
 * Build a store instance over the requests module — the factory {@link useShenoraRequests}
 * itself is built from. Exposed (rather than only the ready-made hook) for the same reason
 * `WindowCommands` takes an optional bridge: a test needs a fake bridge/bus
 * (`requests.test.ts`), an app that renamed the host's `IpcRequestTrackerOptions.ModuleName`
 * needs a store bound to that name instead of the unreachable default, and an app running a
 * secondary window or auxiliary session needs its own scope-filtered instance instead of being
 * stuck with the shared, unscoped default.
 */
export function createRequestsStore(
  options: RequestsStoreOptions = {},
): ShenoraStore<RequestsState, RequestsActions> {
  const module = options.module ?? IpcRequestsModuleName;
  return createShenoraStore<RequestsState, RequestsActions>(module, {
    initial: makeState({}),
    // LIST is the snapshot source (design §4.6): a store cannot replay a stream, so a component
    // that mounts while work is already running gets it from here before folding any deltas.
    // The payload carries `scope` so the initial load is filtered the SAME way the deltas are
    // (below, and via createShenoraStore's own `scope` option) — both halves must agree, or a
    // scoped store loads every scope once and then never sheds the out-of-scope rows.
    snapshot: {
      type: IpcRequestRoutes.List,
      payload: options.scope !== undefined ? { scope: options.scope } : undefined,
      apply: (_state, data) => makeState(index(data as IpcRequestStatus[])),
    },
    on: {
      // ONE event type for every transition (design §4.3) — last-write-wins by id, so folding needs
      // no ordering logic and no cross-type races.
      [IpcRequestEventTypes.Updated]: (state, payload: IpcRequestStatus) =>
        makeState({ ...state.byId, [payload.id]: payload }),
      // The ONE removal delta (Finding 4, generic-library audit), replacing the two hand-written
      // optimistic prunes `clearFinished`/`resume` used to carry (see their own docs below) — deletes
      // exactly the ids the host named, regardless of status; an id this store never had is a no-op.
      [IpcRequestEventTypes.Removed]: (state, payload: { requestIds: string[] }) => {
        const byId = { ...state.byId };
        for (const id of payload.requestIds) delete byId[id];
        return makeState(byId);
      },
    },
    actions: ({ post }) => ({
      cancel: (requestId: string) => post(IpcRequestRoutes.Cancel, { payload: { requestId } }),
      clearFinished: () =>
        // Forward THIS store's own configured scope (Finding 1, generic-library audit) — the same
        // key the LIST snapshot payload already carries above. No local mutation here any more
        // (Finding 4): the host's REQUEST_REMOVED is the ONLY thing that removes a row now, which
        // is also what makes the scope threading safe to add — nothing here can diverge from what
        // the host actually cleared.
        post(IpcRequestRoutes.ClearFinished, {
          payload: options.scope !== undefined ? { scope: options.scope } : undefined,
        }),
    }),
    scope: options.scope,
    bridge: options.bridge,
    bus: options.bus,
  });
}

/**
 * The client side of the operations primitive (design §4.6, `IpcRequestsModule` +
 * `IIpcRequestTracker`): snapshots via `LIST` on first subscribe, then folds `REQUEST_UPDATED` by
 * id — one subscription however many components read it, and a late mounter renders CURRENT state
 * because the host is authoritative (the store primitive's own late-mounter case is now
 * host-backed end to end). `running`/`waiting`/`finished` are selectors an activity panel or status
 * bar reads directly: `useShenoraRequests((s) => s.waiting)` for the one "needs you" bucket
 * (every entry in it reached the band the same way, so there is no sub-case to filter for; read
 * `waitReason` for WHY it is waiting). Bound to the default module/no scope — use
 * {@link createRequestsStore} directly for a renamed module or a scope-filtered instance.
 *
 * Headless, per D13: no component, no UI opinion, no `ProcessType`-style enum — what an operation
 * IS stays the app's `kind` string; this only carries the uniform lifecycle around it.
 */
export const useShenoraRequests = createRequestsStore();
