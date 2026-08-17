# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred three
times — 502 lines holding six open tasks, then 570 holding three, then 458 holding seven — and each
block looked justified on the day it was written. `node devtools/dev.mjs doc-shape` now fails on a done
marker here, because the prose rule alone lost three times.

**Status: v0.11.0 is published (2026-08-17)** — the release the long hold was waiting for, carrying the
whole review-and-fix arc plus `@shenora/cli`'s first real publish. The tree is at the tag. `CHANGELOG.md`
has no `## Unreleased` section until the next change opens one; the 0.11.0 section is the record of what
that release contained, and it is **mostly BREAKING** (D64/D65/D66) — read `### Breaking` before
touching the surface.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** The harness reaches a real iPhone
and can background it; the Island — including repaint and `staleDate` — is fully measurable on the
simulator WITH SCREENSHOTS, while a device round trip needs a human to look at the glass because there
is no `devicectl` screenshot. A session went to hardware for a question the simulator answered in 90
seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 📦 NPM TRUSTED PUBLISHING BEFORE THE NEXT RELEASE — one package is ready, one name does not exist yet

Checked against the FEED 2026-08-17: `@shenora/react` is live (0.8.0…0.10.0), **`@shenora/cli` 404s —
it entered the tree after v0.10.0 and has never been published**, so the next release introduces a new
npm name. npm (unlike PyPI) has NO pending-publisher flow: the Trusted Publisher settings page exists
only for a package that already exists. The workflow is already built for both paths (`--provenance`,
`id-token: write`, npm CLI upgrade for OIDC, token fallback, first-publish guard) — what remains is
npmjs.com configuration, which is owner-side by nature:

The seed exists (`@shenora/cli@0.0.1-seed.0`, published 2026-08-17 15:27 UTC, feed-verified), so
both settings pages are reachable. ⚠ `latest` points at the stub — npm force-creates `latest` on a
first publish whatever `--tag` says and refuses to delete it; the real release repoints it, and the
stub is no-code with a "do not install" description, so the exposure is cosmetic.

- [ ] **Configure the trusted publisher on BOTH packages** (owner-side UI, cannot be verified from
  outside): npmjs.com → package → Settings → Trusted publisher → GitHub Actions → this repo +
  `release.yml` (no environment) — for `@shenora/react` AND `@shenora/cli`. Once both are set, the
  release runs fully tokenless; `dry_run` rehearses the NuGet half of the OIDC policy.
- [ ] Optional, before the real release: `npm deprecate @shenora/cli@0.0.1-seed.0 "placeholder —
  real releases start with the next Shenora release"` so an accidental install warns. The seed
  version can be `npm unpublish`ed until **2026-08-20 15:27 UTC** (72 h from ITS publish); after
  that, deprecation is the only tool.
- [ ] After the first OIDC release: tighten publishing access on both packages (require 2FA /
  disallow tokens) so the trusted publisher is the only path, and drop the fallback sentence from
  RELEASING.md once it describes a path that no longer exists.

### 🎬 STREAMING IS THE PRIMARY PATH — the media tier (D71)

> DIRECTION (owner, 2026-08-12): *"so the question is we need to have a proper streaming logic"* ·
> *"full transcode should be after if we got the full segment, its more like a cache/persist logic so
> the SegementEnegine should be the main focus"* · *"1 planner no platform difference"* · *"we also need
> to consider if the consuming app uses ffmpeg and that should be able to provide the same logic"*.
>
> 🔴 DIRECTION (owner, 2026-08-14): **build 3, then 4, then 5 — the whole tier.** Asked directly whether
> to override the adoption-driven hold, the owner chose the full build.
> ⚠ **The overridden reasoning is now the RISK to manage:** the kit is guessing what a segment engine
> must promise, with no adopter to correct it. **Bias every undecided detail toward what a later adopter
> can change** — seams over baked-in policy — and write down which choices were guesses, so the first
> adoption report knows what to attack. The falsifier is not gone, only the schedule.

The architecture and measurements are **D71**; the container/grid/mobile-only choices are **D75**. In one
line: the planner picks delivery from what the PRODUCER can promise — `Remux` states a length, so it is
a computed file served over 206s to one plain `<video src>`; `Transcode` can promise nothing, so it gets
the time grid and segments.

