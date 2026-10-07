using System.Drawing;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's splash (<see cref="ChromiumHostOptions.Splash"/>): drawn by the operating system rather than
/// Chromium over the main window's render area, so the window's own frame stays live while Chromium loads the page, and
/// optionally as a <see cref="Card"/> before the window exists. It lifts once every <see cref="SplashContext.OnShown"/>
/// has finished and the page is ready.
/// <para>On Linux it needs an X display (Xwayland included, as CEF does); without one there is no splash, and the
/// component's setup and its boot work run all the same.</para>
/// </summary>
public sealed class ChromiumSplashOptions
{
    /// <summary>What it shows: a setup that runs once and returns the render function (<see cref="Splash.Of{T}"/> for a
    /// class). Null is <see cref="Splash.Preset"/> with the main window's title.</summary>
    public SplashComponent? Component { get; init; }

    /// <summary>The fill behind the content. Null is <see cref="ChromiumWindowOptions.BackgroundColor"/>, then a neutral
    /// light or dark by <see cref="SplashContext.SystemDark"/> (dark when it is unknown).</summary>
    public Color? Background { get; init; }

    /// <summary>
    /// The page's ready handshake no longer lifts it: the page does, with <c>closeSplash()</c>, once its own state is
    /// ready. <see cref="Timeout"/> still bounds the wait.
    /// </summary>
    public bool HoldUntilClosed { get; init; }

    /// <summary>The longest it waits for the page, counted from the main window opening. It never cuts
    /// <see cref="SplashContext.OnShown"/> work short.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long it fades out as it lifts; zero lifts it at once. On Linux it fades only under a compositing
    /// window manager, and lifts at once without one.</summary>
    public TimeSpan FadeOut { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>A card shown from the app's first moments until the main window exists. Null (the default): none, and
    /// the splash appears with the window.</summary>
    public SplashCardOptions? Card { get; init; }

    /// <summary>The title strip a frameless main window has while the splash is up. A framed window's title bar is the
    /// operating system's, and ignores it.</summary>
    public SplashTitleBarOptions TitleBar { get; init; } = new();

    /// <summary>Refuse what cannot work, where the caller can see why.</summary>
    internal void Validate(string parameter)
    {
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentException($"The splash's {nameof(Timeout)} must be positive.", parameter);
        if (FadeOut < TimeSpan.Zero)
            throw new ArgumentException($"The splash's {nameof(FadeOut)} cannot be negative.", parameter);
        if (Card is { } card && !(double.IsFinite(card.Width) && double.IsFinite(card.Height) && card.Width > 0 && card.Height > 0))
            throw new ArgumentException(
                $"The splash card's {nameof(SplashCardOptions.Width)} and {nameof(SplashCardOptions.Height)} must be positive.", parameter);
        if (TitleBar is null)
            throw new ArgumentException($"The splash's {nameof(TitleBar)} cannot be null.", parameter);
        if (!(double.IsFinite(TitleBar.Height) && TitleBar.Height > 0 && TitleBar.Height <= 200))
            throw new ArgumentException($"The splash title bar's {nameof(SplashTitleBarOptions.Height)} must be more than 0 and at most 200.", parameter);
    }
}
