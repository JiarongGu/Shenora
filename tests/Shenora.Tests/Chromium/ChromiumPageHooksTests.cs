using Shenora.Chromium;
using Shenora.Chromium.Interop;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The app's answers to a page's downloads, permissions and renderer failures, and what stands when it declines. CEF's
/// callbacks that carry them were driven for real by the sample's <c>HooksProbe</c>.
/// </summary>
public class ChromiumPageHooksTests
{
    private static ChromiumPageHooks Hooks(Func<ChromiumDownloadRequest, bool>? download = null,
        Func<ChromiumPermissionRequest, bool>? permission = null, Func<ChromiumProcessReport, bool>? failed = null) =>
        new(download, permission, failed);

    [Fact]
    public void No_hook_set_means_no_hooks() =>
        Assert.Null(ChromiumPageHooks.From(new ChromiumHostOptions()));

    [Fact]
    public void Any_hook_set_makes_the_hooks()
    {
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnDownloadStarting = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnPermissionRequested = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnProcessFailed = _ => false }));
    }

    [Fact]
    public void The_permission_flags_are_CEFs()
    {
        var cef = Enum.GetValues<cef_permission_request_types_t>().Select(v => (int)v).Order();
        var ours = Enum.GetValues<ChromiumPermissionKind>().Select(v => (int)v).Order();
        Assert.Equal(cef, ours);
    }

    [Fact]
    public void A_download_goes_where_the_app_says_and_sees_what_the_page_asked_for()
    {
        ChromiumDownloadRequest? seen = null;
        var hooks = Hooks(download: d => { seen = d; d.ResultFilePath = @"C:\out\a.txt"; return true; });

        Assert.Equal(@"C:\out\a.txt", hooks.Download("https://app.local/a.txt", "a.txt", "text/plain", null));
        Assert.Equal(("https://app.local/a.txt", "a.txt", "text/plain"), (seen!.Url, seen.SuggestedName, seen.MimeType));
    }

    [Theory]
    [InlineData(true, null)]                // handled, no path: cancelled
    [InlineData(true, "  ")]
    [InlineData(false, @"C:\out\a.txt")]    // declined: a path it set is ignored, and the default (cancel) stands
    public void A_download_with_no_path_or_not_handled_is_cancelled(bool handled, string? path)
    {
        var hooks = Hooks(download: d => { d.ResultFilePath = path; return handled; });
        Assert.Null(hooks.Download("https://app.local/a", "a", "", null));
    }

    [Fact]
    public void A_download_hook_that_throws_declines_and_the_download_is_cancelled()
    {
        var hooks = Hooks(download: _ => throw new InvalidOperationException("app bug"));
        Assert.Null(hooks.Download("https://app.local/a", "a", "", null));
    }

    [Fact]
    public void Without_a_download_hook_nothing_is_taken() =>
        Assert.Null(Hooks(permission: _ => false).Download("https://app.local/a", "a", "", null));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void A_permission_is_answered_as_the_app_sets_it(bool handled, bool allow, bool expected)
    {
        ChromiumPermissionRequest? seen = null;
        var hooks = Hooks(permission: r => { seen = r; r.Allow = allow; return handled; });

        Assert.Equal(expected, hooks.Permission((uint)(ChromiumPermissionKind.Notifications | ChromiumPermissionKind.Geolocation), "https://app.local", null));
        Assert.Equal(("https://app.local", ChromiumPermissionKind.Notifications | ChromiumPermissionKind.Geolocation), (seen!.Origin, seen.Kinds));
    }

    [Fact]
    public void A_permission_the_app_declines_falls_to_the_default()
    {
        var hooks = Hooks(permission: r => { r.Allow = true; return false; });
        Assert.Null(hooks.Permission((uint)ChromiumPermissionKind.Clipboard, "https://app.local", null));
    }

    [Fact]
    public void A_permission_hook_that_throws_falls_to_the_default()
    {
        var hooks = Hooks(permission: _ => throw new InvalidOperationException("app bug"));
        Assert.Null(hooks.Permission((uint)ChromiumPermissionKind.Clipboard, "https://app.local", null));
    }

    [Fact]
    public void Without_a_permission_hook_the_default_answers() =>
        Assert.Null(Hooks(download: _ => false).Permission((uint)ChromiumPermissionKind.Clipboard, "https://app.local", null));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_renderer_failure_the_app_handled_skips_the_reload(bool handled, bool skipsReload)
    {
        ChromiumProcessReport? seen = null;
        var hooks = Hooks(failed: f => { seen = f; return handled; });

        Assert.Equal(skipsReload, hooks.ProcessFailed(cef_termination_status_t.TS_PROCESS_CRASHED, 5, null));
        Assert.Equal((ChromiumProcessStatus.Crashed, 5), (seen!.Status, seen.ErrorCode));
    }

    [Fact]
    public void A_renderer_hook_that_throws_leaves_the_reload()
    {
        var hooks = Hooks(failed: _ => throw new InvalidOperationException("app bug"));
        Assert.False(hooks.ProcessFailed(cef_termination_status_t.TS_PROCESS_OOM, 0, null));
    }

    [Fact]
    public void Without_a_renderer_hook_the_reload_stands() =>
        Assert.False(Hooks(download: _ => false).ProcessFailed(cef_termination_status_t.TS_PROCESS_CRASHED, 0, null));

    [Theory]
    [InlineData((int)cef_termination_status_t.TS_ABNORMAL_TERMINATION, ChromiumProcessStatus.Abnormal)]
    [InlineData((int)cef_termination_status_t.TS_PROCESS_WAS_KILLED, ChromiumProcessStatus.Killed)]
    [InlineData((int)cef_termination_status_t.TS_PROCESS_CRASHED, ChromiumProcessStatus.Crashed)]
    [InlineData((int)cef_termination_status_t.TS_PROCESS_OOM, ChromiumProcessStatus.OutOfMemory)]
    [InlineData((int)cef_termination_status_t.TS_LAUNCH_FAILED, ChromiumProcessStatus.LaunchFailed)]
    [InlineData((int)cef_termination_status_t.TS_INTEGRITY_FAILURE, ChromiumProcessStatus.IntegrityFailure)]
    public void Every_termination_status_has_its_exit(int status, ChromiumProcessStatus expected) =>
        Assert.Equal(expected, ChromiumPageHooks.ProcessStatus((cef_termination_status_t)status));
}
