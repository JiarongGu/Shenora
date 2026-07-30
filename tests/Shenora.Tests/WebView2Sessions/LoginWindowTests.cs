using Shenora.WebView2.Sessions;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The gate mechanics around <see cref="LoginWindow.RunAsync"/> — busy serialization,
/// exactly-once completion via the token fallback (the dropped-post wedge post-mortem), and
/// anchor-gone outcomes — WITHOUT pumping the anchor's queue, so the posted UI delegate
/// deliberately never runs (a real login window is e2e territory, the family precedent).
/// Plus the ComputeFitSize DPI math and ClearProfile.
/// </summary>
public class LoginWindowTests
{
    private static Task<string?> NeverDriver(LoginWindowController controller, CancellationToken ct) =>
        Task.FromResult<string?>("unreached");

    private static LoginWindowOptions OptionsFor(Control anchor) => new()
    {
        Anchor = anchor,
        ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "login-tests", "unused-profile"),
    };

    [Fact]
    public async Task A_second_login_is_busy_and_the_token_fallback_releases_the_gate()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle; // BeginInvoke needs a created handle; the queue is never pumped
        var window = new LoginWindow(OptionsFor(anchor));
        using var cts = new CancellationTokenSource();

        var first = window.RunAsync(NeverDriver, cts.Token);
        Assert.False(first.IsCompleted); // parked on the (unpumped) UI post
        Assert.True(window.IsBusy);

        var second = await window.RunAsync(NeverDriver);
        Assert.Equal(LoginErrorCodes.Busy, second.ErrorCode); // logins serialize

        // The UI delegate never runs (= the measured dropped-post shape) — the token
        // registration must complete the login AND release the gate anyway.
        cts.Cancel();
        var result = await first;
        Assert.Equal(LoginErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_pre_cancelled_token_reports_cancelled_and_leaves_the_gate_open()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;
        var window = new LoginWindow(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver, new CancellationToken(canceled: true));

        Assert.Equal(LoginErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_disposed_anchor_is_unavailable()
    {
        var anchor = new Form { ShowInTaskbar = false };
        anchor.Dispose();
        var window = new LoginWindow(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(LoginErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task An_anchor_without_a_handle_is_unavailable()
    {
        using var anchor = new Form { ShowInTaskbar = false }; // handle never created → BeginInvoke throws
        var window = new LoginWindow(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(LoginErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Theory]
    [InlineData(500, 600, 96, 500, 600)]   // 100% DPI: CSS px == physical px
    [InlineData(500, 450, 192, 1000, 900)] // 200% DPI: the login box is twice as many physical px
    [InlineData(3000, 3000, 96, 1880, 980)] // clamped into the working area (margins for chrome)
    public void ComputeFitSize_scales_by_dpi_and_clamps_to_the_work_area(
        int cssWidth, int cssHeight, int dpi, int expectedWidth, int expectedHeight)
    {
        var size = LoginWindowController.ComputeFitSize(cssWidth, cssHeight, dpi, new Size(1920, 1040));

        Assert.Equal(new Size(expectedWidth, expectedHeight), size);
    }

    [Fact]
    public void ClearProfile_deletes_the_whole_tree_and_tolerates_a_missing_one()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "login-tests", "clear-profile");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "Cookies"), "x");

        LoginWindow.ClearProfile(dir);
        Assert.False(Directory.Exists(dir)); // a real logout wipes the cookie store, not just the blob

        LoginWindow.ClearProfile(dir); // already gone — best-effort, no throw
    }
}
