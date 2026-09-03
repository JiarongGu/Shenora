# DECISIONS.md — the load-bearing choices and why

Numbered rationale so a future session doesn't relitigate them. **One entry = the decision, why it was
taken, and the constraint it imposes.** A measurement, an audit or a design essay belongs in the COMMIT
that landed it, not here.

> 🔴 **CORRECT AN ENTRY IN PLACE. Never append a dated note narrating what it used to say.** Replace the
> wrong sentence with the right one and put the WHY in the commit message — `git log -S "<token>" --
> docs/DECISIONS.md` finds it. Appending is what took this file to 3,207 lines with **47 % of its entries
> still stating something untrue**, and it *blinds* `doc-drift`, whose history suppression an amendment
> stack keeps permanently on. `node devtools/dev.mjs doc-shape` enforces the shape;
> `node devtools/dev.mjs decision-audit` re-checks every entry's claims against the tree.

> 🔴 **A NUMBER IS A PERMANENT ADDRESS. Never reuse one, never renumber a cited entry, and check the
> highest number before writing a new one.** Code cites these — `Mp4Remuxer` says `D51`, `UpdateStage`
> says `D50`, `IMissionScheduler` says `D27–D31` — and those are **shipped XML docs on nuget.org**, so a
> renumber silently redirects a published reference. `doc-drift` fails on a duplicate number.
> **A SUPERSEDED entry keeps its number and becomes a one-line tombstone** pointing at what replaced it,
> so a citation always lands somewhere that explains itself.

> ## The package set — here, once
>
> **There are five packable projects, plus 2 npm packages.** Verify with `node devtools/dev.mjs doctor`,
> which prints the real count. ⚠ **That sentence's WORDING is load-bearing** — `doc-drift` reads
> "there are `<n>` packable projects" from this file and fails closed if it is reworded away, because a
> count-check that silently stops finding its subject passes forever while checking nothing.
>
> | | | |
> |---|---|---|
> | **the framework** | `Shenora` | the three cores (IPC · EventBus · RouteInterceptor), the engine layer, and the modules — D65 |
> | **shells** (D37) | `Shenora.Windows` · `Shenora.Android` · `Shenora.iOS` | one per platform; each implements the cores and its modules' platform halves |
> | **native** (D50) | `Shenora.Launcher` | C++ sources + per-RID binaries; NO managed surface |
> | **npm** | `@shenora/react` | what the app imports |
> | **npm** (D67) | `@shenora/cli` | build-time only — the `shenora` binary, a `devDependency`, in NO shipped artifact |
>
> 🔴 **There is no optional-features tier** (D55). A capability that grows big enough to look like a
> library gets a FOLDER, not a package. The question for a new id is *"does the adopter's app carry this
> at RUN TIME?"* — not *"is it separate?"*, which is why `@shenora/cli` is not an exception (D67).
>
> **The LAYER is the namespace** (D65), and these are namespaces, never package ids:
> `Shenora.Core.*` (Events · Ipc · Shell · WebView) · `Shenora.Engine.*` (Files · Missions) ·
> `Shenora.Modules.*` (Media · FileDialog · Platform · Requests · Update · Update.Compression).
> `Shenora.Media`, `Shenora.IO`, `Shenora.IO.Compression` and `Shenora.Ipc` are retired as **both**
> (D53, D55, D65) and are registered in `devtools/retired-names.txt`.
>
> **When an entry below names a package set, read it as the set AT ITS DATE.** `docs/ARCHITECTURE.md` is
> the as-built map and `doc-drift` gates it against the csproj files.

## Every decision, in one place

**GENERATED from the entries — `node devtools/dev.mjs decisions-index`, checked by `verify`.** Never edit
it by hand: it states each entry's own decision line, so **a row that does not read as a decision is an
ENTRY to fix, not a summary to reword.** You usually arrive here knowing a NUMBER — code and shipped XML
docs cite them — so the number is the column to scan.

<!-- decisions-index:start -->

| | |
|---|---|
| **D1** | Shenora is the BODY; Lyntai is the brain; no dependency between them. |
| **D2** | A package boundary must buy a RUNTIME separation, not a seam. |
| **D3** | One .NET VERSION across the kit: .NET 10. |
| **D4** | Lockstep versioning from one `<VersionPrefix>` in `src/Directory.Build.props` |
| **D5** | No push/PR CI; verification is local (`dev.mjs verify`); releases are a single manual `workflow_dispatch`. |
| **D6** | Publishing: NuGet Trusted Publishing (OIDC, no stored API key); npm publish with `--provenance` via OIDC |
| **D7** | ONE test project, `tests/Shenora.Tests`, not per-package projects |
| **D8** | Extraction-first: lift proven sibling code rather than inventing an abstraction. |
| **D9** | Repo organization clones the family system, and there is no archive tier. |
| **D10** | Two consumption profiles; a `Shenora.Hosting.AspNetCore` package was surveyed and is NO-GO. |
| **D11** | The IPC envelope follows the proven family shape |
| **D12** | Sibling names stay out of tracked files. |
| **D13** | Headless: no UI component library dependency, ever. |
| **D14** | The auxiliary browser subsystem is in scope, and it ships DESKTOP-ONLY. |
| **D15** | Growth is harvest-driven. |
| **D16** | Mobile shells are a target, and the IPC envelope is transport-neutral so they cost no contract change. |
| **D17** | `Shenora` depends on the Microsoft DI IMPLEMENTATION package, not only the abstractions. |
| **D18** | The library is Shenora (神阙); git history restarted at the rename. |
| **D19** | Windows primitives and web hosting are ONE layer, and the direction is `WebView/` → `Shell/`, never the reverse. |
| **D20** | Portable contracts live in `Shenora`; only Windows implementations live in the Windows shell. |
| **D21** | For a whole application FEATURE, the kit ships primitives + lifecycle hooks; the app owns the product. |
| **D22** | Name every public type for its MECHANISM, never for a scenario, product or business need. |
| **D23** | The module contract carries the EVENT path, and the kit tracks long-running requests. |
| **D24** | Frameless chrome is a FIXED WinForms type, not an attachable behaviour. |
| **D25** | Frameless chrome and native drop zones are the kit's FLAGSHIP pair: settled, and not to be redesigned without adopter evidence. |
| **D26** | the kit's DESKTOP scope is Windows only, and Linux is served by the SERVER-BACKED profile rather than by a native Linux shell. |
| **D27** | the scheduler's unit is a MISSION, and a definition is not an execution. |
| **D28** | the queue's storage is named for what it is, and the queue itself stays internal. |
| **D29** | a chain is ONE queue entry, not N with dependency edges. |
| **D30** | filesystem MUTATIONS are a separate component from mission scheduling. |
| **D31** | cross-process file access is TWO problems, and one mechanism cannot serve both. |
| **D32** | a second shell is a PEER, and the kit's job is the substrate under both. |
| **D33** | an ABSENT capability throws and names the platform; a SATISFIED one is an honest no-op. |
| **D34** | a shipped assembly the test project cannot REFERENCE is gated from its IL metadata. |
| **D35** | "open a folder" is a DESKTOP concept, and the portable answer is to decompose it into the intents behind it. |
| **D36** | the HOST advertises what it can do, in the handshake; the client never sniffs the platform. |
| **D37** | ONE shell package per PLATFORM, named for the platform. |
| **D38** | an off-screen session gets the app's own BUNDLE, and deliberately not its custom SCHEMES. |
| **D39** | the auxiliary-SESSION stack stays a DESKTOP capability. |
| **D40 · D41** | media as its own package family: RETIRED, both bodies deleted rather than banner-stacked. |
| **D42** | for an APP that needs total format coverage, an ENGINE is the primary playback path on every platform, including mobile — and the kit ships none. |
| **D43** | the media contracts split by DEPENDENCY, not by feature name. |
| **D44** | the media URL names NO origin, and the two mobile shells need OPPOSITE response BODIES. |
| **D45** | resource interception is a MIDDLEWARE PIPELINE in `Shenora`, implemented by each SHELL. |
| **D46** | a capability that needs a newer PLATFORM TARGET is opt-in, never imposed. |
| **D47** | while ONE repo fully adopts the surface, prefer the CORRECT shape over the compatible one. |
| **D48** | the file-operation engine is its own LAYER hanging off Core, not part of it. |
| **D49** | retired package ids stay LISTED until 1.0; pre-1.0 ids are retired in ONE deliberate pass. |
| **D50** | the native launcher is a LIBRARY plus a template, written in C++, one binary per platform. |
| **D51** | anything the kit SHIPS AS BYTES must be MIT-compatible; an app that wants a copyleft binary supplies it through a `ResourcePack`. |
| **D52** | the media layer is a TRANSLATION LAYER FOR THE WEB, not a media toolkit: the MINIMUM transformation that makes a file playable in a webview, and never more. |
| **D53** | the media package is folded back into `Shenora`. |
| **D54** | THE THESIS: the differentiator against Capacitor and Electron is NATIVE .NET CAPABILITY, and the kit's job is the translation layer between what .NET can do and React cannot. |
| **D55** | there is no "optional features" tier: the framework ships as ONE whole, so the file engine folds into `Shenora` too. |
| **D56** | the deploy/update TOOLING is product, not devtools. |
| **D57** | there are no PRE-IMPLEMENTATION design docs: a plan is scaffolding, and once the thing is built the third copy of its reasoning is the one that goes stale. |
| **D58** | the interceptor's media route is the PLAYER's output pipe, not a parallel feature. |
| **D59** | the converter's job, stated exactly: it bridges what the PIPELINE can decode — the device's hardware, plus whatever an adopter hooks in — to what that device's WEBVIEW will accept. |
| **D60** | the kit ships NO page-diagnostic facade. |
| **D61** | ONE `Use…` call defaults everything the kit may choose on the app's behalf, and refuses anything that changes what the app is EXPOSED to. |
| **D62** | the IPC pipe carries INTENT; BYTES go through the resource interceptor. |
| **D63** | "declared but never consulted" is this repo's recurring defect, and it is INVISIBLE by construction. |
| **D64** | the framework is ON BY DEFAULT: `Use…` CONFIGURES rather than enables, and the only per-platform call is the shell's, which exists to inject implementations. |
| **D65** | THREE LAYERS, the package is called `Shenora`, and "Core" means the WIRE between .NET and the web — nothing else. |
| **D66** | a long-running request IS A REQUEST, so the "operation" — a second identity for one thing — collapsed into the IPC contract. |
| **D67** | the DEVICE LOOP is part of the framework, so the kit ships a CLI: `@shenora/cli`, second npm package, binary `shenora`. |
| **D68** | the WebView2 RUNTIME choice belongs to the ADOPTING APP. |
| **D69** | the Live Activity is DATA the app builds in C# and a GENERIC kit widget READS at runtime. |
| **D70** | the kit SHIPS A DEFAULT CONVERSION ENGINE, and it is the platform's own codecs. |
| **D71** | STREAMING IS THE MEDIA TIER'S PRIMARY PATH, and the whole file is what streaming LEAVES BEHIND rather than the thing it produces. |
| **D72** | THE COMPUTED-REMUX ROUTE GETS NO PAGE-SIDE READINESS CONTRACT: the APP warms the plan in .NET and the page stays one plain `<video src>`. |
| **D73** | MEDIA COMPOSITION FOLLOWS THE KIT'S OWN `Add`/`Use` SPLIT, because .NET already has this shape and a second idiom would be a thing to learn twice. |
| **D74** | ONE PATH FOR BOTH STREAM KINDS: the only difference is the encode/decode logic. |
| **D75** | THE SEGMENT TIER IS fMP4, THE GRID IS WHOLE SECONDS, AND THE DEFAULT ENGINE RUNS WHEREVER A CONVERTER IS REGISTERED. |
| **D76** | THE SEGMENT ENGINE COPIES WHAT MP4 CAN CARRY AND RE-ENCODES ONLY WHAT IT CANNOT; A COPIED TRACK IS CUT ON THE SOURCE'S OWN KEYFRAMES, SO THE BOUNDARIES TRAVEL AS A PLAN. |
| **D77** | THREE HOMES, and this file holds only the first: a DECISION here, a subsystem's DESIGN under `docs/design/`, an invariant in `.claude/knowledge/`. |
| **D78** | FOR A REMOTE MEDIA SOURCE THE KIT SHIPS THE ADAPTER, NEVER THE TRANSPORT: |
| **D79** | THE SHELL RAISES THE BACK GESTURE AND THE PAGE DECIDES WHAT IT MEANS, PER PRESS: |
| **D80** | THE PLAYER'S SECOND SURFACE: ON A PHONE THE SHELL DRAWS THE PICTURE, AND THE PAGE KEEPS THE UI. |

