# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred four
times — 502 lines holding six open tasks, then 570 holding three, then 458 holding seven, then 197
holding six. `node devtools/dev.mjs doc-shape` fails on a done MARKER here, and the fourth recurrence is
the one it could not see: **no marker anywhere, just finished work narrated at length** — a 70-line
diagnosis kept for two open lines under it. ⚠ **The test is not "is there a ✅", it is "would deleting
this paragraph lose anything a future session must ACT on?"** If the answer is no, the commit that
landed it is where it lives.

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

**A remote source HAS now crossed a real network — measured by an adopter, 2026-08-21.** Driven against
that adopter's own running server over a LAN hop (not loopback, and not synthetic), through
`MediaByteSource.ForRanges` on 0.12.0, against a real 882,044-byte track served with real
`206`/`Content-Range`:

| arm | result |
|---|---|
| whole file over the LAN | bytes match — **4 fetches**, 11 ms in fetch, 18 ms total |
| seek to the midpoint, then read 64 KiB | bytes match — **1 fetch** (no re-read from zero) |
| body DIES mid-read (8 KiB in, connection aborted) | **throws `IOException`** rather than truncating |
| every fetch answers a QUARTER of the ask, cleanly | bytes match — 173 fetches |

The 4-request plan holds over a real network — the same number the loopback run produced. A seek costs one
fetch rather than a re-read. The documented *"returning FEWER is legal — it is asked again for the rest"*
is true in practice at a 4× short-answer rate, with no truncation. And a dying body **throws**, which is
the outcome worth having: a caller can retry, where a silent short read cannot be noticed — worth keeping
as a stated guarantee, since a future change that "helpfully" swallowed it would be invisible.

🔴 **The open list narrows from five to two by CONSTRUCTION, not by measurement.** Of *TLS, a proxy, a
redirect, latency, a connection that dies mid-body* — the first three **cannot reach the kit at all**. The
fetch delegate is caller-side: it receives `(offset, count, token)` and returns a `Stream`, so the
transport, the redirect policy and the credentials live entirely on the adopter's side of the seam and the
kit never observes them. That is the seam doing its job. Only latency and mid-body death were ever
testable here, and both now are.

### 🍎🧩 THREE ADOPTER FINDINGS ARE FIXED IN THE TREE AND NONE HAS RUN WHERE IT MATTERS

All three were reported by an adopter on 2026-08-21 and all three are now written, typechecked and
unit-tested — but **every one of them executes only on hardware this repo cannot reach**, so what is left
is a run, not a design. The narrative that produced them is in the commit; what a future session must ACT
on is below.

**What landed.** `ios doctor` gained an `aot cross pack` row and its `ios bindings` row now reads the
project's effective `TargetPlatformVersion` (so a correctly-pinned csproj finally reports `ok`) and names
`-p:ValidateXcodeVersion=false` whenever the band in force is not the Xcode's own band — two constraints,
stated separately, because no choice of band can satisfy the pack's EXACT-Xcode assertion.
`MobileWebViewInterceptor` gained two one-shot warnings: attached-after-the-webview-was-realized, and a
document served with no `hybridwebview.js` in it. `docs/guides/mobile.md` carries the attach rule, the
runtime-served-document half of the bridge-tag warning, and the `transferSize` trap.

**✅ The pack row's assumption is CONFIRMED, and it was confirmable here** — the SDK mechanics are not
Mac-specific. Measured on the Windows dev box, 2026-08-21: `-getProperty:<name>` returns a bare value at
exit 0 (so the parsing is right), `BundledNETCoreAppPackageVersion` evaluates to **`10.0.10`** — exactly
the version the adopter's `AOTCompile` failure reported the iOS SDK resolving the cross pack at — and the
layout is `packs/<pack>/<version>/tools/mono-aot-cross`, beside `llc` and `opt`, which is the path the row
tests. **What remains Mac-only is the NAMING**: this box carries `…AOT.win-x64.Cross.android-arm64`, so
the `…Cross.ios*` pattern is inferred from the android ones rather than seen.

- [ ] 🔴 **Prove the two `ios doctor` rows on a Mac — including the case where they must stay QUIET.** The
  pure halves (`describeBindings`, `describeAotCrossPack`) have 11 tests; the PROBERS (`msbuildProperty`,
  `aotCrossPack`) have none, and after the confirmation above what is left for them is the Mac-only half:
  `xcrun`, the `…Cross.ios*` pack name, and a real iOS `packs/` tree. A row that reports `MISSING` on a
  healthy Mac is worse than the silence it replaced, so run it on one that BUILDS before trusting it.
  - The adopter's symlink (`packs/<pack>/10.0.10 -> 10.0.11`) is how to reproduce the failing side.
**✅ THE ATTACH WARNING IS PROVEN ON ANDROID, BOTH DIRECTIONS** (MuMu emulator, x86_64, SDK 32,
2026-08-21). Late attach FIRES, naming the first request it saw; the constructor attach is SILENT. It ran
twice on the firing side and named a different first request each time (`_framework/hybridwebview.js`, then
`shenora/transport.js`), which is the diagnostic reading the world rather than matching a fixed string.
**Its first catch was this repo's own sample** — fixed in `6c75d3f`, since the sample is what an adopter
copies.

**✅ And the bridge-tag check's QUIET direction is proven** as a side effect: the fragment repair serves the
packaged `index.html` as a `MemoryStream`, which is exactly the path the check runs on, and it stayed
silent because that document HAS the tag.

- [ ] **Prove the bridge-tag check FIRES.** Only its quiet side has run. It needs a document served from
  the pipeline WITHOUT `hybridwebview.js` in it — `PageProbe.SabotageMainDocument` already exists for
  something close to this and is currently commented out at `MainPage.cs`.
  - ⚠ **A document served from disk is never checked at all** — the check reads only a `MemoryStream`,
    deliberately, and stays UNSPENT when it skips one. Confirm that is the intended limit.
- [ ] **Run both on iOS.** Everything above is Android. They compile for `net10.0-ios` here — `verify`
  builds it on this Windows box, so a compile break is caught — but the iOS arm has never executed, and
  `RangeDelivery` is the one place the two shells deliberately differ.

**⚠ Noticed while doing the above, NOT a regression and NOT yet a defect:** `SEEK-RUN: FAIL — seg1 declares
no sound (picture=6000)` on that Android emulator. A/B'd with the sample change stashed and it is
IDENTICAL on both arms, so it is pre-existing rather than caused by the attach move; `REMUX: PASS` and
`REMUX-SEEK: PASS` throughout. The only recorded `SEEK-RUN: PASS` is from the **iOS simulator**, so there is
no Android baseline to compare against — this may be an emulator codec quirk (MuMu is a consumer emulator,
not a standard AVD) rather than the kit. Worth one run on a real Android device before reading anything
into it.
