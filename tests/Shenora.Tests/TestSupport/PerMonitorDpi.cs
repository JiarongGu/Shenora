using System.Runtime.InteropServices;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// Makes the calling thread per-monitor DPI aware (V2), as the Chromium splash's own threads are. WinForms keeps the
/// system DPI of the first thread that asks for the whole process: asked first on a per-monitor thread at 200 %, every
/// WinForms test after it in this process sees 192 and scales by two (measured; asked first on an unaware thread, it
/// stays 96 everywhere). So WinForms is asked here first, while the thread is still unaware like the test process.
/// </summary>
internal static class PerMonitorDpi
{
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);

    public static void Enter()
    {
        using (var probe = new Control()) _ = probe.DeviceDpi;
        SetThreadDpiAwarenessContext(-4);
    }
}
