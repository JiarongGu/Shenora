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
`IFileLockInspector`) lives in `Shenora`; only the Windows implementation lives here (D20). That is what
lets app logic compile with no Windows reference, and `samples/Shenora.Sample.Logic` is a `net10.0`
project that turns red if it stops being true.

## The run sequence

`UseWindows(options)` registers `WinFormsRunner`, and `ShenoraApplication.Run` executes it. The order is
load-bearing at four points, each marked:

```
1. single-instance gate            ← FIRST: before any hook takes an OS lock
2. WinFormsBootstrap.Initialize    ← before any control exists
3. app.Start()                     ← the shared hook sequence, owned by ShenoraApplication
4. MainForm(services)              ← created, NOT shown
5. IFormInteraction.SetMainForm    ← native services need the window
6. WindowStateManager.AttachTo     ← geometry applied BEFORE the loop shows it
7. Application.Run(form)           ← or the MessageLoop test seam
8. app.Stop()                      ← reverse order, guarded, runs even if startup failed partway
9. guard.Dispose()                 ← LAST and explicit
```

1 is first because a losing launch must answer instantly rather than after building half an app, and
because the WebView2 environment prewarm takes the user-data-folder lock. 2 is before any control
because the DPI and text-rendering settings reject a later call. 6 is before 7 because geometry set
after a form is shown is a visible jump. 9 is explicit — not merely a closed handle — so a `--restarted`
relaunch waiting on the mutex proceeds the moment shutdown work is done.

## Process init: STA or fail

`WinFormsBootstrap.Initialize` is **idempotent** (first call wins) and **throws when the thread is not
STA**, naming `[STAThread]` in the message. Without STA, OLE features — drag-and-drop registration, the
shell dialogs, the clipboard — fail later, inside window creation, as a blocking modal dialog on a window
that is often not visible yet.

It wires all three unhandled-exception channels to one app callback: `Application.ThreadException`
(recoverable), `AppDomain.UnhandledException` (usually terminating), and
`TaskScheduler.UnobservedTaskException` (observed by default, still reported). The app's handler runs
through `AppCallback.Run` — the crash handler must never crash.

⚠ **The last-resort dialog has a per-thread re-entrancy guard.** `MessageBox.Show` runs its own message
loop, so a recurring UI-thread exception is dispatched again while the dialog is up; without the guard
the app accumulates modal dialogs faster than a user can dismiss them.

## Single instance

An OS mutex named per (application, scope) — scope defaults to the install root, so distinct installs
coexist — plus a `RegisterWindowMessage` channel for activation. `TryAcquire` answers **three** ways:
`Acquired`, `AlreadyRunning`, and `Unverified` (the OS would not answer; the guard failed OPEN). Only
`AlreadyRunning` stops the launch.

- **`Unverified` is distinct on purpose.** Both it and `Acquired` mean "keep starting", but an app whose
  reason for being single-instance is a single-writer store may want to refuse or degrade.
- **The `--restarted` handoff** uses the waiting overload (25 s): a relaunch started by the outgoing
  instance overlaps its predecessor's shutdown, while a genuine double-launch keeps the instant answer.
  The blocking wait also observes an abandoned mutex as soon as the kernel does, which the zero-wait path
  can race.
- ⚠ **`ActivateMessageId == 0` is a real failure**, not just "not yet acquired": the session's atom table
  can be exhausted (hit on the dev machine, 2026-08-10). Single instance still works — the mutex is the
  guard — but a second launch exits quietly and nothing comes to the front, so the runner logs a WARNING
  rather than skipping in silence.
- Activation is an `IMessageFilter`, not a `WndProc` override, so any `Form` works with no base class.

## The main window

`OptimizedForm` is double-buffered with a raw `WndProcHook` seam, and optionally frameless. The frameless
technique: `WM_NCCALCSIZE` keeps Windows' native side/bottom resize borders and gives the TOP back to the
client, so the window is edge-resizable and Aero-snap capable with no visible frame and no content inset.

🔴 **Maximize is MANUAL** — the window is sized to the monitor work area via `SetWindowPos` rather than
`WindowState.Maximized`, which on a borderless window left a ~6 px gap on every edge and squared off the
Win11 corners. `SC_MAXIMIZE` routes through the same path, so every maximize route agrees. **`IsAppMaximized`
is the truth, not `Form.WindowState`** — a manual maximize keeps `WindowState.Normal`.

Chrome commands arrive over IPC on `SHENORA.WINDOW` (`WindowCommandModule` ⇄ `WindowCommands`), and the
route names are constants on both sides, pinned by `WireMirrorTests`.

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

## The WebView2 host

`WebViewHost` is the ONE place a WebView2 is configured. `WebViewEnvironment` is separate and built once
per app, because two things can only be decided at environment-creation time: the user-data folder (and
its OS lock) and **custom scheme registration**.

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

`ClipboardService` builds ONE `DataObject` for every representation and sets it once, which is what makes
a copy atomic. ⚠ **It TRANSLATES well-known media types into the formats other Windows apps actually
read** — a PNG filed under `"image/png"` is invisible to Explorer, Word and every browser, so the paste
appears to work and produces nothing. An unrecognised type is stored verbatim, which is correct: it is a
private format only the app that wrote it will ask for.

`UseWindows` registers each service with `TryAdd` (an app registration wins) and **exposes the portable
face beside the Windows one, resolving to the same singleton** (D20). Everything is lazy: constructing a
`WindowsPlaybackSession` or a `WindowsMediaPlayer` builds real machinery, and an app that never plays
anything must not pay for it by calling `UseWindows`.

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

`Sessions/` is a family of browsers that are not the app's window, sharing one `SessionBrowserOptions`
(a `record`, so a session can `with`-override only what it owns) and one event catalogue on the app's
`IEventBus`:

| Type | Shape |
|---|---|
| `RenderSessionPool` / `RenderSession` | pooled, off-screen, leased |
| `StreamingSession` | off-screen, screencast frames out |
| `InteractiveSession` | a real window, modal, human-in-the-loop |

A bare `Session…` name means shared by every kind; `InteractiveSession…` / `StreamingSession…` mean one
kind. ⚠ **`FormClosed` is not the end of a window** — cleanup happens after `Application.Run` returns, or
a WebView2 child leaves a locked profile folder behind.

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
