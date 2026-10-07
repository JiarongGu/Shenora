namespace Shenora.Chromium;

/// <summary>The card a splash shows before the main window exists (<see cref="ChromiumSplashOptions.Card"/>): a small
/// borderless window centred where the main window will open, drawn by the operating system from the app's first
/// moments, while Chromium is still starting.</summary>
public sealed class SplashCardOptions
{
    /// <summary>Its width in device-independent pixels.</summary>
    public double Width { get; init; } = 480;

    /// <summary>Its height in device-independent pixels.</summary>
    public double Height { get; init; } = 300;
}