<!-- decisions-index:end -->

## The decisions

- **D1 — Shenora is the BODY; Lyntai is the brain; no dependency between them.** Apps may use both;
  Shenora must never reference Lyntai. Keeps each library adoptable alone. The kit is a **hybrid app
  framework — .NET + React — across Windows, Android and iOS** (D32 made a second shell a peer, D37
  named one package per platform, D53–D55 settled the identity).

- **D2 — A package boundary must buy a RUNTIME separation, not a seam.** An `*.Abstractions` split earns
  nothing because Core already holds the contracts; there is no such package as `Shenora.Modules`, and
  no such package as `Shenora.Extensions.DependencyInjection` — module registration is core plumbing, and
  the standard Microsoft DI abstractions are used directly. **D55 extended this to WEIGHT**, which had been
  the one accepted exception. The current set is the header table, never this entry.

- **D3 — One .NET VERSION across the kit: .NET 10.** Every family app and Lyntai target it, the dev
  machine has no .NET 8 SDK, and .NET 8 LTS reaches end-of-support 2026-11. Portable code is plain
  `net10.0`; a platform TFM is added only when a real capability needs one, and **D46 is the bar** —
  `Shenora.Windows` multi-targets `net10.0-windows;net10.0-windows10.0.17763.0` so a WinRT-only
  capability does not force every Windows consumer to raise their minimum OS.

- **D4 — Lockstep versioning from one `<VersionPrefix>` in `src/Directory.Build.props`** (the Lyntai
  model), including the npm packages: `dev.mjs pack`/`doctor --fix` write the npm `package.json` version
  and the README status headline from it, and `doctor` fails on drift. One version story across two
  registries beats two drifting ones.

- **D5 — No push/PR CI; verification is local (`dev.mjs verify`); releases are a single manual
  `workflow_dispatch`.** Family precedent: Lyntai added push CI and it was removed. Don't re-add it as a "gap".

- **D6 — Publishing: NuGet Trusted Publishing (OIDC, no stored API key); npm publish with
  `--provenance` via OIDC** (fallback: a granular `NPM_TOKEN` secret until the npm trusted-publisher
  policy is configured). The version bump is committed only after both publishes succeed, so a failed
  release leaves no phantom bump.

- **D7 — ONE test project, `tests/Shenora.Tests`, not per-package projects** (the brief sketched four).
  Lyntai proves the single-project layout scales to 11 packages; folders mirror `src/`. It is
  `net10.0-windows10.0.17763.0` and references `Shenora.Windows` + the desktop sample; everything else
  arrives transitively. ⚠ **A Windows test project cannot reference `Shenora.Android` or `Shenora.iOS`**,
  so those two are gated from their IL METADATA instead (`MetadataSurfaceTests` +
  `Api/MetadataBaselines/`). API-surface baselines gate SemVer from the first release, and a packable
  project with neither kind of baseline fails a test — **count coverage against `src/`, never against
  this sentence.**

- **D8 — Extraction-first: lift proven sibling code rather than inventing an abstraction.** Prefer lifting proven sibling code — including its post-mortem comments,
  which are the product — over new abstractions. The primary source is the richest desktop-only sibling;
  the second desktop sibling is the conformance reference; Sonora donates its window-state store,
  singleton/restart skeleton and event bridge. Named map: `local/EXTRACTION-MAP.md` (private);
  de-identified: `.claude/knowledge/extraction-sources.md`.

- **D9 — Repo organization clones the family system, and there is no archive tier.**: short `CLAUDE.md` → `docs/README.md` router →
  two-tier `.claude/rules|knowledge` with `RULES_INDEX.md` → gitignored `local/`, plus Lyntai's
  library-repo docs (`DECISIONS.md`, `CHANGELOG.md`).
  🔴 **There is NO archive tier.** One was added and deleted within two days, by which time
  `docs/archive/tasks.md` was the largest doc in the repo at 290 KB — 62 % of all doc weight was
  finished work. Owner: *"we dont keep historial since we have git for that"*.
  **The tell that a doc is really an archive: nobody reads it, and it grows fastest.** Ask what QUESTION
  a reader arrives with — "why is it done this way?" → here · "what is the shape today?" →
  `ARCHITECTURE.md` · "what is left?" → `TASKS.md` · "what happened?" → `git log`. A warning written for
  a future session was never history: it is an invariant, and it belongs in `.claude/knowledge/`.

- **D10 — Two consumption profiles; a `Shenora.Hosting.AspNetCore` package was surveyed and is NO-GO.** The split exists
  so a Sonora-style app (in-process HTTP server shared with mobile clients; WebView2 as "just a browser"
  plus one-way event push) can adopt the shell without the postMessage command bridge. **The package was
  surveyed against the real server-backed app and both proposed contents evaporated:** the SPA
  static-file policy is five lines of someone else's framework, and the loopback gate is app SECURITY
  POLICY written against that app's threat model — shipping a generic version would be the kit deciding
  security on a consumer's behalf, which is worse than shipping nothing. The kit already covers the
  host→page channel (`WebViewIpcBridge` + `IEventBus` wildcard forwarding; `eventBus.subscribeToAll`),
  and an HTTP endpoint can call `IMessageDispatcher.DispatchAsync` directly. Revisit only if a real
  consumer cannot express what it needs.

- **D11 — The IPC envelope follows the proven family shape** — `{id, module, type, payload, timestamp}`
  request, category-wrapped response, ~50 ms-batched notification array — not the brief's `route`-string
  sketch. Two shipped apps already speak it, migration stays mechanical, and the notification envelope
  doubles as the WebSocket wire format in the server-backed profile. Ergonomic `"module.action"` route
  helpers sit on top in `@shenora/react`.

