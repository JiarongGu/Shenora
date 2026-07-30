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
public class OptimizedForm : Form
{
    private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int WM_NCCALCSIZE = 0x0083, WM_SYSCOMMAND = 0x0112, WM_NCACTIVATE = 0x0086, WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
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

        // Drag-and-drop enabled so a drop-zone manager can see system drag events over the form.
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
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
    /// True when the window is maximized. Frameless chrome maximizes MANUALLY (fills the work
    /// area) so <see cref="Form.WindowState"/> is NOT the source of truth — this property is.
    /// </summary>
    public bool IsAppMaximized => _options.FramelessChrome ? _maximized : WindowState == FormWindowState.Maximized;

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

        // GetMonitorInfo, never Screen.WorkingArea — the managed value is DPI-mis-scaled on a
        // HiDPI monitor (~12 px short per edge, measured); the P/Invoke rect is exact physical px.
        var hMon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return;
        var w = mi.rcWork;
        _maximized = true;
        ApplyCornerPreference(); // square corners while maximized
        SetWindowPos(Handle, IntPtr.Zero, w.left, w.top, w.right - w.left, w.bottom - w.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        MaximizedChanged?.Invoke(this, EventArgs.Empty);
    }

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
        if (_restoreBounds.Width > 0)
            SetWindowPos(Handle, IntPtr.Zero, _restoreBounds.X, _restoreBounds.Y,
                _restoreBounds.Width, _restoreBounds.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
        if (WndProcHook is not null && WndProcHook(m.Msg))
            return;

        if (!_options.FramelessChrome)
        {
            base.WndProc(ref m);
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

        // WM_NCCALCSIZE gave the TOP edge to the client area, so DefWindowProc reports HTCLIENT
        // there and Windows can't resize from the top. Re-add a top resize border: within the
        // strip, return HTTOP (or the diagonal corners). Below the strip the app's header still
        // gets the drag (mousedown → START_DRAG → HTCAPTION), so the top edge both RESIZES
        // (very edge) and DRAGS (header).
        if (m.Msg == WM_NCHITTEST && !_maximized)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var lp = unchecked((int)(long)m.LParam);
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                // DpiHelper owns every device-DPI conversion (P5.5 H4.5). It was `* DeviceDpi / 96`
                // here — integer division that only happened to be safe because the multiply came
                // first, and which silently returns 0 for a non-positive DeviceDpi.
                var border = Math.Max(6, DpiHelper.Scale(_options.TopResizeBorder, DpiHelper.ScaleFromDeviceDpi(DeviceDpi)));
                if (p.Y >= 0 && p.Y < border)
                    m.Result = (IntPtr)(p.X < border ? HTTOPLEFT : p.X >= ClientSize.Width - border ? HTTOPRIGHT : HTTOP);
            }
            return;
        }

        base.WndProc(ref m);
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
