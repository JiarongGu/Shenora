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
| **D51** | anything the kit SHIPS AS BYTES must be MIT-compatible. |
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
  as the thing they need?** `LoginWindow` was renamed `InteractiveSession` because it held no login logic;
  `CoBrowseSession` became `StreamingSession` because an off-screen browser that streams frames and takes
  input is co-browse, remote support, visual capture or a preview pane depending on who wires it. Neither
  was a behaviour bug: a scenario name makes the kit LOOK like it ships that product, so the next
  contributor adds more of it and consumers with a different use case never find the primitive.
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
  🔴 **Every non-terminal state must have a sanctioned exit to a terminal one**, enforced by a test that
  ENUMERATES the state set rather than by reviewer attention — an emergent trap is invisible in any single
  guard's diff (`IpcRequestStateInvariantTests`). 🔴 **Progress is not percent**, and nothing is clamped:
  `Total = null` means no known denominator, never zero, and silently rewriting an app's reported number is
  worse than passing it through. 🔴 **The crash-checkpoint half was CUT before publish**, after ~8 reshapes
  inside one unpublished release that produced its only Critical — it came from ONE app, against the
  standing bar of two (`generic-library.md`).

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
  - **The argument is a measurement, not tidiness.** A path claim excludes two missions for their whole
    duration, but the expensive phase usually touches only a temp file — so under claims alone a
    seven-second compress waits on another mission's three-millisecond rename. **Compute in parallel,
    serialize only the landing.** The failure modes do not overlap either: a scheduler's are starvation and
    deadlock, an applier's are partial writes and locked targets.
  - **Atomicity is the app's choice per update** (owner: *"it depends what the application need"*):
    `PerChange`, or `AllOrNothing` via compensating rollback — which forces STAGED deletes, a delete being
    the one change that cannot be undone from nothing.
  - 🔴 **Crash-atomicity is opt-in via a write-ahead journal, and the ORDERING is the property:** the undo
    plan is durable BEFORE the mutation, since a plan written afterwards is missing exactly the change that
    got interrupted — which is why undo is DATA rather than closures. Recovery rolls BACK an update
    interrupted while applying and FINISHES one interrupted while committing; rolling the latter back would
    undo a success. The engine lives in the `Shenora.Engine.Files` namespace.

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
  **The evidence that the split is in the right place is its SIZE: ~200 lines**, because the substrate
  moved first — a fat shell would have meant something portable was still trapped in the Windows one.
  - **The bar stays D20's, not "it looks platform-neutral":** *can app logic compile off Windows?*
    Window geometry, tray, secondary windows and native drop zones stay in the Windows shell because
    they are desktop CONCEPTS — on mobile they are absent, not different.
  - ⚠ **Checked against the platform before designing, and it cancelled a proposal** — lifting the
    resource-serving layer for reuse died because `HybridWebView` served `Resources/Raw/wwwroot` itself with
    no seam to lift into. 🔴 **That is no longer the platform's answer:** `HybridWebView.WebResourceRequested`
    exists and `MobileWebViewInterceptor` subscribes to it, so mobile intercepts requests like every other
    shell (D45). **Do not cite this bullet to refuse interception work** — what survives is the METHOD
    (check the platform before designing), not the verdict, and a platform limit recorded as permanent
    outlives the platform.
  - **The platform-owned loop is why `Start`/`Stop` exist.** `IShenoraRunner.Run` is contractually
    "blocks until shutdown", which a MAUI activity cannot honour, so the mobile host registers no runner
    and the app drives the pair from its own lifecycle.

- **D33 — an ABSENT capability throws and names the platform; a SATISFIED one is an honest no-op.**
  `ShellCapability.NotSupported` is the one message. A silent no-op is the "mistyped resource prefix
  degrading to an all-404 provider" class this repo keeps paying for.
  **The distinction is load-bearing and was found by implementing it:** putting FILES on the clipboard has
  no expression on either mobile pasteboard → refuse; `IUiInteraction`'s block/unblock is satisfied BY the
  platform (mobile pickers are modal) → an honest documented no-op. Refusing the second kind would break
  portable logic that is behaving correctly. **"Absent" means no expression exists here, not "we did it
  differently".**
  🔴 **And there is a THIRD kind that reads exactly like the first: absent from the WRAPPER, present on the
  PLATFORM.** Clipboard IMAGES were refused here for years on the grounds that MAUI Essentials' clipboard is
  text-only — true, and not the question. `UIPasteboard` and Android's `ClipboardManager` both carry
  pictures; the kit was reporting its own binding's limit as the platform's. **Before writing
  `NotSupported`, check the PLATFORM API, not the convenience wrapper** — same failure the D32 bullet above
  already names, so it has now cost twice.
  ⚠ **NOT a `DispatchProxy`.** One reflection proxy throwing for any interface is the obvious
  implementation and would undo the iOS/AOT work in `IpcJson.AddTypeInfoResolver` — reflection is
  exactly what trimming strips. Each shell writes small explicit stubs sharing the one message.

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
  - ⚠ **The two arguments against merging both measured the easy thing instead of the relevant one:**
    "Sessions is 269 lines of SemVer surface" (it adds no dependency, and the types are maintained either
    way) and "WinForms-only consumers avoid 52.6 MB" (dev-time RESTORE size, not shipped bytes).
  - **The mobile packages share SOURCE, not an assembly** (`src/Shenora.Mobile/`, deliberately with no
    csproj) — a third assembly would either be published, carrying its own surface nobody asked for, or
    need embedding tricks to hide it. Divergence goes in each project's `Platforms/` folder, which the MAUI
    SDK includes per TFM, so it needs no `#if`. **Naming is by platform, not by framework**, since the two
    mobile faces share no web engine and `Shenora.iOS` never touches WebView2.

- **D38 — an off-screen session gets the app's own BUNDLE, and deliberately not its custom SCHEMES.**
  `SessionBrowserOptions` takes `VirtualHost` + `ResourceProvider` + `FolderMappings`, so a packaged
  desktop app can co-browse or off-screen-render its own frontend. It is the SAME two option names the
  host already uses, and the recipe is to pass the host's own values through — passing the same provider
  INSTANCE means the session's requests hit a cache the shell already warmed.
  - **Both halves or neither, refused at initialization** — either alone serves nothing, and the symptom
    would be identical to the bug being fixed.
  - 🔴 **The app's `RequestFilter` is consulted BEFORE the bundle, and both live in ONE
    `WebResourceRequested` handler.** A blocked request is a stated policy the kit must not override from a
    path the app cannot see; and two handlers each assigning `args.Response` is last-writer-wins by
    subscription order, **which is not a contract to rest a security boundary on.**
  - **NOT shipped, deliberately: a custom/deferred SCHEME inside a session** (WebView2 registers schemes
    only at ENVIRONMENT creation, a materially bigger surface nobody has needed), and **`SessionController`
    exposes no `CoreWebView2`** — handing out the raw browser object would make every future session
    capability an escape hatch instead of a seam.
  - ⚠ **Bundle responses carry `Access-Control-Allow-Origin: *`, and in a session the page can be ANY
    origin** — so script in a co-browsed third-party page could `fetch` the whole bundle. Stated plainly
    rather than special-cased: the header is load-bearing for a dev-mode page on another origin, gating on
    `core.Source` walks into the bug `ShouldBlockRequest`'s `pageUri` normalization exists to prevent, and
    the exposure is the app's own shipped frontend behind per-SESSION options.
  - **Why this was invisible for so long:** it only bites a desktop-only app serving an EMBEDDED bundle,
    and both sample demos plus the e2e run in dev mode. **A gap whose reproduction requires the packaged
    build is exactly what the "prove it against the sample" gate exists for.**

