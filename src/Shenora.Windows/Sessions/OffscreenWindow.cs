namespace Shenora.Windows;

/// <summary>
/// The off-screen host-window pattern: a WebView2 needs a REAL window handle and a desktop-sized
/// viewport (some sites gate on window size, and responsive layouts reflow to mobile in a narrow one),
/// but these sessions must never show or steal focus. Realized (<c>Show()</c> attaches it to the app's
/// message loop) but parked far off-screen at opacity 0.
/// </summary>
internal static class OffscreenWindow
{
    /// <summary>
    /// Where an off-screen session window is parked. ONE constant, and <see cref="IsParked"/> is the
    /// only way to ask whether a window is still there — a second site inferring it from its own
    /// threshold is how changing the park position silently breaks reveal detection.
    /// </summary>
    internal const int ParkedCoordinate = -32000;

    public static Form Create(string title, Size clientSize)
    {
        var host = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(ParkedCoordinate, ParkedCoordinate),
            Opacity = 0,
            ClientSize = clientSize,
        };
        host.Show(); // realizes the handle + attaches to the app's message loop (invisible)
        return host;
    }

    /// <summary>
    /// True when <paramref name="form"/> is still parked off-screen (i.e. NOT revealed). Derived from
    /// <see cref="ParkedCoordinate"/> with a generous margin, so the two cannot drift apart.</summary>
    internal static bool IsParked(Form form) => form.Location.X <= ParkedCoordinate / 2;
}
