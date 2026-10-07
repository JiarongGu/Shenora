using Shenora.Chromium.Host;

namespace Shenora.Chromium;

/// <summary>The Chromium shell's splash, for code outside its component (<see cref="ChromiumHostOptions.Splash"/>).
/// Always registered; without a splash, or once it has lifted, it does nothing.</summary>
public sealed class ChromiumSplash
{
    internal ChromiumSplash() { }

    internal SplashSession? Session { get; set; }

    /// <summary>Lift the splash now, whatever it was waiting for.</summary>
    public void Close() => Session?.Close();
}
