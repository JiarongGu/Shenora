using System.Drawing;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Core.Sessions;

/// <summary>Outcome of a <see cref="InteractiveSession.RunAsync"/> flow.</summary>
public sealed class InteractiveSessionResult
{
    /// <summary>True when the driver captured a session.</summary>
    public required bool Success { get; init; }

    /// <summary>The driver's captured session blob (its own format; commonly serialized cookies).</summary>
    public string? Blob { get; init; }

    /// <summary>A <see cref="InteractiveSessionErrorCodes"/> value when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    internal static InteractiveSessionResult Ok(string blob) => new() { Success = true, Blob = blob };

    internal static InteractiveSessionResult Fail(string errorCode) => new() { Success = false, ErrorCode = errorCode };

    /// <summary>
    /// Throw this outcome's failure as a <see cref="ShenoraException"/>: the bridge from
    /// <see cref="InteractiveSessionErrorCodes"/> into the IPC error contract, so a facade route is one call.
    /// <c>ModuleBase</c> and <c>MessageDispatcher</c> already turn one into the structured wire error. No-op on success.
    /// </summary>
    /// <exception cref="ShenoraException">When <see cref="Success"/> is false.</exception>
    public void ThrowIfFailed()
    {
        if (Success) return;
        // A failure with no code should be impossible (every Fail site passes one), but reporting UNKNOWN_ERROR beats
        // throwing a NullReference out of an error path.
        throw new ShenoraException(ErrorCode ?? IpcErrorCodes.UnknownError);
    }
}

/// <summary>Error codes <see cref="InteractiveSession"/> reports (wire-friendly i18n keys, the family shape).</summary>
public static class InteractiveSessionErrorCodes
{
    /// <summary>Another session is already open: interactive sessions serialize.</summary>
    public const string Busy = "SESSION_BUSY";

    /// <summary>The caller's token tripped, or the user closed before the driver captured.</summary>
    public const string Cancelled = "SESSION_CANCELLED";

    /// <summary>The driver finished without capturing anything (e.g. the user closed the window).</summary>
    public const string Incomplete = "SESSION_INCOMPLETE";

    /// <summary>The driver (or the window) threw; details stay in the host log.</summary>
    public const string Error = "SESSION_ERROR";

    /// <summary>The shell's UI thread is gone (headless / teardown).</summary>
    public const string Unavailable = "SESSION_UNAVAILABLE";
}

/// <summary>Inputs for <see cref="InteractiveSession"/>.</summary>
public sealed class InteractiveSessionOptions
{
    /// <summary>The shell's browsers (D91): registered by <c>UseWindows</c> and <c>UseChromium</c> as
    /// <see cref="ISessionHost"/>. The window is modal to the app's main window and wears its icon.</summary>
    public required ISessionHost Host { get; init; }

    /// <summary>
    /// The browser this session runs, configured exactly like a pooled or streaming one.
    /// <para>
    /// 🔴 <b><see cref="SessionBrowserOptions.ProfileDirectory"/> is where the session's isolation is decided</b>: one per
    /// provider, AND per sub-account where a provider serves multiple accounts. The sub scoping is a SECURITY boundary,
    /// not tidiness (measured in the source): definitions under one provider id shared a cookie jar, so one hostile or
    /// sloppy definition could name another's cookie domain and lift the session the user established there. Wipe the
    /// directory to discard the captured session for real (<see cref="InteractiveSession.ClearProfile"/>).
    /// </para>
    /// <para>
    /// <see cref="SessionBrowserOptions.KeepAliveInBackground"/> is the one field this session overrides, from
    /// <see cref="RevealImmediately"/>: a window held off-screen must keep its script running. Everything else passes
    /// through untouched.
    /// </para>
    /// </summary>
    public required SessionBrowserOptions Browser { get; init; }

    /// <summary>Window title.</summary>
    public string Title { get; init; } = "Session";

    /// <summary>
    /// Initial content size, in device-independent pixels: desktop-width by default, since responsive pages reflow to a
    /// mobile layout in a narrow window, and at least one measured provider renders NO interactive UI at all below
    /// desktop width. The driver shrinks to the real content box afterwards via <see cref="SessionController.FitToBox"/>.
    /// </summary>
    public Size ClientSize { get; init; } = new(680, 780);

