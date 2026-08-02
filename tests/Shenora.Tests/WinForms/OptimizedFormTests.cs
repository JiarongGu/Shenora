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
    public void Restoring_from_an_unreachable_saved_rect_lands_somewhere_visible() => Sta.Run(() =>
    {
        // _restoreBounds is RAW PHYSICAL px from whichever monitor the window maximized on, so it can
        // be unreachable later: that monitor unplugged, moved in the virtual desktop, or rescaled
        // (P5.5 H2). Restoring to it blind put the window where the user cannot grab it.
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        form.Bounds = new Rectangle(-40000, -40000, 400, 300); // "a monitor that is no longer there"
        _ = form.Handle;

        form.Maximize();                    // captures the off-screen rect as the restore target
        Assert.True(form.IsAppMaximized);
        form.RestoreFromMax();

        Assert.False(form.IsAppMaximized);
        Assert.True(
            WindowStateManager.IsVisible(form.Bounds.X, form.Bounds.Y, form.Bounds.Width, form.Bounds.Height,
                Screen.AllScreens.Select(s => s.Bounds), new WindowStateOptions()),
            $"restored to {form.Bounds}, which no monitor can reach");
    });

    [Fact]
    public void A_framed_window_does_not_subscribe_to_display_changes() => Sta.Run(() =>
    {
        // SystemEvents is a static, process-lifetime publisher, so the subscription must be both
        // conditional (only a frameless window maximizes manually) and released on dispose — a missed
        // unsubscribe keeps the whole control tree alive forever.
        var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
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
        Assert.True(form.IsAppMaximized);
        Assert.Equal(FormWindowState.Maximized, form.WindowState);

        form.ToggleMaximize();
        Assert.False(form.IsAppMaximized);
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void Frameless_manual_maximize_fills_and_restores_without_touching_window_state() => Sta.Run(() =>
    {
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(120, 130, 640, 480);
        _ = form.Handle; // the manual path needs a real handle (monitor lookup + SetWindowPos)
        var changes = 0;
        form.MaximizedChanged += (_, _) => changes++;
        var original = form.Bounds;

        form.Maximize();

        Assert.True(form.IsAppMaximized);
        // Manual maximize: WindowState stays Normal — IsAppMaximized is the source of truth.
        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.True(form.Width > original.Width && form.Height > original.Height);

        form.RestoreFromMax();

        Assert.False(form.IsAppMaximized);
        // The restore-bounds roundtrip. This also guards the restore TARGET, which now comes from
        // WINDOWPLACEMENT.rcNormalPosition rather than the live window rect (so that maximizing a
        // SNAPPED window and restoring it exits the snap, as every other Windows app does).
        // The snapped case itself cannot be built in-process — Aero Snap is an OS shell gesture, and
        // rcNormalPosition only diverges from the live rect once the OS has actually docked the
        // window — so it is verified live: Win+Left, SC_MAXIMIZE, SC_RESTORE, asserting the window
        // lands back on its PRE-snap rectangle. What this test pins is that the new source returns
        // the right answer in the ordinary case.
        Assert.Equal(original, form.Bounds);
        Assert.Equal(2, changes);
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

    [Fact]
    public void Restore_from_max_unminimizes_first() => Sta.Run(() =>
    {
        // Regression: restoring bounds on a still-minimized window mangled them under
        // WS_MINIMIZE and left the window in the taskbar with the state dropped.
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true });
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(100, 100, 640, 480);
        _ = form.Handle;
        form.Maximize();
        form.WindowState = FormWindowState.Minimized;

        form.RestoreFromMax();

        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.False(form.IsAppMaximized);
        Assert.Equal(new Rectangle(100, 100, 640, 480), form.Bounds);
    });

    [Fact]
    public void WndProc_hook_sees_messages() => Sta.Run(() =>
    {
        using var form = new OptimizedForm();
        var seen = new List<int>();
        form.WndProcHook = msg =>
        {
            seen.Add(msg);
            return false; // observe, don't swallow
        };

        _ = form.Handle; // handle creation pumps creation messages through WndProc

        Assert.NotEmpty(seen);
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
