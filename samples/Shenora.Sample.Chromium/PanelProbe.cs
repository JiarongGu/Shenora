using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Sample.Chromium;

/// <summary>
/// The web panel (D97), run for real rather than unit-tested: <c>SHENORA_SAMPLE_PANEL_PROBE=1…5</c> starts it once the
/// main window is up, writes <c>PANEL:</c> lines to <c>SHENORA_SAMPLE_PANEL_LOG</c> (default: beside the app), and closes
/// the app. The profiles persist between launches; delete the data folder's <c>panel-probe*</c> folders to start clean.
/// <list type="bullet">
/// <item><c>1</c> — the panel draws on screen (screen pixels, not a capture of the page), takes mouse and keyboard
/// input POSTED to the shell's window, goes back and forward, hides, moves, keeps two profiles apart, and leaves a
/// persistent and a session cookie in its profile.</item>
/// <item><c>2</c> and <c>3</c> — report the cookies the profile holds at launch, then set both again: run twice, the
/// second launch shows what a restart keeps with <c>PersistSessionCookies</c> true (<c>2</c>, the default) or false
/// (<c>3</c>).</item>
/// <item><c>4</c> — a panel disposed and another made in the same profile at once, in one process; <c>5</c> — the same
/// with another profile's panel opened and closed in between (the session cookie went missing in 2 of 7 such runs).</item>
/// </list>
/// Windows only, as the panel is. Nothing outside the app is touched: input is posted to its own window, and the page
/// comes from a server on localhost that lives as long as the probe.
/// </summary>
internal static class PanelProbe
{
    private const string Profile = "panel-probe";
    private const string OtherProfile = "panel-probe-b";
    private static readonly Color Content = Color.FromArgb(0x22, 0xAA, 0x77);   // the probe page's own colour
    private static string? _log;
    private static IUiDispatcher _ui = null!;

    /// <summary>Start the probe if the environment asks for it; otherwise nothing.</summary>
    public static void StartIfAsked(ShenoraApplication app)
    {
        var run = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_PANEL_PROBE");
        if (run is not ("1" or "2" or "3" or "4" or "5") || !OperatingSystem.IsWindows()) return;
        _log = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_PANEL_LOG") ?? Path.Combine(AppContext.BaseDirectory, "panel-probe.log");
        _ = Task.Run(async () =>
        {
            var windows = app.Services.GetRequiredService<ChromiumWindows>();
            var sessions = app.Services.GetRequiredService<ChromiumSessionHost>();
            _ui = sessions.Ui;   // ISessionBrowser runs on the shell's UI thread, and CEF refuses DevTools from any other
            try { await RunAsync(run, windows, sessions); }
            catch (Exception ex) { Log($"THREW {ex.GetType().Name}: {ex.Message}"); }
            finally { windows.Close(ChromiumWindows.MainWindowName); }
        });
    }

    private static async Task RunAsync(string run, ChromiumWindows windows, ChromiumSessionHost sessions)
    {
        using var server = PageServer.Start();
        var origin = server.Origin;
        var hwnd = await MainWindowAsync();
        GetClientRect(hwnd, out var client);
        var bounds = new Rectangle(client.Right / 4, client.Bottom / 4, client.Right / 2, client.Bottom / 2);
        Log($"run {run}: main window 0x{hwnd:X}, client {client.Right}x{client.Bottom}, panel at {bounds}");

        if (run == "5")
        {
            // As 4, with another profile's panel opened and closed in between.
            var first = await PanelAsync(windows, sessions, Profile, origin, bounds, persistSessionCookies: true);
            using (await PanelAsync(windows, sessions, OtherProfile, origin + "plain", bounds, persistSessionCookies: true)) { }
            first.Dispose();
            await Task.WhenAny(first.Closed, Task.Delay(5000));
            using var recreated = await PanelAsync(windows, sessions, Profile, origin + "plain", bounds, persistSessionCookies: true);
            Log($"DISPOSE after another profile's panel: recreated: {await CookiesAsync(recreated, origin)}");
            return;
        }
        if (run == "4")
        {
            // In one process: close a panel and make another in the same profile at once.
            var first = await PanelAsync(windows, sessions, Profile, origin, bounds, persistSessionCookies: true);
            Log($"COOKIES before dispose: {await CookiesAsync(first, origin)}");
            first.Dispose();
            var closed = await Task.WhenAny(first.Closed, Task.Delay(5000)) == first.Closed;
            using var recreated = await PanelAsync(windows, sessions, Profile, origin + "plain", bounds, persistSessionCookies: true);
            Log($"DISPOSE closed={closed}; recreated in the same profile at once: {await CookiesAsync(recreated, origin)}");
            return;
        }
        if (run != "1")
        {
            // What this launch found, then both cookies set again for the next one — so two launches with the same
            // setting test it end to end.
            var persist = run == "2";
            using var again = await PanelAsync(windows, sessions, Profile, origin + "plain", bounds, persistSessionCookies: persist);
            Log($"COOKIES at launch (PersistSessionCookies={persist}): {await CookiesAsync(again, origin)}");
            await NavigateAsync(again, origin);
            return;
        }

        var panel = await PanelAsync(windows, sessions, Profile, origin, bounds, persistSessionCookies: true);
        // Above every other window while pixels are read off the screen: another app's window over this one would
        // answer instead. Only this app's own window, and only for the probe's life.
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        await Task.Delay(800);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 4);
        var plain = new Point(bounds.X + 30, bounds.Y + 30);   // page colour, away from the button's label
        Log($"RENDER panel {Describe(PixelAt(hwnd, plain))}, main page beside it {Describe(PixelAt(hwnd, new Point(bounds.X / 2, bounds.Y / 2)))}");

