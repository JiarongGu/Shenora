using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Core.Shell;

/// <summary>
/// The <see cref="IFileLockInspector"/> implementation, on Windows, macOS and Linux. Both desktop shells register it
/// (D88).
/// <list type="bullet">
/// <item>Windows asks the <b>Restart Manager</b>, which names the processes using the file.</item>
/// <item>Linux reads each process's open files (<c>/proc/&lt;pid&gt;/fd</c>) and the executable it runs.</item>
/// <item>macOS asks the kernel for the same two (<c>libproc</c>, what <c>lsof</c> uses).</item>
/// </list>
/// <para>
/// ⚠ <b>On Linux and macOS a file held open locks nothing</b>: another process may still write, rename or delete it,
/// so a failed change there is rarely explained by a holder. And open files are found only in the processes this user
/// may inspect, never as a file mapped into memory with no descriptor left open for it.
/// </para>
/// <para>
/// ⚠ <b>Local handles only.</b> A file on a network share held open from ANOTHER machine is invisible to every one of
/// these; the answer lives on the server, and no client-side API can produce it. That is one reason
/// <see cref="IFileLockInspector"/> documents empty as "cannot tell", not "nobody".
/// </para>
/// </summary>
public sealed class FileLockInspector : IFileLockInspector
{
    /// <inheritdoc/>
    public IReadOnlyList<FileLockHolder> WhoHolds(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // 🔴 Never throws: a diagnostic that fails the operation it describes is worse than none.
        try
        {
            if (OperatingSystem.IsWindows()) return RestartManager.WhoHolds(path);
            // The kernel names a file by its real path, so the one asked about is resolved the same way first.
            if (Posix.RealPath(path) is not { } target) return [];
            if (OperatingSystem.IsMacOS()) return Darwin.WhoHolds(target);
            if (OperatingSystem.IsLinux()) return Proc.WhoHolds(target);
            return [];
        }
        catch (Exception)
        {
            // Includes DllNotFoundException on a Windows edition without rstrtmgr (server core).
            return [];
        }
    }

    private static class RestartManager
    {
        private const int ErrorMoreData = 234;
        private const int CchRmMaxAppName = 255;
        private const int CchRmMaxSvcName = 63;

        public static IReadOnlyList<FileLockHolder> WhoHolds(string path)
        {
            var sessionKey = new string('\0', 32 + 1);
            if (RmStartSession(out var session, 0, sessionKey) != 0) return [];
            try
            {
                string[] resources = [path];
                if (RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null) != 0) return [];

                uint procInfo = 0;
                var result = RmGetList(session, out var procInfoNeeded, ref procInfo, null, out _);
                if (result != ErrorMoreData) return [];   // 0 here means nothing holds it

                var processes = new RmProcessInfo[procInfoNeeded];
                procInfo = procInfoNeeded;
                if (RmGetList(session, out _, ref procInfo, processes, out _) != 0) return [];

                var holders = new List<FileLockHolder>((int)procInfo);
                for (var i = 0; i < procInfo; i++)
                {
                    var name = processes[i].strAppName;
                    holders.Add(new FileLockHolder(processes[i].Process.dwProcessId, string.IsNullOrWhiteSpace(name) ? "unknown" : name));
                }
                return holders;
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
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);
    }

    private static class Posix
    {
        /// <summary>The path with every link and <c>..</c> resolved (and on macOS each name in its stored case), or null
        /// when it does not exist.</summary>
        public static string? RealPath(string path)
        {
            // A buffer of our own rather than NULL, which the oldest macOS realpath does not take. 4096 is Linux's
            // PATH_MAX, and more than macOS's 1024.
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                var full = Path.GetFullPath(path);
                var resolved = OperatingSystem.IsMacOS() ? DarwinRealPath(full, buffer) : LinuxRealPath(full, buffer);
                return resolved == 0 ? null : Marshal.PtrToStringUTF8(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("libc.so.6", EntryPoint = "realpath")]
        private static extern nint LinuxRealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);

