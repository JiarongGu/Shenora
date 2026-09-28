namespace Shenora.Chromium.Host;

/// <summary>
/// Which page sent the request being dispatched. The dispatcher is shared by every page, so a module that acts on
/// "this page" (its drop zones) or "this page's window" (its commands) asks this rather than holding one: each page's
/// bridge enters it around its dispatch, and the context-preserving pipeline carries it through every await.
/// </summary>
internal static class ChromiumBrowserContext
{
    private static readonly AsyncLocal<ChromiumBrowser?> CurrentBrowser = new();

    /// <summary>The browser whose page sent this request; null outside a Chromium page's dispatch.</summary>
    public static ChromiumBrowser? Current => CurrentBrowser.Value;

    /// <summary>Mark what runs until disposal as <paramref name="browser"/>'s.</summary>
    public static IDisposable Enter(ChromiumBrowser browser)
    {
        var previous = CurrentBrowser.Value;
        CurrentBrowser.Value = browser;
        return new Scope(previous);
    }

    private sealed class Scope(ChromiumBrowser? previous) : IDisposable
    {
        public void Dispose() => CurrentBrowser.Value = previous;
    }
}
