# Mobile shells — what a second and third target actually cost

The mobile shell runs on Android and iOS — shipped as `Shenora.Android` + `Shenora.iOS` from one
shared source tree — and needed **no `#if` anywhere** to do it. That
is the good news and it is also the trap: because the C# ports for free, every real cost lands
somewhere else — in the PAGE, on the BUILD HOST, or in the device harness. This rule is those costs.
Earned across the Android port and the iOS port (both 2026-08-02).

## The rules

- **Write the page for the SUPERSET of shells, never the one you tested on.** Identical markup looked
  correct on an Android emulator for a whole session and put its heading under the status bar and the
  Dynamic Island on the first iPhone run. An emulator has no safe-area insets to violate, so the bug
  cannot appear there. Use `env(safe-area-inset-*)` with `viewport-fit=cover` — both collapse to
  nothing where there are no insets, so it costs the desktop shell zero. The same law covers strings:
  a shared bundle means `hello from android` eventually appears in an iPhone screenshot.
- **The runtime identifier must match the TARGET, and the failure always names the wrong step.** Twice
  in the same shape now: `android-x64` against an arm64-only build fails at INSTALL with
  `INSTALL_FAILED_NO_MATCHING_ABIS`, and `iossimulator-x64` against `-arm64` fails the same way. The
  BUILD succeeds both times, so the error reads as packaging rather than architecture. Ask the target
  what it is (`dev.mjs mac` reads the Mac's `uname -m` over ssh); never assume the dev machine matches.
- **Prove it on the device, and say which half you proved.** A shell that compiles is not a shell that
  runs; `dev.mjs android` and `dev.mjs mac` exist so a claim about either can carry a screenshot.
- **A webview on both shells does NOT mean the auxiliary-SESSION stack ports (D39).** `StreamingSession`
  and friends rest on CDP (screencast, device metrics, OS-level input replay), which neither shell
  exposes in-process — iOS has no CDP at all. The trap is that a port IS buildable behind the same
  interface (frame-polling + `evaluateJavaScript` synthetic DOM events) and is materially weaker:
  polled, and `isTrusted: false`, which is exactly what the pages that stack exists for reject. Nothing
  needs stubbing, because the stack is in `Shenora.Windows` and portable logic cannot name it. Read D39
  before writing any of it — the sanctioned mobile answer per intent is there.

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
  needs the Terminal.app hand-off.
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
    with the app's identity. ⚠ **Check the target is REACHED, not just present** — that distinction is the
    whole point of the presence-vs-content audit in `docs/archive/tasks.md`.
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
