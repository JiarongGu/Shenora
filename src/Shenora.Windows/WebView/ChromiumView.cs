using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Shenora.Chromium;

namespace Shenora.Windows;

/// <summary>
/// A Chromium page as a WinForms control, beside the WebView2 control (D83): a <see cref="ChromiumChildBrowser"/>
/// in the control's own window, filling it, with its IPC dispatched on the thread that owns the control, which is
/// right for a <see cref="SecondaryWindows"/> window too. The kit's window commands, frameless chrome and caption
/// buttons work over it as they do over the WebView2 control, with the view as the
/// <see cref="WindowCommandOptions.CoordinateSpace"/>.
/// <para>
/// The engine comes from <see cref="WindowsHostExtensions.UseChromiumEngine"/>. The browser opens as the control's
/// handle is created and closes as it goes, so a recreated handle opens the page again.
/// </para>
/// <para>
/// ⚠ WinForms' own <see cref="Control.Focused"/> stays false while the page has the keyboard focus, and
/// <see cref="Control.Focus"/> returns false: the focus is in CEF's child window, which another thread owns.
/// </para>
/// </summary>
public sealed class ChromiumView : Control
{
    private readonly ChromiumEngine _engine;
    private readonly ILogger<ChromiumView>? _log;
    private ChromiumChildBrowser? _browser;

    /// <param name="engine">The app's engine (<see cref="WindowsHostExtensions.UseChromiumEngine"/> registers it).</param>
    /// <param name="log">Diagnostics: a browser that could not open is logged here.</param>
    public ChromiumView(ChromiumEngine engine, ILogger<ChromiumView>? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _log = log;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>The page to open, relative to the app's origin (or its dev server). Null means its root. Read as the
    /// control's handle is created.</summary>
    [DefaultValue(null)]
    public string? Path { get; set; }

    /// <summary>The page's browser while the control's handle exists, else null.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ChromiumChildBrowser? Browser => _browser;

    /// <summary>Opens the page's browser, before <see cref="Control.HandleCreated"/> is raised, so a handler there sees
    /// <see cref="Browser"/>.</summary>
    /// <param name="e">The event data.</param>
    protected override void OnHandleCreated(EventArgs e)
    {
        if (!DesignMode) OpenBrowser();
        base.OnHandleCreated(e);
    }

    private void OpenBrowser()
    {
        try
        {
            _browser = new ChromiumChildBrowser(_engine, Handle, new ChromiumChildBrowserOptions
            {
                Name = Name is { Length: > 0 } name ? name : "view",
                Path = Path,
                BackgroundColor = BackColor,
                UiDispatcher = new WinFormsUiDispatcher(this),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // Never out of handle creation, where WinForms answers an exception with a blocking modal dialog.
            _log?.LogError(ex, "The Chromium page '{Name}' could not open", Name);
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _browser?.SetBounds(ClientRectangle);
    }

    /// <inheritdoc/>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _browser?.Focus();
    }

    // The control's window takes the browser's with it; closing it first lets CEF close it its own way.
    /// <inheritdoc/>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _browser?.Dispose();
        _browser = null;
        base.OnHandleDestroyed(e);
    }
}
