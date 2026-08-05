# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

⚠ **This rule keeps not being followed, and the file has now been swept TWICE on the same day
(2026-08-05).** The first sweep moved eleven closed items out — two of which were still showing an unchecked
`[ ]` a day after they shipped (Now Playing, the Android session token). The second removed **295 more
lines**: seven blocks that were annotated `DONE`/`CLOSED` in place, kept their `<details>` filings, and were
*already recorded in the archive* — so the file had grown to 502 lines while holding six open tasks.
**An entry is either open or gone. Annotating it in place is how this file stops being usable**, and the
tell is the same every time: the length stops tracking the remaining work.

**Status: 0.9.1 PUBLISHED (2026-08-04)**, six packages on the feed, **verified against the published
package rather than the tree** — a scratch app-shaped iOS consumer with the cache purged and no devkit
opt-in. **`## Unreleased` is large and unshipped:** two new packages (`Shenora.IO`, `Shenora.IO.Compression`)
and five breaking changes. Release history and its incidents live in `CHANGELOG.md`; the current package set
is the table at the top of `docs/DECISIONS.md`; the closed backlog is `docs/archive/tasks.md`.

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

> **The 2026-08-03 work order (E1 → C → D) is fully DISCHARGED.** All of media closed on 2026-08-05 with
> `DM3` and `DM4`; the record is `docs/archive/tasks.md`. Kept only for the reading warning below, which
> still steers.
>
> ⚠ **Read `D44` AND `D45` before designing anything in media.** D44 carries the three rules the device
> runs produced and contradicts two things the design doc asserts; D45 moves interception out of media
> entirely, which the design doc (`docs/2026-08-03-shenora-media-design.md`) predates and does not reflect.
> Read that doc's "THE DESIGN, in one place" section before its trail — the trail contains intermediate
> positions that were later corrected.

### From the first adopter — defects found on the 0.9.1 adoption (2026-08-04/05)

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

### B. Staged application updates

Design + evidence: `docs/2026-08-02-shenora-app-update-design.md` (two independent sibling
implementations, same two-phase model, same `{path, size, sha256}` manifest). The claim to build
against: **only the apply step is native.** B1 (manifest + diff), B2 (the staging area) and B3 (the
release-source seam) are done — `docs/archive/tasks.md`.

_**B4 IS BUILT (2026-08-05)** — `src/Shenora.Launcher/`: the C++17 library + template, CMake, a
win-x64/linux-x64 CI matrix, and a conformance harness that drives the PREBUILT binary against stages the
real C# side produced. **Measured: 322 KB**, above D50's 150–300 KB band — the static CRT is the
difference, and it is a deliberate trade (no VC++ redistributable to bootstrap). Six conformance cases,
sabotage-verified. It found two bugs while being built. Record in `docs/archive/tasks.md`.
**What is NOT done and is the next step: the `runtimes/{rid}/native/` nupkg** — CI produces the per-RID
artifacts, nothing packs them yet._

<details><summary>the original filing, kept for the port notes</summary>

- [x] **B4 — the NATIVE launcher, and it is now much smaller than the design assumed.** Owner's call
  (2026-08-02): ship the apply logic as portable .NET first. Done — `UpdateStage.ApplyAsync` overlays,
  removes and clears, gate-covered and sabotage-verified, so **a self-contained app needs no native
  code at all.** What is left for the launcher is only what genuinely cannot be done in .NET: detect
  and install the runtime when it may be absent, then invoke the applier. Take Sonora's topology
  (app in `{root}/app/`, overlay only that) — the applier already documents and tests that layout.
  Still the one artifact this repo's gate cannot compile, so it ships as a TEMPLATE with that said
  plainly, and the sibling's Node harness (drive a PREBUILT exe over sandbox dirs) is the model for
  testing it on demand rather than in `verify`.
  - **⚠ THE SHAPE IS SETTLED — do not re-argue it, build to it (D50, 2026-08-05; design
    `docs/2026-08-02-shenora-app-update-design.md` §5a).** It is a **C++ LIBRARY + a template**, not a
    template alone: §0's own table shows both siblings split the same way, `updater.cpp` +
    `dotnet_runtime.cpp` generic against a per-app `main.cpp`. Requirements are Linux+Windows (Linux for
    later), small, one binary per platform on the mobile model. CMake, `std::filesystem`, Win32 behind a
    thin header, per-RID binaries from a CI matrix into `runtimes/{rid}/native/`.
  - **Rust was evaluated and rejected on the owner's own criterion** — it brings ZERO NuGet-packing
    benefit, and D8 favours the two proven C++ implementations. D50 records why, and the revisit trigger.
  - **First step when this is picked up is a MEASUREMENT, not code:** the binary-size figures in D50 are
    bands nobody has built. And the Node conformance harness is not optional — without it "library" is a
    promotion in name only.

</details>

- [ ] **B4b — pack the launcher as `runtimes/{rid}/native/`.** CI already produces the per-RID artifacts
  (`.github/workflows/launcher.yml` uploads `launcher-win-x64` / `launcher-linux-x64`); nothing consumes
  them yet. The remaining work is a packaging project that pulls both artifacts into one nupkg so a
  consumer's `PackageReference` drops the right binary into their output by RID — D50's stated shape.
  - ⚠ **It cannot be a normal `src/` csproj that builds the binary**, because the binary comes from a
    different toolchain on a different runner. The pack step consumes downloaded artifacts, which means
    it belongs in a release workflow rather than in `dotnet pack` on a dev box.
  - Decide at that point whether the **template** ships in the same package (as `contentFiles`) or stays
    a file an adopter copies out of the repo. Shipping it means versioning it; copying it means it can
    rot. Neither is obviously right and neither is urgent.

_**DONE 2026-08-05 — the real-release validation is now `node devtools/dev.mjs update-probe`**, and it
found a defect on its first run. Record in `docs/archive/tasks.md`; surface in `CHANGELOG.md`._

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

### Standing habits — NOT checkboxes, deliberately

⚠ **These used to be three `- [ ]` items and that was the bug.** A box that can never be ticked is
permanent noise in a file whose only signal is the box — the same defect the header complains about,
committed by the file itself. They are prose now, and they never "complete":

- **Keep `docs/ARCHITECTURE.md` + `docs/README.md` in sync as pieces land.** Partly gated since
  2026-08-05: `doc-drift` fails if a packable project is named in neither. Everything below package
  granularity — a new type, a moved folder — is still yours to keep honest.
- **Add a `.claude/knowledge/` rule the moment an invariant is EARNED**, via
  `node devtools/dev.mjs knowledge new <name>` — don't let it live only in a code comment. UI-thread
  marshalling, WebView2 gotchas, IPC batching numbers and the mobile header table all got here that way.
- **Keep naming the concrete bug each ADOPTION stage removes.** From the first adopter's Stage-0
  feedback (2026-07-31): what made the decision easy was *"Stage 1 carries no IPC dependency, so it
  deletes the most duplicated code for the least risk; the IPC substrate comes last because it is the
  only stage that touches every module"* — and what justified adopting a kit at all was naming the
  specific bugs a hand-rolled shell tends to have (the DPI-mis-scaled `Screen.WorkingArea` restore;
  `CloseReason.UserClosing` firing for a programmatic `Close()`). Write new stages the same way.
