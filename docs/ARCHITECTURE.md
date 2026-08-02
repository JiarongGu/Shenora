# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/2026-07-30-shenora-design.md`; this file
records only what EXISTS.)

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`, the same way README.md's
     headline is. Don't hand-edit it — and don't date this line either: the release workflow owns
     the version, so a hand-written one is stale the moment a release cuts. Everything ELSE in this
     file dates its claims instead of versioning them, for the same reason. -->
## Current state — **v0.6.0 published**; P1–P7 complete (v0.1.0 shipped 2026-07-31)

Five NuGet packages + `@shenora/react` on npm — since 0.5.0 organised BY PLATFORM (D37): `Core`,
`Ipc`, `Windows` (the three old Windows ids merged), `Android` and `iOS`. All five ship from ONE
Windows runner — 0.5.0 published only the first four because `pack` skipped iOS on the mistaken
belief that it needed a Mac, and 0.5.1 corrected that. Since the summary below was written, P5.5
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
│   │                                          Platforms/ folder (MAUI SDK includes per TFM); there
│   │                                          is none yet. BOTH are in the solution and gated on
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
- **`Shenora.Core`'s mission-scheduling layer (0.3.0, `Missions/` + `Io/`)** — the EXECUTION half of
  long-running work, portable and with no DI, storage or reporting dependency of its own:
  `IMissionScheduler`/`MissionScheduler(+Options)` (`SubmitAsync`, `Lane(name)`, `PendingCount`/
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
  Every request also draws one permit from the default lane (`DefaultLaneCapacity`, 0 = `clamp(cores-1,
  1, 4)`), which is the global concurrency bound.
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
  **`Io/` — the file-update queue (2026-08-02), independent of the scheduler.**
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
  **`Io/` — cross-process locking, the two halves of a problem claims cannot reach.** A
  `MissionClaim` excludes missions inside one process; these cover the rest. `IPathLocker`/`IPathLease`
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
  **`Io/PathClaims`** (static) — `Scope` (a `NestedClaimScope` over `Path.DirectorySeparatorChar`,
  case-insensitive on Windows only), `Exclusive`/`Shared` (claims on the `"path"` scope, `ScopeName`),
  `Canonical` (absolute + separator-normalized, so two spellings of one location are one key) and
  `IsContained(root, candidate)` (the containment guard for anything mapping caller input to a file —
  resolves `..` first, boundary-tested, so `C:\data-old` is not inside `C:\data`).
  **`Io/UpdateManifest`, `ManifestFile`, `ManifestDiff` (2026-08-02)** — the staged-update
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
  **`Io/UpdateStage` (+`Options`, `+Status`)** — the staging half of the two-phase update. The app
  writes downloaded files into `StagedDirectory`; `CommitAsync` hashes every file the manifest lists
  and writes `ready.json` **last**, so the marker's existence IS the promise that the stage is
  complete and verified and an applier need not re-check. `Begin()` clears a previous attempt (its
  leftovers would otherwise verify as part of the next one), `GetStatus()` reads only the marker and
  never throws, and an EMPTY manifest is refused here — the guard `ManifestDiff` defers. No
  downloader and no release source — those are the app's. `IUpdateSource` is the seam (two methods,
  no implementation shipped) and `FetchAsync` is the download-and-stage phase: diff, fetch only the
  CHANGED files, commit. Because only the changeset is staged, `CommitAsync` verifies the manifest of
  what is IN the stage; the full release manifest rides along as `manifest.json` so the applier can
  compute removals and so overlaying it makes the new installed baseline.
  **`ApplyAsync` + `UpdateOutcome`** — the apply pass, portable .NET rather than native: overlay,
  remove what the new manifest dropped, clear. Run it from OUTSIDE the tree it overlays (a launcher
  at `{root}/` over `{root}/app/`), which is what makes self-exclusion guards unreachable rather than
  handled. An unreadable or empty staged manifest BLOCKS the apply, because removals are
  "installed minus release" and a manifest that failed to load would delete everything just written.
  A self-contained app needs no native code; a framework-dependent one still wants a launcher to
  bootstrap the runtime and call this.
  Naming is `Mission*` and deliberately not `Operation*`: `Shenora.Ipc` owns the reporting vocabulary,
  and reusing the word would blur the one distinction the design rests on. It was `Work*` until
  2026-08-02 — too common a word to own or grep, while `Task*` would collide with the BCL.
  **Three as-built facts worth recording, because a reader of the design doc or the XML would expect
  otherwise:** (1) an unknown LANE does NOT throw — it is created at the default capacity on first
  mention, so only an unregistered claim SCOPE is a submit-time error (the trap is a misspelled name
  silently costing the exclusivity that was configured; two XML remarks used to claim the lane threw,
  corrected and pinned by `An_unseen_LANE_name_is_created_at_the_default_capacity_rather_than_throwing`);
  (2) the design's `IFileSystem` and atomic-replace helper were never shipped — `PathClaims` is the
  whole of `Io/`, and the write-to-temp-then-replace SHAPE is what `Run`/`Commit` models; (3) nothing
  in `Shenora.Ipc` implements `IMissionObserver`, so wiring execution to the operation registry is the
  app's own ~35-line adapter — `samples/Shenora.Sample.Logic/MissionOperationObserver.cs` is the worked
  example — and `Shenora.Core` stays free of any reporting dependency either way (D19/D20). That
  adapter's one non-obvious rule: its operations must be `Cancellable = false` unless the app wires
  cancellation itself, because the registry's `Cancel` signals the OPERATION's own token while the
  work observes the one handed to `SubmitAsync`.
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
  per-key directory memory; failures throw), `IShellLauncher`/`ShellLauncher` (reveal/open-dir/
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
  path→name lookups) — the no-cache-HTML / immutable-hashed-asset header policy lives in the
  internal `WebViewContentTypes` and is applied by `WebViewHost` when it serves; `WebViewIpcBridge(+Options)`
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
  init-timeout guard, `GetHtmlAsync`); `RenderSessionPool(+Options)`/`RenderSession`/
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
