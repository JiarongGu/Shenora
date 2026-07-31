# The communication core: event-first module contract, tracked operations, base-agnostic channel — design (2026-08-01)

Status: **DESIGN, approved to write (user, 2026-08-01), not implemented.** Ships as **0.2.0** with a
`### Breaking` section — the first deliberate break since v0.1.0.

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
| id, owning module, `Kind` (app-defined **string**), `Scope`, status, `Progress` (`int?`, null = indeterminate), timestamps | a `ProcessType` **enum** — 15 domain values; the kit ships none |
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
    IOperation? Find(string id);
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

**Provenance note, so a future reviewer can cut it cleanly:** `Interrupted` / `Resumable` /
`ResumePayload` / `RegisterInterrupted` / `RequestResume` come from **one** app, not two. They are
included because they are pure mechanism (a state, an opaque token, an event — the app owns the
checkpoint and the resume entrypoint) and cost five members. Everything else in §4 clears the two-app
bar. If a 1.0 audit wants surface removed, this is the first candidate.

### 4.3 The event contract

The registry publishes on the bus under a kit module (`OperationRegistryOptions.ModuleName`, default
`"OPERATIONS"`):

- `OPERATION_UPDATED` — payload is the full `OperationInfo`, for **every** transition: start, progress,
  terminal. Event `Scope` = the operation's scope.
- `OPERATION_RESUME_REQUESTED` — payload `{ operationId, module, kind, resumePayload, scope }`.

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
| `CLEAR_FINISHED` | — | — |
| `RESUME` | `{ operationId }` | `{ requested: bool }` |

`CANCEL` is the app-level cancel route `ipc-contracts` already prescribes ("what the client 'cancel this
operation' case needs is an app-level CANCEL route carrying the operation id, never a transport
concern") — the kit now ships it instead of describing it.

Client (`@shenora/react`): `useShenoraOperations()`, one `createShenoraStore` instance —
`snapshot: LIST`, `on: { OPERATION_UPDATED: fold-by-id }`, `actions: { cancel, clearFinished, resume }`,
plus selectors (`running`, `byModule`, `byScope`). The late-mounter case the store was built for
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

## 6. What this deliberately does NOT ship

- **No queue, scheduler, retry, or priority.** Starting work is the app's; the kit tracks what the app
  started.
- **No `ProcessType`-style enum, no phase model, no progress semantics.** `Kind` is an app string.
- **No UI, no i18n rendering.** Labels carry key + parameters; the app renders (D13).
- **No persistence.** The registry is in-memory — the source app deleted its state file for good
  reasons (finished history was purged at startup anyway; the only cross-restart state that matters is a
  resumable checkpoint, which belongs to the app's own store). `RegisterInterrupted` is how that
  checkpoint re-enters the kit.
- **No envelope change.** Operations ride ordinary requests and notifications, so `WireMirrorTests` must
  stay green **untouched** — if it needs editing, the design has drifted into a wire change and that is
  the signal to stop.
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
