# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**DELETED**, not checked off in place — so the length of this file is the size of the remaining work,
which is the whole point of looking at it. Git is the archive (2026-08-07).
`CHANGELOG.md` is the release-facing log of what shipped and why. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

⚠ **An entry is either OPEN or GONE — never annotated `DONE` in place.** This rule was broken twice on
2026-08-05 and the file reached 502 lines while holding six open tasks; the tell is always the same, the
length stops tracking the remaining work. Two of the stale entries still showed an unchecked `[ ]` a day
after they shipped.

**Status: 0.10.0 is PUBLISHED (2026-08-05)** — that release shipped nine NuGet packages + `@shenora/react`,
all nine confirmed on the feed, and **that count is now HISTORY**: D53/D55/D65 folded four of them away and
D67 added a second npm package. 🔴 **The current set is the table at the top of `docs/DECISIONS.md` — five
packable projects + two npm — and nowhere else.**
🔴 **`## Unreleased` is LARGE and mostly BREAKING** — D64/D65/D66 all landed in it: the framework on by
default, the three layers, operations merged into `IpcRequest`, request tracking moved to the dispatch
boundary, the pipeline surface on `ShenoraApplication`, and the `Use`-versus-`Add` rule. Read
`CHANGELOG.md`'s `### Breaking` before touching the surface.
**The release is deliberately ON HOLD** (owner, 2026-08-08: *"im still holding the release since currenly
stage this is not a proper version for app to use yet"*) — so correctness beats cosmetics here, and a
half-finished surface is a reason to keep working rather than to cut.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This file is the maintainer's remaining
> work, and a short list here means the kit is in good shape rather than that nothing is happening — what
> SHIPPED is `CHANGELOG.md`. The entries below are honest about what is not done: several are deliberately
> WAITING on an adopter's harvest (D15 working as intended, not a stall), and two need a HUMAN with a
> device — iOS background playback, and the Live Activity decision.

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

> ⚠ **Designing anything in media? The reading list, in this order: `D59` (what the converter is FOR — the
> gap between what the DEVICE decodes and what its WEBVIEW accepts, nothing wider), `D58` (the interceptor
> route is the player's output pipe, not a parallel feature), `D51` (no engine byte ever ships), `D42`
> (the kit ships the QUESTION, never a codec list), then `D63` (the defect class this subsystem kept
> producing).** D52 and D53 are still true and are the earlier framing D59 sharpened.

### D65/D66 leftovers — the restructure is otherwise CLOSED (2026-08-08)

**Read D65 first.** Core is the CONTRACT (IPC · EventBus · RouteInterceptor) · logic is the BRAIN
(missions, safe file mutation) · features BRIDGE .NET to the web (media, dialogs, update). The
membership test: *must both sides agree on it?* → core. *Pure computation the page never sees?* → logic.
*Carries a .NET capability to the page?* → feature.

**Standing constraint, not a task:** `Sample.Maui` sets `net10.0-ios` only under
`$([MSBuild]::IsOSPlatform('osx'))`, so `MainPage`'s `#if IOS` branch **cannot** be verified on Windows by
construction. That is why `mac build` — and now `shenora ios deploy` (D67) — exist.

### Open sample decisions

- [ ] **Ship a fixed-version WebView2 bundle, or stay Evergreen — decide it on its merits.**
  Raised by the owner 2026-08-08 as "the webview2 bundle should be at the application location so it
  should never be shared". Checked against both desktop siblings, and the check SPLITS the question:
  - **The USER-DATA folder is already app-local everywhere** — the kit uses `paths.DataArea("webview2")`
    and both siblings do the same. Nothing is shared today; nothing needs changing.
  - **The BROWSER BINARIES are Evergreen everywhere, including both siblings**, which pass
    `browserExecutableFolder: null` explicitly. A fixed-version bundle is therefore a NEW position, not
    a harvest (D15's bar): ~150 MB per app, plus ownership of the security updates Evergreen handles.
  - The argument FOR is determinism — a framework whose pitch is "the shell" arguably should ship the
    browser it was tested against. The kit already supports it
    (`WebViewEnvironmentOptions.BrowserExecutableFolder`), so this is a DEFAULT and a DOC, not a feature.

⚠ **After ANY rename sweep in this work: READ THE DIFF.** Three instances of "a thing is not itself" from
D37's 2026-08-02 merge survived every review until the D65 sweep surfaced them, because `doc-drift` is
blind to the class by construction — the retired name is gone, so nothing is left to match.

### 🔴 D64 — MAKE THE FRAMEWORK ON BY DEFAULT. In flight (2026-08-07)

> DIRECTION (owner, 2026-08-07): *"this is a full react+.net application framework, media, filesystem
> should probably do the same, and those `use` function basiclly just a way to override or configure"* ·
> *"because non-of them will work without frontend ask via ipc/routing"* · *"this will minimize the
> recoding for all implmentations since they never changes (most the cases)"*.

**Read D64 first — it carries the reasoning and the one trap.** The safety argument is the load-bearing
one: none of these capabilities does anything until the frontend asks, so opt-in gating buys nothing while
containment still fails closed.

**The target shape, which is ASP.NET Core's minimal hosting model** (owner: *"think about this should be
like how .net webapp build setup style"*). D64's table maps every call; the short version:

```csharp
var builder = ShenoraApplication.CreateBuilder(args);   // the framework is ON — engines, modules, dispatcher

builder.UseMissions(x => x.GlobalLaneCapacity = 4);   // CONFIGURE a core module, optional
builder.Services.AddIpcModule<MyModule>();            // the app's own routes — container level, so Add
builder.UseWindows(new WindowsHostOptions { … });     // the ONE platform call — it INJECTS

var app = builder.Build();

app.UseFiles(new WebViewFileOptions { … });   // the PIPELINE — order matters, like app.UseAuthentication()
app.UseMediaPlayer();                          // no `services` argument: the app carries the provider
app.Run();
```

🔴 **SETTLED: `Use*` STAYS. Do not "fix the `Add*`/`Use*` category error" — it was tried and reverted.**
This entry used to sit here demanding that `UseMissions`/`UseFileSystem`/`UseMediaPlayer` move to
`Services.AddShenora*` on the ASP.NET DI-versus-pipeline reading. **D64 records the whole round trip**: the
rename landed, the owner reverted it, and it was re-confirmed on 2026-08-08 in their own words —
> *"we should be use `use` since this is more like middleware/interceptor as in its actual logic"*

**THE TEST, sharpened by the owner the same day and now the rule in D64:**
> *"`Use` means a wider configuration including its pipeline interceptor, and `Add` only means the
> service collection level."*

So `Use*` is right for anything that touches a PIPELINE, and `Add*` is right for plain container
registration. ⚠ **The first pass renamed all four `Add*` and three were reverted** — a module
REGISTRATION is not a pipeline stage, even though a stage is built from it later. Only
`AddMessageDispatcher` moved (it composes the dispatch pipeline). The ASP.NET analogy settles **WHICH
OBJECT owns the call**, never the prefix. Kept as a tombstone: this is the third time it has been argued.

> DIRECTION (owner, 2026-08-08): *"the service should be override inside `useXX(s => {})` config
> instead"* — so **the `Use*` configure callback is the ONE place an app configures OR SUBSTITUTES**, and
> an app should not have to reach into `builder.Services` separately to swap an implementation.

**DONE (2026-08-08): the pipeline surface is on `ShenoraApplication`.** `app.UseFiles(…)`,
`app.UseMediaPlayer()`, `app.MapModule<T>()` and the raw `app.Use(…)`, over a `WebViewPipeline` the builder
registers. The semantic was adopted as written — a step describes the pipeline for EVERY webview the app
hosts — and the pipeline FREEZES on first application, so a step declared too late throws instead of
reaching some windows and not others. Proven on the desktop sample, not just in tests:
`INTERCEPTOR SEAM: PASS … routeHits=4` means a route declared with `app.Use(…)` served real requests
through real WebView2. What remains of it is only the follow-up below.

- [ ] **Give the mobile shells the same `app.Use…()` proof the desktop now has.** The mechanism is wired
  (`MobileWebViewInterceptor` takes the pipeline as a required argument) and it COMPILES for both, but no
  device run has exercised an app-level route on Android or iOS — and the mobile API baselines are
  name-level, so they cannot see that constructor change either. Fold it into the next device pass;
  `MediaRangeProbe` is the natural subject, since it already serves a file route through the real webview.

⚠ **The rule that survives from the platform sweep, because it decides the NEXT capability too:**
*"can this platform do it?"*, never *"have we written it yet?"* — an unwritten implementation is a TASK,
and filing it as a refusal freezes a gap into the surface and makes it look decided. A refusal stays
correct only where the platform genuinely cannot.

- [ ] **`### Breaking` entry**, two parts: the module renames above, and the fact that modules the kit
  registered only on request now appear in the ready handshake — so a page branching on
  `shell.capabilities` sees more than before.

### 🔴 The media player's loop is NOT CLOSED — the hang D64's default registration removes (2026-08-07)

**The review that `TASKS.md` asked for has run.** The media namespace was read end to end; the code fixes
and the doc cleanup are committed. What it found that needs a DECISION is this one thing.

**`builder.UseMediaPlayer()` + `useMediaPlayer(ref)` do not make a working player.** The page posts
`PLAYER_REPORT` on module `MEDIA`; **the kit registers no facade for it**, so nothing turns it into
`MediaPlayer.Report(...)`. `MediaPlayer.OpenAsync` completes on the first non-`Opening` report and on
nothing else — so an adopter who wires both halves the kit ships gets an `OpenAsync` that **never
returns**, with no exception, no log line, and an element that is visibly playing. D63's class exactly, and
the fourth instance in a fortnight.

- Docs no longer claim otherwise: `ADOPTION.md` carries the four-line route as **piece 3 of 3**, and the
  XML/JSDoc on `UseMediaPlayer`, `MediaPlayer` and `useMediaPlayer` all say the joint is the app's.
**DECIDED, and shipped: the kit ships the module.** `UseMediaPlayer` registers `MediaPlayerModule`
itself (D65 — a feature owns its IPC module, so a core never learns a feature's name), and `Build()`
calls `UseMediaPlayer` for every app. The placement objection was answered rather than overruled: the
module name stays configurable through `MediaPlayerOptions.Module`, so an app that already owns a
`MEDIA` module renames the kit's instead of colliding with it.

**✅ AND THE DIAGNOSTIC HALF IS IN TOO (2026-08-08):** `MediaPlayerOptions.OpenTimeout` (30 s) turns
"nobody wired the route" from an await that never returns into a `MediaPlayerException` naming
`PLAYER_REPORT`, the module, and the knob. A caller's own cancellation still reads as cancellation.

### The rest of the review — fixed, or measured and left honest (2026-08-07)

Fixed in the same pass, each with its root cause in the commit message: a convertible-but-empty audio track
was marked kept and so never reported in `MediaRemuxerResult.Dropped` (`Mp4Remuxer`, now pinned by a
sabotage-verified test); `MFVideoFormat_HEVC` and `'HEVS'` were missing from `WindowsMediaCapability`'s
subtype table while `'H265'` was present, and unrecognised subtypes were dropped SILENTLY — so
*"no HEVC on this box"* was never attributable; and four docs still described a package set D53/D55
deleted (`ARCHITECTURE.md`'s and `REVIEW-GUIDE.md`'s opening paragraphs, a shipped XML remark on
`MediaProbeResult` citing retired D40, and a `CHANGELOG` entry saying the page-side driver was not built
after it shipped).

- [ ] **iOS's `IMediaAudioConversion` is very likely NON-FUNCTIONAL, and it will not say so.** It decodes
  AC-3 with `AudioConverterConvertBuffer`, which cannot handle variable-size compressed packets — while
  `AudioConverterNew` still SUCCEEDS for the pair, which is what `IosMediaCapability` measures. So
  `CanConvert` answers yes, the planner says `Transcode`, and every `Push` returns nothing.
  **The remuxer does catch it** (`NoCarriableStream`, *"the device could not convert ac3 after accepting
  it"*), so the outcome is an honest failure rather than a silent film — but the tier does not work on the
  one platform it was built for. **Needs a device run with an AC-3 file before anything else here is
  designed**; if it is confirmed, the fix is `AudioConverterFillComplexBuffer` with a native input
  callback, which the file's own remarks already cost out.

### Device deployment must be the KIT's, not borrowed from an app (2026-08-06)

> DIRECTION (user, 2026-08-06): *"because capacitor can, you should also allow our project can"* and
> *"we should also have those install/helper bundles with our library package"* and — after watching me lean
> on a sibling's Capacitor project to mint a profile — *"why you rely on capacitor instead create your own
> one"*. **Right on all three.** A kit whose measure is *how little native code an adopting app writes*
> cannot require the adopter to own an Xcode project just to reach a device.

**✅ BOTH DIRECTIONS ARE ANSWERED.** The loop works end to end on an iPhone 17 Pro (2026-08-07,
`dev.mjs mac provision` + `mac device` — no Capacitor, no app-owned Xcode project), and **shipping it to
adopters is `@shenora/cli` (D67, 2026-08-08)**: the shape question ("a `buildTransitive` target? a recipe
in `ADOPTION.md`?") was settled as a build-time npm package with a `shenora` binary, the same shape
`cap`/`electron` adopters already expect. The device findings live in `.claude/knowledge/mobile-shells.md`
("Deploying to a REAL iPhone"); the 7-day personal-profile expiry and the trust-the-certificate step are
in the CLI's own README, where an adopter will actually meet them.

- [ ] **Drive the DEVICE half through the CLI once.** The simulator path is proven end to end
  (build → install → launch → screenshot, 2026-08-08); `ios deploy --device` shares its code but has not
  been run with a phone attached — none was connected. ⚠ **It is the half that can fail alone**: extension
  provisioning is only enforced on hardware, which is exactly what `checkExtensions` exists to catch.
- [ ] **Wire the second npm package into the release.** `doctor` already holds its version in lockstep
  (sabotage-verified both ways), but publishing is not wired — see `docs/RELEASING.md`, which still
  describes one npm package.

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

> **SUPERSEDED BY D52 — read that first.** The scope settled while this was being built: the package is a
> TRANSLATION LAYER (the minimum transformation that makes a file playable in a webview), not a media
> toolkit, and the kit ships NO ffmpeg bytes (D51). The build order below replaces what this entry
> originally proposed.

**Built so far:** `ResourcePack` (`Shenora.IO.Compression`) · `MatroskaProbe` (`Probe/`) ·
`MediaPlaybackPlanner` (`Plan/`) · `UseMediaConversion` + `UseSegmentStream` (`Serve/`) · `Mp4Remuxer` +
the `ISegmentEngine` seam (`Engine/`). **Slices 1 and 2 are CLOSED** (2026-08-07 — the pipeline reshape and
the remuxer). The pipeline is **probe → plan → serve → transform**, and
what remains of transform is the AUDIO, which is the half that needs a codec.

**✅ SLICE 3 IS MEASURED (2026-08-07) — and the answer is SPLIT PER PLATFORM, which is the finding.**
`CodecProbe` in the MAUI sample asks each platform directly; run on an iPhone 17 Pro (iOS 26.5.2, a real
device) and an API 36 AOSP emulator:

| | AC-3 decode | E-AC-3 decode | AAC decode | AAC encode |
|---|---|---|---|---|
| **iOS 26.5.2, iPhone 17 Pro** | **YES** (5.1 *and* stereo) | **YES** | YES | YES |
| **Android API 36, AOSP** | no | no | YES | YES |

- **So D52's "yes → a platform call" holds on iOS and fails on Android.** There is no single answer, and a
  design that assumes either one is wrong half the time.
- 🔴 **The ENCODE half is free EVERYWHERE** — both platforms encode AAC. That is a much narrower gap than
  "there is no engine": what is missing is one DECODER, on one platform.
- ⚠ **"AOSP does not" is not "Android does not."** Codec support is vendor-declared per device, which is
  exactly why `MediaCodecList` is a runtime query. A handset may well carry AC-3; this measures the
  emulator. **Never bake either answer in — ask the device.**
- ⚠ Two probe defects had to be fixed before the number meant anything:
  `kAudioFormatProperty_DecodeFormatIDs` is **macOS-only** (`'prop'` on iOS), and
  a failed query was reporting as a NEGATIVE. The AAC control is what caught it.

**Slice 4 — MOSTLY DONE (2026-08-07).** Owner: *"we still support for consumer use their own
decoder/encoder just if they needed, and we built something that can work by default"*. Shipped:

- **`IMediaCapability`** — asks the DEVICE what it decodes and encodes, implemented on both mobile shells.
  Every adopter used to hand-write `MediaPlaybackPolicy`'s codec sets as a guess; the kit now ships the
  QUESTION rather than the answer, which keeps D42 intact. Cross-checked against an independent platform
  query on the iPhone: `ac3 repairable=True`.
- **`Mp4Remuxer.ConvertAsync`** — the default `MediaConversionOptions.Convert`. Container repair with no
  engine, no binary, no licence weight. This is "working playback with NOTHING supplied" for the case D52
  calls the common one.
- **`IMediaAudioConversion`** (+ `IMediaAudioConversionRun`) — the transcode tier: picture copied
  untouched, soundtrack through the device's codecs. Both mobile shells implement it (Android chains a
  `MediaCodec` decoder → AAC encoder; iOS uses AudioToolbox).

### 🔴 THE NEXT THING: a playback LIFECYCLE in .NET — read D54 first (2026-08-07)

> DIRECTION (owner, 2026-08-07): *"our goal is to make web player as good as a regular player, which is
> th[e] a[r]chitecture lack of, and if the consumer want to build a proper player, we can support that with
> proper life cycles of media play in .net code (which is more capable than js for this kind [of] work)"*
> — and on ffmpeg: *"its too big, and its too much for what we want to achieve"*.

**This reframes the media work.** It had been drifting toward "make the webview play more", which is a
treadmill whose ceiling is still the webview. The gap is that **the PAGE owns playback and should not**.

**✅ SHIPPED AND PROVEN ON ALL THREE SHELLS.** `IMediaPlayer` + `MediaPlayerBase`, with
`WindowsMediaPlayer` (Media Foundation), `AndroidMediaPlayer` (the platform's OWN
`android.media.MediaPlayer` — **not ExoPlayer**, which D51 forbids) and `IosMediaPlayer` (AVPlayer). Each
reported `PLAYER: PASS — the host decoded a real file and advanced a real clock` on its own platform, so
the claim the feature rests on is measured rather than asserted. `player.ReportTo(session)` reconciles Now
Playing with the player's real state.

- [ ] 🔴 **Background survival is the ONE claim still unproven, and it is the reason the native player
  exists.** Nothing in the harness can make an app leave the foreground, so this needs a human: press home
  while it plays, then read `mac device-log`. A `<video>` element cannot survive that; if AVPlayer does
  not either, the feature's premise is wrong and we should know.
- [ ] **Expose it over IPC** so the page can drive it. Not done: the contract is C#-side only, so today an
  app wires it in its own module. That is the smaller half and it should follow the device proof, not
  precede it.
  - ⚠ **Does not delete the translation layer, it BOUNDS it.** `Mp4Remuxer` and the conversion pipeline
    stay for apps serving files to a `<video>`; what changes is that they stop being the answer to "the
    webview cannot play this".
  - ⚠ **And it bounds segmentation:** a native player opens the file directly, so the kit ships no default
    segmenter. `ISegmentEngine` stays the seam for progressive streaming, with the sibling's five traps
    recorded against it (below) rather than designed around.

- **The five traps a sibling paid for, kept because any segmenter must answer them** (their hand-off spec,
  2026-08-06 — two are FIXED in the kit as of 2026-08-07, three belong to work not yet done):
  | trap | state |
  |---|---|
  | the output filename picks the muxer (`.m4a.tmp` → refuses before writing) | ✅ FIXED — `MediaConversionRequest.Container` |
  | cache tenancy is two things; iOS purges `Library/Caches` | ✅ FIXED — both `CacheRoot`s document it |
  | a purged cache dir 503s forever | ⚠ documented; re-create per restart is still the app's to do |
  | `-c copy` cannot hit a fixed grid, so a synthetic manifest is illegal | open — segmentation only |
  | the last `#EXTINF` is not full-length; a scrub bar seeks past the end | open — segmentation only |

- [ ] **What remains of slice 4, and the second item is the one that matters.**
  - ✅ **BOTH mobile shells register an `IMediaAudioConversion`** (the seam is `IMediaAudioConversion` +
    `IMediaAudioConversionRun`; this entry called it `IMediaStreamConversion`, a name that never existed
    in the shipped surface, and said iOS had none). Measured on the iOS simulator 2026-08-08:
    `convert ac3: accepted=True`, `convert eac3: accepted=True`. **What is still open is whether iOS's
    actually WORKS** — see the `AudioConverterConvertBuffer` entry above; accepting is not converting.
  - 🔴 **NEITHER IMPLEMENTATION HAS RUN A REAL ENCODER.** The muxing is covered by tests against a fake
    codec — the boxes, the timing, the drained tail, copy-beats-convert — but no device has actually
    transcoded anything. **"Exit 0 is not evidence" applies hardest here**: this repo has already measured
    an encoder that accepted every frame, wrote `video:0KiB` and exited 0. Until a device produces a file
    that PLAYS, the tier is unproven.
    - ⚠ The awkward part is the fixture: proving it needs a source in a codec the target decodes, and AOSP
      has no AC-3, so the emulator cannot exercise the interesting path. Either test on a handset that has
      AC-3, or build a fixture in a codec AOSP does have (vorbis/mp3 → AAC exercises the identical chain).
  - **Windows has no `IMediaCapability`** — Media Foundation enumeration, and `DolbyDecMFT.dll` is present
    on this box so it likely decodes AC-3 too. Desktop currently answers nothing rather than answering
    honestly.

⚠ **Not planned, and needs a reason before it is:** software video decoders (MPEG-2, VC-1, Xvid, ProRes) —
per-codec projects, built only for codecs a real library is SHOWN to contain (D52 tier 4).

<details><summary>The original hand-off entry — REFERENCE, not open work (its "what not to lift" reasoning still holds)</summary>

⚠ **Deliberately checkbox-free.** All three shipped; they are kept for the porting reasoning, not as tasks,
because a `[x]` here would count against the open-work length this file exists to measure. If this block
grows further it should move to `.claude/knowledge/extraction-sources.md`, which is its real home.

- **The resource pack** — shipped as `ResourcePack` in `Shenora.IO.Compression`.

- **The segment stream** — lifted. `SegmentStream.cs` (719 lines, **8 app-specific references** — it was
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

- **An MIT-COMPATIBLE engine for adoption** — SETTLED as **D51** + **D52**; the build order is
  the slice list above. Owner, 2026-08-06: *"sonora currently is close
  sourced, but we are on MIT so we should build one compatible with MIT"*. **Settled as D51 — read it
  first.** The source app's LGPL ffmpeg is fine *for that app* and must NOT be redistributed from an MIT
  package: it would make the kit the redistributor and hand attribution + relinking duties to every
  consumer. **So lifting its 22/27/110 MB binaries is now OFF the table** — what remains is building or
  selecting an engine whose bytes are MIT/BSD/Apache-2.0.
  - **Preference order (D51):** the PLATFORM's own codecs first (`MediaCodec` / VideoToolbox / Media
    Foundation — zero bytes, zero licence weight), then permissive libraries only where the platform
    genuinely lacks something (openh264 BSD-2, dav1d BSD-2, libvpx BSD-3, Opus BSD-3, libFLAC BSD-3, Apple
    ALAC Apache-2.0). Never x264/x265 (GPL), fdk-aac or LAME.
  - ⚠ **Be honest about the platform tier's ceiling.** The source app measured AOSP's set as
    `aac flac mp3 opus pcm vorbis` + an AAC encoder — *barely wider than the WebView's own*, and missing
    `alac`. So a platform-only engine has a small benefit window on Android; ALAC specifically is
    answerable permissively (Apache-2.0 reference implementation) rather than by reaching for ffmpeg.
  - ⚠ **Licence ≠ patents.** A BSD licence on openh264 does not grant H.264 patent rights, and Cisco's
    royalty coverage attaches to THEIR prebuilt binary fetched at runtime, not to one built from source.
    Owner's call per codec; do not let a green licence check read as a settled patent position.
  - **What is already unblocked:** an app that wants LGPL ffmpeg needs nothing from this item — it supplies
    its own archive through `ResourcePack` and keeps the obligation, which is where the source app had
    already put it.

</details>

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

**`IPlaybackSession` shipped and is verified on all three shells** (0.9.0/0.9.1). Recipes in
`docs/ADOPTION.md`; mechanics in `.claude/knowledge/mobile-shells.md`.

### iOS background playback — UNRESOLVED, and my first answer was WRONG (2026-08-07)

- [ ] **Establish what actually survives backgrounding, with an `<audio>` element.** The sample now sets
  both required halves — `UIBackgroundModes: [audio]` and an active `AVAudioSession(.Playback)` — and
  playback still stopped on a swipe. **But the test used a `<video>` element, which iOS pauses on
  backgrounding by design** (the video track cannot render). So that run measured the pause behaviour and
  says nothing about whether the host is configured correctly.
  - The reported behaviour for `<audio>` is that playback already in progress CONTINUES, and what is
    restricted is STARTING new playback while backgrounded or locked. **Untested here** — the sample has no
    `<audio>` element, which is the gap to close before anything is concluded.
  - ⚠⚠ **I first recorded this as "iOS blocks webview background audio, needs Apple-internal entitlements".
    That was FALSE** — generalised from one Apple-forum thread about an iOS 13 regression, on the strength
    of a single failing test that used the one element type which legitimately pauses. Corrected in
    `.claude/knowledge/mobile-shells.md`, where the lesson is kept: a single negative on a device is not a
    platform limit, and a search result about an old OS version is not a statement about the current one.
  - Only if `<audio>` also fails does the question of a NATIVE playback seam arise — and that is a big
    surface (formats, buffering, seeking, interruption, route changes), so the narrow version worth costing
    first would be a seam the app implements, like `ISegmentEngine`, not a player the kit owns.

### 🔴 THE iOS LIVE ACTIVITY DEVKIT DOES NOT WORK ON A DEVICE (2026-08-07) — decision needed

**The widget never renders on real hardware, and the cause is structural rather than a bug to patch.**
Verified on an iPhone 17 Pro: ActivityKit starts the activity and returns an id, updates are accepted, the
system reserves Island space — and the widget process never runs. Its binary has `LC_MAIN` pointing at
Swift's `main`, so `WidgetBundle.main()` runs, **returns**, and the process exits before serving anything.

**An app extension does not start at `main`.** Xcode's Widget Extension target links with
`-e _NSExtensionMain`, so the process begins in Foundation's extension entry point, reads the `NSExtension`
dict and enters the XPC run loop its host talks to. A bare `swiftc` has no notion of an extension target and
applies none of that; passing `-Xlinker -e -Xlinker _NSExtensionMain` was tried and **silently dropped**
(the built binary still has a normal `LC_MAIN`). Others building widgets outside Xcode hit the same wall and
the documented answer is a real Xcode target of product type `com.apple.product-type.app-extension`.

⚠ **So the devkit's headline claim — "one MSBuild property plus four SwiftUI view bodies, no `.xcodeproj`" —
is FALSE on a device.** It was "verified" on a simulator since 0.9.0, and a simulator loads the bundle
regardless. This is the 0.9.0 lesson for the third time in one file's history.

- [ ] **DECIDE, and neither option is obviously right.**
  - **(a) The kit owns a real widget `.xcodeproj`**, generated or templated the way `devtools/ios-provision`
    already is for profiles, driven from the same MSBuild property so the adopter still writes only Swift
    views. Keeps the promise; costs a generated Xcode target in the build, and `xcodebuild` on every iOS
    build that opts in.
  - **(b) Document the devkit as requiring the adopter's own widget extension target**, and ship only the
    Swift shim + `ILiveActivities`. Honest and cheap; abandons the "no `.xcodeproj`" promise, which was the
    whole selling point.

- [ ] **THEN: remove the Swift authoring burden with a C# → Swift translation layer.**
  > DIRECTION (owner, 2026-08-07): *"what we can do to remove the swift dependency is to create a proper
  > config/translation layer from c# -> swift"*.

  The adopter describes the Island's CONTENT in C# — a declarative layout (regions → text bound to a state
  field, a symbol, a progress bar, an image) — and an MSBuild step GENERATES the SwiftUI from it. The kit
  already generates the extension, its plist, the build and the signing; the views are the only thing left
  that an adopter hand-writes, and they are the part that requires learning a second language.
  - ⚠ **ORDER MATTERS: this must come AFTER the extension actually loads.** Generating nicer Swift on top
    of a widget that exits before serving would produce a bigger, better-looking thing that still renders
    nothing — and the generator would then be suspected of the bug it did not cause. Fix (a) or (b) first,
    prove a hand-written widget renders on a DEVICE, then remove the authoring.
  - ⚠ **It brushes against D13** (the kit ships no design system). A declarative model where the app
    supplies content and SEMANTICS while the kit picks the LOOK is a different thing from a component
    library, and defensible — but it is a decision to make deliberately, not to slide into. The honest test:
    can an adopter still express their own look, or have we frozen ours into everyone's Island?
  - SwiftUI views compile at BUILD time, so the generation is a build step and cannot be dynamic. That is
    fine — `swiftc` already runs there — but it means the layout model is fixed per build, not per activity.
  - ⚠ Also worth settling: a sibling's iOS notes argue **a media app should not use a Live Activity for
    playback at all** — it duplicates the presentation the system already gives media apps, and App Review
    pushes back. `IPlaybackSession` is the right answer for a player, which shrinks how much this matters.

**Fixed along the way, all real and all still needed** (they were masking each other): a stale appex bundle
id from an incremental check that did not cover the plist · no entitlements and no `embedded.mobileprovision`
on the extension · missing `CFBundleSupportedPlatforms`/`DTPlatformName`/`DTSDKName`/`UIDeviceFamily` ·
the sample claiming the Island twice (Now Playing **and** a Live Activity — they are mutually exclusive).
**`node devtools/dev.mjs mac appex-check`** now gates the signing and platform-key classes off the build
output, needing no device.
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

---

## Later / candidates — deliberately NOT built, kept so the decision is not re-argued

Moved here 2026-08-07 when `docs/ROADMAP.md` was deleted; growth is harvest-driven (D15) and
adoption-driven, so "next" is not a phase.

- **`Shenora.Hosting.AspNetCore`** (SPA static policy, loopback-gated endpoint helpers) — D10.
- **A mobile transport adapter** speaking the same IPC envelope — D16. The decision point is unchanged
  (first real mobile adoption), and the .NET-side surface such a shell would implement is enumerated
  rather than hypothetical: D20's portable contracts in `Shenora` (`IUiDispatcher`, `IFileDialogs`,
  `IClipboardService`, `IUrlLauncher`, `IUiInteraction`). D16 covers the transport seam, D20 the feature
  seams; neither ships an implementation until there is a consumer.
- **Contract codegen (C# ⇄ TS)** — out of initial scope; revisit after adoption feedback. ⚠ Related but
  distinct: the C#→Swift generator for Live Activities, which is blocked on the devkit's device defect.
- **Harvest-promotions from ongoing app development** (D15) — anything proven in a sibling gets
  generalised and lands here as a task before shipping in a minor.
