# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Released versions are listed newest first; within `## Unreleased`, entries are in
landing order (oldest first) because they narrate one version being built.

**Each `###` heading appears AT MOST ONCE per version** — append to the existing group, never open a
second one. `## Unreleased` had grown two separate `### Breaking` lists (P5.5 H7), which is worse
here than untidy: that heading is the SemVer gate at 1.0, so a reader scanning it would have stopped
at the first list and missed five more breaking changes.

## 0.2.0 — 2026-08-01

The communication core (D23, `docs/2026-08-01-shenora-communication-core-design.md`): the module
contract now carries the EVENT path, the kit tracks long-running operations, and the host outbound
pipeline is base-agnostic. Triggered by the first adopter's IPC + drop-zone design review — the
verdict was that the client design already matched its own stated intent ("a stateful design with an
event hub … async from the UI, progress synced") while the HOST contract did not.

### Breaking

- **`BaseFacade.RouteMessageAsync` now takes an `IModuleContext` — the module contract's EVENT path
  is in the signature, not a side dependency every app wired by hand.**
  `(IpcRequest request, CancellationToken cancellationToken)` →
  `(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)`. Before this,
  `Shenora.Ipc` had **zero references to `IEventBus`** while the kit's own `DropZoneManager` took one
  as a REQUIRED option — the bus was already the spine, the contract just never admitted it.
  **Migration: add the parameter to every override; ignore it if your facade doesn't emit.**
  `context.Publish(type, payload?, scope?)` is the new default gesture for emitting — module-scoped,
  so it can never drift from `ModuleName` the way a hand-typed literal re-used at every call site
  can — and `context.Start`/`context.Run` are the tracked-operation primitive (see `### Added`).
  `BaseFacade`'s own constructor gained two optional parameters, `IEventBus?` and
  `IOperationRegistry?`, to back the context: `protected BaseFacade(ILogger? logger = null, IEventBus?
  events = null, IOperationRegistry? operations = null)`. Existing `base(logger)` calls compile
  unchanged; a facade that never publishes and never starts tracked work is completely unaffected,
  including every bus-less unit test in the suite. `Publish`/`Start`/`Run` fail LOUD at the call site
  — naming the exact fix (`pass an IEventBus to BaseFacade`, `call services.AddShenoraOperations()`)
  — rather than silently no-op-ing when the corresponding dependency was never supplied.
  `WebViewIpcBridge`'s internals also moved onto a new `Shenora.Ipc.NotificationPump` in this release
  (see `### Added`) with no public-surface break: `WebViewIpcBridgeOptions`' existing names
  (`NotificationInterval`, `MaxQueuedNotifications`) and behavior are preserved.
- **`OperationOptions.Resumable` / `OperationInfo.Resumable` (C#) and `resumable` (TS) are REMOVED**
  (generic-library audit finding 2, folded in before publish). The flag was consulted nowhere except
  `RegisterWaiting`'s own required-true gate — every caller had already forced it `true` to pass
  that gate, so it carried no information the method's existing non-empty-`ResumePayload` requirement
  didn't already express. **Migration:** drop the property from any `OperationOptions` initializer; a
  client testing "is this resumable" already used (and should keep using) `status === OperationStatuses.Waiting`.
- **The status collapse (owner direction, before publish — "structured like XHR"; see finding 7 under
  `### Added` for the full rationale).** `OperationStatus.Paused`/`.Interrupted` → one value,
  `OperationStatus.Waiting`; `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause` →
  `Wait(reason?, detail?)`; `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting`;
  `RequestPause` → `RequestWait`; `OperationEvents.PauseRequested`/`OPERATION_PAUSE_REQUESTED` →
  `WaitRequested`/`OPERATION_WAIT_REQUESTED`; facade route `PAUSE` → `WAIT`; client
  `OperationStatuses.Paused`/`.Interrupted` and the `paused`/`interrupted` getters REMOVED,
  `Waiting: 'waiting'` added (`waiting` is now the whole band). **Migration:** rename every occurrence
  1:1; a client testing "is this waiting" now reads `status === OperationStatuses.Waiting` instead of
  unioning `paused`/`interrupted`; a handler that branched on the removed values to guess whether
  `RequestResume` would drop the entry should instead just fold `OPERATION_REMOVED` — the host decides
  the drop-vs-keep asymmetry itself (see finding 8 under `### Added`) and always publishes it as a named
  removal, so a client-side guess at the signal (`resumePayload` or otherwise) is never needed.

### Added

- **The tracked-operation primitive** (D23; harvested mechanism-only from a private sibling's
  320-line process registry, per `generic-library`'s two-app bar): id, owning module, app-defined
  `Kind`/`Scope`, status, progress, idempotent finish, cancel-by-id, bounded history, and throttled
  progress emission — with NO queue, scheduler, retry, priority, phase model, `ProcessType`-style
  enum, i18n rendering, UI or persistence. What an operation IS stays the app's; the kit only tracks
  it. New in `Shenora.Ipc`: `OperationStatus` (`Running`/`Completed`/`Failed`/`Cancelled`/
  `Waiting`), `OperationLabel` (`{Text?, Key?, Parameters?}` — the same i18n shape as
  `IpcError`), `OperationProgress` (`{Value, Total?, Unit?}` — the app's own unit, not an assumed
  percent; see finding 6 below), `OperationOptions`, `OperationInfo` (the one snapshot type for every lifecycle
  transition — a client folds by `Id`, last-write-wins, no cross-type ordering hazard; carries
  `WaitReason`, an app-defined string like `Kind`), `IOperation`
  (`Report`/`Complete`/`Fail`×2/`Cancel`/`Wait`/`Resume`, all idempotent once terminal, with its OWN
  `CancellationToken` — never the request's, because work handed off outlives the request that
  started it), `IOperationRegistry`/`OperationRegistry(+OperationRegistryOptions)`,
  `OperationEvents` (`OPERATION_UPDATED`, `OPERATION_RESUME_REQUESTED`, `OPERATION_WAIT_REQUESTED`,
  `OPERATION_REMOVED`), `OperationsFacade`
  (`LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`/`DISMISS`/`WAIT` under module `OPERATIONS` by default —
  also exposed as the `ListType`/`CancelType`/`ClearFinishedType`/`ResumeType`/`DismissType`/
  `WaitType` constants, pinned against the client by the wire-mirror test), and
  `AddShenoraOperations(OperationRegistryOptions? options = null)` — opt-in DI wiring, so an app with
  no long-running work pays nothing; takes the options RECORD directly (not a configure callback) so
  a renamed `ModuleName` etc. can actually be set, matching every other options type in the kit.
  `GetAll(module?, scope?)` and `ClearFinished(module?, scope?)` share ONE scope rule with
  `IEventBus` — an unscoped operation matches any requested scope, not strict equality — and a
  removal (`MaxHistory` eviction, `ClearFinished`, a no-live-handle entry dropped by `RequestResume`)
  now publishes `OPERATION_REMOVED { operationIds }` so a client mirroring bounded host history
  actually hears about it (generic-library audit finding 4 — see below).
  Progress reports are throttled to `OperationRegistryOptions.ProgressInterval` (default 100 ms) with
  a TRAILING emit so the final value in a window is never dropped; every lifecycle transition emits
  immediately, never throttled. An operation failure obeys the same no-raw-exception-text boundary as
  a request/response failure: an unexpected exception crosses as `IpcErrorCodes.UnknownError` plus the
  exception type name, with the real detail logged host-side only. `Cancel` refuses an operation that
  never opted into `Cancellable`, rather than flipping its status while the body runs on underneath
  it — but the body's OWN end in `OperationCanceledException` (via `Run`, or a direct
  `IOperation.Cancel()` call by the operation's own owner) is always terminal regardless of
  `Cancellable`, because that is not the same permission question as an external by-id cancel
  request. `RequestWait`/`RequestResume` are the ASK half of the waiting band — a client asks, the
  owning module's own `IOperation.Wait`/`Resume` acts (see the design-pass note under `### Removed`
  for the crash-checkpoint half that was cut before publish).
  `IOperationRegistry.Find(id)` resolves a live handle for an already-started operation — reinstated
  after being sketched-then-dropped pre-0.2.0 as unearned surface; see the audit paragraph below for
  why that ruling changed.
  **The lifecycle is completed to THREE BANDS (§5A of the design doc, amendment before merge):** the
  first adopter found that a crash-checkpoint offer could only be removed by resuming it — `Validate`
  hard-coded `Status == Running` for every caller, `ClearFinished` only ever walked `_finishedOrder`
  (which the checkpoint-registration path deliberately never wrote to), and `PruneHistory` skipped
  offers on purpose — three individually-correct guards composing into a state with no exit at all, and
  that adopter had already shipped exactly this bug and stranded a real deployment on it (paused on DNS
  records, permanently offering Resume, permanently undeletable). **The rule this fixes generalises:
  every non-terminal status must have a sanctioned exit to a terminal one** — enforced by
  `OperationLifecycleInvariantTests`, which enumerates the live `OperationStatus` enum (not a
  hardcoded list) and fails BY NAME if a future non-terminal addition has no registered exit.
  `Validate` is reworked so each transition states what it accepts, instead of one hard-coded
  `Running` check: `Report`/`Wait` require `Running`; `Complete`/`Fail` accept `Running` OR `Waiting`
  (a waiting operation can still fail on a deadline); the public by-id `Cancel(id)` accepts `Running` OR
  `Waiting`, keeping its `Cancellable` permission check; the owner-path terminal cancel accepts ANY
  non-terminal status; `Resume`/`Dismiss` require the WAITING band (`Waiting`). The
  "ignored" diagnostic is also now honest about terminal vs. non-terminal — it used to say "has
  already reached a terminal state" for ANY refused status, which was simply false for a non-terminal
  one.
  New: `OperationStatus.Waiting` — a run that stops mid-flight WITHOUT crashing (expired cloud
  credentials, a throttling provider, DNS not yet propagated, a migration awaiting confirmation, or an
  app's own queue parking a just-started operation), reached via `IOperation.Wait(string? reason =
  null, OperationLabel? detail = null)` (`Running` →
  `Waiting`) and exited via `IOperation.Resume()` (`Waiting` → `Running`, clearing the reason) — both new
  members on `IOperation`. `reason` is an app-defined STRING, like `Kind`, never a kit enum, and
  OPTIONAL (generic-library audit finding 5) — a consumer whose wait is self-evident (the user
  clicked Pause) has nothing to name. `IOperationRegistry.Dismiss(string id)` declines a pending
  `Waiting` offer (`→ Cancelled`, terminal — enters bounded history, publishes an
  ordinary `OPERATION_UPDATED` snapshot like any other terminal transition, unlike `ClearFinished`/
  `RequestResume` which remove an entry and instead publish `OPERATION_REMOVED`, see finding 4 below)
  — it REFUSES `Running` on purpose, because declining an offer and cancelling LIVE work are different
  acts, and this branch's only Critical came from exactly that conflation inside `Cancel`; `Dismiss` is
  a separate member rather than `Cancel` accepting more states for the same reason. It signals the
  entry's own `CancellationToken` first when one exists, so a waiting body still parked on its token
  unwinds.
  `RequestResume`'s drop-vs-keep decision keys on how the entry reached `Waiting`, not on a second
  status (there is only one `Waiting` value — see findings 7 and 8 below) and not on the app-controlled
  `ResumePayload` field either (finding 8 closed that as a residual hole before publish), and the two
  cases are handled asymmetrically ON PURPOSE: an entry reached via an ordinary `Wait()` is LEFT IN
  PLACE (the app calls `IOperation.Resume()` on its own handle once it has actually resumed — the
  client asking is not the state changing) — even when the app also attached its own `ResumePayload` at
  `Start()` time, since the handle is still live either way — while one `RegisterWaiting` reconstructed
  from a checkpoint is still REMOVED (there is no live handle to flip — the process that owned it is
  gone, and this now also publishes `OPERATION_REMOVED { operationIds: [id] }`). The
  `OPERATION_RESUME_REQUESTED` payload also carries `status` (always `Waiting`), so a handler can keep
  branching on that field; a handler can no longer look the entry up afterward for the removed case,
  because it is gone.
  `GetAll` sorts by the three bands, not "Running vs. everything else": Active (oldest first) →
  Waiting (oldest first) → Terminal (newest FINISHED first, tiebroken by
  newest `Sequence` — `TimeProvider.System`'s ~15.6 ms granularity on Windows means two same-tick
  finishes would otherwise fall back to dictionary enumeration order, which reshuffles on unrelated
  churn). `IModuleContext.Run`/`IOperationRegistry.Run` only implicitly `Complete` a body when it is
  STILL `Running` once the work returns — a body that calls `op.Wait(reason)` and simply returns
  ("waiting by returning") is left `Waiting`, not silently stamped `Completed`; resuming it from there
  is the app's own job. `Dismiss` and the public by-id `Cancel(id)` now report exactly what the
  transition actually did rather than an assumed success, closing a narrow race where a concurrent
  `Resume()`/finish landing between the caller's own permission check and the terminal transition's
  own re-validation could otherwise answer a client `true` for a change that did not happen.
  `OperationInfo.WaitReason` is cleared by `Resume()` but RETAINED through a terminal transition
  reached directly from `Waiting` (useful history — "failed while waiting on credentials").

  **Generic-library audit (2026-08-01, before publish — every change below is free since 0.2.0 was
  never published):** the first release absorbed the shape of the ONE app it was
  harvested from on the removal and asking halves of the lifecycle, which that app's own host never
  had to solve. Fixed:
  1. **`ClearFinished` is now `ClearFinished(string? module = null, string? scope = null)`**, mirroring
     `GetAll` exactly, and the `CLEAR_FINISHED` route reads the same two payload keys `LIST` already
     did — it used to take/read nothing, so "clear completed" in one scoped window (a secondary
     window, a scoped container router) silently wiped every OTHER scope's finished history too.
  2. **`OperationOptions.Resumable`/`OperationInfo.Resumable` are REMOVED.** The flag was consulted
     nowhere except `RegisterWaiting`'s own required-true gate — every entry it ever produced had
     already forced it `true` to pass that gate, making it a tautology. `RegisterWaiting`'s
     existing non-empty-`ResumePayload` requirement already expresses "this is resumable" on its own.
  3. **`IOperationRegistry.RequestWait(string id)` is added** — an exact mirror of `RequestResume` for
     the direction the kit previously had no client route for at all. §5A.3 reasoned "pausing is the
     host's own knowledge" from one app's semantics (a host discovering its own blocker); that does not
     hold for the equally-common shape the kit itself already names as a consumer (a
     download-manager-style activity panel) — a human clicking Pause on visible work. `RequestWait`
     emits `OPERATION_WAIT_REQUESTED { operationId, module, kind, scope }` and changes nothing itself
     — the owner's own `IOperation.Wait` is what actually stops the work, same ASK/ACT split as
     `RequestResume` vs. `Resume`. The facade gains a matching `WAIT` route (`{ operationId }` →
     `{ requested }`).
     **`IOperationRegistry.Find(id)` is reinstated** for the same reason: `RESUME`/`WAIT` are both
     client-request routes carrying only an id, and whoever handles them (hearing
     `OPERATION_RESUME_REQUESTED`/`OPERATION_WAIT_REQUESTED`) must translate that id back into a
     handle to call `Resume`/`Wait` — a recurring shape every such consumer would otherwise re-solve
     with its own id→handle map. Safe to hold past the operation's life: every `IOperation` member
     re-validates current status before acting.
  4. **`OperationEvents.Removed` (`OPERATION_REMOVED`, payload `{ operationIds: string[] }`) is added**
     — emitted wherever an entry leaves the registry with no corresponding `OPERATION_UPDATED`:
     `MaxHistory` eviction, `ClearFinished`, and the no-live-handle entry drop inside `RequestResume`.
     The host bounds its own history; the client — the side actually rendering — never heard about it,
     so a status bar that never unmounts accumulated every terminal operation for the whole session.
     This also retires the two hand-written optimistic local prunes `@shenora/react`'s `clearFinished`/
     `resume` actions used to carry (below) — one authoritative event that cannot diverge from the
     host, replacing two guesses that already produced this release's only Critical (a `resume` prune
     that once dropped a live-`Wait()` row the host deliberately keeps).
  5. **Minors:** `Wait`'s `reason` is optional (above); doc comments that illustrated the API with "a
     paused deploy" now say "a waiting operation" (D22 permits domain words as examples, but the cost is
     the kit LOOKING like it ships that product); and a limit is recorded rather than solved —
     `MaxHistory` is one global cap with no per-module/scope bounding seam.
  6. **Progress is not percent (owner direction, before publish — "even its progress it might be
     different than 0-100%"), correcting finding 5's OWN fix above.** Stating "0–100 PERCENT" on the
     write side was the wrong fix to the right observation: percent is not the mechanism, it is one way
     an app happens to measure. `OperationOptions.Progress`/`OperationInfo.Progress` (C#) and
     `OperationInfo.progress` (TS) are now a new record, `OperationProgress(double Value, double? Total
     = null, string? Unit = null)` (TS: `{ value: number; total?: number; unit?: string }`), and
     `IOperation.Report(int? progress, …)` is now `Report(OperationProgress? progress, …)`. `Total`
     is the denominator when known and `null` when there is none (an absolute count with nothing to
     divide by — bytes off a chunked stream); `Unit` is app-defined and uninterpreted, exactly like
     `Kind`/`WaitReason`. **`ClampProgress` (`Math.Clamp(value, 0, 100)`) is REMOVED and nothing
     replaces it** — the registry passes `Progress` through completely unchanged; silently rewriting an
     app's own reported number is worse than passing it through, and a `Value` above its own `Total` is
     the app's bug to see, not the kit's to hide. No validation throw was added either: progress is
     reported from background work on a hot path, and throwing there would kill an operation over a
     cosmetic number. **`Complete()` no longer fabricates `Progress = 100`:** it now sets `Value =
     Total` only when the last report carried a known `Total` (the honest "all of it"), and otherwise
     leaves the last reported value exactly as it was — never inventing a figure the app never gave it.
     `@shenora/react` ships NO percent helper; the README documents the one-liner (`total ? (value /
     total) * 100 : undefined`) because that division is the consumer's own policy, not the kit's. The
     desktop sample and its web counterpart were updated to demonstrate the general shape
     (`new OperationProgress(step, steps, "steps")`, rendered as a ratio because `total` is set) instead
     of the percent special case. Caught before 0.2.0 was pushed or published, so free.
  7. **The status collapse (owner direction, before publish — "I don't even think we need any specific
     status than regular — think about this is going to be structured like XHR").** `Paused` and
     `Interrupted` — introduced above as two states — collapse into ONE, `OperationStatus.Waiting`:
     every transition already treated them as one band (`Dismiss`/`RequestResume` both accepted either,
     neither was ever pruned, the client's `waiting` getter already unioned them), and the one place
     they actually diverged (`RequestResume` dropping the crash-checkpoint case, keeping the live-`Wait()`
     case) was always about whether the entry had a live handle, which `ResumePayload` already told the
     registry on its own. Renamed throughout, mechanism not scenario (D22):
     `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause` → `Wait(reason?, detail?)`;
     `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting`; `RequestPause` → `RequestWait`;
     `OperationEvents.PauseRequested`/`OPERATION_PAUSE_REQUESTED` → `WaitRequested`/
     `OPERATION_WAIT_REQUESTED`; facade route `PAUSE` → `WAIT`; client `OperationStatuses.Paused`/
     `.Interrupted` and the `paused`/`interrupted` getters → `Waiting: 'waiting'` (the existing
     `waiting` getter is now the whole band; the two half-getters are DELETED, not deprecated).
     `IOperation.Resume`/`RequestResume`, `Dismiss`, `OPERATION_RESUME_REQUESTED`, `RESUME`, `DISMISS`
     keep their names — resuming and dismissing were already mechanism words. `RequestResume`'s
     drop-vs-keep read `ResumePayload` directly instead of a second status at this point (finding 4's
     asymmetry paragraph above was updated in place to describe this) — **closed further by finding 8
     below**, since that field turned out not to be a safe signal either. Also closes a known limit finding 5 above
     recorded rather than solved: "registered but not yet started" is now representable with no kit
     change — an app calls `Wait("queued")` on the handle immediately after `Start`, before real work
     begins. Full rationale: `docs/DECISIONS.md` D23's amendment. Caught before 0.2.0 was pushed or
     published, so free.
  8. **Keying `RequestResume`'s drop-vs-keep decision on `ResumePayload` (finding 7 above) was itself a
     residual hole, closed before publish, so also free.** `ResumePayload` is APP-controlled data — an
     app may attach one to `OperationOptions` at `Start()` — so it could not reliably answer "does this
     entry have a live handle": an app that did so and then called `Wait()` had a genuinely LIVE
     operation (handle intact, body parked) dropped exactly like a crash checkpoint, silently orphaning
     later `Report`/`Complete`/`Fail` calls on it. `RequestResume` now keys the decision on an internal
     `Entry.Reconstructed` flag instead, set only by `RegisterWaiting` (the one call site that
     legitimately reconstructs an entry with no live body) — never exposed on `OperationInfo`, since no
     consumer needs it and every public member is SemVer surface at 1.0. `ResumePayload`'s other roles
     are unchanged (`RegisterWaiting`'s non-empty requirement, the dedupe key, riding
     `OPERATION_RESUME_REQUESTED`). Full rationale: `docs/DECISIONS.md` D23's amendment.
- **`@shenora/react`: `useShenoraOperations` / `createOperationsStore`** — the client half of the
  primitive above, built the same way `createShenoraStore` already was: `OperationStatuses` (wire
  values, including `Waiting` — collapsed from the originally-shipped `Paused`/`Interrupted` pair, see
  finding 7 above) + `OperationInfo`/`OperationLabel` types (`OperationInfo.waitReason`
  mirrors the host's `WaitReason`), a `LIST` snapshot on first subscribe (so a progress strip that
  mounts mid-run isn't empty), folding `OPERATION_UPDATED` by id afterward, with `running`/
  `waiting`/`finished` DERIVED getters computed from `byId` on every read (`waiting` is now a
  single-status filter, exactly like `running` — the originally-shipped `paused`/`interrupted`
  half-getters and the internal status set that unioned them are DELETED, not deprecated, now that
  the host carries only one waiting value; `interrupted` had been added because it used to fall into
  NO getter at all: not `running`, not `paused` — matched only the literal `'paused'` — not `finished`,
  reachable only by hand-filtering `byId`) and `cancel`/`dismiss`/
  `wait`/`clearFinished`/`resume` actions. `wait` (generic-library audit finding 3; shipped at the
  time as `pause`) posts `WAIT`
  (`{ operationId }`) and touches no local state, mirroring `dismiss`'s shape — asking is not acting.
  **`clearFinished`/`resume` no longer carry an optimistic local prune (generic-library audit finding
  4, folded into 0.2.0 before publish):** they used to guess at what the host had removed, because
  removals had no wire event at all — `clearFinished` pruned every entry in the TERMINAL status set,
  and `resume` pruned only the `interrupted` case to mirror the host's own asymmetry (§5A.4). One of
  those guesses was this release's only Critical: `resume`'s prune once dropped a `paused` row the
  host deliberately keeps, making the still-parked entry unreachable until every subscriber unmounted
  and a fresh `LIST` ran. The host's new `OPERATION_REMOVED { operationIds }` (see finding 4 above) is
  now the ONE authoritative removal signal — folded by deleting exactly the named ids, regardless of
  status — so `clearFinished`/`resume` are now plain fire-and-forget posts (forwarding this store's own
  configured `scope`, generic-library audit finding 1) with no client-side guess left to diverge from
  the host. `dismiss` still mirrors `cancel`'s shape and needs no removal handling at all — the host's
  `Dismiss` publishes an ordinary terminal snapshot for the entry, same as a real cancel, since it
  transitions rather than removes.
  `createOperationsStore({ module?, scope? })` supports a renamed host module
  (avoiding a collision with an app's own module name) and a scope-filtered instance. **Known limit,
  deliberate:** no `byModule`/`byScope` selector — filtering by module or scope is a one-line consumer
  selector over `byId`, and shipping indexes for it would be duplicated derived state for no gain.
- **`Shenora.Ipc.NotificationPump`(+`NotificationPumpOptions`)** — the transport-neutral half of a
  host's outbound notification channel (bus subscribe from CONSTRUCTION → per-channel filter →
  bounded drop-oldest queue → batch → ready gate → guarded per-notification serialize), extracted out
  of `WebViewIpcBridge` so a second, non-WinForms base inherits these already-fixed bugs (P5.5 H2/H3)
  instead of re-earning them — D16's "the seam, not the package" applied to the HOST half of the
  outbound path (the client half, `ShenoraTransport`, has been base-agnostic since P3). The pump owns
  no timer and no transport: which thread may touch a base's client is a base-specific fact, so the
  base drives its own tick (a `Forms.Timer` on WinForms; a `PeriodicTimer` on a headless base) and
  calls `TryDrainBatch`. `WebViewIpcBridge` is now a thin adapter over it, keeping only what is
  WinForms/WebView2: the timer, `WebMessageReceived`, the `ContentLoading`/`READY`/`ProcessFailed`
  gate wiring, and `PostWebMessageAsString`.
- **Per-channel notification filtering** — `NotificationPumpOptions.Filter` /
  `WebViewIpcBridgeOptions.NotificationFilter`, applied at enqueue. Every bridge previously subscribed
  with `SubscribeToAll`, so with two windows every bus event reached both — an auxiliary session or a
  remote client would receive the whole app's traffic with no way to narrow it. Default: deliver
  everything, unchanged for an app that doesn't need the seam.
- **`@shenora/react` exports `OperationProgress`, `OperationEventTypes` and `OperationModuleName`**
  (whole-codebase review, before publish). `OperationInfo.progress` is typed as `OperationProgress`
  and `OperationInfo` was exported, so the field's own type was unnameable from outside the package —
  the tell is that the kit's OWN sample re-declared the shape inline (`{ value: number; total?:
  number; unit?: string }`) to write a one-line formatter. The other two close the same gap for the
  two events `createOperationsStore` deliberately does not subscribe to
  (`OPERATION_RESUME_REQUESTED`/`OPERATION_WAIT_REQUESTED`, which target the OWNING module's own
  service): the app writing that handler had to hard-code the literals the wire-mirror tests exist to
  stop it hard-coding. **The barrel gate could not have caught any of it** — `index.test.ts` compares
  `Object.keys(barrel)`, and a type has no runtime binding; the type half is now pinned by a
  type-only import in that same file, which `npm run typecheck` (the full tsconfig, which includes
  tests) compiles. Verified by sabotage: dropping `OperationProgress` from the barrel fails the
  typecheck naming it.

### Removed

- **The crash-checkpoint half of the operations cluster: `IOperationRegistry.RegisterWaiting`,
  `OperationOptions.ResumePayload` and `OperationInfo.ResumePayload` (and `resumePayload` on the TS
  mirror).** The 0.2.0 design pass, prompted by the owner asking a review to judge the DESIGN rather
  than only the code. The kit's own bar is "generalize what the survey shows at least TWO apps need"
  (`generic-library.md`), and the design doc's §4.2 provenance note had already admitted in writing
  that `Interrupted`/`ResumePayload`/`RegisterWaiting`/`RequestResume` "come from **one** app, not
  two". Shipping it anyway cost more than it carried: that cluster took roughly eight reshapes inside
  this single unpublished release and produced the release's only Critical.
  **The root cause was structural, not a sequence of unlucky bugs.** Accepting an entry the kit had
  never started meant every caller had to answer "does this one still have a live body?" — and each
  answer failed in its own way. A second status (`Interrupted`) turned out to have no terminal exit at
  all, stranding operations forever. Keying on `ResumePayload` read APP-controlled data, so an app that
  attached a token at `Start()` and then called `Wait()` had a genuinely live operation dropped out of
  the registry. An internal provenance flag finally worked, at the cost of a concept no consumer could
  see. Removing the question removes all three.
  **What stays, and why it is not the same thing:** `OperationStatus.Waiting`, `IOperation.Wait`/
  `Resume`, `Dismiss`, and the `RequestWait`/`RequestResume` ask-act pair. Those are the
  download-manager shape the kit itself names as a consumer — a human clicks Pause, then Resume — and
  cutting `RequestResume` too would have left a client able to pause but never resume. `RequestResume`
  is now an EXACT mirror of `RequestWait`: validate, emit, change nothing. Its payload drops
  `resumePayload` and `status` (the latter carried no information once there was one reach), so both
  ask-events are `{ operationId, module, kind, scope }` — pinned by a new test.
  **Migration:** crash recovery is the app's, which is where the checkpoint already lived — the kit
  only ever held an opaque token it could not interpret. Keep the token in your own store; on restart,
  begin the resumed run as an ordinary `Start()`/`Run()`. If you want the pending offer visible while
  the user decides, `Start()` it and immediately `Wait("interrupted")` — the same one-line shape that
  already covers "registered but not yet started".
- **`OPERATION_REMOVED` no longer fires from `RequestResume`** (it never removes an entry now). Its
  two remaining sources — `MaxHistory` eviction and `ClearFinished` — are unchanged, and the client
  folds it identically.

### Changed

- **The genericity rule finally has a tripwire — `SurfaceVocabularyTests`.** The owner's standing
  review criterion is *"make sure this is a library — we're not solving specific business logic;
  everything here has to be generic enough that any of our applications can adopt it"*, and it was
  the only load-bearing invariant in the repo with nothing watching it: `ApiSurfaceTests` is a SemVer
  gate that proves the surface CHANGED, and its documented workflow (copy `.actual` over the
  baseline) waves domain vocabulary straight through. Every public TYPE name is now checked against
  an allow-list of shell/platform words (`tests/Shenora.Tests/Api/surface-lexicon.txt`); an unknown
  word fails the build and the author either renames the type (D22) or argues the word onto the list.
  Allow-list rather than a blocklist of business nouns, because a blocklist only catches the domain
  words someone already imagined — and listing the private siblings' nouns in a tracked file would
  leak what those apps do. Derived from the 147 public types then shipping: 134 words, every one a
  mechanism, so the kit passed its own criterion on the day the gate was written. Sabotage-verified
  both ways, and a second test fails if the lexicon keeps words no type uses. No surface change.
- **`Shenora.Core.AppCallback.Log(Action<string>? sink, Func<string> message)`** — the guarded, lazy
  diagnostic helper existed as FIVE byte-identical private copies (`WebViewHost`,
  `WebViewIpcBridge`, `EmbeddedResourceProvider`, `NotificationPump`, `OperationRegistry`), the same
  "N copies of the rule that must never be broken" shape `IpcErrorMapping` was collapsed for. One
  owner now, on the type that already owns the callback-guard policy. Additive; no behaviour change.
- **D16's host half is now EXECUTED rather than asserted — no code change was needed, which is the
  result.** `NotificationPump` was extracted in this release "so a second, non-WinForms base inherits
  these already-fixed bugs", and no second base existed, so nothing had ever run the kit's IPC stack
  without a Windows presentation layer. A throwaway spike (`devtools/_transport-spike/`, gitignored
  like `_dpi-probe` before it) did: a `net10.0` console app referencing ONLY `Shenora.Core` +
  `Shenora.Ipc`, with a pair of channels standing in for a socket, ran a typed request/response, the
  structured error boundary (`OperationException` → its code; unknown route → `NO_HANDLER`), the pump
  driven by a `PeriodicTimer` instead of a `Forms.Timer`, and a `ctx.Run` operation streamed back as
  batched notifications — all green. **The target framework is the proof**: a Windows type anywhere in
  that graph turns the project red, the same enforcement `samples/Shenora.Sample.Logic` already gives
  app logic, applied to the host half. Follow-ups it surfaced are recorded in `TASKS.md` rather than
  built, since one spike is one consumer and the kit's bar is two.
- **`dev.mjs verify`/`doctor` gained `doc-drift` — the gate the prose never had** (0.2.0 design pass,
  D4). Every code invariant in this repo has a test; no doc claim had anything, and the review that
  prompted this pass found 8 of its ~13 findings in comments and docs. Two PRECISE checks rather than
  one fuzzy sweep, because docs are full of BCL names, TS symbols and deliberately-historical
  references and a matcher that cries wolf gets switched off: **(1)** the dependency graph drawn in
  `README.md`/`docs/ADOPTION.md` is compared against the actual `ProjectReference`s — the check that
  would have caught both files documenting a `Shenora.WinForms → Shenora.Ipc` edge that has never
  existed; **(2)** names listed in `devtools/retired-names.txt` may not be stated as a CURRENT fact.
  Since this repo's docs are amendment stacks, (2) allows a retired name in the PAST tense (it looks
  for "used to / former / renamed / removed / superseded / …" around the mention) and takes an
  explicit `doc-drift:history` marker for a preserved design sketch or rename table.
  It found real drift on its first run: `webview2-hosting.md` still said `LoginWindow.ClearProfile`
  and `CoBrowseSession.StartAsync`, `generic-library.md` still cited `LoginWindow` as a current
  in-repo example, and `REVIEW-GUIDE.md` still told reviewers `CookieLoginFlow` "keeps its scenario
  name deliberately as the one reference driver" — which P7 reversed when it moved that driver out of
  the kit. All corrected. Both checks are sabotage-verified.
- **Frameless chrome stays a FIXED WinForms type, and the caption-button DRAWING moved out of
  `OptimizedForm` into an internal `CaptionButtonRenderer`** (0.2.0 design pass, D24). The review
  flagged `OptimizedForm` as the kit's one inheritance-only feature and proposed making the chrome
  attachable; that was rejected on the evidence — the window style belongs in `CreateParams` at handle
  creation, and attaching it later needs `SetWindowLong`+`SWP_FRAMECHANGED` as a second mechanism,
  doubling the verification surface in the one area where a green unit suite has twice been the wrong
  answer here (P5.6). The cohesion complaint was fair, though, so the part with NO message-loop
  responsibility was split out: palette fallback, glyph selection, the DPI-scaled icon font and the
  painting. `OptimizedForm` 998 → 905 lines. **No public surface change** — the renderer is internal
  and the form's behaviour is identical. The reusable rule (D24): extract what is pure input →
  pixels; leave anything that answers a window message where the OS can see it.
  New direct tests cover glyph choice, the fallback palette, DPI font scaling and its cache — none of
  which previously had any, since they were unreachable without a real window. One of them pins that
  every glyph is a single Private Use Area codepoint, guarding the documented CJK-locale mojibake trap
  that otherwise turns a caption button silently blank; sabotage-verified (a mangled glyph fails it
  reporting `Actual: 63`).

### Fixed

- **`OperationInfo` had no cross-language field mirror** — the single biggest shape on this wire (it
  is both the whole `OPERATION_UPDATED` payload and the `LIST` element) while the much smaller, newer
  `OperationProgress` had one. It was missed behind a plausible claim recorded in that test's own doc:
  "`OperationInfo`'s other fields are pinned by `[JsonPropertyName]` + the API baseline". Both halves
  are true and together they prove nothing about the MIRROR — they pin the host's names against the
  host's own baseline, and nothing compared them to the TS interface. Found when the cut above removed
  a field from both sides by hand and nothing verified that both hands had moved.
  `WireMirrorTests.OperationInfo_fields_match_the_host` now checks it in both directions, sabotage-
  verified (a client-only `resumePayload` fails naming it).
- **Docs on shipped surface still described `RequestResume`'s superseded rule** (whole-codebase
  review, before publish). Five XML/JSDoc sites and three docs said the drop-vs-keep decision is told
  apart by `ResumePayload`; the released behaviour keys on the registry's own internal provenance
  record (see the `### Breaking` note above and D23's closing amendment). An adopter following the
  shipped doc would attach its own `ResumePayload` at `Start()` and expect `RequestResume` to drop the
  entry — the kit now keeps it, which is the whole point of the fix. Corrected in
  `OperationStatus.Waiting`, `IOperationRegistry.RegisterWaiting`, the three TS mirrors in
  `operations.ts`, `docs/ARCHITECTURE.md` (which contradicted its own `RequestResume` paragraph 50
  lines earlier), `docs/ADOPTION.md`, and the design doc's §4.3/§5A.2/§5A.4.
- **`README.md`/`docs/ADOPTION.md` documented a dependency chain the packages do not have** — both
  drew `Shenora.WinForms → Shenora.Ipc`. The graph is a DIAMOND over `Shenora.Core`:
  `Shenora.Ipc` and `Shenora.WinForms` are siblings, and `Shenora.WebView2` is the first package that
  sees both. `Shenora.Ipc` targets `net10.0` and binds to no UI framework — that is what D16's
  transport story rests on, and why the two IPC-facing desktop facades live in `Shenora.WebView2`
  rather than either base. An adopter following ADOPTION Stage 0/1 for "a shell with no web frontend"
  would reference `Shenora.WinForms`, write a `BaseFacade`, and get an unresolved-namespace error the
  docs said could not happen. Both now show the real graph, the TFM per package, and the explicit
  "add `Shenora.Ipc` as a second reference" note.
- **`README.md` still said "Not yet published to NuGet/npm"** — stale since 0.1.0 and the first thing
  an evaluating reader saw, directly under the version headline (first-adopter finding, 2026-07-31).
  The package table also gained a target-framework column, so an adopter no longer has to download a
  nupkg to learn whether it fits (same finding).
- **`Shenora.WebView2.Sessions`' NuGet package description still shipped the scenario vocabulary D22
  removed from the types** — "login windows … (silent refresh, cookie capture)" and "co-browse
  streaming primitives", for types renamed `InteractiveSession`/`StreamingSession` in P5.5 H9.7/H9.8.
  D22's audit method is "sweep the API baselines for domain words", and a csproj `<Description>` is in
  no baseline — while being the single most public place that vocabulary appears (the nuget.org
  listing). Also renamed the off-screen window's caption and two log messages, which are externally
  readable for the same reason.
- **`InteractiveSession`'s loading-fallback timer invoked the app's `OnLoading` unguarded.** A
  WinForms timer tick has no caller on its stack, so a throwing splash toggle (`ObjectDisposedException`
  is the obvious way) was an unhandled UI-thread exception — the bootstrap's modal crash dialog. The
  same callback was already guarded on the two paths below it in the same method, with a comment
  recording what one unguarded `OnLoading` cost last time. Now routed through `AppCallback.Run`.
- **`EmbeddedResourceProvider` called the app's `Log` sink directly at seven sites**, two of them
  inside `BeginWarmup`'s fire-and-forget `Task.Run` where a throwing sink escapes the `catch` it is
  reporting from and becomes an unobserved task exception. All seven now go through the guarded, lazy
  `Log(Func<string>)` every other type in the kit uses.
- **`DropZoneManager` emitted with `_ = EventBus.EmitAsync(…)`** — the discard shape `IEventBus.Emit`
  was added in P6.4 to replace, and whose doc says a caller should not have to read the implementation
  to know the discard is safe. It was the kit's only in-repo emitter and it did not use its own member.
- **Stale/self-contradicting XML docs:** `DropZoneFacade` recommended mapping through
  `AddMessageDispatcher`'s configure callback — the advice `WindowCommandFacade`'s doc already records
  as impossible (that callback runs before any form exists, P5.5 H6); `SessionEnvironmentCache` said
  `WebViewEnvironment` "still has" the faulted-task-caching trap and cited a `TASKS.md H3` that no
  longer exists (H3 fixed it, and the two now share one shape); `ModuleContext` said it is built "at
  construction" while `BaseFacade` builds it lazily and says why; `docs/ARCHITECTURE.md` carried
  "known limit: a mapped module cannot be released" in the same sentence that lists
  `TryReleaseModule`.
- **Recorded a real known limit in its place: `IModuleRegistry` cannot see DI-registered facades.**
  `AddMessageDispatcher` maps them through `MapRegisteredModulesLazily` (one terminal middleware) and
  not through `TryClaimModule`, because claiming needs the module names and resolving facades inside
  the `IMessageDispatcher` singleton factory is the silent `StackOverflow` P5.5 H2 fixed. So
  `IsModuleMapped` answers `false` for a routed module, and a plug-in offering a name a DI facade owns
  gets `true` from `TryMapModule` and then never runs. Precedence is correct; the answer is not.
  Documented on `TryMapModule` and in `ARCHITECTURE.md` rather than guessed at — closing it needs a
  name-reservation seam or re-opening the deadlock, and no consumer has hit it.

## 0.1.2 — 2026-07-31

### Changed

- **`WindowStateManager.Apply(Form)` and `AttachTo(Form)` now resolve per-monitor DPI by default.**
  The parameterless overloads defer to `HandleCreated` when the form has no handle yet, then
  resolve `DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)` at that moment — still before `Show`,
  so the restored geometry lands on the initial paint with no resize flash. On a mixed-DPI setup
  the form is now sized against ITS monitor's DPI, not the primary. The 0.1.1 default used
  `DpiHelper.SystemScale()` (the PRIMARY monitor) synchronously; adopters had to know two
  kit-internal details — that `DeviceDpi` was the right source and that `OnHandleCreated` was
  the only valid moment — and call the explicit `AttachTo(form, DpiHelper.ScaleFromDeviceDpi(
  form.DeviceDpi))` overload themselves. The scale-explicit overloads are unchanged and remain
  as the escape hatch for callers who want to size against a scale they resolve themselves
  (a test harness, a preview against a different monitor). Reported by the first adopter after
  Stage 1 adoption on 0.1.1.

### Fixed

- **`WindowStateManager.Apply` now defers the maximize application to `Shown` for a plain
  `Form` too.** In 0.1.1 the `RestoreMaximizedTag` deferral was `IAppMaximizable`-only; for a
  plain `Form`, `Apply` set `form.WindowState = FormWindowState.Maximized` synchronously — which
  goes back to `Normal` by `OnLoad`, so a window opened restored-down however it was closed.
  The fix extends the existing marker mechanism to plain forms via a one-shot `Shown` handler
  that consumes the same tag. Same shape `IAppMaximizable` implementors already had, one owner
  for "apply maximize once realized". Not a kit regression — the hand-rolled predecessor code
  had the identical bug — but the kit is the right place for it to be fixed once. Reported by
  the first adopter.
- **`WindowStateManager.Apply(Form)` now pre-positions the handle to the saved location before
  resolving `DeviceDpi`, closing a cross-monitor mixed-DPI hole in the initial fix.** The first
  cut of the `HandleCreated` defer read `form.DeviceDpi` immediately — but the handle is
  created wherever WinForms/Windows initially places it (typically the primary monitor, since
  `Location` hasn't been set yet), so on a mixed-DPI setup with a saved position on a
  different-DPI secondary monitor, `DeviceDpi` returned the wrong value and the restored size
  was computed against the wrong scale. The fix moves the handle to the saved location first;
  the move triggers `WM_DPICHANGED` synchronously, updating `DeviceDpi` to the target monitor
  before the scale is resolved. There is no auto-heal to fall back on — the WinForms default
  `WM_DPICHANGED` handler does not rescale a Form's outer `Size` (verified live in
  `devtools/_dpi-probe/`: Windows' `SuggestedRectangle` came back unchanged after a 200% → 150%
  scale change). Caught by adversarial phase review of the first-cut commit.

## 0.1.1 — 2026-07-31

### Added

- **`WindowStateManager.Apply(Form, double scale)` and `AttachTo(Form, double scale)` overloads**
  for per-monitor DPI accuracy. The existing parameterless forms use `DpiHelper.SystemScale()` —
  the PRIMARY monitor — because that is usable before the form has a handle, not because it is
  the most accurate answer: a form opening on a secondary monitor with a different DPI would then
  be sized to the wrong physical size. Callers who can defer to `OnHandleCreated` (handle exists
  → `DeviceDpi` reflects the real monitor, still before `Show` → no resize flash) call
  `AttachTo(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi))` instead. The paired `AttachTo`
  overload was added so that adoption path does not lose the save-on-close ordering guarantee
  `AttachTo` exists to protect (P5.5 H4.5). Reported by the first adopter.
- **`WindowStateOptions.MaxToWorkArea` (default `true`)** — shrink the restored physical size to
  the target monitor's work area when a size saved on a bigger display would overflow a smaller
  one (moving to a laptop, unplugging an external monitor). The MinWidth/MinHeight floor still
  applies. **Behaviour change** for the default case: a saved size that would previously overhang
  now fits — which was the point. Set `MaxToWorkArea = false` for the pre-0.1.1 behaviour.
  Position is validated separately by `IsVisible`, unchanged.
- **`WindowStateManager.ToPhysical` overload taking `IEnumerable<Rectangle> workAreas`** — the
  work-area-aware pure conversion that powers the clamp above. The three-argument overload is
  unchanged and continues to skip the clamp (documented).

### Fixed

- **`docs/ADOPTION.md`: the "hand-rolled uses `Screen.WorkingArea`, kit uses `GetMonitorInfo`"
  fix claim moved from the `WindowStateManager` row to the `OptimizedForm` row**, where the P/Invoke
  actually lives (`TryGetCurrentWorkArea`). The `WindowStateManager` row previously overpromised:
  an adopter taking that primitive without also adopting `OptimizedForm` did not get the fix,
  which they only discovered by reading the source. Reported by the first adopter.
- **`docs/ADOPTION.md`: Stage 1's "highest payoff" heading rephrased** — payoff is proportional
  to what the adopter actually hand-rolled. The row-by-row wording is unchanged; the intro now
  says each row = a specific replacement rather than a claim that every app benefits from every
  row (an adopter that already had a C++ splash launcher, no single-instance mutex and injectable
  shell delegates only saw two rows apply).

## 0.1.0 — 2026-07-31

### Breaking

- **`MapModule(IModuleFacade)` now THROWS when the module is already mapped**, instead of accepting
  it silently. A facade answers every request for its module, so a second mapping was always dead
  code — it simply never ran, with no error and nothing to grep for. This matches the eager DI path
  (`MapRegisteredModules`), which has always guarded duplicates. **Migration:** if a taken name is a
  normal outcome for you rather than a composition bug — dynamically composed modules — call
  `TryMapModule`, which returns false instead. Nothing in a static composition is affected: every
  module is mapped once.
- **`LoginWindowController` is now `SessionController`** (P5.5 H4.6). It was never login-specific:
  `CoBrowseSession.Controller` is typed with it and exposes it publicly, so a co-browse consumer —
  streaming a page for remote viewing, nothing to do with signing in — had to program against a
  login-named type. Pure rename: same members, same behaviour, and the types that ARE
  login-specific keep their names (`LoginWindow`, `LoginResult`, `LoginErrorCodes`,
  `CookieLoginFlow`, `LoginCookie`). Update the type name where you name it explicitly —
  `LoginWindow.RunAsync`'s driver signature and `CookieLoginFlow.DriveAsync` both mention it.
  Deferred deliberately: extracting a genuinely shared base out of `RenderSession` and
  `SessionController`. The neutral NAME is what fixed the surface problem; what the shared core
  should actually be is better decided when the co-browse API is reshaped (D21 / H9) than guessed at
  now.
- **The two Windows packages are now one layer, and the portable contracts moved to
  `Shenora.Core`** (D19 + D20; design: `docs/2026-07-30-shenora-relayering-design.md`).
  `Shenora.WebView2` now depends on `Shenora.WinForms` — the boundary is Windows *primitives* and
  *web hosting on top of them*, not two peers. `WinForms` still carries no `Shenora.Ipc` dependency,
  and `WinForms → WebView2` remains forbidden.
  **What a consumer must change:** add `using Shenora.Core;` where these types are referenced —
  `IFileDialogs`, `IFileDialogPathStore`, `FileDialogOptions`, `FileDialogFilter`,
  `FileDialogResult`, `IClipboardService` moved namespace (identical signatures otherwise). Nothing
  needs re-registering: `UseWinForms` registers the same implementations, now behind both the
  Windows and the portable interface.
  `IShellLauncher` and `IFormInteraction` were **split**, not changed: they now derive from
  `Shenora.Core.IUrlLauncher` and `Shenora.Core.IUiInteraction` respectively, so `OpenUrl`,
  `BlockInteraction` and `UnblockInteraction` are inherited rather than declared. Existing call
  sites compile unchanged; code that *implements* these interfaces still implements the same member
  set. Depend on the portable base where you only need the portable operation, and your logic
  compiles with no Windows reference — the point of the change (D16: mobile shells are a target).
- **`DpiHelper.ScalePixels`, `ScaleSize` and `ScalePoint` are removed** (P5.5 H6). They had no callers,
  and they were worse than unused: each baked in the PRIMARY monitor's scale, so any code that adopted
  them would silently mis-scale on a secondary monitor. Use `DpiHelper.Scale` with the DPI you mean —
  `ScaleFromDeviceDpi(control.DeviceDpi)` for anything attached to a control, `SystemScale()` only when no
  control exists yet.
- **`@shenora/react` no longer augments the global `Window` type** (P5.5 H6). The package shipped
  `declare global { interface Window { chrome?: … } }` in its `.d.ts`, which collides with `@types/chrome`
  in a consumer's program as an unfixable TS2717 in a file they do not own. A library must not claim
  global names; the transport now reads `window` through a local interface. No runtime change.
- **The dispatcher's composition helpers moved from `MessageDispatcher` onto `IMessageDispatcher`**
  (P5.5 H6). `Use(MessageMiddleware)` — the single primitive all of them already delegated to — is now an
  interface member, and `UseModule`/`UseRoute`/`UseLogging`/`UseErrorHandler`/`MapRoute`/`MapModule`/
  `UseScopedRouter`/`MapRegisteredModules`(`Lazily`) are extension methods over the interface
  (`MessageDispatcherExtensions`). **Why:** the interface exposed only dispatch/send, so a composition that
  maps a facade AFTER the container is built — the documented pattern for anything needing the live
  window — had to downcast. The reference composition did, and its `if (dispatcher is MessageDispatcher
  concrete)` had no `else`: registering a different `IMessageDispatcher`, or wrapping it in any decorator,
  silently dropped three whole modules and the frameless title bar just stopped working with no error.
  Adopters copy that branch.
  **What you must change:** almost certainly nothing — `dispatcher.MapModule(…)` etc. still compile
  through extension resolution. A fluent chain whose result you assign to a `MessageDispatcher`-typed
  variable now yields `IMessageDispatcher`; `AddMessageDispatcher`'s configure callback receives
  `IMessageDispatcher` instead of `MessageDispatcher`; and a custom `IMessageDispatcher` implementation
  must add `Use`. `UseLogging`/`UseErrorHandler` gained an optional `ILogger` and default to the
  dispatcher's own logger, so behaviour is unchanged.
- **`IpcResponse.CreateError`'s argument order now matches `OperationException`'s** (P5.5 H6):
  `(id, code, parameters, message)`, previously `(id, code, message, parameters)`. The two are siblings
  that build the same structured error from the same pieces, and they disagreed about the last two — so
  which one you were calling decided what a positional third argument meant. The shared order puts the
  wire-relevant piece first: `parameters` crosses to the client as i18n interpolation values, `message`
  is host-log only. Calls using `parameters:`/`message:` by name are unaffected; a positional third
  argument now fails to compile rather than silently landing in the wrong slot.
- **`BaseFacade` no longer calls `ConfigureAwait(false)` around your `RouteMessageAsync`** (P5.5 H6). It
  was the only such call in the dispatch path and it contradicted the documented context-preserving
  model — a facade routing a window command must be able to resume on the UI thread. If your facade
  relied on being resumed off the captured context, marshal explicitly.
- **`WebViewHost.AutoReloadCooldown` moved to `WebViewHostOptions.AutoReloadCooldown`** (P5.5 H3). It
  was a public static field, so it was neither per-host nor configurable. The new
  `WebViewHostOptions.MaxAutoReloads` joins it — see Fixed for why a cap was needed at all.
- **`OptimizedForm` is no longer a drop target.** It used to set `AllowDrop = true` with a `DragOver`
  handler, justified as letting a drop-zone manager see drags over the form — which is not how OLE drop
  works: targets are registered per HWND and `DropZoneOverlay` registers itself, so nothing in the kit
  ever used the form's drag events. All the flag did was force OLE (hence STA) on every consumer of the
  base class, and show a copy cursor for a drop it then silently discarded, since there was no
  `DragDrop` handler. If your app relies on form-level drops, set `AllowDrop = true` and wire your own
  handlers — plain WinForms, nothing needed from us. The IPC drop zones are unaffected.
- **The auxiliary-session surface is named for MECHANISM, not for scenarios** (P5.5 H9.7 + H9.8, D22).
  Two clusters of the public API were named after ONE use case each while containing no logic specific
  to it, which made the kit look like it shipped those products and forced unrelated consumers to
  program against their vocabulary. Renames only — no behaviour changed.

  | Was | Is |
  |---|---|
  | `LoginWindow` | `InteractiveSession` |
  | `LoginWindowOptions` | `InteractiveSessionOptions` |
  | `LoginResult` | `SessionResult` |
  | `LoginErrorCodes` | `SessionErrorCodes` |
  | `LOGIN_BUSY` / `LOGIN_CANCELLED` / `LOGIN_INCOMPLETE` / `LOGIN_ERROR` / `LOGIN_UNAVAILABLE` | `SESSION_BUSY` / `SESSION_CANCELLED` / `SESSION_INCOMPLETE` / `SESSION_ERROR` / `SESSION_UNAVAILABLE` |
  | `LoginCookie` | `SessionCookie` |
  | `CoBrowseSession` | `StreamingSession` |
  | `CoBrowseSessionOptions` | `StreamingSessionOptions` |
  | `CoBrowseInput` (+ `Pointer`/`Wheel`/`Text`/`Key`/`Viewport` variants, `CoBrowsePointerAction`) | `SessionInput` (+ `SessionPointerInput`/`SessionWheelInput`/`SessionTextInput`/`SessionKeyInput`/`SessionViewportInput`, `SessionPointerAction`) |
  | `CoBrowseFrame` | `SessionFrame` |
  | `CoBrowseEnded` / `CoBrowseEndReason` | `SessionEnded` / `SessionEndReason` |
  | `CoBrowseViewport` | `SessionViewport` |
  | `RunAsync`'s `driveLogin` parameter | `driver` |

  **`InteractiveSessionOptions.Title` now defaults to `"Session"`, not `"Sign in"`** — a default value,
  so this one is behavioural: set it explicitly if your window said "Sign in".
  **Why it mattered beyond tidiness:** `SessionController.GetCookiesAsync` returned
  `IReadOnlyList<LoginCookie>`, so a consumer streaming a page for remote viewing — nothing to do with
  signing in — had to name a login type. `LoginWindow` held no login logic at all: it is a busy-gated,
  profile-isolated browser window that runs an app-supplied driver until it captures a blob (a captcha,
  a terms acceptance, a checkout step). `CoBrowseSession` was an off-screen browser that streams frames
  and accepts input — co-browsing, remote support, visual capture or a preview pane, depending only on
  who wires it. **`CookieLoginFlow` deliberately keeps its name**: naming the scenario is the point of a
  reference driver (D21).
- **`StreamingSession` (was `CoBrowseSession`) takes TYPED input instead of an opaque JSON string**
  (P5.5 H9.1, D21). `DispatchInputAsync(string json)` → `DispatchAsync(SessionInput, CancellationToken)`.
  The old signature took the ORIGINATING APP'S wire protocol verbatim, so a consumer could not know what
  to pass without reading that app's client — the framework's contract was one application's message
  format. Construct `SessionPointerInput`/`SessionWheelInput`/`SessionTextInput`/`SessionKeyInput`/
  `SessionViewportInput`; coordinates stay FRACTIONS of the viewport, which is what keeps the protocol
  resolution-independent. **Migration is mechanical:** `SessionInput.TryParseLegacyJson(json, out var
  input)` parses the old shape, so an existing client keeps its frontend unchanged — it also now reports
  `false` on a malformed message instead of throwing it away silently.
- **`StreamingSession.Frames` is `ChannelReader<SessionFrame>`, not `ChannelReader<byte[]>`**
  (P5.5 H9.3). Each frame now carries the CSS viewport it depicts (`Jpeg`, `Width`, `Height`), read from
  that frame's own screencast metadata. Frames used to arrive as bare bytes with no geometry, so an app
  receiving fraction-coordinate input could not map a click back without inventing a side-channel —
  which is how a consumer ends up needing its own protocol anyway.
- **`StreamingSession.ReadHotspotsAsync()` is removed** (P5.5 H9.2). Returning a stringly-typed list of
  clickable-element rects is a co-browse UX decision, not a browser primitive — and it was
  `Task<string>`. Run it yourself through `session.Controller.ExecuteScriptAsync(...)`; the script that
  shipped is below verbatim, so nothing is lost:
  ```js
  (function(){try{
  var q='a[href],button,input[type=submit],input[type=button],input[type=image],[role=button],[onclick],label[for],select,summary';
  var els=document.querySelectorAll(q),W=innerWidth,H=innerHeight,o=[];
  for(var i=0;i<els.length&&o.length<80;i++){var e=els[i],r=e.getBoundingClientRect();
  if(r.width<8||r.height<8||r.right<0||r.bottom<0||r.left>W||r.top>H)continue;
  var s=getComputedStyle(e);if(s.visibility=='hidden'||s.display=='none'||s.pointerEvents=='none'||+s.opacity===0)continue;
  o.push([+(r.left/W).toFixed(4),+(r.top/H).toFixed(4),+(r.width/W).toFixed(4),+(r.height/H).toFixed(4)]);}
  return o;}catch(_){return [];}})()
  ```
- **`SessionBrowser.InitializeAsync` and `SessionBrowser.GetHtmlAsync` are now `internal`**
  (P5.5 H9.6). Both took a raw WinForms `WebView2` and had no consumer scenario — they mainly invited
  bypassing the render pool's accounting. Use `RenderSessionPool`, `InteractiveSession` or
  `StreamingSession`; `RenderSession.GetHtmlAsync()` is the supported way to read a rendered page.
- **The dispatch surface now carries a `CancellationToken`** (P6.4). The whole IPC pipeline was
  uncancellable: `DispatchAsync`, `SendAsync`, `MessageMiddleware`, `IModuleFacade.HandleMessageAsync`
  and `BaseFacade.RouteMessageAsync` took no token, so a handler could not observe one it was never
  given, and work still awaiting when the page navigated away or the host shut down had no way to
  learn that nobody was listening. `WebViewIpcBridge` now owns a lifetime CTS and cancels it in
  `Dispose`, so that signal reaches every handler.
  **What the token means, and what it does not:** it is the CALLER's lifetime, not per-request client
  cancellation. A one-way `post` has nobody waiting, so "the client changed its mind" remains an
  app-level CANCEL route carrying an operation id — what an operation IS belongs to the app (D21).
  Cancellation still surfaces as `OPERATION_CANCELLED`; `DispatchAsync`'s never-throws contract is
  unchanged, including for a token that is already cancelled on entry.
  **Migration.** Every parameter is optional (`= default`), so CALL sites compile untouched. What must
  change is anything that IMPLEMENTS or OVERRIDES:
  * `protected override Task<object?> RouteMessageAsync(IpcRequest request)` →
    `(IpcRequest request, CancellationToken cancellationToken)` — every facade. Ignore the parameter
    for quick synchronous work; observe it for anything that awaits.
  * a custom `IMessageDispatcher` or a decorator: add the parameter to `DispatchAsync` and both
    `SendAsync` overloads, and FORWARD it (a decorator that drops it silently disables cancellation
    for everything behind it).
  * a custom `IModuleFacade`: add it to `HandleMessageAsync`.
  * `Use(async (request, next) => …)` → `Use(async (request, next, ct) => …)`; `UseModule`/`UseRoute`
    handlers and `ModuleRouteBuilder.RouteAsync` take `(request, ct)`. `MapRoute`'s synchronous
    handler is unchanged.
  ⚠ **A lambda parameter named `_` shadows the discard.** Writing `async (request, _) =>` and then
  `_ = SomethingAsync();` inside it assigns to the token parameter instead of discarding — it is a
  compile error here, but only because the types happen to differ. Name it `ct`.
- **`IEventBus` gained `Emit`** (two overloads, fire-and-forget). Additive for CALLERS; **breaking for
  anyone who implements `IEventBus` themselves** — a test double or a substitute registered over the
  built-in one needs the two new members. See `### Added` for why it exists.
- **`IModuleRegistry.TrackMappedModule(string)` is now `TryClaimModule(IModuleFacade)`, and there is
  a matching `TryReleaseModule(string)`.** Claim and release have to be ONE owner's job: the registry
  can only take a route out again if it holds the routing it installed, and splitting "remember the
  name" from "install the route" is exactly what made release impossible. The claim is also ATOMIC
  now — check and install happen under one lock, so two threads offering the same plug-in name
  concurrently cannot both win, which the previous check-then-map could allow.
  **Migration:** apps never called `TrackMappedModule` (its own doc said so); use
  `MapModule`/`TryMapModule` as before. A DECORATOR that implements `IModuleRegistry` must forward
  the new members instead of the old one.
- **A deferred scheme's `Handler` now takes a `WebViewResourceRequest` and returns a
  `WebViewResourceResponse`**, instead of `Func<Uri, Task<(byte[], string)>>`. See `### Added` for
  what that unlocks and why it could not be done additively — the old signature had no room for a
  request header, a status code, or a stream.
  **Migration**, mechanically:
  `Handler = uri => Task.FromResult((bytes, "text/plain"))` becomes
  `Handler = request => Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.Bytes(bytes, "text/plain"))`.
  Returning null now means 404, and throwing still does (with the message kept host-side, as before).
- **`CookieLoginFlow` and `CookieLoginFlowOptions` are REMOVED from `Shenora.WebView2.Sessions`.**
  They were a product workflow shipping as library surface: `LoginUrl`, `CookieReadUrl`,
  `AuthCookiePatterns`, `RevealDelay` and `CaptureAllCookies` are one app's login recipe, and only an
  app doing cookie logins would use that API unchanged. Two decisions had talked each other into it —
  D21 blessed shipping "one opt-in reference driver", D22 then justified the scenario NAME because
  D21 had blessed shipping it — and neither ever applied D21's own test. Both are amended: **the kit
  ships no drivers**, and a type that needs a scenario name to make sense is telling you it does not
  belong in `src/`.
  **Migration:** the recipe now lives in the desktop sample as `CookieLoginDriver` — copy that file
  into your app and edit it; it is yours. Nothing else changes, because the driver only ever consumed
  public seam members (`InteractiveSession.RunAsync`, `SessionController.GetCookiesAsync`/
  `NavigateAsync`/`Reveal`/`SetLoading`). That it ports across as a plain consumer is the proof D21
  asks for. `SessionCookie` stays — a cookie is a browser primitive, not a login concept.
  A whole-surface audit went with it, by the documented method (sweep the API baselines for domain
  vocabulary): this was the ONLY product leak left. Everything the sweep flagged is genuine browser or
  platform vocabulary — `DownloadHit`/`OnDownloadStarting`, `SessionCookie`, `MuteAudio`,
  `ProfileDirectory`, `UserDataFolder`, `Module`.
- **Missing XML docs are now build ERRORS** (CS1591 unsuppressed, P7 docs sweep). Every public and
  protected member across all five packages is documented. Adding an undocumented public member no
  longer compiles — deliberate, because a public member is SemVer surface from 1.0 and "document it
  later" is how an API ends up with members nobody can explain. Turning it on immediately caught a
  broken `<see cref="..."/>` that had been invisible while warnings were non-fatal.

### Added

- **`IModuleRegistry` + `IMessageDispatcher.TryMapModule` — a dispatcher can say what it routes.**
  Module ownership used to be implicit: nothing recorded that a name was taken, so mapping the same
  module twice was silent (the second facade never ran, with no error). Any app composing its IPC
  surface DYNAMICALLY needs to know — plug-ins, features behind a licence or flag, per-tenant
  modules, lazily loaded areas — and for a module arriving from outside the app it is a boundary
  question: a late mapping that quietly shadowed an earlier one would take over that channel.
  `MessageDispatcher` now implements `IModuleRegistry` (`MappedModules`, `IsModuleMapped`,
  `TrackMappedModule`), kept OFF `IMessageDispatcher` so that interface stays the four things a
  dispatcher IS and a decorator still has four members to write. `TryMapModule` maps unless the name
  is taken; it **throws** rather than answering when the dispatcher does not implement the registry,
  because reporting a name as free is the dangerous wrong answer.
  KNOWN LIMIT, stated rather than papered over: a mapped module cannot be RELEASED — the pipeline
  only grows, so disabling a dynamic module needs a restart. No consumer has needed runtime removal
  yet, so the kit does not guess at that surface (`TASKS.md`).
- **`ShenoraBridge.post` — send without awaiting a reply**, and `createShenoraStore` — a store fed by
  one module's host event stream (P6.3a; design:
  `docs/2026-07-31-shenora-oneway-ipc-design.md`). Until now `invoke` was the ONLY outbound call, so
  every page→host message paid a correlation entry and a 30 s deadline, and — because the dispatch
  pipeline preserves the caller's synchronization context by design — ran its handler's synchronous
  segment on the UI THREAD. That made the wrong shape the only shape for a desktop app. `post` sends
  the same envelope with no pending entry and no timer (so no wire change: a transport and the host
  cannot tell the two apart), returns the request id so a caller can correlate, and reports a FAILED
  response through the new `onPostError` option instead of dropping it — an unmatched response was
  previously discarded silently. Reserve `invoke` for calls that are quick AND UI-thread-safe (the
  window commands are the model) and post everything else.
  `createShenoraStore(module, { initial, snapshot, on, actions })` returns one hook that declares a
  feature's sends, its event reducers and its shared state together. It opens ONE subscription per
  event type however many components read it, and takes a **snapshot on the first subscriber** so a
  component that mounts while work is already running sees current state — a stream cannot be
  replayed, which is the case a progress strip hits every time its tab is opened. Built on React's
  `useSyncExternalStore`, so the package still depends on nothing but React. Reducers are pure and a
  throwing one is reported rather than corrupting shared state. `useShenoraEvent` is unchanged and
  remains the counterpart: **shared or long-lived state → the store; a one-off reaction in one
  component → the hook.** Deliberately no job/queue/progress type — what an operation IS stays in the
  app.
- **Frameless caption buttons now behave like real ones — Snap Layouts, hover and press (P5.6).**
  New `OptimizedFormOptions.NativeCaptionButtons`: the cluster reported to
  `OptimizedForm.SetCaptionButtons` is cut out of the window region of **every direct child that
  covers it**, so those pixels become the form's own client area and the OS finally routes real mouse
  input there — which is the only way Windows 11 offers the Snap Layouts flyout on a maximize button
  a page drew. The window then paints the three buttons itself, with the standard Windows chrome
  glyphs and the maximize↔restore swap.
  New `CaptionButtonColors` (+ `OptimizedForm.CaptionButtonColors`) carries the palette: same split
  as `TrayMenuColors` — the kit owns the renderer (glyphs, hit states, DPI), your app owns every
  colour, because the kit ships no design (D13). Leave it null and a neutral palette is derived from
  the form's `BackColor`, so a half-wired app sees buttons rather than an empty rectangle.
  **Adopting it:** set the option, set the colours, and keep reporting the rectangles you already
  report through `SET_CAPTION_BUTTONS`; the union of those rectangles IS the hole, which is what
  makes it correct at every DPI (the cluster is ~250 physical px at 200% scaling, so any constant
  guessed at 100% cuts through the buttons). Your page should keep RESERVING that space — whatever it
  draws there is clipped away and invisible. Because the clip covers every child rather than one
  named control, the buttons also work while a splash panel is up, i.e. the window is closable before
  the frontend has loaded. `CaptionButtonStateChanged` is unchanged and still the right hook when the
  option is OFF and your app draws the buttons itself.
  This supersedes the previous release note that these types were NOT FUNCTIONAL over a WebView2.
- **The auxiliary session browser gained the three event policies it shipped without** (P5.5 H4.4):
  `NewWindowRequested` is suppressed (a pooled page calling `window.open()` used to get a real,
  visible popup in an app with no session UI), `PermissionRequested` is denied by default (an
  invisible page cannot meaningfully prompt, and an unanswered request stalls whatever asked), and
  `ProcessFailed` is now surfaced through a new `onProcessFailed` parameter on
  `SessionBrowser.InitializeAsync`. That last one closes a hang: a dead renderer was previously
  INVISIBLE, so the pool reset and re-leased the corpse forever, and a co-browse frame channel simply
  stopped with its reader waiting for a stream that could never resume. The pool now marks such an
  instance poisoned and discards it instead of re-pooling; co-browse completes its channel. Script
  dialogs are also disabled — an `alert()` in an off-screen page blocked its JS thread behind a dialog
  nobody could see or dismiss.
- `SessionBrowserOptions.IsDevelopment`, which re-appends `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` so
  a session browser is reachable over CDP. Setting `AdditionalBrowserArguments` at all makes WebView2
  ignore that variable; the sessions package had re-introduced that gotcha by hand-building its
  argument string.
- `BrowserArguments.Compose(preset, isDevelopment, devExtraArguments, additionalArguments)` — the one
  place that knows the two argument invariants, now shared by both presets: each features switch
  appears exactly ONCE (caller lists are MERGED, so an app appending its own `--disable-features=`
  can no longer silently discard the whole preset — the incident this class documents), and the dev
  CDP arguments are re-appended by hand.
- `Log` options on `SessionBrowserOptions`, `RenderSessionPoolOptions` and `CoBrowseSessionOptions`
  (P5.5 H4.7). The sessions package shipped with no logging of any kind against ~30 swallowed
  catches, so a wedged pool or a failing request filter was undiagnosable in production.
- **`IUiDispatcher` + `UiTargetState` (`Shenora.Core`) and `WinFormsUiDispatcher` (`Shenora.WinForms`)**
  — the single UI-thread marshalling seam the design contract specified from the start and P2 never
  built, which is how the pattern ended up hand-rolled 14 times across three packages with five
  mutually incompatible pre-handle policies. The target is deliberately **three-state**
  (`NotReady`/`Ready`/`Gone`) rather than one availability flag: "no handle yet" and "gone" require
  different caller behaviour, and three call sites in the kit have review-earned pre-handle policies
  that a bool would silently break. The dispatcher is per-CONTROL (sessions marshal to their anchor
  form; secondary windows run their own pumps), guards the body on both the posted and the inline
  path, and its awaitable overloads observe their cancellation token — an operation that accepts a
  token and ignores it cannot be cancelled when the UI thread is wedged.
- `LoginWindow.ComposeProfileDirectory(root, params segments)` — builds a per-account profile path
  from untrusted identifier segments, rejecting separators, `..`, drive qualifiers, invalid
  file-name characters and Windows reserved device names. Per-provider/per-account scoping is the
  session stack's isolation boundary, and the library previously documented that boundary while
  shipping no safe way to construct the path.
- **`Shenora.Core.AppCallback`** (P5.5 H2) — the one guard for invoking APP-SUPPLIED code from a place
  where an escaping exception is fatal rather than catchable: a UI-thread event handler, a timer tick, a
  posted delegate, a dispose path. `Run` returns whether the callback completed; `RunOrDefault` returns
  its answer or an explicit policy fallback. Both swallow, deliberately — at these sites the
  alternative to losing the callback's exception is losing the operation, the window, or the process —
  and the optional error sink is itself guarded, because a failure reporter that throws must not become
  the crash it was reporting. Public because three packages consume it (D19/D20 placement law); apps can
  use it against their own extension points for the same reason. Every app callback and log sink in
  `Shenora.WebView2`, `Shenora.WebView2.Sessions` and `OptimizedForm.WndProcHook` now routes through it
  — see Fixed.
- **`RenderSessionPoolOptions.OpTimeout`, `NavigationTimeout` and `ResetTimeout`** (P5.5 H2) — the
  three budgets a leased session runs on, all validated at construction. `OpTimeout` (60 s) caps ONE
  marshalled operation (navigate / script / HTML read / CDP call) and is the piece that lets the pool
  recover from a wedged page: see Fixed. `NavigationTimeout` (30 s) is the document-load cap that used
  to be hardcoded — a SOFT cap, since the caller decides what "settled" means. `ResetTimeout` (5 s)
  bounds the return-to-pool reset. Keep `OpTimeout` above `NavigationTimeout`, or a legitimately slow
  load is reported as a wedge.
- **`StreamingSessionOptions.OnEnded` — the session lifecycle hook** (P5.5 H9.3, D21). Called exactly
  once with a `SessionEnded(SessionEndReason, string? Detail)` when the session ends. A dead renderer
  and a clean `DisposeAsync` both complete the frame channel, so a reader alone could never tell a
  crash from a shutdown; now it can. Fired through a shared latch because the two paths genuinely race,
  and invoked GUARDED — a throwing handler cannot take down the session or the UI thread.
- **`SessionResult.ThrowIfFailed()`** (P5.5 H9.4) — throws the outcome's failure as an
  `OperationException`, bridging `SessionErrorCodes` into the IPC error contract. The codes were always
  SCREAMING_SNAKE i18n keys in the shape `IpcErrorCodes` uses; what was missing was a typed path, so
  every app routing a session over IPC hand-wrote the same throw. Throwing (rather than returning an
  error object) is what plugs into the dispatcher's documented boundary — `BaseFacade` and
  `MessageDispatcher` already map an `OperationException` to the structured wire error.
- **`SessionBrowser` initialization observes a `CancellationToken`** (P5.5 H9.6), wired through the
  render pool and the streaming session. A cancelled lease used to wait out the full `InitTimeout`
  (up to 2×25 s) before anything noticed. The token gates the AWAIT only, never the creation — with the
  per-profile environment cache that task is SHARED across a pool's instances, so cancelling it for one
  caller would break the others.
- **Caption buttons the OS treats as real — the hit-test plumbing (P5.6).** This entry describes the
  MECHANISM; see `OptimizedFormOptions.NativeCaptionButtons` above for the finished feature and how to
  turn it on. (An earlier revision of this entry said "NOT YET FUNCTIONAL — do not adopt": that was
  true of the first attempt, which answered `WM_NCHITTEST` on a door the OS never knocked on, because
  WebView2 covers the client area with child windows owned by the BROWSER PROCESS and they cannot be
  subclassed to decline. Coverage turned out to be the only lever — the window now CLIPS those pixels
  out of every covering child — and the flyout has been confirmed by a human.)
  A frameless app draws its own minimize/maximize/close, and until now they were buttons the
  OS knew nothing about: no snap flyout, and no hover affordance the page could render faithfully.
  New in `Shenora.WinForms`: `CaptionButtonKind`, `CaptionButtonRegion`, `CaptionButtonState`,
  `OptimizedForm.SetCaptionButtons(...)` and `OptimizedForm.CaptionButtonStateChanged`. New in
  `Shenora.WebView2`: `WindowCommandOptions.SetCaptionButtons` + `CoordinateSpace`, enabling the
  `SET_CAPTION_BUTTONS` route (optional, same shape as `SET_THEME`). New in `@shenora/react`:
  `WindowCommands.setCaptionButtons` with `CaptionButtonKind`/`CaptionButtonRect`.
  **How it works, and the part worth knowing before adopting it:** Windows shows the Snap Layouts
  flyout only over a window that answers `WM_NCHITTEST` with `HTMAXBUTTON`, so the page reports where
  it drew its buttons and the window claims those rectangles. Claiming them COSTS the page every
  mouse event there — the OS treats them as non-client, so your `onClick` handlers and CSS `:hover`
  stop firing inside them. The kit therefore performs the click itself (through the same
  `ToggleMaximize`/`Close` the IPC commands use, so a frameless manual maximize keeps its
  bookkeeping) and pushes hover/pressed state out for you to render. Headless as ever (D13): the kit
  ships no CSS — what hot and pressed look like, including whether close goes red, stays yours.
  Re-send the rectangles whenever your layout changes; they are a snapshot, and a stale one moves the
  hit-test off the button the user can see. Opt-in throughout: register nothing and every message
  falls through exactly as before.
- **`ShenoraEventBus.subscribeToAll` / `.subscribeToModule`** — the two broad subscription breadths
  the client was missing (P6.4). The host's `IEventBus` had shipped `SubscribeToAll`/`SubscribeToModule`
  from the start and `WebViewIpcBridge` itself consumes the former, so the client was the asymmetric
  half of one concept: it could only subscribe to an exact `(module, type)`, which is unusable for any
  observer that cannot enumerate the event vocabulary up front — a plug-in-contributed event stream, a
  diagnostics or telemetry tap, a bridge folding the whole stream into another state library, or an
  adoption shim keeping a legacy "every host message" handler alive. Both return an unsubscribe
  function (React-effect friendly) and honour the same scope rule as `subscribe`.
  **Delivery is narrowest-first — exact pair, then module, then catch-all** — so a broad observer never
  runs ahead of the feature code it observes. Unlike the host, the breadths are NOT expressed as a `"*"`
  sentinel inside the key: separate collections mean a module or type an app legitimately names `*`
  can never silently become a catch-all (the `'\0'`-join lesson, applied before it could be earned
  twice — there is a test pinning it). `getSubscriptionCount(module, type)` now answers "how many
  listeners would receive this", counting the broad subscriptions that match; with no arguments it
  still counts everything.
  Found by building the two adoption adapters against the public surface and hitting the wall: the
  workaround — tunnelling every event through one reserved `(module, type)` pair — is expressible, but
  it makes adoption all-or-nothing per event, because tunnelled events are invisible to
  `useShenoraEvent` and `createShenoraStore`.
- **`IpcErrorMapping` is public** — `ToError(exception, …)` for a wire error and
  `ToErrorResponse(request, exception, …)` for a full response. It was internal, on the reasoning that
  a facade gets the error boundary free from `BaseFacade`. True, and beside the point for the case
  that found it (P6.4): an app whose IPC surface reports failures as EVENTS has no response to attach
  an error to, so it had to retype the policy — which is precisely the fifth copy this type was
  created to prevent, and its own doc says the copy that forgets `ex.GetType().Name` and passes
  `ex.Message` is how a path or a connection string reaches the page. Now it is surface rather than a
  rule people are told about.
  Note the sharp edge it documents and a test pins: an `OperationException`'s MESSAGE crosses the wire
  verbatim, because those are the app's own words for an expected failure — so never build one from an
  arbitrary `ex.Message`. That turns the one sanctioned channel into a bypass of the whole boundary.
- **`IEventBus.Emit(…)`** — emit without awaiting the handlers, for a caller that has no `await` to
  offer: a synchronous `Action`-shaped callback, a timer tick, a UI event handler. It is deliberately
  not "just" `_ = EmitAsync(…)` at the call site even though that is what it does. Discarding a task
  is normally a hazard, and whether it is safe here depends on an internal guarantee — every handler
  runs inside the bus's own guard, so the task cannot fault because of a subscriber. A caller could
  only learn that by reading the implementation, which is the actual finding: the guarantee is the
  API's to state, so it states it. Argument errors still throw synchronously — those are caller bugs.
- **`IMessageDispatcher.TryReleaseModule` — a dynamically composed module can now be turned OFF.**
  The pipeline only ever grew, so disabling a plug-in, dropping a per-tenant module when the tenant
  goes away, or unloading a lazily loaded area meant restarting the app. That was recorded as a known
  limit on the grounds that no consumer had needed it; "restart to disable a plug-in" is not something
  an adopter should have to design around, so it is closed. Releasing frees the name for a
  replacement, and `MappedModules` tells you what is releasable.
  **Two things it deliberately does not do.** Requests already executing inside the facade run to
  completion — this removes the ROUTE, it does not abort work in flight, and a caller mid-request
  still gets its answer. And the facade is NOT disposed: its lifetime belongs to whoever created it
  (usually the DI container), so disposing it here would kill a shared instance under another caller.
  Removal is surgical — the released module's entry comes out and the relative order of everything
  else (error handler, logging, app middleware, scoped router) is preserved exactly, which is the part
  that had to be right and has its own test.
- **A deferred scheme can answer any HTTP response, not just "200, here are all the bytes"** —
  `WebViewResourceRequest` (uri, method, headers) in, `WebViewResourceResponse` (status, reason,
  headers, content STREAM) out, plus `WebViewByteRange.TryParse` for the `Range` header.
  Two things were impossible before: a handler never saw a request header, so `Range` was invisible
  and **nothing it served could be sought** — a media element cannot seek a resource whose handler
  has no way to learn what offset was asked for; and it returned the complete `byte[]`, so a 4 GB file
  meant 4 GB of memory. One of the surveyed apps had to bypass the seam entirely and hook WebView2
  itself for exactly this, with an ADR explaining why (P6.6). It is not a media feature: conditional
  GETs, redirects, per-asset CORS and streaming-without-buffering were all equally unreachable.
  `WebViewByteRange.TryParse` ships because each of the three legal forms is its own chance to be
  wrong — `bytes=0-499`, `bytes=500-` (what a player actually sends when it seeks), and `bytes=-500`,
  a SUFFIX meaning the last 500 bytes, which hand-rolled parsers reliably read as "from 500". A start
  past the end is reported unsatisfiable rather than clamped, because clamping serves bytes nobody
  asked for with no error; `WebViewResourceResponse.RangeNotSatisfiable` carries the `Content-Range`
  the spec requires so a client can retry instead of looping on the same bad range.
  `Ok`/`Bytes` advertise `Accept-Ranges: bytes`, without which a media element will not even attempt
  a seek — which looks exactly like "seeking is broken" while the handler is perfectly capable.

### Changed

- **`DropZoneManager` clears its zones on DOCUMENT CHANGE instead of on the ready handshake.** It
  now subscribes to `ContentLoading` itself, so **apps should delete their `ClearAll()` call from
  `OnClientReady`** — leaving it in is harmless but pointless. This removes an ordering contract
  rather than documenting it: a `REGISTER` that arrived before `READY` was destroyed *after being
  acked*, leaving a zone the client believed was live and the host had forgotten, silent on both
  sides — and React's child-before-parent effect order made that the DEFAULT outcome for the obvious
  "call `notifyReady()` once at startup" composition. `useDropZone` therefore has no ordering
  constraint against `notifyReady()` any more. `ClearAll()` remains public for apps that want it.
- **`ShenoraEventBus.subscribe` takes an options object with `scope`, and `useShenoraEvent` passes it
  through** (P5.5 H6). Additive — existing calls compile unchanged. The wire has always carried a scope
  and the host has always keyed on it, but the client had no way to express one, so a component in one
  scope also woke for every other scope's events. The host's rule is mirrored exactly: no subscriber
  scope means every scope, and a global (scope-less) event still reaches scoped subscribers.
- **`BaseModuleService<TRequests>` is now constrained to `object`, not `Record<string, unknown>`**
  (P5.5 H6). The old bound was unsatisfiable by a plain `interface`, so the documented example and the
  README snippet failed with TS2344 — the first thing an adopter copies. Satisfying it the way the kit's
  own `windowCommands.ts` did widened `keyof TRequests & string` back to `string`, so a mistyped request
  type compiled and every payload collapsed to `unknown`: the typed-service feature checked nothing.
  Drop `extends Record<string, unknown>` from your request interfaces — with it, you keep the old
  no-checking behaviour.
- **The npm tarball now ships its LICENSE**, and `"./package.json"` is exported (P5.5 H6). The manifest
  declared MIT while shipping no license text; `dev.mjs doctor` now checks the package's copy byte-matches
  the repository root's, so the two cannot drift.
- `IpcErrorCodes.scopeRequired` (`SCOPE_REQUIRED`) is now exported from `@shenora/react`; it was emitted
  by the host but missing from the client, so a scoped app had to hard-code the string. A new
  `ClientOnlyIpcErrorCodes` export names the codes that exist only client-side (`TIMEOUT`,
  `NO_TRANSPORT`), which is what lets a test enforce the mirror instead of trusting care.
- **The verification gate now covers what it claimed to** (P5.5 H5): `Shenora.slnx` includes the
  sample projects and `Shenora.Core`, so `dev.mjs build|verify` compiles the reference composition
  and the e2e subject (the solution's `samples` folder was empty, so the sample could be red while
  `verify` reported green); `verify` also type-checks the sample web app and runs `doctor`;
  `dev.mjs test <unknown-target>` now fails instead of exiting 0 having run nothing; warnings are
  errors for `src/` (`TreatWarningsAsErrors`, `CS1591` still suppressed pending the P7 doc sweep)
  and are no longer hidden by `-clp:ErrorsOnly`; `vite` installs the sample's own dependencies and
  builds `@shenora/react` first.
- **The sensitive-info guard fails CLOSED** (P5.5 H5): a missing `local/sensitive-patterns.txt` used
  to print a notice and continue with only two structural patterns, so the private-name half of the
  scan silently did not run on a fresh clone or in CI. It now exits non-zero; pass
  `--allow-builtins-only` (or set `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1`, as the release workflow
  does) to opt in deliberately. It also scans file PATHS as well as contents, includes
  renamed/copied staged files (`git mv` stages as `R` and was skipped entirely), and a new
  `commit-msg` hook scans commit messages — which are history too.
- `create_tag: false` no longer produces a tag: the release step was always given `tag_name`, so it
  created the tag itself whenever the gated tag step was skipped — at the default-branch head,
  which need not be the published commit.
- A pool configured with a `NavigationGuard` now cancels unvetted CROSS-HOST navigation. See Fixed.
- **The `notifyReady()` → drop-zone-reset ordering contract is now documented on the surface**
  (P5.5 H7). No behaviour change; it was already sharp enough to bite and lived nowhere. A host clears
  the previous page's drop-zone overlays on the ready handshake, so a `REGISTER` that arrives BEFORE
  `READY` is discarded *after being acked* — the client believes its zone is live, the host has
  forgotten it, and nothing is logged on either side. In React this is the DEFAULT outcome rather than
  bad luck, because CHILD effects run before PARENT effects: the obvious reading of "call `notifyReady`
  once at startup" is a root-component effect, which runs after every child's `useDropZone` has already
  registered. Keep the handshake in the same component as, and declared above, anything that
  registers — or await it before rendering the subtree that does. Written on
  `ShenoraBridge.notifyReady`, `UseDropZoneOptions`, `DropZoneManager.ClearAll` and the npm README.
  `notifyReady()`'s promise REJECTS on a failed handshake, which is now stated too: `void`-ing it makes
  an unhandled rejection, and in a WebView2 page that is a silent console error.
- **The `@shenora/react` docs stopped using `'TODO'` as the example module name** (P5.5 H7). It was
  indistinguishable from an unfinished-work marker in published documentation — and it was the only
  `TODO` anywhere in `src/`. The example domain is now `NOTES` / `NoteService` / `Note`; nothing in the
  API changed.

### Fixed

- **Custom-scheme serving actually works now — `DeferredSchemes` had never served a request.** The
  host added a `WebResourceRequested` filter for `scheme://*`, but nothing registered the scheme with
  `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations`, and WebView2 accepts those only when the
  ENVIRONMENT is created — so every request was rejected by the network stack before the filter was
  consulted. Only `http`/`https` deferred schemes could work, and those were already `VirtualHost` /
  `FolderMappings`, so the feature as documented was empty. Found by an end-to-end probe; the unit
  tests, the API baseline and the docs all agreed it worked.
  **New:** `WebViewEnvironmentOptions.CustomSchemes` + `WebViewCustomScheme`
  (`Name`, `TreatAsSecure`, `HasAuthorityComponent`, `AllowedOrigins`). `WebViewHost` now THROWS at
  construction when `DeferredSchemes` names a non-http(s) scheme the environment does not register —
  the runtime symptom is otherwise a bare `TypeError: Failed to fetch` with nothing in the host log,
  which is undiagnosable from either side.
  **Also fixed, and needed before any of it worked in a page:** deferred-scheme responses now default
  `Access-Control-Allow-Origin: *` and `Access-Control-Expose-Headers: *` (both overridable per
  response). An app scheme is a different ORIGIN from the page that loads it, so without the first
  every fetch is refused; without the second a correct 206 arrives with the right bytes while
  `Content-Range` reads back as **null**. The bundle path already set the former; this path never did.
  **Migration:** add `CustomSchemes = [new WebViewCustomScheme { Name = "…", AllowedOrigins = […] }]`
  to your environment options for each app scheme. The constructor error names the exact fix.
  Note that changing a scheme registration on an existing app can wedge startup until its WebView2
  user-data folder is deleted — documented in `docs/ADOPTION.md`.
- **Maximizing and restoring a SNAPPED frameless window now exits the snap**, matching every other
  Windows app. `OptimizedForm.Maximize` captured the live window rect as its restore target, which
  for a snapped window is the docked half — so restore put the window straight back into the dock. It
  now captures `WINDOWPLACEMENT.rcNormalPosition`, which is Windows' own restore rectangle and which
  Aero Snap leaves at the pre-snap geometry.
- **A route mapped while requests were in flight could answer `NO_HANDLER`** (P5.5 H6). Late mapping is a
  supported, documented pattern — the WinForms host maps its window facades after the form exists — but
  `MessageDispatcher.Use` reassigned a `Lazy` field over an unsynchronized `List<T>` with no
  synchronization anywhere, so a concurrent dispatch could read the old cached pipeline and report no
  handler for a route that was by then registered, and a pipeline build enumerating the list while `Add`
  grew it was a plain data race. The middleware list is now copy-on-write, the built pipeline is volatile,
  and invalidate-then-rebuild happens under one lock.
- **Cancellation is no longer reported as `UNKNOWN_ERROR`** (P5.5 H6). New
  `IpcErrorCodes.OperationCancelled` (`OPERATION_CANCELLED`, mirrored on the client) means a UI can stay
  silent for the one failure it should not report as an error. Placed after `OperationException` in the
  mapping, so an app that models cancellation with its own code keeps its own words. The reference
  composition had already hand-rolled this arm — the tell that every adopting app would have had to.
- **A scope invalidated mid-request failed instead of using the rebuilt scope.**
  `ScopedContainerRouter.HandleAsync` now retries once on `ObjectDisposedException` (and not at all while
  the router itself is disposing, so shutdown cannot spin). `InvalidateScope` is a documented app-facing
  call that can fire while requests are in flight, so this race is normal, not exceptional.
- `EventBus.EmitAsync(module, type, …)` rejects an empty module or type instead of building an event that
  could never match any subscription; and `SubscribeCore` now publishes `_patterns` last — it is what
  `EmitAsync` enumerates, so a concurrent emit could previously see a subscription whose handler and
  match cache were not written yet, making its `continue` mean something other than the "concurrently
  unsubscribed" its comment claims.
- **An option added to `ShenoraPathsOptions` would have been silently dropped under `--app-root`.** The
  merge hand-copied all six properties into a new instance; the type is now a `record` and the merge uses
  `with`.
- **Notifications could stop for the rest of the process** (P5.5 H3). The ready gate closed on EVERY
  `NavigationStarting`, but the client sends `READY` only once per real page load — so a navigation that
  never replaced the document (one an app tap or a policy cancelled, one that failed before committing)
  closed the gate permanently on a page that was still alive: notifications buffered to the 10 000 cap
  and then silently dropped the oldest, forever. The gate now closes on `ContentLoading`, which is raised
  only when a new document actually begins loading. It also closes on `ProcessFailed` — a dead renderer
  left it OPEN, so the next tick drained a whole batch into a process that could not receive it, and
  since the queue was already emptied those notifications were simply gone.
- **Six unvalidated options that failed far from their cause** (P5.5 H3), now all rejected at
  construction: `MaxQueuedNotifications = 0` made `Enqueue` dequeue the item it had just enqueued, so
  every notification for the life of the process vanished with no error and no log line;
  `NotificationInterval` below 1 ms (or above the WinForms timer's int32 millisecond limit) threw from
  inside `Attach()`; `SessionBrowserOptions.InitTimeout = 0` failed init instantly with the
  profile-LOCK diagnosis, sending the caller hunting a zombie browser process that did not exist;
  `RenderSessionPoolOptions.OffscreenClientSize` of zero gave a 0×0 viewport in which pages "load" with
  every element sized zero; and `ScopedContainerRouterOptions.ConfigureScope` set to null surfaced as an
  NRE from inside scope creation, reported to the client as `UNKNOWN_ERROR` (`required` compels the
  caller to write the initializer, not to write a non-null value). `ConfigureScope` now also documents
  that each scope is a ROOT provider, so `AddScoped` there behaves as a per-scope singleton — the
  opposite of what it means elsewhere in Microsoft DI.
- **`WebViewHost.InitializeAsync` is idempotent, and its timeout covers the whole sequence** (P5.5 H3).
  The timeout message advises "start again", so a Retry button is the expected recovery — and a second
  call re-ran the event-policy wiring, double-subscribing every handler: from then on each external link
  opened TWICE, each download decision ran twice, and the renderer auto-reload raced itself. A failed
  initialization clears the cached task so a retry is still a real retry. Separately, each step used to
  get its own full `InitTimeout` — so the documented 25 s was really 50 s before the sequence even
  reached `ApplySettings`, and script injection was unbounded on top of that.
- **One transient WebView2 environment failure was terminal for the process.**
  `WebViewEnvironment.GetSharedAsync` cached its task with `??=`, faulted or not, so every later
  attempt — including the retry the init-timeout message asks for — got the original exception back
  without ever touching WebView2 again. A faulted or cancelled task is now evicted when observed.
- **A mistyped resource prefix opened a black window with no error.** The prefix depends on MSBuild's
  manifest-name mangling, so it matches nothing silently and every request 404s. `WebViewHost` now fails
  at `Navigate()` with an actionable message when the start document IS the packaged bundle and the
  provider has no `index.html`, and `EmbeddedResourceProvider` reports a can-serve-nothing configuration
  (new `CanServe` property) naming the bad prefix and the assembly's actual manifest prefixes. The check
  is deliberately not in the provider's constructor: a provider with nothing to serve is correct when
  the page loads from a dev URL, which is the normal state of a freshly cloned repo.
- **Exception text no longer reaches HTTP response bodies.** All three 404 paths served
  `$"Error: {ex.Message}"` under `Access-Control-Allow-Origin: *`, so page script could fetch and read
  it — routinely a full local filesystem path, and for a deferred-scheme handler potentially a remote
  URL. The body is now a constant and the diagnosis goes to the host log, matching the IPC error
  boundary's rule.
- **A crash-looping page reloaded forever.** The renderer auto-reload was rate-limited but had no
  terminal state, so a page that faults during load reloaded every cooldown for the process lifetime,
  spawning a renderer each time — while the option's own documentation promised that "a crash-looping
  page must not spin". New `MaxAutoReloads` (default 3) is that terminal state; the give-up is logged
  exactly once, and a successful navigation resets the budget so a long-running app is not rationed by
  unrelated crashes hours apart.
- **`@shenora/react`'s robustness tail** (P5.5 H2). A host message of literal `null` — valid JSON —
  survived the parse and then threw a `TypeError` out of the transport listener: an uncaught page error
  with no caller to catch it. `bridge.isAvailable` ignored `disposed`, so a stale reference to a bridge
  that `configureBridge` replaced reported itself available while every `invoke` on it rejected. The
  `fallback` path bypassed the timeout entirely, so an async fallback that never settled hung the caller
  forever. `BaseModuleService` captured the bridge in a constructor default, i.e. at construction — so a
  module-level service singleton (the normal way to write one) built before `configureBridge()` held the
  bridge that call then DISPOSED, and every request from it rejected with "Bridge disposed" for the rest
  of the session; the bridge is now resolved per call, and `this.bridge` still works in subclasses.
  `useDropZone` never registered a target that wasn't mounted on the first effect run — a `RefObject` is
  a stable object and a ref mutation triggers no render, so a conditionally-rendered target was silently
  dead for the component's whole life; the effect now keys on the element itself. `useWindowMaximized`
  fired one un-debounced IPC round-trip per `resize` event (~180 over a 3-second drag, each arming a
  30-second timer) and is now debounced, which is also the correct semantics since the state only
  changes when a resize ends. And `useShenoraQuery` no longer blanks good data when a REFETCH fails —
  one transient hiccup used to turn a recoverable error into an empty screen; both fields are now
  reported so the caller can render stale data with an error banner.
- **The WinForms shell's robustness tail** (P5.5 H2). `WinFormsBootstrap.Initialize` now fails fast on a
  non-STA thread with the fix in the message (a missing `[STAThread]` otherwise surfaced much later as a
  BLOCKING modal dialog inside window creation) and is idempotent (a second call re-registered all three
  exception channels, so every later exception was reported twice and raised two stacked dialogs). Its
  last-resort crash dialog is now one-at-a-time per thread: `MessageBox.Show` pumps, so a recurring
  UI-thread exception re-entered the handler and stacked dialogs unboundedly over a window nobody could
  reach — recurrences still reach the app's logger. `SecondaryWindows` removes its registry entry only
  after `Application.Run` returns (`FormClosed` fires while the form is still disposing its children, so
  a `Dispose` waiting for "no windows left" returned mid-teardown and let the process exit while a
  WebView2 child was still shutting down, leaving its user-data folder locked), removes the entry when
  `thread.Start()` fails (it was otherwise permanently "already open"), and replays an `Activate` that
  arrived before the window's handle existed (previously dropped — and that is the documented "`Open` on
  an existing name activates it" path). `SingleInstanceGuard.TryAcquire` is idempotent: an OS mutex is
  per-thread reentrant, so a second call took a second handle and reported success even when this
  process already owned it, after which `Dispose` could release only one and the mutex stayed held past
  shutdown. `OptimizedForm` re-applies its manual maximize on `WM_DPICHANGED` and display-settings
  changes (a monitor move or scale change left a "maximized" window at the old monitor's size) and
  validates its saved restore rect before using it, so a window whose monitor is gone no longer restores
  somewhere unreachable. `ClipboardService.SetTextAsync("")` clears the clipboard instead of throwing.
- `TrayIcon`'s close-to-tray documentation was factually wrong and is corrected: WinForms reports
  `CloseReason.UserClosing` for a programmatic `Form.Close()` too, so with `CloseToTray` on, an app whose
  startup-abort path calls `Close()` HIDES the window and leaves a resident process with a tray icon and
  a window that can never finish loading. Close from code with `ExitApplication()` or
  `Application.Exit()`. No behaviour changed — the reason code carries no way to tell the two apart.
- **An app callback that threw could take the host down, stall a browser event, or corrupt a tap list**
  (P5.5 H2). Every remaining unguarded app-supplied delegate now runs through `AppCallback`:
  `WebViewHostOptions.OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed` (all three run
  inside WebView2 events, where a throw has no caller and becomes an unhandled UI-thread exception —
  and a failed hook now falls back to the kit's built-in policy, because leaving the event unanswered
  is its own bug: an un-cancelled download proceeds, an unanswered permission request stalls its
  caller, a renderer crash goes unhandled exactly when things are already wrong);
  `OptimizedForm.WndProcHook`, where a throw inside `WndProc` surfaces as WinForms' own BLOCKING modal
  dialog mid-message-dispatch — a throwing hook now reads as "did not handle this message" and the
  window keeps working; `WebViewIpcBridgeOptions.OnClientReady`; and every `Log` sink in
  `Shenora.WebView2`, several of which sat inside a `catch` that exists to stop a failure escaping,
  where a throwing sink defeated the very thing it was reporting from. Log calls are also lazy now, so
  building a message can't throw outside the guard either.
- **`SessionController`'s driver taps were a data race.** The four tap collections were plain
  `List<T>`, appended from the driver's thread (a continuation resumes wherever the pool puts it) while
  the WebView2 event handlers read them on the UI thread. `List<T>.ToArray()` reads the count and then
  copies the backing store, so an `Add` in between throws or copies a torn view, and two concurrent
  `Add`s corrupt the list outright. They are now copy-on-write arrays published under a lock, so
  readers take no lock at all.
- **A wedged page permanently poisoned the render pool** (P5.5 H2, the second half of the
  unobserved-token fix). A page blocked in its own script thread never answers `ExecuteScriptAsync` or
  `GetHtmlAsync`. H4.2 already made the CALLER escape (the marshal observes its token), but that alone
  left the wedged instance going straight back into the pool, so every later lease inherited the
  corpse. Operations are now bounded by `OpTimeout`, an expiry surfaces as `TimeoutException`, and the
  instance is marked poisoned so returning the lease DISCARDS it and the next lease gets a fresh
  browser. A body that ran and merely threw (a rejected URL, a guard refusal) does not poison anything
  — completion is tracked, not inferred from the exception.
- **A returned session that could not be reset was re-pooled forever.** The reset-to-`about:blank`
  swallowed its own timeout and reported success unconditionally, so the documented "a failed reset
  DISCARDS the instance" rule was reachable only if the navigation THREW. An unresponsive renderer was
  therefore recycled indefinitely, each lease burning the full navigation cap before failing. The reset
  now reports its real outcome.
- **A cancelled session start left a live browser behind.** Both `RenderSessionPool` and
  `CoBrowseSession` checked cancellation only BEFORE the multi-second browser init, so a lease
  cancelled — or a pool disposed — during those seconds published nothing to the caller while leaving a
  realized off-screen window and a browser process holding the profile lock, with no owner left to
  dispose either. Both now re-check after init (co-browse also just before publishing) and tear down;
  `LeaseAsync` additionally passes the pool's own dispose token into instance creation.
- **Each retried lease against a locked profile orphaned another browser process.** `InitTimeout`
  abandons the *await* on `CoreWebView2Environment.CreateAsync`, never the creation itself, and every
  instance created its own environment — so a retry queued a second browser process onto the same
  locked profile folder, adding to the very lock the timeout's error message blames. A pool now shares
  ONE environment across its instances and a retry joins the creation already in flight. A failed
  creation is deliberately not cached, so one transient failure is not terminal for the process.
- **A co-browse frame stream could stop silently after a GC.** The CDP screencast receiver was held
  only in a local inside `StartAsync`, so nothing referenced it for the session's lifetime and the
  stream depended on the WebView2 SDK caching it internally. It is now rooted for the session and
  detached in `DisposeAsync`.
- **A late interceptor could read another lease's traffic.** `RenderSession.OnNetwork` and `OnMessage`
  were the only public members with no disposal check, and the only two that install a persistent tap
  — so a subscribe after `DisposeAsync` (a stale reference, a continuation outliving its `await using`)
  attached a live listener to a pooled instance the NEXT lease now owned, streaming its API responses
  and posted messages to the previous caller. Both now throw `ObjectDisposedException`, as every other
  member already did.
- **`AddMessageDispatcher` killed the process for an ordinary composition** (P5.5 H2). It resolved
  module facades INSIDE the `IMessageDispatcher` singleton factory, so any facade whose dependency
  graph reached `IMessageDispatcher` — the documented seam for cross-module `SendAsync` — re-entered
  that factory. Microsoft DI's cycle detection is call-site based and cannot see a factory delegate
  re-entering the provider, and the singleton is not cached yet, so it simply ran again: unbounded
  recursion, `StackOverflowException`, process death with no exception and no log line. Facades are
  now mapped through one terminal middleware that resolves them on the first dispatch, by which point
  the singleton is cached. Two facades claiming the same module name are also rejected instead of the
  second one's whole route table being silently unreachable.
- **`app.Dispose()` threw on a clean shutdown** whenever a singleton implemented only
  `IAsyncDisposable` — which Shenora's own `RenderSession` and `CoBrowseSession` do, so this was
  latent against the kit's own types. `ShenoraApplication` now implements `IAsyncDisposable`; prefer
  `await using var app = builder.Build();`.
- **A relative app root silently re-resolved mid-session.** `ShenoraPaths` returned the resolved root
  and data override verbatim, so a launcher passing `--app-root ..\install` left every derived path
  following the process working directory — and this kit MOVES that directory: the file dialogs set
  `RestoreDirectory = false` on purpose (per-key directory memory is ours), so the first Open/Save
  dialog relocated the CWD and the same `DataDir` string then pointed somewhere else, splitting the
  app's data. It also defeated `SingleInstanceGuard`'s channel hashing. Both paths are now absolute.
- **A throwing app `OnLoading` callback made the login window unclosable** (P5.5 H2). The completion
  block ran the app callback BEFORE `controller.Finish()`, inside an `async void` handler — so a
  throw (an already-disposed splash is the obvious case) meant `Finish()` never ran, and the
  foreground controller HOLDS the user's close until then, so its `FormClosing` handler cancelled
  every close including `Application.Exit`. `Finish()` + `Close()` now come first and the callback is
  guarded.
- **A maximized frameless window lost its state and became unrestorable.** `WindowStateManager` read
  `Form.WindowState`/`RestoreBounds`, but frameless chrome maximizes by hand and keeps
  `WindowState.Normal` — so closing while maximized persisted `Maximized: false` plus the WORK-AREA
  rect as the normal size. On the next launch the window filled the work area believing it was not
  maximized: the border gap the technique exists to remove came back, the chrome glyph was wrong, and
  clicking maximize captured the work-area rect as the restore bounds, making restore a PERMANENT
  no-op. New `IAppMaximizable` seam (implemented by `OptimizedForm`) is now preferred over the
  WinForms properties, and a saved maximized state is restored through the window's own mechanism.
  Live in the reference composition.
- `WindowStateManager.Apply` no longer overwrites a `MinimumSize` the form set for itself — the
  reference composition's own 640×420 minimum was dead code.
- **Arbitrary file read through file-mode frontend serving.** The resource provider applied no path
  containment, and the host unescapes the request path before calling it (it must, so bundle
  filenames with spaces or CJK characters resolve) — so `%2e%2e%2f…` arrived as `../` and walked out
  of the bundle, and a ROOTED path (`/C:%2f…`) escaped even more simply because `Path.Combine`
  discards its first argument when the second is rooted. Responses carry
  `Access-Control-Allow-Origin: *`, so page script could read what came back. Live wherever
  `PreferFiles` is on — which the sample derives from `IsDevelopment`. Both `GetResourceStream` and
  `Exists` now reject rooted and traversing paths and assert the resolved path stays under the root.
- **`NavigationGuard` was bypassed by redirects.** It was consulted only on the explicit
  `NavigateAsync` call, so a guard-approved URL answering `302 → http://127.0.0.1:8080/admin` was
  followed and its DOM handed to the caller. The pool now cancels unvetted cross-host navigation at
  `NavigationStarting`. Note the scope honestly: that event has no deferral in the WebView2 SDK, so
  an async guard cannot be awaited inside it — a synchronous cross-host rule is the most the event
  can enforce, and `SessionBrowserOptions.RequestFilter` (synchronous, `WebResourceContext.All`)
  remains the seam for full redirect/subresource policy. Documented on both options.
- **An unserializable notification payload crashed the UI thread and lost its whole batch.** The
  notification flush drained the queue and then serialized with no try/catch, on a 50 ms WinForms
  timer — so one app event carrying a cyclic object graph, a `Type`/delegate member or a throwing
  getter took down the UI thread (a modal crash dialog under the family bootstrap) and discarded the
  drained batch. Payloads are now serialized per notification so only the offender is dropped, with
  a catch-all around the flush. The incoming path had always been guarded; this asymmetry was the bug.
- **`LoginWindow.ClearProfile` is a recursive delete and accepted a traversing path.** Profile paths
  are normally composed from data-driven identifiers, so a stray `..` segment could aim the delete
  outside the sessions root — while the same options documented that scoping as a security boundary.
  It now refuses traversal segments; use `ComposeProfileDirectory` to build the path safely.
- A `Process` handle leaked on every external link click from the page: the WebView2 host's
  open-in-system-browser path did not dispose the started process, though the sibling implementation
  in `ShellLauncher.OpenUrl` already carried that Win11 fix.

- **`@shenora/react` was not importable under native Node ESM** (`0776f37`). The emitted relative
  imports carried no `.js` extension, which bundler resolution silently tolerated and plain Node
  rejected — so the published tarball would have failed for any consumer not behind a bundler. All
  relative specifiers now carry explicit extensions and `module`/`moduleResolution` are `NodeNext`,
  which makes a missing extension a build error rather than a publish-time surprise. Caught by the
  P1.1 local-feed consumption smoke; root cause in `docs/FIX-LOG.md`.

Bootstrap: repo, docs system, design contract, buildable package skeleton
(`Shenora.Core` / `Shenora.Ipc` / `Shenora.WebView2` / `Shenora.WinForms` / `@shenora/react`),
devtools loop (`build` / `test` / `verify` / `pack` / `doctor` + desktop verification tools),
manual OIDC release workflow. `@shenora/react` exposes only `isShenoraAvailable()`.

First extracted surface (P2 increments 1–5, gated by API-surface baseline tests):
`Shenora.Core` `ShenoraEnvironment` + `AppRootArgument` + `ShenoraPaths(+Options)` + the
application builder (`ShenoraApplication(+Options)`/`ShenoraApplicationBuilder`/`IShenoraModule`/
`IShenoraRunner`/`IShenoraLifecycleHook`);
`Shenora.WinForms` `DpiHelper` + window-state stack (`WindowState`/`WindowStateOptions`/
`IWindowStateStore`/`JsonFileWindowStateStore`/`WindowStateManager`) + `SingleInstanceGuard`
(incl. `TryAcquire(TimeSpan)` — the `--restarted` widened-wait relaunch handoff) +
`WinFormsBootstrap(+Options)`/`UnhandledExceptionReport` + the host composition
(`UseWinForms`, `WinFormsHostOptions`/`SingleInstanceHostOptions`/`WindowStateHostOptions`) +
`SplashPanel(+Options)`;
`Shenora.WebView2` `BrowserArguments` + `WebViewEnvironment(+Options)` (runtime probe, prewarm,
per-thread creation) + `PrewarmWebView2` builder extension + `WebViewHost(+Options)` (init
timeout guard, settings hardening, dev/prod navigation, new-window/download/permission/
process-failure policies, escaped `InjectedGlobals`, sync virtual-host + deferred app-scheme
serving, `WebViewFolderMapping`) + `IWebViewResourceProvider`/`EmbeddedResourceProvider(+Options)`
(lazy-with-warmup, file-fallback mode) + `WebViewDeferredScheme`.
Dependency note: `Shenora.Core` now depends on `Microsoft.Extensions.DependencyInjection`
(the implementation — the builder needs `BuildServiceProvider`), not only the abstractions (D17).

`Shenora.Ipc` first surface (P3.1 — the transport-neutral wire contract, design contract §5 +
D11/D16): `IpcRequest`/`IpcResponse`/`IpcError`/`IpcNotification`/`IpcNotificationBatch`
envelopes (names pinned with `JsonPropertyName`; optional app-defined `scope` field),
`IpcCategories` (lowercase `ipc`/`notification` discriminators), `OperationException`
(code + parameters, i18n-ready, `ToError()`), `IpcErrorCodes` (framework-reserved codes),
`PayloadHelper` (structured missing/invalid errors; JSON null == absent), and `IpcJson`
(frozen camelCase/camelCase-enums/null-omitting wire serializer defaults). Replaces the
assembly marker.

P3.2 — the dispatch pipeline and the in-process event bus. `Shenora.Ipc`:
`IMessageDispatcher`/`MessageDispatcher` (composable middleware pipeline —
`Use`/`UseModule`/`UseRoute`/`UseLogging`/`UseErrorHandler`/`MapRoute`/`MapModule`, incl.
facade-object mapping — plus `DispatchAsync` for transports, never throws/never null, and
programmatic `SendAsync`/`SendAsync<T>` sharing the same pipeline; failed typed sends rethrow
the structured `OperationException`; unknown exceptions cross the bridge as `UNKNOWN_ERROR`
only — details stay in the host log), `MessageMiddleware`, `ModuleRouteBuilder`,
`IModuleFacade`/`BaseFacade` (standardized error boundary), `IpcErrorCodes.NoHandler`.
`Shenora.Core`: `EventMessage`/`IEventBus`/`EventBus` (wildcard patterns + per-subscription
match cache; scoped subscribers also receive global events; handler failures isolated) —
auto-registered by `ShenoraApplicationBuilder.Build()` (`TryAdd`, replaceable).

P3.3 — `Shenora.WebView2` gains `WebViewIpcBridge(+Options)`: the postMessage transport —
incoming requests parsed and dispatched on the UI thread via async interleaving (never
`Task.Run`-per-message), responses/notifications posted with `IsHandleCreated`-guarded
non-blocking `BeginInvoke`, host→page pushes batched every ~50 ms through a bounded drop-oldest
queue (buffering starts at construction; delivery starts at the client's `SHENORA`/`READY`
handshake, which also fires `OnClientReady` per occurrence), optional `IEventBus`
wildcard-forwarding, `SendNotification` for direct pushes.

P4.1 — `Shenora.Ipc` gains the scoped-container router and the standard IPC composition:
`ScopedContainerRouter(+Options)` (per-scope child service containers, single-flight creation,
`MapModule<TFacade>` routing declarations, `GetScopeServices`/`InvalidateScope`/`ActiveScopes`,
structured `SCOPE_REQUIRED` for scoped modules called without a scope — `IpcErrorCodes.ScopeRequired`)
with `UseScopedRouter`, plus `AddModuleFacade<TFacade>`/`MapRegisteredModules`/
`AddMessageDispatcher` (the §5 pipeline order encoded: error handler → app middleware →
DI-registered facades).

P4.2 — the window manager: `Shenora.WinForms` `OptimizedForm(+Options)` (double-buffered base +
`WndProcHook` seam; optional frameless custom chrome — WM_NCCALCSIZE top-only caption removal,
manual work-area maximize with `IsAppMaximized`/`MaximizedChanged`, DWM dark border/rounded
corners, `ApplyChromeTheme` runtime resync — all colors parameterized); `Shenora.WebView2`
`WindowCommandFacade(+Options)` (module `WINDOW`: MINIMIZE/TOGGLE_MAXIMIZE/CLOSE/IS_MAXIMIZED/
START_DRAG/START_RESIZE + optional SET_THEME; delegate seams for the frameless paths);
`@shenora/react` `WindowCommands` service + `useWindowMaximized` hook.

P4.3 — the native desktop services in `Shenora.WinForms`, all TryAdd-registered by
`UseWinForms`: `IFormInteraction`/`FormInteraction` (main-window registry — the runner sets it —
plus nested modal blocking; handle read answers `Zero` before creation instead of creating it on
the wrong thread), `IFileDialogs`/`FileDialogs(+Options)` + `FileDialogOptions`/`Filter`/`Result`
+ the `IFileDialogPathStore` memory seam (STA-thread open/folder/save dialogs, owner-handle
z-order, per-key last-directory memory; failures throw instead of the source's wire-bound error
strings), `IShellLauncher`/`ShellLauncher` (reveal-in-Explorer, open directory, http/https-only
`OpenUrl`, `LaunchProcess` — the Windows 11 handle-leak/orphan-process fixes kept),
`IClipboardService`/`ClipboardService` (STA-marshalled text + image-file operations).

P4.4 — the drag-drop zone stack: `Shenora.WebView2` `DropZoneManager(+Options)` +
`DropZoneFacade` (module `DROP_ZONE`: transparent overlays synced to page elements capture real
OS file paths — including background drags; non-blocking UI marshalling, form-activation sync,
DOM occlusion checks; per-monitor `DeviceDpi` CSS→physical conversion + `DpiChanged` re-apply
from stored CSS rects — the P2.3b DPI tail; events emitted on `IEventBus`, forwarded by the
bridge); `@shenora/react` `useDropZone` (bounds auto-sync via observers, drag CSS feedback —
unstyled/headless, real-path `onDrop`, in-flight-REGISTER and fast-unmount teardown guards).

P4.5 — `Shenora.WinForms` gains `SecondaryWindows` + `SecondaryWindowOptions` (named windows,
each on its own STA thread with its own pump; geometry persistence reuses the window-state
stack per name via `IWindowStateStore`; open-on-existing activates; non-blocking close
discipline) and `TrayIcon(+Options)`/`TrayMenuColors` (NotifyIcon + Open/app-items/Exit menu,
double-click restore, close-to-tray, optional app-colored menu renderer — colors are the app's,
headless).

P3.4 — `@shenora/react` becomes the real client: wire-contract types mirroring `Shenora.Ipc`
(`IpcRequest`/`IpcResponse`/`IpcError`/`IpcNotification`/`IpcNotificationBatch`/`EventMessage`
+ `IpcCategories`/`IpcErrorCodes`/handshake constants), `OperationError` (structured
code + parameters, incl. client-side `TIMEOUT`/`NO_TRANSPORT`), the `ShenoraTransport` seam +
`createWebView2Transport` (transport-pluggable, D16), `ShenoraBridge` (correlated `invoke` with
per-call timeout, category routing, batch unbundling into the event bus, `notifyReady`
handshake, `fallback` seam for pure-UI browser dev) with lazy `getBridge`/`configureBridge`,
`ShenoraEventBus` + `eventBus`, `BaseModuleService<TRequests>` (typed per-module services),
hooks `useShenora`/`useShenoraEvent` (latest-ref, no resubscribe churn)/`useShenoraQuery`
(minimal fetch state, headless per D13), and `installDevInterceptor` (`window.__shenora` ring
buffers for CDP-driven testing). `react` is now a REQUIRED peer (hooks import it);
`isShenoraAvailable()` unchanged.

P5.1/P5.2 — new package `Shenora.WebView2.Sessions` (D14): auxiliary browser sessions — browser
work OUTSIDE the app's own UI, over the same WebView2 runtime. `SessionBrowser(+Options)` (the
ONE configuration path for auxiliary WebView2s: per-profile environment, quiet-start +
background-throttling-off arguments, settings hardening, `RequestFilter` request-blocking seam,
init-timeout guard, `GetHtmlAsync`) and the render pool — `RenderSessionPool(+Options)`/
`RenderSession`/`SessionApiCall` (bounded LIFO pool of off-screen sessions leased for
navigation/scripting/HTML-read/DevTools/network+message taps; capacity waits queue, a creation
failure releases the slot, a failed reset discards the instance instead of re-pooling it;
`NavigationGuard` SSRF policy seam; one shared hidden host in runtime mode or visible
per-session dev windows). The login stack — `LoginWindow(+Options)`/`LoginWindowController`/
`LoginResult`/`LoginErrorCodes`/`LoginCookie`/`DownloadHit`: interactive logins over
per-provider (and per-sub-account — a security boundary) persistent profiles, driven by a
caller-supplied driver over controller primitives (guarded navigate, script, origin-scoped
cookie read, message/download/new-window/navigation taps, `FitToBox` CSS→physical sizing,
`SetLoading`, idempotent `Reveal`); one login at a time with exactly-once completion, the
user's close HELD for a final cookie read, an optional silent-refresh shape (created
off-screen, revealed only if interaction is needed), and `ClearProfile` for real logout.
`CookieLoginFlow(+Options)` is the built-in driver: navigate then poll for a FRESHLY-SET auth
cookie (pattern-matched, judged against a pre-navigation baseline — a stale cookie never
captures, not even on close), cookies read from the separate `CookieReadUrl` origin, blob
round-trip via `ReadBlob`.

P5.3 — `Shenora.WebView2.Sessions` gains `CoBrowseSession(+Options)`/`CoBrowseViewport`:
co-browse an off-screen page in-app (countdowns/captchas stay human-solved, no native window) —
CDP `Page.startScreencast` JPEG frames flow into a bounded latest-wins `ChannelReader<byte[]>`
(`Frames`: a slow client drops the oldest frame, never backs up the compositor), the client's
input JSON is dispatched back via `DispatchInputAsync` (viewport messages mirror the client's
content box 1:1 through device metrics ALONE — never a physical resize; fraction-coordinate
mouse/wheel; `insertText` typing; special keys/shortcuts synthesized with the modifier bitmask +
Windows virtual-key map), `ReadHotspotsAsync` returns clickable-element rects as viewport
fractions (client-side hover/pressed affordances over pixels), and `Controller` exposes the
SAME `LoginWindowController` primitives over the streamed page. The wire protocol is identical
to the proven source for mechanical adoption; the transport (WebSocket, bridge, …) stays the
app's — frames out, input text back.
- **The npm tarball could have shipped test-support code** (P5.5 H7). `tsconfig.build.json` excluded
  only `src/**/*.test.ts(x)`, so the new shared `src/testing/fakeTransport.ts` — a non-test helper
  sitting beside the sources — compiled straight into `dist/`, which `files: ["dist"]` publishes
  wholesale. Caught while adding it, and confirmed by building without the exclusion: `dist/testing/`
  really was emitted. Fixed by excluding `src/testing/**`, and `dev.mjs doctor` now FAILS when
  `dist/testing/` exists so the exclusion cannot be dropped silently while editing an unrelated pattern.
- **The reference sample no longer swallows a failed ready handshake** (P5.5 H7). It called
  `void getBridge().notifyReady()`, so a rejection (no host, disposed bridge, timeout) became an
  unhandled promise rejection — a silent console error in a WebView2 page. It now catches and logs.
  Worth listing even though the sample is not shipped: it is the reference composition, and this is the
  snippet adopters copy. The sample also gained the CSS rule behind its `dropClassName`, which it had
  been passing with nothing to style it — so the e2e subject can finally demonstrate the drop zone's
  HOVER feedback and not only the drop.
- **`@shenora/react`'s shipped types no longer require `@types/react` to be in your global program.**
  `UseDropZoneOptions.targetRef` was declared as `React.RefObject<HTMLElement | null>` — the UMD global
  `React` — while the source imported only the three hooks it used. The emitted
  `dist/useDropZone.d.ts` therefore NAMED `React` with no import, so it resolved only when the
  consumer's program happened to pull `@types/react` in globally. A consumer with `"types": ["node"]`
  in their tsconfig — entirely reasonable, and the default for a non-React entry point — got
  **TS2503 "Cannot find namespace 'React'" out of a declaration file they cannot edit**. Fixed by
  importing `type RefObject`; the type is identical, so nothing source-breaking.
  Found by P6.4's client-adapter probe. P6.1's npm consumer missed it because its own tsconfig
  imports React in a `.tsx`, which loads the global — a consumer probe only ever tests the
  configuration it happens to have, which is the transferable lesson here rather than the one-liner.
