using System.Diagnostics;
using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// Shell integrations: reveal in Explorer, open a folder, open a URL, launch a process.
/// <para>
/// Opening a URL is meaningful on ANY host, so it is inherited from <see cref="IUrlLauncher"/> in
/// <c>Shenora</c> and app logic that only opens links should depend on that (D20); revealing in a file
/// manager and launching a process are desktop-only concepts and stay here. <c>UseWindows</c> registers
/// both faces of the same instance. ⚠ <c>OpenUrl</c> is not redeclared — re-declaring an inherited
/// member is CS0108, an error under warnings-as-errors.
/// </para>
/// </summary>
public interface IShellLauncher : IUrlLauncher
{
    /// <summary>Open Windows Explorer with <paramref name="filePath"/> selected.</summary>
    void RevealInExplorer(string filePath);

    /// <summary>Open a directory in the shell's file manager.</summary>
    void OpenDirectory(string directoryPath);

    /// <summary>Launch an executable.</summary>
    /// <param name="options">What to run, and how.</param>
    /// <returns>
    /// The new process's id, or <c>null</c> when the shell satisfied the request WITHOUT starting one
    /// (it handed the work to an already-running instance). ⚠ <c>null</c> is not a failure — a launch
    /// that did not happen THROWS. It means there is no process for you to wait on or kill.
    /// </returns>
    /// <exception cref="FileNotFoundException"><c>ExecutablePath</c> does not exist.</exception>
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
    /// The command line, already quoted. ⚠ Windows argument quoting is the CALLER's problem here; the
    /// string is passed through untouched.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>Working directory. Null uses the executable's own folder.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// The <see cref="IShellLauncher"/> implementation. Validation failures throw BCL exceptions
/// (<see cref="FileNotFoundException"/>…) — this package carries no IPC dependency, and the dispatch
/// boundary maps throws to structured errors.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    /// <inheritdoc />
    public void RevealInExplorer(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("File to reveal does not exist.", filePath);

        // /select opens Explorer with the file highlighted. UseShellExecute=false + immediate
        // Dispose — keeping a reference leaked process handles on Windows 11.
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(startInfo)?.Dispose();
    }

    /// <inheritdoc />
    public void OpenDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory to open does not exist: {directoryPath}");

        // The shell "open" verb on the directory itself, NOT explorer.exe directly — launching
        // explorer.exe left orphaned explorer processes on Windows 11.
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = directoryPath,
            UseShellExecute = true,
            Verb = "open",
        });
    }

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        // ⚠ Scheme-checked: an app shell must never shell-execute odd protocols on a page's behalf.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Only http/https URLs open in the system browser (got '{url}').", nameof(url));

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
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
}
