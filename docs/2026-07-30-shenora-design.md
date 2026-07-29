# Shenora — design contract (2026-07-30)

**Status: approved direction for the initial build-out.** Derived from `docs/BRIEF.md` (the
originating requirements) plus a full survey of the sibling applications this framework is
extracted from. Amend in place with dated notes; record load-bearing choices in
`docs/DECISIONS.md`.

## 1. What Shenora is

Shenora (神阙) is the reusable **desktop body** for the family's Windows applications: a set of
NuGet packages plus one npm package that provide WinForms + WebView2 hosting for a React
frontend — application host, lifecycle, typed IPC, module registration, window management, and
native desktop service abstractions. Domain logic stays in the consuming apps. Its counterpart
Lyntai (灵台) is the reusable AI brain; Shenora must never depend on Lyntai (apps may use both).

Four sibling applications were surveyed before this design. Three are desktop-only WinForms +
WebView2 + React apps that each hand-rolled the same host layer (one of them literally annotates
its drag-drop code as ported from another); the fourth (Sonora, public) runs an in-process
Kestrel server so LAN/mobile clients share the backend, and uses its WebView2 window as "just a
browser" with a one-way batched event push. Shenora exists to end that per-app duplication:
**extract the proven code once, close its known gaps, and give every future app the same body.**

## 2. Goals / non-goals

Goals (initial releases):
- A developer can build a WinForms+WebView2+React app with `ShenoraApplication.CreateBuilder(...)`,
  typed IPC to .NET handlers, native events in React, file/folder dialogs, window control from
  the frontend, modules, Vite dev-server mode, and packaged static frontend mode (embedded
  resources or a virtual host).
- Extraction-first: prefer lifting proven sibling code (with its hard-won comments) over new
  abstractions. The framework's opinions are the family's measured lessons.
- Ship as `Shenora.Core`, `Shenora.Ipc`, `Shenora.WebView2`, `Shenora.WinForms` (NuGet) and
  `@shenora/react` (npm), versioned in lockstep.

Non-goals (per the brief, unchanged): domain entities, business workflows, AI/LLM orchestration,
app screens/branding, plugin marketplaces, contract codegen, cross-platform shells, alternative
webview engines. The sibling apps consume Shenora; they do not move into it.

## 3. The two consumption profiles

The package split exists so both family architectures are first-class:

1. **Desktop-only profile** — `Core + WinForms + WebView2 + Ipc + @shenora/react`. Commands and
   events flow over correlated `window.chrome.webview.postMessage` IPC. This is the brief's
   architecture and the shape of the three desktop-only siblings.
2. **Server-backed profile (desktop + mobile)** — `Core + WinForms + WebView2` only. The shell
   hosts a WebView2 pointed at the app's own in-process HTTP server; commands stay HTTP so phones
   reach the same API; the batched WebView2 event push remains available as an optional fast-path
   (same envelope as the WebSocket the remote clients use). A `Shenora.Hosting.AspNetCore` helper
   package (static-file policy, loopback-gated endpoints) is a candidate later addition — not
   initial scope (D10).

Consequence: `Shenora.Ipc`'s **contracts** are transport-neutral, and `@shenora/react`'s event
layer is transport-pluggable (postMessage or WebSocket, one envelope).

## 4. Package architecture

