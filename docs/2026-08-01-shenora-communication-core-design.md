# The communication core: event-first module contract, tracked operations, base-agnostic channel — design (2026-08-01)

Status: **IMPLEMENTED (2026-08-01), shipped as 0.2.0** with a `### Breaking` section — the first
deliberate break since v0.1.0. **AMENDED again (generic-library audit, 2026-08-01, before publish —
free, since 0.2.0 was merged but never pushed/published):** the audit asked not "is this correct" but
"has the kit absorbed ONE application's shape", and found the removal/asking halves of the lifecycle
had — the harvested app's own host never had to solve either. Fixed: `ClearFinished` gained the
`module?`/`scope?` filter §4.6's table already promised for `LIST`; `Resumable` (§4.2) was removed as
a tautological flag; `RequestPause` was added as `RequestResume`'s missing mirror and `Find(id)` was
reinstated (§4.2's "NOT shipped" note below is superseded); `OperationEvents.Removed` closes the gap
§4.3 left (a removal published nothing). Full list: `CHANGELOG.md`'s 0.2.0 entry.

**AMENDED again 2026-08-01 (owner direction, before publish — "I don't even think we need any
specific status than regular — think about this is going to be structured like XHR"): `Paused` and
`Interrupted` collapse into ONE status, `Waiting`.** XHR keeps a tiny closed lifecycle and puts the
semantics in fields, not extra states; it has no "paused" because it does not own pausing. The code
already agreed: `Dismiss` and `RequestResume` both accepted either status, neither was ever pruned,
and the client's `waiting` getter already unioned them — they diverged in exactly one place
(`RequestResume` dropping `Interrupted`, keeping `Paused`), and that difference was always about
whether the entry had a live handle, which `ResumePayload` already told the registry on its own.
Renamed throughout, mechanism not scenario (D22): `OperationStatus.Waiting` (one value);
`OperationInfo.WaitReason`; `IOperation.Wait(reason?, detail?)`; `IOperationRegistry.RegisterWaiting`;
`IOperationRegistry.RequestWait`; `OperationEvents.WaitRequested`
(`OPERATION_WAIT_REQUESTED`); the `WAIT` facade route; client `OperationStatuses.Waiting` with the
`paused`/`interrupted` half-getters deleted (`waiting` is now the whole band). `RequestResume` keyed its
drop-vs-keep decision on `ResumePayload` rather than on status at this point — full rationale, the
closed "registered but not started" limit, and every rename: **D23's amendment**, `docs/DECISIONS.md`.
**Superseded again (2026-08-01, before push/publish — see §5A.4's own amendment stack below): keying on
`ResumePayload` was itself a residual hole, since that field is app-controlled, not kit-owned —
`RequestResume` now keys on the registry's internal provenance record instead.** Full
list: `CHANGELOG.md`'s 0.2.0 entry. This doc is AMENDED IN PLACE below (§4.2, §4.3, §5A, §6) rather
than left to describe a two-status band that no longer exists — sections not touched by either audit
still describe the as-shipped shape accurately.

The as-built shape
is recorded in `docs/ARCHITECTURE.md`'s `Shenora.Ipc`/`@shenora/react` inventory; this doc stays for
the rationale, AMENDED in place below rather than left to contradict the current shape. What shipped
task by task, the review findings fixed along the way, and the (now superseded) `Find(id)` known
limit: `docs/task-archive.md` `### 0.2.0`. The task-by-task plan this design was implemented from has
been removed per its own "delete once the work lands" lifecycle (`docs/README.md`'s doc inventory) —
its content is superseded by the archive entry above.

Read first: `.claude/knowledge/ipc-contracts.md` (this design is constrained by it in several places
and contradicts it in none) and `docs/2026-07-31-shenora-oneway-ipc-design.md` (this **supersedes its §6
bullet 1** — see §7). Sibling specifics stay in `local/PROJECT_NOTES.md`; nothing here names a private
project.

## 1. Why this exists

The first adopter reviewed the shipped IPC against its own stated intent — *"not a sync request
pattern, which does not fit desktop or mobile: the backend layer here is mostly attached to its
frontend layer, so a stateful design with an event hub is the way to go — async from the UI, progress
synced"* — and the verdict was **the client design already matches; the HOST contract does not**
(`TASKS.md`, 2026-08-01). Three verified facts, not impressions:

- **`Shenora.Ipc` has zero references to `IEventBus`.** `IModuleFacade.HandleMessageAsync` "always
  produces a response" and `BaseFacade` hands a module an `ILogger` plus `RouteMessageAsync →
  Task<object?>`. The request half is first-class and typed; the event half — the half the design
  rests on — is a side dependency every app wires by hand. The tell that this is real rather than
  theoretical: the kit's own `DropZoneManager` takes `IEventBus` as a REQUIRED option, because the bus
  is the actual spine.
- **The layering never justified it.** `src/Shenora.Ipc/Shenora.Ipc.csproj` already carries
  `<ProjectReference Include="..\Shenora.Core\Shenora.Core.csproj" />`, so `IEventBus` reaches the
  module contract with **zero new package edges**. The gap was contract, not architecture.
- **The kit's own sample proves the ceremony cost.** `samples/Shenora.Sample.Desktop/SampleFacade.cs`
  streams progress with `Task.Run` + a catch-or-it-is-an-unobserved-fault + `ConfigureAwait(false)` +
  a hardcoded `"SAMPLE"` literal that can drift from `ModuleName`. Twenty lines, three ways to get it
  quietly wrong, re-derived per app. The adopter's hand-rolled contract is
  `HandleAsync(action, payload, emit, ct)` — `emit` is IN the signature, so every module streams
  progress by construction. That is the one place the thing being replaced is closer to the stated
  intent than the kit.

And "long-running" is not an edge case on desktop, it is the normal case. "Always produces a response"
leaves it undefined: return immediately and stream (then what is the response?) or hold the request
open for ten minutes (then the response is meaningless and the caller's timeout is wrong)? Both
readings are available today, so every adopter invents a slightly different third one.

Finally, the future the kit already promised (D16: mobile shells via transport-pluggable IPC) is
half-built. The **client** half is genuinely base-agnostic — `ShenoraTransport` in
`src/Shenora.React/src/transport.ts`. The **host** half is not: `WebViewIpcBridge` owns the queue, the
cap, the batch, the ready gate, the `ContentLoading` reset and the guarded serialize, on a
`System.Windows.Forms.Timer`. A Capacitor or WebSocket base re-implements all of it — including four
bugs this repo has already paid for.

## 2. Decisions taken (user, 2026-08-01)

1. **Scope: publish + an operation PRIMITIVE, with progress types.** The kit owns id, background
   handoff, the guarded body, terminal transitions and cancel-by-id — *and* the tracked-progress model,
   harvested from a private sibling's 320-line `ProcessRegistry` (survey in `local/`).
2. **Cross-base: extract the outbound pipeline into `Shenora.Ipc`, plus per-channel event filtering.**
3. **Contract: the context object goes in the route signature.** Breaking; pre-1.0, accepted.
4. **The batch interval stays configurable and is documented as a frame rate** — "this is more like a
   frames-per-second logic", default 50 ms. It already is an option
   (`WebViewIpcBridgeOptions.NotificationInterval`); this design preserves it through the extraction and
   gives the new progress throttle the same treatment (§4.4).
5. **Perfection is not the day-1 bar** (user). Where a choice is defensible and reversible, this design
   takes the simpler one and says so.

## 3. §A — `IModuleContext`: the event path enters the signature

```csharp
namespace Shenora.Ipc;

/// The world a route runs in: who it is, where it logs, how it emits, how it starts long work.
public interface IModuleContext
{
    /// The owning module — the same string as IModuleFacade.ModuleName, supplied by the kit so an
    /// emit can never drift from the module it claims to come from.
    string Module { get; }

    ILogger Logger { get; }

    /// Emit on the host bus under THIS module. The default gesture, not a wiring exercise.
    void Publish(string type, object? payload = null, string? scope = null);

    /// Start a tracked operation and get its handle — for shapes that do their own lifecycle.
    IOperation Start(OperationOptions options);

    /// Start + hand off + guarded body + terminal transition. Returns the operation id immediately.
    string Run(OperationOptions options, Func<IOperation, CancellationToken, Task> work);
}

// BaseFacade
protected abstract Task<object?> RouteMessageAsync(
    IpcRequest request, IModuleContext context, CancellationToken cancellationToken);
```

`IpcRequest` and the token stay explicit — both are pinned by existing docs and tests, and folding them
into the context would be churn for no gain.

**This is the module's context, not an operations entry point** (user direction, 2026-08-01). The
ordering matters: **custom events are the primary channel and are always available.** `Publish` takes an
app-defined type and an app-defined payload, needs no registry, no operation and no opt-in registration,
and a module is expected to define its own event vocabulary with it — that is the event hub the whole
design rests on. Operations are one *opt-in* thing the same context offers, for the subset of work that
is long enough to need a uniform progress spine. A module can use `Publish` and never touch `Start`/`Run`;
`AddShenoraOperations` is what decides whether the other half exists at all. Correlating a custom event
with an operation needs no new API — the id goes in the app's own payload
(`ctx.Publish("ITEM_IMPORTED", new { operationId = op.Id, item })`), which is the correlation convention
`docs/2026-07-31-shenora-oneway-ipc-design.md` §4 already fixed: never in `module`, `type` or `scope`,
because `EventBus`'s match cache is keyed on those and an operation id is a per-entity value.

**Construction.** `BaseFacade`'s constructor gains two optional parameters:
`protected BaseFacade(ILogger? logger = null, IEventBus? events = null, IOperationRegistry? operations = null)`.
Existing `base(logger)` calls still compile. The context is built once per facade (the module name is
known at construction) and handed to every route.

**Absent dependencies fail LOUD, at the call site, naming the fix.** `Publish` without a bus throws
`InvalidOperationException` ("pass an `IEventBus` to `BaseFacade` — `ShenoraApplication.CreateBuilder`
registers one by default"); `Start`/`Run` without a registry throws naming
`services.AddShenoraOperations()`. A silent no-op here would be the exact class this repo keeps
fixing — a mistyped resource prefix degrading to an all-404 provider, a doctor check satisfied by the
prose explaining it. Facades that never publish are unaffected, so a bus-less unit test still works.

## 4. §B — Operations: the harvest, mechanism only

### 4.1 Evidence, and what is deliberately left behind

Two apps independently built this, which is the generalization bar (`generic-library`): one has a
`ProcessRegistry` (Start/Report/Complete/Fail/Cancel/GetToken/GetAll/ClearCompleted, feeding a status
bar and a download-manager-style activity panel); the other has the `JOB_UPDATED`/`JOB_PROGRESS`/
`JOBS_CHANGED` stream behind the `useJobsSync` archetype already cited in the one-way design §5.1.

| Lifted (mechanism) | Left with the app (product) |
|---|---|
| id, owning module, `Kind` (app-defined **string**), `Scope`, status, `Progress` (`OperationProgress?` — `Value`/`Total?`/`Unit?`, the app's own unit, null = indeterminate — see the progress amendment below), timestamps | a `ProcessType` **enum** — 15 domain values; the kit ships none |
| idempotent finish, bounded history, cancellable→CTS map, token lookup | what an operation *means*, its phases, whether it queues |
| **throttled progress emission (≤1 per window + a trailing emit)** | i18n *rendering* — the kit carries key + parameters, the app renders |
| `Run(...)` = the exact fire-and-forget shape their `RunTrackedAsync` proved | the activity-panel/status-bar **UI** (D13: headless) |
| labels as `{Text?, Key?, Parameters}` — the same i18n-ready shape as `IpcError` | profile semantics → the kit's existing app-defined `Scope` |

Their post-mortem also fixes this design's shape: several of their long-op services were assessed as
"NOT clean fits — leave them hand-rolled" (a `Start` outside the background block, two blocks, multiple
`Fail`s, resumable sessions). So the primitive must be **composable** — `Start` returning a handle is
the real API, and `Run` is the convenience over it. An all-in-one wrapper alone would be adoptable by
about half the real call sites.

### 4.2 The types (`Shenora.Ipc`)

**Sketch below is historical (pre-lifecycle-completion, pre-collapse) — kept as originally written,
like the `int? Progress`/`Resumable` sketch a few lines down that the prose after it also corrects.
The CURRENT shape (§5A.2's completed lifecycle, later collapsed to one `Waiting` status by the
amendment at the top of this doc) has `OperationStatus { Running, Completed, Failed, Cancelled,
Waiting }`, `IOperationRegistry.RegisterWaiting`/`RequestWait`, and no `Interrupted` value at all —
see `docs/ARCHITECTURE.md` for the as-built surface.**

```csharp
public enum OperationStatus { Running, Completed, Failed, Cancelled, Interrupted }

/// Human-facing text that the HOST must not render: an English fallback plus an app i18n key and
/// parameters. Same contract as IpcError's {code, parameters} — the kit never formats UI strings.
public sealed record OperationLabel(
    string? Text = null, string? Key = null, IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record OperationOptions
{
    public required string Kind { get; init; }        // app-defined: "IMPORT", "DEPLOY", "SCAN"
    public OperationLabel? Title { get; init; }
    public string? Scope { get; init; }               // app-defined; drives event scope + filtering
    public bool Cancellable { get; init; }
    public int? Progress { get; init; }               // initial; null = indeterminate
    public bool Resumable { get; init; }
    public string? ResumePayload { get; init; }       // opaque app checkpoint token
}

public sealed record OperationInfo   // the event payload AND the LIST response element
{
    public required string Id { get; init; }
    public required string Module { get; init; }
    public required string Kind { get; init; }
    public string? Scope { get; init; }
    public OperationStatus Status { get; init; }
    public int? Progress { get; init; }
    public OperationLabel? Title { get; init; }
    public OperationLabel? Detail { get; init; }
    public IpcError? Error { get; init; }             // structured; never raw exception text (§4.5)
    public bool Cancellable { get; init; }
    public bool Resumable { get; init; }
    public string? ResumePayload { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

public interface IOperation
{
    string Id { get; }
    /// The operation's OWN token — never the request's. See §4.5.
    CancellationToken CancellationToken { get; }
    void Report(int? progress = null, OperationLabel? detail = null);
    void Complete();
    void Fail(string code, IReadOnlyDictionary<string, string>? parameters = null, string? message = null);
    void Fail(OperationException error);
    void Cancel();
}

public interface IOperationRegistry
{
    IOperation Start(string module, OperationOptions options);
    string Run(string module, OperationOptions options, Func<IOperation, CancellationToken, Task> work);
    IOperation? Find(string id);   // sketched here, NOT shipped — see the note below the interface
    IReadOnlyList<OperationInfo> GetAll(string? module = null, string? scope = null);
    bool Cancel(string id);
    void ClearFinished();
    /// Announce a crash-interrupted resumable operation from the APP's own checkpoint. Deduped by
    /// (module, kind, resumePayload) so re-announcing does not stack entries.
    string RegisterInterrupted(string module, OperationOptions options);
    /// Emit OPERATION_RESUME_REQUESTED for the owning module and drop the interrupted entry.
    bool RequestResume(string id);
}
```

`Module` is supplied by the context, never by the app — the same anti-drift reason as `Publish`.
`OperationStatus` crosses the wire as `"running"` etc. for free: `IpcJson` already installs
`JsonStringEnumConverter(CamelCase)`, so the sibling's hard-won enum-serialization trap does not exist
here.

**`Find(id)` was sketched above, dropped pre-0.2.0, then REINSTATED (generic-library audit, 2026-08-01,
before publish) — the sketch above is once again the shipped shape.** The original ruling was "no
consumer resolves a handle from a bare id"; that stopped being true the moment `RequestWait` (then
named `RequestPause`) shipped alongside `RequestResume` (§5A.3's amendment below): both are
client-request routes carrying only an id, and whoever handles them (hearing
`OPERATION_WAIT_REQUESTED`/`OPERATION_RESUME_REQUESTED`) must translate that id back into a handle to
call `Wait`/`Resume` — a recurring shape every such consumer would otherwise re-solve with its own
id→handle map, which is this repo's own stated bar for public surface. Safe to hold past the
operation's life: every `IOperation` member re-validates the entry's current status before acting, so
a stale handle is a no-op, not a dangling reference to guard.

**Provenance note, so a future reviewer can cut it cleanly:** what the sketch above calls
`Interrupted` (later folded into `OperationStatus.Waiting`, see the amendment at the top of this doc)
/ `ResumePayload` / `RegisterWaiting` (sketched as `RegisterInterrupted`) / `RequestResume` come from
**one** app, not two. They are included because they are pure mechanism (a state, an opaque token, an
event — the app owns the checkpoint and the resume entrypoint). Everything else in §4 clears the
two-app bar. **`Resumable` (shown on `OperationOptions`/`OperationInfo` above) was REMOVED by the
generic-library audit** — it was consulted nowhere except `RegisterWaiting`'s own required-true gate,
and every caller had already forced it `true` to pass that gate, so it carried no information
`RegisterWaiting`'s existing non-empty-`ResumePayload` requirement didn't already express. It was this
section's own first-named candidate for removal ("if a 1.0 audit wants surface removed, this is the
first candidate") — the audit took it pre-1.0 instead, since 0.2.0 was still free to change.

**Progress stopped being an assumed percent (owner direction, before publish — "even its progress it
might be different than 0-100%").** The sketch above (`int? Progress` on both types, `Report(int?
progress, …)`) — and this section's own FIRST audit pass — both assumed 0–100 percent; that pass even
amended the write-side XML doc to SAY so (CHANGELOG 0.2.0 finding 5), which was the wrong fix to the
right observation. Percent is not the mechanism, it is one way an app happens to measure: a consumer
reports bytes transferred against a known total, items processed against a known total, an absolute
count with no known denominator (bytes off a chunked stream), or a genuine percent, and forcing percent
makes it pre-compute a ratio and discard the numbers its own UI wants to render. `OperationOptions.Progress`/
`OperationInfo.Progress` and `IOperation.Report`'s `progress` parameter are now a new record,
`OperationProgress(double Value, double? Total = null, string? Unit = null)` (TS mirror: `{ value:
number; total?: number; unit?: string }`) — `Total = null` means no known denominator, never zero, and
`Unit` is app-defined and uninterpreted, exactly like `Kind`. `ClampProgress` (`Math.Clamp(value, 0,
100)`) is REMOVED with nothing put in its place: silently rewriting an app's own reported number is
worse than passing it through untouched, so a `Value` above its own `Total` is the app's bug to see, not
the kit's to hide — and no validation throw was added either, since `Report` runs on a hot path from
background work and throwing there would kill an operation over a cosmetic number.
`IOperation.Complete()` no longer forces `Progress = 100`: it sets `Value = Total` only when the last
report carried a known `Total` (the honest "all of it"), otherwise it leaves the last reported value
untouched — never inventing a figure the app never gave it. `@shenora/react` ships no percent helper;
the README documents the one-liner (`total ? (value / total) * 100 : undefined`) because that division
is the consumer's own policy. `OperationProgress` is a new wire shape both sides name, so it gets its
own mirror tripwire (`WireMirrorTests.OperationProgress_fields_match_the_host`) rather than trusting the
two sides to stay in step by inspection.

**Two more audit fixes to this section's sketch, both additive:** `void ClearFinished();` above is now
`ClearFinished(string? module = null, string? scope = null)`, mirroring `GetAll` exactly — it shipped
with NO filter at all, so "clear completed" in one scoped window could wipe another scope's finished
history. And `IOperationRegistry` gained `bool RequestWait(string id)` (sketched at the time as
`RequestPause`), an exact mirror of `RequestResume` for the direction §5A.3 originally shipped with no
client route at all — see that section's own amendment.

### 4.3 The event contract

The registry publishes on the bus under a kit module (`OperationRegistryOptions.ModuleName`, default
`"OPERATIONS"`):

- `OPERATION_UPDATED` — payload is the full `OperationInfo`, for **every** transition: start, progress,
  terminal. Event `Scope` = the operation's scope.
- `OPERATION_RESUME_REQUESTED` — payload `{ operationId, module, kind, resumePayload, scope, status }`.
- `OPERATION_WAIT_REQUESTED` (generic-library audit, before publish; sketched/shipped at the time as
  `OPERATION_PAUSE_REQUESTED`, renamed by the status collapse — see the amendment at the top of this
  doc) — payload `{ operationId, module, kind, scope }`. Exact mirror of `ResumeRequested`'s ASK/ACT
  split: emitted by `RequestWait`, changes nothing itself, and the owning module's own
  `IOperation.Wait` is what actually stops the work and publishes the resulting `OPERATION_UPDATED`.
- `OPERATION_REMOVED` (generic-library audit finding 4) — payload `{ operationIds: string[] }`,
  emitted with `Scope = null` (global, since one batch can span several scopes). Fires wherever an
  entry leaves the registry with no corresponding `OPERATION_UPDATED`: `MaxHistory` eviction,
  `ClearFinished`, and the no-live-handle entry drop inside `RequestResume` (§5A.4 — keyed on
  `ResumePayload`, not on a second status). The host bounds its own
  history (`MaxHistory`); before this event existed, the client — the side actually rendering — never
  heard about a removal, so a long-lived store's mirror of bounded host history was itself unbounded,
  and `@shenora/react` compensated with two hand-written optimistic local prunes (`clearFinished`,
  `resume`) that this event now retires — one of which produced this release's only Critical (§5A.4's
  amendment note).

**One event type, not `STARTED`/`PROGRESS`/`ENDED`.** Each emission is a complete snapshot of one
operation, so it is last-write-wins per id: no ordering hazard, no cross-type races, and the client
folds it with a single reducer keyed by `id`. This is the harvested design (their consolidated snapshot
"supersedes an earlier one — no ordering hazard") applied per-operation instead of per-list, which keeps
the property and drops the cost of shipping the whole list on every tick. A consumer that only cares
about completion filters on `status`.

**All operation events come from the operations module, not from the owning feature module.** One
subscription, one snapshot source, one place for a channel filter to allow or deny, and the aggregate
view (status bar, activity panel) needs no cross-module wiring. A feature panel reads its own via a
selector on `module`/`kind`.

**Operation events do not replace a module's own events, and must not grow to.** `OPERATION_UPDATED`
carries the kit's uniform lifecycle — is it running, how far, did it end. Everything an app's UI
actually needs to know (*which* item imported, what the analysis found, which rows changed) is a custom
event the module publishes with `ctx.Publish`, correlated by putting the operation id in its own payload
when that matters. The two channels have different jobs: one is generic and kit-owned, the other is the
app's own vocabulary and stays entirely the app's. Anything that would push domain meaning into
`OperationInfo` — a status the kit does not own, a typed result payload, a `Kind` enum — belongs in a
custom event instead. That is the D21 line for this feature.

### 4.4 Frame rates (both configurable, per decision 4)

Two intervals, and it is worth naming them as the same idea at different levels:

| Option | Default | What it paces |
|---|---|---|
| `NotificationPumpOptions.FlushInterval` (today `WebViewIpcBridgeOptions.NotificationInterval`) | **50 ms** (~20 fps) | how often a client gets a frame of *all* pending events |
| `OperationRegistryOptions.ProgressInterval` | **100 ms** (~10 fps) | how often *one* operation's progress contributes an event |

`ProgressInterval` exists because the batcher **queues without coalescing** — a tight `Report` loop
otherwise ships hundreds of updates a second, which is exactly the defect the source app fixed. Progress
emissions are throttled per operation with a **trailing emit**, so the final value always lands; every
lifecycle transition (start, complete, fail, cancel, interrupt) emits immediately and is never
throttled. `MaxHistory` (default 50 finished entries) is an option on the same record.

### 4.5 The two traps the primitive closes structurally

- **The request token is not the operation's lifetime.** `BaseFacade`'s own doc already warns that work
  handed off "outlives the request, so give that its own token — do not capture this one and then wonder
  why a long operation dies when the page navigates". `Run`/`Start` create the operation's own
  `CancellationTokenSource`; the request token is not linked. The warning becomes a property.
- **No raw exception text leaves the host.** `Run`'s guarded body maps `OperationCanceledException` →
  `Cancel()`, `OperationException` → `Fail(code, parameters, message)` (the app naming its own failure —
  the one sanctioned channel), and anything else → `Fail(IpcErrorCodes.UnknownError, {type: <name>})`
  with the detail logged host-side. Identical to the dispatch boundary, so an operation failure and a
  request failure are the same contract. **`Fail(code: …, message: ex.Message)` is the bypass to watch
  for** (`ipc-contracts`); the guarded body never writes it.

### 4.6 Control surface and client

`OperationsFacade` (kit-shipped, precedent: `WindowCommandFacade`, `DropZoneFacade`), registered by
`services.AddShenoraOperations(options)` — opt-in, so an app with no long work pays nothing. It claims
the same `OperationRegistryOptions.ModuleName` the registry emits under, so the request module and the
event module are one string an app can rename once (the duplicate-module guard catches a collision with
an app's own module at composition). A route that starts long work returns
`new { operationId = ctx.Run(…) }` immediately — that is the "always produces a response" answer for
the long case:

| Type | Payload | Answers |
|---|---|---|
| `LIST` | `{ module?, scope? }` | `OperationInfo[]` — the client store's **snapshot** |
| `CANCEL` | `{ operationId }` | `{ cancelled: bool }` |
| `CLEAR_FINISHED` | `{ module?, scope? }` (generic-library audit finding 1 — was `—`, unfilterable) | — |
| `RESUME` | `{ operationId }` | `{ requested: bool }` |
| `DISMISS` (§5A.3) | `{ operationId }` | `{ dismissed: bool }` |
| `WAIT` (generic-library audit finding 3; shipped at the time as `PAUSE`, renamed by the status collapse) | `{ operationId }` | `{ requested: bool }` |

`CANCEL` is the app-level cancel route `ipc-contracts` already prescribes ("what the client 'cancel this
operation' case needs is an app-level CANCEL route carrying the operation id, never a transport
concern") — the kit now ships it instead of describing it. `WAIT` mirrors `RESUME`'s shape exactly:
the request always succeeds, the bool says whether the operation was actually `Running` and eligible
to be asked, and asking never changes the state itself — the owning module's `IOperation.Wait` does.

Client (`@shenora/react`): `useShenoraOperations()`, one `createShenoraStore` instance —
`snapshot: LIST`, `on: { OPERATION_UPDATED: fold-by-id, OPERATION_REMOVED: delete-named-ids }`,
`actions: { cancel, dismiss, wait, clearFinished, resume }`,
plus `running`/`waiting`/`finished` selectors derived from `byId` on every read. **Not shipped, deliberately:**
`byModule`/`byScope` selectors — an earlier revision of this section promised them, but filtering by
module or scope is a one-line consumer selector over `byId`
(`Object.values(state.byId).filter(o => o.module === 'X')`), and shipping indexes for it would be
duplicated derived state for no gain. The late-mounter case the store was built for
(§5.2 of the one-way design) is now host-backed: a progress strip mounting mid-operation renders current
state because the host is authoritative. Headless, per D13 — no components, no UI opinions.

## 5. §C — A base-agnostic channel with per-channel filtering

`NotificationPump` moves the transport-neutral half into `Shenora.Ipc` (`net10.0`):

```csharp
public sealed record NotificationPumpOptions
{
    public IEventBus? EventBus { get; init; }
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public int MaxQueued { get; init; } = 10_000;
    /// Per-channel delivery policy, applied at ENQUEUE. Default: deliver everything.
    public Func<IpcNotification, bool>? Filter { get; init; }
    public Action<string>? Log { get; init; }
}

public sealed class NotificationPump : IDisposable
{
    public NotificationPump(NotificationPumpOptions options);   // validates; subscribes to the bus NOW
    public TimeSpan FlushInterval { get; }
    public bool IsOpen { get; }          // the ready gate
    public int PendingCount { get; }     // was: internal PendingNotificationCount
    public void Enqueue(IpcNotification notification);
    public void Open();                  // client READY handshake
    public void Close();                 // new document / renderer death
    public bool TryDrainBatch(out string? json);   // guarded per-notification serialize + batch envelope
    public void Dispose();
}
```

It owns: bus subscription, the filter, the bounded drop-oldest queue, the ready gate, batch building,
and the per-notification serialization guard (one cyclic payload must not kill a batch). It owns **no
timer and no transport** — the base drives the tick, because *what* thread may touch the client is a
base-specific fact: on WinForms the flush must run on the UI thread (a `Forms.Timer`, which is why one
is used today); a headless base uses a `PeriodicTimer`.

`WebViewIpcBridge` keeps exactly what is WinForms/WebView2: the timer, `WebMessageReceived`,
`ContentLoading` → `Close()`, `READY` → `Open()`, `ProcessFailed` → `Close()`, and `PostWebMessageAsString`.
`WebViewIpcBridgeOptions` keeps its current names (`NotificationInterval`, `MaxQueuedNotifications`) —
forwarded, so no adopter break — and gains `NotificationFilter`.

**Why filtering, now.** Every bridge subscribes with `SubscribeToAll`, so with two windows every event
reaches both, and an auxiliary session or a remote client would receive the whole app's traffic. A
predicate is the seam (`generic-library`: seams over flags); the common cases — scope allow-list, module
deny-list — are one-liners the app writes, and the kit ships no policy.

**Carried invariants** (these are the paid-for bugs a second base must not re-earn): reset on
`ContentLoading`, never `NavigationStarting`; the gate re-closes on `ProcessFailed`; buffering starts at
construction, not at attach; drop-oldest at the cap; construction-time option validation with
self-naming messages; guarded per-notification serialize plus a catch-all in flush.

## 5A. The lifecycle, completed (amendment 2026-08-01, before 0.2.0 merged)

The first adopter reviewed the unreleased branch against ~20 real progress-streaming modules and found
two gaps. They are not two patches — they are one incomplete state machine, and fixing them that way is
what stops the next instance.

### 5A.1 The bug that names the rule

An interrupted offer (originally its own status, `Interrupted` — see §5A.2's later collapse) could only
be removed by RESUMING it. `Validate` gates every transition on `Status == Running`
(`OperationRegistry.cs`), so `Cancel`/`Complete`/`Fail` all refuse it; `ClearFinished` walks
`_finishedOrder`, which the checkpoint-registration path deliberately never writes; `PruneHistory` skips
offers on purpose. Three guards, each individually right and each with a comment explaining why — and
together they leave a state with no exit.

**This is not hypothetical:** the adopter shipped the same bug and hit it in production. A deployment
paused waiting on DNS records at a registrar the owner could not complete: permanently offering Resume,
permanently undeletable, because a paused run *is* the live state. The kit's own final review flagged it
as a Minor and the controller deferred it — the adopter hit it hours later, which is the better evidence.

**The rule, and it generalises past operations:** *every non-terminal state must have a sanctioned exit
to a terminal one.* An emergent trap is not visible in any single guard's diff, so it is enforced by a
test that enumerates the status set rather than by reviewer attention (§5A.4).

### 5A.2 Three bands — and, after a later collapse, five states not six

| Band | States | Pruned? | Exits |
|---|---|---|---|
| **Active** | `Running` | never | `Complete` · `Fail` · `Cancel` · `Wait` |
| **Waiting** — stopped, resumable, awaiting a decision | `Waiting` | **never** — an offer is not history | `Resume` · `Dismiss` · `Complete` · `Fail` |
| **Terminal** | `Completed` · `Failed` · `Cancelled` | yes (`MaxHistory`, `ClearFinished`) | — |

**Historical note (superseded by the amendment at the top of this doc):** this table originally listed
the Waiting band as TWO states, `Paused` and `Interrupted` — `Paused` was the missing band member this
subsection introduced (an app whose run stops mid-flight without crashing — expired cloud credentials, a
throttling provider, DNS not yet propagated, a migration awaiting confirmation — otherwise had to keep it
`Running`, a lie, or `Fail` it and immediately register a checkpoint offer, a terminal event for
something that never terminated, plus a second entry). Both states were later collapsed into the single
`Waiting` value shown above, on the observation that this table's own "Pruned?"/"Exits" columns already
treated them identically — the two-state design was tracking WHICH mechanism reached the band
(`Wait()` vs. a checkpoint registration), not anything the band itself needed to distinguish; that
distinction now lives on `ResumePayload` (§5A.4) instead of on the enum.

### 5A.3 The surface

```csharp
// IOperation — the owner's handle
void Wait(string? reason = null, OperationLabel? detail = null);   // Running → Waiting
void Resume();                                                      // Waiting → Running, clears the reason

// IOperationRegistry
bool Dismiss(string id);      // Waiting → Cancelled (terminal, prunable). Refuses Running.
bool RequestWait(string id);  // ASKS a Running operation to wait — added by the generic-library audit, see below

// OperationInfo
string? WaitReason { get; init; }   // app-defined, like Kind — the kit never interprets it
```

Four decisions worth stating, because each has a plausible-looking alternative:

- **`reason` is an app-defined STRING, not an enum** — the surveyed app switches on `credentials` /
  `transient` / `dns` / `migration` to decide what the UI offers. That is its taxonomy, not the kit's,
  exactly as `Kind` is. The optional `OperationLabel` carries the human-facing half, i18n-ready.
  **OPTIONAL, not required (generic-library audit, before publish):** the surveyed app's four-value
  taxonomy does not generalize to every consumer — a wait whose cause is self-evident (the user
  clicked Pause) has nothing to name, and a required parameter forced a filler string on that caller.
- **`Dismiss` is its own member, not `Cancel` accepting more states.** Declining a pending offer and
  cancelling live work are different acts: one removes an offer, the other signals a token and is
  permission-checked against `Cancellable`. This branch's only Critical came from precisely that
  conflation inside `Cancel`; rebuilding it in a new place would be a poor use of the lesson. `Dismiss`
  refuses `Running` for the same reason — dismissing live work would route around the permission check.
  It DOES signal the token first when one exists, so a waiting body parked on a token still unwinds.
- **`Wait` (the ACT) still has no client route — but `RequestWait` (the ASK) now does (generic-library
  audit finding 3, amended before publish; sketched/shipped at the time as `Pause`/`RequestPause`,
  renamed by the later status collapse — see the amendment at the top of this doc).** The original
  reasoning — "pausing is the host's own knowledge; a client cannot pause work it does not run" — is
  true for a host discovering its OWN blocker (the credentials/DNS/migration shape this section
  surveyed), but it is not the only such semantics that exists: the equally-common shape is a human
  clicking Pause on VISIBLE work — a download, a sync, a backup — which the kit itself already names as
  a consumer (a download-manager-style activity panel, "a download service starting an installer
  fetch"). For that shape the client needs to ASK, so `IOperationRegistry.RequestWait(id)` was added as
  an exact mirror of `RequestResume`: it emits `OPERATION_WAIT_REQUESTED { operationId, module, kind,
  scope }`, refuses anything not `Running`, and changes NOTHING itself — the owning module's own
  `Wait()` is still what actually stops the work, same ASK/ACT split as `RequestResume` vs. `Resume()`.
  `RESUME`/`DISMISS`/`WAIT` are ALL client routes now, because asking is never itself a policy decision;
  only the ACT (`Wait()`/`Resume()`, called by the operation's own owner) stays out of the client's
  hands.
- **`Report` still requires `Running`.** A waiting operation is not progressing, and letting progress
  tick while waiting is how a UI ends up showing motion for work that is stopped.

### 5A.4 The asymmetry that stays, and why

`RequestResume` on an entry reached via a live `Wait()` emits `OPERATION_RESUME_REQUESTED` and leaves
the entry alone — the app calls `Resume()` when it has actually resumed. On one with no live handle
(originally its own `Interrupted` status; now identified by a non-null `ResumePayload` instead — see
§5A.2's collapse) it still drops the entry, because there is no live handle to flip: the body died with
the process, and the app's resume path starts fresh work. That is intrinsic rather than an inconsistency
to tidy away, and it follows the same split that fixed the Critical — *the client asking* is not *the
state changing*.

**Amended (generic-library audit, before publish): the no-live-handle drop now ALSO publishes
`OPERATION_REMOVED { operationIds: [id] }`.** This asymmetry used to have no wire trace at all — the
live-`Wait()` case publishes nothing (nothing changed), and the no-live-handle case ALSO published
nothing (the entry just vanished from the host), so `@shenora/react`'s `resume` action carried its own
local guess at the asymmetry to keep the client in sync. That guess was this release's only Critical: it
once pruned unconditionally, dropping a live-`Wait()` row the host deliberately keeps. `OPERATION_REMOVED`
retires the guess — the client folds a named-id removal instead of re-deriving the host's asymmetry
on its own, so it structurally cannot diverge from it again.

**AMENDED again (owner direction, before publish, the status collapse at the top of this doc): the
asymmetry keyed on `ResumePayload`, not on a second status, for one release.** Once `Paused`/`Interrupted`
folded into one `Waiting` value, `RequestResume` could no longer branch on status at all — so the
drop-vs-keep decision moved to `ResumePayload`: non-null meant no live handle, null meant an ordinary
live `Wait()`, left in place. The `OPERATION_RESUME_REQUESTED` payload still carries `status` (always
`Waiting` now) so a handler can keep branching on the field without a breaking shape change.

**AMENDED again (2026-08-01, closing a residual hole before 0.2.0 was pushed or published): the
decision now keys on the registry's OWN provenance record, not on `ResumePayload`.** `ResumePayload` is
APP-controlled data — an app is free to attach one to `OperationOptions` at `Start()` time — so it was
never actually a reliable signal for "does this entry have a live handle": an app that attached its own
`ResumePayload` at `Start()` and then called `Wait()` had a genuinely LIVE operation (handle intact,
body parked) dropped out of the registry here anyway, silently orphaning every later
`Report`/`Complete`/`Fail` call on it. This is the same defect class `IModuleContext` closed for module
drift — a decision keyed on a value the caller also controls instead of on the fact the kit itself knows
for certain. The registry already knows the real answer: `RegisterWaiting` reconstructs an entry with
NO live body (that path exists precisely for a checkpoint with nothing behind it), while `Start` always
creates one with a live body. An internal `Entry.Reconstructed` flag, set only by `RegisterWaiting`, now
drives the drop-vs-keep decision — never exposed on `OperationInfo` (no consumer needs it, and every
public member is SemVer surface at 1.0). The Start-with-`ResumePayload`-then-`Wait()` combination that
used to be a recorded, deliberate ambiguity is now simply an ordinary live-`Wait()` entry: left in place,
same as any other, with `ResumePayload` unchanged in its other roles (`RegisterWaiting`'s non-empty
requirement, the dedupe key, riding the resume event).

Considered and rejected: an `Adopt(id) → IOperation` that re-attaches a handle to a no-live-handle
entry, unifying both paths and preserving the activity row's identity across a crash. It is genuinely
tidier, but no consumer has asked for identity across restart, the existing drop-then-register-fresh
path works in the app that has this problem today, and every public member is SemVer surface at 1.0.
Recorded as a known limit, not a gap.

**The invariant is enforced, not asserted:** a test enumerates `OperationStatus` and asserts every
non-terminal value has a transition reaching a terminal one. A future state added without an exit fails
that test rather than waiting for an adopter to strand a deployment on it. With one fewer non-terminal
status after the collapse, the sweep is simpler, not weaker — it still enumerates the LIVE enum rather
than a hardcoded list.

## 6. What this deliberately does NOT ship

- **No queue, scheduler, retry, or priority.** Starting work is the app's; the kit tracks what the app
  started.
- **No `ProcessType`-style enum, no phase model, no progress semantics.** `Kind` is an app string.
- **No UI, no i18n rendering.** Labels carry key + parameters; the app renders (D13).
- **No persistence.** The registry is in-memory — the source app deleted its state file for good
  reasons (finished history was purged at startup anyway; the only cross-restart state that matters is a
  resumable checkpoint, which belongs to the app's own store). `RegisterWaiting` (sketched/shipped at the
  time as `RegisterInterrupted`) is how that checkpoint re-enters the kit.
- **No envelope change.** Operations ride ordinary requests and notifications, so `WireMirrorTests` must
  stay green **untouched** — if it needs editing, the design has drifted into a wire change and that is
  the signal to stop.
- **One more limit recorded rather than solved (generic-library audit, before publish):**
  `OperationRegistryOptions.MaxHistory` is ONE global cap across every module and scope — no
  per-module or per-scope bounding seam, so a chatty module's finished history can crowd out a quiet
  one's. No consumer has asked for one; recording it is how the next one that does turns into evidence
  for a real seam instead of a re-argument from scratch.
  **The sibling limit this bullet used to pair it with — "registered but not yet started" has no
  representable status — is CLOSED (owner direction, before publish, the status collapse at the top of
  this doc), not merely recorded.** `Start`/`Run`'s first snapshot is still `Running`, but an app with
  its own queue in front of the registry can immediately call `Wait("queued")` on the returned handle
  before real work begins — the same mechanism a mid-flight blocker uses, needing no kit change and no
  third status, because nothing was ever progressing in either case.
- **No mobile transport package.** D16 stands: the seam, not the package.

## 7. Prior decisions this touches

- **Supersedes `docs/2026-07-31-shenora-oneway-ipc-design.md` §6 bullet 1** ("No operation/job manager,
  registry, queue, or progress TYPE"). That bullet was right about *queue* and about *domain* types, and
  it explicitly held the door open — the standing bar is "a capability someone needs and cannot express
  is a gap; a capability nobody has needed is speculation". Adoption produced the evidence: one app's
  320-line registry plus a second app's job-event archetype, and the kit's own shipped client store
  *requires* a host-side snapshot source that no adopter can express without building one. The queue,
  the phases, the domain enum and the UI stay unshipped — only the correlation-and-lifecycle mechanism
  moves in. Recorded as **D23**.
- **D21** (primitives + hooks, never the product): the test is *could a consumer build its own version
  of this product on our primitives without adopting our product decisions?* An activity panel, a status
  bar, a toast, a per-feature progress strip are all buildable on `OperationInfo` + the event + the
  store, with no kit opinion adopted. Nothing here decides what an operation is.
- **D22** (mechanism names): `Operation` is the kit's existing vocabulary (`OperationException`,
  `OPERATION_CANCELLED`), not a scenario word — deliberately not `Job`/`Task`/`Process`, each of which
  invites a product to grow behind it.
- **D19/D20** (placement): operations live in `Shenora.Ipc`, not `Shenora.Core`, because they reuse
  `IpcError`/`OperationException` — Core cannot reference `Ipc`, so putting them in Core would mean a
  second structured-error type. Both packages are `net10.0`, so the portability bar is met either way;
  a `WinForms`-only consumer (which by D19 has no `Ipc` dependency) has no client to report progress to.
- **`ipc-contracts`** (unchanged, and load-bearing here): correlation id in the PAYLOAD never in
  module/type/scope (the match cache); notifications always batched; the dispatch path stays
  context-preserving while a handed-off body does not; the boundary never throws; no raw exception text.

## 8. Breaking changes (0.2.0)

| Change | Migration |
|---|---|
| `RouteMessageAsync(IpcRequest, CancellationToken)` → `(IpcRequest, IModuleContext, CancellationToken)` | add the parameter; ignore it if unused |
| `BaseFacade(ILogger?)` → `BaseFacade(ILogger?, IEventBus?, IOperationRegistry?)` | source-compatible (optional params); pass the bus to publish |
| `WebViewIpcBridge` internals move to `NotificationPump` | none — options names and public surface preserved, plus `NotificationFilter` |
| `OperationOptions.Resumable` / `OperationInfo.Resumable` (C#) and `resumable` (TS) REMOVED (generic-library audit, before publish) | drop the property; test resumability via `status === OperationStatuses.Waiting` (already the correct client test) |
| `OperationOptions.Progress`/`OperationInfo.Progress`: `int?` → `OperationProgress?`; `IOperation.Report(int?, …)` → `Report(OperationProgress?, …)` (TS: `progress?: number` → `progress?: OperationProgress`) — progress is not percent, before publish | wrap the reported number: `new OperationProgress(value, total, unit)`; a bare percent is `new OperationProgress(pct, 100, "percent")` |
| **The status collapse (owner direction, before publish — XHR framing):** `OperationStatus.Paused`/`.Interrupted` → one value, `OperationStatus.Waiting`; `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause` → `IOperation.Wait(reason?, detail?)`; `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting`; `IOperationRegistry.RequestPause` → `RequestWait`; `OperationEvents.PauseRequested`/`OPERATION_PAUSE_REQUESTED` → `WaitRequested`/`OPERATION_WAIT_REQUESTED`; facade route `PAUSE` → `WAIT`; client `OperationStatuses.Paused`/`.Interrupted` and the `paused`/`interrupted` getters REMOVED, `Waiting: 'waiting'` added (`waiting` is now the whole band) | rename every occurrence 1:1 per the mapping at the top of this doc; a client testing "is this waiting" now reads `status === OperationStatuses.Waiting` instead of unioning `paused`/`interrupted`; `RequestResume`'s drop-vs-keep now reads `resumePayload`, not `status`, if a handler branched on the removed values |

`IModuleFacade.HandleMessageAsync` is unchanged: a facade still always produces a response. What changes
is that the response to a long operation is now *specified* — an immediate `{ operationId }` ack, with
the work streaming under it.

## 9. Verification plan (assert, don't assume)

The gate is `node devtools/dev.mjs verify`, plus these, and each tripwire is **broken on purpose to
prove it can fail** (the standing rule — a green tripwire that cannot fail is worth nothing):

1. **Mirror:** `WireMirrorTests` green untouched. **New tripwire:** `OperationStatus` (C#) ⇄ the TS union
   must be set-equal, parser self-checked with `Assert.NotEmpty`.
2. **Token trap:** an operation started by a route survives cancellation of the *request* token
   (the page-navigation case), and is cancelled by `CANCEL`. Bounded `WaitAsync` — a test that awaits a
   cancellable operation must never be able to hang.
3. **No leak:** a route throwing an exception carrying a planted secret produces an `OperationInfo`
   whose `Error` is `UNKNOWN_ERROR` + the exception type name only (`DoesNotContain` on the secret).
4. **Throttle:** N rapid `Report`s inside one window produce one emission **and the last value always
   lands** (the trailing emit); a lifecycle transition emits immediately regardless.
5. **Idempotence:** `Complete` after `Fail` is a no-op; double `Cancel` is a no-op; `Report` after
   terminal is ignored.
6. **Filtering:** two pumps with different filters each receive only their own events; a filtered-out
   event never enters the queue.
7. **Late mounter:** start an operation, let events flow, THEN mount — the store renders current state.
8. **The UI-thread claim must survive the refactor.** The sample's `SLOW` route is rewritten onto
   `ctx.Run`, and the `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` probe must still report **0
   unresponsive samples** for the streamed shape (v0.1.0 measured 0/95 streamed vs 13/61 blocking). A
   refactor that quietly moves work back onto the UI thread is the exact failure this must catch — and
   the probe still refuses to report unless the click confirms it landed.
9. **Baselines** reviewed by type section before promotion; `docs/ADOPTION.md`, `ARCHITECTURE.md`,
   `CHANGELOG.md` (`### Breaking`) and the npm README updated in the same pass.

## 10. Staging

One spec, three stages, per `phase-workflow` (a deliberate sequence, not one mega-commit). Each stage is
a review boundary; within a stage each task commits on its own, which is what keeps a bisect useful:

1. **Contract** — `IModuleContext`, the signature change, `Publish`, sample + docs.
2. **Operations** — registry, options, facade, throttle, client store, `useShenoraOperations`.
3. **Channel** — `NotificationPump` extraction, per-channel filter, `WebViewIpcBridge` reduced to its
   WinForms/WebView2 adapter.

Stage 1 is independently useful (it closes the adopter's first finding), and stage 3 touches no public
behaviour — which is the order that keeps each review small.
