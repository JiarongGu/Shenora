using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Linux tray's menu as dbusmenu carries it, decided without a bus. The bus half was driven on X11 by a scripted
/// watcher and panel: the layout, clicks, the disabled item, and the window hidden and brought back.
/// </summary>
public class DbusMenuTests
{
    private static Dictionary<string, object> Of(TrayMenuEntry entry) => DbusMenu.Properties(entry).ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void An_item_is_its_label_and_whether_it_is_enabled()
    {
        Assert.Equal(new Dictionary<string, object> { ["label"] = "Open", ["enabled"] = true }, Of(new TrayMenuEntry("Open", () => { })));
        Assert.Equal(false, Of(new TrayMenuEntry("Pause", () => { }, Enabled: false))["enabled"]);
    }

    [Fact]
    public void An_underscore_is_doubled_because_one_marks_the_access_key() =>
        Assert.Equal("Save__as__it__is", Of(new TrayMenuEntry("Save_as_it_is", () => { }))["label"]);

    [Fact]
    public void A_checked_item_carries_a_checkmark_and_an_unchecked_one_none()
    {
        var on = Of(new TrayMenuEntry("Sync", () => { }, Checked: true));
        Assert.Equal("checkmark", on["toggle-type"]);
        Assert.Equal(1, on["toggle-state"]);
        Assert.DoesNotContain("toggle-type", Of(new TrayMenuEntry("Sync", () => { })).Keys);
    }

    [Fact]
    public void A_separator_is_only_its_type() =>
        Assert.Equal(new Dictionary<string, object> { ["type"] = "separator" }, Of(TrayMenuEntry.Separator));

    [Fact]
    public void An_id_is_a_position_plus_one_and_the_menu_itself_is_no_entry()
    {
        IReadOnlyList<TrayMenuEntry> entries = [new("Open", () => { }), TrayMenuEntry.Separator, new("Exit", () => { })];

        Assert.Equal("Open", DbusMenu.Entry(entries, 1)!.Text);
        Assert.Equal("Exit", DbusMenu.Entry(entries, 3)!.Text);
        Assert.Null(DbusMenu.Entry(entries, DbusMenu.Root));
        Assert.Null(DbusMenu.Entry(entries, 4));
        Assert.Null(DbusMenu.Entry(entries, -1));
    }
}
