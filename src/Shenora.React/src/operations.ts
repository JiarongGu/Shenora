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
  /**
   * A crash-announced, resumable operation re-registered from the app's own checkpoint
   * (`IOperationRegistry.RegisterInterrupted`) — a pending RESUME offer, not finished history: the
   * other half of the WAITING band alongside `paused` (design §5A.2,
   * {@link OperationsState.waiting}). The host **never prunes it** on its own; only `Resume` (the
   * host's own `RequestResume`, via the client's `resume` action) or the client's `DISMISS` route
   * removes it — an entry left un-actioned stays offered forever, on purpose.
   */
  Interrupted: 'interrupted',
  /**
   * Stopped mid-flight WITHOUT crashing, awaiting a decision (expired credentials, a throttling
   * provider, DNS not yet propagated, a migration awaiting confirmation) — half of the WAITING band
   * alongside `interrupted` (design §5A.2, {@link OperationsState.waiting}): never pruned as
   * history, and not one of the terminal statuses in {@link OperationsState.finished}. Reached from
   * `running` via the host's own `IOperation.Pause`; exits via the host's `IOperation.Resume` (back
   * to `running`), the `DISMISS` route (to `cancelled`), or a direct complete/fail.
   */
  Paused: 'paused',
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
  ResumeRequested: 'OPERATION_RESUME_REQUESTED',
  /**
   * A client asked to pause a running operation (generic-library audit finding 3) — the owning
   * module should call the host's `IOperation.Pause` once it has actually stopped. Not subscribed by
   * {@link createOperationsStore} itself, same as {@link ResumeRequested}: it targets the owning
   * module's own service, not the generic operations store.
   */
  PauseRequested: 'OPERATION_PAUSE_REQUESTED',
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
 * Mirrors the route names `Shenora.Ipc.OperationsFacade` switches on (its own
 * `ListType`/`CancelType`/`ClearFinishedType`/`ResumeType` constants) — pinned by
 * `WireMirrorTests.Operation_route_names_match_the_hosts_facade`, same rationale as
 * {@link OperationEventTypes}.
 */
export const OperationRoutes = {
  List: 'LIST',
  Cancel: 'CANCEL',
  ClearFinished: 'CLEAR_FINISHED',
  Resume: 'RESUME',
  /** Decline a pending Paused/Interrupted offer by id — mirrors {@link Cancel}'s shape. */
  Dismiss: 'DISMISS',
  /**
   * Ask the owning module to pause a running operation by id (generic-library audit finding 3) —
   * mirrors {@link Resume}'s shape (`{ operationId }` → `{ requested }`). Asking is not acting: the
   * owning module's own `IOperation.Pause` is what actually stops the work and publishes the
   * transition, same split as {@link Resume} vs the host's `IOperation.Resume`.
   */
  Pause: 'PAUSE',
} as const;

/**
 * Default `Shenora.Ipc.OperationRegistryOptions.ModuleName` — pinned by
 * `WireMirrorTests.The_default_operations_module_name_matches_the_host`.
 */
export const OperationModuleName = 'OPERATIONS';

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
  progress?: number;
  title?: OperationLabel;
  detail?: OperationLabel;
  /** Why the operation is `'paused'` — an app-defined string, like `kind`; the kit never interprets it. */
  pauseReason?: string;
  error?: IpcError;
  cancellable: boolean;
  resumePayload?: string;
  startedAt: string;
  finishedAt?: string;
}

/**
 * Statuses that count as finished history. Deliberately excludes `interrupted` — a crash-announced,
 * resumable operation is a pending resume offer, "distinct from finished history"
 * (`Shenora.Ipc.OperationStatus.Interrupted`), not a terminal outcome.
 */
const TERMINAL_STATUSES: ReadonlySet<OperationStatus> = new Set([
  OperationStatuses.Completed,
  OperationStatuses.Failed,
  OperationStatuses.Cancelled,
]);