        [DllImport(Darwin.LibSystem, EntryPoint = "realpath")]
        private static extern nint DarwinRealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);
    }

    private static class Proc
    {
        public static IReadOnlyList<FileLockHolder> WhoHolds(string target)
        {
            var holders = new List<FileLockHolder>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                if (Holds(dir, target)) holders.Add(new FileLockHolder(pid, NameOf(dir)));
            }
            return holders;
        }

        private static bool Holds(string dir, string target)
        {
            if (LinkTarget(Path.Combine(dir, "exe")) == target) return true;
            IEnumerable<string> descriptors;
            try
            {
                descriptors = Directory.GetFileSystemEntries(Path.Combine(dir, "fd"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;   // another user's process, or one that has ended
            }
            foreach (var descriptor in descriptors)
                if (LinkTarget(descriptor) == target) return true;
            return false;
        }

        // A descriptor names its file by the path it has now; a socket, a pipe or a deleted file never matches.
        private static string? LinkTarget(string link)
        {
            try
            {
                return new FileInfo(link).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;   // closed since it was listed, or not ours to read
            }
        }

        // The executable's full name: comm, the fallback, is cut at 15 characters.
        private static string NameOf(string dir)
        {
            if (LinkTarget(Path.Combine(dir, "exe")) is { Length: > 0 } exe) return Path.GetFileName(exe);
            try
            {
                return File.ReadAllText(Path.Combine(dir, "comm")).Trim() is { Length: > 0 } comm ? comm : "unknown";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "unknown";
            }
        }
    }

    private static class Darwin
    {
        internal const string LibSystem = "/usr/lib/libSystem.dylib";
        private const int ProcPidListFds = 1;            // PROC_PIDLISTFDS
        private const int ProcPidFdVnodePathInfo = 2;    // PROC_PIDFDVNODEPATHINFO
        private const uint ProxFdTypeVnode = 1;          // PROX_FDTYPE_VNODE
        private const int FdInfoSize = 8;                // struct proc_fdinfo { int32 fd; uint32 type; }
        // struct vnode_fdinfowithpath: proc_fileinfo (24 bytes), vnode_info (152), then the path (MAXPATHLEN).
        private const int VnodePathInfoSize = 1200;
        private const int VnodePathOffset = 176;
        private const int PathInfoMaxSize = 4096;        // PROC_PIDPATHINFO_MAXSIZE

        public static IReadOnlyList<FileLockHolder> WhoHolds(string target)
        {
            var count = proc_listallpids(null, 0);
            if (count <= 0) return [];
            var pids = new int[count + 64];   // room for what starts while this reads
            count = proc_listallpids(pids, pids.Length * sizeof(int));
            if (count <= 0) return [];

            var holders = new List<FileLockHolder>();
            var info = new byte[VnodePathInfoSize];
            var path = new byte[PathInfoMaxSize];
            for (var i = 0; i < Math.Min(count, pids.Length); i++)
            {
                var pid = pids[i];
                if (pid <= 0) continue;
                var exe = proc_pidpath(pid, path, (uint)path.Length) > 0 ? Utf8Z(path, 0) : null;
                if (exe == target || HasOpen(pid, target, info))
                    holders.Add(new FileLockHolder(pid, exe is { Length: > 0 } ? Path.GetFileName(exe) : "unknown"));
            }
            return holders;
        }

        private static bool HasOpen(int pid, string target, byte[] info)
        {
            var size = proc_pidinfo(pid, ProcPidListFds, 0, null, 0);
            if (size <= 0) return false;   // another user's process, or one that has ended
            var fds = new byte[size + 32 * FdInfoSize];   // room for what opens while this reads
            size = proc_pidinfo(pid, ProcPidListFds, 0, fds, fds.Length);
            for (var at = 0; at + FdInfoSize <= size; at += FdInfoSize)
            {
                if (BitConverter.ToUInt32(fds, at + 4) != ProxFdTypeVnode) continue;
                var fd = BitConverter.ToInt32(fds, at);
                if (proc_pidfdinfo(pid, fd, ProcPidFdVnodePathInfo, info, info.Length) == info.Length
                    && Utf8Z(info, VnodePathOffset) == target)
                    return true;
            }
            return false;
        }

        private static string Utf8Z(byte[] buffer, int offset)
        {
            var end = Array.IndexOf(buffer, (byte)0, offset);
            return Encoding.UTF8.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset);
        }

        [DllImport(LibSystem)] private static extern int proc_listallpids(int[]? buffer, int bufferSize);
        [DllImport(LibSystem)] private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int bufferSize);
        [DllImport(LibSystem)] private static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int bufferSize);
        [DllImport(LibSystem)] private static extern int proc_pidpath(int pid, byte[] buffer, uint bufferSize);
    }
}
