using System.Runtime.InteropServices;

namespace Shenora.WinForms;

/// <summary>Inputs for <see cref="OptimizedForm"/> — the chrome values the source apps hardcoded.</summary>
public sealed class OptimizedFormOptions
{
    /// <summary>
    /// Custom frameless chrome: no title bar, native side/bottom resize borders kept, manual
    /// work-area maximize, DWM-themed border. False (default) = a normal framed window with
    /// just the rendering optimizations.
    /// </summary>
    public bool FramelessChrome { get; init; }

    /// <summary>
    /// The form fill — set it to the app's page background (the family no-white-flash contract:
    /// form = WebView2 = splash = page CSS). Null = the Form default.
    /// </summary>
    public Color? BackColor { get; init; }

    /// <summary>
    /// Frameless: the 1 px Win11 DWM border line's color. Match it to the app's edge color so
    /// the frameless window shows no visible frame. Null = the system default (a light line —
    /// visibly wrong on a dark app).
    /// </summary>
    public Color? DwmBorderColor { get; init; }

    /// <summary>Frameless: DWM immersive dark mode for the non-client parts. Family default true.</summary>
    public bool ImmersiveDarkMode { get; init; } = true;

    /// <summary>
    /// Frameless: rounded corners while windowed (always square while maximized — a maximized
    /// window fills the work area, so rounding would clip the content at the edges; measured).
    /// </summary>
    public bool RoundedCorners { get; init; } = true;

    /// <summary>
    /// Frameless: the top resize strip's thickness in base px (DPI-scaled at runtime, min 6).
    /// The WM_NCCALCSIZE technique gives the top edge to the client, so the top resize border
    /// is re-added by hit-testing this strip.
    /// </summary>
    public int TopResizeBorder { get; init; } = 8;
}

/// <summary>
/// The optimized main-form base, merged from the two desktop siblings: double-buffered
/// rendering + a raw <see cref="WndProcHook"/> seam (from the primary sibling) and the optional
/// borderless "custom chrome" (from the second sibling, with its measured lessons kept as
/// comments below). With <see cref="OptimizedFormOptions.FramelessChrome"/> the default title
/// bar is gone; min/max/close/drag/resize are driven from the frontend over IPC (see
/// <c>WindowCommandFacade</c> in Shenora.WebView2 and <c>WindowCommands</c> in @shenora/react).
///
/// Frameless technique: WM_NCCALCSIZE keeps Windows' native (visually invisible on Win11)
/// side/bottom resize borders but gives the TOP back to the client — so the window is
/// edge-resizable + Aero-snap capable with NO visible frame and NO content inset (the WebView2
/// fills flush). Maximize is done MANUALLY (size the window to the monitor work area via
/// SetWindowPos) rather than WindowState.Maximized — which, for a borderless window, left a
/// ~6 px gap on every edge and squared off the corners. Manual sizing fills the work area
/// exactly AND keeps the Win11 rounded corners; SC_MAXIMIZE (Win+Up / system menu) routes
/// through the same path so all maximize routes are consistent.
/// </summary>
public class OptimizedForm : Form, IAppMaximizable
{
    private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int WM_NCCALCSIZE = 0x0083, WM_SYSCOMMAND = 0x0112, WM_NCACTIVATE = 0x0086, WM_NCHITTEST = 0x0084;
    // Sent when the window moves to a monitor with a different scale factor (PerMonitorV2).
    private const int WM_DPICHANGED = 0x02E0;
    private const int HTCLIENT = 1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    // The caption-button hit-test codes. Answering WM_NCHITTEST with HTMAXBUTTON is the ONLY way to
    // get the Windows 11 Snap Layouts flyout on a page-drawn maximize button — the OS offers it on
    // hover over whatever reports itself as the maximize button, and a frameless window has no real
    // caption for it to find (P5.6).
    private const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const int WM_NCMOUSEMOVE = 0x00A0, WM_NCMOUSELEAVE = 0x02A2,
                      WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
    private const int SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    // Win11 rounded corners. A frameless window (custom WM_NCCALCSIZE) can lose the AUTOMATIC
    // rounding, so it is requested explicitly. 33 = DWMWA_WINDOW_CORNER_PREFERENCE.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWCP_DONOTROUND = 1;

