using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// Which non-client button messages the hit-test keeps for the page's caption buttons and which it passes on. A form
/// stands in for CEF's window, and its own window procedure sees what the subclass passed on. It swallows those
/// messages itself: the system's caption-button loop would wait for a release that never comes.
/// </summary>
public class CaptionHitTestTests
{
    private const int WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_LBUTTONUP = 0x0202;
    private const int HTCLOSE = 20;

    // A framed window's own buttons answer HTCLOSE and the rest from its caption, above the client: here right above
    // the page's own close, so only the height tells them apart.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_framed_windows_own_caption_buttons_are_the_systems(bool pageButtons) => Sta.Run(() =>
    {
        using var form = new Recorder();
        var states = new List<CaptionButtonState>();
        var clicks = new List<CaptionButtonKind>();
        var buttons = new CaptionButtons(states.Add, clicks.Add);
        if (pageButtons) buttons.Set([new CaptionButtonRect(CaptionButtonKind.Close, 600, 0, 46, 32)]);
        using var hitTest = CaptionHitTest.Attach(form.Handle, buttons, frameless: false, null);
        Assert.NotNull(hitTest);
        var caption = OnScreen(form, 610, -10);

        Send(form, WM_NCMOUSEMOVE, HTCLOSE, caption);
        Send(form, WM_NCLBUTTONDOWN, HTCLOSE, caption);
        Send(form, WM_NCLBUTTONUP, HTCLOSE, caption);

        Assert.Equal([WM_NCMOUSEMOVE, WM_NCLBUTTONDOWN, WM_NCLBUTTONUP], form.Received);
        Assert.Empty(states);
        Assert.Empty(clicks);
        Assert.NotEqual(form.Handle, GetCapture());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_press_on_the_pages_button_is_the_pages(bool frameless) => Sta.Run(() =>
    {
        using var form = new Recorder();
        var states = new List<CaptionButtonState>();
        var clicks = new List<CaptionButtonKind>();
        var buttons = new CaptionButtons(states.Add, clicks.Add);
        buttons.Set([new CaptionButtonRect(CaptionButtonKind.Close, 600, 0, 46, 32)]);
        using var hitTest = CaptionHitTest.Attach(form.Handle, buttons, frameless, null);
        Assert.NotNull(hitTest);

        Send(form, WM_NCMOUSEMOVE, HTCLOSE, OnScreen(form, 610, 10));
        Assert.Equal(new CaptionButtonState(CaptionButtonKind.Close, null), states[^1]);
        Send(form, WM_NCLBUTTONDOWN, HTCLOSE, OnScreen(form, 610, 10));
        Assert.True(buttons.IsPressed);
        Send(form, WM_LBUTTONUP, 0, At(610, 10));   // through the capture: client coordinates

        Assert.Equal([WM_NCMOUSEMOVE], form.Received);   // the hover is the system's too, the press and release are not
        Assert.Equal([CaptionButtonKind.Close], clicks);
    });

    private static nint OnScreen(Form form, int x, int y)
    {
        var point = form.PointToScreen(new Point(x, y));
        return At(point.X, point.Y);
    }

    private static nint At(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static void Send(Form form, int msg, nint wParam, nint lParam) => SendMessage(form.Handle, (uint)msg, wParam, lParam);

    private sealed class Recorder : Form
    {
        public Recorder()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(100, 100, 800, 600);
        }

        public List<int> Received { get; } = [];

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is WM_NCMOUSEMOVE or WM_NCLBUTTONDOWN or WM_NCLBUTTONUP)
            {
                Received.Add(m.Msg);
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern nint GetCapture();
}
