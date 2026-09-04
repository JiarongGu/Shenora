# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred SIX
times — 502 lines holding six open tasks, then 570 holding three, 458 holding seven, 197 holding six, 123
holding five, and 182 holding two. `doc-shape` fails on a done MARKER, and the recurrences it could not see
are the ones without one: **no marker at all, just finished work narrated at length**, plus a marker written
as `**✅ …**` that its regex read straight past. ⚠ **The test is not "is there a ✅", it is "would deleting
this paragraph lose anything a future session must ACT on?"** If the answer is no, the commit that landed it
is where it lives.
⚠ **Length is now measured too** — `doc-shape` WARNS past 120 lines, which every one of those six cleared by
60+. A crude proxy for a judgement no script can make, and the only signal the marker check cannot miss.

**Status: v0.16.0 is PUBLISHED and VERIFIED LIVE** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli` answer 0.16.0, checked against the registries rather than the tree. Tag `v0.16.0`, release
commit `f61d410`, `origin == local`.
⚠ **It took three reads over ~3 minutes, and that is normal**: npm was immediate, four NuGet packages
flipped after a minute and `Shenora.iOS` a minute behind them — exactly 0.13.0's pattern. **A partial read
is validation lag, not a half-landed release** (the workflow tags only after every publish succeeds, and
publishes NuGet BEFORE npm). Re-check, never re-push.
It carried **window orientation** (the last Capacitor-parity primitive), the notification path's own
report of what it accepted/filtered/dropped/delivered, and the Android recreation crash — a font-scale
change killed the app 8 times in 10 before, 0 in 10 after, and it needs no adopter action. **A MINOR, not
a patch**: no `### Breaking`, 358 → 363 public types, 0 removals.
⚠ `src/Directory.Build.props` must now stay at `0.16.0`: the release workflow owns the bump, and a
hand-bump moves the baseline and skips a release (`release-discipline.md`).

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 🎬 THE PICTURE SURFACE (D80) — Android is answered, iOS is not

Android is done end to end, pixels included: the run, the layer table and the two refutations are in
`docs/design/media.md`. What is left is all on the other shell.

- [ ] **Re-measure the container delta on iOS and on an older WebView before D52's example is trusted.**
  It is cited as the thing the media tier exists for, and it now has one device saying otherwise.
- [ ] **Run the stage ladder on iOS.** The sample drives the same five rungs there and they have never
  executed — WKWebView carries a THIRD layer the Android chain does not (the scroll view's own
  background, which `MobileWebViewTransparency` already clears), so the rung that fails, if one does, is
  the informative outcome. Needs a Mac: nothing on this box compiles the iOS TFM.
- [ ] **Re-check the safe-area probe on iOS**, and that `Shenora.iOS` compiles at all — nothing on this box
  does (`dotnet workload list` → `maui-android` alone). The sample's `Content` became a `Grid` (with
  `SafeAreaEdges.None` to restore edge-to-edge), and that is the property iOS actually reads.

### 🟡 THE PLAYBACK HEALTH FIGURES — the adopter has now BUILT them, so this is a harvest call

`MediaPlayerStatus.Engine` answers *which* player ran, never *how well*. The adopter has since shipped both
halves against their own engines — LibVLC's `Media.Statistics` for lost pictures, and iOS's
`AVPlayer.TimeControlStatus` for a pause the app never asked for — after finding their own *"LibVLC exposes
no counters"* comment was false.

- [ ] **Decide whether the kit takes them (D15).** ⚠ It is not a free default like `Engine`: each shell
  reads a different platform API, and theirs are LibVLC/ExoPlayer while the kit's are `AVPlayer` and
  `android.media.MediaPlayer` — so the SHAPE harvests, the implementation does not.
  ⚠ **-1 means "no figure" and must never be 0**, which claims a clean play; confirmed on their side too
  (`Stalls` stays -1 deliberately — LibVLC has no stall counter).

### 🟡 D80 MAKES A LATENT iOS DEFECT VISIBLE — a seek before the item is ready

`IosMediaPlayer.SeekCore` seeks unconditionally, and `SeekAsync` only requires a source, not a READY one —
so a `SEEK` arriving while a load is in flight seeks an unprepared `AVPlayerItem`. The adopter paid for
this one: the decoder emits frames before it holds the references they depend on, and **green** is what a
YUV buffer with no luma written looks like, until the next keyframe. Codec-dependent by GOP length, so it
reads as "one file type is broken".

⚠ **It was harmless until now**, which is why it is filed rather than fixed blind: with no surface the
picture was never composited, so the green frames had nowhere to appear.
⚠ **The common case is already safe by design** — `MediaSource.StartAt` is applied from `OnOpened`, i.e.
after `ReadyToPlay`, which is exactly their fix. Only an explicit page seek during `Opening` reaches it.

