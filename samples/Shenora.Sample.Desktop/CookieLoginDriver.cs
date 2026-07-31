using System.Text.Json;
using System.Text.RegularExpressions;

using Shenora.WebView2.Sessions;

namespace Shenora.Sample.Desktop;

/// <summary>Inputs for <see cref="CookieLoginDriver"/>.</summary>
public sealed class CookieLoginDriverOptions
{
    /// <summary>The provider's login page — navigated first, through the window's navigation guard.</summary>
    public required string LoginUrl { get; init; }

    /// <summary>
    /// The origin cookies are READ from — a SEPARATE knob from <see cref="LoginUrl"/> ON PURPOSE:
    /// session cookies often live on a PARENT domain (login at <c>account.example.com</c>, the
    /// session cookie on <c>.example.com</c>) that a read at the login host misses — the primary
    /// sibling's original capture bug, verified against the profile's cookie DB. Point this at
    /// the API origin the app will actually call with the captured session.
    /// </summary>
    public required string CookieReadUrl { get; init; }

    /// <summary>
    /// Regex patterns identifying the AUTH cookie(s), matched against the cookie NAME
    /// (case-insensitive, substring semantics — anchor with <c>^…$</c> for an exact name).
    /// Success = one of these is freshly SET after the flow starts (new, or value changed vs the
    /// pre-navigation snapshot) — merely EXISTING is not enough, because a stale auth cookie left
    /// in the persistent profile is exactly what produced the source's dead-session incident
    /// (captured "successfully", then every API call failed).
    /// </summary>
    public required IReadOnlyList<string> AuthCookiePatterns { get; init; }

    /// <summary>How often the cookie jar is polled while the login window is open.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// How long a hidden window (<see cref="InteractiveSessionOptions.RevealImmediately"/> = false)
    /// waits after navigation for the profile's auto-sign-in to set a fresh cookie before
    /// revealing itself — the silent-refresh grace: long enough for a redirect chain, short
    /// enough that a user who must interact isn't staring at nothing. Zero reveals right after
    /// navigation. Irrelevant when the window is already visible (Reveal is idempotent).
    /// </summary>
    public TimeSpan RevealDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True (default): the captured blob holds EVERY cookie visible at <see cref="CookieReadUrl"/>
    /// (APIs usually need the whole jar, not just the auth cookie). False: only the matching
    /// auth cookies.
    /// </summary>
    public bool CaptureAllCookies { get; init; } = true;
}

/// <summary>
/// A RECIPE, not a library feature. This used to ship inside <c>Shenora.WebView2.Sessions</c> as
/// <c>CookieLoginFlow</c>, and it should never have: <c>LoginUrl</c>, <c>CookieReadUrl</c>,
/// <c>AuthCookiePatterns</c>, <c>RevealDelay</c> and <c>CaptureAllCookies</c> are one product's
/// workflow, and the library rule is to ship the mechanism an app builds its product on and leave the
/// product with the app (D21). Two decisions talked each other into it — D21 blessed shipping "one
/// opt-in reference driver", and D22 then justified the scenario NAME on the grounds that D21 had
/// blessed shipping it — and neither ever applied the actual test: <i>would the other apps use this
/// API unchanged?</i> Only an app doing cookie logins would.
/// <para>
/// Nothing was lost by moving it. Every capability it uses is public kit seam —
/// <see cref="SessionController.GetCookiesAsync"/>, <see cref="SessionController.NavigateAsync"/>,
/// <c>Reveal</c>, <c>SetLoading</c>, <see cref="InteractiveSession.RunAsync"/> — so it is a plain
/// CONSUMER, which is exactly the proof D21 asks for: a consumer can build its own version on the
/// primitives without adopting ours. Copy this file into your app and edit it; it is yours.
/// </para>
/// <para>
/// It stays in the sample rather than in a doc so it keeps COMPILING against the seam, and because
/// <see cref="InteractiveSession"/>'s driver seam otherwise has no worked example anywhere.
/// </para>
///
/// Ported from the primary sibling's cookie login:
/// snapshot the jar, navigate to the login page, and poll until an auth cookie is FRESHLY set —
/// by the profile's silent auto-sign-in (no interaction, the window never reveals) or by the user
/// logging in. The blob is a JSON array of <see cref="SessionCookie"/> (camelCase; read it back
/// with <see cref="ReadBlob"/>).
///
/// <code>
/// var flow = new CookieLoginDriver(new CookieLoginDriverOptions { … });
/// var result = await loginWindow.RunAsync(flow.DriveAsync, ct);
/// </code>
/// </summary>
public sealed class CookieLoginDriver
{
    // The frozen wire serializer, not a private copy (P5.5 H4.5): IpcJson's own docs record that
    // the source app grew three private option sets that drifted apart. Same camelCase shape, and the
    // captured blob rides the IPC contract anyway.
    private static JsonSerializerOptions BlobJson => Shenora.Ipc.IpcJson.Options;

    private readonly CookieLoginDriverOptions _options;
    private readonly List<Regex> _patterns;

