using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Sample.Chromium;

/// <summary>
/// What the engine's Chromium can decode, asked of a real page: <c>SHENORA_SAMPLE_CODEC_PROBE=1</c> opens a web panel on a
/// page that plays <c>probe-media/</c>'s H.264 + AAC, HEVC and VP9 (the control) clips, a second of each, muted, writes
/// <c>CODEC:</c> lines to <c>SHENORA_SAMPLE_CODEC_LOG</c> (default: beside the app), and closes the app. The last line is
/// <c>CODEC: RESULT PASS</c>, or <c>CODEC: RESULT FAIL</c> and the clips that did not play.
/// <para>
/// A clip counts as played only when its <c>currentTime</c> advanced and a picture came out: the answer a page gets from
/// <c>canPlayType</c> is reported beside it, never taken for it. CEF's standard build, which carries none of the proprietary
/// codecs, fails both. HEVC is decoded by the machine's own decoder (a GPU, or Windows' HEVC extension), so a FAIL on it
/// alone names the machine as much as the build; the clip is 320x240 because a GPU's decoder refuses a smaller picture
/// (measured: 128x96 failed on a GeForce with "GetVideoDecoderConfigCount failed", 320x240 played). The <c>graphics:</c>
/// line names the GPU path the page was drawn on. The runner is <c>dev.mjs chromium-codecs</c>.
/// </para>
/// Windows only: it has not been run on another OS. Nothing outside the app is touched; the page comes from a server on localhost that lives
/// as long as the probe.
/// </summary>
internal static class CodecProbe
{
    private static string? _log;
    private static IUiDispatcher _ui = null!;

    /// <summary>The clips the page plays: its name, the file, the MIME type a page would ask <c>canPlayType</c> about, and
    /// whether the result needs it to play. The control (VP9, which CEF's standard build decodes) is what shows a FAIL on the
    /// others is the codec's and not the probe's.</summary>
    private static readonly (string Name, string File, string Type, bool Required)[] Clips =
    [
        ("h264-aac", "h264-aac.mp4", "video/mp4; codecs=\"avc1.42E01E, mp4a.40.2\"", true),
        ("hevc", "hevc.mp4", "video/mp4; codecs=\"hvc1.1.6.L93.B0\"", true),
        ("vp9-control", "vp9.webm", "video/webm; codecs=\"vp9\"", false),
    ];

    /// <summary>Start the probe if the environment asks for it; otherwise nothing.</summary>
    public static void StartIfAsked(ShenoraApplication app)
    {
        if (Environment.GetEnvironmentVariable("SHENORA_SAMPLE_CODEC_PROBE") != "1" || !OperatingSystem.IsWindows()) return;
        _log = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_CODEC_LOG") ?? Path.Combine(AppContext.BaseDirectory, "codec-probe.log");
        _ = Task.Run(async () =>
        {
            var windows = app.Services.GetRequiredService<ChromiumWindows>();
            var sessions = app.Services.GetRequiredService<ChromiumSessionHost>();
            _ui = sessions.Ui;   // ISessionBrowser runs on the shell's UI thread, and CEF refuses DevTools from any other
            try { await RunAsync(windows, sessions); }
            catch (Exception ex) { Log($"THREW {ex.GetType().Name}: {ex.Message}"); Log("RESULT FAIL the probe did not finish"); }
            finally { windows.Close(ChromiumWindows.MainWindowName); }
        });
    }

