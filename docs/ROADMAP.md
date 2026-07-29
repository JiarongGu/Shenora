# ROADMAP.md — done + remaining

`## Done` is the durable record (narrative, newest first — what changed, why, how it was
verified). `## Remaining` is the phase plan; items graduate here from `TASKS.md` when finished.

## Done

### 2026-07-30 — P2 increment 6: samples + the desktop e2e loop, both frontend modes proven live

`samples/Shenora.Sample.Desktop` + `samples/Shenora.Sample.Web` — the reference composition and,
from here on, the e2e subject. The desktop app is the full stack in its intended shape:
`ShenoraApplication.CreateBuilder` → DI-registered `WebViewEnvironmentOptions` (ONE instance
shared by prewarm and the window's host) + `EmbeddedResourceProvider` (embedded
`wwwroot` bundle, file-fallback in dev) + `WebViewHostOptions` (dev URL 3900, virtual host,
injected metadata global, no-white-flash background) → `PrewarmWebView2` + provider warmup as
starting hooks → `UseWinForms` (single instance, `JsonFileWindowStateStore` window state) →
`MainForm` (WebView2 + `SplashPanel` until first navigation, runtime-presence prompt, actionable
init errors). The web sample is a minimal Vite React app consuming `@shenora/react` that displays
its serving mode, `isShenoraAvailable()`, and the injected host metadata — so one screenshot
proves the whole stack. Verified live with the devtools loop (`wgc` capture): PACKAGED mode
(embedded bundle over the virtual host — "frontend: packaged / bridge: WebView2 host detected /
host: Shenora.Sample.Desktop v1.0.0") and DEV mode (live Vite — "frontend: dev (Vite)", same
bridge + metadata), window state persisted DPI-logically (physical ~2538 px stored as 1280
logical at 200 %) and restored on relaunch, and the CDP devtools port reachable in dev — the
`AdditionalBrowserArguments`-clobbers-the-env-var fix working end-to-end. `dev.mjs
sample/vite/shot/wgc/click` now have their target. 126 tests green; `verify` PASSED.

### 2026-07-30 — P2 increment 5: WebView2 host, packaged-frontend serving, event policies, splash