    /// <summary>Minimum window size, in device-independent pixels.</summary>
    public Size MinimumSize { get; init; } = new(300, 340);

    /// <summary>Window and splash-era fill (the no-flash contract). Null = system default.</summary>
    public Color? BackColor { get; init; }

    /// <summary>
    /// True (default): the window shows immediately and <see cref="InteractiveSession.RunAsync"/> behaves like a modal
    /// flow. False: the SILENT-REFRESH shape, the window made REALIZED BUT OFF-SCREEN, and only a driver call to
    /// <see cref="SessionController.Reveal"/> brings it on screen, so a driver that completes without revealing (the
    /// profile was already signed in) refreshes the session with the user never seeing a window.
    /// </summary>
    public bool RevealImmediately { get; init; } = true;

    /// <summary>
    /// Consulted before every controller navigation (return false to refuse): the same SSRF-shaped seam as the session
    /// pool, since the URLs are data-driven and this window both discloses the rendered page and accepts input.
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>
    /// Loading-state hook (marshalled to the UI thread): show/hide the app's own splash overlay over the browser; the
    /// visual is the app's. Driven by the driver via <see cref="SessionController.SetLoading"/>, plus a one-shot fallback
    /// hide after <see cref="LoadingFallbackTimeout"/> so a driver that never signals can't leave the splash up forever.
    /// </summary>
    public Action<bool>? OnLoading { get; init; }

    /// <summary>See <see cref="OnLoading"/>. Zero disables the fallback.</summary>
    public TimeSpan LoadingFallbackTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// A HUMAN-IN-THE-LOOP browser session: a real window over a persistent, isolated profile, whose DRIVER (app code)
/// navigates, watches, and returns whatever it captured. The kit owns the MECHANICS, not the scenario (D21): a window
/// modal to the app; one session at a time; exactly-once completion (a dropped post or a tripped token cannot wedge the
/// busy gate); the user's close is HELD so the driver gets a final read; and reveal-on-demand, so a driver that finishes
/// without help never shows a window at all (see <see cref="InteractiveSessionOptions.RevealImmediately"/>). The kit
/// ships NO driver: signing in, clearing a captcha, accepting terms is the driver's business, and a worked example lives
/// in the desktop sample.
/// </summary>
public sealed class InteractiveSession
{
    private readonly InteractiveSessionOptions _options;
    private int _busy; // 0 idle, 1 a session window is open (they serialize)

    /// <summary>
    /// TEST SEAM: stands in for the window, so the gate ownership around it can be exercised without a real browser.
    /// Null in production.
    /// </summary>
    internal Func<CancellationToken, InteractiveSessionResult>? RunOnUiOverride;

    /// <summary>A session gated by <paramref name="options"/>. One window at a time; see the type summary.</summary>
    public InteractiveSession(InteractiveSessionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Host);
    }

