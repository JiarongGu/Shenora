# The communication core — rationale (0.2.0, D23/D24)

**Status: IMPLEMENTED and shipped in 0.2.0.** This is the WHY for the module contract's event path,
tracked operations, and the base-agnostic outbound pump. The decision record is `docs/DECISIONS.md`
D23 (+ its amendments); the as-built surface is `docs/ARCHITECTURE.md`; the release-facing log is
`CHANGELOG.md`. This doc exists because the code cites its `§` numbers.

> **Rewritten 2026-08-01 (the 0.2.0 cleanup), 647 → this.** It had become ~70% amendment stacks —
> six rounds of "SUPERSEDED / AMENDED again" narrating a shape that the design pass then cut. That
> history is not lost: it is in `CHANGELOG.md` 0.2.0, `DECISIONS.md` D23's amendments, and
> `docs/task-archive.md`. What was left here after removing it is below, and section numbers are
> preserved because ~50 code comments point at them.

---

## §1 The problem

The kit's own client design already matched the intent the first adopter described — *"not a sync
request pattern, which does not fit desktop or mobile: the backend layer here is mostly attached to
its frontend layer, so a stateful design with an event hub is the way to go — async from the UI,
progress synced."* The HOST contract did not: `Shenora.Ipc` had **zero references to `IEventBus`**
while the kit's own `DropZoneManager` took one as a REQUIRED option. The bus was already the spine;
the module contract just never admitted it.

Three gaps followed from that, and 0.2.0 closes all three: no event path in the module contract, no
defined shape for long-running work, and an outbound pipeline welded to WinForms.

## §4.2 The types

`Shenora.Ipc.Operations` — mechanism only. No queue, scheduler, retry, priority or phase model, and
no opinion about what an operation IS.

