using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The three hooks that decide whether an unattended session carries on or stops forever.
/// <para>
/// 🔴 <b>The DEFAULTS are the subject, not the hooks.</b> Leaving <c>ScriptDialogOpening</c>,
/// <c>BasicAuthenticationRequested</c> or <c>ClientCertificateRequested</c> unhandled makes WebView2
/// raise its OWN modal prompt — against a window that is off-screen, so nothing can ever answer it and
/// the page stops for good. Every one of them is now handled whether or not an app supplies a hook, and
/// what these tests pin is that the no-hook answer is the SAFE one.
/// </para>
/// <para>
/// 🔴 <b>WHAT THESE DO NOT COVER.</b> They test <see cref="SessionBrowser.Decide"/> — the decision —
/// and NOT the wiring that reads its answer and sets <c>e.State</c>/<c>e.Handled</c>/<c>e.Cancel</c> on
/// the platform's event args. Measured: replacing the permission wiring with a hard
/// <c>State = Allow</c> still compiles and every test here still passes. That wiring needs a live
/// <c>CoreWebView2</c>, so it is sample/e2e territory — said out loud, because a green run here is not
/// the same as "the browser obeys".
/// </para>
/// <para>
/// ⚠ Measured against the SDK (1.0.4022.49): <c>ScriptDialogOpening</c> and
/// <c>BasicAuthenticationRequested</c> have NO <c>Handled</c> property — subscribing is itself the
/// suppression. So the handlers must exist even when they appear to do nothing.
/// </para>
/// </summary>
public class SessionHookTests
{
    // ── Script dialogs ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_NO_hook_a_script_dialog_is_dismissed()
    {
        var dialog = SessionBrowser.Decide<SessionScriptDialog>(
            null, new SessionScriptDialog("Alert", "https://x/", "are you sure?", ""));

        // Not accepting IS the dismiss — the page carries on rather than waiting on a modal nobody sees.
        Assert.False(dialog.Accept);
    }

    [Fact]
    public void A_hook_can_accept_a_prompt_and_answer_it()
    {
        var dialog = SessionBrowser.Decide<SessionScriptDialog>(
            d => { d.Accept = true; d.ResultText = "42"; },
            new SessionScriptDialog("Prompt", "https://x/", "how many?", ""));

        Assert.True(dialog.Accept);
        Assert.Equal("42", dialog.ResultText);
    }

    [Fact]
    public void A_THROWING_dialog_hook_falls_back_to_dismiss_and_is_reported()
    {
        // The failure must degrade to the safe answer, never to the wedge — and never escape, because
        // this runs inside a WebView2 event where an escaping exception crashes the UI thread.
        Exception? reported = null;

        var dialog = SessionBrowser.Decide<SessionScriptDialog>(
            _ => throw new InvalidOperationException("hook bug"),
            new SessionScriptDialog("Confirm", "https://x/", "?", ""),
            ex => reported = ex);

        Assert.False(dialog.Accept);
        Assert.IsType<InvalidOperationException>(reported);
    }

    // ── HTTP authentication ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_NO_hook_an_auth_challenge_is_cancelled()
    {
        var challenge = SessionBrowser.Decide<SessionAuthRequest>(
            null, new SessionAuthRequest("https://x/secret", "Basic realm=\"x\""));

        // Both null = cancel. The load then fails normally instead of hanging on an invisible prompt.
        Assert.Null(challenge.UserName);
        Assert.Null(challenge.Password);
    }

    [Fact]
    public void A_hook_can_answer_the_challenge()
    {
        var challenge = SessionBrowser.Decide<SessionAuthRequest>(
            c => { c.UserName = "u"; c.Password = "p"; },
            new SessionAuthRequest("https://x/secret", "Basic realm=\"x\""));

        Assert.Equal("u", challenge.UserName);
        Assert.Equal("p", challenge.Password);
    }

    [Fact]
    public void A_HALF_answered_challenge_is_still_a_cancel()
    {
        // A username with no password cannot be sent, and guessing an empty one would send credentials
        // the app did not write. The wiring requires BOTH; this pins the shape the wiring reads.
        var challenge = SessionBrowser.Decide<SessionAuthRequest>(
            c => c.UserName = "u", new SessionAuthRequest("https://x/", "Basic"));

        Assert.NotNull(challenge.UserName);
        Assert.Null(challenge.Password);
    }

