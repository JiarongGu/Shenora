# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

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

> **DIRECTION (owner, 2026-09-29):** *"for the env we dont have lets just park them (still provide code and
> make sure it runs for testing) until the day we need them then we do a real device verification"*. An
> entry that is only a device run stays parked; one whose CODE is missing gets built and run in the nearest
> environment we have — the simulator, the emulator, the build Mac, WSL, the tests.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 🧭 `Shenora.Chromium` ALONE IS A COMPLETE DESKTOP SHELL — Windows included (D88)

> **DIRECTION (owner, 2026-10-01):** *"yes lets complete this entirely so use Shenora.Chromium along will be
> complete"*. An app on any desktop references `Shenora.Chromium` and nothing of WinForms; `Shenora.Windows`
> stays the small-app (WebView2) choice on Windows.

What `Shenora.Windows` has and the Chromium shell does not, inventoried from the source, in the order to build:
- [ ] **A frameless window's first ~0.1–0.15 s under a light theme, when the app follows it.** The cover (D92) shows
  right after CEF's show, so the window's own first frames, during the desktop's open animation, still show Chromium's
  `#F3F3F3` in a dark app whose `ColorScheme` is `System` (held `Dark`, they are `#202020`). Turning the main window's
  own open animation off brought the cover's pixels to ~40 ms after the show from ~130 ms (one A/B, three runs each);
  showing the cover before the show is not the way (one run: the window opened behind). Decide whether a kit window
  may lose its open animation for this. macOS and Linux first frames are not measured.
- [ ] 🅿️ **The splash's frameless title strip with real input, on Windows.** Measured by hit-test only: the strip
  answers `HTCAPTION` and its close button `HTCLOSE`, and that point answers the page after the lift. A real drag, a
  double-click, and Snap Layouts on a hover over its maximize button need a person's mouse.
- [ ] 🅿️ **The Linux splash on a real GNOME or KDE session.** Measured under openbox on Xvfb (order, bounds, the page
  drawing beneath, the strip's drag, double-click and close) and WSLg's Weston (timing, the page drawing); Weston
  keeps no EWMH stacking list, so its order was not seen. Mutter and KWin are compositing managers, where the one-row
  gap a framed window's splash leaves should not be needed; confirm it does no harm there, that they keep a transient
  splash above its owner, and that they carry out the strip's `_NET_WM_MOVERESIZE`.
- [ ] **Two small gaps in the splash on Windows and macOS** (found in review, not fixed): on Windows, a window minimized
  between CEF's show and the splash's first frame (Win+D in those ~40–85 ms) never shows its splash when restored, and
  a card then stays until the lift (`Snap` moves the splash but never shows it); on macOS a frameless window whose app
  paints its own caption buttons (`NativeCaptionButtons` false, the default) has none during the strip, and the strip's
  first 80 DIPs, kept for traffic lights it does not show, are not a drag area.
- [ ] **Two small faults in the Linux splash's strip** (found in review, not fixed): after a click on maximize the
  button stays drawn hot until the pointer next moves (no motion event arrives); and a
  double-click whose second press then drags moves the window it has just maximized.
- [ ] 🅿️ **The macOS splash's PIXELS and real input.** Its presence, order, bounds and lift are measured through the
  window server's list; a screen capture needs Screen Recording permission on the build Mac (without it `screencapture`
  shows only the wallpaper), which a person grants. Then: CJK through CoreText, the splash's rounded bottom corners
  against the window's, a click on the splash bringing its window forward, and a drag on the frameless strip.
- [ ] 🅿️ **The splash card taking the foreground on a real launch, on Windows.** It asks for it, and a launch from a
  background process is refused (measured: it opened under the app the user was in). Whether a double-click from
  Explorer brings it to the front needs a real launch by a person. The splash over the window needs none of this: its
  owner takes the foreground.
