# devtools/ — Shenora dev + test toolkit

One entry point: **`node devtools/dev.mjs <cmd>`** (family pattern — allow-listed once, no ad-hoc
shell). Project-specific paths/names live in **`project.config.mjs`** (the only file to edit to
reuse this toolkit on another repo). The library version is parsed there from
`src/Directory.Build.props` `<VersionPrefix>` — the one version source.

## Commands

| cmd | what |
|---|---|
| `build` | `dotnet build` the solution + `npm run build` in the react package |
| `test [dotnet\|npm]` | `dotnet test` + vitest (or one side) |
| `verify [--release]` | **the "am I done?" gate**: build · test · typechecks · `check-sensitive --tree` · `knowledge check` + `footprint` (the always-loaded budget — reported, never fatal) · `doc-drift` · `doctor`, stop at first red. **`--release`** runs only what protects the ARTIFACT — it skips the two rule-base checks, which ship nothing and cannot harm a consumer (they blocked the 0.4.0 release twice while the packages were fine). `release.yml` uses it |
| `pack` | doctor-fix, then nupkgs + npm tarball → `publish/packages/` (lockstep `-p:Version`, sha256 printed) |
| `doctor [--fix]` | version drift (npm `package.json` + README `## Status` headline vs `VersionPrefix`) **and doc drift** — `--fix` only applies to the version half; every doc-drift finding is a sentence a human has to rewrite |
| — `doc-drift` (run by `doctor`/`verify`; `scripts/doc-drift.mjs --list` to inspect) | **the gate the prose never had.** Every code invariant here has a test and no doc claim had anything; a whole-codebase review found 8 of its ~13 findings in comments and docs. FOUR precise checks, deliberately not a fuzzy symbol sweep (which would drown the signal and get switched off): (1) the dependency graph drawn in `README.md`/`ADOPTION.md` vs the actual `ProjectReference`s — both files documented a `WinForms → Ipc` edge that never existed; (2) names in `devtools/retired-names.txt` stated as CURRENT fact — **including retired PACKAGE IDS since 2026-08-05**, the category that had been missed and had already cost a shipped nupkg description and a wrong adopter instruction; (3) every `docs/…md` pointer resolves; (4) every packable project is named in `README.md` AND `docs/ARCHITECTURE.md` — added after `Shenora.IO.Compression` shipped with no ARCHITECTURE entry while every gate stayed green. Amendment stacks are the norm here, so a retired name is fine in the PAST tense — add `doc-drift:history` to mark a preserved sketch or rename table. **Add a name to `retired-names.txt` the moment you delete or rename one.** ⚠ It cannot see a retired name that a rename REPLACED with the current one — read the diff after a sweep |
| `sample [--dev\|--no-build]` | run the sample desktop app. Builds the packaged frontend first (Production serves the EMBEDDED `wwwroot`, so skipping that ran a stale bundle — see `docs/archive/fix-log.md` 2026-08-02); `--no-build` skips it for a C#-only relaunch. `--dev` = vite URL + CDP port → `.cdp-port`, no bundle build needed |
| `vite` | the sample web dev server (Phase 2+) |
| `shot [name]` | PrintWindow capture of the sample window → `screenshots/` (auto-pruned, see below) |
| `wgc [name]` | occlusion-immune capture (Windows Graphics Capture) — works when the window is hidden/occluded |
| `click <fx> <fy>` | background click at client-rect **fractions** (0–1) — drives the WebView2 UI without CDP |
| `rclick <fx> <fy>` | as `click`, right button |
| `move <fx> <fy>` | background mouse-move to a client-rect fraction (hover states) |
| `drag <fx1> <fy1> <fx2> <fy2>` | background press-move-release between two client-rect fractions |
| `input <args…>` | raw `win-input` passthrough (`list`, `click x y`, `rclick x y`, `move x y`, `drag x1 y1 x2 y2`) |
| `responsiveness <fx> <fy> [--label n] [--duration\|--interval\|--timeout ms]` | click a control, then sample `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` sub-100ms to measure whether the UI thread keeps pumping — the probe behind the one-way-IPC UI-thread claim (see below) |
| `android <devices\|connect\|deploy\|run\|log\|shot>` | the MAUI sample's device loop — see the section below |
| `mac <doctor\|setup\|push\|build\|run\|shot\|tap\|type\|swipe\|safari-eval\|mirror\|log\|provision\|profiles\|awake\|ssh>` | the same loop on iOS, driven over SSH on a Mac — see the section below |
| `knowledge <check\|footprint\|new <name> [--core]>` | two-tier rule-base doctor: index↔files consistency, always-loaded byte budget, scaffold a rule. `check` covers SKILLS the same way — a `.claude/skills/*/SKILL.md` missing from `skill-loader`'s table is never picked, and that was guarded only by a sentence asking the next session to remember |
| `clean [--all]` | drop `_*` scratch BUILD OUTPUT (bin/obj/node_modules/out/dist); `--all` also drops probe sources + `publish/` |
| `check-sensitive [--tree|--history]` | scan for dev paths / private names. `--tree` = checkout; `--history` = ONE-OFF audit of every blob, path and commit message |
| `install-hooks` | point `core.hooksPath` at `devtools/hooks` — ONCE per clone |
| `nuget-retire [--apply] [--api-key <key>] [--only <id>]` | unlist EVERY version of a package id renamed away (D37). Dry-run by default; **REFUSES until the replacement is published**, so it cannot open a window where neither old nor new is findable. Key via `--api-key` or `NUGET_API_KEY`, scoped **Unlist** and revoked afterwards; it is redacted from all output. Prints the deprecation text, which is web-UI only. ⚠ **Built, and deliberately UNUSED until 1.0 (D49)** — pre-1.0 ids are retired in one pass once the package set stops moving, not batch by batch |
| `changelog [--fix] [--version <v>] [--date <d>]` | ⚠ **the RELEASE pipeline's command, not yours.** It stamps `## Unreleased` with the version being published — `--version` exists precisely because that version has not been written back into `VersionPrefix` yet. `CLAUDE.md`'s hard rule says the workflow owns that heading, so running this by hand does the exact thing the rule forbids. Listed here because a command that must never be typed still has to be findable — it was missing from this table until the 2026-08-05 review |
| `launcher` | build the native launcher (`src/Shenora.Launcher`, CMake) and run the conformance harness against the resulting BINARY — prints its size first, because D50 recorded that as a band nobody had measured. Finds VS's bundled CMake when none is on PATH. **Not in `verify`**: this repo has no C++ toolchain and deliberately does not require one (design doc §5). This command IS the local gate; the release workflow builds both targets and blocks on their conformance (D5 — verification is local, the release is the gate). ⚠ The harness stages with the real C# implementation (`update-probe --stage-only`), never a local fixture — a protocol written twice, once per language, is one that drifts. ⚠⚠ **On Windows this proves ONE platform** — both platform `.cpp` files compile, but only the branch your compiler takes is checked, so a POSIX-only break passes green (it did — `platform_posix.cpp` was missing two includes MSVC supplies transitively, and the first release found it). Use `--posix` |
| `launcher --posix` | cross-build the launcher's POSIX half with gcc in a `gcc:13` container, then run the binary to prove it links and starts. Needs Docker; nothing else does, which is why it is opt-in and not in `verify`. It drives the **real `CMakeLists.txt`** rather than its own g++ line — a check carrying a private copy of the build flags drifts away from the thing it checks. No conformance here: the harness drives the binary through the C# `update-probe` and the container has no .NET, so cross-compilation checks the code and conformance stays the release's job. ⚠ First run pulls the image; each run then spends ~20s installing CMake (`gcc:13` ships without it and `--rm` discards it) |
| `update-probe [dir] [--install <dir>] [--keep]` | drive `Shenora.IO`'s staged updater over a **real** tree — manifest → stage → `CommitAsync` → `ApplyAsync` — and report the numbers (files, would-be intrusions, written, removed). With no `dir` it publishes the desktop sample and probes that; give it any directory to probe an adopter's own release. **Deliberately NOT in `verify`** (it publishes, which is slow, and `verify` has no reason to build a release-shaped tree every run). ⚠ Its first run found a real defect the fixture suite could not: `CommitAsync` published its marker without checking for `staged/manifest.json`, so a manually-staged tree verified clean and then failed at APPLY time — in the launcher, after the app had exited |
| `android-jdk` | print the JDK path `android` resolves (JAVA_HOME, else Android Studio's bundled `jbr`), or exit non-zero naming the fix. ONE owner for the probe, the same reason the kit has one owner for UI marshalling |

Releases are cut by the manual **Release** GitHub workflow — see `docs/RELEASING.md`.

## Machine prerequisites (since `Shenora.Android` joined the solution)

`Shenora.Android` targets `net10.0-android`, so **building this repo at all** now needs two things
beyond the .NET SDK. That cost was accepted deliberately (owner, 2026-08-02): a package no gate
compiles is the objection this repo raises against any ungated artifact.

| Need | How to get it | If it is missing |
|---|---|---|
| `maui-android` workload | `dotnet workload install maui-android` | the restore fails naming the missing workload |
| `maui-ios` workload | `dotnet workload install maui-ios` | same. Needed on WINDOWS too — a `net10.0-ios` library compiles against reference assemblies and wants no Xcode; only an iOS *app* needs a Mac |
| A JDK 17+ | **Android Studio already ships one** in its `jbr` folder | `dev.mjs build` probes for it and sets `JAVA_HOME` for the child process; with none it stops and says so, rather than letting MSBuild emit a bare `error XA5300` pointing at an install page on a machine that already has a JDK |

The Android SDK resolves from its default location; set `ANDROID_HOME` only if yours is elsewhere.
The probe order is `JAVA_HOME` → Android Studio's `jbr`/`jre` under Program Files or LOCALAPPDATA —
every candidate derived from an environment variable, never a literal path, so nothing
machine-specific reaches a tracked file.

### Running the MAUI sample on a device — `dev.mjs android`

| Command | What |
|---|---|
| `android devices` | list attached devices (starts the adb server) |
| `android connect <host:port>` | attach an emulator's adb bridge — **its own manager reports the port**; no vendor default is hardcoded here |
| `android deploy` / `android run` | build + install the sample APK; `run` also launches it |
| `android log [-n N] [--all]` | the app's log. Default is the sample's one tag so a run reads as a story; `--all` for the platform's side too; `-n` for a snapshot instead of following |
| `android shot [name]` | screenshot → `devtools/_android/` (gitignored) |

`--device <id>` picks a target when several are attached; with exactly one it is inferred, and with
several it REFUSES rather than guessing — installing to whichever adb listed first is a mistake you
only notice by looking at the wrong screen.

Four traps, each paid for on the first run and now handled by the commands above:

- **The ABI must match.** `androidRuntimeIdentifier` (`project.config.mjs`) is `android-x64` because
  emulators are x86_64; a default build can produce arm64 only and the install fails
  `INSTALL_FAILED_NO_MATCHING_ABIS`, which reads like a packaging fault rather than the wrong
  architecture. Change it for an arm64 phone.
- **A screenshot must not be piped.** `adb exec-out screencap -p > file.png` is CORRUPTED by shell
  redirection on Windows (BOM + re-encoding) and produces a PNG nothing can open. `shot` captures on
  the device and pulls the bytes.
- **`logcat -t N` does not compose with a filterspec.** `-t` prints the last N lines of the RAW
  buffer and the filter is applied afterwards, so `-t 60 -s SHENORA:V` reliably prints nothing once
  60 lines of platform chatter have gone by. `log -n` tails after filtering instead.
- **The page must load MAUI's bridge script** (`_framework/hybridwebview.js` on .NET 10). Without it
  `window.HybridWebView` does not exist, the page renders fine, and the host just sits reporting
  "waiting for the page handshake".

### Running it on iOS — `dev.mjs mac`

iOS cannot be built on this machine at all: it needs Xcode, and Xcode needs macOS. So the loop is
driven over SSH on a Mac. **Ported from the public sibling Sonora's `mac.mjs`**, keeping its
post-mortems — only the build step differs (that project builds an Xcode project; this one runs
`dotnet build -f net10.0-ios`).

| Command | What |
|---|---|
| `mac doctor` | is the Mac reachable, and does it have Xcode, a .NET 10 SDK, the `ios` workload and the configured simulator? Reports every gap, not just the first |
| `mac setup` | one-time: a bare repo + working clone on the Mac, and a local `mac` git remote |
| `mac push` | push the branch and reset the Mac's clone to it |
| `mac build` / `mac run` | build for the simulator; `run` also boots it, installs and launches |
| `mac shot [name]` | screenshot the simulator → `devtools/_mac/` (gitignored) |
| `mac tap <x> <y>` / `mac type <text>` | input, in the NATIVE pixels of that screenshot (see the trap below) |
| `mac swipe <x1> <y1> <x2> <y2>` | drag/scroll, same coordinate space. Needs `cliclick` on the Mac and REFUSES without it rather than half-scrolling |
| `mac safari-eval <js…>` | run JavaScript in the page and print the VALUE. The difference between reading state and guessing at it |
| `mac mirror [port]` | live view of the simulator on the LAN (default **7674**); click to tap, scroll to swipe |
| `mac log [-n N] [--all]` | the sample's own lines from the simulator's unified log; `--all` for the whole process |
| `mac provision [<bundle-id>…]` | mint provisioning profiles using an Xcode project **the kit owns** (`devtools/ios-provision/`). Defaults to the sample's app + its Live Activity extension |
| `mac profiles` | what profiles this Mac has and when they expire — **checks BOTH locations** (see the trap below) |
| `mac awake [on\|off]` | stop the Mac sleeping while it is a build machine |
| `mac ssh <cmd>` | escape hatch |

### Reaching a real device — what the kit owns now, and the two traps

**`.NET cannot mint a provisioning profile.** It only CONSUMES them, so a device build fails with *"Could
not find any available provisioning profiles"* until something else has created one — and the only thing
that does is `xcodebuild -allowProvisioningUpdates`, which needs *an* Xcode project. That is why
`devtools/ios-provision/` exists: a minimal, generic, kit-owned stub whose only product is the profile.
⚠ Borrowing a consumer's Capacitor/Xcode project was tried and rejected — slow, drags that app's SPM
checkouts in, and makes the kit depend on the consumer having Capacitor.

- 🔴 **Xcode 16 MOVED where profiles live**, and checking the old path is how a machine that has profiles
  reports having none. Classic: `~/Library/MobileDevice/Provisioning Profiles/`. Current:
  `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`. Measured on Xcode 26.3, where minting wrote
  all three profiles to the NEW path and never created the old directory at all. `mac profiles` reads both.
- 🔴 **`xcodebuild` exits 0 without necessarily having produced the profile you asked for** — it will
  happily succeed against one it already had. So `mac provision` verifies by reading the profiles OFF DISK
  and matching their `application-identifier`, never by trusting the exit code. That check is what caught
  the path change above; a version trusting exit status would have reported success and left the device
  build failing for a reason nothing pointed at.
- ⚠ **Signing must run in the GUI login session** (`guiRun`), because `codesign` cannot use a login-keychain
  key over ssh (`errSecInternalComponent`). INSTALL and LAUNCH need no keychain, so those go over plain ssh
  via `xcrun devicectl` — getting that split backwards is what makes people reach for a GUI on the Mac.
- ⚠ **Two things can never be automated**, and both belong in any recipe that ships: a free/personal-team
  profile **expires after 7 days**, and a first install needs the certificate trusted ON THE PHONE
  (Settings → General → VPN & Device Management → the account → Trust).

**The Mac's address, user and key live in `local/mac.json`** — gitignored, because the harness is
tracked and this repo is public. `mac doctor` prints the file to create if it is missing.

Carried-over traps worth not re-earning:

- **It REFUSES to push a dirty tree.** The Mac builds HEAD, so an uncommitted fix is not in the build
  and "the fix did not work" is the wrong conclusion. On the sibling this was a warning, it scrolled
  past twice in one session, and cost two rounds of reasoning about code the Mac had never seen.
  `--allow-dirty` to override knowingly.
- **The simulator RID follows the MAC's architecture** (`iossimulator-x64` on Intel, `-arm64` on
  Apple Silicon) — asked over ssh, never assumed. This is the iOS twin of the ABI trap above: the
  build succeeds and the INSTALL is what fails, so the error names the wrong step.
- **`-o pipefail` when piping the build through `tail`.** Without it the pipeline reports tail's
  status, which is always 0, so a failed build sails through and the next step tries to install a
  binary that was never produced.
- **Tap coordinates need two conversions.** A screenshot is device PIXELS, the window is desktop
  POINTS, and the Simulator scales the device to fit. The harness reads the window geometry and the
  screenshot size together and derives the mapping, so nothing about the device model is hardcoded.
  Needs Accessibility permission on the Mac or every click silently does nothing.
- **Signing does not work over ssh.** An ssh login is a different AUDIT SESSION, so a login-keychain
  key fails with `errSecInternalComponent`. Only DEVICE builds need it; simulator builds sign ad-hoc.
  The sibling hands signing to Terminal.app via `osascript`; port that when a real iPhone is wanted.

And four more, all earned on the first real iOS run rather than inherited:

- **`mac log` filters BEFORE it tails, and that is not a nicety.** A MAUI process is ~99% WebKit
  lifecycle chatter (a `runJavaScriptInFrame` pair per notification tick), so a process-wide
  predicate plus `tail -n` shows a screen of noise and NONE of the app's lines — indistinguishable
  from a broken log sink. This is the same trap as `logcat -t N` above, rebuilt in the other harness
  despite being written down. The app reaches the unified log through `libSystem.Native`
  (Console → stdout), so the tag is on the MESSAGE, not a subsystem.
- **An Xcode older than the workload needs TWO flags** (`skipXcodeVersionCheck` in `local/mac.json`
  sets both): `ValidateXcodeVersion=false` clears the up-front equality gate, and
  `MtouchLink=SdkOnly` clears MT0180 from the ILLink Setup step, which separately wants the iOS SDK
  headers Xcode ships. `PublishTrimmed=false` is rejected outright and `MtouchLink=None` still fails
  MT0180 — don't retry either. **Simulator debug only**; matching the Xcode/workload pair is the real fix.
- **The client npm package must be built ON the Mac.** `dist/` is a gitignored build artifact, so
  pushing the branch does not carry it and the sample silently falls back to its hand-written inline
  transport — a quietly weaker proof than the Android run. `mac build` now builds it first.
- **Write the page for the superset of shells, not the one you tested.** The sample's markup was
  unchanged from Android and still put its heading under the status bar and the Dynamic Island,
  because an emulator has no insets to violate. `env(safe-area-inset-*)` + `viewport-fit=cover`.

### The drive loop, harvested from the sibling 2026-08-03 (`swipe` · `safari-eval` · `mirror`)

The first port took the build half and left the DRIVE half behind, which is what made the DM1 media
work slow: every question about page state had to be answered by screenshotting and reading pixels.

- **`mac safari-eval` is the one that changes how a session works.** A screenshot cannot report a
  number, a header or an array, and a `<video>` element can only ever say "no supported source"
  however it actually failed. Running a `fetch` in the page and reading the bytes back is what turned
  DM1 from three equally plausible hypotheses into one measurement (D44).
  ⚠ **It needs `ios-webkit-debug-proxy` on the Mac and that install is not yet done here** — see the
  command's own error text, which carries the exact diagnosis for this machine.
- **`mac tap` takes NATIVE screenshot pixels, not the size a tool DISPLAYED the PNG at.** A preview
  may downscale and label its own size; multiply back first or every tap misses. Hit live during DM1.
- **`tap` now says which mechanism landed it.** cliclick or the System Events fallback — the fallback
  can register as focus-only on some web controls, so a silent downgrade is a tap that "succeeded"
  and did nothing.
- ⚠ **A capability probe must run in the SAME SHELL as the command it gates.** `ssh()` uses
  `bash -lc` (login → Homebrew on PATH); the worker used a plain `bash -s` and answered "cliclick is
  not installed" for a Mac that has it, seconds after a direct check found it. `tap` read that answer
  and quietly took the weaker path. The worker is `bash -l -s` now. **The sibling still has this.**
- **The mirror defaults to 7674, NOT the sibling's 7672.** Both repos live on this machine and both
  mirrors point at the SAME simulator, so a collision answers `/frame` with a valid screenshot and a
  smoke test passes against the other repo's server. Observed on the first run; the port is now
  distinct and `EADDRINUSE` says all of this instead of throwing a raw stack.
- **The persistent ssh worker is why these are usable.** A fresh connection costs ~1.8 s against a
  ~322 ms screenshot, and `ControlMaster` multiplexing does not exist on the Windows ssh client — so
  one `bash -l -s` is held open and fed commands to a sentinel.

**Its public surface is gated differently, and more weakly.** `tests/Shenora.Tests` is
`net10.0-windows` and cannot reference an Android assembly, so `MetadataSurfaceTests` reads the built
DLL's IL metadata instead (`Api/MetadataBaselines/`). That catches an add, a removal or a rename, and
**cannot** catch a signature-only change the five full baselines would. Keep the package thin.

## Screenshots are auto-pruned

`shot`/`wgc` keep the newest `shotRetention` captures (24, in `project.config.mjs`) and delete the
rest BEFORE capturing, so the new file is never the one evicted. Every deletion is printed — a
cleanup that removes work silently is worse than one that never runs. Override once with
`--keep N`, or raise the config value while mid-investigation.

Why: captures are gitignored, transient, and cost a keystroke, so the folder only grows — 53 files /
7.5 MB by v0.1.0, and no doc referred to any of them. Evidence in this repo is recorded as NUMBERS
and prose (ROADMAP, FIX-LOG), never as a PNG.

## Scratch folders (`devtools/_*`)

Gitignored probes — the P6 consumers, the adoption adapters, the P7 profile proofs. `docs/ROADMAP.md`
and `docs/archive/tasks.md` describe them as RE-RUNNABLE, so `clean` removes only their regenerable
build output and leaves the sources; `--all` is the opt-in destructive reading. (Verified: after a
`clean` that reclaimed ~60 MB, the profile probe still rebuilt from source and passed.)

## Desktop verification loop (no CDP needed)

Once the sample app exists (Phase 2), the desktop UI is driven natively: **`win-input`** posts
background mouse messages to the WebView2 render surface (fractions of the client rect — no cursor
move, no focus steal, works even occluded), and **`wgc`** / **`shot`** capture the result. Both
target the process from `project.config.mjs` (`processName`, passed via the `DEVTOOL_PROC` env
var — no project name is baked into the C# tools). Typical loop:

```
node devtools/dev.mjs build
node devtools/dev.mjs sample
node devtools/dev.mjs input list           # find the window + its client size
node devtools/dev.mjs click 0.21 0.30      # click a control by fraction
node devtools/dev.mjs shot after-click     # capture → devtools/screenshots/after-click.png
```

(Two lines, not `&&` — the primary shell here is PowerShell 5.1, which has no `&&`.)

## The UI-thread responsiveness probe

`responsiveness` re-proves the claim the one-way IPC design rests on
(`docs/DECISIONS.md` D23): work left in a route's synchronous segment
stalls the UI thread; work handed off (`ctx.Run`) and streamed back does not. It clicks a control
via `win-input`, then samples `SendMessageTimeout(hwnd, WM_NULL, …, SMTO_ABORTIFHUNG, timeoutMs)` —
which returns only once the target thread's message loop actually PUMPS, so a failed call means the
thread is genuinely busy, not just slow. Sampling is sub-100ms by default (both the interval and each
sample's own timeout), because ~1s sampling cannot resolve a multi-second freeze (a real v0.1.0
mistake).

**It refuses to print numbers unless the click actually landed AND the operation actually started** —
four guards, in order, any of which aborts with a nonzero exit and NO sample stats:

1. A live process with a real main window is found (retries briefly — a GUI app can take a moment to
   create its window). Fixes the v0.1.0 mistake where the app never launched, the click never
   arrived, and the probe still reported "0 stalls" as if that were a pass.
2. One baseline `WM_NULL` sample succeeds BEFORE the click (the thread pumps at all) — catches a
   process that exists but is already stuck for an unrelated reason.
3. `win-input`'s own "click ok on hwnd=0x.." confirmation is present in its captured output.
4. **The window TITLE (`GetWindowText`) shows a marker substring (`--title-contains`, default
   `"SLOW running"`) within a short grace period after the click.** Guard 3 alone is NOT sufficient:
   `win-input` reports "click ok" for any coordinate that resolves to *some* leaf window under the
   point, and for a WebView2 host that leaf is the render surface, which spans the whole client area
   — so stale fraction coordinates, a moved button, or a disabled control would all still "land" a
   click and produce a clean `unresponsive=0` result that measured nothing real. `SampleFacade` sets
   the title BEFORE either shape's slow work begins (see `RunningTitleMarker`'s doc comment there),
   deliberately, because in `block` mode the UI thread freezes for the rest of the route and a title
   set only during the freeze would not be observable in time — a window's title, unlike responses to
   most messages, is cached by Windows and readable cross-process even while the owning thread is
   hung (the same reason Task Manager / Alt-Tab still show a hung app's real title). A companion check
   (guard 1b) also refuses if the marker is ALREADY present *before* the click, so a stale "running"
   title left over from a prior, unfinished run cannot be mistaken for fresh evidence.

**Residual limit, named rather than hidden:** guard 4 proves *some* operation matching the title
marker started at roughly the right time — for a busier app with several concurrently-running,
same-marker operations it would not by itself say WHICH one. This sample has exactly one SLOW route
behind both buttons, so the ambiguity does not arise here; a consumer reusing this probe against a
busier app should give each operation kind its own marker. Guard 4 also requires the app under test
to cooperate with the title convention — unlike guards 1-3, it is not fully app-agnostic.

```
node devtools/dev.mjs sample --dev &            # or a second terminal — leave it running
node devtools/dev.mjs input list                # find the SLOW buttons' fractions once
node devtools/dev.mjs responsiveness 0.30 0.85 --label block  --duration 4000
node devtools/dev.mjs responsiveness 0.62 0.85 --label stream --duration 4000
```

Prints `RESULT label=<name> samples=<n> unresponsive=<n> longestStallMs=<n>` — record the numbers in
`local/PROJECT_NOTES.md`, never only in a screenshot (this repo's evidence is numbers and prose). A
wrong-coordinates run instead prints `REFUSING to report - ... the operation never appeared to start`
and exits nonzero, rather than a vacuous clean zero.

## Ground rules (keep the loop prompt-free)

- Drive everything through `dev.mjs` — don't invent ad-hoc shell for these steps.
- Don't prefix commands with `cd` (a compound `cd …` trips the sandbox permission prompt).
- Inspect code with the Read/Grep/Glob tools, not `cat`/`grep` in Bash.
- Throwaway/probe files go under `devtools/` and are prefixed `_` (gitignored); clean them up.
- Captures land in `devtools/screenshots/` (gitignored).

## Layout

- `dev.mjs` — the dispatcher (reads `project.config.mjs`).
- `project.config.mjs` — names, paths, ports + the `VersionPrefix` bridge. **Edit this to re-target.**
- `scripts/` — the standalone tools: `check-sensitive.mjs` (public-repo leak guard; private
  patterns load from gitignored `local/sensitive-patterns.txt`), `knowledge.mjs` (rule-base
  doctor), `shot-window.ps1` (PrintWindow + PW_RENDERFULLCONTENT capture).
- `hooks/pre-commit` — the tracked pre-commit guard (installed via `install-hooks`).
- `win-input/` · `wgc-shot/` · `ui-responsiveness/` — native C# desktop-verification tools
  (background input, WGC capture, the `SendMessageTimeout` stall probe), built on demand into their
  gitignored `bin/`; retargeted via `project.config.mjs`, not their source.
- `_*` / `screenshots/` / `.cdp-port` — gitignored scratch.

Windows gotchas (PS5 UTF-8/BOM, Node `fs.cpSync` crash, WebView2 CDP arg clobber) live in
`.claude/rules/windows-dev-gotchas.md`.
