namespace Shenora.Chromium.Host;

/// <summary>
/// The tray's menu as <c>com.canonical.dbusmenu</c> carries it, with no bus: each entry's id and properties. Pure, so it
/// is tested without D-Bus. Id 0 is the menu itself, and an entry's id is its position in the menu plus one.
/// </summary>
internal static class DbusMenu
{
    public const int Root = 0;

    /// <summary>The menu's own properties: it has children to show.</summary>
    public static IReadOnlyList<KeyValuePair<string, object>> RootProperties { get; } = [new("children-display", "submenu")];

    /// <summary>
    /// One entry's properties: a separator's type; else its label, whether it is enabled, and a check mark when it has
    /// one. An underscore in the label is doubled, because a single one marks the access key.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object>> Properties(TrayMenuEntry entry)
    {
        if (entry.IsSeparator) return [new("type", "separator")];
        var properties = new List<KeyValuePair<string, object>>
        {
            new("label", entry.Text.Replace("_", "__", StringComparison.Ordinal)),
            new("enabled", entry.Enabled),
        };
        if (entry.Checked)
        {
            properties.Add(new("toggle-type", "checkmark"));
            properties.Add(new("toggle-state", 1));
        }
        return properties;
    }

    /// <summary>The entry an id names in <paramref name="entries"/>, or null for the menu itself or an id it has not.</summary>
    public static TrayMenuEntry? Entry(IReadOnlyList<TrayMenuEntry> entries, int id) =>
        id >= 1 && id <= entries.Count ? entries[id - 1] : null;
}
