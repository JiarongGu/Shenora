# DECISIONS.md — the load-bearing choices and why

Numbered rationale log so a future session doesn't relitigate them. Amend an entry by appending
a dated note (or a later entry that supersedes it) — never silently rewrite.

> 🔴 **A NUMBER IS A PERMANENT ADDRESS. Never reuse one, never renumber a cited entry, and check the
> highest number before writing a new one.** Code cites these — `Mp4Remuxer` says `D51`, `UpdateStage` says
> `D50`, `IMissionScheduler` says `D27–D31`, and those are **shipped XML docs on nuget.org**, so a
> renumber silently redirects a published reference.
> ⚠ **This rule was earned: `D51` was written TWICE on consecutive days and the collision survived four
> sessions**, because the file is appended to at the bottom and nobody reads the middle. The duplicate was
> found on 2026-08-07 and the loser — which nothing cited — became **D60**. When two entries share a
> number, the one with citations keeps it; the orphan moves.
> **A SUPERSEDED entry keeps its number and becomes a tombstone** pointing at what replaced it (see
> `D40 · D41`), so a citation always lands somewhere that explains itself.

> **The package set lives HERE, once** (2026-08-05). Seven entries have moved it — D2 drew it, D37
> reorganised it by platform, D40 added an optional feature package, D48 added a family of them, D50
> added the native launcher, **D53 folded media back into Core** and **D55 folded the IO family in after
> it** — and reconstructing it from that chain is how three of them ended up stating a set that no longer
> existed.
> **As of 2026-08-07 there are SIX packable projects + npm:**
>
> | | | |
> |---|---|---|
> | **shells** (D37) | `Shenora.Core` · `Shenora.Ipc` · `Shenora.Windows` · `Shenora.Android` · `Shenora.iOS` | one per platform |
> | **native** (D50) | `Shenora.Launcher` | C++ sources + per-RID binaries; NO managed surface |
> | **npm** | `@shenora/react` | |
>
> 🔴 **There is no longer an "optional features" tier at all** (D55, 2026-08-07). The framework is ONE
> whole: `Shenora.Core` + a shell + `@shenora/react`. A capability that grows big enough to look like a
> library gets a FOLDER, not a package.
>
> ⚠ **`Shenora.Media`, `Shenora.IO` and `Shenora.IO.Compression` are no longer packages (D53, D55) — but
> all three are still live NAMESPACES inside `Shenora.Core`.** The ids are retired and the namespaces are
> current, which is why those names are deliberately NOT in `devtools/retired-names.txt`: the gate matches
> names and cannot tell a package id from a namespace, so registering them would fire on every correct
> sentence in the repo.
>
> `docs/ARCHITECTURE.md` is the as-built map and `doc-drift` gates it against the csproj files. **When an
> entry below names a package set, read it as the set AT ITS DATE.**
>
> ⚠ This block went stale within a day of being written as the one place the set lives: D50 landed
> `Shenora.Launcher` and the count above still said eight, caught only by reading it against `doctor` after
> the 0.10.0 release. A prose count is not gated by anything — **`node devtools/dev.mjs doctor` prints the
> real number of packable projects, so check it here rather than trusting the sentence.**

- **D1 — Shenora is the BODY; Lyntai is the brain; no dependency between them.** Apps may
  use both; Shenora must never reference Lyntai (brief, "Relationship to Lyntai"). Keeps each
  library adoptable alone. **This half has never changed and is the one thing D1 is cited for.**
  ⚠ **"the DESKTOP body" was the original wording and it is wrong as of 2026-08-07.** The kit is a
  **hybrid app framework — .NET + React — across Windows, Android and iOS** (D32 made a second shell a
  PEER, D37 named one package per platform, D53–D55 settled the identity, and `CLAUDE.md`'s opening now
  carries it). Corrected in place rather than tombstoned because the body/brain split is what D1 means and
  it still holds exactly; only the word "desktop" had gone stale, and it sat in entry #1 where every reader
  starts.

- **D2 — Package set: `Shenora.Core` / `Shenora.Ipc` / `Shenora.WebView2` / `Shenora.WinForms` (NuGet)
  + `@shenora/react` (npm).** No separate `Shenora.Modules` package — module registration is core
  plumbing and lives in `Shenora.Core` (the brief's own solution-structure section agrees with
  this even though its package sketch listed one). No `Shenora.Extensions.DependencyInjection`
  either: standard Microsoft DI abstractions are used directly (brief requirement), so there is
  nothing to put in it yet.
  🔴 **THE SET NAMED ABOVE IS OBSOLETE — read the header table, never this line.** It has been redrawn
  four times since (D14, D37, D53, D55) and the amendment stack that used to live here was wrong twice
  over by 2026-08-07: it announced three optional feature packages that no longer exist, and it concluded
  that "a package for optional WEIGHT earns its keep", which **D55 reversed outright**. Replaced with one
  pointer on 2026-08-07 rather than a fifth amendment — the header table is the single place the set lives,
  and this entry kept contradicting it.
  - **What survives, and is still the rule:** a package for a SEAM or an `*.Abstractions` split earns
    nothing — Core already holds the contracts. That instinct was right on day one and has never been
    overturned; what changed is that WEIGHT is no longer an exception to it (D55).
  - **Why the original split is still worth reading:** it records what a package boundary was thought to
    buy before any of them had consumers, which is the mistake D37, D53 and D55 each had to undo.
    Current rule: `.claude/knowledge/generic-library.md`. Current set: the header table.

- **D3 — One .NET VERSION, .NET 10, not the brief's ".NET 8".** The brief predates
  the survey: every family app and Lyntai target .NET 10, the dev machine has no .NET 8 SDK, and
  .NET 8 LTS reaches end-of-support 2026-11 — multi-targeting a nearly-dead .NET version buys nothing.
  Revisit only if an external consumer asks.
  **⚠ Amended 2026-08-05 (a review found this entry stating the opposite of what the tree does).** It
  was written as "single TFM `net10.0` / `net10.0-windows`" and said multi-targeting "buys nothing",
  which two later decisions overtook:
  - **Platform TFMs multiplied with the shells** — `net10.0-android` and `net10.0-ios` (D16 → D32 → D37).
    Portable code is still plain `net10.0`, which is the part of this entry that was actually load-bearing.
  - **`Shenora.Windows` deliberately MULTI-TARGETS** `net10.0-windows` + `net10.0-windows10.0.17763.0`
    (D46), so a WinRT-only capability does not force every Windows consumer to raise their minimum OS.
  **What survives is the VERSION rule, not a TFM list:** one .NET version across the kit, and a TFM added
  only when a real capability needs it. D46 is the bar for adding one.

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

- **D7 — One test project (`tests/Shenora.Tests`) referencing every src project it CAN** (in practice
  the leaf projects — five as of 2026-08-05: `Ipc`, `Windows`, `Media`, `IO`, `IO.Compression`;
  `Shenora.Core` arrives transitively), not
  per-package test projects (the brief sketched four). Lyntai proves the single-project layout
  scales to 11 packages; folders mirror src. API-surface baseline tests gate SemVer from the
  first release.
  **⚠ "Every src project" has an exception, and it is why D34 exists:** a `net10.0-windows` test project
  cannot reference `Shenora.Android` or `Shenora.iOS`, so those two are gated from their IL METADATA
  instead (`MetadataSurfaceTests` + `Api/MetadataBaselines/`). A packable project with neither kind of
  baseline fails a test — count coverage against `src/`, never against this sentence.

- **D8 — Extraction-first.** Prefer lifting proven sibling code — including its post-mortem
  comments, which are the product — over new abstractions (brief instruction). The primary source
  is the richest desktop-only sibling; the second desktop sibling is the conformance reference;
  Sonora donates its window-state store, singleton/restart skeleton and event bridge. The named
  map lives in `local/EXTRACTION-MAP.md` (private); the de-identified version in
  `.claude/knowledge/extraction-sources.md`.

- **D9 — Repo organization clones the family system**: Sonora's four-layer memory model (short
  `CLAUDE.md` → `docs/README.md` router → two-tier `.claude/rules|knowledge` with
  `RULES_INDEX.md` → gitignored `local/`), plus Lyntai's library-repo docs (`DECISIONS.md`,
  `CHANGELOG.md`).
  🔴 **Amended 2026-08-07 — there is no archive tier at all now, and the reason is worth more than the
  layout.** An archive was added on 2026-08-05 (a fix log + a closed-backlog file) and within two days
  `docs/archive/tasks.md` was the LARGEST doc in the repo at 290 KB — 62% of all doc weight was finished
  work. Owner: *"we dont keep historial since we have git for that"*. Deleted whole.
  - **What an archive is actually FOR turned out to be two different things wearing one name**, and only
    one of them survived the deletion: the *narrative* of what happened (git has it, in far more detail,
    with the diff attached) and the *warnings written for a future session* (which were never history —
    they are invariants, and they belong in `.claude/knowledge/`). Four iOS deploy traps were harvested
    into `mobile-shells.md` on the way out, because nothing else held them.
  - ⚠ **The tell that a doc is really an archive: nobody reads it, and it grows fastest.** Ask what
    QUESTION a reader arrives with. "Why is it done this way?" → this file. "What is the shape today?"
    → `ARCHITECTURE.md`. "What is left?" → `TASKS.md`. "What happened on the 5th?" → `git log`.
  - **`TASKS.md` holds ONLY open work** (owner, 2026-08-05: *"this should just be a backlog with active
    task, completed should move to other docs"*). It had grown to 762 lines with eleven closed items
    annotated in place, and **two of those still showed an unchecked `[ ]` a day after they shipped** —
    the checkbox is the only signal that file gives, and it was wrong twice. **Prune by DELETING an
    entry, never by ticking it in place**; the file's length should be the size of the remaining work.

- **D10 — Two consumption profiles; server-backed hosting helpers are deferred.** The package
  split (Ipc separate from the shell packages) exists so a Sonora-style app (in-process HTTP
  server shared with mobile clients; WebView2 as "just a browser" + optional one-way event push)
  can adopt the shell without the postMessage command bridge. A `Shenora.Hosting.AspNetCore`
  package (SPA static-file policy, loopback-gated endpoint helpers) is a candidate later
  addition — out of initial scope to keep the first releases small.
  **RESOLVED 2026-07-31 (P7): NO-GO. The package will not be built.** The two-profile split stands;
  only the extra package is dropped. P6.6 surveyed the server-backed app to decide this instead of
  reasoning about it, and both proposed contents evaporated on contact:
  * the **SPA static-file policy** is five lines of ASP.NET in that app — an `OnPrepareResponse`
    that sets `no-cache` on the HTML, passed to `UseStaticFiles` and `MapFallbackToFile`. A package
    wrapping five lines of someone else's framework earns nothing and costs a version to keep in
    lockstep.
  * the **loopback gate** is a two-line host check, and in the real app it is embedded in a policy
    written against that app's own threat model (a local page fetching the loopback API and
    exfiltrating the response). That is app security policy, not a reusable helper — shipping a
    generic version would be the kit making a security decision on a consumer's behalf, which is
    worse than shipping nothing.
  Its host→page channel is a one-way event push, exactly what this entry anticipated, and the kit
  already covers it (`WebViewIpcBridge` + `IEventBus` wildcard forwarding on the host,
  `eventBus.subscribeToAll` on the client). Its host-side IPC seam is already
  `IMessageDispatcher.DispatchAsync` — an HTTP endpoint calls it directly, so D16's transport
  pluggability holds with no new surface at all.
  **The test this fails is the standing one:** would the other apps use it unchanged? Only one of the
  four is server-backed, and even it would not — it would keep its own five lines. Per D2 a seam does
  not justify a package; here there is not even a seam, only boilerplate. Revisit only if a real
  consumer cannot express what it needs — the same bar every other deferred capability is held to.

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
  own later package (working name `Shenora.WebView2.Sessions`, which was merged away by D37) so the
  core hosting package stays lean (phase P5, long since complete).
  **⚠ Scope narrowed by D39 (2026-08-03): this stack is DESKTOP-ONLY and stays that way.** Both mobile
  shells host a webview, so "port the sessions to mobile" looks obvious and is not — read D39 before
  proposing it. Built and shipped inside `Shenora.Windows` (`Sessions/`) since D37.

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

  ⚠ **The SEAM held; the MECHANISM named here did not happen.** The kit ships its OWN Android and iOS
  shells (D32, D37) rather than riding a Capacitor bridge, and D56 goes further — the kit owns its own
  device deploy loop precisely so an adopter needs no Capacitor project. `@shenora/capacitor` was never
  built and is not planned. What this entry got right is the part that mattered: the envelope was
  transport-neutral, so shipping a second shell cost **no contract change at all**.
  **✅ RESOLVED 2026-08-02/03 — it happened, and both open questions closed the other way than sketched.**
  The shell is **MAUI `HybridWebView`**, not Capacitor (D32; `src/Shenora.Mobile/` compiled into
  `Shenora.Android` + `Shenora.iOS`, D37). The transport is **`createHybridWebViewTransport` INSIDE
  `@shenora/react`**, not a separate `@shenora/capacitor` package — deferring that packaging call was
  the right move, and the answer turned out to be "no package at all".
  **The load-bearing prediction held exactly:** the envelope was transport-neutral, so the mobile shells
  needed no contract change and app logic written against `@shenora/react` runs unchanged on all three.
  Proven on a device and a simulator. What mobile actually cost landed elsewhere — see
  `.claude/knowledge/mobile-shells.md`, D44 (opposite range bodies per platform) and D45 (interception).
  - **AMENDED (2026-08-01, 0.2.0 design pass D3) — transport neutrality is now EXECUTED, and the
    claim's exact boundary is recorded with it.** D16 said the same envelopes ride WebView2 postMessage
    today and a WebSocket or mobile channel tomorrow; `NotificationPump` was extracted so "a second,
    non-WinForms base" would inherit its fixes. No such base existed, so none of it had ever run — the
    kit's own `generic-library.md` calls that shape speculation. A throwaway spike
    (`devtools/_transport-spike/`, gitignored) closed the gap: a `net10.0` console app referencing ONLY
    `Shenora.Core` + `Shenora.Ipc` ran request/response, the error boundary, the pump on a
    `PeriodicTimer`, and a `ctx.Run` operation streamed as batched notifications. It passed with **no
    change to `Shenora.Ipc`**, and the TFM enforces it: a Windows type anywhere in that graph turns the
    project red.
    **What that does NOT license.** The spike validates the IPC/transport half only. It says nothing
    about the desktop-FLAVOURED service contracts, because a transport needs no file dialogs —
    `FileDialogContracts.cs` still concedes in writing that `FileDialogOptions` carries Win32 vocabulary
    and a mobile picker would ignore half of it and return a content URI. That narrowing is still an
    accepted pre-1.0 possibility awaiting a real mobile consumer (D15), not something a second spike can
    settle. Two convenience gaps the spike also surfaced are in `TASKS.md` and deliberately unbuilt: the
    host-side mirror of `ShenoraBridge` (~40 lines every non-WinForms base rewrites) and a headless
    `IShenoraRunner`, both held to the two-consumer bar rather than built for the spike that found them.

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
  restarted as a single bootstrap commit rather than rewritten — the per-phase narrative is in
  `git log` and `CHANGELOG.md`, and the pre-rename history is
  kept privately offline. The former name stays out of tracked files and commit messages
  permanently (same discipline as the private sibling names, `sensitive-info`).

