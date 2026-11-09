using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace Shenora.Tests.Linux;

/// <summary>
/// The Chromium web panel on Linux (D97), run on real CEF: the Chromium sample's <c>PanelProbe</c> run 6 makes two panels
/// over the main window, hides, shows, moves and closes them one step at a time, and between steps this reads the
/// X server's screen from outside, under a window manager (openbox), the way a person looking at it would. The app's own
/// log says only which step it is at; where a panel sits, which is on top and whether it is gone are the screen's.
/// <para>
/// Needs the built sample (<c>SHENORA_LINUX_SAMPLE</c>, its launcher: <c>linux-tests.sh</c> builds and names it), openbox
/// on the display's <c>PATH</c> and a private Xvfb display (<c>dev.mjs linux-tests</c>).
/// </para>
/// </summary>
public class LinuxWebPanelTests
{
    private const string Title = "Shenora Chromium Sample";
    private const int Green = 0x22AA77, Blue = 0x2255CC, Magenta = 0xFF00FF;   // the probe's pages, and the panel's own background
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60), StartLimit = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task A_web_panel_sits_where_it_is_placed_stacks_hides_moves_and_closes_with_its_window()
    {
        using var run = new PanelRun();
        var x = run.Screen;

        // Before any panel: the main page, the baseline every later step is compared with.
        await run.StepAsync("BEFORE");
        var a = run.Bounds("A");
        var b = run.Bounds("B");
        var moved = run.Bounds("A-MOVED");
        var window = await run.WindowAsync();
        var origin = x.Origin(window);
        var client = x.Size(window);
        Assert.True(client.Width >= b.Right && client.Height >= b.Bottom, $"the window's client area {client} is too small for the panels");
        Rectangle OnScreen(Rectangle r) => new(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
        var area = OnScreen(new Rectangle(0, 0, client.Width, client.Height));
        var before = await run.SettledAsync(area);
        Assert.Equal(0, before.Find(Green, area).Count);
        Assert.Equal(0, before.Find(Blue, area).Count);
        run.Answer();

        // A: its page's pixels fill its bounds exactly, and none of its own background (magenta) shows through.
        await run.StepAsync("A");
        var shot = await run.SeeAsync(s => s.Find(Green, area).Box == OnScreen(a), "panel A's page at its bounds");
        Assert.Equal(0, shot.Find(Magenta, area).Count);
        Assert.Equal(OnScreen(a), shot.Find(Green, area).Box);
        run.Answer();

        // B over A: B's bounds are blue, A shows where B does not cover it, and where they meet B is on top (order).
        await run.StepAsync("B");
        shot = await run.SeeAsync(s => s.Find(Blue, area).Box == OnScreen(b), "panel B's page at its bounds");
        var meet = Overlap(OnScreen(a), OnScreen(b));
        Assert.True(Near(shot, Centre(meet), Blue), $"where B meets A, the screen shows {shot.At(Centre(meet).X, Centre(meet).Y):X6}, not B's");
        Assert.True(Near(shot, new Point(OnScreen(a).X + 6, OnScreen(a).Y + 6), Green), "A, outside B");
        Assert.Equal(OnScreen(a), shot.Find(Green, area).Box);   // B covers only A's lower right, so A's extent stands
        run.Answer();

        // B hidden: gone from the screen with A whole beneath it, and nothing else moved.
        await run.StepAsync("B-HIDDEN");
        shot = await run.SeeAsync(s => s.Find(Blue, area).Count == 0, "panel B hidden");
        Assert.True(Near(shot, Centre(meet), Green), "A beneath B once B is hidden");
        run.Answer();

        // B shown again, still on top, where it was.
        await run.StepAsync("B-SHOWN");
        shot = await run.SeeAsync(s => s.Find(Blue, area).Box == OnScreen(b), "panel B shown again");
        Assert.True(Near(shot, Centre(meet), Blue), "B over A again");
        run.Answer();

        // A moved clear of B: its new bounds, and the page it covered back where it was.
        await run.StepAsync("A-MOVED");
        shot = await run.SeeAsync(s => s.Find(Green, area).Box == OnScreen(moved), "panel A at its new bounds");
        Assert.Equal(OnScreen(b), shot.Find(Blue, area).Box);
        Assert.True(Near(shot, Centre(meet), Blue), "B, which A no longer lies under");
        var vacated = new Point(OnScreen(a).Right - 100, OnScreen(a).Y + 20);   // in A's old place, clear of its new one and of B
        Assert.True(OnScreen(a).Contains(vacated) && !OnScreen(moved).Contains(vacated) && !OnScreen(b).Contains(vacated), "the probe's rectangles changed");
        Assert.True(XScreen.Snapshot.Near(before.At(vacated.X, vacated.Y), shot.At(vacated.X, vacated.Y)),
            $"A's old place shows {shot.At(vacated.X, vacated.Y):X6}, the main page showed {before.At(vacated.X, vacated.Y):X6}");
        run.Answer();

        // B closed: nothing of it left, and the page beneath is the baseline's.
        await run.StepAsync("B-CLOSED");
        shot = await run.SeeAsync(s => s.Find(Blue, area).Count == 0, "panel B closed");
        Assert.Equal(OnScreen(moved), shot.Find(Green, area).Box);
        var freed = Centre(OnScreen(b));
        Assert.True(XScreen.Snapshot.Near(before.At(freed.X, freed.Y), shot.At(freed.X, freed.Y)),
            $"B's old place shows {shot.At(freed.X, freed.Y):X6}, the main page showed {before.At(freed.X, freed.Y):X6}");
        run.Answer();

        // The window closed as a window manager closes one, with A still in it: the panel goes with it, and the app ends.
        await run.StepAsync("A-OPEN");
        x.AskToClose(window);
        await run.ExitsAsync();
        Assert.False(x.Exists(window), "the window is still on the display");
        Assert.DoesNotContain(run.Log(), line => line.Contains("THREW"));
    }

    [Fact]
    public void A_run_whose_app_will_not_start_ends_the_window_manager_and_the_display_connection_it_had_started()
    {
        // A file with no execute bit: it exists, so the run gets as far as starting the app, and Process.Start refuses it.
        var launcher = Path.Combine(AppContext.BaseDirectory, $"not-a-launcher-{Guid.NewGuid():N}");
        File.WriteAllText(launcher, "");
        try
        {
            // The first start in a process opens descriptors of the runtime's own (a pipe of its child-exit signal, assemblies), which stay.
            Assert.ThrowsAny<Exception>(() => new PanelRun(launcher));
            var descriptors = Descriptors();
            var folders = Folders();
            for (var i = 0; i < 2; i++)
                Assert.ThrowsAny<Exception>(() => new PanelRun(launcher));

            Assert.Empty(WindowManagers());
            var left = Descriptors().Where(d => !descriptors.ContainsKey(d.Key)).Select(d => $"{d.Key} -> {d.Value}").ToList();
            Assert.True(left.Count == 0, $"descriptors left open: {string.Join(", ", left)}");
            Assert.Equal(folders, Folders());
        }
        finally { File.Delete(launcher); }
    }

    // The descriptors this process holds, by number: the display connection is one.
    private static Dictionary<string, string> Descriptors()
    {
        var all = new Dictionary<string, string>();
        foreach (var entry in Directory.GetFileSystemEntries("/proc/self/fd"))
        {
            try { all[Path.GetFileName(entry)] = File.ResolveLinkTarget(entry, returnFinalTarget: false)?.FullName ?? ""; }
            catch (FileNotFoundException) { }   // the listing's own descriptor, closed by now
        }
        return all;
    }

    // The openbox processes this one started and that are still there (a process that is killed and not yet reaped is still listed).
    private static List<int> WindowManagers()
    {
        var found = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (File.ReadAllText(Path.Combine(dir, "comm")).Trim() == "openbox" && int.Parse(rest[1]) == Environment.ProcessId) found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static string[] Folders() => Directory.GetDirectories(AppContext.BaseDirectory, "panel-run-*");

    private static Rectangle Overlap(Rectangle a, Rectangle b) => Rectangle.Intersect(a, b);

    private static Point Centre(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static bool Near(XScreen.Snapshot shot, Point at, int colour) => XScreen.Snapshot.Near(shot.At(at.X, at.Y), colour);

    /// <summary>One run of the sample: the display's window manager, the app's probe log and standard input, and a data
    /// folder that is the run's own.</summary>
    private sealed class PanelRun : IDisposable
    {
        private readonly Process _wm;
        private readonly Process _app;
        private readonly string _folder;
        private readonly string _log;
        private int _line;

        public PanelRun() : this(Environment.GetEnvironmentVariable("SHENORA_LINUX_SAMPLE")) { }

        public PanelRun(string? launcher)
        {
            if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
                throw new InvalidOperationException("SHENORA_LINUX_SAMPLE names no built Chromium sample (devtools/scripts/linux-tests.sh builds it).");
            // Started in turn, and the test's `using` only disposes a run that was built: a step that throws ends the ones before it.
            XScreen? screen = null;
            Process? wm = null, app = null;
            string? folder = null;
            try
            {
                screen = new XScreen();
                wm = StartWindowManager(screen, _wmOutput);
                folder = Path.Combine(AppContext.BaseDirectory, $"panel-run-{Guid.NewGuid():N}");
                Directory.CreateDirectory(folder);
                _log = Path.Combine(folder, "panel-probe.log");
                File.WriteAllText(_log, "");
                var info = new ProcessStartInfo(launcher)
                {
                    WorkingDirectory = Path.GetDirectoryName(launcher)!,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                // The libraries Chromium needs that the machine lacks (nspr, nss), from the sysroot where there is one.
                if (Environment.GetEnvironmentVariable("SHENORA_LINUX_SYSROOT") is { Length: > 0 } sysroot)
                    info.Environment["LD_LIBRARY_PATH"] = Path.Combine(sysroot, "usr/lib/x86_64-linux-gnu");
                info.Environment["SHENORA_SAMPLE_PANEL_PROBE"] = "6";
                info.Environment["SHENORA_SAMPLE_PANEL_LOG"] = _log;
                app = new Process { StartInfo = info };
                app.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
                app.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
                app.Start();
                app.BeginOutputReadLine();
                app.BeginErrorReadLine();
            }
            catch
            {
                End(app, wm, screen, folder);
                throw;
            }
            Screen = screen;
            _wm = wm;
            _app = app;
            _folder = folder;
        }

        // What the app wrote to its own output, and what the window manager wrote to its standard error, for a failure to say.
        private readonly System.Text.StringBuilder _output = new(), _wmOutput = new();

        private string Account()
        {
            lock (_output)
                return $"{string.Join(Environment.NewLine, Log())}{Environment.NewLine}--- the app's output:{Environment.NewLine}{_output}"
                    + $"--- openbox's standard error:{Environment.NewLine}{_wmOutput}"
                    + $"--- its processes (pid, state, command):{Environment.NewLine}{Processes()}";
        }

        // The app and everything it started, as /proc has them: what a start that never says anything is stuck on.
        private string Processes()
        {
            var all = new Dictionary<int, (int Parent, string State, string Command)>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    var command = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    all[pid] = (int.Parse(rest[1]), rest[0], command.Length > 120 ? command[..120] : command);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var mine = new HashSet<int> { _app.Id };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, p) in all)
                    if (mine.Contains(p.Parent) && mine.Add(pid)) grew = true;
            }
            return string.Join(Environment.NewLine, mine.Where(all.ContainsKey).Order().Select(pid => $"{pid} {all[pid].State} {all[pid].Command}"));
        }

        public XScreen Screen { get; }

        public IReadOnlyList<string> Log()
        {
            using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }

        /// <summary>A rectangle the probe says it will use, from its <c>BOUNDS</c> line (<c>BOUNDS A x,y,w,h B x,y,w,h …</c>),
        /// in the window's client pixels: what was asked for, which the screen then answers for.</summary>
        public Rectangle Bounds(string name)
        {
            var line = Log().FirstOrDefault(l => l.Contains(" BOUNDS "))
                ?? throw new InvalidOperationException("The probe has not said where its panels go.");
            var words = line[(line.IndexOf(" BOUNDS ", StringComparison.Ordinal) + " BOUNDS ".Length)..].Split(' ');
            var parts = words[(Array.IndexOf(words, name) + 1)..][0].Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return new Rectangle(parts[0], parts[1], parts[2], parts[3]);
        }

        /// <summary>Wait for the probe to reach a step; a probe that threw, or an app that ended, fails the test here.</summary>
        public async Task StepAsync(string name)
        {
            // The first step is the app's own start, which loads CEF's runtime (hundreds of MB) from wherever the build put it:
            // on a drive WSL mounts it sat in uninterruptible disk wait for over a minute on a loaded machine (state D below).
            var limit = name == "BEFORE" ? StartLimit : Step;
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                var lines = Log();
                for (; _line < lines.Count; _line++)
                {
                    if (lines[_line].Contains("THREW")) Assert.Fail($"the probe failed before step {name}: {lines[_line]}{Environment.NewLine}{Account()}");
                    if (lines[_line].EndsWith($"STEP {name}", StringComparison.Ordinal)) { _line++; return; }
                }
                if (_app.HasExited) Assert.Fail($"the app ended (exit {_app.ExitCode}) before step {name}:{Environment.NewLine}{Account()}");
                await Task.Delay(100);
            }
            Assert.Fail($"step {name} was not reached in {limit}:{Environment.NewLine}{Account()}");
        }

        /// <summary>Let the probe go on to its next step.</summary>
        public void Answer() => _app.StandardInput.WriteLine();

        /// <summary>The main window, once it is on the display and titled (it appears a moment after the probe starts).</summary>
        public async Task<uint> WindowAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            do
            {
                if (Screen.FindWindow(Title) is var window and not 0) return window;
                await Task.Delay(200);
            } while (DateTime.UtcNow < deadline);
            Assert.Fail($"no window titled '{Title}' on the display:{Environment.NewLine}{Screen.Describe()}");
            return 0;
        }

        /// <summary>The screen once <paramref name="area"/> shows the main page and has stopped changing between samples: the
        /// baseline later steps are compared with. Before its first paint the window is one flat colour of Chromium's own
        /// (measured: light grey, steady for seconds on a slow start), which a page of text and boxes is not.</summary>
        public async Task<XScreen.Snapshot> SettledAsync(Rectangle area)
        {
            // Part of the same slow start as StartLimit: the page paints once its bundle has loaded.
            var deadline = DateTime.UtcNow + StartLimit;
            var last = Screen.Capture();
            bool stable;
            do
            {
                await Task.Delay(400);
                var now = Screen.Capture();
                stable = now.Same(last, area);
                if (stable && now.Colours(area, 9) > 8) return now;
                last = now;
            } while (DateTime.UtcNow < deadline);
            Assert.Fail($"the main page never painted and settled: {last.Colours(area, 9)} colours in its area (a painted page has more than 8), "
                + $"{(stable ? "steady" : "still changing")}{Environment.NewLine}{Account()}");
            return last;
        }

        /// <summary>The screen once <paramref name="holds"/> says it shows what the step should (a page takes a moment to
        /// paint); the last one seen, with a failure naming <paramref name="what"/>, when it never does.</summary>
        public async Task<XScreen.Snapshot> SeeAsync(Func<XScreen.Snapshot, bool> holds, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            XScreen.Snapshot shot;
            do
            {
                shot = Screen.Capture();
                if (holds(shot)) return shot;
                await Task.Delay(200);
            } while (DateTime.UtcNow < deadline);
            Assert.Fail($"the screen never showed {what}");
            return shot;
        }

        /// <summary>The app ends on its own within 20 s; a hang is a failure.</summary>
        public async Task ExitsAsync()
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await _app.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { Assert.Fail("the app did not end within 20 s of its window's close"); }
        }

        public void Dispose() => End(_app, _wm, Screen, _folder);

        // What a run started, ended, whichever of it was started: the window manager is waited for, so it is gone when this returns.
        private static void End(Process? app, Process? wm, XScreen? screen, string? folder)
        {
            if (app is not null)
            {
                try { if (!app.HasExited) app.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }   // never started, or ended meanwhile
                app.Dispose();
            }
            if (wm is not null)
            {
                try
                {
                    if (!wm.HasExited) wm.Kill(entireProcessTree: true);
                    wm.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException) { }
                wm.Dispose();
            }
            screen?.Dispose();
            if (folder is not null)
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        // openbox, started on this display and waited for until it has taken the root window: a window manager's frame
        // and its stacking are the display a person has. Its libraries come from the sysroot where there is no root to
        // install them (linux-tests.sh).
        // Its standard error is read as it runs, into errors: a window manager that wrote enough would block on a full pipe.
        private static Process StartWindowManager(XScreen screen, System.Text.StringBuilder errors)
        {
            var info = new ProcessStartInfo("openbox") { UseShellExecute = false, RedirectStandardError = true };
            if (Environment.GetEnvironmentVariable("SHENORA_LINUX_SYSROOT") is { Length: > 0 } sysroot)
            {
                info.Environment["LD_LIBRARY_PATH"] = Path.Combine(sysroot, "usr/lib/x86_64-linux-gnu");
                info.Environment["XDG_CONFIG_DIRS"] = Path.Combine(sysroot, "etc/xdg");   // its rc.xml
                info.Environment["XDG_DATA_DIRS"] = Path.Combine(sysroot, "usr/share");   // its themes
            }
            info.ArgumentList.Add("--sm-disable");
            Process wm;
            try { wm = Process.Start(info)!; }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("openbox is not installed (see linux-tests.sh's header).", ex); }
            wm.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
            wm.BeginErrorReadLine();
            string Said() { lock (errors) return errors.ToString(); }
            for (var i = 0; i < 100; i++)
            {
                if (screen.HasWindowManager) return wm;
                if (wm.HasExited)
                {
                    wm.WaitForExit();   // its standard error to the end
                    wm.Dispose();
                    throw new InvalidOperationException($"openbox exited at once: {Said()}");
                }
                Thread.Sleep(100);
            }
            wm.Kill();
            wm.WaitForExit();
            wm.Dispose();
            throw new InvalidOperationException($"openbox did not take the display in 10 s: {Said()}");
        }
    }
}
