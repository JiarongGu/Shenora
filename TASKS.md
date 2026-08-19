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
- [ ] 🔴 **Android's per-request range cost needs a DECISION, not more work.** `Unsliced` delivery makes a
  `Range: bytes=0-65535` on a 79 MiB film read the whole output (82,843,185 bytes, 117,285 reads,
  26–31 s); iOS gets exactly the window it asked for.
  - ⛔ **Two approaches are CLOSED and the reasons are on `WebViewRangeDelivery.Unsliced` — read them
    before proposing either again.** A seekable body cannot work from this side (the platform's binding
    never calls `Seek`); cheap filler for the discarded prefix stakes correctness on the delivery model
    never changing, and fails silently at the wrong offset when it does.
  - **What is left is one question, and it needs the blocked device run:** is D44 still true on current
    Android/Chromium — is a proper `206` + `Content-Range` honoured now, so the shell could move to
    `Sliced`? That is the only path that removes the cost rather than hiding it. Re-measure the way it
    was measured (bytes + read count + wall clock).

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

- [ ] 🔴 **Android ACCEPTS `text/html` and then loses it, which is worse than refusing it.** The custom
  type is refused explicitly and that is honest; HTML passes `UnsupportedFormats`, is written with
  `ClipData.NewHtmlText`, and comes back with no formats at all. Silent loss on a surface whose whole
  job is carrying representations.
  - **The cause is NOT isolated.** Both halves read correctly: the write uses `NewHtmlText`, the read
    takes `GetItemAt(0).HtmlText`. Candidates, none tested — Android gates clipboard READS on the app
    having focus (a startup probe may not); the emulator's own clipboard listener was active in logcat
    during the run and may have replaced the clip; or `HtmlText` needs the clip DESCRIPTION to declare
    the HTML mime type on this API level.
  - **Cheapest next step**: have the probe log `PrimaryClip.Description` mime types straight after its
    own write. That separates "we never wrote HTML" from "something replaced it" from "we cannot read it
    back", which is three different fixes.
  - ⚠ Then decide: carry it, or refuse it like the custom type. **Accepting and dropping is the one
    option that is not defensible** — an adopter cannot tell it happened.

### 🎧 BACKGROUND PLAYBACK — how long it survives is unmeasured

- [ ] **Nobody knows past ~45 s** (Android 45 s, iOS 43 s, against a 60 s clip, on an emulator and a
  simulator — both gentler than a handset, where Android's freezer/Doze arrives sooner). Minutes are
  unmeasured on both. **A documentation claim to earn, not a defect to fix** — a foreground service is
  the app's to post, which is the split `IPlaybackSession` documents.
  - ⚠ It leaves a documented iOS claim in doubt: *"an `<audio>` keeps playing while backgrounded"* rests
    on a **16.0 s** window, and Android's equivalent dies at ~15.4 s. Too close to ignore before
    promising page-side background audio anywhere.

### 📱 THE REMOTE MAC PATH — what is left after the loop closed

`shenora ios --host` and `shenora inspect` are built (`docs/design/cli-remote.md`) and the whole loop was
driven against a real LAN Mac on **2026-08-19**, ending with the sample signed, installed and launched on
an iPhone from a Windows box. Nine defects came out of that; the CHANGELOG has them. What follows is what
that run did NOT settle.

- [ ] 🔴 **NOTHING ON WINDOWS COMPILES THE SAMPLE'S `#if IOS` ARM, and it was broken for FOUR DAYS.**
  Dated from git rather than guessed: `Use()` began taking an `ILogger?` on **2026-08-14** (`71684e6`),
  breaking both arms at once; the ANDROID arm was fixed on **08-18** (`764bdb7`) and the iOS arm three
  lines above it was not, because `dev.mjs android` compiles one and nothing compiles the other. It stayed
  broken across the 0.11.0 release.
  - ⚠ **Bounded, and worth stating so the entry is not read as worse than it is**: `Shenora.iOS` itself
    compiled throughout, and `samples/` ships in no package — so no adopter received this. What was broken
    is the thing an adopter COPIES, which is why it still matters.
  - **The mechanism now exists**: `shenora ios build --host` compiles that arm on the Mac. Make it a
    habit before a release, or wire it into a Mac-side check — it is the only thing that can see this
    class of rot, and the kit's own sample is the first thing an adopter copies.


- [ ] **`ios push` leaves a git checkout's METADATA describing a tree that is no longer there.** The files
  are current; `git log` still names the old commit and `git status` shows everything as modified. Proven
  on the real Mac: after a push its HEAD read `a30d994` while the tree held today's files. Documented on
  `pushTree`, but a `--dir` that defaults to a NON-checkout scratch path may be the better answer —
  decide it the next time someone is actually using the loop, not now.

- [ ] **`inspect` has `eval` but not `fetch` or `navigate` as first-class actions.** `eval` expresses both,
  so this is ergonomics, not capability — file it only if the raw form turns out to be what people
  actually type.

⚠ **A codec probe was deliberately left out of the inspector page.** Yaorin's version carried one and it paid
for itself immediately — run against headless Edge with `--disable-gpu` it reported `HEVC: ""` where the
same engine in a real WebView2 window answers `probably`, which is a real lesson: **a codec matrix is
CONTEXT-dependent and headless is not a proxy for the shipped surface.** But the list it probed encoded
that app's format decisions, and the kit is not a media library (D53). If it returns it should be a
DECLARED probe list the page is given, not a list the kit picks.
