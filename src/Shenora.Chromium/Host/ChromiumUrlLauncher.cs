using System.Diagnostics;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Chromium shell's <see cref="IUrlLauncher"/>: the user's own browser, for http and https only, as the
/// WebView2 shell's <c>ShellLauncher</c> does. The OS's handler for the URL does the rest (on Windows the shell's
/// association; .NET maps the same call to <c>open</c> and <c>xdg-open</c> elsewhere).
/// </summary>
internal sealed class ChromiumUrlLauncher : IUrlLauncher
{
    public void OpenUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Only http/https URLs open in the system browser (got '{url}').", nameof(url));
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
    }
}
