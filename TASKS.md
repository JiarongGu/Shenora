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

**Status: v0.16.0 is PUBLISHED and VERIFIED LIVE** (tag `v0.16.0`, release commit `f61d410`).
⚠ `src/Directory.Build.props` must stay at `0.16.0` — the workflow owns the bump, and a hand-bump moves
the baseline and skips a release. **Cutting the next one? Read `.claude/knowledge/release-discipline.md`
first**: it carries the by-hand `<Description>` read, the prose-audit-before-the-cut rule, and why a
partial registry read afterwards is lag rather than a half-landed release.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 📦 NO WAY TO SHIP AN IPA TO A DEVICE THE BUILD MAC CANNOT SEE — and the obvious OTA workaround is a wall

`ios deploy --device` requires the phone attached to the build Mac. With the developer AWAY (phone in
hand, Mac at home behind a VPN) there is no supported path at all — and the first thing an adopter
reaches for does not work, which is the part worth the kit's ink.

**Measured by an adopter, 2026-09-11.** `ios provision <bundle>` already mints a fresh 7-day profile
remotely over ssh, so the signing half is solved; what is missing is everything after it.

- 🔴 **IP reachability is not deployability.** The Mac pinged the handset (0% loss) while every debug
  port was closed — `62078`, `58783`, `62087`, `49152` — and `devicectl` stayed `transportType: None`
  with `pairingState: paired`. iOS does not expose lockdownd on a network interface until one USB
  session arms "Connect via network". A SERVICE-layer block that reads exactly like a network fault.
- 🔴 **`itms-services` OTA is a wall for a free personal-team DEVELOPMENT build.** With a schema-valid
  manifest (`software-package` + `display-image` + `full-size-image`), bundle id and version matching
  the app, a trusted TLS chain and the payload serving `200`, `com.apple.appstored` fetched the manifest
  **six times and never once requested the .ipa**. Policy, not a manifest defect — and the generic
  *"Unable to install"* names nothing, so it invites an evening of manifest tuning that cannot work.
- **The route that carried it:** re-sign the built `.app` with the fresh profile (nested frameworks
  inside-out, entitlements extracted from the profile, `codesign --verify --deep --strict` clean) →
  `Payload/<app>.app` → zip → `.ipa` → `pymobiledevice3 apps install` over USB **from a Windows host**.
  No Mac in the room, no Apple ID, no third-party signing service.

**Asks:** (1) **`shenora ios package`** — emit a signed installable `.ipa` from the device build rather
than only installing to an attached device; the framework signing ORDER is the trap, since getting it
wrong fails at launch, not at build. (2) Document the non-Mac install path (usbmuxd +
`pymobiledevice3`/`ideviceinstaller`) in `docs/guides/mobile.md`, **including the OTA dead end**.

⚠ The re-sign needs a **GUI session** on the Mac (codesign cannot use a login keychain over ssh), so this
belongs beside the existing provisioning stub, not in the CLI's portable half.
⚠ D15: one consumer — but the weekly free-tier expiry is what makes it bite. A profile that dies while
the developer is travelling currently means the app is gone until they are home.

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
   **What is left:** struct-layout tests (ClangSharp can generate them, and every per-OS difference is
   pointer-sized, so all three layouts can be checked on x64 Windows).
   - 🔴 **Windows' sandbox exists only through CEF's launcher since CEF 150** (`cef_sandbox.lib` is no longer
     shipped): `bootstrap.exe`, renamed to `{app}.exe`, creates the sandbox and loads a NATIVE `{app}.dll`
     exporting `RunWinMain`, and every subprocess re-enters it. **The owner chose a native shim, sandboxed**:
     `src/Shenora.Chromium/native/shim_win.cpp` (`dev.mjs cef-native`, 284 KB with the static CRT). A
     subprocess runs CEF alone; the browser process starts `{app}.App.dll` through hostfxr, and the sandbox
     and instance travel as runtime properties that no child inherits. **Measured through it (A/B, same
     binaries):** sandboxed, the children ran 3× untrusted + 1× low + 1× high integrity; with `no_sandbox`,
     all five ran at the parent's level; `coreclr.dll` loaded in the browser process and in no child either
     way. ⚠ One child stays high when sandboxed and is unidentified.
     **Left:** the app's build laying out `{app}.exe` (CEF's bootstrap, from CEF's own build, per D81) +
     `{app}.dll` (the shim) + `{app}.App.dll`, and the managed host that reads those runtime properties.
   - **The page bridge needs no renderer code, and the kit's IPC runs over it unchanged** (prototype,
     2026-09-28, sandboxed through the shim). The page `fetch`es `POST /__shenora/ipc` on its own origin,
     the browser process answers from the resource handler on CEF's IO thread, and the host pushes with
     `frame->execute_java_script`. The REAL `@shenora/react` `ShenoraBridge` into the REAL
     `MessageDispatcher` + `IpcHostBridge` + `NotificationPump`: the handshake, an echo whose handler ran on
     CEF's UI thread, a notification, and `NO_HANDLER` for an unknown route. 100 `invoke`s: median 0.9 ms,
     p95 1.7 ms. **The client half is built:** `createChromiumTransport()` in `createHostTransport()`'s
     chain, with `ChromiumTransport` (`MarkHtml`, `PushScript`) on the host side and the marker's names
     mirrored by `WireMirrorTests` (sabotage-verified both ways, which the TS `satisfies keyof` also catches
     at compile time). **Left:** the shell calling `MarkHtml` on every document it serves, including a
     proxied dev-server page; accepting IPC only from the APP's browser AND the app's origin (a page in
     another browser sharing the handler could otherwise make a cross-origin, no-cors POST to the route);
     and promoting the prototype into the shell.
