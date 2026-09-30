using Shenora.Core.Sessions;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Sessions;

/// <summary>
/// The interactive session's flow over a host with no engine (D91): what the window's closing does to the driver, the
/// outcome and the busy gate. The WebView2 window itself is the desktop sample's to run.
/// </summary>
public class InteractiveSessionFlowTests
{
    private static InteractiveSession Session(FakeSessionHost host) => new(new InteractiveSessionOptions
    {
        Host = host,
        Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(Path.GetTempPath(), "shenora-tests", "interactive-flow") },
    });

    private static async Task<FakeSessionWindow> WindowOf(FakeSessionHost host)
    {
        for (var i = 0; i < 200 && host.LastWindow is null; i++) await Task.Delay(10);
        return host.LastWindow ?? throw new TimeoutException("the session opened no window");
    }

    /// <summary>
    /// 🔴 A close that is not held (the app exiting, the OS, a close no person asked for) takes the window without
    /// cancelling <see cref="SessionController.WindowClosed"/>. A driver waiting on the page must not then keep the
    /// session, and its gate, open with no window: the flow ends with the window, cancelled, and the driver's token says
    /// so.
    /// </summary>
    [Fact]
    public async Task A_window_closed_under_a_waiting_driver_ends_the_flow_and_frees_the_gate()
    {
        using var ui = new TestUiThread();
        var host = new FakeSessionHost(ui);
        var session = Session(host);
        var driverCancelled = new TaskCompletionSource<bool>();

        var run = session.RunAsync(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { driverCancelled.TrySetResult(true); throw; }
            return null;
        });
        var window = await WindowOf(host);
        Assert.True(session.IsBusy);

        Assert.True(await window.CloseByAsync(byUser: false));   // not a person's: nothing holds it

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(InteractiveSessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(session.IsBusy);
        Assert.True(await driverCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A person's close is held once, so the driver gets its final read, and the session ends with it.</summary>
    [Fact]
    public async Task A_persons_close_is_held_once_and_the_driver_gets_its_final_read()
    {
        using var ui = new TestUiThread();
        var host = new FakeSessionHost(ui);
        var session = Session(host);

        var run = session.RunAsync(async (controller, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (controller.WindowClosed.IsCancellationRequested) { return "final-read"; }
            return null;
        });
        var window = await WindowOf(host);

        Assert.False(await window.CloseByAsync(byUser: true));   // held: the window stays for the driver's last read

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success);
        Assert.Equal("final-read", result.Blob);
        Assert.True(window.Closed.IsCompleted);   // the host closed it once the flow was done
        Assert.False(session.IsBusy);
    }

    /// <summary>The hold is spent after one use: a second close from a person goes through, and the flow ends.</summary>
    [Fact]
    public async Task A_second_close_from_a_person_goes_through()
    {
        using var ui = new TestUiThread();
        var host = new FakeSessionHost(ui);
        var session = Session(host);

        // A driver that ignores its token: only the window's going ends it.
        var run = session.RunAsync(async (_, _) => { await Task.Delay(Timeout.Infinite); return null; });
        var window = await WindowOf(host);

        Assert.False(await window.CloseByAsync(byUser: true));
        Assert.True(await window.CloseByAsync(byUser: true));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(InteractiveSessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(session.IsBusy);
    }
}
