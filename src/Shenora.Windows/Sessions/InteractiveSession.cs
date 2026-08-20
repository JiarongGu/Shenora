using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Windows;

/// <summary>Outcome of a <see cref="InteractiveSession.RunAsync"/> flow.</summary>
public sealed class InteractiveSessionResult
{
    /// <summary>True when the driver captured a session.</summary>
    public required bool Success { get; init; }

    /// <summary>The driver's captured session blob (its own format — commonly serialized cookies).</summary>
    public string? Blob { get; init; }

    /// <summary>A <see cref="InteractiveSessionErrorCodes"/> value when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    internal static InteractiveSessionResult Ok(string blob) => new() { Success = true, Blob = blob };

    internal static InteractiveSessionResult Fail(string errorCode) => new() { Success = false, ErrorCode = errorCode };

    /// <summary>
    /// Throw this outcome's failure as an <see cref="ShenoraException"/> — the bridge from
    /// <see cref="InteractiveSessionErrorCodes"/> into the IPC error contract, so a facade route is one
    /// call: <c>ModuleBase</c> and <c>MessageDispatcher</c> already turn one into the structured wire
    /// error. No-op on success.
    /// </summary>
    /// <exception cref="ShenoraException">When <see cref="Success"/> is false.</exception>
    public void ThrowIfFailed()
    {
        if (Success) return;
        // A failure with no code should be impossible (every Fail site passes one), but reporting
        // UNKNOWN_ERROR beats throwing a NullReference out of an error path.
        throw new ShenoraException(ErrorCode ?? IpcErrorCodes.UnknownError);
    }
}

/// <summary>Error codes <see cref="InteractiveSession"/> reports (wire-friendly i18n keys, the family shape).</summary>
public static class InteractiveSessionErrorCodes
{
    /// <summary>Another session is already open — interactive sessions serialize.</summary>
    public const string Busy = "SESSION_BUSY";

    /// <summary>The caller's token tripped, or the user closed before the driver captured.</summary>
    public const string Cancelled = "SESSION_CANCELLED";

    /// <summary>The driver finished without capturing anything (e.g. the user closed the window).</summary>
    public const string Incomplete = "SESSION_INCOMPLETE";

    /// <summary>The driver (or the window) threw — details stay in the host log.</summary>
    public const string Error = "SESSION_ERROR";

    /// <summary>The UI-thread anchor is gone (headless / teardown).</summary>
    public const string Unavailable = "SESSION_UNAVAILABLE";
}

/// <summary>Inputs for <see cref="InteractiveSession"/>.</summary>
public sealed class InteractiveSessionOptions
{
    /// <summary>A live UI-thread control (typically the main window) window work marshals onto.</summary>
    public required Control Anchor { get; init; }

    /// <summary>
    /// The browser this session runs, configured exactly like a pooled or streaming one.
    /// <para>
    /// 🔴 <b><see cref="SessionBrowserOptions.ProfileDirectory"/> is where the session's isolation is
    /// decided</b> — one per provider, AND per sub-account where a provider serves multiple accounts.
    /// The sub scoping is a SECURITY boundary, not tidiness (measured in the source): definitions under
    /// one provider id shared a cookie jar, so one hostile or sloppy definition could name another's
    /// cookie domain and lift the session the user established there. Wipe the directory to discard the
    /// captured session for real (<see cref="InteractiveSession.ClearProfile"/>).
    /// </para>
    /// <para>
    /// <see cref="SessionBrowserOptions.KeepAliveInBackground"/> is the one field this session
    /// overrides, from <see cref="RevealImmediately"/>: a window held off-screen must keep its JS
    /// running. Everything else passes through untouched.
    /// </para>
    /// </summary>
    public required SessionBrowserOptions Browser { get; init; }

    /// <summary>Window title.</summary>
    public string Title { get; init; } = "Session";

    /// <summary>
    /// Initial client size — desktop-width by default: responsive pages reflow to a mobile layout in a
    /// narrow window, and at least one measured provider renders NO interactive UI at all below desktop
    /// width. The driver shrinks to the real content box afterwards via
    /// <see cref="SessionController.FitToBox"/>.
    /// </summary>
    public Size ClientSize { get; init; } = new(680, 780);

