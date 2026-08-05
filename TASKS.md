# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

⚠ **This rule was not being followed and the file had to be swept on 2026-08-05** — eleven closed items
were annotated in place instead of moved, and two of them were still showing an unchecked `[ ]` box a day
after they shipped (Now Playing, and the Android session token). An open box is the only signal this file
gives; annotating in place is how it stops being true. **Move it or leave it open — do not do both.**

**Status: 0.9.1 PUBLISHED (2026-08-04).** Six NuGet packages (`Core` · `Ipc` · `Media` · `Windows` ·
`Android` · `iOS`) + `@shenora/react`, all confirmed at 0.9.1 on the feed, and **verified against the
published package rather than the tree** — a scratch app-shaped iOS consumer with the cache purged and no
devkit opt-in. Release history, its incidents and their lessons live in `CHANGELOG.md`; the closed backlog
lives in `docs/archive/tasks.md`.

### Release mechanics that still steer

- **NEVER touch `<VersionPrefix>` or the CHANGELOG's `## Unreleased` heading** — the release workflow owns
  both. A hand-bump moves the baseline and SKIPS a release; that is how **0.2.0 was consumed without ever
  shipping** (the registries read 0.1.2 → 0.3.0). Work written while that was in flight calls itself "the
  0.2.0 pass" — those names refer to the WORK, not to a release. Guard: `docs/RELEASING.md`.
- **A release only contains what is PUSHED.** 0.6.0 shipped 0.5.1's CODE because the work was committed
  locally and never pushed. `dev.mjs changelog` now fails a release whose `## Unreleased` is empty, with the
  message pointing at that cause first.
- **A break is CHEAP but never silent (D47).** One repo fully adopts the surface, so a break costs that one
  repo's compile errors — found by the compiler, fixed by whoever asked for the change. So prefer the
  CORRECT shape over the compatible one and ship no compatibility aliases; the test is *"would this be the
  shape on a greenfield surface?"* It still belongs under `### Breaking` with its migration, and it still
  shows as API-baseline drift. ⚠ This is a property of today's adoption count and reverts the moment a
  second repo fully adopts. 1.0 is a separate deliberate freeze, not yet cut.

> **This library is the intended foundation for the author's apps** (owner, 2026-08-03), so the bar on the
> published surface is an adopter's, not a maintainer's: docs that match the artifact, breaks documented
> with their migration, and readiness claims verified against a restored package rather than the tree.

> DIRECTION (user, 2026-07-30): Shenora is the shared infrastructure library for ALL sibling
> projects — a "UI kit for non-web applications" in the headless sense: it holds the desktop
> shell that different applications boot their own logic on, and it must NOT depend on any UI
> component library. Purpose is to stop re-solving the same problems per project. In-scope
> common work explicitly includes: multi-form/multi-window, co-browsing (auxiliary browser
> sessions), drag-drop zones, the IPC package design, the event hub, frontend display
> optimizations, and the React hooks layer.
>
> DIRECTION (user, 2026-07-30, later): growth is harvest-driven — when something nice emerges
> while developing another application, it gets generalized and promoted into Shenora (common
> design/library/tool sharing). And the kit must be able to adopt MOBILE application logic too:
> Capacitor (and similar) shells speaking the same IPC envelope through a pluggable transport.
>
> DIRECTION (user, 2026-08-04): *"one thing you need to keep in mind, we are doing a library, for
> multiple platform, so if the library can provide powerful devtooling that will be even better so
> for example rely on less swift code for ios (dynamic island) and support platform logic like now
> playing"* — so **PLATFORM LOGIC is in scope, not just the shell**, and the measure of a platform
> capability is *how little native code an adopting app has to write*. See
> `### Platform integration` below.
>
> DIRECTION (user, 2026-08-05): *"sonora actually is the first one fully adopting all features so you can
> fix anything into the best here which only cause 1 repo to update"* — so **the API is optimised for
> correctness, not for compatibility**, while adoption is one repo. Full reasoning and its limits: **D47**.

## Open

> **WORK ORDER (owner, 2026-08-03): E1 → C → D (media). E1 and C are done; media is the live work and
> most of it is done too** — `DM1` (a real file plays AND seeks through the interception seam, on both
> mobile shells and the desktop), `DM2` (the per-stream playability planner), `DM5` (packaging) and the
> whole **D45 re-layering** are closed. **`DM3` (the conversion) and the remote half of `DM4` are what is
> left.**
>
> ⚠ **Read `D44` AND `D45` before designing anything in media.** D44 carries the three rules the device
> runs produced and contradicts two things the design doc asserts; D45 moves interception out of media
> entirely, which the design doc (`docs/2026-08-03-shenora-media-design.md`) predates and does not reflect.
> Read that doc's "THE DESIGN, in one place" section before its trail — the trail contains intermediate
> positions that were later corrected.

