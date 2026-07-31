# Communication core (0.2.0) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`)
> syntax for tracking.

**Goal:** Make the host contract event-first — a route receives a module context with `Publish` and
tracked operations — and make the host's outbound pipeline base-agnostic with per-channel filtering.

**Architecture:** Three stages over the existing `Shenora.Ipc` stack. Stage 1 puts `IModuleContext` in
the route signature (breaking). Stage 2 adds the operation registry (`Shenora.Ipc`), its facade, and the
`@shenora/react` store. Stage 3 extracts the transport-neutral half of `WebViewIpcBridge` into a
portable `NotificationPump` with a delivery filter. Full design + rationale:
`docs/2026-08-01-shenora-communication-core-design.md`; decision record: **D23**.

**Tech Stack:** C# / .NET 10 (`net10.0` for `Core`/`Ipc`, `net10.0-windows` for the shell packages),
xunit (single test project, **serial**), TypeScript + vitest (`@shenora/react`), `node devtools/dev.mjs`
as the only dev loop.

## Global Constraints

Every task's requirements implicitly include these. They are repo rules, not preferences.

- **Never commit without explicit user approval** (`CLAUDE.md`). Each task ends with a prepared commit;
  ask once per STAGE and commit the stage's tasks then. Never `--no-verify`.
- **The gate is `node devtools/dev.mjs verify`** — dotnet build + tests, samples, npm build + tests,
  sample-web typecheck, sensitive scan, knowledge check, doctor. A task is not done until it passes.
- **Run one test file with** `dotnet test Shenora.slnx --filter "FullyQualifiedName~<ClassName>" -v minimal --nologo`
  and one client file with `npm test -- <file>` from `src/Shenora.React`.
- **No raw exception text crosses the bridge, on ANY error path** — `{code, parameters}` only; unknown
  exceptions become `UNKNOWN_ERROR` + the exception TYPE name, details to the host log. Never build an
  `OperationException` (or an operation failure) from `ex.Message`.
- **`ConfigureAwait(false)` is banned in the dispatch path and REQUIRED in a handed-off background
  body.** Both halves are load-bearing (`.claude/knowledge/ipc-contracts.md`).
- **A test that awaits a cancellable operation must be bounded** (`.WaitAsync(TimeSpan.FromSeconds(5))`).
  A bare await on something that stops being cancelled HANGS the whole serial suite instead of failing.
- **After adding a tripwire, BREAK the thing it watches** and confirm the message before moving on.
  A green tripwire that cannot fail is worth nothing.
- **Central package management:** no `Version=` on a `PackageReference`; versions live in
  `src/Directory.Packages.props`.
- **Public repo:** no absolute local paths, no private sibling names, no personal data in tracked files
  or commit messages. Private context goes in `local/`.
- **API baselines gate the public surface.** After any public change run the suite, review the baseline
  diff BY TYPE SECTION, then promote. Never promote a diff you have not read.
- **Sources are BOM-less UTF-8**; never round-trip a source file through PowerShell 5
  `Get-Content`/`Set-Content`.

---

## File Structure

**Stage 1 — contract**

| File | Responsibility |
|---|---|
| `src/Shenora.Ipc/IModuleContext.cs` (create) | the route's world: `Module`, `Logger`, `Publish` (grows `Start`/`Run` in Task 4) |
| `src/Shenora.Ipc/ModuleContext.cs` (create) | the internal implementation `BaseFacade` builds once per facade |
| `src/Shenora.Ipc/BaseFacade.cs` (modify) | new ctor params, builds the context, new abstract signature |
| `src/Shenora.WebView2/WindowCommandFacade.cs`, `DropZoneFacade.cs` (modify) | signature update |
| `samples/…/SampleFacade.cs`, `samples/…/PortableSampleFacade.cs` (modify) | signature update |
| `tests/Shenora.Tests/Ipc/ModuleContextTests.cs` (create) | publish reaches the bus, module can't drift, absent bus throws |

**Stage 2 — operations**

| File | Responsibility |
|---|---|
| `src/Shenora.Ipc/Operations/OperationModels.cs` (create) | `OperationStatus`, `OperationLabel`, `OperationOptions`, `OperationInfo` |
| `src/Shenora.Ipc/Operations/IOperation.cs` (create) | the per-operation handle |
| `src/Shenora.Ipc/Operations/IOperationRegistry.cs` (create) | the registry contract |
| `src/Shenora.Ipc/Operations/OperationRegistry.cs` (create) | in-memory implementation: state, events, throttle, history cap |
| `src/Shenora.Ipc/Operations/OperationRegistryOptions.cs` (create) | `ModuleName`, `ProgressInterval`, `MaxHistory`, `TimeProvider` |
| `src/Shenora.Ipc/Operations/OperationEvents.cs` (create) | event/request type-name constants |
| `src/Shenora.Ipc/Operations/OperationsFacade.cs` (create) | `LIST` / `CANCEL` / `CLEAR_FINISHED` / `RESUME` |
| `src/Shenora.Ipc/Operations/OperationServiceCollectionExtensions.cs` (create) | `AddShenoraOperations` |
| `src/Shenora.React/src/operations.ts` (create) | wire types + `useShenoraOperations` |
| `tests/Shenora.Tests/Ipc/Operations*.cs` (create) | one file per task below |

**Stage 3 — channel**

| File | Responsibility |
|---|---|
| `src/Shenora.Ipc/NotificationPump.cs` (create) | bus subscribe → filter → bounded queue → gate → batch → guarded serialize |
| `src/Shenora.Ipc/NotificationPumpOptions.cs` (create) | `EventBus`, `FlushInterval`, `MaxQueued`, `Filter`, `Log` |
| `src/Shenora.WebView2/WebViewIpcBridge.cs` (modify) | reduced to the WinForms/WebView2 adapter |
| `tests/Shenora.Tests/Ipc/NotificationPumpTests.cs` (create) | cap, gate, filter, batch, guarded serialize |

---

# STAGE 1 — The contract

### Task 1: `IModuleContext` in the route signature

**Files:**
- Create: `src/Shenora.Ipc/IModuleContext.cs`, `src/Shenora.Ipc/ModuleContext.cs`
- Modify: `src/Shenora.Ipc/BaseFacade.cs`, `src/Shenora.WebView2/WindowCommandFacade.cs`,
  `src/Shenora.WebView2/DropZoneFacade.cs`, `samples/Shenora.Sample.Desktop/SampleFacade.cs`,
  `samples/Shenora.Sample.Logic/PortableSampleFacade.cs`, and every test facade
  (`tests/Shenora.Tests/Ipc/BaseFacadeTests.cs`, `DispatchCancellationTests.cs`,
  `IpcCompositionTests.cs`, `ModuleLifecycleTests.cs`, `ScopedContainerRouterTests.cs`)
- Test: `tests/Shenora.Tests/Ipc/ModuleContextTests.cs`

**Interfaces:**
- Consumes: `Shenora.Core.IEventBus` (already referenced by `Shenora.Ipc` — no new package edge).
- Produces: `IModuleContext { string Module; ILogger Logger; void Publish(string type, object? payload = null, string? scope = null); }`;
  `BaseFacade(ILogger? logger = null, IEventBus? events = null)`;
  `protected abstract Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shenora.Tests/Ipc/ModuleContextTests.cs`:

```csharp
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class ModuleContextTests
{
    private sealed class PublishingFacade(IEventBus? events) : BaseFacade(null, events)
    {
        public override string ModuleName => "REPORTS";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            context.Publish("STARTED", new { step = 1 }, scope: "s1");
            return Task.FromResult<object?>(new { module = context.Module });
        }
    }

    [Fact]
    public async Task Publish_emits_under_the_facades_own_module()
    {
        var bus = new EventBus();
        var seen = new List<EventMessage>();
        bus.SubscribeToAll(m => { seen.Add(m); return Task.CompletedTask; });

        await new PublishingFacade(bus).HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        var message = Assert.Single(seen);
        Assert.Equal("REPORTS", message.Module);   // NOT a literal the route typed
        Assert.Equal("STARTED", message.Type);
        Assert.Equal("s1", message.Scope);
    }

    [Fact]
    public async Task Context_module_matches_the_facade_module_name()
    {
        var response = await new PublishingFacade(new EventBus())
            .HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        Assert.True(response.Success);
        Assert.Equal("REPORTS", IpcJson.SerializeToElement(response.Data).GetProperty("module").GetString());
    }

    [Fact]
    public async Task Publish_without_a_bus_fails_loudly_and_names_the_fix()
    {
        // A silent no-op here is the failure class this repo keeps paying for. The response still
        // never throws (the dispatch boundary contract), so assert on the LOG-side error shape.
        var response = await new PublishingFacade(null).HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~ModuleContextTests" -v minimal --nologo`