    private readonly OptimizedFormOptions _options;
    private Color? _dwmBorderColor;
    private bool _immersiveDarkMode;
    private bool _maximized;
    private Rectangle _restoreBounds;
    // Page-drawn caption buttons, in CLIENT px. Empty = the app draws none, and every message below
    // falls through untouched — this feature costs nothing until it is used.
    private CaptionButtonRegion[] _captionButtons = [];
    private CaptionButtonKind? _hotCaptionButton;
    private CaptionButtonKind? _pressedCaptionButton;

    public OptimizedForm() : this(null)
    {
    }

    public OptimizedForm(OptimizedFormOptions? options)
    {
        _options = options ?? new OptimizedFormOptions();
        _dwmBorderColor = _options.DwmBorderColor;
        _immersiveDarkMode = _options.ImmersiveDarkMode;

        // Double-buffer only — NEVER add ControlStyles.UserPaint without an OnPaint: with it,
        // the resize-inset/border renders as an unpainted WHITE frame (measured in the source).
        // Let the system paint the (dark) BackColor so no edge is ever a light flash.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        UpdateStyles();

        if (_options.BackColor is { } backColor) BackColor = backColor;
        if (_options.FramelessChrome) FormBorderStyle = FormBorderStyle.None; // custom chrome

        // NO form-level AllowDrop (removed in P5.5 H2). It used to be set here with a DragOver handler,
        // justified as "so a drop-zone manager can see system drag events over the form" — which is not
        // how OLE drop works: a drop target is registered PER HWND, and DropZoneOverlay sets its own
        // AllowDrop and handles all four drag events on itself. Nothing in the kit ever subscribed to
        // the FORM's drag events.
        //
        // So it cost two things and bought none. It made handle creation OLE-dependent, hence
        // STA-dependent, for every consumer of this base class — the trap behind this repo's own
        // earned test rule (handle creation throws inside WndProc on an MTA thread, and WinForms
        // answers with a BLOCKING dialog that stalled a whole suite). And with a DragOver handler but
        // no DragDrop handler, dragging files anywhere over the window showed a COPY CURSOR and then
        // silently discarded the drop — worse than not being a drop target at all.
        //
        // An app that genuinely wants form-level drops sets AllowDrop = true and wires its own
        // handlers; that is plain WinForms and needs nothing from us.

        // Only a FRAMELESS window maximizes manually, so only it can hold a stale fill (P5.5 H2).
        // SystemEvents holds a STRONG static reference, so this must be unsubscribed in Dispose or the
        // form is leaked for the process lifetime.
        if (_options.FramelessChrome)
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Unconditional detach: SystemEvents is a static, process-lifetime publisher, so a missed
        // unsubscribe keeps this form (and its whole control tree) alive forever. Removing a handler
        // that was never added is a no-op, so this needs no matching condition.
        if (disposing) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Optional raw WndProc hook. Return true to mark the message handled (swallow it). The
    /// composition seam for message-level extensions without subclassing (the primary sibling
    /// used it to catch its activation broadcast).
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, bool>? WndProcHook { get; set; }

    /// <summary>
    /// Called when the OS changes what it is doing to a page-drawn caption button (hover in/out,
    /// press, release) — the app's cue to re-render the affordance. Never called unless
    /// <see cref="SetCaptionButtons"/> registered regions. See <see cref="CaptionButtonState"/> for
    /// why the page cannot observe this itself.
    /// <para>App code, so it is invoked GUARDED — a throw here cannot take the window down.</para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<CaptionButtonState>? CaptionButtonStateChanged { get; set; }

    /// <summary>
    /// Tell the window where the page drew its caption buttons, in CLIENT px, so the OS can treat
    /// them as the real thing — chiefly so Windows 11 offers Snap Layouts on the maximize button.
    /// Pass an empty list (the default) to hand every pixel back to the page.
    /// <para>
    /// ⚠ HAS NO EFFECT while a WebView2 covers these pixels, and that CANNOT be fixed from here —
    /// see <see cref="CaptionButtonKind"/> and <c>TASKS.md</c> P5.6. The regions are stored and
    /// hit-tested correctly; the OS simply never asks, because input is routed to the WebView2's
    /// child windows, which belong to the browser PROCESS and cannot be subclassed. Useful today
    /// only for a form whose caption strip is not covered by the WebView2.
    /// </para>
    /// <para>
    /// Re-send this whenever the page's layout changes: the rectangles are a snapshot, and a stale
    /// one silently moves the hit-test away from the button the user can see.
    /// </para>
    /// </summary>
    /// <param name="regions">The button rectangles; null or empty clears them.</param>
    public void SetCaptionButtons(IReadOnlyList<CaptionButtonRegion>? regions)
    {
        _captionButtons = regions is { Count: > 0 } ? [.. regions] : [];
        if (_captionButtons.Length == 0 && (_hotCaptionButton is not null || _pressedCaptionButton is not null))
        {
            // Clearing the regions must also clear any state the app is currently rendering, or it
            // is left painting a hover that can never end.
            _hotCaptionButton = null;
            _pressedCaptionButton = null;
            RaiseCaptionButtonState();
        }
    }

    // No public `HotCaptionButton` property: CaptionButtonStateChanged already delivers it, and a
    // second way to learn the same thing is surface that has to be maintained forever for nothing
    // (generic-library: every public member earns its keep, default to internal). Adding it later is
    // non-breaking; removing it would not be.

    private CaptionButtonKind? CaptionButtonAt(Point screenPoint)
    {
        if (_captionButtons.Length == 0) return null;
        var client = PointToClient(screenPoint);
        foreach (var region in _captionButtons)
            if (region.Bounds.Contains(client))
                return region.Kind;
        return null;
    }

    private void SetCaptionButtonState(CaptionButtonKind? hot, CaptionButtonKind? pressed)
    {
        if (_hotCaptionButton == hot && _pressedCaptionButton == pressed) return;
        _hotCaptionButton = hot;
        _pressedCaptionButton = pressed;
        RaiseCaptionButtonState();
    }

    private void RaiseCaptionButtonState()
    {
        if (CaptionButtonStateChanged is not { } handler) return;
        var state = new CaptionButtonState(_hotCaptionButton, _pressedCaptionButton);
        Shenora.Core.AppCallback.Run(() => handler(state));
    }

    /// <summary>
    /// True when the window is maximized. Frameless chrome maximizes MANUALLY (fills the work
    /// area) so <see cref="Form.WindowState"/> is NOT the source of truth — this property is.
    /// </summary>
    public bool IsAppMaximized => _options.FramelessChrome ? _maximized : WindowState == FormWindowState.Maximized;

    /// <summary>
    /// The windowed geometry to restore to — what <see cref="WindowStateManager"/> must PERSIST while
    /// this window is maximized, since a manual work-area maximize leaves <c>Bounds</c> showing the
    /// work area and <see cref="Form.RestoreBounds"/> showing nothing useful (P5.5 H2).
    /// </summary>
    public Rectangle AppRestoreBounds => _options.FramelessChrome ? _restoreBounds : RestoreBounds;

    /// <summary>
    /// Apply a saved maximized state once the window is realized. <see cref="WindowStateManager.Apply"/>
    /// runs BEFORE the form is shown, and a manual work-area maximize needs a live handle and a monitor
    /// to measure — so it leaves a marker and this consumes it.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (ReferenceEquals(Tag, WindowStateManager.RestoreMaximizedTag))
        {
            Tag = null;
            if (!IsAppMaximized) Maximize();
        }
    }