`Shenora.WebView2` gains the "one place a WebView2 gets configured": `WebViewHost(+Options)` —
environment acquisition (shared/prewarmed or per-STA-thread) and `EnsureCoreWebView2Async` under
the family's 25 s init-timeout guard (an orphaned user-data-folder lock otherwise hangs init
forever, silently), the settings-hardening preset (dev-gated devtools/context menus, everything
unused off, web messages on) with a `ConfigureSettings` escape hatch, dev/prod navigation with
actionable errors (`ResolveStartUrl`: DevUrl in dev — deliberately no default port; explicit
`ProductionUrl` or the virtual host's index in prod), and the four event policies every source
lacked: new-window → system browser (scheme-checked), downloads canceled by default, permissions
silently denied except an allowlist (clipboard-read), renderer-crash auto-reload with a cooldown
— each replaceable by a callback. Resource serving keeps the source's measured sync/deferred
split with its post-mortem comments: the virtual-host bundle serves synchronously in-memory (the
main document must be prompt), app schemes (`WebViewDeferredScheme`) defer off the UI thread and
marshal responses back via `BeginInvoke`; disk-folder virtual hosts (`WebViewFolderMapping`)
are supported alongside interception (both family mechanics, deliberately). Fixed during the
port: the caching policy is now no-cache HTML / immutable hashed assets (the source served
`index.html` immutable — a stale-update trap), and injected globals are real JSON with escaping
(`InjectedGlobals`) instead of raw string interpolation. `EmbeddedResourceProvider(+Options)`
behind the `IWebViewResourceProvider` seam is parameterized by assembly + prefix, lazy-with-warmup
(the source preloaded everything in a blocking parallel ctor loop), file-fallback mode for dev,
and resolves lookups path→name so dotted filenames work. `Shenora.WinForms` gains
`SplashPanel(+Options)` — the startup marquee overlay with app-chosen colors (headless, D13) and
a debounced recenter; the source's dead status labels were dropped. Verified: 126 tests green
(provider modes/warmup/dotted names, script escaping, URL resolution, content-type + cache
policies, splash layout); the live host path is proven by the P2.6 sample e2e; baselines
promoted (additions only).

### 2026-07-30 — P2 increment 4: application builder + lifetime, `--restarted` relaunch handoff

`Shenora.Core` gains the composition root the design's goal statement names:
`ShenoraApplication.CreateBuilder(args)` resolves the launcher contract up front (`--app-root` →
`ShenoraPaths` → `ShenoraEnvironment` anchored at the resolved root), exposes
`Services`/`AddModule(IShenoraModule)`/`OnStarting`/`OnStopping`, and `Build()` produces a
`ShenoraApplication` whose `Run()` executes a host-package-registered `IShenoraRunner` (actionable
error when none). Lifecycle participation is DI-based (`IShenoraLifecycleHook`), so composed
packages hook startup/shutdown without Core referencing them — the mechanic that keeps package
dependencies strictly downward (design §4 amendment; Core's dependency moved to the DI
implementation package, D17). `Shenora.WinForms` gains `UseWinForms(WinFormsHostOptions)` — an
internal runner executing the family's measured order: single-instance gate FIRST (now with the
`--restarted` widened-wait handoff: `SingleInstanceGuard.TryAcquire(TimeSpan)`, abandoned-mutex
recovery, explicit release-before-teardown), `WinFormsBootstrap.Initialize`, starting hooks,
main-form factory (+ optional window-state apply/save and an activate-on-second-launch message
filter that works with ANY `Form` — no base-class requirement), the message loop, then
reverse-order guarded stopping hooks. `Shenora.WebView2` gains `PrewarmWebView2` (a deferred
starting hook — the prewarm's user-data lock must stay behind the gate). Verified: 93 tests
green (builder composition, documented run order, losing-launch path, widened-wait/timeout/
abandonment handoffs, window-state wiring through internal seams); the real message-pump path is
proven by the P2.6 sample e2e; API baselines promoted (additions only).

### 2026-07-30 — P2 increment 3: WebView2 environment factory + runtime presence check

`Shenora.WebView2` gains `WebViewEnvironment(+Options)`: the prewarm pattern (browser-process
spawn overlapping the rest of startup — ~1–2 s measured in the source), the shared environment
with its thread-affinity contract (main UI thread) plus `CreateForCurrentThreadAsync` for
secondary windows on their own STA threads (same options/user-data folder ⇒ one shared browser
process), the dev CDP-args re-append, an injectable log sink instead of the source's
`Console.WriteLine`, and — NEW, the gap every source shipped with — a never-throwing runtime
presence probe (`GetAvailableRuntimeVersion`/`IsRuntimeAvailable`) so apps can show an
actionable install prompt instead of failing inside `EnsureCoreWebView2Async`. 70 tests green.

### 2026-07-30 — P2 increment 2: paths authority, app-root arg, bootstrap + global exception handling

`Shenora.Core` gains `AppRootArgument` (the launcher's `--app-root` contract, both arg forms) and
`ShenoraPaths(+Options)` — the portable layout authority generalized from two sources: explicit
root → root env var → libs-parent detection → base dir, a data env var so child processes share
the host's data dir (a live divergence incident in a source app), configurable folder names, and
ensure-created purpose areas with NO framework-defined area vocabulary. `Shenora.WinForms` gains
`WinFormsBootstrap` — the proven one-call WinForms init (visual styles, GDI+ text, PerMonitorV2,
catch-mode) PLUS the audit's #1 gap fixed: `Application.ThreadException`,
`AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` all routed to a
crash-log callback with a guarded last-resort dialog (a known no-op reflection hack from the
source was deliberately dropped). Verified: 67 tests green; baselines promoted (additions only).

### 2026-07-30 — P2 increment 1: the pure seams (environment, DPI, window-state, single-instance, browser args)

First real extraction, targeting the fully-unit-testable seams. `Shenora.Core.ShenoraEnvironment`
unifies dev-mode detection that one source app duplicated across four files. `Shenora.WinForms`
gains `DpiHelper` (primary-monitor + per-device-DPI scales, pure `Scale` core), the merged
window-state stack (`WindowStateManager` with pure `ToPhysical`/`ToLogical`/`IsVisible`, an
`IWindowStateStore` seam covering both family storage styles plus `JsonFileWindowStateStore`),
and `SingleInstanceGuard` (per-scope FNV-1a key, activate broadcast, fail-open). `Shenora.WebView2`
gains `BrowserArguments` — the measured display-optimization preset with the
single-feature-switch rule and the CDP env-var-clobber fix. The placeholder no-public-types test
was replaced by real API-surface baseline tests (tracked baselines, `.actual` drift dumps).
Verified: 43 tests green (DPI math, state roundtrips, visibility strips, mutex acquire/release
across guards, argument composition), `verify` gate green.

### 2026-07-30 — P0: repo bootstrap

The repo was created from the family preset and turned into a library devkit workspace. All five
sibling repos were surveyed in parallel (the library-template repo, the org-system/host donor, and
the three desktop apps) to produce the extraction map — the brief's Phase 1 audit — recorded in
`local/EXTRACTION-MAP.md` (named) and `.claude/knowledge/extraction-sources.md` (tracked,
de-identified). The design contract (`docs/2026-07-30-shenora-design.md`) and the decision log
(`docs/DECISIONS.md` D1–D12) were written: two consumption profiles, four NuGet packages + one
npm package, net10.0, lockstep versioning, manual OIDC release, no push CI. The Sonora-preset
devtools/rules/skills were culled to the generic core and re-targeted; the docs system
(router/ARCHITECTURE/ROADMAP/TASKS/FIX-LOG/DECISIONS/CHANGELOG), the buildable solution skeleton,
the rewritten devtools (`build`/`test`/`verify`/`pack`/`doctor` + the desktop verification loop),
the release workflow, and the git repo + pre-commit guard were set up. Verified: `dev.mjs verify`
green (dotnet build + tests, npm build + tests, sensitive scan, knowledge check).

## Remaining

### P1 — Skeleton hardening (short tail)

- Decide + pin the placeholder public types that P2 replaces (keep minimal; no speculative API).
- First sibling-consumption smoke: pack locally, restore from the local feed in a scratch consumer.

### P2 — Core host extraction (brief Phase 2) — COMPLETE except deliberate carry-overs

Everything above landed (increments 1–6, see Done). Carried forward on purpose:
- **DPI tail → P4** (`OnDpiChanged` handling + CSS-px→physical conversion) — lands with the
  overlay components that need it (drop zones, login windows).
- **Optimized form / frameless chrome → P4** — lands with the window manager + frontend window
  commands.
- **Stable-chunk frontend build guidance** (docs) → written with the P3 `@shenora/react` docs,
  where frontend build advice naturally lives.

### P3 — IPC extraction (brief Phase 3)

- `Shenora.Ipc`: envelopes, middleware dispatcher, facade base, structured errors, serializer
  defaults. `Shenora.WebView2`: postMessage bridge + 50 ms batched notifications.
- `@shenora/react`: bridge (correlation, timeout, category routing, batch unbundle, ready
  handshake, browser fallback), typed module-service base, event hub + hooks (event
  subscription, drop zone, and the harvested behavior hooks — stable refs, delayed loading,
  scroll position…), dev interceptor. Headless throughout (D13) — no component library.
- Sample round-trip: React → typed .NET handler → typed response; native events in React.
- e2e: drive the sample over CDP/win-input; assert the round-trip.

### P4 — Modules + native services (brief Phase 4)

- `IShenoraModule` registration (services + IPC handlers + lifecycle hooks); scoped-container
  router seam (generalized from the profile router).
- Window manager + frontend window commands (frameless-chrome option, minimize/maximize/close/
  drag/resize routes); secondary windows with `IWindowGeometryStore`.
- STA file/folder/save dialogs, clipboard, shell open/reveal, drag-drop overlay manager +
  `useDropZone`, single-instance surface, tray icon support.

### P5 — Auxiliary browser sessions (`Shenora.WebView2.Sessions`, D14)

- The multi-form/aux-browser stack proven across the siblings, generalized: the "one place a
  WebView2 gets configured" initializer (+ init-timeout guard), offscreen render sessions with
  a bounded LIFO pool, per-provider/per-account persistent login-window profiles with clear-on-
  logout, driveable session primitives (navigate/read/execute, UI-thread marshalled), and
  co-browse streaming (CDP screencast frames over a socket out, human input dispatched back —
  captcha/login flows stay human-solved by design).
- Own package so the core hosting package stays lean; the sample app gains a demo page for each.

### P6 — Sibling adoption (brief Phase 5)

- Adopt in the newest desktop sibling first (smallest host, gaps already documented), via local
  feed + pinning; keep it runnable at every step. Then evaluate the other two desktop siblings
  and the server-backed app (shell-only profile).
- Feed every "the framework almost fits, but…" back into the API before 1.0.

### P7 — Stabilisation + 1.0 (brief Phase 6)

- API-surface baseline tests on; docs pass (XML docs, README per package section); CHANGELOG
  discipline from first publish; `Shenora.Hosting.AspNetCore` go/no-go (D10); first NuGet/npm
  publish via the release workflow; GitHub repo goes public.

### Later / candidates

- `Shenora.Hosting.AspNetCore` (SPA static policy, loopback-gated endpoint helpers) — D10.
- Mobile transport adapter (Capacitor or similar speaking the same IPC envelope) — D16; packaged
  at first mobile adoption (`@shenora/capacitor` vs an adapter in `@shenora/react`).
- Harvest-promotions from ongoing app development (D15) — any proven-nice feature gets
  generalized and lands here as a task before shipping in a minor.
- C++ launcher template (runtime check/install, staged self-update) as a repo template, not a package.
- Scaffolding skills once patterns exist (`new-ipc-module`, `new-native-service`).
- Contract codegen (C# ⇄ TS) — explicitly out of initial scope; revisit after adoption feedback.
