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

**Status: 0.10.0 is PUBLISHED (2026-08-05)** — nine NuGet packages + `@shenora/react`, all nine confirmed
on the feed. It added three packages (`Shenora.IO`, `Shenora.IO.Compression`, `Shenora.Launcher`), the
safe-area shell capability, and **five breaking changes**, each with its migration under `### Breaking`.
`## Unreleased` carries the Android fragment-reload repair (2026-08-06). Release history and its incidents
live in `CHANGELOG.md`; the current package set is the table at the top of `docs/DECISIONS.md`; the closed
backlog is `docs/archive/tasks.md`.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This file is the maintainer's remaining
> work, and a short list here means the kit is in good shape rather than that nothing is happening — what
> SHIPPED is `CHANGELOG.md` and `docs/ROADMAP.md`. The three items below are honest about what is not
> done: one is a KNOWN unrepaired defect whose next step is a measurement, one is deliberately WAITING on
> an adopter's harvest (D15 working as intended, not a stall), and one needs a physical device.

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
- **The launcher binaries are BUILT BY THE RELEASE, both RIDs, and never committed.** `release.yml` has a
  matrix job (win-x64 + MSVC, linux-x64 + gcc) that builds and conformance-tests each one, and `publish`
  `needs:` it — so a launcher that fails conformance, or a missing RID, stops the release before anything
  is published rather than silently shipping a package short. ⚠ Committing them was tried and reverted the
  same day, history rewritten so the blob never existed: "a release might forget the download step" was a
  real risk solved in the wrong place — a build output in git, carrying only the ONE rid this machine
  builds, going stale the moment someone edited the C++ without rebuilding.
- **A one-platform build proves one platform.** The launcher's POSIX half went uncompiled until the 0.10.0
  release tried it and failed on two missing includes. Before a release that touches C++, run
  `node devtools/dev.mjs launcher --posix`; fix-log 2026-08-05.
- **NuGet lags npm on a release, by minutes.** 0.10.0 showed npm at the new version while all nine NuGet
  packages still read 0.9.1 and the three new IDs 404'd — that is the validation pipeline, not a failed
  push. All nine indexed within ~2 minutes. Re-check the feed before concluding anything is broken.

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

### The iOS half of the fragment-reload defect — KNOWN BROKEN, unrepaired, and SILENT (2026-08-06)

- [ ] **Reloading at a hash route does not come back on iOS, and nothing on screen says so.** The Android
  half is fixed and shipped (`## Unreleased`); this is the other half of the same report and it is the
  worse one. Filed by the first adopter and **now REPRODUCED HERE** (2026-08-06, simulator) — so the
  "cannot design a fix against a defect that will not reproduce" blocker is GONE, and a fix is verifiable
  in both directions the moment someone writes one.

  ```
  plain    after reload: stamp=fresh|nodes=56|title=Shenora mobile sample   ← navigated, came back
  fragment after reload: stamp=STALE|nodes=74|title=Shenora mobile sample   ← never left, after 10s
  RELOAD: FAIL — the document never navigated away at all, SILENTLY
  ```

  Matching what they measured: the reload's document request reaches the shell carrying the fragment
  (`uri='app://0.0.0.1/#/library' frag='#/library' path='/'`), and a reload at a hash route **never
  produces a second page boot** — no second bundle burst, no second IPC handshake.
  - 🔴 **The failure is INVISIBLE.** WKWebView keeps the PREVIOUS document on screen when a provisional
    navigation fails, so a screenshot afterwards shows a perfectly healthy app. "It rendered" is not
    evidence here, and this is why the item is worth keeping open rather than closing as low-impact: an
    adopter can ship it without ever seeing it.
  - **Do NOT apply the Android repair.** The adopter tried exactly that: with it registered the reload
    produced no document request at all, and `EvaluateJavaScriptAsync` stopped answering. `Shenora.iOS` is
    deliberately unchanged (`MobileWebViewInterceptor.RepairDocumentRequest`'s `#else` records why).
  - **The STAMP is the only discriminator, and that is the finding.** Everything else in the snapshot says
    the app is healthy — right title, body text intact, and a DOM *larger* than a fresh document's (74 vs
    56, because it is the fully-interacted original). A screenshot cannot catch this and neither can a
    node count; only a marker that a real navigation would have destroyed.
  - **What the next session actually has to decide**, now that it reproduces: whether the kit should own an
    iOS repair at all. The interceptor cannot be it — the adopter measured that claiming the request there
    stops the document request happening at all. The candidates worth costing are a `WKNavigationDelegate`
    hook that catches the failed provisional navigation and re-drives it, or accepting it and documenting
    hash routing as unsupported-on-reload for iOS. **Neither is obviously right, which is why this is a
    task and not a patch.**
  - ⚠ Costs a commit: `mac push` refuses a dirty tree and force-updates the Mac's `main`.
  - ⚠ **Getting a verdict out of iOS at all took two fixes to the probe first** (both shipped): a failing
    JS evaluation aborts the whole app there, and the flattening/backslash rules in
    `.claude/knowledge/mobile-shells.md`. Read those before touching `PageProbe`.

