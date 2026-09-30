using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's tray icon (<see cref="ChromiumHostOptions.Tray"/>): the notification area on Windows, a status
/// item in the menu bar on macOS, and a StatusNotifierItem on Linux, which KDE and most panels show, and GNOME with its
/// AppIndicator extension. Its menu is Open, the app's own items, and Exit. The names match the WinForms shell's
/// <c>TrayIcon</c> where the concept is the same.
/// </summary>
public sealed class ChromiumTrayOptions
{
    /// <summary>The tooltip. Null means the main window's title, else the app's name.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The icon's file: an <c>.ico</c> on Windows, an image macOS reads (such as a PNG) on macOS, drawn at the menu
    /// bar's size, and a PNG or SVG on Linux, which the panel reads from its folder by its name. Null means the app's
    /// own icon: its executable's on Windows, its bundle's on macOS, and on Linux the desktop's generic one for an
    /// application.
    /// </summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// True (the default): closing the main window hides it, and the app keeps running until Exit, or
    /// <see cref="ChromiumTray.ExitApplication"/>. False: the tray only reopens the window.
    /// <para>
    /// Only while the icon is shown: where there is no tray to show it in (a Linux desktop with no tray host), closing
    /// the main window closes it, and the app ends, rather than leaving it running with no way back.
    /// </para>
    /// <para>
    /// ⚠ A close from code hides it too (<see cref="ChromiumWindows.Close"/>, or the page's own close command): CEF
    /// asks the same question for both, so exit with <see cref="ChromiumTray.ExitApplication"/>.
    /// </para>
    /// </summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>The first item, which shows the main window, as a double click on the icon does on Windows.</summary>
    public string OpenMenuItemText { get; init; } = "Open";

    /// <summary>The last item, which ends the app.</summary>
    public string ExitMenuItemText { get; init; } = "Exit";

    /// <summary>The app's own items, between Open and Exit. Asked each time the menu opens, on the UI thread, so they
    /// can change; on Linux, each time the panel lays the menu out, which is also when it first learns of the icon.</summary>
    public Func<IReadOnlyList<ChromiumTrayMenuItem>>? MenuItems { get; init; }
}

/// <summary>One of <see cref="ChromiumTrayOptions.MenuItems"/>.</summary>
public sealed class ChromiumTrayMenuItem
{
    /// <param name="text">The item's label.</param>
    /// <param name="onClick">What choosing it does, on the UI thread.</param>
    public ChromiumTrayMenuItem(string text, Action onClick)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(onClick);
        Text = text;
        OnClick = onClick;
    }

    private ChromiumTrayMenuItem() => Text = "";

    /// <summary>A line between items.</summary>
    public static ChromiumTrayMenuItem Separator { get; } = new();

    /// <summary>The item's label; empty for <see cref="Separator"/>.</summary>
    public string Text { get; }

    /// <summary>What choosing it does; null for <see cref="Separator"/>.</summary>
    public Action? OnClick { get; }

    /// <summary>False shows it greyed, and it cannot be chosen.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A check mark beside it.</summary>
    public bool Checked { get; init; }
}

/// <summary>
/// The tray icon <see cref="ChromiumHostOptions.Tray"/> asks for, from the app's services: what its menu does, for the
/// app to do itself. Registered only when the shell has a tray.
/// </summary>
public sealed class ChromiumTray
{
    private readonly ChromiumTrayOptions _options;
    private readonly ChromiumWindows _windows;
    private readonly string _defaultText;
    private readonly ILogger? _log;
    private NativeTray? _native;
    private volatile bool _exiting;

    internal ChromiumTray(ChromiumTrayOptions options, ChromiumWindows windows, string defaultText, ILogger? log)
    {
        _options = options;
        _windows = windows;
        _defaultText = defaultText;
        _log = log;
        windows.CloseGuard = MayClose;
    }

    /// <summary>Show the main window and bring it to the front, as the menu's Open does. Any thread.</summary>
    public void ShowWindow() => _windows.Activate(ChromiumWindows.MainWindowName);

    /// <summary>Close every window for real, past <see cref="ChromiumTrayOptions.CloseToTray"/>; the app then ends, as
    /// the menu's Exit does. Any thread.</summary>
    public void ExitApplication()
    {
        _exiting = true;
        _windows.CloseAll();
    }

    internal string Text => _options.Text ?? _defaultText;

    internal string? IconPath => _options.IconPath;

    /// <summary>Only the main window hides, only until Exit, and only while its icon is shown to bring it back.</summary>
    internal bool MayClose(string name) =>
        _exiting || !_options.CloseToTray || name != ChromiumWindows.MainWindowName || _native is not { Shown: true };

    /// <summary>
    /// The icon can no longer bring the window back (the panel showing it quit): a main window hidden in the tray is
    /// shown, since nothing else could show it now. UI thread.
    /// </summary>
    internal void IconGone()
    {
        if (!_exiting && !_windows.IsShowing(ChromiumWindows.MainWindowName)) ShowWindow();
    }

    /// <summary>The menu as it opens: Open, the app's items, a separator, Exit. App items that throw are left out, and
    /// logged.</summary>
    internal IReadOnlyList<TrayMenuEntry> Menu()
    {
        var entries = new List<TrayMenuEntry> { new(_options.OpenMenuItemText, ShowWindow, IsDefault: true) };
        var items = _options.MenuItems is { } source
            ? AppCallback.RunOrDefault(source, fallback: [], ex => AppCallback.Log(_log, () => "[Shenora.Chromium] The tray's MenuItems threw; its items are left out", LogLevel.Error, ex))
            : [];
        foreach (var item in items ?? [])
            entries.Add(item.OnClick is null ? TrayMenuEntry.Separator : new TrayMenuEntry(item.Text, item.OnClick, item.Enabled, item.Checked));
        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new TrayMenuEntry(_options.ExitMenuItemText, ExitApplication));
        return entries;
    }

    /// <summary>Carry out a chosen entry, guarded: an app's click never unwinds into the platform's menu loop.</summary>
    internal void Choose(TrayMenuEntry entry)
    {
        if (entry.Run is not { } run || !entry.Enabled) return;
        AppCallback.Run(run, ex => AppCallback.Log(_log, () => $"[Shenora.Chromium] The tray item '{entry.Text}' threw", LogLevel.Error, ex));
    }

    /// <summary>Show the icon. UI thread, once the main window is open. <paramref name="create"/> stands in for the
    /// platform's tray in tests.</summary>
    internal void Start(Func<ChromiumTray, ILogger?, NativeTray?>? create = null)
    {
        _native = (create ?? NativeTray.Create)(this, _log);
        if (_native is null) AppCallback.Log(_log, () => "[Shenora.Chromium] This platform has no tray yet; ChromiumHostOptions.Tray is ignored", LogLevel.Warning);
    }

    /// <summary>Remove the icon, before CEF shuts down: a tray icon left behind stays until the pointer passes over it.
    /// UI thread.</summary>
    internal void Stop()
    {
        _native?.Dispose();
        _native = null;
    }
}

/// <summary>One line of the tray's menu, as the platform draws it.</summary>
internal sealed record TrayMenuEntry(string Text, Action? Run, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static TrayMenuEntry Separator { get; } = new("", null);

    public bool IsSeparator => Run is null;
}
