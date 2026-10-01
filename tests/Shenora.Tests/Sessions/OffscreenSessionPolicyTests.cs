using Shenora.Core.Sessions;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Sessions;

/// <summary>
/// What an off-screen session's browser is told before anyone drives it. A page in one never saves a file: a WebView2
/// pool browser saved into the user's Downloads folder, while CEF's cancelled the same download (Alloy's default).
/// </summary>
public class OffscreenSessionPolicyTests
{
    [Fact]
    public async Task A_pool_browser_cancels_downloads()
    {
        using var ui = new TestUiThread();
        var host = new FakeSessionHost(ui);
        using var pool = new RenderSessionPool(new RenderSessionPoolOptions
        {
            Host = host,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(Path.GetTempPath(), "shenora-tests", "pool-downloads") },
        });

        await using var lease = await pool.LeaseAsync();

        Assert.True(Assert.Single(host.Created).CancelDownloads);
    }

    [Fact]
    public async Task A_streaming_browser_cancels_downloads()
    {
        using var ui = new TestUiThread();
        var host = new FakeSessionHost(ui);

        await using var stream = await StreamingSession.StartAsync(new StreamingSessionOptions
        {
            Host = host,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(Path.GetTempPath(), "shenora-tests", "stream-downloads") },
        });

        Assert.True(Assert.Single(host.Created).CancelDownloads);
    }
}
