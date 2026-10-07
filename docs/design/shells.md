# The desktop shell — as built

**Maintainer-facing.** What `Shenora.Windows` is made of, in what order it runs, and what each piece
promises. For the invariants you must not break while editing read
[`winforms-shell`](../../.claude/knowledge/winforms-shell.md) and
[`webview2-hosting`](../../.claude/knowledge/webview2-hosting.md); for the mobile shells read
[`mobile-shells.md`](mobile-shells.md); for WHY any of it is this way read the decisions linked below —
**this doc states the design, never the rationale** (D77).

## One package, two halves, one direction

`Shenora.Windows` is a single package (D37) with an internal seam that is enforced rather than
remembered:

| Half | Owns | May reference |
|---|---|---|
| `Shell/` | process init, the window, native services, marshalling | nothing from `WebView/` |
| `WebView/` | WebView2 hosting, serving, the IPC transport, drop zones, window commands | `Shell/` freely |
| `Sessions/` | off-screen and human-in-the-loop browsers | both |

**The direction is `WebView/` → `Shell/`, never the reverse** (D19). Every portable CONTRACT
(`IClipboardService`, `IFileDialogs`, `IUrlLauncher`, `IUiInteraction`, `IUiDispatcher`,
`IFileLockInspector`) lives in `Shenora`; only the Windows implementation lives here (D20), and where one
implementation serves every desktop (`ShellLauncher`, `FileLockInspector`) it lives in `Shenora` too (D88). That is what
lets app logic compile with no Windows reference, and `samples/Shenora.Sample.Logic` is a `net10.0`
project that turns red if it stops being true.

## The run sequence

`UseWindows(options)` registers `WinFormsRunner`, and `ShenoraApplication.Run` executes it. The order is
load-bearing at four points, each marked, and the Chromium engine's three steps (marked C) run only when
`UseChromiumEngine(options)` registered one:

```
C. ChromiumEngine.RunIfSubprocess  ← before everything: CEF's subprocesses exit here
1. single-instance gate            ← FIRST: before any hook takes an OS lock
2. WinFormsBootstrap.Initialize    ← before any control exists
C. engine.Start(app)               ← after the process init, before any hook or form
3. app.Start()                     ← the shared hook sequence, owned by ShenoraApplication
4. MainForm(services)              ← created, NOT shown
5. IFormInteraction.SetMainForm    ← native services need the window
6. WindowStateManager.AttachTo     ← geometry applied BEFORE the loop shows it
7. Application.Run(form)           ← or the MessageLoop test seam
8. app.Stop()                      ← reverse order, guarded, runs even if startup failed partway
C. SecondaryWindows.CloseAll, engine.Stop()  ← every browser closes before CEF does
9. guard.Dispose()                 ← LAST and explicit
```

1 is first because a losing launch must answer instantly rather than after building half an app, and
because the WebView2 environment prewarm takes the user-data-folder lock. 2 is before any control
because the DPI and text-rendering settings reject a later call. 6 is before 7 because geometry set
after a form is shown is a visible jump. 9 is explicit — not merely a closed handle — so a `--restarted`
relaunch waiting on the mutex proceeds the moment shutdown work is done.

