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

### 🎬 THE SEGMENT TIER IS COVERED ON DESKTOP — WHAT IS LEFT IS THE OTHER TWO SHELLS

The foreign-muxer corpus exists (`RealSourceShapeTests` + three committed fixtures) and it found what it was
built to find: a source with no cut point lost 92 % of its picture, silently. Fixed — the memory guard
SPILLS rather than republishing the segment it is already filling. Both shapes are now proven twice over:
`dev.mjs media-decode` (ffmpeg) and `dev.mjs media-mse`, which appends them into WebView2's real
`MediaSource` and reads back the buffered range.

⚠ **Do not re-run the ORDINARY shape on the iOS simulator** — it was measured there on the spill fix itself
(`appendedSegments=12/12`, `buffered=0.22-60.02`, on a real `ManagedMediaSource`), which is the regression
that mattered, since `Flush`/`Publish` sit in the writer every shell shares. What is below is what that run
did NOT answer.

- [ ] 🔴 **The MULTI-FRAGMENT shape is still desktop-only, and getting it onto a device needs a DECISION
  first.** Desktop appends it from artifacts the suite produced (`spill=0.167-20.167`); a device cannot, and
  the three ways in are all unattractive: ship a DERIVED artifact as a sample resource (goes stale silently
  when the writer changes), include a gitignored directory conditionally at build time (the sample then
  behaves differently depending on what is on disk), or make `MaxPendingBytes` reachable (product surface
  bought for a test — the thing the desktop probe's own remarks argue against). ⚠ Worth deciding before
  writing any of it.
- [ ] **Android has had none of this.** `dev.mjs android devices` lists nothing — the MuMu instance is not
  running, so even the ordinary re-check needs the emulator started.
- [ ] **Decide whether the EXPERIMENTAL label comes off** (`README.md`). ⚠ An owner call, not a mechanical
  one: desktop coverage is real but the tier's faults were found on mobile, so "proven" may reasonably mean
  the row above is green first.

**All three lacing schemes are now covered by real files** — EBML and Xiph by `clip-laced-audio.mkv`, fixed
by `clip-fixed-lacing.mkv` (CBR MP3, which is what mkvmerge fixed-laces). ⚠ A laced PICTURE track stays
uncovered and is **not reachable with the tools here**: it is legal, and mkvmerge laces no video. Hand-built
blocks are the only coverage it will get short of writing a muxer.

### 📱 THE MEDIA FIRST-LOAD WIN IS MEASURED ON A PHONE NOW — WHAT IS LEFT IS THE BIG FILE AND ANDROID

**Measured on an iPhone 17 Pro / iOS 26.6, 2026-08-22 — INCLUDING the 78 MB file, so the flatness claim is
hardware evidence now** (`docs/design/media.md` § "On a real iPhone"). 160× the duration and size costs
22 ms against 13 ms to plan, with `init.mp4` and seg0 unchanged at 3–4 ms; `120/120` appended,
`buffered=0.09-861.44`, `tries=1` on every one. **The phone beat the simulator 10× on `tInit`**, the term
that dominated there — that reading was measuring virtualised storage more than the kit.
⚠ There is still **no BEFORE number** on any machine, and hardware does not change that.
⚠ `tFirstFrame` is not a first-paint figure — the probe appends every segment before waiting for a frame,
so it scales with segment count (81 ms at 12 segments, 954 ms at 120).

⚠ **The re-encode picture path RAN, and the encoder does not reorder** — `REORDER: ran a re-encode over
clip-h263-aac.mkv — 6 segment(s), 179659 picture bytes`, with `picture (converted) read=60 emitted=60`.
The simulator could only report `SKIPPED — this shell does not convert h263`; the device decodes h263 and
encodes h264, so this is the first time that path has executed at all. Do not re-run it on a simulator.

- [ ] **Confirm the Android encoder change on a device.** The bitrate was ~1/30th of intent (no frame-rate
  factor); the fix is arithmetic and changes output size and encode cost on a phone. ⚠ It also became
  reachable for ORDINARY 1080p H.264, which a grid or head-ramp plan now re-encodes where it used to be
  copied — so this path is newly hot, not newly correct.

### 🍎 THE BUILD MAC BUILDS FOR A DEVICE — PROVEN BY AN APP RUNNING ON THE PHONE

**Settled 2026-08-22 by doing it**, which retires the 2026-08-21 entry claiming the Mac could not: the
sample built, AOT-compiled, signed, installed and launched on `Feedfinger-iPhone` (iPhone 17 Pro, iOS 26.6).
The AOT cross-pack gap that entry described was repaired on the Mac at 2026-08-21 20:40
(`…Cross.ios-arm64/10.0.10` → symlink to 10.0.11), hours after it was written.

**What it took, and both are re-payable rather than one-offs:**

- `mac provision` — the Mac had a profile for one unrelated app only. It mints one per bundle id, the Live
  Activity extension included. ⚠ **A personal-team profile expires after 7 DAYS**; these expire
  **2026-08-29**, so a device run after that re-runs it first.
- 🔴 **`mac device` PUSHES by default, and the push wipes the `TargetPlatformVersion` pin** — so the first
  attempt failed 27× `MT4162 … not available in iOS 26.2 (introduced in 26.4)`, which reads as a code
  defect and is not one. **Re-pin, then `mac device --no-push`.** Unpinned, the SDK takes a band newer than
  Xcode 26.3's iOS SDK 26.2 and the LINKER rejects its own generated bindings.
⚠ **The bridge-tag check is proven BOTH ways on iOS now** (simulator, `PageProbe.ServeDocumentFromDisk`), so
do not re-run it there: a tagged document served from a `FileStream` still reaches the page
(`client READY (handshake id=c1)`, i.e. the check read the body and put its position back), and an untagged
one makes it warn with no handshake at all. **Android has had neither arm** — the probe is shared source, so
that is low risk rather than none.

**⚠ Unattributed, and deliberately NOT a defect claim:** `SEEK-RUN: FAIL — seg1 declares no sound
(picture=6000)` on the Android emulator. A/B'd against the same tree with the sample change stashed and it
is IDENTICAL on both arms, so it is pre-existing; `REMUX: PASS` and `REMUX-SEEK: PASS` throughout. The only
recorded `SEEK-RUN: PASS` is from the **iOS simulator**, so there is no Android baseline to compare
against, and that emulator is a consumer product rather than a standard AVD. Worth one run on a real
Android device before reading anything into it.

### 🔄 A RUNTIME-FETCHED WEB BUNDLE OUTRANKS THE PACKAGED ONE FOR EVER — THE KIT WARNS ABOUT TWO NEIGHBOURING TRAPS AND NOT THIS ONE

Filed from an adopter, 2026-08-22, who found it by asking the right question: how is a STORE update
supposed to reach a device that has already updated its web client at runtime? It is not, in their
implementation, and the shape of the mistake is general enough to be worth a decision here.

The kit already owns the substrate — `Core/WebView/WebViewInterception`, `WebViewFiles`,
`WebViewResourcePipeline` — and since **0.13.0 it WARNS about two of the traps in this exact slice**: an
interceptor attached after the first navigation, and a served document with no bridge tag. Both were filed
from the same adopter. **This is the third of that family, and unlike those two it is silent AND permanent.**

**The shape.** A shell serving a web client can serve either the bundle PACKAGED in the app or one fetched
at runtime into app data. A boot decision picks: pending → active-on-disk → packaged. If the
active-on-disk arm is taken **without comparing versions**, then once a device has fetched any bundle, the
client shipped inside every later app build is never served again — a store release cannot reach the UI.

**Why it looks fine in testing.** It self-heals while the app and its server ship together: an updated
server advertises something newer, the fetch replaces the stale bundle, and all anyone notices is a
redundant download plus a window where a new native binary drives an old page. It dead-ends as soon as the
two can diverge, which is the normal case for a SELF-HOSTED server — app 2.0 from a store, a 1.5 server, an
active 1.9 bundle, and "is 1.5 newer than 1.9?" is no, for ever, with a 2.0 native shell under a 1.9 page.
A renamed or dropped capability is what breaks in that gap.

⚠ **The enabling cause is upstream of the missing comparison, and it is the part a kit-side answer has to
address.** The adopter had nothing comparable to compare: the packaged client's version was baked from one
source at build time while the app manifest's version was an unrelated constant, so the shell could not
know what version its own packaged client was. **Adding an `if` is not the fix — making the packaged
bundle's identity available to the decision is.**

- [ ] **Decide whether the bundle store + boot decision is a harvest (D15).** ⚠ ONE consumer so far, so the
  two-consumer bar is not met — this is offered as evidence, not a request. What argues for it anyway is
  that the state machine has exactly one correct ordering and **every mis-ordering fails silently**:
  increment-and-persist the attempt count BEFORE serving a pending bundle (written the other way, a failure
  that runs none of your code leaves the count at zero and the app retries the same broken bundle for
  ever); serve a pending bundle exactly once; promote only on a page-side confirm — **which travels over the
  bridge, so a bundle missing the injected tag can never confirm and is discarded for ever, i.e. trap #2 and
  this one compound**; roll back and delete otherwise; and prefer the packaged bundle when it is newer,
  deleting the superseded one rather than leaving it to be re-chosen.
- [ ] **If the harvest is declined, name the hazard where the other two are already named** — the
  interceptor's remarks and the adoption docs. An adopter who reads the bridge-tag warning is precisely the
  one who is about to hit this, and the fix is a sentence: compare the fetched bundle against the packaged
  one and prefer the newer. Cheap, no API surface, and it stops the silent case being discovered by a store
  release.

**What should stay with the app either way** (so the boundary is not the open question): the version SCHEME
and its comparator — semver, a build counter, whatever — since a kit dictating one is a product decision;
where the packaged version comes from; and the download source with its authentication.

⚠ **A reload cannot verify any of this.** The bundle is chosen once per process, so the unit of test is a
force-stop and relaunch — which is also why an adopter can carry the defect for months without seeing it.
