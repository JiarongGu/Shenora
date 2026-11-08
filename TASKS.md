# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. Standing direction is stated in this file's own words
and stays as long as it still steers.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave**, and this has recurred six times.
`doc-shape` now fails on a done MARKER and warns past 120 lines — but the recurrences it could not see had
**no marker at all, just finished work narrated at length**. ⚠ **So the test is not "is there a ✅", it is
"would deleting this paragraph lose anything a future session must ACT on?"** If not, the commit that
landed it is where it lives.

**Status: v0.25.0 is the latest release** (tag `v0.25.0`; the release workflow stamps this paragraph).
⚠ `src/Directory.Build.props` must stay at `0.25.0` — the workflow owns the bump, and a hand-bump moves
the baseline and skips a release. **Cutting the next one? Read `.agents/knowledge/release-discipline.md`
first**: it carries the by-hand `<Description>` read, the prose-audit-before-the-cut rule, and why a
partial registry read afterwards is lag rather than a half-landed release.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

> **An environment we do not have is PARKED, not skipped:** the code is written and proven to run in a test
> environment, and the real-device verification waits for the day it is needed. An entry that is only a
> device run stays parked; one whose CODE is missing gets built and run in the nearest environment we
> have — the simulator, the emulator, the build Mac, WSL, the tests.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 🎞 THE CODECS ENGINE PACKAGE (D99) — PUBLISHED; WHAT IS LEFT