The engine's subprocess check comes before the gate because an app that runs an exe of its own with no
launcher beside it is every CEF subprocess too, and one that reached the gate would exit without rendering
(derived, since the build's layout never does it). Started without the launcher, as `dotnet <App>.App.dll`,
CEF is pointed at the laid-out `<App>.exe` for its subprocesses, so none runs .NET (measured on both hosts;
before that, each was `dotnet.exe --type=…` with no app to run, and the app crashed). `engine.Start` comes
before any form because a `ChromiumView` opens its browser as its handle is created. And CEF must not shut
down while a browser is open: a `SecondaryWindows` window outlives the main loop on its own thread, so the
runner closes them, and `Stop` waits a few seconds at most for the browsers still closing.

## Process init: STA or fail

`WinFormsBootstrap.Initialize` is **idempotent** (first call wins) and **throws when the thread is not
STA**, naming `[STAThread]` in the message. Without STA, OLE features — drag-and-drop registration, the
shell dialogs, the clipboard — fail later, inside window creation, as a blocking modal dialog on a window
that is often not visible yet.

It wires all three unhandled-exception channels to one app callback: `Application.ThreadException`
(recoverable), `AppDomain.UnhandledException` (usually terminating), and
`TaskScheduler.UnobservedTaskException` (observed by default, still reported). The app's handler runs
through `AppCallback.Run` — the crash handler must never crash.

The Chromium shell reports the same three to `ChromiumHostOptions.OnUnhandledException`, with the same
`UnhandledExceptionReport` (Core's, D88), wired once per process by its runner after the single-instance gate. Its
UI-thread channel is the dispatcher's: work posted to CEF's UI thread, an `async void` continuation there included,
is caught and the loop goes on, as WinForms' `CatchException` mode does. There is no last-resort dialog: the shell has
no portable one, and what the user sees is the app's.

⚠ **The last-resort dialog has a per-thread re-entrancy guard.** `MessageBox.Show` runs its own message
loop, so a recurring UI-thread exception is dispatched again while the dialog is up; without the guard
the app accumulates modal dialogs faster than a user can dismiss them.

## Single instance

`SingleInstanceGuard` is Core's, and both desktop shells run it (D88). An OS mutex named per (application,
scope) — scope defaults to the install root, so distinct installs coexist — limited to the USER, and on Windows to
the logon session as it always was, but not to the session on Linux and macOS, where a session is one terminal;
plus a named pipe per user (and per Windows session) that only that user can open, which carries a later launch's
arguments and working directory to the running instance. `TryAcquire` answers **three** ways:
`Acquired`, `AlreadyRunning`, and `Unverified` (the OS would not answer; the guard failed OPEN). Only
`AlreadyRunning` stops the launch.

- **`Unverified` is distinct on purpose.** Both it and `Acquired` mean "keep starting", but an app whose
  reason for being single-instance is a single-writer store may want to refuse or degrade.
- **The `--restarted` handoff** uses the waiting overload (25 s): a relaunch started by the outgoing
  instance overlaps its predecessor's shutdown, while a genuine double-launch keeps the instant answer.
  The blocking wait also observes an abandoned mutex as soon as the kernel does, which the zero-wait path
  can race.
- **The running instance listens once it can come forward** (WinForms: the main form's handle created, so an app
  that starts hidden is reachable; Chromium: the main window open), and only when it `Acquired` the scope. It brings the main window to the front,
  restored and shown, then runs `OnActivated` with the launch. A channel that cannot be opened is logged as a
  WARNING: single instance still works, but a later launch exits quietly and nothing comes forward.
- **The losing launch hands the foreground over** on Windows (`AllowSetForegroundWindow`), since it holds it and
  Windows keeps it from a process the user is not using.
- **A launch that meets an instance starting or shutting down waits for one of them** (`ActivateOrTakeOver`, up to
  `RestartWaitTimeout`), taking short turns at handing itself over and at taking the scope. An instance starting takes
  it once it listens. One shutting down stops listening as its shutdown begins (`StopListening`, as its loop ends,
  before the stop hooks) and lets the scope go last, and the launch starts in its place; of two launches meeting
  one shutdown, the one that takes the scope receives the other. Before, a launch waited 5 s for the channel and
  exited, and one meeting a shutdown connected, handed over its arguments, reported success and exited, and nothing
  came forward. Neither within the wait, and the launch exits. The pipe's messages are unchanged, so a launch of this
  version and an instance of an older one still meet.
- **The Chromium shell has a second rule behind the gate: CEF's.** One process per data folder, and a later
  launch on the same folder is handed to it (`on_already_running_app_relaunch`). A process with the app's pages
  always answers it: left unanswered, CEF opens a Chrome-style window there on the app's profile. The shell takes
  it as a launch with NO arguments (Chromium's command line is its own, not the app's), and a `ChromiumView` engine
  logs it. The launch CEF turned away (CEF's exit code 24) exits quietly from the shell, and fails the engine's
  `Start` with a message that says so. The gate runs before CEF starts, in
  `UseChromium` when the app runs from its layout (D87) and in the runner otherwise, after the subprocess check.

## The main window

`OptimizedForm` is double-buffered with a raw `WndProcHook` seam, and optionally frameless. The frameless
technique: `WM_NCCALCSIZE` keeps Windows' native side/bottom resize borders and gives the TOP back to the
client, so the window is edge-resizable and Aero-snap capable with no visible frame and no content inset.

🔴 **Maximize is MANUAL** — the window is sized to the monitor work area via `SetWindowPos` rather than
`WindowState.Maximized`, which on a borderless window left a ~6 px gap on every edge and squared off the
Win11 corners. `SC_MAXIMIZE` routes through the same path, so every maximize route agrees. **`IsAppMaximized`
is the truth, not `Form.WindowState`** — a manual maximize keeps `WindowState.Normal`.

Chrome commands arrive over IPC on `SHENORA.WINDOW` (`WindowCommandModule` ⇄ `WindowCommands`), and the
route names are constants on both sides, pinned by `WireMirrorTests`. The module is mapped once, for the main
window, and each command acts on the window whose page sent it: the page transports (`WebViewIpcBridge`,
`ChromiumView`) run each dispatch as their control, and the module finds that control's form. A page in
another window commands that window, as an `OptimizedForm` or a plain form; only the main window has the
options' callbacks. The Chromium shell's windows also act on the sending page's own window
(`ChromiumWindowCommands`), but a send from no page does nothing there, and a window that paints its caption
buttons has `SET_THEME`. On either shell, a window that paints its caption buttons takes the page's colours
(`SET_CAPTION_BUTTON_COLORS`); the row's height is the page's rects.

**The system menu** is the window's own, whichever way it opens: a page's `SHOW_SYSTEM_MENU`, a right click on a
Chromium page's drag area, Alt+Space, the taskbar. With the real pointer and keys, a right click (on the WebView2
page, through its `contextmenu` handler) and Alt+Space with the page focused opened it on a WebView2 page, a
`ChromiumView` page and the Chromium shell: each engine hands an unhandled Alt+Space to its window by itself. Three
things make that true:
- **A frameless window needs `WS_SYSMENU`, and neither shell's has it by default.** WinForms gives a borderless
  form none, and CEF creates a frameless window without it (style `0x16C70000`, measured). Without it there is
  no menu to open, and `GetSystemMenu` can answer with a menu missing its items, so both helpers refuse a menu with
  none.
- **The items follow the placement the kit keeps.** A manual maximize looks Normal to the system, which would
  offer Maximize, Move and Size. So `FormCaption.UpdateSystemMenu` sets the items before the kit's own menu opens,
  and `OptimizedForm` sets them again on `WM_INITMENUPOPUP` when the system opens it. A Chromium window
  maximizes the system's way, so the system's items are right.
- **It opens queued, never inline**, unlike `START_DRAG`, whose loop must start while the button is down. The
  menu is a modal loop, and inline it would hold the route's answer until the menu closed.

## Window state, and the DPI rule

The process is PerMonitorV2, where a form's OUTER size set in code is device px and is not auto-scaled.
So the store holds **logical px** (physical ÷ the form's current-monitor `DeviceDpi`) and restore
multiplies by the DPI resolved fresh this launch; the DPI itself is never persisted.

🔴 **`Apply` MOVES the handle to the saved position FIRST**, then resolves the scale: the handle is
created wherever Windows first places the form (typically the primary monitor), so a `DeviceDpi` read at
`HandleCreated` is the wrong monitor's. Nothing self-heals it afterwards — WinForms' default
`WM_DPICHANGED` handler does not rescale a Form's outer `Size`.

An off-screen saved position is discarded and the window re-centres; a size saved on a bigger display
shrinks to the target's work area.

**The Chromium shell restores the same state** (`ChromiumHostOptions.WindowState` for the main window, and
`ChromiumWindowOptions.StateStore` for a window opened by name, as `SecondaryWindowOptions.StateStore` is), with none of
that DPI arithmetic: CEF's Views measure in device-independent pixels on every OS, and the saved rect goes through as
is, as the window's initial bounds (`get_initial_bounds`); a maximized one opens maximized (`get_initial_show_state`).
It is checked first against every display's WORK AREA (the WinForms shell checks screen bounds), and when dropped the
window is centred on the primary display, so it always opens with bounds to restore to. On one display, or displays
of one scale, the two shells' numbers agree; across monitors of different scales their positions do not carry over,
and a position that lands nowhere is centred. `WindowStateOptions.MinWidth`/`MinHeight` are the window's live
minimum too (`get_minimum_size`), as they are the WinForms form's `MinimumSize`.

CEF has no restore bounds, so the shell keeps the bounds the window last SETTLED at while normal (held 300 ms) and
saves those as it closes (`on_window_closing`), with the maximized flag it last showed. Settled, because macOS
animates a zoom and reports each frame as a normal window's bounds: before the rule, a maximize from the page saved a
frame of the animation (1673×949) as the size to restore to. And a window maximized as it closes drops its newest
normal-looking bounds unsettled, because macOS can end a zoom on its full-screen frame with no maximized change after
it (2 zooms in 21 saved that frame as a Normal size before; none in 25 after).

## The Chromium shell's splash

`ChromiumHostOptions.Splash` (D92). The WinForms shell needs none of this: its form paints `SplashPanel` at once,
while the Chromium shell's first frame waits on Chromium's GPU process (D87). The splash takes what `SplashPanel`
takes, the main window's render area, and leaves the window's frame to the window: it moves, resizes, maximizes,
minimizes and closes from the moment CEF shows it.

```
UseChromium        ← with a Card, CEF is NOT started early (D87's exception); with none it is
Run: gate          ← a launch turned away shows nothing
     SplashSession.Start  ← setup; the card, if any; OnShown work starts on the pool
     cef_initialize       ← holds this thread 360–540 ms; the card draws on its own
     context ready → app.Start (OnStarting) → main window: Opening → Attach
     WindowOpened         ← the splash made over the window's render area, owned by it, not shown
     CEF's show → WindowShown ← the splash shows, never waited on; then the card goes
     main page handshake / closeSplash() → lift once every OnShown has finished
```

- **Three layers.** The component (`SplashContext` + `SplashState`) produces an element tree; `SplashLayout`, the
  same on every OS, turns it into fills, text runs and images in pixels, measuring text through the platform's own
  engine; an `ISplashSurface` per OS paints and presents them. A component's failure costs a frame or the splash,
  never the app. One component draws both windows; `SplashContext.Surface` says which.
- **The card is placed before CEF can say.** `ChromiumWindows.MainWindowPlan` runs `ChromiumWindowGeometry.PlanFor`
  over the OS's work areas (in DIP, taken at the primary display's scale) and the window-state store the window then
  restores from, and `SplashGeometry.CardRect` centres the card on that plan (on its display's work area when the
  window opens maximized). The splash over the window reads the window's own bounds, so nothing there is approximated.
- **The render area** is what the page will draw in: the window's client area; on a frameless window below the title
  strip and inside the resize band Chromium keeps within its edges (`SplashGeometry.ResizeBandDips`, 4: its hit-test
  answers `HTLEFT` and `HTBOTTOM` for the outer 8 pixels at 200 %), none while maximized. What the splash leaves is the
  window's, so its title bar, buttons and edges answer the window's own hit-test.
- **Made before the window shows, shown after.** `WindowOpened`, on CEF's UI thread before CEF's `show`, makes the
  splash owned by the main window from its creation (owned later, the show would raise the main window over it) and
  draws its first frame; `WindowShown`, from a hook right after `show`, reveals it and returns at once, so CEF never
  waits on the splash's thread (measured on Windows: shown 3–7 ms after `show` returns, which itself took 39–85 ms).
  Shown before `show`, it would stand alone until the window appeared, at the wrong size for a window that opens
  maximized, which it is only once shown. The card goes once the splash is on screen, so nothing falls between them. An owned popup of the same process does
  not count as covering the window for Chromium's occlusion tracking, so the page keeps painting under it (measured,
  CEF 154: 106–120 animation frames a second under it; a foreign window over it: hidden, none).
