# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/2026-07-30-shenora-design.md`; this file
records only what EXISTS.)

## Current state — **v0.1.0 SHIPPED (2026-07-31)**, P1–P7 complete; **0.2.0 communication core landed**

Five NuGet packages + `@shenora/react` on npm. Since the summary below was written, P5.5 landed the
D19/D20 re-layer (`WebView2` → `WinForms`; portable contracts + `IUiDispatcher` in `Core`, enforced by
a `net10.0` sample that turns red if a Windows type reaches app logic), P5.6 added native caption
buttons, P6 readied adoption (`docs/ADOPTION.md`, and six capability gaps found and closed), and P7
stabilised: every public and protected member documented with CS1591 as an error, the login RECIPE
moved out of the library to the sample (D21/D22 amended), and the release pipeline hardened. The
narrative is `docs/ROADMAP.md` `## Done`; the task-level record is `docs/task-archive.md`.

**0.2.0 (D23, `docs/2026-08-01-shenora-communication-core-design.md`, implemented):** the module
contract now carries the EVENT path — `IModuleContext` (`Publish`/`Start`/`Run`/`Logger`) is the
second parameter of `BaseFacade.RouteMessageAsync`, the one breaking change this release makes. A new
operations cluster in `Shenora.Ipc` tracks long-running work (id, status, progress, cancel-by-id,
throttled progress emission) as mechanism only — what an operation IS stays app-defined. The
transport-neutral half of the outbound notification pipeline moved out of `WebViewIpcBridge` into
`Shenora.Ipc`'s `NotificationPump`, so `WebViewIpcBridge` is now a thin WinForms/WebView2 adapter over
it (D16's "the seam, not the package" applied to the host half). `@shenora/react` gained
`useShenoraOperations`/`createOperationsStore`, a host-backed store mirroring the pattern
`createShenoraStore` already established.

P2 delivered the core host (builder, WinForms runner, WebView2 hosting + serving, samples). P3
delivered the full IPC stack (wire contract, dispatcher + facades, event bus, postMessage
transport, `@shenora/react` client, live round-trip). P4 delivered the native desktop surface:
the scoped-container router + standard IPC composition, frameless chrome + frontend window
commands, STA dialogs/shell/clipboard/interaction services, drag-drop zones + `useDropZone`
(+ per-monitor DPI handling), secondary windows + tray. P5 added the `Shenora.WebView2.Sessions`
package: the one browser-configuration path, a bounded LIFO render-session pool, the login-window
stack (persistent per-account profiles, silent refresh, clear-on-logout), and co-browse streaming
— all proven live in the sample.

```
Shenora.slnx
├── src/
│   ├── Shenora.Core        net10.0          — deps: M.E.DependencyInjection (impl, D17), M.E.Logging.Abstractions
│   ├── Shenora.Ipc         net10.0          — deps: Shenora.Core
│   ├── Shenora.WebView2    net10.0-windows  — deps: Shenora.Core, Shenora.Ipc, Microsoft.Web.WebView2
│   ├── Shenora.WebView2.Sessions net10.0-windows — deps: Shenora.WebView2, Microsoft.Web.WebView2
│   ├── Shenora.WinForms    net10.0-windows  — deps: Shenora.Core
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
    │                                            leaving D20's portability merely asserted
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
  `Run()` executes the registered runner; `Dispose` owns the provider),
  `ShenoraApplicationBuilder` (`Services`, `AddModule`, `OnStarting`/`OnStopping`, build-once),
  `IShenoraModule` (per-feature service registration), `IShenoraRunner` (the host-loop seam),
  `IShenoraLifecycleHook` (DI-registered start/stop participation; runners invoke post-gate);
  the in-process event bus — `EventMessage` (`{id, module, type, scope?, payload?, timestamp}`,
  host-side; the wire form is `Shenora.Ipc`'s notification envelope), `IEventBus`/`EventBus`
  (`"*"` wildcards + per-subscription match cache; unscoped subscriptions see every scope and
  global events reach scoped subscribers; handler failures logged + isolated; `EmitAsync` awaits
  every handler, `Emit` is the fire-and-forget twin for a synchronous caller; auto-registered
  by `Build()` via `TryAdd` — replaceable).
- `Shenora.WinForms` — `DpiHelper` (BaseDpi, `SystemScale`, `ScaleFromDeviceDpi`, pure `Scale` +
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
  draws them itself; the native services, TryAdd-registered by `UseWinForms` —
  `IFormInteraction`/`FormInteraction` (main-window registry, runner-wired; nested modal
  blocking), `IFileDialogs`/`FileDialogs(+Options)` + `FileDialogOptions`/`Filter`/`Result` +
  `IFileDialogPathStore` seam (dedicated-STA open/folder/save dialogs, owner-handle z-order,
  per-key directory memory; failures throw), `IShellLauncher`/`ShellLauncher` (reveal/open-dir/
  http-https-`OpenUrl`/launch — Win11 handle-leak fixes), `IClipboardService`/`ClipboardService`
  (STA text + image-file ops); `SecondaryWindows(+SecondaryWindowOptions)` (named windows on
  own-STA-thread pumps, per-name `IWindowStateStore` geometry, activate-on-existing,
  non-blocking close); `TrayIcon(+Options)`/`TrayMenuColors` (NotifyIcon + composed menu,
  double-click restore, close-to-tray, optional app-colored renderer).
- `Shenora.WebView2` — `BrowserArguments` (the measured Chromium display-optimization preset;
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
  posted body, a dispose path). Public because `Shenora.WebView2`, `Shenora.WebView2.Sessions` and
  `Shenora.WinForms` all consume it and a `ProjectReference` grants no `internal` access (D19/D20).
- `Shenora.WebView2.Sessions` — auxiliary browser sessions (D14: browser work outside the
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
  (frozen camelCase/camelCase-enum/null-omitting wire serializer defaults); the dispatch
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
  category routing, batch unbundling, `notifyReady` handshake, `fallback` seam for pure-UI browser
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
- **The two Windows packages are ONE layer (D19):** `Shenora.WebView2` → `Shenora.WinForms`, i.e.
  Windows **primitives** and **web hosting on top of them** — not two peers. This replaced the old
  "never sideways" rule on evidence (the UI-thread marshal pattern had been hand-rolled 14 times with
  five incompatible pre-handle policies, two of them buggy), and it adds no new *technology*
  dependency: `Shenora.WebView2` already sets `UseWindowsForms` and hosts the WebView2 WinForms
  control. Still forbidden: `WinForms` → `WebView2` (that direction would be a cycle), and
  `Shenora.WinForms` still carries **no `Shenora.Ipc` dependency** — which is what keeps a
  WinForms-only consumer (a tray/single-instance utility with no web frontend) viable, and why the
  window-command and drop-zone facades live in `Shenora.WebView2`.
- **Portable contracts live in `Shenora.Core` (D20):** `IUiDispatcher`/`UiTargetState`,
  `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`Filter`/`Result`, `IClipboardService`,
  and the portable bases `IUrlLauncher`/`IUiInteraction`. Their Windows implementations stay in
  `Shenora.WinForms`, which registers BOTH faces of each split service so app logic can depend on the
  neutral contract and compile with no Windows reference. The bar for moving a contract to `Core` is
  "app logic must compile off Windows", NOT "the signature happens to be platform-neutral" — which is
  why the window-state stack deliberately stays in `Shenora.WinForms`. `Shenora.WebView2.Sessions` layers
  on `Shenora.WebView2` (the one deliberate package-on-package edge above `Core` — D14 keeps
  the session stack out of the core hosting package).
- `src/*` never references `tests/`, `samples/`, or anything app-specific.
- No Lyntai reference, ever (docs/DECISIONS.md D1).
