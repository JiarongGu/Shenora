namespace Shenora.Windows;

/// <summary>
/// Runs a function on a dedicated STA thread — required for WinForms dialogs and the clipboard.
/// ALWAYS used for those (never inline on the caller): the source app's measured rule — a
/// dialog on the WebView2's UI thread conflicts with its message handling, and a dedicated STA
/// thread sidesteps every apartment surprise.
/// <para>
/// ⚠ <b>Two entry points, and the difference is the APARTMENT's LIFETIME, not the threading.</b>
/// <see cref="RunAsync"/> gives the call a thread of its own, which is what a blocking modal dialog
/// wants and what it has always used. <see cref="RunSharedAsync"/> queues onto ONE long-lived pumped
/// apartment — see its remarks for the defect that earned it.
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
    /// 🔴 <b>OLE clipboard writes need this and a per-call thread silently corrupts them.</b>
    /// <c>Clipboard.SetDataObject(copy: true)</c> ends in <c>OleFlushClipboard</c>, which asks the data
    /// object to render every advertised format into global memory — and that rendering is serviced
    /// through the apartment's MESSAGE LOOP. A thread that sets the clipboard and immediately exits tears
    /// the apartment down mid-flush, so the STANDARD formats survive and the rest do not.
    /// </para>
    /// <para>
    /// <b>Measured, 8 runs of a copy carrying text + files + HTML + PNG + a private format:</b> text and
    /// files always came back; the PNG returned as ZEROS of the right length and <c>text/html</c> vanished
    /// from the item entirely, on ~1 run in 6 — no exception, no error, a copy that simply lost half of
    /// itself. ⚠ That is a PRODUCTION defect, not a test artifact: an app copying a picture beside its
    /// text would ship the text and an empty picture, occasionally, forever.
    /// </para>
    /// <para>
    /// ⚠ <b>The pump is the whole point — an earlier attempt at this parked the shared thread on a
    /// blocking queue and made things WORSE</b> (every run failed): a long-lived STA thread that does not
    /// pump cannot service the very rendering callbacks the flush depends on.
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

        // BeginInvoke, not Invoke: this is called from arbitrary threads (including the UI thread) and
        // blocking one of them on the apartment would deadlock the moment the apartment needed it back.
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
    /// the clipboard never starts the thread — a static field on <see cref="StaThread"/> would also be
    /// initialised by the first <see cref="RunAsync"/> caller.
    /// </summary>
    private static class SharedApartment
    {
        internal static readonly Control Marshaller = Start();

        private static Control Start()
        {
            // A Control is the marshalling primitive because BeginInvoke needs a window HANDLE — the
            // posted work arrives as a message, which is the same loop the OLE rendering callbacks use.
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
