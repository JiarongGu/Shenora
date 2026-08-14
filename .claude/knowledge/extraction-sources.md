# Extraction sources — which sibling proved which component, and what to fix while porting

Shenora is extracted, not invented. This rule maps each framework area to the app that proved it
(de-identified — the real names + file paths live in the private `local/EXTRACTION-MAP.md`;
read BOTH before porting anything). Foundation: 2026-07-30 survey of all five family repos.

## The rules

- **Port the proven file, keep its post-mortem comments.** In the sources, the comments carry the
  measured incidents (why not `Task.Run`-per-message, why `BeginInvoke` not `Invoke`, which
  Chromium flags were rejected). They are the product — carry them forward, updating names only.
- **Source map (who proved what):**
  - *Primary desktop sibling* — correlated postMessage IPC (envelopes + category wrapper + 50 ms
    notification batching), middleware dispatcher + facade base, WebView2 initializer/prewarmer +
    embedded-resource serving over a virtual host, single-instance guard (tested), drop-zone
    overlays, DPI-correct window-state service, secondary windows on own STA threads, STA file
    dialogs, structured i18n errors, the TS bridge/module-service/event-bus trio, dev interceptor.
  - *Second desktop sibling (conformance reference)* — same framework layer co-evolved; adds
    frameless window chrome (WM_NCCALCSIZE, manual work-area maximize, DWM dark border) + the
    frontend window-command routes (minimize/maximize/close/drag/resize) and a browser fake-bridge
    preview harness.
  - *Third desktop sibling (first adoption target)* — the minimal-seam proof (its tiny Core IPC
    assembly) and the gap list that is Shenora's value: no dev-server switch (stale-bundle
    footgun), uncorrelated fire-and-forget IPC, window-state code duplicated per window, portable
    app-paths layout with env overrides for child processes.
  - *Sonora (public; server-backed profile)* — best-in-family window-state store (logical-px
    store / physical restore / never-block-close), singleton mutex + `--restarted` widened-wait
    relaunch, WebView2 host with `.dev`-marker dev switch + settings hardening +
    NewWindowRequested→system browser, bounded drop-oldest event queue + UI-timer batch flush
    (`{"__batch":[…]}` — the same envelope its WebSocket uses: the transport-pluggable seam),
    tray/close-to-tray pattern, 25 s WebView2-init timeout guard, UI-thread anchor pattern.
    ALSO the P5 auxiliary-browser-sessions stack (D14): the one-place WebView2 configurator,
    offscreen render service + bounded LIFO session pool, driveable session primitives,
    per-provider/per-account login-window profiles with clear-on-logout, and co-browse
    streaming (CDP screencast out / input dispatch back). The primary sibling's external-login
    window is the second proof of the login-window shape.
  - *Lyntai (public; repo template, no code)* — packaging/versioning/release/devtools/docs model.
- **Fix the known gaps DURING the port, not after** (absent in every source): global
  unhandled-exception handlers + crash dialog; WebView2 runtime presence check;
  `NewWindowRequested`/`DownloadStarting`/`PermissionRequested`/`ProcessFailed` handling; options
  records replacing magic numbers (dev port, colors, timeouts, batch intervals); escaped JS
  injection; no `Console.WriteLine` logging (use `ILogger<T>`); no `as dynamic` payloads; no
  static mutable registration state; make eager embedded-resource preload lazy-with-warmup.
- **Merge, don't pick blindly, where two sources solved the same problem** (window-state: merge
  the DPI-pure-function testability of one with the RestoreBounds/never-block-close discipline of
  the other).

- **A declared dependency edge that nothing crosses is a duplication smell.** Found live: the web-hosting
  package declared its `ProjectReference` to the WinForms one and then imported nothing from it
  — so the port re-implemented browser-argument building (re-introducing the CDP
  env-var gotcha from `windows-dev-gotchas`), environment creation, the init-timeout guard, and
  settings hardening, and shipped WITHOUT the `NewWindowRequested`/`PermissionRequested`/
  `ProcessFailed` policies this file lists as must-fix. Before porting a helper a second time, grep
  the packages you already reference for an owner. **After D19/D20 a ported helper's home is decided
  by LAYER, not by which sibling proved it** (portable contract → `Shenora`; Windows
  implementation → `Shenora.Windows`; web hosting on top).

## Gotchas / traps

- The sources disagree on virtual-host mechanics (`SetVirtualHostNameToFolderMapping` vs
  `WebResourceRequested` + embedded resources). Both are legitimate: folder mapping for
  disk-backed bundles, resource interception for single-file embedded bundles. Shenora's frontend
  options must support both — don't "unify" one away.
- Keep sibling names out of tracked code/docs while porting (see `sensitive-info`) — attribution
  comments say "ported from a family app", nothing more.

