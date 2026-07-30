namespace Shenora.WinForms;

/// <summary>
/// Runs a function on a dedicated STA thread — required for WinForms dialogs and the clipboard.
/// ALWAYS used for those (never inline on the caller): the source app's measured rule — a
/// dialog on the WebView2's UI thread conflicts with its message handling, and a dedicated STA
/// thread sidesteps every apartment surprise.
/// </summary>
internal static class StaThread
{
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
}