- **It follows the window**: the window's bounds callback snaps it, on its own thread; the OS hides it with its owner.
- **The splash takes the clicks over it**, so none reaches the page loading unseen beneath; a click never activates it
  but brings its window forward, as a click on that window would, so the keyboard stays with the main window.
- **A frameless window's title strip** (`ChromiumSplashOptions.TitleBar`; `KitStrip` decides when) runs from the
  window's show until the page reports caption buttons or drag regions of its own, or the lift, whichever comes first
  (on Linux, where the splash draws it, until the lift).
  CEF reports an empty set of drag regions as each document starts; only a real set ends the strip. It ends at the
  page's first drag regions rather than at the lift because those come 0.1–0.2 s before the page's title bar paints
  (measured), and ending later showed the kit's buttons over the page's own. On Windows the strip's buttons are the
  window's own, painted by `NativeCaptionButtons` and answered by its hit-test, which is what offers Snap Layouts, and
  the rest of it is a drag region; on macOS the traffic lights and a drag region beside them; on Linux the splash draws
  it (below). The strip on Windows and macOS is the window's own area, so it shows the window's background, which the
  window and its browser view paint until the page draws (Views resets it on each theme change, so it is applied again
  there). Its glyphs read on that background (`CaptionButtonPalette.ForBackground`; none or transparent counts as
  light, since CEF paints white) unless the page has set a theme or colours.
