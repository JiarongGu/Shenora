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
  (User direction, 2026-08-01, on the first adopter's IPC design review.) Three parts, one design:
  `docs/2026-08-01-shenora-communication-core-design.md`, shipping as 0.2.0.
  **(a)** A route receives an `IModuleContext` — `Publish` (module-scoped emit), `Start`/`Run` (tracked
  operations), `Logger` — *in its signature*, because `Shenora.Ipc` had **zero** references to
  `IEventBus` while the kit's own `DropZoneManager` took one as a REQUIRED option. The bus was already
  the spine; the contract did not admit it, so every app re-agreed the conventions by hand. Layering was
  never the obstacle: `Shenora.Ipc` already references `Shenora.Core`, so this adds no package edge.
  **(b)** This **supersedes `docs/2026-07-31-shenora-oneway-ipc-design.md` §6 bullet 1** ("no
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

  **AMENDED again 2026-08-01 (generic-library audit, before publish — free, since 0.2.0 was merged but
  never pushed/published).** The audit asked not "is this correct" but "has the kit absorbed ONE
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
  pre-0.2.0 as unearned surface — see the design doc §4.2) **was reinstated**, because `RequestPause`/
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