- **D12 — Sibling names stay out of tracked files.** Lyntai and Sonora are public repos by the same
  author and may be named; the three private siblings are referred to generically ("the primary desktop
  sibling", …). Real names and paths live only in `local/`, enforced by the pre-commit guard.

- **D13 — Headless: no UI component library dependency, ever.** Shenora is infrastructure — apps boot
  their own design system on it. `@shenora/react` ships bridge/hooks/behaviors only; the WinForms side
  ships neutral primitives (splash, tray theming) with parameterized colors, never a styled control set.

- **D14 — The auxiliary browser subsystem is in scope, and it ships DESKTOP-ONLY.**: offscreen render sessions with a bounded pool,
  login windows with per-provider persistent profiles, and co-browse streaming (CDP screencast frames
  out, human input back). Proven in two siblings. It ships inside `Shenora.Windows` (`Sessions/`) since
  D37. ⚠ **DESKTOP-ONLY, and it stays that way (D39)** — both mobile shells host a webview, so "port the
  sessions to mobile" looks obvious and is not. Read D39 before proposing it.

- **D15 — Growth is harvest-driven.** Shenora evolves by promotion: when something proves itself while
  an application is being built, it gets generalized (per `generic-library.md`) and moved in on a minor
  release. The app keeps a thin wrapper; the framework gains the proven core. This extends D8 from a
  bootstrap strategy into the permanent operating model — **not a speculative roadmap.**

- **D16 — Mobile shells are a target, and the IPC envelope is transport-neutral so they cost no contract
  change.** That prediction held exactly: the kit ships its own **MAUI `HybridWebView`** shells
  (`src/Shenora.Mobile/` compiled into `Shenora.Android` + `Shenora.iOS`, D32/D37), the transport is
  `createHybridWebViewTransport` **inside `@shenora/react`** rather than a separate package, and app
  logic written against `@shenora/react` runs unchanged on all three. Proven on a device and a
  simulator. ⚠ **Transport neutrality is proven for IPC only** — it says nothing about the
  desktop-FLAVOURED service contracts (`FileDialogOptions` carries Win32 vocabulary a mobile picker
  would half-ignore). Narrowing those is an accepted pre-1.0 possibility awaiting a real consumer (D15).
  What mobile actually cost landed elsewhere: `.claude/knowledge/mobile-shells.md`, D44 and D45.

- **D17 — `Shenora` depends on the Microsoft DI IMPLEMENTATION package, not only the abstractions.**
  `ShenoraApplication.CreateBuilder` → `Build()` constructs the `ServiceProvider`, and
  `BuildServiceProvider` lives in `Microsoft.Extensions.DependencyInjection` — the same dependency shape
  as `Microsoft.Extensions.Hosting`. Contracts still bind to the abstractions. A pluggable third-party
  container is deliberately NOT offered; no family app uses one.

- **D18 — The library is Shenora (神阙); git history restarted at the rename.** 神阙 pairs with the
  sibling 灵台 (Lyntai) as an acupoint name, and the ending echoes Sonora. Because the rename predated
  any release or remote, history was restarted as a single bootstrap commit rather than rewritten; the
  pre-rename history is kept privately offline. **The former name stays out of tracked files and commit
  messages permanently**, same discipline as the private sibling names (`sensitive-info.md`).

- **D19 — Windows primitives and web hosting are ONE layer, and the direction is `WebView/` → `Shell/`,
  never the reverse.** D37 merged the two packages, so the edge is now internal to `Shenora.Windows`.
  Extraction proved the split unworkable: the UI-thread marshal pattern had been hand-rolled **14 times
  across 3 packages with 5 incompatible pre-handle policies**, producing real defects (7 unguarded
  `BeginInvoke`s; a site whose comment explains the pre-handle trap and then commits it on the next
  line). ⚠ **`Shell/` carries no `Shenora.Core.Ipc` dependency** — that keeps a WinForms-only consumer
  viable (a tray/single-instance utility with no web frontend), which is why the window-command and
  drop-zone modules live in `WebView/`.
  🔴 **ENFORCED, BECAUSE IT HAD ALREADY BROKEN.** `Shell/WinFormsUiDispatcher.cs` carried an unused
  `using Shenora.Core.Ipc;` and nothing failed — **once a package boundary becomes a folder, a violation
  of it degrades into an unused `using`**, and a full rebuild emits 0 warnings for that. `ShellLayeringTests`
  pins it now and names the offending file when it fails.

- **D20 — Portable contracts live in `Shenora`; only Windows implementations live in the Windows shell.**
  The reusable part of a desktop kit is the *logic* — IPC and the feature contracts — because that is
  what a non-Windows shell can share, so an app's modules compile with no Windows reference.
  `IUiDispatcher` is the one UI-thread marshalling seam, and its contract carries a **three-state**
  target (`NotReady`/`Ready`/`Gone`) rather than a bool, because three call sites have review-earned
  pre-handle policies a bool would silently break.
  **The placement rule, extended by D48: if a SHELL implements it, the contract lives in Core — full
  stop.** The mirror case: `IPathLocker` stays with its implementation, because advisory lock files are
  portable and no shell implements it. **Scope guard:** a contract moves only when app logic needs it to
  compile off Windows — portable-in-signature is not the bar, which is why the whole window-state stack
  stays in the Windows shell.

- **D21 — For a whole application FEATURE, the kit ships primitives + lifecycle hooks; the app owns the
  product.** Owner: *"co-browse itself is a whole feature — you just need to provide enough interface for
  other systems to plug/hook onto its cycle."*
  **The test: could a consumer build its own version of this product on our primitives, without adopting
  our product decisions?** If not, we shipped too much — or we shipped too few hooks.
  **The kit ships NO drivers.** A reference driver is SAMPLE material, not library surface: it becomes
  SemVer surface at 1.0, it makes the kit look like it ships that product, and it invites the next
  recipe in beside it. `CookieLoginDriver` lives in the desktop sample and only ever consumed public
  seam members — which is this test passing in the other direction.

- **D22 — Name every public type for its MECHANISM, never for a scenario, product or business need.**
  Owner: *"we should build a generic library, so for co-browser it should be focus on browser hook, life
  cycle, events instead a single business need."* The naming half of D21, stated separately because the
  kit passed D21 on SHAPE while failing it on NAME twice.
  **The test: could a consumer whose use case is nothing like the one in the name still recognise this type
  as the thing they need?** `LoginWindow` became `InteractiveSession` (it held no login logic);
  `CoBrowseSession` became `StreamingSession`, since streaming frames and taking input is co-browse,
  remote support, capture or a preview pane depending on who wires it. Neither was a behaviour bug: a
  scenario name makes the kit LOOK like it ships that product, so consumers never find the primitive.
  ⚠ **If a type in `src/` needs a scenario name to make sense, that is the signal it does not belong in
  `src/`** — not a licence to name it. Sibling vocabulary that is genuinely mechanism is fine and must not
  be "fixed": `ProfileDirectory`, `Module`, `ImmersiveDarkMode`, `UserDataFolder`. **Enforced** by a
  domain-vocabulary sweep over the API baselines, which enumerate every public type, member and PARAMETER
  name (named arguments are a source contract).

- **D23 — The module contract carries the EVENT path, and the kit tracks long-running requests.** Three
  parts, one design, shipped as 0.2.0. ⚠ **D66 merged the tracked-operation half into `IpcRequest`**, so
  the `Operation*` names are retired.
  - **(a) A route receives an `IModuleContext`** — `Publish` (module-scoped emit), `Report`, `Logger`. The
    bus was already the spine and the contract did not admit it, so every app re-agreed it by hand.
  - **(b) The correlation-and-lifecycle MECHANISM only** — id, state, progress, scope, idempotent finish,
    cancel-by-id, bounded history, throttled progress. **Out stays everything that decides what a request
    IS:** no queue, no scheduler, no phase model, no `Kind` enum, no i18n, no UI (D13), no persistence.
  - **(c) The outbound half is `NotificationPump`**, transport-neutral (subscribe → filter → bounded queue
    → batch → ready gate → guarded serialize); `WebViewIpcBridge` keeps only the WebView2 parts.
  🔴 **Every non-terminal state must have a sanctioned exit to a terminal one**, held by a test that
  ENUMERATES the state set (`IpcRequestStateInvariantTests`) — an emergent trap is invisible in any single
  guard's diff. 🔴 **Progress is not percent and nothing is clamped**: `Total = null` means no known
  denominator, never zero.

- **D24 — Frameless chrome is a FIXED WinForms type, not an attachable behaviour.** Owner: *"Frameless
  chrome should be part of winform (as a style of our winform design)"*. A review proposed extracting it
  into a `FramelessChrome.AttachTo(form)` behaviour; **rejected**, because the window style is naturally
  set in `CreateParams` at handle creation and attaching after the fact needs `SetWindowLong` +
  `SWP_FRAMECHANGED` — a SECOND mechanism for the same property, doubling the verification surface in
  the one area where a green unit suite has twice been the wrong answer. The benefit is also narrower
  than it looks: `WindowCommandOptions` takes a plain `Form` plus delegates, so a window that is not an
  `OptimizedForm` can already drive minimize/maximize/close/drag/resize over IPC.
  **ACCEPTED LIMIT:** an app that cannot change its form
  base cannot take the frameless chrome. Reopen on adopter evidence, not on the symmetry argument.
  **The cohesion complaint WAS fair, and the split line is the rule to reuse:** caption-button rendering
  moved to an internal `CaptionButtonRenderer`. **Extract what is pure input → pixels; leave anything
  that answers a window message where the OS can see it.**

- **D25 — Frameless chrome and native drop zones are the kit's FLAGSHIP pair: settled, and not to be
  redesigned without adopter evidence.** Owner, after testing both by hand: *"those 2 features kind important"* ·
  *"the frameless winform was developed properly so don't really change that"* · *"I have been there
  before so do not change this"*. Both are fully generic AND deliver something the adopting app would
  not have got by hand — the chrome raises the UI bar with Snap Layouts (`HTMAXBUTTON`), Win11 rounded
  corners squared while maximized, immersive dark mode, DWM border colour and runtime theme resync.
  🔴 **Drop zones deliver a capability the page cannot have at all:** a page-side drop yields a `File`
  whose only accessor is its CONTENT, forcing an eager byte copy of every dropped file across IPC
  *before the app knows whether it wants any of them*. Native overlays yield `string[]` paths.
  **So `useDropZone` is not optional sugar — it is THE file-drop path on this kit**, and a DOM drop
  handler is what it replaces, not an alternative to it.

- **D26 — the kit's DESKTOP scope is Windows only, and Linux is served by the SERVER-BACKED profile
  rather than by a native Linux shell.** ⚠ Read this as DESKTOP scope, not kit scope — D32 added Android and iOS
  shells, and they ship; the two decisions are about different platforms.
  - 🔴 **The reusable part is the SELECTION CRITERION: a candidate shell must expose the NATIVE WINDOW, not
    merely host a WebView.** Without one there is nowhere to put transparent native overlays over page
    elements, so drop zones are impossible and the D25 eager-copy problem returns. That is what ruled out
    **Photino** (confirmed still true 2026-08-15) and it is the question to ask of any future candidate —
    **Avalonia** being the unevaluated one.
  - **Reopen on a real Linux consumer plus a shell that passes that test.** Until then the answer is the
    server-backed profile, which already runs there; the Windows shell is ~60 % of the kit's C# and none of
    it ports.

- **D27 — the scheduler's unit is a MISSION, and a definition is not an execution.**
  - **The naming rejections generalise:** `Work` is too common a word to grep; `Task` collides with
    `System.Threading.Tasks` in every consumer importing both; `Quest` reads as DOMAIN vocabulary in a
    games family (what `SurfaceVocabularyTests` keeps out of `src/`).
  - **`MissionDefinition` (what should run) is separate from `MissionExecution` (one specific run)**,
    replacing four types with two. An execution carries `Attempt`/`IsRunning` and **no
    `CancellationToken`** — the body takes its token as a second parameter, so an execution stays a pure
    value safe to hold in a diagnostics view.
  - 🔴 **Why now rather than when a consumer asked.** The two-consumer bar governs adding CAPABILITY; the
    shape rule governs SHAPE: **pay now only where the later change would be BREAKING rather than
    additive.** Owner: *"bigger change does not mean a bad thing … this is still pre-1.0."*
  - Declined and still declined: a handler registry by type (app composition, and it would make the kit
    own serialization of app types — the iOS AOT problem), a separate queue and runner, an `IMission`
    interface (pushes toward class-per-mission).

- **D28 — the queue's storage is named for what it is, and the queue itself stays internal.**
  `IMissionQueueStore` is where the queue's own entries live across a restart, not a "durable missions"
  service beside it — durability stops being a parallel concept.
  ⚠ **A pluggable async QUEUE was designed and rejected; do not re-propose without new evidence.** It
  puts an `await` in the dispatch path, which cannot run under the scheduler's lock, so admission would
  read candidates, take the lock, then re-validate against a collection that may have changed
  underneath — a race in the one place where a race corrupts rather than delays, bought for a
  distributed-queue capability nobody asked for. The part apps actually vary (ordering) is already
  theirs through `IMissionPolicy`.

- **D29 — a chain is ONE queue entry, not N with dependency edges.** `MissionChain.Sequence` returns an
  ordinary `MissionDefinition`, so the scheduler gains no dependency concept, no blocked-on-predecessor
  state and no edges — the alternative was a DAG engine by another name, declined on the evidence that
  no sibling has needed one.
  **The accepted cost, so nobody rediscovers it as a bug:** a chain holds the UNION of its steps' claims
  for its whole life, taking the STRONGER mode where steps disagree, so a five-step chain over five
  paths blocks all five throughout. Claims are still acquired as one set, so deadlock-freedom is
  unchanged. A step's retry repeats THAT step; there is no chain-level retry. `IMissionChainContext` is
  IN-MEMORY only — a durable chain carries state in `Payload`, because the kit cannot serialize an app's
  object graph and a resume that silently lost the context is worse than one that never had it.

- **D30 — filesystem MUTATIONS are a separate component from mission scheduling.** Owner: *"it's more a
  different design rather than put them all into mission management"*. `IFileUpdateQueue` decides how
  changes LAND; the scheduler decides which missions RUN.
  - **Why, and it is not tidiness:** a path claim excludes two missions for their whole duration though the
    expensive phase usually touches only a temp file. **Compute in parallel, serialize only the landing.**
    The failure modes do not overlap either — a scheduler's are starvation and deadlock, an applier's are
    partial writes and locked targets.
  - **Atomicity is the app's choice per update** (owner: *"it depends what the application need"*):
    `PerChange`, or `AllOrNothing` via compensating rollback — which forces STAGED deletes, a delete being
    the one change that cannot be undone from nothing.
  - 🔴 **Crash-atomicity is opt-in via a write-ahead journal, and the ORDERING is the property:** the undo
    plan is durable BEFORE the mutation, since a plan written afterwards is missing exactly the change that
    got interrupted — which is why undo is DATA rather than closures. Recovery rolls BACK an update
    interrupted while applying and FINISHES one interrupted while committing; the reverse undoes a success.

- **D31 — cross-process file access is TWO problems, and one mechanism cannot serve both.**
  - **`IPathLocker`/`IPathLease` excludes PARTICIPANTS** — a second instance, or a child process the app
    spawns while the parent holds the lease, which is how an external command-line tool participates
    without knowing anything about the kit.
  - **`IFileLockInspector` answers for everyone else** — a game holding its assets, a mod loader,
    antivirus. None will ever take a lease, so exclusion is impossible and the only useful thing is a
    NAME. ⚠ **`WhoHolds` returning empty means "cannot tell", never "nobody"** — the distinction matters
    at a call site. The Windows implementation is Restart Manager and lives in `Shenora.Windows`; **the
    CONTRACT stays in Core, because a shell implements it** (D20/D48).
  - **Lock files live in the app's own directory, never the managed tree** — an app frequently does not
    own the folder it manages, and sidecar locks there get synced, committed, and outlive the process.
  - **Network shares are supported.** Leases work over SMB2+ *provided the lock directory is ON the
    share* — a lock in one machine's local storage is invisible to the other, and that is the setting
    that fails silently. A lease released by a crash returns when the SMB session times out.

- **D32 — a second shell is a PEER, and the kit's job is the substrate under both.** Owner: *"abstract
  the logic out as much as possible … so it supports both MAUI and WinForm (some capability can
  implement differently like dropzone and frameless)"*. `Shenora.Mobile` references no Windows assembly.
  **A thin shell is the evidence the split is in the right place**, because the substrate moved first — a
  fat shell would have meant something portable was still trapped in the Windows one.
  - **The bar stays D20's, not "it looks platform-neutral":** *can app logic compile off Windows?*
    Window geometry, tray, secondary windows and native drop zones stay in the Windows shell because
    they are desktop CONCEPTS — on mobile they are absent, not different.
  - 🔴 **A platform limit recorded as permanent outlives the platform.** Lifting the resource-serving layer
    once died because `HybridWebView` had no seam; `WebResourceRequested` now exists and D45 uses it.
    **What survives is the METHOD — check the platform before designing — not the verdict.**
  - **The platform-owned loop is why `Start`/`Stop` exist.** `IShenoraRunner.Run` is contractually
    "blocks until shutdown", which a MAUI activity cannot honour, so the mobile host registers no runner
    and the app drives the pair from its own lifecycle.

- **D33 — an ABSENT capability throws and names the platform; a SATISFIED one is an honest no-op.**
  `ShellCapability.NotSupported` is the one message. A silent no-op is the "mistyped resource prefix
  degrading to an all-404 provider" class this repo keeps paying for.
  **The distinction is load-bearing:** putting FILES on the clipboard has no expression on either mobile
  pasteboard → refuse; `IUiInteraction`'s block/unblock is satisfied BY the platform (mobile pickers are
  modal) → an honest documented no-op. Refusing the second kind would break portable logic that is
  behaving correctly. **"Absent" means no expression exists here, not "we did it differently".**
  🔴 **A THIRD kind reads exactly like the first: absent from the WRAPPER, present on the PLATFORM.**
  Clipboard IMAGES were refused on the grounds that MAUI Essentials' clipboard is text-only — true, and
  not the question, since `UIPasteboard` and `ClipboardManager` both carry pictures. **Before writing
  `NotSupported`, check the PLATFORM API, not the convenience wrapper.**
  ⚠ **NOT a `DispatchProxy`.** One reflection proxy throwing for any interface would undo the iOS/AOT work
  in `IpcJson.AddTypeInfoResolver` — reflection is what trimming strips. Each shell writes small explicit
  stubs sharing the one message.

