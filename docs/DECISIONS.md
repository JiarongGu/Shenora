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

- **D7 — One test project (`tests/Shenora.Tests`) referencing every src project**, not
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
  design-contract doc, task archive).

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
