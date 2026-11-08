using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shenora.Chromium;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>What the app says about one window a <see cref="ChromiumWindowHost"/> opens: its chrome, and nothing of its page.</summary>
public sealed class ChromiumFormOptions
{
    /// <summary>The window's caption and taskbar label. Null = the name it is opened under.</summary>
    public string? Title { get; init; }

    /// <summary>The window's icon. Null = the default form icon.</summary>
    public Icon? Icon { get; init; }

    /// <summary>
    /// The window's chrome: frameless with caption buttons it paints, or a framed window under the system's (the default),
    /// its fill and DWM colours. Null = a framed window.
    /// </summary>
    public OptimizedFormOptions? Chrome { get; init; }

    /// <summary>Sizing defaults and minimums for the remembered geometry. Null = the window-state defaults.</summary>
    public WindowStateOptions? State { get; init; }

    /// <summary>
    /// Run on the window's own STA thread once it is made and before it is shown: the app's seam for what the host does not
    /// own (a <see cref="TaskbarOverlay"/>, a <see cref="TaskbarIdentity"/>, the caption colours, the theme, event
    /// subscriptions on the form). The form is an <see cref="OptimizedForm"/>.
    /// </summary>
    public Action<OptimizedForm>? Created { get; init; }

    /// <summary>The sentence shown in the window when its page's browser could not come up. Default is English.</summary>
    public string FailureText { get; init; } = "The page could not be opened.";
}

/// <summary>
/// Named Chromium windows over a <see cref="SecondaryWindows"/> registry: one STA thread and message pump per name, its page
/// at a route of the app's own origin, its geometry remembered per name, the app giving only its chrome
/// (<see cref="ChromiumFormOptions"/>) and its routes.
/// <para>
/// One window per name, and <see cref="Open"/> on an open one brings it forward. Nothing here blocks: every call arrives on
/// some other thread, and each window runs its own pump, so the work for a window is posted to it
/// (<see cref="Post"/>).
/// </para>
/// <para>
/// A window is an <see cref="OptimizedForm"/> holding a <see cref="ChromiumView"/> that fills it, so the kit's window
/// commands, caption buttons and drag area work in it as they do in the main window, and its page's IPC runs on its own
/// thread. The registry belongs to whoever made it; this host never disposes it.
/// </para>
/// </summary>
public sealed class ChromiumWindowHost
{
    private readonly ChromiumEngine _engine;
    private readonly SecondaryWindows _windows;
    private readonly string _stateFolder;
    private readonly Func<string, ChromiumFormOptions> _describe;
    private readonly ILogger<ChromiumView>? _log;
    private readonly ConcurrentDictionary<string, byte> _asked = new(StringComparer.Ordinal);

    /// <param name="engine">The app's engine (<see cref="WindowsHostExtensions.UseChromiumEngine"/> registers it).</param>
    /// <param name="windows">The registry the windows run in: the app's one <see cref="SecondaryWindows"/>.</param>
    /// <param name="stateFolder">Where each window's geometry is remembered, one file per name. Created on the first save.</param>
    /// <param name="describe">The chrome of the window opened under a name; null = every window framed and untitled.</param>
    /// <param name="log">Diagnostics: a window's browser that could not come up is logged here, and by its view.</param>
    public ChromiumWindowHost(
        ChromiumEngine engine, SecondaryWindows windows, string stateFolder,
        Func<string, ChromiumFormOptions>? describe = null, ILogger<ChromiumView>? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentException.ThrowIfNullOrEmpty(stateFolder);
        _engine = engine;
        _windows = windows;
        _stateFolder = stateFolder;
        _describe = describe ?? (_ => new ChromiumFormOptions());
        _log = log;
    }

    /// <summary>
    /// Open the window <paramref name="name"/> at <paramref name="path"/> (relative to the app's origin, as
    /// <see cref="ChromiumView.Path"/> is), or bring it forward when it is already open. Any thread; the window comes up on
    /// its own.
    /// </summary>
    /// <returns>True when a window is created; false when an existing one is activated.</returns>
    public bool Open(string name, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(path);

        var options = _describe(name);
        _asked[name] = 0;
        return _windows.Open(name, new SecondaryWindowOptions
        {
            // Runs ON the new window's thread: made there, shown by its pump once the geometry has been applied.
            CreateForm = () => ChromiumWindowForm.Create(name, path, _engine, _log, options),
            StateStore = new JsonFileWindowStateStore(Path.Combine(_stateFolder, StateFile(name))),
            StateOptions = options.State,
        });
    }