2. **Read the adopter's own probe evidence** when it lands: the debug port's reach across processes,
   CDP-opened tabs, session cookies, Playwright, the round trip, codecs, install size and licences. Do not
   repeat it.
3. **The Windows shell on Views:** a frameless window, native draggable regions, the caption hit-test,
   the bridge over `IpcHostBridge` + `NotificationPump`, a kit-named page transport in
   `createHostTransport`'s chain, and file drops carrying the engine's own paths (owner: the drop-zone
   overlay is likely unneeded). **Constraints the kit's probe set** (CEF 152, Windows, 2026-09-28):
   - 🔴 **A `--remote-debugging-port` on the app's OWN command line opens the port onto the bridge page**,
     with nothing in the settings. `CommandLineArgsDisabled` closes that route, and a port set in the
     settings still works under it. So the shell disables command-line args in production and is the only
     thing that can set a port.
   - **Chromium's windows are in-process, on CEF's UI thread** (the reverse of WebView2's), and a subclass
     installs from THAT thread only. The caption hit-test (Snap Layouts on page-drawn buttons) is answered
     there.
   - CEF raises everything on its UI thread with no synchronization context, so dispatch needs one over
     CEF's UI task runner, or the context-preserving pipeline has no UI thread to preserve.
   - The renderers are a subprocess exe of the shell's choosing. Never let it be the app's exe behind the
     single-instance gate.
   - 🔴 **Every exe that hosts a CEF process needs Windows 10's `supportedOS` manifest.** Without it the
     GPU process crashed three times per run, and with it never (A/B, two runs each, binaries differing
     only in the manifest). Make it a GATE on the shipped exes.
   - **Under Views, the page's drag bar becomes a real caption only when the shell forwards
     `on_draggable_regions_changed` to `set_draggable_regions`**: HTCAPTION with the forwarding, HTCLIENT
     without it, through real routing (`WindowFromPoint`).
3b. **Chromium inside `Shenora.Windows` (D83):** a WinForms control in `WebViewHost`'s shape on CEF's
   multi-threaded message loop, since WinForms owns the main thread, with IPC dispatched on the WinForms
   UI thread as `WebViewIpcBridge` does. It is the adopter's direct path, since it keeps `OptimizedForm` and
   `SecondaryWindows`. The kit's caption-button hole already works over CEF's child windows (measured, CEF
   152). The same shim starts it.
4. **macOS, on the Mac build host:** CEF on the main thread with its own app integration (a search result
   reported macOS message-pump fixes in CefGlue on 2026-09-22; unconfirmed), and Views support there (an
   old CEF forum post says Views is Windows/Linux only; believed fixed since, unconfirmed).
5. **Linux:** the per-OS services are the hard part: a tray over D-Bus (StatusNotifierItem) and file dialogs
   through xdg-desktop-portal.

**The first adopter moves off `OptimizedForm` and `SecondaryWindows`** onto the new shell's window type.
What it keeps (modules, dispatcher, event bus, `ShenoraPaths`) lives in `Shenora` and is engine-neutral
already. `IpcHostBridge` + `NotificationPump` are what both current shells wrap, and `configureBridge({
transport })` works today. ⚠ **On WebView2 today**, `BrowserArguments` appends an app's
`additionalArguments` in ALL modes, so nothing refuses `--remote-debugging-port` on the environment hosting
the bridge page in production. The adopter is safe (its browser builds its OWN environment); the kit is not.

**Measured by the adopter on WebView2, 2026-09-28**: the limits it is leaving are the API's, not Chromium's.
A tab a CDP client opens has no window and raises no event, where Edge 154 on its own profile shows it; a
session cookie ends with the process, with no setting to keep it.

### 🎬 THE PICTURE SURFACE (D80) — Android is answered, iOS is not

Android is done end to end, pixels included: the run, the layer table and the two refutations are in
`docs/design/media.md`. What is left is all on the other shell.

- [ ] **Re-measure the container delta on iOS and on an older WebView before D52's example is trusted.**
  It is cited as the thing the media tier exists for, and it now has one device saying otherwise.
- [ ] **Run the stage ladder on iOS.** The sample drives the same five rungs there and they have never
  executed — WKWebView carries a THIRD layer the Android chain does not (the scroll view's own
  background, which `MobileWebViewTransparency` already clears), so the rung that fails, if one does, is
  the informative outcome. Needs a Mac: nothing on this box compiles the iOS TFM.
- [ ] **Re-check the safe-area probe on iOS**, and that `Shenora.iOS` compiles at all — nothing on this box
  does (`dotnet workload list` → `maui-android` alone). The sample's `Content` became a `Grid` (with
  `SafeAreaEdges.None` to restore edge-to-edge), and that is the property iOS actually reads.

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