| Package | TFM | Depends on | Contents |
|---|---|---|---|
| `Shenora.Core` | `net10.0` | M.E.DependencyInjection.Abstractions, M.E.Logging.Abstractions | Application/builder/lifetime abstractions, `IShenoraModule` registration, environment (`IsDevelopment` + `.dev` marker), app paths service, `IUiDispatcher` interface, options types, startup-cleanup pipeline, event bus. |
| `Shenora.Ipc` | `net10.0` | `Shenora.Core` | Request/response/notification envelopes, middleware dispatcher (`Use`/`MapModule`/`MapRoute`), handler + facade base, structured `OperationException` (code + params, i18n-ready), payload helpers, `System.Text.Json` serializer defaults (camelCase, camelCase enums). Transport-neutral. |
| `Shenora.WebView2` | `net10.0-windows` | `Shenora.Core`, `Shenora.Ipc`, Microsoft.Web.WebView2 | Environment prewarmer/factory, host/initializer driven by an options record (dev URL, virtual host, custom schemes, background color, injected scripts), embedded-resource provider (assembly + prefix parameters), dev/prod navigation, `NewWindowRequested`/`DownloadStarting`/`PermissionRequested`/`ProcessFailed` hooks, runtime presence check, postMessage bridge + batched event push, settings hardening. |
| `Shenora.WinForms` | `net10.0-windows` | `Shenora.Core` | Bootstrapper (STA, DPI, **global exception handlers**), optimized main form + frameless-chrome option, window-state persistence (DPI-logical store/physical restore, off-screen recovery), single-instance guard + activate-broadcast, secondary windows on own STA threads (`IWindowGeometryStore` seam), STA file/folder/save dialogs, clipboard, shell open/reveal, drag-drop overlay manager, tray icon support, form-interaction (modal blocking), splash panel, `IUiDispatcher` implementation, DPI helpers. |
| `@shenora/react` | — | react ≥18 peer | Bridge (correlation ids, timeout, category routing, batch unbundling, ready handshake, browser fallback seam for pure-UI dev), typed module-service base, event bus + `useShenora`/`useShenoraEvent`/`useShenoraQuery`, drop-zone hook, window-command helpers, dev interceptor (ring buffers + `window.__shenora` for CDP-driven testing). |

Dependency rules (the Lyntai discipline): `Core` stays tiny; packages depend only downward
(never sideways `WinForms`↔`WebView2` — the app composes them; revisit only if extraction proves
it impossible). Everything `IsPackable` lives under `src/`; tests/samples never are.

## 5. IPC contract

Adopt the proven family envelope rather than inventing per the brief's sketch (D11):

- Request: `{ id, module, type, payload?, timestamp }` (+ an optional app-defined scope field —
  the generalization of one sibling's per-profile routing).
- Response (wrapped for routing): `{ category: "ipc", id, success, data?, error: { code, message?, parameters? } }`.
- Notifications (host → page), batched every ~50 ms:
  `{ category: "notification", ... payload: [{ module, type, payload }, ...] }` — same envelope a
  WebSocket transport carries in the server-backed profile.
- Dispatcher: composable middleware (error handler → logging → app middleware → module facades),
  with programmatic `SendAsync<T>` so backend services and plugins share the pipeline.
- Errors: structured code + parameters end-to-end; raw exceptions are logged host-side and never
  cross the bridge (brief requirement, proven pattern).
- Threading: handlers dispatch on the UI thread via async interleaving (the surveyed post-mortem:
  a Task.Run-per-message design starved the pool and froze the app); all host→page posts marshal
  with non-blocking `BeginInvoke`; UI-affine work goes through `IUiDispatcher`.

## 6. What Shenora fixes that the sources lack

These are the survey's cross-cutting gaps — table stakes for a framework, absent in every source:

1. Global unhandled-exception handling (`Application.ThreadException`,
   `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) + a last-resort
   crash dialog and log.
2. WebView2 Evergreen runtime presence check with an actionable install prompt.
3. `NewWindowRequested` (→ system browser, scheme-checked), `DownloadStarting`,
   `PermissionRequested`, `ProcessFailed` (renderer-crash recovery) handling.
4. A real dev/prod switch everywhere (one sibling has none — its docs record a recurring
   stale-bundle footgun).
5. Options records instead of scattered magic numbers (dev port, colors, timeouts, batch
   intervals); JS injection done with proper escaping.
6. Request/response correlation for apps whose hand-rolled IPC is fire-and-forget only.

## 7. Repo, versioning, release (the Lyntai model)

- Layout: `src/` (packable projects + `Shenora.React/` npm package + `Directory.Build.props` +
  `Directory.Packages.props`), `tests/Shenora.Tests` (single xUnit project; API-surface baseline
  tests once a public surface exists), `samples/` (added in Phase 2 — the sample app doubles as
  the e2e subject), `bench/` if ever needed, `devtools/`, `docs/`, `local/` (gitignored).
- One `<VersionPrefix>` in `src/Directory.Build.props` is the only version source; devtools
  syncs the npm package version and the README status headline from it; `doctor` fails on drift.
- All packages (NuGet + npm) version in lockstep, SemVer from 1.0 (family amendment: while every
  consumer is in-house, a documented break may ship in a minor — always under a `### Breaking`
  CHANGELOG heading).
- **No push/PR CI** (family precedent, deliberate): verification is `node devtools/dev.mjs verify`
  locally. **One manual `workflow_dispatch` release workflow**: verify → pack → publish NuGet via
  Trusted Publishing (OIDC, no stored key) → publish npm (provenance) → only then commit the
  version bump → tag → generated release notes → draft GitHub release, no attached binaries.
- Pre-release consumption: siblings consume Shenora from the local pack output as a NuGet local
  feed / npm file dependency, pinned — the exact model one sibling already uses for Lyntai.
- Guards: pre-commit `check-sensitive` (private patterns in `local/`), `knowledge check`
  consistency, sensitive-info rule (this repo is public from day one of publishing).

## 8. Testing strategy

- Unit tests for the pure seams as they're extracted (the sources already prove these testable:
  browser-argument builder, DPI math, single-instance guard, envelope serialization).