- **D34 — a shipped assembly the test project cannot REFERENCE is gated from its IL metadata.**
  `tests/Shenora.Tests` is a Windows TFM and cannot reference the mobile assemblies, so
  `MetadataSurfaceTests` reads the tables with a `MetadataReader` (the full `ApiSurfaceDump` needs
  runtime types for `NullabilityInfoContext`).
  ⚠ **The gate is deliberately weaker and says so everywhere it appears:** NAME-level, so it catches an
  add, a removal and a rename but NOT a signature-only change (`string?` → `string`, a dropped default,
  `set` → `init`). **That is the standing argument for keeping such a package thin.**
  🔴 **`Every_packable_project_has_a_baseline_of_one_kind_or_the_other` is the real fix.**
  `ApiSurfaceTests`' own coverage check walks the TEST assembly's references, so the package it cannot
  reference is exactly the one it cannot notice is missing. `IsPackable` is the definition of "shipped",
  which is why the pack list must agree with it.

- **D35 — "open a folder" is a DESKTOP concept, and the portable answer is to decompose it into the
  intents behind it.** Owner: *"open folder in mobile will be different cases than open folder in
  desktop … for desktop it's more free"*. A desktop folder browser hands back ambient, permanent access to
  an arbitrary path; Android hands back a revocable, scoped grant to a tree URI. **Same word, different
  guarantee — papering over that is how a portable-looking API becomes a lie at the one moment an app
  relies on it.** Ask what the app actually wanted; all three are expressible on both shells:
  1. **"Somewhere I own to read and write."** No picker at all — `ShenoraPaths` on desktop, the MAUI
     app-data directory on mobile. **An app asking the USER for this is the bug.**
  2. **"Let the user hand me some media."** A platform media picker on mobile, a multi-select file dialog
     with image filters on desktop. Genuinely portable.
  3. **"Let the user grant me a working directory."** The only one that stays desktop-flavoured, because
     the permission MODEL differs. Name it desktop-only rather than pretending.
  **Consequence:** `IFileDialogs.OpenFolderAsync` is documented desktop-only and the mobile implementation
  refuses it by pointing at (1) and (2). A media contract is NOT pre-built — nobody has asked.

- **D36 — the HOST advertises what it can do, in the handshake; the client never sniffs the platform.**
  D33 says what happens when a page calls something absent; this is how the page avoids calling it.
  `ShellInfo { Name, Capabilities }` is the ready handshake's response data and the page renders on it.
  - 🔴 **Capability, not platform, because the platform is the wrong question.** What a host offers depends
    on what the APP composed — a desktop shell that never registers `TrayIcon` has no tray, and a frontend
    running in a plain browser tab during `vite dev` has none of it. `Name` is for diagnostics and is
    documented as never-branch-on. **Declared by the app, not inferred by the kit**, since the kit cannot
    know which services were registered; the cost is honesty, in that a capability advertised but not
    composed turns a rendered button into a D33 throw when pressed.
  - **Absent means "assume nothing", never "assume desktop".** A capability-less reply covers browser dev,
    a host that has not opted in, and a host predating this. Defaulting the other way makes the browser the
    one place the page renders wrongly — which is where it is developed.

- **D37 — ONE shell package per PLATFORM, named for the platform.** The three Windows packages merged
  into `Shenora.Windows`; the mobile shell ships as `Shenora.Android` + `Shenora.iOS`. The package COUNT
  has moved since (header table); **the SHAPE this decided is what governs.**
  - 🔴 **The test, applied in both directions: does the boundary correspond to something a CONSUMER
    experiences?** "I am building an Android app" does — so mobile SPLIT even though the two share every
    line of source. "WinForms without WebView2" does not — this kit's premise is React in a webview, so
    that consumer cannot exist, and Windows MERGED. **The same question produced opposite answers, which
    is how you know it is the right question.**
  - ⚠ **Both arguments against merging measured the easy thing instead of the relevant one** — SemVer
    surface that adds no dependency, and a dev-time RESTORE size that is not shipped bytes.
  - **The mobile packages share SOURCE, not an assembly** (`src/Shenora.Mobile/`, deliberately with no
    csproj) — a third assembly would either be published, carrying surface nobody asked for, or need
    embedding tricks to hide it. Divergence goes in each project's `Platforms/` folder, which the MAUI SDK
    includes per TFM. **Naming is by platform, not by framework**: the two mobile faces share no web engine.

- **D38 — an off-screen session gets the app's own BUNDLE, and deliberately not its custom SCHEMES.**
  `SessionBrowserOptions` takes `VirtualHost` + `ResourceProvider` + `FolderMappings`, so a packaged
  desktop app can co-browse or off-screen-render its own frontend, passing the host's own values through.
  - **Both halves or neither, refused at initialization** — either alone serves nothing, and the symptom
    would be identical to the bug being fixed.
  - 🔴 **The app's `RequestFilter` is consulted BEFORE the bundle, and both live in ONE
    `WebResourceRequested` handler.** Two handlers each assigning `args.Response` is last-writer-wins by
    subscription order, **which is not a contract to rest a security boundary on.**
  - **NOT shipped, deliberately: a custom/deferred SCHEME inside a session**, and **`SessionController`
    exposes no `CoreWebView2`** — the raw browser object would make every future capability an escape
    hatch instead of a seam.
  - ⚠ **Bundle responses carry `Access-Control-Allow-Origin: *`, and in a session the page can be ANY
    origin**, so script in a co-browsed page could `fetch` the whole bundle. Accepted: the header is
    load-bearing for a dev-mode page on another origin, and the exposure is the app's own frontend.

- **D39 — the auxiliary-SESSION stack stays a DESKTOP capability. Both mobile shells host a webview;
  that is not the same thing.** `StreamingSession`, `RenderSessionPool` and `InteractiveSession` do NOT
  port, and the reason is CAPABILITY, so it does not rest on a store-policy reading that could change.
  - **The stack rests on CDP, not on "a webview"** — screencast, device-metrics override, synthetic input,
    and neither mobile shell has an in-process CDP client. ⚠ **That is an unre-checked PLATFORM reading and
    D32 was wrong in exactly this shape**, so re-check before citing it to refuse a port.
  - 🔴 **THE TRAP, and the real reason this needs writing down.** A port IS buildable behind the same
    interface — frame-polling plus `evaluateJavaScript` synthetic DOM events. It would compile, demo, and
    be materially WEAKER: polled instead of change-driven, with `isTrusted: false` events, which is
    precisely what fails on the pages `InteractiveSession` exists for. Same method name, different
    guarantee — D35's shape, and tempting because *the C# ports for free*.
  - **What the mobile answer IS, decomposed the way D35 decomposes "open a folder".** *Show a web page* →
    `IUrlLauncher`. *Log into a third-party provider* → the platform auth session, which is BETTER: the
    cookies stay in the system. *Render my own UI off-screen* → does not arise; the app's UI IS the webview.

- **D40 · D41 — media as its own package family: RETIRED, both bodies deleted rather than banner-stacked.**
  They governed a nine-package set that no longer exists: `Shenora.Media` +
  `Shenora.Media.{Windows,Android,iOS}`. The three platform packages were deleted by D45 before any
  shipped (serving bytes to a page turned out to be resource INTERCEPTION, a shell capability), and
  `Shenora.Media` itself folded into `Shenora` by D53.
  **The one rule that outlived both:** app logic names the media types and compiles on `net10.0` with no
  platform reference, enforced by `samples/Shenora.Sample.Logic` turning RED if a platform type reaches it.
  ⚠ **Deleted rather than amended, and the test is reusable: does this entry describe something that
  SHIPPED?** If not, delete it and say what replaced it. Owner: *"we should do a cleanup, remove
  everything thats irrelevant anymore which is clearer than keep adding."*

- **D42 — for an APP that needs total format coverage, an ENGINE is the primary playback path on every
  platform, including mobile — and the kit ships none.** Owner: *"mobile library is not stable to support
  different type of media but if we use engine we have the control"*. The argument is CONSISTENCY — one
  behaviour matrix across three platforms — and it beats the byte count.
  - ⚠ **The APP's choice, not a statement about the kit's own path.** The kit's default is the TRANSLATION
    LAYER — platform codecs, container repair, segments (D59/D70/D71) — rendered by the page's element on
    the desktop and the shell's own surface on a phone (D80). An engine is the reach past both, via D51.
  - **Why:** codec support is vendor-declared PER DEVICE, and containers are a second, worse axis —
    H.264-in-MKV can fail on Android while H.264 itself decodes perfectly. **Recorded as JUDGEMENT, not
    dressed up as a measurement**, because this repo cannot verify the owner's field experience.
  - 🔴 **The constraint: a playability verdict is PER STREAM, never one boolean for the file.** The AUDIO
    track is usually what fails while the video decodes perfectly, so `CanPlay(file) -> bool` would be
    wrong in the commonest case — and it is why `remux` earns its place beside `transcode`. The kit
    references no engine package; an app REFERENCES UPSTREAM, never vendors, and owns the LGPL/GPL choice.

- **D43 — the media contracts split by DEPENDENCY, not by feature name. "Thumbnail" is two mechanisms and
  gets two homes.** ⚠ Read "two homes" as two FOLDERS; the package family it distributed across is gone.
  Thumbnails are still unbuilt: the two-consumer bar (D15) is not met.
  **The honest axis is what each operation NEEDS.** *Probe* (duration, dimensions, streams) needs a
  demuxer; *frame grab* and *playback surface* need a **decoder**, the same one playback uses; *image
  resize* needs an **image codec and no media decoder at all**. So the first three are ONE family and
  resize is its own contract. The playability verdict stays portable logic over a probe result — a pure
  function in `Shenora.Modules.Media`, per stream (D42).
  - **Verified by compiling: image resize needs no extra package on any platform, so thumbnails cost 0 MB
    everywhere** — unlike playback.
  - 🔴 **So "thumbnail" is two types, not one word** — D35's same-word-different-guarantee mistake in
    miniature. The rule is `generic-library.md`; **the APP unifies them, because only it knows whether its
    item is a video or a picture.**

- **D44 — the media URL names NO origin, and the two mobile shells need OPPOSITE response BODIES.** Measured
  on devices; `samples/Shenora.Sample.Maui/MediaRangeProbe.cs` re-measures it.
  - **The URL is a RESERVED PATH on the page's own origin, reached by a RELATIVE url** (`/<reserved>/?src=…`)
    — not a custom scheme and not a virtual host. Neither obvious answer works on both shells: Android
    intercepts `app://` and then its media pipeline **refuses** it (`MEDIA_ERR_SRC_NOT_SUPPORTED`, instantly,
    even for a correct 200), while iOS intercepts **only** `app://` and lets an https host reach the real
    network. The page's own origin is intercepted and media-capable on both **by construction**.
  - 🔴 **THE CONSTRAINT, and it is the load-bearing one: the same portable request needs an UNSLICED body
    on Android and a SLICED one on iOS**, because Android's seam applies the `Range` start itself and iOS
    passes the body through verbatim. Getting it wrong is not graceful degradation — the offset is applied
    twice and a player asking for a file's tail retries the identical range for ever. ⚠ **And the wrong
    choice looks correct** on a faststart file, which only ever asks for `bytes=0-`.
    (Measurements: `docs/design/mobile-shells.md`; how to observe it: `.claude/knowledge/mobile-shells.md`.)

- **D45 — resource interception is a MIDDLEWARE PIPELINE in `Shenora`, implemented by each SHELL.** Owner,
  and the order of the steps is the argument: *"the interceptor interface should live in the core"* →
  *"desktop will also have issue with access local folder/files"* (not a mobile workaround) → *"even file
  access too"* (media is ONE CASE) → *"it's more like a middleware design if you think this way"*.
  - **Interception is a SHELL capability, but the CONTRACT is Core's** — every shell needs it, so one
    `IWebViewInterceptor` means **path containment is written once instead of three times**.
  - 🔴 **MIDDLEWARE, not a list of handlers, because the cross-cutting concerns are the point.**
    Containment, the SSRF guard, a cache, logging, a metric — each WRAPS the next rather than terminating,
    and a "first non-null wins" list cannot express any of them.
  - **Registry and composition are in Core** (`WebViewResourcePipeline`), the only place order,
    decline-and-fall-through and wrapping are TESTABLE with no webview. Shells differ only in event glue:
    **mobile resolves SYNCHRONOUSLY; the desktop has a deferral and must not block.**
  - **The page's half is ONE npm package for every shell**, and **the handshake advertises NEITHER the url
    scheme NOR the range delivery** — a page told "you are on iOS, use `app://`" branches on platform.

