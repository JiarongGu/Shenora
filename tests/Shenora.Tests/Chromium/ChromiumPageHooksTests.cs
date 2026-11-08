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
        Action<ChromiumDownloadProgress>? progress = null, Func<ChromiumCertificateError, bool>? certificate = null,
        Func<ChromiumFileDialogRequest, bool>? fileDialog = null, Func<ChromiumAuthRequest, bool>? auth = null,
        Func<ChromiumScriptDialogRequest, bool>? scriptDialog = null, Func<ChromiumClientCertificateRequest, bool>? clientCertificate = null,
        Func<ChromiumPopupRequest, bool>? popup = null) =>
        new(download, permission, failed, progress, certificate, fileDialog, auth, scriptDialog, clientCertificate, popup);

    private static ChromiumClientCertificate[] Offered(int count) =>
        Enumerable.Range(0, count).Select(i => new ChromiumClientCertificate($"user {i}", "corp CA", new string((char)('a' + i), 64), null, null)).ToArray();

    private static ChromiumAuthRequest Challenge() => new("http://10.0.0.5/status", "http://10.0.0.5", "basic", "device", false);

    [Fact]
    public void No_hook_set_means_no_hooks() =>
        Assert.Null(ChromiumPageHooks.From(new ChromiumHostOptions()));

    [Fact]
    public void Any_hook_set_makes_the_hooks()
    {
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnDownloadStarting = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnPermissionRequested = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnProcessFailed = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnCertificateError = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnCertificateError = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnFileDialog = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnFileDialog = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnAuthRequest = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnAuthRequest = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnScriptDialog = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnScriptDialog = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnClientCertificateRequest = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnClientCertificateRequest = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumHostOptions { OnPopupRequested = _ => false }));
        Assert.NotNull(ChromiumPageHooks.From(new ChromiumEngineOptions { OnPopupRequested = _ => false }));
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

    [Fact]
    public void Only_a_certificate_hook_has_CEF_asked()
    {
        Assert.True(Hooks(certificate: _ => false).HandlesCertificates);
        Assert.False(Hooks(download: _ => false, permission: _ => false).HandlesCertificates);
    }

    [Fact]
    public void A_certificate_error_is_allowed_only_when_the_app_handled_it_and_set_Allow()
    {
        ChromiumCertificateError? seen = null;
        var hooks = Hooks(certificate: e => { seen = e; e.Allow = true; return true; });

        Assert.True(hooks.CertificateError("https://10.0.0.5/", -202, "device", "device", "ab12", null));
        Assert.Equal(("https://10.0.0.5/", -202, "ERR_CERT_AUTHORITY_INVALID", "device", "device", "ab12"),
            (seen!.Url, seen.ErrorCode, seen.ErrorName, seen.Subject, seen.Issuer, seen.Sha256));
    }

    [Theory]
    [InlineData(true, false)]    // handled, Allow left false: the request fails
    [InlineData(false, true)]    // Allow set but not handled: the app declined, so the request fails
    [InlineData(false, false)]
    public void A_certificate_error_the_app_did_not_clearly_allow_fails(bool handled, bool allow) =>
        Assert.False(Hooks(certificate: e => { e.Allow = allow; return handled; }).CertificateError("https://h/", -200, "", "", "", null));

    [Fact]
    public void A_certificate_hook_that_throws_fails_the_request()
    {
        var hooks = Hooks(certificate: e => { e.Allow = true; throw new InvalidOperationException("app bug"); });
        Assert.False(hooks.CertificateError("https://h/", -200, "", "", "", null));
    }

    [Fact]
    public void Without_a_certificate_hook_the_request_fails() =>
        Assert.False(Hooks(download: _ => false).CertificateError("https://h/", -202, "", "", "", null));

    [Fact]
    public void Only_a_file_dialog_hook_has_CEF_given_a_dialog_handler()
    {
        Assert.True(Hooks(fileDialog: _ => false).HandlesFileDialogs);
        Assert.False(Hooks(download: _ => false, certificate: _ => false).HandlesFileDialogs);
    }

    [Fact]
    public void A_file_dialog_the_app_handled_is_answered_with_its_paths_and_sees_what_the_page_asked_for()
    {
        ChromiumFileDialogRequest? seen = null;
        var hooks = Hooks(fileDialog: r => { seen = r; r.Paths = [@"C:\in\a.png", @"C:\in\b.png"]; return true; });

        Assert.Equal([@"C:\in\a.png", @"C:\in\b.png"], hooks.FileDialog(ChromiumFileDialogMode.OpenMultiple, "Open", @"C:\in", ["image/*", ".png"], null)!);
        Assert.Equal((ChromiumFileDialogMode.OpenMultiple, "Open", @"C:\in", 2), (seen!.Mode, seen.Title, seen.DefaultPath, seen.Accept.Count));
    }

    [Theory]
    [InlineData(ChromiumFileDialogMode.Open)]
    [InlineData(ChromiumFileDialogMode.OpenFolder)]
    [InlineData(ChromiumFileDialogMode.Save)]
    public void A_dialog_for_one_path_is_given_the_first_only(ChromiumFileDialogMode mode)
    {
        var hooks = Hooks(fileDialog: r => { r.Paths = [@"C:\in\a", "  ", @"C:\in\b"]; return true; });
        Assert.Equal([@"C:\in\a"], hooks.FileDialog(mode, "", "", [], null)!);
    }

    [Fact]
    public void A_handled_dialog_with_no_paths_is_a_cancel() =>
        Assert.Empty(Hooks(fileDialog: _ => true).FileDialog(ChromiumFileDialogMode.Open, "", "", [], null)!);

    [Fact]
    public void A_file_dialog_the_app_declined_or_threw_on_is_CEFs_own()
    {
        Assert.Null(Hooks(fileDialog: r => { r.Paths = [@"C:ina"]; return false; }).FileDialog(ChromiumFileDialogMode.Open, "", "", [], null));
        Assert.Null(Hooks(fileDialog: _ => throw new InvalidOperationException("app bug")).FileDialog(ChromiumFileDialogMode.Open, "", "", [], null));
        Assert.Null(Hooks(download: _ => false).FileDialog(ChromiumFileDialogMode.Open, "", "", [], null));
    }

    [Fact]
    public void Only_an_auth_hook_has_the_pages_DevTools_opened()
    {
        Assert.True(Hooks(auth: _ => false).HandlesAuth);
        Assert.False(Hooks(download: _ => false, certificate: _ => false, fileDialog: _ => false).HandlesAuth);
    }

    [Fact]
    public void A_challenge_the_app_answered_gives_its_credentials_and_shows_what_the_server_asked()
    {
        ChromiumAuthRequest? seen = null;
        var hooks = Hooks(auth: r => { seen = r; r.UserName = "admin"; r.Password = "pw"; return true; });

        Assert.Equal(("admin", "pw"), hooks.Authenticate(Challenge(), null));
        Assert.Equal(("http://10.0.0.5/status", "http://10.0.0.5", "basic", "device", false), (seen!.Url, seen.Origin, seen.Scheme, seen.Realm, seen.IsProxy));
    }

    [Theory]
    [InlineData("admin", null)]    // one half is not an answer
    [InlineData(null, "pw")]
    [InlineData(null, null)]
    public void A_handled_challenge_without_both_halves_is_a_cancel(string? user, string? password) =>
        Assert.Equal((null, null), Hooks(auth: r => { r.UserName = user; r.Password = password; return true; }).Authenticate(Challenge(), null));

    [Fact]
    public void A_challenge_the_app_declined_or_threw_on_is_left_as_it_was()
    {
        Assert.Null(Hooks(auth: r => { r.UserName = "admin"; r.Password = "pw"; return false; }).Authenticate(Challenge(), null));
        Assert.Null(Hooks(auth: _ => throw new InvalidOperationException("app bug")).Authenticate(Challenge(), null));
        Assert.Null(Hooks(download: _ => false).Authenticate(Challenge(), null));
    }

    [Fact]
    public void Only_a_script_dialog_hook_has_CEF_given_a_dialog_handler()
    {
        Assert.True(Hooks(scriptDialog: _ => false).HandlesScriptDialogs);
        Assert.False(Hooks(download: _ => false, certificate: _ => false, fileDialog: _ => false, auth: _ => false).HandlesScriptDialogs);
    }

    [Fact]
    public void A_dialog_the_app_answered_gives_its_answer_and_shows_what_the_page_raised()
    {
        ChromiumScriptDialogRequest? seen = null;
        var hooks = Hooks(scriptDialog: r => { seen = r; r.Accept = true; r.ResultText = "typed"; return true; });

        Assert.Equal((true, "typed"), hooks.ScriptDialog(ChromiumScriptDialogKind.Prompt, "https://app.local/", "Name?", "anon", false, null));
        Assert.Equal((ChromiumScriptDialogKind.Prompt, "https://app.local/", "Name?", "anon", false),
            (seen!.Kind, seen.Origin, seen.Message, seen.DefaultText, seen.IsReload));
    }

    [Theory]
    [InlineData(ChromiumScriptDialogKind.Alert)]
    [InlineData(ChromiumScriptDialogKind.Confirm)]
    [InlineData(ChromiumScriptDialogKind.BeforeUnload)]
    public void Only_a_prompt_carries_text_back(ChromiumScriptDialogKind kind) =>
        Assert.Equal((true, ""), Hooks(scriptDialog: r => { r.Accept = true; r.ResultText = "typed"; return true; }).ScriptDialog(kind, "", "", "", false, null));

    [Fact]
    public void A_handled_dialog_is_not_accepted_until_the_app_says_so_and_a_refused_prompt_has_no_text() =>
        Assert.Equal((false, ""), Hooks(scriptDialog: r => { r.ResultText = "typed"; return true; })
            .ScriptDialog(ChromiumScriptDialogKind.Prompt, "", "", "", false, null));

    [Fact]
    public void A_dialog_the_app_declined_or_threw_on_is_CEFs_own()
    {
        Assert.Null(Hooks(scriptDialog: r => { r.Accept = true; return false; }).ScriptDialog(ChromiumScriptDialogKind.Confirm, "", "", "", false, null));
        Assert.Null(Hooks(scriptDialog: _ => throw new InvalidOperationException("app bug")).ScriptDialog(ChromiumScriptDialogKind.Confirm, "", "", "", false, null));
        Assert.Null(Hooks(download: _ => false).ScriptDialog(ChromiumScriptDialogKind.Confirm, "", "", "", false, null));
    }

    [Fact]
    public void Only_a_client_certificate_hook_has_CEF_asked_for_one()
    {
        Assert.True(Hooks(clientCertificate: _ => false).HandlesClientCertificates);
        Assert.False(Hooks(download: _ => false, certificate: _ => false, scriptDialog: _ => false).HandlesClientCertificates);
    }

    [Fact]
    public void A_certificate_the_app_chose_is_its_index_and_the_request_shows_what_was_offered()
    {
        ChromiumClientCertificateRequest? seen = null;
        var hooks = Hooks(clientCertificate: r => { seen = r; r.SelectedIndex = 1; return true; });
        Assert.Equal(1, hooks.ClientCertificate("device.local", 8443, false, Offered(3), null));
        Assert.Equal(("device.local", 8443, false, 3), (seen!.Host, seen.Port, seen.IsProxy, seen.Certificates.Count));
        Assert.Equal("user 2", seen.Certificates[2].Subject);
    }

    [Fact]
    public void A_handled_request_with_no_choice_presents_none()
    {
        Assert.Equal(-1, Hooks(clientCertificate: _ => true).ClientCertificate("h", 443, false, Offered(2), null));
        Assert.Equal(-1, Hooks(clientCertificate: r => { r.SelectedIndex = null; return true; }).ClientCertificate("h", 443, false, Offered(2), null));
    }

    [Theory]
    [InlineData(2)]    // one past the end
    [InlineData(-1)]
    public void A_choice_outside_the_list_presents_none_rather_than_another_certificate(int index) =>
        Assert.Equal(-1, Hooks(clientCertificate: r => { r.SelectedIndex = index; return true; }).ClientCertificate("h", 443, false, Offered(2), null));

    [Fact]
    public void A_request_the_app_declined_or_set_no_hook_for_is_CEFs_default()
    {
        Assert.Null(Hooks(clientCertificate: r => { r.SelectedIndex = 0; return false; }).ClientCertificate("h", 443, false, Offered(2), null));
        Assert.Null(Hooks(download: _ => false).ClientCertificate("h", 443, false, Offered(2), null));
    }

    // CEF's default for an unanswered request is its own pick from the list (documented as the first certificate), so a hook that
    // failed would hand the user's certificate to whoever asked, by position, which is what the hook exists to stop.
    [Fact]
    public void A_request_the_app_threw_on_presents_none_rather_than_a_certificate_it_never_chose()
    {
        ChromiumClientCertificateRequest? seen = null;
        var hooks = Hooks(clientCertificate: r => { seen = r; r.SelectedIndex = 0; throw new InvalidOperationException("app bug"); });
        Assert.Equal(-1, hooks.ClientCertificate("h", 443, false, Offered(2), null));
        Assert.NotNull(seen);
    }

    [Fact]
    public void A_popup_the_app_took_is_handled_and_the_request_shows_what_the_page_named()
    {
        ChromiumPopupRequest? seen = null;
        var hooks = Hooks(popup: r => { seen = r; return true; });
        Assert.True(hooks.Popup("https://example.com/a", "side", true, null));
        Assert.Equal(("https://example.com/a", "side", true), (seen!.Url, seen.FrameName, seen.IsUserGesture));
    }

    [Fact]
    public void A_popup_the_app_declined_or_threw_on_is_left_to_the_shells_own_policy()
    {
        Assert.False(Hooks(popup: _ => false).Popup("https://example.com/", "", false, null));
        Assert.False(Hooks(popup: _ => throw new InvalidOperationException("app bug")).Popup("https://example.com/", "", false, null));
        Assert.False(Hooks(download: _ => false).Popup("https://example.com/", "", false, null));
    }

    [Fact]
    public void A_challenges_text_never_prints_the_password()
    {
        var request = Challenge();
        request.UserName = "admin";
        request.Password = "hunter2";
        var text = request.ToString();
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("admin", text);
    }

    [Theory]
    [InlineData(-200, "ERR_CERT_COMMON_NAME_INVALID")]
    [InlineData(-202, "ERR_CERT_AUTHORITY_INVALID")]
    [InlineData(-99999, "ERR_-99999")]    // a code this CEF has no name for is still said
    public void An_error_code_is_named_from_CEFs_list(int code, string name) =>
        Assert.Equal(name, ChromiumPageHooks.ErrorName(code));

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