- [ ] **A splash drawn by the native launcher, from the previous run's first frame.** .NET's own start (~100 ms) and
  the app's composition still come before any splash. The shim could show the first frame the C# splash saved, before
  .NET starts, and the C# splash adopt that window (on Windows the HWND, through a runtime property as `SandboxInfo`
  travels). Owner, 2026-10-07: a follow-up, decided on v1's numbers.
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

### 🌐 A CHROMIUM SHELL OF THE KIT'S OWN — Windows, macOS and Linux, on CEF's own windows

**Owner, 2026-09-28**, over six answers:
- **A new package id.** The engine's bytes come from the app's restore of an upstream package reference,
  and never sit inside a kit nupkg.
- **The kit builds it now.** The first adopter takes it instead of writing a host of its own.
- **All three desktops, ahead of a consumer**: *"its not really about we have consumer rn or not, we need to
  prepare … the window one without chromium is mostly about small size of the final app"*. So WebView2 stays
  the small-app engine, and Chromium's value is REACH.
- **The shape is a shell the kit owns, on CEF's Views framework** (`CefWindow` + `CefBrowserView`), with
  no Avalonia: *"we mostly not using any winform feature if we going self managed chrome"*. The page draws
  everything and Chromium supplies the window, so the kit owns the CEF binding plus the per-OS native
  services behind the contracts it already has.
- **And `Shenora.Windows` gets Chromium as an engine option** (D83), a WinForms control beside
  `WebViewHost`. `Shenora.Windows` depends on `Shenora.Chromium`, and CEF's bytes arrive only on opt-in.
