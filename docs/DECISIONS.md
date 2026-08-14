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

- **D1 — Shenora is the BODY; Lyntai is the brain; no dependency between them.** Apps may use both;
  Shenora must never reference Lyntai. Keeps each library adoptable alone. The kit is a **hybrid app
  framework — .NET + React — across Windows, Android and iOS** (D32 made a second shell a peer, D37
  named one package per platform, D53–D55 settled the identity).

- **D2 — A package boundary must buy a RUNTIME separation, not a seam.** An `*.Abstractions` split earns
  nothing because Core already holds the contracts, and no separate `Shenora.Modules` or
  `Shenora.Extensions.DependencyInjection` package exists — module registration is core plumbing, and
  standard Microsoft DI abstractions are used directly. **D55 extended this to WEIGHT**, which had been
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
  `workflow_dispatch`.** Family precedent: Lyntai added push CI and the owner removed it the same day.
  Don't re-add it as a "gap".

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

- **D8 — Extraction-first.** Prefer lifting proven sibling code — including its post-mortem comments,
  which are the product — over new abstractions. The primary source is the richest desktop-only sibling;
  the second desktop sibling is the conformance reference; Sonora donates its window-state store,
  singleton/restart skeleton and event bridge. Named map: `local/EXTRACTION-MAP.md` (private);
  de-identified: `.claude/knowledge/extraction-sources.md`.

- **D9 — Repo organization clones the family system**: short `CLAUDE.md` → `docs/README.md` router →
  two-tier `.claude/rules|knowledge` with `RULES_INDEX.md` → gitignored `local/`, plus Lyntai's
  library-repo docs (`DECISIONS.md`, `CHANGELOG.md`).
  🔴 **There is NO archive tier.** One was added and deleted within two days, by which time
  `docs/archive/tasks.md` was the largest doc in the repo at 290 KB — 62 % of all doc weight was
  finished work. Owner: *"we dont keep historial since we have git for that"*.
  **The tell that a doc is really an archive: nobody reads it, and it grows fastest.** Ask what QUESTION
  a reader arrives with — "why is it done this way?" → here · "what is the shape today?" →
  `ARCHITECTURE.md` · "what is left?" → `TASKS.md` · "what happened?" → `git log`. A warning written for
  a future session was never history: it is an invariant, and it belongs in `.claude/knowledge/`.

- **D10 — Two consumption profiles; a `Shenora.Hosting.AspNetCore` package is NO-GO.** The split exists
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

- **D14 — The auxiliary browser subsystem is in scope**: offscreen render sessions with a bounded pool,
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
  cycle, events instead a single business need."* This is the naming half of D21, and it needed saying
  separately because the kit passed D21 on SHAPE while failing it on NAME twice.
  **The test: could a consumer whose use case is nothing like the one in the name still recognise this
  type as the thing they need?** `LoginWindow` contained no login logic (→ `InteractiveSession`);
  `CoBrowseSession` was an off-screen browser that streams frames and accepts input, which is co-browse,
  remote support, visual capture or a preview pane depending on who wires it (→ `StreamingSession`).
  Neither was a behaviour bug — the cost is that a scenario name makes the kit LOOK like it ships that
  product, so the next contributor adds more of it, and consumers with a different use case never find
  the primitive.
  ⚠ **If a type in `src/` needs a scenario name to make sense, that is the signal it does not belong in
  `src/`** — not a licence to name it. Sibling vocabulary that is genuinely mechanism is fine and must
  not be "fixed": `ProfileDirectory`, `Module`, `ImmersiveDarkMode`, `UserDataFolder`.
  **Enforcement:** a domain-vocabulary sweep over `tests/Shenora.Tests/Api/Baselines/*.txt`, which
  enumerate every public type, member and PARAMETER name (named arguments are a source contract).

- **D23 — The module contract carries the EVENT path, and the kit tracks long-running requests.**
  Three parts, one design, shipped as 0.2.0. ⚠ **D66 later merged the tracked-operation half into
  `IpcRequest`**, so the vocabulary below is the current one; the `Operation*` names are retired.
  - **(a) A route receives an `IModuleContext` in its signature** — `Publish` (module-scoped emit),
    `Report` (progress), `Logger`. The bus was already the spine and the contract did not admit it, so
    every app re-agreed the conventions by hand.
  - **(b) What moves in is the correlation-and-lifecycle MECHANISM only** — id, state, progress, scope,
    idempotent finish, cancel-by-id, bounded history, throttled progress. **What stays out is everything
    that decides what a request IS:** no queue, no scheduler, no phase model, no `Kind` enum (`Kind` is
    an app string), no i18n rendering, no UI (D13), no persistence. D21's test still passes — an app
    builds its own activity panel without adopting a kit product decision.
  - **(c) The transport-neutral outbound half is `NotificationPump`** (bus subscribe → filter → bounded
    queue → batch → ready gate → guarded serialize), with a per-channel `Filter`; `WebViewIpcBridge`
    keeps only the WinForms/WebView2 parts. This is D16's "the seam, not the package" applied to the
    HOST half.
  🔴 **Every non-terminal state must have a sanctioned exit to a terminal one**, enforced by a test that
  ENUMERATES the state set rather than by reviewer attention — an emergent trap is invisible in any
  single guard's diff. `IpcRequestStateInvariantTests` does this against `IpcRequestState`; read the
  enum for the live set. It is worth more than the test it replaced: `IpcRequestTracker` encodes "in
  flight" as `State == Running` in SIX places, so a second non-terminal state would be treated as
  finished by all of them and nothing would throw. Sabotage-verified with a fifth value.
  🔴 **Progress is not percent.** `IpcProgress(double Value, double? Total = null, string? Unit = null)`
  — apps measure in bytes, items, an absolute count with no denominator, or a genuine percent, and
  forcing percent makes them pre-compute a ratio and discard the numbers their UI wants. `Total = null`
  means no known denominator, never zero; `Unit` is app-defined and uninterpreted, like `Kind`. **Nothing
  is clamped or validated** — silently rewriting an app's reported number is worse than passing it
  through, a value above its own `Total` is the app's bug to see, and throwing on a background hot path
  would kill a request over a cosmetic number. The kit ships no percent helper; that division is the
  consumer's policy.
  🔴 **The crash-checkpoint half was CUT before publish, and the cut is the lesson.** A resumable-offer
  cluster took ~8 reshapes inside one unpublished release and produced its only Critical. The tell was
  in the entry's own notes: it came from **one** app, against a standing bar of two
  (`generic-library.md`). The question "does this entry still have a live body?" only existed because
  the registry accepted entries it had never started; removing that removed the question. **A single
  question answered three times is what a design defect looks like from the inside.**

- **D24 — Frameless chrome is a FIXED WinForms type, not an attachable behaviour.** Owner: *"Frameless
  chrome should be part of winform (as a style of our winform design)"*. A review proposed extracting it
  into a `FramelessChrome.AttachTo(form)` behaviour; **rejected**, because the window style is naturally
  set in `CreateParams` at handle creation and attaching after the fact needs `SetWindowLong` +
  `SWP_FRAMECHANGED` — a SECOND mechanism for the same property, doubling the verification surface in
  the one area where a green unit suite has twice been the wrong answer. The benefit is also narrower
  than it looks: `WindowCommandOptions` takes a plain `Form` plus delegates, so a window that is not an
  `OptimizedForm` can already drive minimize/maximize/close/drag/resize over IPC.
  **ACCEPTED LIMIT, recorded so it is not re-raised as a defect:** an app that cannot change its form
  base cannot take the frameless chrome. Reopen on adopter evidence, not on the symmetry argument.
  **The cohesion complaint WAS fair, and the split line is the rule to reuse:** caption-button rendering
  moved to an internal `CaptionButtonRenderer`. **Extract what is pure input → pixels; leave anything
  that answers a window message where the OS can see it.**

- **D25 — Frameless chrome and native drop zones are the kit's FLAGSHIP pair. Settled; do not redesign
  without adopter evidence.** Owner, after testing both by hand: *"those 2 features kind important"* ·
  *"the frameless winform was developed properly so don't really change that"* · *"I have been there
  before so do not change this"*. Both are fully generic AND deliver something the adopting app would
  not have got by hand — the chrome raises the UI bar with Snap Layouts (`HTMAXBUTTON`), Win11 rounded
  corners squared while maximized, immersive dark mode, DWM border colour and runtime theme resync.
  🔴 **Drop zones deliver a capability the page cannot have at all:** a page-side drop yields a `File`
  whose only accessor is its CONTENT, forcing an eager byte copy of every dropped file across IPC
  *before the app knows whether it wants any of them*. Native overlays yield `string[]` paths.
  **So `useDropZone` is not optional sugar — it is THE file-drop path on this kit**, and a DOM drop
  handler is what it replaces, not an alternative to it.

- **D26 — the kit's DESKTOP scope is Windows only. Linux is served by the SERVER-BACKED profile, not by
  a native Linux shell.** ⚠ Read this as DESKTOP scope, not kit scope — D32 added Android and iOS
  shells, and they ship; the two decisions are about different platforms.
  - 🔴 **A candidate shell must expose the NATIVE WINDOW, not merely host a WebView.** This is the
    selection criterion, earned by the owner having tried and abandoned **Photino**: it cannot do drop
    zones at all, because a window-with-a-WebView gives you nowhere to put transparent native overlays
    over page elements — straight back to the D25 eager-copy problem.
  - **MAUI does not solve the stated problem anyway** (no official Linux target), and MAUI for Windows
    is separately rejected: it would mean rewriting the chrome D24/D25 just settled for no capability
    gain.
  - **Measured:** the Windows shell was ~9,300 lines of `net10.0-windows`, **~60 % of the kit's C#, and
    it is the part that IS the value.** None of it ports. Zero Linux consumers; the server-backed
    profile already runs there.
  - **What would reopen it:** a real Linux consumer plus a shell that passes the native-window test.
    **Avalonia** is the unevaluated candidate. Do not re-propose Photino or MAUI-for-Linux.

