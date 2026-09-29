using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Chromium.Host;

/// <summary>
/// Starts a process that inherits none of this one's handles. On Windows, <see cref="Process.Start(ProcessStartInfo)"/>
/// hands the new process every handle that is inheritable at that moment, and while Chromium starts one of its own
/// processes a pipe end of its is: a process that caught one and outlives the app keeps that channel open, and CEF's
/// shutdown waits on it forever. Elsewhere <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
internal static unsafe class NoInheritProcess
{
    /// <summary>Start <paramref name="exe"/> with <paramref name="arguments"/>, in this process's environment and
    /// folder, as <see cref="Process.Start(ProcessStartInfo)"/> would, but inheriting no handle.</summary>
    /// <param name="exe">The executable's full path.</param>
    /// <param name="arguments">Each argument as the new process's <c>Main</c> receives it.</param>
    /// <param name="creationFlags">Windows' process-creation flags; tests pass <c>CREATE_NO_WINDOW</c> for a console
    /// child.</param>
    public static Process Start(string exe, IEnumerable<string> arguments, int creationFlags = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(exe);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            return Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start.");
        }

        // A buffer of its own: CreateProcessW may write into the command line it is given.
        var line = (CommandLine(exe, arguments) + '\0').ToCharArray();
        var startup = new StartupInfo { cb = sizeof(StartupInfo) };
        ProcessInformation process;
        fixed (char* l = line)
            if (!CreateProcessW(exe, l, 0, 0, inheritHandles: false, creationFlags, 0, null, &startup, &process))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{exe} did not start.");
        try
        {
            // While this handle is open the id cannot name another process.
            try { return Process.GetProcessById(process.dwProcessId); }
            catch (ArgumentException)
            {
                GetExitCodeProcess(process.hProcess, out var code);
                throw new InvalidOperationException($"{exe} exited as it started (exit code {code}).");
            }
        }
        finally
        {
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
    }

    /// <summary>
    /// The Windows command line whose arguments, split as the C runtime and <c>CommandLineToArgvW</c> split them, are
    /// <paramref name="arguments"/>: each quoted when it is empty or holds a space, tab or quote, a quote escaped, and
    /// the backslashes before a quote or the closing quote doubled.
    /// </summary>
    internal static string CommandLine(string exe, IEnumerable<string> arguments)
    {
        var line = new StringBuilder().Append('"').Append(exe).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ');
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }
            line.Append('"');
            for (var i = 0; ; i++)
            {
                var backslashes = 0;
                while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
                if (i == argument.Length) { line.Append('\\', backslashes * 2); break; }
                if (argument[i] == '"') line.Append('\\', backslashes * 2 + 1).Append('"');
                else line.Append('\\', backslashes).Append(argument[i]);
            }
            line.Append('"');
        }
        return line.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, int creationFlags, nint environment, string? currentDirectory, StartupInfo* startupInfo, ProcessInformation* processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint process, out int exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