    /// <summary>Minimum window size.</summary>
    public Size MinimumSize { get; init; } = new(300, 340);

    /// <summary>Window + splash-era fill (the no-flash contract). Null = system default.</summary>
    public Color? BackColor { get; init; }

    /// <summary>Window icon. Null = none (cosmetic).</summary>
    public Icon? Icon { get; init; }

    /// <summary>Owner window for z-order (usually the main window). Null = unowned.</summary>
    public Form? Owner { get; init; }

    /// <summary>
    /// True (default): the window shows immediately and <see cref="InteractiveSession.RunAsync"/>
    /// behaves like a modal flow. False: the SILENT-REFRESH shape — the window is created REALIZED BUT
    /// OFF-SCREEN, and only a driver call to <see cref="SessionController.Reveal"/> brings it on screen,
    /// so a driver that completes without revealing (the profile was already signed in) refreshes the
    /// session with the user never seeing a window.
    /// </summary>
    public bool RevealImmediately { get; init; } = true;

    /// <summary>
    /// Consulted before every controller navigation (return false to refuse) — the same SSRF-shaped seam
    /// as the session pool: the URLs are data-driven, and this window both discloses the rendered page
    /// and accepts input.
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>
    /// Loading-state hook (marshalled to the UI thread): show/hide the app's own splash overlay over the
    /// WebView2 — the visual is the app's. Driven by the driver via
    /// <see cref="SessionController.SetLoading"/>, plus a one-shot fallback hide after
    /// <see cref="LoadingFallbackTimeout"/> so a driver that never signals can't leave the splash up
    /// forever.
    /// </summary>
    public Action<bool>? OnLoading { get; init; }

    /// <summary>See <see cref="OnLoading"/>. Zero disables the fallback.</summary>
    public TimeSpan LoadingFallbackTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// A HUMAN-IN-THE-LOOP browser session: a real WebView2 window over a persistent, isolated profile,
/// whose DRIVER — app code — navigates, watches, and returns whatever it captured. The kit owns the
/// MECHANICS, not the scenario (D21): a modal <c>ShowDialog</c> nested message loop so the window pumps
/// reliably even when triggered from a background thread; one session at a time; exactly-once completion
/// (a dropped post or a tripped token cannot wedge the busy gate); the user's close is HELD so the
/// driver gets a final read; and reveal-on-demand, so a driver that finishes without help never shows a
/// window at all (see <see cref="InteractiveSessionOptions.RevealImmediately"/>). The package ships NO
/// driver — signing in, clearing a captcha, accepting terms is the driver's business, and a worked
/// example lives in the desktop sample.
/// </summary>
public sealed class InteractiveSession
{
    private readonly InteractiveSessionOptions _options;
    private int _busy; // 0 idle, 1 a session window is open (they serialize)

    /// <summary>
    /// TEST SEAM: stands in for the modal window, so the gate ownership around it can be exercised
    /// without a real WebView2 and a nested message loop. Null in production.
    /// </summary>
    internal Func<CancellationToken, InteractiveSessionResult>? RunOnUiOverride;

    /// <summary>A session gated by <paramref name="options"/>. One window at a time — see the type summary.</summary>
    public InteractiveSession(InteractiveSessionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>True when a session window is currently open.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Run one interactive session. <paramref name="driver"/> receives the controller and returns
    /// the captured blob (null = incomplete). The whole session is awaited — desktop callers
    /// long-poll it by design.
    /// </summary>
    public async Task<InteractiveSessionResult> RunAsync(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var anchor = _options.Anchor;
        if (anchor.IsDisposed) return InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Unavailable);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Busy);