    private static async Task RunAsync(ChromiumWindows windows, ChromiumSessionHost sessions)
    {
        using var server = MediaServer.Start();
        using var panel = await windows.CreateWebPanelAsync(ChromiumWindows.MainWindowName, new ChromiumWebPanelOptions
        {
            Session = new SessionBrowserOptions
            {
                ProfileDirectory = InteractiveSession.ComposeProfileDirectory(sessions.ProfilesDirectory, "codec-probe"),
            },
            Url = server.Origin,
            Bounds = new System.Drawing.Rectangle(40, 40, 400, 300),
        });

        // The page sets window.codecs once every clip has played or failed; a script is refused while the page is still
        // being committed, so a refusal is retried rather than reported.
        string? answer = null;
        for (var i = 0; i < 150 && answer is null or "null" or "undefined"; i++)
        {
            await Task.Delay(200);
            try { answer = await _ui.InvokeAsync(() => panel.Browser.ExecuteScriptAsync("window.codecs || null")); }
            catch (InvalidOperationException) { answer = null; }
        }
        if (answer is null or "null") { Log("RESULT FAIL the page reported nothing in 30 s"); return; }

        // ExecuteScriptAsync answers the JSON of the string the page built, so it is a JSON string holding JSON.
        var results = System.Text.Json.JsonSerializer.Deserialize<string>(answer)!;
        using var doc = System.Text.Json.JsonDocument.Parse(results);
        Log($"graphics: {await _ui.InvokeAsync(() => panel.Browser.ExecuteScriptAsync("window.gpuName"))}");
        var failed = new List<string>();
        var controlFailed = false;
        foreach (var clip in doc.RootElement.EnumerateArray())
        {
            var name = clip.GetProperty("name").GetString()!;
            var played = clip.GetProperty("played").GetBoolean();
            Log($"{name}: canPlayType='{clip.GetProperty("canPlayType").GetString()}', MediaSource={clip.GetProperty("mse").GetBoolean()}, "
                + $"{(played ? "PLAYED" : "NOT PLAYED")} ({clip.GetProperty("detail").GetString()})");
            if (!played && Clips.First(c => c.Name == name).Required) failed.Add(name);
            if (!played && !Clips.First(c => c.Name == name).Required) controlFailed = true;
        }
        if (controlFailed) Log("RESULT FAIL the control clip did not play either, so the probe itself is not working");
        else Log(failed.Count == 0 ? "RESULT PASS" : $"RESULT FAIL {string.Join(",", failed)}");
    }

    private static void Log(string line)
    {
        if (_log is null) return;
        lock (Clips) File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} CODEC: {line}{Environment.NewLine}");
    }

    /// <summary>The probe page, and the clips from <c>probe-media/</c> beside the app.</summary>
    private sealed class MediaServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Origin { get; private init; } = "";

        public static MediaServer Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new MediaServer { Origin = $"http://localhost:{port}/" };
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
                var name = (context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
                var clip = Clips.FirstOrDefault(c => c.File == name);
                byte[] body;
                if (name.Length == 0)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    body = Encoding.UTF8.GetBytes(Page());
                }
                else if (clip.File is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, "probe-media", name)))
                {
                    context.Response.ContentType = "video/mp4";
                    body = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "probe-media", name));
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        // Each clip is asked of the page's own APIs and then played, muted (autoplay allows it), for up to 8 s.
        private static string Page()
        {
            var clips = string.Join(",", Clips.Select(c => $"{{name:'{c.Name}',file:'{c.File}',type:'{c.Type.Replace("'", "\\'")}'}}"));
            return $$"""
                <!doctype html><html><body style="margin:0;background:#222"><script>
                const clips = [{{clips}}];
                const results = [];
                async function play(clip) {
                  const video = document.createElement('video');
                  video.muted = true; video.preload = 'auto';
                  document.body.append(video);
                  const result = {
                    name: clip.name,
                    canPlayType: video.canPlayType(clip.type),
                    mse: !!(window.MediaSource && MediaSource.isTypeSupported(clip.type)),
                    played: false, detail: ''
                  };
                  const done = new Promise(resolve => {
                    video.onerror = () => { result.detail = 'error ' + (video.error && video.error.code) + ': ' + (video.error && video.error.message); resolve(); };
                    video.ontimeupdate = () => {
                      if (video.currentTime > 0 && video.videoWidth > 0 && video.videoHeight > 0) {
                        result.played = true; result.detail = 'currentTime ' + video.currentTime.toFixed(2); resolve();
                      }
                    };
                    setTimeout(() => { if (!result.detail) result.detail = 'no frame in 8 s, readyState ' + video.readyState; resolve(); }, 8000);
                  });
                  video.src = clip.file;
                  video.play().catch(e => { if (!result.detail) result.detail = 'play() refused: ' + e.name; });
                  await done;
                  return result;
                }
                function gpuName() {
                  try {
                    const gl = document.createElement('canvas').getContext('webgl');
                    const info = gl.getExtension('WEBGL_debug_renderer_info');
                    return info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : '(no renderer info)';
                  } catch (e) { return 'webgl: ' + e.name; }
                }
                (async () => { for (const clip of clips) results.push(await play(clip)); window.gpuName = gpuName(); window.codecs = JSON.stringify(results); })();
                </script></body></html>
                """;
        }

        public void Dispose() => _listener.Close();
    }
}
