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

### 🟡 `MediaSurfaceView` HAS NO UNIT COVERAGE, AND THAT IS WHY A DEFECT REACHED A DEVICE

It lives in `Shenora.Mobile`, which compiles only for the android/ios TFMs, while `Shenora.Tests` is
`net10.0` — so nothing in the suite can construct it. The handle/player rendezvous shipped broken because
of that: a handle arriving BEFORE the player was dropped for good, while the XML claimed *"the handler
attaches on whichever comes second"*. Only the device found it.

- [ ] **Give the rendezvous a test that runs in the gate.** The logic is pure — remember a handle, hand it
  to whichever arrives second, detach the outgoing player — so it does not need MAUI. Moving it to a
  plain type in `Shenora` that `MediaSurfaceView` delegates to would put it under the suite. ⚠ Weigh that
  against a public type existing only for testability; the alternative is accepting that this class is
  device-tested only, and SAYING so where the claim is made.

### 🎬 THE PICTURE SURFACE (D80) — what the AVD could not answer

The Android run is in `docs/design/media.md`; the engine substitution is settled and the A/B there
refutes D52's headline example on a modern Android WebView. What is left:

- [ ] **Re-measure the container delta on iOS and on an older WebView before D52's example is trusted.**
  It is cited as the thing the media tier exists for, and it now has one device saying otherwise.
- [ ] 🔴 **NOTHING BEHIND THE WEBVIEW IS VISIBLE ON THIS AVD — and that is the ONE assumption D80 rests
  on.** Four screenshots, all uniformly dark. **A MAGENTA `BoxView` in the same Grid at the same rectangle
  is invisible too**, which is the control that matters: it is ordinary MAUI drawing, so it exonerates both
  the `SurfaceView` and `adb screencap` and points squarely at the webview still painting over what is
  behind it.

  **Eliminated, each with a log line in the same run** — do not re-check these:
  - the transparency mapping runs and applies (`webview transparency: applied`);
  - the player has its display (`MediaPlayer: display attached (SurfaceHolder)`);
  - the surface is laid out and visible (`surface visible=True 320x180 at 0,0`);
  - the control really was inserted into a real Grid (`control=inserted contentIsGrid=True`);
  - the clock advances and SurfaceFlinger lists `SurfaceView[…] z=-2`.

  **THE DOCUMENT IS ELIMINATED TOO** — it reported its OWN state in the same run: `html` and `body` both
  computed `rgba(0, 0, 0, 0)` with `visibility: hidden`. And `eval` provably reaches the visible page: a
  control run set `body` magenta and the whole screen turned magenta, which also proves `screencap` captures
  webview content (it caught a playing `<video>`'s colour bars).

  ⚠ **Every earlier dark shot was UNINTERPRETABLE BY CONSTRUCTION, and this is the lesson:** the sample sets
  `Shell = #14161A` and its CSS sets `body { background: #14161a }` — the same colour, deliberately, for the
  no-white-flash chain. A dark screenshot therefore could not say WHICH layer it was showing. **Give each
  layer a distinct colour before reading a screenshot as evidence.**

  🔴 **THE ADOPTER DIFF IS DONE, AND IT POINTS AT THE HARNESS, NOT THE KIT.** Their Android build is
  **stock**: opaque page `BackgroundColor`, a plain `HybridWebView` with no background set, the default
  `Maui.SplashTheme`, no window flags — the same two properties this kit sets and nothing else. Their own
  comment records the working behaviour: dropping the body background *"shows the MAUI page's own `#0F0F14`
  through instead"*, i.e. **their webview really is see-through to the layer below**. So the approach is
  sound and the sample's exercise of it is the suspect.
  **Their page-side half, which the sample has no equivalent of:** `body { background: transparent }` AND
  `#root { visibility: hidden }`, both scoped to `[data-native-video='on'][data-native-stage='full']` —
  their note says dropping the body background alone is *"necessary and NOT sufficient"* because the
  portalled player still has the app painting underneath it in the same webview.

- [ ] **Give the sample page a REAL transparent stage** — the two rules above in its stylesheet, driven by
  an attribute the shell sets — instead of turning the document off from outside with `android eval`. That
  is the one structural difference left between the sample and a build known to work.
  ⚠ **Untested hypothesis, written down rather than committed** (an unverified speculative line was
  reverted): MAUI re-runs its own `Background` mapper on any later property change, which may repaint the
  platform webview opaque after `MobileWebViewTransparency` ran. Setting the CONTROL's `BackgroundColor` to
  transparent, so MAUI's mapper agrees rather than competes, is a one-line test of it.
  ⚠ **Do not iterate blind — this burned six deploy cycles**, and the emulator then died mid-build (the
  documented wedge; `adb kill-server`, and check `qemu` is still alive before blaming the build).
- [ ] **Re-check the safe-area probe on iOS**, and that `Shenora.iOS` compiles at all — nothing on this box
  does (`dotnet workload list` → `maui-android` alone). The sample's `Content` became a `Grid` (with
  `SafeAreaEdges.None` to restore edge-to-edge), and that is the property iOS actually reads.

### 🟡 THE PLAYBACK HEALTH FIGURES — dropped frames and stalls

`MediaPlayerStatus.Engine` now answers *which* player ran. What it cannot answer is **how well** it ran,
and the adopter's evidence is that nothing else can either: a report of *"jumping frames"* on a file that
probes completely clean, where position and duration say nothing because a player can report a perfectly
smooth clock while dropping every other frame. `AVPlayerItemAccessLog` holds it on iOS and
`DecoderCounters` on Android.

- [ ] **Decide whether the kit carries them.** Unlike `Engine` there is no free default — each shell has
  to read a platform API, and the adopter's own Android half leaves both at -1 with *"wiring that is not
  done"*. ⚠ **-1 means "no figure" and must never be 0**, which claims a clean play; they were bitten by
  `default(...)` skipping the initialisers at three call sites. ⚠ iOS cannot be verified from here.

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

