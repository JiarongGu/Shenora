using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Menu/close-to-tray logic over a real (invisible) form on an STA thread (NotifyIcon is
/// shell-backed). The visible tray behavior is e2e/manual territory.
/// </summary>
public class TrayIconTests
{
    private static readonly TrayMenuColors DarkColors = new()
    {
        Surface = Color.FromArgb(26, 26, 26),
        Hover = Color.FromArgb(45, 45, 45),
        Border = Color.FromArgb(52, 52, 52),
        Accent = Color.FromArgb(0, 120, 212),
        Text = Color.FromArgb(236, 237, 242),
        DisabledText = Color.FromArgb(150, 151, 168),
    };

    [Fact]
    public void Menu_composes_open_app_items_and_exit_in_order() => Sta.Run(() =>
    {
        using var form = new Form();
        using var tray = new TrayIcon(new TrayIconOptions
        {
            Window = form,
            OpenMenuItemText = "Show it",
            ExitMenuItemText = "Quit",
            ConfigureMenu = menu => menu.Items.Add(new ToolStripMenuItem("App item")),
            MenuColors = DarkColors,
        });

        var labels = tray.Menu.Items.Cast<ToolStripItem>()
            .Select(i => i is ToolStripSeparator ? "---" : i.Text ?? string.Empty).ToArray();
        Assert.Equal(["Show it", "App item", "---", "Quit"], labels);
        // MenuColors set → the app's colours really reach the renderer. This used to assert the
        // internal type's NAME (`Contains("TrayMenuRenderer", …GetType().Name)`), which pinned a
        // private implementation detail and would have passed a renderer that ignored every colour
        // it was handed (P5.5 H7). The colour table is the observable contract.
        var renderer = Assert.IsAssignableFrom<ToolStripProfessionalRenderer>(tray.Menu.Renderer);
        Assert.Equal(DarkColors.Surface, renderer.ColorTable.ToolStripDropDownBackground);
        Assert.Equal(DarkColors.Surface, renderer.ColorTable.ImageMarginGradientBegin);
    });

    [Fact]
    public void Close_to_tray_hides_and_exit_really_closes() => Sta.Run(() =>
    {
        using var form = new Form();
        _ = form.Handle; // FormClosed needs a created handle (the WM_CLOSE path)
        form.Show();
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        using var tray = new TrayIcon(new TrayIconOptions { Window = form });

        form.Close(); // user-style close → canceled, hidden to the tray
        Assert.False(closed);
        Assert.False(form.Visible);

        tray.ShowWindow();
        Assert.True(form.Visible);

        tray.ExitApplication(); // the Exit item's path — bypasses close-to-tray
        Assert.True(closed);
    });

    [Fact]
    public void Close_to_tray_off_leaves_closing_alone() => Sta.Run(() =>
    {
        using var form = new Form();
        _ = form.Handle;
        form.Show();
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        using var tray = new TrayIcon(new TrayIconOptions { Window = form, CloseToTray = false });

        form.Close();

        Assert.True(closed);
    });

    [Fact]
    public void A_canceled_exit_rearms_close_to_tray() => Sta.Run(() =>
    {
        // Regression: _exiting stayed true after another FormClosing handler canceled the
        // close — the NEXT plain user close then exited instead of hiding to the tray.
        using var form = new Form();
        _ = form.Handle;
        form.Show();
        var vetoOnce = true;
        form.FormClosing += (_, e) =>
        {
            if (vetoOnce)
            {
                e.Cancel = true;
                vetoOnce = false;
            }
        };
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        using var tray = new TrayIcon(new TrayIconOptions { Window = form });

        tray.ExitApplication(); // vetoed by the app's unsaved-changes-style handler
        Assert.False(closed);

        form.Close(); // a plain user close afterwards must hide to the tray again
        Assert.False(closed);
        Assert.False(form.Visible);
    });

    [Fact]
    public void Dispose_detaches_the_closing_handler() => Sta.Run(() =>
    {
        using var form = new Form();
        _ = form.Handle;
        form.Show();
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        var tray = new TrayIcon(new TrayIconOptions { Window = form });

        tray.Dispose();
        form.Close(); // no tray anymore — closes normally

        Assert.True(closed);
    });
}