- **Rejected:** Avalonia + CefGlue (a UI toolkit whose drawing goes unused, with the same binding problem);
  MAUI (no supported way to host CEF on a Mac, since Mac Catalyst is not AppKit, and its Linux support IS
  Avalonia's backend); CefSharp (Windows-only, with no Views API); and CefGlue as-is (CEF 120, no Views).

**The why is D81** (an engine is a package, and its bytes arrive through the app's restore), **D82** (one
shell of the kit's own, on CEF's Views, ahead of a consumer) **and D83** (two hosts, the Windows shell
depending on the engine, and the page finding its transport because the shell marks the HTML). D26, D15,
D37 and D51 are corrected in place to point at them.

**The order, each step measured before the next is shaped:**
1. **The kit's own binding (owner, 2026-09-28), Windows first.** It is generated from CEF's C API headers
   by ClangSharp, once per OS, whenever the pin moves to a new API version; only what the shell uses gets a
   hand-written layer. CefGlue binds CEF **120** and no Views (its `cef_version.h`; no `views` header in its generator's
   125). CEF's own builds are current on all three OSes (**154**, 2026-09-25, in
   `cef-builds.spotifycdn.com/index.json`). **CEF's API versioning is what makes this manageable**: a
   client built for one Stable API version runs on every past and future binary that supports it
   (`chromiumembedded.github.io/cef/api_versioning.html`; CEF 154 supports 13300–15400). So a security
   update is a binary swap, not a regeneration.
   **Proven on Windows, 2026-09-28:** ClangSharp's P/Invoke generator (21.1.8.4) over `include/capi` +
   `capi/views` at `CEF_API_VERSION=15400` produced a binding (297 files, 189 exports), and C# drove CEF 154
   through it alone: a frameless `CefWindow` around a `CefBrowserView`, the page loaded, a clean shutdown.
   **Now tracked:** `node devtools/dev.mjs cef-binding` writes `src/Shenora.Chromium/Interop/Generated/`
   (285 files common to all three OSes and 12–13 per OS, since a build compiles Common plus one), and
   `CefObject` is the memory layer CEF's reference counts drive.
   - 🔴 **Windows' sandbox exists only through CEF's launcher since CEF 150** (`cef_sandbox.lib` is no longer
     shipped): `bootstrap.exe`, renamed to `{app}.exe`, creates the sandbox and loads a NATIVE `{app}.dll`
     exporting `RunWinMain`, and every subprocess re-enters it. **The owner chose a native shim, sandboxed**:
     `src/Shenora.Chromium/native/shim_win.cpp` (`dev.mjs cef-native`, 284 KB with the static CRT). A
     subprocess runs CEF alone; the browser process starts `{app}.App.dll` through hostfxr, and the sandbox
     and instance travel as runtime properties that no child inherits. **Measured through it (A/B, same
     binaries):** sandboxed, the children ran 3× untrusted + 1× low + 1× high integrity; with `no_sandbox`,
     all five ran at the parent's level; `coreclr.dll` loaded in the browser process and in no child either
     way. ⚠ One child runs at the parent's level when sandboxed: the network service, which CEF starts with
     `--service-sandbox-type=none` on Windows. Chromium's `NetworkServiceSandbox` feature, switched on, did not
     change that (measured).
     **The app build does the layout** (`src/Shenora.Chromium/build/Shenora.Chromium.targets`). It fetches
     the pinned build ONCE per machine, checks its SHA-1 against `cef.json` (all six platforms pinned), and
     extracts it. After Build and Publish it places `<App>.exe` (CEF's bootstrap) and `<App>.dll` (the shim)
     beside `<App>.App.dll`. The targets pack into `build/` ONLY, so a direct reference is the opt-in D83
     requires. The package carries the win-x64 and win-arm64 shims in `tools/`, staged by `dev.mjs cef-native
     [--rid win-arm64]`, and packing without either fails. An app with nothing but a `PackageReference` to it, from a local feed, built and ran.
     The package ships, its shim built by `release.yml`'s `chromium-shim` job. A WebView2 app that
     references only `Shenora.Windows` gets its managed code and none of its targets (the dependency
     excludes `Build`, measured from a local feed), and the engine names the package to add.
     **Left:**
     - 🅿️ **win-arm64 has never RUN.** Its shim cross-compiles here (the PE header says ARM64), and an app built
       with `-r win-arm64` lays out an ARM64 launcher, shim and `libcef.dll`. Running it needs ARM64 hardware or
       a `windows-11-arm` CI runner. ⚠ The release job's cross-compile on `windows-latest` is untried until it runs.
     - the Linux and macOS layouts are built and packaged, see 5 and 4.
   - **The page bridge needs no renderer code, and the kit's IPC runs over it unchanged** (prototype,
     2026-09-28, sandboxed through the shim). The page `fetch`es `POST /__shenora/ipc` on its own origin,
     the browser process answers from the resource handler on CEF's IO thread, and the host pushes with
     `frame->execute_java_script`. The REAL `@shenora/react` `ShenoraBridge` into the REAL
     `MessageDispatcher` + `IpcHostBridge` + `NotificationPump`: the handshake, an echo whose handler ran on
     CEF's UI thread, a notification, and `NO_HANDLER` for an unknown route. 100 `invoke`s: median 0.9 ms,
     p95 1.7 ms. **The client half is built:** `createChromiumTransport()` in `createHostTransport()`'s
     chain, with `ChromiumTransport` (`MarkHtml`, `PushScript`) on the host side and the marker's names
     mirrored by `WireMirrorTests` (sabotage-verified both ways, which the TS `satisfies keyof` also catches
     at compile time). Slice 1 (step 3) promoted it into the shell.
2. **The adopter's probe evidence is read and folded in:** D86 (the port, CDP tabs, cookies), the media guide
   (no H.264, AAC or HEVC in CEF's builds) and ADOPTION (install size, licences, CDP's `other` target).
