# Mobile shells — what a second and third target actually cost

The mobile shell runs on Android and iOS — shipped as `Shenora.Android` + `Shenora.iOS` from one
shared source tree — and needed **no `#if` anywhere** to do it. That
is the good news and it is also the trap: because the C# ports for free, every real cost lands
somewhere else — in the PAGE, on the BUILD HOST, or in the device harness. This rule is those costs.

🔴 **INVARIANTS ONLY, AND ONLY THE ONES ABOUT CHANGING THE SHELL.** Two halves have left, both because
this file kept being the biggest thing a mobile task loaded:

- **What the platforms were MEASURED to do** → [`docs/design/mobile-shells.md`](../../docs/design/mobile-shells.md)
  (D77). A measurement is evidence for a design; it is not a rule, and mixing them made this the largest
  file in the knowledge base by a factor of six. **If you are about to CHANGE the shell, read it too.**
- **How to RUN something on a device** → [`mobile-harness.md`](mobile-harness.md) (2026-08-17). Driving a
  phone, a simulator or the Mac build host is a different task with a different audience, and 321 lines
  of ssh, screenshots and probe discipline were loading on every C# change that touched `Shenora.Mobile`.

What is left here is what a code change must not break.

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
- 🔴 **A MAUI ACTIVITY DOES NOT ROUND-TRIP ANDROIDX INSTANCE STATE, so nothing built on
  `ActivityResultRegistry` (or androidx-fragment state) can survive recreation.** Measured 2026-08-17:
  the recreated activity's bundle carried `android:viewHierarchyState`/`android:fragments` and NO
  `androidx.lifecycle.BundlableSavedStateRegistry.key`, so the registry's restored request-code map is
  empty and an arriving result falls through the legacy path unseen. **The FRAMEWORK's own routing
  survives** — `OnActivityResult` fired on the recreated instance with the original request code —
  which is why `ActivityResultRelay` owns its codes over `StartActivityForResult` and needs the
  adopter's one-line forward (`docs/guides/mobile.md`). Anything new that awaits an activity result
  must go through that relay, not the registry.

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
  - **`<audio>` already playing CONTINUES** when the app is backgrounded — for MINUTES, not a grace period
    (measured; the number is in `docs/design/mobile-shells.md`). What is restricted is STARTING new
    playback while backgrounded or locked. ⚠ Android is the opposite: its page element dies in ~15 s, so
    a page that relies on this works on one shell only.
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

## Gotchas / traps — the SHELL and the PAGE

⚠ The device HARNESS half of this section moved to [`mobile-harness.md`](mobile-harness.md) on
2026-08-17: driving a phone or simulator is a different task from changing the shell, and one file
serving both meant a C# change loaded several hundred lines about ssh and screenshots. Read that one
when you are RUNNING something rather than changing it.

- ⚠ **An Android API that is OBSOLETE above this kit's floor is a BUILD ERROR here, not a warning.**
  `Bitmap.CompressFormat.Webp` is obsoleted on API 30+ while the floor is API 21, and `CA1422` is an error
  in this repo — so anything encoding an image must handle both forms or use JPEG. The shape generalises:
  a floor below the deprecation means BOTH branches must compile, and the analyser will not let you pick
  one. (Found while sizing thumbnails, D43.)

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

