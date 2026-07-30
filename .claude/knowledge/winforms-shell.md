# WinForms shell traps — the ones that cost real debugging in `src/Shenora.WinForms/`

The desktop primitives (`WinFormsBootstrap`, `OptimizedForm`, `TrayIcon`, `SecondaryWindows`,
`SingleInstanceGuard`, `WindowStateManager`, `ClipboardService`) sit under everything else, so their
failures look like anything BUT a window bug: a resident process nobody asked for, a stale profile
lock, a maximize button that stops working, a test suite that stalls with no failing test. Each rule
below has an incident behind it. UI-thread marshalling itself lives in `webview2-hosting.md` (the ONE
owner, `IUiDispatcher`) — don't restate it here.

## The rules

- **STA is not optional, and a missing `[STAThread]` must fail at `Initialize`.** Every OLE feature —
  drag-drop registration, the shell file dialogs, the clipboard — needs it, and without it the failure
  lands far away and far worse: handle creation throws inside `WndProc`, and WinForms answers with a
  **BLOCKING modal dialog**, often on a window that isn't visible yet. That is why the test suite has
  its own STA harness (xunit workers are MTA, and it stalled the whole suite with no failing test to
  point at). `WinFormsBootstrap.Initialize` asserts the apartment and puts the fix in the message.
- **Process-global initialization must be IDEMPOTENT.** A second `Initialize` re-registered all three
  exception channels, so every later exception was reported twice and raised two stacked dialogs. The
  natural way to hit it is a library and its host app both being well-behaved. First call wins.
- **Anything that pumps can RE-ENTER you.** `MessageBox.Show` runs its own modal loop, so a UI-thread
  exception that recurs (a broken paint handler, a timer throwing every tick) is dispatched again while
  the dialog is up — re-entering the handler and stacking dialogs unboundedly over a window nobody can
  reach. The last-resort dialog is therefore one-at-a-time per thread; recurrences still reach the
  app's logger, which is where a repeating fault belongs.
- **`CloseReason.UserClosing` does NOT mean "the user".** WinForms reports it for a programmatic
  `Form.Close()` too. So with `TrayIcon.CloseToTray` on, an app whose startup-abort path calls
  `Close()` (a missing WebView2 runtime is that shape) HIDES the window and ships a resident process
  with a tray icon and a window that can never finish loading. The reason code cannot tell them apart —
  close from code with `ExitApplication()` or `Application.Exit()`, never `Close()`. (What does pass
  through: `ApplicationExitCall`, `WindowsShutDown`, `TaskManagerClosing`, `FormOwnerClosing`,
  `MdiFormClosing`.)
- **Frameless chrome maximizes MANUALLY, so `Form.WindowState` is not the source of truth.** Use the
  `IAppMaximizable` seam (`IsAppMaximized` / `AppRestoreBounds`), which `WindowStateManager` prefers
  over the WinForms properties. Reading `WindowState` instead persisted `maximized: false` together
  with the work-area rect, so the next launch filled the work area believing it was NOT maximized: the
  border gap the technique exists to remove came back, the page's glyph was wrong, and clicking
  maximize captured the work-area rect as the restore bounds — making restore a PERMANENT no-op.
- **A manual maximize goes stale, and a manual restore target can become unreachable.** The fill is a
  one-shot `SetWindowPos` to one monitor's work area in physical px, so a monitor move, a scale change
  or a dock/undock leaves it the wrong size while still "maximized" — re-apply on `WM_DPICHANGED` and
  `SystemEvents.DisplaySettingsChanged` (`RefreshMaximizedFill`). The saved restore rect is raw
  physical px from a monitor that may be gone, so validate it with `WindowStateManager.IsVisible` (one
  owner for "can the user reach this") and fall back to a centred work-area rect.
- **`SystemEvents` holds a STRONG static reference** — subscribe only when the window actually needs
  it, and detach in `Dispose(bool)`, or the form and its whole control tree leak for the process
  lifetime. It also raises on its own thread: marshal it.
- **`GetMonitorInfo`, never `Screen.WorkingArea`, for physical geometry.** The managed value is
  DPI-mis-scaled on a HiDPI monitor (~12 px short per edge, measured); the P/Invoke rect is exact.
- **A `SendMessage` probe validates your CODE, never a feature that depends on OS input ROUTING.**
  P5.6 claimed the page-drawn caption buttons for Snap Layouts by answering `WM_NCHITTEST` with
  `HTMAXBUTTON`. It shipped with 10 green unit tests, TWO sabotage verifications, and a live Win32
  probe printing `HTMAXBUTTON` at each button — and the feature had never worked once. Every one of
  those drove `SendMessage(form, …)` **straight at the form**, which is exactly the step real input
  does not take: WebView2 puts a `Chrome_RenderWidgetHostHWND` child over the whole client area, so
  `WindowFromPoint` resolves there and the form is never asked. The tell was in the manual results —
  the two checks that PASSED (clicks work, press-and-cancel works) were the page's own `onClick` and
  ordinary browser behaviour, i.e. passing for reasons that had nothing to do with the feature.
  **So: if the OS decides who receives an input, prove it with `WindowFromPoint`/a real cursor, and
  treat a passing check whose mechanism you cannot name as unverified.** A page-drawn control that
  needs OS treatment also needs the WebView2 child chain to answer `HTTRANSPARENT` for it — the
  covering child is the default, not the exception.
- **A drop target is registered PER HWND.** `OptimizedForm` deliberately does NOT set `AllowDrop`:
  `DropZoneOverlay` registers itself, nothing ever subscribed to the form's drag events, and the
  form-level flag only forced OLE/STA on every consumer of the base class while showing a copy cursor
  for a drop it then silently discarded. An app wanting form-level drops sets `AllowDrop` itself.

## Gotchas / traps

- **`SingleInstanceGuard.TryAcquire` must be idempotent, because an OS mutex is per-thread
  REENTRANT.** A second call used to take a second handle and succeed on the same thread even when this
  process already owned it — so `Dispose` could release only one, the mutex stayed held after shutdown,
  and the fast `--restarted` handoff timed out against a corpse. Already holding it IS success. (The
  guard's real contract is cross-process; in-process tests must hold from a dedicated thread, as a
  second process would.)
- **`SecondaryWindows`: `FormClosed` is NOT the end of the window.** `Application.Run` has not returned
  yet — the form is still disposing its children — so removing the registry entry there let a `Dispose`
  that waits for "no windows left" return mid-teardown, and the process exited while a WebView2 child
  was still shutting down: its user-data folder stayed LOCKED and hung the next launch. Clean up in the
  `finally` after `Application.Run`, the only point that means "finished".
- **A failing `thread.Start()` leaves a phantom registry entry** that nothing can clean up (the thread
  body, the only other cleanup path, never ran), so the name is permanently "already open". Remove the
  entry in a `catch`.
- **Pre-handle intent must be carried in a FLAG, not posted.** The marshal is a deliberate no-op before
  the handle exists (posting would create the handle on the wrong thread and kill the pump), so `Close`
  and `Activate` record `CloseRequested`/`ActivateRequested` and `HandleCreated` replays them. Without
  that, activating a still-opening window was silently dropped — exactly the documented "`Open` on an
  existing name activates it" path a user hits by double-clicking a launcher.
- **`Clipboard.SetText("")` throws.** Empty is app data, not a programming error — an empty selection
  means CLEAR, so `ClipboardService.SetTextAsync` routes it to `Clipboard.Clear()`. A null argument is
  still a caller bug and still throws.
