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
        // A whole SessionBrowserOptions, exactly as StreamingSession takes: the session no longer keeps
        // its own copy of two fields, so everything an app can configure on a pooled browser now reaches
        // an interactive one too.
        Browser = new SessionBrowserOptions
        {
            ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "login-tests", "unused-profile"),
        },
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
        Assert.Equal(InteractiveSessionErrorCodes.Busy, second.ErrorCode); // logins serialize

        // The UI delegate never runs (= the measured dropped-post shape) — the token
        // registration must complete the login AND release the gate anyway.
        cts.Cancel();
        var result = await first;
        Assert.Equal(InteractiveSessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_pre_cancelled_token_reports_cancelled_and_leaves_the_gate_open()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver, new CancellationToken(canceled: true));

        Assert.Equal(InteractiveSessionErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task A_disposed_anchor_is_unavailable()
    {
        var anchor = new Form { ShowInTaskbar = false };
        anchor.Dispose();
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(InteractiveSessionErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task An_anchor_without_a_handle_is_unavailable()
    {
        using var anchor = new Form { ShowInTaskbar = false }; // handle never created → BeginInvoke throws
        var window = new InteractiveSession(OptionsFor(anchor));

        var result = await window.RunAsync(NeverDriver);

        Assert.Equal(InteractiveSessionErrorCodes.Unavailable, result.ErrorCode);
        Assert.False(window.IsBusy);
    }

    // ── The gate belongs to the WINDOW, not to the caller's task ─────────────────────────────────

    [Fact]
    public async Task Cancelling_answers_the_caller_but_does_NOT_free_the_gate_while_the_window_is_up()
    {
        // 🔴 The defect: completing the caller and releasing the gate were ONE action. So a cancelled
        // session reported "cancelled" while its modal window was still on screen — and the caller,
        // reasonably believing it was finished, called ClearProfile against a profile the live browser
        // still held (throwing into a swallow) and started a SECOND session on the same profile.
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;
        var window = new InteractiveSession(OptionsFor(anchor));
        using var cts = new CancellationTokenSource();

        bool busyWhileOpen = false, secondWasRefused = false;
        var probed = 0;
        window.RunOnUiOverride = _ =>
        {
            // Once only. Under the defect the probe below starts a REAL second session, whose delegate
            // re-enters here — and an unbounded version of this recurses until the stack gives out.
            if (Interlocked.Exchange(ref probed, 1) == 0)
            {
                // We are "inside" the window now. Cancel from here, exactly as a user's cancel would
                // land mid-session, and observe the gate BEFORE the window comes down.
                cts.Cancel();
                busyWhileOpen = window.IsBusy;

                // ⚠ Never `.Result` on this. When the gate is correctly HELD, RunAsync refuses
                // synchronously and the task is already complete; when the defect is present it posts to
                // the very UI thread we are standing on, so awaiting it here DEADLOCKS — the test would
                // hang instead of failing, which is the worse outcome by far. Measured: it did.
                var second = window.RunAsync(NeverDriver);
                secondWasRefused = second.IsCompleted
                    && second.Result.ErrorCode == InteractiveSessionErrorCodes.Busy;
            }
            return InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled);
        };

        var run = window.RunAsync(NeverDriver, cts.Token);
        for (var i = 0; i < 200 && !run.IsCompleted; i++) { Application.DoEvents(); Thread.Sleep(5); }

        Assert.Equal(InteractiveSessionErrorCodes.Cancelled, (await run).ErrorCode);  // the caller IS answered…
        Assert.True(busyWhileOpen, "the gate must stay held while the window is up");
        Assert.True(secondWasRefused, "a second session must not open on the same profile");
        Assert.False(window.IsBusy);                                       // …and freed once it is gone
    }

    [Fact]
    public async Task A_session_that_RUNS_to_completion_frees_the_gate()
    {
        // The other direction, so the fix above cannot be "never release": the ordinary path must still
        // open the gate when the window closes.
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;
        var window = new InteractiveSession(OptionsFor(anchor));
        window.RunOnUiOverride = _ => InteractiveSessionResult.Ok("captured");

        var run = window.RunAsync(NeverDriver);
        for (var i = 0; i < 200 && !run.IsCompleted; i++) { Application.DoEvents(); Thread.Sleep(5); }

        Assert.Equal("captured", (await run).Blob);
        Assert.False(window.IsBusy);
    }

    // ── The browser an interactive session actually runs ──────────────────────────────────────────

    /// <summary>
    /// A session with every option an app can set. Deliberately every field at a NON-default value, so
    /// the pass-through check below cannot pass vacuously for one of them.
    /// </summary>
    private static SessionBrowserOptions FullyConfigured() => new()
    {
        ProfileDirectory = @"C:\profiles\acct",
        KeepAliveInBackground = true,
        MuteAudio = false,
        AdditionalBrowserArguments = "--some-flag",
        RequestFilter = (_, _) => true,
        VirtualHost = "app.local",
        ResourceProvider = new StubResourceProvider(),
        FolderMappings = [new WebViewFolderMapping { HostName = "media.local", FolderPath = @"C:\media" }],
        InitTimeout = TimeSpan.FromSeconds(9),
        IsDevelopment = true,
        Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        Events = new Shenora.Core.Events.EventBus(),
        ObserveResponse = _ => true,
        ResponseBodySample = 128,
        OnScriptDialog = _ => { },
        OnAuthRequest = _ => { },
        OnCertificateRequest = _ => { },
        OnWindowRequest = _ => { },
        OnPermissionRequest = _ => { },
    };

    /// <summary>Identity only — this test cares that the SAME instance arrives, never what it serves.</summary>
    private sealed class StubResourceProvider : IWebViewResourceProvider
    {
        public Stream? GetResourceStream(string virtualPath) => null;

        public bool Exists(string virtualPath) => false;
    }

    /// <summary>
    /// 🔴 <b>The interactive session could not be configured like every other session</b>: it built its
    /// browser options internally and forwarded two fields by hand, so the five hooks, the request
    /// filter, bundle serving and the logger simply could not reach it. It takes a whole
    /// <see cref="SessionBrowserOptions"/> now, exactly as <c>StreamingSessionOptions</c> does.
    /// <para>
    /// ⚠ <b>This test walks the options type by REFLECTION on purpose.</b> The obvious fix — copy the
    /// fields across — is the one that ages badly: it works the day it is written and silently drops the
    /// NEXT option added. Reflection means a new field fails HERE (at the self-check, for not being
    /// exercised) rather than in an adopter's app, where its symptom is a knob that does nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void Everything_the_app_configures_reaches_the_browser_except_the_field_the_session_owns()
    {
        var app = FullyConfigured();

        // The one field the session owns, in BOTH directions — an off-screen window must keep its JS
        // running, a revealed one has no reason to.
        Assert.False(InteractiveSession.ComposeBrowserOptions(app, revealImmediately: true).KeepAliveInBackground);
        Assert.True(InteractiveSession.ComposeBrowserOptions(app, revealImmediately: false).KeepAliveInBackground);

        var composed = InteractiveSession.ComposeBrowserOptions(app, revealImmediately: true);
        var bare = new SessionBrowserOptions { ProfileDirectory = "unset" };
        var checkedCount = 0;
        foreach (var property in typeof(SessionBrowserOptions).GetProperties())
        {
            if (property.Name == nameof(SessionBrowserOptions.KeepAliveInBackground)) continue;
            if (property.Name == "EqualityContract") continue;   // the record's own generated member

            Assert.True(!Equals(property.GetValue(app), property.GetValue(bare)),
                $"FullyConfigured() leaves {property.Name} at its default, so the pass-through assertion " +
                "below proves nothing for it. Give it a distinct value — this is the self-check that " +
                "makes a newly added option impossible to forget.");
            // NAMED, not a bare Assert.Equal: the whole failure mode here is "one option out of eighteen
            // stopped being forwarded", and `Expected: False / Actual: True` does not say which.
            Assert.True(Equals(property.GetValue(app), property.GetValue(composed)),
                $"{property.Name} did not survive into the session's browser options. Every field except " +
                "KeepAliveInBackground must pass through untouched — a hand-copied subset is exactly what " +
                "this catches.");
            checkedCount++;
        }

        Assert.True(checkedCount > 10, $"reflection self-check: only {checkedCount} option(s) compared");
    }

    // ── Holding the user's close (SessionController) ──────────────────────────────────────────────

    [Fact]
    public void The_close_hold_applies_ONCE_and_only_to_a_close_the_user_asked_for()
    {
        // 🔴 Two bugs in one line. The rule was "veto whenever the flow has not finished", so:
        //  • Application.Exit was vetoed too — a session window could keep the whole app alive; and
        //  • EVERY attempt was vetoed, so a driver awaiting something that never completes left a modal
        //    window nothing could close.
        Assert.True(SessionController.ShouldHoldClose(false, CloseReason.UserClosing, alreadyHeld: false));

        // Spent after one use: the second click means the user has said it twice.
        Assert.False(SessionController.ShouldHoldClose(false, CloseReason.UserClosing, alreadyHeld: true));

        // Never a close the user did not ask for — these must reach the window untouched.
        Assert.False(SessionController.ShouldHoldClose(false, CloseReason.ApplicationExitCall, false));
        Assert.False(SessionController.ShouldHoldClose(false, CloseReason.WindowsShutDown, false));
        Assert.False(SessionController.ShouldHoldClose(false, CloseReason.TaskManagerClosing, false));
        Assert.False(SessionController.ShouldHoldClose(false, CloseReason.FormOwnerClosing, false));

        // And once the flow has returned, the host's own close is never held.
        Assert.False(SessionController.ShouldHoldClose(true, CloseReason.UserClosing, false));
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

        Assert.True(InteractiveSession.ClearProfile(dir));
        Assert.False(Directory.Exists(dir)); // a real logout wipes the cookie store, not just the blob

        // Already gone is SUCCESS, not a failure: the caller asked for "no cookies here" and that holds.
        Assert.True(InteractiveSession.ClearProfile(dir));
    }

    [Fact]
    public void ClearProfile_REPORTS_a_profile_it_could_not_clear()
    {
        // 🔴 The silent-logout incident, which is what the return value is for. The commonest cause is a
        // session window still holding the profile — reproduced here with an open file handle, which
        // locks the tree the same way. Before this, the app told the user "signed out", the cookies
        // stayed, and the next session walked straight back in.
        var dir = Path.Combine(AppContext.BaseDirectory, "login-tests", "locked-profile");
        Directory.CreateDirectory(dir);
        var held = Path.Combine(dir, "Cookies");
        File.WriteAllText(held, "x");

        using (var _ = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(InteractiveSession.ClearProfile(dir));
            Assert.True(Directory.Exists(dir));   // and it says so because the cookies really are there
        }

        Assert.True(InteractiveSession.ClearProfile(dir));   // released → it clears
    }

    [Fact]
    public void ClearProfile_refuses_a_VOLUME_ROOT()
    {
        // The traversal check stops a path climbing OUT of the sessions tree; it says nothing about one
        // that never pointed inside it. `Path.Combine(root, "")` collapsing to a drive is the realistic
        // way to get here, and this method is a recursive delete that swallows its errors.
        var root = Path.GetPathRoot(AppContext.BaseDirectory)!;

        Assert.Throws<ArgumentException>(() => InteractiveSession.ClearProfile(root));
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
    // 🔴 The ones a BLOCKLIST could never catch: Windows normalises these away, so they passed every
    // check above AND the containment test. Measured with GetFullPath against a root of C:/root - the
    // first four resolve to the ROOT ITSELF, so that "account" owns every other account's directory
    // and ClearProfile on it deletes the lot; the last three land on C:/root/acct, sharing that jar.
    [InlineData("...")]
    [InlineData("....")]
    [InlineData(".. .")]
    [InlineData(" . ")]
    [InlineData("acct.")]
    [InlineData("acct ")]
    [InlineData("acct..")]
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

    [Fact]
    public void ComposeProfileDirectory_never_answers_the_ROOT_however_the_segment_is_spelt()
    {
        // The sharpest consequence of the normalisation hole, asserted directly rather than via the
        // rejection list: whatever a caller passes, the answer must be strictly BELOW the root. A
        // segment that resolved to the root handed one account the whole sessions tree — and
        // `ClearProfile` on it deletes every other account's profile.
        var root = Path.Combine(AppContext.BaseDirectory, "login-tests", "profiles");
        var fullRoot = Path.GetFullPath(root);

        foreach (var segment in new[] { "...", ".. .", " . ", "....", "acct.", "acct " })
        {
            var thrown = Record.Exception(() => InteractiveSession.ComposeProfileDirectory(root, segment));
            Assert.IsType<ArgumentException>(thrown);
        }

        // And the honest one: a plain segment still composes below the root.
        Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            InteractiveSession.ComposeProfileDirectory(root, "acct"), StringComparison.Ordinal);
    }

    // ── The bridge into the IPC error contract (P5.5 H9.4) ────────────────────────────────────────
    // InteractiveSessionErrorCodes was a parallel vocabulary with no typed path to the wire, so every adopting app
    // hand-wrote the same throw. These pin that the codes cross UNCHANGED — the whole point is that
    // an app's i18n keys keep working.

    [Fact]
    public void ThrowIfFailed_is_a_no_op_on_success()
    {
        var result = new InteractiveSessionResult { Success = true, Blob = "{}" };

        result.ThrowIfFailed();  // must not throw
        Assert.Equal("{}", result.Blob);
    }

    [Theory]
    [InlineData(InteractiveSessionErrorCodes.Busy)]
    [InlineData(InteractiveSessionErrorCodes.Cancelled)]
    [InlineData(InteractiveSessionErrorCodes.Incomplete)]
    [InlineData(InteractiveSessionErrorCodes.Error)]
    [InlineData(InteractiveSessionErrorCodes.Unavailable)]
    public void ThrowIfFailed_surfaces_the_login_code_verbatim_as_the_wire_code(string code)
    {
        var ex = Assert.Throws<ShenoraException>(
            () => new InteractiveSessionResult { Success = false, ErrorCode = code }.ThrowIfFailed());

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
            () => new InteractiveSessionResult { Success = false, ErrorCode = null }.ThrowIfFailed());

        Assert.Equal(IpcErrorCodes.UnknownError, ex.Code);
    }
}