### 🟡 D80 MAKES A LATENT iOS DEFECT VISIBLE — a seek before the item is ready

`IosMediaPlayer.SeekCore` seeks unconditionally, and `SeekAsync` only requires a source, not a READY one —
so a `SEEK` arriving while a load is in flight seeks an unprepared `AVPlayerItem`. The adopter paid for
this one: the decoder emits frames before it holds the references they depend on, and **green** is what a
YUV buffer with no luma written looks like, until the next keyframe. Codec-dependent by GOP length, so it
reads as "one file type is broken".

⚠ **It was harmless until now**, which is why it is filed rather than fixed blind: with no surface the
picture was never composited, so the green frames had nowhere to appear.
⚠ **The common case is already safe by design** — `MediaSource.StartAt` is applied from `OnOpened`, i.e.
after `ReadyToPlay`, which is exactly their fix. Only an explicit page seek during `Opening` reaches it.

- [ ] **Defer a seek issued before the item is ready**, and apply it on `OnOpened` like `StartAt` already
  is. Needs a Mac to verify, and the fix belongs in `IosMediaPlayer` rather than the shared state machine
  (Android's `MediaPlayer` queues a seek during prepare itself).

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

### 🔴 ORIENTATION ON iOS IS A REGRESSION THE KIT CAUSED, NOT AN ENHANCEMENT

**Why this is urgent rather than nice-to-have:** the adopter took 0.16.0's orientation, DELETED their own
working `LockPortrait`/`UnlockOrientation`, and now guard everything on
`MobileWindowOrientation.IsSupported` — which was `false` on iOS. Their `Info.plist` permits portrait and
both landscapes, so **their iPhone build lost its portrait lock and nobody has looked.** The fix is built
(`SupportedInterfaceOrientations` + the app-delegate override); only the run is left.

- [ ] **Prove it on an iPhone.** Three things, and the second is the one no reasoning settles: a locked app
  does not rotate; **the lock SURVIVES a device rotation** (the failure the old `requestGeometryUpdate`
  path had, and the reason the delegate mask exists); and `Unlock` hands the decision back rather than
  pinning the current edge. ⚠ Check `IsSupported` is TRUE in the handshake — it goes true only once UIKit
  has asked the delegate, and a false reading there means the capability is advertised absent for that
  whole session. ⚠ Nothing in this repo runs an iOS device, and the sample's iOS head does not compile on
  Windows, so its one-line override is unverified even though the library half is not.

⚠ **Resume deliberately does NOT duplicate `document.visibilitychange`**, which already fires on both shells
— it reports the one thing a throttled, possibly frozen page cannot measure: **how long it was away**. If a
future session is tempted to add a visibility event, that is the reason not to.

### 📱 THE RECREATION CRASH IS FIXED AND AUTOMATIC — one case is still unmeasured

`MobileIpcBridgeOptions.ReleaseHandlerOnDispose` is ON by default and the crash is fixed (8/10 → 0/10 on
API 36). What `HandlerReleaseProbe` has NOT covered is navigation.

- [ ] **Measure the real NAVIGATION case.** ⚠ **Do NOT re-parent the view inside its layout** — that
  shortcut is what this probe tried and it throws `MauiContext should have been set on parent`, which is an
  artefact of re-parenting a handler-less view and says nothing about navigation. MAUI unloads and reloads a
  PAGE while the view keeps its parent. **The faithful mechanic is swapping `Window.Page` away and back**,
  which fires `Unloaded`/`Loaded` on the same page instance. ⚠ It needs a run-once guard: the sample's whole
  probe suite re-runs from `OnLoaded`, so the swap-back would recurse into this probe.
  ⚠ **No adopter needs this yet** — the only one has a single `ContentPage` and no `PushAsync` at all, which
  is the ordinary hybrid shape. It stays filed because the default is ON and the failure is silent.

