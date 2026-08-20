using System.Runtime.InteropServices;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="OptimizedForm"/>.</summary>
public sealed class OptimizedFormOptions
{
    /// <summary>
    /// Custom frameless chrome: no title bar, native side/bottom resize borders kept, manual
    /// work-area maximize, DWM-themed border. False (default) = a normal framed window with
    /// just the rendering optimizations.
    /// </summary>
    public bool FramelessChrome { get; init; }

    /// <summary>
    /// The form fill — set it to the app's page background so form, WebView2, splash and page CSS all
    /// match and startup shows no white flash. Null = the Form default.
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

    /// <summary>
    /// Frameless: the window OWNS the caption-button pixels and paints them itself. The cluster
    /// reported to <see cref="OptimizedForm.SetCaptionButtons"/> is cut out of every child control
    /// that would cover it, so the OS routes real mouse input there — which is what buys Windows 11
    /// Snap Layouts on the maximize button.
    /// <para>
    /// Requires <see cref="FramelessChrome"/> and is inert until rectangles are reported. Pair it
    /// with <see cref="OptimizedForm.CaptionButtonColors"/>.
    /// </para>
    /// </summary>
    public bool NativeCaptionButtons { get; init; }
}

/// <summary>
/// The optimized main-form base: double-buffered rendering, a raw <see cref="WndProcHook"/> seam,
/// and optional borderless "custom chrome". With <see cref="OptimizedFormOptions.FramelessChrome"/>
/// the default title bar is gone and min/max/close/drag/resize are driven from the frontend over IPC
/// (<c>WindowCommandModule</c> here, <c>WindowCommands</c> in @shenora/react).
/// <para>
/// 🔴 Frameless maximize is MANUAL — a <c>SetWindowPos</c> fill of the monitor work area, keeping
/// <c>WindowState.Normal</c> — so <see cref="AppPlacement"/> is the truth and
/// <see cref="Form.WindowState"/> is not. See <c>docs/design/shells.md</c> for the technique.
/// </para>
/// </summary>
public class OptimizedForm : Form, IAppMaximizable
{
    private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int WM_NCCALCSIZE = 0x0083, WM_SYSCOMMAND = 0x0112, WM_NCACTIVATE = 0x0086, WM_NCHITTEST = 0x0084;
    // Sent when the window moves to a monitor with a different scale factor (PerMonitorV2).
    private const int WM_DPICHANGED = 0x02E0;
    private const int HTCLIENT = 1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    // Answering WM_NCHITTEST with HTMAXBUTTON is what makes Windows 11 offer Snap Layouts on a
    // page-drawn maximize button; a frameless window has no real caption for the OS to find.
    private const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const int WM_NCMOUSEMOVE = 0x00A0, WM_NCMOUSELEAVE = 0x02A2,
                      WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
    private const int SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    // A frameless window (custom WM_NCCALCSIZE) can lose the AUTOMATIC Win11 rounding, so it is
    // requested explicitly. 33 = DWMWA_WINDOW_CORNER_PREFERENCE.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWCP_DONOTROUND = 1;

    private readonly OptimizedFormOptions _options;
    private Color? _dwmBorderColor;
    private bool _immersiveDarkMode;
    private bool _maximized;
    private Rectangle _restoreBounds;
    // Caption-button regions in CLIENT px. Empty = every message below falls through untouched.
    private CaptionButtonRegion[] _captionButtons = [];
    private CaptionButtonKind? _hotCaptionButton;
    private CaptionButtonKind? _pressedCaptionButton;
    // The cluster's bounding box: what gets cut out of the covering children and what this form
    // paints. Driven from the REPORTED rects, never guessed — see SetCaptionButtons.
    private Rectangle _captionUnion;
    // Children whose geometry we watch, and the subset we actually gave a region to. Two sets: we
    // must only ever null a region WE set (an app may give its own control one).
    private readonly HashSet<Control> _trackedChildren = [];
    private readonly HashSet<Control> _clippedChildren = [];
    private CaptionButtonColors? _captionButtonColors;
    private readonly CaptionButtonRenderer _captionRenderer = new();

    /// <summary>A form with the default options: double-buffered, framed, no manual maximize.</summary>
    public OptimizedForm() : this(null)
    {
    }

