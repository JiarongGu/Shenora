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
        Func<ChromiumPermissionRequest, bool>? permission = null, Func<ChromiumProcessReport, bool>? failed = null,
        Action<ChromiumDownloadProgress>? progress = null) =>
        new(download, permission, failed, progress);

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
    public void The_engines_hooks_are_the_same_hooks()
    {
        Assert.Null(ChromiumPageHooks.From(new ChromiumEngineOptions()));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnDownloadStarting = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnPermissionRequested = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnProcessFailed = _ => false }));

        var hooks = ChromiumPageHooks.From(new ChromiumEngineOptions { OnDownloadStarting = d => { d.ResultFilePath = @"C:\out\a.txt"; return true; } })!;
        Assert.Equal(@"C:\out\a.txt", hooks.Download("https://app.local/a.txt", "a.txt", "", null));
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

    [Fact]
    public void Watching_downloads_is_a_hook_too_and_gives_CEF_the_download_handler()
    {
        var progress = ChromiumPageHooks.From(new ChromiumHostOptions { OnDownloadProgress = _ => { } })!;
        Assert.True(progress.HandlesDownloads);
        Assert.Null(progress.Download("https://app.local/a", "a", "", null));   // watching takes nothing: still cancelled
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnDownloadProgress = _ => { } }));
        Assert.False(Hooks(permission: _ => false).HandlesDownloads);
    }

    [Fact]
    public void A_download_update_reaches_the_app_with_what_CEF_reported()
    {
        ChromiumDownloadProgress? seen = null;
        var hooks = Hooks(progress: p => seen = p);

        hooks.DownloadUpdated(new ChromiumDownloadProgress(7, "https://app.local/a.bin", @"C:\out\a.bin", ChromiumDownloadState.InProgress, 10, -1), null);

        Assert.Equal((7u, "https://app.local/a.bin", @"C:\out\a.bin", ChromiumDownloadState.InProgress, 10L, -1L),
            (seen!.Id, seen.Url, seen.FilePath, seen.State, seen.ReceivedBytes, seen.TotalBytes));
    }

    [Fact]
    public void An_update_lets_the_app_cancel_pause_and_resume_its_download()
    {
        var control = new RecordingControl();
        ChromiumDownloadProgress? seen = null;
        var hooks = Hooks(progress: p => seen = p);

        hooks.DownloadUpdated(new ChromiumDownloadProgress(3, "u", "p", ChromiumDownloadState.InProgress, 5, 10, isPaused: true, control), null);
        seen!.Pause();
        seen.Resume();
        seen.Cancel();

        Assert.True(seen.IsPaused);
        Assert.Equal(["pause", "resume", "cancel"], control.Calls);
    }

    [Fact]
    public void An_update_made_without_a_control_has_nothing_to_act_on()
    {
        var update = new ChromiumDownloadProgress(1, "u", "p", ChromiumDownloadState.Complete, 1, 1);
        update.Cancel();
        update.Pause();
        update.Resume();
        Assert.False(update.IsPaused);
    }

    private sealed class RecordingControl : IChromiumDownloadControl
    {
        public List<string> Calls { get; } = [];
        public void Cancel() => Calls.Add("cancel");
        public void Pause() => Calls.Add("pause");
        public void Resume() => Calls.Add("resume");
    }

    [Fact]
    public void A_progress_hook_that_throws_is_ignored()
    {
        var hooks = Hooks(progress: _ => throw new InvalidOperationException("app bug"));
        hooks.DownloadUpdated(new ChromiumDownloadProgress(1, "u", "p", ChromiumDownloadState.Complete, 1, 1), null);
    }

    [Theory]
    [InlineData(false, false, false, ChromiumDownloadState.InProgress)]
    [InlineData(false, true, false, ChromiumDownloadState.Complete)]
    [InlineData(true, false, false, ChromiumDownloadState.Canceled)]
    [InlineData(true, false, true, ChromiumDownloadState.Canceled)]     // a cancel reads as interrupted too: cancelled wins
    [InlineData(false, false, true, ChromiumDownloadState.Interrupted)]
    public void A_download_state_is_read_from_CEFs_flags(bool canceled, bool complete, bool interrupted, ChromiumDownloadState expected) =>
        Assert.Equal(expected, ChromiumPageHooks.DownloadState(canceled, complete, interrupted));

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
