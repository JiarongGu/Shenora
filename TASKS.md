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

**Status: v0.11.0 is published (2026-08-17)** — the release the long hold was waiting for, carrying the
whole review-and-fix arc plus `@shenora/cli`'s first real publish. The tree is at the tag. `CHANGELOG.md`
has no `## Unreleased` section until the next change opens one; the 0.11.0 section is that release's
record, and it is **mostly BREAKING** (D64/D65/D66) — read `### Breaking` before touching the surface.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 📦 NPM TRUSTED PUBLISHING — owner-side UI, and it cannot be verified from here

Both packages now exist on the registry, so both Trusted Publisher settings pages are reachable. Until
they are configured, a release needs the `NPM_TOKEN` fallback.

- [ ] **Configure the trusted publisher on BOTH packages**: npmjs.com → package → Settings → Trusted
  publisher → GitHub Actions → this repo + `release.yml` (no environment) — for `@shenora/react` AND
  `@shenora/cli`. Then the release runs fully tokenless.
- [ ] **The `@shenora/cli@0.0.1-seed.0` placeholder — COSMETIC, and nothing depends on it.** The registry
  reads `latest = 0.11.0`, `seed = 0.0.1-seed.0`, so nobody installing the package can reach the stub;
  it is tidiness, not a release problem. `npm unpublish` removes it while npm's 72-hour window is open
  (it closes 2026-08-20 15:27 UTC); `npm deprecate` marks it afterwards. **The window chooses the verb,
  not the outcome**, so letting it lapse costs nothing. (npm force-creates `latest` on a first publish
  whatever `--tag` says, which is why the stub was briefly it.)
- [ ] After the first OIDC release: require 2FA / disallow tokens on both packages so the trusted
  publisher is the only path, and drop the token-fallback sentence from `RELEASING.md`.

### 🔧 THE BOX REFUSES ~30 % OF CLIPBOARD WRITES FROM A LOOPING TEST PROCESS

🔴 **The code is exonerated — do not "fix" `ClipboardService`.** Diagnosed 2026-08-16: a PowerShell
`Set-Clipboard` loop sharing none of our code fails identically, so this is an OS-level condition on this
machine. `cbdhsvc_*` is implicated (restarting it moved 13/15 → 3–6/15) and is not the whole story.
⚠ **Only the SPREAD means anything** — the same day ran 13-of-15 failing and 15-of-15 clean; a healthy
sample proves nothing. The suite is held out of the gate deliberately (`[Trait("Category",
"RealClipboard")]`, run it with `dev.mjs test clipboard`).

- [ ] **The residual ~30 % is unexplained, but the suspect list is now SHORTER and one suspect is
  PROVEN to exist.** Investigated 2026-08-20:
  - ✔ **Cloud Clipboard and clipboard history are ELIMINATED** — `EnableClipboardHistory`,
    `EnableCloudClipboard` and `CloudClipboardAutomaticUpload` are all unset under
    `HKCU\Software\Microsoft\Clipboard`, so neither was ever running. No setting was changed to learn
    this, and none needs to be.
  - 🔴 **A VM clipboard sync DOES exist on this machine and actively WRITES the Windows clipboard**: the
    Android emulator's bridge, proven by setting the host clipboard and watching the guest follow twice
    (see `mobile-harness.md`). A qemu process has been running here since **12 Aug** — spanning the
    16 Aug diagnosis — and a second-writer is exactly the shape this entry predicted.
  - ⚠ **An A/B today could not convict it, because the fault is not currently reproducing**: nine runs
    with and without the live emulator gave ONE failure, on the first run, then eight clean. That is the
    entry's own warning working as intended — a healthy sample proves nothing.
  - **So the experiment is READY rather than done.** When it next bites, run `dev.mjs test clipboard`
    several times with every qemu process stopped and again with one running, and compare the SPREAD.
    ⚠ A wedged emulator from 12 Aug is still resident and cannot be killed — it needs a reboot, and
    until then "no VM running" is not actually achievable on this box.
