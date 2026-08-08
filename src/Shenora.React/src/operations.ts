import type { ShenoraBridge } from './bridge.js';
import type { ShenoraEventBus } from './eventBus.js';
import { createShenoraStore, type ShenoraStore } from './store.js';
import type { IpcError } from './types.js';

/**
 * Mirrors `Shenora.Ipc.OperationStatus` (design §4.2) — crosses the wire as its camelCase name for
 * free: `IpcJson` already installs a camelCase `JsonStringEnumConverter`, so no per-type wiring is
 * needed on either side. Pinned against the host by
 * `WireMirrorTests.Every_operation_status_exists_on_both_sides` — a status added on one side and
 * not the other fails that test by name, not by a green suite that never looked.
 */
export const OperationStatuses = {
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Cancelled: 'cancelled',
  // NO 'waiting'. A request is IN FLIGHT or DONE — the XHR model this mirrors has no parked state,
  // and neither does the host since D66. Work that parks awaiting a human is host-initiated work
  // (a queued mission), which reports on its own event stream rather than as a request.
} as const;

/** One of {@link OperationStatuses}. */
export type OperationStatus = (typeof OperationStatuses)[keyof typeof OperationStatuses];

/**
 * Mirrors `Shenora.Ipc.OperationEvents` — pinned against the host by
 * `WireMirrorTests.Operation_event_names_match_the_host` (ALSO IN THIS BATCH, whole-branch review):
 * these were bare string literals with nothing comparing them to the host's own constants, so a host
 * rename left the suite green and the client permanently deaf to the renamed event.
 */
export const OperationEventTypes = {
  Updated: 'OPERATION_UPDATED',

  /**
   * A client asked to wait a running operation (generic-library audit finding 3, renamed from
   * `PAUSE_REQUESTED`) — the owning module should call the host's `IOperation.Wait` once it has
   * actually stopped. Not subscribed by {@link createOperationsStore} itself, same as
   * {@link ResumeRequested}: it targets the owning module's own service, not the generic operations
   * store.
   */

  /**
   * One or more operation ids left the host registry with no corresponding `Updated` snapshot —
   * `MaxHistory` eviction, `CLEAR_FINISHED`, and the interrupted-entry drop on `RESUME` (Finding 4,
   * generic-library audit). Payload is `{ operationIds: string[] }`; {@link createOperationsStore}
   * folds it by deleting those ids, which is what let the two hand-written optimistic prunes
   * (`clearFinished`/`resume` used to carry one each) be removed — one authoritative event that
   * cannot diverge from what the host actually did, replacing two guesses that could.
   */
  Removed: 'OPERATION_REMOVED',
} as const;

/**
 * Mirrors the route names `Shenora.Ipc.OperationsModule` switches on (its own
 * `ListType`/`CancelType`/`ClearFinishedType`/`ResumeType` constants) — pinned by
 * `WireMirrorTests.Operation_route_names_match_the_hosts_facade`, same rationale as
 * {@link OperationEventTypes}.
 */
export const OperationRoutes = {
  List: 'LIST',
  Cancel: 'CANCEL',
  ClearFinished: 'CLEAR_FINISHED',

  /** Decline a pending Waiting offer by id — mirrors {@link Cancel}'s shape. */

  /**
   * Ask the owning module to wait a running operation by id (generic-library audit finding 3,
   * renamed from `PAUSE`) — mirrors {@link Resume}'s shape (`{ operationId }` → `{ requested }`).
   * Asking is not acting: the owning module's own `IOperation.Wait` is what actually stops the work
   * and publishes the transition, same split as {@link Resume} vs the host's `IOperation.Resume`.
   */

} as const;

/**
 * Default `Shenora.Ipc.OperationRegistryOptions.ModuleName` — pinned by
 * `WireMirrorTests.The_default_operations_module_name_matches_the_host`.
 */
export const OperationModuleName = 'SHENORA.OPERATIONS';

/**
 * Mirrors `Shenora.Ipc.OperationLabel` — human-facing text the HOST never renders itself: an
 * untranslated fallback plus an app i18n key and interpolation parameters (headless, D13).
 */
export interface OperationLabel {
  text?: string;
  key?: string;
  parameters?: Record<string, string>;
}

/**
 * Mirrors `Shenora.Ipc.OperationProgress` — how far a tracked operation has gotten, in the APP's own
 * unit, never a kit-assumed percent (generic-library audit, before publish: percent is not the
 * mechanism, it is one way an app happens to measure). `total` is the denominator when one is known;
 * `undefined` means there is NO known total — an absolute count with nothing to divide by (bytes
 * streamed so far off a chunked response, say), never zero. `unit` is app-defined, like `kind`
 * (`'bytes'`, `'files'`, `'percent'`) — the kit never interprets it and ships no percent helper: render
 * a ratio only when `total` is set, e.g. `total ? (value / total) * 100 : undefined` — that division
 * is the consumer's own policy (see the README example).
 */