### D — media: the live work

Everything `DM3` needs already exists; the item is a composition, not a build.

_**DM3 IS DONE (2026-08-05)** — `UseMediaConversion` in `Shenora.Media`, 6 tests, both atomicity and the
content-key sabotage-verified, `Conversion` argued into the surface lexicon. Record in
`docs/archive/tasks.md`; surface in `CHANGELOG.md` `## Unreleased`. The design below is what it was built
to, kept because it explains why the surface is smaller than the doc's pipeline suggests._

<details><summary>the settled design, kept for the reasoning</summary>

  **DESIGN SETTLED 2026-08-05 — read this before changing any of it.** Two constraints
  found by checking the seam rather than the doc, and each one SHRANK the surface:
  - 🔴 **The mobile interceptor resolves SYNCHRONOUSLY** (`MobileWebViewInterceptor.Run` does
    `.GetAwaiter().GetResult()` — both platforms need the status line and headers when the event returns).
    So the middleware **cannot await a conversion**, and must not probe on the request path either: a
    process launch per request is a webview callback blocked on `ffprobe`. Everything slow goes in the
    MISSION. The request path is then only: resolve → contain → cache key → hit? serve : start-and-answer.
  - **On a cache MISS it answers `503` + `Retry-After` and starts the mission** — the page is event-driven
    by design (`SOURCE_PROGRESS`/`READY`/`FAILED` over the existing pipe, design §5), so it sets
    `<video src>` when READY arrives rather than blocking on the first request.
  - **The middleware does NOT decide whether conversion is needed — the APP does, before it builds the
    URL**, using the `MediaPlaybackPlanner` the kit already ships. That deletes `Probe` and `Policy` from
    the options entirely and keeps the ownership line the design's own table draws: the app owns the
    engine, the codec policy and the decision; the kit owns the composition. A source that plays directly
    is simply pointed at `UseFiles`.
  - **Surface, minimal:** `MediaConversionOptions { Resolve, Convert, CacheRoot, AllowedRoots,
    CacheExtension, ContentType?, Module, Log }` + `MediaConversionRequest(SourcePath, DestinationPath,
    Progress)` + `interceptor.UseMediaConversion(scheduler, events, options)`. `Convert` is the app's —
    the kit ships no engine and never vendors one (D42).
  - ⚠ `Conversion` is a NEW word for the surface lexicon and will fail the gate until argued there.

- [x] **DM3 — the conversion, COMPOSED not built.** `IMissionScheduler` (the long run) +
  `PathClaims.Exclusive` (convert once, never twice) + `Files.BeginReplace` (atomic output) +
  `Core.DerivedCacheKey` (identity + length + mtime — the piece DM3 used to say was missing, and it now
  ships). `Files.BeginReplace`'s own XML already names this composition. Progress rides the existing
  notification pipe as `SOURCE_PROGRESS`/`READY`/`FAILED`.
  It arrives as a MIDDLEWARE on the D45 pipeline, in `Shenora.Media`, layered over the file middleware Core
  already serves with — not as a second serving path.
  - **Second consumer, and its case is the interesting one:** the server-backed sibling converts
    SERVER-side today and would rather the phone decided for itself, because Android codec support is
    vendor-declared per DEVICE and the server is therefore guessing on the client's behalf. The planner
    already moves that decision to the right machine; the conversion is what it cannot yet act on.

</details>
_**DM4 IS DONE (2026-08-05), and it landed WITH its consumer** — `MediaConversionOptions.AllowRemoteSource`,
fail-closed on a missing policy AND on a throwing one, both sabotage-verified. ⚠ **Read the archive entry
before assuming the kit fetches: it does not.** It AUTHORISES, and the app's engine reads the url — which is
what gave the guard a real caller without the kit growing an HTTP client, and is why this could close at all
rather than repeating `MediaAccess.IsRemoteAllowed`'s "public seam with no consumer"._

<details><summary>the original filing, kept for the reasoning</summary>

- [x] **DM4 — the REMOTE authorization seam only; the local half is DONE.** Path containment shipped as
  `Core.WebViewFiles.ResolveContained` (generic, fail-closed, tested) because the page supplies the path.
  What is left is the SSRF surface: a fail-CLOSED guard for *may the host FETCH this url on the page's
  behalf*, the shape `NavigationGuard` already has — denying with no policy AND when the policy throws,
  because the host can reach addresses the page cannot. ⚠ It was written once as
  `MediaAccess.IsRemoteAllowed` and deliberately dropped in the D45 re-layer: nothing in the kit fetches a
  remote resource for a page, so it had no caller. **Land it WITH the middleware that needs it, not before**
  (D15) — a public seam with no consumer is exactly what the last one was.

