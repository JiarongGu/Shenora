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

**Status: v0.11.0 published (2026-08-17); the tree is well AHEAD of it and the next release is not cut.**
`CHANGELOG.md`'s `## Unreleased` carries the media first-load rewrite and a repo-wide comment/doc pass, and
it is **mostly BREAKING** — read `### Breaking` before touching the surface. ⚠ The version in
`src/Directory.Build.props` is still `0.11.0` and must STAY there: the release workflow owns the bump, and a
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

### 📱 THE MEDIA FIRST-LOAD REWRITE IS UNMEASURED — its correctness passed, its SPEED never ran

Built for one reported symptom — **a long initial wait playing video on an iPhone** — which was never
timed, before or after. ⚠ Correctness is covered on the simulator, so do not re-run the seek probe to
find out; what is missing is a stopwatch.

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
- [ ] **Decide whether the kit ships a ranged-HTTP seekable stream.** `MediaByteSource` made the tier
  transport-agnostic, but the kit ships no transport, so a remote source needs the app to supply a seekable
  adapter. It is generic plumbing an app should not rewrite — and it is not obviously "what .NET can do and
  React cannot" (D54). Owner call, and it decides how useful the Cues work is remotely.

### 🔧 THE BOX REFUSES ~30 % OF CLIPBOARD WRITES FROM A LOOPING TEST PROCESS

🔴 **The code is exonerated — do not "fix" `ClipboardService`.** A PowerShell `Set-Clipboard` loop sharing
none of our code fails identically, so this is an OS-level condition on this machine. The suite is held out
of the gate deliberately (`[Trait("Category", "RealClipboard")]` — `dev.mjs test clipboard`).
⚠ **Only the SPREAD means anything**: the same day ran 13-of-15 failing and 15-of-15 clean, so neither a
healthy sample nor an unhealthy one settles anything on its own.

- [ ] **Run the A/B when it next bites — the fault is not reproducing now, and that is what blocks it.**
  `dev.mjs test clipboard` several times with no Android emulator serving, then again with one running, and
  compare the SPREAD. The suspect is the emulator's clipboard bridge, PROVEN to write the Windows clipboard
  (`mobile-harness.md`); a second writer is exactly this entry's shape.
  - ⚠ **Already ruled out — do not re-check:** Cloud Clipboard and clipboard history
    (`EnableClipboardHistory`, `EnableCloudClipboard`, `CloudClipboardAutomaticUpload` all unset under
    `HKCU\Software\Microsoft\Clipboard`). `cbdhsvc_*` is implicated but is not the whole story — restarting
    it moved 13/15 → 3–6/15.
  - ⚠ **No reboot is needed for the no-emulator arm**: the long-running qemu is a zombie with no live
    emulator behind it (`adb devices` lists nothing), and a dead process cannot write the clipboard.

### 🔧 `android build` NEVER RESOLVES A JDK — so `doctor` goes green and the build then dies XA5300

Found by an adopter wiring the CLI in (Windows, no global `JAVA_HOME`). `android doctor` printed
`jdk  C:\Program Files\Android\Android Studio\jbr` and `android build` immediately failed with
`error XA5300: The Java SDK directory could not be found`. **A green check that does not predict the
thing it checks is worse than no check** — the adopter's next move is to distrust the SDK install, which
is the one thing that was fine.

The asymmetry is in `src/Shenora.Cli/src/android.ts`, and it is one line:

- `cmdDeploy` (~143) resolves `jdk`, refuses at ~154 when there is none, and passes it at ~167 —
  `run('dotnet', [...], { cwd: cfg.root, env: { JAVA_HOME: jdk } })`.
- `cmdBuild` (~248) **never calls the resolver at all**; its publish runs `{ cwd: cfg.root }` with no env.
- `doctor` (~353) resolves it a third time, and its comment says "`deploy` uses the same resolution" —
  which is exactly true and exactly why the row misleads: `build` does not.

**Fix:** give `cmdBuild` the same `jdkHome()` call + `env: { JAVA_HOME: jdk }`, and the same refusal when
none is found. Worth a test that runs a publish with `JAVA_HOME` scrubbed from the environment, since the
maintainer's box almost certainly has one set — which is why this survived.

⚠ Same shape worth checking in `ios.ts` before closing: any command whose preflight resolves a tool that
the command itself then does not pass on.

**Verified in BOTH the published build and the tree**, so it is not something the unreleased work already
fixed: npm `@shenora/cli@0.11.0` `dist/android.js` passes `env: { JAVA_HOME: jdk }` at its deploy call
(~155) and `{ cwd: cfg.root }` alone at its publish call (~262) — the same asymmetry as the source.

⚠ **Checked and NOT a bug, recorded so nobody re-files it:** on that same published build `ios doctor`
refuses on Windows outright ("iOS work needs macOS … no way around it"), which looks like the remote-Mac
feature being ignored. It is not — that string exists ONLY in 0.11.0's `dist`, not in the tree, so the
LAN-Mac path (`resolveHost`/`SHENORA_IOS_KEY`/`SshTarget`) is simply UNRELEASED. An adopter on Windows
cannot use `shenora ios *` until the next release is cut.