### Harvesting Sonora's on-device media — the reusable half (2026-08-06)

> DIRECTION (user, 2026-08-06): *"sonora actually got proper solution for media play and you can get its
> binary you can create resource pack to store them"* and, on where the bytes live, *"because this is a
> library so we need to ship this for adoption"*. So the kit SHIPS an engine for adopters — the open
> question was only which package carries it, not whether.

Sonora built on-device conversion + an HLS segment stream, proved both on a device, and wrote a hand-off
spec naming exactly what should and should not move — `2026-08-06-shenora-media-handoff.md`, in THAT repo
under its superpowers specs (not a doc of this repo). This entry tracks taking it. **Read that spec before
designing any of this** — every ⚠ in it is a bug that was actually hit, not a guess.

- [x] ~~**The resource pack.**~~ DONE — `ResourcePack` in `Shenora.IO.Compression`. Moved to the archive
  once the rest of this entry lands; kept here only because the two items below build on it.

- [ ] **Lift the segment stream.** `SegmentStream.cs` (719 lines, **8 app-specific references** — it was
  written portable and is the bulk of the value), `ISegmentEngine` (122, already kit-shaped and its own doc
  references `Shenora.Media`), `ConvertedMedia` (51). Suggested shape from the spec:
  `interceptor.UseSegmentStream(options)` beside the existing `UseMediaConversion`, taking an app-supplied
  engine and a `Resolve` — the app then keeps only its engine implementation.
  - 🔴 **The contract to carry above all: EXIT 0 IS NOT EVIDENCE.** An encoder can accept every frame,
    write nothing, and exit 0 — measured (`h264_mediacodec` on the emulator: advertised by `-encoders` AND
    `MediaCodecList`, opens, maps `hevc→h264`, writes `video:0KiB`, exits 0). So the route must verify the
    OUTPUT before publishing to its cache. And **"has a video stream" is the wrong test** — MPEG-TS names
    streams in the PMT, so a picture-less segment still declares one; the right question is whether it has
    a SIZE. One bug, two predicates, which is why `ISegmentEngine` splits `HasPicture` from
    `HasRenderedPicture`. A kit that offers the verification hook saves every consumer the same day.
  - Five traps the spec records, all paid for once: the CALLER owns the output filename and therefore the
    muxer (`.m4a.tmp` → exit 234, nothing written); cache tenancy is two things and iOS purges one of them;
    a purged cache dir 503s forever; `-c copy` cannot hit a fixed grid; the last `#EXTINF` is not
    full-length.
  - **Do NOT lift**: the four `Ffmpeg*.cs` files (749 lines of decisions about THAT build), the codec
    policy (⚠ `alac` is listed only under `#if IOS`, because WebKit decodes Apple Lossless and Chromium
    does not — a kit-side list could not express that), or the build script.

