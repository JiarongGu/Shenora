using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Shell;
using Shenora.Core.WebView;

namespace Shenora.Sample.Chromium;

/// <summary>
/// The app's hooks on a page's downloads, permissions and renderer failure, run for real rather than unit-tested:
/// <c>SHENORA_SAMPLE_HOOKS_PROBE=1</c> or <c>2</c> makes the main window show a page of the probe's own, which downloads three
/// files (a click posted to the window starts each), asks for notifications and for the position, and is then left without its renderer (killed from outside, since
/// the page cannot crash itself). <c>HOOKS:</c> lines go to <c>SHENORA_SAMPLE_HOOKS_LOG</c> (default: beside the app), and the
/// app closes. In <c>1</c> the renderer hook answers "handled", so the page is NOT reloaded; in <c>2</c> it only observes, so
/// the shell's own reload runs and the page loads a second time.
/// <list type="bullet">
/// <item><c>yes.txt</c> — the download hook names a path: the file must be there afterwards, with the page's content, and
/// <c>PROGRESS</c> lines must end in <c>Complete</c> (a cancelled download ends in <c>Canceled</c>). With
/// <c>SHENORA_SAMPLE_HOOKS_DOWNLOAD</c> set to <c>pause</c> or <c>cancel</c> it arrives slowly (about 5 s, no announced size)
/// and the progress hook acts on it at its first bytes: <c>pause</c> pauses it and resumes it 2.5 s later, so the lines must
/// show <c>paused</c> and then <c>Complete</c> with the whole file; <c>cancel</c> cancels it, so they must end in
/// <c>Canceled</c>.</item>
/// <item><c>no.txt</c> — handled with no path: cancelled. <c>decline.txt</c> — not handled: the default policy cancels it.
/// Neither file may exist.</item>
/// <item>notifications — the permission hook allows them, so the page's promise reads <c>granted</c>; the position — the hook
/// declines, so the default denies it (code 1).</item>
/// </list>
/// Windows only, since the renderer is killed through PowerShell. Nothing outside the app is touched but its own renderer
/// processes, found by their command line.
/// </summary>
internal static class HooksProbe
{
    private static readonly bool On = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") is "1" or "2";
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "hooks-probe-downloads");
    private static readonly string? LogPath = On
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_LOG") ?? Path.Combine(AppContext.BaseDirectory, "hooks-probe.log")
        : null;
    private static readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _loads;

    /// <summary>The probe is running.</summary>
    public static bool Active => On;

    /// <summary>The page the main window shows: the probe's own while it runs, else the one given.</summary>
    public static string Page(string normal) => On ? "hooks.html" : normal;

    /// <summary>The app's provider, answering the probe's page and its reports first while it runs.</summary>
    public static IWebViewResourceProvider Provider(IWebViewResourceProvider app) => On ? new ProbeProvider(app) : app;

    public static Func<ChromiumDownloadRequest, bool>? OnDownloadStarting => !On ? null : download =>
    {
        Log($"DOWNLOAD hook: {download.SuggestedName} ({download.MimeType}) from {download.Url}");
        switch (download.SuggestedName)
        {
            case "yes.txt": download.ResultFilePath = Path.Combine(Folder, "yes.txt"); return true;
            case "no.txt": return true;
            default: download.ResultFilePath = Path.Combine(Folder, download.SuggestedName); return false;   // declined: a path set is ignored
        }
    };

    private static readonly string? SlowMode = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DOWNLOAD") switch { "pause" => "pause", "cancel" => "cancel", _ => null };
    private static int _acted;

    public static Action<ChromiumDownloadProgress>? OnDownloadProgress => !On ? null : progress =>
    {
        Log($"PROGRESS {Path.GetFileName(progress.FilePath)}: {progress.State}{(progress.IsPaused ? " paused" : "")} {progress.ReceivedBytes}/{progress.TotalBytes} (id {progress.Id})");
        if (SlowMode is null || !progress.FilePath.EndsWith("yes.txt") || progress.ReceivedBytes == 0 || Interlocked.Exchange(ref _acted, 1) == 1) return;
        Log($"ACT {SlowMode} on id {progress.Id}");
        if (SlowMode == "cancel") { progress.Cancel(); return; }
        progress.Pause();
        _ = Task.Delay(2500).ContinueWith(_ => { Log($"ACT resume on id {progress.Id}"); progress.Resume(); });
    };

    public static Func<ChromiumPermissionRequest, bool>? OnPermissionRequested => !On ? null : request =>
    {
        Log($"PERMISSION hook: {request.Kinds} from {request.Origin}");
        if (request.Kinds is not (ChromiumPermissionKind.Notifications or ChromiumPermissionKind.MultipleDownloads)) return false;
        request.Allow = true;
        return true;
    };

    public static Func<ChromiumProcessReport, bool>? OnProcessFailed => !On ? null : failure =>
    {
        Log($"RENDERER hook: {failure.Status} ({failure.ErrorCode}); answering {(Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") == "1" ? "handled" : "observed")}");
        return Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") == "1";
    };

    /// <summary>Start the probe if the environment asks for it; otherwise nothing.</summary>
    public static void StartIfAsked(ShenoraApplication app)
    {
        if (!On) return;
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        Directory.CreateDirectory(Folder);
        _ = Task.Run(async () =>
        {
            var windows = app.Services.GetRequiredService<ChromiumWindows>();
            try { await RunAsync(); }
            catch (Exception ex) { Log($"THREW {ex.GetType().Name}: {ex.Message}"); }
            finally { windows.Close(ChromiumWindows.MainWindowName); }
        });
    }

    private static async Task RunAsync()
    {
        var hwnd = await MainWindowAsync();
        await Task.WhenAny(Ready.Task, Task.Delay(20_000));
        GetClientRect(hwnd, out var client);
        var center = (client.Bottom / 2 << 16) | (client.Right / 2 & 0xFFFF);
        for (var i = 0; i < 3; i++)
        {
            PostMessageW(hwnd, WmMouseMove, 0, center);
            PostMessageW(hwnd, WmLButtonDown, MkLButton, center);
            PostMessageW(hwnd, WmLButtonUp, 0, center);
            await Task.Delay(1500);
        }
        var finished = await Task.WhenAny(Done.Task, Task.Delay(30_000)) == Done.Task;
        await Task.Delay(1500);   // a download's file lands a moment after its hook answered
        Log($"PAGE finished: {finished}");
        foreach (var name in new[] { "yes.txt", "no.txt", "decline.txt" })
        {
            var path = Path.Combine(Folder, name);
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            Log($"FILE {name}: {(text is null ? "absent" : $"saved, holds '{text[..Math.Min(40, text.Length)]}' ({text.Length} chars)")}");
        }
        var before = _loads;
        var killed = KillRenderers();
        Log($"RENDERERS killed: {killed}");
        await Task.Delay(6000);
        Log($"RELOAD after the renderer went: page loaded {_loads - before} more time(s)");
    }

    // The shell's own renderer processes: those of this app's exe started with --type=renderer.
    private static int KillRenderers()
    {
        var exe = Path.GetFileName(Environment.ProcessPath!);
        var script = $"$n=0; Get-CimInstance Win32_Process -Filter \"Name='{exe}'\" | Where-Object {{ $_.CommandLine -match '--type=renderer' }} | "
            + "ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $n++ }; $n";
        using var process = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-Command", script])
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return int.TryParse(output, out var n) ? n : -1;
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

    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const nint MkLButton = 0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct RectStruct { public int Left, Top, Right, Bottom; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out RectStruct rect);
    [DllImport("user32")] private static extern bool PostMessageW(nint hwnd, uint message, nint w, nint l);

    private static void Log(string line)
    {
        if (LogPath is null) return;
        lock (Done) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} HOOKS: {line}{Environment.NewLine}");
    }

    private const string PageHtml = """
        <!doctype html><meta charset="utf-8"><title>hooks probe</title>
        <script>
        const say = (key, value) => fetch('/report/' + key + '/' + encodeURIComponent(value)).then(r => r.text()).catch(() => '');
        (async () => {
          // Only the first load runs the steps: a load after the renderer went has none to run again.
          if (await say('loaded', Date.now()) !== '1') return;
          // One link over the whole page, which the probe clicks three times (a download needs a user's click): each click
          // points it at the next file.
          const names = ['yes.txt', 'no.txt', 'decline.txt'];
          const link = document.createElement('a');
          link.style.cssText = 'position:fixed;inset:0;background:#246';
          const next = i => { link.href = '/dl/' + names[i]; link.download = names[i]; };
          next(0);
          document.body.append(link);
          let clicks = 0;
          const finished = new Promise(resolve => link.addEventListener('click', e => { say('click', clicks + 1 + ' trusted=' + e.isTrusted + ' focus=' + document.hasFocus() + ' ' + document.visibilityState); setTimeout(() => {
            if (++clicks === names.length) resolve(); else next(clicks);
          }, 100); }));
          say('ready', '1');
          await finished;
          say('notification', await Notification.requestPermission());
          await new Promise(resolve => navigator.geolocation.getCurrentPosition(
            () => { say('geolocation', 'granted'); resolve(); }, e => { say('geolocation', 'denied ' + e.code); resolve(); }, { timeout: 5000 }));
          say('done', '1');
        })();
        </script>
        """;

    /// <summary>A body that arrives in 12 chunks, 400 ms apart, with no announced length: a download long enough to be paused.</summary>
    private sealed class SlowBody : Stream
    {
        private const int Chunks = 12, ChunkSize = 16 * 1024;
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent >= Chunks) return 0;
            if (_sent > 0) await Task.Delay(400, cancellationToken);
            var count = Math.Min(buffer.Length, ChunkSize);
            buffer.Span[..count].Fill((byte)'x');
            if (_sent == 0) Encoding.UTF8.GetBytes("downloaded yes.txt\n").CopyTo(buffer.Span);
            _sent++;
            return count;
        }
    }

    private sealed class ProbeProvider(IWebViewResourceProvider app) : IWebViewResourceProvider
    {
        public bool Exists(string virtualPath) => virtualPath is "hooks.html" || virtualPath.StartsWith("dl/", StringComparison.Ordinal)
            || virtualPath.StartsWith("report/", StringComparison.Ordinal) || app.Exists(virtualPath);

        public Stream? GetResourceStream(string virtualPath)
        {
            if (virtualPath == "hooks.html") return new MemoryStream(Encoding.UTF8.GetBytes(PageHtml));
            if (virtualPath.StartsWith("dl/", StringComparison.Ordinal))
            {
                Log($"PAGE asked for {virtualPath}");
                return SlowMode is not null && virtualPath == "dl/yes.txt" ? new SlowBody() : new MemoryStream(Encoding.UTF8.GetBytes($"downloaded {virtualPath[3..]}"));
            }
            if (!virtualPath.StartsWith("report/", StringComparison.Ordinal)) return app.GetResourceStream(virtualPath);
            var report = virtualPath["report/".Length..].Split('/');
            var count = report[0] == "loaded" ? Interlocked.Increment(ref _loads) : 0;
            Log($"PAGE {report[0]}: {Uri.UnescapeDataString(report.ElementAtOrDefault(1) ?? "")}");
            if (report[0] == "ready") Ready.TrySetResult();
            if (report[0] == "done") Done.TrySetResult();
            return new MemoryStream(Encoding.UTF8.GetBytes(count.ToString()));
        }
    }
}