        var tcs = new TaskCompletionSource<InteractiveSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🔴 COMPLETING THE CALLER AND RELEASING THE GATE ARE TWO DIFFERENT EVENTS, and merging them was
        // the bug. The caller must be answered the moment it cancels — that is what stops a never-pumped
        // post from hanging it forever. But the gate ALSO opened then, while the modal window was still
        // on screen: the caller took "cancelled" as "finished", called ClearProfile against a profile the
        // live browser still holds (which throws into a swallow), and a second RunAsync sailed past the
        // gate to open a SECOND window on the same profile.
        //
        // So the gate belongs to whoever owns a WINDOW. `owner` says who that is, and only one of the
        // two paths can claim it:
        //   0 = nobody yet · 1 = the UI delegate is running the window · 2 = cancelled before it started
        var owner = 0;
        void Complete(InteractiveSessionResult result) => tcs.TrySetResult(result);
        void ReleaseGate() => Interlocked.Exchange(ref _busy, 0);

        using var registration = cancellationToken.Register(() =>
        {
            Complete(InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled));
            // Release ONLY if no window ever came up. If the UI delegate got there first it owns the
            // gate, and it opens it when ShowDialog returns — i.e. when the window is really gone.
            if (Interlocked.CompareExchange(ref owner, 2, 0) == 0) ReleaseGate();
        });
        try
        {
            anchor.BeginInvoke(new Action(() =>
            {
                // Lost to cancellation: it already answered the caller AND opened the gate, so there is
                // nothing left to own and no window to create.
                if (Interlocked.CompareExchange(ref owner, 1, 0) != 0) return;

                InteractiveSessionResult result;
                try
                {
                    // The window seam, mirroring RenderSessionPool's factory/reset overrides: what
                    // happens between "a window is up" and "the window is gone" needs a real WebView2
                    // and a modal loop, so the GATE OWNERSHIP around it would otherwise be untestable.
                    result = RunOnUiOverride is { } fake
                        ? fake(cancellationToken)
                        : RunOnUi(driver, cancellationToken);
                }
                catch
                {
                    // Details stay host-side; the wire learns only the code (the error contract).
                    result = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Error);
                }
                Complete(result);
                ReleaseGate();   // ShowDialog has returned — the window is gone and the profile is free
            }));
        }
        catch
        {
            Complete(InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Unavailable));
            // The post never landed, so no window exists and no delegate will ever run.
            if (Interlocked.CompareExchange(ref owner, 2, 0) == 0) ReleaseGate();
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The browser this session actually runs: the app's own options, with the ONE field the session
    /// owns overridden. Extracted so the pass-through is TESTABLE without a WebView2 — everything past
    /// here needs a real browser and a modal loop, and the alternative was a rule saying "remember to
    /// forward the new field".
    /// </summary>
    internal static SessionBrowserOptions ComposeBrowserOptions(
        SessionBrowserOptions browser, bool revealImmediately) =>
        browser with { KeepAliveInBackground = !revealImmediately };

    /// <summary>
    /// Runs on the UI thread. Shows the window MODALLY (ShowDialog → its own nested message
    /// loop) and drives the session inside it: on <c>Shown</c> the WebView2 comes up, the driver
    /// runs over the controller, and when it returns the window closes (ending ShowDialog).
    /// </summary>
    private InteractiveSessionResult RunOnUi(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.Browser.ProfileDirectory);

        using var form = new Form
        {
            Text = _options.Title,
            ClientSize = _options.ClientSize,
            MinimumSize = _options.MinimumSize,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true,
        };
        if (_options.BackColor is { } backColor) form.BackColor = backColor;
        if (_options.Icon is { } icon)
        {
            try { form.Icon = icon; } catch { /* cosmetic */ }
        }
        if (!_options.RevealImmediately)
        {
            // Silent-refresh shape: realized (WebView2 needs a real handle) but parked
            // off-screen; Reveal() brings it on screen only when interaction is needed.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OffscreenWindow.ParkedCoordinate, OffscreenWindow.ParkedCoordinate);
            form.ShowInTaskbar = false;
        }

        var web = new WebView2Control { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        // Loading-state plumbing: driver-driven (SetLoading) with a one-shot fallback hide so a
        // driver that never signals can't leave the app's splash up forever.
        System.Windows.Forms.Timer? fallback = null;
        if (_options.OnLoading is { } onLoading)
        {
            onLoading(true);
            if (_options.LoadingFallbackTimeout > TimeSpan.Zero)
            {
                fallback = new System.Windows.Forms.Timer { Interval = (int)_options.LoadingFallbackTimeout.TotalMilliseconds };
                // GUARDED: a timer tick has no caller on its stack, so a throwing OnLoading here is an
                // unhandled UI-thread exception — the family bootstrap's modal crash dialog — and this
                // is a splash toggle, for which ObjectDisposedException is the obvious way to throw.
                fallback.Tick += (_, _) =>
                {
                    fallback.Stop();
                    Shenora.AppCallback.Run(() => onLoading(false));
                };
                fallback.Start();
            }
        }

        var outcome = InteractiveSessionResult.Fail(InteractiveSessionErrorCodes.Cancelled);
        form.Shown += async (_, _) =>
        {
            SessionController? controller = null;
            try
            {
                // 🔴 INSIDE the try, and that is the whole fix. Above it, `form.Close(); return;` skips
                // the finally, which holds the ONE unconditional `OnLoading(false)`. `onLoading(true)`
                // has already run by now, so a session cancelled between the post and `Shown` leaves the
                // app's splash up; with `LoadingFallbackTimeout = Zero` — documented as supported —
                // there is no timer to rescue it either, so the overlay stays for the process lifetime.
                // `outcome` is already Cancelled, and the finally closes the window, so nothing else is
                // lost by falling through.
                //
                // ⚠ NO TEST COVERS THIS, and that is measured rather than assumed: reinstating the early
                // return leaves every session test green. Everything below needs a live WebView2 and a
                // modal loop, so it is sample/e2e territory.
                if (cancellationToken.IsCancellationRequested) return;

                var sessionId = SessionBrowser.NewSessionId();
                await SessionBrowser.InitializeAsync(
                    web,
                    ComposeBrowserOptions(_options.Browser, _options.RevealImmediately),
                // One browser, one session: unlike the pool's, this identity never changes.
                sessionScope: () => sessionId);

                controller = new SessionController(form, web, _options.NavigationGuard, _options.OnLoading,
                    foreground: true, id: sessionId);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controller.WindowClosed);
                var blob = await driver(controller, linked.Token);
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
                // ORDER IS LOAD-BEARING. Finish() + Close() go FIRST; the app callback goes last, inside
                // its own try/catch. OnLoading is APP code and this handler is `async void`, so a throw
                // before Finish() escapes as an unhandled UI-thread exception and Finish() never runs —
                // then the foreground controller, which HOLDS the user's close until Finish(), cancels
                // EVERY close including Application.Exit's. One throwing app callback bricked the app.
                controller?.Finish();               // allow the real close (a user close was held)
                if (!form.IsDisposed) form.Close(); // ends ShowDialog → RunOnUi returns outcome

                try { fallback?.Dispose(); } catch (Exception) { /* timer teardown is best-effort */ }

                // Drop the splash unconditionally: a driver that threw before its own SetLoading(false)
                // (e.g. SessionBrowser init failed) would otherwise leave the app's overlay up for the
                // process lifetime — and the fallback timer that guards that has just been disposed.
                try
                {
                    _options.OnLoading?.Invoke(false);
                }
                catch (Exception)
                {
                    // An app splash that throws on the way down must not become the crash that
                    // prevents the window from closing.
                }
            }
        };

        // A silent-refresh window (created off-screen) must be OWNERLESS: ShowDialog disables its
        // owner, so an owned invisible dialog would silently disable the app's main window for the
        // whole refresh. A visible session window owns the main window normally (modal z-order).
        var owner = _options.RevealImmediately ? (_options.Owner ?? _options.Anchor.FindForm()) : null;
        form.ShowDialog(owner is { Visible: true, IsDisposed: false } ? owner : null); // nested loop until the flow closes it
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
    /// <c>..</c>, or one that IS a volume root, is refused.
    /// </param>
    /// <returns>True when the tree is gone (including when it was never there).</returns>
    /// <exception cref="ArgumentException">The path contains a <c>..</c> segment or is a volume root.</exception>
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
