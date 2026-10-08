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

**Status: v0.20.0 is the latest release** (tag `v0.20.0`; the release workflow stamps this paragraph).
⚠ `src/Directory.Build.props` must stay at `0.20.0` — the workflow owns the bump, and a hand-bump moves
the baseline and skips a release. **Cutting the next one? Read `.claude/knowledge/release-discipline.md`
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
- [ ] **Two small points the colour scheme and cover review left (D93, D92)**, neither a defect in normal use:
  `WindowsHostOptions.ColorScheme` (the enum) and `WebViewHostOptions.ColorScheme` (the `IColorScheme`) share a name
  and not a type; and without a first paint the cover stays until the lift, snaps away at the end of the fade, and
  always draws the active glyphs.
- [ ] 🅿️ **The macOS splash's PIXELS and real input.** Its presence, order, bounds and lift are measured through the
  window server's list; a screen capture needs Screen Recording permission on the build Mac (without it `screencapture`
  shows only the wallpaper), which a person grants. Then: CJK through CoreText, the splash's rounded bottom corners
  against the window's, a click on the splash bringing its window forward, and a drag on the frameless strip (with no
  traffic lights, from its whole width: compiled, not run).
- [ ] **The startup screen for an app without the update launcher.** D94's configured screen runs in
  `Shenora.Launcher`, so an app that does not use the update launcher still shows nothing until .NET has started. The
  Chromium layout's own native exe (CEF's launcher, which boots .NET) could show the same description, compiled in by
  the build, and pass `--startup-screen` as the update launcher does. Not built; a configured picture, never a snapshot
  of the app (D94).
- [ ] **The Chromium shell's folders publish on Linux (D96):** the same `engine/` and `lib/` shape, through the
  helper's and `libcef.so`'s library search paths (rpath). A spike first.
- [ ] **A self-contained Windows Chromium publish.** The SDK refuses it with or without folders (NETSDK1067: the kit
  sets `UseAppHost=false`, because CEF's launcher is the exe), so only macOS and Linux publish self-contained. With it
  enabled, the shim would find `hostfxr.dll` beside the app (`lib\` in a folders publish), a path never run.
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

**Windows**
- [ ] 🅿️ **win-arm64 has never RUN.** Its shim cross-compiles (the PE header says ARM64), and `-r win-arm64` lays out
  an ARM64 launcher, shim and `libcef.dll`; running it needs ARM64 hardware or a `windows-11-arm` CI runner. ⚠ The
  release job's cross-compile on `windows-latest` is untried until it runs.
- [ ] 🅿️ **The system menu from the real taskbar.** Measured only with the message the taskbar sends
  (`WM_POPUPSYSTEMMENU`, posted). A person Shift+right-clicking the app's taskbar button is the check, since a probe
  must not act on the taskbar, another app's window.
- [ ] 🅿️ **With a real pointer:** whether either caption-button mode shows the system's caption tooltips, and two of a
  frameless window's four corner resizes.
- [ ] **App hooks for downloads, permissions and renderer failure**, when an app asks for them; the defaults are the
  WebView2 shell's (D84).

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
- [ ] **The release's `chromium-helper-linux` job has not run in CI**; its steps were rehearsed on WSL.
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

### 🟡 THE PLAYBACK HEALTH FIGURES — the adopter has now BUILT them, so this is a harvest call

`MediaPlayerStatus.Engine` answers *which* player ran, never *how well*. The adopter has since shipped both
halves against their own engines — LibVLC's `Media.Statistics` for lost pictures, and iOS's
`AVPlayer.TimeControlStatus` for a pause the app never asked for — after finding their own *"LibVLC exposes
no counters"* comment was false.

- [ ] **Decide whether the kit takes them (D15).** ⚠ It is not a free default like `Engine`: each shell
  reads a different platform API, and theirs are LibVLC/ExoPlayer while the kit's are `AVPlayer` and
  `android.media.MediaPlayer` — so the SHAPE harvests, the implementation does not.
  ⚠ **-1 means "no figure" and must never be 0**, which claims a clean play; confirmed on their side too
  (`Stalls` stays -1 deliberately — LibVLC has no stall counter).

### 🅿️ PARKED: the held seek on an iPhone — the picture is the only witness

`MediaPlayerBase` now holds a seek sent while a source opens and applies it once it is open, after
`StartAt` — on iOS that is the adopter's own fix for **green** frames after a seek into a loading item, which
they measured on a device (codec-dependent by GOP length, so it reads as "one file type is broken").
⚠ **The simulator cannot tell the two apart**: the sample's `SEEK-EARLY` probe sends a seek during the open
and it landed at 20.00 s both before and after the change. Position is not the symptom; the picture is.

- [ ] **On a device, with a long-GOP file**: seek during the open with the picture surface up, and look for
  green until the next keyframe — before the change (the commit's parent) and after.

### 🅿️ PARKED: a mobile dialog that ends without answering now waits for good

The page waits on a file dialog with no timeout (the 30 s default lost slow picks), so every host path must
answer (`ipc-contracts.md`). Two mobile paths are unverified:

- [ ] **iOS**: a swipe-down dismissal of the export picker. `IosFileDialogs.ExportAsync` answers only through its
  delegate's pick and cancel; the simulator can show whether `WasCancelled` fires.
- [ ] **Android**: an app that does not forward `OnActivityResult` to `ActivityResultRelay.Deliver` used to end in
  a `TIMEOUT` and now never answers. The relay could notice a result that never arrived once the activity resumes.

### 🟡 The CLI package's vitest run failed once on "Channel closed"

Once in five `verify` runs (2026-10-01), `vitest (cli package)` ended on an unhandled rejection from its worker pool,
`Error: Channel closed` (`ERR_IPC_CHANNEL_CLOSED`, tinypool's `ProcessWorker.send`), with every test passed; alone it
passed 5 of 5. A forks-pool worker whose IPC channel closed under load, unattributed. If it recurs: count it under
`verify` and alone, then try `pool: 'threads'` in `src/Shenora.Cli/vitest.config.ts` as the A/B.

### 🟡 A blocked main window's title-bar close is not tried on a Mac

The Chromium shell refuses a person's close of a main window whose input an interactive session has taken, and lets
the app's own `Close()` through (measured on Linux and Windows). The code is shared; macOS's title-bar close was not
tried.

- [ ] On a Mac, with an interactive session's window showing, close the main window from its title bar: it stays; and
  the app's own close (`ChromiumWindows.Close("main")`, the tray's Exit) still ends it.

### 🟡 A background handoff resumes a film the user had paused

`BackgroundPlaybackTransfer.ToBackgroundAsync` hands off a `Paused` player as well as a `Playing` one, because the
platform pauses a backgrounded element itself before `Window.Stopped` (`docs/design/mobile-shells.md`). So a film the
user paused starts playing natively when they leave the app. Found in review, not seen live.

- [ ] Measure on Android whether the platform's pause arrives while `document.hidden` is already true; if it does,
  `useMediaPlayer` can tell that pause from the user's, and the transfer can hand off only a film that was playing.

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
  owner call**, and it needs the phone number re-measured first.

