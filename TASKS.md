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

**Status: v0.15.0 is PUBLISHED and VERIFIED LIVE** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli` answer 0.15.0, checked against the registries rather than the tree, all seven on the first
read. Tag `v0.15.0`, release commit `e87119e`. ⚠ **A partial read is validation lag, not a half-landed
release** — npm is usually immediate and NuGet takes a minute or two; re-check, never re-push.
It carried the Android **system back gesture** (D79) and the **foreground report** carrying how long the
app was away, the version stamp that could never ship on Android (a leading dot is discarded by
`AndroidComputeResPaths`), `PackagedVersionIn(Stream)` for a bundle that is not a directory, and
`WebViewFiles.ServeRange` made public. **A MINOR, not a patch**: one `### Breaking` (the stamp rename,
whose old side shipped in 0.14.0) plus four `### Added`, and 349 → 358 public types.
⚠ `src/Directory.Build.props` must now stay at `0.15.0`: the release workflow owns the bump, and a
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

### 📱 WHAT IS LEFT ON ANDROID NEEDS CODECS THE MuMu EMULATOR DOES NOT HAVE

The segment tier is answered on all three shells (`docs/design/media.md`). Both items below ran on MuMu and
came back SKIPPED for the same underlying reason — **that emulator converts almost nothing**
(`convert ac3/eac3/alac/dts: accepted=False`, `convert video h263/h264/hevc: accepted=False`), so no picture
or sound ever reaches an encoder there. A real phone, or an AVD with a fuller codec set, answers both.

- [ ] **Confirm the Android encoder change.** The bitrate was ~1/30th of intent (no frame-rate factor); the fix
  is arithmetic and changes output size and encode cost on a phone. ⚠ It also became reachable for ORDINARY
  1080p H.264, which a grid or head-ramp plan now re-encodes where it used to be copied — so this path is
  newly hot, not newly correct.
The bridge-tag check is proven BOTH ways on BOTH shells now — `ServeDocumentFromDisk` takes a file switch as
well as an env var, since `adb` cannot pass one. Android: tagged → `client READY`, untagged → the warning
with zero handshakes.

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

### 📱 THE RECREATION CRASH IS FIXED — but the kit still cannot do it FOR the adopter

`MobileWindowLifecycle.ReleaseHandler` closes it (8/10 font-scale changes killed the app; 0/10 after,
measured on API 36). It is a call the PAGE makes, because the one thing that would make it unforgettable —
doing it inside `MobileIpcBridge.Dispose` — would also disconnect the handler of a page that merely
navigated away and will reload the SAME view instance, and that case is **unmeasured**.

- [ ] **Measure the navigation case**, then decide whether the release can move into the bridge's dispose
  and stop being a step to remember. Needs a two-page sample (this one has a single page, so `Unloaded`
  only ever fires for a teardown or a recreation). If it is safe, the guide's line becomes a mechanism.

### 📱 THE STAMP FIX IS BUILT — one leg of it still needs the Mac

The name lost its dot and `PackagedVersionIn` gained a `Stream` overload (`CHANGELOG.md`'s `## Unreleased`
carries both, with the measurement). The cause is pinned: `AndroidComputeResPaths` discards a hidden asset
between `AndroidAsset` and the staged assets directory, and no MSBuild property turns it off.

- [ ] **Confirm the stamp reaches an iOS app bundle**, and that `PackagedVersionIn(directory)` finds it
  there. iOS packages assets as `BundleResource` — a different path from Android's `assets/`, and nothing
  on Windows compiles the iOS TFM (`Shenora.Sample.Maui.csproj` selects it only `IsOSPlatform('osx')`).
  ⚠ **A strong prior is not a measurement**: the dot was never the only way an asset can go missing, and
  this is the same "MSBuild listed the item" trap one platform over.