- **D39 — the auxiliary-SESSION stack stays a DESKTOP capability. Both mobile shells host a webview;
  that is not the same thing.** Owner: *"since on both mobile env we also have fake browser right? is
  that safe to do the same session logic?"* `StreamingSession`, `RenderSessionPool` and
  `InteractiveSession` do NOT port, and the reason is CAPABILITY — so the answer does not rest on a
  store-policy reading that could change.
  - **The stack rests on CDP, not on "a webview"** — screencast, device-metrics override, synthetic input.
    Neither mobile shell has an in-process CDP client. Android is Chromium underneath, but its DevTools
    endpoint is for an EXTERNAL client after enabling debugging: a security red flag to ship in release,
    and not an in-process API regardless. iOS is WebKit — no CDP, no public synthetic-input path.
    - ⚠ **THAT LAST SENTENCE IS AN UNRE-CHECKED PLATFORM READING, and D32 was wrong in exactly this
      shape** — it recorded "`HybridWebView` has no request interception" as permanent, the platform later
      gained the seam, and the entry went on refusing work the kit had already shipped. **Before citing
      this to refuse a port, re-check the platform**; the bullet below does not depend on it.
  - 🔴 **THE TRAP, and the real reason this needs writing down.** A port IS buildable behind the same
    interface — frame-polling plus `evaluateJavaScript` dispatching synthetic DOM events. It would
    compile, demo, and be materially WEAKER: polled instead of change-driven, with `isTrusted: false`
    events. **Untrusted events are precisely what fails on the pages `InteractiveSession` exists for**
    (verification challenges, auth flows). Same method name, different guarantee — D35's shape exactly,
    and tempting because *the C# ports for free, so every real cost lands elsewhere*.
  - **Store policy is a SECOND reason and is NOT verified here — do not cite this entry as if it were.**
  - **What the mobile answer IS, decomposed the way D35 decomposes "open a folder".** *Show the user a web
    page* → `IUrlLauncher`, already shipped. *Log the user into a third-party provider* → the platform
    auth session, which is BETTER: the cookies stay in the system and the app never sees them. *Render my
    own UI off-screen* → does not arise, since on mobile the app's UI already IS the webview.

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
  - ⚠ **This is the APP's choice, not a statement about the kit's own path.** The kit's default is the
    TRANSLATION LAYER — the platform's codecs, a container repair, segments (D59/D70/D71) — rendered by a
    page element (D54). Reading D42 as "the kit expects an engine" inverts that: an engine is what an app
    reaches for when the translation layer's reach is not enough, through the seams D51 keeps open.
  - **Why:** codec support is vendor-declared PER DEVICE, and containers are a second, worse axis —
    H.264-in-MKV can fail on Android while H.264 itself decodes perfectly. **Recorded as JUDGEMENT rather
    than dressed up as a measurement**, because this repo cannot verify it: the owner's field experience of
    *a platform player failing on roughly ONE THIRD of a real collection*.
  - 🔴 **The constraint: a playability verdict is PER STREAM, never one boolean for the file.** The AUDIO
    track is usually what fails while the video decodes perfectly, so `CanPlay(file) -> bool` would have
    been wrong in the commonest case — and it is why `remux` earns its place beside `transcode`.
  - **The kit references no engine package at all**; an app obtains one by REFERENCING UPSTREAM, never
    vendoring, and the LGPL/GPL choice stays the app's. ⚠ An engine is a SUPERSET of the platform player
    rather than a battery trade, LibVLC having MediaCodec and VideoToolbox backends.

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
  reached in five steps, and the order is the argument: *"the interceptor interface should live in the
  core"* → *"desktop will also have issue with access local folder/files so the interceptor is needed"* (not
  a mobile workaround) → *"even file access too"* (media is ONE CASE) → *"it's more like a middleware design
  if you think this way"*.
  - **Interception is a SHELL capability** — it configures a webview, so `IWebViewInterceptor` is a contract
    in `Shenora` and each shell implements it. **Every shell needs it, which is what makes it Core's
    business:** a page cannot reach a local file on ANY shell, so one contract means **path containment is
    written once instead of three times** — and a hand-rolled containment check is the exact defect this kit
    already had to fix (`%2e%2e%2f`, and `Path.Combine` discarding its first argument on a rooted path).
  - 🔴 **MIDDLEWARE, not a list of handlers, because the cross-cutting concerns are the point.**
    Containment, the SSRF guard, a cache, logging, a metric — each WRAPS the next rather than terminating,
    and a "first non-null wins" list cannot express any of them. **The kit already made this choice once:**
    `IMessageDispatcher` is a composable pipeline, so precedent and review instincts transfer.
  - **The registry and composition are in Core, not in each shell** (`WebViewResourcePipeline`) — writing
    the back-to-front chain three times is three chances to invert someone's routing, and it is the only
    way order, decline-and-fall-through and wrapping are TESTABLE with no webview. The shells then differ
    only in event glue: **mobile must resolve the pipeline SYNCHRONOUSLY while the desktop has a deferral
    and must not block the UI thread.**
  - ⚠ **Bundle-versus-pipeline order is OPPOSITE on the two shells**, which is why "keep interception
    paths off bundle paths" is an invariant rather than advice — it lives in `webview2-hosting.md`.
  - **The page's half is ONE npm package for every shell:** `mediaUrl(payload)` returns a RELATIVE
    `<route>?<base64url>`. ⚠ **The handshake advertises NEITHER the url scheme NOR the range delivery** — a
    page told "you are on iOS, use `app://`" is branching on platform again.

- **D46 — a capability that needs a newer PLATFORM TARGET is opt-in, never imposed. The consumer picks the
  target; the kit makes the consequence explicit.** Owner: *"so we let the consumer decide their target
  platform instead of force it?"*
  - **The case:** `IPlaybackSession` on the desktop needs `SystemMediaTransportControls`, which is WinRT,
    and the projections exist only when the TFM names a Windows SDK version. The first implementation simply
    raised `Shenora.Windows` to a versioned TFM, **making every Windows consumer retarget for a capability
    most will never call.**
  - **The rule: multi-target, and let the plain variant REFUSE BY NAME.** On `net10.0-windows` the type
    still exists and throws `ShellCapability.NotSupported` at construction with the one-line remedy in the
    message. Absent would be worse — resolving a missing service names neither the shell nor the reason.
  - **Why a build flag CANNOT do this:** a consumer's MSBuild property is evaluated long after the kit was
    compiled and packed, so all a csproj can do at that point is choose a lib folder. Content can only vary
    by TFM, by package, or by having no compile-time dependency at all.
  - ⚠ **Two hand-written variants of one type need TWO gates.** `ApiSurfaceTests` loads the assembly the
    test project references and so sees only one TFM; the plain variant has its own `MetadataSurfaceTests`
    entry. One type name differing only by TFM must expose the same members, or a consumer that retargets
    finds a different API.
  - **The generalisation:** the kit must not narrow a consumer's supported platforms as a side effect of one
    feature. If a capability needs a newer floor, the floor moves only for consumers who ask for it.
  - ⚠ **Related trap, and it cost a commit:** `TargetPlatformVersion` is what you may COMPILE against;
    `SupportedOSPlatformVersion`/`TargetPlatformMinVersion` is the floor you RUN on — **and leaving the
    latter unset silently defaults it to the former.** That is how bumping a TFM for one API quietly raises
    everyone's minimum OS. Pinned by matching on `-windows10.` rather than an exact TFM string, so a later
    bump cannot slip past the latch.

- **D47 — while ONE repo fully adopts the surface, prefer the CORRECT shape over the compatible one. Ship no
  compatibility aliases; rename when the name is the defect.** Owner: *"sonora actually is the first one
  fully adopting all features so you can fix anything into the best here which only cause 1 repo to
  update"*.
  **What changed is the PRICE of a break, not the rules about it.** A break against a known, single,
  same-author adopter is one repo's compile errors — found by the compiler, fixed by the person who asked
  for the change. That is a bounded, visible cost, not the unbounded one "published" usually implies.
  - 🔴 **THIS ENTRY EXPIRES, AND NOTHING NOTICES WHEN IT DOES.** Its licence rests on a fact about the
    WORLD — how many repos depend on the published surface — which no gate in this repo can read, and which
    the entry never asked anyone to re-check. **It is the second adopter that ends it**, because from then
    on a break costs somebody who did not ask for it, and every `### Breaking` entry in the unreleased
    window is currently written under this licence.
  - **So the trigger is stated rather than assumed: before the next release, confirm the adopter count.**
    One repo → this stands and the breaks stay cheap. Two or more → this is superseded and a deprecation
    path is owed, which is also the moment D49's pre-1.0 id retirement stops being deferrable.
    ⚠ **A deferral nobody wrote down gets rediscovered as a defect** — D49 says exactly this about itself,
    and it survived four releases before anyone re-raised it.

