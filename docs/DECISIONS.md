# DECISIONS.md — the load-bearing choices and why

Numbered rationale log so a future session doesn't relitigate them. Amend an entry by appending
a dated note (or a later entry that supersedes it) — never silently rewrite.

- **D1 — Shenora is the desktop body; Lyntai is the brain; no dependency between them.** Apps may
  use both; Shenora must never reference Lyntai (brief, "Relationship to Lyntai"). Keeps each
  library adoptable alone.

- **D2 — Package set: `Shenora.Core` / `Shenora.Ipc` / `Shenora.WebView2` / `Shenora.WinForms` (NuGet)
  + `@shenora/react` (npm).** No separate `Shenora.Modules` package — module registration is core
  plumbing and lives in `Shenora.Core` (the brief's own solution-structure section agrees with
  this even though its package sketch listed one). No `Shenora.Extensions.DependencyInjection`
  either: standard Microsoft DI abstractions are used directly (brief requirement), so there is
  nothing to put in it yet.
  *(Amended 2026-07-30: the set is now FIVE NuGet packages + npm — `Shenora.WebView2.Sessions` was
  added per D14. A sixth, `Shenora.Shell.Abstractions`, was considered and rejected per D20.)*

- **D3 — Single TFM `net10.0` / `net10.0-windows`, not the brief's ".NET 8".** The brief predates
  the survey: every family app and Lyntai target .NET 10, the dev machine has no .NET 8 SDK, and
  .NET 8 LTS reaches end-of-support 2026-11 — multi-targeting a nearly-dead TFM buys nothing.
  Revisit only if an external consumer asks.

- **D4 — Lockstep versioning from one `<VersionPrefix>` in `src/Directory.Build.props`** (the
  Lyntai model), including the npm package: devtools `pack`/`doctor --fix` write the npm
  `package.json` version and the README status headline from it; `doctor` fails on drift. One
  version story across two registries beats two drifting ones.

- **D5 — No push/PR CI; verification is local (`dev.mjs verify`); releases are a single manual
  `workflow_dispatch` workflow.** Family precedent: Lyntai added push CI and the owner removed it
  the same day (its DECISIONS D20). Don't re-add as a "gap".

- **D6 — Publishing: NuGet Trusted Publishing (OIDC, no stored API key); npm publish with
  `--provenance` via OIDC trusted publishing (fallback: a granular `NPM_TOKEN` secret until the
  npm trusted-publisher policy is configured).** The version bump is committed only after both
  publishes succeed — a failed release leaves no phantom bump.

- **D7 — One test project (`tests/Shenora.Tests`) referencing every src project** (in practice the
  four leaf projects; `Shenora.Core` arrives transitively), not
  per-package test projects (the brief sketched four). Lyntai proves the single-project layout
  scales to 11 packages; folders mirror src. API-surface baseline tests gate SemVer from the
  first release.

- **D8 — Extraction-first.** Prefer lifting proven sibling code — including its post-mortem
  comments, which are the product — over new abstractions (brief instruction). The primary source
  is the richest desktop-only sibling; the second desktop sibling is the conformance reference;
  Sonora donates its window-state store, singleton/restart skeleton and event bridge. The named
  map lives in `local/EXTRACTION-MAP.md` (private); the de-identified version in
  `.claude/knowledge/extraction-sources.md`.

- **D9 — Repo organization clones the family system**: Sonora's four-layer memory model (short
  `CLAUDE.md` → `docs/README.md` router → two-tier `.claude/rules|knowledge` with
  `RULES_INDEX.md` → gitignored `local/`), `TASKS.md` ⇄ `docs/ROADMAP.md` conveyor,
  `docs/FIX-LOG.md`, plus Lyntai's library-repo docs (`DECISIONS.md`, `CHANGELOG.md`,
  design-contract doc). Done work is archived narratively in `docs/ROADMAP.md` `## Done` rather than
  in a separate task-archive file.

- **D10 — Two consumption profiles; server-backed hosting helpers are deferred.** The package
  split (Ipc separate from the shell packages) exists so a Sonora-style app (in-process HTTP
  server shared with mobile clients; WebView2 as "just a browser" + optional one-way event push)
  can adopt the shell without the postMessage command bridge. A `Shenora.Hosting.AspNetCore`
  package (SPA static-file policy, loopback-gated endpoint helpers) is a candidate later
  addition — out of initial scope to keep the first releases small.

- **D11 — IPC envelope follows the proven family shape** (`{id, module, type, payload, timestamp}`
  request; category-wrapped response; ~50 ms-batched notification array), not the brief's
  `route`-string sketch. Two shipped apps already speak it, migration stays mechanical, and the
  notification envelope doubles as the WebSocket wire format in the server-backed profile.
  Ergonomic `"module.action"` route helpers can sit on top in `@shenora/react`.