- API-surface approval tests with tracked baselines (SemVer enforcement) from the first release.
- Sample-app e2e: the sample desktop app is driven over CDP + native input (`win-input`) with
  screenshot verification (`wgc-shot`) — the family's proven desktop verification loop, already
  present under `devtools/`.
- `@shenora/react`: Vitest unit tests + a browser-mode bridge fake so the library is testable
  without a host.

## 9. Phasing

Tracked in `docs/ROADMAP.md` (P1 skeleton/infra → P2 core host extraction → P3 IPC → P4 modules +
native services → P5 auxiliary browser sessions → P6 sibling adoption → P7 packaging/1.0). The
brief's Phase 1 audit is complete:
the classification of every source component (generic / app-specific / mixed-needs-seam) is
recorded privately in `local/EXTRACTION-MAP.md` with a de-identified summary in
`.claude/knowledge/extraction-sources.md`.

## Amendments

**2026-07-30 (user direction — scope sharpened).** Shenora is the shared infrastructure kit — a
"UI kit for non-web applications" in the *headless* sense — for ALL sibling projects: it holds
the desktop shell that each application boots its own logic on, so common problems stop being
re-solved per project. Two consequences now explicit:

1. **Headless, always (D13):** no dependency on any UI component library anywhere.
   `@shenora/react` is bridge + hooks + behaviors; apps bring their own design system.
2. **The full duplicated-work surface is in scope**, mapped to phases: multi-form/multi-window
   management (P4), drag-drop zones (P4), IPC package design (P3), the event hub (P3), frontend
   display optimizations as first-class presets (P2), the React hooks layer (P3), and
   co-browsing / auxiliary browser sessions — offscreen render pool, login windows, co-browse
   streaming — as its own later package (P5, D14).
3. **Growth is harvest-driven (D15):** features are promoted into Shenora when they prove nice
   during application development — generalized per `generic-library`, shipped in a minor.
4. **Mobile shells are a target (D16):** the IPC envelope stays transport-neutral so a
   Capacitor-style native shell speaks the same contract; app logic on `@shenora/react` runs
   unchanged across desktop, browser, and mobile. Packaging of the mobile transport adapter is
   decided at first mobile adoption.

**2026-07-30 (P2 increment 4 — §4 corrections from the build-out).** Two as-built notes:

1. `Shenora.Core`'s dependency row said "M.E.DependencyInjection.Abstractions"; the shipped
   builder needs the DI **implementation** package (`BuildServiceProvider` lives there), so Core
   depends on `Microsoft.Extensions.DependencyInjection` + `M.E.Logging.Abstractions` (D17).
2. How the builder honors the downward-only dependency rule: `ShenoraApplication`/
   `ShenoraApplicationBuilder` live in Core with an `IShenoraRunner` seam and DI-registered
   `IShenoraLifecycleHook`s; `Shenora.WinForms` (`UseWinForms`) and `Shenora.WebView2`
   (`PrewarmWebView2`) contribute through extension methods over the Core builder. The packages
   still never reference each other — the app that references both composes them.
