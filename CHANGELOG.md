# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Released versions are listed newest first; within `## Unreleased`, entries are in
landing order (oldest first) because they narrate one version being built.

## Unreleased (0.1.0)

### Breaking

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

### Added

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

### Changed

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

### Fixed

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
