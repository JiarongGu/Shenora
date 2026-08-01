// ui-responsiveness — measures whether the target app's UI thread keeps PUMPING while a route
// runs. This is the probe behind D23's one-way-IPC claim (docs/DECISIONS.md): work left
// in a route's synchronous segment stalls the UI thread; work handed off and streamed back does not.
// Rebuilt for Task 8 (2026-08-01) — the v0.1.0 probe was a one-off shell session, never kept under
// devtools/, so only its NUMBERS survived in the doc. This is the tracked, re-runnable replacement,
// wired into `node devtools/dev.mjs responsiveness`.
//
// Technique (unchanged from v0.1.0, now scripted instead of hand-run):
//   SendMessageTimeout(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeoutMs, out _)
// returns NONZERO only once the target thread's message loop actually PUMPS that message, so a
// ZERO return (the call "failed") means the thread did not pump within the timeout — i.e. it is
// genuinely busy, not merely slow to reply. WM_NULL is chosen because it is a documented no-op: the
// probe measures pump latency, not application behaviour.
//
// Four vacuous readings are guarded against (two shipped in v0.1.0, two found in Task 8 review):
//   1. The app never actually launched / the click never arrived — a run that measured NOTHING
//      still printed "0 unresponsive", indistinguishable from a genuine pass. Guarded by requiring
//      a live process with a real main window AND a baseline sample BEFORE the click proving the
//      thread pumps at all.
//   2. Sampling too coarse (~1 s) to resolve a 3 s freeze. Guarded by sub-100ms interval AND
//      sub-100ms per-sample timeout by default (both are overridable, but neither defaults coarse).
//   3. THE CLICK LANDED SOMEWHERE, but not on the thing that starts the work. `win-input` reports
//      "click ok" for ANY coordinate that resolves to SOME leaf window under the point — for a
//      WebView2 host that leaf is the render surface, which spans the WHOLE client area. So stale
//      fraction coordinates, a moved button, or a disabled control all still produce a landed click
//      and a clean "0 unresponsive" result that measured nothing real — indistinguishable from the
//      streamed shape genuinely working. Guarded by requiring INDEPENDENT evidence the operation
//      itself started: the target window's TITLE (`GetWindowText`, --title-contains) is polled for
//      a marker substring after the click, with its own timeout. This is why `--title-contains`'s
//      default matches `SampleFacade.RunningTitleMarker` exactly — see that constant's doc for why
//      the title is set BEFORE the slow work begins in BOTH shapes (so it survives the very freeze
//      `block` mode measures) and why a window's title specifically survives being read from another
//      process even while the owning thread is hung (the same mechanism that lets Task Manager show
//      a hung app's title).
//   4. A STALE marker from a PRIOR run's operation (one that never finished/reset, e.g. this same
//      process probed twice back to back) would make guard 3's check pass instantly for a click
//      that did nothing new — the false-positive twin of #3. Guarded by checking the title does NOT
//      already contain the marker BEFORE the click.
//
// RESIDUAL LIMIT, named rather than hidden: this guard proves *some* operation matching the title
// marker started at roughly the right time — it does not itself distinguish WHICH button was
// clicked if an app had several concurrently-running operations sharing one marker. For this sample
// there is exactly one SLOW route behind both buttons, so that ambiguity does not arise in practice,
// but a consumer adapting this probe to a busier app should use a more specific marker per operation
// kind if it needs to. This guard also requires the app under test to COOPERATE with the convention
// (set its own title) — it is not a fully app-agnostic mechanism, unlike guards 1/2/3.
//
// Usage:
//   ui-responsiveness <fx> <fy> --win-input <path-to-win-input.exe> [options]
//     --proc <Name>          target process (else the DEVTOOL_PROC env var, same convention as win-input)
//     --duration <ms>        total sampling window after the click (default 4000)
//     --interval <ms>        target gap between samples while responsive (default 50)
//     --timeout <ms>         SendMessageTimeout's own per-sample timeout (default 50)
//     --title-contains <s>   substring the window title must show after the click, proving the
//                            operation actually started (default "SLOW running")
//     --start-timeout <ms>   how long to poll for --title-contains before refusing (default 1500)
//     --start-poll <ms>      polling interval for --start-timeout (default 20)
//     --label <text>         free-text tag echoed in the result line (e.g. "block" / "stream")
//     --mode <text>          alias for --label (reads naturally next to the SLOW route's own `mode`
//                            payload field; this tool does not speak IPC — it only clicks + samples)

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class UiResponsiveness
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    // Plain GetWindowText, NOT SendMessage(WM_GETTEXT): for a standard top-level window (this app's
    // Form never overrides WM_GETTEXT) Windows caches the caption in shared, cross-process-readable
    // state that this reads directly — it does not require the owning thread's message loop to be
    // pumping, which is exactly why it still works during a `block`-mode freeze (see the guard-4
    // comment above and RunningTitleMarker's doc in SampleFacade.cs).
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    private const uint WM_NULL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static int Main(string[] args)
    {
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: ui-responsiveness <fx> <fy> --win-input <path> [--proc N] "
                + "[--duration ms] [--interval ms] [--timeout ms] [--title-contains text] "
                + "[--start-timeout ms] [--start-poll ms] [--label text] [--mode block|stream]");
            return 2;
        }

        string fx = args[0], fy = args[1];
        string proc = ArgVal(args, "--proc") ?? Environment.GetEnvironmentVariable("DEVTOOL_PROC") ?? "";
        string? winInput = ArgVal(args, "--win-input");
        int durationMs = IntVal(args, "--duration", 4000);
        int intervalMs = IntVal(args, "--interval", 50);
        int timeoutMs = IntVal(args, "--timeout", 50);
        string titleContains = ArgVal(args, "--title-contains") ?? "SLOW running";
        int startTimeoutMs = IntVal(args, "--start-timeout", 1500);
        int startPollMs = IntVal(args, "--start-poll", 20);
        string label = ArgVal(args, "--label") ?? ArgVal(args, "--mode") ?? "run";

        if (proc.Length == 0)
        {
            Console.Error.WriteLine("ui-responsiveness: no target - pass --proc <Name> or set DEVTOOL_PROC");
            return 2;
        }
        if (string.IsNullOrEmpty(winInput) || !System.IO.File.Exists(winInput))
        {
            Console.Error.WriteLine($"ui-responsiveness: --win-input <path> is required and must exist (got '{winInput}')");
            return 2;
        }

        // ---- Guard 1: a live process with a REAL main window. The v0.1.0 failure this fixes: a
        // launch that failed left nothing to click, and nothing downstream noticed.
        var (hwnd, pid) = FindMainWindow(proc);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"ui-responsiveness: REFUSING to report - no process named '{proc}' "
                + "with a visible main window was found after retrying. The app may not have launched.");
            return 3;
        }
        Console.WriteLine($"ui-responsiveness: target pid={pid} hwnd=0x{hwnd.ToInt64():X}");

        // ---- Guard 1b: the title marker (guard 4, below) must not ALREADY be present before this
        // click. Otherwise a stale "running" title left over from a prior invocation (e.g. this same
        // process, probed twice back to back) would make guard 4 report "started" for a click that
        // did nothing — the false-positive twin of the vacuous pass this whole probe exists to catch.
        var titleBefore = WindowText(hwnd);
        if (titleBefore.Contains(titleContains, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"ui-responsiveness: REFUSING to report - the window title already "
                + $"contains '{titleContains}' BEFORE this click ('{titleBefore}'). A prior run's "
                + "operation may not have finished/reset yet; wait for the title to read plain "
                + "'Shenora Sample' and re-run.");
            return 3;
        }

        // ---- Guard 2: the thread pumps at ALL before we touch it. Catches a process that exists
        // but is already stuck for an unrelated reason (e.g. still inside startup init).
        var baseline = Sample(hwnd, Math.Max(timeoutMs, 300));
        if (!baseline)
        {
            Console.Error.WriteLine("ui-responsiveness: REFUSING to report - the target's UI thread was "
                + "already unresponsive BEFORE the click; the measurement below would not mean what it claims to.");
            return 3;
        }
        Console.WriteLine("ui-responsiveness: baseline OK (thread pumps before the click)");

        // ---- Guard 3: drive the click through win-input and require ITS OWN landing confirmation.
        // win-input resolves the LEAF control under the point (occlusion/z-order independent) and
        // posts the click there — its stdout line is the proof a real control was found and messaged,
        // not just that a process exists.
        var click = RunWinInput(winInput!, fx, fy, proc);
        if (click.ExitCode != 0 || !click.Stdout.Contains("click ok on hwnd=0x", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ui-responsiveness: REFUSING to report - the click did not land "
                + $"(win-input exit={click.ExitCode}).");
            if (click.Stdout.Length > 0) Console.Error.WriteLine("  win-input stdout: " + click.Stdout.Trim());
            if (click.Stderr.Length > 0) Console.Error.WriteLine("  win-input stderr: " + click.Stderr.Trim());
            return 3;
        }
        foreach (var line in click.Stdout.Split('\n'))
            if (line.Contains("leaf=", StringComparison.Ordinal) || line.Contains("click ok", StringComparison.Ordinal))
                Console.WriteLine("ui-responsiveness: " + line.Trim());

        // ---- Guard 4: INDEPENDENT evidence the operation itself started, not just that a click
        // landed somewhere. See the header comment for why the click alone is not sufficient (a
        // WebView2 render surface spans the whole client area, so a stale button position still
        // "lands" on it) and why the window TITLE survives being read here even through a `block`
        // freeze. Polls rather than checking once: the click is a POSTed message, so the app needs
        // a moment to actually process it (renderer -> IPC -> host dispatch) before the title moves.
        var titleSw = Stopwatch.StartNew();
        string lastTitle = "";
        bool started = false;
        while (titleSw.ElapsedMilliseconds < startTimeoutMs)
        {
            lastTitle = WindowText(hwnd);
            if (lastTitle.Contains(titleContains, StringComparison.Ordinal)) { started = true; break; }
            Thread.Sleep(startPollMs);
        }
        if (!started)
        {
            Console.Error.WriteLine($"ui-responsiveness: REFUSING to report - the click landed on a "
                + $"control, but the operation never appeared to start: the window title did not "
                + $"contain '{titleContains}' within {startTimeoutMs}ms after the click. "
                + $"Last observed title: '{lastTitle}'. The click may have hit the wrong control "
                + "(stale --fx/--fy, a moved button, a disabled control) rather than the SLOW button.");
            return 3;
        }
        Console.WriteLine($"ui-responsiveness: operation confirmed started after {titleSw.ElapsedMilliseconds}ms "
            + $"(title: '{lastTitle}')");

        // ---- The measurement. Sub-100ms cadence AND sub-100ms per-sample timeout by default, so a
        // multi-second freeze is resolved rather than averaged away (v0.1.0's ~1s-interval mistake).
        Console.WriteLine($"ui-responsiveness: sampling {durationMs} ms (interval<={intervalMs}ms, "
            + $"per-sample timeout={timeoutMs}ms)...");
        var sw = Stopwatch.StartNew();
        int samples = 0, unresponsive = 0;
        long longestStallMs = 0;
        long? stallStartMs = null;

        while (sw.ElapsedMilliseconds < durationMs)
        {
            long before = sw.ElapsedMilliseconds;
            bool responsive = Sample(hwnd, timeoutMs);
            long after = sw.ElapsedMilliseconds;
            samples++;

            if (!responsive)
            {
                unresponsive++;
                stallStartMs ??= before;
            }
            else
            {
                if (stallStartMs.HasValue)
                {
                    longestStallMs = Math.Max(longestStallMs, before - stallStartMs.Value);
                    stallStartMs = null;
                }
                // Only sleep the remainder when the call itself returned fast — while genuinely
                // hung, SendMessageTimeout already blocks up to timeoutMs, giving natural
                // back-to-back sampling through the stall with no added gap.
                int remaining = intervalMs - (int)(after - before);
                if (remaining > 0) Thread.Sleep(remaining);
            }
        }
        if (stallStartMs.HasValue) longestStallMs = Math.Max(longestStallMs, sw.ElapsedMilliseconds - stallStartMs.Value);

        Console.WriteLine($"ui-responsiveness: RESULT label={label} samples={samples} "
            + $"unresponsive={unresponsive} longestStallMs={longestStallMs}");
        return 0;
    }

    /// <summary>One WM_NULL round-trip. True = the thread pumped within <paramref name="timeoutMs"/>.</summary>
    private static bool Sample(IntPtr hwnd, int timeoutMs)
        => SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, (uint)timeoutMs, out _) != IntPtr.Zero;

    /// <summary>Current window title, read directly (never via a message send — see guard 4's comment).</summary>
    private static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static (IntPtr Hwnd, int Pid) FindMainWindow(string proc, int retries = 10, int delayMs = 300)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            foreach (var p in Process.GetProcessesByName(proc))
            {
                if (p.MainWindowHandle != IntPtr.Zero) return (p.MainWindowHandle, p.Id);
            }
            if (attempt < retries) Thread.Sleep(delayMs);
        }
        return (IntPtr.Zero, 0);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunWinInput(string exe, string fx, string fy, string proc)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("click");
        psi.ArgumentList.Add(fx);
        psi.ArgumentList.Add(fy);
        psi.ArgumentList.Add("--proc");
        psi.ArgumentList.Add(proc);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    private static string? ArgVal(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntVal(string[] args, string flag, int fallback)
    {
        var v = ArgVal(args, flag);
        return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }
}