- **D48 — the file-operation engine is its own LAYER hanging off Core, not part of it.** Owner: *"because
  this include file operation so we should have a sperated library/package for this"*.
  ⚠ **Its PACKAGING conclusion is reversed — D55 removed the package, D65 made it the
  `Shenora.Engine.Files` namespace. Read the layering; ignore the package ids.** What this entry proved is
  not about packaging and is cited from fourteen places.
  - **The measurement that decided it:** the engine was 2,244 lines — **34 % of `Shenora`** — and all but
    ~500 of it is the update engine. Core is what *every other package references*, so a phone app that
    hosts a page and plays a file was carrying a self-updater it will never call. **This APPLIES D37's test
    rather than overturning it:** "I am on Windows" is not a choice you make per feature; **"I am building
    an app that mutates a file tree or self-updates" IS one.**
  - 🔴 **The edge points `engine → Core`, CHECKED rather than assumed**, and that direction decided the
    leftovers. `Files`/`FileReplacement` stay in Core because `IFileDialogs.SaveAsync`'s default calls
    `Files.BeginReplace`; `PathClaims` stays as scheduling vocabulary; `IFileLockInspector` was SPLIT BACK
    OUT because **a shell must be able to implement a Core contract without reaching outward for it**,
    while `IPathLocker` went the other way — advisory lock files are portable, so contract and
    implementation ship with the engine that uses them.
  - ⚠ **The strongest objection, and it is REJECTED: it holds TWO clusters that do not touch.**
    They stay together because the consumer story is ONE, and because **the cost of unused code inside
    something you CHOSE to add is close to nothing, unlike the same code in Core.** **The trigger to revisit
    is a real adopter that wants one and refuses the other**, not a call graph with two components.
  - ⚠ **The gate that did NOT catch the gap this created:** compression shipped with no `ARCHITECTURE.md`
    entry and every gate stayed green, because `doc-drift` checked dependency ARROWS and dangling links —
    never *"is this shipped package described anywhere"*. It now requires every packable project to be named
    in `README.md`'s table and in `ARCHITECTURE.md`, with a fence so the retired `Shenora.IO` could not be
    satisfied by `Shenora.iOS`.

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
  linux+ windows for future needs, and small)"*. Shipped in 0.10.0.
  - **Library + template is not a judgement call — the seam was MEASURED twice.** Two siblings, no contact,
    wrote the same three files: `updater.cpp` and `dotnet_runtime.cpp` are generic → the LIBRARY; `main.cpp`
    is per-app → the TEMPLATE, and **the library is the larger half.** What stays per-app is smaller than "a
    launcher": exe name, icon, version resources, signature, topology constants, failure-UI wording. **That
    asymmetry is a build step, not a source fork**, which is what makes one shared tree honest.
  - **Rust was evaluated and lost on the criterion the owner named** — whether it helps NuGet packing. It
    does not: a `.nupkg` is a zip and MSBuild's RID copy cannot tell what compiler produced the bytes. **D8
    then decides it:** two proven C++ implementations exist in production, so **the two-consumer bar is met
    in C++ specifically**, not for "a launcher" in the abstract. ⚠ **C++ was NOT chosen because Rust is
    worse** — decide a future native component with no C++ prior art on its own evidence.
  - 🔴 **The JSON parser is a CONFORMANCE requirement, not a taste one.** It must agree with
    `UpdateManifest.Parse`, including two rules the C# side already holds: **paths normalise
    separators AND case; hashes compare case-insensitively.** Getting either wrong makes a release look
    either fully changed or fully unchanged.
  - ⚠ **Calling it a library makes the verification problem sharper, not softer** — the kit owns its
    correctness while `dev.mjs verify` compiles none of it, which `README`/`ADOPTION` must say plainly. The
    answer is the Node harness driving a PREBUILT launcher over sandbox directories, so an adopter's CI runs
    THE KIT'S conformance suite against THEIR binary. **Bounded:** a self-contained app needs no launcher at
    all, so this serves framework-dependent apps, where the runtime may be absent and files held open.

- **D51 — anything the kit SHIPS AS BYTES must be MIT-compatible. The kit never redistributes a copyleft
  binary; an app that wants one supplies it through a `ResourcePack`.** Owner: *"we are on MIT so we should
  build one compatible with MIT"*.
  - **The asymmetry that forced it:** an LGPL ffmpeg is fine inside a closed-source app — dynamically link,
    attribute, keep relinking possible. Shipping the same binary from HERE makes the kit the
    **redistributor** and hands attribution and relinking duties to every consumer. **A package whose
    licence expression reads `MIT` while its payload is LGPL is a surprise an adopter's compliance review
    finds later, at the worst time.**
  - **The rule:** bytes the kit ships must be MIT / BSD / Apache-2.0 / ISC / public-domain. **GPL never** —
    x264/x265 and `--enable-gpl` relicense the consuming APP, the one outcome a devkit must never cause.
    Not LGPL binaries either: the licence is fine, **the redistribution is what is wrong**.
  - **What an engine should be, in order:** (1) **the PLATFORM's own codecs** — zero bytes, zero licence
    weight. ⚠ Measured limit: AOSP's set is narrow, *barely wider than the WebView's own*. (2) **Permissive
    libraries** where the platform genuinely lacks something — openh264, dav1d, libvpx, Opus, libFLAC, ALAC.
    (3) **Never** — x264/x265 (GPL), fdk-aac (not OSI-free), LAME (LGPL).
  - 🔴 **PATENTS ARE NOT COPYRIGHT, and a permissive licence does not settle them.** openh264 being BSD does
    not grant H.264 patent rights. "MIT-compatible" answers the licence question and leaves the patent one
    open — the owner's call per shipped codec, and this entry is not legal advice.
  - 🔴 **A closed-source app is NOT at risk from LGPL — that is the entire difference between LGPL and
    GPL**. **For the KIT it is not a preference
    but a requirement, because the kit REDISTRIBUTES.**
  - ⚠ **ffmpeg's licence is not chosen but DETERMINED by what is compiled in** — LGPL by default, GPL under
    `--enable-gpl`, and **non-free under `--enable-nonfree`, which may never be distributed at all.** The
    last is the trap: it is what someone reaches for to get fdk-aac, and the result passes every functional
    gate with nothing in the build output saying so. **The operational test is DISTRIBUTION, not "is it in
    the code base"** — a package that fetches it at build time, a fixture vendoring a binary, or a release
    asset all leak it from a clean repo.