        Post(hwnd, WmMouseMove, 0, center);
        Post(hwnd, WmLButtonDown, MkLButton, center);
        Post(hwnd, WmLButtonUp, 0, center);
        await Task.Delay(600);
        Log($"MOUSE posted click -> page counted {await ScriptAsync(panel, "window.clicks")}");

        var field = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height * 3 / 4);
        Post(hwnd, WmLButtonDown, MkLButton, field);
        Post(hwnd, WmLButtonUp, 0, field);
        await Task.Delay(400);
        foreach (var c in "abc") PostMessageW(hwnd, WmChar, c, 1);
        await Task.Delay(600);
        Log($"KEYS posted 'abc' -> field holds {await ScriptAsync(panel, "window.keys")}");

        await NavigateAsync(panel, origin + "two");
        await _ui.InvokeAsync(panel.GoBack);
        await LoadedAsync(panel);
        var back = await _ui.InvokeAsync(() => Task.FromResult(panel.Browser.Source));
        await _ui.InvokeAsync(panel.GoForward);
        await LoadedAsync(panel);
        Log($"HISTORY back -> {Tail(back)}, forward -> {Tail(await _ui.InvokeAsync(() => Task.FromResult(panel.Browser.Source)))}");
        await NavigateAsync(panel, origin);

        panel.SetVisible(false);
        await Task.Delay(500);
        var hiddenPixel = PixelAt(hwnd, plain);
        panel.SetVisible(true);
        await Task.Delay(500);
        Log($"HIDE {Describe(hiddenPixel)}, SHOW {Describe(PixelAt(hwnd, plain))}");

        var moved = new Rectangle(bounds.X / 2, bounds.Y / 2, bounds.Width / 2, bounds.Height / 2);
        panel.SetBounds(moved);
        await Task.Delay(500);
        Log($"MOVE new place {Describe(PixelAt(hwnd, new Point(moved.X + 30, moved.Y + 30)))}, "
            + $"old place {Describe(PixelAt(hwnd, new Point(bounds.Right - 10, bounds.Bottom - 10)))}");

        Log($"COOKIES {Profile}: {await CookiesAsync(panel, origin)}");
        using (var other = await PanelAsync(windows, sessions, OtherProfile, origin + "plain", bounds, persistSessionCookies: true))
            Log($"ISOLATION {OtherProfile}: {await CookiesAsync(other, origin)}");
        SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static async Task<ChromiumWebPanel> PanelAsync(ChromiumWindows windows, ChromiumSessionHost sessions, string profile,
        string url, Rectangle bounds, bool persistSessionCookies)
    {
        var panel = await windows.CreateWebPanelAsync(ChromiumWindows.MainWindowName, new ChromiumWebPanelOptions
        {
            Session = new SessionBrowserOptions
            {
                ProfileDirectory = InteractiveSession.ComposeProfileDirectory(sessions.ProfilesDirectory, profile),
            },
            Url = url,
            Bounds = bounds,
            BackgroundColor = Color.Magenta,   // what an embed that never presents its content would show
            PersistSessionCookies = persistSessionCookies,
        });
        await LoadedAsync(panel);
        return panel;
    }

    private static Task<string?> ScriptAsync(ChromiumWebPanel panel, string script) =>
        _ui.InvokeAsync(() => panel.Browser.ExecuteScriptAsync(script));

    private static async Task NavigateAsync(ChromiumWebPanel panel, string url)
    {
        await _ui.InvokeAsync(() => panel.Browser.Navigate(url));
        await LoadedAsync(panel);
    }

    // The document, not the navigation event: a navigation completes before its script has run. A script refused while
    // the page is still being committed is retried; one refused for the whole 10 s is reported.
    private static async Task LoadedAsync(ChromiumWebPanel panel)
    {
        await Task.Delay(300);
        string? refused = null;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                if (await ScriptAsync(panel, "document.readyState") == "\"complete\"") return;
                refused = null;
            }
            catch (InvalidOperationException ex) { refused = ex.Message; }
            await Task.Delay(200);
        }
        Log($"a page did not finish loading in 10 s{(refused is null ? "" : $" (script refused: {refused})")}");
    }

    private static async Task<string> CookiesAsync(ChromiumWebPanel panel, string origin)
    {
        var cookies = await _ui.InvokeAsync(() => panel.Browser.GetCookiesAsync(origin));
        return cookies.Count == 0 ? "none" : string.Join(", ", cookies.Select(c => c.Name).Order());
    }

    private static async Task<nint> MainWindowAsync()
    {
        for (var i = 0; i < 150; i++)
        {
            var hwnd = FindWindowW(null, "Shenora Chromium Sample");
            if (hwnd != 0 && IsWindowVisible(hwnd)) return hwnd;
            await Task.Delay(200);
        }
        throw new TimeoutException("the main window did not appear");
    }

    private static Color PixelAt(nint hwnd, Point clientPoint)
    {
        var point = new PointStruct { X = clientPoint.X, Y = clientPoint.Y };
        ClientToScreen(hwnd, ref point);
        var dc = GetDC(0);
        try
        {
            var bgr = GetPixel(dc, point.X, point.Y);
            return Color.FromArgb((int)(bgr & 0xFF), (int)((bgr >> 8) & 0xFF), (int)((bgr >> 16) & 0xFF));
        }
        finally { ReleaseDC(0, dc); }
    }

    private static string Describe(Color c)
    {
        var name = Near(c, Content) ? "PAGE" : Near(c, Color.Magenta) ? "PANEL-BACKGROUND" : "other";
        return $"{name} #{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static bool Near(Color a, Color b) =>
        Math.Abs(a.R - b.R) < 24 && Math.Abs(a.G - b.G) < 24 && Math.Abs(a.B - b.B) < 24;

    private static string Tail(string url) => url.Length == 0 ? "(none)" : new Uri(url).AbsolutePath;

    private static void Post(nint hwnd, uint message, nint keys, Point at) =>
        PostMessageW(hwnd, message, keys, (at.Y << 16) | (at.X & 0xFFFF));

    private static void Log(string line)
    {
        if (_log is null) return;
        lock (Content.GetType()) File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} PANEL: {line}{Environment.NewLine}");
    }

    /// <summary>A page on localhost that sets a persistent and a session cookie, counts clicks and echoes keys.</summary>
    private sealed class PageServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Origin { get; private init; } = "";

        public static PageServer Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new PageServer { Origin = $"http://localhost:{port}/" };
            server._listener.Prefixes.Add(server.Origin);
            server._listener.Start();
            _ = Task.Run(server.ServeAsync);
            return server;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (path == "/")
                {
                    context.Response.AppendHeader("Set-Cookie", "persist=1; Max-Age=86400; Path=/");
                    context.Response.AppendHeader("Set-Cookie", "session=1; Path=/");
                }
                var colour = path == "/two" ? "#2255cc" : "#22aa77";
                var body = Encoding.UTF8.GetBytes($"""
                    <!doctype html><html><body style="margin:0;background:{colour};font:32px sans-serif">
                    <button id="b" style="position:absolute;inset:0 0 50% 0;background:{colour};border:0">click</button>
                    <input id="t" style="position:absolute;inset:50% 0 0 0;background:{colour};border:0;font:32px sans-serif">
                    <script>window.clicks=0;window.keys='';b.onclick=()=>window.clicks++;t.oninput=()=>window.keys=t.value;</script>
                    </body></html>
                    """);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmChar = 0x0102;
    private const nint MkLButton = 0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct PointStruct { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectStruct { public int Left, Top, Right, Bottom; }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out RectStruct rect);
    [DllImport("user32")] private static extern bool ClientToScreen(nint hwnd, ref PointStruct point);
    [DllImport("user32")] private static extern bool PostMessageW(nint hwnd, uint message, nint w, nint l);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    private const nint HwndTopmost = -1, HwndNoTopmost = -2;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    [DllImport("user32")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32")] private static extern uint GetPixel(nint dc, int x, int y);
}