/**
 * The WAITING band (design §5A.2): stopped, resumable, awaiting a decision — exactly what
 * `Dismiss`/`RequestResume` both accept, and never pruned as history (an offer is not history).
 * Defined ONCE, same discipline as {@link TERMINAL_STATUSES}, so {@link OperationsState.waiting} is
 * *derived* from this set rather than a hand-listed pair repeated across getters — a second,
 * independently-maintained copy is exactly how `interrupted` fell into no band in the first place.
 */
const WAITING_STATUSES: ReadonlySet<OperationStatus> = new Set([
  OperationStatuses.Paused,
  OperationStatuses.Interrupted,
]);

/**
 * State behind {@link useShenoraOperations}. Mirrors the host's THREE bands (design §5A.2), not five
 * bare statuses:
 *
 * | Band | Getter(s) | Exits |
 * |---|---|---|
 * | Active | {@link running} | complete / fail / cancel / pause |
 * | Waiting — stopped, resumable, awaiting a decision | {@link paused}, {@link interrupted}, {@link waiting} (their union) | resume / dismiss / complete / fail |
 * | Terminal | {@link finished} | — (prunable via `clearFinished`) |
 *
 * An `'interrupted'` entry is a pending RESUME **offer**, not finished history: the host never prunes
 * it on its own, and only `Resume` or `Dismiss` removes it — the same rule `'paused'` follows, which
 * is why the two share the {@link waiting} getter. Every getter here is DERIVED from `byId` on every
 * read — never a second copy a fold has to remember to keep in sync. `byId` itself is the only thing
 * any reducer here ever writes.
 */
export interface OperationsState {
  byId: Record<string, OperationInfo>;
  /** Every currently-running operation, in `byId` order. */
  readonly running: OperationInfo[];
  /** Every operation currently `'paused'` — one half of the WAITING band; see {@link waiting} for both. */
  readonly paused: OperationInfo[];
  /**
   * Every operation currently `'interrupted'` — a crash-announced, pending RESUME offer (the other
   * half of the WAITING band; see {@link waiting} for both). Never pruned by the host on its own:
   * only `Resume`/`Dismiss` remove it.
   */
  readonly interrupted: OperationInfo[];
  /**
   * The WAITING band (design §5A.2): {@link paused} ∪ {@link interrupted} — exactly the set
   * `Dismiss`/`RequestResume` both accept, so a status bar can render "needs you" as one bucket
   * without caring whether the process restarted in between.
   */
  readonly waiting: OperationInfo[];
  /** Every operation that reached a terminal status (completed/failed/cancelled). */
  readonly finished: OperationInfo[];
}

/** Fire-and-forget actions exposed on {@link useShenoraOperations}, routed to `OperationsFacade`. */
export interface OperationsActions {
  /** `CANCEL { operationId }` — the app-level cancel route `ipc-contracts` prescribes. */
  cancel: (operationId: string) => string;
  /**
   * `DISMISS { operationId }` — decline a pending `paused`/`interrupted` offer (design §5A.3),
   * mirroring {@link cancel}'s shape. No optimistic local prune: the host's `Dismiss` transitions the
   * entry to `cancelled` and publishes an ordinary `OPERATION_UPDATED` snapshot for it (same as a real
   * cancel), so the store already folds the result from the wire.
   */
  dismiss: (operationId: string) => string;
  /**
   * `CLEAR_FINISHED { scope? }` — drop retained finished history, forwarding this store's own
   * configured scope (generic-library audit finding 1) so a scoped store's "clear completed" cannot
   * wipe another scope's history host-side. No local mutation here: the host's
   * `OPERATION_REMOVED` (finding 4) is the only thing that removes a row from this store now — see
   * {@link OperationEventTypes.Removed}. It used to carry an optimistic local prune of every TERMINAL
   * entry, added because removals had no wire event at all; that guess is retired now that one exists.
   */
  clearFinished: () => string;
  /**
   * `RESUME { operationId }` — ask the host to continue a paused or interrupted operation. No local
   * mutation here: the host's `OPERATION_REMOVED` fold is what actually drops an `interrupted` entry
   * (the asymmetric half of design §5A.4 — a `paused` entry is deliberately LEFT IN PLACE host-side,
   * so no removal event ever arrives for it either). It used to carry an optimistic local prune
   * gated on the `interrupted` status, which was the source of this release's only Critical (it
   * pruned a `paused` row once, before the asymmetry was re-derived into it) — the authoritative
   * event now makes that guess unnecessary.
   */
  resume: (operationId: string) => string;
  /**
   * `PAUSE { operationId }` — ask the owning module to pause a running operation (generic-library
   * audit finding 3), mirroring {@link dismiss}'s shape. No optimistic local prune: asking never
   * changes the state by itself — the owning module's own `IOperation.Pause` is what publishes the
   * `paused` transition, and the store folds it from the wire like any other `OPERATION_UPDATED`.
   */
  pause: (operationId: string) => string;
}

