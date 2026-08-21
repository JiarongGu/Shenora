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

**Status: v0.12.0 is PUBLISHED and verified live** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli`, checked against the registries rather than the tree. It carried the media first-load
rewrite, the remote byte-range source (D78) and a repo-wide comment/doc pass, and it was **mostly
BREAKING** — `CHANGELOG.md`'s `## 0.12.0` is the migration record. ⚠ `src/Directory.Build.props` must now
stay at `0.12.0`: the release workflow owns the bump, and a hand-bump moves the baseline and skips a
release (`release-discipline.md`).

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 🎬 THE SEGMENT TIER NEEDS A FIXTURE CORPUS — TWO DEFECTS ARE OPEN AND UNFIXABLE WITHOUT ONE

The 2026-08-21 full review found 14 blocking defects; 13 are fixed (`git log`). These two are not, and
they are held open deliberately rather than patched blind: **nothing in the suite constructs a
`SegmentRunWriter` at all**, so a change to muxer sequencing or buffering would be unverifiable, and a
wrong guess produces a silently corrupt stream. The tier is labelled EXPERIMENTAL in `README.md` until
they close.

🔴 **The root cause is one thing, and it is why patches will not settle it:** the tier had only ever been
tested against media THIS KIT produced. Every fault found sits on a shape our own muxer never emits.
The already-fixed `esds` defect is the proof — our `Mp4Builder` always writes the expanded 4-byte
descriptor length, so the broken scan never matched our own files and a fallback supplied the right
answer for months.

- [ ] 🔴 **Build the corpus first: MP4Box, Bento4, mkvmerge and Apple output**, covering laced audio, a
  track that starts late, and short-form descriptor encodings. Small files, committed as fixtures.
- [ ] **`SegmentRunWriter.cs:444` — a track absent from the FIRST fragment is dropped for the whole run.**
  The init segment is written beside the first fragment and declares only the tracks that had produced by
  then; a copied track produces from frame one while an encoder may hold a whole segment. So the late
  track is undeclared, its samples are dropped for ever, and `VerifyPicture` only checks for picture —
  nothing downstream notices. **Silent film, entire length.** The likely fix is declaring the EXPECTED
  track set rather than the observed one, which is a sequencing change.
- [ ] **`SegmentRunWriter.cs:372` — `Pending` grows to whole-source size when the lead channel never
  cuts**, then is doubled by the final flush. ~150–200 MB, i.e. OOM on a phone. `StalledWithoutPicture`
  explicitly cannot stop it. A bound is easy; choosing one that does not corrupt the segmentation is not.
  - ⚠ **`SegmentRunWriter.cs:91` rides along** — the index-extend guard uses `All()`, so the first track
    to run out consumes its last sample with a fallback duration. Same file, same corpus.
- [ ] ⚠ **`SegmentRunWriter`'s SpreadTies fix (`d59ec84`) is UNPINNED.** It mirrors `Mp4Remuxer`'s proven
  call site exactly, which is the argument for it — not evidence. The corpus is what would pin it.

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
