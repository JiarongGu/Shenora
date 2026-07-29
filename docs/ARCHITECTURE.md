# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/2026-07-30-shenora-design.md`; this file
records only what EXISTS.)

## Current state (P2 extraction in progress)

The P2 core host is extracted (increments 1–6): pure seams, application builder + WinForms host
composition, WebView2 host + packaged-frontend serving + splash, and the sample apps that serve
as the e2e subject (both frontend modes proven live via the devtools capture loop).
`Shenora.Ipc` is still an empty placeholder until P3.

```
Shenora.slnx
├── src/
│   ├── Shenora.Core        net10.0          — deps: M.E.DependencyInjection (impl, D17), M.E.Logging.Abstractions
│   ├── Shenora.Ipc         net10.0          — deps: Shenora.Core
│   ├── Shenora.WebView2    net10.0-windows  — deps: Shenora.Core, Shenora.Ipc, Microsoft.Web.WebView2
│   ├── Shenora.WinForms    net10.0-windows  — deps: Shenora.Core
│   └── Shenora.React/      @shenora/react    — peer: react >=18; build tsc, test vitest
├── tests/
│   └── Shenora.Tests       net10.0-windows  — xunit; references all four src projects
└── samples/                                 — never packable; the e2e subject (dev.mjs sample/vite/shot/wgc/click)
    ├── Shenora.Sample.Desktop  net10.0-windows — the reference composition (builder → UseWinForms →
    │                                            prewarm → WebViewHost + provider + SplashPanel);
    │                                            embeds wwwroot (built by the web sample, gitignored)
    └── Shenora.Sample.Web      Vite + React    — consumes @shenora/react (file:), port 3900, builds
                                                 into the desktop sample's wwwroot
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
  `IShenoraLifecycleHook` (DI-registered start/stop participation; runners invoke post-gate).
- `Shenora.WinForms` — `DpiHelper` (BaseDpi, `SystemScale`, `ScaleFromDeviceDpi`, pure `Scale` +
  internal-element helpers); `WindowState`/`WindowStateOptions`/`IWindowStateStore`/
  `JsonFileWindowStateStore`/`WindowStateManager` (logical-px persistence, physical restore,
  off-screen recovery — pure `ToPhysical`/`ToLogical`/`IsVisible` cores); `SingleInstanceGuard`
  (per-scope FNV-1a mutex + activate broadcast, fail-open; `TryAcquire(TimeSpan)` = the
  `--restarted` widened-wait handoff with abandoned-mutex recovery); `WinFormsBootstrap(+Options)`
  + `UnhandledExceptionReport/Source` (one-call WinForms init + the three global exception
  channels with crash-log callback and last-resort dialog); the host composition —
  `UseWinForms(WinFormsHostOptions)` with `SingleInstanceHostOptions` (gate scope/restart
  argument/wait/losing-launch callback) and `WindowStateHostOptions` (store factory + options),
  backed by an internal runner (gate → bootstrap → starting hooks → form factory → window state →
  activate-message filter → loop → reverse-order stopping hooks → release); `SplashPanel(+Options)`
  (startup marquee overlay, app-chosen colors — headless per D13, debounced recenter).
- `Shenora.WebView2` — `BrowserArguments` (the measured Chromium display-optimization preset;
  single-occurrence feature lists; dev CDP-args append); `WebViewEnvironment(+Options)`
  (runtime presence probe, idempotent prewarm, thread-affine shared environment +
  per-STA-thread creation for secondary windows); `PrewarmWebView2` builder extension
  (prewarm as a deferred starting hook — stays behind the single-instance gate);
  `WebViewHost(+Options)` (the ONE place a WebView2 is configured: env + ensure under a 25 s
  init-timeout guard, settings-hardening preset + `ConfigureSettings` escape hatch, dev/prod
  `Navigate` with actionable errors, sync virtual-host serving of the packaged bundle vs
  deferred off-UI-thread app schemes (`WebViewDeferredScheme`), disk-folder hosts
  (`WebViewFolderMapping`), escaped `InjectedGlobals` + family scripts, and the four default
  event policies: new-window→system browser, downloads canceled, permissions denied except
  allowlist, guarded renderer-crash reload); `IWebViewResourceProvider` seam +
  `EmbeddedResourceProvider(+Options)` (assembly+prefix, lazy-with-warmup, file-fallback mode,
  path→name lookups; no-cache HTML / immutable hashed-asset headers).
- `Shenora.Ipc` — none yet (P3).

## Dependency rules (enforced by review)

- `Core` depends only on Microsoft.Extensions DI (implementation — the builder needs
  `BuildServiceProvider`, D17) + logging abstractions. Everything else depends downward on
  `Core`; never sideways (`WinForms` ↔ `WebView2`) — host packages contribute via extension
  methods over the Core builder, and the app composes them.
- `src/*` never references `tests/`, `samples/`, or anything app-specific.
- No Lyntai reference, ever (docs/DECISIONS.md D1).