## Harvesting the media stack from Sonora — what to lift, and what NOT to

⚠ **Moved out of `TASKS.md` on 2026-08-09**, where it had become 57 lines carrying no open work. It is
reference for a PORT, not a backlog item: read it before lifting anything from that sibling.


> DIRECTION (user, 2026-08-06): *"sonora actually got proper solution for media play and you can get its
> binary you can create resource pack to store them"* and, on where the bytes live, *"because this is a
> library so we need to ship this for adoption"*. So the kit SHIPS an engine for adopters — the open
> question was only which package carries it, not whether.

Sonora built on-device conversion + an HLS segment stream, proved both on a device, and wrote a hand-off
spec naming exactly what should and should not move — `2026-08-06-shenora-media-handoff.md`, in THAT repo
under its superpowers specs (not a doc of this repo). This entry tracks taking it. **Read that spec before
designing any of this** — every ⚠ in it is a bug that was actually hit, not a guess.

> **SUPERSEDED BY D52 — read that first.** The scope settled while this was being built: the package is a
> TRANSLATION LAYER (the minimum transformation that makes a file playable in a webview), not a media
> toolkit, and the kit ships NO ffmpeg bytes (D51). The build order below replaces what this entry
> originally proposed.

**Built so far:** `ResourcePack` (`Shenora.Modules.Update.Compression`) · `MatroskaProbe` (`Probe/`) ·
`MediaPlaybackPlanner` (`Plan/`) · `UseMediaConversion` + `UseSegmentStream` (`Deliver/`) · `Mp4Remuxer` +
the `ISegmentEngine` seam (`Engine/`). **Slices 1 and 2 are CLOSED** (2026-08-07 — the pipeline reshape and
the remuxer). The pipeline is **probe → plan → deliver → transform**, and
what remains of transform is the AUDIO, which is the half that needs a codec.

**✅ SLICE 3 IS MEASURED (2026-08-07) — and the answer is SPLIT PER PLATFORM, which is the finding.**
`CodecProbe` in the MAUI sample asks each platform directly; run on an iPhone 17 Pro (iOS 26.5.2, a real
device) and an API 36 AOSP emulator:

| | AC-3 decode | E-AC-3 decode | AAC decode | AAC encode |
|---|---|---|---|---|
| **iOS 26.5.2, iPhone 17 Pro** | **YES** (5.1 *and* stereo) | **YES** | YES | YES |
| **Android API 36, AOSP** | no | no | YES | YES |

- **So D52's "yes → a platform call" holds on iOS and fails on Android.** There is no single answer, and a
  design that assumes either one is wrong half the time.
- 🔴 **The ENCODE half is free EVERYWHERE** — both platforms encode AAC. That is a much narrower gap than
  "there is no engine": what is missing is one DECODER, on one platform.
- ⚠ **"AOSP does not" is not "Android does not."** Codec support is vendor-declared per device, which is
  exactly why `MediaCodecList` is a runtime query. A handset may well carry AC-3; this measures the
  emulator. **Never bake either answer in — ask the device.**
- ⚠ Two probe defects had to be fixed before the number meant anything:
  `kAudioFormatProperty_DecodeFormatIDs` is **macOS-only** (`'prop'` on iOS), and
  a failed query was reporting as a NEGATIVE. The AAC control is what caught it.

**Slice 4 — MOSTLY DONE (2026-08-07).** Owner: *"we still support for consumer use their own
decoder/encoder just if they needed, and we built something that can work by default"*. Shipped:

- **`IMediaCapability`** — asks the DEVICE what it decodes and encodes, implemented on both mobile shells.
  Every adopter used to hand-write `MediaPlaybackPolicy`'s codec sets as a guess; the kit now ships the
  QUESTION rather than the answer, which keeps D42 intact. Cross-checked against an independent platform
  query on the iPhone: `ac3 repairable=True`.
- **`new Mp4Remuxer().ToConverter()`** — the default `MediaConversionOptions.Convert`. Container repair
  with no engine, no binary, no licence weight. This is "working playback with NOTHING supplied" for the
  case D52 calls the common one.
  ⚠ It is an extension on `IMediaContainerWriter`, not a method on the remuxer: wrapping a writer is the
  INTERFACE's job, so a class named "Remuxer" does not mint a route delegate that might run a different
  muxer.
- **`IMediaStreamConversion`** (+ `IMediaStreamConversionRun`) — the transcode tier, ONE seam keyed by
  `MediaStreamKind`: a stream MP4 cannot carry goes through the device's codecs and everything carriable is
  copied untouched. Both mobile shells implement it (Android chains a `MediaCodec` decoder → AAC encoder; iOS
  uses AudioToolbox).
