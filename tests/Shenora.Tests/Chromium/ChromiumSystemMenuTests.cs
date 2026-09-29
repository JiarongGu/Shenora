using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// CEF creates a frameless window with no system menu (measured: no <c>WS_SYSMENU</c>), so a caption's right click,
/// Alt+Space and <c>SHOW_SYSTEM_MENU</c> had nothing to open. A form with no ControlBox stands in for it here; the real
/// menu, a modal loop, is the Chromium probe's to measure.
/// </summary>
public class ChromiumSystemMenuTests
{
    private const int GWL_STYLE = -16, WS_SYSMENU = 0x00080000;

    [Fact]
    public void A_window_without_a_system_menu_opens_none_and_is_given_one() => Sta.Run(() =>
    {
        using var form = new Form { ControlBox = false, ShowInTaskbar = false };
        var hwnd = form.Handle;
        Assert.Equal(0, GetWindowLong(hwnd, GWL_STYLE) & WS_SYSMENU);

        Assert.False(SystemMenu.ShowAtPointer(hwnd));   // nothing to show

        SystemMenu.Ensure(hwnd);
        Assert.NotEqual(0, GetWindowLong(hwnd, GWL_STYLE) & WS_SYSMENU);
        Assert.True(GetMenuItemCount(GetSystemMenu(hwnd, false)) > 0);
    });

    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32")] private static extern nint GetSystemMenu(nint hwnd, bool revert);
    [DllImport("user32")] private static extern int GetMenuItemCount(nint menu);
}