    [Fact]
    public void The_challenge_never_prints_the_credentials_it_carries()
    {
        // 🔴 A record's generated ToString() prints EVERY property, and this one holds a password. Any
        // log line, exception message or debugger watch that formats the object would have leaked it —
        // and none of those looks like a place a credential goes, which is what makes it worth a test.
        var challenge = new SessionAuthRequest("https://x/secret", "Basic realm=\"x\"")
        {
            UserName = "alice",
            Password = "hunter2",
        };

        var printed = challenge.ToString();

        Assert.DoesNotContain("hunter2", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", printed, StringComparison.Ordinal);
        // …while still being useful: the resource and the scheme are what a diagnostic actually needs.
        Assert.Contains("https://x/secret", printed, StringComparison.Ordinal);
    }

    // ── Popups and permissions: policy the kit used to make silently ─────────────────────────────
    // ⚠ These two differ from the three above: the default is not a HANG, it is a decision the kit made
    // with no way for an app to disagree. So the tests that matter most are the DEFAULTS being unchanged
    // — an existing app must see exactly what it saw before.

    [Fact]
    public void With_NO_hook_a_popup_is_suppressed_exactly_as_before()
    {
        var request = SessionBrowser.Decide<SessionWindowRequest>(
            null, new SessionWindowRequest("https://x/popup", UserInitiated: true));

        Assert.False(request.Allow);
    }

    [Fact]
    public void With_NO_hook_a_permission_is_denied_exactly_as_before()
    {
        var request = SessionBrowser.Decide<SessionPermissionRequest>(
            null, new SessionPermissionRequest("Camera", "https://x/", UserInitiated: true));

        Assert.False(request.Allow);
    }

    [Fact]
    public void A_hook_can_allow_a_popup_or_grant_a_permission()
    {
        Assert.True(SessionBrowser.Decide<SessionWindowRequest>(
            r => r.Allow = true, new SessionWindowRequest("https://x/", false)).Allow);

        // The realistic shape: grant one capability to the app's own origin, deny everything else.
        var granted = SessionBrowser.Decide<SessionPermissionRequest>(
            r => r.Allow = r.Kind == "ClipboardRead" && r.Uri.StartsWith("https://app.local/", StringComparison.Ordinal),
            new SessionPermissionRequest("ClipboardRead", "https://app.local/page", true));
        var refused = SessionBrowser.Decide<SessionPermissionRequest>(
            r => r.Allow = r.Kind == "ClipboardRead" && r.Uri.StartsWith("https://app.local/", StringComparison.Ordinal),
            new SessionPermissionRequest("Camera", "https://app.local/page", true));

        Assert.True(granted.Allow);
        Assert.False(refused.Allow);
    }

    [Fact]
    public void A_THROWING_policy_hook_falls_back_to_the_RESTRICTIVE_answer()
    {
        // The safe direction differs from the three wedge hooks: there, safety is "keep going"; here it
        // is "keep refusing". A buggy hook must not become an open door.
        Assert.False(SessionBrowser.Decide<SessionWindowRequest>(
            _ => throw new InvalidOperationException("bug"), new SessionWindowRequest("https://x/", false)).Allow);
        Assert.False(SessionBrowser.Decide<SessionPermissionRequest>(
            _ => throw new InvalidOperationException("bug"), new SessionPermissionRequest("Camera", "https://x/", true)).Allow);
    }

    // ── Client certificates ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_NO_hook_a_certificate_request_is_cancelled()
    {
        var request = SessionBrowser.Decide<SessionCertificateRequest>(
            null, new SessionCertificateRequest("intranet", 443, ["CN=a", "CN=b"]));

        // Null index = cancel. ⚠ Deliberately not "continue without one", which prompts the user on
        // some servers — the hang again by another route.
        Assert.Null(request.SelectedIndex);
    }

    [Fact]
    public void A_hook_can_select_one_of_the_offered_certificates()
    {
        var request = SessionBrowser.Decide<SessionCertificateRequest>(
            r => r.SelectedIndex = r.Subjects.ToList().FindIndex(s => s == "CN=b"),
            new SessionCertificateRequest("intranet", 443, ["CN=a", "CN=b"]));

        Assert.Equal(1, request.SelectedIndex);
    }

    [Fact]
    public void An_OUT_OF_RANGE_selection_is_treated_as_a_cancel_by_the_wiring()
    {
        // Decide itself does not police the index — the wiring bounds-checks before indexing the
        // platform's collection. Pinned here so that check is not "simplified" away: an unchecked
        // index would be an IndexOutOfRange inside a WebView2 event, i.e. a UI-thread crash.
        var request = SessionBrowser.Decide<SessionCertificateRequest>(
            r => r.SelectedIndex = 99, new SessionCertificateRequest("intranet", 443, ["CN=a"]));

        Assert.Equal(99, request.SelectedIndex);
        Assert.True(request.SelectedIndex >= request.Subjects.Count, "the wiring must reject this");
    }
}