export interface OperationProgress {
  value: number;
  total?: number;
  unit?: string;
}

/**
 * Mirrors `Shenora.Ipc.OperationInfo` — a full snapshot of one tracked operation. Every lifecycle
 * transition (start, progress, terminal) publishes one of these under `OPERATION_UPDATED`, so the
 * client folds by `id`: last write wins, with no cross-type ordering hazard.
 */
export interface OperationInfo {
  id: string;
  module: string;
  kind: string;
  scope?: string;
  status: OperationStatus;
  progress?: OperationProgress;
  title?: OperationLabel;
  detail?: OperationLabel;
  error?: IpcError;
  cancellable: boolean;
  // No `resumePayload` (0.2.0 design pass): an opaque app checkpoint token used to ride here, for
  // announcing crash-interrupted work the host never started. Cut with the rest of that half — the
  // app owns its own checkpoints, and a resumed run is a fresh operation.
  startedAt: string;
  finishedAt?: string;
}

/**
 * Statuses that count as finished history. Deliberately excludes `waiting` — a resumable operation
 * (whether a live `Wait()` or a crash-announced checkpoint) is a pending offer, "distinct from
 * finished history" (`Shenora.Ipc.OperationStatus.Waiting`), not a terminal outcome.
 */
const TERMINAL_STATUSES: ReadonlySet<OperationStatus> = new Set([
  OperationStatuses.Completed,
  OperationStatuses.Failed,
  OperationStatuses.Cancelled,
]);

/**
 * State behind {@link useShenoraOperations}. Mirrors the host's THREE bands (design §5A.2):
 *
 * | Band | Getter | Exits |
 * |---|---|---|
 * | Active | {@link running} | complete / fail / cancel / wait |
 * | Waiting — stopped, resumable, awaiting a decision | {@link waiting} | resume / dismiss / complete / fail |
 * | Terminal | {@link finished} | — (prunable via `clearFinished`) |
 *
 * `waiting` is now the WHOLE band, not a union of two getters — the host's `OperationStatus` carries
 * only one waiting value (the former `paused`/`interrupted` pair collapsed into it, since every host
 * transition already treated them as one band). A waiting entry is a pending offer, not finished
 * history: the host never prunes it on its own, and only `Resume` or `Dismiss` removes it. Every
 * getter here is DERIVED from `byId` on every read — never a second copy a fold has to remember to
 * keep in sync. `byId` itself is the only thing any reducer here ever writes.
 */
export interface OperationsState {
  byId: Record<string, OperationInfo>;
  /** Every currently-running operation, in `byId` order. */
  readonly running: OperationInfo[];
  /** Every operation that reached a terminal status (completed/failed/cancelled). */
  readonly finished: OperationInfo[];
}

/** Fire-and-forget actions exposed on {@link useShenoraOperations}, routed to `OperationsModule`. */
export interface OperationsActions {
  /** `CANCEL { operationId }` — the app-level cancel route `ipc-contracts` prescribes. */
  cancel: (operationId: string) => string;
  /**
   * `CLEAR_FINISHED { scope? }` — drop retained finished history, forwarding this store's own
   * configured scope (generic-library audit finding 1) so a scoped store's "clear completed" cannot
   * wipe another scope's history host-side. No local mutation here: the host's
   * `OPERATION_REMOVED` (finding 4) is the only thing that removes a row from this store now — see
   * {@link OperationEventTypes.Removed}. It used to carry an optimistic local prune of every TERMINAL
   * entry, added because removals had no wire event at all; that guess is retired now that one exists.
   */
  clearFinished: () => string;
}

function index(list: OperationInfo[]): Record<string, OperationInfo> {
  const byId: Record<string, OperationInfo> = {};
  for (const operation of list) byId[operation.id] = operation;
  return byId;
}

/**
 * The one place `running`/`waiting`/`finished` are computed — wrap `byId` here, nowhere else.
 */
function makeState(byId: Record<string, OperationInfo>): OperationsState {
  return {
    byId,
    get running() {
      return Object.values(byId).filter((operation) => operation.status === OperationStatuses.Running);
    },
    get finished() {
      return Object.values(byId).filter((operation) => TERMINAL_STATUSES.has(operation.status));
    },
  };
}