- **Windows** (`WindowsSplashSurface`): a layered popup on a per-monitor-aware thread of its own, presented whole with
  `UpdateLayeredWindow`; GDI draws the text (it font-links, so CJK falls back), GDI+ the fills and images. Its bounds
  are read on that thread: read on one that is not per-monitor aware, the client rect comes back scaled, and the splash
  opened at half its size at 200 % (measured). Windows 11 rounds a normal window, so the card cuts all four corners and
  the splash the two at its bottom; a maximized window is square. The card draws its own shadow in a margin (a layered
  window gets none from the compositor) and asks for the foreground as it shows, because a non-foreground window opens
  under the foreground one (measured); Windows' documented rules grant that only to a launch allowed to take the
  foreground, so one started in the background stays under (not yet seen on a real launch: TASKS). The splash over the
  window needs none of it: its owner takes the foreground. Measured on Windows 11 at 200 %: the splash at the client
  area exactly (framed) or below the strip and inside the band (frameless); the title bar, the strip and the close
  button answering the main window's hit-test; 0 of 60 samples behind a resize storm 16 ms apart; maximize, restore and
  minimize followed; the page beneath at 122 frames a second; the card ~0.3 s after launch with no 10-ms sample
  between it and the window showing nothing.
- **macOS** (`MacSplashSurface`): a borderless `NSWindow` made on the main thread, after `MacPlatform.Prepare`, since
  `NSApp` must be CEF's own class before anything makes one. A thread of its own renders through CoreGraphics,
  CoreText and ImageIO into a layer the splash owns (no delegate, so AppKit manages none of it). The card's frames are
  committed from that thread in explicit Core Animation transactions, the only kind that reaches the screen from a
  thread with no run loop, while the main thread composes the app and starts CEF. The splash over the window covers
  its `contentRectForFrameRect:`, less a frameless window's strip, masks its bottom corners, has its frames posted to
  the main thread, which CEF's loop runs, and joins the window as a child once that is on screen. With no saved place
  CEF centres the window with AppKit's `[NSWindow center]`, above the middle, so the card asks AppKit where that is
  (`MacScreens.CentredContentDip`, through a window never shown); centred on the work area it sat ~80 points low.
  Measured through the window server's list (macOS 15; `screencapture` without Screen Recording permission shows only
  the wallpaper): the splash below the 28-point title bar or the 32-point strip, through a move and resize and into
  fullscreen, the page beneath at 64 frames a second, the card's centre the splash's. Its corners and a click on it are
  not yet seen (TASKS).