Expected: FAIL — `IModuleContext` does not exist; `BaseFacade` has no two-argument constructor.

- [ ] **Step 3: Add the contract**

Create `src/Shenora.Ipc/IModuleContext.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The world a route runs in: which module it speaks for, where it logs, and how it EMITS.
/// <para>
/// This exists because the module contract carried the request path and not the event path
/// (D23): <c>Shenora.Ipc</c> had zero references to <see cref="Shenora.Core.IEventBus"/> while
/// the kit's own <c>DropZoneManager</c> took one as a REQUIRED option, so every app re-agreed
/// the module/type/scope conventions by hand. Publishing is the default gesture here, not a
/// wiring exercise.
/// </para>
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// The owning module — the same string as <see cref="IModuleFacade.ModuleName"/>, supplied by
    /// the kit. A route can therefore never emit under a module it does not own, which is exactly
    /// what a hand-typed literal in every emit call allowed.
    /// </summary>
    string Module { get; }

    /// <summary>The facade's logger (never null — <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> when unconfigured).</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Emit an event on the host bus under <see cref="Module"/>. Fire-and-forget by design:
    /// <see cref="Shenora.Core.IEventBus.Emit(string, string, object?, string?)"/> guarantees a
    /// subscriber cannot fault the caller.
    /// </summary>
    void Publish(string type, object? payload = null, string? scope = null);
}
```

Create `src/Shenora.Ipc/ModuleContext.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// The <see cref="IModuleContext"/> a <see cref="BaseFacade"/> builds once, at construction — the
/// module name is known then, and rebuilding it per request would allocate on the IPC hot path.
/// </summary>
internal sealed class ModuleContext(string module, ILogger logger, IEventBus? events) : IModuleContext
{
    public string Module { get; } = module;

    public ILogger Logger { get; } = logger;

    public void Publish(string type, object? payload = null, string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        // LOUD, not silent. A no-op here would drop an app's progress stream with no error and no
        // log line — the same shape as MaxQueuedNotifications = 0 discarding every notification.
        // The message names the fix, because the error is a COMPOSITION mistake, not a code bug.
        if (events is null)
            throw new InvalidOperationException(
                $"Module '{Module}' called IModuleContext.Publish but no IEventBus was supplied to " +
                "BaseFacade. Pass one (ShenoraApplication.CreateBuilder registers an IEventBus by default, " +
                "so inject IEventBus into the facade and forward it: base(logger, events)).");
        events.Emit(Module, type, payload, scope);
    }
}
```

- [ ] **Step 4: Change `BaseFacade`**

In `src/Shenora.Ipc/BaseFacade.cs`: take the new dependencies, build the context, pass it to the route.
Keep every existing comment — the `ConfigureAwait` post-mortem above `RouteMessageAsync` and the
`UnknownType`/`Done` helpers are unchanged.

```csharp
public abstract class BaseFacade : IModuleFacade
{
    private readonly ILogger _logger;
    private IModuleContext? _context;

    /// <summary>
    /// The logger is optional so composition works without <c>AddLogging</c>; the bus is optional so a
    /// facade that never publishes (and every unit test that constructs one bare) still works. A facade
    /// that DOES publish without one fails loudly at the call site — see <see cref="ModuleContext"/>.
    /// </summary>
    protected BaseFacade(ILogger? logger = null, IEventBus? events = null)
    {
        _logger = logger ?? NullLogger.Instance;
        Events = events;
    }

    /// <summary>The bus this facade publishes on, if one was supplied.</summary>
    protected IEventBus? Events { get; }

    /// <summary>Built lazily: ModuleName is an abstract property, so it is not readable from the ctor.</summary>
    protected IModuleContext Context => _context ??= new ModuleContext(ModuleName, _logger, Events);

    // … HandleMessageAsync unchanged except for the call:
    //     var data = await RouteMessageAsync(request, Context, cancellationToken);
}
```

⚠ `ModuleName` is abstract, so it CANNOT be read in the constructor — a derived class's property is not
initialized yet. Build the context lazily as shown; do not "simplify" it into the constructor.

- [ ] **Step 5: Update the abstract signature and its doc**

```csharp
    /// <summary>
    /// Route the request to the module's handler and return the response data (null when the
    /// operation returns nothing). Throw <see cref="OperationException"/> for every expected failure.
    /// <para>
    /// <paramref name="context"/> is how a route EMITS (<see cref="IModuleContext.Publish"/>) — the
    /// event path is the desktop default and the request path the special case, so it is in the
    /// signature rather than behind a base-class member a route author may never find.
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> is the CALLER's lifetime, not a per-request cancel …
    /// (keep the existing paragraph verbatim)
    /// </para>
    /// </summary>
    protected abstract Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken);
```

- [ ] **Step 6: Update every override**

Add the parameter to `WindowCommandFacade`, `DropZoneFacade`, `SampleFacade`, `PortableSampleFacade` and
the five test facades. Mechanical — no behaviour change in this step.

Run: `dotnet build Shenora.slnx -v minimal --nologo`
Expected: 0 errors. Any remaining CS0534 ("does not implement inherited abstract member") names an
override that was missed.

- [ ] **Step 7: Run the new tests and the whole Ipc suite**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~Shenora.Tests.Ipc" -v minimal --nologo`
Expected: PASS, including the three new tests.

- [ ] **Step 8: Prove the loud failure is real**

Temporarily change `ModuleContext.Publish` to `if (events is null) return;`, re-run
`ModuleContextTests`, and confirm `Publish_without_a_bus_fails_loudly_and_names_the_fix` FAILS. Restore
the throw. (Standing rule: a tripwire you have not broken is not a tripwire.)

- [ ] **Step 9: Promote the API baseline**

Run: `node devtools/dev.mjs verify`
Then read the `Shenora.Ipc.txt` baseline diff **by type section** — expected: `IModuleContext` added,
`BaseFacade` constructor and `RouteMessageAsync` changed, nothing else. Promote per the repo's baseline
workflow (see `tests/Shenora.Tests/Api/ApiSurfaceTests.cs` for the promotion command it prints).

- [ ] **Step 10: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc src/Shenora.WebView2 samples tests
git commit -m "feat(ipc)!: put the event path in the route signature (IModuleContext)

A route now receives IModuleContext — Module, Logger, Publish — so emitting is the
default gesture instead of a per-app wiring exercise. Publish emits under the facade's
own module, which a hand-typed literal could drift from. Absent bus fails loudly and
names the fix rather than silently dropping the app's event stream.

