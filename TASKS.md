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

**Status: v0.14.0 is PUBLISHED and VERIFIED LIVE** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli` answer 0.14.0, checked against the registries rather than the tree. Tag `v0.14.0`, release
commit `8864990`. ⚠ **A partial read is validation lag, not a half-landed release** — npm is usually
immediate and NuGet takes a minute or two; re-check, never re-push.
It carried the segment tier's foreign-muxer corpus and the data-loss fix that corpus found (a source with
no cut point kept only its last few seconds), the bundle-update answer (`ResourcePackJournal` + `shenora
copy`'s version stamp), and the bridge-tag check reading disk-served documents. **Not breaking**: 0
removals, 0 new `required` on a shipped type, 0 namespace moves.
⚠ `src/Directory.Build.props` must now stay at `0.14.0`: the release workflow owns the bump, and a
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

### 📱 THREE SHELL PRIMITIVES AN ADOPTER CANNOT LEAVE CAPACITOR WITHOUT

Filed 2026-08-23 by the adopter now retiring Capacitor in favour of this kit's mobile shell. Their audit of
what still keeps that dependency alive found **one app-level gap (an SMB client — theirs, not the kit's)** and
**three shell primitives the kit does not offer at all**, each of which Capacitor gives away in a plugin and
each of which is a WINDOW/SHELL concern rather than an app one. Measured against 0.14.0's source, not assumed:
`grep -rl "BackPressed\|BackButton" src/` finds nothing, and `MobileWindowLifecycle` answers only
`IsRecreating`.

- [ ] **Screen orientation.** Lock to portrait, and unlock/relock around a full-screen media viewer where
  rotation is genuinely wanted. Two calls, both platforms, no policy — the app decides WHEN.
- [ ] **The Android hardware back button.** 🔴 The one whose absence is not a missing feature but a BROKEN
  APP: unhandled, back finishes the activity from any screen, so a user two levels deep is dumped to the home
  screen. Capacitor's plugin exists mainly to override exactly that default. The shell cannot decide what
  back MEANS (their page closes an expanded player first, then walks its own SPA history, and only exits at
  the root) — so the ask is an EVENT the page can answer, plus a way to say "handled".
- [ ] **Foreground/resume, surfaced to the page.** After a background the websocket may be dead and the
  paired server may have come or gone; their client reconnects and re-probes on resume. `MobileWindowLifecycle`
  cannot answer this — it is about teardown, not activation.

⚠ **D15 says two consumers and this is one**, so a decline is a fair answer — but note the shape before
deciding: all three are things the kit ALREADY plays in (it owns `MobileSafeArea`, the window teardown wiring
and the IPC that would carry the event), they are per-platform in exactly the way the kit exists to absorb,
and the back button in particular is not an enhancement — **a MAUI shell without it ships an app whose back
button quits it.** If declined, the honest fallback is a line in `docs/guides/mobile.md` telling the next
adopter that these three are theirs to write, because the natural reading of "the kit is the shell" is that
they are not.

### 📱 THE STAMP FIX IS BUILT — one leg of it still needs the Mac

The name lost its dot and `PackagedVersionIn` gained a `Stream` overload (`CHANGELOG.md`'s `## Unreleased`
carries both, with the measurement). The cause is pinned: `AndroidComputeResPaths` discards a hidden asset
between `AndroidAsset` and the staged assets directory, and no MSBuild property turns it off.

- [ ] **Confirm the stamp reaches an iOS app bundle**, and that `PackagedVersionIn(directory)` finds it
  there. iOS packages assets as `BundleResource` — a different path from Android's `assets/`, and nothing
  on Windows compiles the iOS TFM (`Shenora.Sample.Maui.csproj` selects it only `IsOSPlatform('osx')`).
  ⚠ **A strong prior is not a measurement**: the dot was never the only way an asset can go missing, and
  this is the same "MSBuild listed the item" trap one platform over.
