# The mobile shells — what the platforms were MEASURED to do

**Maintainer-facing, and every figure here cost a device or simulator run.** Read it when you are about
to change the mobile shell, the media delivery it serves, or the device harness — the numbers are what
the design rests on, and re-earning them costs hardware time.

⚠ **These are FINDINGS, not rules.** The invariants a session must not break stayed in
[`.claude/knowledge/mobile-shells.md`](../../.claude/knowledge/mobile-shells.md), which loads on demand
when a task matches; a measurement that lived beside them read as a law and was applied as one (D77).
⚠ **A measurement is true of the DEVICE and DATE in its heading.** A codec table is per-device — one
probe chose a fixture from an iPhone 17 Pro table and lost a round trip to it — so treat a figure as
evidence to re-check, never as a promise the platform makes.

## Deploying to a REAL iPhone — four traps, each found by running it rather than writing it

Harvested 2026-08-07 from the closed task record before it was deleted. Under **D56** this is product
knowledge, not devtool trivia: the deploy loop is part of what the framework sells.

- **⚠ Xcode 16 MOVED where provisioning profiles live.** Classic
  `~/Library/MobileDevice/Provisioning Profiles/`, current
  `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`. On Xcode 26.3 minting writes to the NEW
  path and never creates the old directory at all — so a tool that reads only the classic location reports
  **"zero provisioning profiles on disk"**, a true statement about the wrong directory that reads as
  *provisioning has never worked here*. `dev.mjs mac profiles` reads BOTH; anything new must too.
- 🔴 **`xcodebuild` exits 0 without necessarily minting what you asked for** — it succeeds against a
  profile it already had. So `mac provision` verifies by reading profiles OFF DISK and matching their
  `application-identifier`, and that check is what caught the trap above: the run printed "minted ok" twice
  and then correctly refused to claim success. **Trusting exit status would have reported success and left
  device builds failing for a reason nothing pointed at.**