BREAKING: RouteMessageAsync gains an IModuleContext parameter.
Design: docs/2026-08-01-shenora-communication-core-design.md §3 · Decision: D23"
```

---

# STAGE 2 — Operations

### Task 2: The operation model and registry core

**Files:**
- Create: `src/Shenora.Ipc/Operations/OperationModels.cs`, `IOperation.cs`, `IOperationRegistry.cs`,
  `OperationRegistry.cs`, `OperationRegistryOptions.cs`, `OperationEvents.cs`
- Test: `tests/Shenora.Tests/Ipc/OperationRegistryTests.cs`

**Interfaces:**
- Consumes: `IEventBus` (Core), `IpcError`, `IpcErrorCodes`, `OperationException` (Ipc).
- Produces: the types listed in the design §4.2, plus
  `OperationEvents.Updated = "OPERATION_UPDATED"`, `OperationEvents.ResumeRequested = "OPERATION_RESUME_REQUESTED"`,
  `OperationRegistryOptions { string ModuleName = "OPERATIONS"; TimeSpan ProgressInterval = 100ms; int MaxHistory = 50; TimeProvider TimeProvider = TimeProvider.System; Action<string>? Log = null; }`,
  `OperationRegistry(IEventBus bus, OperationRegistryOptions? options = null)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shenora.Tests/Ipc/OperationRegistryTests.cs`:

```csharp
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class OperationRegistryTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build(
        OperationRegistryOptions? options = null)
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        // ProgressInterval = zero disables throttling; Task 3 covers the throttle itself.
        return (new OperationRegistry(bus, options ?? new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.Zero,
        }), events);
    }

    private static OperationInfo Payload(EventMessage message) => Assert.IsType<OperationInfo>(message.Payload);

    [Fact]
    public void Start_publishes_a_running_snapshot_under_the_operations_module()
    {
        var (registry, events) = Build();

        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });

        var message = Assert.Single(events);
        Assert.Equal("OPERATIONS", message.Module);
        Assert.Equal(OperationEvents.Updated, message.Type);
        Assert.Equal("prod", message.Scope);
        var info = Payload(message);
        Assert.Equal(operation.Id, info.Id);
        Assert.Equal("DEPLOY", info.Module);      // the OWNING module rides in the payload
        Assert.Equal("PUSH", info.Kind);
        Assert.Equal(OperationStatus.Running, info.Status);
        Assert.Null(info.Progress);               // null = indeterminate, not zero
    }

    [Fact]
    public void Report_updates_progress_and_detail()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Report(40, new OperationLabel(Text: "uploading", Key: "deploy.stage.upload"));

        var info = Payload(events[^1]);
        Assert.Equal(40, info.Progress);
        Assert.Equal("deploy.stage.upload", info.Detail!.Key);
        Assert.Equal("uploading", info.Detail.Text);
    }

    [Fact]
    public void Progress_is_clamped_to_the_0_100_range()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Report(140);

        Assert.Equal(100, Payload(events[^1]).Progress);
    }

    [Fact]
    public void Complete_is_terminal_and_finishing_twice_is_a_no_op()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Complete();
        var afterComplete = events.Count;
        operation.Fail("TOO_LATE");                 // the "Complete at the end + Fail in a catch" pattern
        operation.Report(50);

        Assert.Equal(afterComplete, events.Count);  // nothing after the terminal transition
        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Completed, info.Status);
        Assert.Equal(100, info.Progress);           // completion implies 100
        Assert.NotNull(info.FinishedAt);
    }

    [Fact]
    public void Fail_carries_a_structured_error_never_free_text()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Fail("DEPLOY_REJECTED", new Dictionary<string, string> { ["env"] = "prod" });

        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Failed, info.Status);
        Assert.Equal("DEPLOY_REJECTED", info.Error!.Code);
        Assert.Equal("prod", info.Error.Parameters!["env"]);
    }

    [Fact]
    public void Cancel_cancels_the_operations_own_token()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });

        Assert.True(registry.Cancel(operation.Id));

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }

    [Fact]
    public void GetAll_filters_by_module_and_scope_and_lists_running_first()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        var done = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        registry.Start("SCAN", new OperationOptions { Kind = "FILES", Scope = "dev" });
        done.Complete();

        var deployProd = registry.GetAll(module: "DEPLOY", scope: "prod");

        Assert.Equal(2, deployProd.Count);
        Assert.Equal(running.Id, deployProd[0].Id);       // running before finished
        Assert.Equal(done.Id, deployProd[1].Id);
        Assert.Single(registry.GetAll(module: "SCAN"));
    }

    [Fact]
    public void ClearFinished_removes_history_and_keeps_running_work()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();

        registry.ClearFinished();

        Assert.Equal(running.Id, registry.GetAll().Single().Id);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~OperationRegistryTests" -v minimal --nologo`
Expected: FAIL — none of the operation types exist.

- [ ] **Step 3: Write the models**

