using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>Outcome of a <see cref="LoginWindow.RunAsync"/> flow.</summary>
public sealed class LoginResult
{
    /// <summary>True when the driver captured a session.</summary>
    public required bool Success { get; init; }

    /// <summary>The driver's captured session blob (its own format — commonly serialized cookies).</summary>
    public string? Blob { get; init; }

    /// <summary>A <see cref="LoginErrorCodes"/> value when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    internal static LoginResult Ok(string blob) => new() { Success = true, Blob = blob };

    internal static LoginResult Fail(string errorCode) => new() { Success = false, ErrorCode = errorCode };
}

/// <summary>Error codes <see cref="LoginWindow"/> reports (wire-friendly i18n keys, the family shape).</summary>
public static class LoginErrorCodes
{
    /// <summary>Another login window is already open — logins serialize.</summary>
    public const string Busy = "LOGIN_BUSY";

    /// <summary>The caller's token tripped, or the user closed before the driver captured.</summary>
    public const string Cancelled = "LOGIN_CANCELLED";

    /// <summary>The driver completed without a session (e.g. window closed while signed out).</summary>
    public const string Incomplete = "LOGIN_INCOMPLETE";

    /// <summary>The driver (or the window) threw — details stay in the host log.</summary>
    public const string Error = "LOGIN_ERROR";

    /// <summary>The UI-thread anchor is gone (headless / teardown).</summary>
    public const string Unavailable = "LOGIN_UNAVAILABLE";
}

/// <summary>Inputs for <see cref="LoginWindow"/>.</summary>
public sealed class LoginWindowOptions
{
    /// <summary>A live UI-thread control (typically the main window) window work marshals onto.</summary>
    public required Control Anchor { get; init; }

    /// <summary>
    /// The login's persistent profile directory — one per provider, AND per sub-account where a
    /// provider serves multiple accounts. The sub scoping is a SECURITY boundary, not tidiness
    /// (measured in the source): definitions under one provider id shared a cookie jar, so one
    /// hostile or sloppy definition could name another's cookie domain and lift the session the
    /// user established there. Compose the path per (provider, sub) and each account's cookies
    /// live in a store the others cannot open. Wipe it on logout (<see cref="LoginWindow.ClearProfile"/>).
    /// </summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>Window title.</summary>
    public string Title { get; init; } = "Sign in";

    /// <summary>
    /// Initial client size — desktop-width by default ON PURPOSE: responsive login pages reflow
    /// to a mobile layout in a narrow window, and at least one family provider renders NO login
    /// UI at all below desktop width (measured). The driver shrinks to the login box afterwards
    /// via <see cref="LoginWindowController.FitToBox"/>.
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
    /// True (default): the window shows immediately and <see cref="LoginWindow.RunAsync"/>
    /// behaves like the server-backed sibling's modal flow. False: the SILENT-REFRESH shape from
    /// the primary sibling — the window is created REALIZED BUT OFF-SCREEN, and only a driver
    /// call to <see cref="LoginWindowController.Reveal"/> brings it on screen; a driver that
    /// completes without revealing (the persistent profile was already signed in) refreshes the
    /// session with the user never seeing a window ("no interaction ⇒ no window").
    /// </summary>
    public bool RevealImmediately { get; init; } = true;

    /// <summary>
    /// Consulted before every controller navigation (return false to refuse) — the same
    /// SSRF-shaped seam as the session pool: login URLs are data-driven (provider definitions),
    /// and this window both discloses the rendered page and accepts input.
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>
    /// Loading-state hook (marshalled to the UI thread): show/hide the app's own splash overlay
    /// over the WebView2 — the visual is the app's (headless). Driven by the driver via
    /// <see cref="LoginWindowController.SetLoading"/>, plus a one-shot fallback hide after
    /// <see cref="LoadingFallbackTimeout"/> so a driver that never signals can't leave the
    /// splash up forever (measured — three independent drop paths in the source).
    /// </summary>
    public Action<bool>? OnLoading { get; init; }

    /// <summary>See <see cref="OnLoading"/>. Zero disables the fallback.</summary>
    public TimeSpan LoadingFallbackTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The interactive login window, merged from both family proofs: a real WebView2 window over a
/// per-provider persistent profile whose DRIVER (your <c>DriveLoginAsync</c>-shaped callback, or
/// the built-in <see cref="CookieLoginFlow"/>) navigates, watches, and returns the captured
/// session blob. Runs the flow inside a MODAL <c>ShowDialog</c> nested message loop (so the
/// window pumps reliably even when triggered from a background thread) with the sibling-proven
/// mechanics: one login at a time, exactly-once completion (a dropped post or a tripped token
/// can't wedge the busy gate), the user's close is HELD so the driver gets a final cookie read,
/// and optional silent-refresh (see <see cref="LoginWindowOptions.RevealImmediately"/>).
/// </summary>
public sealed class LoginWindow
{
    private readonly LoginWindowOptions _options;
    private int _busy; // 0 idle, 1 a login window is open (logins serialize)

