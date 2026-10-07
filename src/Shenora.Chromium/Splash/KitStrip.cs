namespace Shenora.Chromium.Host;

/// <summary>
/// When a frameless main window's kit strip begins and ends (<see cref="ChromiumSplashOptions.TitleBar"/>). It ends
/// once, at the first sign that the page has a title bar of its own — its caption buttons or its drag regions — or at
/// the lift, whichever comes first; a sign before it began means it never begins. Pure: the window applies what it
/// decides, on CEF's UI thread, which is the only thread that calls it.
/// </summary>
internal sealed class KitStrip(Action<KitStrip.Apply> apply)
{
    /// <summary>What the window does.</summary>
    internal enum Apply
    {
        /// <summary>Put the strip's caption buttons and drag region up.</summary>
        Begin,

        /// <summary>Take them down, leaving the page's own.</summary>
        End,
    }

    private bool _ended;

    /// <summary>The strip is up.</summary>
    public bool Active { get; private set; }

    public void Begin()
    {
        if (_ended || Active) return;
        Active = true;
        apply(Apply.Begin);
    }

    /// <summary>The page reported its caption buttons.</summary>
    public void PageCaptionButtons() => End();

    /// <summary>The page's title bar laid out its drag regions.</summary>
    public void PageDragRegions() => End();

    /// <summary>The splash lifted.</summary>
    public void SplashLifted() => End();

    private void End()
    {
        if (_ended) return;
        _ended = true;
        if (!Active) return;
        Active = false;
        apply(Apply.End);
    }
}