`src/Shenora.Ipc/Operations/OperationModels.cs` — exactly the shapes in design §4.2. Key points the
tests pin: `Progress` is `int?` (null = indeterminate, NOT zero), `Kind` is an app-defined **string**
(the kit ships no operation-type enum — that is the app's domain), `OperationLabel` mirrors `IpcError`'s
i18n-ready `{code, parameters}` idea so the host never formats a UI string, and `Module` is the OWNING
module carried in the payload while the event itself comes from the operations module.

`OperationEvents.cs`:

```csharp
namespace Shenora.Ipc;

/// <summary>Event and request type names for the operations module. Constants, so an app matches by
/// symbol rather than by a literal that a rename cannot follow.</summary>
public static class OperationEvents
{
    /// <summary>A full <see cref="OperationInfo"/> snapshot — every transition uses this one type,
    /// so folding is last-write-wins by id with no cross-type ordering hazard.</summary>
    public const string Updated = "OPERATION_UPDATED";

    /// <summary>An interrupted+resumable operation should be continued by its owning module.</summary>
    public const string ResumeRequested = "OPERATION_RESUME_REQUESTED";
}
```

- [ ] **Step 4: Write the registry**

`OperationRegistry` holds one lock over `Dictionary<string, OperationEntry>` (mutable state + its CTS),
and publishes an immutable `OperationInfo` snapshot on every transition. The handle is a small class
closing over the registry and the id. Shape:

```csharp
public sealed class OperationRegistry : IOperationRegistry, IDisposable
{
    private readonly IEventBus _bus;
    private readonly OperationRegistryOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public OperationRegistry(IEventBus bus, OperationRegistryOptions? options = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _options = options ?? new OperationRegistryOptions();
        if (_options.MaxHistory < 0) throw new ArgumentOutOfRangeException(nameof(options), …);
        if (_options.ProgressInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), …);
    }

    public IOperation Start(string module, OperationOptions options) { … Publish(entry, immediate: true); }

    // Report → mutate under the lock, then Publish(entry, immediate: false)  (throttled in Task 3)
    // Complete/Fail/Cancel → Finish(id, status, error): ignore if already terminal (idempotent),
    //   set FinishedAt, force Progress = 100 on Completed, dispose the CTS, prune history,
    //   then Publish(entry, immediate: true)
    // Cancel additionally cancels the CTS BEFORE the status flip so a body observing the token
    //   sees cancellation, not a completed-then-cancelled race.
}
```

Rules the implementation owes, each earned in the source app or by this repo:
- **Finish is idempotent** — `Complete` after `Fail` is a safe no-op, so the "`Complete` at the end +
  `Fail` in the catch" pattern works. `Report` after terminal is ignored.
- **Options are validated at construction**, with a message that names the option (repo convention).
- **The bus emit is fire-and-forget** via `IEventBus.Emit` — its doc states subscribers cannot fault the
  caller, which is why discarding is safe here and not a hazard.
- **History is capped** at `MaxHistory` finished entries (oldest first out), never touching running ones.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~OperationRegistryTests" -v minimal --nologo`
Expected: PASS (9 tests).

- [ ] **Step 6: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc/Operations tests/Shenora.Tests/Ipc/OperationRegistryTests.cs
git commit -m "feat(ipc): operation registry — tracked long-running work on the bus

Start/Report/Complete/Fail/Cancel/GetAll/ClearFinished with idempotent terminal
transitions, publishing one OPERATION_UPDATED snapshot per transition (last-write-wins
by id). Kind is an app string and labels carry key+parameters: the kit tracks
operations, it does not decide what one is.

Design: docs/2026-08-01-shenora-communication-core-design.md §4 · Decision: D23"
```

### Task 3: Throttled progress (the frame rate) and the history cap

**Files:**
- Modify: `src/Shenora.Ipc/Operations/OperationRegistry.cs`,
  `src/Shenora.Ipc/Operations/OperationRegistryOptions.cs`, `src/Directory.Packages.props`,
  `tests/Shenora.Tests/Shenora.Tests.csproj`
- Test: `tests/Shenora.Tests/Ipc/OperationThrottleTests.cs`

**Interfaces:**
- Consumes: `OperationRegistry` from Task 2.
- Produces: `OperationRegistryOptions.ProgressInterval` (default 100 ms) and `.TimeProvider`
  (default `TimeProvider.System`) honoured by `Report`.

- [ ] **Step 1: Add the test-only fake clock package**

Add to `src/Directory.Packages.props` under `<!-- Tests -->`:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```

and to `tests/Shenora.Tests/Shenora.Tests.csproj`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Run: `dotnet restore Shenora.slnx`
Expected: success. If the version does not resolve, run
`dotnet package search Microsoft.Extensions.TimeProvider.Testing --exact-match` and use the newest
stable release; **`src/` must never reference it** — production code uses only the BCL `TimeProvider`.

- [ ] **Step 2: Write the failing test**

Create `tests/Shenora.Tests/Ipc/OperationThrottleTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Progress emission is a FRAME RATE, and it has to be one: the notification batcher queues events
/// WITHOUT coalescing, so an unthrottled per-item Report loop ships hundreds of updates a second.
/// The trailing emit is the half that is easy to omit and impossible to notice — without it the last
/// progress value of a fast operation is simply lost, and a stuck-at-80% bar is the symptom.
/// </summary>
public class OperationThrottleTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events, FakeTimeProvider Clock) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(100),
            TimeProvider = clock,
        });
        return (registry, events, clock);
    }

    [Fact]
    public void Rapid_progress_reports_collapse_to_one_emission_per_window()
    {
        var (registry, events, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        events.Clear();   // drop the Start emission

        for (var i = 1; i <= 50; i++) operation.Report(i);

        Assert.Single(events);   // 50 reports, one frame
    }

    [Fact]
    public void The_last_progress_value_always_lands_via_the_trailing_emit()
    {
        var (registry, events, clock) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        for (var i = 1; i <= 50; i++) operation.Report(i);

        clock.Advance(TimeSpan.FromMilliseconds(101));   // close the window; nothing else is reported

        var last = Assert.IsType<OperationInfo>(events[^1].Payload);
        Assert.Equal(50, last.Progress);
    }

    [Fact]
    public void Lifecycle_transitions_are_never_throttled()
    {
        var (registry, events, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        operation.Report(10);
        events.Clear();

        operation.Complete();   // same window as the report above

        var info = Assert.IsType<OperationInfo>(Assert.Single(events).Payload);
        Assert.Equal(OperationStatus.Completed, info.Status);
    }

    [Fact]
    public void Finished_history_is_capped_and_running_work_is_never_pruned()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 2 });
        var running = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "FILES" }).Complete();

        var all = registry.GetAll();

        Assert.Equal(3, all.Count);                        // 1 running + 2 kept
        Assert.Contains(all, o => o.Id == running.Id);
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~OperationThrottleTests" -v minimal --nologo`
Expected: FAIL — every report emits (50 events, not 1); `TimeProvider` is not an option yet.

- [ ] **Step 4: Implement the throttle**

Per-operation throttle state on the entry (`LastEmitUtc`, `TrailingScheduled`), guarded by the same
lock:

```csharp
    private void Publish(Entry entry, bool immediate)
    {
        if (immediate) { EmitNow(entry); return; }

        var now = _options.TimeProvider.GetUtcNow();
        lock (_lock)
        {
            if (entry.Status != OperationStatus.Running) return;          // terminal already
            if (now - entry.LastEmitUtc < _options.ProgressInterval)
            {
                if (entry.TrailingScheduled) return;                       // one pending trailer, not N
                entry.TrailingScheduled = true;
                var delay = _options.ProgressInterval - (now - entry.LastEmitUtc);
                _ = TrailingEmitAsync(entry, delay);                       // fire-and-forget, guarded below
                return;
            }
            entry.LastEmitUtc = now;
        }
        EmitNow(entry);
    }

    private async Task TrailingEmitAsync(Entry entry, TimeSpan delay)
    {
        try
        {
            // Task.Delay's TimeProvider overload is what makes the FakeTimeProvider test deterministic —
            // a real 100 ms sleep in the suite would be both slow and flaky.
            await Task.Delay(delay, _options.TimeProvider).ConfigureAwait(false);
            lock (_lock)
            {
                entry.TrailingScheduled = false;
                entry.LastEmitUtc = _options.TimeProvider.GetUtcNow();
                if (entry.Status != OperationStatus.Running) return;       // a terminal emit already went
            }
            EmitNow(entry);
        }
        catch (Exception ex)
        {
            // An unguarded fire-and-forget body makes any fault an UNOBSERVED task exception.
            _options.Log?.Invoke($"[Shenora.Ipc] trailing progress emit failed: {ex.GetType().Name}");
        }
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~Operation" -v minimal --nologo`
Expected: PASS — both operation test classes.

- [ ] **Step 6: Prove the trailing emit is really under test**

Delete the `TrailingEmitAsync` call (leave the suppression), re-run: only
`The_last_progress_value_always_lands_via_the_trailing_emit` must fail, reporting the stale value.
Restore it.

- [ ] **Step 7: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc/Operations src/Directory.Packages.props tests/Shenora.Tests
git commit -m "feat(ipc): throttle operation progress to a configurable frame rate

Report-driven emissions collapse to one per ProgressInterval (default 100ms) with a
TRAILING emit so the final value always lands; lifecycle transitions are never
throttled. The batcher queues without coalescing, so an unthrottled per-item loop
shipped a frame per item. Finished history is capped (default 50)."
```

### Task 4: `ctx.Start` / `ctx.Run` — the handoff the sample hand-rolls today

**Files:**
- Modify: `src/Shenora.Ipc/IModuleContext.cs`, `src/Shenora.Ipc/ModuleContext.cs`,
  `src/Shenora.Ipc/BaseFacade.cs`
- Test: `tests/Shenora.Tests/Ipc/ModuleOperationTests.cs`

**Interfaces:**
- Consumes: `IOperationRegistry` (Task 2), `IModuleContext` (Task 1).
- Produces: `IModuleContext.Start(OperationOptions) → IOperation`,
  `IModuleContext.Run(OperationOptions, Func<IOperation, CancellationToken, Task>) → string`;
  `BaseFacade(ILogger?, IEventBus?, IOperationRegistry?)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shenora.Tests/Ipc/ModuleOperationTests.cs`:

```csharp
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class ModuleOperationTests
{
    private sealed class WorkFacade(IEventBus bus, IOperationRegistry registry, Func<IOperation, CancellationToken, Task> work)
        : BaseFacade(null, bus, registry)
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
            => Task.FromResult<object?>(new { operationId = context.Run(new OperationOptions { Kind = "BUILD" }, work) });
    }

    private static (WorkFacade Facade, OperationRegistry Registry) Build(Func<IOperation, CancellationToken, Task> work)
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });
        return (new WorkFacade(bus, registry, work), registry);
    }

    private static async Task<OperationInfo> WaitForTerminalAsync(OperationRegistry registry, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));   // BOUNDED — never bare
        while (!timeout.IsCancellationRequested)
        {
            var info = registry.GetAll().SingleOrDefault(o => o.Id == id);
            if (info is not null && info.Status != OperationStatus.Running) return info;
            await Task.Delay(10, timeout.Token);
        }
        throw new TimeoutException($"operation {id} never reached a terminal state");
    }

    [Fact]
    public async Task Run_returns_immediately_and_completes_in_the_background()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (facade, registry) = Build(async (op, ct) => { started.SetResult(); await release.Task; op.Report(90); });

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == id).Status);   // the route did NOT wait
        release.SetResult();
        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task A_running_operation_outlives_the_requests_cancellation_token()
    {
        // The trap this closes: capturing the REQUEST token kills long work the moment the page
        // navigates. The operation gets its OWN token; the request's is not linked.
        var release = new TaskCompletionSource();
        var (facade, registry) = Build(async (op, ct) => await release.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var requestLifetime = new CancellationTokenSource();

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"), requestLifetime.Token);
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;
        await requestLifetime.CancelAsync();

        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == id).Status);
        release.SetResult();
        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task A_cancelled_body_finishes_as_cancelled_not_as_a_fault()
    {
        var (facade, registry) = Build(async (op, ct) => await Task.Delay(Timeout.Infinite, ct));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        Assert.True(registry.Cancel(id));

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task An_expected_failure_keeps_the_apps_own_words()
    {
        var (facade, registry) = Build((op, ct) => throw new OperationException("BUILD_REJECTED", "step", "link"));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        var info = await WaitForTerminalAsync(registry, id);

        Assert.Equal(OperationStatus.Failed, info.Status);
        Assert.Equal("BUILD_REJECTED", info.Error!.Code);
        Assert.Equal("link", info.Error.Parameters!["step"]);
    }

    [Fact]
    public async Task Custom_events_work_with_no_operations_registered()
    {
        // The context is the MODULE's context, not an operations entry point: Publish is the primary,
        // always-available channel and must not acquire a dependency on the registry. A module that
        // only ever emits its own vocabulary is a first-class citizen.
        var bus = new EventBus();
        var seen = new List<EventMessage>();
        bus.SubscribeToAll(m => { seen.Add(m); return Task.CompletedTask; });
        var facade = new PublishOnlyFacade(bus);

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "ANY"));

        Assert.True(response.Success);
        Assert.Equal("ITEM_IMPORTED", Assert.Single(seen).Type);
    }

    private sealed class PublishOnlyFacade(IEventBus bus) : BaseFacade(null, bus)   // no registry at all
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            context.Publish("ITEM_IMPORTED", new { item = "a.txt" });
            return Task.FromResult<object?>(null);
        }
    }

    [Fact]
    public async Task An_unexpected_failure_never_leaks_its_message()
    {
        var (facade, registry) = Build((op, ct) => throw new InvalidOperationException("connection string secret"));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        var info = await WaitForTerminalAsync(registry, id);

        Assert.Equal(IpcErrorCodes.UnknownError, info.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), info.Error.Parameters!["exceptionType"]);
        Assert.DoesNotContain("secret", IpcJson.Serialize(info));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~ModuleOperationTests" -v minimal --nologo`
