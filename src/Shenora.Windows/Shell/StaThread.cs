namespace Shenora.Windows;

/// <summary>
/// Runs a function on an STA thread — required for WinForms dialogs and the clipboard, and 🔴 ALWAYS
/// used for those rather than inline on the caller: a dialog on the WebView2's UI thread conflicts with
/// its message handling.
/// <para>
/// ⚠ <b>Two entry points, and the difference is the APARTMENT's LIFETIME.</b> <see cref="RunAsync"/>
/// gives the call a thread of its own, which is what a blocking modal dialog wants;
/// <see cref="RunSharedAsync"/> queues onto ONE long-lived PUMPED apartment.
/// </para>
/// </summary>
internal static class StaThread
{
    /// <summary>A fresh STA thread per call, for work that BLOCKS (a modal dialog owns its thread).</summary>
    public static Task<T> RunAsync<T>(Func<T> function)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(function());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true; // never block app shutdown
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Queue onto the ONE shared STA apartment, which lives for the process and PUMPS MESSAGES.
    /// <para>
    /// 🔴 <b>OLE clipboard writes need a PUMPED apartment, and both other shapes corrupt them SILENTLY</b> —
    /// a per-call thread tears down mid-<c>OleFlushClipboard</c> and a non-pumping one never services it, so
    /// formats come back as zeros of the right length with no exception. Measured both ways:
    /// <c>docs/design/shells.md</c>, "Native services, and the STA rule".
    /// </para>
    /// <para>
    /// ⚠ <b>Nothing queued here may BLOCK</b> — work is serialised, so a modal dialog would stall every
    /// later clipboard call behind it. That is what <see cref="RunAsync"/> is for.
    /// </para>
    /// </summary>
    public static Task<T> RunSharedAsync<T>(Func<T> function)
    {
        var marshaller = SharedApartment.Marshaller;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // ⚠ BeginInvoke, not Invoke: this is called from arbitrary threads including the UI thread, and
        // blocking one on the apartment deadlocks the moment the apartment needs it back.
        marshaller.BeginInvoke(() =>
        {
            try
            {
                tcs.SetResult(function());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// The one long-lived pumped STA apartment, started on first use. Nested so an app that never touches
    /// the clipboard never starts the thread — a static field on <see cref="StaThread"/> would be
    /// initialised by the first <see cref="RunAsync"/> caller too.
    /// </summary>
    private static class SharedApartment
    {
        internal static readonly Control Marshaller = Start();

        private static Control Start()
        {
            // A Control, because BeginInvoke needs a window HANDLE: the posted work arrives as a message
            // on the same loop the OLE rendering callbacks use.
            var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var control = new Control();
                control.CreateControl();        // realize the handle BEFORE anyone can post to it
                ready.SetResult(control);
                Application.Run();              // the pump; never returns, and the thread is background
            })
            {
                IsBackground = true,            // never block app shutdown
                Name = "Shenora clipboard STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return ready.Task.GetAwaiter().GetResult();
        }
    }
}