- **Linux** (`LinuxSplashSurface`): an X11 window on a connection and thread of its own, drawn by cairo and pango and
  put up with `XPutImage`; typed `_NET_WM_WINDOW_TYPE_SPLASH`, undecorated, never given the keyboard. Over the window
  it covers the client window (the manager's frame is outside it), transient for it from its creation, so the manager
  keeps it above it. Without a compositor X marks a window covered entirely as fully obscured, and Chromium stops
  drawing it (0 frames a second and hidden under openbox on Xvfb; 60 and visible with a row left), so the splash never
  covers it all: a frameless window's resize band stays clear on every side, a framed one's bottom row. It sends the
  server only the pixels that changed (an indeterminate bar's slide is a few rows, the whole window megabytes), fades
  through `_NET_WM_WINDOW_OPACITY` where a compositor runs, and has no splash without an X display.
- **The Linux strip is the splash's.** The kit paints no caption buttons on Linux, so the splash covers a frameless
  window's strip and draws it: minimize, maximize (restore when maximized) and close, in the palette Windows paints.
  The kit's `CaptionButtons` model decides hover, press and click from X's pointer events, and a click calls the
  window's own command; a press on the rest becomes a move once it travels 4 pixels (`StripGesture`), handed to the
  manager as `_NET_WM_MOVERESIZE` after the pointer's grab is let go, and two presses within 400 ms (GTK's) maximize or
  restore. It stays until the lift: the page's title bar beneath it is not seen before then. Whether the window is maximized is the manager's `_NET_WM_STATE` (fullscreen counts: it keeps no band
  either). It selects every click, which X would otherwise pass up to the manager's windows.
- **Xlib's default error handler exits the process**, and the Linux splash names a window it does not own, the main
  one, which may be gone by the time a request reaches the server. So it calls `XInitThreads` before its first Xlib
  call (two threads hold connections) and installs a handler that answers errors on its own connections and hands
  every other to the one there before. It also sets `WM_DELETE_WINDOW`, which it ignores: a manager closes a window
  without it by killing its client's connection (ICCCM), and Xlib's I/O error handler would then exit the app.
- Measured on Linux, under openbox on Xvfb: a frameless window's splash inset by its 4-pixel band with the page beneath
  at 60 frames a second; a drag from the strip moving the window by exactly the drag, the splash with it; a
  double-click maximizing (no band, the bottom row kept) and a second restoring; a click on its close button ending
  the app; a framed window's splash its client window less the bottom row; the card centred where the window opens and
  gone within one 50-ms sample of the window's splash showing; 30 moves of the window drawing no frame.
- **Until its first frame a frameless window shows Chromium's colours in its strip and band, not its own.** Measured
  under a light Windows theme: `#F3F3F3` from about 0.15 s after the show until the page's first paint (0.44 and
  0.88 s in two runs), never the window's `#1E1E1E`; held dark (D93), `#202020`. On Windows `WindowsSplashCover`
  paints them as the window will meanwhile: a layered popup owned by the main window over its whole client area, under
  the splash, click-through (`WS_EX_TRANSPARENT`), so the window's own strip still takes the drag, the double-click,
  Snap Layouts and the buttons. It is made on CEF's UI thread before the show and shown right after it, not on the
  splash's thread, whose first frame came 110–120 ms after the show. Not before the show: in the one run tried, the
  window then opened under the terminal it was launched from (what was in front at launch was not recorded). It has the desktop's open and close transitions off
  (`DWMWA_TRANSITIONS_FORCEDISABLED`): with them, its pixels came 110 ms after it showed and it faded out over the
  window's colours. It goes at the page's first paint, which `ChromiumBrowser.WatchFirstPaint` learns from Chromium
  over the in-process DevTools protocol (`Page.lifecycleEvent`, within ~35 ms of the pixels) and two animation frames
  after, or at the lift. Measured: light only in the window's first ~0.1–0.15 s, during its own open animation, and
  none after. That animation is the app's to keep or drop (`ChromiumWindowOptions.Animations`, `WindowAnimations`,
  the system's by default): off, the cover's pixels came ~40 ms after the show instead of ~130 ms (one A/B, three runs
  each, before the setting existed).

