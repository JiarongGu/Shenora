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
        var host = new QuietForm
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(ParkedCoordinate, ParkedCoordinate),
            Opacity = 0,
        };
        // Device-independent pixels, as ISessionHost documents and CEF takes them: ClientSize is physical, so at 150 %
        // the page saw a window two thirds the size asked for.
        host.ClientSize = host.LogicalToDeviceUnits(clientSize);
        host.Show(); // realizes the handle + attaches to the app's message loop (invisible)
        return host;
    }

    /// <summary>
    /// True when <paramref name="form"/> is still parked off-screen (i.e. NOT revealed). Derived from
    /// <see cref="ParkedCoordinate"/> with a generous margin, so the two cannot drift apart.</summary>
    internal static bool IsParked(Form form) => form.Location.X <= ParkedCoordinate / 2;
}

/// <summary>
/// A form that shows WITHOUT being activated. <c>Show()</c> activates a window, and a parked one at opacity 0 then held
/// the keyboard while the user typed into the app; <see cref="Form.Activate"/> still activates it once it is revealed.
/// </summary>
internal sealed class QuietForm : Form
{
    protected override bool ShowWithoutActivation => true;
}