Expected: FAIL — `IModuleContext` has no `Run`.

- [ ] **Step 3: Extend the context**

Add to `IModuleContext` (doc every rule, they are the reason the primitive exists):

```csharp
    /// <summary>
    /// Start a tracked operation owned by this module and get its handle — for work whose lifecycle
    /// does not match <see cref="Run"/> 1:1 (a start outside the background body, several failure
    /// branches, a resumable session). This is the real primitive; <see cref="Run"/> is the sugar.
    /// </summary>
    IOperation Start(OperationOptions options);

    /// <summary>
    /// Start the operation, hand <paramref name="work"/> OFF to the background, and finish it:
    /// <c>Complete</c> on success, <c>Cancel</c> on <see cref="OperationCanceledException"/>,
    /// <c>Fail</c> otherwise. Returns the operation id IMMEDIATELY — a route that awaits long work
    /// blocks the dispatch, and the dispatch is on the UI thread.
    /// <para>
    /// The work gets the OPERATION's token, never the request's: work handed off outlives the
    /// request, and capturing the request token kills it the moment the page navigates.
    /// </para>
    /// </summary>
    string Run(OperationOptions options, Func<IOperation, CancellationToken, Task> work);
```

Implement on `ModuleContext` (registry absent → throw naming `AddShenoraOperations`, same shape as
`Publish`):

```csharp
    public string Run(OperationOptions options, Func<IOperation, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var operation = Start(options);
        _ = Task.Run(async () =>
        {
            try
            {
                // ConfigureAwait(false) is REQUIRED here and banned in the dispatch path: this body is
                // deliberately NOT the dispatch path, and capturing the UI context would put the work
                // back on the thread this exists to free.
                await work(operation, operation.CancellationToken).ConfigureAwait(false);
                operation.Complete();
            }
            catch (OperationCanceledException) { operation.Cancel(); }
            catch (OperationException expected) { operation.Fail(expected); }
            catch (Exception ex)
            {
                // The boundary rule, identical to MessageDispatcher's: the app never sees the message.
                Logger.LogError(ex, "Operation {Kind} in {Module} failed", options.Kind, Module);
                operation.Fail(IpcErrorCodes.UnknownError,
                    new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name });
            }
        });
        return operation.Id;
    }
```

Add `IOperationRegistry? operations = null` to `BaseFacade`'s constructor and pass it into
`ModuleContext`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~ModuleOperationTests" -v minimal --nologo`
Expected: PASS (6 tests).

- [ ] **Step 5: Prove the leak test can fail**

Change the last `catch` to `operation.Fail("UNKNOWN_ERROR", message: ex.Message)` and confirm
`An_unexpected_failure_never_leaks_its_message` FAILS on the planted secret. Restore. (That exact line
is the natural one to write when porting a host whose dispatcher did `$"{action} failed: {ex.Message}"`
— which is how this bypass was found the first time.)

- [ ] **Step 6: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc tests/Shenora.Tests/Ipc/ModuleOperationTests.cs
git commit -m "feat(ipc): ctx.Run — start long work, stream progress, never block the dispatch

Run owns the handoff, the guarded body, the terminal transition and the error mapping
(cancel is not a fault; an unexpected exception crosses as UNKNOWN_ERROR + type name).
The operation gets its OWN token, so it survives the page navigating away. ctx.Start
stays the primitive for shapes that do not match Run 1:1."
```

### Task 5: `OperationsFacade` + `AddShenoraOperations`

**Files:**
- Create: `src/Shenora.Ipc/Operations/OperationsFacade.cs`,
  `src/Shenora.Ipc/Operations/OperationServiceCollectionExtensions.cs`
- Test: `tests/Shenora.Tests/Ipc/OperationsFacadeTests.cs`

**Interfaces:**
- Consumes: `IOperationRegistry`, `BaseFacade`, `PayloadHelper`, `IpcServiceCollectionExtensions`.
- Produces: `OperationsFacade(IOperationRegistry registry, OperationRegistryOptions? options = null)`
  serving `LIST` / `CANCEL` / `CLEAR_FINISHED` / `RESUME` on the module named by
  `OperationRegistryOptions.ModuleName`; `IServiceCollection.AddShenoraOperations(Action<OperationRegistryOptions>?)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shenora.Tests/Ipc/OperationsFacadeTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class OperationsFacadeTests
{
    private static (OperationsFacade Facade, OperationRegistry Registry) Build()
    {
        var registry = new OperationRegistry(new EventBus(),
            new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });
        return (new OperationsFacade(registry), registry);
    }

    [Fact]
    public async Task LIST_answers_the_client_stores_snapshot()
    {
        var (facade, registry) = Build();
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        registry.Start("SCAN", new OperationOptions { Kind = "FILES" });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "LIST", new { module = "DEPLOY" }));

        Assert.True(response.Success);
        var operations = Assert.IsAssignableFrom<IReadOnlyList<OperationInfo>>(response.Data);
        Assert.Equal("DEPLOY", Assert.Single(operations).Module);
    }

    [Fact]
    public async Task CANCEL_cancels_by_operation_id()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "CANCEL", new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task An_unknown_type_gets_the_frameworks_NO_HANDLER_shape()
    {
        var (facade, _) = Build();

        var response = await facade.HandleMessageAsync(IpcRequests.Create("OPERATIONS", "NOPE"));

        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }

    [Fact]
    public void AddShenoraOperations_registers_one_registry_and_maps_the_facade()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddShenoraOperations();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IOperationRegistry>(),
                    provider.GetRequiredService<IOperationRegistry>());          // singleton
        Assert.Contains(provider.GetServices<IModuleFacade>(), f => f is OperationsFacade);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~OperationsFacadeTests" -v minimal --nologo`
Expected: FAIL — `OperationsFacade` does not exist.

- [ ] **Step 3: Implement the facade**