- **D46 — a capability that needs a newer PLATFORM TARGET is opt-in, never imposed. The consumer picks the
  target; the kit makes the consequence explicit.** Owner: *"so we let the consumer decide their target
  platform instead of force it?"*
  - **The case:** `IPlaybackSession` on the desktop needs `SystemMediaTransportControls`, which is WinRT,
    and the projections exist only when the TFM names a Windows SDK version — so raising `Shenora.Windows`
    to a versioned TFM would make every Windows consumer retarget for a capability most never call.
  - **The rule: multi-target, and let the plain variant REFUSE BY NAME** — on `net10.0-windows` the type
    still throws `ShellCapability.NotSupported` with the remedy in the message. Absent would be worse.
  - **Why a build flag CANNOT do this:** a consumer's MSBuild property is evaluated long after the kit was
    packed, so content can only vary by TFM, by package, or by having no compile-time dependency at all.
    ⚠ Two hand-written variants of one type then need TWO gates — `ApiSurfaceTests` sees only one TFM.
  - ⚠ **The related trap:** `TargetPlatformVersion` is what you may COMPILE against;
    `SupportedOSPlatformVersion` is the floor you RUN on — **and leaving the latter unset silently defaults
    it to the former**, which is how bumping a TFM for one API quietly raises everyone's minimum OS.

- **D47 — while ONE repo fully adopts the surface, prefer the CORRECT shape over the compatible one. Ship no
  compatibility aliases; rename when the name is the defect.** Owner: *"sonora actually is the first one
  fully adopting all features so you can fix anything into the best here which only cause 1 repo to
  update"*.
  **What changed is the PRICE of a break, not the rules about it.** A break against a known, single,
  same-author adopter is one repo's compile errors — a bounded, visible cost, not the unbounded one
  "published" usually implies.
  - 🔴 **THIS ENTRY EXPIRES, AND NOTHING NOTICES WHEN IT DOES.** Its licence rests on a fact about the
    WORLD — how many repos depend on the published surface — which no gate here can read. **It is the
    second adopter that ends it**, because from then on a break costs somebody who did not ask for it.
  - **So the trigger is stated rather than assumed: before the next release, confirm the adopter count.**
    One repo → this stands. Two or more → superseded, a deprecation path is owed, and D49's pre-1.0 id
    retirement stops being deferrable. ⚠ **A deferral nobody wrote down gets rediscovered as a defect.**

- **D48 — the file-operation engine is its own LAYER hanging off Core, not part of it.** Owner: *"because
  this include file operation so we should have a sperated library/package for this"*.
  ⚠ **Its PACKAGING conclusion is reversed — D55 removed the package, D65 made it the
  `Shenora.Engine.Files` namespace. Read the layering; ignore the package ids.**
  - **Why:** Core is what *every other package references*, so a phone app that hosts a page and plays a
    file was carrying a self-updater it will never call. **This APPLIES D37's test:** "I am on Windows" is
    not a choice you make per feature; **"I am building an app that self-updates" IS one.**
  - 🔴 **The edge points `engine → Core`, CHECKED rather than assumed**, and that decided the leftovers.
    `IFileLockInspector` was SPLIT BACK OUT because **a shell must be able to implement a Core contract
    without reaching outward for it**; `IPathLocker` went the other way, advisory lock files being portable.
  - ⚠ **The strongest objection is REJECTED: it holds TWO clusters that do not touch.** They stay together
    because the consumer story is ONE, and **unused code inside something you CHOSE to add costs close to
    nothing, unlike the same code in Core.** **The trigger to revisit is a real adopter that wants one and
    refuses the other**, not a call graph with two components.

- **D49 — retired package ids stay LISTED until 1.0; pre-1.0 ids are retired in ONE deliberate pass.**
  Owner: *"its okay let them be there we will retire all pre-1.0 packages once we got a fully working app
  framework working"*.
  - **What this settles:** the ids D37 merged away are still listed and undeprecated on nuget.org. That is
    a CHOICE with a trigger, not an overdue chore — which matters because **a deferral nobody wrote down
    gets rediscovered as a defect.**
    Unlisting buys nothing while the id set is still moving, and it is not stable yet.
  - **The trigger, stated so it can actually fire:** the kit reaching "a fully working app framework" — the
    same milestone that makes a deliberate 1.0 freeze possible. Then retire every unused pre-1.0 id in one
    pass with `dev.mjs nuget-retire`, which already refuses to unlist an id whose replacement is unpublished.
  - ⚠ **Nothing may CLAIM the retirement has happened.** `README.md` said the old ids "carry a deprecation
    notice"; they do not. An unlisted-but-restorable id is harmless — **a doc describing a state of
    nuget.org that does not exist is not.**

- **D50 — the native launcher is a LIBRARY plus a template, written in C++, one binary per platform.**
  Owner: *"so it probably need to be template + c++ library"* · *"the only requirement is (compatibale
  linux+ windows for future needs, and small)"*.
  - **Library + template is not a judgement call — the seam was MEASURED twice.** Two siblings, no contact,
    wrote the same three files: the generic two are the LIBRARY, `main.cpp` is the TEMPLATE. What stays
    per-app (exe name, icon, version resources, signature) is **a build step, not a source fork**.
  - **Rust was evaluated and lost on the owner's criterion** — whether it helps NuGet packing; it does not.
    **D8 decides it:** two proven C++ implementations exist, so **the two-consumer bar is met in C++
    specifically**. ⚠ **C++ was NOT chosen because Rust is worse.**
  - 🔴 **The JSON parser is a CONFORMANCE requirement, not a taste one.** It must agree with
    `UpdateManifest.Parse`: **paths normalise separators AND case; hashes compare case-insensitively.**
    Getting either wrong makes a release look either fully changed or fully unchanged.
  - ⚠ **Calling it a library makes verification sharper** — `dev.mjs verify` compiles none of it, so a Node
    harness drives a PREBUILT launcher and an adopter's CI runs THE KIT'S suite against THEIR binary.

- **D51 — anything the kit SHIPS AS BYTES must be MIT-compatible; an app that wants a copyleft binary
  supplies it through a `ResourcePack`.** Owner: *"we are on MIT so we should build one compatible with
  MIT"*. A closed-source app is NOT at risk from LGPL — that is the whole LGPL/GPL difference — but
  shipping the same binary from HERE makes the kit the **redistributor** and hands attribution and
  relinking duties to every consumer. `MIT` over an LGPL payload is what a compliance review finds late.
  - **The constraint:** shipped bytes are MIT / BSD / Apache-2.0 / ISC / public-domain. **GPL never** —
    x264/x265 and `--enable-gpl` relicense the consuming APP, the one outcome a devkit must never cause.
    Not LGPL binaries either: the licence is fine, **the REDISTRIBUTION is what is wrong**. Prefer the
    platform's own codecs (zero bytes, zero licence weight), then permissive libraries.
  - 🔴 **PATENTS ARE NOT COPYRIGHT** — openh264 being BSD grants no H.264 patent rights, so this settles
    the licence question and leaves the patent one open, per shipped codec. Not legal advice.
  - ⚠ **ffmpeg's licence is DETERMINED by what is compiled in**, `--enable-nonfree` may not be distributed
    at all, and **the operational test is DISTRIBUTION, not "is it in the code base"** — a build-time
    fetch, a fixture vendoring a binary, or a release asset all leak it from a clean repo.

