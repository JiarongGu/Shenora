using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Windows;

/// <summary>Menu palette for <see cref="TrayIcon"/> — the stock WinForms menu is light gray, which
/// clashes with a dark app. Null on the options = the stock renderer.</summary>
public sealed class TrayMenuColors
{
    /// <summary>Menu background.</summary>
    public required Color Surface { get; init; }

    /// <summary>Hovered-item background.</summary>
    public required Color Hover { get; init; }

    /// <summary>Menu + separator lines.</summary>
    public required Color Border { get; init; }

    /// <summary>Selection border + check highlight.</summary>
    public required Color Accent { get; init; }

    /// <summary>Item text.</summary>
    public required Color Text { get; init; }

    /// <summary>Disabled-item text (headers/status lines) — the stock renderer grays it into
    /// illegibility on a dark background, so pick something legible on <see cref="Surface"/>.</summary>
    public required Color DisabledText { get; init; }
}

/// <summary>Inputs for <see cref="TrayIcon"/>.</summary>
public sealed class TrayIconOptions
{
    /// <summary>The window the tray shows/hides.</summary>
    public required Form Window { get; init; }

    /// <summary>Tray tooltip. Null = the window's title.</summary>
    public string? Text { get; init; }

    /// <summary>Tray icon. Null = the window's icon, else a neutral system icon.</summary>
    public Icon? Icon { get; init; }

    /// <summary>True (default): closing the window hides it to the tray and the app keeps running; the
    /// Exit menu item (or <see cref="TrayIcon.ExitApplication"/>) closes for real. False: the tray is
    /// just a launcher.</summary>
    /// <remarks>
    /// 🔴 While this is on, a bare <see cref="Form.Close"/> from YOUR code HIDES the window rather than
    /// exiting: WinForms reports <see cref="CloseReason.UserClosing"/> for a programmatic close exactly as
    /// for the user's X, with no way to tell them apart. Close from code with
    /// <see cref="TrayIcon.ExitApplication"/> or <c>Application.Exit()</c> — a startup-abort path calling
    /// <c>Close()</c> leaves a resident process with a window that can never finish loading.
    /// </remarks>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Label of the built-in open item (bold, double-click equivalent).</summary>
    public string OpenMenuItemText { get; init; } = "Open";

    /// <summary>Label of the built-in exit item.</summary>
    public string ExitMenuItemText { get; init; } = "Exit";

    /// <summary>Add the app's items — they land between the built-in Open item and the trailing
    /// separator + Exit. Refresh dynamic items from the menu's <c>Opening</c> event.</summary>
    public Action<ContextMenuStrip>? ConfigureMenu { get; init; }

    /// <summary>App-colored menu; null = the stock renderer.</summary>
    public TrayMenuColors? MenuColors { get; init; }
}

