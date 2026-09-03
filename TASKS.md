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

### 🎬 THE PICTURE SURFACE (D80): THE DESIGN IS DEVICE-CONFIRMED, THE SUBSTITUTIONS ARE NOT

The adopter runs this shape on Android and on an iPhone and reports it working, which retires the risk that
mattered: **the webview really can be made see-through, the page's hole really composites, and a
CSS-pixel rectangle really is the right contract.** Nothing below re-asks those.

🔴 **ONE substitution is left to measure, because analysing the other two closed them.** Their handlers
were read for the REASON behind each choice, not just the shape:

- **iOS: their shape is now the kit's.** A `layerClass` backing layer was written first, on the grounds
  that UIKit would then size the layer and implicit animations would not apply. **Reverted to their proven
  sublayer + `LayoutSubviews` + `CATransaction.DisableActions`** — the cleaner mechanism was unbuilt,
  unbuildable here, and failed as an `InvalidCastException` on page load if the export did not bind.
- **Android's holder callbacks are FORCED, not a deviation.** `ExoPlayer.SetVideoSurfaceView(view)` takes
  the view and registers the holder callbacks itself; `MediaPlayer.SetDisplay(holder)` takes the holder and
  registers nothing, so the caller must. Their own comment is the spec for it: *attached AFTER the player
  exists, detached BEFORE it is released*.

- [ ] **The Android ENGINE substitution — the one thing their run genuinely cannot speak to.** They drive
  ExoPlayer; the kit's default is `android.media.MediaPlayer` (D51/D42: no engine ships). Both sit on
  `MediaCodec`, so the DECODERS are the same and the difference is the EXTRACTOR set — which is the whole
  delta D80 exists for. The AVD answers it: play an `.mkv` holding H.264 through the hole, confirm the
  frame appears and follows a scroll. ⚠ The sample's page must leave a TRANSPARENT region first; with the
  page painting its own background, a working surface and a broken one look identical.
- [ ] **Re-check the safe-area probe on iOS**, and that `Shenora.iOS` compiles at all — nothing on this box
  does (`dotnet workload list` → `maui-android` alone). The sample's `Content` became a `Grid` (with
  `SafeAreaEdges.None` to restore edge-to-edge), and that is the property iOS actually reads.

### 🟡 NOTHING SAYS WHICH PLAYER RAN

`MediaPlayerStatus` has no `Engine`, and with D80's seam an app can supply its own `MediaPlayerBase` — so
"which decoder produced this reading" is unanswerable from the page. The adopter carries the field because
two rounds of their debugging went into the wrong engine while nothing said which one was running; they
carry `DroppedFrames`/`Stalls` beside it, with **-1 meaning "no figure" and never 0**, since a zero claims
a clean play.

- [ ] **Decide whether the kit answers it.** Cheap and additive on the record, but each shell has to
  populate it, and a field only Android fills is the D39 shape one level down. ⚠ iOS cannot be verified
  from here at all.

### 🟡 ONE CAPABILITY OR TWO? The adopter needed two and the kit ships one

They split it: `nativeVideoLayer` = *there is a SURFACE*, `nativeVideoPlayback` = *the shell DECODES AND
PLAYS*. Their reason, verbatim: *"For one slice that surface was a coloured box that could not play
anything, so a page branching on the layer alone would have handed it a film and rendered a rectangle."*

The kit ships `mediaSurface` meaning BOTH, now stated that way on both sides, with `MobileMediaSurface`
naming a null `Player` in the log on first use. **That is a mechanism, not a second wire string** — their
split answered a BUILD-ORDER problem (surface shipped a slice ahead of the player) that the kit does not
have, since both landed together.

- [ ] **Decide from an adopter, not from here, whether the PAGE needs the two facts apart.** It does the
  moment an app supplies its OWN player through the seam and composes the two halves separately — then
  "a hole exists" and "something decodes into it" really are independent, and only the page can fall back.
  ⚠ Do not pre-emptively add the second capability: D15's bar, and a capability string is forever.

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
