# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave**, and this has recurred six times.
`doc-shape` now fails on a done MARKER and warns past 120 lines — but the recurrences it could not see had
**no marker at all, just finished work narrated at length**. ⚠ **So the test is not "is there a ✅", it is
"would deleting this paragraph lose anything a future session must ACT on?"** If not, the commit that
landed it is where it lives.

**Status: v0.16.0 is PUBLISHED and VERIFIED LIVE** (tag `v0.16.0`, release commit `f61d410`).
⚠ `src/Directory.Build.props` must stay at `0.16.0` — the workflow owns the bump, and a hand-bump moves
the baseline and skips a release. **Cutting the next one? Read `.claude/knowledge/release-discipline.md`
first**: it carries the by-hand `<Description>` read, the prose-audit-before-the-cut rule, and why a
partial registry read afterwards is lag rather than a half-landed release.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 📦 NO WAY TO SHIP AN IPA TO A DEVICE THE BUILD MAC CANNOT SEE — and the obvious OTA workaround is a wall

`ios deploy --device` requires the phone attached to the build Mac. With the developer AWAY (phone in
hand, Mac at home behind a VPN) there is no supported path at all — and the first thing an adopter
reaches for does not work, which is the part worth the kit's ink.

**Measured by an adopter, 2026-09-11.** `ios provision <bundle>` already mints a fresh 7-day profile
remotely over ssh, so the signing half is solved; what is missing is everything after it.

- 🔴 **IP reachability is not deployability.** The Mac pinged the handset (0% loss) while every debug
  port was closed — `62078`, `58783`, `62087`, `49152` — and `devicectl` stayed `transportType: None`
  with `pairingState: paired`. iOS does not expose lockdownd on a network interface until one USB
  session arms "Connect via network". A SERVICE-layer block that reads exactly like a network fault.
- 🔴 **`itms-services` OTA is a wall for a free personal-team DEVELOPMENT build.** With a schema-valid
  manifest (`software-package` + `display-image` + `full-size-image`), bundle id and version matching
  the app, a trusted TLS chain and the payload serving `200`, `com.apple.appstored` fetched the manifest
  **six times and never once requested the .ipa**. Policy, not a manifest defect — and the generic
  *"Unable to install"* names nothing, so it invites an evening of manifest tuning that cannot work.
- **The route that carried it:** re-sign the built `.app` with the fresh profile (nested frameworks
  inside-out, entitlements extracted from the profile, `codesign --verify --deep --strict` clean) →
  `Payload/<app>.app` → zip → `.ipa` → `pymobiledevice3 apps install` over USB **from a Windows host**.
  No Mac in the room, no Apple ID, no third-party signing service.

**Asks:** (1) **`shenora ios package`** — emit a signed installable `.ipa` from the device build rather
than only installing to an attached device; the framework signing ORDER is the trap, since getting it
wrong fails at launch, not at build. (2) Document the non-Mac install path (usbmuxd +
`pymobiledevice3`/`ideviceinstaller`) in `docs/guides/mobile.md`, **including the OTA dead end**.

⚠ The re-sign needs a **GUI session** on the Mac (codesign cannot use a login keychain over ssh), so this
belongs beside the existing provisioning stub, not in the CLI's portable half.
⚠ D15: one consumer — but the weekly free-tier expiry is what makes it bite. A profile that dies while
the developer is travelling currently means the app is gone until they are home.

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

**Why this is urgent rather than nice-to-have:** the adopter took 0.16.0's orientation, DELETED their own
working `LockPortrait`/`UnlockOrientation`, and now guard everything on
`MobileWindowOrientation.IsSupported` — which was `false` on iOS. Their `Info.plist` permits portrait and
both landscapes, so **their iPhone build lost its portrait lock and nobody has looked.** The fix is built
(`SupportedInterfaceOrientations` + the app-delegate override); only the run is left.

- [ ] **Prove it on an iPhone.** Three things, and the second is the one no reasoning settles: a locked app
  does not rotate; **the lock SURVIVES a device rotation** (the failure the old `requestGeometryUpdate`
  path had, and the reason the delegate mask exists); and `Unlock` hands the decision back rather than
  pinning the current edge. ⚠ Check `IsSupported` is TRUE in the handshake — it goes true only once UIKit
  has asked the delegate, and a false reading there means the capability is advertised absent for that
  whole session. ⚠ Nothing in this repo runs an iOS device, and the sample's iOS head does not compile on
  Windows, so its one-line override is unverified even though the library half is not.

⚠ **Resume deliberately does NOT duplicate `document.visibilitychange`**, which already fires on both shells
— it reports the one thing a throttled, possibly frozen page cannot measure: **how long it was away**. If a
future session is tempted to add a visibility event, that is the reason not to.

### 📱 THE RECREATION CRASH IS FIXED AND AUTOMATIC — one case is still unmeasured

`MobileIpcBridgeOptions.ReleaseHandlerOnDispose` is ON by default and the crash is fixed (8/10 → 0/10 on
API 36). What `HandlerReleaseProbe` has NOT covered is navigation.

- [ ] **Measure the real NAVIGATION case.** ⚠ **Do NOT re-parent the view inside its layout** — that
  shortcut is what this probe tried and it throws `MauiContext should have been set on parent`, which is an
  artefact of re-parenting a handler-less view and says nothing about navigation. MAUI unloads and reloads a
  PAGE while the view keeps its parent. **The faithful mechanic is swapping `Window.Page` away and back**,
  which fires `Unloaded`/`Loaded` on the same page instance. ⚠ It needs a run-once guard: the sample's whole
  probe suite re-runs from `OnLoaded`, so the swap-back would recurse into this probe.
  ⚠ **No adopter needs this yet** — the only one has a single `ContentPage` and no `PushAsync` at all, which
  is the ordinary hybrid shape. It stays filed because the default is ON and the failure is silent.

