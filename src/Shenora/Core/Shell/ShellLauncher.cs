using System.Diagnostics;

namespace Shenora.Core.Shell;

/// <summary>
/// Shell integrations: reveal a file in the OS's file manager, open a folder, open a URL, launch a process. Both
/// desktop shells register it (D88), as this and as its portable face <see cref="IUrlLauncher"/>, the same instance.
/// <para>
/// Opening a URL is meaningful on ANY host, so it is inherited from <see cref="IUrlLauncher"/> and app logic that only
/// opens links should depend on that (D20); the rest are desktop concepts, which a phone shell does not register.
/// ⚠ <c>OpenUrl</c> is not redeclared — re-declaring an inherited member is CS0108, an error under
/// warnings-as-errors.
/// </para>
/// </summary>
public interface IShellLauncher : IUrlLauncher
{
    /// <summary>Open the OS's file manager with <paramref name="filePath"/> selected: Explorer, Finder, or the
    /// desktop's own on Linux (its folder, where the file manager cannot select). ⚠ On Linux it WAITS, up to 5 s, for
    /// the file manager to answer over D-Bus — one that is still starting takes a moment — so call it off the UI
    /// thread there.</summary>
    void RevealInFileManager(string filePath);

    /// <summary>Open a directory in the OS's file manager.</summary>
    void OpenDirectory(string directoryPath);

    /// <summary>Launch an executable.</summary>
    /// <param name="options">What to run, and how.</param>
    /// <returns>
    /// The new process's id, or <c>null</c> when the shell satisfied the request WITHOUT starting one
    /// (it handed the work to an already-running instance). ⚠ <c>null</c> is not a failure — a launch
    /// that did not happen THROWS. It means there is no process for you to wait on or kill.
    /// </returns>
    /// <exception cref="FileNotFoundException"><c>ExecutablePath</c> does not exist. ⚠ On macOS an app bundle is a
    /// FOLDER, so <c>Foo.app</c> is refused: pass the executable inside it, <c>Foo.app/Contents/MacOS/Foo</c>.</exception>
    int? LaunchProcess(ProcessLaunchOptions options);
}

/// <summary>
/// What to launch, and how — for <see cref="IShellLauncher.LaunchProcess"/>. An options type rather than
/// parameters, so the next requirement (an elevation verb, environment variables, an argument list) is an
/// added property instead of a broken signature.
/// </summary>
public sealed class ProcessLaunchOptions
{
    /// <summary>The executable to run. Must exist.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// The command line, already quoted. ⚠ Argument quoting is the CALLER's problem here; the string is passed
    /// through untouched.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>Working directory. Null uses the executable's own folder.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// The <see cref="IShellLauncher"/> implementation, on Windows, macOS and Linux. Validation failures throw BCL
/// exceptions (<see cref="FileNotFoundException"/>…) — the dispatch boundary maps throws to structured errors.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    internal enum Os { Windows, MacOS, Linux }

    private readonly Os _os;
    private readonly Action<ProcessStartInfo> _launch;
    private readonly Func<ProcessStartInfo, TimeSpan, int> _runAndWait;

    /// <summary>The launcher for the OS this runs on.</summary>
    public ShellLauncher()
        : this(OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux, Launch, RunAndWait)
    {
    }

    /// <summary>Tests: which OS, and what "start a process" and "run one and wait for its exit code" do.</summary>
    internal ShellLauncher(Os os, Action<ProcessStartInfo> launch, Func<ProcessStartInfo, TimeSpan, int> runAndWait)
    {
        _os = os;
        _launch = launch;
        _runAndWait = runAndWait;
    }

    /// <inheritdoc />
    public void RevealInFileManager(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("File to reveal does not exist.", filePath);
        var full = Path.GetFullPath(filePath);
        switch (_os)
        {
            case Os.Windows:
                // /select opens Explorer with the file highlighted. UseShellExecute=false + immediate
                // Dispose — keeping a reference leaked process handles on Windows 11.
                _launch(new ProcessStartInfo
                {
                    FileName = "explorer.exe", Arguments = $"/select,\"{full}\"", UseShellExecute = false, CreateNoWindow = true,
                });
                break;
            case Os.MacOS:
                _launch(Command("open", "-R", full));
                break;
            default:
                // The freedesktop file manager interface, which GNOME's, KDE's and most others answer, selects the
                // file; where none does, its folder opens instead. A comma would split dbus-send's array, and is
                // percent-encoded as a file URI allows.
                var uri = new Uri(full).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal);
                var shown = _runAndWait(Command("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"),
                    TimeSpan.FromSeconds(5));
                if (shown != 0) OpenDirectory(Path.GetDirectoryName(full)!);
                break;
        }
    }

    /// <inheritdoc />
    public void OpenDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory to open does not exist: {directoryPath}");

        // The shell's "open" on the directory itself: on Windows its file manager, NOT explorer.exe directly —
        // launching explorer.exe left orphaned explorer processes on Windows 11; elsewhere .NET hands it to
        // `open` (macOS) or `xdg-open` (Linux).
        if (_os != Os.Linux)
        {
            _launch(_os == Os.Windows
                ? new ProcessStartInfo { FileName = directoryPath, UseShellExecute = true, Verb = "open" }
                : new ProcessStartInfo { FileName = directoryPath, UseShellExecute = true });
            return;
        }
        try
        {
            _launch(new ProcessStartInfo { FileName = directoryPath, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // With no xdg-open .NET tries to RUN the folder, and says "No such file or directory" of one that exists
            // (measured on a machine without xdg-utils): the missing piece named instead.
            throw new InvalidOperationException(
                $"Nothing on this machine opens a folder (xdg-open, from xdg-utils, was not found): {directoryPath}", ex);
        }
    }

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        // ⚠ Scheme-checked: an app shell must never shell-execute odd protocols on a page's behalf.
        ArgumentNullException.ThrowIfNull(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Only http/https URLs open in the system browser (got '{url}').", nameof(url));

        _launch(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    /// <inheritdoc />
    public int? LaunchProcess(ProcessLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        if (!File.Exists(options.ExecutablePath))
            throw new FileNotFoundException("Executable does not exist.", options.ExecutablePath);

        using var started = Process.Start(new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            Arguments = options.Arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = options.WorkingDirectory
                ?? Path.GetDirectoryName(options.ExecutablePath) ?? string.Empty,
        });
        // ⚠ Read the id BEFORE the `using` disposes the handle.
        return started?.Id;
    }

    private static ProcessStartInfo Command(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static void Launch(ProcessStartInfo info) => Process.Start(info)?.Dispose();

    // The exit code, or -1 when it could not start or did not finish in time (then it is left to finish alone).
    private static int RunAndWait(ProcessStartInfo info, TimeSpan timeout)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        try
        {
            using var process = Process.Start(info);
            if (process is null) return -1;
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            return process.WaitForExit(timeout) ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;   // no dbus-send on this machine
        }
    }
}