    public CookieLoginDriver(CookieLoginDriverOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.LoginUrl)) throw new ArgumentException("LoginUrl is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.CookieReadUrl)) throw new ArgumentException("CookieReadUrl is required.", nameof(options));
        if (options.AuthCookiePatterns is not { Count: > 0 }) throw new ArgumentException("At least one auth cookie pattern is required.", nameof(options));
        if (options.PollInterval <= TimeSpan.Zero) throw new ArgumentException("PollInterval must be positive.", nameof(options));
        // Compiled up-front so a bad pattern fails at construction, not mid-login; the match
        // timeout caps a pathological pattern (provider definitions are data).
        try
        {
            _patterns = [.. options.AuthCookiePatterns.Select(p =>
                new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))];
        }
        catch (ArgumentException ex) // RegexParseException stays inner — callers get ONE ctor contract
        {
            throw new ArgumentException("AuthCookiePatterns contains an invalid regex.", nameof(options), ex);
        }
    }

    /// <summary>Drive one login over the window's controller (pass this to <see cref="InteractiveSession.RunAsync"/>).</summary>
    public Task<string?> DriveAsync(SessionController controller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return DriveAsync(new Hooks
        {
            ReadCookies = controller.GetCookiesAsync,
            Navigate = controller.NavigateAsync,
            Reveal = controller.Reveal,
            SetLoading = controller.SetLoading,
        }, cancellationToken);
    }

    /// <summary>Deserialize a blob this flow captured.</summary>
    public static IReadOnlyList<SessionCookie> ReadBlob(string blob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        return JsonSerializer.Deserialize<List<SessionCookie>>(blob, BlobJson) ?? [];
    }

    /// <summary>The controller surface the flow actually uses — a seam so the poll/capture logic
    /// is testable without a live browser (the pool-seam precedent).</summary>
    /// <summary>See the summary above — the seam that makes the poll/capture logic testable.</summary>
    public sealed class Hooks
    {
        public required Func<string, CancellationToken, Task<IReadOnlyList<SessionCookie>>> ReadCookies { get; init; }
        public required Func<string, CancellationToken, Task> Navigate { get; init; }
        public required Action Reveal { get; init; }
        public required Action<bool> SetLoading { get; init; }
    }

    /// <summary>The poll/capture loop, over the seam rather than a live browser.</summary>
    public async Task<string?> DriveAsync(Hooks hooks, CancellationToken cancellationToken)
    {
        hooks.SetLoading(true);

        // The pre-navigation snapshot is the freshness baseline: only a cookie SET after this
        // counts as a login. It doubles as the sibling's probe-before-navigate — reading works
        // on the profile store before any page loads.
        var baseline = Snapshot(await hooks.ReadCookies(_options.CookieReadUrl, cancellationToken).ConfigureAwait(false));

        await hooks.Navigate(_options.LoginUrl, cancellationToken).ConfigureAwait(false);
        hooks.SetLoading(false);

        var navigated = DateTimeOffset.UtcNow;
        var revealed = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCapture(await hooks.ReadCookies(_options.CookieReadUrl, cancellationToken).ConfigureAwait(false),
                        baseline, out var blob))
                    return blob;
                if (!revealed && DateTimeOffset.UtcNow - navigated >= _options.RevealDelay)
                {
                    // The silent auto-sign-in didn't pan out within the grace — interaction is
                    // needed, bring the window on screen (idempotent; no-op when already shown).
                    revealed = true;
                    hooks.Reveal();
                }
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The user closed the window (which the controller HOLDS open for exactly this) or
            // the caller cancelled — ONE final read, so a login completed a beat before the
            // close still captures. The capture gate still applies: closing a signed-out window
            // must NOT produce an anonymous blob (the source's measured post-mortem).
            try
            {
                if (TryCapture(await hooks.ReadCookies(_options.CookieReadUrl, CancellationToken.None).ConfigureAwait(false),
                        baseline, out var blob))
                    return blob;
            }
            catch
            {
                // teardown — the cancellation below is the honest outcome
            }
            throw;
        }
    }

    /// <summary>Identity-keyed values of the whole jar (freshness is judged per cookie identity —
    /// same name on two domains is two cookies).</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<SessionCookie> cookies)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cookie in cookies) map[Key(cookie)] = cookie.Value;
        return map;
    }

    private static string Key(SessionCookie cookie) =>
        string.Join('\0', cookie.Domain, cookie.Path, cookie.Name); // '\0' join — a delimiter no cookie field contains (the EventBus key precedent)

    private bool TryCapture(IReadOnlyList<SessionCookie> cookies, Dictionary<string, string> baseline, out string? blob)
    {
        var fresh = cookies.Where(c => IsAuthCookie(c.Name)
            && (!baseline.TryGetValue(Key(c), out var previous) || previous != c.Value)).ToList();
        if (fresh.Count == 0)
        {
            blob = null;
            return false;
        }
        blob = JsonSerializer.Serialize(_options.CaptureAllCookies ? cookies : fresh, BlobJson);
        return true;
    }

    private bool IsAuthCookie(string name)
    {
        foreach (var pattern in _patterns)
        {
            try
            {
                if (pattern.IsMatch(name)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // pathological pattern × hostile name — treat as no match, keep polling
            }
        }
        return false;
    }
}