A normal `BaseFacade` — reads its payload through `PayloadHelper`, ends its switch with
`throw UnknownType(request)`. `ModuleName` comes from the injected `OperationRegistryOptions` so the
request module and the event module are one renameable string. `LIST` takes optional `module`/`scope`
filters, `CANCEL`/`RESUME` take `operationId`, `CLEAR_FINISHED` takes nothing.

- [ ] **Step 4: Implement the DI extension**

```csharp
    /// <summary>
    /// Register the operation registry + its facade. OPT-IN: an app with no long-running work should
    /// pay nothing for it, and D21 says the kit ships the primitive, never the product.
    /// </summary>
    public static IServiceCollection AddShenoraOperations(
        this IServiceCollection services, Action<OperationRegistryOptions>? configure = null)
```

Register `OperationRegistryOptions` (configured), `IOperationRegistry` → `OperationRegistry` as a
singleton, and the facade through the existing `AddModuleFacade<OperationsFacade>()` so it is mapped by
`AddMessageDispatcher` like any other module.

- [ ] **Step 5: Run the tests + the whole suite**

Run: `dotnet test Shenora.slnx -v minimal --nologo`
Expected: PASS — everything, including the duplicate-module guard (an app naming its own module
`OPERATIONS` now throws at composition, which is the intended diagnosis).

- [ ] **Step 6: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc/Operations tests/Shenora.Tests/Ipc/OperationsFacadeTests.cs
git commit -m "feat(ipc): OperationsFacade — LIST/CANCEL/CLEAR_FINISHED over the registry

LIST is the client store's snapshot source (a late mounter cannot replay a stream);
CANCEL is the app-level cancel route the contract always prescribed and never shipped.
Opt-in via AddShenoraOperations."
```

### Task 6: Interrupted + resume

**Files:**
- Modify: `src/Shenora.Ipc/Operations/OperationRegistry.cs`, `OperationsFacade.cs`
- Test: `tests/Shenora.Tests/Ipc/OperationResumeTests.cs`

**Interfaces:**
- Produces: `IOperationRegistry.RegisterInterrupted(string module, OperationOptions options) → string`,
  `IOperationRegistry.RequestResume(string id) → bool`, facade type `RESUME`.

⚠ **Single-app provenance** (design §4.2). Everything else in Stage 2 clears the two-app bar; this does
not. It is five members of pure mechanism — a state, an opaque token, an event — and the app owns the
checkpoint and the resume entrypoint. If a 1.0 audit trims surface, cut this first.

- [ ] **Step 1: Write the failing test**

```csharp
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class OperationResumeTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        return (new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero }), events);
    }

    private static OperationOptions Checkpoint(string payload) =>
        new() { Kind = "ANALYSIS", Resumable = true, ResumePayload = payload, Scope = "p1" };

    [Fact]
    public void RegisterInterrupted_announces_a_resumable_entry_from_the_apps_checkpoint()
    {
        var (registry, _) = Build();

        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));

        var info = registry.GetAll().Single();
        Assert.Equal(id, info.Id);
        Assert.Equal(OperationStatus.Interrupted, info.Status);
        Assert.Equal("session-7", info.ResumePayload);
    }

    [Fact]
    public void Re_announcing_the_same_checkpoint_does_not_stack_entries()
    {
        var (registry, _) = Build();

        var first = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        var second = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));

        Assert.Equal(first, second);
        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void RequestResume_emits_for_the_owning_module_and_drops_the_offer()
    {
        var (registry, events) = Build();
        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("SCAN", payload.GetProperty("module").GetString());
        Assert.Equal("session-7", payload.GetProperty("resumePayload").GetString());
        Assert.Empty(registry.GetAll());   // the resumed op registers a FRESH operation when it restarts
    }

    [Fact]
    public void An_interrupted_entry_is_not_prunable_history()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 1 });
        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "X" }).Complete();

        Assert.Contains(registry.GetAll(), o => o.Id == id);   // a pending resume OFFER, not history
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~OperationResumeTests" -v minimal --nologo`
Expected: FAIL — `RegisterInterrupted` does not exist.

- [ ] **Step 3: Implement**

`RegisterInterrupted` requires `Resumable = true` and a non-empty `ResumePayload` (throw
`ArgumentException` naming the missing one); dedupes on `(module, kind, resumePayload)` among
`Interrupted` entries. `RequestResume` returns false unless the entry is `Interrupted` **and**
`Resumable`, then removes it and emits `OperationEvents.ResumeRequested` with
`{ operationId, module, kind, resumePayload, scope }`. `PruneHistory` must skip interrupted+resumable
entries.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~Operation" -v minimal --nologo`
Expected: PASS.

- [ ] **Step 5: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc/Operations tests/Shenora.Tests/Ipc/OperationResumeTests.cs
git commit -m "feat(ipc): interrupted + resume offers for crash-resumable operations

The app owns the checkpoint and the resume entrypoint; the kit holds the state, the
opaque token and the RESUME_REQUESTED event. Re-announcing the same checkpoint is
deduped, and a pending offer is never pruned as history."
```

### Task 7: The client — wire types, the store, and the mirror tripwire

**Files:**
- Create: `src/Shenora.React/src/operations.ts`, `src/Shenora.React/src/operations.test.ts`
- Modify: `src/Shenora.React/src/index.ts`, `tests/Shenora.Tests/Ipc/WireMirrorTests.cs`
- Test: both of the above

**Interfaces:**
- Consumes: `createShenoraStore`, `eventBus`, `getBridge` from the existing client.
- Produces: `OperationStatuses` (const object), `OperationInfo` (interface), `useShenoraOperations`.

- [ ] **Step 1: Write the failing client test**

Create `src/Shenora.React/src/operations.test.ts` (follow the existing `store.test.ts` harness for the
fake bridge/bus):

```ts
import { describe, expect, it } from 'vitest';
import { createOperationsStore } from './operations.js';

const info = (over: Partial<{ id: string; status: string; progress: number }>) => ({
  id: 'op-1', module: 'DEPLOY', kind: 'PUSH', status: 'running', ...over,
});

describe('operations store', () => {
  it('loads the snapshot on first subscribe', async () => {
    const { store, bridge } = harness([info({}), info({ id: 'op-2' })]);
    store.subscribe(() => {});
    await bridge.settled();

    expect(Object.keys(store.getState().byId)).toEqual(['op-1', 'op-2']);
  });

  it('folds an update by id — last write wins', async () => {
    const { store, bus } = harness([info({})]);
    store.subscribe(() => {});

    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: 40 }));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ progress: 80 }));

    expect(store.getState().byId['op-1'].progress).toBe(80);
    expect(Object.keys(store.getState().byId)).toHaveLength(1);
  });

  it('exposes running work separately from finished history', async () => {
    const { store, bus } = harness([]);
    store.subscribe(() => {});
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({}));
    bus.emit('OPERATIONS', 'OPERATION_UPDATED', info({ id: 'op-2', status: 'completed' }));

    expect(store.getState().running.map((o) => o.id)).toEqual(['op-1']);
  });
});
```

- [ ] **Step 2: Run it and watch it fail**

Run (from `src/Shenora.React`): `npm test -- operations`
Expected: FAIL — module not found.

- [ ] **Step 3: Write `operations.ts`**

Mirror the host types, then build the store on the shipped primitive — no new mechanism:

```ts
/** Mirrors Shenora.Ipc.OperationStatus (camelCase on the wire — IpcJson's string-enum policy). */
export const OperationStatuses = {
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Cancelled: 'cancelled',
  Interrupted: 'interrupted',
} as const;

export type OperationStatus = (typeof OperationStatuses)[keyof typeof OperationStatuses];

export interface OperationLabel { text?: string; key?: string; parameters?: Record<string, string>; }

export interface OperationInfo {
  id: string; module: string; kind: string; scope?: string;
  status: OperationStatus; progress?: number;
  title?: OperationLabel; detail?: OperationLabel;
  error?: IpcError; cancellable: boolean; resumable: boolean; resumePayload?: string;
  startedAt: string; finishedAt?: string;
}