| Type | Carries |
|---|---|
| `OperationStatus` | `Running` · `Completed` · `Failed` · `Cancelled` · `Waiting`. Crosses the wire camelCase for free via `IpcJson`'s enum converter. |
| `OperationOptions` | `Kind` (app-defined **string**, never an enum), `Title`, `Scope`, `Cancellable`, `Progress`. |
| `OperationInfo` | The full snapshot — both the `OPERATION_UPDATED` payload and the `LIST` element. One type for every transition, so a client folds by `Id`: last write wins, no cross-type ordering hazard. |
| `OperationProgress` | `Value` · `Total?` · `Unit?` — the app's own unit, **never an assumed percent**. `Total = null` means no known denominator (bytes off a chunked stream), never zero. The kit does not clamp, validate or interpret it. |
| `OperationLabel` | `Text?` · `Key?` · `Parameters?` — the same i18n shape as `IpcError`, applied to labels. The kit carries the pieces; the app renders (headless, D13). |
| `IOperation` | The handle: its OWN `CancellationToken` (never the request's), `Report`/`Complete`/`Fail`/`Cancel`/`Wait`/`Resume`, all idempotent once terminal. |

**What the harvest deliberately left behind** (the source app had them; they are product, not
mechanism): a `ProcessType` enum with 15 domain values, a queue, a scheduler, retry policy, and a
phase model.

## §4.3 The events

All published under `OperationRegistryOptions.ModuleName` (default `OPERATIONS`), so one
subscription and one client-side filter cover the lot.

- **`OPERATION_UPDATED`** — the full `OperationInfo`, for **every** transition: start, progress,
  terminal. Event scope = the operation's scope.
- **`OPERATION_WAIT_REQUESTED`** / **`OPERATION_RESUME_REQUESTED`** — `{ operationId, module, kind,
  scope }`, identical shapes because both are pure ASKS. The owning module's own
  `IOperation.Wait`/`Resume` is what changes state. See §5A.4.
- **`OPERATION_REMOVED`** — `{ operationIds: string[] }`, for entries that leave the registry with no
  `OPERATION_UPDATED` of their own: `MaxHistory` eviction and `ClearFinished`. Scope-`null` (global)
  on purpose: a batch can span scopes, and deleting an id a subscriber never had is a harmless no-op.
  Without it, a client mirroring bounded host history was itself unbounded.

**Progress is throttled** to `ProgressInterval` (default 100 ms) with a TRAILING emit, because the
notification batcher queues without coalescing. Lifecycle transitions are never throttled — a
terminal state arriving late is a different class of bug than a missed progress tick.

## §4.6 The control surface

`OperationsFacade` (module `OPERATIONS`) — an ordinary `BaseFacade`, opt-in via
`services.AddShenoraOperations()`.

`LIST` is the client store's **snapshot source**, and that is the load-bearing part: a store cannot
replay a stream, so a component mounting while work is already running gets its state here and only
then folds deltas. `CANCEL` is the app-level cancel route (a one-way `post` has no caller waiting, so
"the client changed its mind" can never be a transport concern). `CLEAR_FINISHED` takes the same
optional `module`/`scope` keys `LIST` reads. `WAIT`/`RESUME`/`DISMISS` are covered in §5A.3.

## §5 `NotificationPump` — the transport-neutral outbound half

Extracted out of `WebViewIpcBridge` so a second, non-WinForms base inherits its already-fixed bugs
(P5.5 H2/H3) instead of re-earning them. The pump owns: the bus subscription (from CONSTRUCTION, not
`Open` — events emitted during a slow host init must survive), the per-channel `Filter` applied at
enqueue, the bounded drop-oldest queue, the ready gate, batch building, and the guarded
per-notification serialize.

**It owns NO timer and NO transport, and that is the whole design.** Which thread may touch a base's
client is a base-specific fact: WinForms must flush on the UI thread (a `Forms.Timer`), a headless
base uses a `PeriodicTimer`. So the base drives the tick and calls `TryDrainBatch`.

`WebViewIpcBridge` is now a thin adapter keeping only what is WinForms/WebView2: the timer,
`WebMessageReceived`, the `ContentLoading`→`Close` / `READY`→`Open` / `ProcessFailed`→`Close` gate
wiring, and `PostWebMessageAsString`.

> **Validated, not assumed (D16 amendment, 0.2.0 design pass D3).** A throwaway `net10.0` spike
> referencing only `Shenora.Core` + `Shenora.Ipc` ran the pump on a `PeriodicTimer` over a
> non-WebView2 transport, with a full request/response, the error boundary and a streamed operation.
> It passed with no change to `Shenora.Ipc`. The TFM is the proof.

## §5A The lifecycle

### §5A.1 Every non-terminal state must have a sanctioned exit — enforced by a test

The rule generalises past operations, and it exists because of a bug that no single diff showed. A
crash-checkpoint offer could only be removed by *resuming* it: `Validate` hard-coded `Status ==
Running` so `Cancel`/`Complete`/`Fail` all refused it, `ClearFinished` only walked `_finishedOrder`
(which that path deliberately never wrote to), and `PruneHistory` skipped offers on purpose. **Three
guards, each individually correct, each with a comment explaining why — and together they left a
state with no exit at all.** The same app that reviewed this had already stranded a real production
deployment on the identical bug hours earlier.

The REUSABLE half is the test shape, `OperationLifecycleInvariantTests`: enumerate the LIVE enum via
reflection (never a hardcoded list, so a future status is swept in automatically), and for each
non-terminal value require a registered `(reach, exit)` pair, asserting `ContainsKey` **by name** so a
new status with no exit fails loudly instead of silently checking nothing. Prove the exit reaches a
terminal state through the real object. Verified by sabotage. **Any future state machine here — a
session lifecycle, a connection state — should get the same shape.**

### §5A.2 Three bands

| Band | Statuses | Pruned as history? |
|---|---|---|
| Active | `Running` | no |
| Waiting | `Waiting` | **no** — stopped work is not finished work |
| Terminal | `Completed` · `Failed` · `Cancelled` | yes, bounded by `MaxHistory` |

`GetAll` sorts by band (Active oldest-first → Waiting oldest-first → Terminal newest-finished-first,
tiebroken by sequence because `TimeProvider.System` has ~15.6 ms granularity on Windows). A waiting
entry in the "everything else" bucket buried the exact row a user needs in order to act on it.

**`Waiting` is reached ONE way: `IOperation.Wait` on a live handle.** It was once two statuses
(`Paused`/`Interrupted`) that every transition already treated as one band, and the registry once
also accepted crash-checkpoint entries it had never started. Both are gone — see §5A.4.

### §5A.3 The surface

- `IOperation.Wait(reason?, detail?)` — `Running` → `Waiting`. The APP names why (`"credentials"`,
  `"dns"`, `"queued"`, `"rate-limited"`); the kit ships no reason enum. Optional: a wait whose cause
  is self-evident has nothing to name.
- `IOperation.Resume()` — `Waiting` → `Running`, clearing `WaitReason`.
- `IOperationRegistry.Dismiss(id)` — `Waiting` → `Cancelled`, terminal. **Refuses `Running`**:
  declining a pending offer and cancelling LIVE work are different acts, and letting `Cancel` accept
  both is the conflation that produced this release's only Critical.
- `IOperationRegistry.Find(id)` — resolves a handle from a bare id, which is exactly what a
  `WAIT`/`RESUME` handler has and needs.

"Registered but not yet started" needs no kit change: `Start()` then immediately `Wait("queued")`.

### §5A.4 Asking is not acting

`RequestWait(id)` and `RequestResume(id)` are **exact mirrors**: validate the status, emit the event,
change nothing. The owning module's own `IOperation.Wait`/`Resume` is what moves the state, because
only it knows when the work has actually stopped or restarted.

That symmetry was hard-won and is worth not re-breaking. `RequestResume` used to be asymmetric — it
REMOVED entries that had no live body (crash checkpoints registered through a `RegisterWaiting` +
`ResumePayload` pair) while keeping live ones. So every call had to answer *"does this entry still
have a live body?"*, and three answers were tried in one unpublished release: a second status (no
terminal exit — §5A.1's bug), then `ResumePayload` (APP-controlled, so it dropped genuinely live
operations), then an internal provenance flag. **The 0.2.0 design pass removed the question instead of
answering it a fourth time**: the crash-checkpoint half is cut, so every entry reaches `Waiting`
through a live `Wait()` and nothing is ever removed here.

Crash recovery is the APP's — it owns the checkpoint, the kit only ever held an opaque token it could
not interpret, and a resumed run is a fresh `Start()`/`Run()`. To keep the offer visible while the
user decides: `Start()` then `Wait("interrupted")`.

**The general lesson (in `.claude/knowledge/ipc-contracts.md`): when one decision needs its third
rewrite, suspect the decision's EXISTENCE, not its current answer.**

## §6 What this deliberately does not ship

- **No job/queue/progress TYPE on the client.** What an operation IS belongs to the app; the store
  carries only the uniform lifecycle around it.
- **No state library.** `createShenoraStore` is built on `useSyncExternalStore`. All three surveyed
  siblings reached for the same library; imposing it would have been solving their stack, not their
  problem.
- **No percent helper.** Render a ratio only when `Total` is set — that division is the consumer's
  policy.
- **No crash-resume mechanism** (cut in the design pass — see §5A.4).
- **No per-module/per-scope history bound.** `MaxHistory` is one global cap; a chatty module can
  crowd out a quiet one. Recorded as a known limit rather than guessed at, because no consumer has
  asked.