- 🔴 **A target whose output depends on an MSBuild PROPERTY cannot be made incremental on files.** The
  Live Activity extension's `Info.plist` sat in a target with Inputs = the Swift files, Outputs = the
  executable — but its content depends on `$(ApplicationId)`, which cannot be an Input, because MSBuild
  compares FILES not property values. A skipped target left a stale bundle id and the device rejected the
  install with `AppexBundleIDNotPrefixed`, naming an id the project does not contain.
  ⚠ **Worse for an adopter than for the kit:** rename your app or reuse an `obj/` and the build succeeds
  and the **simulator is happy** (it does not enforce the appex prefix) — only a real device rejects it.
  Second bug of this exact shape in that one target (the first: one architecture's `.a` satisfying
  another's check). **An incremental check must cover everything the target WRITES.**
- 🔴 **`xcrun … | tail` makes the pipeline report TAIL's status — always 0.** `mac device` announced
  "running on the device" after both the install and the launch had failed. `mac.mjs` already documented
  this trap on its build step and it was reintroduced directly beneath that comment the same day. Name it
  as a pattern: **a harness that pipes a tool through `tail`/`head` to keep output readable silently
  converts every failure into a success.** `set -o pipefail`, or check the tool's status before piping.

- 🔴 **`xcrun devicectl device console` DOES NOT EXIST, and the tool that called it said nothing at all.**
  `devicectl device` offers copy, info, install, notification, orientation, process, reboot, sysdiagnose
  and uninstall — no `console`. The invocation was `… console … 2>/dev/null | head -N`, so the error text
  went to `/dev/null` AND the exit status became `head`'s, which is always 0: no output, no failure, and a
  fallback message that could never fire. **This is the SAME `| head`/`| tail` trap recorded above for
  `mac device`, in a sibling function, surviving the write-up of the first one** — which is the reason it
  is worth a second bullet rather than a footnote. Fix the pattern everywhere, not the call site.
  **Reading an app's log off a device is `xcrun devicectl device process launch --console
  --terminate-existing`**, which relaunches with stdout attached. That is right rather than merely
  available: this repo's probes run at STARTUP, so anything attaching to a running app misses what it came
  for. ⚠ Exit 141 (SIGPIPE) is the SUCCESS path once `head` closes the stream — treat it as such, or the
  fix reintroduces the bug in the opposite direction.
- 🔴 **BOUND THE CONSOLE BY TIME, NOT BY LINE COUNT** (2026-08-09). `--console` piped into `head -N` only
  flushes once N lines exist, so asking for more lines than the app logs HANGS with an empty file — which
  reads as "the device is not talking". Bound it instead:
  `nohup xcrun devicectl device process launch --console --terminate-existing --device $D <bundle>
  > /tmp/x.txt 2>&1 & sleep 45; pkill -f devicectl`. 🔴 **That form is also what makes BACKGROUND testing
  possible**, because the stream survives the app leaving the foreground — background it by launching
  another app (`… process launch --device $D com.apple.mobilesafari`).
- **NETWORK PAIRING DOES WORK — a whole build → sign → install → launch → `--console` cycle ran over
  `localNetwork` with no cable** (iPhone 17 Pro). Over Wi-Fi the phone can vanish mid-operation (`peer is
  no longer reachable`, `NWError 60`) and then read `unavailable`, so the honest rule is **reach for USB
  when a long operation keeps dropping** — not *LAN cannot do this*. ⚠ Stating it as the stronger rule
  cost a round: a usable phone was written off as unreachable.
  - 🔴 **AND THE TOOL AGREED WITH THE WRONG RULE.** `devicectl list devices --json-output` carries
    **`connectionProperties.pairingState`** (`paired` — can I deploy?) and **`transportType`**
    (`localNetwork`/`wired`) beside **`tunnelState`**, which is a debug channel opened ON DEMAND and
    therefore reads `disconnected` for any idle-but-perfectly-usable device. `shenora ios devices` printed
    the tunnel. **Read `pairingState`; `tunnelState` answers a question nobody asked.**
  - `system_profiler SPUSBDataType` showing no iPhone means "not on USB" — which is now a statement about
    the transport, not about whether you can deploy.
- 🔴 **Anything that SIGNS must run in the Mac's GUI login session** — codesign cannot reach the login
  keychain over ssh. The failure is actively misleading: appex signing reports missing provisioning
  profiles while `mac profiles` shows them present and valid. Use `dev.mjs mac gui <cmd>`, which
  base64-encodes the command so quoting survives ssh + a login shell + AppleScript.

## Measured platform facts — STREAMING DELIVERY (2026-08-12, Android WebView 133 + iPhone 16 Pro sim)

🔴 **Every one of these contradicted an assumption, and three of them fail SILENTLY.** Read before designing
any delivery path; the architecture they produced is D71.

| | Android | iOS |
|---|---|---|
| native HLS (`index.m3u8` + `.ts`) | **NO** — `ready=4 size=0x0 dur=0`, **no error** | untested |
| `canPlayType('application/vnd.apple.mpegurl')` | `"maybe"` — **A LIE** | `"maybe"` |
| `MediaSource` | yes | **undefined** |
| `ManagedMediaSource` | undefined | **yes** (iOS 17.1+) |
| 200 with no `Content-Length` | **NO** — `err=4`, with or without `Accept-Ranges` | — |
| 206 + real total, throttled body | **YES** — `dur=60`, `seekable=[0–60]` at `buffered=[0–8.3]` | **YES** |

- 🔴 **EVERYTHING THAT FAILED, FAILED FOR WANT OF A SIZE — not for want of streaming.** MAUI's Android
  intercept path always emits `Content-Length: 0` and cannot be told otherwise (the measurement is in
  `MobileWebViewInterceptor.PlatformHeaders`), so a media element learns the total from `Content-Range` on a
  **206** and from nowhere else. Give it one and a body may arrive as slowly as you like — the whole
  timeline is seekable while almost nothing is buffered.
- ⚠ **A `fetch` CONTROL is what separated transport from decode.** The failing 200 delivered all 474,744
  bytes while the element refused it: without the control this reads as "streaming does not work" rather
  than "the header is wrong". Same lesson as the `/media` 404s — `err=4` is not a codec verdict.
- 🔴 **iOS READS A CONTAINER IN HUNDREDS OF TINY RANGES** (4, 8, 24, 512 bytes) before streaming forward,
  where Android issues one large request. **Per-request cost dominates on iOS**, so a delivery path
  validated only on Android can look fine and be unusable there — AVFoundation is the pickiest consumer the
  kit has (D44), and this is a second instance of that rule.
- 🔴 **THE SEGMENT TIER RUNS END TO END ON iOS, AND THE DEVICE FOUND A BUG NO TEST COULD.** Measured
  2026-08-14 (iPhone 16 Pro simulator, iOS 26, `SegmentRouteProbe` + `clip-ac3.mkv`):
  ```
  manifest=200 version=7 map=init.mp4 segments=4
  initBytes=620 seg0Bytes=41969 typeSupported=true sourceopen=true
  appendInit=ok appendSeg0=ok buffered=0.00-6.01 startstreaming=1
  ```
  - ✅ **A real `ManagedMediaSource` ACCEPTS the kit's fragments**, and `OutputConfig` is populated early
    enough for the init segment written beside the first fragment (620 bytes, and the appends prove it
    carries a usable configuration).
  - 🔴 **THE BUG, and it is the reason to assert `buffered` rather than `appendBuffer` resolving.** The
    first run reported `appendInit=ok appendSeg0=ok buffered=0.00-0.00` for 234 KB of AAC — accepted,
    no error, `updateend` on both, and worth NOTHING. Cause: `IosMediaAudioConversion` returns every frame
    as `new MediaFrame(bytes, 0)`, because an audio encoder does not time its output — a packet is a fixed
    number of samples, so the timeline is arithmetic. `SegmentRunWriter` derived durations from
    presentation-time GAPS, so every packet got 1 µs and 575 packets spanned half a millisecond.
    **`Mp4Remuxer` already drew that line and says so in its own comment; the fragment writer was a second
    implementation of the same calculation and disagreed.** Fixed by giving each channel its own timescale.
  - ⚠ **An append that succeeds proves less than it looks.** Only the buffered RANGE distinguishes
    "the platform accepted these bytes" from "these bytes describe any playable time at all".
- ⚠ **THE SIMULATOR CONVERTS NEITHER h263 NOR mpeg4, unlike the iPhone 17 Pro** — its own
  `CONVERT-PICTURE` probe says so in the same run, and the first segment probe wasted a round trip on a
  h263 fixture chosen from the DEVICE table. A codec table is per-device; carrying one from a phone to a
  simulator is the same mistake as the reverse, which this file already records once. Consequence: **the
  segment tier's PICTURE path is still unmeasured**, and with it the question of whether a platform video
  encoder reorders its output (`SegmentRunWriter` fail-closes and would produce short segments).
- 🔴 **And the iPhone 17 Pro has NO MPEG-4 Part 2 DECODER, with a valid ESDS in hand.** `TryStart` logged
  `no DECODER for Mpeg4Video at 480x270 (codecPrivate 47B)` — 47 bytes of ESDS present and VideoToolbox
  still refused, on the same device that decodes `h263`. ⚠ **So "the device converts h263" does not
  generalise to "the device converts legacy video"**, and a codec table needs a row per codec rather than a
  verdict per family. This is a DECODER absence, which no capability query the kit can make will report:
  the refusal arrives when the session is created.
- 🔴 **iOS HAS NO `window.MediaSource` AT ALL — only `ManagedMediaSource` — and its `startstreaming` DOES
  fire. Measured 2026-08-14** (iPhone 16 Pro simulator, iOS 26, `MSE-PROBE` in the MAUI sample's page):
  ```
  MediaSource=false  ManagedMediaSource=true
  isTypeSupported('video/mp4; codecs="avc1.640028,mp4a.40.2"') = true
  @2s and @6s: sourceopen=true startstreaming=1 endstreaming=0 readyState=open streaming=true
  ```
  - **So a binder that only knows `window.MediaSource` does nothing on iOS** — not "degrades", nothing. The
    feature detection has to name both, which is what `pickMediaSource` in `@shenora/react` does.
  - **`startstreaming` arrives within 2 s of attaching**, so a fetch loop gated on it (D71 piece 4's
    `nextSegment`) really does start. That gate was written against Apple's documentation with nothing
    confirming it; it is measured now.
  - ⚠ **`endstreaming` was NOT observed, and that is expected rather than reassuring:** the probe appended
    no bytes, so the buffer never filled and the source had no reason to say stop. **The other half of the
    gate is still unverified** — a binder that never honours `endstreaming` correctly would look identical
    in this measurement.
  - ⚠ **Attachment is `srcObject` + `disableRemotePlayback`, not `createObjectURL`.** Both are load-bearing
    per Apple's guidance; the probe used them, so this result says nothing about the URL form.
  - ⚠ **The `isTypeSupported` `true` above is subject to the very next bullet** — it is the same query that
    lied about MPEG-TS. Only an actual append proves the kit's fragments are acceptable.
- ⚠ **`MediaSource.isTypeSupported('video/mp2t')` answered `true` on BOTH shells. Do not believe it** until
  something appends a real TS segment: `canPlayType` produced exactly such a `true` for HLS on the same
  device on the same day, and an MSE append failure is silent.
- **The instruments:** Android takes `dev.mjs android eval` directly. **iOS has no `safari-eval` on this
  build Mac**, so a page-side probe must log through the app's own mirror — and `dev.mjs mac log` shows only
  a recent window, so a result can scroll out before you read it. `mac shot` is the cheap cross-check: the
  sample renders its own log panel, and a playing `<video>` is visible in the screenshot.

## Measured platform facts — A COMPUTED REMUX ON A DEVICE (2026-08-12)

The first time `UseComputedRemux` — an MP4 answered over ranges that **has never been produced** — met a real
webview. Fixture `clip-h264-aac.mkv` (60 s, 468 KB Matroska) planning to a 488,377-byte MP4, 3,185 samples,
served by `RemuxRouteProbe` in the MAUI sample.

⚠ **Two words, used precisely in this whole section.** "**Shell**" means the platform's webview and its host
as they BEHAVE at runtime (Chromium's WebView on Android, WKWebView on iOS) — never the `Shenora.Android` /
`Shenora.iOS` packages, which is the other thing this repo calls a shell; every crash and disposal fact below
belongs to the webview, and no kit code can intercept it. "**iOS**" means the **iPhone 16 Pro SIMULATOR on iOS
26** unless a line says otherwise: the repo's status line claims proof on a real iPhone, and that claim covers
earlier work, not the 2026-08-13 measurements here. AVFoundation's request pattern is the one thing most worth
re-checking on hardware.

✅ **RE-RUN ON BOTH SHELLS ON 2026-08-13, against the `503` and past the deleted ceiling.** What changed under
this table: the metadata walk moved into a mission, so the first request for an unplanned source is now
`503 Retry-After: 1` and a CLIENT must retry (`RemuxRouteProbe` polls at the same one-second interval
`ConversionRouteProbe.CheckAsync` does); and the 64 MiB output ceiling was deleted, so a big film reaches this
path at all.

⚠ **WHAT THE RE-RUN COVERS, ROW BY ROW — because it does not cover all of them.** The **plays**, **duration +
seekable** and **cold seek** rows reproduced within noise (iOS `advanced=1.40` vs `1.37` here, seek `t=49.45`
vs `49.43`; Android identical). The **disposes the response body** row is unchanged and was re-confirmed
independently (see the abandonment measurement below). 🔴 **The `range requests, whole probe` row is
SUPERSEDED, not re-verified:** the probe grew a first-request arm, a 404 control, a retry control fetch and a
big-film pass, so one Android run now reaches `#14` for reasons that have nothing to do with the shells. Those
two numbers (4 / 508) describe the 2026-08-12 probe and are kept for the RATIO they establish — Android asks a
handful of large ranges, iOS hundreds of tiny ones — not as a current count.

| | Android (WebView 133, API 36) | iOS (iPhone 16 Pro sim, iOS 26) |
|---|---|---|
| plays from a computed layout | **YES** — `size=480x270 dur=60.023 ready=4 err=- advanced=1.40` | **YES** — `size=480x270 dur=60.023 ready=4 err=- advanced=1.37` |
| duration + seekable window | `seekable=60.02` with `buffered=60.02` | `seekable=60.02` with `buffered=60.02` |
| **cold seek to 80 %** | **YES** — `target=48.02 landed=48.02 t=49.42 advanced=1.40 paused=false` | **YES** — `target=48.02 landed=48.02 t=49.43 advanced=1.42 paused=false` |
| range requests, ONE clip, 2026-08-12 probe (see the note above — a ratio, not a current count) | **4** | **508**, every one `206` |
| disposes the response body | **YES** | **NO** |

🔴 **RE-VERIFIED on Android 16 / SDK 36 / WebView 133.0.6943.137: choosing `Sliced` there still breaks it,
and the failure is a LOOP rather than an error.** On a non-faststart file `Sliced` produced **35 requests, 28
of them the identical tail range** (`bytes=393216-`, each answered `206` with a correct `Content-Range`)
where `Unsliced` serves the same clip in **four**. The platform applies the range start to whatever body it
is given, so slicing applies the offset twice, the player never receives the bytes it asked for, and it asks
again. ⚠ This is a *separate, later* measurement from the 4-vs-508 row above — that one is a
`Sliced`-vs-`Unsliced` platform comparison, this one is the cost of getting D44's choice wrong on Android.

- ✅ **The design's central claim holds on BOTH shells: a seek into a region nothing has produced is
  serviceable cold.** Android asked for `bytes=393216-` and got `206 … bytes 393216-488376/488377`. iOS is
  more convincing still — it read only the header and then jumped:

  ```
  [REMUX] #313 range=bytes=36968-41775     -> 206 len=4808   (still inside the 41,784-byte header)
  [REMUX] #314 range=bytes=41937-45097     -> 206 len=3161
  [REMUX] #315 range=bytes=399291-402146   -> 206 len=2856   ← 82 % in, nothing had produced it
  ```

  Both landed at 48.02 s of 60.02 and played on. Android's `screencap` in that window is the independent
  half: the frame carries `testsrc`'s big **48** and the native controls read `0:48 / 1:00` with the scrubber
  at ~80 %. **5/5 Android launches identical** (`t=1.93 / 1.91 / 1.97 / 1.98 / 1.93`), so this is not one lucky
  run. ⚠ **The counts are 4 + 1, not 5 in a row, and the split is the interesting part**: four launches while a
  throwaway disposal probe shared the pipeline, then a fifth AFTER it was removed — which is what proves the
  revert did not break what the first four measured. A probe deleted without re-running the one beside it is
  how a green measurement becomes a claim about a tree that no longer exists.
- ⚠ **iOS's 508 tiny requests cost NOTHING extra here, and that is a property of the design rather than luck**
  — the layout is planned ONCE per source identity and cached, so serving a range is a binary search plus a
  seek. A path that re-planned per request would have run 508 full metadata walks. `ComputedRemuxRoute`'s
  layout cache is what makes iOS viable, not an optimisation.
- 🔴 **BUT ANDROID TRANSFERS THE WHOLE OUTPUT FOR A TAIL RANGE, and the header hides it.** Its delivery is
  `Unsliced`, so `WebViewFiles.ServeRange` calls `read(0, totalLength)` and the platform applies the range
  start itself: the tail request above sent `Content-Length: 95161` while **488,377 bytes were read to answer
  it**. So "only the bytes the range touches" is true of the layout reader and **false of an Android response** —
  which is a claim about the TRANSFER, and the cost is the transfer rather than the allocation because the
  body is read lazily.
  ⚠ **There is no size ceiling on this path, deliberately.** A 64 MiB one existed and was deleted: a number
  sized for a BUFFERED body protects nothing once no body is buffered, and it was never the bound it looked
  like anyway — it was checked against the PLANNED length, which the metadata walk produces, so a big film
  paid its whole walk first and was declined after. A big film on Android therefore streams its whole output
  per request — **82,843,185 bytes in 117,285 reads of 2 KiB, 26–31 s**, in the big-film section below.
- ⚠ **A fixture for this path must be PLANNABLE, and the sample's older `.mkv` clips are not.**
  `Mp4Remuxer.Plan` is lossless by contract — H.264/HEVC video and AAC audio only — while every clip built for
  the conversion tier deliberately carries something MP4 cannot hold. Measured against `Plan` directly:
  `clip-mp3`, `clip-ac3`, `clip-video-mp3`, `clip-video-ac3`, `clip-mpeg4-aac`, `clip-mpeg2-aac` **all answer
  `null`** and fall through. A route that correctly declines every fixture you have looks exactly like a route
  that does not work.

### 🔴 What a `<video>` does with the route's `503` — **IT IS INDISTINGUISHABLE FROM A 404** (2026-08-13)

The route's premise is ONE ordinary `<video src>` and its first request for an unplanned source is a `503`, so
this is the claim the whole design rests on. **A media element is not a polling loop, and neither status code
makes it one.** Both arms below ran on the SAME element in the SAME document, seconds apart
(`RemuxRouteProbe.CheckFirstRequestAsync`; the 404 arm is a name in the allow-list with no file behind it, so
the ROUTE answers it rather than the platform's asset handler).

```
Android  [REMUX] #1 range=bytes=0-      -> status=404
         [REMUX-FIRST] 404 control -> ERROR@0.05 ready=0 net=3 size=0x0 dur=NaN err=4 msg=[MEDIA_ELEMENT_ERROR: Format error] t=0.00
                                     || PLAY-REJECTED@0.05 NotSupportedError || t+0.3s … || t+1.0s … || t+2.0s …
                                     || t+4.0s ready=0 net=3 size=0x0 dur=NaN err=4 msg=[MEDIA_ELEMENT_ERROR: Format error] t=0.00
         [REMUX] #2 range=bytes=0-      -> status=503
         [REMUX-FIRST] timeline    -> ERROR@0.07 ready=0 net=3 size=0x0 dur=NaN err=4 msg=[MEDIA_ELEMENT_ERROR: Format error] t=0.00
                                     || PLAY-REJECTED@0.07 NotSupportedError || t+0.3s … || t+1.0s … || t+2.0s … || t+4.0s … || t+8.0s …
                                     || t+12.0s ready=0 net=3 size=0x0 dur=NaN err=4 msg=[MEDIA_ELEMENT_ERROR: Format error] t=0.00
         [REMUX-FIRST] the route answered 1 request(s) in those 12 s, 1 of them 503
         [REMUX-FIRST] re-pointed  -> size=480x270|dur=60.023|ready=4|err=-|advanced=1.40

iOS      [REMUX] #1 range=bytes=0-1     -> status=404      #2 range=bytes=0-1445 -> status=404
         [REMUX-FIRST] 404 control -> PLAY-REJECTED@0.08 NotSupportedError || ERROR@0.08 ready=0 net=3 size=0x0 dur=NaN err=4 msg=[] t=0.00
                                     || t+0.3s … || t+1.0s … || t+2.0s … || t+4.0s ready=0 net=3 size=0x0 dur=NaN err=4 msg=[] t=0.00
         [REMUX] #3 range=bytes=0-1     -> status=503      #4 range=bytes=0-1445 -> status=503   (30 ms apart)
         [REMUX-FIRST] timeline    -> PLAY-REJECTED@0.06 NotSupportedError || ERROR@0.07 ready=0 net=3 size=0x0 dur=NaN err=4 msg=[] t=0.00
                                     || t+0.3s … || t+1.0s … || t+2.0s … || t+4.0s … || t+8.0s … || t+12.0s ready=0 net=3 size=0x0 dur=NaN err=4 msg=[] t=0.00
         [REMUX-FIRST] the route answered 2 request(s) in those 12 s, 2 of them 503
         [REMUX-FIRST] re-pointed  -> size=480x270|dur=60.023|ready=4|err=-|advanced=1.38
```

- 🔴 **`error.code 4` (`MEDIA_ERR_SRC_NOT_SUPPORTED`), `readyState 0`, `networkState 3`
  (`NETWORK_NO_SOURCE`), `play()` rejected `NotSupportedError` — within ~70 ms, and identical in EVERY FIELD
  the element offers, for the 404 and the 503, on both shells.** That now includes `error.message`, sampled
  after a review pointed out that "identical" had been claimed without having looked at every field: **Android
  says `MEDIA_ELEMENT_ERROR: Format error` for both arms, iOS says nothing (`msg=[]`) for both arms.** ⚠ So
  the message is the one field that differs BETWEEN shells while being identical WITHIN each — which is what
  makes it useless to a page (UA-specific and non-normative; nothing portable can branch on it) and worth
  recording anyway. The element never recovers on its own: the plan for this fixture lands in ~200 ms and the
  element issues NO request in the following 12 s, so a retry would have succeeded and there was none.
- ⚠ **iOS's "2 requests" is not a retry** — it is AVFoundation's sniff pair (`bytes=0-1` then `bytes=0-1445`)
  issued 30 ms apart, and the 404 arm produces exactly the same two. **This is why the request COUNT is the
  instrument and the element's own state is not**: "never recovered" and "retried and failed again" look
  identical from `readyState`.
- ✅ **Re-pointing `src` after the plan lands plays it immediately, on the same element** — so the route's
  documented page contract holds. What does NOT hold is the reasoning in
  `MediaConversionExtensions.NotReadyYet` ("404 would tell a media element to give up permanently"): so does
  the 503. **The 503's advantage is real only for a retrying `fetch` client**, and for a page that re-points
  after an event rather than remembering a 404. Corrected in that XML doc, in `ComputedRemuxRoute`'s and here
  at the same time.
- 🔴 **THIS GATE HAS TWO PRECONDITIONS THAT MUST BE MECHANICAL, because both failures print a plausible PASS.**
  `RemuxRouteProbe` counts the `503`s and `404`s the route actually answered and refuses to interpret a
  timeline without them. Sabotage-verified in both directions on a device, 2026-08-13:
  - **Fixture already planned** (`CheckAsync` moved in front of the gate) → the "503 arm" is really a `206`,
    the timeline reads `METADATA@0.04 … PLAYING@0.08 ready=4 size=480x270 dur=60.023`, and the pre-mechanism
    verdict was *"PASS — and BETTER than the route documents: the element recovered from the 503 BY ITSELF"* —
    the exact false claim the gate exists to prevent. With the count: `0 of them 503` → **FAIL**, naming the
    ordering.
  - **`AbsentFixture` removed from `Resolve`'s allow-list** → the route logs
    `#1 … -> status=FELL THROUGH` and the PLATFORM's asset handler answers, whose element state is
    *indistinguishable* (`ERROR@0.03 ready=0 net=3 err=4 msg=[MEDIA_ELEMENT_ERROR: Format error]`). Only the
    count catches it: `0 route 404(s)` → **FAIL**. ⚠ **This is the one to remember**: an arm can measure the
    wrong responder and produce a perfect-looking timeline, so a status-code A/B must assert WHO answered.
  - Both restored, and the run after them is green (Android `1 of them 503`, `1 route 404`).
  - ⚠ **The counts are per-shell and the thresholds must stay "at least one", not "exactly one":** iOS answers
    the sniff pair, so a healthy run there reports `2 request(s) …, 2 of them 503` and two route 404s.
    Confirmed green on the iOS simulator against the same code.

### A film PAST the deleted 64 MiB ceiling (2026-08-13)

Source built rather than committed (78 MB — the command is on `RemuxRouteProbe.BigFixture`): 81,635,953-byte
Matroska, 1000.02 s, H.264 640x360 + AAC, planning to **82,843,185 bytes and 76,876 samples** — 79 MiB of
output, where the deleted ceiling declined anything over 64 MiB.

All numbers below are from the **committed tree** (the run after the throwaway instrument was deleted), not
from the runs that first produced them — the spread across four Android and three iOS runs is in the last
column-note.

| | Android (WebView 133, API 36) | iOS (iPhone 16 Pro simulator, iOS 26) |
|---|---|---|
| plans | `82843185 bytes, 76876 samples` in **~0.9 s**, in a mission (one `503` then `206`) | same plan, **~1 s** |
| plays | **YES** — `size=640x360 dur=1000.022 ready=4 err=- t=1.29 advanced=1.22` | **YES** — `size=640x360 dur=1000.021 ready=4 err=- t=1.44 advanced=1.39` |
| seekable / buffered | `seekable=1000.02` with `buffered=62.87` (54.51–64.87 across runs) | `seekable=1000.02` with `buffered=1000.02` |
| **cold seek to 80 % (800 s)** | **YES** — `target=800.02 landed=800.02 t=800.93 advanced=0.91` | **YES** — `target=800.02 landed=800.02 t=801.22 advanced=1.20` |
| run-to-run spread | play `advanced=1.04–1.31`, seek `t=800.93–801.44` | play `advanced=1.20–1.39`, seek `t=801.22–801.27` |

- ✅ **The ceiling's removal is earned: a 79 MiB computed output plays and cold-seeks on both shells**, and the
  walk it used to be checked against costs about a second for 76,876 samples.
- ✅ **Independent of the element's own numbers: an Android `screencap` in the seek window shows `testsrc`'s
  big counter reading `800` and the native controls reading `13:20 / 16:40`** with the scrubber at ~80 % — the
  frame is decoded, from a region of a file that was never produced. ⚠ The capture window is ~5 s wide (the
  conversion probe takes the element next), so take shots back-to-back from ~30 s after launch rather than
  trying to time one.
- 🔴 **BUT ANDROID'S PER-REQUEST COST IS THE WHOLE OUTPUT, AND IT IS NOW MEASURED IN SECONDS.** Its delivery is
  `Unsliced`, so every request reads `read(0, totalLength)`: a `fetch` asking for `Range: bytes=0-65535` was
  handed **all 82,843,185 bytes in 117,285 reads of 2 KiB, taking 26–31 s.** The tail requests the element
  makes are the same shape (`bytes=66584576- -> 206 len=16258609`, and the body under it is the whole film).
  iOS asks for exact windows and gets exactly those (`firstChunk=65536`, `content-length=65536`). **So a
  two-hour film is comfortable on iOS and expensive on Android, per request** — that is the number to have
  before promising the computed path for large media there.
- ⚠ **A harness consequence worth keeping:** a probe that reads a whole body to count it will time out on
  Android for a big film (over `PageProbe`'s 10 s budget, at the remux probe's 30 s) and report NO ANSWER
  about a route that answered perfectly. Read ONE chunk and cancel.

### 🔴 A body that throws MID-RESPONSE — **ANDROID IS FIXED; iOS STILL GOES SHORT AND SILENT** (2026-08-13)

A lazily-read body moved failure detection AFTER the headers are committed (`WebViewFiles.Read`,
`ComputedRemuxRoute.Produce`). ⚠ **This is not only about truncation, and it is not only about media:** the old
`WebViewFiles.Read` wrapped `OpenRead` → `Seek` → `ReadExactly` in ONE `catch (Exception) { return null; }`, and
null is a clean 404, so **any** read failure — a shrunken file, an ejected card, a dropped share, a revoked
permission — was answered before a byte had left. The new one guards only the open and the seek, so all of them
now surface from inside a PLATFORM read, and **`UseFiles` (shipped since 0.9.1) is in scope, not just the
computed-remux route.** Measured two ways — a real source truncated to 1,000,000 bytes mid-request (which
arrives as `EndOfStreamException`), and a stream that simply throws at a fixed offset with no kit code under it:

```
Android  [LAZYBODY] truncated to 1000000 bytes mid-request
         [Shenora.Modules.Media] computed remux could not read a planned range (EndOfStreamException)
                                 — dropping the cached plan so the next request re-plans
         E chromium: [ERROR:jni_android.cc(159)] Crashing due to uncaught Java exception
         E cr_JniAndroid: android.runtime.JavaProxyThrowable: [System.IO.EndOfStreamException]:
             the source ended before a planned span's bytes did
           at Shenora.Modules.Media.Mp4LayoutRangeStream.Read
           at Shenora.Core.WebView.BoundedBodyStream.Read
           at Android.Runtime.InputStreamAdapter.Read
           at mono.android.runtime.InputStreamAdapter.read(InputStreamAdapter.java:52)
           at org.chromium.components.embedder_support.util.InputStreamUtil.read(chromium-…)
         I ActivityManager: Process com.shenora.sample.maui (pid 10094) has died: fg TOP     ← 0.4 s later

iOS      [LAZYBODY] truncated to 1000000 bytes mid-request
         [LAZYBODY] B: element -> SUSPEND@0.60 || PLAYING@0.60 … || t+10s ready=4 net=1 err=- t=9.33 size=640x360
         [LAZYBODY] answering /shortbody: 200, Content-Length 262144, a body that throws after 65536
         [LAZYBODY] A: short body, fetch -> status=200|bytes=0|ms=3      ← the app SURVIVES
```

- 🔴 **On Android the kit's own "fail loudly" throw WAS an APP KILL**, not a failed response. Reproduced twice,
  once with the kit's whole stack under it (`Mp4LayoutRangeStream` → `BoundedBodyStream`) and once with a bare
  throwing stream — **so it was the platform's behaviour, not this seam's.** The route's own recovery (`Forget`
  the cached plan) ran correctly first and then the process died anyway.
- 🔴 **THE MECHANISM, AND STATING IT AS "Chromium does not catch it" MISDIRECTS THE FIX — that reading is
  FALSE.** **KNOWN, from Chromium's source:** a failing body already has an error path.

  ```java
  @CalledByNative
  public static int read(InputStream stream, byte[] b, int off, int len) {
      try { return Math.max(CALL_FAILED_STATUS, stream.read(b, off, len)); }
      catch (IOException e) { Log.e(LOGTAG, logMessage("read"), e); return EXCEPTION_THROWN_STATUS; }  // -2
  }
  ```

  `-2` is a distinct status from `-1` (EOF) and the native reader turns it into a net error — a **failed load
  the page can see**. **KNOWN too, and it is the actual cause:** .NET Android marshals a managed exception into
  Java as `android.runtime.JavaProxyThrowable`, which extends **`java.lang.Error`** — deliberately outside
  `catch (IOException)` and outside any `catch (Exception)` — so it leaves `InputStreamAdapter.read` uncaught and
  JNI's uncaught handler kills the process. The logcat above is consistent with exactly that: the frame the
  stack ends on IS `InputStreamUtil.read`, and the throwable's type is `JavaProxyThrowable`, not
  `java.io.IOException`. So the fix is not "teach the shell to cope"; it is **"hand the shell something its
  existing catch can already see"**.
- ✅ **THE MARSHALLING ASSUMPTION HELD, AND THAT IS THE FIX (2026-08-13, same emulator: API 36, WebView 133).**
  A managed `Java.IO.IOException` thrown from inside the body arrives in Java as its **PEER** — not re-wrapped in
  a `JavaProxyThrowable` — so Chromium's existing `catch (IOException)` runs. `MobileWebViewInterceptor` now
  wraps every Android body (`PlatformBody` → `AndroidResponseBody`) and translates a mid-read failure into one.

  🔴 **THE MEASUREMENT HAS THREE ARMS, AND THE MIDDLE ONE IS WHY.** The wrapper BRANCHES on the exception's
  type, so a single-exception A/B measured one point on the axis the code discriminates — and the first version
  of this wrapper was WRONG at a second point. It rethrew every `Java.Lang.Throwable` on the reasoning that "it
  already has a peer, so the platform sees its real type"; **the goal is not that the platform sees the real
  type, it is that the throwable lands inside `catch (IOException)`.** A peered throwable that is not a
  `java.io.IOException` marshals as its own peer and misses that catch exactly as `JavaProxyThrowable` did.
  Reachable by `WebViewFiles.Read`'s own named trigger — an adopter serving a user-picked SAF document through
  `ContentResolver.OpenInputStream` whose URI permission is revoked mid-read gets a
  `Java.Lang.SecurityException`, a peered `RuntimeException`. The instrument was a `ThrowingBodyProbe` in the
  MAUI sample — the deterministic arm of `LazyBodyProbe` rebuilt with a `?kind=` switch, and deleted again once
  it had answered, the shape `ResponseDisposalProbe` set. The three arms, one route answering `200` with
  `Content-Length: 262144` over a body that throws after 65,536 bytes:

  | arm | body throws | rethrow-every-peer wrapper | `catch (Java.IO.IOException)` wrapper |
  |---|---|---|---|
  | MANAGED | `System.IO.EndOfStreamException` | translated → failed load | translated → failed load |
  | **PEERED-NON-IO** | `Java.Lang.SecurityException` | 🔴 **PROCESS DIED** | ✅ translated → failed load |
  | PEERED-IO | `Java.IO.IOException` | passes through → failed load | passes through → failed load |

  ```
  ARM 2 UNDER THE REJECTED WRAPPER — the hole, reproduced (pid 14226)
    I SHENORA        : [THROWBODY] arm MANAGED -> status=200|BODY-THREW TypeError: Failed to fetch|ms=33
    I SHENORA        : [THROWBODY] answering /throwbody kind=PeeredNonIo: 200, Content-Length 262144, …
    E chromium       : [ERROR:jni_android.cc(159)] Crashing due to uncaught Java exception
    E cr_JniAndroid  : java.lang.SecurityException: deliberate mid-response failure at 65536 of 262144
    E AndroidRuntime : FATAL EXCEPTION: ThreadPoolForeg
    I ActivityManager: Process com.shenora.sample.maui (pid 14226) has died: fg TOP
    ⇒ note the throwable is `java.lang.SecurityException`, NOT `JavaProxyThrowable`. The peer marshalling
      worked perfectly — which is precisely why it killed the app. PEER ≠ CATCHABLE.

  ALL THREE ARMS UNDER THE SHIPPED WRAPPER (pid 14410; and the earlier 262 KiB run's 2 trials)
    E InputStreamUtil: java.io.IOException: Shenora: the response body failed mid-read (EndOfStreamException).
    E InputStreamUtil: java.io.IOException: Shenora: the response body failed mid-read (SecurityException).
    E InputStreamUtil: java.io.IOException: deliberate mid-response failure at 65536 of 262144
    E InputStreamUtil: Got exception when calling read() on an InputStream returned from
                       shouldInterceptRequest. This will cause the related request to fail.
    I SHENORA        : THROWBODY: promised=262144 threwAfter=65536 | MANAGED=status=200|BODY-THREW TypeError:
                       Failed to fetch|ms=35 | PEERED-NON-IO=…|ms=31 | PEERED-IO=…|ms=22
    ⇒ zero `Crashing due to uncaught` / `JavaProxyThrowable` / `has died` in the whole run. The THIRD line has
      no `Shenora:` prefix — that is the body's OWN message, i.e. the passthrough arm really passed through.

  THE ORIGINAL CRASH, for the record (pid 13071, managed exception, rethrow-nothing build)
    E cr_JniAndroid  : android.runtime.JavaProxyThrowable: [System.IO.EndOfStreamException]: …
    I ActivityManager: Process com.shenora.sample.maui (pid 13071) has died: fg TOP   ← 1.4 s after the throw
    ⇒ the app's own log ENDS at the route's "answering" line. The page never reported.
  ```

  🔴 **`InputStreamUtil`'s OWN log line is the load-bearing evidence, not merely the absence of a crash** — it is
  Chromium saying it caught an `IOException` and *"this will cause the related request to fail"*, i.e. the `-2`
  path. And the throwable carries only the exception TYPE: Chromium logs the message itself, so a real path in an
  OS `IOException` would go to logcat; the detail goes to the kit's own log instead.
  ⚠ **`AppCallback.Log` swallowing a throwing sink is load-bearing HERE** and not merely tidy: that log call
  sits inside the translating `catch`, so an unguarded sink that threw would itself become a `JavaProxyThrowable`
  and defeat the whole fix.
  ⚠ **A genuine `java.lang.Error` is rethrown deliberately** (OOM, StackOverflow). Converting one into a failed
  request would let the app limp past a fatal condition; the crash this wrapper removes was a correctness bug,
  that one is the runtime telling the truth.
  ⚠ **`status=200|BODY-THREW` is the correct shape and not a half-failure** — the status line was committed
  before the body was read, exactly as `WebViewFiles.Read` documents, so the failure can only arrive on the body
  stream. It is what a `fetch` reports for any interrupted response.
  ✅ **THE KEPT PROBES WERE RE-RUN ON THE WRAPPED BUILD**, because it wraps EVERY Android body, not just failing
  ones — and the whole run is in the saved log rather than asserted: `HEADERS`, `REMUX-FIRST`, `REMUX ×2` (incl.
  the 79 MiB film, `content-range: bytes 0-82843184/82843185`), `REMUX-SEEK ×2` (48.02 s and 800.02 s cold
  seeks), `CONVERT ×2`, `CONVERT-REFUSAL`, `APP PIPELINE`, `MEDIA`, `RELOAD` — all PASS, zero crash tokens.
  (`UI-PLAY: INCONCLUSIVE` is the standing gesture limitation, not a regression.) ⚠ **Run TWICE, and the second
  is the one that counts**: once with the probe still registered, then again on the tree AS COMMITTED with the
  probe deleted — because a probe that shares the pipeline can be what makes the others pass, and the run that
  proves the delivered tree is the one with no instrument in it.
  ✅ **AND THE NON-READ MEMBERS ARE UNREACHABLE, NOT MERELY UNMEASURED:** the same run counted every non-read
  member the platform touched on a body it was handed — **`Length=0 Position=0 CanSeek=0 Seek=0`**, across four
  bodies including a 64 MiB one and one abandoned mid-transfer. So `InputStreamAdapter` reads and closes and
  does nothing else, and translating a throw from `Length` would be dead code. ⚠ Those counters covered the
  PROBE's own bodies; a throwing `Close` is still untranslated and still unmeasured.
  ✅ **AN ABANDONED BODY IS STILL DISPOSED THROUGH THE WRAPPER** — re-measured rather than carried over, since
  the wrapper's `Dispose` forwarding is what releases the source handle: a 64 MiB body whose `fetch` aborted
  after 6,144 bytes gave `handed=1 drained=0 disposed=1`, disposed 7 reads / 14,336 bytes in, undrained. ⚠ The
  `/proc/self/fd` counter was sampled only before and after (`0 → 0 → 0`), so it is consistent with disposal but
  is NOT independent evidence; the `DISPOSED` line is.
- ⚠ **On iOS the same failure is invisible**: the page keeps its committed `200` and gets a body SHORT of the
  promise — zero bytes in the measured case, in 3 ms, with no exception on the fetch. A `<video>` already
  streaming did not even notice (it played on from what it had, and the post-truncation requests it did make
  answered `503` because the shrink changed the source's length, hence its identity, hence the plan).
- ⚠ **The truncation window is Android-shaped.** There, one request reads the whole output, so a shrink lands
  mid-body easily; on iOS every window is small and completes, so the shrink lands BETWEEN requests and
  re-keys instead. To ask iOS the mid-body question at all you need a body that throws by construction.
- **Do not "fix" this by returning 0** from a bounded body: that is the silent-corruption path, and it trades
  a visible crash for a film that plays back wrong. The fix belongs at the per-platform seam that READS the body
  (`MobileWebViewInterceptor`'s handover) — which is where Android's went. ⚠ **And it is deliberately NOT
  symmetric:** iOS already produces the silent short body a "swallow the exception" wrapper would give, so
  wrapping there would make an untested shell merely LOOK handled. Its answer is separate work — see `TASKS.md`.
- ✅ **WINDOWS IS MEASURED NOW (2026-08-13), AND IT BEHAVES LIKE iOS: A SILENT SHORT BODY.** A route
  answering `200` with `Content-Length: 262144` over a body that throws after 65,536 bytes gave the page
  `status=200|promised=262144|bytes=98304`, **no error on the `fetch` at all**, with `throws=1` proving the
  read really did fail. The app SURVIVED — `INTERCEPTOR SEAM: PASS` ran in the same process afterwards — so
  **Android remains the only shell where a throwing body was fatal**, and its `JavaProxyThrowable` mechanism
  has no WebView2 analogue, exactly as suspected.
  - ⚠ **98,304 is not 65,536, and the difference is the finding's shape:** WebView2 pulled 1.5× the bytes the
    stream served before failing, so it reads AHEAD in chunks and the page keeps whatever completed chunks
    arrived. A test asserting "the page got exactly what the stream served" would be wrong.
  - **The instrument was `ThrowingBodyProbe` in the DESKTOP sample** — the Android one's shape, and to be
    deleted the same way once it has answered. ⚠ Launched from a throwaway `devtools/_*.mjs` script
    (gitignored — spawn the exe, then `p.kill()` on a timer), never under `timeout`, which manufactures the
    renderer crash this file documents.
  - 🔴 **So TWO of three shells commit a short body silently, which makes it the PIPELINE's answer rather
    than a platform quirk** — and `UseFiles` has shipped that way since 0.9.1. Deciding what the kit should
    DO about it is now one decision covering iOS and Windows together, not two. ✅ What IS measured there (2026-08-13, `InterceptorProbe` in the desktop sample):
  a NON-seekable `BoundedBodyStream` handed to `CreateWebResourceResponse` — where WebView2 always got a
  seekable `MemoryStream` before — serves `206` + `Content-Range: bytes 3-7/1000` with the offset pinned by
  content, the whole file, `416` and `404` correctly (`INTERCEPTOR SEAM: PASS`, 4 route hits). So WebView2 never
  seeks a body reporting `CanSeek == false`, not even the `Seek(0, Current)` position query a COM `IStream`
  consumer can make, and the unconditional `Seek`/`Position`-set throws cost nothing. ⚠ An ABANDONED desktop
  response is still UNMEASURED: `WebViewHost` disposes `Content` only when the handover FAILED, so a window the
  page drops before EOF has no closer but the finaliser. Android answers that question with a real `Dispose`;
  WebView2 has not been asked.
- ⚠ **The scenario to reproduce with is not the one that was easiest to script.** A truncation is convenient
  (`SetLength` on a second handle, mid-request) but the realistic case an adopter meets is removable or remote
  storage disappearing mid-playback from `UseFiles`, which produces a raw `IOException` on the same path with
  the same outcome and no line in the app's own log.

### 🔴 Does a shell DISPOSE a response's `Content` stream? — **THE TWO SHELLS DISAGREE**

This decides whether a delivery route may hand back a **lazily-read** body, which is what unblocked
`WebViewFiles.Read` and `ComputedRemuxRoute.Produce` (both ported 2026-08-13).
⚠ **It was recorded here as the thing that would "lift the 64 MiB ceiling and let a two-hour film use the
computed path", and it was right about the CAUSE and wrong about the SEQUENCE** — the lazy body shipped and the
ceiling stayed for a day, kept on the mistaken belief that it was still bounding the synchronous metadata walk.
It was not: the ceiling was checked against the PLANNED length, which the walk produces. Deleted the same day,
along with the belief. Moving the walk into an `IMissionScheduler` mission happened in the same session and was
necessary for its own reason (never block the resource thread), not for this one.
Measured with a `Stream` logging its own reads and its own `Dispose`, handed back as an ordinary 200 body of
262,144 bytes through the same `SetResponse` path every route uses:

```
Android  [DISPOSAL] first read asked for 2048, gave 2048
         [DISPOSAL] EOF after 128 reads, 262144 bytes
         [DISPOSAL] DISPOSED (disposing=True) after 128 reads, 262144 bytes, eof=True
         DISPOSAL: reads=128 bytesRead=262144/262144 eof=True DISPOSED=True

iOS      [DISPOSAL] first read asked for 32768, gave 32768
         [DISPOSAL] EOF after 8 reads, 262144 bytes
         DISPOSAL: reads=8 bytesRead=262144/262144 eof=True DISPOSED=False
```

- **Android reads in 2 KiB chunks, hits EOF, and disposes ~1 ms later on another thread. iOS reads in 32 KiB
  chunks, hits EOF, and NEVER disposes** — re-checked 4 s later and across a 12-minute log window; no
  `DISPOSED` line exists at all.
- 🔴 **So "the platform closes it" is NOT available as a design assumption, and neither is the opposite.** The
  kit disposes on neither shell's success path (`WebViewHost` only in `Build()`'s catch;
  `MobileWebViewInterceptor` hands the stream over and never touches it), so this is entirely the platform's
  behaviour — and it differs. **A lazily-read body must close ITSELF at EOF.** Both shells do read to EOF, so
  that is a reachable contract; trusting `Dispose` would leak a file handle per request on iOS, which is the
  shell where a container costs hundreds of requests.
- ⚠ Idempotence matters for the same reason: Android WILL also call `Dispose`, so a self-closing body must
  survive being closed twice.
- The instrument was `ResponseDisposalProbe` in the MAUI sample, deleted once it had answered — the numbers
  above are what it was for.

#### And an ABANDONED body — **Android disposes it; iOS never gets one** (2026-08-13)

The open question the section above could not answer: a body that never reaches its bound never runs the
self-close, so does the handle survive? Measured by counting every body a route handed out against how many
were DRAINED (read to the promised length) and how many the platform DISPOSED, while a `<video>` playing the
79 MiB film had its `src` re-pointed mid-download and then dropped entirely:

```
Android  LAZY-ABANDON: handed=2 drained=0 disposed=2 | fds(before=1 after=0 afterGC=0)
         [LAZYBODY] #3 DISPOSED after 5578 reads, 5003264/82843185 bytes, drained=False
         [LAZYBODY] #4 DISPOSED after 6656 reads, 5756928/82843185 bytes, drained=False

iOS      LAZY-ABANDON: handed=712 drained=712 disposed=0 | fds(before=-1 after=-1)   ← no /proc on Darwin
         lsof -p <sim pid> | grep -c clip-   ->  0
```

- ✅ **Android DISPOSES an abandoned body — that is a real answer to the question.** Two bodies, 5–5.7 MB into
  an 82.8 MB promise, neither drained, both disposed — and `/proc/self/fd` shows the fixture's open descriptor
  going 1 → 0 without waiting for a GC. **`Dispose` is not only an at-EOF event there; it is also how an
  abandonment is signalled.**
- 🔴 **iOS's result is WEAKER THAN "an abandoned body does not leak there", and the difference matters:
  NO iOS BODY WAS EVER ACTUALLY ABANDONED.** All 712 were DRAINED — re-pointing the element mid-download does
  not leave a half-read window, because every window WKWebView asks for is small and it reads each one to the
  end, at which point `BoundedBodyStream` self-closes. So what is proven is **"this request pattern cannot
  leak"**, by construction rather than by the shell's cooperation (it disposed nothing). ⚠ **The `lsof` = 0
  cross-check is consistent with that and proves less than it looks like**: it was never shown able to read
  non-zero, so treat it as agreeing with the counters, not as an independent instrument.
- ⚠ **The untested case on iOS is therefore a LARGE abandoned window.** A route that answered one open-ended
  range there, plus a page that navigated away mid-stream, would hold that handle until the finaliser ran, and
  nothing in these numbers says otherwise. Nothing in the kit produces that today; a future path must not
  assume the drain — it is the ONLY thing standing between iOS and a leaked handle per request.
- ⚠ **A cancelled `fetch` reads as an abandonment too** — page-side `reader.cancel()` produced
  `#2 DISPOSED after 2 reads, 4096/82843185 bytes, drained=False` on Android, which is how the retry loop's
  control fetch can afford to stop after one chunk.

## Measured platform facts — playback and codecs (2026-08-09)

- 🔴 **BACKGROUND PLAYBACK CANNOT BE SOLVED FROM THE PAGE — measured on Android 2026-08-12, both halves.**
  The obvious pattern (on `visibilitychange`, copy the playhead to an `<audio>`, start it, pause the
  `<video>`) fails, and then the fallback fails too:
  1. **The start is REFUSED**: `play() REJECTED: NotAllowedError: play() can only be initiated by a user
     gesture`. The tap that started the video granted activation and activation is TRANSIENT — long gone by
     the time the user presses HOME, which is not a gesture on the page and never can be. So this is
     ordinary autoplay policy, NOT an "too late to start in background" rule.
  2. **An ALREADY-PLAYING `<audio>` advances ~15.3–15.6 s while hidden and then PAUSES**, measured twice,
     mid-clip (`t=20.30` of 60, `ended=false`) so it is not the end of the file. The process is suspended.
     ⚠ **Confirmed a third time from OUTSIDE the page** (2026-08-20, `dumpsys audio`): `started` at t+6 s,
     `stopped` by t+15 s, **still stopped at t+300 s** — so it is a stop, not a stall that recovers. An
     external instrument matters here because the page cannot report its own suspension.
  **Therefore the answer is a NATIVE anchor** — `IPlaybackSession` (MediaSession) and/or `IMediaPlayer` —
  which is the kit's thesis rather than a workaround. The sample keeps the failing handoff as a
  demonstration with the measurement beside it, because it is the first thing anyone tries.
- ✅ **THE HANDOFF WORKS END TO END, and two ordering facts decide it** (2026-08-12, API 36). It was proven
  in a sample probe (`BackgroundHandoffProbe`, since DELETED) and is now the kit's
  `BackgroundPlaybackTransfer`, driven from `MainPage`'s `Window.Stopped`/`Resumed` — the app supplies only
  the lifecycle hooks and `ResolveNativeSource`:

  ```
  HANDOFF: page 34.98s -> native 34.98s state=Playing      (app hidden)
  HANDBACK: native 45.76s -> page: resumed t=45.76         (…then played on to ended=true)
  ```

  - 🔴 **`visibilitychange` FIRES BEFORE MAUI's `Window.Stopped`, and the platform pauses a backgrounded
    `<video>` by itself — so by the time the host is told, the element reports `paused` and its live state
    is USELESS.** The first version asked the element directly and logged *"skipped — the page was not
    playing"* about a video that had been playing a second earlier: a true statement about the wrong
    moment. **So the playhead must come from something that OUTLIVES the pause.** The probe used a page
    global (`timeupdate` → `{src, t}`), which is what a raw-JS probe could reach; **the kit uses
    `IMediaPlayer.Status`**, fed by the page's own `PLAYER_REPORT`, and an app gets that from
    `useMediaPlayer(ref)`. ⚠ The hook reports on TRANSITIONS ONLY, so the position is as of the last one —
    the platform's own pause fires `pause` and refreshes it, but that report has to cross IPC before the
    process freezes. UNMEASURED on the hook's path: the sample's page also reports on `timeupdate`, so a
    green sample result does not prove it.
  - 🔴 **TWO OWNERS OF ONE ELEMENT LOSE.** The page's own JS handoff also called `vid.pause()`, which
    destroyed the very state the host needed. Only one side may drive the element per transition — the page
    hands off, the HOST hands back.
  - ✅ **The page CAN resume without a fresh gesture**, which was the open question: `play()` resolved on
    return and playback continued to the end. An element already played by a real gesture keeps that
    privilege, so the return trip does not need a button. ⚠ **Measured on ANDROID.** On iOS the policy is
    not gating this at all — `play()` resolved on a COLD page with no gesture ever (2026-08-20) — so the
    question does not arise there, but the return path itself was not run.
  - ⚠ **`Stopped` arriving AFTER the app is hidden is fine, and that is the whole reason this design works**
    — a native player is not subject to the webview's autoplay policy, so there is no race to lose. The
    page-side handoff had to win one, which is why it could not.
  - ⚠ Harness note: each `android eval` costs ~5–10 s (adb forward + CDP), so a 60 s fixture reaches its end
    within a couple of round trips. Read `ended` before calling a `paused` reading a failure.
  - ✅ **AND IT WORKS ON iOS TOO — the shell where the gap is sharpest** (iPhone 17 Pro simulator,
    2026-08-12). `HANDOFF: took over at 10.09s` → backgrounded via
    `simctl launch booted com.apple.springboard` → `HANDBACK: resumed t=53.31`: **43 s of native playback
    while hidden**, and a longer run completed the whole 60 s clip. The prerequisites are the documented
    pair and the sample has both: `UIBackgroundModes: [audio]` in Info.plist and an active
    `AVAudioSession(.Playback)`, which `IosMediaPlayer` takes.
  - 🔴 **IF IT FINISHES WHILE YOU ARE AWAY, HANDING THE POSITION BACK RESTARTS THE FILM.** Found on iOS:
    the clip ended in the background, the handback set `currentTime = 60.00` on a 60 s element, and the page
    reported **`resumed t=0.00`** — seeking to the very end REWINDS, and `play()` then started the film from
    the opening. Coming back to the credits is a worse bug than losing the audio. So a finished playback
    must hand back a FINISHED page: park just before the end, do not play, and say so. Detect it with
    `State is Ended` OR `Position >= Duration - 500ms`, because the position can report just under.
- ✅ **AND THE NATIVE PLAYER SURVIVES WHERE THE PAGE DIES — a clean A/B, same device, same HOME press**
  (2026-08-12, API 36). `IMediaPlayer` sampled every 5 s ran **45 s while hidden**, `state=Playing`, position
  advancing 1:1 with the wall clock; `document.visibilityState` confirmed `hidden` throughout. The page's
  already-playing `<audio>` dies at ~15.3 s on the same run. **3×, with no foreground service and no
  MediaSession notification** — so `IMediaPlayer` is not merely the tidier answer, it is the working one.
  - ⚠ **PROVEN FOR 45 s, NOT FOR MINUTES.** The staged clip is 60 s, so a longer window needs a longer
    file, and Android's freezer/Doze can arrive later. An EMULATOR is also gentler than a handset, where
    vendor power management is the real adversary. For long playback the app posts a FOREGROUND SERVICE —
    the kit owns the session, the app owns the notification, which is the split `IPlaybackSession` already
    documents.
  - ⚠ **My prediction was that it would die too**, on the reasoning that Android freezes a backgrounded
    process without a foreground service. It did not. Worth recording because the reasoning was sound and
    the measurement still overruled it — 45 s of grace exists before any service is needed.
- ✅ **An `<audio>` element keeps playing while backgrounded on iOS, and it is not a short grace period** —
  given `UIBackgroundModes: [audio]` and an active `AVAudioSession(.Playback)`. Measured 2026-08-20 on an
  iPhone 16 Pro simulator: backgrounded at 08:39:38, still playing at 08:44:57 when the log stream was cut
  — **319 s (5 min 19 s), ended by the observer and not by the platform**. `t` advanced 1:1 with the wall
  clock (08:39:57 `t=42.97` → 08:40:12 `t=57.97`: 15 s of clock, 15.00 s of audio) across FIVE crossings of
  the 60 s clip's loop boundary. The app process was alive at the end.
  🔴 **THE OLD 16 s WAS A 16 s WINDOW, NOT A LIMIT — nobody had left it backgrounded for longer.** It was
  then read as a platform ceiling because it landed next to Android's ~15.4 s, and **that closeness was
  coincidence**, which is what made it convincing. Two facts about the instrument had to be fixed before a
  longer run could mean anything:
  - **The probe played the 60 s clip ONCE**, so "survives 60 s" and "survives forever" gave the identical
    reading. `StartBackgroundAudioAsync` now sets `loop`. This ceiling was latent — no earlier figure is
    known to have hit it — but it made a long run unable to answer the question.
  - **The startup probe suite was still running inside the measurement window.**
    `StartBackgroundAudioAsync` re-clicks the audio button, `CheckUiPlaybackAsync` drives the `<video>`
    (which took the audio one second before a pause), and `CheckReloadAsync` reloads the page — and **a
    page reload is indistinguishable from the OS cutting playback off**, because both send `t` back to
    zero. Two runs here read as hard ceilings of ~76 s and ~87 s for exactly that reason.
  **To re-run**: `StartBackgroundAudioAsync`, wait for the suite to go quiet (~70 s after launch — watch
  the page log stop; 210 s was used and was ample), then `simctl launch booted com.apple.mobilesafari`.
  ⚠ **SIMULATOR, not a handset** — the same caveat the Android figure above carries.
  ⚠ **`play()` needs NO user gesture on iOS** — it resolved on a cold page load in this run, where Android
  refuses. So an iOS page can start its own audio; an Android one cannot.
  **`<video>` pauses by design** — the track cannot render — so a
  background test driven from a video element measures that rule and nothing else. ⚠ An earlier session
  concluded "iOS blocks webview background audio" from exactly that mistake. **So "only a native player
  survives backgrounding" is false**; the native player's case rests on codecs and the webview's ceiling.
  - ⚠ **The kit's own `IMediaPlayer` takes the audio session and pauses the page's `<audio>`** — measured
    one second before a native-player probe ran. Anything starting page audio must run after them.
  - ⚠ **`SAMPLE_LOGIC/TICK` is HOST-generated** (`[PAGE] <= EVENT`), so a continuing tick proves the HOST
    is alive — which background audio guarantees — and says nothing about page timers. The page's OWN
    `setInterval` throttling (2 s → 3 s) is what shows it backgrounded. With no audio playing the whole
    app suspends and the tick stops outright. **Know which side a signal comes from before using it as an
    instrument.**
- 🔴 **A decoder frame is routinely LARGER than one encoder input buffer.** MP3 decodes 1152 samples and
  AC-3 1536, while an AAC encoder's buffers are sized for its own 1024 — so feeding one decoded frame into
  one `MediaCodec` input buffer overflows on the FIRST frame. Chunk it, and flag `EndOfStream` on the last
  chunk only. This broke Android transcoding for every input it ever had.
- 🔴 **`AudioConverterConvertBuffer` cannot do a conversion needing a complex converter**, and
  compressed→PCM always is. The simulator refuses with `'op??'`
  (`kAudioConverterErr_OperationNotSupported`); **a real iPhone returns status 0 and ZERO BYTES** — success
  that converted nothing. The correct API is `AudioConverterFillComplexBuffer` with a native input
  callback. ⚠ Render an `OSStatus` as a FOURCC when logging it; nobody reads `1869627199`.
- 🔴 **AND THEN: `*ioNumberDataPackets = 0` + `noErr` from that input callback means THE STREAM HAS ENDED,
  not "nothing more in this call" — and `AudioConverter` LATCHES it forever.** Measured on the simulator
  2026-08-09, and it is invisible without a callback tally:

  ```
  decode #1  status=0 produced=1248 out=4992B | pump calls=2   ← works (1536 minus priming latency)
  decode #2  status=0 produced=0    out=0B    | pump calls=0   ← the converter never asks again
  ```

  A converter fed one frame per `Push` **starves on every call**, so the obvious callback declares the
  stream over while decoding the first frame and the whole rest of the file converts to nothing. **Return a
  non-zero OSStatus of your own when merely starved** — `FillComplexBuffer` hands that code straight back
  to the caller and keeps everything already written — and return `0` only on the deliberate final call.
  - 🔴 **`pump calls` IS the instrument.** *"The converter refused"* and *"the converter never asked"* are
    opposite diagnoses with identical return values, and three ranked candidates (an under-specified ASBD,
    the wrong `kAudioFormat*` constant, a frame that is not a bare syncframe) were all **wrong** — the ASBD
    was fine, `kAudioFormatProperty_FormatInfo` changed nothing, and `head=0B77…` proved the syncframe.
    Count the callback's invocations into the pinned context and read them back after the call.
  - **A decoder holds a priming tail**, so draining only your own PCM buffer ends every soundtrack early in
    a file that is well formed and reports nothing. Give both legs a final call.
- **Building fixtures: ffmpeg does what the platform tools refuse.** macOS cannot ENCODE AC-3
  (`afconvert` answers `fmt?`), which had this filed as needing a handset or the owner — ffmpeg encodes it
  anywhere. A transcode fixture needs a codec MP4 cannot CARRY, so decode→encode→mux is forced:
  `ffmpeg -i in.mp4 -vn -c:a ac3 -b:a 192k -ac 2 -t 20 -f matroska clip-ac3.mkv` (and `libmp3lame` for the
  Android side, since AOSP has no AC-3 decoder at all).
