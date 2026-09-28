namespace Shenora.Chromium.Host;

/// <summary>
/// Which window's page sent the request being dispatched. The dispatcher is shared by every window, so a module
/// that acts on "the window" (its commands, its drop zones) asks this rather than holding one: each window's bridge
/// enters it around its dispatch, and the context-preserving pipeline carries it through every await.
/// </summary>
internal static class ChromiumWindowContext
{
    private static readonly AsyncLocal<ChromiumWindow?> CurrentWindow = new();

    /// <summary>The window whose page sent this request; null outside a Chromium window's dispatch.</summary>
    public static ChromiumWindow? Current => CurrentWindow.Value;

    /// <summary>Mark what runs until disposal as <paramref name="window"/>'s.</summary>
    public static IDisposable Enter(ChromiumWindow window)
    {
        var previous = CurrentWindow.Value;
        CurrentWindow.Value = window;
        return new Scope(previous);
    }

    private sealed class Scope(ChromiumWindow? previous) : IDisposable
    {
        public void Dispose() => CurrentWindow.Value = previous;
    }
}