export const useShenoraOperations = createShenoraStore<OperationsState, OperationsActions>('OPERATIONS', {
  initial: { byId: {} },
  snapshot: { type: 'LIST', apply: (s, data) => ({ byId: index(data as OperationInfo[]) }) },
  on: { OPERATION_UPDATED: (s, p: OperationInfo) => ({ byId: { ...s.byId, [p.id]: p } }) },
  actions: ({ post }) => ({
    cancel: (operationId: string) => post('CANCEL', { payload: { operationId } }),
    clearFinished: () => post('CLEAR_FINISHED'),
    resume: (operationId: string) => post('RESUME', { payload: { operationId } }),
  }),
});
```

Expose `running` / `finished` as derived selectors, not as duplicated state. Export everything from
`index.ts`.

- [ ] **Step 4: Add the mirror tripwire**

In `tests/Shenora.Tests/Ipc/WireMirrorTests.cs`, add — using the existing `ClientSource` +
`ParseConstObject` helpers:

```csharp
    [Fact]
    public void Every_operation_status_exists_on_both_sides()
    {
        var host = Enum.GetNames<OperationStatus>()
            .Select(n => JsonNamingPolicy.CamelCase.ConvertName(n))
            .ToHashSet(StringComparer.Ordinal);
        var client = ParseConstObject(ClientSource("operations.ts"), "OperationStatuses").Values.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(host);      // parser self-check: a regex that matched nothing must not pass
        Assert.NotEmpty(client);
        Assert.Equal(host, client);
    }
```

- [ ] **Step 5: Run both sides**

Run: `npm test -- operations` (in `src/Shenora.React`) — Expected: PASS.
Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~WireMirrorTests" -v minimal --nologo` — Expected: PASS.

- [ ] **Step 6: Break the tripwire on purpose**

Add `Paused` to the C# `OperationStatus` and confirm the mirror test fails naming `paused`; then remove
it. Do the same by deleting a TS entry. Both directions must fail — a one-directional mirror is how
`SCOPE_REQUIRED` survived two phases.

- [ ] **Step 7: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.React tests/Shenora.Tests/Ipc/WireMirrorTests.cs
git commit -m "feat(react): useShenoraOperations — host-backed operation state, plus a mirror tripwire

Snapshot via LIST then fold OPERATION_UPDATED by id, on the shipped store primitive.
The status set is now pinned across C#/TS in both directions."
```

### Task 8: Rewrite the sample's SLOW route and re-measure the UI-thread claim

**Files:**
- Modify: `samples/Shenora.Sample.Desktop/SampleFacade.cs`,
  `samples/Shenora.Sample.Desktop/Program.cs` (register `AddShenoraOperations`),
  `samples/Shenora.Sample.Web/src/App.tsx`
- Test: the live probe (`devtools`), not a unit test

**Interfaces:** consumes everything from Tasks 1–7.

- [ ] **Step 1: Replace the hand-rolled block**

`SampleFacade`'s `stream` branch becomes the shape the kit now ships. Keep the `block` branch and its
"DELIBERATELY THE WRONG SHAPE" comment — the sample's job is to show both.

```csharp
                // The right shape, now one call: Run owns the handoff, the guarded body, the terminal
                // transition and the token. What the sample used to hand-roll here — Task.Run, a catch
                // that existed only to stop an unobserved fault, ConfigureAwait(false) and a hardcoded
                // "SAMPLE" literal — is the kit's job as of 0.2.0 (D23).
                var operationId = context.Run(
                    new OperationOptions { Kind = "SLOW", Cancellable = true, Title = new OperationLabel(Text: "Slow work") },
                    async (operation, ct) =>
                    {
                        const int steps = 6;
                        for (var step = 1; step <= steps; step++)
                        {
                            await Task.Delay(totalMs / steps, ct).ConfigureAwait(false);
                            operation.Report(step * 100 / steps,
                                new OperationLabel(Text: $"step {step}/{steps} (onUiThread: {Application.MessageLoop})"));
                        }
                    });
                return new { Mode = mode, RanOnUiThread = onUiThread, OperationId = operationId };
```

- [ ] **Step 2: Point the web sample at the operations store**

Replace the `SLOW_PROGRESS`/`SLOW_DONE` reducers in `App.tsx` with `useShenoraOperations`, rendering
progress and a Cancel button — the sample doubles as the adoption example, so it should show the
supported path.

- [ ] **Step 3: Build and run the sample**

Run: `node devtools/dev.mjs verify` then `node devtools/dev.mjs sample`
Expected: the window renders, the streamed run shows advancing progress and the 1 Hz tick keeps moving.

- [ ] **Step 4: Re-measure the UI-thread claim**

Re-run the responsiveness probe used for v0.1.0 (`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`; see
`docs/2026-07-31-shenora-oneway-ipc-design.md` §7 for the recorded numbers and the two vacuous readings
it caught).
Expected: streamed = **0 unresponsive samples**, blocking still stalls. If streamed is not 0, the
refactor put work back on the UI thread — stop and fix, do not adjust the claim. Record the new numbers
in `local/PROJECT_NOTES.md`.

- [ ] **Step 5: Prepare the commit (ask before running it)**

```bash
git add samples
git commit -m "refactor(sample): stream SLOW through ctx.Run and the operations store

20 lines of ceremony become one call. Re-measured with the WM_NULL probe: streamed
stays at 0 unresponsive samples, blocking still stalls — the claim survives the
refactor rather than being re-asserted."
```

---

# STAGE 3 — The base-agnostic channel

### Task 9: `NotificationPump`

**Files:**
- Create: `src/Shenora.Ipc/NotificationPump.cs`, `src/Shenora.Ipc/NotificationPumpOptions.cs`
- Test: `tests/Shenora.Tests/Ipc/NotificationPumpTests.cs`

**Interfaces:**
- Consumes: `IEventBus`, `IpcNotification`, `IpcNotificationBatch`, `IpcJson`.
- Produces: `NotificationPump { TimeSpan FlushInterval; bool IsOpen; int PendingCount; void Enqueue(IpcNotification); void Open(); void Close(); bool TryDrainBatch(out string? json); void Dispose(); }`.

- [ ] **Step 1: Write the failing test**

```csharp
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class NotificationPumpTests
{
    private static NotificationPump Pump(NotificationPumpOptions? options = null) => new(options ?? new());

    private static IpcNotification Note(string module = "APP", string type = "TICK", string? scope = null) =>
        new() { Module = module, Type = type, Scope = scope };

    [Fact]
    public void Nothing_is_delivered_before_the_client_is_ready()
    {
        using var pump = Pump();
        pump.Enqueue(Note());

        Assert.False(pump.TryDrainBatch(out _));
        Assert.Equal(1, pump.PendingCount);      // buffered, NOT dropped
    }

    [Fact]
    public void Opening_the_gate_delivers_everything_buffered_since_construction()
    {
        using var pump = Pump();
        pump.Enqueue(Note(type: "FIRST"));
        pump.Open();

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("FIRST", json);
        Assert.Equal(0, pump.PendingCount);
    }

    [Fact]
    public void Closing_the_gate_buffers_again_instead_of_draining_into_a_dead_page()
    {
        using var pump = Pump();
        pump.Open();
        pump.Close();
        pump.Enqueue(Note());

        Assert.False(pump.TryDrainBatch(out _));
    }

    [Fact]
    public void The_queue_is_bounded_and_drops_the_OLDEST()
    {
        using var pump = Pump(new NotificationPumpOptions { MaxQueued = 2 });
        pump.Enqueue(Note(type: "ONE"));
        pump.Enqueue(Note(type: "TWO"));
        pump.Enqueue(Note(type: "THREE"));
        pump.Open();

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.DoesNotContain("ONE", json);
        Assert.Contains("THREE", json);
    }

    [Fact]
    public void A_filter_decides_per_channel_what_is_delivered()
    {
        using var pump = Pump(new NotificationPumpOptions { Filter = n => n.Scope == "w1" });
        pump.Open();
        pump.Enqueue(Note(scope: "w1"));
        pump.Enqueue(Note(scope: "w2"));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("w1", json);
        Assert.DoesNotContain("w2", json);
    }

    [Fact]
    public void One_unserializable_payload_does_not_lose_the_rest_of_its_batch()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(new IpcNotification { Module = "APP", Type = "BAD", Payload = new Throws() });
        pump.Enqueue(Note(type: "GOOD"));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("GOOD", json);
        Assert.DoesNotContain("BAD", json);
    }

    private sealed class Throws { public string Boom => throw new InvalidOperationException("nope"); }

    [Fact]
    public void Bus_events_arrive_as_notifications_and_stop_after_dispose()
    {
        var bus = new EventBus();
        var pump = Pump(new NotificationPumpOptions { EventBus = bus });
        pump.Open();
        bus.Emit("APP", "FROM_BUS");
        pump.Dispose();
        bus.Emit("APP", "AFTER_DISPOSE");

        Assert.Equal(1, pump.PendingCount);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction_naming_the_option()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationPump(new NotificationPumpOptions { MaxQueued = 0 }));

        Assert.Contains(nameof(NotificationPumpOptions.MaxQueued), error.Message);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~NotificationPumpTests" -v minimal --nologo`
