namespace Shenora.Chromium.Host;

/// <summary>
/// Whether a crashed renderer is reloaded: the WebView2 shell's policy (<c>WebViewHostOptions</c>'s
/// <c>ReloadOnRenderProcessFailure</c>, <c>AutoReloadCooldown</c> and <c>MaxAutoReloads</c>), so a page recovers the
/// same way on either engine. At most <see cref="MaxReloads"/> reloads, never two within <see cref="Cooldown"/>,
/// and a successful load restores the budget. Rate-limiting alone is not a stopping condition: a page that
/// faults while loading would otherwise reload every cooldown for ever, burning a renderer each time.
/// </summary>
/// <param name="log">Where the reload and the give-up are said, once each.</param>
internal sealed class RendererRecovery(Action<string> log)
{
    public const int MaxReloads = 3;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private int _reloads;
    private DateTime _lastReload = DateTime.MinValue;

    /// <summary>The renderer terminated at <paramref name="now"/>: true when the page should be reloaded.</summary>
    public bool ShouldReload(DateTime now)
    {
        if (now - _lastReload <= Cooldown) return false;
        if (_reloads >= MaxReloads)
        {
            if (_reloads == MaxReloads)
            {
                _reloads++;   // past the cap: never said again
                log($"The renderer terminated {MaxReloads} times; no longer reloading. The page most likely crashes as it loads.");
            }
            return false;
        }
        _reloads++;
        _lastReload = now;
        log($"The renderer terminated; reloading ({_reloads}/{MaxReloads}).");
        return true;
    }

    /// <summary>The main frame finished loading successfully: the budget is whole again.</summary>
    public void LoadSucceeded() => _reloads = 0;
}