- **D12 — Sibling names stay out of tracked files.** Lyntai and Sonora are public repos by the
  same author and may be named; the three private siblings are referred to generically ("the
  primary desktop sibling", …) in tracked docs. Real names/paths live only in `local/`
  (enforced by the pre-commit guard's private patterns).

- **D13 — Headless: no UI component library dependency, ever.** (User direction, 2026-07-30.)
  Shenora is infrastructure — the shell applications boot their own logic and design system on.
  `@shenora/react` ships bridge/hooks/behaviors only (no components styled by a library, no
  antd/mui/etc. dependency); the WinForms side ships neutral primitives (splash, tray theming)
  with parameterized colors, never a styled control set. Apps bring their own UI.

- **D14 — The auxiliary browser subsystem is in scope** (user direction, 2026-07-30): offscreen
  render sessions with a bounded session pool, login windows with per-provider persistent
  profiles, and co-browse streaming (CDP screencast frames out, human input back). Proven in two
  siblings (one's render/login/co-browse stack; another's external-login window). Ships as its
  own later package (working name `Shenora.WebView2.Sessions`) so the core hosting package stays
  lean — phase P5 in `docs/ROADMAP.md`.

- **D15 — Growth is harvest-driven** (user direction, 2026-07-30). Shenora evolves by promotion:
  when something proves nice while an application is being developed, it gets generalized (per
  `generic-library`) and moved into Shenora in a minor release — common design/library/tool
  sharing, not speculative roadmap features. The app keeps a thin wrapper; the framework gains
  the proven core. This extends D8 (extraction-first) from a bootstrap strategy into the
  permanent operating model.

- **D16 — Mobile shells are a target, via transport-pluggable IPC** (user direction,
  2026-07-30). The client bridge's envelope (D11) is transport-neutral by design; a Capacitor
  (or similar) native shell implements the same request/response/notification contract over its
  own channel, so application logic written against `@shenora/react` runs unchanged on desktop
  (WebView2 postMessage), in a browser (fallback/HTTP), and in a mobile shell. One sibling
  already ships Capacitor mobile clients — its native-bridge seam is the proof shape. Concrete
  packaging (`@shenora/capacitor` transport adapter vs an adapter inside `@shenora/react`) is
  decided when the first mobile adoption happens, not before (YAGNI on the package, not on the
  seam).

- **D17 — `Shenora.Core` depends on the Microsoft DI IMPLEMENTATION package, not only the
  abstractions.** The builder (`ShenoraApplication.CreateBuilder` → `Build()`) constructs the
  application's `ServiceProvider`, and `BuildServiceProvider` lives in
  `Microsoft.Extensions.DependencyInjection`, not in `.Abstractions` — the same dependency shape
  as `Microsoft.Extensions.Hosting`. Contracts still bind to the abstractions; a pluggable
  third-party container is deliberately NOT offered (no family app uses one — revisit only if
  adoption demands it). Supersedes the "abstractions only" wording in the design contract §4
  (amended there, dated).

- **D18 — The library is Shenora (神阙); git history restarted at the rename.** (User direction,
  2026-07-30, at the P2/P3 boundary.) The kit was built under an earlier private working name;
  before anything was published the owner renamed it — 神阙 pairs with the sibling 灵台 (Lyntai)
  as an acupoint name, and the ending echoes Sonora. Everything renamed in lockstep: packages
  (`Shenora.Core|Ipc|WebView2|WinForms`, `@shenora/react`), namespaces and `Shenora*` type
  names, repo files/docs/rules. Because the rename predates any release or remote, history was
  restarted as a single bootstrap commit rather than rewritten — the per-phase narrative lives
  in `docs/ROADMAP.md` `## Done` (the durable record by design), and the pre-rename history is
  kept privately offline. The former name stays out of tracked files and commit messages
  permanently (same discipline as the private sibling names, `sensitive-info`).

- **D19 — The two Windows shell packages are ONE layer: `Shenora.WebView2` depends on
  `Shenora.WinForms`.** (User direction, 2026-07-30, after the first full code review.) The design
  contract §4 forbade the sideways edge *"revisit only if extraction proves it impossible"* —
  extraction proved it: the UI-thread marshal pattern ended up hand-rolled **14 times across 3
  packages with 5 incompatible pre-handle policies**, and the divergence produced real defects (7
  unguarded `BeginInvoke`s in the sessions package; a site whose comment explains the pre-handle
  trap and then commits it on the next line; a P0 where `RenderSession` accepts cancellation tokens
  it cannot observe). Deciding facts: `Shenora.WebView2` is ALREADY a WinForms assembly — its csproj
  sets `<UseWindowsForms>true</UseWindowsForms>`, it hosts the `Microsoft.Web.WebView2.WinForms`
  control, and 5 of its files use a `Form`/`Control`-derived type — so the edge adds no new
  *technology* dependency, only an honest package reference; and **neither consumption profile (§3)
  takes `WinForms` without `WebView2`**, so that split served no profile. The boundary is now
  **primitives → hosting-on-primitives**, all edges still strictly downward. UNCHANGED and still
  load-bearing: `Shenora.WinForms` carries NO `Shenora.Ipc` dependency — the reason is that it keeps a
  **WinForms-only consumer** viable (a tray/single-instance utility with no web frontend), and it is
  why the window-command and drop-zone facades stay in `Shenora.WebView2`. (NOT because profile 2
  avoids `Ipc`: `Shenora.WebView2` references `Shenora.Ipc`, so profile 2 receives it transitively and
  merely doesn't use the postMessage bridge — an earlier draft of this entry claimed otherwise.)
  `WinForms` never references `WebView2`. Rejected:
  merging into one `Shenora.Windows` package (preserves no WebView2-free option, much larger diff
  for the same benefit) and sharing via a linked source file (two binaries carrying the same
  internal type; solves the least). Full design: `docs/2026-07-30-shenora-relayering-design.md`.

- **D20 — Portable contracts live in `Shenora.Core`; only Windows implementations live in
  `Shenora.WinForms`.** (User direction, 2026-07-30.) The reusable part of a desktop kit is the
  *logic* — IPC and the feature contracts — because that is what a non-Windows shell (mobile, D16)
  can share; an app's facades should compile with no Windows reference. So the platform-neutral
  contracts move to `Shenora.Core` (`IClipboardService`, `IFileDialogs`/`IFileDialogPathStore` +
  their models, a portable `IUrlLauncher` and `IUiInteraction` base for the mixed
  `IShellLauncher`/`IFormInteraction`), and `IUiDispatcher` — specified in design contract §4 and
  never built in P2 — is added there as the one UI-thread marshalling seam: a public
  `WinFormsUiDispatcher(Control)` (per-control, consumed by the WebView2 packages) plus an internal
  `MainFormUiDispatcher` for the DI singleton. Its contract carries a **three-state** target
  (`NotReady`/`Ready`/`Gone`) rather than one bool, because three call sites have review-earned
  pre-handle policies that a bool would silently break. This restores original intent: Core's shipped NuGet description
  already advertised a "UI-dispatcher seam" that did not exist. Home is Core, NOT a sixth
  `Shenora.Shell.Abstractions` package — D2 resists speculative packages and §4 already placed
  these seam types in Core. Scope guard: contracts move only when **app logic needs them to compile
  off Windows** — portable-in-signature is not the bar, which is why the whole window-state stack
  stays in `Shenora.WinForms` (window geometry is a desktop concept). Per D16 this pass ships NO
  mobile host or transport adapter — the seam, not the package.

- **D21 — For a whole application FEATURE, the kit ships primitives + lifecycle hooks; the app owns
  the product.** (User direction, 2026-07-30: *"co-browse itself is a whole feature — you just need
  to provide enough interface for other systems to plug/hook onto its cycle; you don't really need to
  implement the entire business feature."*) The test for any feature-shaped addition: **could a
  consumer build its own version of this product on our primitives, without adopting our product
  decisions?** If not, we shipped too much — or we shipped too few hooks.
  Shenora already followed this twice: `RenderSessionPool` ships the pool + session and deliberately
  did NOT port the sibling's render/analyze flows (the sample writes its own `RENDER` route), and
  `LoginWindow` keeps policy in a driver seam with `CookieLoginFlow` as ONE opt-in reference driver.
  `CoBrowseSession` is the outlier, and the audit that produced this entry is concrete: `Frames`
  (bounded latest-wins channel), `StartAsync`/`DisposeAsync` and the 1:1 device-metrics viewport
  mirroring are genuine primitives — CDP screencast, its ack protocol and stale-frame dropping are
  exactly what a library should absorb. But `DispatchInputAsync(string)` takes **the source app's wire
  protocol as an opaque JSON string** (a consumer cannot know what to pass without reading that app's
  client — "ship the consumer's shape" in its purest form, chosen for mechanical adoption), and
  `ReadHotspotsAsync()` returns a stringly-typed clickable-rect list, which is a co-browse **UX**
  decision. Meanwhile the hooks that make a feature extensible are MISSING: nothing signals the
  session ending or faulting (`ProcessFailed` is unwired, so a renderer crash leaves the frame channel
  never completed and the app's reader waiting forever) and frames carry no geometry, so an app cannot
  map input coordinates. Target shape: session lifecycle + frames out + **typed** input in + hooks
  (started / frame / navigated / ended-or-faulted) + the neutral session controller; transport,
  viewer UI, hotspot UX, permissions and recording belong to the app.
  Accepted cost: the sibling that already speaks the verbatim JSON protocol needs a thin mapping shim
  at adoption — consistent with D15 (the app keeps a thin wrapper; the framework gains the proven
  core). Tracked as `TASKS.md` P5.5 batch H9, deliberately AFTER the re-layer so an API redesign is
  not mixed into a package-boundary move.
