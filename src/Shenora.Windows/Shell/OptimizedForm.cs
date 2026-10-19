using System.Runtime.InteropServices;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="OptimizedForm"/>.</summary>
public sealed class OptimizedFormOptions
{
    /// <summary>
    /// Custom frameless chrome: no title bar, native side/bottom resize borders kept, the system's own
    /// animated minimize, maximize and restore, DWM-themed border. False (default) = a normal framed window
    /// with just the rendering optimizations.
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
/// The frameless window keeps its caption style, which is what makes Windows animate its minimize, maximize and
/// restore, and hides the caption in <c>WM_NCCALCSIZE</c>; it maximizes the system's way, so
/// <see cref="Form.WindowState"/> reads Maximized as it does for any window. See <c>docs/design/shells.md</c> for
/// the technique.
/// </para>
/// </summary>
public class OptimizedForm : Form, IAppMaximizable
{
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
                      WS_SYSMENU = 0x00080000;
    private const int WM_SIZE = 0x0005, WM_NCCALCSIZE = 0x0083, WM_NCACTIVATE = 0x0086, WM_NCHITTEST = 0x0084;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;
    private const int HTCLIENT = 1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    // Answering WM_NCHITTEST with HTMAXBUTTON is what makes Windows 11 offer Snap Layouts on a
    // page-drawn maximize button; a frameless window has no real caption for the OS to find.
    private const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const int WM_NCMOUSEMOVE = 0x00A0, WM_NCMOUSELEAVE = 0x02A2,
                      WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3;
    // A held press on a caption button captures the mouse, as the system's own caption loop does.
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDBLCLK = 0x0209,
                      WM_XBUTTONDOWN = 0x020B, WM_XBUTTONDBLCLK = 0x020D, WM_CAPTURECHANGED = 0x0215, MK_LBUTTON = 0x0001;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    // A frameless window (custom WM_NCCALCSIZE) can lose the AUTOMATIC Win11 rounding, so it is
    // requested explicitly. 33 = DWMWA_WINDOW_CORNER_PREFERENCE.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWCP_DONOTROUND = 1;

    private readonly OptimizedFormOptions _options;
    private Color? _dwmBorderColor;
    private bool _immersiveDarkMode;
    // The placement the corners and MaximizedChanged last followed (SyncPlacement).
    private bool _placedMaximized;
    // Caption-button regions in CLIENT px. Empty = every message below falls through untouched.
    private CaptionButtonRegion[] _captionButtons = [];
    private CaptionButtonKind? _hotCaptionButton;
    private CaptionButtonKind? _pressedCaptionButton;
    // The button a press STARTED on, held until its release; while it is, this form has the mouse capture.
    private CaptionButtonKind? _heldCaptionButton;
    private bool _captionCapture;
    // The cluster's bounding box: what gets cut out of the covering children and what this form
    // paints. Driven from the REPORTED rects, never guessed — see SetCaptionButtons.
    private Rectangle _captionUnion;
    // Children whose geometry we watch, and the subset we actually gave a region to. Two sets: we
    // must only ever null a region WE set (an app may give its own control one).
    private readonly HashSet<Control> _trackedChildren = [];
    private readonly HashSet<Control> _clippedChildren = [];
    private CaptionButtonColors? _captionButtonColors;
    // An inactive window's idle glyphs are dimmed, as the system's are.
    private bool _captionActive = true;
    private readonly CaptionButtonRenderer _captionRenderer = new();

    /// <summary>A form with the default options: double-buffered and framed.</summary>
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

