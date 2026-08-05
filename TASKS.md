# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

⚠ **An entry is either OPEN or GONE — never annotated `DONE` in place.** This rule was broken twice on
2026-08-05 and the file reached 502 lines while holding six open tasks; the tell is always the same, the
length stops tracking the remaining work. Two of the stale entries still showed an unchecked `[ ]` a day
after they shipped.

**Status: 0.9.1 is the last PUBLISHED release (2026-08-04). `## Unreleased` is large and is the
next one** — three new packages (`Shenora.IO`, `Shenora.IO.Compression`, `Shenora.Launcher`), the
safe-area shell capability, and **five breaking changes**, each with its migration under `### Breaking`.
Release history and its incidents live in `CHANGELOG.md`; the current package set is the table at the top
of `docs/DECISIONS.md`; the closed backlog is `docs/archive/tasks.md`.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This file is the maintainer's remaining
> work, and a short list here means the kit is in good shape rather than that nothing is happening — what
> SHIPPED is `CHANGELOG.md` and `docs/ROADMAP.md`. The three items below are honest about what is not
> done: one is blocked on an adopter's answers, one on a release step, and one on a physical device.

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
  - ✅ **RE-PROVEN ON A CURRENT WEBVIEW (2026-08-05). The Chromium-version hypothesis is DISPROVEN.**
    A raised concern — that the original pass came from MuMu's AOSP **Chromium 110**, ~20 major versions
    behind any real user, and that `net::ERR_INVALID_RESPONSE` is exactly the area Chromium tightened —
    was worth testing and turned out to be wrong. Built an API 36 AVD carrying
    **`com.google.android.webview` 133.0.6943.137** and re-ran the whole probe set there:
    - `RELOAD: PASS` — `href=/|ready=complete|stamp=fresh|title=Shenora mobile sample|nodes=52`.
      **`stamp=fresh` is the load-bearing field**: the pre-reload marker is gone, so the document really
      did navigate away and come back rather than never leaving.
    - `HEADERS: PASS` and `MEDIA: PASS` (both clips, duration 60.00, seeked 48.00) on the same run.
    - So it does not reproduce on Chromium 110 **or** 133. The remaining explanations are on the
      adopter's side, which makes questions 3 and 4 below the live ones.
    - ⚠ MuMu's limitation is still real and still recorded in `.claude/knowledge/mobile-shells.md` — it
      just is not the explanation *here*. Use `shenora-a36` for anything Chromium decides.
  - **What to ask the adopter before doing anything else**, since a fix cannot be designed against a defect
    that will not reproduce:
    1. **Their WebView version** (`adb shell dumpsys webviewupdate`) — cheapest question, newly added, and
       the one that tests the hypothesis above. If they are on 13x and we are on 110, that gap is the story.
    2. Their MAUI version (this ran 10.0.20).
    3. Whether their middleware ever returns NON-null for `/` — the kit's file middleware answers a 404 for
       a path it resolves but cannot find, which would produce exactly this symptom and is the likeliest
       candidate.
    4. Whether it ever THROWS on the main-frame request — `MobileWebViewInterceptor` converts a throwing
       middleware into a 404, deliberately, which for the DOCUMENT is indistinguishable from the report.
  - Do not "fix" this speculatively. A change to main-frame fall-through with no reproduction is a change
    that cannot be verified in either direction.

### B. Staged application updates

Design + evidence: `docs/2026-08-02-shenora-app-update-design.md` (two independent sibling
implementations, same two-phase model, same `{path, size, sha256}` manifest). The claim to build
against: **only the apply step is native.** B1 (manifest + diff), B2 (the staging area) and B3 (the
release-source seam) are done — `docs/archive/tasks.md`.

**B4 (the native launcher), B4b (its package) and the real-release validation of the update stage all
closed on 2026-08-05** — records in `docs/archive/tasks.md`, shape in **D50**, surface in `CHANGELOG.md`.

**The launcher binaries are BUILT BY THE RELEASE, both RIDs, and never committed.** `release.yml` has a
matrix job (win-x64 + MSVC, linux-x64 + gcc) that builds and conformance-tests each one, and `publish`
`needs:` it — so a launcher that fails conformance, or a missing RID, stops the release before anything
is published rather than silently shipping a package short.

⚠ Committing the binaries was tried and reverted the same day, and the history was rewritten so the blob
never existed. The reasoning had been "a release might forget the download step, and a missing package
announces itself to nobody" — true, but it solved that in the wrong place: it put a build output in git,
could only ever carry the ONE rid this machine builds, and would go stale against the C++ the moment
someone edited it without rebuilding. A job dependency fixes the same failure and gets both RIDs.

### Safe-area insets

**DONE (2026-08-05) — `SafeAreaOptions` + `MobileSafeArea`, proven on device at first paint.** Four
defects were measured; two were fixed in the page and two needed the shell, because Android reports the
display cutout to CSS but never the system bars, and reports nothing at all for the whole first page
load. Record in `docs/archive/tasks.md`; adopter-facing recipe in `docs/ADOPTION.md`.

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