    /// <summary>The names this host opened that are open now, in order. Any thread.</summary>
    public IReadOnlyList<string> Opened => [.. _asked.Keys.Where(_windows.HasWindow).Order(StringComparer.Ordinal)];

    /// <summary>The window opened under <paramref name="name"/>, or null. Its members belong to its own thread: use <see cref="Post"/>.</summary>
    public OptimizedForm? FormNamed(string name) => _asked.ContainsKey(name) ? _windows.TryGetForm(name) as OptimizedForm : null;

    /// <summary>
    /// Run <paramref name="action"/> on the thread of the window opened under <paramref name="name"/>, never blocking.
    /// False when it is not open or its handle does not exist yet, which is nothing wrong: whatever it was to be told,
    /// the app's <see cref="ChromiumFormOptions.Created"/> hears it first.
    /// </summary>
    public bool Post(string name, Action<OptimizedForm> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var form = FormNamed(name);
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return false;
        try
        {
            // BeginInvoke, never Invoke: this arrives on the IPC thread, and the window runs its own pump.
            form.BeginInvoke(() =>
            {
                if (!form.IsDisposed) action(form);
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            // The handle went between the look and the post.
            return false;
        }
    }

    /// <summary>Bring the window forward, or nothing when it is not open. Any thread, never blocking.</summary>
    public void Activate(string name)
    {
        if (_asked.ContainsKey(name)) _windows.Activate(name);
    }

    /// <summary>Close the window, or nothing when it is not open. Any thread, never blocking.</summary>
    public void Close(string name)
    {
        if (_asked.ContainsKey(name)) _windows.Close(name);
    }

    /// <summary>
    /// The file a window's geometry is kept in, one per name. The name is escaped, and the escape is injective over every
    /// character a name may hold, so two names can never share a file (a <c>-</c> substitution would have met
    /// <c>a/b</c> with <c>a-b</c>).
    /// </summary>
    internal static string StateFile(string name) => $"{Uri.EscapeDataString(name)}.json";
}

/// <summary>The window a <see cref="ChromiumWindowHost"/> makes: a Chromium page filling an <see cref="OptimizedForm"/>.</summary>
internal sealed class ChromiumWindowForm : OptimizedForm
{
    private readonly ChromiumView _view;
    private readonly ChromiumFormOptions _options;
    private readonly ILogger<ChromiumView>? _log;

    private ChromiumWindowForm(string name, string path, ChromiumEngine engine, ILogger<ChromiumView>? log, ChromiumFormOptions options)
        : base(options.Chrome)
    {
        _options = options;
        _log = log;
        Name = name;
        Text = options.Title ?? name;
        if (options.Icon is { } icon) Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;

        // Its browser opens as the view's handle is created, on this window's own thread.
        _view = new ChromiumView(engine, log) { Name = name, Dock = DockStyle.Fill, Path = path };
        Controls.Add(_view);
        Load += async (_, _) => await BringUpAsync();
    }

    internal static OptimizedForm Create(
        string name, string path, ChromiumEngine engine, ILogger<ChromiumView>? log, ChromiumFormOptions options)
    {
        var form = new ChromiumWindowForm(name, path, engine, log, options);
        options.Created?.Invoke(form);
        return form;
    }

    /// <summary>
    /// 🔴 A closed window's page must stop hearing the bus: a bridge left attached queues every later event, up to its cap,
    /// once per window ever opened. The view closes its browser, and its page's IPC with it, as its handle goes; disposing it
    /// here makes that happen with the window rather than whenever the form is collected.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _view.Dispose();
        base.OnFormClosed(e);
    }

    private async Task BringUpAsync()
    {
        try
        {
            if (_view.Browser is { } browser) await browser.Created;
        }
        catch (Exception ex)
        {
            // Never a silent blank window: a sentence stands in its place. Closing the window is the whole recovery.
            AppCallback.Log(_log, () => $"The page of window '{Name}' could not come up", LogLevel.Error, ex);
            if (IsDisposed) return;
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = _options.FailureText,
                ForeColor = ForeColor,
                Padding = new Padding(24),
            });
        }
    }
}
