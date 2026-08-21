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

### 🍎 THE ADOPTER FIXES ARE PROVEN ON ANDROID — iOS IS THE HALF NO MACHINE HERE CAN REACH

Three findings from an adopter on 2026-08-21 are fixed and landed: the interceptor's two diagnostics, the
two `ios doctor` rows, and the sample that the first of those caught. Both interceptor warnings are
sabotage-verified in both directions on an Android emulator, and the pack row's MSBuild assumption is
confirmed on this box — `git log` carries the evidence. What is left needs a Mac.

- [ ] 🔴 **Prove the two `ios doctor` rows on a Mac, including where they must stay QUIET.** The decision
  halves (`describeBindings`, `describeAotCrossPack`) have 11 tests; the PROBERS (`msbuildProperty`,
  `aotCrossPack`) have none and need `xcrun`, a real iOS `packs/` tree, and the `…Cross.ios*` pack name —
  which is inferred from this box's `…Cross.android-arm64` rather than seen. **A row that reports
  `MISSING` on a healthy Mac is worse than the silence it replaced**, so run it on one that BUILDS first.
  - The adopter's symlink (`packs/<pack>/10.0.10 -> 10.0.11`) is how to reproduce the failing side.
- [ ] **Run both interceptor warnings on iOS.** They compile for `net10.0-ios` here — `verify` builds it
  on this Windows box, so a compile break is caught — but the iOS arm has never executed, and
  `RangeDelivery` is the one place the two shells deliberately differ.
- [ ] **Decide the disk-served document limit.** The bridge-tag check reads only a `MemoryStream`, so
  `ToArray()` cannot disturb a response where seeking a `FileStream` could; it stays UNSPENT when it skips
  one. So a document served from disk is never checked at all. That is a design question, not a run.

**⚠ Unattributed, and deliberately NOT a defect claim:** `SEEK-RUN: FAIL — seg1 declares no sound
(picture=6000)` on the Android emulator. A/B'd against the same tree with the sample change stashed and it
is IDENTICAL on both arms, so it is pre-existing; `REMUX: PASS` and `REMUX-SEEK: PASS` throughout. The only
recorded `SEEK-RUN: PASS` is from the **iOS simulator**, so there is no Android baseline to compare
against, and that emulator is a consumer product rather than a standard AVD. Worth one run on a real
Android device before reading anything into it.