- **D52 — the media layer is a TRANSLATION LAYER FOR THE WEB, not a media toolkit: the MINIMUM
  transformation that makes a file playable in a webview, and never more.** (Owner: *"we're not remaking
  ffmpeg … if H.265 is not supported on the web we translate it."*)
  - **The scope test, narrow on purpose:** *would a normal file the user already has fail to play, and is
    this the least we can do about it?* D59 states it as a measurable DELTA — what the DEVICE decodes minus
    what its WEBVIEW accepts — because "make more formats play" has no end.
  - 🔴 **What actually breaks for ordinary video is not the picture** but the **container** (`.mkv` holding
    playable H.264) and the **soundtrack** (`AC-3`, `E-AC-3`, `DTS`). **That is why a remuxer is worth
    writing in managed code and a codec library is not** — H.265 needs no software codec anywhere, since
    hardware decodes HEVC and encodes H.264. Reach is D70; the licence bound is D51.
  - 🔴 **The delta is bounded by what .NET CAN REACH — the platform's own codecs AND anything an app
    supplies through the seams.** ⚠ Reading it as "only what the device already does" is drift and would
    refuse the case the seams exist for. Out of scope: the kit SHIPPING that library (D51), or capability
    the web already has (D54).

- **D53 — the media package is folded back into `Shenora`. Media repair is SHELL WORK, not an optional
  feature, and the package's own justification had become false.** (Owner: *"we are not making a video
  convertor library we are making a hybrid app development framework."*)
  - 🔴 **The reason is IDENTITY, not layering.** A separate media package **advertised the wrong thing**,
    making the kit look like a media library with a hybrid shell attached. **Package boundaries are a
    public statement about what a thing IS; when a boundary has to be justified by an argument, check
    whether it is saying something about the product you did not intend to say.**
  - **The premise it rested on can never come true.** D40 created the package because media "is not going
    to be small" — but **D51 then guaranteed the kit ships no engine byte, ever**. ⚠ The size argument was
    the weak one and was argued first; the owner was right to push back on it.
  - **The layering test it applied** — *is this shell work, or something only SOME apps do?* Every app that
    hosts a page can be handed a file it cannot play. ⚠ **That test no longer decides PACKAGING: D55
    replaced it** with "the framework is one whole". **What is NOT claimed:** that fewer packages is better.

- **D54 — THE THESIS: the differentiator against Capacitor and Electron is NATIVE .NET CAPABILITY, and the
  kit's job is the translation layer between what .NET can do and React cannot.** (Owner: *"its something
  that .net can do but react d[oes]nt. We build that translation layer."*)
  **The lens:** *.NET does the platform work · React does the interface · the kit owns the seam.*
  - 🔴 **So the question for a proposed feature is not "is this useful?" but "can React already do this?"**
    If it can, the kit is competing with the web platform and loses. Capacitor and Electron give you a
    webview and a JS bridge, so their ceiling is the web platform plus plugins; this kit's is .NET's.
  - **Applied to playback: the PAGE should not own it.** With a `<video src>` the ceiling is whatever the
    webview can do, and Now Playing then describes something the page is doing with nothing to reconcile a
    disagreement. `IMediaPlayer` is a lifecycle the host owns — **the same shape as `IFileDialogs`.**
  - ⚠ **This BOUNDS the translation layer; it does not delete it** — translation stops being the answer to
    *"the webview cannot play this"*.
  - 🔴 **Two conclusions here are superseded:** *"no default segmenter"* (D71/D75 ship one) and *"only a
    native player survives backgrounding"* — an iOS `<audio>` plays on. **It stands on the other legs.**

- **D55 — there is no "optional features" tier: the framework ships as ONE whole, so the file engine folds
  into `Shenora` too.** (Owner: *"the final framework is a whole, what we should support is bridge the
  both, react and .net."*)
  - **This is D53's identity argument applied where D53 declined to apply it.** A nuget.org listing of a
    media package plus a file package plus a compression package reads as a collection of single-domain
    libraries; the product is a hybrid app framework. **D53's "the next feature is judged on the same
    question" is hereby replaced**: it is judged on whether the framework is one whole.
  - 🔴 **The mechanism was FORCED, not chosen, and this is the part worth keeping.** "Different projects,
    one shipped package" is structurally impossible here: the edge runs `engine → Core`, so for
    `Shenora.nupkg` to carry the engine's dll, Core's csproj must reference it — a cycle. ⚠ **A dependency
    edge decides whether a "keep the projects, merge the package" plan is even available. Check the
    direction before promising it.**
  - **What the owner asked for survives as FOLDERS** under `src/Shenora/Engine/` (D65), so the D47 break
    is two `PackageReference` deletions AND a `using` sweep. ⚠ Re-check that claim after any restructure.

- **D56 — the deploy/update TOOLING is product, not devtools.** (Owner: *"the launcher, platform
  testing/deployment tools kind become more needed"*.)
  - **The competitive read that makes it obvious.** D54's differentiator is true about the RUNTIME, but
    Capacitor's moat is `npx cap sync` / `cap run ios` and Electron's is `electron-builder` plus the
    auto-updater. **An adopter meets the tooling before they meet the runtime**, and a framework whose
    deploy story is "write your own MSBuild" loses whatever its capability ceiling.
  - **It passes D54's own test cleanly.** *Can React already do this?* No — a React toolchain cannot mint a
    provisioning profile, sign an `.appex`, install to a connected iPhone, or apply a staged update over
    files the OS holds open. Same gap `IMediaPlayer` sits in, and **wider**: every adopter hits deployment.
  - ⚠ **This is a SCOPE claim, not a finished design**, since the harnesses assume THIS repo's layout; what
    an adopter's equivalent is — an MSBuild target set, a `dotnet` tool, a recipe — is not decided here.
  - 🔴 **A tooling defect IS a product defect** — the consequence to keep.

- **D57 — there are no PRE-IMPLEMENTATION design docs: a plan is scaffolding, and once the thing is built
  the third copy of its reasoning is the one that goes stale.** ⚠ **Scope narrowed by D77**, which adds
  AS-BUILT subsystem docs under `docs/design/` — a different animal.
  - **What triggered the audit:** `docs/README.md` called a design contract load-bearing because *"code
    cites its `§5`"* — **zero source files cited it.** The claim was written when it was true and nothing
    re-checked it: `doc-claims.md`'s exact defect class, in the router.
  - **Where the five went:** communication core → **D23**; app update → **D30**/**D31**/**D50**; mission
    scheduling → **D27**–**D31**; the rest is superseded by D54/D55. Git holds the documents.
  - 🔴 **What ONLY they held, kept because it is invariant rather than narrative:**
    - **A mission policy is consulted only about LEGAL moves, which is what makes it safe to expose** — an
      item has passed admission by then, so **it can only DELAY work, never corrupt it**.
    - **Why app updates are two phases:** a running process cannot replace its own executable on Windows,
      so the app verifies while alive and something that runs *before* it applies the result. Two siblings
      arrived at the same contract independently — **D15's bar met on evidence, not direction.**

- **D58 — the interceptor's media route is the PLAYER's output pipe, not a parallel feature. There is one
  media-play layer in .NET and the webview is one of its surfaces.** (Owner: *"the .net one is a proper
  player but using web as its display and sound"*.)
  - **What was wrong before it.** Serving handed bytes to an element the PAGE drove while the player was
    native, so every adopter wired probe → plan → URL by hand. **The join is `MediaPlayer`**: a media
    request at the interceptor is a question **.NET** answers, and the page renders what it is handed.
  - 🔴 **This is what makes a consumer's own converter reusable.** The URL the player resolves points at
    the conversion route, so the pipeline an app already extends serves the player too. **Nobody writes a
    second converter to get a player** — D53/D55's "one whole" applied inside a subsystem.
  - **Named `MediaPlayer`, NOT `WebMediaPlayer`.** A `Web` prefix frames rendering-through-the-page as a
    variant of some purer thing; in a hybrid framework it is the NORMAL case.
  - 🔴 **An `IMediaRenderTarget` seam was drafted and cut as over-engineering** — one implementation plus a
    fake, where `IMediaPlayer` was already the seam. **The page is the only clock:** position comes from
    `MediaPlayer.Report`, the element being what advances.

- **D59 — the converter's job, stated exactly: it bridges what the PIPELINE can decode — the device's
  hardware, plus whatever an adopter hooks in — to what that device's WEBVIEW will accept.** (Owner: *"if
  a better encoder/decoder comes in by adopter app, they can hook that into the same pipeline."*)
  - **This is sharper than D52's framing and supersedes how it was read.** "Make a file the webview cannot
    play, play" invites a treadmill of formats; the real target is a DELTA between two measurable things —
    `IMediaCapability` asks what can be decoded, `MediaPlaybackPolicy` says what the element accepts.
    **Where NOTHING can decode it there is nothing to bridge, and refusing is correct** (D51).
  - 🔴 **The DELTA moves when an adopter hooks a library in, and that is the design working.** ⚠ Reading it
    as "the device's hardware, full stop" is drift that would refuse the case the seam exists for.
  - 🔴 **The claim was FALSE when made, and the defect INVISIBLE:** the overload every adoption example
    wired passed `conversion: null`, so the remux dropped the soundtrack. The generalisation is D63.
  - **"Without additional code" is a claim about the PIPELINE:** what gets consulted is the
    `MediaStreamConversion` chain, last-registered-first, so one `Use(...)` serves the default converter,
    the segment engine and the player alike.

- **D60 — the kit ships NO page-diagnostic facade. The two-consumer signal is real and the generalisation
  is still not worth making.** The pattern stays documented in `docs/ADOPTION.md`; `PageDiagModule` stays
  sample-local. ⚠ **Renumbered from D51 (a duplicate)** — every `D51` citation means MIT-compatible bytes.
  - **The signal that made it a question.** Two repos independently built the same tiny facade for the same
    measured reason: **WebKit does not forward a page's `console.*` to the unified log.** That is normally
    the harvest bar (D15).
  - **What fails the bar is the SHAPE, not the count.** It is a `switch` with one case and a log call, and
    the parts that differ per app are the ones that matter — module name, log sink, whether page text is
    redacted. A kit version would hard-code those or take three delegates.
  - ⚠ **And a kit-shipped version would be a PRIVACY hazard the app cannot see:** it writes page-supplied
    text to the device log, so registering it by default makes a data-handling decision on a consumer's
    behalf. **A generic security-shaped helper is worse than nothing — the consumer stops thinking.**
  - **It is also a DEVELOPMENT workaround, not a product capability.** **Revisit trigger:** an adopter that
    cannot express what it needs over the existing IPC pipe — wanting ready-made twenty lines is not that.

- **D61 — ONE `Use…` call defaults everything the kit may choose on the app's behalf, and refuses anything
  that changes what the app is EXPOSED to.** (Owner: *"its okay as long as the adopter when using get
  similar treatment as UseMediaPlayer"*.)
  - ⚠ **This entry said a capability is "adopted through" that call, and D64 replaced that model:** the
    framework is ON BY DEFAULT and `Use…` CONFIGURES rather than enables. The DEFAULTING rule survives.
  - **The test for what may be defaulted is "does this change what the app is EXPOSED to?"** Journal and
    lock directories are the app's own storage, so `UseFileSystem()` defaults them; `AllowedRoots` is a
    containment boundary, so `UseMediaPlayer()` refuses to pick it. **The security line and the ergonomic
    line are the same line.**
  - **What the question actually was:** a proposed rename, where the real fix was ergonomic parity.
    **Once the entry point is a METHOD, the namespace stops being what an adopter reads.**
  - 🔴 **Every rename candidate collided when measured against the compiler**, because a namespace under
    `Shenora.` shadows a same-named TYPE for all other `Shenora.*` code — `Shenora.File` shadows
    `System.IO.File`. ⚠ **`IO` collided with nothing precisely BECAUSE it is not a common type name.**

- **D62 — the IPC pipe carries INTENT; BYTES go through the resource interceptor. So a binary IPC pipeline
  would not speed up media.** (Owner: *"why there is a bus send so we cannot really wire into the native
  web player?"*)
  - **What the bus costs in the player, counted rather than guessed:** SIX messages for an entire playback
    session, plus one report per element TRANSITION — not per frame, not per second.
  - 🔴 **The page's `<video>` IS the platform's native player.** WKWebView decodes through AVFoundation and
    Android's WebView through its own stack, so driving the element through this hook *is* wiring into the
    native player, via the DOM rather than around it. What the kit adds is what the element cannot do
    itself: probing, planning against a capability query, and pointing at a conversion.
  - **And the bytes were never on the IPC pipe** — serving answers through the resource interceptor, the
    platform's own binary range-capable path (D45). **A media file has never been base64'd through JSON.**
  - ⚠ **Where binary IPC WOULD earn its place:** a payload that is genuinely data rather than intent with
    no URL to fetch it from — a large structured result, a screenshot handed back, telemetry batches.
    **The bar is a MEASUREMENT**, and the interceptor is what to reach for before widening the envelope.

- **D63 — "declared but never consulted" is this repo's recurring defect, and it is INVISIBLE by
  construction. Every extension point must have a socket, and something must ASK.** (2026-08-07, after the
  third instance in two days. Owner: *"lets make this library properly"*.)
  - **The three, and what they had in common:** a remuxer overload that passed `conversion: null`, so AC-3
    films played SILENTLY (D59); `RestartManagerLockInspector` registered by nothing, so "who holds this
    file?" said *cannot tell* on the one platform that can tell; `IMediaContainerWriter` implemented and
    consumed by nothing, so a consumer's native muxer had nowhere to plug in.
    🔴 **None of them threw, logged, or failed a test.** A capability that is ABSENT rather than broken
    produces no signal at all — the degraded behaviour is indistinguishable from the intended one.
  - **The rule: an extension point is not done when the interface compiles. It is done when something
    ASKS for it.** Concretely — the kit resolves it from DI or takes it as a parameter, a default applies
    when it is absent, and **a test supplies a fake and asserts the fake was USED.** Not that it exists.
  - **The audit that finds them is a standing habit, not a task**, and both halves of the question live in
    `.claude/knowledge/standing-habits.md`.

- **D64 — the framework is ON BY DEFAULT: `Use…` CONFIGURES rather than enables, and the only per-platform
  call is the shell's, which exists to inject implementations.** (Owner: *"those `use` function basiclly
  just a way to override or configure"*.)
  🔴 **The core is THREE FIXED MESSAGE PIPELINES and everything else is an interceptor on one of them** —
  resources (BYTES), IPC (an ACTION) and events (the host telling the page). **That is what makes
  on-by-default safe: an interceptor nothing routes to is inert BY CONSTRUCTION.** The boundary that
  matters is CONTAINMENT (`AllowedRoots`), fail-closed. **The constraints:**
  - **Registration is free; construction is lazy. Nothing may touch a disk, a thread or a handle until
    something asks** — a capability that provisions directories at `Use…` time cannot be a default.
  - **A default must land on the instance RESOLVED FROM DI**, never one captured at `Use…` time, and **the
    app's callback runs FIRST**. Kit IPC modules take a RESERVED `SHENORA.` prefix.
  - **Where a platform CAN do it, IMPLEMENT it; where it cannot, refuse EXPLICITLY**, never by an absent
    registration. ⚠ The test is *can this platform do it?*, not *have we written it yet?*
    🔴 **`Use` touches a PIPELINE, `Add` only the container**; a CORE module is CONFIGURED, never added.

- **D65 — THREE LAYERS, the package is called `Shenora`, and "Core" means the WIRE between .NET and
  the web — nothing else.** (Owner: *"the Core is the main wire between .net and web …, on top of that is
  pure logic layer … and then what we call 'features'"*.) **Core is the CONTRACT · the engine is the BRAIN
  · modules BRIDGE .NET and the web.** 🔴 **The layer names ARE the namespace segments, so the layout
  cannot lie about the architecture.**
  - 🔴 **The membership test:** *must both sides AGREE on it?* → core. *Pure computation the page never
    sees?* → engine. *Carries a .NET capability to the page?* → module. ⚠ **An OPTIONAL collaborator is not
    a platform half:** the question is whether the thing NEEDS a platform, not whether one improves it.
  - **Core holds TWO KINDS of wire.** IPC and EventBus are **EXPLICIT** — page code has to ask. The route
    interceptor is **IMPLICIT**: the page does ordinary web things and .NET answers, needing **no page
    cooperation at all** — the highest-leverage wire the kit has, and why bytes were never on IPC (D62).
  - 🔴 **THE CATEGORY IS SETTLED WITH IT: Shenora is not a web application framework**, and **a package set
    is a statement about the category**. So IPC folds in and `Shenora.Core` becomes `Shenora`. ⚠ **Fold
    first, rename second, each with its own green gate** — a sweep on a fold makes failures unattributable.

- **D66 — a long-running request IS A REQUEST, so the "operation" — a second identity for one thing —
  collapsed into the IPC contract.** (Owner: *"the long run request still a request?"*.)
  - 🔴 **The defect the naming argument uncovered.** The former registry minted a `Guid` with NO
    relationship to the `IpcRequest.Id` that caused it, so **the page had to correlate the two itself.**
    One `XMLHttpRequest` carries `readyState`, `progress` and `abort()` instead.
  - ⚠ **The minority case that does NOT fold: work nobody asked for.** A scheduled or crash-recovered
    mission reports progress with no request behind it, and **is an event stream, which `IEventBus` already
    provides.** ⚠ **A naming problem that resists every candidate is a design smell, not a shortage.**
  - 🔴 **EVERY request can take a while, so a GRACE PERIOD replaces the declaration.** The 50 ms is
    `NotificationPumpOptions.FlushInterval`, so nothing is left for a module author to declare wrongly.
    ⚠ **Batching is not coalescing:** coalescing is keyed by REQUEST ID, last-write-wins.
  - 🔴 **THE RESPONSE IS NEVER DELAYED — the window suppresses NOTIFICATIONS, never the answer.** Parking
    the response inverts it, adding latency to every fast call to save a notification nobody would have
    seen. Safe by construction: `NotificationPump` has ZERO references to `IpcResponse`.

- **D67 — the DEVICE LOOP is part of the framework, so the kit ships a CLI: `@shenora/cli`, second npm
  package, binary `shenora`.** (Owner: *"the deploy to sim/iphone should be able to finish with cli"*.)
  - 🔴 **It does not contradict D53/D55, and the distinction is the whole entry.** "A capability gets a
    FOLDER, never a package id" is a rule about what ships **INSIDE the app**. A CLI ships inside nothing —
    a `devDependency` absent from every artifact the user installs. ⚠ **So the test for any future package
    is *"does an adopter's app carry this at run time?"*, not *"is it a separate id?"***
  - **Nor can it be a folder in `@shenora/react`, the shape that looks cheaper** — folding a
    `node:child_process` CLI into browser code puts Node built-ins in a graph headed for a bundler.
  - **Why it earns its keep against the kit's own thesis (D54):** *can React already do this?* No. And the
    ceiling argument cuts the other way for once: **Capacitor and Electron both ship a CLI.**
  - **The scope is the LAST MILE only** — a built app onto a simulator or a phone; it builds no bundles.
  - ⚠ **The config describes the PROJECT; the command line describes the MACHINE.** A machine-specific fix
    goes after `--`, never a committed field, which would silence the mismatch for everyone who clones.
    ⚠ And **THIS repo's harness is not the CLI's constraints**: signing needs a GUI login session.

- **D68 — the WebView2 RUNTIME choice belongs to the ADOPTING APP. The kit stays Evergreen by default and
  ships no browser bytes.** (Owner: *"ship a fixed version for webview2 should be decided by the adopted
  app not us"*.)
  - **The question arrived as one and was two.** The **user-data folder** is already app-local everywhere,
    so that half was a non-issue; only the **browser binaries** were ever in question. **Half of a reported
    problem not existing is worth recording, because the other half is then a NEW position rather than a
    harvest** (D15's bar).
  - 🔴 **Why it is the app's call.** A fixed-version bundle is ~150 MB per app and takes ownership of the
    security updates Evergreen handles. That trade depends on facts the kit cannot see. **The kit ships the
    SEAM and the default; it does not decide** — the same shape as D42 and D51.
  - **Nothing to build: the seam already exists.** `WebViewEnvironmentOptions.BrowserExecutableFolder` takes
    a fixed-version runtime folder and `null` — the default — means Evergreen. ⚠ **The kit must not grow a
    "bundle the runtime" feature later without reopening this**: it would charge every consumer 150 MB for
    one consumer's requirement.

- **D69 — the Live Activity is DATA the app builds in C# and a GENERIC kit widget READS at runtime. Raw
  Swift stays a first-class path, the normal Apple way.** (Owner: *"c# builds a config like code and swift
  part reads it … activity is not for a complex component"*.)
  - 🔴 **It is a RUNTIME CONFIG, not code generation, and that distinction is the decision.** Nothing is
    generated and the same compiled widget serves every app; SwiftUI still compiles at build time, so **the
    PRIMITIVES are fixed and their COMPOSITION is data**.
  - **The bounded model is the point** — a declarative schema covers most of a constrained surface.
    🔴 **THAT SETTLES THE D13 TENSION:** raw Swift stays first-class, so **the kit's look is a DEFAULT.**
  - ✅ **Proven on a device: one MSBuild property plus four SwiftUI view bodies, no `.xcodeproj` and no
    prebuilt `.appex` for the adopter**, and `ActivityAttributes` stays the app's type.
  - 🔴 **The real limitation belongs to the PLATFORM:** `ILiveActivities.Update` calls ActivityKit
    IN-PROCESS, so swiping the app away freezes the activity at its last value — **the card outlives the
    app, the update loop does not.** Advancing one while the app is gone needs ActivityKit PUSH, so APNs
    and a server: the adopter's infrastructure.

- **D70 — the kit SHIPS A DEFAULT CONVERSION ENGINE, and it is the platform's own codecs. `Convert` is the
  OVERRIDE, for work past the platform's reach.** (Owner: *"we can ship default conversion engine for each
  platform mainly focus on hardware support"*.)
  - **What it is.** `Convert` defaults to the kit's remuxer joined to the shell's `IMediaStreamConversion`.
    **No shipped codec bytes**, so D51 is untouched: wiring decoders the OS already has is D51's FIRST
    preference, not an engine.
  - 🔴 **THE BOUNDARY IS THE DESIGN, and it is D59's line: what the PIPELINE decodes and the WEBVIEW
    refuses, nothing wider** — which is what makes a DEFAULT defensible where a shipped engine would not
    be. ⚠ The boundary moves with the pipeline.
  - ⚠ **Setting both `Convert` and `Conversion` THROWS at registration** — two ways to say the same thing
    leaves one unread (D63). ⚠ **No desktop implementation exists**, so its default is container repair.
  - 🔴 **A DROPPED STREAM IS A FAILURE** — a user cannot tell "this film has no soundtrack" from "this
    device cannot play it". It fails with `UnsupportedCodec`. 🔴 **The two causes need opposite responses,
    so the log names which:** WITH a codec seam means unsupported here; with NONE nothing was ever asked.

- **D71 — STREAMING IS THE MEDIA TIER'S PRIMARY PATH, and the whole file is what streaming LEAVES
  BEHIND rather than the thing it produces.** (Owner: *"full transcode should be after if we got the full
  segment, its more like a cache/persist logic"*; *"1 planner no platform difference"*.)
  - **The inversion.** Materialising an ENTIRE output first makes the opening play wait for the whole
    transcode and a seek inexpressible, so it becomes the TAIL: **"all the segments" and "the finished
    file" are one state.**
  - 🔴 **THE PLANNER CHOOSES ON WHAT THE PRODUCER CAN PROMISE, NEVER ON THE PLATFORM.** `Remux` derives the
    output length AND the byte↔time map up front, so it is a COMPUTED file with no frontier to stall on;
    `Transcode` can promise neither, so it gets segments.
  - 🔴 **A design validated on ONE shell is not validated** — Android and iOS produced opposite failure
    modes from one design. **It SUPERSEDES "no default segment engine"**, which had made itself
    falsifiable: *"something must ASK first (D63)"*. Something did. **D51 is untouched.**
  - 🔴 **THE STREAMING CACHE AND THE OFFLINE ARTIFACT HAVE OPPOSITE POLICIES** — playback would evict a
    film someone waited for, so the route REFUSES a destination inside it (`docs/design/media.md`).

- **D72 — THE COMPUTED-REMUX ROUTE GETS NO PAGE-SIDE READINESS CONTRACT: the APP warms the plan in
  .NET and the page stays one plain `<video src>`.** (Owner, on being offered a readiness event: *"this
  sounds more like HLS now"*.)
  - **The question it closes.** A source nobody has planned answers `503 Retry-After: 1`, and **a `<video>`
    cannot ride that out** — it errors within ~70 ms and retries no sooner than 12 s.
  - 🔴 **THE REJECTED ANSWER IS THE INTERESTING ONE: a readiness event plus a page-side consumer.** It
    fits the kit's habits and forfeits the only thing this route has over HLS — **the plain-`<video src>`
    claim IS the differentiator**, and at the cost of an event MSE/HLS is strictly MORE capable.
  - ✅ **THE ANSWER: nothing tells the PAGE, because the APP already knows** — it built that URL. The warm-up
    is an ordinary .NET call and the page contract does not change. Blocking the first request is not
    available: both mobile shells resolve SYNCHRONOUSLY and a blocking wait deadlocks the iOS main thread.
  - ⚠ **`PlanAsync` MUST apply the request path's authorisation chain** — skipping it would walk any file
    the process can read, from app code that believed it was only hinting.
  - 🔴 **THIS DECISION IS FALSIFIABLE:** it assumes an app knows what it will play. **If not, go segments.**

- **D73 — MEDIA COMPOSITION FOLLOWS THE KIT'S OWN `Add`/`Use` SPLIT, because .NET already has this shape and
  a second idiom would be a thing to learn twice.** (Owner: *"lets do this properly a more .net fasion of
  styling of app build"*.)
  - **The rule is not invented here — it SHIPPED with D64's test:** ***`Use` means a wider configuration
    INCLUDING its pipeline; `Add` is the service-collection level only.*** So the media tier gets
    `services.AddShenoraMedia(...)` and the routes stay `Use…`.
  - **What the composition audit found:** four hazards that are silent rather than loud, led by route ORDER
    being load-bearing and unenforced. As-built in `docs/design/media.md`.
  - ✅ **THE DOC IS THE DELIVERABLE, NOT THE CODE** — `docs/guides/media.md` had covered `UseMediaConversion`
    and **not `UseComputedRemux` at all**, leaving the PRIMARY delivery path (D71) absent from the only page
    an adopter reads.
  - 🔴 **A `UseMedia` composite is NOT yet earned**, settled empirically by writing that doc. What it
    exposed is that **the ordering hazard cannot be fixed by prose** — the doc has to SAY "nothing enforces
    this", the shape of a defect waiting for a gate. **Prefer a test or an analyser over a hiding helper.**

- **D74 — ONE PATH FOR BOTH STREAM KINDS: the only difference is the encode/decode logic.** (Owner,
  2026-08-12, against a recommendation to close the video tier as good enough: *"we need a proper media
  pipeline not just something half working"*, then *"both audio and video should [take] the same path, [the
  only] difference is encoding and decoding logic"*.)
  - **The steer raised the BAR rather than the scope**, and it is why the tier has no per-kind types: one
    `IMediaStreamConversion` keyed by `MediaStreamKind`, one `MediaFrame` for a soundtrack and a picture, one
    chain where declining is how a converter opts out. **A kind is a VALUE, never a different shape** —
    branching on kind silently treated SUBTITLES as audio, and keying by kind removed the branch and the bug
    together.
  - ✅ Both halves are proven ON HARDWARE: Android converts `mpeg4` at 480x270 on an API 36 AVD, iOS converts
    `h263` at 352x288 on an iPhone 17 Pro. ⚠ **The assertion is the frame SIZE, never `readyState`**, because
    a wrong `avcC` still converts, still caches, still fires `Ready`, and then shows a blank rectangle.

- **D75 — THE SEGMENT TIER IS fMP4, THE GRID IS WHOLE SECONDS, AND THE DEFAULT ENGINE RUNS WHEREVER A
  CONVERTER IS REGISTERED.**
  ⚠ **The risk being managed:** the kit is guessing what a segment engine must promise with no adopter to
  correct it, so undecided details bias toward what an adopter can change.
  - **fMP4, not MPEG-TS, and the deciding reason is not compatibility** — `isTypeSupported('video/mp2t')`
    answers `true` on both shells and a MediaSource append failure is SILENT, so that claim is not trusted.
    What settled it is that **fMP4 makes `HasRenderedPicture` ANSWERABLE**: the `trun` states every sample's
    size, where MPEG-TS declares the stream in its PMT either way. **A container chosen for what it lets
    you CHECK.**
  - **A RE-ENCODED track is cut on whole seconds and a fractional grid is REFUSED rather than rounded** — a
    boundary with no keyframe plays and misbehaves only on a seek. ⚠ **A COPIED track has no grid** (D76).
  - 🔴 **The engine runs wherever an `IMediaStreamConversion` is REGISTERED — a registration test, not a
    platform one, and reading it as "mobile only" is the drift to avoid.** It is false on the desktop only
    because `Shenora.Windows` ships no converter. As-built: `docs/design/media.md`.

- **D76 — THE SEGMENT ENGINE COPIES WHAT MP4 CAN CARRY AND RE-ENCODES ONLY WHAT IT CANNOT; A COPIED TRACK IS
  CUT ON THE SOURCE'S OWN KEYFRAMES, SO THE BOUNDARIES TRAVEL AS A PLAN.**
  - 🔴 **Why: re-encoding everything produces NO VIDEO for almost any real file.** The platform video
    encoders offer h263/mpeg4/mpeg2video and never h264/hevc, so the intersection with what a webview
    decodes is EMPTY. **An H.264 or HEVC track needs no encoder at all**; only a stream MP4 cannot hold
    (AC-3, DTS, VP9) costs a codec.
  - **Copying beats converting wherever both are possible:** faster, lossless, cannot fail halfway, and it
    spares one of a phone's few hardware codecs. **ONE predicate answers it for both writers**
    (`Mp4Carriage`) — a second spelling is how the plan and the write disagree about one file.
  - 🔴 **What a copy costs is the GRID: copied frames keep the ORIGINAL encoder's keyframes**, and a
    segment not beginning on one cannot be decoded alone. So the boundaries travel as ONE `SegmentPlan`
    handed to BOTH the manifest and every run — **two derivations of the cuts fail silently**: the bytes
    stay valid and a seek arrives elsewhere.
  - **`IsAvailable` requires a conversion, so the engine is mobile-only** (D75); `docs/design/media.md`.

- **D77 — THREE HOMES, and this file holds only the first: a DECISION here, a subsystem's DESIGN under
  `docs/design/`, an invariant in `.claude/knowledge/`.** (Owner: *"decision has a lot rules in there which
  does not fit 100% all the cases but will be taken for each session which cause decision drift"*.)
  - 🔴 **A RULE HERE IS APPLIED TO EVERY CASE, because every session takes the whole file at once.** An
    invariant earned in one context reads as universal. **A knowledge rule is loaded only when the task
    matches its area**, so scope is enforced by the loader, not by the reader remembering.
  - **A subsystem's DESIGN is not a decision either** — its shape, needed before changing it and irrelevant
    otherwise. It was spread across sixteen entries and a third of this file.
  - ⚠ **This NARROWS D57 rather than reversing it.** D57 killed PRE-implementation plans; an AS-BUILT doc
    is written after, from the code. **The staleness D57 feared returns the moment a design doc argues
    instead of describes**, so it LINKS the `D<n>` for every claim that needs defending.
  - **The test:** *re-read when about to relitigate?* → here. *When about to change this subsystem?* →
    `docs/design/`. *Every time you touch the area?* → `.claude/knowledge/`. **Or better: a gate or test.**

- **D78 — FOR A REMOTE MEDIA SOURCE THE KIT SHIPS THE ADAPTER, NEVER THE TRANSPORT:**
  `MediaByteSource.ForRanges` turns an app-supplied byte-range fetch into the seekable, buffered stream the
  demuxer needs — the app owns the address, the auth and the retry policy; the kit owns the `Stream`.
  - 🔴 **The buffering is what had to ship, and it is not an optimisation.** EBML is parsed one `ReadByte`
    at a time, so the obvious adapter costs a round trip **per byte**; a `FileStream` buffers for free, so
    porting from `ForFile` gives no warning it is unusable.
  - **Why not the transport (D54).** An `HttpClient` brings auth, refresh, proxies, redirects, TLS and retry
    — the app's policy — and ranged HTTP is not "what .NET can do and React cannot".
  - ⚠ **The length must be known up front:** Matroska is read by offset from the END, so a source that
    cannot state its size cannot be indexed.
  - 🔴 **A server ignoring `Range` and answering `200` is refused by name** — otherwise silent, since it
    passes every length check while feeding the demuxer the file's START. The kit sees a `Stream`, not a
    status, so it checks a range past zero opening with the container's magic. `docs/design/media.md`.

- **D79 — THE SHELL RAISES THE BACK GESTURE AND THE PAGE DECIDES WHAT IT MEANS, PER PRESS:**
  `BackNavigation` correlates one press with one answer over a token, bounds the wait, and falls through to
  the platform whenever it is unsure. The shell cannot know that back should close an expanded player before
  it walks the page's history, so it does not guess.
  - **Why the kit owns it at all**, against D15's two-consumer bar: unhandled, Android's back FINISHES THE
    ACTIVITY from any screen, so a MAUI shell without this ships an app whose back button quits it — and
    the web platform has no answer, since `popstate` sees only history the page pushed itself.
  - 🔴 **Opt-in, because the failure is asymmetric.** A press the kit SWALLOWS is a dead back button nobody
    can diagnose from outside; a press that reaches the platform is at worst the old behaviour. So an app
    that never asked pays no round trip, an unanswered press falls through on a timeout, and a throwing
    page handler answers "not handled".
  - ⚠ **It is a NOTIFICATION plus a REQUEST, not one call, because the kit has no host→page request.** That
    is what the token is for: an answer to a press that already timed out must not be applied to the next
    one.

- **D80 — THE PLAYER'S SECOND SURFACE: ON A PHONE THE SHELL DRAWS THE PICTURE, AND THE PAGE KEEPS THE UI.**
  `IMediaSurface` takes the rectangle the page measured and the shell's own player fills it from
  underneath, through a transparent region the page leaves. D58 said the layer had surfaces plural and cut
  the seam for it as *"one implementation plus a fake"*; this is the second, device-proven in an adopter.
  - 🔴 **It refutes a premise, which is why it is a decision.** D42 said the kit's default is the
    translation layer *rendered by a page element*. On a phone that is measured false: the element refused
    ordinary films and the on-device re-encode built to compensate cost more than the decode it replaced.
    So D54's *can React already do this?* answers NO.
  - 🔴 **NO new planning verdict.** `MediaPlaybackPlanner` plans against `MediaPlaybackPolicy` — *what the
    app's player can open* — and never names the webview, so pointing it at the native player's policy
    already collapses the conversion cases. A `Native` action would restate the policy argument.
  - **The kit ships no engine** (D51/D42): the default is the platform's own player and the seam is
    `MediaPlayerBase.AttachSurfaceCore`. Its handle is `object` — `Shenora` is `net10.0` (D19/D20).
  - ⚠ **Mobile only** — Windows reports the capability absent rather than half-satisfying it (D39).

## Anti-goals — deliberately NOT built

Each was decided, not skipped. **Every public type earns its keep** (`generic-library.md`); package symmetry
is not a reason. Read this section before proposing any of it.

- **`Shenora.Media.Windows`** — owner: *"no need … for now"*, and D45 makes it less likely still. The desktop
  shell already serves ranges correctly two ways (`WebViewDeferredScheme`, and `WebViewHost.Interceptor` +
  `UseFiles` — the portable one). The package would hold a WebView2 args adapter and one constant, both of
  which `Shenora.Windows` owns outright. Add it only when a desktop consumer shows something it genuinely
  cannot express today (a native surface, an engine binding).
- **Thumbnails and image resize** — deferred with the analysis already done (D43). They cost 0 MB on every
  platform and need no engine, so they are cheap to add later, and the player does not depend on them.
- **Folder picking as a portable capability** — CLOSED, D35. Same word, different guarantee on each platform;
  documented as a DESKTOP capability, with the mobile refusal pointing at the three intents that ARE
  portable.
- **An FFmpeg bundle as a separate LGPL package** — asked 2026-08-09 (*"can we ship a bundle (seperate from
  this) and it will be a private repo … so that one can stay LGPL"*), **answered NOT YET by measurement.**
  Shenora is unaffected either way: it ships no engine, no binaries, no path option and no downloader, and
  **no adopter is blocked** — below D59's line the platform's own decoders do the work with no adopter code
  at all (D70), and above it they supply a converter, which is the seam D51 designed.
  - **The measured gap is ANDROID-ONLY, and it is four codecs.** From the sample's `[CODEC]` probe, where
    `repairable` is *the DEVICE can decode it* and `accepted` is *the kit's converter will take it*:

    | | ac3 | eac3 | dts | alac | vorbis · mp3 · flac |
    |---|---|---|---|---|---|
    | Android API 36 (`google_apis`, 2026-08-10) | ✗ | ✗ | ✗ | ✗ | ✓ |
    | iPhone 17 Pro (2026-08-07) | ✓ | ✓ | ? | ✓ | ✓ |

    The kit's converter declines exactly what the device cannot decode, so nothing over-promises.
  - 🔴 **The licensing geometry matches the gap, which is what settled it.** Android links a shared `.so`, so
    relinking — the LGPL's core requirement — is comfortable there. iOS static-links into the app binary,
    where relinking is awkward and store-signing makes it contested — **and iOS has no gap to fill.** Desktop
    has no conversion implementation at all, so there is no tier to extend. **The only defensible scope is
    Android alone**, far narrower than "an FFmpeg bundle".
  - **Why not yet:** four codecs on one platform, against a private repo, an ffmpeg build per Android ABI,
    and attribution + a written offer + a maintained relink path on every release, forever — with nobody
    having asked (D15). ⚠ **What reopens it is an ADOPTER, not an argument**: an app that must play an
    AC-3/DTS file on Android **offline** — the case the whole mobile media tier exists for. ⚠ `alac` is the
    one that could be closed PERMISSIVELY and needs no LGPL at all (Apple's ALAC reference is Apache-2.0,
    which D51's preference order already blesses). Also waiting on an ask.
- **Levelling AUTOPLAY between the shells** — CLOSED 2026-08-10, and there was nothing to level. Android
  refuses an UNMUTED `play()` without user activation and iOS does not, but **a real user is never blocked**:
  measured on WebView 133, the page's own button plays the clip unmuted from a genuine touch and from trusted
  CDP input. What fails is a SYNTHETIC `click()`, which grants no activation at all — **the harness was
  measuring itself.** ⚠ `MediaPlaybackRequiresUserGesture = false` remains a trap, and is also unnecessary.
- **Android's live-activity analogue** — for media it is already `IPlaybackSession`, and a progress
  notification means choosing icons and channels (D15/D13). It waits for a real non-media consumer.
- **Proving the Live Activity PUSH TOKEN path** — the seam is exercised and the token is not, and only money
  changes that. On an iPhone 17 Pro the `.token` request is ACCEPTED and no token follows
  (`SessionCore.PermissionsError Code=3`, then the documented fallback): the sample ships **no entitlements
  file**, so it has no `aps-environment`, and iOS issues no token to an app lacking the Push Notifications
  entitlement — which needs an App ID a free team cannot create. The honest claim until someone has a paid
  account is *"the seam is exercised, the token path is not"*.
- **A freshness horizon (`staleDate`) on a Live Activity** — REMOVED 2026-08-10 after being measured, and
  re-adding one is a product decision rather than a fix. `staleDate` marks content out of date for
  `context.isStale`; **it is not a repaint trigger**, and a horizon declares every activity stale that long
  after its last update — wrong for a status activity that legitimately does not change. Pinned by
  `The_shim_sets_no_staleDate_on_either_call_site`, which fails by file and line. ⚠ What would reopen it is a
  MEASUREMENT on HARDWARE: `dev.mjs mac island-watch` gives the simulator verdict mechanically (7 frames, 7
  distinct with no horizon), and a device has no `devicectl` screenshot, so that reading is a human's.
- **A page-side hook for the media-player DRIVE routes** — `@shenora/react` has `useMediaPlayer` for
  host-drives-the-element; driving the HOST's player is `invoke` by hand, three lines an adopter can already
  write. The ROUTES are the half they could not write themselves, and those ship. A hook lands when an
  adopter asks (D15).
- **A hand-written `docs/reference/` for types and methods** — a generated dump does not beat the IDE's view
  of the nupkg's XML docs, and a hand-written copy is the third-copy rot D57 retired five design docs over.
  **The WIRE is the exception and it SHIPS** (`docs/reference/wire.md`, generated, `verify` fails on drift):
  module names, route types, event types, error codes and capability names are strings a page types by hand
  across a language boundary where no IDE can help.

### Later / candidates — kept so the decision is not re-argued

Growth is harvest-driven (D15) and adoption-driven, so "next" is not a phase.

- **`Shenora.Hosting.AspNetCore`** (SPA static policy, loopback-gated endpoint helpers) — D10.
- **A mobile transport adapter** speaking the same IPC envelope — D16. The decision point is unchanged (first
  real mobile adoption), and the .NET-side surface such a shell would implement is enumerated rather than
  hypothetical: D20's portable contracts in `Shenora` (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`,
  `IUrlLauncher`, `IUiInteraction`). D16 covers the transport seam, D20 the feature seams; neither ships an
  implementation until there is a consumer.
- **Contract codegen (C# ⇄ TS)** — out of initial scope; revisit after adoption feedback.
- **Harvest-promotions from ongoing app development** (D15) — anything proven in a sibling gets generalised
  and lands here as a task before shipping in a minor.
