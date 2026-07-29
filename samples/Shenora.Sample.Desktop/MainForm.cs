using Shenora.WebView2;
using Shenora.WinForms;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample main window: WebView2 filling the form, a <see cref="SplashPanel"/> on top until
/// the first navigation completes, and the runtime presence check surfaced as an actionable
/// prompt (the gap every source app shipped with).
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>One background everywhere — form, WebView2, splash, and the page's own CSS.</summary>
    public static readonly Color Background = Color.FromArgb(31, 31, 31);

    private readonly WebViewHost _host;
    private readonly SplashPanel _splash;
    private readonly WebView2Control _webView;

    public MainForm(WebViewHostOptions hostOptions)
    {
        Text = "Shenora Sample";
        BackColor = Background;

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        _splash = new SplashPanel(new SplashPanelOptions { BackColor = Background });
        Controls.Add(_splash);
        _splash.BringToFront();

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
}