- [ ] **Ship an engine for adoption — packaging + licence, and this is the item with real-world risk.**
  Owner's call is that the kit ships it. Planned as an OPT-IN package so a desktop app that never converts
  media pays neither the megabytes nor the obligations; `generic-library.md` sanctions exactly this shape
  (*"a package for optional WEIGHT"* — only some apps do it, and it is real shipped weight).
  - **Sizes, measured:** 22 MB (android arm64-v8a) + 27 MB (x86_64), and 110 MB for the Windows
    `ffmpeg.exe`. All gitignored in the source app on purpose.
  - ⚠ **The build is LGPL-only BY DESIGN and that must not be lost in the move.** No `--enable-gpl` /
    `--enable-nonfree`, because linking libx264 is GPL and **relicenses the consuming app**; the
    licence-clean H.264 encoder is the platform's, via `--enable-mediacodec --enable-jni`, and openh264 is
    Cisco's under 2-clause BSD. **Whoever ships the binary inherits the LGPL duties** — attribution, and
    preserving the ability to relink — on behalf of every consumer of that package. That is the whole
    reason the source app kept the script rather than the binaries.
  - **Decide before publishing anything:** whether the kit REBUILDS from a tracked script (reproducible,
    and the licence claim is ours to make) or republishes a binary it did not build (cheaper, and the
    provenance claim is someone else's). nuget.org's 250 MB package limit also argues for per-RID packages
    rather than one.

### From the first adopter — mobile media conversion has no engine (2026-08-06)

- [ ] **🎧 `UseMediaConversion` has no engine under it on MOBILE, and every adopter will write the same
  several hundred lines of native interop to supply one.** Not a challenge to D42 — "the right encoder
  differs per app and a bundled one is tens of megabytes every consumer pays for" is right, and a codec
  baked into `Shenora.Media` would be wrong. The gap is one layer below that decision.

  **What the adopter hits.** `UseMediaConversion` composes beautifully — mission scheduling, `PathClaims`
  so a source converts once, `MissionKey` dedup, `BeginReplace` for atomic output, `DerivedCacheKey` for
  invalidation. All the hard *plumbing* is there. Then `Convert` needs an engine, and on mobile that is not
  "call the app's ffmpeg": **iOS forbids `fork`/`exec` entirely**, so a CLI binary is not an option at all
  and the only route is linking `libav*` and calling it in-process. Android can exec from
  `nativeLibraryDir`, so the two platforms want *different shapes* — which means the adopter writes a
  per-platform abstraction that has nothing to do with their app.

  **Measured, on Android 12 / AOSP codecs, which is why this matters rather than being theoretical:**
  the platform's own decoders are `aac flac mp3 opus pcm vorbis` (+ telephony) with an AAC encoder —
  **barely wider than the WebView's own set**, and missing `alac`, the single codec that drives transcodes
  for our app on that platform. So `MediaCodec`/`AVAssetExportSession` cannot be the engine: a conversion
  route built on platform codecs has an **empty benefit window**. Anyone reaching for `UseMediaConversion`
  on mobile ends up needing ffmpeg, and therefore ends up writing the same interop.

  **The ask, shaped to keep D42 intact:** an **optional companion package** — `Shenora.Media.FFmpeg` or
  just a documented recipe — so the size cost is opt-in rather than baked into `Shenora.Media`. Even the
  recipe alone would be worth it: the per-platform invocation reality above (exec vs in-process link) is
  the part that costs an adopter a day to discover, and it is a property of the platforms, not of any app.

  ⚠ **This is squarely your own stated measure** — *"not 'does the kit expose the API' but 'how much native
  code does the adopting app still have to write'"*. Today the answer for mobile media conversion is "all
  of it".

  **We are building it in Sonora first**, per the harvest-driven growth direction — if it generalises, it
  is yours to take. Expect a report either way, including the LGPL packaging duties (dynamic linking to
  keep relinking possible, no `--enable-gpl`, attribution), which are also per-platform and also not
  app-specific.

  > **KIT-SIDE POSITION (2026-08-06): deliberately NOT started, and this is D15 working rather than a
  > stall.** The adopter is building it in their own repo first and has offered the harvest; building a
  > `Shenora.Media.FFmpeg` here *before* that report would be inventing the abstraction instead of lifting a
  > proven one, which is the extraction-first rule the whole kit is built on. Their measurement — AOSP
  > decoders being `aac flac mp3 opus pcm vorbis`, barely wider than the WebView's own set, so a
  > platform-codec engine has an **empty benefit window** — is the load-bearing finding and is worth
  > re-reading before anyone designs against `MediaCodec`/`AVAssetExportSession`.
  >
  > **What to decide when the report lands** (owner's call, and worth deciding deliberately rather than by
  > momentum): a package vs a documented recipe. The recipe is the cheaper half and carries most of the
  > value they name — *exec on Android, in-process link on iOS* is a property of the PLATFORMS, not of any
  > app, and it is the day an adopter loses. A package additionally owns binary size, per-platform build and
  > the LGPL duties, which is a materially bigger commitment than anything the kit ships today.

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