/** Test/alternate-transport seams, a renamed host module, and an optional scope filter, for {@link createOperationsStore}. */
export interface OperationsStoreOptions {
  /**
   * The request/event module this store talks to. Must match the host's
   * `OperationRegistryOptions.ModuleName` — default `'OPERATIONS'` on both sides — when an app
   * renamed it to avoid a collision with one of its own module names (the duplicate-module guard
   * `OperationsModule`'s own docs describe). A store bound to the default name cannot reach a
   * renamed host at all, which is exactly the gap this field closes.
   */
  module?: string;
  /**
   * Optional app-defined scope, applied to THREE places so the store stays internally consistent:
   * the bus subscription (only deltas whose event scope matches are folded), the actions' request
   * envelope, and the initial `LIST` snapshot's payload (`OperationsModule` reads its scope filter
   * from the payload, not the envelope — see `OperationsModule.RouteMessageAsync`). Threading it
   * into only the first two would load every scope on first subscribe and never remove the
   * out-of-scope rows, since no delta for them ever arrives: a silent, permanent leak.
   */
  scope?: string;
  /** Test/multi-transport seams. Default: the shared bridge and event bus. */
  bridge?: ShenoraBridge;
  bus?: ShenoraEventBus;
}

/**
 * Build a store instance over the operations module — the factory {@link useShenoraOperations}
 * itself is built from. Exposed (rather than only the ready-made hook) for the same reason
 * `WindowCommands` takes an optional bridge: a test needs a fake bridge/bus
 * (`operations.test.ts`), an app that renamed the host's `OperationRegistryOptions.ModuleName`
 * needs a store bound to that name instead of the unreachable default, and an app running a
 * secondary window or auxiliary session needs its own scope-filtered instance instead of being
 * stuck with the shared, unscoped default.
 */
export function createOperationsStore(
  options: OperationsStoreOptions = {},
): ShenoraStore<OperationsState, OperationsActions> {
  const module = options.module ?? OperationModuleName;
  return createShenoraStore<OperationsState, OperationsActions>(module, {
    initial: makeState({}),
    // LIST is the snapshot source (design §4.6): a store cannot replay a stream, so a component
    // that mounts while work is already running gets it from here before folding any deltas.
    // The payload carries `scope` so the initial load is filtered the SAME way the deltas are
    // (below, and via createShenoraStore's own `scope` option) — both halves must agree, or a
    // scoped store loads every scope once and then never sheds the out-of-scope rows.
    snapshot: {
      type: OperationRoutes.List,
      payload: options.scope !== undefined ? { scope: options.scope } : undefined,
      apply: (_state, data) => makeState(index(data as OperationInfo[])),
    },
    on: {
      // ONE event type for every transition (design §4.3) — last-write-wins by id, so folding needs
      // no ordering logic and no cross-type races.
      [OperationEventTypes.Updated]: (state, payload: OperationInfo) =>
        makeState({ ...state.byId, [payload.id]: payload }),
      // The ONE removal delta (Finding 4, generic-library audit), replacing the two hand-written
      // optimistic prunes `clearFinished`/`resume` used to carry (see their own docs below) — deletes
      // exactly the ids the host named, regardless of status; an id this store never had is a no-op.
      [OperationEventTypes.Removed]: (state, payload: { operationIds: string[] }) => {
        const byId = { ...state.byId };
        for (const id of payload.operationIds) delete byId[id];
        return makeState(byId);
      },
    },
    actions: ({ post }) => ({
      cancel: (operationId: string) => post(OperationRoutes.Cancel, { payload: { operationId } }),
      clearFinished: () =>
        // Forward THIS store's own configured scope (Finding 1, generic-library audit) — the same
        // key the LIST snapshot payload already carries above. No local mutation here any more
        // (Finding 4): the host's OPERATION_REMOVED is the ONLY thing that removes a row now, which
        // is also what makes the scope threading safe to add — nothing here can diverge from what
        // the host actually cleared.
        post(OperationRoutes.ClearFinished, {
          payload: options.scope !== undefined ? { scope: options.scope } : undefined,
        }),
    }),
    scope: options.scope,
    bridge: options.bridge,
    bus: options.bus,
  });
}

/**
 * The client side of the operations primitive (design §4.6, `OperationsModule` +
 * `IOperationRegistry`): snapshots via `LIST` on first subscribe, then folds `OPERATION_UPDATED` by
 * id — one subscription however many components read it, and a late mounter renders CURRENT state
 * because the host is authoritative (the store primitive's own late-mounter case is now
 * host-backed end to end). `running`/`waiting`/`finished` are selectors an activity panel or status
 * bar reads directly: `useShenoraOperations((s) => s.waiting)` for the one "needs you" bucket
 * (every entry in it reached the band the same way, so there is no sub-case to filter for; read
 * `waitReason` for WHY it is waiting). Bound to the default module/no scope — use
 * {@link createOperationsStore} directly for a renamed module or a scope-filtered instance.
 *
 * Headless, per D13: no component, no UI opinion, no `ProcessType`-style enum — what an operation
 * IS stays the app's `kind` string; this only carries the uniform lifecycle around it.
 */
export const useShenoraOperations = createOperationsStore();
