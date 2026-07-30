using Shenora.Core;
using Shenora.Ipc;
using Shenora.WebView2;
using Shenora.WinForms;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample main window: WebView2 filling the form, a <see cref="SplashPanel"/> on top until
/// the first navigation completes, the runtime presence check surfaced as an actionable prompt
/// (the gap every source app shipped with), and the IPC bridge wired in its intended order —
/// construct before init (event buffering), attach after init, before navigation.
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>One background everywhere — form, WebView2, splash, and the page's own CSS.</summary>
    public static readonly Color Background = Color.FromArgb(31, 31, 31);

    private readonly WebViewHost _host;
    private readonly SplashPanel _splash;
    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridge _bridge;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private int _tickCount;

    public MainForm(WebViewHostOptions hostOptions, IMessageDispatcher dispatcher, IEventBus eventBus)
    {
        Text = "Shenora Sample";
        BackColor = Background;

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

        // Construct the bridge BEFORE InitializeAsync — bus buffering starts here, so events
        // emitted during the (slow) WebView2 init survive to the first post-ready batch.
        _bridge = new WebViewIpcBridge(_webView, new WebViewIpcBridgeOptions
        {
            Dispatcher = dispatcher,
            EventBus = eventBus,
            Log = Console.WriteLine,
            OnClientReady = _ => _tickTimer.Start(), // fires on the UI thread, per handshake
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
            _bridge.Dispose();
        }
        base.Dispose(disposing);
    }
}
