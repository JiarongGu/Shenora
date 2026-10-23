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

**Status: v0.23.2 is the latest release** (tag `v0.23.2`; the release workflow stamps this paragraph).
⚠ `src/Directory.Build.props` must stay at `0.23.2` — the workflow owns the bump, and a hand-bump moves
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
- [ ] **A self-contained Windows Chromium publish.** Refused today, by the kit's own check with the way out: the kit
  sets `UseAppHost=false` (CEF's launcher is the exe) and the SDK needs an apphost to be self-contained, so only
  macOS and Linux publish self-contained. With it
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
- [ ] **The app's icon on the D86 browser process's windows, and after a navigation.** Same probe
  (`input icons --proc <App>` while `ChromiumBrowserProcess` runs, and again after the page navigates); only the
  shell's main window was measured. Chrome's own tabbed windows may send `WM_SETICON` themselves.
- [ ] 🅿️ **win-arm64 has never RUN.** Its shim cross-compiles (the PE header says ARM64), and `-r win-arm64` lays out
  an ARM64 launcher, shim and `libcef.dll`; running it needs ARM64 hardware or a `windows-11-arm` CI runner. ⚠ The
  release job builds the ARM64 shim (the 0.23.2 package's is PE machine `0xaa64`); it has never been loaded.
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

- [ ] 🟡 **A session cookie was missing from a panel recreated in the same process, twice in seven runs.** The
  sequence: a panel in profile A, a panel in profile B opened and closed, A disposed, a new panel in A at once —
  `persist` came back, `session` did not (`SHENORA_SAMPLE_PANEL_PROBE=5`). Without the B panel it kept both, 6 of 6;
  across a restart it kept both every time. A lead, not a cause: count it again before changing anything (flushing
  the context's cookie store on close is the first thing to A/B).
- [ ] **Decide whether history nav generalises onto `ISessionBrowser`.** `GoBack`/`GoForward`/`Reload`/`Stop` are on the
  panel today (Chromium-only, no Core or WebView2 change); WebView2's `CoreWebView2` has the same four, so they could
  move to the shared session contract if a second consumer wants them there.
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
  (`Stalls` stays -1 deliberately — LibVLC has no stall counter).

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

- [ ] **`MobileIpcBridge` has no test.** It is MAUI source compiled into the mobile packages, which the test project
  cannot reference; its gate never closes on a document change (by design). A MAUI-free seam or a device probe.
- [ ] **A request in flight across a reload, on the host.** Nothing cancels it on navigation (only disposing the bridge
  does), and its response goes into the new document, whose client drops the unknown id. Pinned on the client only.
- [ ] **`NotificationPump.Enqueue` after dispose still queues** (no disposed check); bounded and never drained, so
  memory only. WebView2's and the mobile bridge's `SendNotification` reach it; Chromium's `Notify` checks first.

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

### 🟡 The iOS background handoff: the lock screen, and a pause the player never reports

Measured on an iPhone 17 Pro on 2026-10-23 (`docs/design/mobile-shells.md`); the native player now plays in the
background there.

- [ ] **Control Center during the handoff.** Nothing reports the native player to `IPlaybackSession`, so Control
  Center showed stale state and its play/pause did not track the film (the owner's reading; a remote play earlier
  resumed the page's muted `<video>` instead). Report the native player to the session for the time it plays —
  the sample's wiring, or an option on the transfer — and read Control Center on the phone.
- [ ] **`IosMediaPlayer` does not notice the system pausing it.** iOS set the player's rate to 0 while its `Status`
  still read `Playing`, so the handback said `Resumed` at a frozen position. Observe `timeControlStatus` and report
  the pause (or the wait) as the state it is.

### 🟡 What the page player's carried-forward position cannot see

`MediaPlayer.Status` carries a playing position forward from the page's last report (`docs/design/mobile-shells.md`).

- [ ] **A rate the element changes by itself is not reported**: a speed picked from the element's own controls, or
  one Chromium refuses (outside 0.0625–16). The position is then carried forward at the rate the host asked for.
  The fix is the hook reporting `ratechange` with `element.playbackRate` (a new optional report field), the estimate
  running at that and `Status.Rate` staying the rate asked for.

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

