using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// State-machine tests over real (invisible) forms — the WndProc chrome visuals (frameless
/// borders, DWM rounding) are the sample e2e's subject, family precedent.
///
/// Every body runs on a dedicated STA thread. The original trigger was <see cref="OptimizedForm"/>
/// setting <c>AllowDrop = true</c>, whose OLE registration REQUIRES STA — xunit workers are MTA, and
/// the failure mode is not a clean test failure but a blocking WinForms unhandled-exception dialog
/// inside handle creation that stalls the whole suite (found live). P5.5 H2 removed that
/// <c>AllowDrop</c>, so this class no longer strictly needs STA — but the harness STAYS: STA is simply
/// correct for tests that realize window handles, and the next OLE-touching feature (a file dialog, the
/// clipboard) would silently reintroduce the same stall.
/// </summary>
public class OptimizedFormTests
{
    [Fact]
    public void Defaults_are_a_framed_window_with_the_perf_styles() => Sta.Run(() =>
    {
        using var form = new OptimizedForm();

        Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);

        // NOT a drop target (P5.5 H2). This used to assert true, with a comment claiming drop-zone
        // managers need the form's drag events — they do not: OLE registers drop targets per HWND and
        // DropZoneOverlay registers itself. All the form-level flag did was force OLE/STA on every
        // consumer and show a copy cursor for a drop it then silently discarded.
        Assert.False(form.AllowDrop);
    });

    [Fact]
    public void A_throwing_WndProcHook_does_not_take_the_window_down() => Sta.Run(() =>
    {
        // The hook is APP CODE inside WndProc — the worst place for an escaping exception (P5.5 H2):
        // nothing is above it on the stack, and before the bootstrap installs its handlers this
        // surfaces as WinForms' own BLOCKING modal dialog, mid-message-dispatch. A throwing hook must
        // read as "did not handle the message" so the window keeps working.
        var calls = 0;
        using var form = new OptimizedForm
        {
            WndProcHook = _ =>
            {
                calls++;
                throw new InvalidOperationException("app hook bug");
            },
        };

        _ = form.Handle;      // realizing the window pumps a stream of messages through the hook
        form.Text = "still alive";

        Assert.True(calls > 0);                 // the hook really did run (and really did throw)
        Assert.True(form.IsHandleCreated);      // …and the window survived it
        Assert.Equal("still alive", form.Text); // …and still responds
    });

    [Fact]
    public void A_throwing_MaximizedChanged_subscriber_does_not_take_the_window_down() => Sta.Run(() =>
    {
        // Same class as the WndProcHook test above, and the same position: the event is raised from
        // WM_SIZE for the system's own routes, so an app subscriber that throws escapes into the window
        // procedure. This event is PUBLIC and its own summary invites an app to resync chrome on it, so
        // "no app subscribes" is not an answer.
        var calls = 0;
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        form.MaximizedChanged += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("app chrome bug");
        };

        _ = form.Handle;
        form.Maximize();                        // raises through the guard
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);       // …and the state change still completed
        form.RestoreFromMax();
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);

        Assert.Equal(2, calls);                 // the subscriber really did run, and really did throw
        Assert.True(form.IsHandleCreated);      // …and the window survived both
    });

    [Fact]
    public void Restoring_from_an_unreachable_saved_rect_lands_somewhere_visible() => Sta.Run(() =>
    {
        // The windowed bounds are physical px on whichever monitor the window was on, so they can be
        // unreachable later: that monitor unplugged, moved in the virtual desktop, or rescaled
        // (P5.5 H2). Restoring to them blind put the window where the user cannot grab it.
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        form.Bounds = new Rectangle(-40000, -40000, 400, 300); // "a monitor that is no longer there"
        _ = form.Handle;

        form.Maximize();
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
        form.RestoreFromMax();

        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
        Assert.True(
            WindowStateManager.IsVisible(form.Bounds.X, form.Bounds.Y, form.Bounds.Width, form.Bounds.Height,
                Screen.AllScreens.Select(s => s.Bounds), new WindowStateOptions()),
            $"restored to {form.Bounds}, which no monitor can reach");
    });

    [Fact]
    public void Disposing_twice_is_safe() => Sta.Run(() =>
    {
        var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true, NativeCaptionButtons = true });
        _ = form.Handle;
        form.Dispose();

        // Disposing twice must stay safe: the detach is unconditional, and removing a handler that was
        // never added is a no-op.
        form.Dispose();
        Assert.True(form.IsDisposed);
    });

    [Fact]
    public void Frameless_options_apply() => Sta.Run(() =>
    {
        using var form = new OptimizedForm(new OptimizedFormOptions
        {
            FramelessChrome = true,
            BackColor = Color.FromArgb(20, 20, 20),
        });

        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
        Assert.Equal(Color.FromArgb(20, 20, 20), form.BackColor);
    });

    [Fact]
    public void Framed_toggle_maximize_falls_back_to_window_state() => Sta.Run(() =>
    {
        using var form = new OptimizedForm();
        var changes = 0;
        form.MaximizedChanged += (_, _) => changes++;

        form.ToggleMaximize();
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
        Assert.Equal(FormWindowState.Maximized, form.WindowState);

        form.ToggleMaximize();
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(2, changes);
    });

    // The caption style is what makes Windows animate a minimize, a maximize and a restore: a probe sampling the screen
    // through each saw no frame between the two states without it, and 4 to 12 with it. WM_NCCALCSIZE hides it.
    [Fact]
    public void A_frameless_window_keeps_the_caption_style_and_hides_the_caption() => Sta.Run(() =>
    {
        using var form = Frameless(new Rectangle(120, 130, 640, 480));
        _ = form.Handle;

        Assert.Equal(WS_CAPTION, GetWindowLong(form.Handle, GWL_STYLE) & WS_CAPTION);
        Assert.Equal(form.Bounds.Top, form.PointToScreen(Point.Empty).Y);
    });

    // The system's own maximize: the window overhangs its monitor by its frame, and the client is the work area exactly.
    [Fact]
    public void Frameless_maximize_is_the_systems_and_its_client_is_the_work_area() => Sta.Run(() =>
    {
        using var form = Frameless(new Rectangle(120, 130, 640, 480));
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        var changes = 0;
        form.MaximizedChanged += (_, _) => changes++;
        var original = form.Bounds;

        form.Maximize();

        Assert.Equal(FormWindowState.Maximized, form.WindowState);
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
        Assert.Equal(WorkArea(form), form.RectangleToScreen(form.ClientRectangle));

        form.RestoreFromMax();

        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
        Assert.Equal(original, form.Bounds);
        Assert.Equal(2, changes);
    });

    // Win+Up, the caption's double click, a snap and the taskbar maximize without this form's members.
    [Fact]
    public void A_maximize_by_the_systems_own_route_is_followed() => Sta.Run(() =>
    {
        using var form = Frameless(new Rectangle(120, 130, 640, 480));
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        var changes = 0;
        form.MaximizedChanged += (_, _) => changes++;

        SendMessage(form.Handle, WM_SYSCOMMAND, SC_MAXIMIZE, IntPtr.Zero);
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
        Assert.Equal(1, changes);

        SendMessage(form.Handle, WM_SYSCOMMAND, SC_RESTORE, IntPtr.Zero);
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
        Assert.Equal(2, changes);
    });

    // WinForms sizes the window for its styles, whose caption a frameless window's client covers.
    [Fact]
    public void ClientSize_is_the_frameless_windows_real_client() => Sta.Run(() =>
    {
        using var form = Frameless(new Rectangle(120, 130, 640, 480));
        _ = form.Handle;

        form.ClientSize = new Size(500, 400);

        GetClientRect(form.Handle, out var client);
        Assert.Equal(new Size(500, 400), new Size(client.Right - client.Left, client.Bottom - client.Top));
    });

    [Fact]
    public void Frameless_maximize_is_idempotent() => Sta.Run(() =>
    {
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        _ = form.Handle;
        var changes = 0;
        form.MaximizedChanged += (_, _) => changes++;

        form.Maximize();
        form.Maximize(); // no-op
        form.RestoreFromMax();
        form.RestoreFromMax(); // no-op

        Assert.Equal(2, changes);
    });

    // Minimized from maximized, the window is still maximized to the window-state stack, and comes back that way when
    // the kit brings it forward; RestoreFromMax takes it to its windowed bounds.
    [Fact]
    public void A_window_minimized_from_maximized_stays_maximized_until_restored() => Sta.Run(() =>
    {
        using var form = Frameless(new Rectangle(100, 100, 640, 480));
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        form.Maximize();

        ShowWindow(form.Handle, SW_SHOWMINNOACTIVE);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);

        WindowActivation.Unminimize(form);
        Assert.Equal(FormWindowState.Maximized, form.WindowState);

        ShowWindow(form.Handle, SW_SHOWMINNOACTIVE);
        form.RestoreFromMax();
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(new Rectangle(100, 100, 640, 480), form.Bounds);
    });

    // Windows gives a minimized window an empty client area, and WinForms lays the children out into it. A Chromium
    // page shrunk that way renders itself again on restore, and reports its caption buttons from the tiny layout
    // first. Shown for real, off-screen and never activated: only a shown window is minimized.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_minimized_window_keeps_its_childrens_layout_through_the_restore(bool maximizedFirst) => Sta.Run(() =>
    {
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(-20000, -20000, 800, 600),
            ShowInTaskbar = false,
        };
        var child = new Panel { Dock = DockStyle.Fill };
        form.Controls.Add(child);
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        if (maximizedFirst) form.Maximize();
        var laidOut = child.Size;
        var resized = new List<Size>();
        child.SizeChanged += (_, _) => resized.Add(child.Size);

        ShowWindow(form.Handle, SW_SHOWMINNOACTIVE);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
        Assert.Equal(laidOut, child.Size);

        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        Assert.NotEqual(FormWindowState.Minimized, form.WindowState);
        Assert.Equal(laidOut, child.Size);
        Assert.Empty(resized);
    });

    private static OptimizedForm Frameless(Rectangle bounds) => new(new OptimizedFormOptions { FramelessChrome = true })
    {
        StartPosition = FormStartPosition.Manual,
        Bounds = bounds,
        ShowInTaskbar = false,
    };

    private static Rectangle WorkArea(Form form)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(form.Handle, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
        return Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
    }

    private const int SW_SHOWMINNOACTIVE = 7, SW_SHOWNOACTIVATE = 4;
    private const int WS_CAPTION = 0x00C00000, GWL_STYLE = -16, WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [Fact]
    public void WndProc_hook_sees_messages() => Sta.Run(() =>
    {
        using var form = new OptimizedForm();
        var seen = new List<int>();
        form.WndProcHook = m =>
        {
            seen.Add(m.Msg);
            return null; // observe, don't swallow
        };

        _ = form.Handle; // handle creation pumps creation messages through WndProc

        Assert.NotEmpty(seen);
    });

    /// <summary>
    /// The reason the hook takes a whole <c>Message</c>: it must be able to read the PARAMETERS and
    /// answer with a RESULT. As `Func&lt;int, bool&gt;` it saw only the id, so every real use of a
    /// window-procedure hook — WM_COPYDATA, WM_POWERBROADCAST, any RegisterWindowMessage channel with
    /// a payload — was out of reach.
    /// </summary>
    [Fact]
    public void WndProc_hook_reads_the_PARAMETERS_and_can_answer_with_a_RESULT() => Sta.Run(() =>
    {
        const int WM_USER = 0x0400;
        using var form = new OptimizedForm();
        var payload = (wParam: IntPtr.Zero, lParam: IntPtr.Zero);

        form.WndProcHook = m =>
        {
            if (m.Msg != WM_USER) return null;          // null = not handled, fall through
            payload = (m.WParam, m.LParam);
            return new IntPtr(42);                       // handled, and this becomes m.Result
        };

        var message = Message.Create(form.Handle, WM_USER, new IntPtr(7), new IntPtr(9));
        typeof(Control).GetMethod("WndProc", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(form, [message]);

        Assert.Equal(new IntPtr(7), payload.wParam);
        Assert.Equal(new IntPtr(9), payload.lParam);
    });

    [Fact]
    public void ApplyChromeTheme_updates_the_fill_and_survives_a_live_handle() => Sta.Run(() =>
    {
        using var form = new OptimizedForm(new OptimizedFormOptions
        {
            FramelessChrome = true,
            BackColor = Color.FromArgb(20, 20, 20),
        });
        _ = form.Handle;

        form.ApplyChromeTheme(Color.FromArgb(245, 246, 248), Color.FromArgb(232, 232, 232), immersiveDarkMode: false);

        Assert.Equal(Color.FromArgb(245, 246, 248), form.BackColor);
    });
}
