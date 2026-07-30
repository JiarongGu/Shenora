# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Newest first.

## Unreleased (0.1.0)

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
