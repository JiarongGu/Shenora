using System.Runtime.ExceptionServices;
using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

/// <summary>
/// State-machine tests over real (invisible) forms — the WndProc chrome visuals (frameless
/// borders, DWM rounding) are the sample e2e's subject, family precedent.
///
/// Every body runs on a dedicated STA thread: <see cref="OptimizedForm"/> sets
/// <c>AllowDrop = true</c>, whose OLE drag-drop registration REQUIRES STA — xunit workers are
/// MTA, and the failure mode is not a clean test failure but a blocking WinForms
/// unhandled-exception dialog inside handle creation that stalls the whole suite (found live).
/// </summary>
public class OptimizedFormTests
{
    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Defaults_are_a_framed_window_with_the_perf_styles() => RunSta(() =>
    {
        using var form = new OptimizedForm();

        Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
        Assert.True(form.AllowDrop); // drop-zone managers need system drag events over the form
    });

    [Fact]
    public void Frameless_options_apply() => RunSta(() =>
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
    public void Framed_toggle_maximize_falls_back_to_window_state() => RunSta(() =>
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
    public void Frameless_manual_maximize_fills_and_restores_without_touching_window_state() => RunSta(() =>
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
        Assert.Equal(original, form.Bounds); // the restore-bounds roundtrip
        Assert.Equal(2, changes);
    });

    [Fact]
    public void Frameless_maximize_is_idempotent() => RunSta(() =>
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
    public void Restore_from_max_unminimizes_first() => RunSta(() =>
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
    public void WndProc_hook_sees_messages() => RunSta(() =>
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
    public void ApplyChromeTheme_updates_the_fill_and_survives_a_live_handle() => RunSta(() =>
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