    /// <summary>Raised after the maximize/restore state changes (chrome glyphs resync on it).</summary>
    public event EventHandler? MaximizedChanged;

    /// <summary>Toggle maximize/restore (the manual work-area path when frameless).</summary>
    public void ToggleMaximize()
    {
        if (IsAppMaximized) RestoreFromMax();
        else Maximize();
    }

    /// <summary>Maximize — frameless fills the monitor work area manually; framed uses <see cref="Form.WindowState"/>.</summary>
    public void Maximize()
    {
        if (!_options.FramelessChrome)
        {
            if (WindowState == FormWindowState.Maximized) return;
            WindowState = FormWindowState.Maximized;
            MaximizedChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_maximized) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (GetWindowRect(Handle, out var wr))
            _restoreBounds = Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom);

        if (!TryGetCurrentWorkArea(out var work)) return;
        _maximized = true;
        ApplyCornerPreference(); // square corners while maximized
        FillWorkArea(work);
        MaximizedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The CURRENT monitor's work area in exact physical px.
    /// <para>
    /// GetMonitorInfo, never <see cref="Screen.WorkingArea"/> — the managed value is DPI-mis-scaled on
    /// a HiDPI monitor (~12 px short per edge, measured); the P/Invoke rect is exact.
    /// </para>
    /// </summary>
    private bool TryGetCurrentWorkArea(out RECT workArea)
    {
        workArea = default;
        if (!IsHandleCreated) return false;
        var hMon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        workArea = mi.rcWork;
        return true;
    }

