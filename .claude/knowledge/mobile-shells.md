# Mobile shells — what a second and third target actually cost

The mobile shell runs on Android and iOS — shipped as `Shenora.Android` + `Shenora.iOS` from one
shared source tree — and needed **no `#if` anywhere** to do it. That
is the good news and it is also the trap: because the C# ports for free, every real cost lands
somewhere else — in the PAGE, on the BUILD HOST, or in the device harness. This rule is those costs.
Earned across the Android port and the iOS port (both 2026-08-02).

## The rules

- **Write the page for the SUPERSET of shells, never the one you tested on.** Identical markup looked
  correct on an Android emulator for a whole session and put its heading under the status bar and the
  Dynamic Island on the first iPhone run. Use `env(safe-area-inset-*)` with `viewport-fit=cover` —
  both collapse to nothing where there are no insets, so it costs the desktop shell zero. The same law
  covers strings: a shared bundle means `hello from android` eventually appears in an iPhone screenshot.
- **⚠ `env(safe-area-inset-*)` IS NOT THE WHOLE ANSWER, and the three ways it fails were each measured
  on a device (2026-08-05, Android 16 / API 36, punch-hole emulator).** The rule above used to stop at
  "use env()", which is why all three shipped:
  1. **Padding on `<body>` scrolls away.** The document scrolls, so the inset strip goes with it and
     content passes under the status bar the moment you scroll. Screenshot showed the button row behind
     the clock. **Fix is structural: body must NOT scroll** — make it a viewport-height flex column that
     owns the insets, and scroll a child (`min-height: 0` on that child, or it never shrinks).
  2. **`max()`, never `calc(12px + inset)`.** The inset already contains the clearance; adding your own
     padding stacks two and shows as a dead strip visibly bigger than the thing it avoids — measured 61
     CSS px reserved where the platform asked for 49. `max(12px, env(...))` gives breathing room where
     there is no inset and the platform's number where there is.
  3. 🔴 **Android reports the display CUTOUT only — never the system bars.** Measured on one device:
     `top=49` CSS px (exactly the 128-device-px camera band) while `bottom=0` **even though the
     navigation bar is genuinely 24 CSS px tall**. So content sits under the gesture pill and no amount
     of CSS can discover it. iOS does report both. **A page cannot solve this alone: the inset has to
     come from the host**, which knows it from `WindowInsetsCompat`.
  - **And they are 0 at FIRST PAINT.** The same probe logged `top=0 right=0 bottom=0 left=0` on the
    initial load and `top=49` only after a reload — so even the cutout is unprotected on first render.
  4. 🔴 **ROTATION MOVES AN INSET TO A DIFFERENT EDGE — it does not merely resize it.** Measured on the
     iOS simulator 2026-08-06, same device, one rotation apart:

     ```
     portrait   top=62 right=0  bottom=34 left=0
     landscape  top=0  right=62 bottom=20 left=62
     ```

     So a shell that reads the insets ONCE does not publish a slightly stale value — it publishes the
     wrong SHAPE forever, and the page reserves a strip along the top while the cutout is down the side.
     The iOS branch did exactly that (`view.SafeAreaInsets` read once at attach, commented "a single read
     after layout is enough", which is true of the first orientation only). Android was already correct
     because it re-read on `LayoutChange`. **Subscribe to MAUI's `SizeChanged`** — it fires on both
     platforms, so the fix is not per-platform even though the bug was.
     - ⚠ **Re-read ACROSS the rotation animation, not just at its start.** The size change is reported
       when the animation begins and iOS still reports the OLD orientation's insets at that moment, so a
       single read on `SizeChanged` republishes exactly the values the rotation invalidated.
     - 🔴 **And it was worse than stale: pre-fix, the iOS host never published a real inset AT ALL.** Its
       one read happened before layout and returned `0,0,0,0`, which `SafeAreaScript` correctly declines
       to write over a good default — so every iOS app got the DEFAULT guess for the whole session.
     - 🔴 **The sample MASKED this, and that is the transferable lesson.** Its page also reads `env()`
       itself, and iOS reports env() correctly and updates it on rotation — so the page looked right while
       the kit's own published `--sa-*` variables were wrong. An adopting app that trusts those variables
       (which is what the kit TELLS it to do, because env() is insufficient on Android) would have been
       stuck in portrait. **A sample that can answer the question a second way cannot detect that the kit
       stopped answering it.** The fix was only observable once the host logged its own numbers.
     - Painting the inset strips needs FOUR edges, not two. Top+bottom is the portrait shape mistaken for
       the general one: in landscape the padding is right and the strip is missing exactly where the notch
       is. Four borders on one full-viewport element do it with no JS.
  - **Measure, do not squint at a screenshot.** The sample logs its four insets plus viewport and dpr at
    startup (`samples/Shenora.Sample.Maui/.../index.html`); that one line turned three guesses into
    three numbers, one of which contradicted what the screenshot appeared to show.
- **The runtime identifier must match the TARGET, and the failure always names the wrong step.** Twice
  in the same shape now: `android-x64` against an arm64-only build fails at INSTALL with
  `INSTALL_FAILED_NO_MATCHING_ABIS`, and `iossimulator-x64` against `-arm64` fails the same way. The
  BUILD succeeds both times, so the error reads as packaging rather than architecture. Ask the target
  what it is (`dev.mjs mac` reads the Mac's `uname -m` over ssh); never assume the dev machine matches.
- **Prove it on the device, and say which half you proved.** A shell that compiles is not a shell that
  runs; `dev.mjs android` and `dev.mjs mac` exist so a claim about either can carry a screenshot.
- **⚠ MuMu's WebView is CHROMIUM 110 (AOSP), so an Android web claim proven only there is weaker than
  it looks — and this applies to EVERY such claim, not one.** Measured 2026-08-05:
  `com.android.webview 110.0.5481.154.1` on Android 12 / API 32 / x86_64. That is the AOSP WebView, not
  Google's, and it is roughly **20 major Chromium versions behind** what any real user runs. It also
  **cannot be upgraded**: the image ships no `config-webview.xml` and `com.android.webview` is the only
  allowed provider, so there is no Google WebView or Chrome to switch to.
  - **What it is still fine for:** anything above the web-platform layer — the IPC envelope, transports,
    lifecycle, file dialogs, the MediaSession/`IPlaybackSession` bridge, deployment and logging. Those
    are Android-framework behaviours, and MuMu is a real Android 12.
  - **What it is NOT sufficient for:** anything decided by Chromium — response validation
    (`net::ERR_INVALID_RESPONSE`), ORB/CORS, range handling, codec support, `fetch` semantics. A
    negative result there means "does not reproduce on Chromium 110", which is not "does not reproduce".
    Say which one you mean. The adopter's Android navigation report is exactly this shape: it
    reproduces for them, not for us, and we are 2½ years apart on the component that emits the error.
  - **So ask an adopter for their WebView version** (`adb shell dumpsys webviewupdate`) before treating
    a non-reproduction as evidence. It is the cheapest question on any Android web report and it was
    missing from ours.
  - **The fix, and it is a one-off: an SDK AVD carries a REAL WebView.** `shenora-a36` (API 36,
    `google_apis;x86_64`) reports `com.google.android.webview` **133.0.6943.137** and also has Chrome as
    an alternative provider. Recipe and its three traps are in `local/PROJECT_NOTES.md`; the one that
    matters most is that with MuMu AND an AVD attached, **unqualified `adb` picks MuMu** — always
    `adb -s <serial>`, and note the AVD is not always `emulator-5554`.
  - **Driving MuMu headlessly:** `MuMuManager.exe` is in `<install>\nx_main\`; `control -v <i> launch`
    starts an instance, `info -v <i>` reports its state and `adb_port` (`127.0.0.1:16384` for instance 0).
    `launch` returns immediately, so poll `info` before depending on the instance.
  - 🔴 **IF ADB HANGS OR A CAPTURE COMES BACK EMPTY, CHECK THE EMULATOR IS STILL RUNNING BEFORE DEBUGGING
    ADB.** `adb logcat -d` against a device that has gone away does not error — it HANGS, which reads as a
    broken log reader. Confirm with `MuMuManager info -v <i>`.
    ⚠ **Recorded 2026-08-09 with the wrong CAUSE attached, and the correction is the lesson.** This first
    said MuMu shuts itself down if you attach adb before `is_android_started` flips true. The owner had
    simply closed the emulator window mid-run. The evidence never supported the invented story either:
    the FIRST connection was made in exactly that window and deployed and ran fine through to
    `MEDIA: PASS`. **A story that explains the symptom is a hypothesis, and a harness symptom with a human
    at the keyboard has a suspect that no amount of log-reading will reveal — ask before writing a rule.**
  - ⚠ **Run the emulator with `-gpu host`.** Under `swiftshader_indirect` this AVD died after a few
    minutes, repeatedly, with nothing fatal in its log — which reads as a flaky harness rather than a
    software-rendering limit and cost an hour before the GPU was suspected.
  - **And the honest coda: the version gap did NOT explain the one report it was raised for.** The
    Android navigation defect fails to reproduce on Chromium 110 *and* on 133. A good hypothesis that
    dies to a measurement is still worth the measurement — but record which way it went, or the next
    session re-argues it.
- **A webview on both shells does NOT mean the auxiliary-SESSION stack ports (D39).** `StreamingSession`
  and friends rest on CDP (screencast, device metrics, OS-level input replay), which neither shell
  exposes in-process — iOS has no CDP at all. The trap is that a port IS buildable behind the same
  interface (frame-polling + `evaluateJavaScript` synthetic DOM events) and is materially weaker:
  polled, and `isTrusted: false`, which is exactly what the pages that stack exists for reject. Nothing
  needs stubbing, because the stack is in `Shenora.Windows` and portable logic cannot name it. Read D39
  before writing any of it — the sanctioned mobile answer per intent is there.

- **A platform CAPABILITY is asked at runtime, never assumed — and the answer differs per platform AND per
  device.** Measured 2026-08-07 with `CodecProbe` in the MAUI sample, on an iPhone 17 Pro (iOS 26.5.2) and
  an API 36 AOSP emulator:

  | | AC-3 decode | E-AC-3 decode | AAC decode | AAC encode |
  |---|---|---|---|---|
  | iOS 26.5.2, iPhone 17 Pro | **YES** (5.1 and stereo) | **YES** | YES | YES |
  | Android API 36, AOSP | no | no | YES | YES |

  🔴 **VIDEO: `mpeg4` DECODES ON THE DEVICE AND NOT IN THE WEBVIEW — the one measured gap, 2026-08-10.**
  `MediaCodecList(RegularCodecs)` reports `av1 h264 hevc mpeg4 vp8 vp9`; Chromium 133 answers
  `canPlayType("video/mp4; codecs=\"mp4v.20.8\"")` with `""` (controls `avc1`/`hev1`/`av01`/`vp9` all
  return `"probably"`). A real MPEG-4 Part 2 file served through the app's own route — `fetch` confirmed
  `200`, 176816 bytes — loads to `readyState=4` with **`videoWidth×videoHeight = 0x0` and NO error**, while
  the h264 original on the identical path gives `480x270`.
  - 🔴 **AN UNSUPPORTED VIDEO CODEC BESIDE A DECODABLE AUDIO TRACK RAISES NOTHING.** No `error`, a full
    buffer, and a blank picture — so `size=0x0` is the ONLY signal, which is why `CheckMediaAsync` asserts
    it. Never conclude "it played" from the absence of an error.
  - ✅ **The platform side is proven too:** `MediaMetadataRetriever.GetFrameAtTime` returned a real
    `480x270` frame from that file. It is an AVD — though Chromium's `mp4v` refusal is a licensing
    position, not a device property.
  - 🔴 **`MediaCodecList.FindDecoderForFormat` REFUSES THE FORMAT `MediaExtractor` GAVE YOU, so a working
    decoder looks absent.** The extractor's format carries `profile`/`level` (plus `max-bitrate`,
    `frame-count`, `sar-*`); nothing matches it and the answer is `(none)`. Strip to mime + dimensions and
    `c2.android.mpeg4.decoder` appears. **Build a MINIMAL format when asking**, or the platform's own API
    will tell you a codec you have is missing.
  - ⚠ Both mpeg4 decoders here are SOFTWARE (`c2.android.*`, `OMX.google.*`), so bridging that codec costs
    CPU per frame — not the "the OS does it in silicon" economics that make the audio path cheap.
  - ⚠ **`IMediaPlayer` cannot answer "did video decode"**: `MediaPlayerStatus` has no dimensions, and
    `android.media.MediaPlayer` advances position on the audio track alone. And the STOCK video player is
    useless as an instrument — it spins forever on `file:///sdcard` for the h264 CONTROL too.
  - Fixture: `ffmpeg -i in.mp4 -t 6 -c:v mpeg4 -q:v 5 -c:a aac -movflags +faststart out.mp4`. ⚠ `-vtag
    XVID` is refused against `mp4v` in MP4.
  **WIDENED 2026-08-10 on an API 36 `google_apis` AVD** (the image with a real Google WebView), reading
  the sample's `[CODEC]` probe where `repairable` = *the device decodes it*: `ac3` ✗, `eac3` ✗, `dts` ✗,
  `alac` ✗, and `vorbis` ✓, `mp3` ✓, `flac` ✓. The kit's converter declines exactly the four it cannot
  decode, so the planner never promises work the engine would refuse — that agreement is what the probe
  asserts. **Those four ARE the whole Android conversion gap**, and they are the entire case for an
  external engine (`TASKS.md`, "Deliberately NOT built").
  ⚠ **"AOSP does not" is not "Android does not."** Codec support is vendor-declared per device, which is
  exactly why `MediaCodecList` is a runtime query — a handset may carry AC-3 where the emulator does not.
  Neither answer may be baked into the kit.
  - **The working question on iOS is `AudioConverterNew`, not a format list.**
    `kAudioFormatProperty_DecodeFormatIDs` is **macOS-only** and returns OSStatus `'prop'`
    (`kAudioFormatUnsupportedPropertyError`) on a device. Constructing a converter between the two formats
    succeeds only when a codec for the pair exists, and it is what an engine has to do anyway.
  - **On Android use `MediaCodecList(RegularCodecs)`** — it excludes vendor-hidden codecs a capability
    check would otherwise count and then fail to instantiate.
  - 🔴 **Ask about a codec you already KNOW the answer to, as a control.** The first device run reported
    `aac: decode=no` from an iPhone, because a failed query returned an empty set and "empty" was read as
    "unsupported". With AC-3 alone in the probe, that "no" would have looked exactly like a finding and
    settled a design decision on a broken measurement. **A failed query must never be indistinguishable
    from a negative result** — report the status, and say INCONCLUSIVE.
  - Ask a multi-channel codec at BOTH 5.1 and stereo: a downmix-only decoder makes the difference between
    "no decoder" and "no 5.1 decoder", which are different design conclusions.

- 🔴 **An iOS APP EXTENSION cannot be produced by `swiftc` alone, and the failure is invisible.** An
  extension does not start at `main`: Xcode links one with **`-e _NSExtensionMain`**, so the process begins
  in Foundation's extension entry point, reads the `NSExtension` dict from `Info.plist` and enters the XPC
  run loop its host talks to. Link it as an ordinary executable and SwiftUI's `@main` runs
  `WidgetBundle.main()`, **that returns**, and the process exits before serving anything.
  - **What that looks like from outside is SUCCESS everywhere.** ActivityKit starts the activity and returns
    an id, updates are accepted, the system reserves Dynamic Island space — and the widget simply never
    loads. Nothing logs an error. Measured on an iPhone 17 Pro, 2026-08-07.
  - ⚠ **`-Xlinker -e -Xlinker _NSExtensionMain` was tried and is SILENTLY DROPPED** — the built binary still
    carries a normal `LC_MAIN`. Check it, do not assume: `otool -l <binary> | grep -A3 LC_MAIN`.
  - **A SIMULATOR CANNOT CATCH ANY OF THIS.** It loads the bundle regardless, which is how the devkit was
    "verified" for a whole release band while being broken on every real device.
  - **A hand-written `.appex` `Info.plist` must also carry the PLATFORM keys** Xcode writes and nobody
    otherwise learns exist — `CFBundleSupportedPlatforms`, `DTPlatformName`, `DTSDKName`, `UIDeviceFamily`.
    Without them the bundle installs, signs, validates and is **never registered as an extension**.
  - **And an extension is PROVISIONED SEPARATELY from its container**: its own `embedded.mobileprovision`
    inside the `.appex`, and its own entitlements (`application-identifier` matching its `CFBundleIdentifier`).
    The container's profile does not cover it.
  - `node devtools/dev.mjs mac appex-check` asserts the last two classes off the BUILD OUTPUT — no device,
    no install, seconds. The entry-point one it cannot yet see.

- ⚠ **iOS has TWO mechanisms that reach the Dynamic Island and they are MUTUALLY EXCLUSIVE.** **Now Playing**
  (`MPNowPlayingInfoCenter` + `MPRemoteCommandCenter`) is the system's MEDIA presentation, Apple's own look,
  and what a player is supposed to use. A **Live Activity** is a custom app-drawn card, for deliveries,
  timers and scores. An app publishing a Now Playing session takes the Island, and a Live Activity started
  beside it has nowhere to render — the tell is *"a long bar that only opens the app"*. A media app should
  probably not use a Live Activity for playback at all: it duplicates a presentation the system already
  gives media apps, which is the sort of duplication App Review pushes back on.

- **iOS background playback from a webview: `<video>` and `<audio>` behave DIFFERENTLY, and conflating them
  wastes a device cycle.** Two settings are required for either, and they are a pair — without
  `UIBackgroundModes: [audio]` iOS suspends the process; without an active `AVAudioSession(.Playback)` the
  system does not believe the app is playing. Then:
  - **`<audio>` already playing CONTINUES** when the app is backgrounded. What is restricted is STARTING
    new playback while backgrounded or locked.
  - **`<video>` is PAUSED on backgrounding** — the video track cannot render — and does not resume by
    itself on return. That is expected iOS behaviour, not a fault in the host.
  - ⚠ **So test background playback with an `<audio>` element.** Testing it with `<video>` measures the
    pause behaviour and tells you nothing about whether the host is configured correctly, because the two
    failures look identical from outside: plays in the foreground, silent after the swipe.
  - ⚠⚠ **Webview background audio does NOT need Apple-internal entitlements**
    (`com.apple.multitasking.*assertions`). The opposite was believed here for a while, on the strength of
    ONE Apple-forum thread about an iOS 13 regression plus a single failing test — which was a `<video>`
    element, i.e. the one case that legitimately pauses. **Two lessons, the second being the one that
    matters: a single negative result on a device is not a platform limit, and a search result describing
    an old OS-version bug is not a statement about the current OS.**

## Gotchas / traps

- 🔴 **An iOS link error naming the SDK's OWN symbols usually means STALE `obj/`, not a broken SDK — and
  `dotnet workload repair` is not the fix.** Measured 2026-08-08. The simulator build failed with
  `Undefined symbols for architecture x86_64: "_xamarin_gc_pump", referenced from xamarin_setup_impl() in
  main.x86_64.o`. Both names belong to the iOS SDK's own generated `main.m` and its runtime library, which
  is exactly what makes it read as "their bug, not ours" — and the task list had recorded
  `dotnet workload repair` as the next step for a day on that reading.
  **`rm -rf obj bin` then rebuilding fixed it. Repair had already been run and changed nothing.**
  - **Why it happens:** `main.m` is GENERATED into `obj/` and compiled to `main.x86_64.o`. Two iOS SDK
    generations were installed side by side (`26.0.11017` and `26.5.10301`), and `xamarin_gc_pump` exists
    only in 26.0's *debug* static lib — it is absent from both of 26.5's. So a `main.o` left over from an
    earlier resolution keeps demanding a symbol the current one no longer supplies. **The iOS incremental
    check does not cover the generated `main.m`.** This repo had already been bitten by the same class
    once: a stale appex plist whose incremental check covered only the executable.
  - **The elimination order that worked, and each step was cheap:** confirm the pack is not corrupt
    (`lipo -info` → both slices x86_64; `nm -gU` → the symbol IS in 26.0's debug `.a`), then check the
    OTHER installed generation (absent from both 26.5 libs → the symbol was removed, so a mixed
    resolution explains everything), then look at the actual link line — `grep` the `-v diag` log for the
    lines mentioning `main.x86_64.o` and count `libxamarin-dotnet.a` vs `libxamarin-dotnet-debug.a`.
    **Neither appeared: no xamarin archive was on the link line at all**, which is what finally pointed
    away from "wrong variant linked" and toward "these objects are not from this build".
  - ⚠ **Do not start by editing the sample or the harness flags.** `MtouchLink=SdkOnly` was suspected and
    cleared by one build without it; the managed half had been compiling clean throughout.
  - **The generalisable tell:** when the undefined symbol is the TOOLCHAIN'S own and its managed half
    builds, suspect your intermediates before their SDK. A clean build is 90 seconds and settles it.
- **A device-log command must FILTER BEFORE IT TAILS.** Written down for Android (`logcat -t N`
  applies `-t` to the RAW buffer, so a filterspec after it prints nothing) — and then rebuilt from
  scratch in the iOS harness, where a process-wide predicate is ~99% WebKit lifecycle chatter and
  `tail -n` showed a screen of noise with none of the app's lines. Both look exactly like a broken log
  sink. **The meta-lesson is why this bullet exists: a rule written about one harness does not protect
  the next harness you write in a different file.** Adding a second device loop? Re-read the first
  one's traps deliberately.
- **A build artifact does not travel with `git push`.** `dist/` is gitignored, so a remote build host
  has no `@shenora/react` and the sample silently falls back to its hand-written inline transport —
  running, looking correct, and proving less than the run it is being compared against. Build the
  client package ON the build host, and make the page SAY which transport it used.
- **iOS pairs the workload to an exact Xcode, and clearing that takes two flags.**
  `ValidateXcodeVersion=false` clears the up-front gate (an EQUALITY check on major.minor — a NEWER
  Xcode is refused too); `MtouchLink=SdkOnly` clears MT0180 from the ILLink **Setup** step, which
  independently wants the SDK headers Xcode ships. `PublishTrimmed=false` is rejected outright and
  `MtouchLink=None` still fails MT0180 — do not retry either. Simulator-debug only; matching the pair
  is the real fix. Machine-specific, so it belongs in gitignored config, never a tracked csproj.
- **A LINK-time package defect is invisible to every project-reference check — only an app-shaped
  PACKAGE consumer finds it.** `Shenora.iOS` 0.9.0 could not be linked by any iOS app that had not
  enabled the Live Activity devkit (five undefined `_shenora_activity_*`), and shipped anyway because
  a `DllImport("__Internal")` resolves at STATIC LINK time, which nothing in this repo's own builds
  exercised the way a consumer does. Verify a published iOS package like this, and each step is load-
  bearing: purge that version from `~/.nuget/packages/<id>/<ver>` first (else you validate your own
  build); `OutputType=Exe` + an `ApplicationId` (a library builds clean while doing NOTHING);
  `ManagePackageVersionsCentrally=false`; **and actually CALL the API** — an app that never touches
  the kit roots no `DllImport` and proves nothing. Then read the binary, don't trust "Build
  succeeded": `nm <app>/<name> | grep " T _<sym>"` for each symbol and `nm -u` for undefined. Two
  traps met while doing it: `dotnet new ios`'s own sources did not compile here (implicit global
  usings not applying), so hand-write the app rather than debug the template; and `$?` after a pipe
  reads the LAST command, so `dotnet build … | tail` reported exit 0 for a FAILED build — use
  `${PIPESTATUS[0]}`.
- **"iOS needs a Mac" is TRUE OF AN APP AND FALSE OF A LIBRARY, and the difference cost a whole
  release design.** A `net10.0-ios` library builds anywhere the `maui-ios` workload is installed —
  Windows included — because compiling C# against `Microsoft.iOS` reference assemblies needs no Xcode.
  Only producing an `.app` bundle and running it does; the MSBuild target that enforces the Xcode
  pairing (`_ValidateXcodeVersion`) is conditioned on `_CanOutputAppBundle`. Believing otherwise
  produced a three-job macOS release pipeline, drafted in full, for a problem that did not exist —
  deleted unbuilt when someone asked "does it actually need that?". **Both mobile packages build and
  are gated on Windows.** Check which artifact a platform constraint applies to before designing
  around it.
  - **So `dotnet build src/Shenora.iOS` works on Windows and is the fast way to clear compile errors** in
    real AudioToolbox/UIKit interop — only RUNNING needs the Mac. ⚠ A task note once said the package
    "cannot be compiled on Windows at all" and told the next session to batch diagnostics into one Mac
    round-trip; that tax was imaginary.
  - ⚠ **The SAMPLE is the opposite case, and this is what the confusion comes from:**
    `Shenora.Sample.Maui` sets `net10.0-ios` only under `$([MSBuild]::IsOSPlatform('osx'))`, so its
    `MainPage` `#if IOS` branch genuinely cannot be compiled or verified on Windows. Library vs app,
    again — which is why `dev.mjs mac build` and `shenora ios deploy` exist.
  - **An iOS probe does NOT need a commit.** `mac push` refuses a dirty tree by design, but it
    force-resets the Mac's clone anyway, so `scp`-ing the changed file straight into that tree and
    deploying is equivalent and free. Budgeting "one commit per iOS round trip" was a habit, not a rule.
- **Signing does not work over ssh** — an ssh login is a different AUDIT SESSION, so a login-keychain
  key fails `errSecInternalComponent`. Simulator builds sign ad-hoc and are unaffected; a device build
  needs the Terminal.app hand-off. **Confirmed 2026-08-06 by reaching it**: a device build now gets all the
  way through compile, link and bundling and dies only at `/usr/bin/codesign … errSecInternalComponent`.

- 🔴 **A DEVICE build is a different world from a simulator build, and this repo had never done one.** Three
  walls in a row on the first attempt (2026-08-06), each hiding the next:
  1. **The Xcode/workload pairing is FATAL on device where the simulator workaround is not.**
     `ValidateXcodeVersion=false` + `MtouchLink=SdkOnly` clears the up-front gate, but a device build runs
     the full linker over the SDK bindings — and bindings newer than the installed Xcode's SDK produce a
     wall of `MT4162` ("not available in iOS 26.2, introduced in 26.4"). **No flag bypasses it**: the
     bindings themselves reference APIs the SDK lacks.
     - **Fix without touching Xcode** (which may be capped by the macOS version): roll the iOS workload
       manifest back to one matching the installed SDK. `dotnet workload update --print-rollback` captures
       the current state; edit ONLY `microsoft.net.sdk.ios`; apply with `--from-rollback-file`. ⚠ It is
       MACHINE-WIDE — it changes every project on that Mac, so ask first. Both manifests were already on
       disk here, so nothing downloaded.
     - ⚠ **`DOTNET_SDK_WORKLOAD_MANIFEST_ROOTS` does NOT scope it** — tried; a Mac in workload-sets mode
       ignores it. There is no per-invocation override, so do not spend time hunting for one.
  2. **The live-activity shim path bug** (CHANGELOG, same day) — only a device build can expose it, because
     it needs a SECOND architecture to collide with the first.
  3. **Signing**, the wall above, which is the human's to clear.
  - **The meta-lesson is the launcher's POSIX lesson again:** "iOS builds and runs" had always meant "the
    SIMULATOR builds and runs". All three had been latent for months behind a green gate.
- **A capability probe must run in the SAME SHELL as the command it gates.** `mac.mjs`'s one-shot `ssh()`
  uses `bash -lc` — a LOGIN shell, so Homebrew is on PATH — while its persistent worker originally used a
  plain `bash -s`, which is not. So `command -v cliclick` answered NO over the worker and YES over ssh, for
  the same Mac with cliclick installed. The damage was silent: `tap` read the NO and fell back to System
  Events, which lands as focus-only on some web controls, so a tap "succeeded" and did nothing. Fixed by
  making the worker `bash -l -s`, and `tap` now PRINTS which mechanism it used. **The donor still has this
  bug** — do not port a probe without checking which shell will run it.
- **Two sibling repos on one machine will collide on a fixed dev PORT, and the collision is silent.**
  `mac mirror` defaulted to the donor's 7672; the sibling's mirror was already bound and pointed at the SAME
  simulator, so `/frame` answered with a valid screenshot and the smoke test passed against the other repo's
  server. Pick a distinct port (Shenora uses 7674) and make `EADDRINUSE` say WHY rather than throwing a raw
  stack — this is the family's one-dev-port-per-app rule (`webview2-hosting.md`) applied to devtools.
- **A default written in two places is a default that is not there.** The mirror's port constant said 7674
  while the dispatch passed `Number(rest[0]) || 7672`, so the constant described a port the tool never used.
  Let the parameter default be the only one.
- **`mac tap`/`swipe` take NATIVE screenshot pixels — not the size a tool DISPLAYED the PNG at.** A preview
  may downscale (1206×2622 shown at 920×2000) and label the display size; multiply back or every tap misses.
- 🔴 **"IT FAILS ON ONE EMULATOR AND PASSES ON THE OTHER" IS USUALLY DISK STATE, NOT THE PLATFORM.**
  `CONVERT` 404'd on a fresh AVD and passed on a MuMu instance, and the standing suspicion was the
  WebView version (133 vs AOSP 110) — the same suspicion that had already died twice. Nothing about the
  devices differed: the probe's fixture was staged by a LATER probe, so a cold install had no source file
  and every run after one found the copy the previous run left behind. **Uninstall before believing any
  per-device difference** (`adb uninstall`, `simctl uninstall`) — it is one command and it settles the
  whole class. And **a probe must stage what it needs**, never inherit another's side effect.
- 🔴 **AN INJECTED `click()` IS NOT A USER GESTURE, AND ON ANDROID THAT COSTS YOU A FALSE BUG REPORT.**
  Measured 2026-08-10 on WebView 133: `navigator.userActivation.isActive` reads `false` both before and
  after a script's `element.click()`, so Chromium refuses the page's unmuted `play()` — correctly. The
  same button plays when a REAL touch drives it (`adb shell input tap`) and when trusted CDP input does
  (`dev.mjs android tap`), on the same build. `UI-PLAY` was filed as an Android-vs-iOS shell divergence
  for a day on the strength of that refusal; iOS "passes" only because MAUI's WKWebView requires no user
  action, so the untrusted path is allowed there.
  - **Muted playback needs no gesture anywhere**, which is why the probe that MUTES the element passes on
    Android while the page's own unmuted button does not. Two probes, two policies, one page.
  - **So a harness cannot answer "does playback start" by clicking.** Report INCONCLUSIVE, never FAIL —
    the `CodecProbe` rule again: a query that could not be performed must not look like a negative result.
  - ⚠ `err=4 / size=0x0 / readyState=0` means **the bytes never arrived**, not that a codec is missing —
    a deliberately broken media URL reproduces it exactly. Worth knowing because that signature is what
    `MediaPlaybackRequiresUserGesture = false` produced, and it was read as a FORMAT problem.
- **`dev.mjs android eval "<js>"` and `android tap "<selector>"`** are the Android peers of
  `mac safari-eval`, over the webview's own CDP socket (`@webview_devtools_remote_<pid>`, Debug builds).
  ⚠ Pick the socket by the app's PID: a device has several debuggable webviews, and the wrong one answers
  perfectly well-formed questions about somebody else's page. `tap` is TRUSTED input, which is the whole
  reason it exists — see the bullet above.
- **Get page state as TEXT, not pixels — a screenshot cannot report a number, a header or an array**, and
  a `<video>` element reports only "no supported source" however it failed (three different DM1 causes
  were indistinguishable until a `fetch` ran in the page — D44). Three routes, in the order they were
  tried, because the cheap two are both closed:
  1. ⚠ **A page's `console.log` does NOT reach the device's unified log** on iOS — measured on the
     simulator with a tagged line and zero hits, so `mac log` cannot see it. Do not build on this.
  2. `mac safari-eval` is the general answer and needs a Homebrew package — see the warning below.
  3. **What works: send page log lines over the IPC pipe the app already has** and let the HOST write
     them to the device log (`samples/Shenora.Sample.Maui/PageDiagModule.cs`). Keep it SAMPLE-LOCAL: a
     kit that logged every inbound page message would be noisy and a privacy hazard.
     ⚠ Two recursion traps, both instant: the page's own `post()` calls `log()`, so mirroring inside
     `log()` loops through the transport; and the host ANSWERS every request, so the reply to a mirrored
     line mirrors again. Send on the raw channel without logging, and drop the replies by id — but
     **report the first one ONCE**, because a channel that silently swallows its own failure looks
     exactly like a channel that was never wired, which cost a full round of debugging here.
- **⚠ A device result is worthless until you confirm the app was RELAUNCHED.** `mac run` printing
  `build ok` is not the same as printing `running`; the launch step can be skipped or fail while the
  build succeeds, and then a fresh binary sits on disk while the SIMULATOR keeps executing the previous
  one. That reads exactly like "my change did nothing" — chased for a round here even though the missing
  line was right there in the output. The sibling's rule states the commit half of this ("never diagnose
  a Mac build result without checking what commit it built"); this is the launch half.
- **⚠ Some Mac operations cannot go over ssh AT ALL, and that is a tooling requirement, not a nuisance.**
  `sudo` needs a TTY and code signing needs a real login AUDIT SESSION, so the human must run those — and
  then has to get pages of output back. Serve the script over the LAN and have it POST its own transcript
  back (this repo's throwaway is `devtools/_relay.mjs`; the machine-specific findings live in
  `local/MAC-DIAGNOSTICS.md`). Four things that pattern earned: prove reachability with a `ping` route
  BEFORE handing a human a URL, or a firewall block looks like them doing it wrong; round-trip the whole
  loop over ssh first, which often produces the diagnosis with nobody touching anything; download-then-run
  rather than `curl | sh` for anything using sudo; and guard on the ACCOUNT, naming the one required, so a
  refusal is visible on the driving side instead of silent.
- **⚠ A custom scheme can be REGISTERED on iOS and on the desktop, but NOT on Android — so `app://` can
  never be media-capable there.** Asked by compiling (2026-08-04), because "register the scheme" is the
  obvious fix and it only works on two of three platforms:
  - **iOS**: `WKWebViewConfiguration.SetUrlSchemeHandler(handler, "app")` + `IWKUrlSchemeHandler` both
    compile. This is already how MAUI serves `app://` there, which is why the iOS page origin IS
    `app://0.0.0.1/` — nothing needs adding.
  - **Desktop**: `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` (see `webview2-hosting.md`).
  - **Android**: `Mono.Android` exposes NO scheme registration under any name. `shouldInterceptRequest`
    fires for any scheme — which is why a handler IS called for `app://` — but Chromium's media pipeline
    still refuses to decode from a non-standard scheme. Corroborating rather than coincidental: Google's own
    answer to the same problem, `WebViewAssetLoader`, serves over an **https virtual host**.
  **Consequence: write the media URL RELATIVE.** It resolves to `app://0.0.0.1/…` on iOS (the app scheme,
  registered) and `https://0.0.0.1/…` on Android (media-capable), so one page gets the right scheme on both
  without naming either. A literal `app://` is right on one shell and silently broken on the other.
- **⚠ Before changing ANY permission on a Mac's Homebrew tree, read `local/MAC-DIAGNOSTICS.md`.** That
  install is in a mixed-ownership state, a package install fails there in three DIFFERENT ways in
  sequence, and the donor's advice (`sudo chown -R` the tree) is wrong for the first two. The generic
  lesson, which is the part worth carrying: **each stage produces a different error, and the change in
  error is the evidence** — so diagnose to the next error rather than escalating the remedy. Also ask
  whether the tool is needed at all: two rounds went into a 2019-era Homebrew for a CDP bridge when the
  actual goal (read page state programmatically) can be met by having the PAGE post to the LAN relay.

- **⚠ MAUI's Android intercept path RE-DERIVES `Content-Type` and `Content-Length`, so supplying either in
  the header dictionary makes the page see it twice.** Measured 2026-08-05 with a route that varied only
  which headers the kit supplied — which is the technique worth keeping, because "the page saw two
  content-types" cannot otherwise be attributed to us or to the platform:

  | kit supplies | page receives content-type | page receives content-length |
  |---|---|---|
  | type + length | `x-probe, x-probe` | `0, 32` |
  | type only | `x-probe, x-probe` | `0` |
  | length only | `application/octet-stream` | `0, 32` |
  | neither | `application/octet-stream` | `0` |

  A custom `X-` header arrived exactly ONCE in every variant, so this is two well-known fields being
  re-derived, not blanket duplication. `Content-Length` must therefore NOT be sent (two differing values is
  an invalid message per RFC 9110 §8.6, and a consumer taking the first reads the body as EMPTY); the
  platform's own `0` cannot be removed and is what MAUI serves its own assets with. `Content-Type` must
  still be sent — MAUI reads it from the dictionary to set the native mime type, and there is **no
  `SetResponse` overload taking a content type alongside a dictionary**, so omitting it means
  `application/octet-stream` and no `<video>` will play. Fix lives in `MobileWebViewInterceptor.PlatformHeaders`,
  Android-only, because iOS's `NSHTTPURLResponse` path is different and unmeasured.

- **⚠ `e.Handled = true` WITHOUT calling `SetResponse` is a no-op on Android, not a broken response.** MAUI
  returns `platformArgs.Response` (null), and Android reads a null from `shouldInterceptRequest` as "not
  intercepted, serve it yourself". Worth knowing for its own sake, but it was learned as a **failed
  sabotage**: it was the obvious way to break a top-level navigation for a gate that had to be shown to
  fail, the gate reported PASS, and the honest reading was "my harness did nothing", not "the gate works".
  What DOES break a main-frame navigation is claiming it and answering — a 404 for `/` renders Chromium's
  error document (`title=` empty, ~5 nodes, `text=Not Found`), which is what
  `PageProbe.SabotageMainDocument` does. **Read what the harness actually did, not just the verdict.**

- **A page-state probe must prove the page actually NAVIGATED, not merely that it is healthy.** A reload gate
  that only checks `document.readyState === 'complete'` passes against the PRE-navigation document — the
  first version here passed in 515 ms and may never have left. Stamp a JS global immediately before
  navigating and require it to be GONE afterwards: a real navigation destroys the context, so its absence is
  the only evidence that the document under test is a new one.

- 🔴 **RECOGNISE YOUR OWN DOCUMENT — never try to recognise the platform's ERROR document.** The reload gate
  reported `RELOAD: PASS` on 2026-08-06 while staring at Chromium's error page, and it did so because the
  check was a BLOCKLIST — "an empty `title`, or `ERR_` in the body text". **Both signals failed at once and
  independently**, which is what makes this worth a rule rather than a fix:
  - the error page's title is **LOCALIZED and non-empty**. This CJK-locale device reported
    `title=网页无法打开`, so a test for `title=|` matched nothing — and on an English-locale device the same
    bug would have stayed hidden indefinitely.
  - the body text was truncated to 60 characters **one character before the underscore** (`net::ERR`), so
    the second signal missed too. **A diagnostic truncated mid-token is worse than no diagnostic**: it reads
    as evidence.
  The allow-check has no such holes — read the live page's `<title>` as a baseline BEFORE navigating and
  require the post-navigation document to carry it. Whatever the platform substitutes, it is not your page.
  ⚠ **The hole was in the ORIGINAL probe as well**; it never fired because the arm it guarded never failed.
  A gate is only verified on the paths its sabotage actually exercised.

- **A probe with an ARM must prove the arm was AIMED.** The same gate reloads at `/` and at `#/route`; if
  the fragment silently fails to take, the hash arm quietly re-runs the plain arm and passes. Assert the
  precondition (here `location.hash`) from the PAGE's own state rather than assuming the assignment worked,
  and report "misaimed" distinctly from "failed" — they need opposite responses.

- **Two arms beat one when a failure has to be ATTRIBUTABLE.** Plain-pass + fragment-fail is a platform
  defect and nothing else; both failing is a broken harness that has proven nothing about fragments. One
  extra reload buys the difference between a verdict and a guess.

- 🔴 **⚠ MAUI's Android asset mapping strips a QUERY and NOT a FRAGMENT, so every hash-routed page dies on
  RELOAD** — `https://host/#/library` looks for an asset named `#/library`, 404s with no body and no MIME,
  and Chromium reports `net::ERR_INVALID_RESPONSE`. Reproduced here 2026-08-06 on WebView 110 after being
  filed by the first adopter, and it reproduces **with the kit entirely absent** (A/B on one binary, the
  arm with no interceptor constructed fails identically). `MobileWebViewInterceptor.RepairDocumentRequest`
  owns the fix — the interceptor is the ONLY seam that sees the document request while it can still be
  answered, since the request fails before any page script runs.
  - **This is why the gate was green for a real bug for two days.** The reload probe passed on Chromium
    110 *and* 133 because it reloaded at `/`. It was aimed one character short of the defect. **When a
    report will not reproduce, suspect the SHAPE of your reproduction before you suspect the report.**
  - 🔴 **iOS had the same trigger and IS REPAIRED BY THE SAME CODE since 2026-08-09** — the guard is pure URL
    shape with no platform test and `RepairDocumentRequest` runs in unguarded shared code, so both shells get
    it. iOS *does* issue the document request (`app://0.0.0.1/#/probe-route`); this was believed
    unrepairable for days because the repair's first IMPLEMENTATION broke — a blocking bundle read inside
    the handler, which deadlocks the main thread there — and the IDEA was discarded with it.
    🔴 **A hypothesis discarded because its first implementation failed was never tested.**
    The DIAGNOSTIC lesson below stands regardless, because WKWebView still keeps the PREVIOUS page on
    screen when a provisional navigation fails:

    ```
    plain    after reload: stamp=fresh|nodes=56|title=Shenora mobile sample   ← navigated, came back
    fragment after reload: stamp=STALE|nodes=74|title=Shenora mobile sample   ← never left
    ```

    **Everything except `stamp` says the app is fine** — right title, a bigger DOM than the fresh document
    (it is the fully-interacted original), body text intact. So the PRE-NAVIGATION STAMP is the only
    discriminator on this shell; `nodes` and `title` are there to show you what a healthy-looking corpse
    looks like, not to detect it. "It rendered" is not evidence, and neither is a screenshot.
    Applying the Android repair here was measured (by the adopter) to make it worse: no document request at
    all, and `EvaluateJavaScriptAsync` stopped answering.

- 🔴 **A FAILING `EvaluateJavaScriptAsync` KILLS THE APP on iOS, and your `try/catch` around the `await`
  cannot catch it.** MAUI's `HybridWebViewHandler.MapEvaluateJavaScriptAsync` runs the evaluation as a
  fire-and-forget task and rethrows the failure onto the SYNCHRONIZATION CONTEXT (`Task.ThrowAsync` →
  `NSAsyncSynchronizationContextDispatcher.Apply`) — a different stack from the one awaiting it. It lands
  on the UI thread with nothing above it, becomes an unhandled managed exception, and aborts the process
  with SIGABRT. Measured 2026-08-06: the sample died ~7 s after launch, before one verdict was logged, and
  the guard in the probe's own `EvaluateAsync` never saw it. Two things are needed together:
  - **Flatten every script to ONE LINE.** WKWebView rejected the multi-line ones outright —
    `SyntaxError: Unexpected EOF` at line 1 — and a parse failure happens before any JS runs, so no in-page
    guard can help. ⚠ Consequence: a `//` comment inside a script then swallows the rest of the program.
    Keep script commentary in C#, outside the string.
  - **Wrap the script in a JS `try/catch`** so a RUNTIME error returns a value instead of throwing. Every
    probe script is an expression, so an IIFE wrapper preserves its value.
  - ⚠ **Attribute before you fix**: the pre-change commit was run on the same simulator and crashed
    identically, which is what proved the crash was latent rather than caused by the change in flight.

- **iOS loses BACKSLASHES on the way to WKWebView.** `.replace(/\s+/g, ' ')` arrived as `/s+/g` and
  replaced every letter "s" in the page text with a space — `"shell"` read `" hell"`, `"desktop"` read
  `"de ktop"`. Cosmetic, but it reads as a corrupted PAGE rather than a corrupted probe, which is the
  expensive kind of wrong. Android is unaffected, so a script proven there can still be mangled here. Write
  probe JS with no backslash at all: `String.fromCharCode(10)` instead of `'\n'`, and prefer
  `split().join()` over a regex.

- **`WebViewResourceRequest.Uri` CARRIES A FRAGMENT, and the safe reading hides it.** `AbsolutePath` is
  correct (`/`) where `ToString()`/`PathAndQuery` mis-resolve — but because `AbsolutePath` reports `/` for
  `https://host/#/library`, logging it is what convinced the adopter the URL was fragment-free and produced
  a defect filed against the wrong component. **Log the whole `Uri` when a document request surprises you.**

- **A page-side probe cannot report its own death** (adopter, 2026-08-06). "The reload failed" and "the
  reload succeeded but the bridge never came back" are the same silence, so only a NATIVE-side witness
  survives the event it is measuring. A probe that lives in the page is the obvious first design and is
  wrong for this whole class of question — which is why `PageProbe` drives from the host.

- **There IS an eval on iOS: `EvaluateJavaScriptAsync`, from native code** — no `ios-webkit-debug-proxy`,
  no bridge, no CDP. Worth knowing next to the "get page state as TEXT" routes above, whose first two are
  both closed: it keeps working precisely when the page or the bridge is the suspect, which is when it is
  needed. If the kit ever wants an observable iOS shell, that is the seam.

- **`Application.Current` is null inside `CreateMauiApp`** (`builder.Build()` makes the MauiApp, not
  the Application) — use `Dispatcher.GetForCurrentThread()`. And the page MUST load the platform's own
  bridge script or `window.HybridWebView` never exists, the page renders fine, and the host sits
  waiting for a handshake that cannot arrive.

- **A SWIFT/SwiftUI app extension (widget, Live Activity) CAN ship in a .NET iOS app, and needs no second
  build system.** Measured end-to-end on 2026-08-04 before any of it was designed on; every claim below is
  from that run, not from documentation.
  - **`AdditionalAppExtensions` is first-class in-SDK support**, not a community hack:
    `_ExtendAppExtensionReferences` (Xamarin.Shared.targets) injects a prebuilt `.appex` into the embed and
    codesign lists, and it is reached from `_CompileToNativeDependsOn`, so it runs on every app build.
    `_CopyAppExtensionsToBundle` dittos it to `.app/PlugIns/`, deletes the stale signature, and re-signs
    with the app's identity. ⚠ **Check the target is REACHED, not just present** — a target that exists
    and is skipped looks identical to one that ran, and an incremental check on the wrong Inputs is how it
    gets skipped (see the `$(ApplicationId)` plist trap below).
    Metadata: `Include` = a directory, `BuildOutput` = a subdirectory under it, `Name` = the appex name
    without extension (the path built is `%(Identity)/%(BuildOutput)/%(Name).appex`), plus optional
    `CodesignEntitlements`.
  - **`swiftc` alone builds the widget** — no `.xcodeproj`, no `xcodebuild`. `-parse-as-library` is
    required (`@main` in a single file otherwise collides with swiftc's top-level-code mode), plus
    `-target <arch>-apple-ios16.2-simulator`, `-sdk $(xcrun --sdk iphonesimulator --show-sdk-path)` and
    `-framework ActivityKit -framework WidgetKit -framework SwiftUI`. That this works is what keeps a
    devkit from having to own a second build system.
    ⚠ Known rough edge, not yet fixed: the LINK step emits `clang: warning: using sysroot for 'MacOSX' but
    targeting 'iPhone'` — it produced a binary the OS accepted, but the linker is getting the macOS
    sysroot and a real implementation should pass the SDK through explicitly.
  - **The appex's `CFBundleIdentifier` MUST be prefixed by the container app's**, `CFBundlePackageType` is
    `XPC!`, and a WidgetKit extension declares only `NSExtensionPointIdentifier =
    com.apple.widgetkit-extension` — no principal class, because the `@main WidgetBundle` is the entry.
    A higher `MinimumOSVersion` than the app's is normal (ActivityKit is 16.1+); the OS just does not load
    it on older systems.
  - **Verify with the OS's own registry, not the file listing.** `xcrun simctl spawn <udid> pluginkit
    -mAvvv` reported `com.shenora.sample.maui.islandprobe(1.0)` with `SDK =
    com.apple.widgetkit-extension`, which is iOS saying it accepted and classified the extension —
    strictly stronger evidence than "the .appex is in PlugIns". `codesign --verify --deep` on the app then
    confirms the nested code did not break the container's signature.
  - **ActivityKit has NO Objective-C surface** — its `Headers/ActivityKit.h` is an empty include guard and
    the entire API is in `ActivityKit.swiftmodule`. So the start/update/end LIFECYCLE needs a Swift shim
    too, not only the view. (WidgetKit does ship headers, but declaring a widget is a Swift
    result-builder DSL regardless.)
  - **A Live Activity really STARTS from C#, and the system launches the widget to render it** (second
    probe, same day). `Activity.request` returned an id, three `update`s were accepted, and
    `liveactivitiesd` logged `Created activity` / `Starting activity … state: active`. Then `chronod`
    launched the extension through ExtensionKit — `Executing launch request for
    xpcservice<…islandprobe>` — so the whole chain works, not just the build.
    - **The app-side shim is a STATIC Swift library, and this is the part with a real recipe.** Activities
      are started BY THE APP, so Swift has to be linked into the app too, not only into the appex.
      `swiftc -emit-library -static -parse-as-library` produces a `.a`; `@_cdecl` gives each entry point an
      unmangled C symbol; `<NativeReference Include="…/lib.a"><Kind>Static</Kind></NativeReference>` links
      it; and C# reaches it with `[DllImport("__Internal")]` — `"__Internal"` because the symbols end up in
      the app binary itself. Verified with `nm` on the app executable, not inferred.
      Compile the shim at the APP's minimum OS and guard ActivityKit with `if #available(iOS 16.2, *)`; a
      static library built for a higher floor than its host is an argument nobody needs.
    - **⚠ `-module-name` MUST MATCH between the appex and the shim.** ActivityKit pairs a running activity
      with a widget by its `ActivityAttributes` TYPE, and a Swift type's identity includes its module — so
      the same shared source compiled into two different module names declares two different types, the
      pairing silently fails, and every API call still reports success. Hit live.
    - **⚠ Never hand-delete part of an app bundle to force a rebuild.** Doing that produced
      `SIGKILL (Code Signature Invalid)` / `CODESIGNING Invalid Page` at launch, which reads like a
      provisioning problem and is really "the bundle no longer matches its signature". `rm -rf bin obj` and
      build clean instead. Cost: one full build; the alternative cost an hour chasing a phantom.
      **This has now bitten twice, the second time from a DEVTOOL** — `dev.mjs mac build`'s link check
      deleted the `.app` but not `obj/`, so its follow-up rebuild shipped an inconsistent bundle and every
      launch died instantly. Both times the build reported success throughout, which is the whole problem:
      a partially-deleted bundle is a RUN-time failure that no build output mentions. If a script deletes
      anything under `bin/`, it must clean `obj/` too — and something must actually LAUNCH the app
      afterwards, or the corruption ships.
    - **⚠ An edited `Platforms/iOS/Info.plist` does NOT reach the app on an incremental build.**
      `_WriteAppManifest` merges `obj/**/AppManifest.plist` — a COPY of the source — with generated
      fragments, and its Inputs/Outputs are satisfied by that copy. The built plist's mtime moves while its
      CONTENT stays stale, which is the worst possible symptom. Delete the intermediate, or build clean.
    - **Still not visually confirmed on a simulator, and the reason is informative:** the activity's
      `sceneTargets` came back as `[lockscreen: widget(...)]` only — no Dynamic Island destination — so an
      unlocked simulator shows an empty pill however long you wait. That is a SIMULATOR presentation limit,
      not a limit of this approach; the extension is launched and the activity is active either way. Settle
      it on a real device before claiming the Island renders.
      🔴 **NO LONGER TRUE — the iOS 26.3 / iPhone 17 Pro simulator RENDERS the Dynamic Island** (measured
      2026-08-09; the compact pill draws the widget's leading and trailing regions). The claim above was
      measured on an iOS 17-era simulator and was carried forward for a fortnight, costing a device
      round-trip per visual change. **This is the loop for designing one:**
      ```
      shenora ios deploy --simulator 'iPhone 17 Pro'
      sleep 5                                   # the activity must still be RUNNING
      xcrun simctl launch booted com.apple.springboard   # background the app or there is no Island
      xcrun simctl io booted screenshot /tmp/x.png
      ```
      ⚠ **Two traps, both hit immediately.** The Island only shows while the app is BACKGROUNDED, and it
      shows nothing once the activity has ENDED — a blank pill after a probe finishes looks identical to a
      widget that failed to draw, and I read one as the other. Screenshot inside the activity's lifetime,
      and check the log for its VERDICT line before concluding anything about pixels.
    - 🔴 **A SWIFT `print()` DOES NOT REACH `log show` ON THE SIMULATOR — use `simctl launch --console-pty`.**
      Every `[SHENORA]` line visible through `xcrun simctl spawn booted log show` is tagged
      `(libSystem.Native.dylib)`: that is .NET's `Console` being routed into the unified log. Swift's
      `print()` goes to plain stdout and is simply absent there.
      ⚠ **This cost six runs and produced a confident wrong diagnosis.** A widget diagnostic printed
      nothing, so I concluded C# was sending an empty payload, and then "fixed" progressively larger
      things — a stale static library, the appex, a full clean of the sample. Captured properly with
      `nohup xcrun simctl launch --console-pty booted <id> > /tmp/x.txt 2>&1 & sleep 30`, the very first
      line read `layout: 229 bytes, decoded=true` — the path had been working the whole time.
      **This is the repo's own rule arriving from a new direction: abandoning (or trusting) a broken
      instrument is the error, because every experiment after it answers a question nobody asked.**
    - 🔴 **NEVER set `.foregroundStyle(.primary)` in a Live Activity view.** The Island is always dark, but
      `.primary` resolves against the WIDGET's colour scheme — a light-scheme render draws black text on a
      black pill, so the region reserves its space and shows nothing, which reads exactly like a layout
      that was never applied. Say nothing and inherit; `.secondary` is safe because it is scheme-aware.
      ✅ **Settled 2026-08-09 on an iPhone 17 Pro over USB — it RENDERS**, in both Island regions.
      🔴 **The proof was designed so it could FAIL: the symbol and tint were deliberately non-default**, so
      a widget that ignored the config would have looked plausible and been wrong. When the thing under
      test is "does it read my configuration", a screenshot of the DEFAULT proves nothing.
      🔴 **BUT THE COMPACT ISLAND DOES NOT REPAINT ON UPDATE — owner-observed, same session.** All three
      updates were accepted AND applied (`update applied: progress=1.00 state=active`), and the pill still
      read `67%`. **I concluded from that log that nothing was stuck, and the owner corrected me.** A Live
      Activity update has three separate outcomes — accepted, applied, and REPAINTED — and this shim can
      only see the first two. ⚠ **Never close a rendering question with a log line**; it is the same
      mistake the `LC_MAIN` diagnosis made, one layer up.
    - A device build additionally needs entitlements for the appex — the simulator build logs
      `No entitlements set for …IslandProbe.appex` and that warning is expected there.

### 🔴 THE APP→WIDGET WIRE HAS **TWO** LEGS, AND THE APP-SIDE DIAGNOSTIC ONLY WATCHES THE FIRST

The app-described layout tree rendered NOTHING in the Island while the kit's own default icon drew fine
beside it. Two independent bugs, both silent, both on the leg nothing was looking at (measured 2026-08-09,
simulator):

1. **`ActivityKit` ENCODES the whole `ActivityAttributes` to hand it to the widget process.** The Swift
   mirror's `encode(to:)` was a stub writing `{"kind":"unknown"}` — "the kit only ever decodes this" — so
   the layout was deleted on the way IN and the widget rendered `EmptyView`. **A `Codable` mirror needs
   both directions even when your code only ever calls one; the framework calls the other.**
2. **`System.Text.Json` writes an enum as a NUMBER by default**, the Swift side does
   `decode(String.self)`, that fails, and the interpreter falls back to its own default. Every
   `Horizontal` stack laid out VERTICALLY and every role rendered as body text — a plausible-looking
   wrong layout, which is worse than a blank one. Fixed by putting `[JsonConverter(typeof(
   JsonStringEnumConverter<T>))]` on the enum TYPES: as a serializer OPTION it has to be repeated at every
   call site and the one that forgets fails exactly like this. Use the GENERIC converter — iOS is AOT.

⚠ **The instrument said `layout: 229 bytes, decoded=true regions=[… ct=true]` throughout, and it was
TRUE** — about the C#→shim leg, which was never broken. A diagnostic that watches one leg of a two-leg
path reads as coverage of the whole path. **Ask which leg the instrument is on before trusting a green
line**, and note that the widget runs in ANOTHER PROCESS whose `print()` never reaches the app's console.
Both halves are pinned by `LiveActivityMirrorTests` (kinds, enum member names, property keys, and the
enums-as-names wire), sabotage-verified in both directions.
🔴 **BUT THOSE NINE TESTS READ THE SWIFT AS TEXT — do not mistake them for proof.** They assert the two
sides LOOK like they agree and never EXERCISE the agreement, which is why both defects above reached a
phone. **What exercises it is a GOLDEN PAYLOAD, and it is two commands:**
`dev.mjs verify` asserts the serialized JSON and a described tree (`LiveActivityGoldenTests`, Windows,
every push), and **`node devtools/dev.mjs mac layout-check`** feeds those same two committed files to the
real decoder under a bare `swiftc` — no simulator, no device, no SDK, ~5 s — requiring the same tree back
AND a lossless re-encode, which is the ActivityKit leg the stub broke. ⚠ **CI has no macOS runner**, so
run the Mac half yourself after touching either side, and say which half you ran.

### The simulator design loop, and where it stops

`shenora ios deploy --simulator 'iPhone 17 Pro'` → relaunch → `simctl launch booted com.apple.springboard`
→ `simctl io booted screenshot` → `sips -c 260 900 --cropOffset 30 150` to crop the pill. **Screenshot
INSIDE the activity's lifetime and check the log's start line first** — a blank pill after it ends looks
identical to a widget that failed. Two more things measured the hard way:
- ⚠ **`-p:MtouchLink=SdkOnly` is not optional on the sample.** Without it, full trimming makes the app
  die on RELAUNCH with `Token … is not valid in the scope of module Microsoft.iOS.dll` — which reads as a
  corrupt bundle, not as a linker setting.
- ⚠ **The system's own audio indicator takes a compact region** once the page starts playing, so a
  screenshot taken late shows the volume glyph where the app's trailing region was. Shoot early.
- ✅ **THE LOCK-SCREEN BANNER IS REACHABLE — drive the Simulator's own MENU, not a keystroke** (settled
  2026-08-09, once the owner granted the two permissions below):
  ```
  osascript -e 'tell application "Simulator" to activate' \
            -e 'tell application "System Events" to tell process "Simulator" \
                to click menu item "Lock" of menu 1 of menu bar item "Device" of menu bar 1'
  ```
  ⚠ **`keystroke "l" using command down` does NOT lock it**, and a menu-item click is more robust anyway
  (it survives a shortcut change). `Device` also carries `Home`, `Rotate`, `Siri`, `Shake`, `App Switcher`.
- 🔴 **TWO macOS PERMISSIONS, AND THEY ARE DIFFERENT ONES.** *Automation* lets `osascript` talk to System
  Events at all — without it every call TIMES OUT after ~60 s with `AppleEvent timed out (-1712)`, which
  reads like a hung Simulator. *Accessibility* is what allows sending keystrokes and synthetic mouse
  events — without it you get `osascript is not allowed to send keystrokes (1002)`. Granting the first
  and stopping looks like progress and changes nothing.
- 🔴 **`booted` IS AMBIGUOUS THE MOMENT TWO SIMULATORS ARE UP, and it fails SILENTLY.** With an iPhone 17
  Pro and an iPhone 16 Pro both booted, `simctl io booted screenshot` and the Simulator's frontmost
  window resolved to DIFFERENT devices — so a Lock click landed on one device and the screenshot came
  off the other, and the lock screen "did not work" twice. **Pass an explicit UDID to every `simctl`
  call**, or `simctl shutdown` the spare first. The tell is a screenshot that looks untouched by an
  action that reported success.
  ⚠ **`simctl shutdown` + `boot` also lost the installed app** on that device; `get_app_container`
  answered "No such file or directory" and every `launch` failed with
  `FBSOpenApplicationServiceErrorDomain code=4`. Re-deploy rather than debug the launch — and note that
  code=4 ALSO means "the device is locked", so the same error covers two very different causes.
- ⚠ **A locked simulator refuses `simctl launch`**, and `Device ▸ Home` does not unlock it. Verify the
  screen state with a screenshot before concluding anything about a launch failure.
- 🔴 **THE EXPANDED ISLAND IS STILL NOT AUTOMATED, and the blocker is the WINDOW rather than the press.**
  It needs a press-and-HOLD, which AppleScript cannot do (`click` has no duration) — a compiled
  `press <x> <y> <hold-ms>` Swift helper posting CGEvents lives at `~/shenora-tools/press` on the Mac and
  works. What does not work is aiming it:
  - ⚠ **`simctl boot` does NOT open a device window.** Shutting a device down closes its window, and
    booting it back only makes it available to `simctl` — `open -a Simulator` is what brings the window
    back. The Simulator app can be frontmost, with a booted device, and no window on screen at all.
  - ⚠ **`window 1`'s reported frame can name a window that is not in `screencapture`'s output** — it sits
    on another Mission Control SPACE, and `screencapture` only sees the active one. The tell is a
    screenshot of bare desktop at coordinates System Events swears the window occupies.
  - **The points→pixels ratio is NOT the panel's.** This Mac reports a 2880×1800 built-in panel, runs a
    1680×1050 point space, and `screencapture` writes 3360×2100 — so the factor between AppleScript
    points and captured pixels is 2, and the panel resolution is a red herring.

  **Do not iterate on this blind.** A long press on a real phone takes five seconds and settles it.
- ⚠ **A/B THE STYLE, DO NOT REASON ABOUT IT.** A per-region type scale (`compact` → one size down) was
  written on the theory that the pill clips oversized text; re-run with the same state and the same tree,
  `.title3` and `.body` came out pixel-identical, and the flag was deleted. The Island already constrains
  what it hosts.

- **`staleDate: nil` does NOT stop the Island repainting, and the kit now sets no `staleDate` at all**
  (owner's call, 2026-08-10 — a 60 s horizon lived on `update` for one day). `staleDate` marks content
  out of date for `context.isStale`; it is not a repaint trigger, and nothing in the kit read the flag.
  **A pair of gates holds it:** `The_shim_sets_no_staleDate_on_either_call_site` fails by file and line if
  one comes back.
  🔴 **MEASURED MECHANICALLY — `node devtools/dev.mjs mac island-watch`, which is the point.** With the
  horizon: 8 frames, 5 distinct. Without it, on a fresh install of the changed source: **7 frames, 7
  DISTINCT** (values stepping, one caught mid-digit-animation). The earlier matrix was read by EYE from
  screenshots — the instrument that twice let an interpretation get written down as a measured fact. A
  sha256 cannot do that.
  ⚠ **What is still unconfirmed is HARDWARE.** `nil` is the configuration the original 2026-08-09 frozen-pill
  observation was made in; it goes back because that symptom has a better explanation (the `encode(to:)`
  stub and enums-as-numbers, both fixed the same day). There is no `devicectl` screenshot, so a device
  confirmation is a human reading — designed as look-whenever: start 11%, one update to 55%, leave it
  running, read the FINAL value (`55%` repaints, `11%` frozen).
  ⚠ **Two traps the run hit, both of which make a live pill look frozen:** the system AUDIO indicator
  takes the compact trailing region as soon as the sample's media probes start (so shoot early, or the
  last frames are all identical and say nothing), and a relaunch STACKS a second activity unless you
  `simctl uninstall` first — the pill then shows a value from the previous run.
- **`Activity.request` is refused while the app is BACKGROUNDED** — a plain null handle. Start the
  activity in the foreground, then background the app to see the pill.
- **Check `activityState` in the update log before believing any Island reading.** ActivityKit silently
  ignores an update to an `.ended`/`.dismissed` activity, so `state=dismissed` means the pill cannot have
  repainted whatever else was true — one device run read that way and would otherwise have been recorded
  as a repaint failure.
- **Disable the sample's media probes for any Island measurement**: once page audio starts the system
  audio indicator takes the compact TRAILING region, which looks identical to a frozen value.
- **An activity outlives its app**, so a previous run's is still up when the next starts — `simctl
  uninstall` + `install` is the only reset, and a pre-launch screenshot proves it worked.

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
    privilege, so the return trip does not need a button. ⚠ Android only; iOS unmeasured.
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
- ⚠ **An `<audio>` element keeps playing while backgrounded on iOS** — given `UIBackgroundModes: [audio]`
  and an active `AVAudioSession(.Playback)`. Measured on an iPhone 17 Pro: 16.01 s of audio across a
  16.0 s background window.
  🔴 **THAT WINDOW IS 16 SECONDS AND ANDROID DIES AT ~15.4, so treat "keeps playing" as PROVEN FOR 16 s
  AND NO LONGER.** The two numbers are suspiciously close. Nobody has run the iOS case for minutes, which
  is what an adopter means by background playback — do that before promising it. **`<video>` pauses by design** — the track cannot render — so a
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
