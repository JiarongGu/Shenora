using System.Runtime.InteropServices;
using Shenora.Chromium;
using Shenora.Core.Ipc;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The window's system menu, as a page's caption opens it (<c>SHOW_SYSTEM_MENU</c>, a <c>ChromiumView</c>'s drag area)
/// and as the system opens it itself (Alt+Space, the taskbar). The menu is a modal loop that waits for a person, so the
/// system's <c>TrackPopupMenu</c> is replaced: the fake reads the items as the menu would open with them, and picks.
/// Whether the real menu opens is the WinForms probe's to measure.
/// </summary>
public class SystemMenuTests
{
    private const int SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060,
        SC_RESTORE = 0xF120;
    private const int WS_SYSMENU = 0x00080000, GWL_STYLE = -16, WM_INITMENUPOPUP = 0x0117;
    private const uint MF_BYCOMMAND = 0, MF_GRAYED = 1, MF_DISABLED = 2;

    /// <summary>Replaces the system's menu for one test, and records each time it would have opened.</summary>
    private sealed class FakeMenu : IDisposable
    {
        private readonly Func<nint, Point, nint, int> _real = FormCaption.TrackMenu;
        public readonly List<(Dictionary<int, bool> Items, Point At, nint Window)> Opened = [];

        public FakeMenu(int choose = 0) =>
            FormCaption.TrackMenu = (menu, at, window) => { Opened.Add((Items(menu), at, window)); return choose; };

        public void Dispose() => FormCaption.TrackMenu = _real;
    }

    [Fact]
    public void A_frameless_window_has_a_system_menu_for_Alt_Space_and_the_taskbar() => Sta.Run(() =>
    {
        using var form = Frameless();

        Assert.NotEqual(0, GetWindowLong(form.Handle, GWL_STYLE) & WS_SYSMENU);
        Assert.NotEqual(0, GetSystemMenu(form.Handle, false));
    });

    // A manual maximize looks Normal to the system, which would offer Maximize, Move and Size.
    [Fact]
    public void A_window_maximized_its_own_way_offers_restore_and_not_move_size_or_maximize() => Sta.Run(() =>
    {
        using var form = Frameless();
        form.ToggleMaximize();
        Assert.Equal(FormWindowState.Normal, form.WindowState);   // the system's view of it
        using var fake = new FakeMenu(choose: SC_RESTORE);

        Assert.True(FormCaption.ShowSystemMenu(form, new Point(40, 20)));

        var (items, at, window) = Assert.Single(fake.Opened);
        Assert.Equal(form.Handle, window);
        Assert.Equal(new Point(40, 20), at);
        Assert.True(items[SC_RESTORE]);
        Assert.False(items[SC_MOVE]);
        Assert.False(items[SC_SIZE]);
        Assert.False(items[SC_MAXIMIZE]);
        Assert.True(items[SC_MINIMIZE]);
        Assert.True(items[SC_CLOSE]);

        // The choice reaches the window after the menu has closed, and goes its own way.
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
        Application.DoEvents();
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
    });

    [Fact]
    public void A_restored_window_offers_move_size_minimize_and_maximize() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        _ = form.Handle;
        using var fake = new FakeMenu();

        Assert.True(FormCaption.ShowSystemMenu(form, Point.Empty));

        var items = Assert.Single(fake.Opened).Items;
        Assert.False(items[SC_RESTORE]);
        Assert.True(items[SC_MOVE]);
        Assert.True(items[SC_SIZE]);
        Assert.True(items[SC_MINIMIZE]);
        Assert.True(items[SC_MAXIMIZE]);
        Application.DoEvents();
        Assert.False(form.IsDisposed);   // nothing chosen, nothing done
    });

    // A form with no ControlBox has no system menu to show.
    [Fact]
    public void A_window_without_a_system_menu_shows_none() => Sta.Run(() =>
    {
        using var form = new Form { ControlBox = false, ShowInTaskbar = false };
        _ = form.Handle;
        using var fake = new FakeMenu();

        Assert.Equal(0, GetWindowLong(form.Handle, GWL_STYLE) & WS_SYSMENU);
        Assert.False(FormCaption.ShowSystemMenu(form, Point.Empty));
        Assert.Empty(fake.Opened);
    });

    // Alt+Space and the taskbar open the window's menu themselves, with the items the system set from its own view.
    [Fact]
    public void The_systems_own_menu_is_set_again_for_a_window_maximized_its_own_way() => Sta.Run(() =>
    {
        using var form = Frameless();
        form.ToggleMaximize();
        var menu = GetSystemMenu(form.Handle, false);
        void AsTheSystemSeesIt()
        {
            EnableMenuItem(menu, (uint)SC_RESTORE, MF_BYCOMMAND | MF_GRAYED);
            EnableMenuItem(menu, (uint)SC_MAXIMIZE, MF_BYCOMMAND);
            EnableMenuItem(menu, (uint)SC_MOVE, MF_BYCOMMAND);
        }

        AsTheSystemSeesIt();
        SendMessage(form.Handle, WM_INITMENUPOPUP, menu, 0);   // a popup that is not the system menu
        Assert.False(Items(menu)[SC_RESTORE]);

        SendMessage(form.Handle, WM_INITMENUPOPUP, menu, 1 << 16);   // the system menu
        var items = Items(menu);
        Assert.True(items[SC_RESTORE]);
        Assert.False(items[SC_MAXIMIZE]);
        Assert.False(items[SC_MOVE]);
    });

    // Queued, where the drag's handoff runs inline: the menu is a modal loop, and the route answers before it opens.
    [Fact]
    public void SHOW_SYSTEM_MENU_answers_first_and_opens_the_senders_window_menu() => Sta.Run(() =>
    {
        using var main = new Form { ShowInTaskbar = false };
        using var other = new Form { ShowInTaskbar = false };
        var page = new Control();
        other.Controls.Add(page);
        _ = main.Handle;
        _ = other.Handle;
        var module = new WindowCommandModule(new WindowCommandOptions { Window = main });
        using var fake = new FakeMenu();

        var response = module.HandleMessageAsync(Request()).GetAwaiter().GetResult();
        Assert.True(response.Success);
        Assert.Empty(fake.Opened);   // not inline
        Application.DoEvents();
        Assert.Equal(main.Handle, Assert.Single(fake.Opened).Window);

        using (PageSender.Enter(page)) Assert.True(module.HandleMessageAsync(Request()).GetAwaiter().GetResult().Success);
        Application.DoEvents();
        Assert.Equal(other.Handle, fake.Opened[^1].Window);

        // A page whose window has gone opens nothing.
        var gone = new Control();
        gone.Dispose();
        using (PageSender.Enter(gone)) Assert.True(module.HandleMessageAsync(Request()).GetAwaiter().GetResult().Success);
        Application.DoEvents();
        Assert.Equal(2, fake.Opened.Count);
    });

    [Fact]
    public void A_ChromiumView_drag_areas_right_click_opens_its_forms_menu_where_it_was_released() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        var view = new ChromiumView(new ChromiumEngine()) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        _ = form.Handle;
        _ = view.Handle;
        using var fake = new FakeMenu();

        view.BrowserOptions().DragAreaPressed!(new ChromiumDragAreaPress(ChromiumDragAreaAction.ShowSystemMenu, new Point(30, 12)));

        var (_, at, window) = Assert.Single(fake.Opened);
        Assert.Equal(form.Handle, window);
        Assert.Equal(new Point(30, 12), at);
    });

    private static OptimizedForm Frameless()
    {
        var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, 800, 600),
            ShowInTaskbar = false,
        };
        _ = form.Handle;
        return form;
    }

    private static IpcRequest Request() => IpcRequests.Create(WindowCommandModule.Module, WindowCommandModule.ShowSystemMenuType);

    private static Dictionary<int, bool> Items(nint menu)
    {
        var items = new Dictionary<int, bool>();
        foreach (var command in new[] { SC_RESTORE, SC_MOVE, SC_SIZE, SC_MINIMIZE, SC_MAXIMIZE, SC_CLOSE })
        {
            var state = GetMenuState(menu, (uint)command, MF_BYCOMMAND);
            Assert.True(state != uint.MaxValue, $"the system menu has no item 0x{command:X}");
            items[command] = (state & (MF_GRAYED | MF_DISABLED)) == 0;
        }
        return items;
    }

    [DllImport("user32")] private static extern nint GetSystemMenu(nint hwnd, bool revert);
    [DllImport("user32")] private static extern uint GetMenuState(nint menu, uint item, uint flags);
    [DllImport("user32")] private static extern int EnableMenuItem(nint menu, uint item, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32")] private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);
}