</details>

### From the first adopter — defects found on the 0.9.1 adoption (2026-08-04/05)

_**Three playback/interception defects from this round are FIXED (2026-08-05)** — the Windows `Duration`
drop, the Android paused `Rate`, and the duplicated `Content-Length`. Each was verified against the OS's own
read-back rather than "the call did not throw", and each landed with the gate that would have caught it.
Records in `docs/archive/tasks.md`; detail in `CHANGELOG.md` `## Unreleased`._

- [ ] **🟡 Android navigation — NOT REPRODUCIBLE here, and the gate that looked for it now exists.** Filed as
  a 🔴 blocker: with a route registered, a page RELOAD dies with `net::ERR_INVALID_RESPONSE` and the webview
  shows its error page, while sub-resource requests and hash routing stay fine. **The sample now does exactly
  the filed reproduction and passes** (2026-08-05, MAUI 10.0.20, Android WebView, emulator): a route
  registered, `location.reload()`, and the bundle comes back — `stamp=fresh|title=Shenora mobile
  sample|nodes=55`. `PageProbe.CheckReloadAsync` is the permanent gate.
  - ⚠ **The gate is sabotage-verified, so the PASS means something.** Claiming `/` and answering it with a
    404 produces `title=|nodes=5|text=Not Found` and a FAIL. Recorded because the FIRST sabotage was
    ineffective and looked like a working gate: `e.Handled = true` without `SetResponse` leaves MAUI
    returning a null response, which Android reads as "not intercepted, serve it yourself" — a no-op, not a
    breakage. See `.claude/knowledge/mobile-shells.md`.
  - **So the reported mechanism is wrong, at least on this version.** "MAUI's `HybridWebView` does not
    re-serve the main document once a `WebResourceRequested` subscriber exists" is not what happens:
    `MauiHybridWebViewClient.ShouldInterceptRequest` raises the event FIRST and, when the app does not claim
    the request, falls through to its own asset serving. Subscribing costs the main document nothing.
  - **What to ask the adopter before doing anything else**, since a fix cannot be designed against a defect
    that will not reproduce: their MAUI version (this ran 10.0.20); whether their middleware ever returns
    NON-null for `/` (the kit's file middleware answers a 404 for a path it resolves but cannot find, which
    would produce exactly this symptom and is the likeliest candidate); and whether it ever THROWS on the
    main-frame request — `MobileWebViewInterceptor` converts a throwing middleware into a 404, deliberately,
    which for the DOCUMENT is indistinguishable from the report.
  - Do not "fix" this speculatively. A change to main-frame fall-through with no reproduction is a change
    that cannot be verified in either direction.

_**The `ILane.Capacity` report is CLOSED (2026-08-05)** — record in `docs/archive/tasks.md`, surface in
`CHANGELOG.md` `## Unreleased`. In one line: the `min(lane, global)` behaviour was correct and stays, and
what was actually broken — that it was undetectable, and that the bound could never be raised at runtime —
is fixed by `IMissionScheduler.GlobalLane` + `ILane.EffectiveCapacity` + a log when a widen cannot apply.
**The adopter's `CapacityGovernor` can now restore as well as throttle**, and the workaround (set
`DefaultLaneCapacity` to the widest any lane will ever need) is no longer required, only still valid._

