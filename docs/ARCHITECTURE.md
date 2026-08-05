# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/2026-07-30-shenora-design.md`; this file
records only what EXISTS.)

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`, the same way README.md's
     headline is. Don't hand-edit it — and don't date this line either: the release workflow owns
     the version, so a hand-written one is stale the moment a release cuts. Everything ELSE in this
     file dates its claims instead of versioning them, for the same reason. -->
## Current state — **v0.9.1 published**; P1–P7 complete (v0.1.0 shipped 2026-07-31)

Eight packable NuGet packages + `@shenora/react` on npm. **Five are the SHELL set, organised BY
PLATFORM since 0.5.0 (D37)**: `Core`, `Ipc`, `Windows` (the three old Windows ids merged), `Android`
and `iOS`. All five ship from ONE Windows runner — 0.5.0 published only the first four because `pack`
skipped iOS on the mistaken belief that it needed a Mac, and 0.5.1 corrected that. **Three are
OPTIONAL feature packages hanging off `Core`, not layers under it**: `Media` (v0.9.0), and — new since
2026-08-05, so not in v0.9.1 — `IO` and `IO.Compression` (D48). Since the summary below was written, P5.5
landed the
D19/D20 re-layer (`WebView2` → `WinForms`; portable contracts + `IUiDispatcher` in `Core`, enforced by
a `net10.0` sample that turns red if a Windows type reaches app logic), P5.6 added native caption
buttons, P6 readied adoption (`docs/ADOPTION.md`, and six capability gaps found and closed), and P7
stabilised: every public and protected member documented with CS1591 as an error, the login RECIPE
moved out of the library to the sample (D21/D22 amended), and the release pipeline hardened. The
narrative is `docs/ROADMAP.md` `## Done`; the task-level record is `docs/archive/tasks.md`.

