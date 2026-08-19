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
- [ ] **The `@shenora/cli@0.0.1-seed.0` placeholder.** `npm unpublish` works until **2026-08-20 15:27
  UTC**; after that `npm deprecate` is the only tool. (`latest` already points at 0.11.0 — npm
  force-creates `latest` on a first publish whatever `--tag` says, so the stub was briefly it.)
- [ ] After the first OIDC release: require 2FA / disallow tokens on both packages so the trusted
  publisher is the only path, and drop the token-fallback sentence from `RELEASING.md`.

### 🎬 STREAMING — the media tier (D71), two questions left

> ⚠ DIRECTION (owner, 2026-08-14), and it still steers every undecided detail: the tier was built
> AHEAD of an adopter, so **bias anything undecided toward what a later adopter can change** — seams
> over baked-in policy — and write down which choices were guesses, so the first adoption report knows
> what to attack.

- [ ] ⚠ **A frame-index cache: only the COLD-cache case is still open; the CPU case is CLOSED.** Measured
  2026-08-15 (10 min / 89 MiB H.264+AAC, warm): the walk is 65 ms for 40,841 samples while a two-hour
  index would be ~51 MiB — the walk is cheap and the INDEX is expensive, the opposite of the assumption
  that filed this. **Do not build a cache for CPU.** A cold two-hour file is disk-bound and is the only
  remaining argument; it needs its own measurement first, and any cache kept must be bounded by BYTES
  and evict on memory pressure.
**ANSWERED 2026-08-20 — the cost STAYS, and `Unsliced` is correct.** The open question was whether D44
still holds on current Android: does a proper `206` + `Content-Range` get honoured, letting the shell
move to `Sliced`? Measured on Android 16 (SDK 36, WebView 133.0.6943.137) by flipping the constant and
serving a non-faststart file: **35 requests, 28 of them the identical tail range**, each answered `206`
with a correct `Content-Range` — the retry loop, unchanged. `Unsliced` serves the same clip in FOUR.
The reasoning now lives on `WebViewRangeDelivery.Unsliced` with the numbers; re-measure there if a much
later WebView is worth retesting.

### 🔧 THE BOX REFUSES ~30 % OF CLIPBOARD WRITES FROM A LOOPING TEST PROCESS

🔴 **The code is exonerated — do not "fix" `ClipboardService`.** Diagnosed 2026-08-16: a PowerShell
`Set-Clipboard` loop sharing none of our code fails identically, so this is an OS-level condition on this
machine. `cbdhsvc_*` is implicated (restarting it moved 13/15 → 3–6/15) and is not the whole story.
⚠ **Only the SPREAD means anything** — the same day ran 13-of-15 failing and 15-of-15 clean; a healthy
sample proves nothing. The suite is held out of the gate deliberately (`[Trait("Category",
"RealClipboard")]`, run it with `dev.mjs test clipboard`).

- [ ] **The residual ~30 % is unexplained.** Worth one pass when it next bites: the next suspects are
  another clipboard listener (a manager tool, RDP/VM sync) or Cloud Clipboard. ⚠ Toggling Windows
  clipboard history OFF is the untried experiment; it changes a user-facing setting, so restore it.

### 📋 THE MOBILE CLIPBOARD — `text/html` is LOST on Android

Both platforms have now run the sample's `[CLIPBOARD]` startup probe (2026-08-19, simulator + emulator).

| | text | `text/html` | an app's own type |
|---|---|---|---|
| iOS | round-trips | present | present (arbitrary UTI) |
| Android | round-trips | **DROPPED** | refused, by name |

- [ ] **Android `text/html`: NOT REPRODUCIBLE on an emulator, and five explanations are dead.** The kit
  writes a clip that declares `text/html` (instrumented: `built=[text/html]`). What the clipboard holds
  afterwards varies run to run, and nothing about the WRITE predicts it.

  | varied | prediction | result |
  |---|---|---|
  | the platform drops HTML | control fails too | control succeeded 9/10 — **dead** |
  | the kit is at fault | control always succeeds | control failed 1/10 — **dead** |
  | the writing THREAD | main thread loses it | main thread WON, background lost — **dead** |
  | the ORDER of writes | later writes win | 1st html, 2nd plain, 3rd html — **dead** |
  | racing read-after-write | a settled read is stable | 1.2 s settle: still varies — **dead** |

  - ✔ **The kit is CLEARED, and this is now evidence rather than inference.** Logging both call sites
    showed identical arguments — `label`, a 23-char text, `html='<b>SHENORA-CLIPBOARD-PROBE</b>'`,
    30 chars — into the identical `ClipData.NewHtmlText`. On one run BOTH the kit's write and the direct
    control returned `text/plain`. Same inputs, same API, different answers between runs: nothing in the
    kit's path distinguishes it from calling the platform by hand.
  - ✔ **The read is not racing either.** A second `GetAsync` five seconds later reports the same
    `(no formats)` while the TEXT is present — consistently. So the HTML is genuinely absent, not merely
    invisible yet, and the earlier read-race idea is dead too.
  - 🔴 **The prime remaining suspect is the EMULATOR'S CLIPBOARD BRIDGE**, which mirrors the guest
    clipboard to the Windows host and is the one variable I could not hold still. **The way to test it is
    to turn that sharing OFF** (Extended Controls → Settings) and repeat: stable results implicate the
    bridge and clear both the kit and Android.
  - ⚠ **Until then this says nothing about a handset**, and it is NOT a reason to change the kit — the
    write is correct and every attempt to "fix" it made things worse. One such attempt shipped briefly:
    verifying the write by reading it back refused writes that had succeeded, because the read is the
    unreliable half.
  - **The instrument is reusable**: `dev.mjs android probes` prints the control line every run.

### 🎧 BACKGROUND PLAYBACK — how long it survives is unmeasured

**ANDROID IS ANSWERED — the page cannot do it, and that is the kit's thesis rather than a gap.** A
backgrounded `<audio>` advances ~15 s and then pauses mid-clip; the process is suspended and no page code
prevents it. Now measured three times by two independent instruments: twice from inside the page (the
`audio t=` timer, ~15.3–15.6 s, recorded on the sample's own handoff block) and once from OUTSIDE via
`dumpsys audio` on 2026-08-20 — `started` at t+6 s, `stopped` by t+15 s, still stopped at t+300 s.
The page pausing itself was ELIMINATED by reading: its two `visibilitychange` handlers report and hand
off, and the only `aud.pause()` is on the handback, which fires when the app returns. So the answer is a
NATIVE anchor — `IPlaybackSession`/`IMediaPlayer` — which is what the tier exists for.

- [ ] **iOS past 43 s is still unmeasured**, and it is the half the documentation claim rests on: *"an
  `<audio>` keeps playing while backgrounded"* rests on a 16.0 s window there. Until it is measured the
  same way, do not promise page-side background audio on iOS either.
  - **The route is built**: drive the simulator from Windows (`shenora ios deploy --simulator`), and read
    the survival the way Android was read — the page's own `audio t=` gap, or `xcrun simctl spawn booted
    log` for the equivalent of `dumpsys`.