- [ ] **Defer a seek issued before the item is ready**, and apply it on `OnOpened` like `StartAt` already
  is. Needs a Mac to verify, and the fix belongs in `IosMediaPlayer` rather than the shared state machine
  (Android's `MediaPlayer` queues a seek during prepare itself).

### 📱 WHAT IS LEFT ON ANDROID NEEDS A PHONE'S ENCODER, NOT AN EMULATOR'S

The segment tier is answered on all three shells (`docs/design/media.md`), and the encoder's ARITHMETIC is
now confirmed on an API 36 AVD: 1280×720@30 asks for 4,147 kbps, and the 400 kbps floor without the
frame-rate factor. What no emulator here can answer is the CONSEQUENCE — a software encoder ignores the
request, producing ~1.7–1.9 Mbps either way. ⚠ MuMu converts no picture at all; the AVD converts only
mpeg4.

- [ ] **Confirm the encoder change CHANGES ANYTHING, on a phone.** A hardware encoder that honours
  `KEY_BIT_RATE` is the only instrument for "output size and encode cost", and the claim that this path is
  "newly hot" for ordinary 1080p H.264 rests on it. The host logs the rate it requests now, so the run is
  a read of two numbers.
- [ ] **Decide what the writer should do with a REORDERED encoder.** Same runs lost 1–6 frames of 149: the
  writer fail-closes on a backwards presentation time and drops the frame. The phone measured 60/60, so
  this is per-encoder, not settled. Dropping is safe and lossy; buffering and sorting is neither. **An
  owner call**, and it needs the phone number re-measured first.

### 🔴 ORIENTATION ON iOS IS A REGRESSION THE KIT CAUSED, NOT AN ENHANCEMENT

Android holds it (`IWindowOrientation`, measured: the page's viewport goes `412×915` → `915×412` under a
landscape lock). iOS REFUSES rather than half-working (D39), because `requestGeometryUpdate` rotates the
window while the root view controller still decides what it supports — so the next device rotation undoes
it. That was filed as "merely an enhancement". **It is not, and reading the adopter's tree is what showed
it:** they adopted 0.16.0's orientation, DELETED their own working `LockPortrait`/`UnlockOrientation` and
their `screenOrientation` capability, and now guard the whole thing on `MobileWindowOrientation.IsSupported`
— which is `false` on iOS. Their `Info.plist` permits portrait and both landscapes, so **their iPhone build
lost its portrait lock and nobody has looked**: their own verification row for orientation says "on an
Android phone".

- [ ] **Give iOS a real lock — a view-controller hook**, so the shell answers
  `GetSupportedInterfaceOrientations` instead of merely requesting a geometry change. Needs a MAUI handler
  override and a Mac to prove it. ⚠ **Until it lands, say so where an adopter reads it**: a capability that
  is honestly absent is still a feature they may have deleted to take it.

⚠ **Resume deliberately does NOT duplicate `document.visibilitychange`**, which already fires on both shells
— it reports the one thing a throttled, possibly frozen page cannot measure: **how long it was away**. If a
future session is tempted to add a visibility event, that is the reason not to.

### 📱 THE RECREATION CRASH IS FIXED AND AUTOMATIC — one case is still unmeasured

`MobileIpcBridgeOptions.ReleaseHandlerOnDispose` is ON by default (owner, 2026-08-23: make it app config,
not a step to remember — there is one adopter and they are on Android). 8/10 font-scale changes killed the
app before; 0/10 after, measured on API 36 with no explicit call anywhere in the sample.

**Half-measured now** (`HandlerReleaseProbe`, opt-in per launch — env `SHENORA_SAMPLE_HANDLER_RELEASE` or a
`handler-release` file in the cache dir): releasing the handler on a live webview **does not crash the app**,
and an evaluation against the released view **never completes** — it does not throw, so an adopter's `await`
hangs with nothing to read. `docs/guides/mobile.md` carries that, and the recommendation no longer rests on
a hedge.

- [ ] **Measure the real NAVIGATION case.** ⚠ **Do NOT re-parent the view inside its layout** — that
  shortcut is what this probe tried and it throws `MauiContext should have been set on parent`, which is an
  artefact of re-parenting a handler-less view and says nothing about navigation. MAUI unloads and reloads a
  PAGE while the view keeps its parent. **The faithful mechanic is swapping `Window.Page` away and back**,
  which fires `Unloaded`/`Loaded` on the same page instance. ⚠ It needs a run-once guard: the sample's whole
  probe suite re-runs from `OnLoaded`, so the swap-back would recurse into this probe.
  ⚠ **No adopter needs this yet** — the only one has a single `ContentPage` and no `PushAsync` at all, which
  is the ordinary hybrid shape. It stays filed because the default is ON and the failure is silent.