3. **The Windows shell on Views.** **Slice 1 is built** (owner: windows first, multi-view composition in
   slice 2): `builder.UseChromium(new ChromiumHostOptions { … })`, `ChromiumWindows`, CEF's own frameless
   windows with native drag regions, the bundle served and marked, the app's interceptor pipeline behind
   it, IPC over the kit's own bridge, and the `SHENORA.WINDOW` routes. It is 61 tests without CEF, plus an
   end-to-end run through the public API in the shim layout: the real client's DEFAULT bridge found the
   shell by its marker, and a handshake, an echo, `IS_MAXIMIZED` and 100 `invoke`s (median 1.0 ms, p95
   3.6 ms) worked, with a clean exit when the window closed.
   **Page-drawn caption buttons are real ones** (`SET_CAPTION_BUTTONS`, and `useCaptionButtonState` for hover
   and press), Snap Layouts included, and a press behaves as the system's (27 real-cursor checkpoints). Or
   the window paints them (`NativeCaptionButtons`): the system's glyphs, its measured fills, fades and
   `SET_THEME`; their 85/150 ms fades are the system's measured timings, not re-measured on ours. Whether
   either mode shows the system's caption tooltips is unmeasured. With a real pointer, beside a native window:
   the page's drag bar moves the window exactly (a still press stalls nothing), all four edges and two corners
   resize it (the other two corners unmeasured), a double-click maximizes and restores, and dragging a maximized
   window's bar restores it. Mouse only: touch and pen, and a framed window's caption, are left to Chromium.
   **`useDropZone` gets real paths with no overlay** (the page is Alloy style, D84): the page's own drop names
   the zone, and the host answers with the paths CEF reported as the drag entered. A file dropped anywhere
   else no longer navigates the app away. A crashed renderer is reloaded on the WebView2 shell's policy.
   `window.open` and `target=_blank` go to the user's browser through `IUrlLauncher` (http/https only), never to
   a Chromium window. ⚠ On BOTH shells a popup to the app's own origin goes there too, and the browser cannot
   load the app's virtual host. `IFileDialogs` and the page's dialog route are CEF's native dialogs (no
   file-or-folder mode, so `AllowFileSelection` is a folder pick). `IClipboardService` is the Win32
   clipboard, in the WebView2 shell's formats. ⚠ Its pictures are `PNG` only: a bitmap-only copy (Print
   Screen) reads as no picture, since the shell carries no image codec; a page's `navigator.clipboard` has
   images in full. Secondary windows run their own pages, and each page's window commands and drop zones
   act on its own window, a main window opened again included. Development against a real Vite server works
   end to end: the proxied document is marked, IPC runs on the dev origin, and an edit hot-reloads the page
   (D83 has the Chromium checks that are off, and why).
   **Left:** app hooks for downloads, permissions and renderer failure, which wait for an app that asks (the
   defaults are the WebView2 shell's, D84).

   **Constraints the kit's probe set** (CEF 152, Windows, 2026-09-28):
   - 🔴 **A `--remote-debugging-port` on the app's OWN command line opens the port onto the bridge page**,
     with nothing in the settings. `CommandLineArgsDisabled` closes that route, and a port set in the
     settings still works under it. So the shell disables command-line args in production and is the only
     thing that can set a port.
   - **Chromium's windows are in-process, on CEF's UI thread** (the reverse of WebView2's), and a subclass
     installs from THAT thread only. It is where the caption hit-test lives.
   - CEF raises everything on its UI thread with no synchronization context, so dispatch needs one over
     CEF's UI task runner, or the context-preserving pipeline has no UI thread to preserve.
   - The renderers are a subprocess exe of the shell's choosing. Never let it be the app's exe behind the
     single-instance gate.
   - 🔴 **Every exe that hosts a CEF process needs Windows 10's `supportedOS` manifest.** Without it the
     GPU process crashed three times per run, and with it never (A/B, two runs each, binaries differing
     only in the manifest). The exe the kit's layout ships carries it: CEF 154's `bootstrap.exe` and
     `bootstrapc.exe`, which become `<App>.exe`. Started without it (`dotnet <App>.App.dll`) the browser
     process is `dotnet.exe`, which carries it too, and every subprocess still runs as `<App>.exe`. A plain
     .NET apphost does not carry it; the targets turn it off anyway, since CEF's launcher is the app's exe.
   - **Under Views, the page's drag bar becomes a real caption only when the shell forwards
     `on_draggable_regions_changed` to `set_draggable_regions`**: HTCAPTION with the forwarding, HTCLIENT
     without it, through real routing (`WindowFromPoint`).
3b. **`ChromiumView` in `Shenora.Windows` (D83), what is left.** The control and `UseChromiumEngine(options)` ship,
   proven in an `OptimizedForm` with the kit's window commands: the caption hole and Snap Layouts, a real
   `START_DRAG`, drops with real paths, `SecondaryWindows` pages on their own threads, keyboard focus, Tab out
   of the page and back in (to its first element), Shift+Tab out and back in (to its last, as a native control
   takes it; real keys), and the page's `-webkit-app-region: drag` bar with the real pointer: a drag of (200, 120)
   moved the window by exactly that, a still click held the form's thread 16 ms at most, and a double click
   maximized and restored. The areas keep working after a renderer crash, a scroll, a CSS zoom and a browser zoom
   (posted presses).
   - WinForms' `Focused` stays false while the page has the focus, because it is in CEF's child window, which
     another thread owns.
3c. **The system menu, what is left: the real taskbar.** Its window menu opens on a frameless `OptimizedForm` and a
   Chromium window, measured only with the message the taskbar sends (`WM_POPUPSYSTEMMENU`, posted); CEF's window
   ignored it until the shell's subclass answered it. A person Shift+right-clicking the app's taskbar button is
   the check, since a probe must not act on the taskbar, another app's window.
4. **macOS: the shell RUNS from the repo on an Intel Mac** (macOS 15, CEF 154, 2026-09-29). An app built with
   `-r osx-x64` is laid out as `bundle/<App>.app`: the .NET apphost as `Contents/MacOS/<App>`, CEF's framework and
   five helper apps in `Contents/Frameworks`, each helper the kit's `helper_mac.c`, which sandboxes the process
   and runs CEF alone. `NSApp` is the kit's subclass speaking `CefAppProtocol`, registered through the Objective-C
   runtime from C#. CEF's Views works there: the probe's Views window showed its page, visible and painting 60
   frames a second, and it handshook and echoed (100 `invoke`s: median 1.1 ms) and exited cleanly when its window
   closed. Every subprocess ran sandboxed (`sandbox_check`), the network service included; the .NET process does not.
   **The package carries it**: the macOS binding once, as `runtimes/osx/lib/net10.0/` (compiled on Windows by `dev.mjs
   pack`), the helpers in `tools/osx-*/native/` (a macOS release job builds both with clang), and the layout marks
   each helper executable, since a package keeps no Unix permissions. From nuget.org (0.17.0), on the Mac, with
   nothing but a `PackageReference`: the macOS binding reached the app, the release-built x64 helper ran, and the
   page echoed and exited cleanly. The cookie
   key is Chromium's mock keychain's (owner's call): the real one is an item every CEF app shares, and a second kit
   app on the Mac stalled behind a login-password prompt.
   **Left:**
   - osx-arm64 has never run (no Apple Silicon Mac here): all there is, is the 0.17.0 package's helper whose Mach-O
     header says arm64.
   - A framework-dependent app needs .NET installed for every user: a per-user install is not found when the app
     starts from Finder or `open`, whose environment has no `DOTNET_ROOT` ("You must install .NET"). The publish
     layout is the answer for distribution: `dotnet publish -r osx-x64 --self-contained` makes
     `publish/bundle/<App>.app` with the runtime inside, and it started through `open` with no `DOTNET_ROOT`
     (measured). Not yet: signing and notarizing that bundle, which Gatekeeper needs from a download.
   - The window on macOS is frameless by default, and `NativeCaptionButtons` gives it the system's own traffic
     lights (System Events: close, minimize and full-screen buttons with it, none without). `TOGGLE_MAXIMIZE` and
     `MINIMIZE` do what they say (the page grew to 1680×951 and back to 900×600; the window read back minimized),
     and `SET_CAPTION_BUTTONS` answers `NO_ROUTE`. **Unmeasured, because it needs a real pointer:** that the page's
     drag bar moves the window (CEF gets the regions on every OS), and the traffic lights' position over it.
   - The clipboard is `NSPasteboard`, measured on a PRIVATE pasteboard so the owner's was never touched: text, two
     files, HTML, PNG, an app's own type and `application/json` round-tripped in one write; `SetText` replaced
     everything; a write the pasteboard could not take threw and left the previous content; `Clear` emptied it. The
     system pasteboard is the same code, unexercised (writing the owner's clipboard needs their yes).
   - The tray (`ChromiumHostOptions.Tray`) is built on Windows and macOS. Windows is done by real clicks: items
     run, a toggled item shows its checkmark on each next open, a disabled item is grey and never runs, and Exit
     ends the app. On macOS nobody has clicked the status item yet. The file dialogs are CEF's own and should
     work, but a dialog needs a person to answer it. Code signing; osx-arm64 (no Apple Silicon Mac here).
   - Whether the app comes to the front when started from Finder: from `open` over ssh it stayed behind the
     active app, which macOS 15's cooperative activation explains and does not settle.
5. **Linux: the shell RUNS, and the package carries it** (WSL Ubuntu 24.04 with WSLg, X11, CEF 154, 2026-09-30). An app
   built with `-r linux-x64` is laid out flat: CEF's runtime and resources beside the app, the .NET apphost also as
   `<App>`, and the kit's `helper_linux.c` as `<App>-helper`, which every subprocess runs, since the zygote forks the
   renderers and a .NET process must not be forked. The page loaded from the app's origin, handshook and echoed (50
   `invoke`s: median 1.6 ms), and the app exited cleanly when its window closed. Chromium's sandbox is on without a
   setuid `chrome-sandbox`: the renderers and the storage service run in their own user and PID namespaces under
   seccomp. **Packaged**: the binding as `runtimes/linux/lib`, both helpers in `tools/linux-*/native` (the release's
   `chromium-helper-linux` job builds them with zig for glibc 2.28: they need `GLIBC_2.2.5` and `GLIBC_2.17`), and
   from a local feed an app with nothing but a `PackageReference` built, ran, and self-contained-published a folder
   that ran with no .NET installed. The build strips `libcef.so` in the cache (1,455 MB to 272 MB).
   The window and the file dialogs work under a real window manager (openbox on Xvfb; CHANGELOG has the measures).
   **Left:**
   - The `chromium-helper-linux` job has not run in CI yet; its steps were rehearsed on WSL.
   - ⚠ **On WSL the window's close took 8–12 s** in development mode (every run) and in about one production run in
     five, with Chromium's GL on WSL's own (`ZINK: failed to choose pdev` on every run); with SwiftShader or no GPU it
     closed in 15–190 ms. A real Linux desktop with working GL is unmeasured (none here).
   - Wayland: CEF drew through XWayland even with `WAYLAND_DISPLAY` set, and could not reach WSLg's Wayland socket
     when asked to (`--ozone-platform=wayland`) from a `wsl.exe` shell. The Wayland app id is set and unmeasured.
   - WSLg's own window manager (Weston) ignores minimize, xdotool's as well as the page's.
   - A real desktop: its panel's tray (the StatusNotifierItem was measured against a scripted watcher and panel only;
     WSLg has no tray host), including whether a click on the icon RAISES the window past the window manager's
     focus-stealing prevention (no user timestamp or XDG activation token is passed), and its dialogs (Chromium may
     use the XDG desktop portal where one runs; WSL has none).
     And linux-arm64 (its helper is built, never run).
   - The clipboard hands no copy to a clipboard manager at exit (the freedesktop `SAVE_TARGETS` handover), so a copy
     leaves with the app where no manager copies on change; and it writes no format larger than one X request.
   - The machine: WSL had no `sudo`-free gcc, NSS, `bzip2` or binutils; the runs used `apt-get download`ed packages
     (with an index refreshed into user-owned folders) on `LD_LIBRARY_PATH` and `PATH`, and zig as `CC`.

**The first adopter moves off `OptimizedForm` and `SecondaryWindows`** onto the new shell's window type.
What it keeps (modules, dispatcher, event bus, `ShenoraPaths`) lives in `Shenora` and is engine-neutral
already. `IpcHostBridge` + `NotificationPump` are what both current shells wrap, and `configureBridge({
transport })` works today.

**Measured by the adopter on WebView2, 2026-09-28**: the limits it is leaving are the API's, not Chromium's.
A tab a CDP client opens has no window and raises no event, where Edge 154 on its own profile shows it; a
session cookie ends with the process, with no setting to keep it.

The adopter's in-app browser runs on the kit's engine as `ChromiumBrowserProcess` (D86); osx-arm64 is unmeasured
there as it is for the shell (4).

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

### 🟡 Two session leaks the review left for a minor release

Found in review, neither seen live; each needs more than a patch.

- [ ] **A popup a pool lease allowed outlives the lease.** The pool's reset navigates to `about:blank` and closes no
  popup, so the next lease's browser still has the last one's windows. Needs a way for the pool to close a browser's
  popups, which is a new `ISessionBrowser` member.
- [ ] **CEF's `on_before_popup` may leak a reference to the session's client** through its `client**` parameter.
  Whether CEF hands that reference over is the question, and releasing one it did not is a use-after-free, so it is
  settled by experiment: count the client's `OnFreed` after a page opens a popup and the browser closes.

### 🟡 On macOS and Linux a main window blocked by a modal session can still be closed

`IUiInteraction` disables the Chromium shell's main window while an interactive session's window shows. On Windows
`EnableWindow` keeps the user from closing it; on macOS and Linux the title bar still closes it, the loop quits, and
the session's window goes with it. Refusing in `can_close` while blocked would also refuse the app's own `Close`.
Found in review, not seen live.

- [ ] Measure whether `can_close` runs inside `window->close()` (then a flag can tell the app's close from the
  user's), and try the title-bar close on a Mac.

### 🟡 A launch that arrives while the app shuts down is lost

The single-instance channel stays open until the guard is disposed, last in shutdown, so a later launch arriving
after the window has gone connects, hands over its arguments, reports success, and exits; nothing comes forward and
no instance starts. Found in review, not seen live.

- [ ] Stop listening when shutdown begins (a new `SingleInstanceGuard` member, so not a patch), and have a losing
  launch whose activation failed wait for the mutex and start in its place.

### 🟡 A background handoff resumes a film the user had paused

`BackgroundPlaybackTransfer.ToBackgroundAsync` hands off a `Paused` player as well as a `Playing` one, because the
platform pauses a backgrounded element itself before `Window.Stopped` (`docs/design/mobile-shells.md`). So a film the
user paused starts playing natively when they leave the app. Found in review, not seen live.

- [ ] Measure on Android whether the platform's pause arrives while `document.hidden` is already true; if it does,
  `useMediaPlayer` can tell that pause from the user's, and the transfer can hand off only a film that was playing.

### 🟡 A request that completed as a cancel landed is recorded as cancelled

A route that has already answered, whose `CANCEL` lands before the dispatcher ends its scope, is recorded
`Cancelled`: the scope's `Dispose` reads only the token, and the page has its result. Narrow, and it needs the
dispatcher to tell the scope the request SUCCEEDED, which `IIpcRequestScope` (a public seam) has no member for.

- [ ] Decide whether a success-aware end is worth the seam change (found in review, not seen live).

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