/// <summary>A <see cref="NotifyIcon"/> with an Open/…/Exit menu, double-click restore, and the
/// close-to-tray dance. Create it once the main window exists; dispose it with the window — a leaked
/// <see cref="NotifyIcon"/> ghosts in the tray until hovered.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TrayIconOptions _options;
    private readonly ILogger<TrayIcon> _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly Font _openItemFont; // ToolStripItem doesn't own assigned fonts — dispose ours
    private bool _exiting;
    private bool _disposed;

    /// <summary>A tray icon and its themed menu. ⚠ See <see cref="TrayIconOptions.CloseToTray"/> — a
    /// programmatic <c>Close()</c> hides rather than exits.</summary>
    public TrayIcon(TrayIconOptions options, ILogger<TrayIcon>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<TrayIcon>.Instance;

        Menu = new ContextMenuStrip { ShowImageMargin = false };
        if (options.MenuColors is { } colors)
            Menu.Renderer = new TrayMenuRenderer(colors);

        var open = new ToolStripMenuItem(options.OpenMenuItemText, null, (_, _) => ShowWindow());
        _openItemFont = new Font(open.Font, FontStyle.Bold);
        open.Font = _openItemFont;
        Menu.Items.Add(open);

        options.ConfigureMenu?.Invoke(Menu);

        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(new ToolStripMenuItem(options.ExitMenuItemText, null, (_, _) => ExitApplication()));

        _notifyIcon = new NotifyIcon
        {
            Icon = options.Icon ?? options.Window.Icon ?? SystemIcons.Application,
            Text = options.Text ?? options.Window.Text,
            ContextMenuStrip = Menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        if (options.CloseToTray)
            options.Window.FormClosing += OnWindowClosing;
        // ⚠ Hide the shell icon only once the close COMPLETED: hiding in FormClosing means a later handler
        // cancelling the close leaves a running app with no tray icon and close-to-tray still armed — an
        // unreachable window.
        options.Window.FormClosed += OnWindowClosed;
    }

    /// <summary>The tray menu (built-in Open first, app items, separator, Exit last).</summary>
    public ContextMenuStrip Menu { get; }

    /// <summary>Restore + focus the window (double-click / the Open item), through
    /// <see cref="WindowActivation"/>.</summary>
    public void ShowWindow() => WindowActivation.BringToFront(_options.Window);

    /// <summary>Close the window FOR REAL — bypasses close-to-tray.</summary>
    public void ExitApplication()
    {
        _exiting = true;
        if (_options.Window.IsDisposed) return;
        _options.Window.Close();
        // ⚠ Another FormClosing handler may have CANCELED the close (an unsaved-changes prompt). Re-arm
        // close-to-tray, or the next plain user close would exit.
        if (!_options.Window.IsDisposed) _exiting = false;
    }

    private void OnWindowClosing(object? sender, FormClosingEventArgs e)
    {
        // Hides to tray: UserClosing — the user's X AND a bare Form.Close() (see CloseToTray).
        // Passes through: _exiting, ApplicationExitCall, WindowsShutDown, TaskManagerClosing,
        // FormOwnerClosing, MdiFormClosing.
        if (_exiting || e.CloseReason != CloseReason.UserClosing)
            return;
        e.Cancel = true;
        _options.Window.Hide();
        _logger.LogDebug("Window hidden to tray");
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _notifyIcon.Visible = false;

    /// <summary>Hides and releases the icon. Idempotent — the shell keeps a ghost icon otherwise.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_options.Window.IsDisposed)
        {
            if (_options.CloseToTray) _options.Window.FormClosing -= OnWindowClosing;
            _options.Window.FormClosed -= OnWindowClosed;
        }
        _notifyIcon.Visible = false; // a disposed-but-visible NotifyIcon ghosts until hovered
        _notifyIcon.Dispose();
        Menu.Dispose();
        _openItemFont.Dispose();
    }

    /// <summary>Parameterized dark menu renderer: legible text on the app's surface, including disabled
    /// header lines.</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly TrayMenuColors _colors;

        public TrayMenuRenderer(TrayMenuColors colors) : base(new TrayMenuColorTable(colors))
        {
            _colors = colors;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _colors.Text : _colors.DisabledText;
            base.OnRenderItemText(e);
        }
    }

    private sealed class TrayMenuColorTable(TrayMenuColors colors) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => colors.Surface;
        public override Color ImageMarginGradientBegin => colors.Surface;
        public override Color ImageMarginGradientMiddle => colors.Surface;
        public override Color ImageMarginGradientEnd => colors.Surface;
        public override Color MenuBorder => colors.Border;
        public override Color MenuItemBorder => colors.Accent;
        public override Color MenuItemSelected => colors.Hover;
        public override Color MenuItemSelectedGradientBegin => colors.Hover;
        public override Color MenuItemSelectedGradientEnd => colors.Hover;
        public override Color MenuItemPressedGradientBegin => colors.Surface;
        public override Color MenuItemPressedGradientEnd => colors.Surface;
        public override Color SeparatorDark => colors.Border;
        public override Color SeparatorLight => colors.Border;
        public override Color CheckBackground => colors.Accent;
        public override Color CheckSelectedBackground => colors.Accent;
        public override Color CheckPressedBackground => colors.Accent;
    }
}