    /// <summary>True when a session window is currently open.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Run one interactive session. <paramref name="driver"/> receives the controller and returns the captured blob
    /// (null = incomplete). The whole session is awaited: desktop callers long-poll it by design.
    /// </summary>
    public async Task<InteractiveSessionResult> RunAsync(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var ui = _options.Host.Ui;
        if (ui.State is UiTargetState.Gone) return InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Unavailable);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Busy);

        var tcs = new TaskCompletionSource<InteractiveSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🔴 COMPLETING THE CALLER AND RELEASING THE GATE ARE TWO DIFFERENT EVENTS, and merging them was the bug. The
        // caller must be answered the moment it cancels: that is what stops a never-run post from hanging it forever.
        // But the gate ALSO opened then, while the window was still on screen: the caller took "cancelled" as
        // "finished", called ClearProfile against a profile the live browser still holds (which throws into a swallow),
        // and a second RunAsync sailed past the gate to open a SECOND window on the same profile.
        //
        // So the gate belongs to whoever owns a WINDOW. `owner` says who that is, and only one of the two paths can
        // claim it:
        //   0 = nobody yet · 1 = the UI body is running the window · 2 = cancelled before it started
        var owner = 0;
        void Complete(InteractiveSessionResult result) => tcs.TrySetResult(result);
        void ReleaseGate() => Interlocked.Exchange(ref _busy, 0);

        using var registration = cancellationToken.Register(() =>
        {
            Complete(InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled));
            // Release ONLY if no window ever came up. If the UI body got there first it owns the gate, and it opens it
            // when the window is really gone.
            if (Interlocked.CompareExchange(ref owner, 2, 0) == 0) ReleaseGate();
        });
        var posted = ui.Queue(async () =>
        {
            // Lost to cancellation: it already answered the caller AND opened the gate, so there is nothing left to own
            // and no window to create.
            if (Interlocked.CompareExchange(ref owner, 1, 0) != 0) return;

            InteractiveSessionResult result;
            try
            {
                // The window seam, mirroring the pool's factory/reset overrides: what happens between "a window is up"
                // and "the window is gone" needs a real browser, so the GATE OWNERSHIP around it would otherwise be
                // untestable.
                result = RunOnUiOverride is { } fake
                    ? fake(cancellationToken)
                    : await RunOnUiAsync(driver, cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                // Details stay host-side; the wire learns only the code (the error contract).
                result = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Error);
            }
            Complete(result);
            ReleaseGate();   // the window is gone and the profile is free
        });
        if (!posted)
        {
            Complete(InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Unavailable));
            // The post never landed, so no window exists and no body will ever run.
            if (Interlocked.CompareExchange(ref owner, 2, 0) == 0) ReleaseGate();
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The browser this session actually runs: the app's own options, with the ONE field the session owns overridden.
    /// Extracted so the pass-through is TESTABLE without a browser: everything past here needs a real one, and the
    /// alternative was a rule saying "remember to forward the new field". <c>with</c> keeps a shell's own derived
    /// options' type and fields.
    /// </summary>
    internal static SessionBrowserOptions ComposeBrowserOptions(SessionBrowserOptions browser, bool revealImmediately) =>
        browser with { KeepAliveInBackground = !revealImmediately };

    /// <summary>
    /// Runs on the UI thread: opens the window, drives the session inside it, and returns once the window is GONE, so
    /// the gate opens only when the profile is free.
    /// </summary>
    private async Task<InteractiveSessionResult> RunOnUiAsync(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.Browser.ProfileDirectory);
        var host = _options.Host;

        // Loading-state plumbing: driver-driven (SetLoading) with a one-shot fallback hide so a driver that never signals
        // can't leave the app's splash up forever.
        using var fallback = new CancellationTokenSource();
        if (_options.OnLoading is { } onLoading)
        {
            // GUARDED: this is app code on the UI thread with no caller left on its stack.
            AppCallback.Run(() => onLoading(true));
            if (_options.LoadingFallbackTimeout > TimeSpan.Zero)
            {
                _ = Task.Delay(_options.LoadingFallbackTimeout, fallback.Token).ContinueWith(
                    _ => host.Ui.Post(() => AppCallback.Run(() => onLoading(false))),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }

        var outcome = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled);
        ISessionWindow? window = null;
        SessionController? controller = null;
        try
        {
            // 🔴 INSIDE the try: the finally holds the ONE unconditional `OnLoading(false)`, and `OnLoading(true)` has
            // already run, so a session cancelled here must still reach it.
            cancellationToken.ThrowIfCancellationRequested();

            var sessionId = RenderSessionPool.NewSessionId();
            window = await host.OpenWindowAsync(new SessionWindowDefinition
            {
                Options = ComposeBrowserOptions(_options.Browser, _options.RevealImmediately),
                // One browser, one session: unlike the pool's, this identity never changes.
                Scope = () => sessionId,
                Title = _options.Title,
                ClientSize = _options.ClientSize,
                MinimumSize = _options.MinimumSize,
                BackColor = _options.BackColor,
                Revealed = _options.RevealImmediately,
            }, cancellationToken).ConfigureAwait(true);

            controller = new SessionController(host.Ui, window.Browser, window, _options.NavigationGuard,
                _options.OnLoading, sessionId);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controller.WindowClosed);
            var driving = driver(controller, linked.Token);
            // 🔴 THE FLOW ENDS WITH THE WINDOW, whatever the driver is doing. A close that is not held (the app exiting,
            // the OS, a close that is not a person's) takes the window without cancelling WindowClosed, and a driver
            // waiting on the page would otherwise keep this session, and its busy gate, open forever with no window.
            if (await Task.WhenAny(driving, window.Closed).ConfigureAwait(true) != driving)
            {
                linked.Cancel();   // the driver's token says so too, so it can stop
                _ = driving.ContinueWith(static t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new OperationCanceledException();
            }
            var blob = await driving.ConfigureAwait(true);
            outcome = !string.IsNullOrEmpty(blob)
                ? InteractiveSessionResult.Ok(blob)
                : InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Incomplete);
        }
        catch (OperationCanceledException)
        {
            outcome = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled);
        }
        catch
        {
            outcome = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Error);
        }
        finally
        {
            // ORDER IS LOAD-BEARING. Finish() + Close() go FIRST; the app callback goes last, guarded. OnLoading is APP
            // code, so a throw before Finish() would leave the controller HOLDING the user's close, and it would then
            // cancel EVERY close including the app's own exit. One throwing app callback once bricked the app that way.
            controller?.Finish();               // allow the real close (a user close was held)
            if (window is not null) AppCallback.Run(window.Close);

            fallback.Cancel();

            // Drop the splash unconditionally: a driver that threw before its own SetLoading(false) (e.g. the browser
            // could not be made) would otherwise leave the app's overlay up for the process lifetime, and the fallback
            // that guards that has just been cancelled.
            if (_options.OnLoading is { } done) AppCallback.Run(() => done(false));
        }

        // The gate opens when the window is really gone and the profile with it.
        if (window is not null) await window.Closed.ConfigureAwait(true);
        return outcome;
    }

    /// <summary>
    /// Wipe a session's persistent profile so discarding it is REAL — deleting only the captured blob
    /// would still let the next session silently re-establish itself from the cached profile cookies
    /// (measured: the user "signed out" and came back already signed in). Wipe the provider's whole
    /// tree, sub-accounts included, when the whole provider is discarded.
    /// <para>
    /// 🔴 <b>CHECK THE RESULT when you are telling a user they signed out.</b> The commonest failure is
    /// a profile still LOCKED by a session window that has not finished closing, and a silent false
    /// there recreates the very incident this method exists to prevent: the app says "signed out", the
    /// cookies survive, and the next session walks straight back in. Returning false means the cookies
    /// are still on disk — close the window and call again.
    /// </para>
    /// </summary>
    /// <param name="profileDirectory">
    /// The profile to wipe. Build it with <see cref="ComposeProfileDirectory"/>; a path containing
    /// <c>..</c>, one that IS a volume root, or a browser's whole data folder (the Chromium shell's
    /// <c>ProfilesDirectory</c>) is refused.
    /// </param>
    /// <returns>True when the tree is gone (including when it was never there).</returns>
    /// <exception cref="ArgumentException">The path contains a <c>..</c> segment, is a volume root, or is a browser's
    /// whole data folder.</exception>
    public static bool ClearProfile(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        // A RECURSIVE DELETE on a caller-composed path, normally built from data-driven provider/account
        // identifiers — so a stray ".." segment would aim it outside the sessions root.
        if (HasTraversalSegment(profileDirectory))
            throw new ArgumentException("profileDirectory must not contain '..' segments", nameof(profileDirectory));

        // ⚠ AND REFUSE A VOLUME ROOT. The traversal check above stops a path CLIMBING out of the
        // sessions tree; it says nothing about one that never pointed inside it. `C:\` and
        // `\\server\share\` are what an empty or collapsed composition produces, and this method would
        // have recursively deleted the volume, swallowing every error on the way.
        var full = Path.GetFullPath(profileDirectory);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("profileDirectory must not be a volume root", nameof(profileDirectory));
        // ⚠ AND A BROWSER'S WHOLE DATA FOLDER, which holds Chromium's "Local State" directly; a profile never does (and a
        // WebView2 user data folder keeps it a level down). The Chromium shell's ProfilesDirectory is one, and passed
        // here instead of a profile composed inside it, this deleted every profile and the app's own browser state.
        if (File.Exists(Path.Combine(full, "Local State")))
            throw new ArgumentException("profileDirectory is a browser's whole data folder, not one profile in it: "
                                        + "compose the profile with ComposeProfileDirectory", nameof(profileDirectory));

        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            return !Directory.Exists(full);
        }
        catch
        {
            // Locked (a session window still closing), or gone from under us. Never throws — a logout
            // path must not become an exception — but it no longer CLAIMS to have cleared anything.
            return !Directory.Exists(full);
        }
    }

    /// <summary>
    /// Compose a per-account profile directory under <paramref name="root"/> from untrusted
    /// identifier <paramref name="segments"/> (a provider id, an account id, …). Each segment must be
    /// a single plain name: separators, <c>..</c>, drive qualifiers and Windows reserved device names
    /// are rejected. Per-provider/per-account scoping is the session stack's isolation boundary — two
    /// accounts sharing a directory share a cookie jar.
    /// <para>
    /// ⚠ <b>Two identifiers differing only in CASE are the same directory here</b>, because the Windows
    /// filesystem says so and this method cannot overrule it. If account ids are case-sensitive in your
    /// system, fold or encode them before passing them in — otherwise <c>bob</c> and <c>Bob</c> share a
    /// cookie jar, which is the one thing this is for.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A segment is empty, contains a separator or drive qualifier, is <c>.</c>/<c>..</c>, contains an
    /// invalid file-name character, names a Windows reserved device, or does not survive Windows' path
    /// normalisation unchanged (a trailing dot or space, a run of dots).
    /// </exception>
    public static string ComposeProfileDirectory(string root, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(segments);

        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                               "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                               "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                throw new ArgumentException("profile segments must be non-empty", nameof(segments));
            if (segment.Contains('/') || segment.Contains('\\') || segment.Contains(':'))
                throw new ArgumentException($"profile segment '{segment}' must not contain a path separator or drive qualifier", nameof(segments));
            if (segment is "." or "..")
                throw new ArgumentException("profile segments must not be '.' or '..'", nameof(segments));
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"profile segment '{segment}' contains invalid file-name characters", nameof(segments));
            var stem = Path.GetFileNameWithoutExtension(segment);
            if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"profile segment '{segment}' is a Windows reserved device name", nameof(segments));

            // 🔴 THE SEGMENT MUST SURVIVE WINDOWS' OWN NORMALISATION UNCHANGED, and this is asked of the
            // OS rather than enumerated, because every check above is a blocklist and this is the hole a
            // blocklist leaves. Trailing dots and spaces are STRIPPED, and a run of dots collapses to
            // nothing — measured with `Path.GetFullPath` against a root of `C:\root`:
            //
            //     "..."  ".. ."  " . "   ->  C:\root\      the ROOT itself
            //     "acct."  "acct "       ->  C:\root\acct  the same jar as "acct"
            //
            // Every one of them passes `IsNullOrWhiteSpace`, the separator test, the `.`/`..` test,
            // `GetInvalidFileNameChars` (a dot and a space are both legal) and the reserved-name test —
            // and the containment check below passes too, because the root does start with the root. So
            // an account id of `"..."` returned the whole sessions tree, which `ClearProfile` would then
            // delete for every account; and `"acct "` silently shared `"acct"`'s cookie jar, which is
            // precisely the isolation this method exists to provide.
            var probe = Path.Combine(Path.GetFullPath(root), segment);
            if (Path.GetFullPath(probe) != probe)
            {
                throw new ArgumentException(
                    $"profile segment '{segment}' is not a stable directory name — Windows normalises it "
                    + "away (a trailing dot or space is stripped, a run of dots collapses). Trim it, or "
                    + "encode the identifier.", nameof(segments));
            }
        }

        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(segments).ToArray()));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && combined != fullRoot)
            throw new ArgumentException("the composed profile directory would fall outside the root", nameof(segments));
        return combined;
    }

    private static bool HasTraversalSegment(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar)
            .Any(s => s == "..");
}
