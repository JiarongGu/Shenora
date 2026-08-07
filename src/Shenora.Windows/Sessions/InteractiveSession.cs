using Shenora.Ipc;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>Outcome of a <see cref="InteractiveSession.RunAsync"/> flow.</summary>
public sealed class SessionResult
{
    /// <summary>True when the driver captured a session.</summary>
    public required bool Success { get; init; }

    /// <summary>The driver's captured session blob (its own format — commonly serialized cookies).</summary>
    public string? Blob { get; init; }

    /// <summary>A <see cref="SessionErrorCodes"/> value when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    internal static SessionResult Ok(string blob) => new() { Success = true, Blob = blob };

    internal static SessionResult Fail(string errorCode) => new() { Success = false, ErrorCode = errorCode };

    /// <summary>
    /// Throw this outcome's failure as an <see cref="OperationException"/> — the bridge from
    /// <see cref="SessionErrorCodes"/> into the IPC error contract (P5.5 H9.4). No-op on success.
    /// <para>
    /// The two vocabularies were never really separate: these codes are already SCREAMING_SNAKE i18n
    /// keys in the shape <c>IpcErrorCodes</c> uses, so the only thing missing was a typed path between
    /// them — and without one, every app routing a session over IPC hand-wrote the same
    /// <c>if (!result.Success) throw new OperationException(result.ErrorCode!)</c>. Throwing (rather
    /// than returning an error object) is what plugs into the dispatcher's documented boundary:
    /// <c>BaseFacade</c> and <c>MessageDispatcher</c> already turn an <see cref="OperationException"/>
    /// into the structured wire error, so a facade route becomes a single call.
    /// </para>
    /// <code>
    /// var result = await session.RunAsync(flow.DriveAsync, cancellationToken);
    /// result.ThrowIfFailed();          // SESSION_BUSY / SESSION_CANCELLED / … cross as the wire code
    /// return new { blob = result.Blob };
    /// </code>
    /// </summary>
    /// <exception cref="OperationException">When <see cref="Success"/> is false.</exception>
    public void ThrowIfFailed()
    {
        if (Success) return;
        // A failure with no code should be impossible (every Fail site passes one), but reporting
        // UNKNOWN_ERROR beats throwing a NullReference out of an error path.
        throw new OperationException(ErrorCode ?? IpcErrorCodes.UnknownError);
    }
}

/// <summary>Error codes <see cref="InteractiveSession"/> reports (wire-friendly i18n keys, the family shape).</summary>
public static class SessionErrorCodes
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
    /// The session's persistent profile directory — one per provider, AND per sub-account where a
    /// provider serves multiple accounts. The sub scoping is a SECURITY boundary, not tidiness
    /// (measured in the source): definitions under one provider id shared a cookie jar, so one
    /// hostile or sloppy definition could name another's cookie domain and lift the session the
    /// user established there. Compose the path per (provider, sub) and each account's cookies
    /// live in a store the others cannot open. Wipe it to discard the captured session for real
    /// (<see cref="InteractiveSession.ClearProfile"/>).
    /// </summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>Window title.</summary>
    public string Title { get; init; } = "Session";

    /// <summary>
    /// Initial client size — desktop-width by default ON PURPOSE: responsive pages reflow
    /// to a mobile layout in a narrow window, and at least one measured provider renders NO
    /// interactive UI at all below desktop width. The driver shrinks to the real content box afterwards
    /// via <see cref="SessionController.FitToBox"/>.
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
    /// behaves like the server-backed sibling's modal flow. False: the SILENT-REFRESH shape from
    /// the primary sibling — the window is created REALIZED BUT OFF-SCREEN, and only a driver
    /// call to <see cref="SessionController.Reveal"/> brings it on screen; a driver that
    /// completes without revealing (the persistent profile was already signed in) refreshes the
    /// session with the user never seeing a window ("no interaction ⇒ no window").
    /// </summary>
    public bool RevealImmediately { get; init; } = true;

    /// <summary>
    /// Consulted before every controller navigation (return false to refuse) — the same
    /// SSRF-shaped seam as the session pool: the URLs are data-driven (provider definitions),
    /// and this window both discloses the rendered page and accepts input.
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>
    /// Loading-state hook (marshalled to the UI thread): show/hide the app's own splash overlay
    /// over the WebView2 — the visual is the app's (headless). Driven by the driver via
    /// <see cref="SessionController.SetLoading"/>, plus a one-shot fallback hide after
    /// <see cref="LoadingFallbackTimeout"/> so a driver that never signals can't leave the
    /// splash up forever (measured — three independent drop paths in the source).
    /// </summary>
    public Action<bool>? OnLoading { get; init; }

    /// <summary>See <see cref="OnLoading"/>. Zero disables the fallback.</summary>
    public TimeSpan LoadingFallbackTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// A HUMAN-IN-THE-LOOP browser session: a real WebView2 window over a persistent, isolated profile,
