using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Modules.Platform;

/// <summary>How long the shell waits for the page to answer a back press, and what it does if it does not.</summary>
public sealed class BackNavigationOptions
{
    /// <summary>
    /// The longest a press is held while the page decides. On expiry the press falls through to the
    /// platform, which on Android means the activity finishes.
    /// <para>
    /// 🔴 <b>Falling through is the SAFE direction and the reason there is a timeout at all.</b> Held
    /// forever, a page that stopped answering — a crashed bundle, a listener that threw during
    /// registration — makes the back button do nothing, and a back button that does nothing is a broken
    /// app in a way an adopter cannot debug from the outside. Two seconds is long enough for a page
    /// walking its own history and short enough that a user presses again rather than files a bug.
    /// </para>
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The system back gesture, offered to the page before the platform acts on it — Android's hardware or
/// gesture back, which unhandled finishes the activity from whatever screen the user is on.
/// <para>
/// 🔴 <b>The shell cannot decide what back MEANS, so it does not try.</b> A page two levels deep wants to
/// close its expanded player, then walk its own history, and only exit at the root; none of that is
/// visible from C#. This type owns the part that IS the shell's — correlating one press with one answer,
/// bounding the wait, and falling through when nobody answers — and leaves the decision to the page.
/// </para>
/// <para>
/// ⚠ <b>Interception is OPT-IN, and that is load-bearing rather than tidy.</b> Until a page asks for it
/// (<see cref="InterceptType"/>) <see cref="Intercepting"/> stays false, and a shell that watches
/// <see cref="InterceptingChanged"/> keeps its platform hook switched OFF — so the press never enters
/// managed code at all. That is what makes "an app that never asked pays nothing" true rather than
/// nearly true, and on Android it also leaves the platform's predictive-back gesture alone.
/// </para>
/// </summary>
/// <remarks>
/// Portable on purpose: nothing here touches a platform, so the ordering — which is the part that breaks
/// — is testable with no device and no webview. The platform half is <c>MobileBackNavigation</c>, which
/// is thin because everything hard lives here.
/// </remarks>
public sealed class BackNavigation : IDisposable
{
    /// <summary>The module a back press is published under, and the one the page answers on.</summary>
    public const string Module = "SHENORA.BACK";

    /// <summary>
    /// Event: the user pressed back and the page is being asked. Payload <c>{ token }</c>, which must
    /// come back verbatim on <see cref="ResolveType"/> — an answer to a press that has already timed out
    /// must not be mistaken for an answer to the one after it.
    /// </summary>
    public const string PressedType = "PRESSED";

    /// <summary>Route: the page takes or releases responsibility for back. Payload <c>{ enabled }</c>.</summary>
    public const string InterceptType = "INTERCEPT";

    /// <summary>Route: the page answers one press. Payload <c>{ token, handled }</c>.</summary>
    public const string ResolveType = "RESOLVE";

    private readonly BackNavigationOptions _options;
    private readonly IEventBus _events;
    private readonly ILogger? _log;

    // Keyed by token. A press is removed by whichever of the answer and the timeout gets there first, so
    // a late answer finds nothing and says so rather than completing a task twice.
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _next;
    private bool _disposed;

    /// <param name="events">Where <see cref="PressedType"/> is published. The pump forwards it to the page.</param>
    /// <param name="options">Null takes the defaults.</param>
    /// <param name="log">Optional diagnostics. A timeout is reported here and nowhere else.</param>
    public BackNavigation(IEventBus events, BackNavigationOptions? options = null, ILogger? log = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _options = options ?? new BackNavigationOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Timeout, TimeSpan.Zero);
        _log = log;
    }

    /// <summary>
    /// True once a page has asked to handle back, false again when it releases it or the page goes away.
    /// <para>
    /// 🔴 <b>A NEW DOCUMENT DOES NOT RESET THIS, and that is a real hazard rather than a note.</b> The
    /// shell gets no document-lifecycle signal it could reset on, so after a navigation — or a reload
    /// that lands on the platform's error page — this stays true while nothing is subscribed. Every press
    /// then waits the full timeout before reaching the platform, so back becomes "nothing happens, then
    /// the app quits". A page that intercepts must ask again on load; asking twice is harmless.
    /// </para>
    /// </summary>
    public bool Intercepting { get; private set; }

