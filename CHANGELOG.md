# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Released versions are listed newest first; within `## Unreleased`, entries are in
landing order (oldest first) because they narrate one version being built.

## Unreleased (0.1.0)

### Fixed

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
