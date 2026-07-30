namespace Shenora.WebView2.Sessions;

/// <summary>
/// The family's off-screen host-window pattern: a WebView2 needs a REAL window handle and a
/// desktop-sized viewport (some sites gate on window size, and responsive layouts reflow to
/// mobile in a narrow one) — but these sessions must never show or steal focus. Realized
/// (<c>Show()</c> attaches it to the app's message loop) but parked far off-screen at opacity 0.
/// </summary>
internal static class OffscreenWindow
{
    public static Form Create(string title, Size clientSize)
    {
        var host = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Opacity = 0,
            ClientSize = clientSize,
        };
        host.Show(); // realizes the handle + attaches to the app's message loop (invisible)
        return host;
    }
}