    /// <summary>
    /// Raised when <see cref="Intercepting"/> changes, so a shell can switch its platform hook off while
    /// nobody is listening.
    /// <para>
    /// ⚠ Raised on whichever thread called <see cref="SetIntercepting"/> — the IPC dispatch thread, not
    /// the UI thread. A handler touching platform state must marshal.
    /// </para>
    /// </summary>
    public event EventHandler? InterceptingChanged;

    /// <summary>Take or release responsibility for back, from the page's <see cref="InterceptType"/> route.</summary>
    /// <param name="enabled">True to intercept.</param>
    public void SetIntercepting(bool enabled)
    {
        if (Intercepting == enabled) return;
        Intercepting = enabled;
        Log(enabled
            ? "back: the page is now handling the back gesture"
            : "back: the page released the back gesture — presses go to the platform");

        // Releasing while a press is in flight must not strand it: the presses already asked are answered
        // NOT HANDLED, which is the same thing the page would now say.
        if (!enabled) CompleteAll(handled: false);

        // ⚠ Guarded: this reaches a SHELL, and a throwing subscriber must not fault the page's route.
        try { InterceptingChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log($"back: an InterceptingChanged handler threw ({ex.GetType().Name})"); }
    }

    /// <summary>
    /// Offer one press to the page and wait for its answer. True means the page handled it and the
    /// platform must NOT act; false means fall through to the platform's own behaviour.
    /// <para>
    /// 🔴 <b>False is returned for every way this can fail</b> — nobody intercepting, nobody answering,
    /// the page answering "not mine", a disposed coordinator. The caller therefore has one branch, and
    /// the platform default is what happens whenever the kit is unsure. The alternative — swallowing a
    /// press the page never claimed — is the failure that cannot be recovered from the outside.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait; the press falls through.</param>
    public async Task<bool> PressAsync(CancellationToken cancellationToken = default)
    {
        // The fast path, and the reason interception is opt-in: an app that never asked pays nothing and
        // its back button keeps the platform's own latency.
        if (_disposed || !Intercepting) return false;

        var token = "b" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed) return false;
            _pending[token] = completion;
        }

        try
        {
            // ⚠ Emit, not EmitAsync: the caller is a platform back handler on the UI thread, and awaiting
            // subscribers there would hold the gesture for as long as the slowest of them.
            _events.Emit(Module, PressedType, new BackNavigationEvent(token));

            using var timeout = new CancellationTokenSource(_options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            using (linked.Token.Register(() => completion.TrySetResult(false)))
            {
                var handled = await completion.Task.ConfigureAwait(false);
                if (!handled && timeout.IsCancellationRequested)
                    Log($"back: the page did not answer within {_options.Timeout.TotalMilliseconds:0}ms — "
                      + "the press went to the platform. A page that intercepts must answer every press.");
                return handled;
            }
        }
        finally
        {
            lock (_gate) _pending.Remove(token);
        }
    }

    /// <summary>
    /// The page's answer to one press, from its <see cref="ResolveType"/> route.
    /// </summary>
    /// <param name="token">The token that press was published with.</param>
    /// <param name="handled">True if the page consumed it.</param>
    /// <returns>
    /// False when the token names no press still waiting — it timed out, it was already answered, or it
    /// never existed. ⚠ The caller should REPORT that rather than drop it: a page whose answers always
    /// arrive too late is a page whose back button is silently taking the platform default every time,
    /// and this is the only place that is visible.
    /// </returns>
    public bool Resolve(string token, bool handled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            if (!_pending.TryGetValue(token, out completion)) return false;
        }
        return completion.TrySetResult(handled);
    }

    private void CompleteAll(bool handled)
    {
        TaskCompletionSource<bool>[] waiting;
        lock (_gate) waiting = [.. _pending.Values];
        foreach (var completion in waiting) completion.TrySetResult(handled);
    }

    private void Log(string message) => AppCallback.Log(_log, () => $"[Shenora] {message}");

    /// <summary>Releases every waiting press to the platform. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Never leave a press awaiting a page that is gone — the platform default is the right answer now.
        CompleteAll(handled: false);
    }
}

/// <summary>
/// The payload of a <see cref="BackNavigation.PressedType"/> event: which press the page is answering.
/// </summary>
/// <param name="Token">Return it verbatim on <see cref="BackNavigation.ResolveType"/>.</param>
public sealed record BackNavigationEvent(string Token);