> `Shenora.Chromium.Codecs.win-x64` 154.0.32 (`engines/`, outside `src/`) is on nuget.org, and `node devtools/dev.mjs
> chromium-codecs` with no argument restores it into an empty package cache and passes (H.264 + AAC and HEVC, on a GeForce).
> What is open:
>
> - **An engine that arrives after install already works, with nothing in the kit:** whatever runs before the app starts (an
>   installer, a native starter, the staged updater) merges the package's Release and Resources into `engine\` beside the exe,
>   and the shim loads CEF from there at the next launch. `chromium-codecs --later` is the check. Not built, on purpose: a
>   download helper or a swap step in the shim (the owner said the installer can be anything, so the kit's code stays out of it).
> - **The Electron model** (Electron builds Chromium with `proprietary_codecs=true ffmpeg_branding=Chrome` and FFmpeg as a
>   separate shared library, `ffmpeg.dll`, so only a few MB need swapping): a CEF built with `is_component_ffmpeg=true` could ship
>   its codecs as a small `ffmpeg.dll` downloaded later. ⚠ Unproven in CEF, and every app would then carry the kit's CEF build
>   instead of CEF's own, widening D51's reopening from one opt-in package to the base engine.
> - **Every CEF bump needs a new build and a new package version** (CEF's), about 9 hours on 22 cores with Windows SDK
>   10.0.28000.0 installed. `.github/workflows/cef-codecs.yml` is that build as a workflow; it has never run as one. macOS, Linux
>   and arm64 are runners and matrix rows it does not have, and packages of their own (`ShenoraChromiumEnginePlatform` refuses a mismatch).
> - **A tiny HEVC test clip is not evidence:** a 128x96 clip failed on a GeForce and 320x240 played.

### 🧭 `Shenora.Chromium` ALONE IS A COMPLETE DESKTOP SHELL — Windows included (D88)

> **`Shenora.Chromium` is completed as a whole shell, usable on its own.** An app on any desktop references
> `Shenora.Chromium` and nothing of WinForms; `Shenora.Windows` stays the small-app (WebView2) choice on
> Windows.

What `Shenora.Windows` has and the Chromium shell does not, inventoried from the source, in the order to build:
- [ ] 🅿️ **The splash's frameless title strip with real input, on Windows.** Measured by hit-test only: the strip
  answers `HTCAPTION` and its close button `HTCLOSE`, and that point answers the page after the lift. A real drag, a
  double-click, and Snap Layouts on a hover over its maximize button need a person's mouse.
- [ ] 🅿️ **The Linux splash on a real GNOME or KDE session.** Measured under openbox on Xvfb (order, bounds, the page
  drawing beneath, the strip's drag, double-click and close) and WSLg's Weston (timing, the page drawing); Weston
  keeps no EWMH stacking list, so its order was not seen. Mutter and KWin are compositing managers, where the one-row
  gap a framed window's splash leaves should not be needed; confirm it does no harm there, that they keep a transient
  splash above its owner, and that they carry out the strip's `_NET_WM_MOVERESIZE`.
- [ ] 🅿️ **The macOS splash's PIXELS and real input.** Its presence, order, bounds and lift are measured through the
  window server's list; a screen capture needs Screen Recording permission on the build Mac (without it `screencapture`
  shows only the wallpaper), which a person grants. Then: CJK through CoreText, the splash's rounded bottom corners
  against the window's, a click on the splash bringing its window forward, and a drag on the frameless strip (with no
  traffic lights, from its whole width: compiled, not run).
- [ ] **A startup screen for a Chromium app without the update launcher, on Linux and macOS (D103).** Windows draws it from the
  shim, which is the native code CEF's launcher runs before .NET. On Linux and macOS the browser process IS the .NET apphost
  (the helper `helper_linux.c` / `helper_mac.c` only runs CEF's subprocesses), so nothing native of the kit's runs first;
  a screen there needs a native starter in front of the app, which is what the update launcher already is (D94: Windows
  and Linux). Whether a Linux app without it needs one is for an adopter to say.
- [ ] 🅿️ **The shim's screen on a person's desktop.** `dev.mjs chromium-startup-screen` reads its windows from outside; what a
  person sees (the picture's crispness at a second monitor's DPI, the first frame beside a slow disk) needs their eye.
- [ ] **The Chromium shell's folders publish on Linux (D96):** the same `engine/` and `lib/` shape, through the
  helper's and `libcef.so`'s library search paths (rpath). A spike first.
- [ ] 🅿️ **Whether a later launch's window takes the foreground on Linux and macOS.** Not observed: WSL's desktop and
  Xvfb enforce no focus-stealing prevention, so they cannot show it. On Linux a later launch could hand over its
  activation token (`XDG_ACTIVATION_TOKEN`, `DESKTOP_STARTUP_ID`) as Windows hands over the foreground; it needs a
  real GNOME or KDE session to see whether it is needed.
- [ ] 🅿️ **A real click on a blocked window, on macOS.** `IUiInteraction` disables the window's Views; on Linux
  that stopped a real click, and on Windows the window itself is disabled. On macOS the page's own `NSView` may
  take a click whatever Views says; it needs real input on the Mac to find out.
- [ ] 🅿️ **Native media in the Chromium shell** (D90): a player per OS for H.264, AAC and HEVC, its
  `IPlaybackSession` and `IMediaCapability`, when an app on the shell needs them. The page's own media session already
  reaches the OS's controls there.

### 🅿️ RE-SIGNING AN EXPIRED iOS BUILD — built; its signing half is parked

`ios deploy` installs only to a phone attached to the build Mac. An adopter measured the way around it
(2026-09-11), and `docs/guides/mobile.md` ("Installing when the phone is with you…") now carries it: the OTA
dead end, the network-vs-USB trap, and the Windows USB install of a re-signed `.ipa`.

`shenora ios resign` is built: it re-signs inside-out in the Mac's GUI session and brings the `.ipa` back.
Run on the build Mac as far as the profile lookup: it took the extension first and stopped, rightly, at no
unexpired profile for that bundle id.

- [ ] 🅿️ **Its signing half, against a live profile, then the install.** It needs a fresh profile for the
  sample's ids (`ios provision`, which mints on the Apple account, spending a free team's App ID quota), and
  the install needs the phone.

### 🌐 THE CHROMIUM SHELL — what each OS still lacks (D81–D96)

The shell runs on Windows and macOS from the published package, and on Linux from a local feed (the published
package's Linux helper has not run: see below); how it works is `docs/design/shells.md`, and why is D81–D96. What
is left is mostly measurement that needs hardware or a person.

**Every desktop OS**
- [ ] 🅿️ **`OnClientCertificateRequest` on macOS and Linux, and what the Windows probe did not reach.** Run on Windows only, where the
  offered list is the user's certificate store; a proxy that asks, a server naming its acceptable authorities, a decline (run once on Windows: the server then saw
  no page request, and why CEF's documented first-certificate default completed no handshake is not known) and a hardware token are not run, nor is what macOS's keychain and Linux's
  NSS database offer (`docs/design/shells.md`, the hooks paragraph).
- [ ] 🅿️ **`OnPopupRequested` on macOS and Linux, and what the Windows probe did not reach.** Run on Windows only; `Disposition`/window features CEF gives are not run or offered
  (`docs/design/shells.md`, the hooks paragraph).
- [ ] 🅿️ **`OnAuthRequest` on macOS and Linux, and what the Windows probe did not reach.** The code is shared (the browser's
  own DevTools channel) and was run on Windows only; a proxy's challenge (a `--proxy-server` try never reached the proxy) and the NTLM and Negotiate schemes
  are not run (`docs/design/shells.md`, the hooks paragraph), nor is whether a Linux/macOS CEF raises `Fetch.authRequired` the same.

**Windows**
- [ ] 🅿️ **win-arm64 has never RUN.** Its shim cross-compiles (the PE header says ARM64), and `-r win-arm64` lays out
  an ARM64 launcher, shim and `libcef.dll`; running it needs ARM64 hardware or a `windows-11-arm` CI runner. ⚠ The
  release job builds the ARM64 shim (the 0.23.2 package's is PE machine `0xaa64`); it has never been loaded.
- [ ] 🅿️ **The system menu from the real taskbar.** Measured only with the message the taskbar sends
  (`WM_POPUPSYSTEMMENU`, posted). A person Shift+right-clicking the app's taskbar button is the check, since a probe
  must not act on the taskbar, another app's window.
- [ ] 🅿️ **With a real pointer:** whether either caption-button mode shows the system's caption tooltips, and two of a
  frameless window's four corner resizes.

**macOS**
- [ ] 🅿️ **osx-arm64 has never run** (no Apple Silicon Mac here), for the shell or for `ChromiumBrowserProcess` (D86):
  all there is, is the 0.17.0 package's helper whose Mach-O header says arm64.
- [ ] **Signing and notarizing the published bundle**, which Gatekeeper needs from a download. A self-contained
  publish starts from Finder with no .NET installed (measured); a framework-dependent one does not find a per-user
  .NET there, since Finder and `open` pass no `DOTNET_ROOT`.
- [ ] 🅿️ **Real input:** the page's drag bar moving the window and the traffic lights' place over it; a click on the
  tray's status item; a file dialog answered by a person; whether the app comes to the front when started from
  Finder (from `open` over ssh it stayed behind the active app).
- [ ] 🅿️ **The system pasteboard.** The same code was measured on a private pasteboard; writing the owner's clipboard
  needs their yes.

**Linux**
- [ ] 🅿️ **The CI-built Linux helper has never RUN.** The release's `chromium-helper-linux` job builds the linux-x64
  and linux-arm64 helpers the packages carry (ELF, read in 0.19.0 and 0.23.2); the runs on WSL used helpers built
  locally.
- [ ] 🅿️ **A real desktop with working GL.** On WSL the window's close took 8–12 s in development mode (every run)
  and in about one production run in five, with WSL's own GL (`ZINK: failed to choose pdev` on every run); with
  SwiftShader or no GPU it closed in 15–190 ms.
- [ ] 🅿️ **Wayland.** CEF drew through XWayland even with `WAYLAND_DISPLAY` set, and `--ozone-platform=wayland` could
  not reach WSLg's socket from a `wsl.exe` shell. The Wayland app id is set and unmeasured.
- [ ] 🅿️ **A real desktop's tray and dialogs.** The StatusNotifierItem was measured against a scripted watcher and
  panel only, including whether a click on it raises the window past focus-stealing prevention (no user timestamp or
  XDG activation token is passed); Chromium may use the XDG desktop portal for dialogs where one runs. And
  linux-arm64, whose helper is built and has never run.
- [ ] **The clipboard at exit.** It hands no copy to a clipboard manager (the freedesktop `SAVE_TARGETS` handover), so
  a copy leaves with the app where no manager copies on change; and it writes no format larger than one X request.

### 🌐 THE CHROMIUM WEB PANEL (D97) — runs on Windows; what is left

It has run on real CEF (the Chromium sample with `SHENORA_SAMPLE_PANEL_PROBE=1`; how it works and what was seen:
`docs/design/shells.md`).

- [ ] **Decide whether history nav generalises onto `ISessionBrowser`.** `GoBack`/`GoForward`/`Reload`/`Stop` are on the
  panel today (Chromium-only, no Core or WebView2 change); WebView2's `CoreWebView2` has the same four, so they could
  move to the shared session contract if a second consumer wants them there. **Proposed as D100, awaiting the owner.**
- [ ] **macOS/Linux panels.** Windows only today: `ChromiumSessionHost.CreateChild` throws `PlatformNotSupportedException`
  in the macOS and Linux builds, while the overlay it builds is Views on every OS. Lift the guard and run the probe on
  Linux first (WSL can).

### 🟡 THE PLAYBACK HEALTH FIGURES — the adopter has now BUILT them, so this is a harvest call

`MediaPlayerStatus.Engine` answers *which* player ran, never *how well*. The adopter has since shipped both
halves against their own engines — LibVLC's `Media.Statistics` for lost pictures, and iOS's
`AVPlayer.TimeControlStatus` for a pause the app never asked for — after finding their own *"LibVLC exposes
no counters"* comment was false.

- [ ] **Decide whether the kit takes them (D15).** ⚠ It is not a free default like `Engine`: each shell
  reads a different platform API, and theirs are LibVLC/ExoPlayer while the kit's are `AVPlayer` and
  `android.media.MediaPlayer` — so the SHAPE harvests, the implementation does not.
  ⚠ **-1 means "no figure" and must never be 0**, which claims a clean play; confirmed on their side too
  (`Stalls` stays -1 deliberately — LibVLC has no stall counter). **Proposed as D101, awaiting the owner.**

### 🅿️ PARKED: the held seek on an iPhone — the picture is the only witness

`MediaPlayerBase` now holds a seek sent while a source opens and applies it once it is open, after
`StartAt` — on iOS that is the adopter's own fix for **green** frames after a seek into a loading item, which
they measured on a device (codec-dependent by GOP length, so it reads as "one file type is broken").
⚠ **The simulator cannot tell the two apart**: the sample's `SEEK-EARLY` probe sends a seek during the open
and it landed at 20.00 s both before and after the change. Position is not the symptom; the picture is.

- [ ] **On a device, with a long-GOP file**: seek during the open with the picture surface up, and look for
  green until the next keyframe — before the change (the commit's parent) and after.

### 🟡 IPC lifecycle coverage that the 2026-10-22 pass left

The bridges' gate wiring is now pinned (Chromium's `DocumentStarted`, WebView2's `Attach` read from its IL), along with
the re-handshake after a reload, the progress throttle and `configureBridge`'s rejection of the old default's work.
Still uncovered:

- [ ] **`MobileIpcBridge`'s control wiring is proven on a device only.** Everything but the `HybridWebView` is in
  `MobileIpcCore`, which `MobileIpcCoreTests` compiles; what is left is the `RawMessageReceived` hook, the dispatcher
  timer and the handler release, and its gate never closes on a document change (by design). A device probe.

### 🟡 The CLI package's vitest run failed once on "Channel closed"

Once in five `verify` runs (2026-10-01), `vitest (cli package)` ended on an unhandled rejection from its worker pool,
`Error: Channel closed` (`ERR_IPC_CHANNEL_CLOSED`, tinypool's `ProcessWorker.send`), with every test passed; alone it
passed 5 of 5. A forks-pool worker whose IPC channel closed under load, unattributed. If it recurs: count it under
`verify` and alone, then try `pool: 'threads'` in `src/Shenora.Cli/vitest.config.ts` as the A/B.

### 🟡 The splash's cover was once not under the splash in z-order

Once in two `verify` runs (2026-10-22), `WindowsSplashTests.A_frameless_owner_is_covered_click_through_under_the_splash_until_it_draws`
failed at `Assert.Equal(cover, below)`: walking down the z-order from the splash (`GW_HWNDNEXT`) reached the end without
meeting the cover. Alone it passed 5 of 5; nothing in that change touched the splash, and another app's windows were
starting on the desktop at the time. If it recurs: count it under `verify` and alone, and log the z-order (owner, cover,
splash, and what sits between) at the failure before changing the test or the surface.

### 🟡 A blocked main window's title-bar close is not tried on a Mac

The Chromium shell refuses a person's close of a main window whose input an interactive session has taken, and lets
the app's own `Close()` through (measured on Linux and Windows). The code is shared; macOS's title-bar close was not
tried.

- [ ] On a Mac, with an interactive session's window showing, close the main window from its title bar: it stays; and
  the app's own close (`ChromiumWindows.Close("main")`, the tray's Exit) still ends it.

### 📱 WHAT IS LEFT ON ANDROID NEEDS A PHONE'S ENCODER, NOT AN EMULATOR'S

The segment tier is answered on all three shells (`docs/design/media.md`), and the encoder's ARITHMETIC is
now confirmed on an API 36 AVD: 1280×720@30 asks for 4,147 kbps, and the 400 kbps floor without the
frame-rate factor. What no emulator here can answer is the CONSEQUENCE — a software encoder ignores the
request, producing ~1.7–1.9 Mbps either way. ⚠ MuMu converts no picture at all; the AVD converts only
mpeg4.

- [ ] **Confirm the encoder change CHANGES ANYTHING, on a phone.** A hardware encoder that honours
  `KEY_BIT_RATE` is the only instrument for "output size and encode cost", and the claim that this path is
  "newly hot" for ordinary 1080p H.264 rests on it. The host logs the rate it requests now, so the run is
  a read of two numbers.
- [ ] **Decide what the writer should do with a REORDERED encoder.** Same runs lost 1–6 frames of 149: the
  writer fail-closes on a backwards presentation time and drops the frame. The phone measured 60/60, so
  this is per-encoder, not settled. Dropping is safe and lossy; buffering and sorting is neither. **An
  owner call**, and it needs the phone number re-measured first. **Proposed as D102, awaiting the owner.**
