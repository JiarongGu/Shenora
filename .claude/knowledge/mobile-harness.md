# The device harness — driving a phone, a simulator and the Mac build host

Everything here is about RUNNING something on a device: `dev.mjs android|mac`, the build hosts, and
the probe discipline that makes a device result mean anything. **Split out of `mobile-shells.md` on
2026-08-17** — that file is the shell and page INVARIANTS, and a session changing `Shenora.Mobile`
C# had to load all of this to reach them.

⚠ **If you are changing the shell rather than running it, you want**
[`mobile-shells.md`](mobile-shells.md); the measured platform numbers are
[`docs/design/mobile-shells.md`](../../docs/design/mobile-shells.md) (D77).

🔴 **Every entry cost a real device run.** None of it is reasoning — where a line says "measured",
the alternative was believed and turned out wrong.

## Gotchas / traps

- 🔴 **AN UNRESPONSIVE EMULATOR HANGS THE ANDROID *APP* BUILD FOR EVER — `adb kill-server` fixes it in
  seconds.** The MAUI app build's `GetPrimaryCpuAbi` runs `adb shell getprop` against every device adb
  lists, with **no timeout**, so a wedged emulator still listed as `device` stalls it at 0 % CPU. `verify`
  builds the solution, so it takes the whole release gate with it.
  - **The tell that it is THIS and not a slow machine:** the Android LIBRARY builds in ~7 s beside the
    hang, because only an APP queries the device. Sample the build's CPU — a 0 s delta over 3 s means
    blocked, not slow — then `dotnet build … -v diag` and read the last `Task "…"` line.
  - ⚠ A qemu in this state is **unkillable** and stays so: `Stop-Process -Force` and `taskkill /F /T` both
    refuse (`taskkill` even says "no running instance") while `tasklist` still lists it — orphaned parent,
    ONE thread, thousands of handles, stuck in a kernel wait.
  - ✅ **DEPLOYING does NOT need a reboot — the AVD lock is PER-AVD, so make a SECOND one, then DELETE
    it.** `avdmanager create avd -n <name>-tmp -k "system-images;android-36;google_apis;x86_64" -d pixel_6`
    (needs `JAVA_HOME`, which `dev.mjs android-jdk` prints) then `emulator -avd <name>-tmp -port 5556`. It
    boots beside the zombie and `adb` lists it as a normal device.
    🔴 **`-d` is not optional and its absence is silent:** without a device profile the AVD gets **96 MB**
    of RAM and density 160, which boots and runs and is nobody's phone. ⚠ `-read-only` alone does NOT
    work either: the emulator requires it on EVERY instance, and the zombie was not started that way.
    **Clean up when done** — `adb -s emulator-5556 emu kill`, then `avdmanager delete avd -n <name>-tmp`.
    One AVD per project; a spare is also what makes `booted` ambiguous for every later `adb`/`simctl` call.
  - ⚠ An **`offline`** device does not hang the app build — measured, the whole solution built in 2:15
    with one listed. The hang above needs a device listed as `device` that will not answer.

- 🔴 **FOUR WAYS A DEVICE LOOP REPORTS SUCCESS IT DID NOT HAVE**, each earned by hitting it (D67, and the
  reason `@shenora/cli` checks rather than assumes):
  - **A piped install reports the PIPE's exit code.** `… install | tail` answers with `tail`'s status, so a
    REJECTED install "succeeds". Check the install's own code, not the pipeline's.
  - **An app extension installs happily and never launches when provisioned separately** — and **a
    simulator cannot catch it, because it does not enforce signing.** Only a device says so.
  - **An unqualified device picker takes the first of two phones.** Name the target; the same ambiguity
    bites `simctl` with two booted simulators, where a click and a screenshot landed on different devices.
  - **An unfiltered log tail is ~99 % platform chatter**, so "no output" and "the line scrolled past" look
    identical.

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
- **Forcing an Activity RECREATION on demand: CHANGE `settings put system font_scale` while the target
  is backgrounded** (it is not in MAUI's declared `ConfigurationChanges`, so the deferred relaunch
  fires when the activity returns to front — exactly the mid-flight shape a picker test needs).
  Two traps, both hit 2026-08-17: ⚠ **setting the SAME value is a silent no-op** — one run "passed"
  recreation while `OnCreate #2` never appeared, so assert the recreation marker, never just the
  outcome; and "Don't keep activities" does NOT fire behind the documents picker on API 36 — the host
  only pauses there, so that setting proves nothing for this scenario.

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
- **⚠ Before changing ANY permission on a Mac's Homebrew tree, read `local/MAC-DIAGNOSTICS.md`.** That
  install is in a mixed-ownership state, a package install fails there in three DIFFERENT ways in
  sequence, and the donor's advice (`sudo chown -R` the tree) is wrong for the first two. The generic
  lesson, which is the part worth carrying: **each stage produces a different error, and the change in
  error is the evidence** — so diagnose to the next error rather than escalating the remedy. Also ask
  whether the tool is needed at all: two rounds went into a 2019-era Homebrew for a CDP bridge when the
  actual goal (read page state programmatically) can be met by having the PAGE post to the LAN relay.

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

- **A page-side probe cannot report its own death** (adopter, 2026-08-06). "The reload failed" and "the
  reload succeeded but the bridge never came back" are the same silence, so only a NATIVE-side witness
  survives the event it is measuring. A probe that lives in the page is the obvious first design and is
  wrong for this whole class of question — which is why `PageProbe` drives from the host.

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