- **D52 — the media layer is a TRANSLATION LAYER FOR THE WEB, not a media toolkit: the MINIMUM
  transformation that makes a file playable in a webview, and never more.** (Owner, 2026-08-06: *"what I'm
  building is a translation layer for web"* · *"we're not remaking ffmpeg, neither any complex
  encoder/decoder — our goal is to support web playback; it's like if H.265 is not supported on the web we
  translate it."*)
  - **The scope test, narrow on purpose:** *would a normal file the user already has fail to play, and is
    this the least we can do about it?* D59 states the target as a measurable DELTA — what the DEVICE can
    decode (`IMediaCapability`) minus what its WEBVIEW accepts (`MediaPlaybackPolicy`) — because "make more
    formats play" has no end.
  - 🔴 **What actually breaks for ordinary video is not the picture.** The video stream is nearly always
    H.264 or HEVC and hardware decodes both; the two real failures are the **container** (`.mkv`/`.avi`
    holding perfectly playable H.264) and the **soundtrack** (`AC-3`, `E-AC-3`, `DTS` — routine in MKV,
    playable in no browser). **That is why a remuxer is worth writing in managed code and a codec library
    is not.** The verdicts are `MediaPlaybackAction`; the reach is D70 and its licence bound is D51.
  - **H.265 is the flagship case precisely because it needs no software codec anywhere:** the device
    already decodes HEVC in hardware and already encodes H.264 in hardware, so the translation is two
    platform calls and a container — zero bytes, zero licence, no codec written.
  - 🔴 **The scope test is a translation between what the user HAS and what the web ACCEPTS, reached with
    what .NET CAN REACH — which is the platform's own codecs AND anything an app supplies through the
    seams.** ⚠ Reading it as "only what the device already does" is drift, and it would refuse the case the
    seams were built for: an app that brings a proper decoding library widens what its users can play, and
    the kit's job is unchanged — carry it to the page. What stays out of scope is the kit SHIPPING that
    library (D51) or growing capability the web already has (D54).
  - **The test for anything proposed here:** *does a React+C# app fail without it?* A React+C# app whose
    user's video will not play is a BROKEN APP, and repairing that is shell work — the same category as
    serving a local file or honouring a safe-area inset (D53). Reach is bounded by what can be TESTED
    (owner): the Android emulator, the Mac simulator, and a real iPhone. It widens when something real
    needs it, not in anticipation.
  - **No `IMediaProbe` seam, deliberately:** `MediaPlaybackPlanner.Plan` takes a `MediaProbeResult` the app
    supplies, so `MatroskaProbe` is a helper it may or may not call. An interface here would be shipping
    flexibility nobody asked for.

- **D53 — the media package is folded back into `Shenora`. Media repair is SHELL WORK, not an optional
  feature, and the package's own justification had become false.** (Owner, 2026-08-07: *"my previous
  description for the goal of this library is not that clear, thats why I removed the entire media library
  and move[d] into core, because we are not making a video convertor library we are making a hybrid app
  development framework."*)
  - 🔴 **The reason is IDENTITY, not layering.** A separate media package **advertised the wrong thing** —
    it made the kit look like a media library with a hybrid shell attached, when media is one capability
    among windows, dialogs, IPC and the rest. **Package boundaries are a public statement about what a
    thing IS; when a boundary has to be justified by an argument, check first whether it is saying
    something about the product you did not intend to say.**
  - **The premise it rested on can never come true.** D40 created the package because media "is not going
    to be small" — a demuxer or an image codec is real shipped bytes. **D51 then guaranteed the kit ships
    no engine byte, ever.** What exists is managed code the kit wrote itself: 98 KB of Release IL against
    Core's 125 KB, and iOS mandates trimming, so an app that never calls a media type does not carry it.
    ⚠ The size argument was the weak one and was argued first; the owner was right to push back on it.
  - **The layering test it applied** — *is this shell work, or something only SOME apps do?* Every app that
    hosts a page can be handed a file it cannot play, so media belongs beside `IWebViewInterceptor` and
    `WebViewFiles`. ⚠ **That test no longer decides PACKAGING: D55 replaced it** with "the framework is one
    whole" and folded the file engine in too.
  - **A documented BREAK (D47), and it costs TWO steps now, not one.** D53 itself was a pure move — the API
    baselines went `Shenora.Media.txt` −180 (deleted) and `Shenora.txt` **+180 / −0** — but D65 renamed the
    namespace to `Shenora.Modules.Media`, so an adopter changes a `PackageReference` **and** a `using`.
    Both the retired id and the retired namespace are registered in `devtools/retired-names.txt`.
  - **What is NOT claimed:** that fewer packages is better in general.

- **D54 — THE THESIS: the differentiator against Capacitor and Electron is NATIVE .NET CAPABILITY, and the
  kit's job is the translation layer between what .NET can do and React cannot.** (Owner, 2026-08-07:
  *"this differenti[ates] our platform compared to capacitor or electron — native .net capability. And what
  we [are] mostly trying to solve is not a media convertor, its something that .net can do but react
  d[oes]nt. We build that translation layer."* · *"think we are building a cross platform application
  framework mainly in .net + react"*.)
  **The lens, in one line:** *.NET does the platform work · React does the interface · the kit owns the
  seam between them* — a portable contract, one implementation per shell, and the IPC that carries it.
  - 🔴 **So the question for a proposed feature is not "is this useful?" but "can React already do this?"**
    If it can, the kit is competing with the web platform and loses. Capacitor and Electron give you a
    webview and a JS bridge, so their ceiling is the web platform plus a plugin ecosystem; this kit's
    ceiling is .NET's — the whole BCL, real threads, real handles, the platform SDKs. **Anything a plugin
    ecosystem already does well is not where the value is.**
  - **Applied to playback: the PAGE should not own it.** With a `<video src>` in the React tree the ceiling
    is whatever the webview can do, and Now Playing then describes something the page is doing, so the two
    can disagree with nothing to reconcile them. `IMediaPlayer` is the answer — a lifecycle the host owns
    and the page drives. **Read that way the player is not a media feature at all**, but the same shape as
    `IFileDialogs` and the safe-area insets.
  - ⚠ **This BOUNDS the translation layer; it does not delete it.** `Mp4Remuxer` and the conversion pipeline
    stay, because serving files to a `<video>` is a legitimate and common shape. What changes is that
    translation stops being the answer to *"the webview cannot play this"*.
  - 🔴 **Two of this entry's own conclusions are superseded and must not be cited.** *"No default
    segmenter"* — D71 made streaming the tier's primary path and the kit ships one (D75). *"Only a native
    player survives backgrounding"* — iOS pauses a `<video>` when the app leaves the foreground but an
    `<audio>` keeps playing, given `UIBackgroundModes: [audio]` and an active `AVAudioSession`. **The
    decision stands on its other two legs**, the system surfaces and the formats.

- **D55 — there is no "optional features" tier: the framework ships as ONE whole, so the file engine folds
  into `Shenora` too.** (Owner, 2026-08-07: *"we can have different projects for clearer namespacing and
  easier for testing, but the final framework is a whole, what we should support is bridge the both, react
  and .net and support for other consumer to implement things in .net to complete their goal."*)
  - **This is D53's identity argument applied where D53 declined to apply it.** A nuget.org listing of a
    media package plus a file package plus a compression package reads as a collection of single-domain
    libraries; the product is a hybrid app framework. **D53's "the next feature is judged on the same
    question" is hereby replaced**: it is judged on whether the framework is one whole.
  - 🔴 **The mechanism was FORCED, not chosen, and this is the part worth keeping.** "Different projects,
    one shipped package" was tried first and is structurally impossible here: D48 established, by checking,
    that the edge runs `engine → Core`, because every type in the engine logs through Core's `AppCallback`.
    For `Shenora.nupkg` to carry the engine's dll, Core's csproj must reference it — a cycle. ⚠ **A
    dependency edge decides whether a "keep the projects, merge the package" plan is even available. Check
    the direction before promising it.**
  - **Proven a pure move by measurement**, because "nothing changed but the location" deserves checking:
    `Shenora.txt` went **+243 / −0**, the two deleted baselines were 206 + 37 = 243 lines, and `comm -23`
    against their union was empty. Nothing was invented, renamed or dropped.
  - **What the owner asked for survives as FOLDERS**, though not the ones named when this was written: D65
    relayered them to `src/Shenora/Engine/Files/` (`Shenora.Engine.Files`), and the pre-1.0 surface pass
    then split the rest into `src/Shenora/Engine/Update/` (`Shenora.Engine.Update`) and
    `src/Shenora/Engine/Compression/` (`Shenora.Engine.Compression`) — extraction is not update-specific,
    and neither belongs under `Modules/`, which means a capability carried to the PAGE (D65). **So the D47 break is two `PackageReference` deletions AND a
    `using` sweep**, as in D53. ⚠ **A migration-cost claim is the first thing a later restructure
    invalidates, and it is the one an adopter acts on** — re-read it after every restructure.

- **D56 — the deploy/update TOOLING is product, not devtools.** (Owner, 2026-08-07, on being shown the
  package set: *"from this scope, the launcher, platform testing/deployment tools kind become more
  needed"*.)
  - **The competitive read that makes it obvious.** D54's differentiator is true about the RUNTIME, but it
    is not the whole of what Capacitor and Electron sell: Capacitor's moat is `npx cap sync` / `cap run
    ios`, Electron's is `electron-builder` plus the auto-updater and signing. **An adopter meets the
    tooling before they meet the runtime**, and a framework whose deploy story is "read our docs and write
    your own MSBuild" loses regardless of which has the better capability ceiling.
  - **It passes D54's own test cleanly.** *Can React already do this?* No — a React toolchain cannot mint a
    provisioning profile, sign an `.appex`, install to a connected iPhone, or apply a staged update over
    files the OS is holding open. That is the same gap `IMediaPlayer` sits in, and it is **wider**: every
    adopter hits deployment, only some hit media.
  - **What it reclassifies:** `dev.mjs mac device|provision|device-log|appex-check` and `dev.mjs android`
    are the `cap run ios` equivalent owed to adopters; `Shenora.Launcher` is the app-lifecycle piece
    Electron ships as a core feature, not a niche extra; `devtools/ios-provision/` is the answer to "how do
    I sign without hand-rolling Xcode".
  - ⚠ **This is a SCOPE claim, not a finished design.** The hard part is that these assume THIS repo's
    layout (`devtools/project.config.mjs`, a reachable Mac, paths under `local/`); what an adopter's
    equivalent is — an MSBuild target set, a `dotnet` tool, or a documented recipe — is not decided here.
  - 🔴 **A tooling defect IS a product defect** — that is the consequence to keep, and it applies to the
    device harnesses and the Live Activity devkit alike.

- **D57 — there are no PRE-IMPLEMENTATION design docs: a plan is scaffolding, and once the thing is built
  the third copy of its reasoning is the one that goes stale.** (2026-08-07, applying the 0.2.0 cleanup's
  precedent to the five dated docs that outlived it.) ⚠ **Scope narrowed by D77**, which adds AS-BUILT
  subsystem docs under `docs/design/` — a different animal, and the distinction is D77's whole first bullet.
  - **What triggered the audit rather than the calendar:** `docs/README.md` called a design contract
    load-bearing because *"code cites its `§5`"* — **zero source files cited it.** The claim was written
    when it was true and nothing re-checked it: `doc-claims.md`'s exact defect class, in the router, about
    the doc the router called the design contract.
  - **Where the five went:** the design contract's package set and "desktop body" framing are superseded
    (D54/D55, and `CLAUDE.md` carries the identity); communication core → **D23**; app update →
    **D30**/**D31**/**D50**; mission scheduling → **D27**–**D31**; the mobile-offline plan was an
    assessment, not a queue. Six code citations were repointed at D-entries first; git holds the documents.
  - 🔴 **What ONLY they held, kept because it is invariant rather than narrative:**
    - **A mission policy is consulted only about LEGAL moves, and that is what makes it safe to expose** —
      by the time `IMissionPolicy.ShouldStart`/`Compare` sees an item it has passed admission, so **the
      worst a buggy policy can do is DELAY work; it can never corrupt it.** The consequence for a policy
      deferring on an external condition is `IMissionScheduler.Reevaluate` — read `MissionPolicy.cs`, which
      now owns this in its XML docs.
    - **Why app updates are two phases, in one sentence:** a running process cannot replace its own
      executable on Windows, so the app downloads and verifies while alive and something that runs *before*
      it applies the result. Two siblings built this independently and arrived at the same three-file
      launcher, the same `staged/` + `ready.json` contract and the same `{path, size, sha256}` manifest
      triple — **D15's two-consumer bar met on evidence, not direction.**
    - **The offline-mobile blocker is on the ADOPTER's side:** transport coupling in the app, not anything
      missing from the shell. Nothing to build here until an app is actually decoupled.

- **D58 — the interceptor's media route is the PLAYER's output pipe, not a parallel feature. There is one
  media-play layer in .NET and the webview is one of its surfaces.** (Owner, 2026-08-07: *"everything from
  the interceptor for media, is actually saying we going to .net right?"* — yes, and *"the .net one is a
  proper player but using web as its display and sound"*.)
  - **What was wrong before it.** Serving handed bytes to an element the PAGE drove while the player was
    native; they shared a namespace and nothing else. Every adopter wired probe → plan → URL by hand and
    got a different answer. **The join is `MediaPlayer`**, which owns that chain: a media request arriving
    at the interceptor is a question **.NET** answers — the file as-is, a remux, a transcode, a segment
    window — and the page never decides anything about format. It renders what it is handed.
  - 🔴 **This is what makes a consumer's own converter reusable.** The URL the player resolves points at
    the conversion route, so the pipeline an app already extends (`MediaConversionOptions.Convert`,
    `IMediaStreamConversion`, `IMediaContainerWriter`) serves the player too. **Nobody writes a second
    converter to get a player** — D53/D55's "one whole" applied inside a subsystem.
  - **Named `MediaPlayer`, NOT `WebMediaPlayer`** (owner: *"you can just call it MediaPlayer, since the
    hybrid is our feature"*). A `Web` prefix frames rendering-through-the-page as a variant of some purer
    thing; in a hybrid framework it is the NORMAL case and the native player is the special one.
  - 🔴 **A first draft added an `IMediaRenderTarget` seam and it was over-engineering** (owner: *"isn't
    that should be just the web? I think we have bit over engineered this."*) — one production
    implementation plus a test fake, where `IMediaPlayer` was already the seam. The generalised test is in
    `generic-library.md`.
  - **The page is the only clock:** position and duration come from `MediaPlayer.Report` and nowhere else,
    because the element is the thing actually advancing.

- **D59 — the converter's job, stated exactly: it bridges what the PIPELINE can decode — the device's
  hardware, plus whatever an adopter hooks in — to what that device's WEBVIEW will accept. Nothing wider.** (Owner, 2026-08-07: *"the default convertor is
  actually bridging the gap between the device hardware to its webview, and if a better encoder/decoder
  comes in by adopter app, they can hook that into the same pipeline without additional code."*)
  - **This is sharper than D52's framing and supersedes how it was read.** "Make a file the webview cannot
    play, play" invites a treadmill of formats; the real target is a DELTA between two measurable things
    already in the code — `IMediaCapability` asks what can be decoded, `MediaPlaybackPolicy` says what the
    element accepts. **Where NOTHING in the pipeline can decode it there is nothing to bridge, and refusing
    is correct** — which is also why the kit ships no engine (D51).
    - 🔴 **The DELTA moves when an adopter hooks a library in, and that is the design working rather than an
      exception to it.** ⚠ Reading this as "the device's hardware, full stop" is drift — it was half of the
      quote above, and it would refuse the case `IMediaStreamConversion` exists for.
  - 🔴 **The claim was FALSE when it was made, and the defect was INVISIBLE:** the overload every adoption
    example wired passed `conversion: null`, so a registered conversion was never called and the remux
    simply dropped the soundtrack — a film that played SILENTLY. The generalisation is D63.
  - **"Without additional code" is a claim about the PIPELINE, and it holds.** What gets consulted is the
    `MediaStreamConversion` middleware chain, last-registered-first — not any one implementation — so an
    adopter with a better decoder calls `Use(...)` and it serves the default converter, the segment engine
    and the player alike. Pinned by
    `ToConverter_accepts_a_pipeline_so_a_registered_converter_is_consulted`.

- **D60 — the kit ships NO page-diagnostic facade. The two-consumer signal is real and the generalisation
  is still not worth making.** (2026-08-05.) The pattern stays documented in `docs/ADOPTION.md`;
  `PageDiagModule` stays sample-local in `samples/Shenora.Sample.Maui/`.
  ⚠ **Renumbered from D51 (a duplicate) on 2026-08-07** — every existing `D51` citation means the
  MIT-compatible-bytes entry, which kept the number.
  - **The signal that made it a question.** Two repos independently built the same tiny facade for the same
    measured reason: **WebKit does not forward a page's `console.*` to the unified log**, and a screenshot
    cannot report a number, a header or an array. That is normally the harvest bar (D15).
  - **What fails the bar is the SHAPE, not the count.** It is a `switch` with one case and a log call, and
    the parts that differ per app are the parts that matter — the module name, the log sink, and whether
    page text is redacted. A kit version would hard-code those or take three delegates, at which point the
    adopter has written more configuration than the twenty lines it replaces.
  - ⚠ **And a kit-shipped version would be a PRIVACY hazard the app cannot see:** it writes page-supplied
    text to the device log, readable by anything with log access. Registered by default, that is the kit
    making a data-handling decision on a consumer's behalf — the same reasoning that killed D10's
    loopback-gate helper. **A generic security-shaped helper is worse than shipping nothing, because the
    consumer stops thinking about it.**
  - **It is also a DEVELOPMENT workaround, not a product capability** — it gets *less* useful the moment
    the platform fixes its logging. **Revisit trigger:** an adopter that cannot express what it needs over
    the existing IPC pipe. Wanting a ready-made twenty lines is not that.

- **D61 — ONE `Use…` call defaults everything the kit may choose on the app's behalf, and refuses anything
  that changes what the app is EXPOSED to.** (Owner, 2026-08-07: *"its okay as long as the adopter when
  using get similar treatment as UseMediaPlayer"*.)
  - ⚠ **This entry said a capability is "adopted through" that call, and D64 replaced that model:** the
    framework is ON BY DEFAULT and `Use…` CONFIGURES rather than enables. What survives — and what the
    entry is for — is the DEFAULTING rule below, which is unaffected by when the capability turns on.
  - **The test for what may be defaulted is "does this change what the app is EXPOSED to?"** Journal and
    lock directories are the app's own storage, so `UseFileSystem()` defaults them; `AllowedRoots`
    (`MediaAccessOptions`, reached through `MediaPlayerOptions.Access`) is a containment boundary, so
    `UseMediaPlayer()` refuses to pick it. **The security line and the ergonomic line are the same line.**
  - **What the question actually was.** `IO` had been a package-family name and D55 made it a folder, so it
    looked like a leftover; the proposed fix was a rename and the real fix was ergonomic parity —
    `builder.UseFileSystem()` beside `builder.UseMediaPlayer()`. **Once the entry point is a METHOD, the
    namespace appears in exactly one `using` line and stops being what an adopter reads.**
    ⚠ The naming outcome ("`Shenora.IO` keeps its name") did not survive: D65 made the LAYER the namespace,
    so it is `Shenora.Engine.Files` today. The ergonomic rule is what this entry is for.
  - 🔴 **Every rename candidate was measured against the compiler and every one collided**, because a
    namespace under `Shenora.` shadows a same-named TYPE for all other `Shenora.*` code (enclosing-namespace
    members beat `using`-imported types): `Shenora.File` shadows `System.IO.File`, `Shenora.FileSystem`
    shadows MAUI's, `Shenora.Files` shadows the kit's own `Files` class. ⚠ **`IO` collided with nothing
    precisely BECAUSE it is not a common type name** — the obscurity that made it look like a leftover was
    the property that made it safe.

- **D62 — the IPC pipe carries INTENT; BYTES go through the resource interceptor. So a binary IPC pipeline
  would not speed up media.** (Owner, 2026-08-07: *"to improve the performance of the platform, we might
  also need to introduce binary pipeline for ipc"*, and *"why there is a bus send so we cannot really wire
  into the native web player?"*)
  - **What the bus costs in the player, counted rather than guessed:** SIX messages for an entire playback
    session — load, play, pause, seek, rate, unload — plus one report per element TRANSITION. Not per frame,
    not per second. A JSON hop for six control messages is not a performance problem.
  - 🔴 **The page's `<video>` IS the platform's native player.** WKWebView decodes through AVFoundation and
    Android's WebView through the platform's own stack, so driving the element through this hook *is* wiring
    into the native player — via the DOM rather than around it. What the kit adds is the part the element
    cannot do for itself: probing, planning against a device capability query, and pointing at a conversion.
  - **And the bytes were never on the IPC pipe.** `UseFiles`/`UseMediaConversion`/`UseSegmentStream` answer
    through the resource interceptor — the platform's own binary, range-capable path — which is why D45 put
    serving there. **A media file has never been base64'd through a JSON envelope.**
  - ⚠ **Where binary IPC WOULD earn its place, stated so the idea is not lost:** a payload that is genuinely
    data rather than intent and has no URL to be fetched from — a large structured result, a screenshot
    handed back from the page, telemetry batches. **The bar is a MEASUREMENT**, not a plausible story, and
    the interceptor is the first thing to reach for before widening the envelope.

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
  - **The audit that finds them is a standing habit, not a task** — two greps, its narrow signal, and the
    second half of the question (does anything IMPLEMENT what the kit PROMISES?) are in
    `.claude/knowledge/standing-habits.md`.

- **D64 — the framework is ON BY DEFAULT: `Use…` CONFIGURES rather than enables, and the only
  per-platform call is the shell's, which exists to inject implementations.** (Owner, 2026-08-07: *"this is a full
  react+.net application framework … those `use` function basiclly just a way to override or configure"* ·
  *"because non-of them will work without frontend ask via ipc/routing"*.)

  🔴 **The core is THREE FIXED MESSAGE PIPELINES and everything else is an interceptor on one of them** —
  routes/resources (`IWebViewInterceptor`, the page asking for BYTES), IPC (`IMessageDispatcher`, the page
  asking for an ACTION) and events (`IEventBus`, the host telling the page, D23). **That is what makes
  on-by-default safe: an interceptor nothing routes to is inert BY CONSTRUCTION**, so opt-in gating would
  buy nothing and only guarantee every app re-types the same block. The boundary that matters is
  CONTAINMENT (`AllowedRoots`), which stays fail-closed.

  **The constraints this imposes:**
  - **Registration is free; construction is lazy. Nothing may touch a disk, a thread or a handle until
    something asks** — a capability that provisions its directories at `Use…` time cannot be a default.
  - **A default must land on the instance RESOLVED FROM DI**, never one captured at `Use…` time, or a
    `TryAdd` that no-ops leaves the kit configuring an object nothing reads. **The app's callback runs
    FIRST**, buying a single registration rather than a kit default shadowed behind one.
  - **Kit IPC modules live under a RESERVED `SHENORA.` prefix**, so the app is free to own `MEDIA`, `FILES`
    or any unqualified name.
  - **Where a platform CAN do it, IMPLEMENT it; where it genuinely cannot, refuse EXPLICITLY**
    (`ShellCapability.NotSupported`), never by an absent registration. ⚠ The test is *can this platform do
    it?*, not *have we written it yet?* — recording a TASK as a refusal freezes a gap into the surface.
  - 🔴 **`Use` vs `Add`: does the method touch a PIPELINE, or only the container?** (Owner: ***"`Use` means
    a wider configuration including its pipeline interceptor, and `Add` only means the service collection
    level."***) **A module REGISTRATION is not a pipeline stage**, even though a stage is built from it, and
    a receiver follows the capability's real dependency — builder-level only when it needs `builder.Paths`.
  - **A CORE module is CONFIGURED by the app's setup, never added to it:** `Build()` registers it
    unconditionally, so exposing an "add" offers a choice that does not exist.
  - **A default is also a promise that the composition can be TORN DOWN** (invariant: `generic-library.md`).

- **D65 — THREE LAYERS, the package is called `Shenora`, and "Core" means the WIRE between .NET and
  the web — nothing else.** (Owner, 2026-08-07, redefining it after D64 exposed that the word had no edge:
  *"the Core is the main wire between .net and web …, on top of that is pure logic layer, like Mission,
  Files, and then we have what we call 'features' Media, Dialog"*.)

  **In one line each** (owner's own framing): **core is the CONTRACT · the engine is the BRAIN · modules
  BRIDGE the gap between .NET and the web.** 🔴 **The layer names ARE the namespace segments, so the layout
  cannot lie about the architecture:** `Shenora` is the composition root, `Core/` holds the contract
  (`Shenora.Core.Ipc` · `.Events` · `.WebView` · `.Shell`), `Engine/` the brain (`.Missions` · `.Files`) and
  `Modules/` the bridges (`.Media` · `.FileDialog` · `.Platform` · `.Requests` · `.Update`).
  - 🔴 **The membership test:** *must both sides AGREE on it?* → core. *Is it pure computation the page never
    sees?* → engine. *Does it carry a .NET capability to the page?* → module. **Read mechanically, that is
    "platform half and/or IPC surface"** — which moves Media out of the engine and keeps Missions in. ⚠ **An
    OPTIONAL collaborator is not a platform half:** the question is whether the thing NEEDS a platform to
    function, not whether a platform can improve it.
  - **Core holds TWO KINDS of wire, which is why there are three members rather than two.** IPC and EventBus
    are **EXPLICIT** — page code has to ask. The route interceptor is **IMPLICIT**: the page does ordinary
    web things (`<video src>`, `fetch`) and .NET answers. **It needs no page cooperation at all**, which
    makes it the highest-leverage wire the kit has — and is why bytes were never on the IPC pipe (D62).
  - 🔴 **AND THE CATEGORY IS SETTLED WITH IT: Shenora is not a web application framework** (owner: *"its
    desktop + mobile just like other multiple platform app framework but its .net + react"*). It competes
    with Flutter, React Native, MAUI, Electron and Capacitor, and **a package set is a statement about the
    category** — which is why the old separate-IPC justification had to go: it quietly imagined a WEB
    consumer, and building for one drags the product toward a category it was never in.
  - **So IPC folds in and `Shenora.Core` becomes `Shenora`** — it is the framework, not a component of one.
    🔴 **The fold unblocks everything else:** a feature could not own its IPC module while `ModuleBase` lived
    somewhere Core may not reference. ⚠ **Fold first, rename second, each with its own green gate** — a
    namespace sweep landed on top of a package fold makes any failure unattributable.
  - **The layering was already in the CODE; only the names failed to say it** — the media files depend on
    missions and files and nothing goes the other way, found by reading the edges rather than by design.

- **D66 — a long-running request IS A REQUEST, so the "operation" — a second identity for one thing —
  collapsed into the IPC contract.** (Owner, 2026-08-08, after rejecting every replacement NAME: *"maybe
  just IpcRequest? so we sharp the original request properly to have this logic into it"* · *"because the
  long run request still a request?"*.)
  - 🔴 **The defect the naming argument uncovered, measured rather than argued.** The former registry minted
    `Id = Guid.NewGuid()` — an id with NO relationship to the `IpcRequest.Id` that caused it — so a page sent
    request `r1`, the module started operation `guid-xyz`, and **the page had to correlate the two itself.**
  - **XHR is the comparison that makes it obvious:** one `XMLHttpRequest` carries `readyState`, `progress`
    and `abort()` rather than handing you a separate "operation". What the kit lacked was the admission that
    a request may outlive its response, so progress, status and cancel are all keyed by the REQUEST id.
  - ⚠ **The minority case that does NOT fold: work nobody asked for.** A scheduled or crash-recovered
    mission reports progress with no request behind it, and **is modelled as what it is — an event stream,
    which `IEventBus` already provides.** `waiting`/`resume` left the model for the same reason: the only
    code driving it marked a queued MISSION. **Why this is recorded rather than named:** three replacements
    were rejected as words every library owns, and ⚠ **a naming problem that resists every candidate is a
    design smell, not a vocabulary shortage.**
  - 🔴 **EVERY request can take a while, so a GRACE PERIOD replaces the declaration** (owner: *"if its
    shorter than 50ms … we only take the last state"*). The 50 ms is not a new number — it is
    `NotificationPumpOptions.FlushInterval`, so the grace period IS the flush window the kit already runs
    on, and there is nothing left for a module author to declare wrongly. ⚠ **Batching is not coalescing:**
    coalescing is keyed by REQUEST ID, last-write-wins within the window, or a 5 ms request still delivers
    `running` AND `completed`.
  - 🔴 **THE RESPONSE IS NEVER DELAYED — the window suppresses NOTIFICATIONS, never the answer.** Anyone
    building it by parking the response has inverted it, adding latency to every fast call in the app to
    save a notification nobody would have seen. Safe by construction: `NotificationPump` has ZERO references
    to `IpcResponse`.

- **D67 — the DEVICE LOOP is part of the framework, so the kit ships a CLI: `@shenora/cli`, second npm
  package, binary `shenora`.** (Owner, 2026-08-08: *"since our kit should be a full set of development, so
  the deploy to sim/iphone should be able to finish with cli, this is not xcode project issue or not"*.)
  - 🔴 **It does not contradict D53/D55, and the distinction is the whole entry.** "A capability gets a
    FOLDER, never a package id" is a rule about what ships **INSIDE the app** — a feature tier lets an
    adopter believe a capability is optional when the framework is one whole. A CLI ships inside nothing: it
    is a `devDependency` that runs on the developer's machine at build time and is absent from every artifact
    the user installs. ⚠ **So the test for any future package is *"does an adopter's app carry this at run
    time?"*, not *"is it a separate id?"***
  - **Nor can it be a folder in `@shenora/react`, which is the shape that looks cheaper.** That package is
    browser code going through an adopter's bundler; the CLI is `node:child_process`, `node:fs` and a `bin`.
    Folding them puts Node built-ins in a module graph headed for a browser, and the failure is a bundler
    error in the ADOPTER's build of code they never imported.
  - **Why it earns its keep against the kit's own thesis (D54):** *can React already do this?* No — reaching
    a real iPhone is `xcrun`, `devicectl`, codesigning and provisioning. And the ceiling argument cuts the
    other way for once: **Capacitor and Electron both ship a CLI**, so a hybrid framework without one is
    missing a table stake, not adding a luxury.
  - **The scope is the LAST MILE only** — take a built app onto a simulator or a phone. It does not build web
    bundles, run tests, or manage a native project (there isn't one, which is why `cap add/open/ls` have no
    counterpart). `src/Shenora.Cli/README.md` carries the parity table against `cap`.
  - 🔴 **Every check it makes is one a real device loop needs, not a defensive habit** — the four ways such
    a loop reports success it did not have are in `.claude/knowledge/mobile-harness.md`.
  - ⚠ **The config describes the PROJECT; the command line describes the MACHINE.** A machine-specific fix
    such as `-p:ValidateXcodeVersion=false` goes after `--`, never into a committed field — a config that
    records machine facts silences the mismatch for everyone who clones the repo, including whoever hits it
    when it is the real problem.
  - ⚠ **Do not mistake THIS repo's harness for the CLI's constraints.** iOS signing needs a GUI login
    session, which is a wall for a Windows→Mac ssh harness and not for an adopter: they run
    `npx shenora ios build` on their own Mac exactly as they would `cap`.

- **D68 — the WebView2 RUNTIME choice belongs to the ADOPTING APP. The kit stays Evergreen by default and
  ships no browser bytes.** (Owner, 2026-08-09: *"ship a fixed version for webview2 should be decided by the
  adopted app not us"*.)
  - **The question arrived as one and was two.** The report was *"the webview2 bundle should be at the
    application location so it should never be shared"* — but the **user-data folder** is already app-local
    everywhere (`paths.DataArea("webview2")`), so that half was a non-issue. Only the **browser binaries**
    were ever in question. **Half of a reported problem not existing is worth recording, because the other
    half is then a NEW position rather than a harvest** (D15's bar).
  - 🔴 **Why it is the app's call.** A fixed-version bundle is ~150 MB per app and comes with ownership of
    the security updates Evergreen handles for you. That trade depends on facts the kit cannot see — managed
    machines, install size, whether an untested browser update is acceptable. **The kit ships the SEAM and
    the default; it does not decide** — the same shape as D42 and D51.
  - **Nothing to build: the seam already exists.** `WebViewEnvironmentOptions.BrowserExecutableFolder` takes
    a fixed-version runtime folder and `null` — the default — means Evergreen. ⚠ **The kit must not grow a
    "bundle the runtime" feature later without reopening this**: it would charge every consumer 150 MB for
    one consumer's requirement.

- **D69 — the Live Activity is DATA the app builds in C# and a GENERIC kit widget READS at runtime. Raw
  Swift stays a first-class path, the normal Apple way.** (Owner, 2026-08-09: *"we should be allow for raw
  swift code, to be used (just like regular) but provide a way to do a c# built up → swift code logic so c#
  builds a config like code and swift part reads it and make the activity logic, because I think activity is
  not for a complex component we probably can cover most of the cases"*.)
  - 🔴 **It is a RUNTIME CONFIG, not code generation, and that distinction is the decision.** Nothing is
    generated, there is no generated-source build step to debug, and the same compiled widget serves every
    app. SwiftUI still compiles at build time — so **the PRIMITIVES are fixed and their COMPOSITION is
    data**, which is exactly the trade *"an activity is not a complex component"* accepts.
  - **The bounded model is the point, not a limitation.** A Live Activity is a small, highly constrained
    surface, so a declarative schema covers most of it and the cases it does not cover are exactly the ones
    that should be written by hand.
  - 🔴 **AND THAT IS WHAT SETTLES THE D13 TENSION** — *can an adopter still express their own look, or have
    we frozen ours into everyone's Island?* Because raw Swift remains a first-class path rather than an
    escape hatch of last resort, **the kit's look is a DEFAULT and never a ceiling.** A config-driven default
    plus an unrestricted manual path is not a design system (D13).
  - ✅ **Proven on a device**, so the promise stands as first made: **one MSBuild property plus four SwiftUI
    view bodies, and no `.xcodeproj` for the adopter** — no prebuilt `.appex`, and `ActivityAttributes` stays
    the app's type.
  - 🔴 **The real limitation belongs to the PLATFORM:** `ILiveActivities.Update` calls ActivityKit
    IN-PROCESS, so swiping the app away freezes the activity at its last value — **the card outlives the app,
    the update loop does not.** Advancing one while the app is not running is what ActivityKit PUSH updates
    are for; that needs APNs and a server, which is the adopter's infrastructure, so the kit's part is at
    most surfacing the token.

- **D70 — the kit SHIPS A DEFAULT CONVERSION ENGINE, and it is the platform's own codecs. `Convert` is the
  OVERRIDE, for work past the platform's reach.** (Owner, 2026-08-10: *"we can ship default conversion engine
  for each platform mainly focus on hardware support"*, *"but for more complex decode/encoding the
  application use our framework will need to do their work"*.)
  - **What it is.** `MediaConversionOptions.Conversion` takes the shell's `IMediaStreamConversion`, and
    `Convert` — previously `required` — defaults to the kit's remuxer joined to it. With neither, the default
    repairs containers. **No shipped codec bytes**, so D51 is untouched and unamended: wiring decoders the OS
    already has is not an engine. It is D51's own FIRST preference, made the default instead of a recipe.
  - 🔴 **THE BOUNDARY IS THE DESIGN, and it is D59's line: what the PIPELINE decodes and the WEBVIEW
    refuses, nothing wider.** That is what makes a DEFAULT defensible where a shipped engine would not be —
    **the kit's own converters cannot grow into ffmpeg, because past what the platform offers the answer is
    "register one".** ⚠ The boundary moves with the pipeline rather than being fixed at the hardware: an app
    that hooks in a decoder widens what its users can play, through the same route and with no kit change.
    Out of the box the window is narrow, and per-device (tables: `docs/design/mobile-shells.md`).
  - ⚠ **Setting both `Convert` and `Conversion` THROWS at registration**, while the app is composing and
    with a stack naming the call site. Two ways to say the same thing leaves one unread — D63's defect class
    — and the unread one would be the codecs the adopter believed were in use.
  - ⚠ **No desktop implementation exists**, so the desktop default is container repair. Not an oversight to
    fix by symmetry: a Media Foundation conversion is real native work and wants a consumer first (D15).
  - 🔴 **A DROPPED STREAM IS A FAILURE** (owner: *"i dont think fail silently is good … we should not taking
    what's supported unsupported"*), because a user cannot tell "this film has no soundtrack" from "this
    device cannot play the soundtrack it has". It fails with `UnsupportedCodec`, names the codecs, and caches
    nothing, so a later request cannot serve the silence as a hit.
    - 🔴 **THE TWO CAUSES OF A DROP NEED OPPOSITE RESPONSES, so the log names which one:** a drop WITH a
      codec seam is unsupported on this device, a drop with NONE means the platform was never asked — the
      adopter's composition rather than the file. (Generalised in `probe-diagnostics.md`.) ⚠ Stricter than
      the seam it replaced; an app wanting a video-only result still has `Convert` (D42).

- **D71 — STREAMING IS THE MEDIA TIER'S PRIMARY PATH, and the whole file is what streaming LEAVES
  BEHIND rather than the thing it produces.** (Owner, 2026-08-12: *"full transcode should be after if we got the full segment,
  its more like a cache/persist logic so the SegementEnegine should be the main focus"*; *"1 planner no
  platform difference"*.)
  - **The inversion.** Materialising an ENTIRE output before serving it makes the first play of anything
    convertible wait for the whole transcode, and a seek is not expressible until the file exists. So that
    becomes the TAIL of the pipeline rather than its middle: **"we have all the segments" and "we have the
    finished file" are one state**, and playback then reverts to `Direct`.
  - 🔴 **THE PLANNER CHOOSES ON WHAT THE PRODUCER CAN PROMISE, NEVER ON THE PLATFORM.** `Remux` derives the
    output length AND the byte↔time map before any work is done, so it is a COMPUTED file with no frontier
    to stall on; `Transcode` can promise neither, so it gets segments. **The split is behind the seam:** one
    method for the CONSUMER, two productions for the kit.
  - 🔴 **The constraint its measurements impose: a design validated on ONE shell is not validated.** Android
    and iOS produced opposite failure modes from the same design.
  - **It SUPERSEDES the "no default segment engine" position**, which had made itself falsifiable —
    *"something must ASK before one is written (D63)"*. Something did. **D51 is untouched**: no engine bytes
    ship, and an app past the platform's reach still supplies its own.
  - 🔴 **THE STREAMING CACHE AND THE OFFLINE ARTIFACT HAVE OPPOSITE POLICIES, and conflating them breaks
    offline silently** — ordinary playback would quietly evict a film someone waited for, surfacing later as
    a file that used to work. So "complete" is a checkable predicate and the route REFUSES a destination
    inside the cache, rather than documenting the hazard.
  - As-built, and every measurement behind this: `docs/design/media.md` (+ `mobile-shells.md` for numbers).

- **D72 — THE COMPUTED-REMUX ROUTE GETS NO PAGE-SIDE READINESS CONTRACT: the APP warms the plan in
  .NET and the page stays one plain `<video src>`.** (Owner, 2026-08-13, on being offered a readiness event: *"this
  sounds more like HLS now"*.)
  - **The question it closes.** A source nobody has planned answers `503 Retry-After: 1`, and **a `<video>`
    cannot ride that out** — on both shells the element errors within ~70 ms and issues no retry for at
    least 12 s, while re-pointing `src` after the plan lands plays it immediately. So the 503 buys nothing
    for an element (it still buys a retrying `fetch` client), and something had to tell a page when to point.
  - 🔴 **THE REJECTED ANSWER IS THE INTERESTING ONE: a readiness event plus a page-side consumer.** It fits
    the kit's habits, and it forfeits the only thing this route has over HLS. **The plain-`<video src>` claim
    IS the differentiator:** a page that must subscribe to an event and set `src` from a handler is no longer
    plain, and at that integration cost MSE/HLS is strictly MORE capable.
  - ✅ **THE ANSWER: nothing tells the PAGE, because the APP already knows.** The 503 exists for one reason —
    nobody told the kit which source is about to play — and the app built that URL. So the warm-up is an
    ordinary .NET call (`route.PlanAsync(source)`, once, cached by identity) and the page contract does not
    change at all. **That is the thesis rather than a workaround**, and a page on any frontend still works.
  - **Blocking the first request instead is not available:** both mobile shells resolve a resource
    SYNCHRONOUSLY, and a blocking wait there deadlocks the iOS main thread. **The wait has to move EARLIER
    than the request, which is precisely what warming is.**
  - ⚠ **`PlanAsync` MUST apply the request path's authorisation chain, not a shortened one** — remote check,
    containment against `AllowedRoots`, then the identity key. A warm entry point that skipped it would be a
    way to make the kit walk any file the process can read, from app code that believed it was only hinting.
  - 🔴 **THIS DECISION IS FALSIFIABLE, and here is the test.** It assumes an app can know what it is about to
    play. Where it cannot, the honest options are "await the warm before mounting" or *that case belongs to
    segments*. **If apps routinely cannot warm ahead, the answer is to go segments, NOT to add the event
    back**, because the event costs the differentiator without buying the capability.

- **D73 — MEDIA COMPOSITION FOLLOWS THE KIT'S OWN `Add`/`Use` SPLIT, because .NET already has this shape and
  a second idiom would be a thing to learn twice.** (Owner, 2026-08-13: *"lets do this properly a more .net
  fasion of styling of app build"*.)
  - **The rule is not invented here — it SHIPPED with D64's test:** ***`Use` means a wider configuration
    INCLUDING its pipeline; `Add` is the service-collection level only.*** So the media tier gets
    `services.AddShenoraMedia(...)` and the routes stay `Use…`.
  - **What the composition audit found, counted rather than felt** (23 hand-wiring sites in the sample's
    main page alone): four hazards that are silent rather than loud, led by route ORDER being load-bearing
    and unenforced. They are as-built and listed in `docs/design/media.md`.
  - ✅ **THE DOC IS THE DELIVERABLE, NOT THE CODE**, and it is `docs/guides/media.md` — which had covered
    `UseMediaConversion` and **not `UseComputedRemux` at all**, leaving the kit's PRIMARY delivery path (D71)
    absent from the only page an adopter reads about media.
  - 🔴 **Writing it settled the composite question empirically: a `UseMedia` composite is NOT yet earned.**
    The honest snippet is four short blocks and reads fine. What it exposed is that **the ordering hazard
    cannot be fixed by prose** — the doc has to SAY "nothing enforces this", which is the shape of a defect
    waiting for a gate. **Prefer a test or an analyser over a helper that hides the order**, and if one is
    added the individual calls stay public.

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
  (Owner, 2026-08-14, overriding the adoption-driven hold on D71's remaining pieces: build the whole tier.
  ⚠ What that hold was RIGHT about survives as the risk being managed — the kit is guessing what a segment
  engine must promise, with no adopter to correct it, so undecided details bias toward what an adopter can
  change and the guesses are written down.)
  - **fMP4, not MPEG-TS, and the deciding reason is not compatibility.** `isTypeSupported('video/mp2t')`
    answered `true` on both shells and that claim is not trusted, a MediaSource append failure being SILENT.
    What settled it is that **fMP4 makes `ISegmentEngine.HasRenderedPicture` ANSWERABLE**: the `trun` states
    every sample's size, so "the encoder accepted every frame, wrote `video:0KiB` and exited 0" — a measured
    bug — becomes a subtraction, where MPEG-TS declares the stream in its PMT either way. **A container
    chosen for what it lets you CHECK, not for what it lets you play.**
  - **A RE-ENCODED track is cut on a whole number of seconds, and a fractional grid is REFUSED rather than
    rounded** — a boundary where no keyframe exists produces segments that PLAY and only misbehave on a
    seek, so it is refused at composition time. ⚠ **A COPIED track has no grid at all** (D76).
  - 🔴 **The engine runs wherever an `IMediaStreamConversion` is REGISTERED — a registration test, not a
    platform one, and reading it as "mobile only" is the drift to avoid.** It is false on the desktop today
    only because `Shenora.Windows` ships no converter, which is a fact about what the KIT provides; an app
    supplying its own decoding library gets the engine on Windows with no change here (D42/D51/D70).
  - **The routes do not arbitrate between themselves:** which one a source takes is the app's decision,
    expressed by which it registers for that URL, since a route that silently declined work it was
    explicitly given would be undebuggable.
  - As-built, and the measurements: `docs/design/media.md`.

- **D76 — THE SEGMENT ENGINE COPIES WHAT MP4 CAN CARRY AND RE-ENCODES ONLY WHAT IT CANNOT; A COPIED TRACK IS
  CUT ON THE SOURCE'S OWN KEYFRAMES, SO THE BOUNDARIES TRAVEL AS A PLAN.**
  - 🔴 **Why: re-encoding everything produces NO VIDEO for almost any real file.** The platform video
    encoders offer h263/mpeg4/mpeg2video and never h264/hevc, so the intersection with what a webview
    decodes is EMPTY and every ordinary source comes back sound-only. **An H.264 or HEVC track needs no
    encoder at all** — Matroska already stores it in the length-prefixed form MP4 uses, which is why
    `Mp4Remuxer` can copy it. Only a stream MP4 cannot hold (AC-3, DTS, VP9) costs a codec.
  - **Copying beats converting wherever both are possible:** faster, lossless, cannot fail halfway, and it
    does not spend one of a phone's handful of hardware codecs. **ONE predicate answers it for both writers**
    (`Mp4Carriage`) — *a second spelling of that question is how the plan and the write come to disagree
    about one file*, and a second CALLER is the same hazard.
  - 🔴 **What a copy costs is the GRID: copied frames keep the keyframes the ORIGINAL encoder chose**, and a
    segment not beginning on one cannot be decoded alone. So the boundaries travel as ONE `SegmentPlan`,
    computed once and handed to BOTH the manifest and every run — **two derivations of where the cuts are
    fail silently**: the bytes stay valid, the player believes the playlist, and a seek arrives elsewhere.
  - **`IsAvailable` still requires a conversion, so the engine stays mobile-only** (D75): a copy-only run
    needs no codec and *could* answer on the desktop, but a source every stream of which can be copied is
    exactly what the computed-remux route serves better.
  - ✅ **Since proven on all three implementations** — a real `avcC` copied into a fragment decodes in
    Chromium, on iOS and on Android. As-built and the numbers: `docs/design/media.md`.

- **D77 — THREE HOMES, and this file holds only the first: a DECISION here, a subsystem's DESIGN under
  `docs/design/`, an invariant in `.claude/knowledge/`.** (Owner, 2026-08-14: *"we should have proper
  feature design docs instead just dump everything in decision, and also decision has a lot rules in there
  which does not fit 100% all the cases but will be taken for each session which cause decision drift"*.)
  - 🔴 **A RULE HERE IS APPLIED TO EVERY CASE, because every session takes the whole file at once.** That is
    the named failure: an invariant earned in one context ("never block the resource thread", "prefer a test
    to a helper") reads as universal, and a later session applies it where it does not fit. **A knowledge rule
    is loaded only when the task matches its area**, so scope is enforced by the loader rather than by the
    reader remembering. An invariant therefore lives in `.claude/knowledge/`, and this file links it.
  - **A subsystem's DESIGN is not a decision either.** How the media stages compose, which seam produces
    what, which clock each track is on — that is one subsystem's shape, needed before changing it and
    irrelevant otherwise. It was spread across sixteen entries and a third of this file.
  - ⚠ **This NARROWS D57 rather than reversing it, and the distinction is what keeps D57's win.** D57
    killed PRE-implementation plans: written before the thing exists, superseded the moment it does, and
    competing with `ARCHITECTURE.md` for *what is true now*. An AS-BUILT design doc is written after, from
    the code, and answers a question no other doc does — `ARCHITECTURE.md` is a MAP (where things live),
    a guide is ADOPTER-facing (how do I use it), this file is WHY. **The staleness D57 feared returns the
    moment a design doc argues instead of describes**, so a design doc states the design and LINKS the
    `D<n>` for every claim that needs defending.
  - **The test for where something goes:** *would you re-read it when about to relitigate?* → here. *When
    about to change this subsystem?* → `docs/design/`. *Every time you touch this area, to avoid breaking
    something?* → `.claude/knowledge/`. **And before any of them: can a gate or a test hold it instead?**


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
