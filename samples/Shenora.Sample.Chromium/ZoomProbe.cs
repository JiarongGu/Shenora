using System.Net;
using System.Net.Sockets;
using System.Text;
using Shenora.Core.WebView;

namespace Shenora.Sample.Chromium;

/// <summary>
/// The zoom guard (<c>ChromiumZoom</c>) run on real CEF, driven by REAL input from outside: <c>SHENORA_SAMPLE_ZOOM_PROBE=zoom</c>
/// makes the main window show a page of the probe's own that reports, as <c>ZOOM:</c> lines in <c>SHENORA_SAMPLE_ZOOM_LOG</c>
/// (default: beside the app), what it hears and what zoom it is at: every wheel (is Ctrl down, was it cancelled before the
/// page's own listener), every <c>keydown</c>, <c>keypress</c> and <c>keyup</c>, and its <c>devicePixelRatio</c>,
/// <c>innerWidth</c>, visual-viewport scale and <c>touch-action</c> whenever one changes, and the same for a frame of another
/// origin (served from loopback, so Chromium puts it in a process of its own). Whoever drives it (<c>zoom-linux</c> in
/// <c>tests/Shenora.Tests.Linux</c>, <c>devtools/scripts/zoom-windows.ps1</c>) presses Ctrl and wheels, plus, minus and 0 over
/// the window; the page, not the app's own account, says what a page would have felt.
/// <c>SHENORA_SAMPLE_ZOOM_ALLOW=1</c> sets <c>AllowZoom</c> (the CONTROL: the same input must then change the zoom, or the
/// probe could not have seen it fail). <c>SHENORA_SAMPLE_ZOOM_PROBE=scroll</c> allows zoom and has the page time wheel to scroll
/// in four phases of 120 wheels, a non-passive capture listener (the guard's own code) off, on, off, on, so the listener's
/// cost is read in one run of one process. Nothing outside the app is touched; its one server is on loopback.
/// </summary>
internal static class ZoomProbe
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_ZOOM_PROBE") is "zoom" or "scroll"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_ZOOM_PROBE") : null;
    private static readonly string LogPath = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_ZOOM_LOG") ?? Path.Combine(AppContext.BaseDirectory, "zoom-probe.log");
    private static readonly object Gate = new();
    private static readonly Lazy<FrameServer?> Frame = new(() => Mode is null ? null : new FrameServer());

    /// <summary>The probe is running: no splash, which is one more thing over the page.</summary>
    public static bool Active => Mode is not null;

    /// <summary>The page the main window shows: the probe's own while it runs, else the one given.</summary>
    public static string Page(string normal) => Mode is null ? normal : "zoom.html";

    /// <summary>The user may zoom: the control, and the scroll timing, which has its own listener to time.</summary>
    public static bool AllowZoom => Mode == "scroll" || Environment.GetEnvironmentVariable("SHENORA_SAMPLE_ZOOM_ALLOW") == "1";

    /// <summary>The app's provider, answering the probe's page and its reports first while it runs.</summary>
    public static IWebViewResourceProvider? Provider(IWebViewResourceProvider? app) => Mode is null ? app : new ProbeProvider(app);

    private static void Log(string line)
    {
        lock (Gate) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} ZOOM: {line}{Environment.NewLine}");
    }

    // The script both pages share: what they hear, and the zoom they are at. `report` is the page's own way to say it.
    private const string Script = """
        const mode = '__MODE__', where = '__WHERE__';
        const say = (kind, text) => { try { fetch(__REPORT__ + kind + '/' + encodeURIComponent(where + ' ' + text)).catch(() => {}); } catch (e) { } };
        const state = () => 'dpr=' + devicePixelRatio + ' iw=' + innerWidth + ' vv=' + (window.visualViewport ? window.visualViewport.scale : '-')
            + ' touch-action=' + getComputedStyle(document.documentElement).touchAction;
        let last = '';
        const tick = () => { const s = state(); if (s !== last) { last = s; say('state', s); } };
        tick(); setInterval(tick, 150);
        if (mode === 'zoom') {
          // Capture and passive, on the window: it hears a wheel the guard's own capture listener (registered first) cancelled
          // as defaultPrevented, and cannot cancel one itself.
          addEventListener('wheel', e => say('wheel', 'ctrl=' + (+e.ctrlKey) + ' dy=' + e.deltaY + ' prevented=' + (+e.defaultPrevented)), { capture: true, passive: true });
          for (const type of ['keydown', 'keypress', 'keyup'])
            addEventListener(type, e => say(type, 'key=' + e.key + ' code=' + e.code + ' ctrl=' + (+e.ctrlKey) + ' keyCode=' + e.keyCode + ' charCode=' + e.charCode), true);
          addEventListener('focus', () => say('focus', 'window'));
          // Where the pointer is, at most twice a second: which of the two documents a wheel was aimed at.
          let moved = 0;
          addEventListener('mousemove', e => { if (performance.now() - moved > 500) { moved = performance.now(); say('pointer', e.clientX + ',' + e.clientY); } }, true);
        }
        say('loaded', state());
        """;

    // The scroll timing, in the main page only: a wheel's arrival to the scroll event it caused, in four phases.
    private const string ScrollScript = """
        if (mode === 'scroll' && where === 'main') {
          const guard = e => { if (e.ctrlKey || e.metaKey) e.preventDefault(); };
          let phase = 0, count = 0, pending = [], latencies = [], frames = [], lastFrame = performance.now();
          const on = () => phase % 2 === 1;
          const pct = (a, p) => a.length ? a.slice().sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * p))].toFixed(1) : 'n/a';
          addEventListener('wheel', () => {
            pending.push(performance.now());
            if (++count < 120) return;
            say('phase', 'guard=' + (on() ? 'on' : 'off') + ' wheels=' + count + ' scrolls=' + latencies.length + ' latency-ms p50=' + pct(latencies, .5) + ' p95=' + pct(latencies, .95)
              + ' max=' + pct(latencies, 1) + ' frame-ms p50=' + pct(frames, .5) + ' p95=' + pct(frames, .95) + ' max=' + pct(frames, 1) + ' scrollY=' + Math.round(scrollY));
            phase++; count = 0; latencies = []; frames = [];
            if (on()) addEventListener('wheel', guard, { capture: true, passive: false }); else removeEventListener('wheel', guard, { capture: true });
            if (phase === 4) say('done', 'scroll');
          }, { capture: true, passive: true });
          addEventListener('scroll', () => { const t = performance.now(); for (const p of pending) latencies.push(t - p); pending = []; }, { passive: true });
          (function frame(t) { frames.push(t - lastFrame); lastFrame = t; requestAnimationFrame(frame); })(performance.now());
        }
        """;

    private static string Html(string where, string report, string body) =>
        "<!doctype html><meta charset=utf-8><title>zoom probe</title>"
        + "<style>html,body{margin:0;background:#202020;color:#ddd;font:14px sans-serif}#tall{height:400000px;background:repeating-linear-gradient(#222 0 40px,#2a2a2a 40px 80px)}iframe{width:480px;height:240px;border:1px solid #555;display:block}</style>"
        + body + "<script>" + (Script + ScrollScript).Replace("__MODE__", Mode!).Replace("__WHERE__", where).Replace("__REPORT__", report) + "</script>";

    private static string MainPage => Html("main", "'report/'",
        $"<div style='height:300px'>zoom probe: the page<iframe src='http://127.0.0.1:{Frame.Value!.Port}/frame'></iframe></div><div id=tall></div>");

    private sealed class ProbeProvider(IWebViewResourceProvider? app) : IWebViewResourceProvider
    {
        public bool Exists(string virtualPath) => virtualPath is "zoom.html" || virtualPath.StartsWith("report/", StringComparison.Ordinal) || app?.Exists(virtualPath) == true;

        public Stream? GetResourceStream(string virtualPath)
        {
            if (virtualPath == "zoom.html") return new MemoryStream(Encoding.UTF8.GetBytes(MainPage));
            if (!virtualPath.StartsWith("report/", StringComparison.Ordinal)) return app?.GetResourceStream(virtualPath);
            var report = virtualPath["report/".Length..].Split('/', 2);
            Log($"{report[0]} {Uri.UnescapeDataString(report.ElementAtOrDefault(1) ?? "")}");
            return new MemoryStream("ok"u8.ToArray());
        }
    }

    /// <summary>The frame of another origin, on loopback: its page, and where it reports.</summary>
    private sealed class FrameServer
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FrameServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private static void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = reader.ReadLine() ?? "";
                    while (reader.ReadLine() is { Length: > 0 }) { }
                    var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                    string body, type = "text/html";
                    if (path.StartsWith("/report/", StringComparison.Ordinal))
                    {
                        var report = path["/report/".Length..].Split('/', 2);
                        Log($"{report[0]} {Uri.UnescapeDataString(report.ElementAtOrDefault(1) ?? "")}");
                        body = "ok"; type = "text/plain";
                    }
                    else body = Html("frame", "'/report/'", "<div style='height:200px;padding:8px'>the frame, another origin</div>");
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    stream.Write(head);
                    stream.Write(bytes);
                }
                catch (IOException) { }
            }
        }
    }
}
