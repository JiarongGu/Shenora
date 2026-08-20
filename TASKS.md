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

### 🔧 CLIPBOARD WRITES ARE REFUSED WHILE AN ANDROID EMULATOR RUNS — A/B RUN, CAUSE ESTABLISHED

🔴 **The code is exonerated and so is the machine: the variable is a RUNNING EMULATOR.** The A/B this
entry asked for was run by an adopter on 2026-08-21, on the box that reported it, with a live MuMu
instance — their earlier attempt had only a zombie qemu, which is exactly why it never reproduced:

| arm | emulator | failures |
|---|---|---|
| A | shut down | **0 of 45** |
| B | **running** | **59 of 60** |
| A′ | shut down again | **1 of 45** |

Off → on → off. Deterministic rather than a spread, so the ~30 % in the old title was sampling an emulator
that came and went. The instrument was this entry's own control — a 15-iteration PowerShell
`Set-Clipboard` loop sharing none of the kit's code — with a read-back added.

⚠ **The mechanism is NOT a second writer, which this entry assumed.** Measured at the instant of failure:

- `Set-Clipboard` **throws** `Requested Clipboard operation did not succeed`. It is a REFUSED write, not a
  value overwritten afterwards — a write-then-read-back probe records **zero** mismatches, only throws.
- `GetOpenClipboardWindow()` is **null** at that moment, so nothing is holding the clipboard open.
- The clipboard **owner** after each failed call is the CALLING process itself — so the call got as far as
  taking ownership and then failed.

That is the shape of a clipboard-format **listener** misbehaving on the update notification — which is
precisely what a host↔guest clipboard bridge registers — rather than a competing `SetClipboardData`.

- [ ] **Decide whether to state this as an operating condition rather than chase it further.** The suite is
  already held out of the gate (`[Trait("Category", "RealClipboard")]`), the code is now exonerated twice
  over, and the trigger is known and avoidable — so the remaining question is one line in
  `mobile-harness.md` ("shut the emulator down before `dev.mjs test clipboard`"), not an investigation.
  - ⚠ **Already ruled out — do not re-check:** Cloud Clipboard and clipboard history
    (`EnableClipboardHistory`, `EnableCloudClipboard`, `CloudClipboardAutomaticUpload` all unset under
    `HKCU\Software\Microsoft\Clipboard`). `cbdhsvc_*` is implicated but was never the whole story —
    restarting it moved 13/15 → 3–6/15, which this now explains: restarting it never removed the emulator.