        // The maximize glyph is maximize-vs-restore, so it repaints whenever that state moves —
        // including via Win+Up, the system menu and a snap (SyncPlacement follows every route).
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
        // Unconditional detach — removing a handler that was never added is a no-op.
        if (disposing)
        {
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

        if (_captionButtons.Length == 0)
        {
            // A press held on a button that no longer exists ends here, capture and all.
            _heldCaptionButton = null;
            EndCaptionCapture();
        }
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

    /// <summary>True when this window paints the caption buttons, which a page's <c>SET_CAPTION_BUTTON_COLORS</c>
    /// colours.</summary>
    internal bool PaintsCaptionButtons => NativeCaptionButtonsEnabled;

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

    private CaptionButtonKind? CaptionButtonAt(Point screenPoint) =>
        _captionButtons.Length == 0 ? null : CaptionButtonAtClient(PointToClient(screenPoint));

    private CaptionButtonKind? CaptionButtonAtClient(Point client)
    {
        foreach (var region in _captionButtons)
            if (region.Bounds.Contains(client))
                return region.Kind;
        return null;
    }

    // Our own release changes the capture too, so the flag drops FIRST: the notice it causes is not a cancel.
    private void EndCaptionCapture()
    {
        if (!_captionCapture) return;
        _captionCapture = false;
        if (IsHandleCreated) Capture = false;
    }

    // A held press whose capture is gone: nothing shows and nothing is clicked.
    private void CancelHeldCaption()
    {
        _captionCapture = false;
        _heldCaptionButton = null;
        SetCaptionButtonState(null, null);
    }

    // The capture ended the system's leave tracking, and a click often moves the window from under the pointer
    // (maximize, restore, minimize): without a fresh one, the button stays hot.
    private void TrackNonClientLeave()
    {
        if (!IsHandleCreated) return;
        var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = Handle };
        TrackMouseEvent(ref track);
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
            _captionButtonColors, _captionActive);
    }

