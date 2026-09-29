using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The browser process is started inheriting no handle: <c>Process.Start</c> handed it a pipe end of Chromium's, and the
/// app then hung in Chromium's shutdown. The detector is a pipe whose write end is inheritable: with this process's copy
/// closed, a read ends at once unless a child still holds one. The control shows it catches <c>Process.Start</c>.
/// Relies on this assembly running its tests one at a time (xunit.runner.json), since another test's launch while the
/// end is inheritable would take it too.
/// </summary>
public class NoInheritProcessTests
{
    private const int CreateNoWindow = 0x08000000;
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    private static readonly string[] ThreeSeconds = ["-n", "4", "127.0.0.1"];

    [Fact]
    public void A_started_process_holds_no_handle_of_the_app()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var child = NoInheritProcess.Start(Ping, ThreeSeconds, CreateNoWindow);
        try
        {
            Assert.True(PipeClosesWhileAlive(pipe, child), "the child holds the pipe's write end: it inherited a handle");
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void Process_Start_hands_the_child_the_handle_which_is_what_the_detector_sees()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var info = new ProcessStartInfo(Ping) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in ThreeSeconds) info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        try
        {
            Assert.False(PipeClosesWhileAlive(pipe, child), "Process.Start no longer hands the child inheritable handles");
        }
        finally
        {
            child.Kill();
        }
    }

    // The arguments, '|'-separated.
    [Theory]
    [InlineData("--browser|9333")]
    [InlineData(@"D:\App Data\a b\profile|")]
    [InlineData(@"say ""hi""|ends\|ends\ with space\|a\\""b")]
    [InlineData("\ttab|" + @"\\server\share\")]
    public void The_command_line_splits_back_into_the_same_arguments(string joined)
    {
        var arguments = joined.Split('|');
        var line = NoInheritProcess.CommandLine(@"C:\App\My App.exe", arguments);
        Assert.Equal([@"C:\App\My App.exe", .. arguments], Split(line));
    }

    [Fact]
    public void The_browser_needs_its_arguments() =>
        Assert.Throws<ArgumentNullException>(() => ChromiumBrowserProcess.Start(null!));

    // True when the pipe reports its end within a second while the child still runs.
    private static bool PipeClosesWhileAlive(AnonymousPipeServerStream pipe, Process child)
    {
        pipe.DisposeLocalCopyOfClientHandle();
        var read = Task.Run(() => pipe.Read(new byte[1]));
        var closed = read.Wait(TimeSpan.FromSeconds(1)) && read.Result == 0;
        Assert.False(child.HasExited, "the child ended before the pipe was read, so the read proves nothing");
        return closed;
    }

    private static string[] Split(string line)
    {
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