/// whose DRIVER — app code — navigates, watches, and returns whatever it captured.
/// <para>
/// The kit owns the MECHANICS, not the scenario (D21). Those mechanics, merged from both family
/// proofs: a modal <c>ShowDialog</c> nested message loop so the window pumps reliably even when
/// triggered from a background thread; one session at a time; exactly-once completion (a dropped
/// post or a tripped token cannot wedge the busy gate); the user's close is HELD so the driver gets
/// a final read; and reveal-on-demand, so a driver that finishes without help never shows a window
/// at all (see <see cref="InteractiveSessionOptions.RevealImmediately"/>).
/// </para>
/// <para>
/// WHAT IT IS FOR is the driver's business: signing in, clearing a captcha or an interstitial,
/// accepting terms, completing a checkout step — anything that needs a real browser and, sometimes,
/// a real person. This type was called <c>LoginWindow</c> until P5.5 H9.7 and contained no login
/// logic even then; the name was the last of the login vocabulary H4.6 started removing when it made
/// the controller neutral. The package ships NO driver at all: a cookie-login one used to, and was
/// removed in P7 because a login workflow is a product, not a mechanism (D21 amended). A worked
/// example lives in the desktop sample — copy it, it is yours.
/// </para>
/// </summary>
public sealed class InteractiveSession
{
    private readonly InteractiveSessionOptions _options;
    private int _busy; // 0 idle, 1 a session window is open (they serialize)

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
    public async Task<SessionResult> RunAsync(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var anchor = _options.Anchor;
        if (anchor.IsDisposed) return SessionResult.Fail(SessionErrorCodes.Unavailable);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return SessionResult.Fail(SessionErrorCodes.Busy);

        var tcs = new TaskCompletionSource<SessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Release the busy gate + complete EXACTLY ONCE, whoever finishes first: the UI
        // delegate, or the token if that delegate is never pumped (host teardown between the
        // post and the message loop) — otherwise a dropped post would wedge the gate at busy
        // for the whole process (every future session answers SESSION_BUSY) and hang the caller
        // (the source's measured incident).
        void Finish(SessionResult result)
        {
            if (tcs.TrySetResult(result)) Interlocked.Exchange(ref _busy, 0);
        }

        using var registration = cancellationToken.Register(() => Finish(SessionResult.Fail(SessionErrorCodes.Cancelled)));
        try
        {
            anchor.BeginInvoke(new Action(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Finish(SessionResult.Fail(SessionErrorCodes.Cancelled));
                    return;
                }
                SessionResult result;
                try
                {
                    result = RunOnUi(driver, cancellationToken);
                }
                catch
                {
                    // Details stay host-side; the wire learns only the code (the error contract).
                    result = SessionResult.Fail(SessionErrorCodes.Error);
                }
                Finish(result);
            }));
        }
        catch
        {
            Finish(SessionResult.Fail(SessionErrorCodes.Unavailable));
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the UI thread. Shows the window MODALLY (ShowDialog → its own nested message
    /// loop) and drives the session inside it: on <c>Shown</c> the WebView2 comes up, the driver
    /// runs over the controller, and when it returns the window closes (ending ShowDialog).
    /// </summary>
    private SessionResult RunOnUi(
        Func<SessionController, CancellationToken, Task<string?>> driver,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.ProfileDirectory);

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
                // The same callback is already guarded on the two paths below (see the finally block's
                // own comment, which records what one unguarded OnLoading cost); this site was the
                // one that still ran bare.
                fallback.Tick += (_, _) =>
                {
                    fallback.Stop();
                    Shenora.AppCallback.Run(() => onLoading(false));
                };
                fallback.Start();
            }
        }

        var outcome = SessionResult.Fail(SessionErrorCodes.Cancelled);
        form.Shown += async (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested) { form.Close(); return; }
            SessionController? controller = null;
            try
            {
                await SessionBrowser.InitializeAsync(web, new SessionBrowserOptions
                {
                    ProfileDirectory = _options.ProfileDirectory,
                    KeepAliveInBackground = !_options.RevealImmediately, // a hidden window must keep its JS running
                });

                controller = new SessionController(form, web, _options.NavigationGuard, _options.OnLoading, foreground: true);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controller.WindowClosed);
                var blob = await driver(controller, linked.Token);
                outcome = !string.IsNullOrEmpty(blob)
                    ? SessionResult.Ok(blob)
                    : SessionResult.Fail(SessionErrorCodes.Incomplete);
            }
            catch (OperationCanceledException)
            {
                outcome = SessionResult.Fail(SessionErrorCodes.Cancelled);
            }
            catch
            {
                outcome = SessionResult.Fail(SessionErrorCodes.Error);
            }
            finally
            {
                // ORDER IS LOAD-BEARING (P5.5 H2). Finish() + Close() go FIRST, and the app callback
                // goes last inside its own try/catch.
                //
                // This block used to run OnLoading BEFORE Finish(), and OnLoading is APP code — a
                // splash toggle, so ObjectDisposedException is the obvious way for it to throw. This
                // whole handler is `async void`, so a throw here escaped as an unhandled UI-thread
                // exception and Finish() never ran. The foreground controller HOLDS the user's close
                // until Finish() (so a driver gets its last cookie read), which means its FormClosing
                // handler then cancelled EVERY close — the user's, and Application.Exit's. Result: an
                // unclosable modal window, ShowDialog never returning, and the busy gate held
                // for the process lifetime. One throwing app callback bricked the app.
                controller?.Finish();               // allow the real close (a user close was held)
                if (!form.IsDisposed) form.Close(); // ends ShowDialog → RunOnUi returns outcome

                try { fallback?.Dispose(); } catch (Exception) { /* timer teardown is best-effort */ }

                // Drop the splash unconditionally: a driver that threw before its own
                // SetLoading(false) (e.g. SessionBrowser init failed) would otherwise leave the
                // app's overlay up for the process lifetime — the measured incident the fallback
                // timer guards, which the timer's own disposal here would defeat.
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
    /// Wipe a session's persistent profile so discarding it is REAL — deleting only the captured
    /// blob would still let the next session silently re-establish itself from the cached profile
    /// cookies (both siblings' measured lesson: the user "signed out" and came back already signed
    /// in). Wipe the provider's whole tree, sub-accounts included, when the whole provider is being
    /// discarded — a sub's cookies left behind re-establish too.
    /// Best-effort: a locked folder (a window still open) just isn't cleared.
    /// </summary>
    public static void ClearProfile(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        // This is a RECURSIVE DELETE on a caller-composed path, and the path is normally built from
        // data-driven provider/account identifiers — so a stray ".." segment would aim it outside the
        // sessions root. Refuse rather than trust the caller (found in the P0–P5 review). Use
        // ComposeProfileDirectory to build the path and this can't arise.
        if (HasTraversalSegment(profileDirectory))
            throw new ArgumentException("profileDirectory must not contain '..' segments", nameof(profileDirectory));
        try
        {
            if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, recursive: true);
        }
        catch
        {
            // locked / already gone — the caller's stored session is cleared regardless
        }
    }

    /// <summary>
    /// Compose a per-account profile directory under <paramref name="root"/> from untrusted
    /// identifier <paramref name="segments"/> (a provider id, an account id, …). Each segment must be
    /// a single plain name: separators, <c>..</c>, drive qualifiers and Windows reserved device names
    /// are rejected. Per-provider/per-account scoping is the session stack's isolation boundary — two
    /// accounts sharing a directory share a cookie jar — and the library previously documented that
    /// boundary while shipping no safe way to build the path.
    /// </summary>
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