- **D19 — The two Windows shell packages are ONE layer: `Shenora.WebView2` depends on
  `Shenora.WinForms`.** (User direction, 2026-07-30, after the first full code review.)
  **⚠ The PACKAGES merged in D37 (2026-08-02), so this edge is now INTERNAL** — `Shell/` and
  `WebView/` inside `Shenora.Windows`. The rule survives exactly as stated, one level down: the
  direction still holds and the reverse is still a cycle. The evidence below is why it was decided,
  and none of it changed.
  The design
  contract §4 forbade the sideways edge *"revisit only if extraction proves it impossible"* —
  extraction proved it: the UI-thread marshal pattern ended up hand-rolled **14 times across 3
  packages with 5 incompatible pre-handle policies**, and the divergence produced real defects (7
  unguarded `BeginInvoke`s in the sessions package; a site whose comment explains the pre-handle
  trap and then commits it on the next line; a P0 where `RenderSession` accepts cancellation tokens
  it cannot observe). Deciding facts, in the package names of the day: `Shenora.WebView2` was already
  a WinForms assembly — its csproj
  set `<UseWindowsForms>true</UseWindowsForms>`, it hosted the `Microsoft.Web.WebView2.WinForms`
  control, and 5 of its files used a `Form`/`Control`-derived type — so the edge added no new
  *technology* dependency, only an honest package reference; and **neither consumption profile (§3)
  took `WinForms` without `WebView2`**, so that split served no profile. The boundary is now
  **primitives → hosting-on-primitives**, all edges still strictly downward. UNCHANGED and still
  load-bearing, stated in the CURRENT shape: `Shell/` carries no `Shenora.Ipc` dependency — the reason
  is that it keeps a **WinForms-only consumer** viable (a tray/single-instance utility with no web
  frontend), and it is why the window-command and drop-zone facades live in `WebView/`. (NOT because
  profile 2 avoids `Ipc`: the shell references `Shenora.Ipc`, so profile 2 receives it transitively and
  merely doesn't use the postMessage bridge — an earlier draft of this entry claimed otherwise.)
  `WinForms` never references `WebView2`. Rejected:
  merging into one `Shenora.Windows` package (preserves no WebView2-free option, much larger diff
  for the same benefit) and sharing via a linked source file (two binaries carrying the same
  internal type; solves the least). As-built layering and full surface: `docs/ARCHITECTURE.md`.

- **D20 — Portable contracts live in `Shenora.Core`; only Windows implementations live in the Windows
  shell** (written as `Shenora.WinForms`, which was merged into `Shenora.Windows` by D37).
  (User direction, 2026-07-30.) The reusable part of a desktop kit is the
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
  stays in the Windows shell (window geometry is a desktop concept). Per D16 this pass ships NO
  mobile host or transport adapter — the seam, not the package.
  **⚠ Corollary added by D48 (2026-08-05), which decides the cases this entry does not: if a SHELL
  implements it, the contract lives in Core — full stop.** `IFileLockInspector` travelled out with the
  file-operation engine and had to be split back, or `Shenora.Windows` would have gained a `Shenora.IO`
  reference for one interface. The mirror case is also recorded there: `IPathLocker` stays WITH its
  implementation, because advisory lock files are portable and no shell implements it.
  *(D48 phrased this as "a shell must never need an OPTIONAL PACKAGE to implement a Core contract". D55
  removed the optional tier, so the packaging half is moot; the rule now decides which FOLDER a contract
  belongs in, and it decides the same cases the same way.)*

- **D21 — For a whole application FEATURE, the kit ships primitives + lifecycle hooks; the app owns
  the product.** (User direction, 2026-07-30: *"co-browse itself is a whole feature — you just need
  to provide enough interface for other systems to plug/hook onto its cycle; you don't really need to
  implement the entire business feature."*) The test for any feature-shaped addition: **could a
  consumer build its own version of this product on our primitives, without adopting our product
  decisions?** If not, we shipped too much — or we shipped too few hooks.
  Shenora already followed this twice: `RenderSessionPool` ships the pool + session and deliberately
  did NOT port the sibling's render/analyze flows (the sample writes its own `RENDER` route), and
  `LoginWindow` keeps policy in a driver seam.
  **AMENDED 2026-07-31 (P7, user direction).** This entry used to add "…with `CookieLoginFlow` as ONE
  opt-in reference driver", and D22 then justified that type's scenario NAME on the grounds that D21
  had blessed shipping it. That is circular, and neither decision ever applied this entry's OWN test
  to it. Applied now, it fails outright: `LoginUrl`, `CookieReadUrl`, `AuthCookiePatterns`,
  `RevealDelay` and `CaptureAllCookies` are one product's workflow, and only an app doing cookie
  logins would use that API unchanged. **A reference driver is SAMPLE material, not library surface.**
  Shipping one costs three things — it becomes SemVer surface at 1.0, it makes the kit look like it
  ships that product (the exact "so the next contributor adds more of the product" failure D22 names),
  and it invites the next recipe in beside it. `CookieLoginFlow` was removed from
  `Shenora.WebView2.Sessions` and lives in the desktop sample as `CookieLoginDriver`. Nothing was lost:
  it only ever consumed public seam members, which is this entry's test passing in the other
  direction — a consumer really can build it on the primitives. **The kit ships no drivers.**
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

- **D22 — Name every public type for its MECHANISM, never for a scenario, product or business need.**
  (User direction, 2026-07-31: *"why we have a really specific business logic for login??"* … *"this is
  more than behavior leak, its about what we currently building — we should build a generic library,
  so for co-browser it should be focus on browser hook, life cycle, events instead a single business
  need."*) D21 said ship primitives, not the product. This is the naming half of the same idea, and it
  needed saying separately because the kit passed D21 on SHAPE while failing it on NAME — twice.
  **The test:** could a consumer whose use case is nothing like the one in the name still recognise
  this type as the thing they need? If not, it is named for a scenario.
  Two worked examples, both fixed in P5.5 H9.7/H9.8 and both caught only because a reader asked:
  `LoginWindow` contained NO login logic — it is a busy-gated, profile-isolated browser window that
  runs an app-supplied driver until it captures a blob (→ `InteractiveSession`); and
  `CoBrowseSession` was an off-screen browser that streams frames and accepts input, which is
  co-browsing, remote support, visual capture or a preview pane depending on who wires it
  (→ `StreamingSession`). Neither was a behaviour bug: both worked. The cost is subtler and worse —
  a scenario name makes the kit LOOK like it ships that product, so the next contributor adds more of
  the product to it, and consumers with a different use case never find the primitive at all. It also
  leaks: `SessionController.GetCookiesAsync` returned `IReadOnlyList<LoginCookie>`, so a consumer
  streaming a page for remote viewing had to program against a login type.
  **The "reference driver" exception is WITHDRAWN (2026-07-31, P7, user direction).** This entry used
  to say a reference driver may name the scenario it demonstrates, because D21 blessed shipping one —
  while D21 pointed back here for the name. Two decisions leaning on each other, and the question
  neither asked was whether a scenario recipe belongs in a shipped package at all. It does not (see
  D21's amendment): the driver moved to the sample, so there is no exception left to state. **If a
  type in `src/` needs a scenario name to make sense, that is the signal it does not belong in `src/`
  — not a licence to name it.**
  Sibling vocabulary that is genuinely mechanism is still fine and must not be "fixed":
  `ProfileDirectory` is a Chromium user-data folder, `Module` is the kit's composition unit,
  `ImmersiveDarkMode`/`UserDataFolder` are platform SDK terms.
  **How to enforce it:** the API-surface baselines already enumerate every public type and member, so a
  domain-vocabulary sweep over `tests/Shenora.Tests/Api/Baselines/*.txt` is the cheap periodic check —
  that is how the whole-library audit was done, and it found the Login cluster was the only real leak
  plus one PARAMETER name (`driveLogin`), which the baselines pin because named arguments are a source
  contract. Recorded as a rule in `.claude/knowledge/generic-library.md`.

- **D23 — The module contract carries the EVENT path, and the kit tracks long-running operations.**
  (User direction, 2026-08-01, on the first adopter's IPC design review.) Three parts, one design,
  shipped as 0.2.0.
  **(a)** A route receives an `IModuleContext` — `Publish` (module-scoped emit), `Start`/`Run` (tracked
  operations), `Logger` — *in its signature*, because `Shenora.Ipc` had **zero** references to
  `IEventBus` while the kit's own `DropZoneManager` took one as a REQUIRED option. The bus was already
  the spine; the contract did not admit it, so every app re-agreed the conventions by hand. Layering was
  never the obstacle: `Shenora.Ipc` already references `Shenora.Core`, so this adds no package edge.
  **(b)** This **supersedes the one-way-IPC design's original "not shipped" list** ("no
  operation/job manager, registry, queue, or progress TYPE"), which itself said to revisit if adoption
  showed the need. It did: one sibling ships a 320-line `ProcessRegistry` feeding a status bar and an
  activity panel, a second ships the `JOB_UPDATED`/`JOB_PROGRESS` archetype, and the kit's own shipped
  `createShenoraStore` *requires* a host-side snapshot source that no adopter can express without
  building one. What moves in is the correlation-and-lifecycle MECHANISM only — id, status, progress,
  scope, idempotent finish, cancel-by-id, bounded history, throttled progress emission. What stays out
  is everything that decides what an operation IS: no queue, no scheduler, no phase model, no
  `ProcessType`-style enum (`Kind` is an app string), no i18n rendering (labels carry key + parameters
  like `IpcError`), no UI (D13), no persistence. D21's test still passes — an app builds its own
  activity panel on `OperationInfo` + the event + the store without adopting a kit product decision.
  **(c)** The transport-neutral half of the outbound path (bus subscribe → filter → bounded queue →
  batch → ready gate → guarded serialize) becomes `NotificationPump` in `Shenora.Ipc`, with a
  per-channel `Filter`; `WebViewIpcBridge` keeps only the WinForms/WebView2 parts (the timer, the
  WebView2 events, `postMessage`). This is D16's "the seam, not the package" applied to the HOST half —
  the client half has been base-agnostic since P3 (`ShenoraTransport`), while the host half welded four
  paid-for bug fixes to a `System.Windows.Forms.Timer`. It also closes a real gap: every bridge
  subscribes with `SubscribeToAll`, so with two windows every event reaches both.
  Placement (D19/D20): operations live in `Shenora.Ipc`, not `Shenora.Core`, so they reuse
  `IpcError`/`OperationException` rather than duplicating a structured-error type in Core; both packages
  are `net10.0`, so portability is satisfied either way.
  Cost accepted: `RouteMessageAsync` gains a parameter, which breaks every override — mechanical, and
  taken deliberately pre-1.0 rather than shipped as a second migration later.
  **AMENDED 2026-08-01 (before 0.2.0 merged, user direction) — the operation lifecycle is completed to
  THREE BANDS, and the rule that produced it is the durable part.** The first adopter reviewed the
  unreleased branch and found that an `Interrupted` offer could only be removed by RESUMING it: `Validate`
  gates every transition on `Status == Running`, `ClearFinished` walks `_finishedOrder` which
  `RegisterInterrupted` deliberately never writes, and `PruneHistory` skips offers on purpose. Three
  guards, each individually correct and commented, composing into a state with no exit — and the same app
  had already shipped that bug and stranded a real deployment on it. **The rule: every non-terminal state
  must have a sanctioned exit to a terminal one**, enforced by a test that enumerates the status set
  rather than by reviewer attention, because an emergent trap is invisible in any single guard's diff.
  So the states are now *Active* (`Running`), *Waiting — stopped, resumable, never pruned* (`Paused`,
  `Interrupted`), and *Terminal* (`Completed`/`Failed`/`Cancelled`). `Paused` is new and earns its place:
  a run that stops mid-flight WITHOUT crashing (expired credentials, a throttling provider, DNS not yet
  propagated, a migration awaiting confirmation) previously had to be misrepresented as `Running` (a lie —
  the UI spins for work waiting on a human) or as `Fail` + `RegisterInterrupted` (a terminal event for
  something that never terminated, plus a second entry); in the surveyed app it is the most common
  non-success outcome, more common than failure. It carries an app-defined `PauseReason` STRING — the app's
  taxonomy, like `Kind`, never the kit's. `Dismiss(id)` is a separate member rather than `Cancel` accepting
  more states, because declining a pending offer and cancelling live work are different acts and this
  branch's only Critical came from exactly that conflation inside `Cancel`. `Pause` has no client route —
  pausing is the host's knowledge, while resume and dismiss are the human's decisions. Kept asymmetric on
  purpose: resuming a `Paused` entry leaves it for the app to flip via the handle, while resuming an
  `Interrupted` one still drops it, because a crash leaves no live body to flip. An `Adopt(id)` unifying
  the two was considered and rejected as unearned surface (recorded as a known limit).

  **AMENDED again 2026-08-01 (generic-library audit, before publish — free, since 0.2.0 was never
  published; publishing is the gate that freezes a surface, not pushing).** The audit asked not "is this correct" but "has the kit absorbed ONE
  application's shape" on the removal/asking halves of this lifecycle, and found four things this
  entry's own reasoning needs correcting for: (1) **"`Pause` has no client route" was too narrow** —
  true for a host discovering its OWN blocker, false for the equally-common shape of a human clicking
  Pause on visible work (a download, a sync, a backup), which the kit itself already names as a
  consumer. `IOperationRegistry.RequestPause(id)` was added as an exact mirror of `RequestResume`: it
  ASKS (emits `OPERATION_PAUSE_REQUESTED`, changes nothing), the owner's own `Pause()` still ACTS —
  `RESUME`/`DISMISS`/`PAUSE` are now all client routes, only the act itself stays out of the client's
  hands. (2) **`Resumable` was removed** — consulted nowhere except `RegisterInterrupted`'s own
  required-true gate, which every caller had already satisfied, making it a tautology; the existing
  non-empty-`ResumePayload` requirement already expresses resumability. (3) **`Find(id)`** (dropped
  pre-0.2.0 as unearned surface) **was reinstated**, because `RequestPause`/
  `RequestResume` both need an id→handle translation every such consumer would otherwise re-solve by
  hand. (4) **`ClearFinished` gained the `module?`/`scope?` filter `GetAll` always had**, and a removal
  (`MaxHistory` eviction, `ClearFinished`, the `Interrupted`-drop above) now publishes
  `OperationEvents.Removed`, retiring the two client-side optimistic prunes that guessed at removals
  before — one of which reproduced this entry's own Critical one layer up in the client. Full list:
  `CHANGELOG.md`'s 0.2.0 entry.

  **AMENDED again 2026-08-01 (owner direction, before publish — "even its progress it might be
  different than 0-100%"): progress is not percent, and the previous fix to this said otherwise.**
  (5) **`OperationOptions.Progress`/`OperationInfo.Progress`/`IOperation.Report`'s `progress` were
  `int?` — implicitly 0–100 percent — and finding 5 above only patched the WRITE side's doc comment to
  SAY so, which was the wrong fix to a right observation.** Percent is not the mechanism; it is one way
  an app happens to measure. Real consumers measure differently — bytes transferred against a known
  total, items processed against a known total, an absolute count with NO known denominator (bytes off
  a chunked stream), or a genuine percent — and forcing percent makes an app pre-compute a ratio and
  discard the numbers its own UI wants to render. Worse, the silent `ClampProgress` (`Math.Clamp(value,
  0, 100)`) meant a consumer reporting bytes got a permanent 100% with no diagnostic — the exact trap
  this decision's own finding 5 named and then only half-fixed. This is the same class of mistake as
  `Kind` being an app string rather than the source app's enum (D22): the kit must carry the
  measurement, not define its unit. Fixed: a new record, `OperationProgress(double Value, double?
  Total = null, string? Unit = null)`, replaces `int?` in all three members (TS mirror: `{ value:
  number; total?: number; unit?: string }`). `Total = null` means no known denominator, never zero;
  `Unit` is app-defined and uninterpreted, exactly like `Kind`/`PauseReason`. `ClampProgress` is
  DELETED and nothing is clamped or validated in its place — silently rewriting an app's own reported
  number is worse than passing it through untouched, and a `Value` above its own `Total` is the app's
  bug to see, not the kit's to hide; no validation throw was added either, because progress is
  reported from background work on a hot path and throwing there would kill an operation over a
  cosmetic number. `Complete()` stops fabricating a number: it sets `Value = Total` only when the last
  report carried a known `Total` (the honest "all of it"), and otherwise leaves the last reported value
  untouched — never inventing a figure the app never gave it. The kit ships no percent helper; the
  README documents the one-liner (`total ? (value / total) * 100 : undefined`) because that division
  is the consumer's own policy. Caught before 0.2.0 was pushed or published, so free. Full list:
  `CHANGELOG.md`'s 0.2.0 entry.

  **AMENDED again 2026-08-01 (owner direction, before publish — "I don't even think we need any
  specific status than regular — think about this is going to be structured like XHR"): `Paused` and
  `Interrupted` collapse into ONE status, `Waiting`, closing the three-band model out with two bands'
  worth of states instead of three.** XHR keeps a tiny closed lifecycle and puts the semantics in
  fields, not in extra states; it has no "paused" because it does not own pausing — the same standard
  this entry's own §5A.2 table already half-applied (three BANDS, not five states) without going the
  last step. The evidence for going the last step was already sitting in this entry's own text, not
  new: `Dismiss` accepted both `Paused` and `Interrupted`; `RequestResume` accepted both; NEITHER was
  ever pruned by `PruneHistory`/`ClearFinished`; the client's `waiting` getter was already defined as
  their UNION. The two statuses diverged in exactly one place — `RequestResume` dropping the
  `Interrupted` entry (no live handle) while leaving `Paused` in place (a live handle to flip) — and
  that difference was never actually ABOUT the status; it was about whether the entry had a live body,
  which `OperationOptions.ResumePayload` already told the registry on its own (`RegisterInterrupted`
  required it non-empty; an ordinary `Pause()` from `Running` normally has none). Recording it as a
  second status was the D22 mistake in miniature — not a scenario name this time, but a scenario
  ***count***: `Paused`/`Interrupted` read as two answers to "why is nothing progressing" (crashed vs.
  not-crashed) when the mechanism only ever needed one, with the app's own reason string already
  carrying the "why" (`"credentials"`/`"dns"`/`"queued"`/`"rate-limited"`, never a kit taxonomy). It
  also closes a known limit this entry itself recorded: "registered but not yet started" was
  unrepresentable (§6's own list) — now it is `Waiting("queued")`, since a just-`Start`ed operation can
  immediately `Wait` on its own handle before real work begins, needing no kit change and no third
  status.
  **Renames (mechanism, not scenario, per D22 — every one earns its keep the same way `Operation`
  already did over `Job`/`Task`/`Process`):** `OperationStatus.Paused`/`.Interrupted` → one value,
  `OperationStatus.Waiting`; `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause(reason,
  detail?)` → `IOperation.Wait(string? reason = null, OperationLabel? detail = null)`;
  `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting` (still requires a non-empty
  `ResumePayload`); `IOperationRegistry.RequestPause` → `RequestWait`; `OperationEvents.PauseRequested`
  (`OPERATION_PAUSE_REQUESTED`) → `WaitRequested` (`OPERATION_WAIT_REQUESTED`); the `PAUSE` facade
  route → `WAIT`; client `OperationStatuses.Paused`/`.Interrupted` and the `paused`/`interrupted`
  half-getters → `Waiting: 'waiting'`, with the existing `waiting` getter now covering the WHOLE band
  (the two half-getters are deleted, not deprecated — they named a distinction the wire no longer
  carries). `IOperation.Resume`/`RequestResume`, `Dismiss`, `OPERATION_RESUME_REQUESTED`, `RESUME`,
  `DISMISS` all keep their names: resuming and dismissing were already mechanism words, not scenario
  words, so D22 has nothing to fix there.
  **`RequestResume` keyed its drop-vs-keep decision on `ResumePayload`, not on status, for one
  release** — non-null meant the entry has no live handle (a reconstructed offer, whether from
  `RegisterWaiting`'s checkpoint or one an app itself attached at `Start()`), so it was removed and the
  app started fresh work via `Start`/`Run`; null meant an ordinary `Wait()`, left IN PLACE for the
  app's own `Resume()` to flip. This was meant to express the intrinsic difference the two-status
  design was really encoding — a crash leaves no live body — directly instead of through an extra enum
  value, and it *mostly* did.
  **CLOSED (2026-08-01, before 0.2.0 was pushed or published): keying on `ResumePayload` was itself a
  residual hole, not a harmless simplification, because that field is APP-CONTROLLED data, not a signal
  the kit owns.** An app that attaches its own `ResumePayload` to `OperationOptions` at `Start()` time
  (not through `RegisterWaiting` at all) and then calls `Wait()` on the live handle has a genuinely LIVE
  operation — handle intact, body parked — dropped by `RequestResume` exactly like a crash-checkpoint
  offer, because the old decision read the field, not the call site that produced it: later
  `Report`/`Complete`/`Fail` calls on that operation were silently ignored (and only logged) from then
  on. This was recorded here as a deliberate, out-of-contract known limit rather than fixed — the wrong
  resolution, the same defect class `IModuleContext` closed for module drift (a decision keyed on a
  value the caller also controls instead of on the fact the kit itself knows for certain). The fix keys
  the decision on the registry's OWN provenance instead: an internal `Entry.Reconstructed` flag, set
  `true` only by `RegisterWaiting` (the one call site that legitimately reconstructs an entry with no
  live body) and left `false` by `Start` (which always allocates one) — never exposed on
  `OperationInfo`, since no consumer needs it and every public member is SemVer surface at 1.0. The
  Start-with-`ResumePayload`-then-`Wait()` combination is no longer ambiguous: it is now an ordinary
  live-`Wait()` entry, left in place like any other. `ResumePayload`'s other roles are unchanged —
  `RegisterWaiting` still requires it non-empty, the dedupe key still uses it, and it still rides the
  `OPERATION_RESUME_REQUESTED` event so a handler knows which checkpoint to continue. The
  `OPERATION_RESUME_REQUESTED` payload still carries `status` (always `Waiting` now) so a handler can
  keep branching on the field without a breaking shape change.
  **SUPERSEDED by the 0.2.0 design pass (2026-08-01, still before publish): the crash-checkpoint half
  is CUT, so the drop-vs-keep decision no longer exists in any form.** The owner asked a review to
  judge the design rather than only the code, and this cluster is what it found. Three things line up
  and they point the same way: this entry's own §4.2 provenance note already recorded in writing that
  `Interrupted`/`ResumePayload`/`RegisterWaiting`/`RequestResume` "come from **one** app, not two",
  against a standing bar of "generalize what the survey shows at least TWO apps need"
  (`generic-library.md`); the cluster then took ~8 reshapes inside one unpublished release; and it
  produced the release's only Critical. The amendments above are the record of a single question being
  answered three times — a second status, then an app-controlled field, then an internal provenance
  flag — which is what a design defect looks like from the inside. The question was "does this entry
  still have a live body?", and it only existed because the registry accepted entries it had never
  started. Removing that removes the question: `RegisterWaiting`, `OperationOptions.ResumePayload` and
  `OperationInfo.ResumePayload` are gone; `RequestResume` is now an exact mirror of `RequestWait`
  (validate, emit, mutate nothing) and both carry `{ operationId, module, kind, scope }`.
  **What stays is what more than one app needed:** `OperationStatus.Waiting`, `IOperation.Wait`/
  `Resume`, `Dismiss`, and the ask-act pair — the download-manager shape the kit itself names as a
  consumer. Cutting `RequestResume` as well would have left a client able to pause but never resume,
  which is why the cut is narrower than "the crash-resume half" as a whole. Crash recovery returns to
  the app, where the checkpoint already lived: the kit only ever held an opaque token it could not
  interpret, and a resumed run is a fresh `Start()`. Full migration note: `CHANGELOG.md` 0.2.0
  `### Removed`.
  **Enforcement, unchanged in spirit, simpler in fact:** `OperationLifecycleInvariantTests` (host) and
  its client-side mirror still enumerate the LIVE status set via reflection and require a registered
  exit per non-terminal value — with one fewer status to enumerate, the sweep is simpler, not weaker,
  and still fails BY NAME the moment a future status is added with no exit.
  Caught before 0.2.0 was pushed or published, so free — same standing as every other amendment in
  this entry. Full list: `CHANGELOG.md`'s 0.2.0 entry.

- **D24 — Frameless chrome is a FIXED WinForms type, not an attachable behaviour.** (0.2.0 design
  pass, owner direction: *"Frameless chrome should be part of winform (as a style of our winform
  design)"* / *"or you can make a fixed winform type"*.) A whole-codebase review flagged that
  OptimizedForm is the ONE piece of the kit reachable only by inheritance — everything else
  composes (WindowStateManager.AttachTo(form), TrayIcon(form), DropZoneManager(form),
  SecondaryWindows(Func<Form>)) — and proposed extracting the chrome into a
  FramelessChrome.AttachTo(form) behaviour so an app with its own Form base could take it.
  **That proposal was considered and REJECTED**, and the reasoning is worth keeping because the
  observation behind it was correct:
  - The window STYLE (WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX) is naturally set in
    CreateParams, i.e. at handle creation. Attaching after the fact needs SetWindowLong +
    SWP_FRAMECHANGED — a SECOND mechanism for the same property, and both would have to be
    verified against the OS. That doubles the verification surface in the exact area where a green
    unit suite has twice been the wrong answer here (P5.6; docs/REVIEW-GUIDE.md §6).
  - The benefit is narrower than it first looks. WindowCommandOptions already takes a plain
    Form plus delegates — deliberately, so it never assumes the form type — so a window that is
    NOT an OptimizedForm can already drive minimize/maximize/close/drag/resize over IPC. Only the
    chrome itself needs the type, and an app adopting the kit's frameless SHELL window is adopting
    the kit's window: MainForm : OptimizedForm is the normal shape.
  - "A fixed type" is the coherent expression of "a style of our WinForms design": the style is
    selected by OptimizedFormOptions.FramelessChrome, and the type is what carries it.
  **ACCEPTED LIMIT, recorded so it is not re-raised as a defect:** an app that cannot change its
  form base cannot take the frameless chrome. It can still take every other Stage-1 primitive and
  drive the window commands. If a real adopter hits this, the evidence — not the symmetry argument —
  is what should reopen it.
  **What DID change, because the cohesion complaint was fair:** OptimizedForm was 998 lines doing
  five loosely-related jobs. The part that carries no message-loop responsibility — caption-button
  RENDERING (palette fallback, glyph selection, the DPI-scaled icon font, painting) — moved to an
  internal CaptionButtonRenderer (905 lines left). The split line is deliberate and is the rule to
  follow next time: **extract what is pure input → pixels; leave anything that answers a window
  message where the OS can see it.** The renderer is unit-tested directly with no STA thread, no
  handle and no pump — including a guard that every glyph is a single Private Use Area codepoint,
  which pins the documented mojibake trap that a BOM-less UTF-8 source on a CJK-locale machine
  otherwise turns into silently empty buttons.

- **D25 — Frameless chrome and native drop zones are the kit's FLAGSHIP pair. Settled; do not
  redesign without adopter evidence.** (Owner, 2026-08-02, after testing both by hand on the running
  sample: *"testing done all good… those 2 features kind important"*, plus *"the frameless winform
  was developed properly so don't really change that"* and, on the drop stack, *"I have been there
  before so do not change this"*.) These two carry most of the kit's answer to "why adopt a shared
  body at all", and they are the two most likely to be *rediscovered* by a future reviewer as
  candidates for a tidier design — so the verdict is recorded rather than left to be re-derived.
  - **Why they are the flagship pair.** They are the clearest instances of the owner's two review
    criteria (`REVIEW-GUIDE.md` §1) holding at once: fully generic — no app concept in either — and
    each delivers something the adopting app would not have got by hand. The chrome is the kit
    RAISING the UI bar using the tech available (Snap Layouts via HTMAXBUTTON, Win11 rounded corners
    squared while maximized, immersive dark mode, DWM border colour, runtime theme resync — the exact
    things a hand-rolled frameless window loses). Drop zones deliver a capability the page cannot
    have at all: a page-side drop yields a `File` whose only accessor is its CONTENT, forcing a full,
    EAGER byte copy of every dropped file across the IPC boundary at drop time — before the app knows
    whether it wants any of them. Native overlays yield `string[]` paths instead. This is also the
    kit's strongest dedup case: four independent ports of the drop component across the family.
  - **Consequences.** `useDropZone` is not optional sugar — it is THE file-drop path for a page on
    this kit, and a DOM drop handler for files is the thing it replaces, not an alternative to it.
    Neither component's design is open for restructuring on symmetry or cohesion grounds; D24 already
    rejected one such proposal for the chrome. Reopen either only on a real adopter hitting a real
    limit — the same bar as D24, and the same bar the three `TASKS.md` follow-ups are held to.
  - **Verified live, not asserted** (2026-08-02, `dev.mjs sample`): both exercised by hand on the
    running desktop sample. That session also exposed the stale-bundle defect — worth remembering that
    the hands-on test found something eight green `verify` runs did not.

- **D26 — the kit's DESKTOP scope is Windows only. Linux is served by the SERVER-BACKED profile, not by
  a native Linux shell.** (Owner, 2026-08-02, asked whether MAUI could cover Linux + Windows.)
  **⚠ Read the headline as DESKTOP scope, not kit scope — it was written "the kit stays
  Windows-desktop-scoped" and that stopped being true the same week.** D32 (2026-08-02) added MAUI
  shells for **Android and iOS**, and they ship. That is not a reversal of this entry: everything below
  argues against MAUI *for desktop* and against a native *Linux* shell, and both still stand. The two
  decisions are about different platforms and neither weakens the other.
  - **A candidate shell must expose the NATIVE WINDOW, not merely host a WebView.** This is the
    selection criterion, and it is the one that actually decides the question — earned by the owner
    having already tried and abandoned **Photino** for exactly this: *it cannot do drop zones at all*,
    because a window-with-a-WebView gives you nowhere to put transparent native overlays over page
    elements, so you are back to HTML5 drop and its blob URLs (D25 — the eager byte-copy problem).
    Its second failing was maintenance: the form/window layer was not being kept current. Any future
    candidate gets measured against this bar before anything else; a thin WebView wrapper cannot carry
    this kit, whatever its cross-platform reach.
  - **MAUI does not solve the stated problem anyway** — it has no official Linux target (Android, iOS,
    Mac Catalyst, WinUI). And MAUI *for Windows* is separately rejected: it would mean rewriting the
    frameless chrome D24/D25 just settled, discarding the Snap Layouts / DWM / rounded-corner work,
    for no capability gain.
  - **Measured cost of a Linux desktop shell** (2026-08-02): `Shenora.Core` + `Shenora.Ipc` are ~6,000
    lines and already run on Linux (`net10.0`, no UI binding, D16/D3). The Windows shell — three
    packages then (`Shenora.WinForms` + `WebView2` + `WebView2.Sessions`, merged into `Shenora.Windows`
    by D37 later the same day) — was ~9,300 lines of `net10.0-windows` — **~60% of the kit's C#,
    and it is the part that IS the value**: frameless chrome, tray, single-instance, DPI-correct
    window state, drop-zone overlays, WebView2 hosting and sessions. None of it ports. A Linux shell
    shares Core, Ipc and the React client, and re-implements the rest.
  - **There is already a Linux answer, and it costs nothing:** the server-backed profile (in-process
    Kestrel + a browser) runs on Linux today. "Usable on Linux" is solved; only "native Linux desktop
    shell" is not, and nothing asks for that.
  - **Zero Linux consumers** — all three donor apps are Windows desktop apps. The two-consumer bar is
    not close to met.
  - **What would reopen it:** a real Linux consumer, plus a shell technology that passes the
    native-window test above. **Avalonia** is the unevaluated candidate — genuinely cross-platform and
    a real UI framework rather than a WebView wrapper, so it is the one worth measuring against the
    bar. Do not re-propose Photino, and do not re-propose MAUI for Linux.

- **D27 — the scheduler's unit is a MISSION, and a definition is not an execution.** (Owner,
  2026-08-02; shipped as `Work*` hours earlier and renamed before any release took it.)
  - **`Mission` was chosen against two rejected alternatives, and the reasons generalise.** `Work` is
    too common a word to own or grep — every hit in a codebase is ambiguous. `Task` collides with
    `System.Threading.Tasks`: `TaskScheduler` would be ambiguous against the BCL type in every
    consumer importing both namespaces, a papercut in the one type an adopter constructs. `Quest` was
    rejected for reading as DOMAIN vocabulary in a games family — the thing `SurfaceVocabularyTests`
    exists to keep out of `src/` — and `Expedition` for putting ten characters in front of fifteen
    types. Naming is not cosmetic here: the lexicon gate makes every new word a review.
  - **`MissionDefinition` (what should run) is separate from `MissionExecution` (one specific run),**
    replacing four types with two (`MissionRequest`, `MissionContext`, `MissionView`,
    `MissionSnapshot`). An execution carries `Attempt` and `IsRunning` and NO `CancellationToken` —
    the body takes its token as a second parameter, so an execution stays a pure value safe to hold in
    a diagnostics view.
  - **Why now rather than when a consumer asked — the rule that was being misapplied.** The
    two-consumer bar governs adding CAPABILITY. The `A2` principle governs SHAPE: pay now only where
    the later change would be BREAKING rather than additive. This one alters `SubmitAsync`, every
    body's parameter, all three observer callbacks and both policy methods at once. Owner: *"bigger
    change does not mean a bad thing, we need to think forward for future, change is allowed, this is
    still pre-1.0."* Deferring a breaking-later change behind a capability bar is the error to avoid
    repeating.
  - Declined from the same proposal, and still declined: a handler registry by type (app composition,
    and it would make the kit own serialization of app types — the iOS AOT problem in `TASKS.md` is
    already waiting there), a separate queue and runner (rebuilds the two-component shape the one-
    engine claim collapses), an `IMission` interface (pushes toward class-per-mission),
    `MissionStatus` beside `MissionState`, and `MissionOptions` beside `MissionSchedulerOptions`.

- **D28 — the queue's storage is named for what it is, and the queue itself stays internal.**
  (2026-08-02.) `IMissionStore` became **`IMissionQueueStore`**: not a "durable missions" service
  beside the queue, but where the queue's own entries live across a restart. Durability stops being a
  parallel concept.
  - **A pluggable async QUEUE was designed and rejected — do not re-propose it without new
    evidence.** It puts an `await` in the dispatch path, which cannot run under the scheduler's lock,
    so admission would read candidates, take the lock, then RE-VALIDATE against a collection that may
    have changed underneath. That is a race in the one place where a race corrupts rather than
    delays, bought for a distributed-queue capability no consumer has asked for — while the part apps
    actually vary, ordering, is already theirs through `IMissionPolicy`.

- **D29 — a chain is ONE queue entry, not N with dependency edges.** (Owner, 2026-08-02.)
  `MissionChain.Sequence` returns an ordinary `MissionDefinition`, so the scheduler gains no
  dependency concept, no blocked-on-predecessor state, and no edges — the alternative was a DAG
  engine by another name, which stays declined on the evidence that no sibling has ever needed one.
  - **The accepted cost, so nobody rediscovers it as a bug:** a chain holds the UNION of its steps'
    claims for its whole life, taking the STRONGER mode where steps disagree. A five-step chain over
    five paths blocks all five throughout. Claims are still acquired as one set, so deadlock-freedom
    is unchanged. Per-step claims are the escalation, and they are design (a) — a different pass.
  - A step's retry repeats THAT step; there is no chain-level retry. `IMissionChainContext` is
    IN-MEMORY only — a durable chain carries state in `Payload`, because the kit cannot serialize an
    app's object graph and a resume that silently lost the context is worse than one that never had it.

- **D30 — filesystem MUTATIONS are a separate component from mission scheduling.** (Owner,
  2026-08-02: *"it's more a different design rather than put them all into mission management"*.)
  `IFileUpdateQueue` decides how changes LAND; the scheduler decides which missions RUN.
  - **The argument is a measurement, not tidiness.** A path claim excludes two missions for their
    whole duration, but the expensive phase usually touches only a temp file — so under claims alone
    a seven-second compress waits on another mission's three-millisecond rename. Compute in parallel,
    serialize only the landing. The failure modes do not overlap either: a scheduler's are starvation
    and deadlock, an applier's are partial writes and locked targets.
  - **Atomicity is the app's choice per update** (owner: *"it depends what the application need"*):
    `PerChange`, or `AllOrNothing` via compensating rollback — which forces STAGED deletes, a delete
    being the one change that cannot be undone from nothing.
  - **Crash-atomicity is opt-in via a write-ahead journal**, and the ordering is the property: the
    undo plan is durable BEFORE the mutation, because a plan written afterwards is missing exactly
    the change that got interrupted. That is why undo is DATA rather than closures and why every
    change is planned before it is applied — **do not "simplify" that split away**. Recovery rolls
    back an update interrupted while APPLYING and FINISHES one interrupted while COMMITTING; rolling
    the latter back would undo a success.
  - **⚠ It is a separate PACKAGE too, since D48 (2026-08-05): `Shenora.IO`.** This entry made it a
    separate component; the measurement that later made it a separate package is the same shape of
    argument — it was 34% of `Shenora.Core`, which everything references, for a job most apps never do.
    Everything above still describes the code exactly; only the namespace moved (`Shenora.Core` →
    `Shenora.IO`), and `PathClaims` stayed behind because it is scheduling vocabulary.

- **D31 — cross-process file access is TWO problems, and one mechanism cannot serve both.** (Owner,
  2026-08-02, from a filesystem-heavy adopter that does not own its working folder.)
  - **`IPathLocker`/`IPathLease` excludes PARTICIPANTS** — a second instance, or a child process the
    app spawns while the parent holds the lease, which is how an external command-line tool
    participates without knowing anything about the kit.
  - **`IFileLockInspector` answers for everyone else.** A game holding its assets, a mod loader,
    antivirus, another application editing the same tree: none will ever take a lease, so exclusion is
    impossible and the only useful thing is a NAME. `WhoHolds` returning empty means "cannot tell",
    never "nobody" — the distinction matters at a call site. The Windows implementation is Restart
    Manager, and it lives in `Shenora.Windows` because it is Win32. **The CONTRACT stayed in
    `Shenora.Core` when the rest of the file-operation engine moved out to the `Shenora.IO` namespace**
    (D48; a package until D55), and for this same reason: a per-platform answer means a portable contract
    with a shell implementation, and if a shell implements it, the contract lives in Core.
  - **Lock files live in the app's own directory, never the managed tree.** An app frequently does
    not own the folder it manages; sidecar locks there get synced, committed, and outlive the process.
  - **Network shares are supported, correcting an earlier "not a target".** Leases work over SMB2+
    provided the lock directory is ON the share — a lock in one machine's local storage is invisible
    to the other, and that is the setting that fails silently. A lease released by a crash returns
    when the SMB session times out, not instantly.

- **D32 — a second shell is a PEER, and the kit's job is the substrate under both.** (Owner,
  2026-08-02: *"abstract the logic out as much as possible (or make interface) so it supports both
  MAUI and WinForm (some capability can implement differently like dropzone and frameless)."*)
  `Shenora.Mobile` references neither `Shenora.WinForms` nor `Shenora.WebView2`. The evidence that the
  split is in the right place is its SIZE: ~200 lines, because the substrate moved first (the IPC
  host half, the headless runner, `ShenoraApplication.Start`/`Stop`). A fat shell package would have
  meant something portable was still trapped in the Windows one.
  - **The bar stays D20's, not "it looks platform-neutral".** *Can app logic compile off Windows?*
    Window geometry, tray, secondary windows and native drop zones stay in `Shenora.WinForms`
    because they are desktop CONCEPTS — on mobile they are absent, not different.
  - **Checked against the platform before designing, and it cancelled a proposal.** A plan to lift
    the resource-serving layer for reuse died on the fact that `HybridWebView` has NO request
    interception; it serves `Resources/Raw/wwwroot` itself. There was no seam to lift into. Do not
    re-propose it.
  - **The platform-owned loop is why `Start`/`Stop` exist.** `IShenoraRunner.Run` is contractually
    "blocks until shutdown", which a MAUI activity cannot honour, so `UseMobile` registers no runner
    and the app drives the pair from its own lifecycle.

- **D33 — an ABSENT capability throws and names the platform; a SATISFIED one is an honest no-op.**
  (Owner, 2026-08-02.) `ShellCapability.NotSupported` is the one message. A silent no-op is the
  "mistyped resource prefix degrading to an all-404 provider" class this repo keeps paying for, and
  `ModuleContext.Publish` already fails loud for the same reason.
  - **The distinction is load-bearing and was found by implementing it.** Clipboard IMAGES have no
    expression in MAUI Essentials → refuse. `IUiInteraction`'s block/unblock is satisfied BY the
    platform (mobile pickers are modal) → an honest documented no-op. Refusing the second kind would
    break portable logic that is behaving correctly. "Absent" means no expression exists here, not
    "we did it differently".
  - **NOT a `DispatchProxy`.** One reflection proxy throwing for any interface is the obvious
    implementation and would undo the iOS/AOT work in `IpcJson.AddTypeInfoResolver` — reflection is
    exactly what trimming strips. Each shell writes small explicit stubs sharing the one message.

- **D34 — a shipped assembly the test project cannot REFERENCE is gated from its IL metadata.**
  (2026-08-02.) `tests/Shenora.Tests` is `net10.0-windows`; `Shenora.Mobile` is `net10.0-android`. The
  full `ApiSurfaceDump` cannot run over it — `NullabilityInfoContext` needs runtime types, so a
  `MetadataLoadContext` cannot drive it, and a plain `LoadFrom` would have to resolve
  `Microsoft.Maui.Controls`. `MetadataSurfaceTests` reads the tables with a `MetadataReader` instead.
  - **The gate is deliberately weaker and says so everywhere it appears:** NAME-level, so it catches
    an add, a removal and a rename but NOT a signature-only change (`string?` → `string`, a dropped
    default, `set` → `init`). **That is the standing argument for keeping such a package thin.**
  - **`Every_packable_project_has_a_baseline_of_one_kind_or_the_other` is the real fix.**
    `ApiSurfaceTests`' own coverage check walks the TEST assembly's references, so the package it
    cannot reference is exactly the one it cannot notice is missing — a seventh package would have
    slipped through the same gap. `IsPackable` is the definition of "shipped", which is why the pack
    list must agree with it.

- **D35 — "open a folder" is a DESKTOP concept, and the portable answer is to decompose it into the
  intents behind it.** (Owner, 2026-08-02: *"open folder in mobile will be different cases than open
  folder in desktop — it usually means something like camera roll, or folder space for the
  application, usually authorized by the mobile system; for desktop it's more free."*)
  The kit will NOT make `OpenFolderAsync` mean something on mobile. A desktop folder browser hands
  back ambient, permanent access to an arbitrary path; Android hands back a revocable, scoped grant
  to a tree URI. Same word, different guarantee — and papering over that is how a portable-looking
  API becomes a lie at the one moment an app relies on it.
  **Ask what the app actually wanted; all three are expressible on both shells:**
  1. **"Somewhere I own to read and write."** Already solved and needs no picker at all:
     `ShenoraPaths` on the desktop, `FileSystem.AppDataDirectory` on MAUI (the sample wires exactly
     that). An app asking the USER for this is the bug.
  2. **"Let the user hand me some media."** `MediaPicker.PickPhotosAsync` on MAUI (verified present
     in Essentials), a multi-select file dialog with image filters on the desktop. Genuinely
     portable, and the mobile-native answer to what a camera-roll folder was standing in for.
  3. **"Let the user grant me a working directory."** The only one that stays desktop-flavoured,
     because the permission MODEL differs, not just the API. Name it as desktop-only rather than
     pretending.
  **Consequence:** `IFileDialogs.OpenFolderAsync` is documented as a desktop capability, the MAUI
  implementation refuses it by pointing at (1) and (2), and a media contract is NOT pre-built —
  no consumer has asked, and `generic-library.md` calls that speculation. The shape is recorded so
  the first one that does gets it in a day.

- **D36 — the HOST advertises what it can do, in the handshake; the client never sniffs the
  platform.** (Owner, 2026-08-02: *"the universal I mean is more about the interfaces also about the
  frontend code itself, as possible"*.) D33 says what happens when a page calls something absent.
  D36 is how the page avoids calling it: `ShellInfo { Name, Capabilities }` is the ready handshake's
  response data, and the page renders on it —
  `shell.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar/>`.
  - **Capability, not platform, because the platform is the wrong question.** A user-agent or
    `Name` check assumes the OS determines the surface. It does not: what a host offers depends on
    what the APP composed — a desktop shell that never registers `TrayIcon` has no tray, and a
    desktop frontend running in a plain browser tab during `vite dev` has none of it. `Name` exists
    for diagnostics and is documented as never-branch-on.
  - **Declared by the app, not inferred by the kit.** The kit cannot know which services were
    registered, and inferring from the package set would be confidently wrong in exactly the case
    above. The cost is honesty: a capability advertised but not composed turns a rendered button
    into a D33 throw when pressed.
  - **Absent means "assume nothing", never "assume desktop".** `Shell` is optional, so a
    capability-less reply covers three cases at once — browser dev, a host that has not opted in,
    and a host predating this. Defaulting the other way makes the browser the one place the page
    renders wrongly, which is where it is developed.
  - **It cost nothing on the wire.** The handshake already round-tripped and already returned an
    empty success. Additive in both languages, mirrored name-for-name by `WireMirrorTests` — which
    grew a block-comment stripper in the same change, because its TS interface parser truncated at
    the first `{@link …}`. Disabling the stripper showed it fails a CORRECT mirror rather than
    passing a wrong one; the danger is the repair it tempts you into, since loosening the assertion
    to a subset check is what would actually make the tripwire stop checking. Fix the parser.

- **D37 — ONE shell package per PLATFORM, named for the platform. Supersedes D2's package set.**
  (Owner, 2026-08-02.) `Shenora.WinForms` + `Shenora.WebView2` + `Shenora.WebView2.Sessions` merge
  into **`Shenora.Windows`**; the mobile shell ships as **`Shenora.Android`** + **`Shenora.iOS`**.
  D2 stands as the record of the original set and why it was drawn that way; this replaces it.
  ⚠ **And this entry's SET is in turn superseded — D53 and D55 folded media and IO back into Core, so
  the current list is five managed packages + the launcher + npm. The header table is the only place to
  read it.** What this entry decided and what still governs is the SHAPE: one shell package per platform,
  named for the platform. That rule is untouched; only the count around it moved.
  - **The test, applied in both directions: does the boundary correspond to something a CONSUMER
    experiences?** "I am building an Android app" does — so mobile SPLIT even though the two share
    every line of source. "WinForms without WebView2" does not — this kit's premise is React in a
    webview, so that consumer cannot exist, and Windows MERGED. The same question produced opposite
    answers, which is how you know it is the right question.
  - **What the old Windows split was actually protecting was an adoption STAGE, not a configuration.**
    The evidence for it was a first adopter saying "Stage 1 carries no IPC dependency, so it deletes
    the most duplicated code for the least risk" — a statement about the ORDER of adoption. They
    take WebView2 at Stage 3 regardless.
  - **Two arguments I made against merging, and the measurements that killed them.** "Sessions is
    269 lines of SemVer surface" — it adds no dependency of its own, and the same types are
    maintained either way; the owner's counter that we swallow `Microsoft.Maui.Controls`' entire
    surface without blinking was correct. "WinForms-only consumers avoid 52.6 MB" — that is dev-time
    RESTORE size, not shipped bytes; the WebView2 runtime is an Evergreen system component. Measuring
    the easy thing instead of the relevant thing is the mistake to remember here.
  - **D19's layer rule survives, one level down.** Windows primitives and web hosting are still one
    layer with a direction: `Shell/` must never depend on `WebView/`. The edge became internal, not
    absent.
  - **The mobile packages share SOURCE, not an assembly** (`src/Shenora.Mobile/`, deliberately with no
    csproj). A third assembly would either be published — a package nobody asks for, carrying its own
    surface — or need embedding tricks to hide it. Divergence goes in each project's `Platforms/`
    folder, which the MAUI SDK includes per TFM, so it needs no `#if`.
  - **Naming is by platform, not by framework**, because the two mobile faces do not even share a web
    engine (Chromium's WebView on Android, WKWebView on iOS) and `Shenora.iOS` never touches WebView2.
    A framework name would have described the build system rather than the thing.
  - **Cost, stated plainly:** three published ids retire. Migration is a rename — the merged API
    surface was diffed against the three old baselines and is identical once namespaces are rewritten.

- **D38 — an off-screen session gets the app's own BUNDLE, and deliberately not its custom SCHEMES.**
  (2026-08-03, closing E1.) `SessionBrowserOptions` takes `VirtualHost` + `ResourceProvider` +
  `FolderMappings`, so a packaged desktop app can co-browse or off-screen-render its own frontend. Until
  then a session reached NETWORK-reachable URLs only: a session browser builds its own
  `CoreWebView2Environment` with none of the shell's serving on it, so a navigation to
  `https://app.local/…` came up as WebView2's "can't reach this page".
  - **It is the SAME two option names the host already uses**, and the recipe is to pass the host's own
    values through. Mirroring rather than inventing is the point: an app wiring both reads the same
    words twice, and passing the same provider INSTANCE means the session's requests hit a cache the
    shell already warmed.
  - **Both halves or neither, refused at initialization.** Either alone serves nothing, and the symptom
    of that is identical to the bug being fixed — so a silent no-op here would be indistinguishable
    from a regression. Same convention as `WebViewHost`'s constructor refusing an unregistered deferred
    scheme.
  - **The app's `RequestFilter` is consulted BEFORE the bundle**, and both live in ONE
    `WebResourceRequested` handler. Two reasons, and the second is the load-bearing one: a blocked
    request is a stated policy the kit must not override from a path the app cannot see; and two
    handlers each assigning `args.Response` is last-writer-wins by subscription order, which is not a
    contract to rest a security boundary on.
  - **NOT shipped, and this is the deliberate half: a custom/deferred SCHEME inside a session**
    (`app://`, `media://`). WebView2 accepts scheme registrations only at ENVIRONMENT creation, so this
    is a materially bigger surface than the bundle pair — env options, `AllowedOrigins`, CORS — and no
    consumer has needed it. `generic-library.md`'s rule applies as written: a capability someone needs
    and cannot express is a gap; one nobody has needed is speculation. Recorded as a known limit.
  - **`SessionController` still exposes no `CoreWebView2`.** The E1 finding named that absence as
    evidence the gap could not be worked around, not as the fix. Handing out the raw browser object
    would make every future session capability an escape hatch instead of a seam.
  - **The shared serving code means one header now means two different things, and that is DOCUMENTED
    rather than special-cased.** Bundle responses carry `Access-Control-Allow-Origin: *`. In the app
    shell the bundle IS the document's own origin, so it barely matters; in a session the page can be
    ANY origin, so script in a third-party page being co-browsed could `fetch` the whole bundle. Two
    fixes were considered and rejected in favour of saying so plainly on the option, in `ADOPTION.md`
    and here. Dropping the header would change `WebViewHost`'s behaviour to fix a session concern, and
    it is load-bearing for a dev-mode page on a different origin. Gating serving on `core.Source` being
    the bundle host walks straight into the bug `ShouldBlockRequest`'s `pageUri` normalization exists to
    prevent — at the moment the FIRST document is requested the source is still the previous page, so
    the rule would have to special-case empty/`about:blank`, and a source-dependent serving rule is
    exactly the subtlety that produced that bug. The exposure is the app's own shipped frontend, the
    options are per-SESSION, and an app co-browsing other people's pages already gives that session its
    own options object for profile isolation — so the mitigation is free and the hazard just has to be
    visible. Revisit if a consumer ever needs the bundle served to a session on a foreign origin.
  - **Why this was invisible for so long:** it only bites a desktop-only app serving an EMBEDDED
    bundle. A server-backed profile puts its pages on a real loopback origin, both sample demos work in
    dev mode, and the e2e runs in dev. A gap whose reproduction requires the packaged build is exactly
    what the "prove it against the sample" gate exists for.

- **D39 — the auxiliary-SESSION stack stays a DESKTOP capability. Both mobile shells host a webview;
  that is not the same thing.** (Owner asked directly, 2026-08-03: *"since on both mobile env we also
  have fake browser right? is that safe to do the same session logic?"*) `StreamingSession`,
  `RenderSessionPool` and `InteractiveSession` do NOT port, and the reason is CAPABILITY — which
  matters because it means the answer does not rest on a store-policy reading that could change.
  - **The stack rests on CDP, not on "a webview".** `Page.startScreencast`,
    `Emulation.setDeviceMetricsOverride`, `Input.dispatchMouseEvent`/`insertText`/`dispatchKeyEvent`.
    Neither mobile shell has an in-process CDP client. Android is Chromium underneath, so a DevTools
    endpoint exists — but only for an EXTERNAL client to attach after
    `setWebContentsDebuggingEnabled(true)`, which is a security red flag to ship in release and is not
    an in-process API regardless. iOS is WebKit: no CDP at all, and no public synthetic-input path
    (OS-level touch synthesis is private API, which is a rejection independent of any policy).
  - **THE TRAP, and the real reason this needs writing down.** A port IS buildable behind the same
    interface — frame-polling (`View.draw(Canvas)` / `takeSnapshot`) plus `evaluateJavaScript`
    dispatching synthetic DOM events. It would compile, demo, and be materially WEAKER: polled instead
    of change-driven, and the events are `isTrusted: false`. Untrusted events are precisely what fails
    on the pages `InteractiveSession` exists for (verification challenges, auth flows). Same method
    name, different guarantee — D35's shape exactly, and `mobile-shells.md`'s warning is why it is
    tempting: *the C# ports for free, so every real cost lands somewhere else*.
  - **`HybridWebView` also has no request interception at all**, so the bundle seam D38 just added has
    no mobile analogue either. There is no seam to plug one into (recorded when the mobile shell was
    built, and still true).
  - **Store policy is a SECOND reason and is NOT verified here — do not cite this entry as if it were.**
    The shapes to expect friction on are `InteractiveSession` (a hidden webview over a per-provider
    profile capturing cookies reads as credential interception even when the user does the typing) and
    streaming third-party pages out of the device. Guidelines change; check the current text before
    relying on any of that. It is listed second deliberately, because the capability argument alone
    settles the decision.
  - **What the mobile answer IS, decomposed the way D35 decomposes "open a folder".** *Show the user a
    web page* → `IUrlLauncher`, already shipped (in-app browser tab / system browser). *Log the user
    into a third-party provider* → the platform auth session (`WebAuthenticator` →
    `ASWebAuthenticationSession` / Custom Tabs), which is BETTER than the kit's version, not a
    downgrade: the cookies stay in the system and the app never sees them. *Render my own UI
    off-screen* → does not arise; on mobile the app's UI already IS the webview.
  - **No stub and no refusal is needed, and that is the LAYERING paying off rather than an omission.**
    The whole stack lives in `Shenora.Windows`, which the mobile source does not reference, so portable
    app logic cannot NAME `StreamingSession` in the first place — there is nothing for a mobile shell to
    refuse. This is the same finding that closed A2: the hole a capability-stub proposal describes does
    not exist, because D19/D20 already prevents it. Contrast `IFileDialogs`, which IS a Core contract
    and therefore DOES need a loud refusal per D33.
  - So the honest statement is not "mobile loses this" but "the platform already ships the sanctioned
    version of each intent". Revisit only if a platform gains a real in-process automation surface —
    not because a webview exists.

- **D40 · D41 — media as its own package family. RETIRED 2026-08-07, and the bodies are gone rather than
  banner-stacked.** Both entries governed **`Shenora.Media` + `Shenora.Media.{Windows,Android,iOS}`** — a
  nine-package set. None of it exists:
  - The three `Media.{Platform}` packages were **deleted by D45 (2026-08-04) before any of them shipped**.
    Serving bytes to a page turned out to be resource INTERCEPTION, which configures a webview and is
    therefore a shell capability, so they had nothing left to hold.
  - **`Shenora.Media` itself was folded into `Shenora.Core` by D53 (2026-08-07)**, because D40's premise —
    "a demuxer or image codec is real shipped bytes" — was made permanently false by D51, and D52 framed
    media repair as shell work.

  **The one rule that outlived both, and it still holds:** app logic names the media types and compiles on
  `net10.0` with no platform reference, enforced by `samples/Shenora.Sample.Logic` turning RED if a platform
  type reaches it. That tripwire is unchanged — only the reference carrying it moved to Core.

  ⚠ **Deleted rather than amended, deliberately, and this is a note about how to read this FILE.** The
  amend-in-place rule exists so a reader can follow why a real thing changed. It does not earn its keep for
  a decision whose subject NEVER EXISTED: D41's own banner already said "the package family this entry
  governs was never built", and forty lines of range-versioning rules for packages nobody can install is
  noise that makes the surrounding stack harder to read, not safer. Owner, 2026-08-07: *"we should do a
  cleanup, remove everything thats irrelevant anymore which is clearer than keep adding."* Git history has
  both entries in full. **The test applied: does this describe something that shipped? If not, delete it and
  say what replaced it.** D42–D45, D51 and D52 are all still live and are untouched.
- **D42 — an ENGINE is the primary playback path on every platform, including mobile. Corrects D40/§0c's
  "mobile uses the platform".** (Owner, 2026-08-03: *"I prefer to use engine, because mobile library is not
  stable to support different type of media but if we use engine we have the control"*.)
  - **The argument is CONSISTENCY, and it beats the byte count.** An engine gives ONE behaviour matrix
    across three platforms, which is the same instinct that produced D41's unified interface.
  - **EVIDENCE STATUS, because the owner asked whether the premise was real rather than asserting it
    (2026-08-03) — and the parts divide cleanly:**
    - ✅ **VERIFIED on a device** (Android 12 emulator, `adb`): codec support is **vendor-declared per
      device**. `/system/etc/media_codecs.xml` plus `media_codecs_google_video.xml` and
      `media_codecs_performance.xml`, and the decoder list mixes Google's software defaults with SoC-vendor
      hardware decoders — `OMX.qcom.video.decoder.avc`/`.hevc` sitting beside `OMX.google.*`. The set is a
      function of the CHIPSET, which is why `MediaCodecList` is a runtime query rather than a constant.
    - ✅ **VERIFIED:** that device's declared video decode set is H.263 / H.264 / HEVC / MPEG-4 / VP8 / VP9
      — finite, and with real gaps (no AV1).
    - ✅ **VERIFIED as a distinct and worse axis: CONTAINERS.** `MediaCodec` decodes codecs;
      `MediaExtractor` handles containers, and its MKV support is thin. So **H.264-in-MKV can fail on
      Android while H.264 decodes perfectly** — exactly the case the video sibling's `remux` mode exists
      for, and a failure a codec table alone would never predict.
    - ✅ **Well-founded but not measured here:** that behaviour varies too — seeking semantics, subtitle
      handling, track selection, gapless playback across `MediaPlayer` / `ExoPlayer` / `AVPlayer`.
    - ❌ **NOT verifiable by this repo: how OFTEN the variance bites a real catalogue.** That is the
      owner's field experience and it is the deciding input, recorded as such rather than dressed up as a
      measurement. The mechanism is proven; the frequency is judgement, and it is the owner's to make.
      **Their observed rate: a platform player failing on roughly ONE THIRD of a real collection**
      (2026-08-03). That is not implausible — it is arguably conservative, and the composition explains it:
      MKV containers (most video rips, and `MediaExtractor`'s MKV support is partial), **AC3 / E-AC3 / DTS
      audio, which are LICENSED and not in Android's mandatory set**, HEVC **10-bit** (Main10 is a separate
      profile from the `hevc` a device may declare), plus AV1, VC-1 and MPEG-2. At a one-in-three failure
      rate an engine stops being a preference and becomes the only way to ship a media app with a
      predictable support story.
  - **THE DESIGN CONSEQUENCE, which this data point is what surfaced: the AUDIO track is often what fails,
    not the video.** A file shows picture with no sound, or refuses outright, because of AC3 — while its
    H.264 video decodes perfectly. So **a playability verdict must be PER STREAM, not one boolean for the
    file.** Both donor planners already hint at this (the video sibling's `MediaInfo` carries `Codec` AND
    `AudioCodec`; Sonora's is audio-only because that is its whole domain), and a single
    `CanPlay(file) -> bool` would have been wrong in the most common failure case. It also explains why
    `remux` earns its place beside `transcode`: copying a fine video stream while re-encoding only the
    audio is both the cheap fix and the frequent one.
  - ⚠ **A CLAIM THIS REPO MADE TWICE AND HAD WRONG:** that shipping an engine on mobile "duplicates
    hardware decoders the OS already exposes and burns battery doing it in software". **False.** LibVLC has
    MediaCodec (Android) and VideoToolbox (iOS) hardware-acceleration backends — it USES the platform
    decoders and falls back to software only for what the platform cannot do. So an engine is strictly a
    superset of the platform player, not a power trade. The measured 0 MB in §4a was real; the reasoning
    attached to it was not. (Worth confirming the per-format hardware path on a device before relying on it
    for a specific codec — that is a device-run claim, not a build-time one.)
  - **What it costs, measured (§4a):** +42.2 MB per Android ABI (arm64), +33.5 MB for the iOS device
    slice, ~25–30 MB pruned on Windows. An arm64-only Android app therefore grows ~42 MB. For a media
    application that is an acceptable price for a single support matrix; for an app that plays the odd
    notification sound it is not, which is exactly why this is the APP's choice and not the kit's.
  - **The kit still ships no engine (§2 stands).** `Shenora.Media.{Platform}` must not hard-depend on one,
    because that would force the 42 MB on every consumer and pick their licence. What changes is the
    EXPECTED path the contracts are designed for: engine-first, with the platform player as an optional
    implementation rather than the default. A contract shaped around the platform player would have baked
    in the per-device variance this decision exists to remove.
  - **HOW the engine is obtained: REFERENCE UPSTREAM, never vendor.** (Owner: *"we dont need a VLC bundle,
    the correct way is just reference the vlc if that existing… if not then we ship one for backup
    reference purpose"*.) The upstream story is complete and first-party, all published by `videolan`:
    `LibVLCSharp` 3.10 · `LibVLCSharp.WinForms` · **`LibVLCSharp.MAUI` 3.10** · and the natives
    `VideoLAN.LibVLC.Windows` / `.Android` / `.iOS`. **So the backup case does not arise on any platform**
    — verified by package search, not assumed. Revisit only if upstream drops a target.
  - **THE KIT'S OWN BUILD MUST STAY LIGHT, and that is a harder constraint than "do not force it on
    consumers".** (Owner: *"so our build can go together does not rely on heavy resources"*.) Referencing
    the three natives would add **~823 MB of restore** (Windows 410 + Android 257 + iOS 157, measured
    2026-08-03) to **every** `dev.mjs verify` and every CI run. So `Shenora.Media.{Platform}` references
    **no engine package at all** — not `LibVLCSharp`, not a native. It compiles against the platform SDK
    and the kit's own contracts, and an app supplies whatever engine it wants behind them.
  - **Consequence for the gate, and there is precedent: the kit's gate proves the CONTRACT, not the
    engine.** The sample can exercise the surface through the platform player (0 MB) — enough to prove the
    contract, the marshalling and the lifecycle. Proving a real engine end to end is an on-demand probe
    (`devtools/_*`, gitignored) rather than part of `verify`, exactly as the C++ launcher is tested by a
    Node harness against a prebuilt exe rather than compiled by this repo. State plainly in the docs which
    half the green gate covers — the P0–P5 latent defects passed five reviews precisely because nobody
    said.
  - **Licence remains the open item, and it is the consumer's to settle**, not the kit's: LibVLC is
    LGPL 2.1+ (dynamic linking keeps a closed-source app clear, and `DynamicMobileVLCKit` is the dynamic
    variant), but some plugins are GPL and ffmpeg is LGPL only when built without its GPL parts. Since the
    kit references nothing, it never makes that choice for anyone — which is the same reason §2 exists.

- **D43 — the media contracts split by DEPENDENCY, not by feature name. "Thumbnail" is two mechanisms and
  gets two homes.** (Owner asked for thumbnails alongside playback, 2026-08-03: *"yes we kind need that too
  if possible"*. It is possible on all three platforms at zero added bytes — see below.)

  ⚠ **The HOMES it names are gone; the AXIS it chose is not.** The `Shenora.Media.{Platform}` family this
  entry distributed contracts across was deleted by D45, and media itself moved into `Shenora.Core` by
  D53 — so read "two homes" as two FOLDERS, not two packages. What survives is the question it settled and
  which is still the right one: split by **what an operation NEEDS**, not by the feature name a user would
  give it. Thumbnails are still unbuilt (D15 — no consumer has asked twice).
  - **The question was whether "play this" and "thumbnail this" are one host contract or two. Neither: the
    honest axis is what each operation NEEDS.**

    | Capability | Needs | Windows | Android | iOS |
    |---|---|---|---|---|
    | **Probe** — duration, dimensions, streams, codecs | a demuxer | ffprobe / engine | `MediaMetadataRetriever` | `AVAsset` |
    | **Frame grab** — a still at time T | a **decoder** (same as playback) | engine / ffmpeg | `MediaMetadataRetriever.FrameAtTime` | `AVAssetImageGenerator` |
    | **Playback surface** | a **decoder** + a view | engine + control | `SurfaceView` | `AVPlayerLayer` |
    | **Image resize** — make this picture smaller | an **image codec**, NOT a media decoder | `System.Drawing`/WIC (already in the WinForms shell) | `BitmapFactory` | `UIImage` |

  - **So probe + frame-grab + surface are ONE family** (they share the media decoder), and **image resize is
    its own contract** (a different dependency entirely). The playability verdict stays portable logic over
    a probe result — a pure function in `Shenora.Media`, per stream (D42).
  - **VERIFIED by compiling: image resize needs no extra package on any platform.** Android
    `BitmapFactory` with the `InJustDecodeBounds` → `InSampleSize` low-memory path, then
    `Bitmap.CreateScaledBitmap` and `Compress`; iOS `UIImage.FromFile` +
    `UIGraphicsImageRenderer.CreateImage` + `AsJPEG`; Windows `System.Drawing`/WIC, which the WinForms
    shell already brings. **So thumbnails cost 0 MB everywhere** — unlike playback, which the owner has
    accepted an engine for (D42). Sonora's ImageSharp dependency does not need porting.
    ⚠ **Android trap found while verifying:** `Bitmap.CompressFormat.Webp` is obsoleted on API 30+ (split
    into `WebpLossy`/`WebpLossless`) while this kit's floor is API 21, so a WebP encoder here must handle
    both or use JPEG. `CA1422` is an ERROR in this repo, so it fails the build rather than warning.
  - **NAMING: do not ship a `Thumbnail` type that spans both mechanisms.** That would be D35's
    same-word-different-guarantee mistake in miniature — the harvest already found "thumbnail" meaning
    *extract a frame* in one sibling and *resize an image* in another. Name the mechanism (D22).
  - **The APP unifies them, not the kit.** "Give me a thumbnail for this library item" needs to know
    whether the item is a video or a picture, and only the app does. A kit facade that dispatched between
    the two would be deciding the app's policy — and note Sonora keys its cache per item KIND precisely
    because it has that knowledge.
  - **⚠ DEFERRED, not queued (owner, 2026-08-03: *"lets focus on the player itself first"*).** Thumbnails
    and the transcode/serving path are recorded here so the analysis is not re-done, but **the PLAYER comes
    first**. Nothing above should be built until the surface contract exists — and note that the player
    needs none of it.

- **D44 — the media URL names NO origin, and the two mobile shells get OPPOSITE response BODIES. Measured
  on devices, and it corrects three things this repo had already written down.** (DM1, 2026-08-03. Full
  the probe is `samples/Shenora.Sample.Maui/MediaRangeProbe.cs`, which is what re-measures it.)
  - **The URL is a RESERVED PATH on the page's own origin, reached by a RELATIVE url**
    (`/<reserved>/?src=…`), not a custom scheme and not a virtual host. Neither of the obvious answers
    works on both shells: Android intercepts `app://` and then its media pipeline **refuses** it
    (`MEDIA_ERR_SRC_NOT_SUPPORTED`, instantly, even for a plain 200 with a correct `Content-Type`), while
    iOS intercepts **only** `app://` and lets an https host reach the real network. The page's own origin
    is intercepted and media-capable on both **by construction**, because it is what the platform already
    serves the bundle from — `https://0.0.0.1/` on Android, `app://0.0.0.1/` on iOS. The path must be
    reserved: it shadows the bundle.
    - This supersedes the media design's "it MUST be the app scheme", which generalised an iOS
      INTERCEPTION fact into a rule about PLAYBACK. Those are different questions, and the earlier probe
      only ever asked the first.
  - **`e.PlatformArgs` is NOT required on either shell.** The portable
    `SetResponse(int, string, IReadOnlyDictionary<string,string>?, Stream?)` exists on both mobile TFMs
    and MAUI forwards status, reason phrase and every header (including lifting `Content-Type` into
    Android's native constructor argument). The previous "there is no way to set a response header
    through the portable seam" read ONE overload as the whole set, and it had put a per-platform
    implementation on the critical path before any contract existed. **Cost of believing it: a whole
    design constraint. Cost of checking it: one build.**
  - **⚠ THE ASYMMETRY, and it is the load-bearing one: Android's seam applies the `Range` START ITSELF to
    whatever body it is handed (and ignores the range END); iOS passes the body through verbatim.** So the
    SAME portable request needs an UNSLICED body on Android and a SLICED one on iOS. Getting it wrong is
    not a graceful degradation: a sliced body on Android has the offset applied twice — `bytes=4-11`
    returned four bytes of file bytes 8-11 — and a player asking for a file's tail receives an empty body
    and **retries the identical range forever**.
  - **This was written as the measured justification for D40's `Shenora.Media.{Platform}` split, and one
    day later it justified something else instead.** The observation stands — the platforms need genuinely
    opposite behaviour behind one portable contract — but D45 (2026-08-04) put that divergence in the SHELL
    packages, where the platform code already lived, and deleted the media platform packages entirely.
    Read this bullet as evidence about the DIVERGENCE, not about where it belongs. Everything else is
    identical across the two — the URL, the call, the 206, `Content-Range`, `Accept-Ranges`. One row of the
    table differs, and an app must never have to know which side it is on.
  - **⚠ The trap this leaves for whoever implements it: the wrong choice looks CORRECT.** A faststart file
    only ever requests `bytes=0-`, where the double-skip is a no-op — so the naive implementation plays
    perfectly on the file everyone tests with and fails on every file whose index is at the end. Any test
    for this needs the control pair the probe ships (same content, `moov` at the front vs at the end), and
    the honest instrument is an explicit `fetch` with a `Range` asserting the returned BYTES, not a
    `<video>` element, which can only ever report "no supported source".

- **D45 — resource interception is a MIDDLEWARE PIPELINE in `Shenora.Core`, implemented by each SHELL, with
  content handled by a FAMILY of opt-in middleware packages (`Shenora.Media`, later `.Image`, `.Excel`).
  Re-layers D40 and supersedes its `Media.{Platform}` set.** (Owner, 2026-08-04, reached in five steps —
  each widened the scope, and the last two settled the shape.)
  - **The steps, because the order is the argument:** (1) *"the interceptor interface should live in the core,
    and the implementation should live in mobile.ios/android, and media just taking care of media logic"*;
    (2) *"desktop will also have issue with access local folder/files so the interceptor is needed"* — not a
    mobile workaround; (3) *"even file access too"* — media is ONE CASE; (4) *"it's more like a middleware
    design if you think this way"*; (5) *"lets do media since we can have .Image .Excel later"*.
  - **Interception is a SHELL capability.** It configures a webview, so `IWebViewInterceptor` is a contract in
    `Shenora.Core` and each of `Shenora.Windows`/`.Android`/`.iOS` implements it. A feature depends on the
    contract and stays portable — D19/D20's law, applied to resources.
  - **Every shell needs it, which is what makes it Core's business rather than mobile's.** A page cannot reach
    a local file on ANY shell: `file://` is blocked from a virtual-host origin and would be wrong anyway,
    since it hands over the whole filesystem. So an interceptor is how local content reaches a page at all,
    and one contract means **path containment is written once instead of three times** — a hand-rolled
    containment check being the exact defect this kit already had to fix (`%2e%2e%2f`, and `Path.Combine`
    discarding its first argument on a rooted path).
  - **MIDDLEWARE, not a list of handlers, because the cross-cutting concerns are the point.** Containment, the
    SSRF guard, a cache, logging what an opaque payload decoded to, a metric — each WRAPS the next rather than
    terminating. A "first non-null wins" list cannot express any of them. **And the kit already made this
    choice once:** `IMessageDispatcher` is a composable middleware pipeline over one transport, so this is
    that shape applied to RESOURCES instead of messages, and the precedent, vocabulary and review instincts
    all transfer.
  - **Content is a FAMILY of opt-in middlewares, one per KIND** — `Shenora.Media` today, `Shenora.Image` and
    `Shenora.Excel` foreseen. Each is a layer an app adds to the pipeline; none is required. This is what
    keeps `Shenora.Media` the right name: it covers audio AND video (the frequent real failure is AC-3
    *audio*), and `Shenora.Video` would be wrong on the first day.
  - **⚠ It follows that anything the FAMILY shares belongs in Core, not in a member.** `MediaCacheKey` keys any
    derived artefact — a thumbnail, a probe result, a rendered sheet — so it moved to `Shenora.Core` beside
    `Files` **and was renamed `DerivedCacheKey`**, because the new name says what is cached rather than which
    feature happened to need it first. A shared helper living in `.Media` would make `.Image` depend on media
    to cache a thumbnail.
  - **`Shenora.Media.Android` and `Shenora.Media.iOS` are DELETED** (8 package ids → 6 **as of 2026-08-04**;
    D48 later took it back to 8 by a different route — see the header table). With interception in
    the shells and generic serving in Core, they held only the platform's range-delivery constant — which is
    a property of the INTERCEPTION, so it became `Core.WebViewRangeDelivery` and the packages had nothing
    left. **Free: all three `shenora.media*` ids were unpublished (verified 404) when this was decided.**
    They return only if genuinely platform-specific media work lands — the frame-grab pixels D43 deferred.
  - **What D40 got right and what it did not.** Right: media is optional and must not tax `Shenora.Core`'s
    consumers. Wrong in one clause — it argued the split on DEPENDENCIES ("an image codec or a container
    parser is real shipped bytes"), and `Shenora.Media` ships neither; it is pure functions and costs a
    consumer nothing. The argument that survives is **vocabulary**: an app that never plays media should not
    carry containers and codecs on its surface. Weaker than claimed, still sound — and D43's deferred
    thumbnail work would restore the original one.
  - **The page's half is ONE npm package for every shell** (D36): `mediaUrl(payload)` returns a RELATIVE
    `<route>?<base64url>`, and `ShellCapability.LocalFiles` says whether the host can serve at all.
    ⚠ The handshake must advertise NEITHER the url scheme NOR the range delivery — a page told "you are on
    iOS, use `app://`" is branching on platform again, and it is unnecessary because a relative url already
    resolves correctly on each shell (D44's matrix).

  **BUILT, on all three shells — and three things only the desktop half settled (2026-08-04).**

  - **The registry and composition are in Core, not in each shell** (`WebViewResourcePipeline`). Writing the
    back-to-front chain build three times is three chances to invert someone's routing, and neither the
    copy-on-write array nor reference-identity removal is obvious. It is also the only way any of it is
    TESTABLE: order, decline-and-fall-through, wrapping and independent removal are all provable with no
    webview, where before this the only way to learn whether a route ran first was to launch a device. A
    shell implementation is now just the platform's event glue — and the two are genuinely different there:
    mobile must resolve the pipeline SYNCHRONOUSLY (both platforms need status and headers when the event
    returns), while the desktop has a deferral and must not block the UI thread.
  - **The desktop interceptor shares the page's own origin with the packaged bundle, and the BUNDLE wins.**
    A relative media URL on the desktop is `https://app.local/media?…` — the same virtual host the frontend
    is served from — so one handler now answers both. Order: the bundle is asked first and, if it *has* the
    path, serves it synchronously and inline (the pre-existing invariant: deferring the MAIN DOCUMENT stalls
    the initial navigation, which only ever reproduced in production); a path it does NOT have falls through
    to the pipeline instead of 404ing, which is what makes the relative-URL contract work here at all.
    ⚠ **It is the opposite order from mobile,** where the platform serves the bundle and therefore only sees
    what the pipeline declined. The rule that holds on both: **keep interception paths off bundle paths** — a
    route that collides with one is relying on a difference between shells.
    One handler, not two, for the reason `SessionBrowser.DecideRequest` already documents: two
    `WebResourceRequested` subscriptions each assigning `args.Response` is last-writer-wins by subscription
    order.
  - **In DEV the page lives on the Vite server, so that origin is filtered too.** WebView2 raises
    `WebResourceRequested` only for registered filter patterns, and in production the bundle's pattern
    already covers the page's origin — in development nothing did. Without the extra filter a file route
    works in a packaged build and 404s through every day of development, which is the worst possible place
    for the gap. A blanket `"*"` was rejected (it raises the event for every request the page makes,
    including the open internet), and a `ProductionUrl` origin is deliberately NOT filtered: that profile has
    a real in-process HTTP server behind the page, and letting middleware shadow Kestrel's routes means two
    servers for one origin, silently disagreeing.
  - **`WebViewRangeDelivery.Sliced` on the desktop is MEASURED.** `samples/Shenora.Sample.Desktop`'s
    `InterceptorProbe` answers `bytes=3-7` and the page reads `DEFGH`; sabotaged to `Unsliced` it reads 1000
    bytes starting at `A`, which is the direct observation that WebView2 does not apply the offset itself.
    The probe also pins containment (a traversal to a file that really exists → 404) and that the bundle
    still wins on the shared origin. Both directions verified, per the repo's tripwire rule.
  - **The SSRF policy seam did not survive the move, deliberately.** `MediaAccess.IsRemoteAllowed` had no
    caller once serving became generic — nothing in the kit fetches a remote resource on a page's behalf — so
    it would have shipped as a public type with no consumer. It returns with the middleware that needs it
    (D15). The reasoning to keep: the host can reach addresses the page cannot, so a *throwing* policy must
    deny.

- **D46 — a capability that needs a newer PLATFORM TARGET is opt-in, never imposed. The consumer picks the
  target; the kit makes the consequence explicit** (owner, 2026-08-04: *"so we let the consumer decide their
  target platform instead of force it?"*).
  - **The case that produced it.** `IPlaybackSession` on the desktop needs `SystemMediaTransportControls`,
    which is WinRT, and the WinRT projections exist only when the TFM names a Windows SDK version — with a
    bare `net10.0-windows`, `Windows.Media` is not a namespace at all (measured: `CS0234`). The first
    implementation simply raised `Shenora.Windows` to a versioned TFM, which made every Windows consumer
    retarget for a capability most of them will never call.
  - **The rule: multi-target, and let the plain variant REFUSE BY NAME.** `Shenora.Windows` ships
    `net10.0-windows` and `net10.0-windows10.0.17763.0`. On the plain one the type still exists and throws
    `ShellCapability.NotSupported` at construction with the one-line remedy in the message. Absent would be
    worse — resolving a missing service names neither the shell nor the reason — and that is the same
    reasoning `ShellCapability` was created for.
  - **Why a build flag CANNOT do this, which is the question worth pre-answering.** A consumer's MSBuild
    property is evaluated long after the kit's assemblies were compiled and packed; all a csproj can do at
    that point is choose a lib folder. So content can only vary by TFM, by package, or by having no
    compile-time dependency at all (hand-rolled COM — invented interop this kit declines). A flag WOULD work
    for a `ProjectReference` consumer building from source, which is exactly why a project reference hides
    packaging (`ADOPTION.md`).
  - **Measured cost, so the trade is not a guess:** ~190 KB of extra nupkg content (two `lib/` folders at
    ~275 KB each before compression), a doubled build for that one package, and one hand-written refusal
    stub. Paid only by Windows consumers.
  - **⚠ Two hand-written variants of one type need TWO gates.** `ApiSurfaceTests` loads the assembly the test
    project itself references, so it sees only one TFM; the plain variant has its own entry in
    `MetadataSurfaceTests` — the machinery that already existed for assemblies the test project cannot
    reference. One type name in one package differing only by TFM must expose the same members, or a consumer
    that retargets finds a different API.
  - **The generalisation, which is the part to apply next time:** the kit must not narrow a consumer's
    supported platforms as a side effect of one feature it happens to implement. If a capability needs a
    newer floor, the floor moves for the consumers who ask for that capability — and the ones who do not get
    a message naming the platform, the capability and the fix. Applies to any future WinRT surface, and to
    the same question on the mobile shells.
  - **Related trap, recorded because it cost a commit:** `TargetPlatformVersion` (from the TFM) is what you
    may COMPILE against; `SupportedOSPlatformVersion`/`TargetPlatformMinVersion` is the floor you RUN on —
    and leaving the latter unset silently defaults it to the former. That is how bumping a TFM for one API
    quietly raises everyone's minimum OS. It is pinned here, matched on `-windows10.` rather than an exact
    TFM string so a later bump cannot slip past the latch, with `CA1416` (a build error in this repo) forcing
    any newer API to be guarded instead.

- **D47 — while ONE repo fully adopts the surface, prefer the CORRECT shape over the compatible one. Ship no
  compatibility aliases; rename when the name is the defect** (owner, 2026-08-05).

  > *"sonora actually is the first one fully adopting all features so you can fix anything into the best here
  > which only cause 1 repo to update"*

  - **What changed is the PRICE of a break, not the rules about it.** `TASKS.md` had said the
    free-breaking-change window closed when the surface was published, which was right about publication and
    wrong about cost: a break against a known, single, same-author adopter is one repo's compile errors,
    found by the compiler and fixed by the person who asked for the change. That is a bounded, visible cost —
    not the unbounded one "published" usually implies.
  - **The test to apply: would this be the shape on a greenfield surface?** If yes, take it and write the
    migration. **Compatibility is not, by itself, a reason to keep a worse API right now.** Backward
    compatibility is a promise you make to people who cannot be in the room; while every consumer is in the
    room, it buys nothing and costs clarity.
  - **Concretely, no `[Obsolete]` aliases.** One was built for the
    `DefaultLaneCapacity` → `GlobalLaneCapacity` rename and deleted the same day on this direction. An alias
    leaves BOTH names on the public surface indefinitely and the misleading one still writeable — which is
    the entire thing a rename exists to prevent — and it costs a backing field, a rule for "what if both are
    set", and a test to pin a promise nobody would otherwise check. Deprecation earns its keep when migration
    is genuinely hard; a rename is one word per site.
  - **What this does NOT change.** A break is still recorded under `### Breaking` in `CHANGELOG.md` with its
    migration, still shows up as API-baseline drift, and still needs a reason. The packages are public on
    nuget.org, so "one adopter" describes who we KNOW of; the record is also what makes a deliberate 1.0
    freeze possible later. Cheap is not free, and undocumented is not an option.
  - **⚠ This expires, and it expires quietly.** It is a property of TODAY's adoption count, not a permanent
    licence — the moment a second repo fully adopts, the calculus reverts and this decision should be amended
    rather than cited. Check the adoption reality before invoking it.
  - **It does not revive every rejected rename.** `ILane` → `IPermitPool` was declined on three grounds and
    only one of them was the cost of the break; the other two (it is the HARVESTED word the donor apps
    already used, and the metaphor is what carries weighted permits — `MissionLane("gpu", Permits: 2)` reads
    as "occupies 2 of the lane's width") stand on their own. A cheaper break is not an argument for a change
    that was rejected on merit.

- **D48 — file operations are a `Shenora.IO.*` FAMILY hanging off `Core`, not part of it. `Shenora.IO` is the
  portable engine; a format or a platform that needs its own dependency gets its own package**
  (owner, 2026-08-05).

  🔴 **ITS PACKAGING CONCLUSION IS REVERSED — see D55 (2026-08-07). `Shenora.IO` and
  `Shenora.IO.Compression` are namespaces inside `Shenora.Core`, not packages.** Kept unabridged, and
  cited from fourteen places, because **what this entry actually proved is still load-bearing and is not
  about packaging**: the dependency edge runs `IO → Core` (checked, not assumed — every type logs through
  `AppCallback`), and that direction is what decided which types could move and which could not. It is
  also what made D55's merge the ONLY available mechanism: a Core that packed `Shenora.IO.dll` would have
  to reference what already references it. Read the layering; ignore the package ids.

  > *"because this include file operation so we should have a sperated library/package for this"* …
  > *"so IO becomes like a contract and logic library just like core media, and compression is one of the
  > option"*

  - **The measurement that decided it.** `Shenora.Core/Io/` was 2,244 lines — **34% of `Shenora.Core`** — and
    all but ~500 of it is the update ENGINE: the journalled queue, path leases, the manifest pair, the staged
    updater. `Shenora.Core` is the package *every other package references*, so a phone app that hosts a page
    and plays a file was carrying a self-updater it will never call. That is the same argument D40 made for
    `Shenora.Media` and it lands the same way.
  - **This APPLIES D37's test rather than overturning it.** D37 asks whether a boundary corresponds to
    something a CONSUMER experiences, and killed the platform-suffix split because "I am on Windows" is not a
    choice you make per feature. "I am building an app that mutates a file tree or self-updates" IS one — some
    apps do it and most do not, and the ones that do not can now say so by not referencing it.
  - **The edge points `IO → Core`, checked rather than assumed.** Every type in the engine logs through
    `AppCallback`, Core's one guarded-callback helper. That direction is what decided the LEFTOVERS, and they
    are the interesting part of this decision:
    - `Files`/`FileReplacement` stay in `Core` — `Core`'s own `IFileDialogs.SaveAsync` default calls
      `Files.BeginReplace`, so moving them would invert the edge.
    - `PathClaims` stays — it is a claim SCOPE built on the mission types. Scheduling vocabulary that happens
      to be about paths, not a file operation.
    - `IFileLockInspector`/`FileLockHolder` were SPLIT BACK OUT of the move (they were in `Io/` and went with
      it in the first pass). "Who is holding this file open?" has a genuinely different answer per platform —
      Windows asks the Restart Manager — so it is a portable contract with a shell implementation, exactly
      like `IFileDialogs` and `IPlaybackSession`. **A shell package must be able to implement a Core contract
      without referencing an optional feature package**; leaving it in `Shenora.IO` would have forced
      `Shenora.Windows → Shenora.IO` for one interface. Its sibling `IPathLocker` went the other way for the
      opposite reason: advisory lock files are portable, so contract and implementation ship together with
      the engine that uses them.
  - **`Shenora.IO.Compression` is the first member, and the family is why it is named that way.** Zip needs no
    native engine and rides on `System.IO.Compression`; 7-Zip or rar would each drag real shipped bytes, so
    each earns its own package rather than a flag. Naming the package after the framework's own area also made
    the TYPES smaller — `ExtractionResult`, not `ArchiveExtractionResult` — because the namespace already says
    what they operate on. It was briefly shipped as `Shenora.Archives` with `Archive…` type names, which
    over-claimed (everything in it is zip-only) and contradicted the kit's lexicon note written the same day.
    **A package name that has to be explained by its type names is the wrong package name.**
  - **`Shenora.IO.Windows` / `Shenora.IO.Android` are NOT being created yet, and that is deliberate.** The
    naming leaves room for them, and today they would contain one class and zero classes respectively —
    `RestartManagerFileLockInspector` already lives in `Shenora.Windows`, where it is a shell contract's shell
    implementation and belongs. **Reserve the name, ship the package only when it has contents** (D15). The
    trigger to create one: a platform file API that is not a Core contract — Android's SAF tree, macOS
    security-scoped bookmarks.
  - **⚠ The strongest objection, raised in this batch's review and REJECTED — `Shenora.IO` holds TWO clusters
    that do not touch each other.** Checked rather than assumed: `UpdateStage` references no
    `IFileUpdateQueue`, no `FileUpdate`, no `FileChange` and no `IPathLocker`. So the package is "land a file
    change safely" *plus* "update an installed tree", sharing only `Files` and `AppCallback` — both from
    `Core`. By the letter of D37 those could be two packages. They are not, for three reasons: the consumer
    story is ONE ("my app owns a file tree on disk and must change it without corrupting it"), which is how
    `ADOPTION.md` already teaches them; the cost of unused code inside a package you CHOSE to add is close to
    nothing, unlike the same code in `Core`, which every package drags — that asymmetry is the whole of this
    decision and it does not repeat one level down; and two ~850-line packages is the over-splitting D37 was
    written to stop. **If this is ever revisited, the trigger is a real adopter that wants one and refuses the
    other**, not the observation that the call graph has two components.
  - **Cost, stated because a break needs one:** every moved type changes namespace `Shenora.Core` →
    `Shenora.IO`, which is a `using` line per consuming file plus one `PackageReference`. Thirty public types
    moved, nothing was added or removed, and the API baselines show exactly that — `Shenora.Core.txt` lost 206
    lines and gained none. Cheap under D47, and it will not be cheap for long.
  - **⚠ The gate that did NOT catch the gap this created.** `Shenora.IO.Compression` shipped a day earlier with
    no `docs/ARCHITECTURE.md` entry at all, and every gate stayed green: `doc-drift` checked documented
    dependency ARROWS, retired names and dangling doc links — never "is this shipped package described
    anywhere". A fourth check now requires every packable project to be named in `README.md`'s package table
    and in `ARCHITECTURE.md`. It is exact rather than heuristic (a name either appears or it does not), which
    is the bar `doc-drift`'s own header sets, and it is case-sensitive with a trailing-identifier fence so
    `Shenora.IO` cannot be satisfied by `Shenora.iOS` or by `Shenora.IO.Compression` — both verified.

- **D49 — retired package ids stay LISTED until 1.0. Pre-1.0 ids are retired in ONE deliberate pass, once
  the kit is a fully working app framework** (owner, 2026-08-05: *"its okay let them be there we will retire
  all pre-1.0 packages once we got a fully working app framework working"*).

  - **What this settles.** `Shenora.WinForms`, `Shenora.WebView2` and `Shenora.WebView2.Sessions` were merged
    into `Shenora.Windows` by D37 and are still listed, undeprecated, on nuget.org. That is now a CHOICE with
    a trigger, not an overdue chore — which matters because a note reading "pending, do it next release"
    survived four releases and was re-raised as a finding by the 2026-08-05 review. **A deferral nobody wrote
    down gets rediscovered as a defect.**
  - **Why deferring is the better trade.** Unlisting costs an API key, a `--apply` run and a web-UI pass per
    id, and it buys nothing while the id set is still moving: three ids were retired by D37, one more
    (`Shenora.Archives`) came and went inside a day, and D48 has just added two packages. Retiring them one
    batch at a time means repeating the ceremony every time the shape changes. **The set is not stable yet,
    so the cleanup is not ready to be done once.**
  - **The trigger, stated so it can actually fire:** the kit reaching "a fully working app framework" — the
    same milestone that makes a deliberate 1.0 surface freeze possible. At that point retire EVERY pre-1.0 id
    the set no longer uses, in one pass, with `dev.mjs nuget-retire`. Until then the tool stays built and
    unused, which is fine: it already refuses to unlist an id whose replacement is not published.
  - **What must stay true in the meantime.** Nothing may CLAIM the retirement has happened. `README.md` said
    the old ids "carry a deprecation notice" — they do not, and that sentence was removed in the same review
    that produced this entry. An unlisted-but-restorable id is harmless; a doc describing a state of
    nuget.org that does not exist is not.

- **D50 — the native launcher is a LIBRARY plus a template, written in C++, shipped as one binary per
  platform** (owner, 2026-08-05; it overturned an earlier "template only" plan). **Shipped in 0.10.0.**

  > *"so it probably need to be template + c++ library"* … *"the only requirement is (compatibale linux+
  > windows for future needs, and small) we can have for different platform use differnt binary too just
  > like the mobile development"*

  - **The requirements, in the owner's terms:** Linux **and** Windows (Linux for a future need, not today);
    **small**; one binary per platform is fine, explicitly on the mobile model — ONE shared source tree, N
    platform artifacts.
  - **Library + template is not a judgement call — §0 measured the seam twice.** Two siblings, no contact,
    wrote the same three files: `updater.cpp` (234/145 lines) and `dotnet_runtime.cpp` (170/116) are generic
    → the LIBRARY; `main.cpp` (142/76) is per-app → the TEMPLATE. The library is the larger half. What stays
    per-app is smaller than "a launcher": exe name, icon and version resources, the code signature, topology
    constants, failure-UI wording. On Windows those are embedded, so an adopter applies them as a post-build
    step before signing; on Linux the same facts live in a `.desktop` file and never touch the binary. **That
    asymmetry is a build step, not a source fork** — which is what makes one shared tree honest.
  - **Rust was evaluated properly and lost on the criterion the owner named.** The question put was whether
    it helps **NuGet packing**, because that would have outweighed size. It does not, at all: a `.nupkg` is a
    zip, `runtimes/{rid}/native/` is a folder convention, and MSBuild's RID-based copy cannot tell what
    compiler produced the bytes. The packing story is identical for any language that emits a native binary.
    - ⚠ **A cross-compilation advantage was also claimed for Rust, and it was overstated in the discussion
      that produced this entry — recorded so it is not repeated.** `--target x86_64-unknown-linux-musl` still
      needs a linker for the target (Zig, Docker, or WSL). It is somewhat smoother than C++, not
      categorically different — and once each target builds on its own CI runner, which is what the mobile
      packages already do, the advantage disappears for both languages.
  - **D8 decides it.** Two proven C++ implementations exist, in production, one carrying an incident that is
    expensive to re-earn (§4: omitting the launcher from the new manifest made the OLD launcher delete the
    freshly-copied new one). Extraction-first means porting proven code *with its post-mortem comments,
    which are the product*. Rewriting that in a language neither donor uses is the thing D8 exists to
    prevent. Note also that the two-consumer bar is met **in C++ specifically**, not for "a launcher" in the
    abstract.
  - **The durability argument, in the owner's own framing, because it is the forward-looking half:** the
    choice is not about the language being old. It is that C++ **will not lose support in any relevant
    horizon and its platform coverage keeps growing** — AI workloads have pulled real, current investment
    back into the C++ toolchain (the performance layer of essentially every ML stack), so the compilers and
    the platform support are being funded, not merely maintained. For an artifact whose entire job is to run
    on a machine you do not control, before anything else is installed, "there is a mature compiler and a
    stable ABI for this platform" IS the requirement — and it is the one C++ has never failed.
    - **The concrete in-repo payoff:** the toolchain is reusable. D42 says the app supplies the media engine
      and the kit vendors none — LibVLC, ffmpeg, image codecs and any inference shim are all C++-shaped. If
      native work ever appears in this family beyond the launcher, it lands in the toolchain the launcher
      already paid for. Rust would mean a second toolchain talking to the first over FFI.
  - **⚠ C++ was NOT chosen because Rust is worse, and the record should not be read that way.** It was
    chosen because the prior art is in C++, the NuGet benefit that would have justified switching is zero,
    and the toolchain investment is reusable. **Revisit trigger:** a future native component with no C++
    prior art and no native engine to talk to — decide that one on its own evidence rather than by citing
    this entry.
  - **Shape:** one source tree; `std::filesystem` (C++17) for everything portable with the Win32 specifics
    (§4's `GetModuleFileNameW` self-exclusion) behind a thin platform header; **CMake**, so MSVC and
    gcc/clang build the same tree; per-RID binaries from a CI matrix (`windows-latest` + `ubuntu-latest`)
    into `runtimes/win-x64/native/` + `runtimes/linux-x64/native/`. No cross-compilation anywhere.
  - **The JSON parser is a CONFORMANCE requirement, not a taste one.** Whether it is hand-rolled (what both
    siblings did) or vendored single-header `nlohmann/json`, it must agree with `UpdateManifest.Parse` —
    including the two comparison rules already sabotage-verified on the C# side: paths normalise separators
    AND case, hashes compare case-insensitively. A second implementation is a second place to get those
    wrong, and getting either wrong makes a release look either fully changed or fully unchanged.
  - **~~⚠ Sizes are BANDS, not measurements~~ — MEASURED 2026-08-05 when it was built: 322 KB on
    Windows, 46.8 KB on Linux.** MSVC Release with `/O1 /GL` and `/LTCG /OPT:REF /OPT:ICF`; gcc 13 with
    `-Os` and `--gc-sections`. Windows is **above** this entry's own 150–300 KB guess and Linux is far
    below it, and the whole difference is the **statically linked CRT** on Windows — a deliberate trade,
    because a launcher that needs a VC++ redistributable installed has the same bootstrap problem it
    exists to solve. Recorded rather than quietly re-banded: the estimate was wrong in the direction
    estimates usually are, and wrong by 7× in the other direction on the platform nobody had built. The
    Rust figure remains unmeasured and now has no reason to be measured.
  - **⚠ "Both platform files always compile" did NOT mean both TARGETS compiled**, and the first release
    run is what taught it: `platform_posix.cpp` built clean under MSVC for days and failed instantly
    under gcc (`'all_of' is not a member of 'std'` — MSVC drags most of the standard library in through
    other headers). An `#ifdef`-guarded body is only checked by the compiler that takes that branch, so
    a one-platform build proves one platform. Reproduce Linux locally with the `gcc:13` container line in
    `src/Shenora.Launcher/CMakeLists.txt` rather than round-tripping a release.
  - **§5's verification problem is UNCHANGED, and calling it a library makes it sharper, not softer.** A
    library implies the kit owns its correctness while `dev.mjs verify` compiles none of it. The answer stays
    the sibling's: ship the Node harness that drives a PREBUILT launcher with `--apply-and-exit` over sandbox
    directories, so an adopter's CI builds once and runs THE KIT'S conformance suite against THEIR binary.
    Without that harness this is a promotion in name only, and `README`/`ADOPTION` must still say plainly
    that this repo's gate does not compile it.
  - **Bounded, so nobody over-builds it:** a **self-contained** app needs no launcher at all —
    `UpdateStage.ApplyAsync` already overlays, removes and clears in portable .NET, gate-covered and
    sabotage-verified. This whole capability serves framework-dependent apps, where the runtime may be
    absent and files may be held open. Harvest-driven (D15): build it when an app needs it.

- **D51 — anything the kit SHIPS AS BYTES must be MIT-compatible. The kit never redistributes a copyleft
  binary; an app that wants one supplies it through a `ResourcePack`.** (owner, 2026-08-06:
  *"we are on MIT so we should build one compatible with MIT"*.)
  - **The asymmetry that forced this.** The first media engine came from a CLOSED-SOURCE app, where an
    LGPL ffmpeg is perfectly fine: dynamically link it, attribute it, keep relinking possible, done. Shenora
    is **MIT** and is consumed as a package. Shipping the same binary from here does not "infect" any MIT
    source — LGPL does not work that way — but it does make the KIT the redistributor, and it hands the
    attribution and relinking duties to every consumer of that package, and to anyone who redistributes
    theirs. A package whose licence expression reads `MIT` while its payload is LGPL is a surprise that an
    adopter's own compliance review finds later, at the worst time. **The obligation belongs where the
    choice is made.**
  - **The rule.** Bytes the kit ships (a NuGet payload, a build output, a vendored source tree) must be
    MIT / BSD / Apache-2.0 / ISC / public-domain. **GPL never** — `--enable-gpl`, x264 and x265 relicense
    the consuming APP, which is the one outcome a devkit must never cause. **LGPL binaries not from a kit
    package** either; the licence is fine, the redistribution is what is wrong here.
  - **What an engine should therefore be, in order of preference:**
    1. **The PLATFORM's own codecs** — `MediaCodec` on Android, VideoToolbox/AVFoundation on iOS, Media
       Foundation on Windows. Zero bytes, zero licence weight, and it is the OS's patent problem, not ours.
       ⚠ Measured limit, carried from the source app: AOSP's set is narrow (`aac flac mp3 opus pcm vorbis`
       plus an AAC encoder) — *barely wider than the WebView's own* — so a platform-only engine has a small
       benefit window on Android and must be honest about it rather than presented as complete.
    2. **Permissively-licensed libraries** where the platform genuinely lacks something: openh264
       (BSD-2), dav1d (BSD-2), libvpx (BSD-3), Opus (BSD-3), libFLAC (BSD-3 — the FLAC *tools* are GPL,
       the library is not), Apple's ALAC reference (Apache-2.0).
    3. **Never**: x264/x265 (GPL), fdk-aac (Fraunhofer's own terms, not OSI-free), LAME (LGPL).
  - ⚠ **PATENTS ARE NOT COPYRIGHT, and a permissive licence does not settle them.** openh264 being BSD does
    not grant H.264 patent rights; Cisco's royalty coverage attaches to *their* prebuilt binaries fetched at
    runtime, not to one built from source. So "MIT-compatible" answers the licence question and leaves the
    patent question open — it is the owner's call per shipped codec, and this entry is not legal advice.
  - **This is why `ResourcePack` exists and is the good outcome, not a workaround.** An app that wants LGPL
    ffmpeg still gets it: it supplies its own archive, the kit stages it, and the duty stays with the app —
    exactly where the source app had already put it by gitignoring its binaries and tracking the build
    script instead. The kit owns the mechanism; the app owns the bytes and their licence (D42).
  - **AMENDED 2026-08-06 (same day), because the question behind it was asked with a wrong premise and the
    premise matters more than the answer.** Asked: *"we should be making a MIT if possible, so when sonora
    reference it can still stay close source?"* — **a closed-source app is NOT at risk from LGPL, and that
    is the entire difference between LGPL and GPL.** A proprietary app may link an LGPL library and stay
    proprietary, provided it attributes it and preserves the user's ability to RELINK. So neither the source
    app's current ffmpeg nor an LGPL dependency of any consumer forces anything open, and no decision should
    ever be made in the belief that it does. **GPL is the one that would** (x264/x265, `--enable-gpl`), and
    it stays banned for exactly that reason.
  - **The real reasons to prefer permissive are narrower and worth keeping straight:**
    1. **iOS static linking.** The relinking condition is cheap with a shared `.so` on Android and awkward
       on iOS, where the engine effectively links into the app binary — satisfying it means shipping object
       files or an equivalent relink path, every release. A permissive licence removes the condition, not
       just the paperwork.
    2. **Per-release compliance work** — attribution and a source-or-written-offer for the LGPL parts, kept
       accurate on every build, buying nothing when a BSD/Apache component would have done the job.
    3. **For the KIT it is not a preference at all but a requirement**, because the kit REDISTRIBUTES;
       that is the body of this decision and it is unchanged.
  - ⚠ **And the thing no design can engineer around: ffmpeg cannot be relicensed by us.** "An MIT engine"
    therefore means *not ffmpeg* — the platform's codecs first, permissive libraries where the platform
    genuinely falls short. Anyone proposing "just ship a small ffmpeg" has proposed an LGPL redistribution
    with extra steps.
  - **Shenora being MIT imposes nothing on any consumer.** A closed-source app references it and stays
    closed; that is what MIT is for, and it is why the kit's own licence is not the thing under discussion.
  - **AMENDED again 2026-08-06 — ffmpeg has THREE licence states, not two, and the third is the trap.**
    Asked: *"so ffmpeg can either go GPL or LGPL?"* Nearly — the licence is not chosen, it is DETERMINED by
    what is compiled in, and it follows the most restrictive component:
    | build | licence | distributable |
    |---|---|---|
    | default | LGPL 2.1+ | yes — a closed-source app may link it |
    | `--enable-gpl` | GPL 2+ | yes, but it **relicenses the consuming app** (x264, x265, libxvid, libpostproc, some filters) |
    | `--enable-nonfree` | non-free | **NO — may be built and used, may never be distributed** |
    (`--enable-version3` additionally bumps affected components to the v3 variants.)
    ⚠ **`--enable-nonfree` is the one to watch**, because it is what someone reaches for to get **fdk-aac**
    for better AAC, and the result works perfectly, passes every functional gate, and cannot legally be
    shipped — nothing in the build output says so. A licence failure that no test can see is exactly the
    class this decision exists to stop. Guard BOTH flags in any build script, as the source app's already does.
    **None of the three changes the kit's answer:** LGPL and GPL are both non-MIT, so none may ship from
    Shenora. Which one an APP builds is the app's call, and the sane one is the default LGPL build.
  - **AMENDED 2026-08-06 — the operational test is DISTRIBUTION, not "is it in the code base".** Asked:
    *"if the ffmpeg is not in the code base then everything is okay?"* Right conclusion for the kit, but the
    test has to be applied to what is PUBLISHED, because the obligation attaches to conveying bytes. Three
    ways it leaks without ever being in the repo, and all three are worth checking before a release:
    a NuGet package that **fetches and embeds** it during build or install (repo clean, artifact not); a
    **sample or test fixture** that vendors a binary for convenience; **release assets** attached to a tag.
    Keep those clear and the kit owes nothing and its `MIT` expression is honest.
  - **What is entirely free, so this does not get over-applied:** calling ffmpeg as a separate PROCESS,
    defining an interface an app implements with it, documenting the wiring, and staging an app-supplied
    archive through `ResourcePack`. None of that is distribution, and there is no copyleft-by-association —
    an interface written specifically for an LGPL implementation is fine when no implementation ships.
  - ⚠ **And it changes nothing for the APP.** An app shipping ffmpeg inside its own binary still owes
    attribution, licence text and the relink provision. "Not in the kit" makes the KIT clean and does
    nothing for the consumer — the only case where the app owes nothing too is when it ships no ffmpeg
    either (the user installs it, or the platform already has it).

- **D52 — `Shenora.Media` is a TRANSLATION LAYER FOR THE WEB, not a media toolkit. It does the MINIMUM
  transformation that makes a file playable in a webview, and never more.** (owner, 2026-08-06: *"the issue
  is not I want to recreate ffmpeg because it's capable of any kind of video/audio type and adjust them,
  what I'm building is a translation layer for web"*.)

  ⚠ **SHARPENED BY D59 (2026-08-07) — read that first.** "A translation layer for the web" was still loose
  enough to be read as *make more formats play*, which is a treadmill. D59 states the target as a DELTA
  between two things the code already measures: what the **device** can decode (`IMediaCapability`) versus
  what **its webview** accepts (`MediaPlaybackPolicy`). Also note `Shenora.Media` is a NAMESPACE now, not a
  package (D53), and D58 made this layer the player's output pipe rather than a parallel feature.
  - **The scope test, and it is narrow on purpose:** *would a normal file the user already has fail to play,
    and is this the least we can do about it?* Not "can we convert anything to anything" — that is ffmpeg's
    job and it is explicitly not ours (D42, D51).
  - **The four moves were already the planner's enum before this was written down**, which is the strongest
    evidence the shape is right: `Direct` (serve it) → `Remux` (right codecs, wrong box) → `Transcode`
    (one stream, usually the AUDIO) → `Unsupported` (say so honestly).
  - 🔴 **What actually breaks for ordinary video, which is NOT what it looks like from the outside.** The
    video stream is nearly always H.264 or HEVC and hardware decodes both. The two real failures are the
    **container** (`.mkv`/`.avi` holding perfectly playable H.264) and the **soundtrack** (`AC-3`, `E-AC-3`,
    `DTS` — routine in MKV, playable in no browser). So the common repair touches the PICTURE not at all.
    That is why a remuxer is worth writing in managed code and a codec library is not.
  - **Engine tiers, in order (D51 decides the licence, this decides the reach):**
    1. **remux** — container rewrite, no decode, no patents, pure managed code.
    2. **platform codecs** for transcode — `MediaCodec`, VideoToolbox, Media Foundation. They ENCODE as well
       as decode, which is the part that is easy to miss. ⚠ And note what this means: an LGPL ffmpeg has NO
       H.264 encoder either (libx264 is GPL), so the platform encoder was always the only licence-clean
       option — dropping ffmpeg costs nothing on the encode side.
    3. **permissive managed decoders** where a platform genuinely lacks one (ALAC is Apache-2.0; WavPack and
       Theora references are BSD; DSD→PCM is a filter). ⚠ `WMA` and `APE` are the awkward ones — LGPL source
       and a non-OSI licence respectively — and are deliberately unanswered rather than assumed.
    4. **software video decoders** (MPEG-2, VC-1, Xvid, ProRes) — a per-codec project each, built ONLY for
       codecs a real library is shown to contain. A decoder nobody needs is waste.
  - **Reach is bounded by what can be TESTED** (owner, same day): the Android emulator, the Mac simulator,
    and an iPhone 17 Pro. Scope widens when something real needs it, not in anticipation.
  - ⚠ **Reshaping the package is sanctioned but is NOT a package split.** The clusters are probe → plan →
    serve → transform; the first three exist and the fourth is the gap. None of it is real WEIGHT, and
    weight is D48's bar for a new package — so this is folders and an ARCHITECTURE narrative, nothing more.
  - **Deliberately NOT done: an `IMediaProbe` seam.** `MediaPlaybackPlanner.Plan` already takes a
    `MediaProbeResult` the app supplies, so `MatroskaProbe` is a helper an app may or may not call. An
    interface would be shipping flexibility nobody asked for.
  - **AMENDED 2026-08-06 — the kit ships a DEFAULT that works; the seam is the escape hatch, not the only
    door.** (owner: *"we still support for consumer use their own decoder/encoder just if they needed, and
    we built something that can work by default, main job is to make web play support"*.) This narrows D42
    rather than reversing it: D42's objection was **vendoring** — tens of megabytes every consumer pays for,
    and a licence every consumer inherits (D51). A default assembled from a managed REMUX (no decoding at
    all) and the PLATFORM's own codecs costs zero bytes and zero obligations, so it contradicts neither.
    ⚠ Consequence for the code: "the kit ships no implementation and never will" was written on
    `ISegmentEngine` earlier the same day and is now WRONG — corrected there. An app implements the seam
    when it needs reach the default lacks, not to get off the ground.
  - **And the sentence that explains why media is in scope at all** (owner, same message): *"the entire
    framework is about to make react+c# application development support"*. A React+C# app whose user's
    video will not play is a BROKEN APP, and repairing that is shell work — the same category as serving a
    local file or honouring a safe-area inset. It is emphatically not a licence to grow media features that
    do not end in "…and now the page can play it". **The test for anything proposed here: does a React+C#
    app fail without it?**
  - **The worked example, and the clearest one-line statement of this whole decision** (owner, 2026-08-06):
    *"we're not remaking ffmpeg, neither any complex encoder/decoder — our goal is to support web playback;
    it's like if H.265 is not supported on the web we translate it."* **H.265 is the flagship case precisely
    because it needs no software codec anywhere:** the device already decodes HEVC in hardware and already
    encodes H.264 in hardware, so the translation is two platform calls and a container. Zero bytes, zero
    licence, no codec written. If a proposal here cannot be described that way — as a translation between
    what the user HAS and what the web ACCEPTS, using what the device already does — it is out of scope.

- **D53 — `Shenora.Media` is folded back into `Shenora.Core`. Media repair is SHELL WORK, not an optional
  feature, and the package's own justification had become false.** (Owner, 2026-08-07, on being shown the
  weight numbers: *"since we dont really have any binary? and its mostly just our code should we just
  remove Media package merge to core"*, then *"98 kb on app still small?"* — both right.)
  - **What changed on the ground.** D40 created the package because media "is not going to be small": a
    demuxer or image codec is *real shipped bytes*. **D51 then guaranteed the kit ships no engine byte,
    ever** — not ffmpeg, not a vendored codec, nothing. So the premise the package rested on can never
    come true. What exists is managed code the kit wrote itself.
  - **The numbers, because "it is small" deserved measuring rather than asserting** (Release IL):
    `Shenora.Core` 125 KB · `Shenora.Media` 98 KB · `Shenora.IO` 82 KB · `Shenora.IO.Compression` 24 KB.
    Merging costs Core +98 KB — and iOS mandates trimming (`PublishTrimmed=true`), so an app that never
    calls a media type does not carry it either way. ⚠ **The size argument was the weak one and it was
    argued first; the owner was right to push back on it.**
  - 🔴 **AMENDED 2026-08-07, same day — the OWNER'S actual reason, which is better than the one first
    recorded here and is about IDENTITY rather than layering.** (*"my previous description for the goal of
    this library is not that clear, thats why I removed the entire media library and move[d] into core,
    because we are not making a video convertor library we are making a hybrid app development
    framework."*) A separate `Shenora.Media` package **advertised the wrong thing**: it made the kit look
    like it ships a media library with a hybrid shell attached, when it is a hybrid app framework in which
    media is one capability among windows, dialogs, IPC and the rest. Package boundaries are a public
    statement about what a thing IS, and that one was making a claim nobody meant.
    ⚠ Worth keeping because it generalises past media: the weight numbers and the shell-work test below
    are both true and both were downstream of this. **When a package boundary has to be justified by an
    argument, check first whether it is saying something about the product you did not intend to say.**
  - **The argument that actually decides it is D52's own framing:** *"A React+C# app whose user's video
    will not play is a BROKEN APP, and repairing that is shell work — the same category as serving a local
    file or honouring a safe-area inset."* Serving a local file is `IWebViewInterceptor` + `WebViewFiles`,
    in **Core**. Media repair belongs beside the thing it is the same category as.
  - 🔴 **This SHARPENS D48 rather than contradicting it.** D48's bar was read as *weight*; weight alone
    would have merged `Shenora.IO` too (also pure managed, also no binary). The line that survives both
    decisions is about the CONSUMER, which is what D37 and D48 were really testing all along:
    | | |
    |---|---|
    | **shell work** — making the page host, serve and play what it was handed | **Core** (interception, safe area, media) |
    | **something only SOME apps do** | its own package (`IO` = my app mutates a file tree; `IO.Compression` = …from archives) |
    Every app that hosts a page can be handed a file it cannot play. Not every app mutates a file tree.
  - **A documented BREAK, and a cheap one (D47).** The namespace stays `Shenora.Media`, so an adopter
    changes a `PackageReference` and **not one line of code**. Proof it was a pure move: the API baselines
    went `Shenora.Media.txt` −180 lines (deleted) and `Shenora.Core.txt` **+180, −0**.
  - ⚠ **`Shenora.Media` is deliberately NOT in `devtools/retired-names.txt`.** The package id is retired
    while the NAMESPACE is current, and that gate matches names — it cannot tell the two apart, so
    registering it would fire on every correct sentence in the repo. This bullet is the record instead.
  - **It also dissolves a design problem that had made slice 4 unbuildable.** `ISegmentEngine` sat in a
    portable `net10.0` package that cannot call `MediaCodec` or AudioToolbox, and no shell references an
    optional feature package — so a default engine using platform codecs had nowhere to live short of
    resurrecting the `Shenora.Media.{Platform}` family D45 deleted. With the contracts in Core, which
    every shell already references, a per-platform engine is an ordinary shell capability.
  - **What is NOT claimed:** that fewer packages is better in general. `Shenora.IO` stays split, and the
    next feature is judged on the same question — *is this shell work, or is it something only some apps
    do?* — rather than on package count.

- **D54 — the goal is a WEB PLAYER AS GOOD AS A NATIVE ONE, and the way there is a playback LIFECYCLE in
  .NET — not a bigger translation layer.** (Owner, 2026-08-07: *"our goal is to make web player as good as
  a regular player, which is th[e] a[r]chitect[ure] lack of, and if the consumer want to build a proper
  player, we can support that with proper life cycles of media play in .net code (which is more capable
  than js for this kind [of] work)"*.)
  - **What this REPLACES as the plan.** The media work had been drifting toward "make the webview able to
    play more" — translate every container, transcode every soundtrack, and eventually segment for HLS.
    That is a treadmill: each format the `<video>` element refuses becomes another thing the kit converts,
    and the ceiling is still whatever the webview can do. **The ceiling is the problem, not the formats.**
  - 🔴 **The architectural gap, stated plainly: the PAGE owns playback and it should not.** Today an app
    puts a `<video src>` in its React tree and the host only serves bytes. Everything a real player does
    beyond that is either impossible or awkward, and today's device work measured three of them:
    - **Background playback.** iOS PAUSES a `<video>` the moment the app leaves the foreground — the video
      track cannot render. A native player is not subject to that.
    - **The system surfaces.** `IPlaybackSession` already publishes Now Playing, but it is publishing
      ABOUT something the page is doing, so the two can disagree and nothing reconciles them.
    - **Formats.** Anything the webview cannot decode needs a whole translation layer to work around a
      decoder the platform already has.
  - **So the seam to ship is a PLAYER lifecycle the host owns and the page drives** — load, play, pause,
    seek, rate, position, ended, error — implemented per platform (AVPlayer, ExoPlayer/MediaPlayer, Media
    Foundation) and reported back over the existing IPC. The page stays the UI, which is what React is
    good at; .NET does the playing, which is what it is good at. That is the same division the kit already
    makes everywhere else — the page asks, the shell does the platform work.
  - ⚠ **This does NOT delete the translation layer, it BOUNDS it.** `Mp4Remuxer` and the conversion
    pipeline stay: an app serving files to a `<video>` is still a legitimate and common shape, and
    container repair costs nothing. What changes is that the translation layer stops being the answer to
    *"the webview cannot play this"* — the answer to that is a player that can.
  - ⚠ **And it bounds the segmentation work specifically.** HLS segmentation existed to feed a webview a
    format it would accept, piece by piece. A native player opens the file directly, so the kit does not
    need a segmenter to reach the same outcome. `ISegmentEngine` stays as the seam for an app that wants
    progressive streaming; the kit does not ship a default segmenter, and the five traps a sibling paid
    for stay recorded against that seam rather than being designed around.
  - **The scope test is unchanged and this passes it hard** (D52): *does a React+C# app fail without it?*
    A media app on iOS cannot play in the background at all today, which is the failure users notice most.
  - **NOT ffmpeg, and not for licence reasons alone** (owner, same message): *"its too big, and its too
    much for what we want to achieve"*. Every platform already ships a capable player; the work is exposing
    its lifecycle portably, not shipping a second implementation of one (D51 also forbids the bytes).
  - 🔴 **AND THE GENERAL RULE THIS IS AN INSTANCE OF** (owner, same day): *"think we are building a cross
    platform application framework mainly in .net + react"*. That is the lens for every capability
    question, and it settles them faster than arguing each on its own merits:

    | | |
    |---|---|
    | **.NET does** | the platform work — lifecycles, OS surfaces, files, codecs, background execution, anything needing a real thread or a real handle |
    | **React does** | the interface — what it is good at, and the reason an app chooses this stack |
    | **the kit provides** | the SEAM between them: a portable contract, one implementation per shell, and the IPC that carries it |

    Read that way, the player is not a media feature at all — it is the same shape as `IPlaybackSession`,
    `IFileDialogs`, `IWebViewInterceptor` and the safe-area insets, and it belongs for the same reason.
    ⚠ **The test to apply to the NEXT capability, before designing it:** *is this the page trying to do
    something the platform is better at?* If yes, the kit's job is a lifecycle contract, not a workaround —
    the translation layer was the workaround, and this decision is what bounds it.
  - 🔴 **THE THESIS, and it renames what "translation layer" means** (owner, same day): *"this
    differenti[ates] our platform compared to capacitor or electron — native .net capability. And what we
    [are] mostly trying to solve is not a media convertor, its something that .net can do but react
    d[oes]nt. We build that translation layer."*
    - **The layer this kit builds translates .NET CAPABILITY into something a React page can use.** Not
      container A into container B — that was one instance, and naming the media package after it was
      close enough to be misleading. `Shenora.Media`'s job is a special case of the kit's job.
    - **It is also the competitive answer, and worth stating because it decides what is worth building.**
      Capacitor and Electron give you a webview and a JS bridge; the capability ceiling is whatever the web
      platform plus a plugin ecosystem offers. This kit's ceiling is **.NET's** — the whole BCL, real
      threads, real handles, the platform SDKs, background execution — with React on top for the interface.
      Anything a plugin ecosystem already does well is not where the value is.
    - **So the question for any proposed feature is not "is this useful?" but "can React already do this?"**
      If it can, the kit is competing with the web platform and will lose. If it cannot — and .NET can —
      that is exactly the gap this framework exists to close, and the kit owes a lifecycle contract plus one
      implementation per shell.

- **D55 — the `Shenora.IO` family folds into `Shenora.Core` too. There is no "optional features" tier
  any more: the framework ships as ONE whole.** (Owner, 2026-08-07, immediately after D53's amendment:
  *"its the same thing for Compression and IO, we can have different projects for clearer namespacing and
  easier for testing, but the final framework is a whole, what we should support is bridge the both, react
  and .net and support for other consumer to implement things in .net to complete their goal."*)
  - **This is D53's identity argument applied where D53 explicitly declined to apply it.** D53 kept
    `Shenora.IO` split on the test *"is this shell work, or something only SOME apps do?"* — and that test
    is a fine LAYERING test but it was answering the wrong question. The question is what the package set
    tells a stranger the product IS. A nuget.org listing of `Shenora.Media` + `Shenora.IO` +
    `Shenora.IO.Compression` reads as a collection of single-domain libraries; the product is a hybrid app
    framework. **D53's closing line — "the next feature is judged on the same question" — is hereby
    replaced**: it is judged on whether the framework is one whole, and the answer so far is always yes.
  - **The mechanism was forced, not chosen, and this is the part worth keeping.** "Different projects, one
    shipped package" was tried first and is structurally impossible here: D48 established (by checking,
    not assuming) that the edge runs `IO → Core`, because every type in the engine logs through Core's
    `AppCallback`. For `Shenora.Core.nupkg` to carry `Shenora.IO.dll`, Core's csproj must reference IO,
    which already references Core — a cycle. ⚠ **A dependency edge decides whether a "keep the projects,
    merge the package" plan is even available.** Check the direction before promising it.
  - What the owner asked for survives anyway: `src/Shenora.Core/Io/` and `Io/Compression/` are folders with
    the namespaces `Shenora.IO` / `Shenora.IO.Compression` unchanged, which is what `Media/` already does.
    Tests and probes reference `Shenora.Core` and compile untouched.
  - **A documented BREAK, and the same cheap one as D53 (D47):** an adopter deletes two
    `PackageReference` lines and changes **no code**.
  - **Proven a pure move by the same measurement D53 used**, because "nothing changed but the location"
    deserves checking rather than asserting: `Shenora.Core.txt` went **+243 / −0**, the two deleted
    baselines were 206 + 37 = 243 lines, and every added line was verified to come from them (`comm -23`
    against their union was empty). Nothing was invented, renamed or dropped in the move.
  - ⚠ **The count in this file's header block is the thing that goes stale** — it has now done so twice.
    `node devtools/dev.mjs doctor` prints the real number; `doc-drift` failed this change until the table
    matched, which is the only reason it is right.

- **D56 — the deploy/update TOOLING is product, not devtools. Under D54's framing it is one of the most
  load-bearing parts of the kit, and it is currently the least finished.** (Owner, 2026-08-07, on being
  shown the D55 package set: *"from this scope, the launcher, platform testing/deployment tools kind
  become more needed"*.)
  - **The competitive read that makes this obvious.** D54 says the differentiator is native .NET
    capability, which is true about the RUNTIME — but it is not the whole of what Capacitor and Electron
    actually sell. Capacitor's moat is `npx cap sync` / `cap run ios`: the thing that takes a web app and
    puts it on a phone. Electron's is `electron-builder` + the auto-updater + signing. **An adopter meets
    the tooling before they meet the runtime**, and a framework whose deploy story is "read our docs and
    write your own MSBuild" loses to one where a single command installs on a device — regardless of which
    has the better capability ceiling.
  - **It passes D54's own test cleanly, which is why it belongs.** *"Can React already do this?"* No: a
    React toolchain cannot mint a provisioning profile, sign an `.appex`, install to a connected iPhone, or
    apply a staged update over files the OS is holding open. .NET and the platform SDKs can. That is the
    same gap `IMediaPlayer` sits in, and it is *wider* — every adopter hits deployment, while only some
    hit media.
  - **What this reclassifies, concretely.** These exist and work; what changes is that they are no longer
    scratch:
    | Today | Under this decision |
    |---|---|
    | `devtools/dev.mjs mac device \| provision \| device-log \| appex-check`, `android` — written this session to get the KIT onto a phone | the `cap run ios` equivalent, owed to adopters |
    | `Shenora.Launcher` — framed as a niche extra for framework-dependent self-updaters | the app-lifecycle piece Electron ships as a core feature |
    | `devtools/ios-provision/` — a kit-owned stub `.xcodeproj` | the answer to "how do I sign without hand-rolling Xcode" |
  - ⚠ **This is a scope claim, not a finished design.** The hard part is not the commands, it is that they
    currently assume THIS repo's layout (`devtools/project.config.mjs`, a reachable Mac, paths under
    `local/`). Shipping them means deciding what an adopter's equivalent is — an MSBuild target set, a
    `dotnet` tool, or a documented recipe — and that decision is not made here. What IS decided: **the
    tooling is in scope as product and is judged by the same bar as the runtime surface**, rather than
    living forever in `devtools/` as the kit's private convenience.
  - 🔴 **It also raises the stakes on the known-broken Live Activity devkit** (`CHANGELOG.md`
    `### Known broken`). A devkit whose stated adoption does not hold on a device is a tooling defect, and
    tooling defects are now product defects.

- **D57 — the dated design docs are RETIRED, all five. A design doc is scaffolding: once the thing is
  built, `ARCHITECTURE.md` says what it is and this file says why, and the third copy is the one that goes
  stale.** (2026-08-07, applying the 0.2.0 cleanup's own precedent to the docs that outlived it.)
  - **What triggered the audit rather than the calendar:** `docs/README.md` claimed the 2026-07-30 design
    contract was load-bearing because *"code cites its `§5`"*. **Zero source files cite it** — the claim was
    written when it was true and nothing re-checked it. That is `doc-claims.md`'s exact defect class, in
    the router, about the doc the router calls the design contract.
  - **Retired:** `2026-07-30-shenora-design.md` (its package set and "desktop body for the family's Windows
    applications" framing are both superseded — D54/D55, and `CLAUDE.md` now carries the identity),
    `2026-08-01-shenora-communication-core-design.md` (→ **D23**), `2026-08-02-shenora-app-update-design.md`
    (→ **D30**/**D31**/**D50**), `2026-08-02-shenora-mission-scheduling-design.md` (→ **D27**–**D31**),
    `2026-08-02-shenora-mobile-offline-plan.md` (an assessment, not a queue). Six code citations were
    repointed at D-entries first; git holds the documents.
  - 🔴 **What ONLY they held, preserved here because it is invariant rather than narrative:**
    - **A mission policy is consulted only about LEGAL moves, and that is what makes it safe to expose.**
      By the time `IMissionPolicy.Compare`/`ShouldStart` sees an item it has already passed admission —
      claims free, lane permits available, fairness satisfied. So a policy chooses among legal moves: it
      cannot make conflicting work run concurrently, cannot bypass a lane, cannot reorder work that
      conflicts with something earlier. **The worst a buggy policy can do is DELAY work; it can never
      corrupt it.** A throwing policy is caught and read as "not now" rather than wedging the scheduler.
      ⚠ Consequence: a policy deferring on an EXTERNAL condition (clock, load, battery) needs
      `Reevaluate()`, because dispatch is event-driven and **the kit deliberately owns no timer** —
      polling belongs to whoever knows what is being polled.
    - **Why app updates are two phases, in one sentence:** a running process cannot replace its own
      executable on Windows, so the app downloads and verifies while alive and something that runs
      *before* it applies the result. Two siblings built this independently and arrived at the same
      three-file launcher, the same `.update/staged` + `ready.json` contract and the same
      `{path, size, sha256}` manifest triple — D15's two-consumer bar met on evidence, not direction.
    - **The offline-mobile blocker is on the ADOPTER's side, not the kit's:** transport coupling in the
      app, not anything missing from the shell. Nothing to build here until an app is actually decoupled.

- **D58 — the interceptor's media route is the PLAYER's output pipe, not a parallel feature. There is one
  media-play layer in .NET and the webview is one of its surfaces.** (Owner, 2026-08-07: *"everything from
  the interceptor for media, is actually saying we going to .net right?"* — yes, and *"the .net one is a
  proper player but using web as its display and sound"*.)
  - **What was wrong before this.** `Media/Serve/` handed bytes to an element the PAGE drove, and
    `Media/Play/` was a native player. They shared a namespace and nothing else, and the split showed in the
    surface: `MediaConversionOptions` said in its own remarks that *"whether a source needs converting is
    the APP's decision, made before it builds the URL"*. Every adopter therefore wired probe → plan → URL
    by hand, and got a different answer.
  - **The join is `MediaPlayer`**, which owns exactly that chain and hands the result to an
    `IMediaRenderTarget`. So a media request arriving at the interceptor is a question **.NET** answers —
    the file as-is, a remux, a transcode, a segment window — and the page never decides anything about
    format. It renders what it is handed.
  - 🔴 **This is what makes a consumer's own converter reusable, which was the owner's second requirement.**
    The URL the player resolves points at the conversion route, so the pipeline an app already extends
    (`MediaConversionOptions.Convert`, `IMediaAudioConversion`, `IMediaContainerWriter`) serves the player
    too. **Nobody writes a second converter to get a player.** That is the D53/D55 "one whole" argument
    applied inside a subsystem rather than across the package set.
  - **Named `MediaPlayer`, NOT `WebMediaPlayer`** (owner: *"you can just call it MediaPlayer, since the
    hybrid is our feature"*). A `Web` prefix frames rendering-through-the-page as a variant of some purer
    thing; in a hybrid framework it is the NORMAL case, and the native player is the special one. ⚠ The
    lexicon gate agreed by accident and it is worth noting: `Web` was the only questionable word in the
    original name, and removing it made the gate pass without an edit.
  - 🔴 **CORRECTED WITHIN THE HOUR — the first draft had an `IMediaRenderTarget` interface and it was
    over-engineering.** (Owner: *"IMediaRenderTarget? isn't that should be just the web? I think we have bit
    over engineered this."* Right.) It had exactly ONE production implementation — the page element — plus
    a test fake, which is the seam **D52 already refused to build** for probing. Worse, `IMediaPlayer` is
    itself the seam that separates the web player from the native one, so this was a second seam underneath
    the first. Deleted; `MediaPlayer` now talks to the page over **`IEventBus`**, the channel the conversion
    route already uses, and the page answers by calling `MediaPlayer.Report` from its IPC route.
    ⚠ **The generalisable tell: an interface whose only implementations are the real one and a test
    double.** A test fake is not a second consumer — it is the *cost* of the abstraction, not evidence for
    it. Ask what the second REAL implementation is; if the answer is hypothetical, use the concrete type.
  - **The page is the only clock.** Position and duration come from `Report` and nowhere else, because the
    element is the thing actually advancing — anything the player computed itself would be a second, worse
    one. ⚠ Report on TRANSITIONS: `timeupdate` fires ~4×/second and forwarding it costs battery to tell the
    host something it can extrapolate.
  - ⚠ **What is NOT built:** the page-side driver. Nothing in the kit yet subscribes to
    `MediaPlayerEvents` and drives an element, so an app writes that half itself. It belongs in
    `@shenora/react`, where the element lives.

- **D59 — the converter's job, stated exactly: it bridges what the DEVICE's hardware can decode to what
  that device's WEBVIEW will accept. Nothing wider.** (Owner, 2026-08-07: *"what we solve here is the
  streaming and the connection, and the default convertor is actually bridging the gap between the device
  hardware to its webview, and if a better encoder/decoder comes in by adopter app, they can hook that into
  the same pipeline without additional code."*)
  - **This is sharper than D52's framing and supersedes how it was being read.** "Make a file the webview
    cannot play, play" invited a treadmill of formats. The real target is a DELTA between two measurable
    things, and both are already in the code: `IMediaCapability` asks the device what it decodes, and
    `MediaPlaybackPolicy` says what the element accepts. **The converter's whole job is the gap between
    them.** Where the device cannot decode it either, there is nothing to bridge and refusing is correct —
    which is also why the kit ships no engine (D51): an engine would be claiming to beat the hardware.
  - 🔴 **The claim was FALSE when it was made, and the defect was invisible.** `Mp4Remuxer.ConvertAsync` —
    the overload every adoption example wires, documented as "the kit's default" — passed
    `conversion: null`. So a shell that had registered a perfectly good `IMediaAudioConversion` (Android's
    `MediaCodec` one ships in the box) never had it called. **Nothing failed:** the remux succeeded and
    dropped the soundtrack, so the symptom was a film that played SILENTLY. Fixed by
    `Mp4Remuxer.ConvertWith(conversion)`, with `ConvertAsync` kept as container-repair-only and its remarks
    now saying so.
    ⚠ **Worth the entry for the failure MODE, not the fix.** A capability that is absent rather than broken
    produces no error, no log line and no failing test — the gates were green throughout. When a feature is
    "supplied by the app and consulted by the kit", the test that matters is *does the kit actually call
    it?*, and that test did not exist.
  - **"Without additional code" is a claim about the PIPELINE, and it holds.** What gets consulted is
    `MediaAudioConversion` — a middleware chain, last-registered-first — not any one implementation. An
    adopter with a better decoder calls `Use(...)` and it serves the default converter, the segment engine
    and the player alike. Pinned by `ConvertWith_accepts_a_pipeline_so_a_registered_converter_is_consulted`.

- **D60 — the kit ships NO page-diagnostic facade. The two-consumer signal is real and the generalisation
  is still not worth making.** (2026-08-05, closing the open question in `TASKS.md`.) The pattern stays
  documented in `docs/ADOPTION.md`; `PageDiagFacade` stays sample-local in `samples/Shenora.Sample.Maui/`.
  ⚠ **Renumbered from D51 on 2026-08-07 — it was a DUPLICATE.** Two entries were written as D51 on
  consecutive days and the collision survived four sessions. Every one of the 12+ citations in code and
  docs means the other one (MIT-compatible bytes), so that keeps the number and this moved. See the header
  block's numbering rule, added the same day.

  - **The signal that made this a question.** Two repos independently built the same tiny facade for the
    same measured reason: **WebKit does not forward a page's `console.*` to the unified log** (checked on
    the simulator with a tagged line and zero hits), and a screenshot cannot report a number, a header or an
    array. That is normally the harvest bar (D15).
  - **What fails the bar is the SHAPE, not the count.** D15 promotes something that *proves nice and pays
    to generalise*. This is a `switch` with one case and a log call — and the parts that differ per app are
    the parts that matter: the module name, the log sink, and whether page text is redacted before it is
    written. A kit version would either hard-code those or take three delegates, at which point the adopter
    has written more configuration than the twenty lines it replaces.
  - **⚠ And a kit-shipped version would be a PRIVACY hazard the app cannot see.** It writes page-supplied
    text to the device log — readable by anything with log access on the device. Shipped as kit surface and
    registered by default, that is the kit making a data-handling decision on a consumer's behalf. **This is
    the same reasoning that killed D10's loopback-gate helper**: a generic security-shaped helper is worse
    than shipping nothing, because the consumer stops thinking about it.
  - **It is also a DEVELOPMENT workaround, not a product capability.** It exists because a diagnostic route
    is closed on one platform. Baking a dev-loop workaround into a public, SemVer-frozen surface is exactly
    the "ship the consumer's shape" failure `generic-library.md` warns about — and unlike a real capability,
    it gets *less* useful the moment the platform fixes its logging.
  - **Revisit trigger:** an adopter that cannot express what it needs over the existing IPC pipe. Wanting a
    ready-made twenty lines is not that — the same bar every other deferred capability is held to (D10).