- [ ] ⚠ **Only the COLD-cache case is still open for a frame-index cache; the CPU case is CLOSED.**
  Measured 2026-08-15 on a generated 10 min / 89 MiB H.264+AAC source, three runs, warm cache: **walk
  65 ms** for 40,841 samples, 4 MiB allocated — 625 k samples/s. Extrapolated to a two-hour film:
  ~490 k samples, walk ~0.8 s, **index ~51 MiB**. So the walk is the cheap half and the INDEX is the
  expensive one, the opposite of the assumption that filed this, and at `MatroskaSampleReader.MaxSamples`
  one index would be ~416 MiB. **Do not build a cache for CPU.** A cold two-hour file is disk-bound and
  is the only remaining argument; it needs its own measurement before anything is built, and if one is
  ever kept it must be bounded by BYTES and evict on memory pressure.
- [ ] 🔴 **Android's per-request range cost — the owner approved a FIX, and the fix's mechanism was then
  REFUTED. It needs a new decision, not more work.** `Unsliced` delivery makes a
  `Range: bytes=0-65535` on a 79 MiB film read the whole output: 82,843,185 bytes in 117,285 reads,
  26–31 s. iOS gets exactly the window it asked for.
  - ⛔ **Two approaches are CLOSED, and the reasons are on `WebViewRangeDelivery.Unsliced` — read them
    before proposing either again.** Making the body seekable cannot work from this side (the platform's
    binding never calls `Seek`); serving cheap filler for the discarded prefix stakes correctness on the
    delivery model never changing, and fails silently at the wrong offset if it does.
  - **What is actually left is ONE question for the owner, and it needs the blocked device run:** is
    D44 still true on current Android/Chromium — does a proper `206` with `Content-Range` now get
    honoured, so the shell could move to `Sliced`? That is the only path that removes the cost rather
    than hiding it. Re-measure the way it was measured (bytes + read count + wall clock).

### 🔧 A LOOP OF CLIPBOARD WRITES FROM A TEST PROCESS FAILS PART OF THE TIME ON THIS BOX

🔴 **SAY WHAT WAS MEASURED, WHICH IS NARROW: a test process writing to the clipboard REPEATEDLY has a
fraction of those writes refused.** Nothing else. Not this repo's code (see the table), and no longer a
gate.

⚠ **This heading has been wrong TWICE, and the second time the owner caught it again — *"how can you
claim a machine clipboard is not working if I am using it to copy and paste???"*** It read "THE DEV
MACHINE'S CLIPBOARD FAILS INTERMITTENTLY", which claims something enormous next to the evidence. The
body was scoped after the first push-back and the HEADING was not, so the overclaim survived where
everyone reads it. **A heading is a claim; scope it like one.**

⚠ **"Rapid" was wrong too, and the table below says so** — 12/12 failed at 2-SECOND spacing, so write
rate is not the distinguishing factor. What the failing case has that ordinary use does not is a LOOP
of writes from a test process; which of those properties matters is exactly what the residual below
does not know.

⚠ **INTERACTIVE COPY/PASTE HAS NEVER FAILED HERE — and note the honest form of that:** it was never
OBSERVED failing and was never deliberately TESTED, so the owner's daily use is the evidence, not a
measurement of ours. Nothing here says Ctrl+C is unreliable on this machine.

⚠ **"Intermittently" is load-bearing.** The failure comes and goes on its own: measured across one day it
ran 13-of-15 failing at its worst, 3–6-of-15 after a service restart, and **15-of-15 CLEAN** a few hours
later with nothing changed. So a healthy sample proves nothing here, and neither does a bad one — only
the spread does.

**DIAGNOSED 2026-08-16 with a throwaway probe. The code is exonerated; do not "fix" `ClipboardService`.**
`Clipboard.SetDataObject` fails with *"Requested Clipboard operation did not succeed"* — the write never
lands, so nothing the test asserts is ever reached. Four A/B experiments, each 12–40 rounds:

| varied | result |
|---|---|
| **payload** — text-only vs text+files+HTML+PNG+private | 26/30 vs 25/30 fail — *identical*, so the multi-format `DataObject` is not involved |
| **rate** — 120 / 400 / 1000 / 2000 ms between writes | 12/12 fail even at 2 s, so it is not a listener race on rapid updates |
| **apartment** — the shared pumped STA vs a fresh STA thread per call | 13/15 vs 14/15 fail — *identical*, so `StaThread`'s design is not involved |
| **runtime** — PowerShell `Set-Clipboard`, outside this repo entirely | **4/12 fail with the same error** |