    private void FillWorkArea(RECT work) =>
        SetWindowPos(Handle, IntPtr.Zero, work.left, work.top, work.right - work.left, work.bottom - work.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    /// <summary>
    /// Re-apply the maximized fill to whatever monitor the window is on now (P5.5 H2).
    /// <para>
    /// A manual maximize is a one-shot <c>SetWindowPos</c> to one monitor's work area, so nothing kept
    /// it true afterwards: moving a maximized window to a monitor with different DPI or resolution
    /// (Win+Shift+Arrow), changing the display scale, or docking/undocking left the window at the OLD
    /// monitor's physical size — too small (a gap) or too large (spilling off-screen) — while still
    /// believing it was maximized. Called from <c>WM_DPICHANGED</c> and from
    /// <c>SystemEvents.DisplaySettingsChanged</c>.
    /// </para>
    /// </summary>
    private void RefreshMaximizedFill()
    {
        if (!_options.FramelessChrome || !_maximized || IsDisposed || !IsHandleCreated) return;
        if (WindowState == FormWindowState.Minimized) return; // nothing to fill while minimized
        if (TryGetCurrentWorkArea(out var work)) FillWorkArea(work);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        // SystemEvents raises this on its OWN thread, so it must be marshalled — and through the one
        // owner, whose guard matters here because a system-event handler has no caller to catch a throw.
        new WinFormsUiDispatcher(this).Post(RefreshMaximizedFill);

    /// <summary>Restore from maximize (the manual path when frameless).</summary>
    public void RestoreFromMax()
    {
        if (!_options.FramelessChrome)
        {
            if (WindowState != FormWindowState.Maximized) return;
            WindowState = FormWindowState.Normal;
            MaximizedChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_maximized) return;
        // Un-minimize first (mirrors Maximize): restoring bounds on a still-minimized window
        // mangles them under WS_MINIMIZE and leaves the window in the taskbar (found in review).
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _maximized = false;
        ApplyCornerPreference(); // rounded corners again when windowed

        // _restoreBounds is RAW PHYSICAL px captured on whichever monitor the window was maximized from,
        // so it can be unreachable by now: that monitor may have been unplugged, moved in the virtual
        // desktop, or rescaled (P5.5 H2). Restoring to it blind put the window somewhere the user
        // cannot grab it. Reuse the window-state stack's own reachability check rather than a second
        // opinion on what "off-screen" means, and fall back to a centred half-work-area.
        var target = _restoreBounds;
        if (target.Width > 0 && target.Height > 0
            && !WindowStateManager.IsVisible(target.X, target.Y, target.Width, target.Height,
                                             Screen.AllScreens.Select(s => s.Bounds), new WindowStateOptions()))
        {
            target = Rectangle.Empty;
        }

        if (target.Width <= 0 && TryGetCurrentWorkArea(out var work))
        {
            var w = (work.right - work.left) / 2;
            var h = (work.bottom - work.top) / 2;
            target = new Rectangle(work.left + w / 2, work.top + h / 2, w, h);
        }

        if (target.Width > 0)
        {
            _restoreBounds = target;
            SetWindowPos(Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        MaximizedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-apply the chrome to a new theme at RUNTIME — a light↔dark switch must resync the DWM
    /// border/dark-mode flag and the form fill, or the frame keeps the old theme's (near-white
    /// or near-black) edge (measured in the source; its frontend re-sends on every effective
    /// theme change — see <c>WindowCommandOptions.ApplyTheme</c>).
    /// </summary>
    public void ApplyChromeTheme(Color backColor, Color? dwmBorderColor, bool immersiveDarkMode = true)
    {
        BackColor = backColor;
        _dwmBorderColor = dwmBorderColor;
        _immersiveDarkMode = immersiveDarkMode;
        if (_options.FramelessChrome && IsHandleCreated) ApplyDwmChrome();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Keep Aero snap / edge resize / taskbar min-max without the caption. Null-guarded:
            // the BASE Form constructor evaluates this virtual before our ctor assigns _options —
            // that early read is style bookkeeping only; the value that matters is re-read at
            // handle creation, after construction.
            if (_options is { FramelessChrome: true }) cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_options.FramelessChrome) ApplyDwmChrome();
    }

    private void ApplyDwmChrome()
    {
        var dark = _immersiveDarkMode ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (_dwmBorderColor is { } color)
        {
            // COLORREF is 0x00BBGGRR — matching the border line to the app edge means no
            // visible frame on the frameless window.
            var border = color.R | (color.G << 8) | (color.B << 16);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        ApplyCornerPreference();
    }

    /// <summary>Rounded when WINDOWED, SQUARE when maximized — a maximized window fills the work
    /// area, so rounded corners would clip the content at the edges (user-reported in the source).</summary>
    private void ApplyCornerPreference()
    {
        if (!IsHandleCreated) return;
        var corner = _maximized || !_options.RoundedCorners ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // The hook is APP CODE running inside WndProc, which is the worst possible place for an
        // escaping exception (P5.5 H2): there is no caller on the stack, and before the family
        // bootstrap installs its handlers — window creation happens early — an unhandled exception
        // here surfaces as WinForms' own BLOCKING modal dialog, mid-message-dispatch, on a window that
        // may not even be visible yet. The measured version of that failure stalled a whole test suite.
        // A throwing hook is therefore treated as "did not handle the message": the window keeps
        // working and the message falls through to the real handling below, which is the only
        // behaviour that leaves the app usable.
        // msg is copied out first: `m` is a `ref` parameter and cannot be captured by the guard's
        // lambda (CS1628). The hook only ever received the message id anyway.
        var msg = m.Msg;
        if (WndProcHook is { } hook && Shenora.Core.AppCallback.RunOrDefault(() => hook(msg), false))
            return;

        if (!_options.FramelessChrome)
        {
            base.WndProc(ref m);
            return;
        }

        // The window moved to a monitor with a different scale factor. Let WinForms rescale fonts and
        // child controls FIRST, then re-apply our manual fill: a maximized frameless window is sized to
        // one monitor's work area in physical px, so after the move it is the wrong size until we
        // recompute it (P5.5 H2).
        if (m.Msg == WM_DPICHANGED)
        {
            base.WndProc(ref m);
            RefreshMaximizedFill();
            return;
        }

        // Route the system maximize/restore (Win+Up, system menu, snap) through the manual path
        // so every maximize fills the work area exactly (no 6 px gap) and keeps rounded corners.
        if (m.Msg == WM_SYSCOMMAND)
        {
            var cmd = (int)m.WParam & 0xFFF0;
            if (cmd == SC_MAXIMIZE) { Maximize(); return; }
            // Intercept SC_RESTORE only when NOT minimized: a minimized window's restore must
            // reach DefWindowProc to un-minimize (swallowing it left the window in the taskbar
            // with the maximize state silently dropped — found in review); the manual work-area
            // bounds survive un-minimize, so the window comes back still maximized.
            if (cmd == SC_RESTORE && _maximized && WindowState != FormWindowState.Minimized)
            {
                RestoreFromMax();
                return;
            }
        }

        // On focus change Windows repaints the (removed) caption in the inactive colour → a grey
        // strip at the top. lParam = -1 to DefWindowProc suppresses that non-client repaint while
        // keeping the correct active/inactive result.
        if (m.Msg == WM_NCACTIVATE)
        {
            m.Result = DefWindowProc(Handle, WM_NCACTIVATE, m.WParam, new IntPtr(-1));
            return;
        }

        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            if (_maximized)
            {
                // Maximized: the window IS sized to the work area, so make the client fill it
                // with NO inset (otherwise the native side/bottom resize border shows as a ~6 px
                // gap on each edge).
                m.Result = IntPtr.Zero;
                return;
            }
            // Normal: let DefWindowProc compute the standard non-client inset (native,
            // invisible-on-Win11 resize borders), then give the TOP back to the client →
            // caption gone, side/bottom resize kept, frameless look, no content inset.
            // (Returning 0 for ALL sides instead would need a visible inset for resize.)
            var nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            var originalTop = nccsp.rgrc0.top;
            base.WndProc(ref m);
            nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            nccsp.rgrc0.top = originalTop;
            Marshal.StructureToPtr(nccsp, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }

        // ── Page-drawn caption buttons (P5.6) ────────────────────────────────────────────────────
        // Claiming the hit-test is what buys Snap Layouts, and it COSTS the page every mouse event in
        // those rectangles — Windows now considers them non-client, so the WebView2 sees nothing
        // there. That is why the three messages below are handled rather than left to DefWindowProc:
        // without them the buttons would show the flyout and stop working, which is the classic
        // half-done version of this feature.
        if (_captionButtons.Length > 0)
        {
            if (m.Msg == WM_NCMOUSEMOVE)
            {
                SetCaptionButtonState(HitTestToKind((int)m.WParam), _pressedCaptionButton);
                // Do NOT return: DefWindowProc still owns tooltip/leave tracking for the caption.
            }
            else if (m.Msg == WM_NCMOUSELEAVE)
            {
                // The pointer left the non-client area entirely — including into the page, which is
                // the ordinary way out of a button.
                SetCaptionButtonState(null, null);
            }
            else if (m.Msg == WM_NCLBUTTONDOWN && HitTestToKind((int)m.WParam) is { } pressed)
            {
                // Swallow the press. Handing it to DefWindowProc would run the OS's own caption
                // button loop against a caption this window does not have.
                SetCaptionButtonState(pressed, pressed);
                m.Result = IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_NCLBUTTONUP && HitTestToKind((int)m.WParam) is { } released)
            {
                var wasPressed = _pressedCaptionButton;
                SetCaptionButtonState(released, null);
                // Only act if the press STARTED on this button — press here, drag away, release
                // elsewhere must not activate, matching every other button on the system.
                if (wasPressed == released) InvokeCaptionButton(released);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        // WM_NCCALCSIZE gave the TOP edge to the client area, so DefWindowProc reports HTCLIENT
        // there and Windows can't resize from the top. Re-add a top resize border: within the
        // strip, return HTTOP (or the diagonal corners). Below the strip the app's header still
        // gets the drag (mousedown → START_DRAG → HTCAPTION), so the top edge both RESIZES
        // (very edge) and DRAGS (header).
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var lp = unchecked((int)(long)m.LParam);
                var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var p = PointToClient(screen);

                // Caption buttons win over the resize strip: they sit at the top edge, and losing a
                // few pixels of resize border is a far smaller cost than a close button that resizes
                // the window. Checked while MAXIMIZED too — unlike the resize strip below, which is
                // meaningless then.
                if (CaptionButtonAt(screen) is { } kind)
                {
                    m.Result = (IntPtr)(kind switch
                    {
                        CaptionButtonKind.Minimize => HTMINBUTTON,
                        CaptionButtonKind.Maximize => HTMAXBUTTON,
                        _ => HTCLOSE,
                    });
                    return;
                }

                // DpiHelper owns every device-DPI conversion (P5.5 H4.5). It was `* DeviceDpi / 96`
                // here — integer division that only happened to be safe because the multiply came
                // first, and which silently returns 0 for a non-positive DeviceDpi.
                var border = Math.Max(6, DpiHelper.Scale(_options.TopResizeBorder, DpiHelper.ScaleFromDeviceDpi(DeviceDpi)));
                if (!_maximized && p.Y >= 0 && p.Y < border)
                    m.Result = (IntPtr)(p.X < border ? HTTOPLEFT : p.X >= ClientSize.Width - border ? HTTOPRIGHT : HTTOP);
            }
            return;
        }

        base.WndProc(ref m);
    }

    private static CaptionButtonKind? HitTestToKind(int hitTest) => hitTest switch
    {
        HTMINBUTTON => CaptionButtonKind.Minimize,
        HTMAXBUTTON => CaptionButtonKind.Maximize,
        HTCLOSE => CaptionButtonKind.Close,
        _ => null,
    };

    /// <summary>
    /// Perform what a caption button does. Routed through the SAME public members the page's IPC
    /// commands use (<see cref="ToggleMaximize"/>, <see cref="Form.Close"/>), so a click on the
    /// button and a click delivered by the page cannot diverge — in particular the frameless manual
    /// maximize keeps its <see cref="IsAppMaximized"/> bookkeeping either way (P5.5 H2).
    /// </summary>
    private void InvokeCaptionButton(CaptionButtonKind kind)
    {
        switch (kind)
        {
            case CaptionButtonKind.Minimize: WindowState = FormWindowState.Minimized; break;
            case CaptionButtonKind.Maximize: ToggleMaximize(); break;
            default: Close(); break;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
}