Expected: FAIL — `NotificationPump` does not exist.

- [ ] **Step 3: Move the logic out of `WebViewIpcBridge`**

This is a **move, not a rewrite**. Take the queue (`ConcurrentQueue` + `Interlocked` count + drop-oldest),
the gate flag, `TryBuildBatchJson`'s guarded per-notification serialization, the option validation and
the bus `SubscribeToAll` handler from `src/Shenora.WebView2/WebViewIpcBridge.cs` **with their comments**
— those comments are the post-mortems (P5.5 H2/H3) and are the most valuable part of the port. Add the
filter at enqueue. `Enqueue` stays callable from any thread; `TryDrainBatch` is called by the base's tick
and returns false when the gate is closed or nothing is pending.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~NotificationPumpTests" -v minimal --nologo`
Expected: PASS (8 tests).

- [ ] **Step 5: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.Ipc tests/Shenora.Tests/Ipc/NotificationPumpTests.cs
git commit -m "feat(ipc): NotificationPump — the transport-neutral outbound half

Buffer, cap, gate, batch and guarded serialize move to Shenora.Ipc so a non-WinForms
base inherits the fixed bugs instead of re-earning them, plus a per-channel delivery
filter (every bridge subscribes to ALL events today, so two windows both see everything).

Design: docs/2026-08-01-shenora-communication-core-design.md §5"
```

### Task 10: `WebViewIpcBridge` becomes the WinForms/WebView2 adapter

**Files:**
- Modify: `src/Shenora.WebView2/WebViewIpcBridge.cs`
- Test: `tests/Shenora.Tests/WebView2/WebViewIpcBridgeTests.cs` (existing — must stay green)

**Interfaces:**
- Consumes: `NotificationPump` (Task 9).
- Produces: unchanged public surface plus `WebViewIpcBridgeOptions.NotificationFilter`.

- [ ] **Step 1: Delegate to the pump**

The bridge keeps: the timer (a `Forms.Timer` ticks on the UI thread — the only thread allowed to touch
`CoreWebView2`, so the flush needs no marshalling), `WebMessageReceived`, `ContentLoading` → `Close()`,
the `READY` handshake → `Open()`, `ProcessFailed` → `Close()`, `PostWebMessageAsString`, the dispatcher
wiring and the lifetime CTS. It forwards `NotificationInterval` → `FlushInterval`,
`MaxQueuedNotifications` → `MaxQueued`, `EventBus`, `Log` and the new `NotificationFilter` → `Filter`.
Option NAMES on `WebViewIpcBridgeOptions` do not change — an adopter must not have to rename anything.

- [ ] **Step 2: Run the existing bridge suite unchanged**

Run: `dotnet test Shenora.slnx --filter "FullyQualifiedName~WebViewIpcBridge" -v minimal --nologo`
Expected: PASS with **no test edits**. Editing a bridge test here is the signal that behaviour moved,
not just code — stop and re-read the diff.

- [ ] **Step 3: Full gate**

Run: `node devtools/dev.mjs verify`
Expected: PASS. Review the `Shenora.WebView2.txt` baseline diff — expected: only
`NotificationFilter` added.

- [ ] **Step 4: Prepare the commit (ask before running it)**

```bash
git add src/Shenora.WebView2 tests/Shenora.Tests
git commit -m "refactor(webview2): reduce WebViewIpcBridge to its WinForms/WebView2 adapter

Everything transport-neutral now lives in NotificationPump; the bridge keeps the timer,
the WebView2 events, the ready gate wiring and postMessage. Options keep their names,
and gain NotificationFilter. No bridge test changed."
```

### Task 11: Docs, rules, and the release surface

**Files:**
- Modify: `docs/ARCHITECTURE.md`, `docs/ADOPTION.md`, `CHANGELOG.md`, `README.md`,
  `src/Shenora.React/README.md`, `.claude/knowledge/ipc-contracts.md`, `TASKS.md`,
  `docs/ROADMAP.md`, `local/PROJECT_NOTES.md`

- [ ] **Step 1: `ARCHITECTURE.md`** — add `IModuleContext`, the operations cluster and `NotificationPump`
  to the `Shenora.Ipc` inventory; note that `WebViewIpcBridge` is now an adapter over the pump.

- [ ] **Step 2: `.claude/knowledge/ipc-contracts.md`** — add the invariants earned here, each with its
  reason: publish goes through the context so a module string cannot drift; an operation's token is its
  own, never the request's; progress emission is throttled with a trailing emit because the batcher does
  not coalesce; an operation failure obeys the same no-raw-text boundary as a response; the pump owns
  gate/cap/batch and a base owns only the tick.

- [ ] **Step 3: `ADOPTION.md`** — Stage 3 gains the operations path; state plainly that
  `RouteMessageAsync` changed shape and what the one-line migration is. Also close the standing
  drop-zone finding while here: `DropZoneManager` is Stage-1-adoptable standalone (it depends only on
  `Shenora.Core`, the control and a `Form`), and only `DropZoneFacade`/`useDropZone` need Stage 3.

- [ ] **Step 4: `CHANGELOG.md`** — a `### Breaking` section naming the `RouteMessageAsync` change and
  the one-line fix, plus `### Added` for operations, the store and the filter.

- [ ] **Step 5: `TASKS.md` / `docs/ROADMAP.md` / `docs/task-archive.md`** — move the two closed adopter
  findings out of `TASKS.md` into the archive (a done entry MOVES, it is not checked off in place), and
  record the milestone in `ROADMAP.md`.

- [ ] **Step 6: Retire the design docs' status lines** — mark
  `docs/2026-08-01-shenora-communication-core-design.md` implemented and fold its as-built parts into
  `ARCHITECTURE.md` per the doc inventory's "Nature" column.

- [ ] **Step 7: Final gate + release prep**

Run: `node devtools/dev.mjs verify` then `node devtools/dev.mjs doctor`
Expected: both clean. Bump `<VersionPrefix>` to `0.2.0` in `src/Directory.Build.props` **only** (the
single version source; npm/README are synced by `doctor --fix` — never hand-edit them).

- [ ] **Step 8: `/phase-review` before the final commit** — the standing rule: a review subagent over
  the full diff, fix its real findings, then commit.

---

## Self-review notes

- **Spec coverage:** design §3 → Task 1; §4.1–4.2 → Tasks 2, 6; §4.3 → Tasks 2, 7; §4.4 → Task 3;
  §4.5 → Task 4; §4.6 → Tasks 5, 7; §5 → Tasks 9, 10; §8 (breaking + migration) → Tasks 1, 11;
  §9 verification → the per-task probes plus Tasks 8 and 11.
- **Deliberate ordering:** `IModuleContext` ships in Task 1 with `Publish` only and grows `Start`/`Run`
  in Task 4, so each task is independently testable rather than blocked on the registry.
- **Known risk:** Task 1 touches every facade in the repo at once. That is unavoidable — an abstract
  signature change has no partial state — which is why it is the whole of Stage 1 and why its own tests
  land first.
