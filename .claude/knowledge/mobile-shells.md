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
  - ⚠⚠ **A CORRECTION WORTH KEEPING (2026-08-07):** this entry first claimed webview background audio was
    impossible without Apple-internal entitlements (`com.apple.multitasking.*assertions`). **That is
    false.** It came from ONE Apple-forum thread about a specific iOS 13 regression, generalised into a
    platform law after a single failing test — which was a `<video>` element, i.e. the one case that
    legitimately pauses. Two lessons, and the second is the one that matters: a single negative result on a
    device is not a platform limit, and **a search result describing an old OS-version bug is not a
    statement about the current OS.**

## Gotchas / traps

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
- **Get page state as TEXT, not pixels — a screenshot cannot report a number, a header or an array**, and
  a `<video>` element reports only "no supported source" however it failed (three different DM1 causes
  were indistinguishable until a `fetch` ran in the page — D44). Three routes, in the order they were
  tried, because the cheap two are both closed:
  1. ⚠ **A page's `console.log` does NOT reach the device's unified log** on iOS — measured on the
     simulator with a tagged line and zero hits, so `mac log` cannot see it. Do not build on this.
  2. `mac safari-eval` is the general answer and needs a Homebrew package — see the warning below.
  3. **What works: send page log lines over the IPC pipe the app already has** and let the HOST write
     them to the device log (`samples/Shenora.Sample.Maui/PageDiagFacade.cs`). Keep it SAMPLE-LOCAL: a
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
  - 🔴 **iOS has the same trigger and different machinery, is NOT repaired, and REPRODUCES HERE** (measured
    2026-08-06 on the simulator, after the probe was made survivable — see the two traps below). The reload
    never produces a second document at all, and WKWebView keeps the PREVIOUS page on screen when a
    provisional navigation fails. The evidence is one field:

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
    - A device build additionally needs entitlements for the appex — the simulator build logs
      `No entitlements set for …IslandProbe.appex` and that warning is expected there.

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