## The app's colour scheme

`IColorScheme` (D93) is the app's setting, `ColorSchemeState` in both desktop shells. The Chromium shell applies it
with `set_chrome_color_scheme` (`ChromiumColorSchemes`): to Chromium's own context before the first window, and
again on CEF's UI thread after each change, the latest setting winning when changes come faster; to a session's
profile as it opens; and where CEF runs inside a WinForms app (`ChromiumEngine`), from the setting the WinForms shell
registers. The windows' default caption-button palette and the splash's `Dark` follow it too. The WebView2 shell
applies it to each `WebViewHost` given it, as its `CoreWebView2` initializes and after each change, marshalled
through the host's `IUiDispatcher` and released with the control. Measured on the Chromium shell (a probe page
reading `matchMedia`): `prefers-color-scheme` dark when started `Dark`, light when started `Light` or `System` under a
light theme, and the page's own change event on each later change that altered it.

## The WebView2 host

`WebViewHost` is the ONE place a WebView2 is configured. `WebViewEnvironment` is separate and built once
per app, because two things can only be decided at environment-creation time: the user-data folder (and
its OS lock) and **custom scheme registration**.

**Prewarm is worth doing because the expensive part needs nothing from you.** The browser-process spawn
plus user-data init is the dominant chunk of WebView2 startup — **measured at ~1–2 s** — and it needs no
control and no message loop. Started first thing, it overlaps DI build, window-state load and form
creation, so by the time a window wants it the task is usually already done and the remaining wait is
often zero.