- **D27 — the scheduler's unit is a MISSION, and a definition is not an execution.**
  - **The naming rejections generalise:** `Work` is too common a word to grep; `Task` collides with
    `System.Threading.Tasks` in every consumer importing both; `Quest` reads as DOMAIN vocabulary in a
    games family (what `SurfaceVocabularyTests` keeps out of `src/`).
  - **`MissionDefinition` (what should run) is separate from `MissionExecution` (one specific run)**,
    replacing four types with two. An execution carries `Attempt`/`IsRunning` and **no
    `CancellationToken`** — the body takes its token as a second parameter, so an execution stays a pure
    value safe to hold in a diagnostics view.
  - 🔴 **Why now rather than when a consumer asked — the rule that was being misapplied.** The
    two-consumer bar governs adding CAPABILITY; the shape rule governs SHAPE: **pay now only where the
    later change would be BREAKING rather than additive.** Owner: *"bigger change does not mean a bad
    thing, we need to think forward for future, change is allowed, this is still pre-1.0."*
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
    serialize only the landing.** The failure modes do not overlap either: a scheduler's are starvation
    and deadlock, an applier's are partial writes and locked targets.
  - **Atomicity is the app's choice per update** (owner: *"it depends what the application need"*):
    `PerChange`, or `AllOrNothing` via compensating rollback — which forces STAGED deletes, a delete
    being the one change that cannot be undone from nothing.
  - 🔴 **Crash-atomicity is opt-in via a write-ahead journal, and the ORDERING is the property:** the
    undo plan is durable BEFORE the mutation, because a plan written afterwards is missing exactly the
    change that got interrupted. That is why undo is DATA rather than closures and why every change is
    planned before it is applied — **do not "simplify" that split away.** Recovery rolls BACK an update
    interrupted while applying and FINISHES one interrupted while committing; rolling the latter back
    would undo a success.
  - The engine lives in the `Shenora.Engine.Files` namespace (D48 made it a package, D55 folded it
    back). `PathClaims` stayed behind because it is scheduling vocabulary.

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
  implement differently like dropzone and frameless)"*. `Shenora.Mobile` references no Windows
  assembly. **The evidence that the split is in the right place is its SIZE: ~200 lines**, because the
  substrate moved first. A fat shell package would have meant something portable was still trapped in
  the Windows one.
  - **The bar stays D20's, not "it looks platform-neutral":** *can app logic compile off Windows?*
    Window geometry, tray, secondary windows and native drop zones stay in the Windows shell because
    they are desktop CONCEPTS — on mobile they are absent, not different.
  - ⚠ **Checked against the platform before designing, and it cancelled a proposal.** Lifting the
    resource-serving layer for reuse died on the fact that `HybridWebView` has **no request
    interception** — it serves `Resources/Raw/wwwroot` itself. There was no seam to lift into. Do not
    re-propose it.
  - **The platform-owned loop is why `Start`/`Stop` exist.** `IShenoraRunner.Run` is contractually
    "blocks until shutdown", which a MAUI activity cannot honour, so the mobile host registers no runner
    and the app drives the pair from its own lifecycle.

- **D33 — an ABSENT capability throws and names the platform; a SATISFIED one is an honest no-op.**
  `ShellCapability.NotSupported` is the one message. A silent no-op is the "mistyped resource prefix
  degrading to an all-404 provider" class this repo keeps paying for.
  **The distinction is load-bearing and was found by implementing it:** clipboard IMAGES have no
  expression in MAUI Essentials → refuse; `IUiInteraction`'s block/unblock is satisfied BY the platform
  (mobile pickers are modal) → an honest documented no-op. Refusing the second kind would break portable
  logic that is behaving correctly. **"Absent" means no expression exists here, not "we did it
  differently".**
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
  desktop … for desktop it's more free"*. A desktop folder browser hands back ambient, permanent access
  to an arbitrary path; Android hands back a revocable, scoped grant to a tree URI. **Same word,
  different guarantee — papering over that is how a portable-looking API becomes a lie at the one moment
  an app relies on it.**
  Ask what the app actually wanted; all three are expressible on both shells:
  1. **"Somewhere I own to read and write."** Needs no picker at all — `ShenoraPaths` on desktop, the
     MAUI app-data directory on mobile. **An app asking the USER for this is the bug.**
  2. **"Let the user hand me some media."** A platform media picker on mobile, a multi-select file
     dialog with image filters on desktop. Genuinely portable.
  3. **"Let the user grant me a working directory."** The only one that stays desktop-flavoured, because
     the permission MODEL differs. Name it as desktop-only rather than pretending.
  **Consequence:** `IFileDialogs.OpenFolderAsync` is documented as desktop-only, the mobile
  implementation refuses it by pointing at (1) and (2), and a media contract is NOT pre-built — nobody
  has asked, and that is speculation. The shape is recorded so the first one that does gets it in a day.

- **D36 — the HOST advertises what it can do, in the handshake; the client never sniffs the platform.**
  D33 says what happens when a page calls something absent; this is how the page avoids calling it.
  `ShellInfo { Name, Capabilities }` is the ready handshake's response data and the page renders on it.
  - 🔴 **Capability, not platform, because the platform is the wrong question.** What a host offers
    depends on what the APP composed — a desktop shell that never registers `TrayIcon` has no tray, and
    a desktop frontend running in a plain browser tab during `vite dev` has none of it. `Name` exists
    for diagnostics and is documented as never-branch-on.
  - **Declared by the app, not inferred by the kit.** The kit cannot know which services were
    registered. The cost is honesty: a capability advertised but not composed turns a rendered button
    into a D33 throw when pressed.
  - **Absent means "assume nothing", never "assume desktop".** A capability-less reply covers browser
    dev, a host that has not opted in, and a host predating this. Defaulting the other way makes the
    browser the one place the page renders wrongly — which is where it is developed.
  - ⚠ **`WireMirrorTests` grew a block-comment stripper here, and the repair it tempts you into is the
    hazard:** loosening the assertion to a subset check is what would make the tripwire stop checking.
    Fix the parser.