    public LoginWindow(LoginWindowOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>True when a login window is currently open.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Run one login. <paramref name="driveLogin"/> receives the controller and returns the
    /// captured session blob (null = incomplete). The whole login is awaited — desktop callers
    /// long-poll it by design.
    /// </summary>
    public async Task<LoginResult> RunAsync(
        Func<LoginWindowController, CancellationToken, Task<string?>> driveLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driveLogin);
        var anchor = _options.Anchor;
        if (anchor.IsDisposed) return LoginResult.Fail(LoginErrorCodes.Unavailable);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return LoginResult.Fail(LoginErrorCodes.Busy);

        var tcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Release the busy gate + complete EXACTLY ONCE, whoever finishes first: the UI
        // delegate, or the token if that delegate is never pumped (host teardown between the
        // post and the message loop) — otherwise a dropped post would wedge the gate at busy
        // for the whole session (every future login answers LOGIN_BUSY) and hang the caller
        // (the source's measured incident).
        void Finish(LoginResult result)
        {
            if (tcs.TrySetResult(result)) Interlocked.Exchange(ref _busy, 0);
        }

        using var registration = cancellationToken.Register(() => Finish(LoginResult.Fail(LoginErrorCodes.Cancelled)));
        try
        {
            anchor.BeginInvoke(new Action(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Finish(LoginResult.Fail(LoginErrorCodes.Cancelled));
                    return;
                }
                LoginResult result;
                try
                {
                    result = RunOnUi(driveLogin, cancellationToken);
                }
                catch
                {
                    // Details stay host-side; the wire learns only the code (the error contract).
                    result = LoginResult.Fail(LoginErrorCodes.Error);
                }
                Finish(result);
            }));
        }
        catch
        {
            Finish(LoginResult.Fail(LoginErrorCodes.Unavailable));
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the UI thread. Shows the window MODALLY (ShowDialog → its own nested message
    /// loop) and drives the login inside it: on <c>Shown</c> the WebView2 comes up, the driver
    /// runs over the controller, and when it returns the window closes (ending ShowDialog).
    /// </summary>
    private LoginResult RunOnUi(
        Func<LoginWindowController, CancellationToken, Task<string?>> driveLogin,
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
            form.Location = new Point(-32000, -32000);
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
                fallback.Tick += (_, _) => { fallback.Stop(); onLoading(false); };
                fallback.Start();
            }
        }

        var outcome = LoginResult.Fail(LoginErrorCodes.Cancelled);
        form.Shown += async (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested) { form.Close(); return; }
            LoginWindowController? controller = null;
            try
            {
                await SessionBrowser.InitializeAsync(web, new SessionBrowserOptions
                {
                    ProfileDirectory = _options.ProfileDirectory,
                    KeepAliveInBackground = !_options.RevealImmediately, // a hidden window must keep its JS running
                });

                controller = new LoginWindowController(form, web, _options.NavigationGuard, _options.OnLoading, foreground: true);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controller.WindowClosed);
                var blob = await driveLogin(controller, linked.Token);
                outcome = !string.IsNullOrEmpty(blob)
                    ? LoginResult.Ok(blob)
                    : LoginResult.Fail(LoginErrorCodes.Incomplete);
            }
            catch (OperationCanceledException)
            {
                outcome = LoginResult.Fail(LoginErrorCodes.Cancelled);
            }
            catch
            {
                outcome = LoginResult.Fail(LoginErrorCodes.Error);
            }
            finally
            {
                fallback?.Dispose();
                // Drop the splash unconditionally: a driver that threw before its own
                // SetLoading(false) (e.g. SessionBrowser init failed) would otherwise leave the
                // app's overlay up for the process lifetime — the measured incident the fallback
                // timer guards, which the timer's own disposal here would defeat.
                _options.OnLoading?.Invoke(false);
                controller?.Finish();               // allow the real close (a user close was held)
                if (!form.IsDisposed) form.Close(); // ends ShowDialog → RunOnUi returns outcome
            }
        };

        // A silent-refresh window (created off-screen) must be OWNERLESS: ShowDialog disables its
        // owner, so an owned invisible dialog would silently disable the app's main window for the
        // whole refresh. A visible login window owns the main window normally (modal z-order).
        var owner = _options.RevealImmediately ? (_options.Owner ?? _options.Anchor.FindForm()) : null;
        form.ShowDialog(owner is { Visible: true, IsDisposed: false } ? owner : null); // nested loop until the flow closes it
        return outcome;
    }

    /// <summary>
    /// Wipe a login's persistent profile so logout is a REAL logout — clearing only the stored
    /// session blob would still let the next login window silently auto-sign-in from the cached
    /// profile cookies (both siblings' measured lesson). Wipe the provider's whole tree (subs
    /// included) on a provider-level logout — a sub's cookies left behind auto-sign-in too.
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
