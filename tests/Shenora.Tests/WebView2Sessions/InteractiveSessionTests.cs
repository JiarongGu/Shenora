using Shenora.Windows;
using Shenora.Core.Ipc;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The gate mechanics around <see cref="InteractiveSession.RunAsync"/> — busy serialization,
/// exactly-once completion via the token fallback (the dropped-post wedge post-mortem), and
/// anchor-gone outcomes — WITHOUT pumping the anchor's queue, so the posted UI delegate
/// deliberately never runs (a real login window is e2e territory, the family precedent).
/// Plus the ComputeFitSize DPI math and ClearProfile.
/// </summary>
public class InteractiveSessionTests
{
    private static Task<string?> NeverDriver(SessionController controller, CancellationToken ct) =>
        Task.FromResult<string?>("unreached");

    private static InteractiveSessionOptions OptionsFor(Control anchor) => new()
    {
        Anchor = anchor,
        ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "login-tests", "unused-profile"),
    };

    [Fact]
    public async Task A_second_login_is_busy_and_the_token_fallback_releases_the_gate()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle; // BeginInvoke needs a created handle; the queue is never pumped
        var window = new InteractiveSession(OptionsFor(anchor));
        using var cts = new CancellationTokenSource();

        var first = window.RunAsync(NeverDriver, cts.Token);
        Assert.False(first.IsCompleted); // parked on the (unpumped) UI post
        Assert.True(window.IsBusy);

        var second = await window.RunAsync(NeverDriver);
        Assert.Equal(SessionErrorCodes.Busy, second.ErrorCode); // logins serialize

        // The UI delegate never runs (= the measured dropped-post shape) — the token
        // registration must complete the login AND release the gate anyway.
        cts.Cancel();
        var result = await first;
        Assert.Equal(SessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_pre_cancelled_token_reports_cancelled_and_leaves_the_gate_open()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver, new CancellationToken(canceled: true));

        Assert.Equal(SessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_disposed_anchor_is_unavailable()
    {
        var anchor = new Form { ShowInTaskbar = false };
        anchor.Dispose();
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(SessionErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task An_anchor_without_a_handle_is_unavailable()
    {
        using var anchor = new Form { ShowInTaskbar = false }; // handle never created → BeginInvoke throws
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(SessionErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Theory]
    [InlineData(500, 600, 96, 500, 600)]   // 100% DPI: CSS px == physical px
    [InlineData(500, 450, 192, 1000, 900)] // 200% DPI: the login box is twice as many physical px
    [InlineData(3000, 3000, 96, 1880, 980)] // clamped into the working area (margins for chrome)
    public void ComputeFitSize_scales_by_dpi_and_clamps_to_the_work_area(
        int cssWidth, int cssHeight, int dpi, int expectedWidth, int expectedHeight)
    {
        var size = SessionController.ComputeFitSize(cssWidth, cssHeight, dpi, new Size(1920, 1040));

        Assert.Equal(new Size(expectedWidth, expectedHeight), size);
    }

    [Fact]
    public void ClearProfile_deletes_the_whole_tree_and_tolerates_a_missing_one()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "login-tests", "clear-profile");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "Cookies"), "x");

        InteractiveSession.ClearProfile(dir);
        Assert.False(Directory.Exists(dir)); // a real logout wipes the cookie store, not just the blob

        InteractiveSession.ClearProfile(dir); // already gone — best-effort, no throw
    }

    // ── Profile-path containment (P5.5 H1) ────────────────────────────────────────────────────────
    // ClearProfile is a RECURSIVE DELETE and profile paths are normally composed from data-driven
    // provider/account identifiers, so a stray ".." would aim it outside the sessions root — while
    // the same options doc calls per-account scoping a security boundary.

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("provider/../../elsewhere")]
    [InlineData(@"provider\..\..\elsewhere")]
    public void ClearProfile_refuses_a_path_with_traversal_segments(string relative)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "login-tests", relative);
        Assert.Throws<ArgumentException>(() => InteractiveSession.ClearProfile(path));
    }

    [Fact]
    public void ComposeProfileDirectory_builds_a_contained_path_from_plain_segments()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "login-tests", "profiles");

        var composed = InteractiveSession.ComposeProfileDirectory(root, "provider-a", "account-1");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "provider-a", "account-1"), composed);
        // …and the result is safe to hand straight to ClearProfile.
        InteractiveSession.ClearProfile(composed);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]           // separator smuggled into one segment
    [InlineData(@"a\b")]
    [InlineData("C:")]            // drive qualifier
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CON")]           // Windows reserved device name
    [InlineData("nul.txt")]       // reserved even with an extension
    public void ComposeProfileDirectory_rejects_an_unsafe_segment(string segment)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "login-tests", "profiles");
        Assert.Throws<ArgumentException>(() => InteractiveSession.ComposeProfileDirectory(root, segment));
    }

    [Fact]
    public void ComposeProfileDirectory_keeps_two_accounts_in_separate_cookie_jars()
    {
        // The isolation boundary the docs promise: distinct accounts must not collide on one directory.
        var root = Path.Combine(AppContext.BaseDirectory, "login-tests", "profiles");
        var a = InteractiveSession.ComposeProfileDirectory(root, "provider", "account-1");
        var b = InteractiveSession.ComposeProfileDirectory(root, "provider", "account-2");
        Assert.NotEqual(a, b);
    }

    // ── The bridge into the IPC error contract (P5.5 H9.4) ────────────────────────────────────────
    // SessionErrorCodes was a parallel vocabulary with no typed path to the wire, so every adopting app
    // hand-wrote the same throw. These pin that the codes cross UNCHANGED — the whole point is that
    // an app's i18n keys keep working.

    [Fact]
    public void ThrowIfFailed_is_a_no_op_on_success()
    {
        var result = new SessionResult { Success = true, Blob = "{}" };

        result.ThrowIfFailed();  // must not throw
        Assert.Equal("{}", result.Blob);
    }

    [Theory]
    [InlineData(SessionErrorCodes.Busy)]
    [InlineData(SessionErrorCodes.Cancelled)]
    [InlineData(SessionErrorCodes.Incomplete)]
    [InlineData(SessionErrorCodes.Error)]
    [InlineData(SessionErrorCodes.Unavailable)]
    public void ThrowIfFailed_surfaces_the_login_code_verbatim_as_the_wire_code(string code)
    {
        var ex = Assert.Throws<ShenoraException>(
            () => new SessionResult { Success = false, ErrorCode = code }.ThrowIfFailed());

        Assert.Equal(code, ex.Code);
        // The dispatcher's boundary maps an ShenoraException to its structured error, so this is
        // exactly what a client receives — no re-mapping table anywhere in between.
        Assert.Equal(code, ex.ToError().Code);
    }

    [Fact]
    public void ThrowIfFailed_reports_unknown_rather_than_a_null_reference()
    {
        // Should be unreachable — every Fail() site passes a code — but an error path that throws
        // NullReferenceException replaces the real diagnosis with a worse one.
        var ex = Assert.Throws<ShenoraException>(
            () => new SessionResult { Success = false, ErrorCode = null }.ThrowIfFailed());

        Assert.Equal(IpcErrorCodes.UnknownError, ex.Code);
    }
}
