# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred three
times — 502 lines holding six open tasks, then 570 holding three, then 458 holding seven — and each
block looked justified on the day it was written. `node devtools/dev.mjs doc-shape` now fails on a done
marker here, because the prose rule alone lost three times.

**Status: v0.10.0 is published; the tree is well ahead of it.** `CHANGELOG.md`'s `## Unreleased` is large
and **mostly BREAKING** (D64/D65/D66) — read `### Breaking` before touching the surface. **The release is
deliberately ON HOLD** (owner, 2026-08-08: *"im still holding the release since currenly stage this is
not a proper version for app to use yet"*), so correctness beats cosmetics and a half-finished surface
is a reason to keep working rather than to cut.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** The harness reaches a real iPhone
and can background it; the Island — including repaint and `staleDate` — is fully measurable on the
simulator WITH SCREENSHOTS, while a device round trip needs a human to look at the glass because there
is no `devicectl` screenshot. A session went to hardware for a question the simulator answered in 90
seconds. Read `mobile-shells.md`'s simulator loop before choosing a target.

## Open

### 🔴 ANDROID: THE BUILD IS FIXED; ONE DEPLOY REMAINS

- [ ] **Run the sample on Android once, to execute what only compiles today.** `MobileHostExtensions`
  became shared (unconditional `PlatformMediaVideoConversion.Use(pipeline, log)` + the resolved log
  sink), and the Android arms of the policy claims, the VfW codec-name fix and the seam-driven picture
  fixture have never RUN. Expected there: the picture arm picks `mpeg4` (not h263), and
  `[CODEC] convert video …` shows claim ∩ device against a codec set that differs from iOS.
  - ⚠ **Blocked on a REBOOT, and only for the deploy.** The old emulator is unkillable
    (`Stop-Process -Force` and `taskkill /F /T` both refuse while `tasklist` lists it), so a fresh AVD
    cannot take its place. Building needs nothing; installing does.

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

- [ ] 🔴🔴 **THE DEFAULT SEGMENT ENGINE PRODUCES NO VIDEO FOR ALMOST ANY REAL FILE.** A design gap, not a
  device limit. The kit's iOS video converter offers h263/mpeg4/mpeg2video and **never h264/hevc**; the
  simulator decodes h264 only, so the intersection is empty and even a real iPhone 17 Pro yields only
  `{h263}`. For essentially every real source the engine emits **sound-only segments** and honestly
  reports it has no converter.
  - **Root cause: `SegmentRunWriter` RE-ENCODES everything.** It does not have to — an h264 track can be
    COPIED into fragments verbatim, exactly as `Mp4Remuxer` already copies it. Only tracks MP4 cannot
    carry need a codec.
  - **What blocks the copy is the grid.** `SegmentGrid` refuses a grid that cannot start on a keyframe,
    and a copied track lands on the SOURCE's keyframes. `MatroskaSampleReader` already indexes every
    keyframe, so the fix is to derive boundaries from that index for a copied track and emit real
    per-segment durations (`#EXTINF` already varies). **Real work, not a tweak.**
  - ⚠ **Until this lands the tier must not be described as "working" without that qualifier.**
- [ ] **Does a platform video encoder REORDER its output?** `SegmentRunWriter` fail-closes and would
  produce short segments rather than wrong ones. Needs a device whose encoder the kit will actually
  drive — so it is blocked behind the gap above. ⚠ A codec table is per-DEVICE; the first probe chose a
  h263 fixture from the iPhone 17 Pro table and lost a round trip to it.
- [ ] 🔴 **4b — the imperative MSE glue, deliberately NOT written.** Creating a `SourceBuffer`, appending,
  and wiring `startstreaming`/`endstreaming` cannot be verified anywhere this repo runs: jsdom has no
  MediaSource and both real implementations live on devices. **Writing ~300 lines of unverifiable browser
  code and calling piece 4 done would be the overclaim this repo keeps paying for** — it should be
  written WITH the simulator run that verifies it. The pure half already ships (`segmentStream.ts`).
  - ⚠ **`endstreaming` is unverified and its absence proved nothing:** the probe appended no bytes, so
    the buffer never filled and the source had no reason to say stop. A binder mishandling the stop half
    would look identical. **4b's verification has to append real segments**, i.e. the same device run
    piece 3 owes.
- [ ] **5. Streaming cache vs PINNED artifact, and the collapse to `Direct`.** Opposite policies — the
  streaming cache may evict anything, a persisted download may evict nothing — so they share neither a
  policy nor a directory. "Complete" is the checkable predicate *every segment on the grid exists*, and
  at that point the artifact is one file and playback reverts to `Direct`.
- [ ] **Does WebView2 dispose a response body the page abandons before EOF?** On the SUCCESS path,
  nothing but the body's own at-bound self-close would. Android answers this with a real `Dispose`
  (measured); do not assume WebView2 does. ⚠ Needs a probe that abandons a `fetch` mid-body and counts
  disposals — the existing probe's four requests were all drained, which is exactly why it cannot answer.
- [ ] **Android's per-request cost is a JUDGEMENT for the owner, not a defect.** `Unsliced` delivery
  applies the range itself, so a `Range: bytes=0-65535` on a 79 MiB film reads the WHOLE output:
  82,843,185 bytes in 117,285 reads of 2 KiB, 26–31 s. iOS gets exactly the window it asked for. Nothing
  is broken — but "advisable for a two-hour film on Android?" should be decided deliberately.

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
- [ ] 🔴 **Does the playhead survive on the HOOK's path? The sample cannot tell you, by construction.**
  `BackgroundPlaybackTransfer` reads `IMediaPlayer.Status.Position`, and `useMediaPlayer` reports on
  TRANSITIONS ONLY — never `timeupdate`. The platform's pause at background time fires `pause` and so
  refreshes the position, but that report must cross IPC before the process freezes; if it does not, a
  React adopter hands the native player the position from the last transition — which can be the moment
  playback STARTED. **The sample's `index.html` also reports on `timeupdate`, so its green round trip
  proves nothing here.** Measure by dropping that one listener and re-running the transfer.