    /// <summary>
    /// A form configured by <paramref name="options"/> (null = defaults). ⚠ Native caption buttons
    /// without frameless chrome THROWS here — a framed window never reaches the hit-test they depend
    /// on, so the alternative is buttons that silently do nothing.
    /// </summary>
    public OptimizedForm(OptimizedFormOptions? options)
    {
        _options = options ?? new OptimizedFormOptions();

        if (_options.NativeCaptionButtons && !_options.FramelessChrome)
        {
            throw new ArgumentException(
                $"{nameof(OptimizedFormOptions.NativeCaptionButtons)} requires " +
                $"{nameof(OptimizedFormOptions.FramelessChrome)}: a framed window already has real " +
                "caption buttons, and none of the custom hit-testing runs for one.",
                nameof(options));
        }

        _dwmBorderColor = _options.DwmBorderColor;
        _immersiveDarkMode = _options.ImmersiveDarkMode;

        // ⚠ NEVER add ControlStyles.UserPaint without an OnPaint: the resize inset then renders as an
        // unpainted WHITE frame (measured). Let the system paint BackColor so no edge is a light flash.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        UpdateStyles();

        if (_options.BackColor is { } backColor) BackColor = backColor;
        if (_options.FramelessChrome) FormBorderStyle = FormBorderStyle.None; // custom chrome

        // No form-level AllowDrop: a drop target is registered PER HWND and DropZoneOverlay registers
        // its own. Setting it here would force OLE/STA on every consumer of this base class.

        // Only a FRAMELESS window maximizes manually, so only it can hold a stale fill.
        // ⚠ SystemEvents holds a STRONG static reference — unsubscribed in Dispose, or the form and
        // its whole control tree leak for the process lifetime.
        if (_options.FramelessChrome)
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // The maximize glyph is maximize-vs-restore, so it repaints whenever that state moves —
        // including via Win+Up, the system menu and a snap, which all route through the manual path.
        MaximizedChanged += (_, _) => InvalidateCaptionButtons();

        if (_options.FramelessChrome && _options.NativeCaptionButtons)
        {
            ControlAdded += OnCaptionChildAdded;
            ControlRemoved += OnCaptionChildRemoved;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Unconditional detach — removing a handler that was never added is a no-op, and a missed
        // SystemEvents unsubscribe keeps this form alive for the process lifetime.
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            ControlAdded -= OnCaptionChildAdded;
            ControlRemoved -= OnCaptionChildRemoved;
            foreach (var child in _trackedChildren.ToArray()) Untrack(child);
            _captionRenderer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Optional raw WndProc hook — the composition seam for message-level extensions without
    /// subclassing. Receives the whole <see cref="Message"/>.
    /// <para>
    /// <b>Return <c>null</c> to let the message fall through</b> to the frameless chrome and
    /// <c>DefWindowProc</c>. Return a value to mark it HANDLED: that value becomes
    /// <see cref="Message.Result"/>, so <c>IntPtr.Zero</c> is the ordinary "handled, nothing to
    /// report". A hook that throws counts as not-handled and the window keeps working.
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Message, IntPtr?>? WndProcHook { get; set; }

    /// <summary>
    /// Called when the OS changes what it is doing to a caption button (hover in/out, press,
    /// release). Never called unless <see cref="SetCaptionButtons"/> registered regions.
    /// <para>
    /// Only needed when <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is OFF: an app that
    /// draws the buttons itself loses every mouse event in those rectangles to the hit-test, so this
    /// is the only way it learns what to render. Invoked GUARDED — a throw cannot take the window down.
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<CaptionButtonState>? CaptionButtonStateChanged { get; set; }

    /// <summary>
    /// The palette this window paints the caption buttons with when
    /// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is on. Null = a neutral fallback
    /// derived from <see cref="Control.BackColor"/>, so a half-wired app sees buttons rather than an
    /// empty rectangle.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public CaptionButtonColors? CaptionButtonColors
    {
        get => _captionButtonColors;
        set { _captionButtonColors = value; InvalidateCaptionButtons(); }
    }

    /// <summary>
    /// Tell the window where the caption buttons are, in CLIENT px, so the OS treats them as the real
    /// thing — chiefly so Windows 11 offers Snap Layouts on the maximize button. Pass an empty list
    /// (the default) to hand every pixel back. With
    /// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> on, the clipped hole is the UNION of
    /// these rectangles. Calling it before the handle exists is supported.
    /// <para>
    /// ⚠ <b>Re-send it whenever the page's layout changes, and call it on the UI THREAD.</b> The
    /// rectangles are a snapshot, so a stale one silently moves the hit-test off the visible button;
    /// and with native caption buttons this reshapes child controls, which is not safe cross-thread
    /// (the kit's own route already marshals through <c>IUiDispatcher</c>).
    /// </para>
    /// </summary>
    /// <param name="regions">The button rectangles; null or empty clears them.</param>
    public void SetCaptionButtons(IReadOnlyList<CaptionButtonRegion>? regions)
    {
        var previousUnion = _captionUnion;
        _captionButtons = regions is { Count: > 0 } ? [.. regions] : [];
        _captionUnion = UnionOf(_captionButtons);

        if (_captionButtons.Length == 0 && (_hotCaptionButton is not null || _pressedCaptionButton is not null))
        {
            // Clearing the regions clears the rendered state too, or whoever draws is left painting a
            // hover that can never end.
            _hotCaptionButton = null;
            _pressedCaptionButton = null;
            RaiseCaptionButtonState();
        }

        ApplyCaptionButtonClip();
        // Repaint what the cluster LEFT as well as where it landed, or a shrinking layout leaves the
        // old buttons painted on pixels the web view has just been handed back.
        if (!previousUnion.IsEmpty) Invalidate(previousUnion);
        InvalidateCaptionButtons();
    }

    private static Rectangle UnionOf(CaptionButtonRegion[] regions)
    {
        var union = Rectangle.Empty;
        var any = false;
        foreach (var region in regions)
        {
            // Not seeded with Rectangle.Empty: Union against an empty rect at the ORIGIN stretches
            // the result all the way to (0,0), which would clip out the entire title bar.
            union = any ? Rectangle.Union(union, region.Bounds) : region.Bounds;
            any = true;
        }
        return union;
    }

    /// <summary>True when this window owns and paints the caption-button pixels.</summary>
    private bool NativeCaptionButtonsEnabled => _options.FramelessChrome && _options.NativeCaptionButtons;

    private void OnClippedChildGeometryChanged(object? sender, EventArgs e)
    {
        // ⚠ A window region is relative to the window's top-left and SURVIVES a resize, so it goes
        // stale the moment the control changes size and leaves a dead strip where the child stopped
        // rendering. Recompute from the stored union.
        ApplyCaptionButtonClip();
        InvalidateCaptionButtons();
    }

    private void OnCaptionChildAdded(object? sender, ControlEventArgs e)
    {
        // A control added AFTER the cluster was reported would cover the buttons.
        ApplyCaptionButtonClip();
        InvalidateCaptionButtons();
    }

    private void OnCaptionChildRemoved(object? sender, ControlEventArgs e)
    {
        if (e.Control is not { } child) return;
        Untrack(child);
    }

    /// <summary>
    /// Watch a child's geometry so its hole stays correct — including <c>HandleCreated</c>, without
    /// which a control added after startup would be skipped once (a region cannot be applied before
    /// the control is realized, and <c>ControlAdded</c> fires first) and never revisited.
    /// </summary>
    private void Track(Control child)
    {
        if (!_trackedChildren.Add(child)) return;
        child.SizeChanged += OnClippedChildGeometryChanged;
        child.LocationChanged += OnClippedChildGeometryChanged;
        child.HandleCreated += OnClippedChildGeometryChanged;
    }

    private void Untrack(Control child)
    {
        if (_trackedChildren.Remove(child))
        {
            child.SizeChanged -= OnClippedChildGeometryChanged;
            child.LocationChanged -= OnClippedChildGeometryChanged;
            child.HandleCreated -= OnClippedChildGeometryChanged;
        }
        ClearOurRegion(child);
    }

    /// <summary>Hand the pixels back — but only if WE took them; an app may have given its own
    /// control a region.</summary>
    private void ClearOurRegion(Control child)
    {
        if (!_clippedChildren.Remove(child)) return;
        if (!child.IsDisposed) child.Region = null;
    }

    /// <summary>
    /// Cut the cluster out of every direct child that would cover it (and restore any that no longer
    /// does). Every child, not one named control: what is on top changes over a window's life —
    /// splash, then web view, then overlays.
    /// </summary>
    private void ApplyCaptionButtonClip()
    {
        if (!NativeCaptionButtonsEnabled || IsDisposed) return;

        // Snapshot: clearing a region can run layout, which may mutate Controls underneath us.
        var children = new Control[Controls.Count];
        Controls.CopyTo(children, 0);

        foreach (var child in children)
        {
            if (child is null || child.IsDisposed) continue;

            // Watch it FIRST and unconditionally: a child with no handle has no hole to compute, and
            // HandleCreated is what brings us back to finish the job.
            Track(child);

            var hole = HoleFor(child);
            if (hole.IsEmpty)
            {
                ClearOurRegion(child);
                continue;
            }

            var full = new Rectangle(Point.Empty, child.Size);
            var region = new Region(full);
            region.Exclude(hole);
            child.Region = region; // Control.Region disposes the previous one
            _clippedChildren.Add(child);
        }

        // A child that has since left Controls still holds our region and our subscriptions.
        foreach (var tracked in _trackedChildren.ToArray())
            if (!Controls.Contains(tracked)) Untrack(tracked);
    }

    /// <summary>
    /// The cluster in <paramref name="child"/>'s own client px, or empty when it does not cover it.
    /// </summary>
    private Rectangle HoleFor(Control child)
    {
        if (_captionUnion.IsEmpty || child.Width <= 0 || child.Height <= 0) return Rectangle.Empty;
        // The union is in this FORM's client px; a window region is in the CHILD's own. Converted via
        // screen coordinates, which is correct whether or not the child fills the form.
        if (!IsHandleCreated || !child.IsHandleCreated) return Rectangle.Empty;
        var topLeft = child.PointToClient(PointToScreen(_captionUnion.Location));
        var hole = new Rectangle(topLeft, _captionUnion.Size);
        // The reported top edge can sit a pixel ABOVE the client origin (measured: y = -1), so the
        // hole is intersected with the child rather than trusted to be inside it.
        hole.Intersect(new Rectangle(Point.Empty, child.Size));
        return hole;
    }

    private void InvalidateCaptionButtons()
    {
        if (!NativeCaptionButtonsEnabled || _captionUnion.IsEmpty) return;
        if (IsDisposed || !IsHandleCreated) return;
        Invalidate(_captionUnion);
    }

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
        // ⚠ Repaint BEFORE telling the app: when this window owns the pixels, changing the state is
        // not enough — without this the buttons never visibly react, silently. No-op when the app draws.
        InvalidateCaptionButtons();
        RaiseCaptionButtonState();
    }

    private void RaiseCaptionButtonState()
    {
        if (CaptionButtonStateChanged is not { } handler) return;
        var state = new CaptionButtonState(_hotCaptionButton, _pressedCaptionButton);
        Shenora.AppCallback.Run(() => handler(state));
    }

    /// <summary>
    /// Paint the caption buttons into the hole cut out of the covering children.
    /// <para>
    /// ⚠ On the FORM, never in a child control placed in the hole: a child becomes the window
    /// <c>WindowFromPoint</c> finds, which puts back the coverage problem the clip exists to remove.
    /// </para>
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!NativeCaptionButtonsEnabled) return;
        _captionRenderer.Paint(e.Graphics, _captionButtons, _captionUnion,
            _hotCaptionButton, _pressedCaptionButton, AppPlacement == WindowPlacement.Maximized, DeviceDpi, BackColor,
            _captionButtonColors);
    }

    /// <summary>
    /// The authoritative placement. 🔴 Frameless chrome maximizes MANUALLY, so
    /// <see cref="Form.WindowState"/> is not the source of truth — this property is.
    /// </summary>
    public WindowPlacement AppPlacement =>
        (_options.FramelessChrome ? _maximized : WindowState == FormWindowState.Maximized)
            ? WindowPlacement.Maximized
            : WindowPlacement.Normal;

    /// <summary>
    /// The windowed geometry to restore to — what <see cref="WindowStateManager"/> must PERSIST while
    /// this window is maximized, since a manual work-area maximize leaves <c>Bounds</c> showing the
    /// work area and <see cref="Form.RestoreBounds"/> showing nothing useful.
    /// </summary>
    public Rectangle AppRestoreBounds => _options.FramelessChrome ? _restoreBounds : RestoreBounds;

    /// <summary>
    /// Apply a saved maximized state once the window is realized.
    /// <see cref="WindowStateManager.Apply(Form)"/> runs before the form is shown and a manual
    /// work-area maximize needs a live handle, so it leaves a marker and this consumes it.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (ReferenceEquals(Tag, WindowStateManager.RestoreMaximizedTag))
        {
            Tag = null;
            if (AppPlacement != WindowPlacement.Maximized) Maximize();
        }

        // A hole can only be cut once the child has a handle, and an app may report its rectangles
        // during construction. Re-apply here, where every child is realized.
        ApplyCaptionButtonClip();
        InvalidateCaptionButtons();
    }

    /// <summary>Raised after the maximize/restore state changes (chrome glyphs resync on it).</summary>
    public event EventHandler? MaximizedChanged;

    /// <summary>
    /// Raise <see cref="MaximizedChanged"/> through the app-callback guard — two of the four raise
    /// paths are inside <c>WndProc</c>, where a subscriber's exception has no catcher.
    /// ⚠ Containment, not isolation: a throwing subscriber still shadows the ones after it.
    /// </summary>
    private void RaiseMaximizedChanged()
    {
        if (MaximizedChanged is not { } handler) return;
        Shenora.AppCallback.Run(() => handler(this, EventArgs.Empty));
    }

    /// <summary>Toggle maximize/restore (the manual work-area path when frameless).</summary>
    public void ToggleMaximize()
    {
        if (AppPlacement == WindowPlacement.Maximized) RestoreFromMax();
        else Maximize();
    }

    /// <summary>Maximize — frameless fills the monitor work area manually; framed uses <see cref="Form.WindowState"/>.</summary>
    public void Maximize()
    {
        if (!_options.FramelessChrome)
        {
            if (WindowState == FormWindowState.Maximized) return;
            WindowState = FormWindowState.Maximized;
            RaiseMaximizedChanged();
            return;
        }

        if (_maximized) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (TryGetRestoreTarget(out var restore)) _restoreBounds = restore;

        if (!TryGetCurrentWorkArea(out var work)) return;
        _maximized = true;
        ApplyCornerPreference(); // square corners while maximized
        FillWorkArea(work);
        RaiseMaximizedChanged();
    }

    /// <summary>
    /// Where this window should go when it is restored — <c>WINDOWPLACEMENT.rcNormalPosition</c>,
    /// Windows' own restore rectangle, not the live rect. <c>GetWindowRect</c> returns the docked half
    /// of a SNAPPED window, so restoring from maximize put it straight back into the snap; Aero Snap
    /// leaves <c>rcNormalPosition</c> at the pre-snap rectangle, so preferring it exits the snap.
    /// (It is documented as WORKSPACE coordinates; <see cref="RestoreFromMax"/> validates the target,
    /// so a top/left-docked taskbar costs accuracy rather than reachability.)
    /// </summary>
    private bool TryGetRestoreTarget(out Rectangle bounds)
    {
        bounds = default;
        if (!IsHandleCreated) return false;

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(Handle, ref placement))
        {
            var normal = Rectangle.FromLTRB(
                placement.rcNormalPosition.left, placement.rcNormalPosition.top,
                placement.rcNormalPosition.right, placement.rcNormalPosition.bottom);
            if (normal.Width > 0 && normal.Height > 0)
            {
                bounds = normal;
                return true;
            }
        }

        // Fall back to the live rect rather than leaving the restore target unset — a stale target is
        // a window that never comes back.
        if (GetWindowRect(Handle, out var wr))
        {
            bounds = Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The CURRENT monitor's work area in exact physical px. ⚠ <c>GetMonitorInfo</c>, never
    /// <see cref="Screen.WorkingArea"/> — the managed value is DPI-mis-scaled on a HiDPI monitor
    /// (~12 px short per edge, measured).
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
    /// Re-apply the maximized fill to whatever monitor the window is on now — a manual maximize is a
    /// one-shot <c>SetWindowPos</c> in physical px, so a monitor move, a scale change or a dock leaves
    /// it the wrong size while still "maximized". Called from <c>WM_DPICHANGED</c> and
    /// <c>SystemEvents.DisplaySettingsChanged</c>.
    /// </summary>
    private void RefreshMaximizedFill()
    {
        if (!_options.FramelessChrome || !_maximized || IsDisposed || !IsHandleCreated) return;
        if (WindowState == FormWindowState.Minimized) return; // nothing to fill while minimized
        if (TryGetCurrentWorkArea(out var work)) FillWorkArea(work);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        // ⚠ SystemEvents raises this on its OWN thread, so it must be marshalled.
        new WinFormsUiDispatcher(this).Post(RefreshMaximizedFill);

    /// <summary>Restore from maximize (the manual path when frameless).</summary>
    public void RestoreFromMax()
    {
        if (!_options.FramelessChrome)
        {
            if (WindowState != FormWindowState.Maximized) return;
            WindowState = FormWindowState.Normal;
            RaiseMaximizedChanged();
            return;
        }

        if (!_maximized) return;
        // Un-minimize first (mirrors Maximize): restoring bounds on a still-minimized window mangles
        // them under WS_MINIMIZE and leaves the window in the taskbar.
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _maximized = false;
        ApplyCornerPreference(); // rounded corners again when windowed

        // ⚠ _restoreBounds is RAW PHYSICAL px from whichever monitor the window was maximized on, which
        // may since have been unplugged, moved or rescaled — restoring to it blind put the window
        // somewhere the user cannot grab. Validate through the window-state stack's own reachability
        // check, and fall back to a centred half-work-area.
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
        RaiseMaximizedChanged();
    }

    /// <summary>
    /// Re-apply the chrome to a new theme at RUNTIME — a light↔dark switch must resync the DWM
    /// border/dark-mode flag and the form fill, or the frame keeps the old theme's edge.
    /// </summary>
    public void ApplyChromeTheme(Color backColor, Color? dwmBorderColor, bool immersiveDarkMode = true)
    {
        BackColor = backColor;
        _dwmBorderColor = dwmBorderColor;
        _immersiveDarkMode = immersiveDarkMode;
        if (_options.FramelessChrome && IsHandleCreated) ApplyDwmChrome();
    }

    /// <inheritdoc />
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Keep Aero snap / edge resize / taskbar min-max without the caption. Null-guarded: the
            // BASE Form constructor evaluates this before our ctor assigns _options, and that early
            // read is bookkeeping only — the value that matters is re-read at handle creation.
            if (_options is { FramelessChrome: true }) cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            return cp;
        }
    }

    /// <inheritdoc />
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
            // COLORREF is 0x00BBGGRR.
            var border = color.R | (color.G << 8) | (color.B << 16);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        ApplyCornerPreference();
    }

    /// <summary>Rounded when WINDOWED, SQUARE when maximized — a maximized window fills the work
    /// area, so rounded corners would clip the content at the edges.</summary>
    private void ApplyCornerPreference()
    {
        if (!IsHandleCreated) return;
        var corner = _maximized || !_options.RoundedCorners ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// The window procedure. Overriding it in a derived form is supported, but call
    /// <c>base.WndProc</c> or the frameless chrome, caption hit-testing and maximize bookkeeping all
    /// stop working — prefer <see cref="WndProcHook"/>, which runs first and
    /// cannot silently swallow the base behaviour.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // The hook is APP CODE inside WndProc, where an escaping exception has no caller and surfaces
        // as WinForms' own BLOCKING modal dialog mid-dispatch. A throwing hook is therefore treated as
        // "did not handle the message" and the window keeps working.
        // ⚠ The message is COPIED because `m` is `ref` and cannot be captured by the guard's lambda
        // (CS1628) — which is why the hook ANSWERS with a result: a write to the copy would be lost.
        var message = m;
        if (WndProcHook is { } hook
            && Shenora.AppCallback.RunOrDefault(() => hook(message), null) is { } result)
        {
            m.Result = result;
            return;
        }

        if (!_options.FramelessChrome)
        {
            base.WndProc(ref m);
            return;
        }

        // The window moved to a monitor with a different scale factor: let WinForms rescale fonts and
        // child controls FIRST, then re-apply our manual fill.
        if (m.Msg == WM_DPICHANGED)
        {
            base.WndProc(ref m);
            RefreshMaximizedFill();
            return;
        }

        // Route the system maximize/restore (Win+Up, system menu, snap) through the manual path so
        // every maximize route agrees.
        if (m.Msg == WM_SYSCOMMAND)
        {
            var cmd = (int)m.WParam & 0xFFF0;
            if (cmd == SC_MAXIMIZE) { Maximize(); return; }
            // ⚠ Intercept SC_RESTORE only when NOT minimized: a minimized window's restore must reach
            // DefWindowProc to un-minimize, and swallowing it left the window stuck in the taskbar.
            if (cmd == SC_RESTORE && _maximized && WindowState != FormWindowState.Minimized)
            {
                RestoreFromMax();
                return;
            }
        }

        // On focus change Windows repaints the (removed) caption in the inactive colour → a grey strip
        // at the top. lParam = -1 suppresses that non-client repaint, keeping the correct result.
        if (m.Msg == WM_NCACTIVATE)
        {
            m.Result = DefWindowProc(Handle, WM_NCACTIVATE, m.WParam, new IntPtr(-1));
            return;
        }

        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            if (_maximized)
            {
                // The window IS sized to the work area, so the client fills it with NO inset —
                // otherwise the native side/bottom resize border shows as a ~6 px gap per edge.
                m.Result = IntPtr.Zero;
                return;
            }
            // Normal: let DefWindowProc compute the standard non-client inset (the native,
            // invisible-on-Win11 resize borders), then give the TOP back to the client.
            var nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            var originalTop = nccsp.rgrc0.top;
            base.WndProc(ref m);
            nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            nccsp.rgrc0.top = originalTop;
            Marshal.StructureToPtr(nccsp, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }

        // ── Page-drawn caption buttons ───────────────────────────────────────────────────────────
        // Claiming the hit-test buys Snap Layouts and COSTS the page every mouse event in those
        // rectangles (Windows now calls them non-client), so hover/press/release are handled here or
        // the buttons show the flyout and stop working.
        if (_captionButtons.Length > 0)
        {
            if (m.Msg == WM_NCMOUSEMOVE)
            {
                SetCaptionButtonState(HitTestToKind((int)m.WParam), _pressedCaptionButton);
                // Do NOT return: DefWindowProc still owns tooltip/leave tracking for the caption.
            }
            else if (m.Msg == WM_NCMOUSELEAVE)
            {
                // The pointer left the non-client area entirely — including into the page.
                SetCaptionButtonState(null, null);
            }
            else if (m.Msg == WM_NCLBUTTONDOWN && HitTestToKind((int)m.WParam) is { } pressed)
            {
                // Swallow the press: DefWindowProc would run the OS's own caption-button loop against
                // a caption this window does not have.
                SetCaptionButtonState(pressed, pressed);
                m.Result = IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_NCLBUTTONUP && HitTestToKind((int)m.WParam) is { } released)
            {
                var wasPressed = _pressedCaptionButton;
                SetCaptionButtonState(released, null);
                // Only act if the press STARTED on this button, matching every other button on the
                // system.
                if (wasPressed == released) InvokeCaptionButton(released);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        // WM_NCCALCSIZE gave the TOP edge to the client area, so DefWindowProc reports HTCLIENT there
        // and Windows cannot resize from the top. Re-add a top resize border: within the strip, answer
        // HTTOP (or the diagonal corners). Below it the app's header still gets the drag.
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var lp = unchecked((int)(long)m.LParam);
                var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var p = PointToClient(screen);

                // Caption buttons win over the resize strip they share the top edge with. Checked
                // while MAXIMIZED too, unlike the resize strip below.
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
    /// Perform what a caption button does, through the SAME public members the page's IPC commands
    /// use (<see cref="ToggleMaximize"/>, <see cref="Form.Close"/>), so the two routes cannot diverge.
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

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public Point ptMinPosition;
        public Point ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
}