**2026-08-01 — the communication core** (D23, `docs/2026-08-01-shenora-communication-core-design.md`,
implemented; drafted under the name "0.2.0" and released later that day as part of v0.3.0): the module
contract now carries the EVENT path — `IModuleContext` (`Publish`/`Start`/`Run`/`Logger`) is the
second parameter of `BaseFacade.RouteMessageAsync`, the one breaking change this release makes. A new
operations cluster in `Shenora.Ipc` tracks long-running work (id, status, progress, cancel-by-id,
throttled progress emission) as mechanism only — what an operation IS stays app-defined. The
transport-neutral half of the outbound notification pipeline moved out of `WebViewIpcBridge` into
`Shenora.Ipc`'s `NotificationPump`, so `WebViewIpcBridge` is now a thin WinForms/WebView2 adapter over
it (D16's "the seam, not the package" applied to the host half). `@shenora/react` gained
`useShenoraOperations`/`createOperationsStore`, a host-backed store mirroring the pattern
`createShenoraStore` already established. (No 0.2.0 release exists — `CHANGELOG.md`
`## 0.2.0 — never released` has the account. **Dates, not version numbers, are how this file marks
time**, precisely because that story exists: a version is assigned by the release workflow, so any
version written into prose is a guess about the future.)

**2026-08-02 — `Shenora.Core`'s mission-scheduling + filesystem-claims layer**
(`docs/2026-08-02-shenora-mission-scheduling-design.md`): one scheduler whose key spaces are pluggable, so
a filesystem operation planner (paths conflict by containment) and a job queue (lanes admit N) are the
same engine — the EXECUTION half of long-running work, composing with `Shenora.Ipc`'s operations
cluster (the REPORTING half) rather than merging with it. Surface below; adopter-facing mapping in
`docs/ADOPTION.md`.

P2 delivered the core host (builder, WinForms runner, WebView2 hosting + serving, samples). P3
delivered the full IPC stack (wire contract, dispatcher + facades, event bus, postMessage
transport, `@shenora/react` client, live round-trip). P4 delivered the native desktop surface:
the scoped-container router + standard IPC composition, frameless chrome + frontend window
commands, STA dialogs/shell/clipboard/interaction services, drag-drop zones + `useDropZone`
(+ per-monitor DPI handling), secondary windows + tray. P5 added the `Shenora.Windows`
package: the one browser-configuration path, a bounded LIFO render-session pool, the login-window
stack (persistent per-account profiles, silent refresh, clear-on-logout), and co-browse streaming
— all proven live in the sample.

```
Shenora.slnx
├── src/
│   ├── Shenora.Core        net10.0          — deps: M.E.DependencyInjection (impl, D17), M.E.Logging.Abstractions
│   ├── Shenora.Ipc         net10.0          — deps: Shenora.Core
│   ├── Shenora.Media       net10.0          — deps: NONE, and that is the design. A LEAF: it holds
│   │                                          decisions, not plumbing, so every type is a pure function
│   │                                          over its own data — the per-stream playability planner
│   │                                          (D42) and the best-effort probe-result shape it reads.
│   │                                          Its own package because a demuxer or image codec is real
│   │                                          shipped bytes and EVERYTHING references Core, so an app
│   │                                          that never touches media must not pay for one (D40).
│   │                                          net10.0, so app logic compiles against it on any shell —
│   │                                          enforced by the Sample.Logic tripwire (D41).
│   │                                          Ships no codec list and no engine: policy is the app's.
│   │                                          ⚠ It holds NO serving code, and there are no
│   │                                          Media.Android/.iOS packages any more (D45). Serving bytes
│   │                                          to a page is interception, which configures a WEBVIEW and
│   │                                          is therefore a shell capability — see IWebViewInterceptor
│   │                                          in Core, implemented once per shell. So <video>/<audio>/
│   │                                          <img> over local files need no media package at all, and
│   │                                          this one is what an app adds to DECIDE about a file the
│   │                                          platform cannot decode.
│   ├── Shenora.IO          net10.0          — deps: Shenora.Core
│   │                                          The file-operation ENGINE: the journalled update queue,
│   │                                          cross-process path leases, the manifest/diff pair and the
│   │                                          staged updater. Left Core on 2026-08-05 (D48) — 1,700
│   │                                          lines of machinery that only an app which MUTATES a file
│   │                                          tree needs, sitting in the one package everything
│   │                                          references. The edge points at Core (not the reverse)
│   │                                          because every type here logs through Core's AppCallback;
│   │                                          that is also what decided the leftovers — Files/
│   │                                          FileReplacement stayed because Core's own
│   │                                          IFileDialogs.SaveAsync default calls Files.BeginReplace,
│   │                                          PathClaims because it is scheduling vocabulary, and
│   │                                          IFileLockInspector because it is a portable CONTRACT with
│   │                                          a per-platform implementation, like IFileDialogs (D19/D20).
│   ├── Shenora.IO.Compression net10.0       — deps: Shenora.IO
│   │                                          Archives, safely: containment-checked extraction with
│   │                                          size/count limits, and ZipUpdateSource — the IUpdateSource
│   │                                          over one or more ZIPs. Its own package because zip is ONE
│   │                                          format and the next (7-Zip, rar) needs a native engine
│   │                                          that must not reach an app using neither.
│   ├── Shenora.Launcher/   (C++17, CMake — NOT a NuGet package yet)
│   │                                          The native APPLY step, which is the one part of staged
│   │                                          updates that cannot be done in .NET: it runs when the
│   │                                          runtime may be absent and must replace files the app
│   │                                          holds open. A LIBRARY (manifest parse, overlay, tracked
│   │                                          removals, the platform seam) plus a TEMPLATE `main.cpp`
│   │                                          an app copies and edits four constants in — the split
│   │                                          D50 took from §0's measurement of two donor launchers.
│   │                                          One tree for Windows + Linux: std::filesystem everywhere,
│   │                                          Win32/POSIX behind include/shenora/platform.hpp, and BOTH
│   │                                          compiled on every build so neither can rot.
│   │                                          It re-hashes NOTHING — `ready.json` exists only when the
│   │                                          C# side verified the whole stage, and re-verifying would
│   │                                          duplicate a rule that can drift.
│   │                                          Gated by `.github/workflows/launcher.yml` (win-x64 +
│   │                                          linux-x64) running the conformance harness against the
│   │                                          built binary, NOT by `dev.mjs verify`, which has no C++
│   │                                          toolchain and deliberately does not grow one.
│   ├── Shenora.Launcher.Native (packaging only — compiles NOTHING)
│   │                                          B4b: puts the per-RID launcher binaries the `launcher`
│   │                                          CI matrix builds (win-x64 + linux-x64) into one nupkg
│   │                                          under runtimes/{rid}/native/, alongside the C++ library
│   │                                          sources and template under launcher-src/ so an adopter
│   │                                          can either use the stock binary or build their own.
│   │                                          It consumes DOWNLOADED artifacts because the binaries
│   │                                          come from two different toolchains on two different
│   │                                          runners — no single `dotnet pack` can produce both — so
│   │                                          `dev.mjs pack` skips it unless they are staged, and the
│   │                                          csproj ERRORS rather than shipping an empty runtimes/.
│   │                                          ⚠ It is the one packable project with NO managed
│   │                                          surface, so it declares <NoManagedSurface>true</> and
│   │                                          MetadataSurfaceTests exempts it BY THAT DECLARATION —
│   │                                          delete the line and the baseline gate turns back on.
│   ├── Shenora.Windows     net10.0-windows  — deps: Shenora.Core, Shenora.Ipc, Microsoft.Web.WebView2
│   │                                          The Windows shell, WHOLE (merged 2026-08-02 from
│   │                                          WinForms + WebView2 + WebView2.Sessions). Three folders
│   │                                          keep the areas legible: Shell/ (primitives — bootstrap,
│   │                                          frameless chrome, tray, secondary windows, window state,
│   │                                          STA dialogs/clipboard, the UI-thread dispatcher),
│   │                                          WebView/ (hosting, serving, IPC bridge, drop zones,
│   │                                          window commands), Sessions/ (render pool, interactive,
│   │                                          streaming). The old split protected a WinForms-without-
│   │                                          WebView2 consumer that cannot exist in a React-in-a-
│   │                                          webview kit; D19's package edge is now internal.
│   ├── Shenora.Mobile/     (SOURCE, no csproj) — the mobile shell's shared code; NOT a package, and
│   │                                          there is deliberately no Shenora.Mobile on nuget.org.
│   │                                          Ipc/ Threading/ Services/ Hosting/, compiled INTO both
│   │                                          platform packages by Shenora.Mobile.props.
│   ├── Shenora.Android     net10.0-android  — deps: Shenora.Core, Shenora.Ipc, Microsoft.Maui.Controls
│   ├── Shenora.iOS         net10.0-ios      — same deps, same source, and it builds on WINDOWS: a
│   │                                          net10.0-ios LIBRARY needs only the maui-ios workload,
│   │                                          never Xcode. Only an iOS APP needs a Mac.
│   │                                          The SECOND shell, one package per platform. Peers of
│   │                                          WinForms+WebView2, not layers on them: neither
│   │                                          references either. Thin by construction — the IPC
│   │                                          substrate is already portable, so this is the
│   │                                          HybridWebView adapter, a UI dispatcher and the
│   │                                          Essentials-backed Core contracts.
│   │                                          Both PROVEN on device/simulator, and neither needed a
│   │                                          single #if. Divergence goes in each project's
│   │                                          Platforms/ folder (MAUI SDK includes per TFM, verified
│   │                                          in BOTH directions for these single-TFM libraries).
│   │                                          Since 2026-08-03 there IS some: SaveAsync, because SAF
│   │                                          and UIDocumentPicker have nothing in common. It is a
│   │                                          `partial` method, so a THIRD platform cannot compile
│   │                                          until it decides what save means (CS8795), rather than
│   │                                          inheriting a stub that refuses at runtime.
│   │                                          BOTH are in the solution and gated on
│   │                                          every run — LIBRARY, not app, so no Mac is involved.
│   └── Shenora.React/      @shenora/react    — peer: react >=18; build tsc, test vitest
├── tests/
│   └── Shenora.Tests       net10.0-windows  — xunit; references the four leaf src projects (Core transitively)
└── samples/                                 — never packable; the e2e subject (dev.mjs sample/vite/shot/wgc/click)
    ├── Shenora.Sample.Desktop  net10.0-windows — the reference composition (builder → UseWinForms →
    │                                            prewarm → WebViewHost + provider + SplashPanel +
    │                                            frameless OptimizedForm + WindowCommandFacade +
    │                                            DropZoneManager/Facade + SecondaryWindows + TrayIcon +
    │                                            SampleFacade → MessageDispatcher → WebViewIpcBridge,
    │                                            1 Hz IEventBus tick source); embeds wwwroot
    │                                            (built by the web sample, gitignored)
    ├── Shenora.Sample.Logic    net10.0         — the PORTABILITY PROOF (H4.3): one facade that picks
    │                                            a file, reads the clipboard and opens a URL through
    │                                            the Core contracts only (IUrlLauncher, NOT the
    │                                            Windows IShellLauncher). Plain net10.0 with no
    │                                            Windows reference, referenced by the desktop sample
    │                                            and in the solution — so a Windows type dragged into
    │                                            a portable contract turns the build RED instead of
    │                                            leaving D20's portability merely asserted. Also the
    │                                            SCHEDULER's worked example: SCHEDULE_DEMO submits
    │                                            four items (two contending for one path, two
    │                                            disjoint) under a capacity-2 lane, and
    │                                            MissionOperationObserver is the ~35-line IMissionObserver
    │                                            adapter that reports them through Shenora.Ipc's
    │                                            operation registry — execution, reporting and the
    │                                            seam between them, all with no Windows reference.
    │                                            CHAIN_DEMO adds the composition an adopter builds:
    │                                            two MissionChains whose staging steps overlap and
    │                                            whose file landings go through one IFileUpdateQueue
    │                                            partition (proven live 2026-08-02 — both staged at
    │                                            the same millisecond, both landed through the queue)
    └── Shenora.Sample.Web      Vite + React    — consumes @shenora/react (file:), port 3900, builds
                                                 into the desktop sample's wwwroot; page-owned title
                                                 bar (WindowCommands + useWindowMaximized), notifyReady,
                                                 useShenoraQuery echo, useShenoraEvent tick, useDropZone
                                                 target, secondary-window controls, dev interceptor
                                                 (the e2e subject)
```

- Version: single `<VersionPrefix>` in `src/Directory.Build.props`; npm + README synced by
  `dev.mjs pack`/`doctor --fix`.
- Package metadata (authors, license MIT, repo URL, snupkg symbols, SourceLink, README-in-package)
  is shared in `src/Directory.Build.props`; each csproj adds only `PackageId` + `Description`.
- Central package management: `src/Directory.Packages.props` (root file is an import shim).

## Public surface

Gated by the API-surface baseline tests (`tests/Shenora.Tests/Api/Baselines/*.txt` — tracked;
drift writes a gitignored `.actual` and fails; copy over the baseline only for intentional
changes, noting them in `CHANGELOG.md`).

- `Shenora.Core` — `ShenoraEnvironment` (the ONE dev-mode detection: `DOTNET_ENVIRONMENT`/
  `ASPNETCORE_ENVIRONMENT` or the `.dev` marker; base directory); `AppRootArgument`
  (`--app-root` launcher-arg parsing); `ShenoraPaths`/`ShenoraPathsOptions` (the portable on-disk
  layout authority: explicit-root → root env var → libs-parent detection → base dir; data env
  var for child-process sharing; ensure-created `DataArea`s); the application builder —
  `ShenoraApplication(+Options)` (`CreateBuilder` resolves `--app-root` → paths → environment;
  `Run()` executes the registered runner; `Start()`/`Stop()` are the lifecycle-hook sequence itself —
  one owner, both idempotent, driven directly by a host whose platform owns the loop and used
  internally by every runner so a second shell cannot drift; `Dispose` owns the provider),
  `ShenoraApplicationBuilder` (`Services`, `AddModule`, `OnStarting`/`OnStopping`, build-once),
  `IShenoraModule` (per-feature service registration), `IShenoraRunner` (the host-loop seam),
  `UseHeadless`/`HeadlessRunnerOptions` (the no-UI runner: hooks → block on a stop token or
  SIGINT/SIGTERM → ordered shutdown, so `Run()` no longer needs a Windows package. NOT for a host
  whose platform owns the loop — a MAUI activity cannot honour "blocks until shutdown" and brings
  its own runner), `IShenoraLifecycleHook` (DI-registered start/stop participation; runners invoke post-gate);
  the in-process event bus — `EventMessage` (`{id, module, type, scope?, payload?, timestamp}`,
  host-side; the wire form is `Shenora.Ipc`'s notification envelope), `IEventBus`/`EventBus`
  (`"*"` wildcards + per-subscription match cache; unscoped subscriptions see every scope and
  global events reach scoped subscribers; handler failures logged + isolated; `EmitAsync` awaits
  every handler, `Emit` is the fire-and-forget twin for a synchronous caller; auto-registered
  by `Build()` via `TryAdd` — replaceable).
- **`Shenora.Core`'s resource-interception layer (2026-08-04, D45)** — how a page gets bytes the platform will
  not give it, portable and identical on all three shells. `WebViewResourceRequest`/`Response`/
  `WebViewByteRange` are the exchange (relocated here in 0.8.0 by D2a); on top of them:
  `IWebViewInterceptor` (`RangeDelivery` + `Use(middleware)` → `IDisposable`), the
  `WebViewResourceMiddleware`/`WebViewResourceHandler` delegates, and `WebViewResourcePipeline` — the
  registry and composition itself, shared by every shell so the back-to-front chain build, the
  copy-on-write array and reference-identity removal exist ONCE and are unit-testable with no webview.
  **MIDDLEWARE, not a handler list,** because the cross-cutting concerns are the point (containment, a
  cache, a metric, a log of what a payload decoded to) — the same shape `IMessageDispatcher` already is
  for messages, applied to bytes.
  `WebViewRangeDelivery` (`Sliced`/`Unsliced`) is a property of the INTERCEPTION rather than of the
  content: Android's webview applies the `Range` start to whatever body it receives, WebView2's and iOS's
  send it verbatim (D44, measured on each).
  Serving files is `WebViewFileOptions` + `WebViewFiles.ResolveContained`/`Serve` +
  `interceptor.UseFiles(…)` — an extension over the interceptor so `RangeDelivery` is READ from the
  platform and cannot be passed in wrong. Fail-closed: no allowed roots means nothing is servable, `..`
  is refused before the filesystem is touched, roots are compared with a separator appended, and every
  refusal is the same 404 as a missing file so nothing probes for existence. `WebViewContentTypes`
  (public here since D45) answers the MIME type, and `DerivedCacheKey` keys anything derived from a
  source file by identity + length + mtime rather than by path.
  **This is what makes `<video>`, `<audio>` and `<img>` work with no media package at all** — a file the
  platform cannot decode simply errors in the element, and deciding what to do about that is
  `Shenora.Media`'s job as a further middleware.
- **`Shenora.Core`'s mission-scheduling layer (0.3.0, `Missions/` + `Io/`)** — the EXECUTION half of
  long-running work, portable and with no DI, storage or reporting dependency of its own:
  `IMissionScheduler`/`MissionScheduler(+Options)` (`SubmitAsync`, `Lane(name)`, `GlobalLane`, `PendingCount`/
  `RunningCount`, `IsActive(MissionKey)`, `Snapshot()`, `Reevaluate()`, `RecoverAsync(rehydrate)`;
  `IAsyncDisposable` — dispose cancels what is queued and awaits what is running). **Admission** is
  event-driven, evaluated on submit and on each completion (no worker thread, no polling), and an item
  starts only when no in-flight AND no EARLIER-PENDING item holds a conflicting claim (rule 2 is
  fairness — it is what stops a queued item starving behind newer disjoint work) and every named lane
  has a permit; the lock covers bookkeeping only, never the body.
  **Claims** — mutual exclusion without the caller taking a lock: `MissionClaim` (`Scope`/`Key`/`Mode` +
  `Exclusive`/`Shared` factories), `ClaimMode`, and the `IClaimScope` seam supplying each key space's
  conflict rule — `FlatClaimScope` (equal only) and `NestedClaimScope` (equal or containment, tested at
  a SEPARATOR boundary so `a/b` contains `a/b/c` but not `a/bc`; `Normalize` collapses repeated
  separators and trims a trailing one, once, at submit). A request declares its whole claim SET, so
  there is no per-key lock object to leak and no acquisition ORDER to get wrong — the two bugs the
  family's hand-rolled versions had.
  **Lanes** — capacity, orthogonal to exclusion: `ILane` (`Capacity` settable LIVE, floor 1 and no
  ceiling; lowering swallows permits as items finish rather than killing in-flight work; `Hold`/`Release`
  re-entrant, the mechanism a load governor actuates with — the kit ships no probe, hysteresis or
  debounce policy), `MissionLane(Name, Permits = 1)` for a lane that is a BUDGET rather than a slot count.
  Every request also draws one permit from the GLOBAL lane (`GlobalLaneCapacity`, 0 = `clamp(cores-1,
  1, 4)`), which is the total concurrency bound — so a named lane runs at `min(its capacity, the bound)`,
  and `ILane.EffectiveCapacity` is what reports that (`Capacity` keeps the value you REQUESTED, and a
  request above the bound is logged rather than clamped or thrown). The bound is itself a lane —
  `IMissionScheduler.GlobalLane`, addressable as `MissionScheduler.GlobalLaneName` and resolving to the
  same instance from `Lane(name)` or from a mission declaring it — so it is live-resizable and holdable,
  which is what lets a load governor RESTORE and not only throttle (before it existed the bound was
  `init`-only and unreachable, so a throttled lane could never recover past its startup value).
  (`GlobalLaneCapacity` was renamed from `DefaultLaneCapacity`, a documented break with no alias kept.)
  **Definition vs execution — the split the rest of the layer is built on.** `MissionDefinition` is
  WHAT should run (`Run` + optional `Commit`: setting `Commit` makes `Run` run exactly ONCE and retries
  only the commit, so a failed cheap replace never recompresses; plus `Claims`, `Lanes`, `Priority`,
  `Key`, `Retry`, `Durable`, `Kind`, `Payload`). `MissionExecution` is ONE specific run of it
  (`MissionId`, `Kind`, `Priority`, `QueuedUtc`, `Sequence`, `Attempt`, `IsRunning`) — the single value
  handed to the body, to all three observer callbacks, to the policy, and back out of `Snapshot()`,
  where four differently-shaped types used to sit. It carries NO `CancellationToken`: the body takes
  its token as a second parameter (`Func<MissionExecution, CancellationToken, Task>`), matching the
  rest of the kit and keeping an execution a pure value that is safe to hold in a diagnostics view.
  Also `MissionKey` (dedup identity — a matching submission completes against the live item, body
  once), `RetryPolicy`(+`None`) (3 × 500 ms × attempt, `IOException` only), and `MissionResult`/
  `MissionOutcome` (`Completed`/`Failed`/`Cancelled`/`Deduplicated`; a failing body is REPORTED, not
  thrown — a batch submitter must survive one bad item — with `ThrowIfFailed()` for callers who prefer
  exceptions, while caller bugs still throw at submit).
  **The app's own scheduling rules** — `IMissionPolicy` (`Compare` = what next, `ShouldStart` = when) +
  `PriorityMissionPolicy` (priority, then FIFO — plain FIFO with no priorities set). Consulted ONLY about
  items that already passed admission, which is the structural reason a custom policy can delay work
  but never make conflicting work overlap or bypass a lane; a throwing policy is treated as "not now"
  rather than wedging the scheduler.
  **Observation + durability** — `IMissionObserver` (`OnQueued`/`OnStarted`/`OnFinished`, each guarded
  through `AppCallback`; the seam for metrics, tracing, or binding execution to a progress registry
  without `Core` learning what an operation is), `MissionSchedulerState` (what the scheduler is doing
  right now, for the policy);
  `IMissionQueueStore`/`MissionRecord`/`MissionState` + `RecoveryPolicy` (`Requeue`/`Fail`/`Discard`, defaulting to
  `Requeue` for `Queued` and **`Fail` for `Running`** — work found running after a crash may be what
  killed the process, and re-running it turns one crash into a boot loop) and `RecoveryPolicyFor`.
  Recovery is an explicit `RecoverAsync` with an app `rehydrate` delegate, never implicit: a delegate
  does not serialize, and that same delegate is why the kit ships no handler-registry-by-type.
  **Chains (multi-step missions)** — `MissionChain.Sequence(kind, params MissionStep[])` returns an
  ordinary `MissionDefinition`, so the scheduler gains NO concept of dependencies: a chain is ONE
  queue entry whose steps run in order, sharing an `IMissionChainContext`
  (`StepIndex`/`StepName`/`StepCount` + a `Get`/`Set` bag). `MissionStep` carries an optional
  per-step `Claims` and `RetryPolicy` — the claims are unioned onto the chain and held for its whole
  life (taking the STRONGER mode where steps disagree, so a read-then-write chain holds the key
  exclusively), and the retry repeats only that step, never the ones before it. A failing step fails
  the chain; cancelling cancels the chain. The context is IN-MEMORY only: a durable chain carries
  state in `Payload`, because an arbitrary object graph is what the kit cannot serialize for an app.
  **`Io/PathClaims`** (static) — `Scope` (a `NestedClaimScope` over `Path.DirectorySeparatorChar`,
  case-insensitive on Windows only), `Exclusive`/`Shared` (claims on the `"path"` scope, `ScopeName`),
  `Canonical` (absolute + separator-normalized, so two spellings of one location are one key) and
  `IsContained(root, candidate)` (the containment guard for anything mapping caller input to a file —
  resolves `..` first, boundary-tested, so `C:\data-old` is not inside `C:\data`).
  Naming is `Mission*` and deliberately not `Operation*`: `Shenora.Ipc` owns the reporting vocabulary,
  and reusing the word would blur the one distinction the design rests on. It was `Work*` until
  2026-08-02 — too common a word to own or grep, while `Task*` would collide with the BCL.
  **Three as-built facts worth recording, because a reader of the design doc or the XML would expect
  otherwise:** (1) an unknown LANE does NOT throw — it is created at the default capacity on first
  mention, so only an unregistered claim SCOPE is a submit-time error (the trap is a misspelled name
  silently costing the exclusivity that was configured; two XML remarks used to claim the lane threw,
  corrected and pinned by `An_unseen_LANE_name_is_created_at_the_default_capacity_rather_than_throwing`);
  (2) the design's `IFileSystem` and atomic-replace helper were never shipped — `PathClaims` is the
  whole of the scheduler's `Io/` half, and the write-to-temp-then-replace SHAPE is what `Run`/`Commit`
  models; (3) nothing
  in `Shenora.Ipc` implements `IMissionObserver`, so wiring execution to the operation registry is the
  app's own ~35-line adapter — `samples/Shenora.Sample.Logic/MissionOperationObserver.cs` is the worked
  example — and `Shenora.Core` stays free of any reporting dependency either way (D19/D20). That
  adapter's one non-obvious rule: its operations must be `Cancellable = false` unless the app wires
  cancellation itself, because the registry's `Cancel` signals the OPERATION's own token while the
  work observes the one handed to `SubmitAsync`.
- **`Shenora.IO` — the file-operation engine (its own package since 2026-08-05, D48; all of it shipped
  in `Shenora.Core` before that).** Portable `net10.0`, and it pairs with the scheduler above without
  depending on it: missions compute in parallel, this lands their results one at a time.
  **The file-update queue (2026-08-02), independent of the scheduler.**
  `IFileUpdateQueue`/`FileUpdateQueue(+Options)` serializes filesystem MUTATIONS so missions can
  compute in parallel and land one at a time: `ApplyAsync(FileUpdate)` completes when that update has
  landed. A `FileUpdate` is an ordered `FileChange` list (`Replace`, `Move`, `Delete`,
  `CreateDirectory` — a closed hierarchy), a `Partition` (null = one global writer; different
  partitions land concurrently), a `RetryPolicy` applied per change, and a `FileAtomicity`:
  `PerChange` stops at the first failure and reports its index, `AllOrNothing` undoes applied changes
  in REVERSE — which is why a delete under it is STAGED (moved aside, really removed only once the
  whole set lands), a delete being the one change that cannot be undone from nothing. Backups and
  aside-copies are siblings of the target, so every move is same-volume. `FileUpdateResult` reports
  rather than throws, like `MissionResult`. **Crash-atomicity is opt-in:** supply
  `FileUpdateQueueOptions.Journal` (`IFileUpdateJournal`, with the shipped `FileUpdateJournal` — one
  `WriteThrough` JSON file per in-flight update) and the undo plan is durable BEFORE each change, with
  `RecoverAsync()` resolving what a previous run left. `FileUpdateStage` decides which way: an update
  interrupted while `Applying` is rolled back, one interrupted while `Committing` is FINISHED, since
  rolling that back would undo a success. Undo is DATA (`FileUndoStep`/`FileUndoKind`) rather than
  closures — that is what a journal requires, and it is why each change is planned before it is
  applied. Without a journal the same `AllOrNothing` covers a failed change only. The internal `IFileOperations` seam exists so serialization and rollback
  ORDER are provable with a probe instead of with sleeps; the kit still ships no public filesystem
  abstraction.
  **Cross-process locking, the two halves of a problem claims cannot reach — and they live in
  DIFFERENT packages.** A `MissionClaim` excludes missions inside one process; these cover the rest.
  `IPathLocker`/`IPathLease`
  + `FilePathLocker(+Options)` are advisory leases as lock FILES in a directory of the app's own
  (never the managed tree — an app frequently does not own the folder it manages), opened
  `FileShare.Read` + `DeleteOnClose` so the OS releases them when a process dies, keyed by a hash of
  the canonical path so two spellings are one lease. `FileUpdateQueueOptions.Locker` makes the queue
  take them for every path an update touches, in sorted order so two overlapping updates cannot
  deadlock. That covers PARTICIPANTS — a second instance, or a child process the app spawns while
  holding leases. For a process that will never take one (a game, a mod loader, antivirus, another
  app editing the same folder), exclusion is impossible and the answer is `IFileLockInspector`:
  `WhoHolds(path)` → `FileLockHolder`s, surfaced on `FileUpdateResult.Holders`, so an opaque
  `IOException` becomes a name. Empty means "cannot tell", never "nobody". Over a share, leases work
  provided the lock directory is ON the share, and a crash-released lease returns when the SMB session
  times out rather than instantly.
  ⚠ **`IFileLockInspector` + `FileLockHolder` stayed in `Shenora.Core`** when the rest moved out (D48),
  and the asymmetry is the layering rule rather than an oversight: "who holds this file open?" has a
  genuinely different answer per platform (Windows asks the Restart Manager), so it is a portable
  CONTRACT with a shell implementation — exactly like `IFileDialogs` — and a shell must be able to
  implement it without referencing an optional feature package. Advisory leases went the other way for
  the opposite reason: lock files are portable, so contract and implementation ship together.
  **`UpdateManifest`, `ManifestFile`, `ManifestDiff` (2026-08-02)** — the staged-update
  changeset, and the FIRST piece of `docs/2026-08-02-shenora-app-update-design.md` to ship.
  `ManifestFile` is `{Path, Size, Sha256}` (the triple two sibling apps arrived at independently);
  `UpdateManifest` is `{Version, GeneratedAt?, Files}` with camelCase `Parse`/`ToJson` matching what
  they already emit; `ManifestDiff.Compute(installed, release)` yields `Added`/`Updated`/`Removed` +
  `DownloadBytes`. Pure data and a pure function — no downloader, no release source, no applier;
  those are the app's or the native step's. Two comparison rules are load-bearing and
  sabotage-verified: paths normalize separators and case (or the same file is "added" on every check
  and never converges) and hashes compare case-insensitively (or a generator's hex casing reports
  EVERY file changed). `Removed` is tracked paths only, never a directory sweep — user data lives in
  the same tree.
  ⚠ An empty RELEASE manifest legitimately removes everything, so a manifest that failed to load must
  never reach `Compute`; validating it is the caller's job — and that caller is `UpdateStage`, below.
  **`UpdateStage` (+`Options`, `+Status`)** — the staging half of the two-phase update. The app
  writes downloaded files into `StagedDirectory`; `CommitAsync` hashes every file the manifest lists,
  rejects anything staged that the manifest does NOT list, and writes `ready.json` **last**, so the
  marker's existence IS the promise that the stage is complete and verified and an applier need not
  re-check. Verification covers all three failure modes since 2026-08-03 — truncation (listed, missing),
  tamper (present, wrong hash) and **intrusion** (present, unlisted); the third was added because
  `ApplyAsync` overlays the staged TREE rather than the manifest, so an unlisted file was written into
  the install root having been verified by nothing. `UpdateStageOptions.IsUnindexed` exempts what a
  given release legitimately carries unindexed (a predicate, not a list — the answer belongs to whatever
  generated the manifest), and the kit's own `manifest.json` is always exempt because `FetchAsync` puts
  it there itself. `Begin()` clears a previous attempt (its
  leftovers would otherwise verify as part of the next one), `GetStatus()` reads only the marker and
  never throws, and an EMPTY manifest is refused here — the guard `ManifestDiff` defers. No
  downloader and no release source — those are the app's. `IUpdateSource` is the seam (two methods,
  no implementation shipped) and `FetchAsync` is the download-and-stage phase: diff, fetch only the
  CHANGED files, commit. Because only the changeset is staged, `CommitAsync` verifies the manifest of
  what is IN the stage; the full release manifest rides along as `manifest.json` so the applier can
  compute removals and so overlaying it makes the new installed baseline.
  **`UpdateStageOptions.BaselinePath` relocates that baseline** (2026-08-04, filed by the first adopter).
  Null = `{installRoot}/manifest.json`, unchanged for an install tree, where the baseline belongs with the
  thing it describes. It exists because that is only true of an install TREE: the adopter's targets are
  deploy INPUTS whose aggregate content hash decides what gets re-uploaded, so a per-release
  `manifest.json` inside one changed the hash on every release even when the payload was byte-identical —
  breaking their invariant that a part's content is a pure function of SOURCE, never of build HISTORY.
  `ApplyAsync` now writes the baseline EXPLICITLY and always excludes it from the overlay, so the
  configured and default cases are one code path rather than a containment test; it appears in
  `UpdateOutcome.Written` only when it really landed inside the tree.
  **`ApplyAsync` + `UpdateOutcome`** — the apply pass, portable .NET rather than native: overlay,
  remove what the new manifest dropped, clear. Run it from OUTSIDE the tree it overlays (a launcher
  at `{root}/` over `{root}/app/`), which is what makes self-exclusion guards unreachable rather than
  handled. An unreadable or empty staged manifest BLOCKS the apply, because removals are
  "installed minus release" and a manifest that failed to load would delete everything just written.
  A self-contained app needs no native code; a framework-dependent one still wants a launcher to
  bootstrap the runtime and call this.
- **`Shenora.IO.Compression` — archives, safely (2026-08-05).** `ZipExtraction.ExtractTo` +
  `ExtractionLimits`/`ExtractionResult`: every entry is containment-checked against the destination
  **plus a separator** (so `data-evil` is not a child of `data`), a refused entry is SKIPPED and NAMED
  rather than throwing, and total-bytes / entry-count limits (1 GiB / 100k) throw — the zip-bomb bound,
  fatal because a partial extraction that stopped quietly would leave the caller believing it had
  everything. `ZipUpdateSource` implements `Shenora.IO`'s `IUpdateSource` over one or MORE archives
  (a release is commonly one zip per part under a single manifest), indexed at construction, refusing a
  path carried by two archives rather than last-wins. ⚠ Non-seekable streams are refused up front
  (`ZipArchive` reads the central directory from the END), and it is not thread-safe because
  `ZipArchive` is not.
- `Shenora.Windows` — `DpiHelper` (BaseDpi, `SystemScale`, `ScaleFromDeviceDpi`, pure `Scale` +
  internal-element helpers); `WindowState`/`WindowStateOptions`/`IWindowStateStore`/
  `JsonFileWindowStateStore`/`WindowStateManager` (logical-px persistence, physical restore,
  off-screen recovery — pure `ToPhysical`/`ToLogical`/`IsVisible` cores); `SingleInstanceGuard`
  (per-scope FNV-1a mutex + activate broadcast, fail-open; `TryAcquire(TimeSpan)` = the
  `--restarted` widened-wait handoff with abandoned-mutex recovery); `WinFormsBootstrap(+Options)`
  + `UnhandledExceptionReport/Source` (one-call WinForms init + the three global exception
  channels with crash-log callback and last-resort dialog); the host composition —
  `UseWinForms(WinFormsHostOptions)` on `WinFormsHostExtensions`, with `SingleInstanceHostOptions` (gate scope/restart
  argument/wait/losing-launch callback) and `WindowStateHostOptions` (store factory + options),
  backed by an internal runner (gate → bootstrap → starting hooks → form factory → window state →
  activate-message filter → loop → reverse-order stopping hooks → release); `SplashPanel(+Options)`
  (startup marquee overlay, app-chosen colors — headless per D13, debounced recenter);
  `OptimizedForm(+Options)` (double-buffered base + `WndProcHook` seam; optional frameless
  chrome: WM_NCCALCSIZE top-only caption removal, manual work-area maximize —
  `IsAppMaximized`/`MaximizedChanged` are the truth, not `WindowState` — DWM
  dark-mode/border/corner handling, top resize strip, `ApplyChromeTheme` runtime resync; all
  colors parameterized); native caption buttons (P5.6) — `NativeCaptionButtons` cuts the cluster
  reported to `SetCaptionButtons` out of the window region of every covering child so the OS routes
  real input to the form (Snap Layouts), and the form paints it with app-supplied
  `CaptionButtonColors`; `CaptionButtonStateChanged` remains for the un-clipped mode where the app
  draws them itself. **Frameless chrome is deliberately a FIXED TYPE rather than an attachable
  behaviour (D24)** — the window style belongs in `CreateParams` at handle creation, and attaching it
  later would need `SetWindowLong`+`SWP_FRAMECHANGED` as a second mechanism in the one area where a
  green unit suite has twice been wrong; the accepted limit (an app that cannot change its form base
  cannot take the chrome, though it can still drive the window commands) is recorded there. The
  drawing itself lives in the internal `CaptionButtonRenderer` (0.2.0 design pass): pure input →
  pixels — palette fallback, glyph selection, the DPI-scaled icon font — so it is unit-tested with no
  STA thread, no handle and no pump, while everything that answers a window message stayed in the
  form. The native services, TryAdd-registered by `UseWinForms` —
  `IFormInteraction`/`FormInteraction` (main-window registry, runner-wired; nested modal
  blocking), `IFileDialogs`/`FileDialogs(+Options)` + `FileDialogOptions`/`Filter`/`Result` +
  `IFileDialogPathStore` seam (dedicated-STA open/folder/save dialogs, owner-handle z-order,
  per-key directory memory; failures throw). `IFileDialogs` carries two universal members with default
  implementations the desktop shell inherits unchanged — `OpenReadAsync` (the host resolves a picked
  handle) and, since 2026-08-03, `SaveAsync(options, write)` (the host picks AND writes, ATOMICALLY via
  `Files.BeginReplace`, so a failed or cancelled save leaves the previous file untouched). The
  path-returning `SaveFileAsync`/`OpenFolderAsync` are documented as the DESKTOP-flavoured pair (D35):
  they promise an addressable location, which mobile has no expression of.
  `IShellLauncher`/`ShellLauncher` (reveal/open-dir/
  http-https-`OpenUrl`/launch — Win11 handle-leak fixes), `IClipboardService`/`ClipboardService`
  (STA text + image-file ops); `SecondaryWindows(+SecondaryWindowOptions)` (named windows on
  own-STA-thread pumps, per-name `IWindowStateStore` geometry, activate-on-existing,
  non-blocking close); `TrayIcon(+Options)`/`TrayMenuColors` (NotifyIcon + composed menu,
  double-click restore, close-to-tray, optional app-colored renderer);
  `RestartManagerLockInspector` — the Windows implementation of `Shenora.Core`'s
  `IFileLockInspector`, answering "who is holding this file?" through the Restart Manager API (the one
  an installer uses to say "close these applications"). Here rather than in `Core` because it is
  Win32; returns empty for a remote holder over a share, because that answer only exists on the server.
- `Shenora.Windows` — `BrowserArguments` (the measured Chromium display-optimization preset;
  single-occurrence feature lists; dev CDP-args append); `WebViewEnvironment(+Options)`
  (runtime presence probe, idempotent prewarm, thread-affine shared environment +
  per-STA-thread creation for secondary windows); `PrewarmWebView2` on `WebView2BuilderExtensions`
  (prewarm as a deferred starting hook — stays behind the single-instance gate);
  `WebViewHost(+Options)` (the ONE place a WebView2 is configured: env + ensure under a 25 s
  init-timeout guard, settings-hardening preset + `ConfigureSettings` escape hatch, dev/prod
  `Navigate` with actionable errors, sync virtual-host serving of the packaged bundle vs
  deferred off-UI-thread app schemes (`WebViewDeferredScheme` — a full request/response seam:
  `WebViewResourceRequest` (uri/method/headers) in, `WebViewResourceResponse` (status/headers/
  content STREAM) out, with `WebViewByteRange.TryParse` + `PartialContent`/`RangeNotSatisfiable`
  so a served resource can be SOUGHT and a large one is never buffered whole), disk-folder hosts
  (`WebViewFolderMapping`), escaped `InjectedGlobals` + family scripts, and the four default
  event policies: new-window→system browser, downloads canceled, permissions denied except
  allowlist, guarded renderer-crash reload); `IWebViewResourceProvider` seam +
  `EmbeddedResourceProvider(+Options)` (assembly+prefix, lazy-with-warmup, file-fallback mode,
  path→name lookups) — the no-cache-HTML / immutable-hashed-asset header policy lives in
  `Shenora.Core`'s `WebViewContentTypes` (public since D45, because every shell's interceptor needs a
  MIME map) and is applied by `WebViewHost` when it serves; **`WebViewHost.Interceptor`** — the desktop
  half of the D45 contract (`WebView2Interceptor`, `RangeDelivery = Sliced`, measured by the sample's
  `InterceptorProbe`), wired into the host's ONE `WebResourceRequested` subscription rather than a second
  one, sharing the page's own origin with the bundle: a path the bundle does not contain falls through to
  the pipeline (`WebViewBundleServing.TryServe`) instead of 404ing, and in DEV an extra filter is
  registered for the dev-server origin because that is where the page lives then; `WebViewIpcBridge(+Options)`
  (the postMessage transport: UI-thread async-interleaved request dispatch into an
  `IMessageDispatcher`, `IsHandleCreated`-guarded `BeginInvoke` posts, bounded drop-oldest
  notification queue buffering from construction + ~50 ms batch flush after the reserved
  `SHENORA`/`READY` client handshake, optional `IEventBus` wildcard forwarding,
  `SendNotification`, `OnClientReady` per-handshake callback); `WindowCommandFacade` + `WindowCommandOptions`
  (module `WINDOW`: MINIMIZE/TOGGLE_MAXIMIZE/CLOSE/IS_MAXIMIZED/START_DRAG/START_RESIZE +
  optional SET_THEME; `ToggleMaximize`/`IsMaximized` delegate seams for frameless apps — here
  because the commands arrive over the bridge and need Ipc, which WinForms doesn't reference);
  the drop-zone stack — `DropZoneManager(+Options)` (transparent overlays over page elements
  capture real OS paths incl. background drags; non-blocking UI marshalling, activation sync,
  DOM occlusion checks, per-monitor `DeviceDpi` conversion + `DpiChanged` re-apply; zones cleared on
  `ContentLoading` so overlay lifetime follows the DOCUMENT, never the ready handshake, which used to
  race the page that was registering; events on `IEventBus`) + `DropZoneFacade` (module `DROP_ZONE`:
  REGISTER/UPDATE/UNREGISTER/SHOW).
- `Shenora.Core` also owns `AppCallback` — the ONE guard for invoking app-supplied code from a place
  where an escaping exception is fatal rather than catchable (a UI-thread event handler, a timer tick, a
  posted body, a dispose path). Public because `Shenora.Windows`, `Shenora.Windows` and
  `Shenora.Windows` all consume it and a `ProjectReference` grants no `internal` access (D19/D20).
- `Shenora.Windows` — auxiliary browser sessions (D14: browser work outside the
  app's own UI, kept out of the core hosting package): `SessionBrowser(+Options)` (the ONE
  auxiliary-WebView2 configuration path — per-profile environment, quiet-start +
  background-throttling-off arguments, settings hardening, `RequestFilter` block seam,
  init-timeout guard, `GetHtmlAsync`, and — since 2026-08-03 — `VirtualHost` + `ResourceProvider` +
  `FolderMappings`, so a session can serve the app's OWN packaged bundle and "co-browse / off-screen
  render MY UI" works in a packaged build (E1/D38; before it, a session reached network-reachable URLs
  only). The two halves are both-or-neither, refused at initialization; the app's `RequestFilter` is
  consulted BEFORE the bundle, from ONE `WebResourceRequested` handler; the serving itself is
  `WebViewBundleServing`, the same internal implementation `WebViewHost` uses);
  `RenderSessionPool(+Options)`/`RenderSession`/
  `SessionApiCall` (bounded LIFO-pooled off-screen sessions: lease → navigate (http/https-only
  + `NavigationGuard` SSRF seam + `NavigationTimeout`)/execute/read/DevTools/network+message taps →
  dispose returns to the pool; capacity waits queue, a creation failure or a cancelled-during-init
  creation releases the slot and tears down, every operation is capped by `OpTimeout` and an
  abandoned one POISONS its instance, a poisoned instance or a `ResetTimeout`-expired about:blank
  reset DISCARDS it rather than re-pooling; one shared hidden host in runtime mode,
  visible cascaded windows in dev mode; internal `SessionEnvironmentCache` gives the pool ONE
  `CoreWebView2Environment` for its profile — owner-scoped, not static, because a live environment
  holds the profile's folder lock and would defeat `ClearProfile`); internal `SessionLog` (the
  package's one guarded-diagnostic path — an app `ILogger` is an app callback); the human-in-the-loop
  stack — `InteractiveSession(+Options)` (a modal, driver-run browser window over
  per-provider/per-sub-account persistent profiles — the sub scoping is a security boundary;
  busy-serialized with exactly-once completion incl. the token fallback, the user's close HELD so the
  driver gets a final read, silent-refresh off-screen shape, static `ClearProfile` so discarding a
  session is real), `SessionController` (guarded `NavigateAsync`,
  `ExecuteScriptAsync`, origin-scoped `GetCookiesAsync`, `OnMessage`/`OnDownload`/
  `OnNewWindow`/`OnNavigation` taps, `FitToBox` CSS→physical, `SetLoading`, idempotent
  `Reveal`, `WindowClosed`), `SessionResult` (+ `ThrowIfFailed` bridging into the IPC error
  contract)/`SessionErrorCodes`, and `CookieLoginFlow(+Options)`/
  `SessionCookie`/`DownloadHit` (the one opt-in REFERENCE DRIVER, which keeps its scenario name on
  purpose — D22: fresh-set auth-cookie detection against a
  pre-navigation baseline, so a stale cookie never captures, not even on close; separate
  `CookieReadUrl` origin; `ReadBlob`); and `StreamingSession(+Options)`/`SessionViewport`
  (an off-screen browser that STREAMS what it renders and ACCEPTS synthetic input: screencast JPEGs
  into a bounded latest-wins `ChannelReader<SessionFrame>` — each frame carrying the CSS viewport it
  depicts — `DispatchAsync(SessionInput, …)` for typed input (`SessionPointerInput`/`SessionWheelInput`/
  `SessionTextInput`/`SessionKeyInput`/`SessionViewportInput` + `SessionPointerAction`, plus
  `SessionInput.TryParseLegacyJson` as the adoption shim), 1:1 device-metrics viewport mirroring,
  fraction coordinates, and `OnEnded`/`SessionEnded`/`SessionEndReason` as the exactly-once lifecycle
  hook. The LIFECYCLE is the contract — started / navigated / frames / ended-or-faulted; the transport,
  viewer UI, hover affordances and what any of it is FOR belong to the app (D21/D22), which is what the
  sample's `STREAM` route + `StreamViewer` demonstrate).
- `Shenora.Ipc` — the transport-neutral wire contract (design §5, D11/D16; names pinned with
  `JsonPropertyName` so envelopes hold under any serializer options): `IpcRequest`
  (`{id, module, type, scope?, payload?, timestamp}` — `scope` is the app-defined routing
  field), `IpcResponse` (`{category:"ipc", id, success, data?, error?}` + `CreateSuccess`/
  `CreateError`), `IpcError` (`{code, message?, parameters?}` — code is the client-side i18n
  key), `IpcNotification`/`IpcNotificationBatch` (`{category:"notification", id, payload:[…],
  timestamp}` — always-batched host→client push; the same envelope any transport carries),
  `IpcCategories`, `OperationException` (the one exception whose details cross the bridge;
  `ToError()`), `IpcErrorCodes` (framework-reserved codes), `PayloadHelper`
  (`GetRequiredValue`/`GetOptionalValue` with structured errors; JSON null == absent), `IpcJson`
  (frozen camelCase/camelCase-enum/null-omitting wire serializer defaults, plus
  `AddTypeInfoResolver` — a startup-only seam for an app's source-generated `JsonSerializerContext`,
  chained AHEAD of the reflection fallback so an AOT/trimmed host can supply the metadata reflection
  cannot; it adds metadata rather than reopening the one frozen instance, and registering after
  `Options` is built throws); `IpcHostBridge`/`IpcHostBridgeOptions` (the transport-neutral INBOUND
  half — parse → handshake-or-dispatch → response JSON, the dispatch lifetime token and the
  no-raw-exception-text boundary; owns no transport and no timer, the mirror of `NotificationPump`
  on the other direction, and the host-side mirror of the client's `ShenoraBridge`. Takes the pump
  optionally so the handshake opens the outbound gate in one place; CLOSING it stays the base's
  call. `HandshakeModule`/`HandshakeType` live here — `WebViewIpcBridge` forwards the consts);
  `ShellInfo` (`Name` + `Capabilities`, returned as the handshake's response data via
  `IpcHostBridgeOptions.Shell` — how one web bundle targets every shell: the page renders on the
  advertised capabilities instead of sniffing the platform, since what a host offers depends on what
  the app composed. Optional; null says nothing, which the client reads as "assume nothing");
  the dispatch
  pipeline — `IMessageDispatcher`/`MessageDispatcher` (`Use`/`UseModule`/`UseRoute`/`UseLogging`/
  `UseErrorHandler` + `MapRoute`/`MapModule(name, routes)`/`MapModule(facade)`; `DispatchAsync`
  transport entry: never throws, never null — `NO_HANDLER`/structured/`UNKNOWN_ERROR` mapping
  with details kept host-side; programmatic `SendAsync`/`SendAsync<T>` over the same pipeline,
  typed failures rethrow `OperationException`), `MessageMiddleware` delegate,
  `ModuleRouteBuilder`, `IModuleFacade` (carries `ModuleName` — facade objects route via DI +
  `MapModule`, no static registry) / `BaseFacade` (standardized error boundary) /
  `IpcErrorMapping` (that boundary as public surface: `ToError`/`ToErrorResponse`, for an app whose
  failures travel as events and so has no response to attach one to); a `CancellationToken` flows
  the whole pipeline — the CALLER's lifetime, supplied by the transport and cancelled on its dispose,
  not a per-request client cancel;
  `ScopedContainerRouter(+Options)` (per-scope child containers: app `ConfigureScope` +
  `OnScopeCreated`, single-flight creation, `MapModule<TFacade>` declarations, structured
  `SCOPE_REQUIRED`, `GetScopeServices`/`InvalidateScope`/`ActiveScopes`) + `UseScopedRouter`
  (on `ScopedContainerRouterExtensions`); composition helpers
  `AddModuleFacade<TFacade>`/`MapRegisteredModules`/`AddMessageDispatcher` on
  `IpcServiceCollectionExtensions` (error handler → app middleware → DI-registered facades, mapped
  LAZILY so the singleton is cached before the provider is enumerated); and
  `MessageDispatcherExtensions`, which carries the composition helpers as extensions over the
  interface's ONE `Use(MessageMiddleware)` primitive — so they work on any `IMessageDispatcher`,
  including a decorator, without the downcast the reference composition used to need (H6);
  and `IModuleRegistry` (`MappedModules`/`IsModuleMapped`/`TryClaimModule`/`TryReleaseModule` — claim, ask, release; implemented by
  `MessageDispatcher`) + `TryMapModule` — the seam for a DYNAMICALLY composed surface (plug-ins,
  licence-gated or per-tenant modules), kept OFF `IMessageDispatcher` so that interface stays the
  four things a dispatcher IS. `MapModule(facade)` throws on a duplicate; `TryMapModule` returns
  false instead, and throws rather than answering when the dispatcher cannot know. (The line that
  used to sit here — "known limit: a mapped module cannot be released, the pipeline only grows" —
  was stale from the release that added `TryReleaseModule`, and contradicted this same sentence's
  own member list.)
  **Known limit, recorded rather than solved: the registry does not see DI-registered facades.**
  `AddMessageDispatcher` maps them through `MapRegisteredModulesLazily` — ONE terminal middleware
  resolving them on first dispatch — not through `TryClaimModule`, because claiming needs the module
  NAMES and reading those means resolving the facades, which inside the `IMessageDispatcher` singleton
  factory is the silent `StackOverflow` P5.5 H2 fixed. Two consequences: `IsModuleMapped("OPERATIONS")`
  is `false` while `OPERATIONS` is routed, and a plug-in offering a name a DI facade already owns gets
  `true` from `TryMapModule` and then never runs, because the lazy middleware is composed earlier and
  answers first. Precedence is the one you want (the app's own modules win); the honesty is not.
  Closing it needs either a name-reservation seam the registry does not have or re-opening the
  deadlock — so until a consumer actually hits it, map anything that must be checkable through
  `MapModule(facade)`/`TryMapModule` explicitly rather than through DI registration.
  **The module contract's event half (0.2.0, D23):** `IModuleContext` (`Module`, `Logger`,
  `Publish(type, payload?, scope?)`, `Start(OperationOptions)`, `Run(OperationOptions, work)`) is the
  second parameter of `BaseFacade.RouteMessageAsync` — the release's one breaking change, because
  `Shenora.Ipc` had zero references to `IEventBus` while the kit's own `DropZoneManager` took one as a
  REQUIRED option. Built once per facade (`BaseFacade.Context`, lazy — `ModuleName` is abstract and
  unreadable from the base constructor) from the now-optional `BaseFacade(ILogger?, IEventBus?,
  IOperationRegistry?)` constructor params; `Publish`/`Start`/`Run` throw a loud, self-naming
  `InvalidOperationException` when the corresponding dependency was never supplied, rather than
  silently no-op-ing. `Publish` needs no registry and no opt-in — the primary, always-available
  channel; `Start`/`Run` are the one OPT-IN thing the same context offers (only present when
  `AddShenoraOperations` is called), never the other way round.
  **The operations cluster** (`Shenora.Ipc.Operations` mechanism, tracked long-running work — no
  queue, scheduler, retry, priority or phase model, and no opinion on what an operation IS):
  `OperationStatus` (`Running`/`Completed`/`Failed`/`Cancelled`/`Waiting` — crosses the
  wire camelCase for free via `IpcJson`'s enum converter), `OperationLabel` (`{Text?, Key?, Parameters?}`,
  the same i18n shape as `IpcError`), `OperationProgress` (`{Value, Total?, Unit?}` — the app's own
  unit, e.g. bytes-of-a-known-total, items-of-a-known-total, an absolute count with no known total
  (`Total = null`), or a genuine percent; `Unit` is app-defined and uninterpreted, like `Kind`),
  `OperationOptions` (`Kind` an app-defined string, `Title`, `Scope`, `Cancellable`, `Progress`),
  `OperationInfo` (the full
  snapshot — both the `OPERATION_UPDATED` event payload and the `LIST` response element; one type for
  every transition, so a client folds by `Id` with no cross-type ordering hazard; carries
  `WaitReason`, an app-defined string like `Kind`), `IOperation`
  (`Id`, its OWN `CancellationToken` — never the request's — `Report`(`OperationProgress?`, passed
  through unchanged — no clamp, no validation)/`Complete`/
  `Fail`(×2)/`Cancel`/`Wait`(reason OPTIONAL)/`Resume`, all idempotent once terminal),
  `IOperationRegistry`/`OperationRegistry(+Options)`
  (one lock over in-memory state; `Start`/`Run` — `Run` is `Start` + a guarded background body mapping
  `OperationCanceledException`→`Cancel`, `OperationException`→`Fail(code, parameters, message)`, else
  →`Fail(UnknownError, {exceptionType})`, identical to the dispatch boundary's no-raw-text rule —
  `Find(id)` (resolves a live handle for an id — reinstated post-audit, see below),
  `GetAll(module?, scope?)`/`ClearFinished(module?, scope?)` (both share ONE scope rule with
  `IEventBus` — an unscoped operation matches any requested scope, not strict equality — and
  `ClearFinished`'s filter mirrors `GetAll`'s exactly), `Cancel` (refuses an operation that never
  opted into `Cancellable`, so the status can't lie about a body still running underneath it),
  `Dismiss` (declines a pending `Waiting` offer → `Cancelled`, terminal — refuses
  `Running` on purpose, since declining an offer and cancelling LIVE work are different acts and
  conflating them inside `Cancel` was this branch's only Critical), and the ASK pair
  `RequestWait`/`RequestResume` — exact mirrors of each other, both emitting
  `{ operationId, module, kind, scope }` and changing NOTHING: the client asks, the owning module's own
  `IOperation.Wait`/`Resume` acts. A removal (`MaxHistory` eviction, `ClearFinished`) publishes
  `OperationEvents.Removed` naming the ids, so a client mirroring bounded host history actually hears
  about it. Progress
  emission is throttled to `ProgressInterval` — default 100 ms — with a TRAILING emit so the final
  value in a window is never dropped, and every lifecycle transition emits immediately, never
  throttled. `OperationEvents`
  (`Updated` = `OPERATION_UPDATED`, `ResumeRequested` = `OPERATION_RESUME_REQUESTED`,
  `WaitRequested` = `OPERATION_WAIT_REQUESTED`, `Removed` = `OPERATION_REMOVED`),
  `OperationsFacade` (module `OPERATIONS` by default, shared with the registry via one
  `OperationRegistryOptions` instance so the two can never drift apart:
  `LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`/`DISMISS`/`WAIT`), `AddShenoraOperations` (opt-in DI
  wiring; an app with no long-running work pays nothing).
  **The file-dialog cluster** (2026-08-05) — the page's route to whichever `IFileDialogs` the SHELL
  registered, so a picker needs no app-written route: `FileDialogFacade` (module `FILE_DIALOGS`, a fixed
  const because this facade publishes nothing for a configurable name to stay in step with —
  `OPEN_FILE`/`OPEN_FOLDER`/`SAVE_FILE`/`SAVE_TEXT`) + `AddShenoraFileDialogs` (opt-in). `SAVE_TEXT` is the
  PORTABLE save — the host does the writing, so it works on every shell — and carries text rather than
  arbitrary bytes because the content crosses the envelope. `OPEN_FOLDER`/`SAVE_FILE` are desktop
  capabilities (D35) and refuse elsewhere with `IpcErrorCodes.CapabilityNotSupported`, a NAMED code so a
  client can hide the control instead of showing a fault — which is what `@shenora/react`'s
  `useFileDialogs()` does, reading `canPickFile`/`canPickFolder`/`canPickSavePath` off the handshake (D36).
  This closed a real gap: `ShellCapability.FilePicker`/`FolderPicker`/`SavePicker` were kit vocabulary that
  crossed the wire with nothing in the kit able to satisfy them, so both samples had written the same
  routes independently.
  **Post-0.2.0-merge generic-library audit (before publish, so free):** the harvest absorbed one
  app's shape on the removal/asking halves of the lifecycle its own source never had to solve.
  `ClearFinished` gained the `module?`/`scope?` filter above (was unfilterable — a scoped window's
  "clear completed" could wipe another scope's history); `OperationOptions.Resumable`/
  `OperationInfo.Resumable` were REMOVED (consulted nowhere except the then-existing
  `RegisterWaiting`'s required-true gate, which every caller had already satisfied — a tautological
  flag, and the whole checkpoint path it gated went the same way in the design pass); `RequestWait`
  (shipped at the time as `RequestPause`) and the reinstated `Find(id)` were added (above);
  `OperationEvents.Removed` was added (above).
  `IOperation.Wait`'s `reason` became optional. One limit recorded rather than solved: `MaxHistory`
  is one global cap with no per-module/scope bounding seam. "Registered but not yet started" is
  representable with no kit change: an app calls `Wait("queued")` on the handle immediately after
  `Start`, before real work begins.
  **Progress is not percent (owner direction, before publish, correcting this same audit's own first
  pass):** `Progress` was `int?` (implicitly 0–100) with a silent `ClampProgress`; it is now
  `OperationProgress?` (`Value`/`Total?`/`Unit?`, above) passed through completely unchanged —
  `ClampProgress` is deleted, and `Complete()` sets `Value = Total` only when a `Total` was ever
  reported, never a hardcoded 100.
  **The lifecycle is enforced as THREE BANDS** (§5A of the design doc — Active: `Running`; Waiting,
  never pruned: `Waiting`; Terminal: `Completed`/`Failed`/`Cancelled`), and the rule that
  produced it is structural, not a convention: `OperationLifecycleInvariantTests` enumerates the LIVE
  `OperationStatus` enum and asserts every non-terminal value has a registered exit reaching a
  terminal one — a future status added with no exit fails that test by name instead of stranding an
  operation the way a no-live-handle offer used to (its only exit, `RequestResume`, never reached a
  terminal status at all).
  **How the band got to ONE status and ONE reach, in two steps — the second is the 0.2.0 design pass
  (D1) and it is the reason none of the machinery above exists any more.** `Paused` and `Interrupted`
  were originally two statuses distinguished only by how the entry was reached (a live
  `IOperation.Wait()` vs. a crash checkpoint registered by the former `RegisterWaiting`); every
  transition already treated them as one band, so they collapsed into `Waiting`. That left the
  distinction to be carried some other way, and each attempt failed: `ResumePayload` (app-controlled,
  so it dropped live operations), then an internal provenance flag. The design pass removed the
  QUESTION instead — the crash-checkpoint half is gone, so every entry reaches `Waiting` through a
  live `IOperation.Wait` and `RequestResume` mutates nothing. Crash recovery is the app's: it owns the
  checkpoint, and a resumed run is a fresh `Start()`. Full rationale: `docs/DECISIONS.md` D23's
  amendments and `CHANGELOG.md` 0.2.0 `### Removed`.
  **`NotificationPump`(+`Options`)** — the transport-neutral half of the outbound notification
  channel (design §5, D16 applied to the host side): bus subscription (from CONSTRUCTION, not
  `Open`), the per-channel `Filter` (applied at enqueue, fail-CLOSED on a throwing predicate — the
  filter exists so a channel gets only its own slice of traffic, and delivering a notification the
  app meant to keep off this channel is the more dangerous failure), the bounded drop-oldest queue,
  the ready gate (`Open`/`Close`), batch building, and the guarded per-notification serialize (one
  bad payload must not sink its batch). Owns NO timer and NO transport — `TryDrainBatch` is called by
  whatever the base drives its own tick with (a `Forms.Timer` on WinForms; a `PeriodicTimer` on a
  headless base), because which thread may touch a base's client is a base-specific fact.
  `WebViewIpcBridge` is now a thin adapter over it: it keeps only what is WinForms/WebView2 — the
  timer, `WebMessageReceived`, `ContentLoading`→`Close()`, `READY`→`Open()`,
  `ProcessFailed`→`Close()`, and `PostWebMessageAsString` — while `WebViewIpcBridgeOptions` keeps its
  existing option names (`NotificationInterval`, `MaxQueuedNotifications`, forwarded to the pump's
  `FlushInterval`/`MaxQueued`) and gains `NotificationFilter`.
- `@shenora/react` — the client side of the contract: wire types mirroring `Shenora.Ipc`
  name-for-name (+ `IpcCategories`/`IpcErrorCodes`/handshake constants), `OperationError`
  (structured code + parameters; client-side `TIMEOUT`/`NO_TRANSPORT` reject the same way),
  `ShenoraTransport` seam + `createWebView2Transport` (D16 pluggability) +
  `isShenoraAvailable`, `ShenoraBridge` (correlated `invoke` + timeout, one-way `post` +
  `onPostError` — no pending entry, no deadline, and a failed response reported rather than dropped;
  category routing, batch unbundling, `notifyReady` handshake — which RESOLVES to the host's
  `ShellInfo | undefined` and caches it on `bridge.shell`, the client half of "one bundle, every
  shell"; `ShellInfo`/`ShellCapabilities` mirror the host names — `fallback` seam for pure-UI browser
  dev; lazy default via `getBridge`/`configureBridge`), `ShenoraEventBus`/`eventBus` (three
  subscription breadths mirroring the host's `IEventBus` — exact `(module, type)`,
  `subscribeToModule`, `subscribeToAll` — delivered narrowest-first),
  `createShenoraStore` (a store fed by one module's event stream: ONE subscription however many
  components read it, `snapshot` on the first subscriber so a late mounter is not empty, built on
  React's `useSyncExternalStore` so the package imposes no state library),
  `BaseModuleService<TRequests>`, hooks (`useShenora`/`useShenoraEvent`/`useShenoraQuery`),
  `WindowCommands` typed service + `useWindowMaximized` (resize-triggered resync), `useDropZone`
  (native drop zones synced to elements — real OS paths, unstyled drag feedback),
  `installDevInterceptor` (`window.__shenora` CDP-testing global); **`useShenoraOperations`/
  `createOperationsStore`** (0.2.0) — mirrors `Shenora.Ipc`'s operations cluster: `OperationStatuses`
  (the wire values, including `waiting`), `OperationEventTypes` + `OperationModuleName` (the event
  vocabulary and default module, for the two events the store deliberately does NOT subscribe to —
  `RESUME_REQUESTED`/`WAIT_REQUESTED` target the OWNING module's service), and the
  `OperationInfo`/`OperationLabel`/`OperationProgress` types (`waitReason`
  mirrors the host's `WaitReason`; `resumable` removed post-audit, see below), and a
  `createShenoraStore` instance (`snapshot: LIST`, `on: { OPERATION_UPDATED: fold-by-id,
  OPERATION_REMOVED: delete-named-ids }`, `actions: { cancel, dismiss, wait, clearFinished, resume }`)
  with `running`/`waiting`/`finished` DERIVED getters
  computed from `byId` on every read — never a second copy a reducer has to remember to keep in sync.
  **The status collapse (owner direction, before publish — "structured like XHR"):** `waiting` used to
  be two getters, `paused` and `interrupted`, unioned by a third — `interrupted` itself was added
  (0.2.0, second adopter review) to close a gap the design's own three-band table (§5A.2) exposed: an
  `interrupted` entry used to fall into NO getter at all (matched only the literal status string, not
  `finished`) — reachable only by hand-filtering `byId`. Once the host's `OperationStatus` collapsed
  `Paused`/`Interrupted` into the single `Waiting` value (every transition already treated them as one
  band), the two half-getters were DELETED rather than kept as aliases: `waiting` is now the whole
  band, a single-status filter exactly like `running`, with no second internal status set to derive
  it from. `finished`/`waiting` stay disjoint by construction (the TERMINAL set `finished` filters on
  excludes `waiting` on purpose). **Post-audit (before publish):** `clearFinished`/`resume` no longer
  carry an optimistic local prune — they used to guess at what the host had removed (`clearFinished`
  on the TERMINAL set; `resume` mirroring the host's `RequestResume` asymmetry, §5A.4, dropping only
  the no-live-handle case), because removals had no wire event at all; one of those guesses was this
  release's only Critical (a `resume` prune that once dropped a live-`Wait()` row the host deliberately
  keeps, rebuilding "a waiting entry with no reachable exit" one layer up). The host's
  `OPERATION_REMOVED` is now the ONE authoritative removal signal, folded by deleting exactly the
  named ids regardless of status — `clearFinished`/`resume` are now plain posts (`clearFinished`
  forwards this store's own configured `scope`), with no client-side guess left to diverge from the
  host. `wait` (post-audit; shipped at the time as `pause`) posts `WAIT` and mirrors `dismiss`'s shape
  — asking is not acting, so neither needs any local mutation.
  `dismiss` needs no removal handling at all, since the host's `Dismiss` publishes an ordinary
  terminal snapshot for the entry over the wire rather than removing it.
  `createOperationsStore(options)` takes an
  optional renamed module (for an app that changed `OperationRegistryOptions.ModuleName` to avoid a
  collision) and an optional `scope`, threaded into the snapshot payload, the bus subscription AND
  the action envelopes so a scoped store stays internally consistent; `useShenoraOperations` is the
  ready-made default instance. Known limit, deliberate: no `byModule`/`byScope` selector — filtering
  by module or scope is a one-line consumer selector over `byId`
  (`Object.values(state.byId).filter(o => o.module === 'X')`), and shipping indexes for it would be
  duplicated derived state for no gain. react ≥18 required peer.

## Dependency rules (enforced by review)

- `Core` depends only on Microsoft.Extensions DI (implementation — the builder needs
  `BuildServiceProvider`, D17) + logging abstractions. Everything else depends downward on `Core`.
- **Execution and reporting compose; they do not merge.** `Core`'s `Work/` layer must never learn what
  an operation is — a mission body reports into `Shenora.Ipc`'s operation registry, and the seam pointing
  that way is `IMissionObserver`. `Shenora.Ipc` may depend on `Shenora.Core`, never the reverse (D19/D20),
  which is also why the scheduler ships no storage dependency: `IMissionQueueStore` is a seam, not an
  implementation.
- **Windows is ONE package, and D19's edge is now internal (2026-08-02).** D19 established that the
  Windows primitives and the web hosting on top of them are one layer rather than two peers — decided
  on evidence, after the UI-thread marshal pattern had been hand-rolled 14 times with five
  incompatible pre-handle policies, two of them buggy. Three packages then expressed that one layer,
  and the split's remaining justification was a WinForms-without-WebView2 consumer: a tray or
  single-instance utility with no web frontend. **That consumer cannot exist in this kit** — the
  premise is React in a webview — so the boundary described an adoption STAGE, not a shipping
  configuration, and the three merged. `Sessions` folded in for free: it added no dependency of its
  own, already sharing `Microsoft.Web.WebView2`.
  The internal direction still matters and the folders carry it: `Shell/` must never depend on
  `WebView/`, which would be the cycle D19 forbade.
- **Portable contracts live in `Shenora.Core` (D20):** `IUiDispatcher`/`UiTargetState`,
  `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`Filter`/`Result`, `IClipboardService`,
  and the portable bases `IUrlLauncher`/`IUiInteraction`, plus `ShellCapability` — the shared
  capability vocabulary (`windowChrome`, `dropZones`, `filePicker`, `folderPicker`, `savePicker`,
  `secondaryWindows`, `tray`) and the `NotSupported` factory a shell throws from when it lacks one
  (D33). The names are what a host advertises through `ShellInfo` and what a page branches on.
  Their Windows implementations stay in
  `Shenora.Windows`, which registers BOTH faces of each split service so app logic can depend on the
  neutral contract and compile with no Windows reference. The bar for moving a contract to `Core` is
  "app logic must compile off Windows", NOT "the signature happens to be platform-neutral" — which is
  why the window-state stack deliberately stays in `Shenora.Windows`. `Shenora.Windows` layers
  on `Shenora.Windows` (the one deliberate package-on-package edge above `Core` — D14 keeps
  the session stack out of the core hosting package).
- `src/*` never references `tests/`, `samples/`, or anything app-specific.
- No Lyntai reference, ever (docs/DECISIONS.md D1).