- **`InitializeAsync` is idempotent and capped** by `InitTimeout`. A faulted or cancelled attempt is NOT
  cached, so the timeout message's "start again" is advice a caller can act on; an orphaned
  user-data-folder lock would otherwise hang init forever.
- 🔴 **An unregistered deferred scheme throws at COMPOSITION.** WebView2 rejects an unknown scheme in the
  network stack before the `WebResourceRequested` filter is consulted, so the page sees a bare
  `TypeError: Failed to fetch` with nothing host-side. The constructor names the missing registration.
- **The app pipeline travels with the options** (D64), so `app.UseFiles(…)` reaches a SECONDARY window
  too rather than being re-wired per window. A throwing pipeline step fails the window loudly.
- Serving is two mechanisms, both proven: a virtual host over an `IWebViewResourceProvider` (embedded
  bundle) and disk-folder mappings.

## Native services, and the STA rule

Every dialog and every clipboard operation runs on a dedicated STA thread — **never inline on the
WebView2's UI thread**, where a dialog conflicts with its message handling.

`StaThread` has two entry points, and the difference is the apartment's LIFETIME: `RunAsync` gives a call
its own thread (what a blocking modal dialog wants), `RunSharedAsync` queues onto one long-lived **pumped**
apartment (`Application.Run`) — required because OLE must service `OleFlushClipboard` on that thread.

🔴 **Both halves of that are MEASURED, and both failures are silent.** `SetDataObject(copy: true)` ends in
`OleFlushClipboard`, which asks the data object to render every advertised format into global memory through
the apartment's message loop. Over 8 runs of a copy carrying text + files + HTML + PNG + a private format:

| apartment | result |
|---|---|
| set-then-exit (torn down mid-flush) | **~1 run in 6** returned the PNG as ZEROS of the right length and lost `text/html` — no exception, no error |
| long-lived but NOT pumping | **every run** failed |
| long-lived and pumping (what ships) | clean |

⚠ **Two plausible fixes were tried and REJECTED**: `Application.DoEvents()` after the flush made it *worse*
(34/40 against 29/40), and the residual flakiness after the real defects were fixed turned out to sit below
this repo — PowerShell's own `Set-Clipboard` failed alongside it. So a copy losing half of itself is a
design failure, not a flaky environment, and the pumped apartment is what prevents it.

`ClipboardService` builds ONE `DataObject` for every representation and sets it once, which is what makes
a copy atomic. ⚠ **It TRANSLATES well-known media types into the formats other Windows apps actually
read** — a PNG filed under `"image/png"` is invisible to Explorer, Word and every browser, so the paste
appears to work and produces nothing. An unrecognised type is stored verbatim, which is correct: it is a
private format only the app that wrote it will ask for.

`UseWindows` registers each service with `TryAdd` (an app registration wins) and **exposes the portable
face beside the Windows one, resolving to the same singleton** (D20). Everything is lazy: constructing a
`WindowsPlaybackSession` or a `WindowsMediaPlayer` builds real machinery, and an app that never plays
anything must not pay for it by calling `UseWindows`.

