using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

public class TaskbarFlashTests
{
    // FLASHW_TRAY | FLASHW_TIMERNOFG: the button alone, until the window is in front. FLASHW_STOP is zero.
    [Fact]
    public void A_start_flashes_the_button_only_until_the_window_is_in_front_and_a_stop_is_the_stop_flag()
    {
        var start = TaskbarFlash.Describe((IntPtr)1234, on: true);
        var stop = TaskbarFlash.Describe((IntPtr)1234, on: false);

        Assert.Equal(0x2u | 0xCu, start.Flags);
        Assert.Equal(0u, stop.Flags);
        Assert.Equal((IntPtr)1234, start.Window);
        // The structure's own size, which the call refuses to run without: FLASHWINFO is 32 bytes on x64 and 20 on x86.
        Assert.Equal(IntPtr.Size == 8 ? 32u : 20u, start.Size);
        // Until the foreground, not a fixed count.
        Assert.Equal(0u, start.Count);
    }

    // The caller is the thread that learned of it: the call is the window's thread's, and the caller does not wait for it.
    [Fact]
    public void The_call_is_posted_to_the_windows_own_thread_from_any_thread() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        _ = form.Handle;
        var uiThread = Environment.CurrentManagedThreadId;
        var calls = new List<(TaskbarFlash.FlashInfo Info, int Thread)>();

        Task.Run(() => TaskbarFlash.Set(form, on: true, info => { calls.Add((info, Environment.CurrentManagedThreadId)); return true; })).Wait();
        Assert.Empty(calls); // nothing ran inline on the caller's thread: it is queued behind the pump this thread has not run

        for (var i = 0; i < 200 && calls.Count == 0; i++)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        var call = Assert.Single(calls);
        Assert.Equal(uiThread, call.Thread);
        Assert.Equal(form.Handle, call.Info.Window);
    });

    [Fact]
    public void A_window_with_no_handle_or_none_at_all_is_left_alone() => Sta.Run(() =>
    {
        var called = false;
        using var form = new Form();
        TaskbarFlash.Set(form, on: true, _ => { called = true; return true; });
        TaskbarFlash.Set(null, on: true, _ => { called = true; return true; });
        TaskbarFlash.Start(null);
        TaskbarFlash.Stop(form);

        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Assert.False(called);
    });

    [Fact]
    public void A_disposed_window_is_left_alone() => Sta.Run(() =>
    {
        var called = false;
        var form = new Form { ShowInTaskbar = false };
        _ = form.Handle;
        TaskbarFlash.Set(form, on: true, _ => { called = true; return true; });
        form.Dispose(); // between the post and its running

        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Assert.False(called);
    });

    // The one real call, on a window nobody sees: starting and stopping is never an error.
    [Fact]
    public void The_real_call_starts_and_stops_without_throwing() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        _ = form.Handle;

        TaskbarFlash.Start(form);
        TaskbarFlash.Stop(form);
        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    });
}