🔴 **The last row is the one that settles it:** a process sharing none of our code fails the same way, so
this is an OS-level condition affecting every application on the box. `cbdhsvc_*` (Clipboard User
Service — clipboard history) is Running, and one captured failure showed `explorer` holding the
clipboard open; the rest showed our own `CLIPBRDWNDCLASS` owning it with nobody holding it open.

⚠ **Two hypotheses were tested and REJECTED**, recorded so nobody re-runs them: pumping the apartment
after the flush (`Application.DoEvents()`) made it *worse* — 34/40 fail vs 29/40 — and "the shared STA
has no message pump" is simply false, `StaThread.SharedApartment` calls `Application.Run()`.

**CAUSATION IS PARTLY CONFIRMED, and the gate is dealt with.** Restarting `cbdhsvc_*` moved the failure
rate from **13/15 to 5/15**, then it settled at **3–6/15 across three further rounds** rather than
creeping back. So the clipboard history service is implicated but is not the whole story: even in its
good state this machine refuses roughly a third of clipboard writes.

**The gate no longer depends on it** — `ClipboardServiceTests` carries `[Trait("Category",
"RealClipboard")]`, `dev.mjs test` filters it out and PRINTS that it did, and `dev.mjs test clipboard`
runs it deliberately. Verified in both directions: 1,644 → 1,642 in the gate, exactly 2 in the verb.
`Assert.Skip` was never an option (xunit is pinned at 2.9.3; runtime skipping arrived in v3), and making
the failure silently pass was ruled out — a green test that never touched the code is the vacuous-pass
shape this repo keeps paying for.

**2026-08-17: 10 consecutive runs of `dev.mjs test clipboard`, 10 CLEAN.** Another point on the spread,
which is the only thing that means anything here — and the reason there was nothing to debug when the
owner asked for a fix that day. ⚠ It does NOT argue for putting the suite back in the gate: the same
machine ran 13-of-15 failing, and a gate that is green on the good days is worse than no gate.

- [ ] **The residual ~30 % is still unexplained.** Worth one more pass when
  it next bites: `cbdhsvc` is implicated but not sufficient, so the next suspects are another clipboard
  listener (a manager tool, RDP/VM clipboard sync) or Cloud Clipboard. ⚠ Toggling Windows clipboard
  history OFF is the untried experiment; it changes a user-facing setting, so restore it afterwards.
- [ ] ⚠ **A held-out suite can rot.** Run `dev.mjs test clipboard` after touching `ClipboardService` —
  the gate will not do it for you, which is the price of this split and the reason the exclusion
  announces itself on every run.

### 📋 THE MOBILE CLIPBOARD IS WRITTEN BUT NEVER RUN ON A DEVICE

The contract and the Windows shell are proven (`ClipboardServiceTests` round-trips text + HTML + PNG +
files against the real clipboard). The mobile halves compile for both TFMs and nothing more.

- [ ] **Run the pasteboard paths on a device/simulator, both directions.** iOS goes through
  `UIPasteboard.Items` with UTIs (`public.utf8-plain-text`, `public.png`, `public.html`); Android
  through `ClipData.NewHtmlText`. **What a compile cannot tell us:** whether `board.Items = [item]`
  with several UTIs is really read back as one item, whether `HtmlText` survives a round trip through
  another app, and whether an unrecognised UTI string is accepted as a pasteboard type at all.
  ⚠ **Paste into a FOREIGN app** (Notes, Gmail) — the only test that says the formats are the ones the
  platform's own apps look for. A self round-trip would pass even if the kit invented a private UTI
  nothing else reads, which is precisely the silent-success shape this repo keeps paying for.

### 🎧 BACKGROUND PLAYBACK — the two windows nobody has measured

The feature is DONE and it is the kit's (`BackgroundPlaybackTransfer`, consumed by the MAUI sample via
`Window.Stopped`/`Resumed`). What stays open is only what the API deliberately does not promise.

- [ ] **How long does it actually survive? Nobody knows past ~45 s.** Android carried 45 s hidden with no
  foreground service, iOS 43 s — but the staged clip IS 60 s, so **minutes are unmeasured on both**, and
  an emulator/simulator is gentler than a handset (Android's freezer/Doze arrives later than any run so
  far). A foreground service is the app's to post, which is the split `IPlaybackSession` documents. **A
  documentation claim to earn, not a defect to fix.**
  - ⚠ It leaves a documented iOS claim in doubt: *"an `<audio>` keeps playing while backgrounded"* rests
    on a **16.0 s** window, and Android's equivalent dies at ~15.4 s. Too close to ignore before
    promising page-side background audio anywhere.
