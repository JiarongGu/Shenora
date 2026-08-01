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
  Interrupted: 'interrupted',
} as const;

/** One of {@link OperationStatuses}. */
export type OperationStatus = (typeof OperationStatuses)[keyof typeof OperationStatuses];

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
  error?: IpcError;
  cancellable: boolean;
  resumable: boolean;
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
 * State behind {@link useShenoraOperations}. `running`/`finished` are DERIVED getters computed
 * from `byId` on every read — never a second copy a fold has to remember to keep in sync. `byId`
 * itself is the only thing any reducer here ever writes.
 */
export interface OperationsState {
  byId: Record<string, OperationInfo>;
  /** Every currently-running operation, in `byId` order. */
  readonly running: OperationInfo[];
  /** Every operation that reached a terminal status (completed/failed/cancelled). */
  readonly finished: OperationInfo[];
}

/** Fire-and-forget actions exposed on {@link useShenoraOperations}, routed to `OperationsFacade`. */
export interface OperationsActions {
  /** `CANCEL { operationId }` — the app-level cancel route `ipc-contracts` prescribes. */
  cancel: (operationId: string) => string;
  /** `CLEAR_FINISHED` — drop retained finished history host-side. */
  clearFinished: () => string;
  /** `RESUME { operationId }` — continue an interrupted, resumable operation. */
  resume: (operationId: string) => string;
}

function index(list: OperationInfo[]): Record<string, OperationInfo> {
  const byId: Record<string, OperationInfo> = {};
  for (const operation of list) byId[operation.id] = operation;
  return byId;
}

/** The one place `running`/`finished` are computed — wrap `byId` here, nowhere else. */
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

/** Test/alternate-transport seams, plus an optional routing scope, for {@link createOperationsStore}. */
export interface OperationsStoreOptions {
  /** Optional app-defined routing scope, applied to both the subscription and the actions. */
  scope?: string;
  /** Test/multi-transport seams. Default: the shared bridge and event bus. */
  bridge?: ShenoraBridge;
  bus?: ShenoraEventBus;
}

/**
 * Build a store instance over the `OPERATIONS` module — the factory {@link useShenoraOperations}
 * itself is built from. Exposed (rather than only the ready-made hook) for the same reason
 * `WindowCommands` takes an optional bridge: a test needs a fake bridge/bus
 * (`operations.test.ts`), and an app running a secondary window or auxiliary session needs its own
 * routing-scoped instance instead of being stuck with the shared default.
 */
export function createOperationsStore(
  options: OperationsStoreOptions = {},
): ShenoraStore<OperationsState, OperationsActions> {
  return createShenoraStore<OperationsState, OperationsActions>('OPERATIONS', {
    initial: makeState({}),
    // LIST is the snapshot source (design §4.6): a store cannot replay a stream, so a component
    // that mounts while work is already running gets it from here before folding any deltas.
    snapshot: { type: 'LIST', apply: (_state, data) => makeState(index(data as OperationInfo[])) },
    on: {
      // ONE event type for every transition (design §4.3) — last-write-wins by id, so folding needs
      // no ordering logic and no cross-type races.
      OPERATION_UPDATED: (state, payload: OperationInfo) =>
        makeState({ ...state.byId, [payload.id]: payload }),
    },
    actions: ({ post }) => ({
      cancel: (operationId: string) => post('CANCEL', { payload: { operationId } }),
      clearFinished: () => post('CLEAR_FINISHED'),
      resume: (operationId: string) => post('RESUME', { payload: { operationId } }),
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
 * host-backed end to end). `running`/`finished` are selectors an activity panel or status bar reads
 * directly: `useShenoraOperations((s) => s.running)`.
 *
 * Headless, per D13: no component, no UI opinion, no `ProcessType`-style enum — what an operation
 * IS stays the app's `kind` string; this only carries the uniform lifecycle around it.
 */
export const useShenoraOperations = createOperationsStore();
