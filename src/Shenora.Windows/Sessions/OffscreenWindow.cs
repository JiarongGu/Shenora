namespace Shenora.Windows;

/// <summary>
/// The family's off-screen host-window pattern: a WebView2 needs a REAL window handle and a
/// desktop-sized viewport (some sites gate on window size, and responsive layouts reflow to
/// mobile in a narrow one) — but these sessions must never show or steal focus. Realized
/// (<c>Show()</c> attaches it to the app's message loop) but parked far off-screen at opacity 0.
/// </summary>
internal static class OffscreenWindow
{
    /// <summary>
    /// Where an off-screen session window is parked. ONE constant (P5.5 H4.5): the coordinate was
    /// written literally in two places while a THIRD site inferred "is this window on screen?" from a
    /// different threshold (<c>&gt; -30000</c>), so changing the park position would have silently
    /// broken the reveal detection. <see cref="IsParked"/> is now the only way to ask.
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
    /// <see cref="ParkedCoordinate"/> with a generous margin, so the two can never drift apart.
    /// </summary>
    internal static bool IsParked(Form form) => form.Location.X <= ParkedCoordinate / 2;
}
