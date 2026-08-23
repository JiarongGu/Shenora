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

### 📱 THE CAPACITOR-PARITY PRIMITIVES — two of three built, both owed a device run

Filed 2026-08-23 by the adopter retiring Capacitor in favour of this kit's mobile shell. Their audit found
**one app-level gap (an SMB client — theirs, not the kit's)** and three shell primitives, each a WINDOW/SHELL
concern rather than an app one.

**BUILT: the back gesture (D79) and the foreground/resume report.** Orientation is the only one left, and it
is the only one that was ever merely an enhancement.
⚠ **Resume deliberately does NOT duplicate `document.visibilitychange`**, which already fires on both shells
— it reports the one thing a throttled, possibly frozen page cannot measure: **how long it was away**. If a
future session is tempted to add a visibility event, that is the reason not to.

- [ ] **Run the back gesture on a device.** The ordering has tests with no device; the PLATFORM leg is
  compile-proven only. The sample page now intercepts and answers (a budget of 2 HANDLED presses, then it
  declines), which is what makes the run mean anything — without a page that intercepts, `Intercepting`
  stays false and only the fast path is reachable. **What to check, in order:** two presses stay in the app
  and log `BACK: press … handled=true`; the third leaves it. Then a configuration change (rotate, or change
  font scale) and repeat — that exercises the self-driven re-attach onto the new activity.
  ⚠ **NOT the re-issue loop** — that was reasoned through and is not a risk: `OnBackPressedDispatcher`
  picks the first ENABLED callback, and ours is disabled while it re-issues. **The unmeasured claim is the
  predictive-back one**: the callback is now enabled only while a page intercepts, specifically so an app
  that never intercepts keeps Android 16's predictive back gesture. Worth confirming on the glass.
- [ ] **Run the foreground/resume report on a device too.** Same gap as the back gesture: the arithmetic
  has tests, the MAUI `Window.Stopped`/`Resumed` wiring has none. ⚠ **What to actually check is the PAIRING**
  — an Android background that stops and resumes once should produce ONE duration, and the process-scoped
  `Window` makes a doubled subscription the easy mistake.
- [ ] **Screen orientation.** Lock to portrait, and unlock/relock around a full-screen media viewer where
  rotation is genuinely wanted. Two calls, both platforms, no policy — the app decides WHEN. **The last of
  the three, and the only one that is purely an enhancement.**

### 🔴 `ServeRange` IS INTERNAL, SO THE ONE-IMPLEMENTATION PROMISE ENDS AT A FILE PATH

Filed 2026-08-23 by the same adopter, from writing an SMB reader for their mobile shell.
`WebViewFiles.Serve(request, PATH, …)` is public; `ServeRange(request, totalLength, contentType, delivery,
read)` — the same answer over any producer of bytes — is `internal`. Its own doc is the argument for
exporting it: *"🔴 `WebViewRangeDelivery` has exactly ONE implementation. D44 is a measured platform fact
whose failure mode is silent."*

**A body that is not a file is not exotic** — it is SMB, an object store, a decrypting stream, a database
blob. Every one of those adopters gets `WebViewByteRange.TryParse` and the response builders (all public,
which is why this is survivable) and then has to re-derive the last step themselves:

```csharp
var unsliced = delivery is WebViewRangeDelivery.Unsliced;
var sent = unsliced ? new WebViewByteRange(range.From, total - 1) : range;
var body = unsliced ? read(0, total) : read(range.From, range.Length);
```

Three lines, and **the failure mode of getting them wrong is the worst shape there is**: every faststart
file plays perfectly and every file whose index sits at the end fails. That is precisely the trap D44 exists
to absorb, so leaving it outside the boundary means the kit absorbs it for the file case and hands it back
for every other one.

- [ ] **Make it public, or expose the same thing under a name you prefer** (`WebViewFiles.ServeStream`,
  a `WebViewRangeResponse.For(...)`). The signature already takes a `Func<long, long, Stream?>`, so nothing
  needs to change but the modifier and a doc line saying when to reach for it.
  ⚠ Not urgent for the adopter — they copied the three lines and pointed a comment at this entry — but it
  is a small change that deletes a copy nobody should own.

### 📱 THE STAMP FIX IS BUILT — one leg of it still needs the Mac

The name lost its dot and `PackagedVersionIn` gained a `Stream` overload (`CHANGELOG.md`'s `## Unreleased`
carries both, with the measurement). The cause is pinned: `AndroidComputeResPaths` discards a hidden asset
between `AndroidAsset` and the staged assets directory, and no MSBuild property turns it off.

- [ ] **Confirm the stamp reaches an iOS app bundle**, and that `PackagedVersionIn(directory)` finds it
  there. iOS packages assets as `BundleResource` — a different path from Android's `assets/`, and nothing
  on Windows compiles the iOS TFM (`Shenora.Sample.Maui.csproj` selects it only `IsOSPlatform('osx')`).
  ⚠ **A strong prior is not a measurement**: the dot was never the only way an asset can go missing, and
  this is the same "MSBuild listed the item" trap one platform over.
