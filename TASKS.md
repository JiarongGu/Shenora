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

### 🔄 THE BUNDLE-UPDATE ANSWER IS BUILT BUT HAS NEVER RUN IN AN APP

`ResourcePackJournal` + `shenora copy`'s version stamp answer the adopter's report; `docs/guides/mobile.md`
carries the three-trap masking order. Unit tests cover the ordering with a fresh journal per simulated start.

- [ ] **Run it in a real shell, once.** ⚠ A reload cannot test any of it — the pack is chosen once per
  process, so the unit of test is a force-stop and relaunch, which is also why an adopter can carry the
  defect for months without seeing it.
- [ ] **Ask the adopter which trap actually broke them.** The masking order is INFERENCE from a timeline
  (0.13.0 shipped the attach warning 2026-08-21; this was filed 2026-08-22), not their account. Their logs
  carrying `attached after its webview was already realized`, and a break dating from their attach fix rather
  than a store release, would confirm it — otherwise it is the version comparison alone and
  `docs/guides/mobile.md` is telling the next adopter a sequence that did not happen.
