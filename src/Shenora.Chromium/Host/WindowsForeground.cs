#if CEF_WINDOWS
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// Windows keeps the foreground from a process the user is not using: a running instance asked by a later launch may
/// take it only when that launch, which the user just started and which holds it, hands it over.
/// </summary>
internal static class WindowsForeground
{
    private const int ASFW_ANY = -1;

    /// <summary>Let any process take the foreground this one holds. Harmless when it holds none.</summary>
    public static void AllowAny() => AllowSetForegroundWindow(ASFW_ANY);

    [DllImport("user32.dll")]
    private static extern int AllowSetForegroundWindow(int dwProcessId);
}
#endif
