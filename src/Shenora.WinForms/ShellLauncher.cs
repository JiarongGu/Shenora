using System.Diagnostics;

namespace Shenora.WinForms;

/// <summary>Shell integrations: reveal in Explorer, open a folder, open a URL, launch a process.</summary>
public interface IShellLauncher
{
    /// <summary>Open Windows Explorer with <paramref name="filePath"/> selected.</summary>
    void RevealInExplorer(string filePath);

    /// <summary>Open a directory in the shell's file manager.</summary>
    void OpenDirectory(string directoryPath);

    /// <summary>Open an http/https URL in the system browser (anything else is rejected).</summary>
    void OpenUrl(string url);

    /// <summary>Launch an executable (working directory defaults to the exe's folder).</summary>
    void LaunchProcess(string executablePath, string? arguments = null, string? workingDirectory = null);
}

/// <summary>
/// The <see cref="IShellLauncher"/> implementation, ported from the primary desktop sibling with
/// its Windows 11 lessons kept. Validation failures throw BCL exceptions
/// (<see cref="FileNotFoundException"/>…) — this package carries no IPC dependency; the dispatch
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
        // Scheme-checked like the WebView2 new-window policy: an app shell must never
        // shell-execute odd protocols on a page's behalf.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Only http/https URLs open in the system browser (got '{url}').", nameof(url));

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
    }

    /// <inheritdoc />
    public void LaunchProcess(string executablePath, string? arguments = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Executable does not exist.", executablePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? string.Empty,
        })?.Dispose();
    }
}