- **D37 — ONE shell package per PLATFORM, named for the platform.** The three Windows packages merged
  into `Shenora.Windows`; the mobile shell ships as `Shenora.Android` + `Shenora.iOS`. The package COUNT
  has moved since (header table); **the SHAPE this decided is what governs.**
  - 🔴 **The test, applied in both directions: does the boundary correspond to something a CONSUMER
    experiences?** "I am building an Android app" does — so mobile SPLIT even though the two share every
    line of source. "WinForms without WebView2" does not — this kit's premise is React in a webview, so
    that consumer cannot exist, and Windows MERGED. **The same question produced opposite answers, which
    is how you know it is the right question.**
  - **What the old Windows split was protecting was an adoption STAGE, not a configuration** — a
    statement about the ORDER of adoption, and they take WebView2 at Stage 3 regardless.
  - ⚠ **Two arguments against merging, and the measurements that killed them.** "Sessions is 269 lines
    of SemVer surface" — it adds no dependency and the types are maintained either way. "WinForms-only
    consumers avoid 52.6 MB" — that is dev-time RESTORE size, not shipped bytes; the WebView2 runtime is
    an Evergreen system component. **Measuring the easy thing instead of the relevant thing is the
    mistake to remember here.**
  - **The mobile packages share SOURCE, not an assembly** (`src/Shenora.Mobile/`, deliberately with no
    csproj). A third assembly would either be published — a package nobody asks for, carrying its own
    surface — or need embedding tricks to hide it. Divergence goes in each project's `Platforms/`
    folder, which the MAUI SDK includes per TFM, so it needs no `#if`.
  - **Naming is by platform, not by framework**, because the two mobile faces do not share a web engine
    (Chromium's WebView on Android, WKWebView on iOS) and `Shenora.iOS` never touches WebView2.

- **D38 — an off-screen session gets the app's own BUNDLE, and deliberately not its custom SCHEMES.**
  `SessionBrowserOptions` takes `VirtualHost` + `ResourceProvider` + `FolderMappings`, so a packaged
  desktop app can co-browse or off-screen-render its own frontend. It is the SAME two option names the
  host already uses, and the recipe is to pass the host's own values through — passing the same provider
  INSTANCE means the session's requests hit a cache the shell already warmed.
  - **Both halves or neither, refused at initialization.** Either alone serves nothing, and the symptom
    would be identical to the bug being fixed.
  - 🔴 **The app's `RequestFilter` is consulted BEFORE the bundle, and both live in ONE
    `WebResourceRequested` handler.** A blocked request is a stated policy the kit must not override
    from a path the app cannot see; and two handlers each assigning `args.Response` is
    last-writer-wins by subscription order, **which is not a contract to rest a security boundary on.**
  - **NOT shipped, deliberately: a custom/deferred SCHEME inside a session.** WebView2 accepts scheme
    registrations only at ENVIRONMENT creation, so it is a materially bigger surface (env options,
    `AllowedOrigins`, CORS) and no consumer has needed it. Recorded as a known limit.
  - **`SessionController` still exposes no `CoreWebView2`.** Handing out the raw browser object would
    make every future session capability an escape hatch instead of a seam.
  - ⚠ **Bundle responses carry `Access-Control-Allow-Origin: *`, and in a session the page can be ANY
    origin** — so script in a third-party page being co-browsed could `fetch` the whole bundle. Stated
    plainly rather than special-cased: dropping the header would change `WebViewHost` to fix a session
    concern and is load-bearing for a dev-mode page on another origin, and gating on `core.Source` walks
    into the bug `ShouldBlockRequest`'s `pageUri` normalization exists to prevent. The exposure is the
    app's own shipped frontend and the options are per-SESSION.
  - **Why this was invisible for so long:** it only bites a desktop-only app serving an EMBEDDED bundle.
    Both sample demos work in dev mode, and the e2e runs in dev. **A gap whose reproduction requires the
    packaged build is exactly what the "prove it against the sample" gate exists for.**

- **D39 — the auxiliary-SESSION stack stays a DESKTOP capability. Both mobile shells host a webview;
  that is not the same thing.** Owner: *"since on both mobile env we also have fake browser right? is
  that safe to do the same session logic?"* `StreamingSession`, `RenderSessionPool` and
  `InteractiveSession` do NOT port, and the reason is CAPABILITY — so the answer does not rest on a
  store-policy reading that could change.
  - **The stack rests on CDP, not on "a webview"** — screencast, device-metrics override, synthetic
    input. Neither mobile shell has an in-process CDP client. Android is Chromium underneath, but its
    DevTools endpoint is for an EXTERNAL client after enabling debugging, which is a security red flag
    to ship in release and is not an in-process API regardless. iOS is WebKit: no CDP, and no public
    synthetic-input path.
  - 🔴 **THE TRAP, and the real reason this needs writing down.** A port IS buildable behind the same
    interface — frame-polling plus `evaluateJavaScript` dispatching synthetic DOM events. It would
    compile, demo, and be materially WEAKER: polled instead of change-driven, and the events are
    `isTrusted: false`. **Untrusted events are precisely what fails on the pages `InteractiveSession`
    exists for** (verification challenges, auth flows). Same method name, different guarantee — D35's
    shape exactly, and it is tempting because *the C# ports for free, so every real cost lands elsewhere*.
  - **Store policy is a SECOND reason and is NOT verified here — do not cite this entry as if it were.**
    Guidelines change; check the current text. It is listed second deliberately, because the capability
    argument alone settles the decision.
  - **What the mobile answer IS, decomposed the way D35 decomposes "open a folder".** *Show the user a
    web page* → `IUrlLauncher`, already shipped. *Log the user into a third-party provider* → the
    platform auth session, which is BETTER than the kit's version, not a downgrade: the cookies stay in
    the system and the app never sees them. *Render my own UI off-screen* → does not arise; on mobile
    the app's UI already IS the webview.

- **D40 · D41 — media as its own package family. RETIRED; both bodies deleted rather than banner-stacked.**
  They governed `Shenora.Media` + `Shenora.Media.{Windows,Android,iOS}` — a nine-package set, none of which
  exists. The three platform packages were deleted by D45 before any shipped (serving bytes to a page turned
  out to be resource INTERCEPTION, a shell capability), and `Shenora.Media` itself folded into `Shenora`
  by D53.
  **The one rule that outlived both:** app logic names the media types and compiles on `net10.0` with no
  platform reference, enforced by `samples/Shenora.Sample.Logic` turning RED if a platform type reaches it.
  ⚠ **Deleted rather than amended, and the test is reusable: does this entry describe something that
  SHIPPED?** If not, delete it and say what replaced it — forty lines of range-versioning rules for
  packages nobody can install is noise that makes the surrounding stack harder to read, not safer. Owner:
  *"we should do a cleanup, remove everything thats irrelevant anymore which is clearer than keep adding."*

- **D42 — an ENGINE is the primary playback path on every platform, including mobile.** Owner: *"I prefer
  to use engine, because mobile library is not stable to support different type of media but if we use
  engine we have the control"*. The argument is CONSISTENCY — one behaviour matrix across three platforms —
  and it beats the byte count.
  - **Verified on a device:** codec support is **vendor-declared per device** — the decoder list mixes
    Google's software defaults with SoC-vendor hardware decoders, which is why `MediaCodecList` is a runtime
    query rather than a constant. **Containers are a distinct and worse axis:** `MediaCodec` decodes codecs
    while `MediaExtractor` handles containers and its MKV support is thin, so **H.264-in-MKV can fail on
    Android while H.264 decodes perfectly** — a failure a codec table alone would never predict.
  - **NOT verifiable by this repo: how OFTEN the variance bites a real catalogue.** That is the owner's
    field experience and it is the deciding input, **recorded as judgement rather than dressed up as a
    measurement**: *a platform player failing on roughly ONE THIRD of a real collection*. The composition
    explains it — MKV containers, AC3/E-AC3/DTS audio (licensed, not in Android's mandatory set), HEVC
    10-bit, AV1, VC-1, MPEG-2. At that rate an engine stops being a preference.
  - 🔴 **THE DESIGN CONSEQUENCE: a playability verdict must be PER STREAM, not one boolean for the file.**
    The AUDIO track is often what fails while the H.264 video decodes perfectly, so `CanPlay(file) -> bool`
    would have been wrong in the most common failure case. It is also why `remux` earns its place beside
    `transcode`: copying a fine video stream while re-encoding only the audio is the cheap fix and the
    frequent one.
  - ⚠ **A claim this repo made twice and had WRONG:** that shipping an engine on mobile duplicates hardware
    decoders and burns battery in software. **False** — LibVLC has MediaCodec and VideoToolbox
    hardware-acceleration backends, so an engine is strictly a SUPERSET of the platform player, not a power
    trade. The measured 0 MB was real; the reasoning attached to it was not.
  - **Cost, measured:** +42.2 MB per Android ABI (arm64), +33.5 MB for the iOS device slice, ~25–30 MB
    pruned on Windows. Acceptable for a media application, not for one that plays the odd notification
    sound — **which is exactly why this is the APP's choice and not the kit's.**
  - **The kit still ships no engine, and its own build must stay light** (owner: *"so our build can go
    together does not rely on heavy resources"*). Referencing the three natives would add **~823 MB of
    restore** to every `verify` and every CI run, so the kit references **no engine package at all**. Obtain
    one by REFERENCING UPSTREAM, never vendoring — the `videolan` story is complete and first-party on all
    three platforms, so the backup case does not arise.
  - **The gate proves the CONTRACT, not the engine.** The sample exercises the surface through the platform
    player at 0 MB; proving a real engine end to end is an on-demand `devtools/_*` probe. **State plainly in
    the docs which half a green gate covers.**
  - **Licence is the consumer's to settle, not the kit's** — LibVLC is LGPL 2.1+, some plugins are GPL, and
    ffmpeg is LGPL only when built without its GPL parts. Referencing nothing means never choosing for
    anyone.

- **D43 — the media contracts split by DEPENDENCY, not by feature name. "Thumbnail" is two mechanisms and
  gets two homes.** ⚠ Read "two homes" as two FOLDERS; the package family it distributed across is gone.
  Thumbnails are still unbuilt (D15 — no consumer has asked twice).
  **The honest axis is what each operation NEEDS:**

  | Capability | Needs | Windows | Android | iOS |
  |---|---|---|---|---|
  | **Probe** — duration, dimensions, streams, codecs | a demuxer | ffprobe / engine | `MediaMetadataRetriever` | `AVAsset` |
  | **Frame grab** — a still at time T | a **decoder** (same as playback) | engine / ffmpeg | `MediaMetadataRetriever.FrameAtTime` | `AVAssetImageGenerator` |
  | **Playback surface** | a **decoder** + a view | engine + control | `SurfaceView` | `AVPlayerLayer` |
  | **Image resize** | an **image codec**, NOT a media decoder | `System.Drawing`/WIC | `BitmapFactory` | `UIImage` |

  So probe + frame-grab + surface are ONE family (they share the media decoder) and image resize is its own
  contract. The playability verdict stays portable logic over a probe result — a pure function in
  `Shenora.Modules.Media`, per stream (D42).
  - **Verified by compiling: image resize needs no extra package on any platform, so thumbnails cost 0 MB
    everywhere** — unlike playback. ⚠ **Android trap:** `Bitmap.CompressFormat.Webp` is obsoleted on API 30+
    while this kit's floor is API 21, and `CA1422` is an ERROR here, so a WebP encoder must handle both or
    use JPEG.
  - 🔴 **Do not ship a `Thumbnail` type that spans both mechanisms** — that is D35's
    same-word-different-guarantee mistake in miniature, and the harvest already found "thumbnail" meaning
    *extract a frame* in one sibling and *resize an image* in another. Name the mechanism (D22).
  - **The APP unifies them, not the kit.** "Give me a thumbnail for this library item" needs to know whether
    the item is a video or a picture, and only the app does.

- **D44 — the media URL names NO origin, and the two mobile shells need OPPOSITE response BODIES.** Measured
  on devices; `samples/Shenora.Sample.Maui/MediaRangeProbe.cs` re-measures it.
  - **The URL is a RESERVED PATH on the page's own origin, reached by a RELATIVE url** (`/<reserved>/?src=…`)
    — not a custom scheme and not a virtual host. Neither obvious answer works on both shells: Android
    intercepts `app://` and then its media pipeline **refuses** it (`MEDIA_ERR_SRC_NOT_SUPPORTED`, instantly,
    even for a correct 200), while iOS intercepts **only** `app://` and lets an https host reach the real
    network. The page's own origin is intercepted and media-capable on both **by construction**. The path
    must be reserved: it shadows the bundle.
  - **`e.PlatformArgs` is NOT required on either shell.** The portable `SetResponse` overload carrying
    headers exists on both mobile TFMs. The belief that it did not read ONE overload as the whole set and
    put a per-platform implementation on the critical path before any contract existed. **Cost of believing
    it: a whole design constraint. Cost of checking it: one build.**
  - 🔴 **THE ASYMMETRY, and it is the load-bearing one: Android's seam applies the `Range` START ITSELF to
    whatever body it is handed (and ignores the range END); iOS passes the body through verbatim.** So the
    same portable request needs an UNSLICED body on Android and a SLICED one on iOS. Getting it wrong is not
    graceful degradation — a sliced body on Android has the offset applied twice, and a player asking for a
    file's tail receives an empty body and **retries the identical range forever**.
  - ⚠ **The trap this leaves for whoever implements it: the wrong choice looks CORRECT.** A faststart file
    only ever requests `bytes=0-`, where the double-skip is a no-op — so the naive implementation plays
    perfectly on the file everyone tests with and fails on every file whose index is at the end. **Test with
    the control pair** (same content, `moov` at the front vs at the end) and assert the returned BYTES from
    an explicit `fetch`, never a `<video>` element, which can only report "no supported source".

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
    Containment, the SSRF guard, a cache, logging what an opaque payload decoded to, a metric — each WRAPS
    the next rather than terminating, and a "first non-null wins" list cannot express any of them. **The kit
    already made this choice once:** `IMessageDispatcher` is a composable pipeline over one transport, so
    the precedent, vocabulary and review instincts all transfer.
  - **Anything the family SHARES belongs in Core, not in a member.** `DerivedCacheKey` keys any derived
    artefact — a thumbnail, a probe result, a rendered sheet — and its name says what is cached rather than
    which feature happened to need it first.
  - **The registry and composition are in Core, not in each shell** (`WebViewResourcePipeline`). Writing the
    back-to-front chain build three times is three chances to invert someone's routing, and it is the only
    way any of it is TESTABLE — order, decline-and-fall-through, wrapping and independent removal are all
    provable with no webview. A shell implementation is then just the platform's event glue, and the two
    genuinely differ: **mobile must resolve the pipeline SYNCHRONOUSLY** (both platforms need status and
    headers when the event returns) **while the desktop has a deferral and must not block the UI thread.**
  - **On the desktop the bundle and the pipeline share the page's origin, and the BUNDLE wins** — asked
    first, served synchronously and inline (deferring the MAIN DOCUMENT stalls the initial navigation, which
    only ever reproduced in production); a path it does NOT have falls through to the pipeline instead of
    404ing. ⚠ **That is the OPPOSITE order from mobile**, where the platform serves the bundle and the
    pipeline only sees what it declined. **The rule that holds on both: keep interception paths off bundle
    paths** — a route that collides with one is relying on a difference between shells.
  - ⚠ **In DEV the page lives on the Vite server, so that origin must be filtered too.** WebView2 raises
    `WebResourceRequested` only for registered patterns; in production the bundle's pattern covers the
    page's origin and in development nothing did, so a file route would work packaged and 404 through every
    day of development. A blanket `"*"` was rejected, and a `ProductionUrl` origin is deliberately NOT
    filtered — that profile has a real in-process HTTP server, and letting middleware shadow Kestrel's
    routes means two servers for one origin, silently disagreeing.
  - **`Sliced` on the desktop is MEASURED**, both directions: the probe answers `bytes=3-7` and the page
    reads `DEFGH`; sabotaged to `Unsliced` it reads 1000 bytes starting at `A`. It also pins containment (a
    traversal to a file that really exists → 404) and that the bundle still wins on the shared origin.
  - **The page's half is ONE npm package for every shell:** `mediaUrl(payload)` returns a RELATIVE
    `<route>?<base64url>`, and `ShellCapability.LocalFiles` says whether the host can serve at all.
    ⚠ **The handshake must advertise NEITHER the url scheme NOR the range delivery** — a page told "you are
    on iOS, use `app://`" is branching on platform again, and a relative url already resolves correctly on
    each shell.

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

- **D48 — the file-operation engine is its own LAYER hanging off Core, not part of it.** Owner: *"because
  this include file operation so we should have a sperated library/package for this"*.
  ⚠ **Its PACKAGING conclusion is reversed — D55 removed the package, D65 made it the
  `Shenora.Engine.Files` namespace. Read the layering; ignore the package ids.** What this entry proved is
  not about packaging and is cited from fourteen places.
  - **The measurement that decided it:** the engine was 2,244 lines — **34 % of `Shenora`** — and all but
    ~500 of it is the update engine (journalled queue, path leases, the manifest pair, the staged updater).
    Core is the thing *every other package references*, so a phone app that hosts a page and plays a file
    was carrying a self-updater it will never call.
  - **This APPLIES D37's test rather than overturning it.** "I am on Windows" is not a choice you make per
    feature; **"I am building an app that mutates a file tree or self-updates" IS one.**
  - 🔴 **The edge points `engine → Core`, CHECKED rather than assumed** (every type logs through
    `AppCallback`), and that direction decided the leftovers — which are the interesting part:
    - `Files`/`FileReplacement` stay in Core, because Core's own `IFileDialogs.SaveAsync` default calls
      `Files.BeginReplace` and moving them would invert the edge.
    - `PathClaims` stays — it is a claim SCOPE built on the mission types. Scheduling vocabulary that
      happens to be about paths, not a file operation.
    - `IFileLockInspector`/`FileLockHolder` were SPLIT BACK OUT of the move. "Who is holding this file
      open?" has a genuinely different answer per platform, so it is a portable contract with a shell
      implementation. **A shell must be able to implement a Core contract without reaching outward for it**
      — leaving it in the engine would have forced `Shenora.Windows` to reference the engine for one
      interface. `IPathLocker` went the other way for the opposite reason: advisory lock files are portable,
      so contract and implementation ship with the engine that uses them.
  - **Compression is the first member of the family, and the naming is the lesson.** Zip needs no native
    engine; 7-Zip or rar would each drag real shipped bytes, so each would earn its own home rather than a
    flag. Naming it after the framework's own area also made the TYPES smaller — `ExtractionResult`, not
    `ArchiveExtractionResult`. The retired `Shenora.Archives` over-claimed (everything in it is
    zip-only): **a package name that has to be explained by its type names is the wrong name.**
  - ⚠ **The strongest objection, raised in review and REJECTED: it holds TWO clusters that do not touch.**
    Checked, not assumed — `UpdateStage` references no queue, no `FileUpdate`, no `FileChange`, no
    `IPathLocker`. They stay together because the consumer story is ONE ("my app owns a file tree on disk
    and must change it without corrupting it"), and because **the cost of unused code inside something you
    CHOSE to add is close to nothing, unlike the same code in Core** — that asymmetry is the whole of this
    decision and it does not repeat one level down. **The trigger to revisit is a real adopter that wants
    one and refuses the other**, not the observation that the call graph has two components.
  - ⚠ **The gate that did NOT catch the gap this created:** compression shipped with no `ARCHITECTURE.md`
    entry at all and every gate stayed green, because `doc-drift` checked dependency ARROWS, retired names
    and dangling links — never *"is this shipped package described anywhere"*. It now requires every packable
    project to be named in `README.md`'s table and in `ARCHITECTURE.md`, case-sensitively and with a
    trailing-identifier fence so `Shenora.IO` could not be satisfied by `Shenora.iOS`.

- **D49 — retired package ids stay LISTED until 1.0; pre-1.0 ids are retired in ONE deliberate pass.**
  Owner: *"its okay let them be there we will retire all pre-1.0 packages once we got a fully working app
  framework working"*.
  - **What this settles:** the three ids D37 merged away are still listed and undeprecated on nuget.org.
    That is a CHOICE with a trigger, not an overdue chore — which matters because **a deferral nobody wrote
    down gets rediscovered as a defect** (this one survived four releases and was re-raised in review).
  - **Why deferring is the better trade:** unlisting costs an API key, a `--apply` run and a web-UI pass per
    id, and buys nothing while the id set is still moving — three ids went with D37, one came and went
    inside a day, and D48 added two. **The set is not stable yet, so the cleanup is not ready to be done
    once.**
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
    wrote the same three files: `updater.cpp` (234/145 lines) and `dotnet_runtime.cpp` (170/116) are generic
    → the LIBRARY; `main.cpp` (142/76) is per-app → the TEMPLATE. **The library is the larger half.** What
    stays per-app is smaller than "a launcher": exe name, icon, version resources, code signature, topology
    constants, failure-UI wording — embedded on Windows, a `.desktop` file on Linux. **That asymmetry is a
    build step, not a source fork**, which is what makes one shared tree honest.
  - **Rust was evaluated properly and lost on the criterion the owner named.** The question put was whether
    it helps **NuGet packing**. It does not, at all: a `.nupkg` is a zip, `runtimes/{rid}/native/` is a
    folder convention, and MSBuild's RID copy cannot tell what compiler produced the bytes. ⚠ A
    cross-compilation advantage was also claimed and **overstated** — a musl target still needs a linker for
    the target, and once each target builds on its own CI runner the advantage disappears for both languages.
  - **D8 decides it:** two proven C++ implementations exist in production, one carrying an incident
    expensive to re-earn (omitting the launcher from the new manifest made the OLD launcher delete the
    freshly-copied new one). **The two-consumer bar is met in C++ specifically**, not for "a launcher" in
    the abstract.
  - **The durability argument, in the owner's framing:** C++ **will not lose support in any relevant horizon
    and its platform coverage keeps growing** — AI workloads pulled real current investment back into the
    toolchain. For an artifact whose whole job is to run on a machine you do not control, before anything
    else is installed, *"there is a mature compiler and a stable ABI for this platform"* IS the requirement.
    The toolchain is also reusable: LibVLC, ffmpeg, image codecs and any inference shim are all C++-shaped.
  - ⚠ **C++ was NOT chosen because Rust is worse.** Revisit trigger: a future native component with no C++
    prior art and no native engine to talk to — decide that one on its own evidence rather than by citing
    this entry.
  - **Shape:** one source tree; `std::filesystem` (C++17) with Win32 specifics behind a thin platform
    header; **CMake**, so MSVC and gcc/clang build the same tree; per-RID binaries from a CI matrix. No
    cross-compilation anywhere.
  - 🔴 **The JSON parser is a CONFORMANCE requirement, not a taste one.** It must agree with
    `UpdateManifest.Parse`, including the two rules already sabotage-verified on the C# side: **paths
    normalise separators AND case; hashes compare case-insensitively.** Getting either wrong makes a release
    look either fully changed or fully unchanged.
  - **Measured size: 322 KB on Windows, 46.8 KB on Linux.** Windows is ABOVE the original 150–300 KB guess
    and Linux is 7× below it; the whole difference is the **statically linked CRT** on Windows — a
    deliberate trade, because a launcher needing a VC++ redistributable has the same bootstrap problem it
    exists to solve.
  - ⚠ **"Both platform files always compile" did NOT mean both TARGETS compiled.** `platform_posix.cpp`
    built clean under MSVC for days and failed instantly under gcc (`'all_of' is not a member of 'std'` —
    MSVC drags most of the standard library in through other headers). **An `#ifdef`-guarded body is only
    checked by the compiler that takes that branch.** Reproduce Linux locally with the `gcc:13` container
    line in `CMakeLists.txt` rather than round-tripping a release.
  - ⚠ **Calling it a library makes the verification problem sharper, not softer** — the kit owns its
    correctness while `dev.mjs verify` compiles none of it. The answer is the Node harness driving a
    PREBUILT launcher over sandbox directories, so an adopter's CI runs THE KIT'S conformance suite against
    THEIR binary. **`README`/`ADOPTION` must say plainly that this repo's gate does not compile it.**
  - **Bounded, so nobody over-builds it:** a **self-contained** app needs no launcher at all —
    `UpdateStage.ApplyAsync` already overlays, removes and clears in portable .NET. This serves
    framework-dependent apps, where the runtime may be absent and files may be held open.

- **D51 — anything the kit SHIPS AS BYTES must be MIT-compatible. The kit never redistributes a copyleft
  binary; an app that wants one supplies it through a `ResourcePack`.** Owner: *"we are on MIT so we should
  build one compatible with MIT"*.
  - **The asymmetry that forced it:** an LGPL ffmpeg is fine inside a closed-source app — dynamically link,
    attribute, keep relinking possible. Shipping the same binary from HERE makes the kit the
    **redistributor** and hands attribution and relinking duties to every consumer, and to anyone who
    redistributes theirs. **A package whose licence expression reads `MIT` while its payload is LGPL is a
    surprise an adopter's compliance review finds later, at the worst time.**
  - **The rule:** bytes the kit ships must be MIT / BSD / Apache-2.0 / ISC / public-domain. **GPL never** —
    `--enable-gpl`, x264 and x265 relicense the consuming APP, the one outcome a devkit must never cause.
    LGPL binaries not from a kit package either: the licence is fine, **the redistribution is what is wrong**.
  - **What an engine should be, in order:** (1) **the PLATFORM's own codecs** — zero bytes, zero licence
    weight, and the OS's patent problem. ⚠ Measured limit: AOSP's set is narrow (`aac flac mp3 opus pcm
    vorbis` plus an AAC encoder), *barely wider than the WebView's own*, so a platform-only engine has a
    small benefit window on Android and must say so. (2) **Permissive libraries** where the platform
    genuinely lacks something — openh264 (BSD-2), dav1d (BSD-2), libvpx (BSD-3), Opus (BSD-3), libFLAC
    (BSD-3 — the FLAC *tools* are GPL, the library is not), Apple's ALAC (Apache-2.0). (3) **Never** —
    x264/x265 (GPL), fdk-aac (not OSI-free), LAME (LGPL).
  - 🔴 **PATENTS ARE NOT COPYRIGHT, and a permissive licence does not settle them.** openh264 being BSD does
    not grant H.264 patent rights; Cisco's royalty coverage attaches to *their* prebuilt binaries fetched at
    runtime, not to one built from source. "MIT-compatible" answers the licence question and leaves the
    patent question open — the owner's call per shipped codec, and this entry is not legal advice.
  - 🔴 **A closed-source app is NOT at risk from LGPL — that is the entire difference between LGPL and
    GPL**, and a decision was nearly taken in the belief that it was. A proprietary app may link an LGPL
    library and stay proprietary, provided it attributes it and preserves the user's ability to RELINK.
    **The real reasons to prefer permissive are narrower:** iOS static linking makes the relink condition
    genuinely awkward (satisfying it means shipping object files every release, so a permissive licence
    removes the *condition*, not just the paperwork); per-release attribution work buys nothing when a
    BSD/Apache component would do; and **for the KIT it is not a preference at all but a requirement,
    because the kit REDISTRIBUTES.**
  - ⚠ **ffmpeg has THREE licence states, and the third is the trap.** The licence is not chosen, it is
    DETERMINED by what is compiled in, and it follows the most restrictive component:

    | build | licence | distributable |
    |---|---|---|
    | default | LGPL 2.1+ | yes — a closed-source app may link it |
    | `--enable-gpl` | GPL 2+ | yes, but it **relicenses the consuming app** |
    | `--enable-nonfree` | non-free | **NO — may be built and used, may never be distributed** |

    🔴 **`--enable-nonfree` is the one to watch**, because it is what someone reaches for to get fdk-aac for
    better AAC — and the result works perfectly, passes every functional gate, and cannot legally be
    shipped, with nothing in the build output saying so. **A licence failure no test can see is exactly the
    class this decision exists to stop.** Guard BOTH flags in any build script.
  - **The operational test is DISTRIBUTION, not "is it in the code base".** Three ways it leaks without ever
    being in the repo, all worth checking before a release: a package that **fetches and embeds** it during
    build or install (repo clean, artifact not); a **sample or test fixture** vendoring a binary for
    convenience; **release assets** attached to a tag.
  - **What is entirely free, so this is not over-applied:** calling ffmpeg as a separate PROCESS, defining
    an interface an app implements with it, documenting the wiring, and staging an app-supplied archive
    through `ResourcePack`. **There is no copyleft-by-association** — an interface written specifically for
    an LGPL implementation is fine when no implementation ships.
  - ⚠ **ffmpeg cannot be relicensed by us**, so "an MIT engine" means *not ffmpeg*. Anyone proposing "just
    ship a small ffmpeg" has proposed an LGPL redistribution with extra steps.
  - ⚠ **And it changes nothing for the APP.** An app shipping ffmpeg inside its own binary still owes
    attribution, licence text and the relink provision. "Not in the kit" makes the KIT clean and does
    nothing for the consumer.

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
    platform calls and a container — zero bytes, zero licence, no codec written. **If a proposal cannot be
    described that way — a translation between what the user HAS and what the web ACCEPTS, using what the
    device already does — it is out of scope.**
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
  - **The lens for every capability question**, and it settles them faster than arguing each on its merits:

    | | |
    |---|---|
    | **.NET does** | the platform work — lifecycles, OS surfaces, files, codecs, background execution, anything needing a real thread or a real handle |
    | **React does** | the interface — what it is good at, and the reason an app chooses this stack |
    | **the kit provides** | the SEAM between them: a portable contract, one implementation per shell, and the IPC that carries it |

  - 🔴 **So the question for a proposed feature is not "is this useful?" but "can React already do this?"**
    If it can, the kit is competing with the web platform and loses. Capacitor and Electron give you a
    webview and a JS bridge, so their ceiling is the web platform plus a plugin ecosystem; this kit's
    ceiling is .NET's — the whole BCL, real threads, real handles, the platform SDKs. **Anything a plugin
    ecosystem already does well is not where the value is.**
  - **The instance that produced it: the PAGE owns playback and it should not.** An app puts a
    `<video src>` in its React tree and the host only serves bytes, so the ceiling is whatever the webview
    can do; and `IPlaybackSession` publishes Now Playing *about* something the page is doing, so the two
    can disagree with nothing to reconcile them. The answer is a lifecycle the host owns and the page
    drives — `IMediaPlayer`: load, play, pause, seek, rate, position, ended, error, one implementation per
    shell, reported over the existing IPC. **Read that way the player is not a media feature at all** — it
    is the same shape as `IFileDialogs`, `IWebViewInterceptor` and the safe-area insets.
  - ⚠ **This BOUNDS the translation layer; it does not delete it.** `Mp4Remuxer` and the conversion
    pipeline stay — serving files to a `<video>` is a legitimate and common shape. What changes is that
    translation stops being the answer to *"the webview cannot play this"*.
  - 🔴 **Its conclusion about SEGMENTATION is reversed — do not cite it.** "A native player opens the file
    directly, so the kit ships no default segmenter" was true of this entry and is not true now: **D71 made
    streaming the media tier's PRIMARY path** and the kit ships `DefaultSegmentEngine` (D75).
  - ⚠ **"Only a native player survives backgrounding" is FALSE and must not be cited either.** iOS pauses a
    `<video>` the moment the app leaves the foreground — the video track cannot render — but an `<audio>`
    keeps playing: 16.01 s of audio across a 16.0 s window on an iPhone 17 Pro, given
    `UIBackgroundModes: [audio]` and an active `AVAudioSession(.Playback)`. The kit's own sample does it.
    **The decision stands on its other two legs**, the system surfaces and the formats.

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
    relayered them to `src/Shenora/Engine/Files/` (`Shenora.Engine.Files`) and
    `src/Shenora/Modules/Update/Compression/` (`Shenora.Modules.Update.Compression`) — compression belongs
    to UPDATE, which is what it is for. **So the D47 break is two `PackageReference` deletions AND a
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

- **D57 — there are no design docs. A design doc is scaffolding: once the thing is built, `ARCHITECTURE.md`
  says what it is and this file says why, and the third copy is the one that goes stale.** (2026-08-07,
  applying the 0.2.0 cleanup's precedent to the five dated docs that outlived it.)
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
    that should be just the web? I think we have bit over engineered this."*). It had exactly ONE
    production implementation plus a test fake, and `IMediaPlayer` is already the seam that separates the
    web player from the native one. `MediaPlayer` talks to the page over `IEventBus` instead.
    ⚠ **The generalisable tell: an interface whose only implementations are the real one and a test
    double.** A test fake is not a second consumer — it is the *cost* of the abstraction, not evidence for
    it. Ask what the second REAL implementation is; if the answer is hypothetical, use the concrete type.
  - **The page is the only clock.** Position and duration come from `MediaPlayer.Report` and nowhere else,
    because the element is the thing actually advancing. ⚠ Report on TRANSITIONS: `timeupdate` fires
    ~4×/second and forwarding it costs battery to tell the host something it can extrapolate.

- **D59 — the converter's job, stated exactly: it bridges what the DEVICE's hardware can decode to what
  that device's WEBVIEW will accept. Nothing wider.** (Owner, 2026-08-07: *"the default convertor is
  actually bridging the gap between the device hardware to its webview, and if a better encoder/decoder
  comes in by adopter app, they can hook that into the same pipeline without additional code."*)
  - **This is sharper than D52's framing and supersedes how it was read.** "Make a file the webview cannot
    play, play" invites a treadmill of formats; the real target is a DELTA between two measurable things
    already in the code — `IMediaCapability` asks the device what it decodes, `MediaPlaybackPolicy` says
    what the element accepts. **Where the device cannot decode it either there is nothing to bridge, and
    refusing is correct** — which is also why the kit ships no engine (D51): an engine would be claiming to
    beat the hardware.
  - 🔴 **The claim was FALSE when it was made, and the defect was INVISIBLE.** The remuxer overload every
    adoption example wired passed `conversion: null`, so a shell that had registered a perfectly good
    stream conversion never had it called. **Nothing failed:** the remux succeeded and dropped the
    soundtrack, so the symptom was a film that played SILENTLY. ⚠ **Worth the entry for the failure MODE,
    not the fix** (D63 generalises it): when a feature is "supplied by the app and consulted by the kit",
    the test that matters is *does the kit actually call it?*, and that test did not exist.
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

- **D61 — a capability is adopted through ONE `Use…` call that defaults everything the kit may choose on
  the app's behalf.** (Owner, 2026-08-07: *"its okay as long as the adopter when using get similar
  treatment as UseMediaPlayer"*.)
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
  - **How to find them, because the method generalises:** enumerate every public contract, then ask of each
    *"is there an implementation, and does anything consume it?"* Two greps. Most contracts are
    legitimately unregistered — options-supplied collaborators (`IMissionPolicy`), per-webview objects
    (`IWebViewInterceptor`), app-supplied seams (`IUpdateSource`) — so the signal is narrow: **a kit-built
    implementation with no consumer.** ⚠ Re-run it after any pass that adds seams.
  - 🔴 **The FOURTH is a variant worth naming separately: the PLAN promises what no engine implements.**
    A device capability query marked video encodable, so the planner answered `Transcode "transcode
    (video)"` while nothing could perform it and the track was silently dropped. **Those first three were
    silent; this one SAYS THE WORD** — a plan naming a conversion is read as a promise. **So the audit
    question has a second half:** not only *"does anything consume this contract?"* but *"does anything
    IMPLEMENT what this plan names?"* `WithDeviceEncoders` now intersects the device's answer with what the
    app can actually convert, defaulting to the kit's own reach (audio); an app that registers an
    `IMediaStreamConversion` passes the kinds it can really perform and gets them back honestly.
    ⚠ **The grep did not find it — the owner's question about video did.** That is the limit of the method.

- **D64 — the framework is ON BY DEFAULT. `Use…` CONFIGURES; it does not enable. The only per-platform
  call is the shell's, and it exists to inject implementations.** (Owner, 2026-08-07: *"this is a full
  react+.net application framework … those `use` function basiclly just a way to override or configure"* ·
  *"because non-of them will work without frontend ask via ipc/routing"*.)

  ### 🔴 THE CORE IS THE FIXED MESSAGE PIPELINES. Everything else is an interceptor on one of them.

  (Owner: *"those media file mission should be like interceptors to the proper app route ipc and events"* ·
  *"so those 'fixed' message pipelines are the cores"*.) **Read this first — the rest follows from it.**
  The framework is not a bag of capabilities. It is three fixed pipelines, and they are the product:

  | Pipeline | Contract | What flows |
  |---|---|---|
  | **Routes / resources** | `IWebViewInterceptor` (`.Use`) | the page asking for BYTES — a file, a media URL, a segment |
  | **IPC** | `IMessageDispatcher` (`UseModule`/`UseRoute`/`UseLogging`/`UseErrorHandler`) | the page asking for an ACTION, correlated |
  | **Events** | `IEventBus` | the host TELLING the page, batched and one-way (D23) |

  Media, files and missions are interceptors plugged into those pipelines, not services sitting beside
  them — which is also **why on-by-default is safe: an interceptor nothing routes to is inert BY
  CONSTRUCTION.** It is not "cheap", it is genuinely nothing until a request reaches it.
  - 🔴 **So the argument is a SAFETY one, not an ergonomic one.** None of these capabilities DOES anything
    until the frontend asks over IPC or requests a URL, so opt-in gating buys nothing real — it only
    guarantees every app re-types the same block and one of them forgets. The gate that matters is the page
    making a request; the boundary that matters is CONTAINMENT (`AllowedRoots`, `WebViewFileOptions`),
    which stays fail-closed and is untouched by this.
  - **The precondition, stated so it can be checked:** *registration is free; construction is lazy; nothing
    touches a disk, a thread or a handle until something asks.* `UseFileSystem` built its journal and locker
    at `Use…` time and `Paths.DataArea` CREATES the directory it names, so as a default it would have
    provisioned `journal/` and `locks/` in every app that never mutates a file. Both build inside the DI
    factory now, and a test asserts the directory does NOT exist until the queue is resolved.
    ⚠ **Defaults must land on the instance RESOLVED FROM DI, never the one captured at `Use…` time:**
    `TryAddSingleton` no-ops when the app registered its own options, so defaulting onto the captured object
    silently configures something nothing will ever read.
  - **The kit's own IPC modules live under a RESERVED `SHENORA.` prefix** (owner's call) — read the module
    constants for the live set. That is what makes shipping a module by default safe: **the app is free to
    own `MEDIA`, `FILES` or any other unqualified name**, and a consumer can still subscribe to the kit's.
    ⚠ Everything moved, including the two already shipped: one rule with two grandfathered exceptions is a
    rule nobody can apply from memory, and it was a wire break taken deliberately under `### Breaking`.
  - 🔴 **Where a platform CAN do it, IMPLEMENT it — a refusal is the last resort, not the default answer**
    (owner: *"we should try to implement a default if the platform can support this is to close the web
    gap"*). A stub that refuses on two of three platforms leaves the framework claiming a capability it
    declines to provide, which is the same outcome as shipping nothing. **A refusal is still right where the
    platform genuinely cannot — and then it must be EXPLICIT** (`ShellCapability.NotSupported`), never an
    absent registration: D63's rule reaching its conclusion, that the framework must never answer a question
    by not being there. ⚠ **The test is "can this platform do it?", not "have we written it yet?"** — an
    unwritten implementation is a TASK, and recording it as a refusal freezes a gap into the surface.
  - **`UseWindows()` / `UseAndroid()` / `UseIOS()` are the one call that remains, and they INJECT.** They
    were `UseWinForms()` and `UseMobile()` — a UI-FRAMEWORK name and a CATEGORY name, while the packages
    they ship from are `Shenora.Windows`, `Shenora.Android` and `Shenora.iOS`. **D37's law had never reached
    the method names**, and a platform is the one thing an adopter genuinely picks.
    - ⚠ **It costs the shared-baseline trick, ACCEPTED rather than worked around.** `UseMobile` lived in the
      shared source compiled into both mobile packages, whose surfaces are deliberately IDENTICAL — which is
      what lets the Android API baseline gate iOS from a Windows host. Two platform-named methods make them
      differ by exactly that method. **Do not contort the gate to preserve it**; revisit WITH the build
      toolkit D56 puts in scope.
  - 🔴 **`Use` vs `Add` — THE TEST: does the method touch a PIPELINE, or only the container?** (Owner,
    2026-08-08: ***"`Use` means a wider configuration including its pipeline interceptor, and `Add` only
    means the service collection level."***)

    | entry point | verb | why |
    |---|---|---|
    | `UseMessageDispatcher` | `Use` | composes the dispatch pipeline — error handler, the app's configure callback, the lazy module mapper |
    | `builder.UseMissions` / `UseFileSystem` / `UseMediaPlayer` / `UseRequests` | `Use` | the application's own setup for a capability, including the module or route it contributes |
    | `AddIpcModule<T>` | `Add` | `AddSingleton<IIpcModule, T>` and nothing else — the dispatcher builds the stage later |
    | `AddShenoraFileDialogs` | `Add` | one `TryAddEnumerable`; pure registration |

    ⚠ **This has been argued THREE times**, which is why the RULE is written here rather than the answer —
    a first pass renamed all four `Add*` to `Use*` on the coarser "these are all middleware-ish" reading and
    three were reverted the same day. **A module REGISTRATION is not a pipeline stage**, even though a stage
    is eventually built from it. ⚠ **A task that changes public surface must cite its `D<n>`, and that entry
    must be read before the task is done** — a `TASKS.md` entry demanding the opposite outlived the decision
    that killed it and was nearly executed.
    - ⚠ **RECEIVERS never moved, and two hard constraints say so:** `IShenoraModule.ConfigureServices`
      receives only an `IServiceCollection`, so `AddIpcModule` must stay reachable there; and composing IPC
      over a bare `ServiceCollection` with NO builder is a supported shape the composition tests exercise,
      so `UseMessageDispatcher` stays an `IServiceCollection` extension. **The receiver follows a
      capability's real dependency** — builder-level when it needs `builder.Paths`/`Environment`, the
      container otherwise.
  - **A CORE module is CONFIGURED by the application's setup, never added to it** (owner: *"this entire
    framework cannot work without those core modules"*). `Build()` registers unconditionally, so exposing an
    "add" offers a choice that does not exist — hence request tracking's container registration is
    `internal` and the app-facing surface is `builder.UseRequests(x => …)`.
    - **Each takes an `(options, services)` overload** so one call CONFIGURES and SUBSTITUTES. ⚠ **ORDERING
      IS NOT THE MECHANISM** — Microsoft DI resolves the LAST descriptor, so an app wins from either side of
      a `TryAdd`; running the callback first buys a SINGLE registration with no kit default shadowed behind
      it. Measured by sabotage: moving the callback to run last left every test green, which is how the
      first doc claim was caught being wrong.
    - ⚠ **`AddShenoraFileDialogs` is the opposite answer to the same question**, and deciding them
      separately is what produced two answers: it is SHELL wiring called from `Shenora.Windows` AND
      `Shenora.Mobile`, so it stays PUBLIC (a `ProjectReference` grants no `internal` access).
  - **The pipeline surface belongs on `ShenoraApplication`, and that is the fix for the two-phase call this
    repo used to apologise for.** `interceptor.UseMediaPlayer(services)` existed because the interceptor is
    created WITH the webview — a real constraint, and exactly the split ASP.NET draws — but ASP.NET's second
    phase is `app.Use*()`, where the app already carries the provider, while ours made the CALLER fetch the
    interceptor and hand the provider back. `app.UseFiles(…)` and `app.UseMediaPlayer(…)` ship.
    ⚠ **The semantic is deliberate: `app.Use*()` describes the pipeline for EVERY webview the app hosts**,
    the way an ASP.NET pipeline serves every request — better than per-interceptor wiring, where secondary
    windows and session browsers got nothing unless wired again by hand. The per-interceptor call stays for
    the case that genuinely wants one pipeline to differ.
  - 🔴 **Defaulting a registration immediately found a real crash, which is the argument for tripwiring a
    default at all.** `MissionScheduler` implemented only `IAsyncDisposable`, and Microsoft DI's synchronous
    `ServiceProvider.Dispose()` THROWS when it holds an async-only singleton — a bug this kit had already
    paid for once, and defaulting the scheduler would have handed the same crash to *every* adopter. Fixed
    with a synchronous `Dispose()` that cancels pending work and signals shutdown WITHOUT awaiting in-flight
    bodies, because awaiting there is a blocking wait on whatever thread disposes, routinely the UI thread.
    **The rule this leaves: the kit must never register a singleton it would be unsafe to dispose the
    documented way.**
  - **The evidence that the old shape was wrong is the kit's OWN sample**, which hand-constructed the
    scheduler and the update queue inside `AddSingleton` lambdas with a comment claiming the kit shipped no
    DI extension for it. **A reference app that has to write the framework's own composition is the
    finding.**

- **D65 — THREE LAYERS, and the package is called `Shenora`. "Core" means the WIRE between .NET and the
  web — nothing else.** (Owner, 2026-08-07, redefining it after D64 exposed that the word had no edge:
  *"the Core is the main wire between .net and web …, on top of that is pure logic layer, like Mission,
  Files, and then we have what we call 'features' Media, Dialog"*.)

  **In one line each** (owner's own framing, and the form worth remembering):
  **core is the CONTRACT · the engine is the BRAIN · modules BRIDGE the gap between .NET and the web.**

  🔴 **The layer names ARE the namespace segments, so the layout cannot lie about the architecture:**

  | Folder | Namespace | Holds |
  |---|---|---|
  | *(root)* | `Shenora` | the composition root — the one place allowed to reach every layer |
  | `Core/` | `Shenora.Core.Ipc` · `.Events` · `.WebView` · `.Shell` | the contract |
  | `Engine/` | `Shenora.Engine.Missions` · `.Files` | the brain |
  | `Modules/` | `Shenora.Modules.Media` · `.FileDialog` · `.Platform` · `.Requests` · `.Update` | the bridges |

  - 🔴 **The membership test:** *must both sides AGREE on it?* → core. *Is it pure computation the page
    never sees?* → engine. *Does it carry a .NET capability to the page?* → module. **Read mechanically,
    that is "platform half and/or IPC surface"** — a bridge needs one or both, a brain needs neither. It is
    what moves Media out of "engine" (three platform halves and a route) and keeps Missions in (nothing on
    the page asks it anything).
    - ⚠ **An OPTIONAL collaborator is not a platform half.** The file engine consults `IFileLockInspector`,
      which does have a Windows implementation — and the engine works without it. The question is whether
      the thing NEEDS a platform to function, not whether a platform can improve it.
  - **Core holds TWO KINDS of wire, and that is why there are three members rather than two** (owner:
    *"event + ipc is kind the main method we use to wire communication, interceptor is to wire default web
    calls into the .net"*). IPC + EventBus are **EXPLICIT** — page code has to ask. The route interceptor is
    **IMPLICIT**: the page does ordinary web things (`<video src>`, `<img src>`, `fetch`) and .NET answers.
    **It needs no page cooperation at all**, which makes it the highest-leverage wire the kit has — and is
    why serving belongs in the shells (D45) and why bytes were never on the IPC pipe (D62).
  - **Per-platform implementations are EXPECTED in core** (WebView2 `postMessage` vs HybridWebView
    `SendRawMessage`): the contract is core and each platform implements it — D19/D20's law applied to the
    wire itself.
  - **A module's root type is `XxxModule`, not `XxxFacade`** (owner: *"they actually the module root rn"*).
    "Facade" described a thin front over something else; these ARE the module. ⚠ The lexicon gate caught
    `Facade` going unused the moment the last one was renamed — **an allow-list that only grows reviews
    nothing.**
  - 🔴 **AND THE CATEGORY IS SETTLED WITH IT: Shenora is not a web application framework** (owner:
    *"shenora is never mean to be web application framework its desktop + mobile just like other multiple
    platform app framework but its .net + react which no existing replacement can be more capable"*). It
    competes with Flutter, React Native, MAUI, Electron and Capacitor. **That is why the old separate-IPC
    justification had to go:** "a server-backed app might take IPC without a shell" (D10) quietly imagined a
    WEB consumer, and building for one drags the product toward a category it was never in. **A package set
    is a statement about the category.**
  - **So IPC folds into the main package and `Shenora.Core` becomes `Shenora`** — it is the framework, not a
    component of one. 🔴 **The fold is what unblocks everything else:** a feature could not own its own IPC
    module while `ModuleBase` lived in a package `Core` may not reference, which is exactly why D64's
    modules ended up registered from inside `UseMessageDispatcher` — a core knowing the name of every
    feature built on it. ⚠ **Fold first, rename second, each with its own green gate** — landing a namespace
    sweep on top of a package fold makes any failure unattributable.
  - **`Files/` splits, because the name covered two layers.** Atomic replace, path claims, advisory locks
    and the journalled mutation queue are the brain; `UpdateManifest`, `UpdateStage` and compression are the
    app-UPDATE story, which is a bridge — it has a platform half in the native `Launcher` (D50).
  - **The layering was already in the CODE; only the names failed to say it** — the media files depend on
    missions and files and nothing goes the other way, discovered by reading the edges rather than by
    design. That is the strongest evidence the split is real and not imposed.

- **D66 — a long-running request IS A REQUEST. The "operation" was a second identity for one thing and
  collapsed into the IPC contract.** (Owner, 2026-08-08, after rejecting every replacement NAME: *"maybe
  just IpcRequest? so we sharp the original request properly to have this logic into it"* · *"because the
  long run request still a request?"*.)
  - 🔴 **The defect the naming argument uncovered, measured rather than argued.** The former registry minted
    `Id = Guid.NewGuid()` — an id with NO relationship to the `IpcRequest.Id` that caused it — and the
    module context never received the request at all. A page sent request `r1`, the module started operation
    `guid-xyz`, and **the page had to correlate the two itself.** One logical thing, two identities.
  - **XHR is the comparison that makes it obvious.** `XMLHttpRequest` does not hand you a separate
    "operation" object: one request carries `readyState`, `progress` events and `abort()`. The kit already
    had the request; what it lacked was the admission that a request may outlive its response. Progress and
    status are keyed by the REQUEST id, cancel targets the request id, and the module's list view is
    "requests still in flight" — a view over the wire, not a registry with its own vocabulary.
  - ⚠ **The minority case that does NOT fold, and it is the interesting one: work nobody asked for.** A
    scheduled or crash-recovered mission reports progress with no request behind it. **That is genuinely a
    different thing and is modelled as what it is — an event stream, which `IEventBus` already provides** —
    not squeezed into a request-shaped hole. The old design's real fault was making both share one bucket,
    which is why neither had a good name.
  - 🔴 **`waiting`/`resume` LEFT the model rather than being ported.** It was the one part XHR did not
    answer — a request there is in flight or done, never parked — so the tie-breaker was usage: the only
    code that drove it was the sample's own `MissionEventPublisher`, marking a queued MISSION. **Host-
    initiated work, which the bullet above says does not fold.** Keeping it would make "request" cover
    something that outlives the page that sent it and must survive a reload, a cost carried by every request
    for a case that is not one.
  - **Why this is recorded rather than named:** three replacement names were rejected for being words every
    library owns (`Exchange`, `Progress`, and `Operation` itself), and the reason none fitted is that the
    concept should not exist. ⚠ **A naming problem that resists every candidate is a design smell, not a
    vocabulary shortage.**

  ### EVERY request can take a while — the grace period replaces the declaration (owner, 2026-08-08)

  *"we can treat all request can take a while, and use some grace period logic to optimize — say if its
  shorter than 50ms (which is the event bundle timing?) we only take the last state"*. **The 50 ms is not a
  new number:** `NotificationPumpOptions.FlushInterval` already defaults to it, documented as the family's
  measured sweet spot. The grace period IS the flush window the kit already runs on.
  - 🔴 **This removes the last piece of dualism.** With a grace period there is nothing to DECLARE — no
    "is this one long-running?" for a module author to get wrong. Every request has a lifecycle; a fast one
    simply never emits an intermediate state because it finished inside the window. **And the boundary is
    right for a HUMAN, not only for the wire:** ~50 ms is below the threshold where anyone wants a spinner,
    so the page is told *this is taking a while* at roughly the moment it becomes true — which a
    declaration-based design cannot do, because the author must guess at authoring time what only the clock
    knows at run time.
  - ⚠ **Batching is not coalescing, and the difference is what had to be built.** A pump that only
    accumulates would still deliver `running` AND `completed` for a 5 ms request — in one message, but both.
    Coalescing is keyed by REQUEST ID, last-write-wins within the window, which is also exactly what a
    progress feed wants (a hundred ticks in 50 ms collapse to the one the page would have rendered).
  - 🔴 **THE RESPONSE IS NEVER DELAYED. The grace window suppresses NOTIFICATIONS, never the answer.**
    (Owner: *"request completed within grace window should emit the last response immediately so we don't
    get blocked by grace period"*.) A 5 ms request answers at 5 ms and the page sees exactly one thing; a
    500 ms request answers at 500 ms AND gets a `running` notification at the first flush boundary. **Safe
    by construction and worth keeping that way:** `NotificationPump.Enqueue` takes an `IpcNotification` and
    the file has ZERO references to `IpcResponse`, so the two paths cannot be confused by accident.
    ⚠ Anyone building the grace period by parking the RESPONSE has inverted it — that adds latency to every
    fast call in the app to save a notification nobody would have seen.
  - **The window is one knob and it is already public.** Because the grace period IS the flush window,
    lowering `FlushInterval` shortens the "taking a while" threshold at the same time — correct rather than
    incidental, since an app that wants snappier progress also wants its spinner sooner. ⚠ The trade is the
    one the option already documents: lowering it buys fluency with IPC volume.

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
  - 🔴 **Every check in it is one this repo hit on real hardware**, which is the only reason to trust them: a
    piped install reporting `tail`'s exit code so a REJECTED install "succeeded"; an app extension that
    installs happily and never launches because it is provisioned separately — **which a simulator cannot
    catch, since it does not enforce signing**; a device picker that silently takes the first of two phones;
    a log tail that is ~99 % platform chatter without a filter.
  - ⚠ **The config describes the PROJECT; the command line describes the MACHINE.** Proven on the first real
    run: this Mac's Xcode is newer than the installed workload accepts, and the fix
    (`-p:ValidateXcodeVersion=false`) is true of one machine. It goes after `--`, never into a committed
    field — a config that records machine facts silences the mismatch for everyone who clones the repo,
    including whoever hits it when it is the real problem.
  - ⚠ **Do not mistake THIS repo's harness for the CLI's constraints.** iOS signing needs a GUI login
    session, which is a wall for our Windows→Mac ssh harness and not for an adopter: they run
    `npx shenora ios build` on their own Mac exactly as they would `cap`. A session once reported a shipped
    command as an open gap on that confusion.

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
    surface (compact leading/trailing, minimal, expanded regions; text, a symbol, a progress bar, an image).
    A declarative schema covers most of it, and the ones it does not cover are exactly the ones that should
    be written by hand.
  - 🔴 **AND THAT IS WHAT SETTLES THE D13 TENSION** — *can an adopter still express their own look, or have
    we frozen ours into everyone's Island?* Because raw Swift remains a first-class path rather than an
    escape hatch of last resort, **the kit's look is a DEFAULT and never a ceiling.** A config-driven default
    plus an unrestricted manual path is not a design system (D13); it is the same shape as `UseFiles` versus
    writing your own middleware.
  - ✅ **The devkit works on a device** (iPhone 17 Pro): the widget renders and redraws, watched moving
    33 % → 66 % while the host logged an applied update for every one. So the promise stands as first made —
    **one MSBuild property plus four SwiftUI view bodies, and no `.xcodeproj` for the adopter**; no prebuilt
    `.appex` is needed and `ActivityAttributes` stays the app's type.
  - 🔴 **The real limitation belongs to the PLATFORM:** `ILiveActivities.Update` calls ActivityKit
    IN-PROCESS, so swiping the app away freezes the activity at its last value — **the card outlives the app,
    the update loop does not.** Advancing one while the app is not running is what ActivityKit PUSH updates
    are for, and the kit exposes no push token today. Filed rather than built: it needs APNs and a server,
    which is an adopter's infrastructure, so the kit's part is at most surfacing the token.
  - ⚠ **A DIAGNOSIS WHOSE EVIDENCE DOES NOT DISCRIMINATE IS NOT A DIAGNOSIS**, and this entry is where the
    repo paid for it: *"the built binary still has a normal `LC_MAIN`"* was read as proof the extension entry
    point had been dropped. **Every Mach-O executable has `LC_MAIN`, including an Apple-built `.appex`** —
    the fact was compatible with both the broken and the working case, stood for two days as settled, and
    shaped a design decision that had to be withdrawn. Read `entryoff` against the section map instead.

- **D70 — the kit SHIPS A DEFAULT CONVERSION ENGINE, and it is the platform's own codecs. `Convert` is the
  OVERRIDE, for work past the platform's reach.** (Owner, 2026-08-10: *"we can ship default conversion engine
  for each platform mainly focus on hardware support"*, *"but for more complex decode/encoding the
  application use our framework will need to do their work"*.)
  - **What it is.** `MediaConversionOptions.Conversion` takes the shell's `IMediaStreamConversion`, and
    `Convert` — previously `required` — defaults to the kit's remuxer joined to it. With neither, the default
    repairs containers. **No shipped codec bytes**, so D51 is untouched and unamended: wiring decoders the OS
    already has is not an engine. It is D51's own FIRST preference, made the default instead of a recipe.
  - 🔴 **THE BOUNDARY IS THE DESIGN, and it is D59's line: what the DEVICE decodes and its WEBVIEW refuses,
    nothing wider.** That is what makes a default defensible where an engine would not be — **it cannot grow
    into ffmpeg, because past that line the answer is "write `Convert`".** Measured 2026-08-10: an API 36
    Android device decodes mp3/flac/vorbis and NOT ac3/eac3/dts/alac; an iPhone decodes ac3/eac3.
  - ⚠ **Setting both `Convert` and `Conversion` THROWS at registration.** Two ways to say the same thing
    leaves one unread — D63's defect class — and the unread one would be the codecs the adopter believed were
    in use. It throws while the app is composing, with a stack naming the call site, rather than inside a
    mission minutes later.
  - ⚠ **No desktop implementation exists**, so the desktop default is container repair. That is not an
    oversight to fix by symmetry: a Media Foundation conversion is real native work and wants a consumer
    first (D15), and the repair already buys the wrong-CONTAINER case, a genuine webview gap needing no
    codecs.
  - 🔴 **A DROPPED STREAM IS A FAILURE** (owner: *"i dont think fail silently is good — if codec not support
    just not support, but we should not taking what's supported unsupported"*). The route used to commit the
    output and report READY with a `dropped` list beside it, so a film whose soundtrack could not be carried
    was **served and cached as a 200** — a user cannot tell "this film has no soundtrack" from "this device
    cannot play the soundtrack it has". It fails with `MediaConversionErrorCodes.UnsupportedCodec`, names the
    codecs, and caches nothing, so a later request cannot serve the silence as a hit.
    - 🔴 **THE TWO CAUSES OF A DROP NEED OPPOSITE RESPONSES, so the log names which one.** A drop with a
      codec seam supplied is genuinely unsupported on this device; a drop with NO seam means the platform was
      never asked — the adopter's composition, not the file's fault. **A query that could not be performed
      must never be indistinguishable from a negative result.**
    - ⚠ This makes the DEFAULT stricter than the seam it replaced, deliberately. An app that genuinely wants
      a video-only result still has `Convert`, where the policy belongs (D42).

- **D71 — STREAMING IS THE MEDIA TIER'S PRIMARY PATH. The whole file is what streaming LEAVES BEHIND, not
  the thing it produces.** (Owner, 2026-08-12: *"full transcode should be after if we got the full segment,
  its more like a cache/persist logic so the SegementEnegine should be the main focus"*; *"1 planner no
  platform difference"*.)
  - **The inversion.** Materialising an ENTIRE output before serving it makes the first play of anything
    convertible wait for the whole transcode, and a seek is not expressible until the file exists. That
    becomes the TAIL of the pipeline rather than its middle: a source is STREAMED, and when every piece
    exists the artifact is simply a file and playback reverts to `Direct`. **"We have all the segments" and
    "we have the finished file" are one state.**
  - 🔴 **THE PLANNER CHOOSES ON WHAT THE PRODUCER CAN PROMISE, NEVER ON THE PLATFORM.** One planner, and
    `MediaPlaybackAction` already draws the line:
    - `Remux` — output length AND the byte↔time map are derivable from the source index before any work is
      done, so the output is a COMPUTED file rather than a growing one. Any range is serviceable immediately,
      cold, without having produced the byte before it; delivered as one ordinary `<video src>` over 206s.
      **There is no frontier, so there is nothing to stall on.**
    - `Transcode` — a re-encoder can promise neither, which is exactly the shape of an app-supplied ffmpeg.
      It gets the TIME grid: the manifest is synthetic, computed from duration alone, so every segment is
      addressable before any exists and a seek is arithmetic followed by a restart.
    - **The split is behind the seam.** A frontend writes one hook and gets one element either way — one
      method for the CONSUMER, two productions for the kit.
  - 🔴 **THE MEASUREMENTS IT RESTS ON** (2026-08-12, Android WebView 133 + iPhone 16 Pro simulator), because
    every one contradicted something that had been assumed:

    | | Android | iOS |
    |---|---|---|
    | native HLS (`index.m3u8`) | **NO** — `ready=4 size=0x0 dur=0`, no error | untested |
    | `canPlayType('application/vnd.apple.mpegurl')` | `"maybe"` — **a lie** | `"maybe"` |
    | `MediaSource` / `ManagedMediaSource` | yes / undefined | **undefined** / **yes** (iOS 17.1+) |
    | 200 with no `Content-Length` | **NO** — `err=4`, with or without `Accept-Ranges` | — |
    | 206 + real total, slow body | **YES** — `dur=60`, `seekable=[0–60]` at `buffered=[0–8.3]` | **YES** |

    - **Everything that failed, failed for want of a SIZE.** MAUI's Android intercept path always emits
      `Content-Length: 0` and cannot be told otherwise, so the element learns the total from `Content-Range`
      on a 206 and from nowhere else.
    - ⚠ **A `fetch` control is what separated transport from decode:** the failing 200 delivered all 474,744
      bytes while the element refused it. Without that control this reads as "streaming does not work"
      instead of "the header is wrong".
    - ⚠ **iOS reads a container in HUNDREDS of tiny ranges** (4–512 bytes) before streaming forward, so
      per-request cost dominates there in a way Android's single large request never reveals. **A design
      validated only on Android would look fine and be unusable on iOS.**
  - ✅ **THE `Remux` ARM IS PROVEN ON HARDWARE, including the claim the whole design turns on.** A 60 s
    H.264+AAC Matroska plays as one plain `<video src>` at `size=480x270 dur=60.023`, and **a COLD seek to
    80 % lands and plays on** — iOS jumped to `bytes=399291-` right after the header and got a `206`, with
    nothing produced before or after it. Android needed 4 range requests, iOS 508; the per-source layout
    cache is what makes the chatty shell viable. Repeated past the deleted size ceiling on a 79 MiB source
    (`dur=1000.022`, cold seek to 800 s). Raw numbers in `mobile-shells.md`.
    - 🔴 **A body must be LAZY, and an output-size ceiling cannot bound the WALK that computes it.** The old
      64 MiB cap was a buffered body's memory budget; it was checked AFTER the walk returned, against a
      number the walk produces, so a two-hour film paid its whole ~110–150 MB walk on the platform's resource
      thread and was only then declined. Anyone reintroducing a bound wants a PRE-walk figure (the source's
      own length).
    - 🔴 **Never block a webview's resource thread, at any size** — measured, not inferred: one blocking read
      in that position deadlocked the iOS main thread. The walk is an `IMissionScheduler` mission, and the
      first request for an unplanned source answers `503 Retry-After: 1` while it runs.
  - **It SUPERSEDES the "no default segment engine" position**, which had made itself falsifiable —
    *"something must ASK before one is written (D63)"*. Something did. The kit ships one, composed of what it
    already owns: the platform codecs behind `IMediaStreamConversion` plus a fragment writer. **D51 is
    untouched**: no engine bytes ship, and an app past the platform's reach still supplies its own.
  - **The restructure this required**, and the reason the work is ordered this way: three options types each
    declared their own `AllowedRoots` and cache root, and a second delivery path would have made that
    three-way drift permanent. The tier splits by the question each part answers — `Probe/` what it IS,
    `Plan/` what SHOULD happen, `Engine/` how bytes are PRODUCED, `Deliver/` how they REACH the page — and
    containment plus cache location are stated ONCE. ⚠ The `Convert`-versus-`Conversion` collision dissolved
    in that split rather than needing a rename: they are different layers and stopped sharing an object.
  - 🔴 **THE STREAMING CACHE AND THE OFFLINE ARTIFACT HAVE OPPOSITE POLICIES, and conflating them breaks
    offline silently.** `SegmentStreamOptions.CacheCapBytes` is a budget for a REBUILDABLE cache with
    oldest-used-first eviction; a persisted download must be complete and never evicted. Shared policy means
    ordinary playback quietly evicts an offline film, and the failure appears much later as a file that used
    to work. **"Complete" is a checkable predicate — every segment on the grid exists** — not an assumption.

- **D72 — THE COMPUTED-REMUX ROUTE GETS NO PAGE-SIDE READINESS CONTRACT. The APP warms the plan in .NET, and
  the page stays one plain `<video src>`.** (Owner, 2026-08-13, on being offered a readiness event: *"this
  sounds more like HLS now"*.)
  - **The question it closes.** A source nobody has planned answers `503 Retry-After: 1`, and a `<video>`
    cannot ride that out: measured on BOTH shells, the element errors within ~70 ms (`error.code 4`,
    `readyState 0`, `networkState 3`, `play()` rejected `NotSupportedError`) and issues no retry for at least
    12 s; re-pointing `src` after the plan lands plays it immediately. **So the 503 buys nothing for an
    element** — it still buys the retrying `fetch` client — and something had to tell a page when to point.
  - 🔴 **THE REJECTED ANSWER IS THE INTERESTING ONE: a readiness event plus a page-side consumer.** It fits
    the kit's existing habit, which is why it was proposed — and it forfeits the only thing this route has
    over HLS. **The plain-`<video src>` claim IS the differentiator.** A page that must subscribe to an IPC
    event and set `src` from a handler is no longer plain, and at that integration cost MSE/HLS is strictly
    MORE capable. The owner's earlier rule settles it: *"1 method better than 2 because there is no
    significant benefit."*
  - ✅ **THE ANSWER: nothing tells the PAGE, because the APP already knows.** The 503 exists for one reason —
    nobody told the kit which source is about to play — and the app built that URL. So the warm-up is an
    ordinary .NET call on the handle `UseComputedRemux` already returns, and the page contract does not
    change at all:
    ```
    app (C#):   await route.PlanAsync(source);          // ~1.9 s, once, cached by identity
    page:       <video src="app://…/remux?f=…">         // unchanged, zero kit JS
    ```
    That is the thesis rather than a workaround: **.NET does the platform work · React does the interface ·
    the kit owns the seam.** A page on any frontend — or none — still works.
  - **Why not simply block the first request until the plan lands.** Forced, not chosen: both mobile shells
    resolve a resource SYNCHRONOUSLY, and a blocking wait in that position DEADLOCKED the iOS main thread.
    **The wait has to move EARLIER than the request, which is precisely what warming is.**
  - ⚠ **`PlanAsync` MUST apply the request path's authorisation chain, not a shortened one** — remote check,
    containment against `AllowedRoots`, then the identity key. A warm entry point that skipped it would be a
    way to make the kit walk any file the process can read, from app code that believed it was only hinting.
  - 🔴 **THIS DECISION IS FALSIFIABLE, and here is the test.** It assumes an app can know what it is about to
    play. Where it cannot — a source that must play the instant it is named — the honest options are "await
    the warm before mounting" or *that case belongs to segments*. **If apps routinely cannot warm ahead, the
    answer is to go segments, NOT to add the event back**, because the event costs the differentiator without
    buying the capability.

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

- **D75 — THE SEGMENT TIER IS fMP4, THE GRID IS WHOLE SECONDS, AND THE DEFAULT ENGINE IS MOBILE-ONLY.**
  (Owner, 2026-08-14, overriding the adoption-driven hold on D71's remaining pieces: build the whole tier.
  ⚠ What that hold was RIGHT about survives as the risk being managed — the kit is guessing what a segment
  engine must promise, with no adopter to correct it, so undecided details bias toward what an adopter can
  change and the guesses are written down.)
  - **fMP4, not MPEG-TS, and the deciding reason is not compatibility.** `isTypeSupported('video/mp2t')`
    answered `true` on both mobile shells and that claim is not trusted — `canPlayType` produced exactly such
    a `true` for HLS the same day, and a MediaSource append failure is SILENT. What settled it is that
    **fMP4 makes `ISegmentEngine.HasRenderedPicture` ANSWERABLE**: the `trun` states every sample's size, so
    "the encoder accepted every frame, wrote `video:0KiB` and exited 0" — a measured bug — becomes a
    subtraction. MPEG-TS names its streams in the PMT, where a picture-less segment is indistinguishable from
    a healthy one. **A container chosen for what it lets you CHECK, not for what it lets you play.**
  - **The grid must be a whole number of seconds, and a fractional one is REFUSED rather than rounded.**
    `SegmentRunRequest.SegmentSeconds` is not negotiable by the engine; what makes it hittable is that both
    platform encoders emit a keyframe every second, each calling it *"a SEEKING decision, not a quality
    one"*. 🔴 **That coupling lives in two files and nowhere else, which is why no forced-keyframe API was
    needed** — the blocker this piece was expected to hit does not exist, and `SegmentGrid` now states it. A
    2.5-second grid puts boundaries where no keyframe exists and those segments PLAY: only a seek
    misbehaves, which is exactly why it is refused at composition time rather than discovered later.
  - **The init segment is written BESIDE THE FIRST FRAGMENT, never ahead of the run.**
    `IMediaStreamConversionRun.OutputConfig` is knowable only after an encoder has produced output, and an
    init segment carrying an empty configuration yields a movie that opens and plays nothing. So the route
    answers `503 Retry-After: 1` for `init.mp4` until it lands — the same not-ready reply the other routes
    give, and a page following `#EXT-X-MAP` must tolerate it exactly as it does for a segment.
  - **Mobile only, stated rather than pretended.** `IMediaStreamConversion` is implemented on Android and
    iOS; `Shenora.Windows` has none, so the desktop reports `IsAvailable = false`. That is the right answer
    there anyway — WebView2 serves byte ranges properly, so the desktop's path is the computed-remux route
    (D72). Building a Media Foundation codec is a separate decision, not a gap this leaves.
  - **Segments are the TRANSCODE path; the routes do not arbitrate between themselves.** A source whose
    streams the container can already carry belongs on the computed-remux route. An earlier draft had the
    engine DECLINE such sources; that was inference and was dropped — **which route a source takes is the
    app's decision, expressed by which route it registers for that URL, and a route that silently declined
    work it was explicitly given would be undebuggable.**
  - ⚠ **What is NOT yet proven, so it is not claimed.** The pump, the cutting, the seeking and the fragment
    bytes are unit-tested end to end against a FAKE `IMediaStreamConversion`. Whether the PLATFORM's encoders
    behave as that fake does — in particular whether they reorder output, which the writer fail-closes on —
    has not been measured on hardware.

- **D73 — MEDIA COMPOSITION FOLLOWS THE KIT'S OWN `Add`/`Use` SPLIT, because .NET already has this shape and
  a second idiom would be a thing to learn twice.** (Owner, 2026-08-13: *"lets do this properly a more .net
  fasion of styling of app build"*.)
  - **The rule is not invented here — it SHIPPED with D64's test:** ***`Use` means a wider configuration
    INCLUDING its pipeline; `Add` is the service-collection level only.*** So the media tier gets
    `services.AddShenoraMedia(...)` for what belongs in the container, and the routes stay `Use…`. Anything
    else would be a parallel convention beside a documented one.
  - **What the audit found, counted rather than felt** (23 hand-wiring sites in the sample's main page
    alone), and it is the list any composition helper has to answer:
    - **Route ORDER is load-bearing and unenforced.** `UseComputedRemux` must precede `UseMediaConversion` or
      the conversion route answers every request its own `Resolve` matches, and **the computed route becomes
      dead code that still passes all of its own tests.** A kit test catches a swap; an app gets nothing.
    - 🔴 **Diagnostics require a DOWNCAST.** The shell registers the platform converters with no log sink
      (deliberately — an app that wants them registers its own), so an app must fetch the pipeline, cast it
      and re-register. That is the documented escape hatch and still the wrong first experience: it cost
      THREE device round-trips in one session because the converters were mute.
    - **The conversion pipeline silently degrades without an `IMediaCapability`**, answering `CanConvert` by
      CONSTRUCTING codecs — the shape that produced an over-claim and an under-claim in one evening.
    - **Three options types must share ONE `MediaAccessOptions` and ONE `IMissionScheduler`.** D71 made that
      possible; nothing makes it automatic, and a scheduler per route is a concurrency bound per route.
  - ✅ **THE DOC IS THE DELIVERABLE, NOT THE CODE — and the doc is `docs/guides/media.md`.** The gap there was
    worse than "not mentioned": it covered `UseMediaConversion` and **not `UseComputedRemux` at all**, so the
    kit's PRIMARY delivery path (D71) was absent from the only page an adopter reads about media. It now
    carries a whole adoption: the `Add`, the two routes IN ORDER with the hazard stated, the `PlanAsync`
    warm, and the four-line page.
  - 🔴 **Writing it settled the composite question empirically, which is why the doc came first: a `UseMedia`
    composite is NOT yet earned.** The honest snippet is four short blocks and reads fine. What it exposed
    instead is that **the ordering hazard cannot be fixed by prose** — the doc has to SAY "nothing enforces
    this", which is the shape of a defect waiting for a gate. **Prefer a test or an analyser over a helper
    that hides the order.** ⚠ And if one is ever added, the individual calls stay public: a composite hides a
    load-bearing ordering, which is right until an app must interleave its own middleware between the routes.

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