    /// <inheritdoc />
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _captionActive = true;
        InvalidateCaptionButtons();
    }

    /// <inheritdoc />
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _captionActive = false;
        InvalidateCaptionButtons();
    }

    /// <summary>
    /// The window's placement: <see cref="Form.WindowState"/>, except that a window minimized from maximized is
    /// still maximized here, since that is where it goes back to and what <see cref="WindowStateManager"/> saves.
    /// </summary>
    public WindowPlacement AppPlacement =>
        WindowState == FormWindowState.Maximized || (WindowState == FormWindowState.Minimized && RestoresToMaximized())
            ? WindowPlacement.Maximized
            : WindowPlacement.Normal;

    /// <summary>The windowed geometry to restore to: <see cref="Form.RestoreBounds"/>.</summary>
    public Rectangle AppRestoreBounds => RestoreBounds;

    private bool RestoresToMaximized()
    {
        if (!IsHandleCreated) return false;
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(Handle, ref placement) && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0;
    }

    /// <summary>
    /// Apply a saved maximized state before the window first shows, so it opens maximized rather than animating
    /// there from its windowed bounds. <see cref="WindowStateManager.Apply(Form)"/> leaves a marker, since a
    /// <see cref="Form.WindowState"/> it set itself would be back to Normal by now (measured on a plain form).
    /// </summary>
    /// <param name="e">The event data.</param>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (WindowStateManager.ConsumeRestoreMaximized(this)) Maximize();
    }

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // A hole can only be cut once the child has a handle, and an app may report its rectangles
        // during construction. Re-apply here, where every child is realized.
        ApplyCaptionButtonClip();
        InvalidateCaptionButtons();
    }

    /// <summary>
    /// No layout while minimized, so the children keep their bounds through a minimize and its restore. Windows gives
    /// a minimized window an empty client area, and a Chromium page laid out into it renders itself again from that
    /// size on restore and reports its caption buttons from that layout first. The restore's own resize lays the
    /// children out again, to the bounds they already have.
    /// </summary>
    /// <param name="levent">The event data.</param>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (IsHandleCreated && IsIconic(Handle)) return;
        base.OnLayout(levent);
    }

    /// <summary>Raised after the maximize/restore state changes (chrome glyphs resync on it).</summary>
    public event EventHandler? MaximizedChanged;

    /// <summary>
    /// Raise <see cref="MaximizedChanged"/> through the app-callback guard — it is raised from
    /// <c>WndProc</c> too, where a subscriber's exception has no catcher.
    /// ⚠ Containment, not isolation: a throwing subscriber still shadows the ones after it.
    /// </summary>
    private void RaiseMaximizedChanged()
    {
        if (MaximizedChanged is not { } handler) return;
        Shenora.AppCallback.Run(() => handler(this, EventArgs.Empty));
    }

    /// <summary>
    /// Follow a placement change, whichever route made it (this form's members, the caption's double click, Win+Up,
    /// a snap, the taskbar): the corners, the event, and a restore that would land where nobody can reach.
    /// </summary>
    private void SyncPlacement()
    {
        var maximized = AppPlacement == WindowPlacement.Maximized;
        if (maximized == _placedMaximized) return;
        _placedMaximized = maximized;
        ApplyCornerPreference();
        if (!maximized) KeepReachable();
        RaiseMaximizedChanged();
    }

    /// <summary>Toggle maximize/restore.</summary>
    public void ToggleMaximize()
    {
        if (AppPlacement == WindowPlacement.Maximized) RestoreFromMax();
        else Maximize();
    }

    /// <summary>Maximize, the system's way: animated, onto the window's monitor's work area, frameless or not.</summary>
    public void Maximize()
    {
        if (WindowState == FormWindowState.Maximized) return;
        WindowState = FormWindowState.Maximized;
        SyncPlacement();
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

    /// <summary>
    /// Restore from maximize, the system's way: animated, back to the windowed bounds. A window minimized from
    /// maximized is restored to those bounds too.
    /// </summary>
    public void RestoreFromMax()
    {
        if (AppPlacement != WindowPlacement.Maximized) return;
        WindowState = FormWindowState.Normal;
        SyncPlacement();
    }

    /// <summary>
    /// Move a restored window that no monitor shows to a centred half of the work area. Its windowed bounds can be
    /// on a monitor since unplugged, moved or rescaled; checked through the window-state stack's own reachability
    /// rule.
    /// </summary>
    private void KeepReachable()
    {
        if (WindowState != FormWindowState.Normal) return;
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0
            || WindowStateManager.IsVisible(bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                            Screen.AllScreens.Select(s => s.Bounds), new WindowStateOptions()))
            return;
        if (!TryGetCurrentWorkArea(out var work)) return;
        var w = (work.right - work.left) / 2;
        var h = (work.bottom - work.top) / 2;
        Bounds = new Rectangle(work.left + w / 2, work.top + h / 2, w, h);
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
            // Keep Aero snap / edge resize / taskbar min-max, and the system menu, which Alt+Space and the taskbar open
            // (WinForms gives a borderless form none). And the CAPTION style, which WM_NCCALCSIZE hides: without it
            // Windows plays no animation as the window minimizes, maximizes or restores (measured, all four). Null-guarded:
            // the BASE Form constructor evaluates this before our ctor assigns _options, and that early
            // read is bookkeeping only — the value that matters is re-read at handle creation.
            if (_options is { FramelessChrome: true })
                cp.Style |= WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            return cp;
        }
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_options.FramelessChrome) ApplyDwmChrome();
    }

    /// <summary>
    /// Size the window so its client is <paramref name="x"/> × <paramref name="y"/>. WinForms sizes the window for its
    /// styles, whose caption a frameless window's client covers, so the client came out taller by the caption (58 px
    /// at 200 %, measured); the window is corrected by the difference it reports itself.
    /// </summary>
    /// <param name="x">The client width.</param>
    /// <param name="y">The client height.</param>
    protected override void SetClientSizeCore(int x, int y)
    {
        base.SetClientSizeCore(x, y);
        if (!_options.FramelessChrome || !IsHandleCreated || WindowState != FormWindowState.Normal
            || !GetClientRect(Handle, out var client))
            return;
        var dx = x - (client.right - client.left);
        var dy = y - (client.bottom - client.top);
        if (dx != 0 || dy != 0) Size = new Size(Width + dx, Height + dy);
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
        var corner = AppPlacement == WindowPlacement.Maximized || !_options.RoundedCorners ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
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

        // Every maximize, restore and minimize arrives here, whichever route made it.
        if (m.Msg == WM_SIZE)
        {
            base.WndProc(ref m);
            SyncPlacement();
            return;
        }

        if (!_options.FramelessChrome)
        {
            base.WndProc(ref m);
            return;
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
            // Let DefWindowProc compute the standard non-client inset (the native, invisible-on-Win11 resize
            // borders, and the caption), then give the TOP back to the client. A maximized window overhangs its
            // monitor by its frame on every side, so its top keeps the inset its sides got: the client is then the
            // work area exactly.
            var nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            var originalTop = nccsp.rgrc0.top;
            var originalLeft = nccsp.rgrc0.left;
            base.WndProc(ref m);
            nccsp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            nccsp.rgrc0.top = IsZoomed(Handle) ? originalTop + (nccsp.rgrc0.left - originalLeft) : originalTop;
            Marshal.StructureToPtr(nccsp, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }

        // ── A press held on a caption button ─────────────────────────────────────────────────────
        // The press took the capture, as the OS's own caption loop does, so its moves and its release
        // arrive here wherever the pointer is: only its button shows, pressed while the pointer is on it,
        // and the release ends it anywhere. Swallowed, since the page never saw the press begin.
        // The flag is checked against the OS: a hook that swallowed WM_CAPTURECHANGED, or a recreated handle,
        // must not leave a press held that no capture backs.
        if (_captionCapture && GetCapture() != Handle) CancelHeldCaption();
        if (_captionCapture)
        {
            if (m.Msg is WM_MOUSEMOVE or WM_LBUTTONUP)
            {
                var lp = unchecked((int)(long)m.LParam);
                var at = CaptionButtonAtClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                var held = _heldCaptionButton;
                // A move with the button already up is a release that never arrived.
                if (m.Msg == WM_MOUSEMOVE && ((int)m.WParam & MK_LBUTTON) != 0)
                {
                    SetCaptionButtonState(at == held ? held : null, at == held ? held : null);
                }
                else
                {
                    _heldCaptionButton = null;
                    EndCaptionCapture();
                    SetCaptionButtonState(at, null);
                    TrackNonClientLeave();
                    if (at is { } released && released == held) InvokeCaptionButton(released);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg is (>= WM_RBUTTONDOWN and <= WM_MBUTTONDBLCLK) or (>= WM_XBUTTONDOWN and <= WM_XBUTTONDBLCLK))
            {
                m.Result = IntPtr.Zero;   // the other buttons, as the system's loop ignores them
                return;
            }
            // Taken away (a menu, another window, Alt+Tab): the press is cancelled, nothing clicked.
            if (m.Msg == WM_CAPTURECHANGED) CancelHeldCaption();
        }

        // ── Page-drawn caption buttons ───────────────────────────────────────────────────────────
        // Claiming the hit-test buys Snap Layouts and COSTS the page every mouse event in those
        // rectangles (Windows now calls them non-client), so hover/press/release are handled here or
        // the buttons show the flyout and stop working.
        if (_captionButtons.Length > 0)
        {
            if (m.Msg == WM_NCMOUSEMOVE)
            {
                var kind = HitTestToKind((int)m.WParam);
                if (_heldCaptionButton is { } held) SetCaptionButtonState(kind == held ? held : null, kind == held ? held : null);
                else SetCaptionButtonState(kind, null);
                // Do NOT return: DefWindowProc still owns tooltip/leave tracking for the caption.
            }
            else if (m.Msg == WM_NCMOUSELEAVE)
            {
                // The pointer left the non-client area entirely — including into the page. A held press
                // ignores it: the capture reports where the pointer is.
                if (_heldCaptionButton is null) SetCaptionButtonState(null, null);
            }
            else if (m.Msg is WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK && HitTestToKind((int)m.WParam) is { } pressed)
            {
                // Swallow the press: DefWindowProc would run the OS's own caption-button loop against
                // a caption this window does not have. The capture stands in for that loop.
                // The capture FIRST: the state change runs app code, which may pump messages (a dialog) and
                // would let the release go elsewhere before this window had asked for it.
                _heldCaptionButton = pressed;
                Capture = true;
                _captionCapture = true;
                SetCaptionButtonState(pressed, pressed);
                m.Result = IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_NCLBUTTONUP && HitTestToKind((int)m.WParam) is { } released)
            {
                var held = _heldCaptionButton;
                _heldCaptionButton = null;
                EndCaptionCapture();
                SetCaptionButtonState(released, null);
                // Only act if the press STARTED on this button, matching every other button on the
                // system.
                if (held == released) InvokeCaptionButton(released);
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
                if (WindowState != FormWindowState.Maximized && p.Y >= 0 && p.Y < border)
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

    private const uint TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
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
