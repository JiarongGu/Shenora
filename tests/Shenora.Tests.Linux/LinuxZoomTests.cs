using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Shenora.Tests.Linux;

/// <summary>
/// The zoom guard (<c>ChromiumZoom</c>) on real CEF, driven by real input: the Chromium sample's <c>ZoomProbe</c> shows a page and
/// a frame of another origin, which report what they hear and what zoom they are at, and this presses Ctrl and wheels over each
/// and Ctrl with plus, minus and 0, through the X server's own input extension (xdotool, under openbox on the private Xvfb the
/// run owns), so the events reach Chromium as a person's would. The page, not the app, says what came of them. The
/// control run allows zoom and must then see the same wheel zoom the page, or a guard that held would prove nothing.
/// </summary>
public class LinuxZoomTests
{
    private static readonly Regex Level = new(@"state (main|frame) dpr=([\d.]+) ", RegexOptions.Compiled);

    [Fact]
    public async Task With_zoom_not_allowed_a_ctrl_wheel_over_the_page_or_a_frame_of_another_origin_and_the_zoom_keys_leave_the_zoom_alone()
    {
        // Chromium keeps a zoom per host in the profile: this run starts from one an allowed run made, in the same app root.
        var root = Path.Combine(Path.GetTempPath(), $"shenora-zoom-{Guid.NewGuid():N}");
        IReadOnlyList<string> log;
        try
        {
            await LeaveZoomedOutAsync(root);
            await AssertKeptAsync(root);
            using var run = Start(allow: false, root);
            await ReadyAsync(run);
            await DriveAsync(run);
            log = run.Log();
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }

        // Real input reached both documents: the wheels were heard, with Ctrl down, by the page and by the frame.
        Assert.True(Heard(log, "wheel main ctrl=1") >= 6, "the page heard too few Ctrl+wheels");
        Assert.True(Heard(log, "wheel frame ctrl=1") >= 3, "the frame heard too few Ctrl+wheels");
        // None zoomed: the zoom the profile held was undone as the page loaded, and never left 1 after it, in either document, and
        // every Ctrl+wheel was cancelled before the page's own listener.
        var firstInput = log.ToList().FindIndex(l => l.Contains("wheel ") || l.Contains("keydown "));
        Assert.All(log.Skip(firstInput).Select(l => Level.Match(l)).Where(m => m.Success), m => Assert.Equal("1", m.Groups[2].Value));
        Assert.Equal("1", LastLevel(log, "main"));
        Assert.Equal("1", LastLevel(log, "frame"));
        Assert.DoesNotContain(log, l => l.Contains("wheel ") && l.Contains("ctrl=1") && l.Contains("prevented=0"));
        // The script reached both documents: each has the guard's touch-action, the frame being a process of its own.
        Assert.Contains(log, l => l.Contains("state main ") && l.Contains("touch-action=pan-x pan-y"));
        Assert.Contains(log, l => l.Contains("state frame ") && l.Contains("touch-action=pan-x pan-y"));
        // The keys: Ctrl itself was heard (the input arrived), and nothing of plus, minus or 0 with it, in any of its events.
        Assert.True(Heard(log, "keydown main key=Control") >= 7 && Heard(log, "keydown frame key=Control") >= 3, "the keys did not reach the page");
        Assert.DoesNotContain(log, l => Regex.IsMatch(l, @"key(down|press|up) (main|frame) key=[+=\-0] code=\w+ ctrl=1"));
        // And the guard is not wider than it says: a wheel and a minus without Ctrl are heard as ever.
        Assert.Contains(log, l => l.Contains("wheel main ctrl=0"));
        Assert.Contains(log, l => l.Contains("wheel frame ctrl=0"));
        Assert.Contains(log, l => l.Contains("keypress") && l.Contains("key=- ") && l.Contains("ctrl=0"));
    }

