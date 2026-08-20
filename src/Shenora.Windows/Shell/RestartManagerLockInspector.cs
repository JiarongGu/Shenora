using System.Runtime.InteropServices;
using Shenora;
// `Core.Shell`, not `Engine.Files`: a shell implements a portable contract without reaching into the
// layer that consumes it (D48).
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// Names the processes holding a file open, using the Windows <b>Restart Manager</b> — the answer for
/// contention a lease cannot touch (a game holding its own assets, a mod loader, antivirus, Explorer's
/// preview handler), where the only useful thing left is to say WHO.
/// <para>
/// ⚠ <b>Local handles only.</b> A file on a network share held open from ANOTHER machine is invisible to
/// Restart Manager — that answer lives on the server and no client-side API can produce it. This returns
/// empty there rather than guessing, which is why <see cref="IFileLockInspector"/> documents empty as
/// "cannot tell", not "nobody".
/// </para>
/// </summary>
public sealed class RestartManagerLockInspector : IFileLockInspector
{
    private const int RmRebootReasonNone = 0;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    /// <inheritdoc/>
    public IReadOnlyList<FileLockHolder> WhoHolds(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsWindows()) return [];

        // 🔴 Never throws — a diagnostic that fails the operation it describes is worse than none. Every
        // failure path below returns empty.
        var sessionKey = new string('\0', 32 + 1);
        if (RmStartSession(out var session, 0, sessionKey) != 0) return [];

        try
        {
            string[] resources = [path];
            if (RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null) != 0)
                return [];

            uint procInfoNeeded = 0;
            uint procInfo = 0;
            var result = RmGetList(session, out procInfoNeeded, ref procInfo, null, out _);
            if (result != ErrorMoreData) return [];   // 0 here means nothing holds it

            var processes = new RmProcessInfo[procInfoNeeded];
            procInfo = procInfoNeeded;
            if (RmGetList(session, out procInfoNeeded, ref procInfo, processes, out _) != 0) return [];

            var holders = new List<FileLockHolder>((int)procInfo);
            for (var i = 0; i < procInfo; i++)
            {
                var name = processes[i].strAppName;
                holders.Add(new FileLockHolder(
                    processes[i].Process.dwProcessId,
                    string.IsNullOrWhiteSpace(name) ? "unknown" : name));
            }
            return holders;
        }
        catch (Exception)
        {
            // Includes DllNotFoundException on a Windows edition without rstrtmgr (server core).
            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);
}
