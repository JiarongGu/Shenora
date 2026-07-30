using Shenora.Core;
using Shenora.Ipc;
using Shenora.WebView2;
using Shenora.WinForms;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample main window — since P4 a FRAMELESS <see cref="OptimizedForm"/>: the page renders
/// its own title bar and drives the window over the <c>WINDOW</c> IPC module
/// (<see cref="WindowCommandFacade"/>); drop zones overlay page elements
/// (<see cref="DropZoneManager"/>); a tray icon (launcher-style, no close-to-tray so the e2e's
/// graceful close still exits) rounds out the native surface. The IPC bridge keeps its intended
/// order — construct before init (event buffering), attach after init, before navigation.
/// </summary>
public sealed class MainForm : OptimizedForm
{
    /// <summary>One background everywhere — form, WebView2, splash, page CSS, and the DWM border.</summary>
    public static readonly Color Background = Color.FromArgb(31, 31, 31);

    private readonly WebViewHost _host;
    private readonly SplashPanel _splash;
    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridge _bridge;
    private readonly DropZoneManager _dropZones;
    private readonly TrayIcon _tray;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private int _tickCount;

    public MainForm(WebViewHostOptions hostOptions, IMessageDispatcher dispatcher, IEventBus eventBus)
        : base(new OptimizedFormOptions
        {
            FramelessChrome = true,
            BackColor = Background,
            DwmBorderColor = Background, // border line matches the app edge → no visible frame
        })
    {
        Text = "Shenora Sample";
        MinimumSize = new Size(640, 420);

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        _splash = new SplashPanel(new SplashPanelOptions { BackColor = Background });
        Controls.Add(_splash);
        _splash.BringToFront();

        // Native events → React: a 1 Hz tick emitted on the app's event bus; the bridge forwards
        // it to the page as a batched notification. Started on the client's ready handshake so
        // the first tick is never wasted on an unsubscribed page.
        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) => _ = eventBus.EmitAsync("SAMPLE", "TICK",
            new { Count = ++_tickCount, At = DateTimeOffset.Now.ToString("HH:mm:ss") });

        // Drop zones: transparent overlays synced to the page's zone elements (real OS paths).
        _dropZones = new DropZoneManager(new DropZoneManagerOptions
        {
            WebView = _webView,
            ParentForm = this,
            EventBus = eventBus,
        });

        // The window-facing facades need the live form, so they map here (late registration is
        // supported — the dispatcher rebuilds its pipeline lazily).
        if (dispatcher is MessageDispatcher concrete)
        {
            concrete.MapModule(new WindowCommandFacade(new WindowCommandOptions
            {
                Window = this,
                ToggleMaximize = ToggleMaximize,      // the frameless manual work-area path
                IsMaximized = () => IsAppMaximized,   // WindowState never reflects it
            }));
            concrete.MapModule(new DropZoneFacade(_dropZones));
        }

        // Construct the bridge BEFORE InitializeAsync — bus buffering starts here, so events
        // emitted during the (slow) WebView2 init survive to the first post-ready batch.
        _bridge = new WebViewIpcBridge(_webView, new WebViewIpcBridgeOptions
        {
            Dispatcher = dispatcher,
            EventBus = eventBus,
            Log = Console.WriteLine,
            OnClientReady = _ =>
            {
                // Every (re)load: stale overlays belong to the previous page — clear before the
                // new page re-registers its own. Then start the tick source.
                _dropZones.ClearAll();
                _tickTimer.Start();
            },
        });

        // Launcher-style tray (no close-to-tray: closing exits — keeps the e2e's graceful close).
        _tray = new TrayIcon(new TrayIconOptions
        {
            Window = this,
            CloseToTray = false,
            MenuColors = new TrayMenuColors
            {
                Surface = Background,
                Hover = Color.FromArgb(50, 50, 50),
                Border = Color.FromArgb(60, 60, 60),
                Accent = Color.FromArgb(127, 209, 140),
                Text = Color.FromArgb(236, 237, 242),
                DisabledText = Color.FromArgb(150, 151, 168),
            },
        });

        _host = new WebViewHost(_webView, hostOptions);
        Load += OnLoadAsync;
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        // Actionable install prompt instead of an obscure EnsureCoreWebView2Async failure.
        if (!WebViewEnvironment.IsRuntimeAvailable())
        {
            MessageBox.Show(
                "The WebView2 Runtime is not installed.\n\n" +
                "Install the Evergreen WebView2 Runtime from Microsoft and start the app again.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            await _host.InitializeAsync();
            // Attach after init (the core must exist), BEFORE Navigate — hooking after
            // navigation would lose the page's earliest messages.
            _bridge.Attach();
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (_splash.IsDisposed) return;
                Controls.Remove(_splash);
                _splash.Dispose();
                if (!args.IsSuccess)
                {
                    // Never leave a silent dark window with a spinning splash — say what failed
                    // (typical: Vite not running in dev, or an empty wwwroot in a fresh clone).
                    MessageBox.Show(
                        $"The frontend failed to load ({args.WebErrorStatus}).\n\n" +
                        "Dev mode needs the Vite server (dev.mjs vite); packaged mode needs the " +
                        "bundle built (npm run build in samples/Shenora.Sample.Web) before the app build.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _host.Navigate();
        }
        catch (Exception ex)
        {
            // The init-timeout guard and navigation config errors both land here with actionable
            // messages — surface them instead of leaving a silent dark window.
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stop the flush timer + detach before the WebView goes down (the source app's
            // transport once kept posting into a torn-down WebView for the process lifetime).
            _tickTimer.Dispose();
            _dropZones.Dispose();
            _bridge.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
