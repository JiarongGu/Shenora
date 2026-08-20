import type { ShenoraBridge } from './bridge.js';
import type { ShenoraEventBus } from './eventBus.js';
import { createShenoraStore, type ShenoraStore } from './store.js';
import type { IpcError } from './types.js';

/**
 * Mirrors `Shenora.Core.Ipc.IpcRequestState` (design §4.2), pinned against the host by
 * `WireMirrorTests.Every_request_state_exists_on_both_sides`.
 */
export const IpcRequestStates = {
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Cancelled: 'cancelled',
} as const;

/** One of {@link IpcRequestStates}. */
export type IpcRequestState = (typeof IpcRequestStates)[keyof typeof IpcRequestStates];

/**
 * Mirrors `Shenora.Core.Ipc.IpcRequestEvents`, pinned against the host by
 * `WireMirrorTests.Request_event_names_match_the_host`.
 */
export const IpcRequestEventTypes = {
  Updated: 'REQUEST_UPDATED',

  /**
   * One or more request ids left the host with no corresponding `Updated` snapshot — history eviction
   * and `CLEAR_FINISHED`. Payload is `{ requestIds: string[] }`; {@link createRequestsStore} folds it
   * by deleting those ids.
   */
  Removed: 'REQUEST_REMOVED',
} as const;

/**
 * Mirrors the route names `Shenora.Modules.Requests.IpcRequestsModule` switches on, pinned by
 * `WireMirrorTests.Request_route_names_match_the_hosts_module`.
 */
export const IpcRequestRoutes = {
  List: 'LIST',
  Cancel: 'CANCEL',
  ClearFinished: 'CLEAR_FINISHED',
} as const;

/**
 * Default `Shenora.Core.Ipc.IpcRequestTrackerOptions.ModuleName` — pinned by
 * `WireMirrorTests.The_default_requests_module_name_matches_the_host`.
 */
export const IpcRequestsModuleName = 'SHENORA.REQUESTS';

/**
 * Mirrors `Shenora.Core.Ipc.IpcLabel` — human-facing text the HOST never renders itself: an
 * untranslated fallback plus an app i18n key and interpolation parameters.
 */
export interface IpcLabel {
  text?: string;
  key?: string;
  parameters?: Record<string, string>;
}

/**
 * Mirrors `Shenora.Core.Ipc.IpcProgress` — how far a tracked operation has gotten, in the APP's own
 * unit rather than a kit-assumed percent. `unit` is app-defined (`'bytes'`, `'files'`, `'percent'`)
 * and the kit never interprets it.
 *
 * ⚠ `total` undefined means there is NO known total — an absolute count with nothing to divide by,
 * never zero. Render a ratio only when it is set; the README has the example.
 */
export interface IpcProgress {
  value: number;
  total?: number;
  unit?: string;
}

/**
 * Mirrors `Shenora.Core.Ipc.IpcRequestStatus` — a full snapshot of one tracked operation. Every lifecycle
 * transition (start, progress, terminal) publishes one of these under `REQUEST_UPDATED`, so the
 * client folds by `id`: last write wins, with no cross-type ordering hazard.
 */
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
 * State behind {@link useShenoraRequests}. `byId` is the only thing a reducer writes; the two bands
 * are derived from it on every read.
 *
 * ⚠ Most requests never appear at all: one that finishes inside the host's grace period is never
 * announced, so this lists work that is actually TAKING A WHILE, not every call the page made.
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
   * configured scope so a scoped store's "clear completed" cannot wipe another scope's history
   * host-side. Nothing is mutated locally: the host's {@link IpcRequestEventTypes.Removed} is the only
   * thing that removes a row.
   */
  clearFinished: () => string;
}

function index(list: IpcRequestStatus[]): Record<string, IpcRequestStatus> {
  const byId: Record<string, IpcRequestStatus> = {};
  for (const operation of list) byId[operation.id] = operation;
  return byId;
}

/** The one place `running`/`finished` are computed — wrap `byId` here, nowhere else. */
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
   * `IpcRequestTrackerOptions.ModuleName` — default `'SHENORA.REQUESTS'` on both sides — for an app
   * that renamed it to avoid colliding with one of its own modules.
   */
  module?: string;
  /**
   * Optional app-defined scope, applied to THREE places: the bus subscription, the actions' request
   * envelope, and the initial `LIST` snapshot's PAYLOAD (`IpcRequestsModule` reads its scope filter
   * from the payload, not the envelope).
   *
   * ⚠ All three or none. Threading it into only the first two loads every scope on first subscribe and
   * then never sheds the out-of-scope rows, since no delta for them ever arrives.
   */
  scope?: string;
  /** Test/multi-transport seams. Default: the shared bridge and event bus. */
  bridge?: ShenoraBridge;
  bus?: ShenoraEventBus;
}

/**
 * Build a store instance over the requests module — the factory {@link useShenoraRequests} is built
 * from. Use it directly for a renamed host module, a scope-filtered instance, or a fake bridge/bus.
 */
export function createRequestsStore(
  options: RequestsStoreOptions = {},
): ShenoraStore<RequestsState, RequestsActions> {
  const module = options.module ?? IpcRequestsModuleName;
  return createShenoraStore<RequestsState, RequestsActions>(module, {
    initial: makeState({}),
    // LIST is the snapshot source (design §4.6): a store cannot replay a stream, so a component that
    // mounts while work is already running gets it from here before folding any deltas. The payload
    // carries `scope` so the initial load is filtered the same way the deltas are.
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
      // The ONE removal delta: deletes exactly the ids the host named, regardless of status. An id
      // this store never had is a no-op.
      [IpcRequestEventTypes.Removed]: (state, payload: { requestIds: string[] }) => {
        const byId = { ...state.byId };
        for (const id of payload.requestIds) delete byId[id];
        return makeState(byId);
      },
    },
    actions: ({ post }) => ({
      cancel: (requestId: string) => post(IpcRequestRoutes.Cancel, { payload: { requestId } }),
      clearFinished: () =>
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
 * id — one subscription however many components read it, and a late mounter renders CURRENT state.
 * `running`/`finished` are selectors an activity panel reads directly:
 * `useShenoraRequests((s) => s.running)`.
 *
 * Bound to the default module and no scope — use {@link createRequestsStore} for a renamed module or
 * a scope-filtered instance.
 */
export const useShenoraRequests = createRequestsStore();