    [Fact]
    public async Task With_zoom_allowed_the_same_wheels_zoom_the_page_and_the_frame_so_the_probe_can_see_a_zoom()
    {
        using var run = Start(allow: true);
        await ReadyAsync(run);
        await DriveAsync(run);
        var log = run.Log();

        var levels = log.Select(l => Level.Match(l)).Where(m => m.Success)
            .Select(m => (Where: m.Groups[1].Value, Dpr: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        foreach (var where in new[] { "main", "frame" })
            Assert.True(levels.Any(l => l.Where == where && l.Dpr > 1.05), $"the {where} was never zoomed in by its Ctrl+wheel");
        Assert.True(levels.Any(l => l.Where == "main" && l.Dpr < 0.95), "the page was never zoomed out by its Ctrl+wheel");
        Assert.DoesNotContain(log, l => l.Contains("wheel ") && l.Contains("ctrl=1") && l.Contains("prevented=1"));
        // Heard in full: the zoom keys' own key events reach the page when nothing blocks them.
        Assert.Contains(log, l => Regex.IsMatch(l, @"keydown main key=[+=\-0] code=\w+ ctrl=1"));
        Assert.Contains(log, l => l.Contains("state main ") && l.Contains("touch-action=auto"));
    }

    // An allowed run that zooms the page out and ends as a window's close ends it, so the profile keeps the zoom.
    private static async Task LeaveZoomedOutAsync(string root)
    {
        using var run = Start(allow: true, root);
        await ReadyAsync(run);
        var window = await run.WindowAsync();
        var origin = run.Screen.Origin(window);
        await Task.Delay(1500);
        await Input($"mousemove {origin.X + 700} {origin.Y + 500}", "click 1");
        await Input("keydown ctrl", "click 5 click 5 click 5", "keyup ctrl");
        await Task.Delay(800);
        Assert.Contains(run.Log(), l => Level.Match(l) is { Success: true } m && double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < 0.95);
        run.Screen.AskToClose(window);
        await run.ExitsAsync();
    }

    // The zoom is really kept: an allowed run in the same root starts zoomed out, which is what the next run undoes.
    private static async Task AssertKeptAsync(string root)
    {
        using var run = Start(allow: true, root);
        await ReadyAsync(run);
        var first = run.Log().Select(l => Level.Match(l)).First(m => m.Success && m.Groups[1].Value == "main");
        Assert.True(double.Parse(first.Groups[2].Value, CultureInfo.InvariantCulture) < 0.95, $"the page started at {first.Groups[2].Value}: the profile kept no zoom");
        var window = await run.WindowAsync();
        run.Screen.AskToClose(window);
        await run.ExitsAsync();
    }

    private static string LastLevel(IReadOnlyList<string> log, string where) =>
        log.Select(l => Level.Match(l)).Last(m => m.Success && m.Groups[1].Value == where).Groups[2].Value;

    private static int Heard(IReadOnlyList<string> log, string what) => log.Count(l => l.Contains(what));

    private static LinuxWebPanelTests.PanelRun Start(bool allow, string? root = null) =>
        new(Environment.GetEnvironmentVariable("SHENORA_LINUX_SAMPLE"), new Dictionary<string, string>
        {
            ["SHENORA_SAMPLE_ZOOM_PROBE"] = "zoom",
            ["SHENORA_SAMPLE_ZOOM_LOG"] = "{log}",
            ["SHENORA_SAMPLE_ZOOM_ALLOW"] = allow ? "1" : "0",
        }, root);

    // Both documents have loaded and the window is on the display; the first start loads CEF's runtime from wherever the build
    // put it, which on a drive WSL mounts took over a minute.
    private static async Task ReadyAsync(LinuxWebPanelTests.PanelRun run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            var log = run.Log();
            if (log.Any(l => l.Contains("loaded main")) && log.Any(l => l.Contains("loaded frame"))) return;
            if (run.Exited) Assert.Fail($"the app ended before its page and frame loaded:{Environment.NewLine}{run.Describe()}");
            await Task.Delay(200);
        }
        Assert.Fail($"the probe's page and frame did not both load in 4 minutes:{Environment.NewLine}{run.Describe()}");
    }

    // The inputs, one group at a time over the page and then over the frame, a beat between so each lands on its own.
    private static async Task DriveAsync(LinuxWebPanelTests.PanelRun run)
    {
        var window = await run.WindowAsync();
        var origin = run.Screen.Origin(window);
        string Move((int X, int Y) at) => $"mousemove {origin.X + at.X} {origin.Y + at.Y}";
        var page = (X: 700, Y: 500);    // the probe's page: below its frame, in the long body
        var frame = (X: 240, Y: 160);   // inside the frame of another origin, at the page's top left
        await Task.Delay(1500);   // the first frame settles, and the page's own focus with it
        await Input(Move(page), "click 1");
        await Input("keydown ctrl", "click 4 click 4 click 4", "keyup ctrl");
        await Input("keydown ctrl", "click 5 click 5 click 5 click 5 click 5", "keyup ctrl");
        foreach (var keys in new[] { "ctrl+plus", "ctrl+equal", "ctrl+minus", "ctrl+0", "ctrl+KP_Add", "ctrl+KP_Subtract", "ctrl+KP_0" })
            await Input($"key {keys}");
        await Input(Move(frame));
        await Input("keydown ctrl", "click 4 click 4 click 4", "keyup ctrl");
        await Input("keydown ctrl", "click 5 click 5 click 5", "keyup ctrl");
        await Input("click 1");   // into the frame, so its keys are the frame's
        foreach (var keys in new[] { "ctrl+plus", "ctrl+minus", "ctrl+0" })
            await Input($"key {keys}");
        await Input("click 5 click 5");   // the guard is not wider than it says: a wheel without Ctrl scrolls as ever
        await Input(Move(page), "click 5 click 5");
        await Input("key minus");
        await Task.Delay(1200);   // the page's last report
    }

    // One xdotool command line per argument, in order, each waited for; then a beat for the report to arrive.
    private static async Task Input(params string[] commands)
    {
        foreach (var command in commands)
        {
            var info = new ProcessStartInfo("xdotool") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var word in command.Split(' ')) info.ArgumentList.Add(word);
            // `click 4 click 4` is two commands to xdotool and one to this: it takes them as a chain.
            if (Environment.GetEnvironmentVariable("SHENORA_LINUX_SYSROOT") is { Length: > 0 } sysroot)
                info.Environment["LD_LIBRARY_PATH"] = Path.Combine(sysroot, "usr/lib/x86_64-linux-gnu");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("xdotool did not start (see linux-tests.sh's header).");
            var errors = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), $"xdotool {command} did not end in 30 s");
            Assert.True(process.ExitCode == 0, $"xdotool {command} exited {process.ExitCode}: {errors.Result}");
            await Task.Delay(commands.Length > 1 ? 40 : 700);
        }
    }
}