_**The rename is DONE too (2026-08-05, owner approved):** `DefaultLaneCapacity` → `GlobalLaneCapacity`, a
documented break under `### Breaking` with **no compatibility alias** (owner: *"we dont really need Obsolete,
lets keep this library logic clean"*) — an `[Obsolete]` alias would leave both names on the surface for years
and the misleading one still writeable, which is what the rename exists to prevent. Migration is one word per
site, found by the compiler. ⚠ **Renaming `ILane` itself was considered and
REJECTED**: it is the harvested word (the donor apps already said "named lanes (gpu/cpu)"), the metaphor is
what carries weighted permits (`MissionLane("gpu", Permits: 2)` = "occupies 2 of the lane's width"), and it
is published surface whose rename cascades through `MissionLane`/`Lanes`/`Lane(string)`. Don't relitigate
without a new reason._

### B. Staged application updates

Design + evidence: `docs/2026-08-02-shenora-app-update-design.md` (two independent sibling
implementations, same two-phase model, same `{path, size, sha256}` manifest). The claim to build
against: **only the apply step is native.** B1 (manifest + diff), B2 (the staging area) and B3 (the
release-source seam) are done — `docs/archive/tasks.md`.

- [ ] **B4 — the NATIVE launcher, and it is now much smaller than the design assumed.** Owner's call
  (2026-08-02): ship the apply logic as portable .NET first. Done — `UpdateStage.ApplyAsync` overlays,
  removes and clears, gate-covered and sabotage-verified, so **a self-contained app needs no native
  code at all.** What is left for the launcher is only what genuinely cannot be done in .NET: detect
  and install the runtime when it may be absent, then invoke the applier. Take Sonora's topology
  (app in `{root}/app/`, overlay only that) — the applier already documents and tests that layout.
  Still the one artifact this repo's gate cannot compile, so it ships as a TEMPLATE with that said
  plainly, and the sibling's Node harness (drive a PREBUILT exe over sandbox dirs) is the model for
  testing it on demand rather than in `verify`.

_**DONE 2026-08-05 — `ZipUpdateSource`.** ⚠ This entry said "DEFERRED by owner direction"; that was a
MISREADING of *"do our own first … in the meantime you should have your own"*, which meant lower PRIORITY,
not deferral (owner, 2026-08-05: "I didn't say defer was just put on lower priority"). Corrected here rather
than quietly, because the wrong word had been propagated into three documents and would have kept the item
from ever being picked up. Record in `docs/archive/tasks.md`._

<details><summary>the original filing, kept for the four port notes it carries</summary>

- [x] **The archive-backed `IUpdateSource`.**
  `IUpdateSource.OpenAsync(ManifestFile)` fits a release that publishes loose files. The first adopter's
  releases publish one ZIP per part with a manifest listing per-file hashes — a shape at least as common,
  since it is what GitHub Releases encourages. Everything else fits perfectly: `UpdateStage` verifies every
  staged file's SHA-256 before the stage counts as pending, which is precisely the load-bearing step that
  app hand-rolled, with the same anti-truncation reasoning. **The other half of this blocker,
  `UpdateStageOptions.BaselinePath`, is DONE** (2026-08-04) — this is what remains.
  - **The adopter builds theirs first** (owner: *"do our own first … in the meantime you should have your
    own"*), which is the sequence this kit is designed around (D8/D15): port a proven implementation
    rather than guess at one, exactly how `Files`/`FileReplacement` arrived. Theirs already does download →
    extract → verify every file against the release manifest → swap, is verified against a real published
    release, and self-heals an interrupted run because the installed-version stamp is written only after a
    successful swap. **A deferral with a working alternative, not a hole.** Pick it up when theirs works,
    and read it before designing anything.
  - **VERIFIED 2026-08-03 — the INTERFACE already fits, so this is purely a shipped implementation, not a
    contract change.** Four notes for whoever writes it:
    - `FetchAsync` opens files **sequentially** (`UpdateStage.cs:275`, a plain `foreach` with `await`),
      so one shared `ZipArchive` is safe today. `ZipArchive` is NOT thread-safe, so that is a coupling
      to state in the XML rather than leave the next person to parallelise the loop and find out.
    - The archive stream must be **seekable**. Over a live HTTP response `ZipArchive` is forward-only
      and random access by entry fails; download to a file (or a `MemoryStream`) first.
    - The adopter publishes **one ZIP per part**, not one per release, so the source must map
      manifest path → (archive, entry). A single-archive implementation would only serve half of them.
    - ~~It belongs where `IUpdateSource` already lives (`Shenora.Core`, no new package per D2)~~ and needs
      no dependency: `System.IO.Compression` is in the shared framework.
      **This one note was OVERRULED when it shipped** (owner, 2026-08-05 — *"because this include file
      operation so we should have a sperated library/package for this"*): it went to a new
      `Shenora.IO.Compression`, and `IUpdateSource` itself then followed the rest of the update engine out
      of `Shenora.Core` into `Shenora.IO`. See D48. The other three notes were honoured as written.
  - **Recorded honestly:** the adopter declined partly on a BAD metric — adapter lines ≈ deleted lines —
    which misses that the tricky, worth-inheriting logic (staging, verification, journal, resume) is all on
    the kit's side and the bridge is boring. With the source shipped, this becomes a straight adoption.

</details>

- [ ] **⚠ Still owed, and it survives `ZipUpdateSource` shipping: validate the update stage against a REAL
  release, not fixtures.** The stage verifier's
    third failure mode (intrusion) is closed — `UpdateStage.CommitAsync` rejects a staged file the manifest
    does not list, exempted by the caller-supplied `UpdateStageOptions.IsUnindexed` predicate. But **the
    failure mode of getting the exclusion list wrong is inverted**: too LOOSE lets an injected file through;
    too STRICT rejects every honest download — and the second is worse, because it breaks for every user at
    once rather than for an attacker, and synthetic fixtures pass it happily since the tester writes both
    sides. That adopter validated against its actual published release (management 106 files/104 indexed,
    backend 103/102, frontend 21/21, zero would-be-intrusions, the extras exactly the exempt ones). Make
    that a documented step for the kit's verifier, not a habit one adopter happened to have.

### Greenfield-shape sweep of the public surface (2026-08-05, under D47) — ALL FOUR CLOSED

Swept all four API baselines (~1,840 lines) for shapes that exist only because changing them used to be
expensive. **Each was checked against the SOURCE, not the signature** — several plausible-looking candidates
turned out to be correct already, and that half of the result matters as much as the findings.

_Verified FINE, recorded so they are not re-swept: no `Dto` suffixes anywhere; no boolean flag parameter
standing in for a seam (`ApplyChromeTheme(…, bool immersiveDarkMode)` is a real platform toggle,
`SetLoading(bool)` is a state setter); every other option default is a real value validated with a throw
(`LeaseTimeout` 30 s, `PollInterval` 50 ms, `MaxQueuedNotifications` 10 000 — the IPC options THROW on
out-of-range rather than reinterpreting); `Shenora.Media`'s surface is clean; `ShellCapability`'s stringly
typed constants are justified because they cross the wire to JS in the handshake._

_**1–3 are DONE (2026-08-05)** — `IEventBus` returns `IDisposable`, `GlobalLaneCapacity` is `int?`, and the
dialog contract states its third outcome. Record in `docs/archive/tasks.md`; surface in `CHANGELOG.md`
`## Unreleased`. ⚠ **Finding 3 was WRONG as filed and the source said so** — read the archive entry before
re-proposing it._

_**4 and 4b are BOTH DONE (2026-08-05)** — the options split shipped as part of the dialog facade work, and
`4a` (the `Folder Selection` placeholder collision) is the only piece of this cluster still open. Full record
in `docs/archive/tasks.md`; surface in `CHANGELOG.md` `## Unreleased`._

<details><summary>the original filing and its recommendation, kept for the reasoning</summary>

- [x] **4 — `FileDialogOptions` is one bag serving four methods, and only its XML says which field is for
  which.** Of its 11 fields: `Title`/`DefaultPath`/`RememberPathKey` apply everywhere; `Filters` and
  `FileName` to open+save; `DefaultExtension`/`OverwritePrompt` are *"Save dialog:"*; `AllowFileSelection` is
  *"Folder dialog only"*; and `CheckFileExists`/`CheckPathExists`/`ValidateNames` are *"Desktop hint"* that
  the mobile shells ignore. So 5 are mode-specific and 3 are platform-specific, and nothing but prose says so.
  - **Cost is lower than assumed — checked, not guessed:** `FileDialogOptions` is **not** mirrored in
    `@shenora/react`, so a split is C#-only (3 call sites in this repo, plus the adopter's). The
    "wire-friendly" note on the type describes what an app's own facade may do, not a kit TS contract.
  - **Options:** (A) split per method, type-enforcing what is legal, needing a shared base for the three
    universal fields; (B) keep one type but nest the desktop-only hints so platform-specificity is
    structural rather than prose; (C) leave the shape, which is already documented field by field.
  - **RECOMMENDATION: (C) — do not change it without evidence from a real adoption.** Three reasons.
    (1) The contract's own header already planned this: narrow *"at first mobile adoption rather than a
    break we pre-empt for a consumer that doesn't exist yet (D15)"* — that trigger FIRED on 2026-08-02/03
    against a real mobile consumer, and the measured answer was that no break was needed
    (`docs/archive/tasks.md`, section C). (2) It is the same inference that made **finding 3 wrong** in this
    very sweep: a shape that reads muddled in a baseline listing, with implementations quietly depending on
    it. (3) **Nothing here fails in the dangerous direction** — a save-only field on a folder pick is
    IGNORED. Every defect actually fixed this round had a wrong-ANSWER failure mode (a lying `Capacity`, a
    `content-length: 0`, a `FilePath!` that NREs); this one has a no-op failure mode.
  - **The one question that would flip it to (A):** has an adopter actually set a dialog option that did
    nothing, and lost time to it? That is evidence. "The type reads muddled" is not.
  - **⚠ ANSWERED 2026-08-05 by reading the implementation — the premise was backwards.** Owner asked whether
    to *"move to a more system native implementation so not rely on ourself for those feature"*. **We already
    do not: 9 of the 11 fields are pure passthroughs to the Win32 common dialog** (`CheckFileExists`,
    `CheckPathExists`, `ValidateNames` → the identical `OpenFileDialog` properties; `OverwritePrompt`,
    `DefaultExtension`→`DefaultExt`+`AddExtension`, `FileName`, `Filters`→`Filter`, `Title`,
    `DefaultPath`→`InitialDirectory`). **The type looks Win32-flavoured BECAUSE it is a thin passthrough** —
    that is the good outcome, and renaming or regrouping them would make the mapping harder to see, not
    easier. Only two fields are kit-invented:
    - `RememberPathKey` — the kit sets `RestoreDirectory = false` to switch the OS's own memory OFF and
      substitute per-KEY, cross-session memory. Deliberate and earned: Windows cannot express "the import
      folder and the export folder are remembered separately", and this is the siblings' proven behaviour.
      **Keep.**
    - `AllowFileSelection` — the one real hand-rolled workaround, and **it has a wrong-ANSWER bug**. See 4a.

</details>

_**4a is DONE (2026-08-05)** — a real file now wins over the placeholder, the disambiguation is a pure
`internal static` with five tests, and the old ordering is sabotage-verified to fail exactly the two defect
cases. Record in `docs/archive/fix-log.md`; surface in `CHANGELOG.md` `## Unreleased`. This closes the whole
file-dialog cluster (4 · 4a · 4b)._

<details><summary>the original filing, kept for the reasoning</summary>

- [x] **4a — 🔴 `OpenFolderAsync(AllowFileSelection: true)` returns the PARENT FOLDER for a file named
  `Folder Selection.txt`.** `ShowFileOrFolderDialog` fakes "folder or file" with an `OpenFileDialog` whose
  `FileName` is the literal placeholder `"Folder Selection"`, then recovers the intent by string-matching it
  back out — including `Path.GetFileNameWithoutExtension(selected) == placeholder`, which a REAL file of that
  name also satisfies. So picking such a file silently yields its directory instead. Found by reading, not
  reported; unlikely input, but it is the wrong-answer class rather than the no-op class.
  - **There is no system-native escape.** Windows' Common Item Dialog has a folders-only mode
    (`IFileOpenDialog` + `FOS_PICKFOLDERS`, which is what `FolderBrowserDialog` already uses on the other
    branch) and NO "either" mode — so the hack exists because the OS lacks the concept, not because nobody
    looked.
  - **Fix without dropping the capability, one reordering:** only treat the placeholder as "this folder"
    when no real file by that name exists — `if (!File.Exists(selected) && IsPlaceholder(selected))`. A real
    file then wins over the fake name. Extract the disambiguation as an `internal static` pure function so it
    is testable with no dialog, alongside `BuildFilterString`/`ResolveInitialPathAsync` which are already
    internal seams for exactly this reason.
  - ⚠ The placeholder is also a hardcoded English string, so the recovery is locale-fragile in a second way.
    Worth stating even if the fix above makes it harmless in practice.

</details>

_**4b — the dialog half on both sides — is COMPLETE (all six phases, 2026-08-05).** `verify` green.
`FileDialogFacade` + `AddShenoraFileDialogs`, `FileDialogs`/`useFileDialogs()`/`useShellInfo()` in
`@shenora/react`, wire mirrors for the module, the four routes and all five shapes, both samples wired.
Full record — including the two premise corrections and the seven findings — in `docs/archive/tasks.md`._

  **Phase A's findings, worth keeping:**
  - **The compiler immediately caught a real one:** `ShowFileOrFolderDialog` passes `Filters`, and in
    `AllowFileSelection` mode that is a genuine capability (it is an `OpenFileDialog` underneath). Dropping
    filters from `OpenFolderOptions` would have silently removed working behaviour, so `Filters` is BACK on
    that type — documented as ignored unless `AllowFileSelection` is set. **A field conditional on a SIBLING
    field is a different thing from one conditional on which METHOD you called**; the split is what surfaced
    the distinction.
  - `Filters` and `FileName` are now declared on both `OpenFileOptions` and `SaveFileOptions`. That
    duplication is the accepted cost of base+derived; hoisting them to the base would put them back on the
    folder pick, which is the thing being fixed.
  - ⚠ **The surface lexicon gate fired and was the right call to take deliberately** — `Open` and `Save`
    are the first VERBS on this surface. Added with the reasoning in `surface-lexicon.txt`: they are the
    platform's own words (`OpenFileDialog`/`SaveFileDialog` are BCL type names), `IFileDialogs` has used
    them as METHOD names since P5.5 (members are not swept, which is why this never fired before), and
    every mechanism-noun alternative mis-describes the folder case.
  - ⚠ **The mobile shells only compile under `dev.mjs build`, not the test project** — two stale
    `<see cref>`s survived a clean `dotnet test` build. Run the full build before believing a rename is done.

  **Phase B's decisions, worth keeping:**
  - **`SAVE_TEXT` is the portable save, and it is TEXT on purpose.** `IFileDialogs.SaveAsync` takes a write
    DELEGATE, which a page cannot send — so the content has to cross the envelope, and that bounds what the
    route may honestly carry. Binary or large payloads belong host-side through `SaveAsync` directly, where
    they never enter a message. Streaming bytes through JSON would be the kit growing a file-transfer product.
  - **A capability refusal gets its own code**, `IpcErrorCodes.CapabilityNotSupported`, for the same reason
    `OPERATION_CANCELLED` has one: a client must tell "this shell cannot" from "something broke", because the
    right UI is to HIDE the control, not show a fault. Built from the kit's own words plus the capability
    NAME — never `ex.Message`, which crosses the wire verbatim and would bypass the whole error boundary.
    Sabotage-verified: swapping in `ex.Message` fails the leak test by name.
  - **Module name is a fixed const, not an option.** `OperationsFacade` makes its name configurable because
    the registry EMITS under the same module and the two must not drift; this facade publishes nothing, so a
    knob would be a public member earning nothing.
  - ⚠ **The wire mirror fired before the client half existed** — adding `CapabilityNotSupported` host-side
    failed `Every_host_error_code_exists_on_the_client_and_vice_versa` immediately. Working exactly as
    designed, and a good reminder that a host-side error code is never a one-sided edit.

  **Phase C's findings:**
  - 🔴 **`useShellInfo()` DID NOT EXIST — and two shipped doc examples used it** (`media.ts` and
    `types.ts` both write `const shell = useShellInfo();`). Only `bridge.shell` and `notifyReady()`
    existed, so an adopter following the kit's own example wrote code that does not compile. Same class
    as the MediaSession-token defect and the `FilePath`-when-`Success` one: **the docs described
    something that was not connected.** Now shipped, since capability gating needs it anyway.
  - **The hook reads SYNCHRONOUSLY and does not re-render on a late handshake**, which is the bridge's
    own documented design (*"cached so components can read it synchronously while rendering — a
    capability learned after layout is a flash"*). The requirement to await `notifyReady()` before
    rendering that tree is stated in the JSDoc rather than hidden; absent still means "assume nothing".
  - ⚠ **The type pin earned its keep again, exactly as `ipc-contracts.md` describes.** Sabotage: dropping
    `FileDialogResult` from the barrel fails `npm run typecheck` NAMING the type — while all 115 runtime
    tests still pass, because a type has no runtime binding. That is the `OperationProgress` defect class
    reproduced on demand.
  - **The gap is real and measurable.** The kit ships THREE facades with TS counterparts —
    `OperationsFacade`→`operations.ts`, `WindowCommandFacade`→`windowCommands.ts`,
    `DropZoneFacade`→`useDropZone.ts` — and **none for dialogs**. Yet `ShellCapability.FilePicker`,
    `.FolderPicker` and `.SavePicker` already exist as kit vocabulary that crosses the wire, so the kit
    advertises three capabilities it provides no way to satisfy. Both samples then hand-roll the same route
    (`PortableSampleFacade` and `SampleFacade` each call `dialogs.OpenFileAsync` themselves) — the
    two-consumer bar met inside this repo, before counting the adopter.
  - ⚠ **"Detect the system type like media does" is two mechanisms, and only one of them is media's.**
    `mediaUrl()` detects NOTHING — it returns a RELATIVE url, and that is the whole trick (D44: a fixed
    scheme fails on exactly one platform, in opposite directions). The detection half is separate and
    already shipped: `useShellInfo()` + `ShellCapabilities`, which `mediaUrl`'s own example demonstrates.
    A dialog helper needs the DETECTION half, not the relative-url half — it calls a host route, so
    `canPickFolder` comes from the handshake.
  - **What it buys, in D36's terms:** on mobile, `folderPicker` is absent by design (D35), so a page reading
    the capability HIDES the button instead of calling and catching a refusal. One bundle, both shells,
    without sniffing the platform.
  - 🔴 **This FLIPS finding 4 from "leave it" to "split it", and that is the load-bearing consequence.**
    `FileDialogOptions` is C#-only today, which is most of why leaving its mode-mixed shape alone was
    defensible (the XML tags each field, and a C# caller sees them in tooltips). Ship the facade and it
    becomes a **WIRE contract with a TS mirror**, exported to every page author — where save-only and
    folder-only fields on one object are materially worse, and where the wire-mirror tripwire has to pin
    whichever shape is chosen. **Decide the split as part of this work, not after it.**

### Open questions — decide these deliberately, not by momentum

- [ ] **Should the kit SHIP a page-diagnostic facade?** Two repos have now built the same three lines for
  the same reason (WebKit does not forward page `console.*`), which is the two-consumer signal — but the
  adopter filed it as an observation and explicitly doubted it earns a public type.
  `generic-library.md`'s bar is that every public type earns its keep, so the honest question is whether a
  documented PATTERN (now in `ADOPTION.md`) is the right shape instead of an API.

### Platform integration — OS-level logic, measured by how little native code an app writes

> DIRECTION (user, 2026-08-04): *"we are doing a library, for multiple platform, so if the library can
> provide powerful devtooling that will be even better so for example rely on less swift code for ios
> (dynamic island) and support platform logic like now playing"*

This widens the kit's scope in a specific way and it is worth stating precisely, because it is easy to
read as "add features". The shell packages so far hold what an app needs to *host a page* — windows, IPC,
dialogs, interception. This direction says they should also hold what an app needs to *be a citizen of the
OS*: the lock-screen transport, the system media controls, the live-activity surface. The stated measure is
the useful part — **not "does the kit expose the API" but "how much native code does the adopting app still
have to write"** — which is the same test D45 passed by moving interception into the shells (an adopting app
writes `interceptor.UseFiles(...)` and no platform code at all).

**Both named examples have now shipped** — `IPlaybackSession` on all three shells (0.9.0/0.9.1) and the iOS
Live Activity devkit, whose whole adoption is one MSBuild property plus four SwiftUI view bodies. Records in
`docs/archive/tasks.md`; recipes in `docs/ADOPTION.md`; mechanics in `.claude/knowledge/mobile-shells.md`.
Two things carry forward from them:

- [ ] **The one unverified claim: the Dynamic Island's VISUAL rendering, which needs a real device.** A
  simulator gives an activity only a lock-screen scene target. Everything else about the devkit is
  measured — start, update, end, and the automatic `buildTransitive` import from a real package reference.
- **The rule both of them earned: a platform capability ships with the tooling that drives and observes
  it, or it ships as an assertion.** Every platform capability so far became trustworthy the moment it had
  a HARNESS: `dev.mjs android`/`mac` for the device loops, `InterceptorProbe` and `MediaRangeProbe` for the
  serving seam, and D44's body-rule asymmetry was only ever *found* because a probe could run on both
  devices. Now Playing was verified by reading each OS's own registry (`dumpsys media_session`,
  `mediaremoted`, `GlobalSystemMediaTransportControlsSessionManager`) rather than by trusting that a call
  succeeded — that is the standard for the next one.

### Deliberately NOT built — read before proposing any of these

Each was decided, not skipped. **Every public type earns its keep** (`generic-library.md`); package symmetry
is not a reason.

- **`Shenora.Media.Windows`** — owner: *"no need … for now"*, and D45 makes it less likely still. The
  desktop shell already serves ranges correctly two ways (`WebViewDeferredScheme`, and
  `WebViewHost.Interceptor` + `UseFiles` — the portable one). The package would hold a WebView2 args adapter
  and the `Sliced` constant, both of which `Shenora.Windows` now owns outright. Add it only when a desktop
  consumer shows something it genuinely cannot express today (a native surface, an engine binding). Note
  the mobile pair it would be symmetrical with **no longer exists either** — `Shenora.Media.Android`/`.iOS`
  were deleted before ever being published.
- **Thumbnails and image resize** — deferred with the analysis already done (D43). They cost 0 MB on every
  platform and need no engine, so they are cheap to add later, and the player does not depend on them.
- **Folder picking as a portable capability** — CLOSED, D35. Same word, different guarantee on each
  platform; documented as a DESKTOP capability, with the mobile refusal pointing at the three intents that
  ARE portable.
- **Android's live-activity analogue** — for media it is already `IPlaybackSession`, and a progress
  notification means choosing icons and channels (D15/D13). It waits for a real non-media consumer.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
- [ ] **Keep naming the concrete bug each ADOPTION stage removes.** The first adopter's Stage-0
  feedback (2026-07-31), recorded here as the habit it is rather than as work: what made the adoption
  decision easy was "Stage 1 carries no IPC dependency, so it deletes the most duplicated code for the
  least risk; the IPC substrate comes last because it is the only stage that touches every module" —
  and what justified adopting a kit at all was naming the specific bugs a hand-rolled shell tends to
  have (the DPI-mis-scaled `Screen.WorkingArea` restore; `CloseReason.UserClosing` firing for a
  programmatic `Close()`). Write new stages the same way.
