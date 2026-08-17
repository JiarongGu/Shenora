using System.Text.Json;
using System.Text.RegularExpressions;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

using Shenora.Windows;

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

    /// <summary>
    /// How often the cookie jar is polled while the login window is open.
    /// <para>
    /// ⚠ The poll is the CORRECTNESS mechanism and stays even with <see cref="Events"/> set — a cookie
    /// written by JS (<c>document.cookie</c>) appears in no response header, so nothing would report it.
    /// The event only shortens the wait.
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Optional: the bus the session publishes on, which turns the poll from the only mechanism into a
    /// backstop — a <c>Set-Cookie</c> on an observed response wakes the loop immediately instead of it
    /// waiting out <see cref="PollInterval"/>.
    /// <para>
    /// ⚠ <b>Needs BOTH halves configured on the session</b>: <c>InteractiveSessionOptions.Events</c> (the
    /// same bus) and <c>ObserveResponse</c> (which responses are worth reporting — it is off by default,
    /// being the one per-subresource event). With only one of them the driver still works, just at poll
    /// speed.
    /// </para>
    /// </summary>
    public IEventBus? Events { get; init; }

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
/// A RECIPE, not a library feature. This used to ship inside <c>Shenora.Windows</c> as
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
    private static JsonSerializerOptions BlobJson => Shenora.Core.Ipc.IpcJson.Options;

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
    public async Task<string?> DriveAsync(SessionController controller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);

        // The composition the kit's event catalogue is FOR, and the whole point of this file: a login
        // flow built from generic parts, with the library never learning the word "login". The hint is
        // `Set-Cookie` on a response — a mechanism — and the driver decides that means "check the jar".
        using var hint = _options.Events is { } bus ? new CookieHint(bus, controller.Id) : null;

        return await DriveAsync(new Hooks
        {
            ReadCookies = controller.GetCookiesAsync,
            Navigate = controller.NavigateAsync,
            Reveal = controller.Reveal,
            SetLoading = controller.SetLoading,
            WaitForHint = hint is null ? null : hint.WaitAsync,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bridges <see cref="SessionEvents.ResponseReceived"/> to something the poll loop can wait on:
    /// signalled whenever an observed response carries a <c>Set-Cookie</c>.
    /// <para>
    /// ⚠ <b>A one-permit semaphore, deliberately.</b> An unbounded <c>Release()</c> accumulates permits,
    /// and the very next thing the loop does with them is NOT wait — so a page setting five cookies
    /// would turn the next five polls into a spin. Capped at one, a burst costs at most one extra
    /// immediate re-read, which is exactly the behaviour wanted.
    /// </para>
    /// </summary>
    public sealed class CookieHint : IDisposable
    {
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly IDisposable _subscription;

        public CookieHint(IEventBus bus, string scope)
        {
            ArgumentNullException.ThrowIfNull(bus);
            _subscription = bus.Subscribe(SessionEvents.Module, SessionEvents.ResponseReceived, scope, message =>
            {
                if (CarriesACookie(message.Payload)) Signal();
                return Task.CompletedTask;
            });
        }

        /// <summary>True when this event's payload is a response that sets at least one cookie.</summary>
        public static bool CarriesACookie(object? payload) =>
            payload is SessionResponse response
            && response.Headers.Any(h => string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));

        /// <summary>Completes on the next signal (or immediately if one is already pending).</summary>
        public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

        /// <summary>
        /// Is a signal pending right now? ⚠ Peeking with <see cref="WaitAsync"/> instead does not work
        /// and does not look broken: on an empty semaphore it leaves a QUEUED WAITER behind, so the next
        /// signal is handed to that abandoned task and the real waiter never wakes.
        /// </summary>
        public bool IsSignalled => _signal.CurrentCount > 0;

        public void Dispose()
        {
            _subscription.Dispose();
            _signal.Dispose();
        }

        private void Signal()
        {
            // Racing another signal is normal — two responses can land together. Losing that race means
            // a permit is already waiting, which is the state we wanted anyway.
            try { if (_signal.CurrentCount == 0) _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { /* the flow finished while a response was in flight */ }
        }
    }

    /// <summary>Deserialize a blob this flow captured.</summary>
    public static IReadOnlyList<SessionCookie> ReadBlob(string blob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        return JsonSerializer.Deserialize<List<SessionCookie>>(blob, BlobJson) ?? [];
    }

    /// <summary>The controller surface the flow actually uses — a seam so the poll/capture logic
    /// is testable without a live browser (the pool-seam precedent).</summary>
    public sealed class Hooks
    {
        public required Func<string, CancellationToken, Task<IReadOnlyList<SessionCookie>>> ReadCookies { get; init; }
        public required Func<string, CancellationToken, Task> Navigate { get; init; }
        public required Action Reveal { get; init; }
        public required Action<bool> SetLoading { get; init; }

        /// <summary>
        /// Optional: completes when something suggests the jar is worth re-reading NOW, racing the poll
        /// interval. Null = poll only, which is the behaviour with no bus configured.
        /// </summary>
        public Func<CancellationToken, Task>? WaitForHint { get; init; }
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
                await WaitForNextReadAsync(hooks, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Wait before the next jar read: the poll interval, or whichever of it and a hint arrives first.
    /// </summary>
    private async Task WaitForNextReadAsync(Hooks hooks, CancellationToken cancellationToken)
    {
        if (hooks.WaitForHint is not { } hint)
        {
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Its own CTS so the LOSING wait is cancelled rather than abandoned. Not merely tidiness: an
        // abandoned `WaitAsync` stays QUEUED on the hint's semaphore, and the next signal is handed to
        // that dead waiter instead of the live one — the hint would then fire once and never again.
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(_options.PollInterval, race.Token);
        var hinted = hint(race.Token);
        await Task.WhenAny(timer, hinted).ConfigureAwait(false);
        race.Cancel();

        // ⚠ AWAIT THE LOSER before `using` disposes the source. Disposing a linked CTS while a waiter is
        // still unwinding its just-fired cancellation is the shape `webview2-hosting.md` records from the
        // pool's semaphore teardown, where it left a task PERMANENTLY INCOMPLETE. Both tasks are expected
        // to end cancelled, which is not a failure here.
        try { await Task.WhenAll(timer, hinted).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        // WhenAny never throws, so the caller's cancellation has to be re-observed by hand — without
        // this a cancelled flow would spin the loop instead of unwinding to the final-read handler.
        cancellationToken.ThrowIfCancellationRequested();
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
