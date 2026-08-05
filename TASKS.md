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

- [ ] **A release must STAGE the launcher artifacts before packing them.** `src/Shenora.Launcher.Native`
  packs `artifacts/runtimes/**`, and nothing fills that folder yet: the binaries come from the `launcher`
  workflow's two runners, so a release has to download both `launcher-win-x64` and `launcher-linux-x64`
  into it first. Procedure is in `docs/RELEASING.md`; wiring it into `release.yml` is the owner's, since
  that workflow is theirs.
  - ⚠ **The failure mode is a MISSING package, not a broken one** — `dev.mjs pack` skips that project and
    says why, and `dotnet pack` on it errors rather than emitting an empty `runtimes/`. That is the safe
    direction, and it also means a release would ship without the launcher and look entirely normal.

### Safe-area insets — the page cannot solve this, the SHELL must

- [ ] 🔴 **The shell must hand the page its window insets.** Three defects were found on a device
  (2026-08-05, Android 16 / API 36, punch-hole emulator) and **two of them are unfixable from CSS or
  page JS** — proven by trying:
  1. ~~inset padding on a scrolling `<body>` scrolls away~~ — FIXED in the sample: body is a
     non-scrolling flex column, a child scrolls.
  2. ~~`calc(12px + inset)` stacks two paddings~~ — FIXED: `max(12px, inset)`. Reserved 61 CSS px where
     the platform asked for 49.
  3. 🔴 **Android reports the display CUTOUT only, never the system bars.** Measured `bottom=0` on a
     device whose navigation bar is genuinely 24 CSS px — so content sits under the gesture pill and
     CSS cannot discover it. iOS reports both.
  4. 🔴 **The insets are 0 for the WHOLE first page load** and only appear on a later one. A page-side
     re-read (rAF + timeout + `resize` + `visualViewport`) was written and **did not help** — no change
     event ever fires, because the value never becomes non-zero in that document. The sample's own
     reload probe is what makes the first screen *look* right, which is why this hid for so long.
  - **So the fix is a shell capability, not page CSS:** read `WindowInsetsCompat` (Android) /
    `safeAreaInsets` (iOS) and push them to the page as CSS custom properties, re-pushing on change.
    The page already prefers `var(--sa-*)` with `env()` as fallback, so the host half is all that is
    missing. This fits the owner's platform-logic direction below: the measure is how little native
    code an adopting app writes, and today every adopter has to solve this themselves — badly, because
    two of the four defects are invisible without a device.
  - Full measurements and the three traps: `.claude/knowledge/mobile-shells.md`.

  > **DIRECTION (owner, 2026-08-05):** *"we better have some pre configured default height for this hole
  > area, and when it changes we do an animation … a splash screen is also the solution"*, then:
  > *"we should be able to let consumer choose to use or not use the splash, and use or not use the
  > default size, we should be provide enough freedom for cusomer but prove all our solution works"*.

  - **The shape this asks for is THREE independent, individually declinable mechanisms** — D21's
    primitives-and-hooks rule applied to a UX problem rather than a feature:
    1. **A pre-configured default inset**, so the first paint is right rather than zero. Opt-out (an app
       that would rather render flush takes nothing) and configurable (an app that knows its device
       class sets the number and never sees a correction).
    2. **An animated settle** when the real value replaces the default, so the unavoidable reflow reads
       as intentional. Opt-out — an app with its own motion language will want its own curve, and one
       that dislikes motion wants none.
    3. **A splash that spans the gap.** ⚠ Evidence this is real: on this emulator the MAUI splash is
       STILL UP at 2 s while the page's first paint has already happened behind it — so whether an
       adopter ever sees the wrong first screen is a RACE they are currently winning or losing by luck.
       The kit already ships `SplashPanel` on desktop; the mobile equivalent is the hook.
  - ⚠ **"Prove all our solution works" is the load-bearing half of that direction.** Each of the three
    needs its own device evidence, and two of them are invisible in a screenshot taken at the wrong
    moment — the default is only observable BEFORE the real inset lands, and the animation only in the
    ~180 ms between. Timed captures, or a probe that reports the value it laid out with, not a photo of
    the settled state. The measurement probe already in the sample page is the starting point.

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