function index(list: OperationInfo[]): Record<string, OperationInfo> {
  const byId: Record<string, OperationInfo> = {};
  for (const operation of list) byId[operation.id] = operation;
  return byId;
}

/**
 * The one place `running`/`paused`/`interrupted`/`waiting`/`finished` are computed — wrap `byId`
 * here, nowhere else.
 */
function makeState(byId: Record<string, OperationInfo>): OperationsState {
  return {
    byId,
    get running() {
      return Object.values(byId).filter((operation) => operation.status === OperationStatuses.Running);
    },
    get paused() {
      return Object.values(byId).filter((operation) => operation.status === OperationStatuses.Paused);
    },
    get interrupted() {
      return Object.values(byId).filter((operation) => operation.status === OperationStatuses.Interrupted);
    },
    get waiting() {
      return Object.values(byId).filter((operation) => WAITING_STATUSES.has(operation.status));
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
   * `OperationsFacade`'s own docs describe). A store bound to the default name cannot reach a
   * renamed host at all, which is exactly the gap this field closes.
   */
  module?: string;
  /**
   * Optional app-defined scope, applied to THREE places so the store stays internally consistent:
   * the bus subscription (only deltas whose event scope matches are folded), the actions' request
   * envelope, and the initial `LIST` snapshot's payload (`OperationsFacade` reads its scope filter
   * from the payload, not the envelope — see `OperationsFacade.RouteMessageAsync`). Threading it
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
      dismiss: (operationId: string) => post(OperationRoutes.Dismiss, { payload: { operationId } }),
      pause: (operationId: string) => post(OperationRoutes.Pause, { payload: { operationId } }),
      clearFinished: () =>
        // Forward THIS store's own configured scope (Finding 1, generic-library audit) — the same
        // key the LIST snapshot payload already carries above. No local mutation here any more
        // (Finding 4): the host's OPERATION_REMOVED is the ONLY thing that removes a row now, which
        // is also what makes the scope threading safe to add — nothing here can diverge from what
        // the host actually cleared.
        post(OperationRoutes.ClearFinished, {
          payload: options.scope !== undefined ? { scope: options.scope } : undefined,
        }),
      resume: (operationId: string) => post(OperationRoutes.Resume, { payload: { operationId } }),
    }),
    scope: options.scope,
    bridge: options.bridge,
    bus: options.bus,
  });
}

/**
 * The client side of the operations primitive (design §4.6, `OperationsFacade` +
 * `IOperationRegistry`): snapshots via `LIST` on first subscribe, then folds `OPERATION_UPDATED` by
 * id — one subscription however many components read it, and a late mounter renders CURRENT state
 * because the host is authoritative (the store primitive's own late-mounter case is now
 * host-backed end to end). `running`/`waiting`/`finished` are selectors an activity panel or status
 * bar reads directly: `useShenoraOperations((s) => s.waiting)` for the one "needs you" bucket
 * (`paused`/`interrupted` individually if the UI distinguishes a resume prompt from a pause-reason
 * display). Bound to the default module/no scope — use
 * {@link createOperationsStore} directly for a renamed module or a scope-filtered instance.
 *
 * Headless, per D13: no component, no UI opinion, no `ProcessType`-style enum — what an operation
 * IS stays the app's `kind` string; this only carries the uniform lifecycle around it.
 */
export const useShenoraOperations = createOperationsStore();
