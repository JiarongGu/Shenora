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