**The Chromium shell's `IUiInteraction` disables the main window's Views** (the window and its browser view),
and a main window opened while blocked opens blocked. ⚠ **On Windows it disables the HWND as well**: Views stops
the page's input, not the frame's, and the kit's caption hit-test would still drag the window and press its
caption buttons. **A close the app did not ask for is refused while blocked** (`can_close`): the window manager's,
which the title bar offers (measured on Linux with Alt+F4; macOS by the same path, not tried), and on Windows a
`WM_CLOSE` from a tool (End task, `taskkill`), which still reaches a disabled window; the kit's caption buttons and
the splash strip's do nothing. The app's own `Close()` goes through, because CEF asks within `window->close()`
(measured on Linux), where a flag marks it.

⚠ **The native player is OPT-IN, by name.** Registering it as `IMediaPlayer` would move audio out of the
page's own element and leave `PLAYER_REPORT` landing on a player with no `Report` to take — nothing would
fail, it would quietly stop working.

## Marshalling has ONE owner

`IUiDispatcher`, constructed **per control**, because different targets run different pumps. The
DI-registered one resolves the main form **lazily per call**: the provider is built before the runner
creates the form, so a dispatcher capturing it at registration captures null. After shutdown the form is
reachable but disposed — `UiTargetState.Gone` is a real outcome, not a defensive branch.

## Secondary windows and sessions

`SecondaryWindows` gives each named window its OWN STA thread and pump; `Open` on an existing name
ACTIVATES rather than recreating. Threads are **background**, so an exit never hangs on a forgotten
window, and everything marshals with non-blocking `BeginInvoke` — a blocking `Invoke` from the IPC thread
deadlocks the UI.

The sessions are a family of browsers that are not the app's window, written ONCE in `Shenora.Core.Sessions`
over the shell's `ISessionHost` (D91), sharing one `SessionBrowserOptions` (a `record`, so a session can
`with`-override only what it owns, and a shell's options derive from it) and one event catalogue on the app's
`IEventBus`:

| Type | Shape |
|---|---|
| `RenderSessionPool` / `RenderSession` | pooled, off-screen, leased |
| `StreamingSession` | off-screen, screencast frames out |
| `InteractiveSession` | a real window, modal, human-in-the-loop |

A bare `Session…` name means shared by every kind; `InteractiveSession…` / `StreamingSession…` mean one
kind. The host is the narrow part a shell adds: make a browser on its UI thread (off-screen, or a watched dev
window), share a profile across a pool's browsers (`CreateContext`), and open the interactive window. Almost all
the driving (script, the screencast and its input) is the DevTools protocol, which both engines speak.

This package's `Sessions/` folder is the WebView2 host: parked off-screen forms, one environment per profile
context, and the interactive window as a modal `ShowDialog`, which it runs in a post of its own (`IUiDispatcher.Queue`)
so the caller gets the window as it shows and drives it from inside the nested loop. ⚠ **`FormClosed` is not the end
of a window** — cleanup happens after `Application.Run` returns, or a WebView2 child leaves a locked profile folder
behind. ⚠ **The interactive flow ends with its window**: a close nobody holds (the app exiting, a bare `WM_CLOSE`)
takes the window without cancelling `WindowClosed`, so the session stops waiting on the driver then.

## What is deliberately absent

- **No UI component library, ever** (D13). The splash, the tray menu palette and the drop-zone hover
  class are values the app supplies; the kit ships mechanism.
- **No app FEATURE.** The kit ships primitives and lifecycle hooks; login, updates-as-a-product and
  onboarding belong to the app (D21). `InteractiveSession` ships no driver at all.
- **No platform sniffing on the page.** Capabilities are advertised in the ready handshake (D36); a shell
  that cannot satisfy a capability registers neither the service nor its route.
- **No codec list.** The shell answers what THIS machine decodes (D42) rather than the kit guessing.
- **No `Form.ShowInTaskbar` on a live window.** The setter recreates the HWND, so
  `WindowActivation.ShowTaskbarButton` sets `WS_EX_APPWINDOW` directly.