### 📦 `ResourcePackJournal`: a PENDING pack older than the packaged app still gets its boot

Filed by the adopter, 2026-08-24, MEASURED on a real iPhone. `Open(packaged)` correctly prefers the packaged
client over an **active** pack — the log says so and it is the hole 0.14.0 closed. A **pending** pack is
handed back regardless of version, so a device that stages one and THEN takes an app update boots the older
staged client anyway:

```
packaged 1.0.19, pending 1.0.18
ota: serving PENDING bundle 1.0.18 — it must call CONFIRM to be kept
```

- [ ] **Compare the pending pack against the packaged version too, and drop it when it is not newer.** The
  comparison already exists for `Active`; this is the same question one branch earlier. Suggested shape: the
  result is `Packaged` and the pending entry is discarded, which is what an adopter would otherwise have to
  reimplement on top of the journal's own decision.
  ⚠ **The user-visible failure is the worst kind, which is why it is worth a release:** a fix that is
  demonstrably inside the installed app does not appear, so the app looks broken AND the fix looks wrong. It
  cost a round of "you said you fixed it" before the log was read.
  ⚠ Not urgent for us any more — worked around in `Ota/ClientBundles.Decide` (refuse a pending pack that is
  not newer, then let the journal's own rollback take it on the next launch; nothing is Reset, so the
  rollback target survives). **Verified in the field on the next deploy:**
  `ota: pending bundle 1.0.19 is not newer than the packaged client 1.0.20 — serving the packaged client`.
  The guard stays either way; the journal is still the right owner of the comparison.

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

### 📱 THE CAPACITOR-PARITY PRIMITIVES — orientation is the one left

Filed 2026-08-23 by the adopter retiring Capacitor in favour of this kit's mobile shell. Their audit found
**one app-level gap (an SMB client — theirs, not the kit's)** and three shell primitives, each a WINDOW/SHELL
concern rather than an app one. The back gesture (D79) and the foreground/resume report are built and have
both run on hardware; orientation is the only one that was ever merely an enhancement.

⚠ **Resume deliberately does NOT duplicate `document.visibilitychange`**, which already fires on both shells
— it reports the one thing a throttled, possibly frozen page cannot measure: **how long it was away**. If a
future session is tempted to add a visibility event, that is the reason not to.

- [ ] **Orientation on iOS.** Android holds it (`IWindowOrientation`, measured: the page's viewport goes
  `412×915` → `915×412` under a landscape lock); iOS REFUSES rather than half-working, because
  `requestGeometryUpdate` rotates the window while the root view controller still decides what it
  supports — so the next device rotation undoes it (D39: an API that compiles on both shells and means
  something weaker on one). **What it needs is a view-controller hook**, which means a MAUI handler
  override and a Mac to prove it on. Until then the capability is honestly absent there.

### 🟡 `MobileAppLifecycle` "raises nothing on Android" — DID NOT REPRODUCE ON EITHER EMULATOR

Filed by the adopter on 0.15.0: the service resolves, the module answers, and no event ever arrives.
Checked on an API 36 AVD backgrounded with HOME and on the emulator it was filed from; both give one
stop, one resume and two page frames carrying the duration (numbers in the commit).

⚠ **What did not happen in the original report is the BACKGROUND.** On that emulator each app gets its
own virtual display, so starting another app only takes FOCUS — and `topResumedActivity`'s first match
answers for display 0, so the control the report cites reads a different display's activity
(`mobile-harness.md`). Three runs here read as a confirmed defect on that same control.

- [ ] **Point them at `bridge.NotificationReport`** rather than asking them questions — it separates the
  remaining causes on their own machine (`Accepted=0` = the host half is not wired · `Filtered` = their own
  `NotificationFilter`, which also swallows a THROWING one · `IsOpen=false` = no handshake · `Delivered>0`
  = it is the page). Unreleased, so it reaches them with the next cut. They still have to background the
  app for real, which is the one thing no report can check for them.

### 📱 THE RECREATION CRASH IS FIXED AND AUTOMATIC — one case is still unmeasured

`MobileIpcBridgeOptions.ReleaseHandlerOnDispose` is ON by default (owner, 2026-08-23: make it app config,
not a step to remember — there is one adopter and they are on Android). 8/10 font-scale changes killed the
app before; 0/10 after, measured on API 36 with no explicit call anywhere in the sample.

- [ ] **Measure the NAVIGATION case** — a page that unloads and RELOADS the same view instance, where the
  default pulls the handler out from under a view that is coming back. Needs a two-page sample (this one
  has a single page, so `Unloaded` only ever fires for a teardown or a recreation). The escape hatch
  exists and is documented; what is missing is knowing whether anyone needs it.

### 📱 THE STAMP FIX IS BUILT — one leg of it still needs the Mac

The name lost its dot and `PackagedVersionIn` gained a `Stream` overload (`CHANGELOG.md`'s `## Unreleased`
carries both, with the measurement). The cause is pinned: `AndroidComputeResPaths` discards a hidden asset
between `AndroidAsset` and the staged assets directory, and no MSBuild property turns it off.

- [ ] **Confirm the stamp reaches an iOS app bundle**, and that `PackagedVersionIn(directory)` finds it
  there. iOS packages assets as `BundleResource` — a different path from Android's `assets/`, and nothing
  on Windows compiles the iOS TFM (`Shenora.Sample.Maui.csproj` selects it only `IsOSPlatform('osx')`).
  ⚠ **A strong prior is not a measurement**: the dot was never the only way an asset can go missing, and
  this is the same "MSBuild listed the item" trap one platform over.
