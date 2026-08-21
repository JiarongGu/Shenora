# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred five
times — 502 lines holding six open tasks, then 570 holding three, then 458 holding seven, then 197
holding six, then 123 holding five. `node devtools/dev.mjs doc-shape` fails on a done MARKER here, and the
last two recurrences are the ones it could not see: **no marker at all, just finished work narrated at
length**, and then a marker written as `**✅ …**`, which its regex read straight past. ⚠ **The test is not
"is there a ✅", it is "would deleting this paragraph lose anything a future session must ACT on?"** If
the answer is no, the commit that landed it is where it lives.

**Status: v0.13.0 is PUBLISHED and VERIFIED LIVE** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli` answer 0.13.0, checked against the registries rather than the tree. Tag `v0.13.0`, release
commit `35d065a`. ⚠ They did not land together: npm was immediate, NuGet took ~1 minute and
`Shenora.iOS` a further minute — **a partial read is validation lag, not a half-landed release**.
It carried all 14 blockers from the 2026-08-21 full review — two of them critical (a `Set`/`Map`/`Date`
selector pinned for a component's life in `@shenora/react`; a `302`/`304` response killing the Android
process) — and it is **not breaking**: 0 removals, 0 new `required`, 0 namespace moves.
⚠ `src/Directory.Build.props` must now stay at `0.13.0`: the release workflow owns the bump, and a
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

### 🎬 THE SEGMENT TIER IS FIXED BUT THINLY COVERED — THE CORPUS IS WHAT IS LEFT

All 14 blockers from the 2026-08-21 review are fixed (`git log`). The tier stays labelled EXPERIMENTAL in
`README.md` until the coverage below exists, because the reason its faults clustered has NOT changed: it
had only ever been exercised against media THIS KIT produced, and every fault sat on a shape our own muxer
never emits. `SegmentRunWriterTests` now builds those shapes synthetically — a late-starting track, a
source with no cut point — which is a floor, not a corpus.

- [ ] **Cover the shapes still untested at unit level**, in `SegmentRunWriterTests`: laced audio (which
  would pin the `SpreadTies` fix, still unpinned — it mirrors `Mp4Remuxer`'s proven call site, which is an
  argument rather than evidence), and `SegmentRunWriter.cs:91`'s index-extend guard, which uses `All()` so
  the first track to run out consumes its last sample with a fallback duration.
- [ ] **Then an END-TO-END fixture from a foreign muxer** — ffmpeg is on the dev box; MP4Box, Bento4 and
  mkvmerge are not. Small files, committed. ⚠ This is the backstop the synthetic shapes cannot be: it is
  the only thing that proves the READER agrees with the writer about a real file.
- [ ] **Re-measure the forced-cut cost.** The 64 MB bound cuts on a non-keyframe when the lead track never
  reaches one, so seeking into that segment may not work. Nothing has measured how a player behaves there.

### 📱 THE MEDIA FIRST-LOAD WIN IS MEASURED ON A SIMULATOR, NEVER ON THE PHONE IT WAS REPORTED ON

Shipped in v0.12.0 and timed: **first load is FLAT across a 160× range** in duration and size — 18 ms
manifest, 55 ms init, 19 ms seg0 on a 78 MB / 1000 s file, `tries=1` throughout
(`docs/design/media.md` § "First load does not scale with the file"). ⚠ Correctness is covered too, so do
not re-run the seek probe. **What is missing is hardware**: the symptom was reported on a real iPhone, the
readings are a simulator's, and there is no BEFORE number on any machine — the case rests on the flatness.

- [ ] 🔴 **MEASURE FIRST PAINT, AND SPLIT THE TERMS** — manifest response · `init.mp4` response · seg0
  response. A total cannot say which change earned it, and the four changes are separable. ⚠ On a **real
  iPhone**: the symptom was reported on hardware, and the simulator has a different codec table and
  different storage.
- [ ] **The re-encode picture path is still unmeasured** — `REORDER: SKIPPED — this shell does not convert
  h263`. The simulator converts no video at all, so only a device reaches an encoder (`mobile-shells.md`).
- [ ] **Confirm the Android encoder change on a device.** The bitrate was ~1/30th of intent (no frame-rate
  factor); the fix is arithmetic and changes output size and encode cost on a phone. ⚠ It also became
  reachable for ORDINARY 1080p H.264, which a grid or head-ramp plan now re-encodes where it used to be
  copied — so this path is newly hot, not newly correct.

### 🍎 THE BUILD MAC CANNOT BUILD FOR A DEVICE — TWO OF ITS THREE AOT PACKS LACK THE RESOLVED VERSION

Found by the new `aot cross pack` row on its first real run, 2026-08-21. The SDK resolves the AOT cross
pack at **10.0.10** and only ONE of the Mac's three ios cross packs has it:

| pack | versions | a build for that target |
|---|---|---|
| `…Cross.ios-arm64` (device) | 10.0.11, 9.0.19 | **dies in `AOTCompile`** |
| `…Cross.iossimulator-arm64` | 10.0.11, 9.0.19 | **dies in `AOTCompile`** |
| `…Cross.iossimulator-x64` | **10.0.10**, 10.0.11, 9.0.19 | works — this is the adopter's symlink |

So the simulator loop works on that Intel Mac purely because someone patched the one pack it uses, and a
DEVICE build is still broken. This is a machine condition, not a kit defect, and the row now names it
instead of letting a build die in an MSBuild task.

- [ ] **Decide whether to repair that Mac or leave it simulator-only.** The adopter's repair is a symlink
  (`packs/<pack>/10.0.10 -> 10.0.11`), offered as evidence rather than a recommendation — the packs are
  compatible, the skew is only in the version the SDK asks for. ⚠ It blocks every device item below.
- [ ] **Decide the disk-served document limit.** The bridge-tag check reads only a `MemoryStream`, so
  `ToArray()` cannot disturb a response where seeking a `FileStream` could; it stays UNSPENT when it skips
  one. So a document served from disk is never checked at all. That is a design question, not a run.
  - ⚠ **Also unproven: the bridge-tag check FIRING on iOS.** Its quiet direction is proven there and both
    directions are proven on Android; the source is shared, so this is low risk rather than no risk.

**⚠ Unattributed, and deliberately NOT a defect claim:** `SEEK-RUN: FAIL — seg1 declares no sound
(picture=6000)` on the Android emulator. A/B'd against the same tree with the sample change stashed and it
is IDENTICAL on both arms, so it is pre-existing; `REMUX: PASS` and `REMUX-SEEK: PASS` throughout. The only
recorded `SEEK-RUN: PASS` is from the **iOS simulator**, so there is no Android baseline to compare
against, and that emulator is a consumer product rather than a standard AVD. Worth one run on a real
Android device before reading anything into it.
