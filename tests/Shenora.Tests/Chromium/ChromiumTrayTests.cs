using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's tray without the platform's: its menu, what a choice does, and close-to-tray. The Windows half
/// is also checked without showing anything: an icon event's meaning, and the native menu built from the model.
/// </summary>
public class ChromiumTrayTests
{
    private static (ChromiumTray Tray, ChromiumWindows Windows, List<Action> Posted) Tray(ChromiumTrayOptions options)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Tray test" });
        var host = new ChromiumHostOptions { Tray = options };
        builder.UseChromium(host);
        var app = builder.Build();
        var posted = new List<Action>();
        var ui = new CefUiDispatcher(work => { posted.Add(work); return true; }, () => false);
        ui.MarkReady();
        var windows = new ChromiumWindows(host, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null, new ChromiumUrlLauncher());
        return (new ChromiumTray(options, windows, "Tray test", null), windows, posted);
    }

    [Fact]
    public void The_menu_is_Open_the_apps_items_a_separator_and_Exit()
    {
        var (tray, _, _) = Tray(new ChromiumTrayOptions
        {
            OpenMenuItemText = "Show",
            ExitMenuItemText = "Quit",
            MenuItems = () => [new ChromiumTrayMenuItem("Sync", () => { }) { Checked = true }, ChromiumTrayMenuItem.Separator,
                new ChromiumTrayMenuItem("Pause", () => { }) { Enabled = false }],
        });

        var menu = tray.Menu();

        Assert.Equal(["Show", "Sync", "", "Pause", "", "Quit"], menu.Select(e => e.Text));
        Assert.True(menu[0].IsDefault);
        Assert.True(menu[1].Checked);
        Assert.True(menu[2].IsSeparator);
        Assert.False(menu[3].Enabled);
        Assert.True(menu[4].IsSeparator);
    }

    [Fact]
    public void Items_that_throw_are_left_out_and_the_menu_still_opens()
    {
        var (tray, _, _) = Tray(new ChromiumTrayOptions { MenuItems = () => throw new InvalidOperationException("the app's bug") });

        Assert.Equal(["Open", "", "Exit"], tray.Menu().Select(e => e.Text));
    }

    [Fact]
    public void A_choice_runs_its_item_guarded_and_a_disabled_one_runs_nothing()
    {
        var ran = new List<string>();
        var (tray, _, _) = Tray(new ChromiumTrayOptions
        {
            MenuItems = () => [
                new ChromiumTrayMenuItem("Throws", () => { ran.Add("throws"); throw new InvalidOperationException(); }),
                new ChromiumTrayMenuItem("Off", () => ran.Add("off")) { Enabled = false },
            ],
        });
        var menu = tray.Menu();

        tray.Choose(menu[1]);   // no exception escapes into the platform's menu loop
        tray.Choose(menu[2]);

        Assert.Equal(["throws"], ran);
    }

    // An icon of the test's own, so no platform tray is created.
    private sealed class FakeIcon(bool shown) : NativeTray
    {
        public bool Disposed { get; private set; }
        public override bool Shown => shown;
        public override void Dispose() => Disposed = true;
    }

    [Fact]
    public void Close_to_tray_keeps_only_the_main_window_and_only_until_Exit()
    {
        var (tray, windows, posted) = Tray(new ChromiumTrayOptions());
        tray.Start((_, _) => new FakeIcon(shown: true));

        Assert.False(windows.CloseGuard!(ChromiumWindows.MainWindowName));
        Assert.True(windows.CloseGuard!("second"));

        tray.ExitApplication();

        Assert.True(windows.CloseGuard!(ChromiumWindows.MainWindowName));
        Assert.Single(posted);   // the close of every window, on the UI thread
    }

    [Fact]
    public void Without_close_to_tray_the_main_window_closes()
    {
        var (tray, windows, _) = Tray(new ChromiumTrayOptions { CloseToTray = false });
        tray.Start((_, _) => new FakeIcon(shown: true));

        Assert.True(windows.CloseGuard!(ChromiumWindows.MainWindowName));
    }

    // With nowhere to show the icon, hiding the window would leave the app running with no way back to it: the Linux
    // shell had no tray at all and hid the window anyway.
    [Theory]
    [InlineData(false)]   // no platform tray (Linux, before its own)
    [InlineData(true)]    // a tray with no host to show it (a desktop with no tray)
    public void With_no_icon_shown_the_main_window_closes(bool hasTray)
    {
        var (tray, windows, _) = Tray(new ChromiumTrayOptions());
        var icon = new FakeIcon(shown: false);
        tray.Start((_, _) => hasTray ? icon : null);

        Assert.True(windows.CloseGuard!(ChromiumWindows.MainWindowName));
        tray.Stop();
        Assert.Equal(hasTray, icon.Disposed);
    }

    [Fact]
    public void The_shell_registers_the_tray_only_when_asked_for_one()
    {
        var without = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "No tray" });
        without.UseChromium(new ChromiumHostOptions());
        using var plain = without.Build();
        Assert.Null(plain.Services.GetService<ChromiumTray>());

        var with = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Tray" });
        with.UseChromium(new ChromiumHostOptions { Tray = new ChromiumTrayOptions() });
        using var app = with.Build();
        var tray = app.Services.GetRequiredService<ChromiumTray>();
        Assert.Equal("Tray", tray.Text);   // no window title: the app's name
        Assert.NotNull(app.Services.GetRequiredService<ChromiumWindows>().CloseGuard);
    }

    [Theory]
    [InlineData(0x0203, "ShowWindow")]   // WM_LBUTTONDBLCLK
    [InlineData(0x0401, "ShowWindow")]   // NIN_KEYSELECT
    [InlineData(0x007B, "Menu")]         // WM_CONTEXTMENU
    [InlineData(0x0400, "None")]         // NIN_SELECT: a single click does nothing, as TrayIcon's
    [InlineData(0x0200, "None")]         // WM_MOUSEMOVE
    public void An_icon_event_is_read_from_the_low_word(int message, string expected) =>
        Assert.Equal(expected, WindowsTray.Decode(message | (1 << 16)).ToString());   // the icon's id rides in the high word

    [Fact]
    public void The_native_menu_carries_the_model()
    {
        IReadOnlyList<TrayMenuEntry> entries =
        [
            new("Open", () => { }, IsDefault: true), new("Sync", () => { }, Checked: true), TrayMenuEntry.Separator,
            new("Pause", () => { }, Enabled: false), TrayMenuEntry.Separator, new("Exit", () => { }),
        ];
        var menu = WindowsTray.BuildMenu(entries);
        try
        {
            Assert.Equal(6, GetMenuItemCount(menu));
            Assert.Equal("Open", Text(menu, 1));
            Assert.Equal("Exit", Text(menu, 6));
            Assert.Equal(1u, GetMenuDefaultItem(menu, 0, 0));
            Assert.NotEqual(0u, GetMenuState(menu, 2, 0) & 0x8);    // MF_CHECKED
            Assert.NotEqual(0u, GetMenuState(menu, 4, 0) & 0x1);    // MF_GRAYED
            Assert.NotEqual(0u, GetMenuState(menu, 2, 0x400) & 0x800);   // the third item, by position, is a separator
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string Text(nint menu, uint id)
    {
        var buffer = new char[64];
        var length = GetMenuStringW(menu, id, buffer, buffer.Length, 0);
        return new string(buffer, 0, length);
    }

    [DllImport("user32")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32")] private static extern uint GetMenuDefaultItem(nint menu, uint byPosition, uint flags);
    [DllImport("user32")] private static extern uint GetMenuState(nint menu, uint item, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetMenuStringW(nint menu, uint item, char[] text, int max, uint flags);
    [DllImport("user32")] private static extern int DestroyMenu(nint menu);
}
