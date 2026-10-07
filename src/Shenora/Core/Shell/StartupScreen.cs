using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shenora.Core.Shell;

/// <summary>When a shell closes the launcher's startup screen (<see cref="IStartupScreen"/>).</summary>
public enum StartupScreenMode
{
    /// <summary>Once the app's first window is on screen: its splash card, or its main window. The default.</summary>
    FirstWindow,

    /// <summary>The app closes it itself, through <see cref="IStartupScreen.Close"/>; the launcher's timeout ends it
    /// otherwise.</summary>
    Manual,
}

/// <summary>
/// The screen a native launcher put up before the app started (<c>Shenora.Launcher</c>), passed to the app as
/// <c>--startup-screen &lt;window id&gt;</c>. Both desktop shells register one and close it at the app's first window
/// unless told otherwise (<see cref="StartupScreenMode"/>). Without the argument there is no screen and
/// <see cref="Close"/> does nothing.
/// </summary>
public interface IStartupScreen
{
    /// <summary>A launcher passed its screen, and it has not been closed from here.</summary>
    bool IsShown { get; }

    /// <summary>Close the launcher's screen. Idempotent, any thread, never throws.</summary>
    void Close();
}

/// <summary>The kit's <see cref="IStartupScreen"/>: the launcher's window, from the arguments.</summary>
public sealed class StartupScreen : IStartupScreen
{
    /// <summary>The launcher's argument (<c>--startup-screen &lt;window id&gt;</c>, or joined with <c>=</c>).</summary>
    public const string Flag = "--startup-screen";

    /// <summary>The launcher's window class (Windows) and WM_CLASS (X11): a window without it is not the launcher's.</summary>
    internal const string WindowClass = "ShenoraStartupScreen";

    private readonly Func<ulong, bool> _close;
    private readonly ILogger? _log;
    private int _closed;

    /// <summary>The screen of the launcher's window <paramref name="window"/>: an HWND on Windows, an X11 window id on
    /// Linux. Null is no screen.</summary>
    /// <param name="window">The launcher's window, or null.</param>
    /// <param name="log">Where a close that failed is reported, at Debug.</param>
    public StartupScreen(ulong? window, ILogger? log = null) : this(window, CloseNative, log) { }

    internal StartupScreen(ulong? window, Func<ulong, bool> close, ILogger? log)
    {
        Window = window;
        _close = close;
        _log = log;
    }

    /// <summary>The screen the arguments name, or none.</summary>
    /// <param name="args">The app's arguments.</param>
    /// <param name="log">Where a close that failed is reported, at Debug.</param>
    public static StartupScreen FromArguments(IReadOnlyList<string>? args, ILogger? log = null) => new(Parse(args), log);

    /// <summary>The launcher's window, or null when no launcher passed one.</summary>
    public ulong? Window { get; }

    /// <inheritdoc />
    public bool IsShown => Window is not null && Volatile.Read(ref _closed) == 0;

    /// <inheritdoc />
    public void Close()
    {
        if (Window is not { } window || Interlocked.Exchange(ref _closed, 1) == 1) return;
        try
        {
            if (!_close(window))
                AppCallback.Log(_log, () => $"[Shenora] The launcher's startup screen {window} could not be closed (it may be gone)", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => $"[Shenora] Closing the launcher's startup screen {window} failed", LogLevel.Debug, ex);
        }
    }

    internal static ulong? Parse(IReadOnlyList<string>? args)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is null) continue;
            string? value;
            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)) value = i + 1 < args.Count ? args[i + 1] : null;
            else if (arg.StartsWith(Flag + "=", StringComparison.OrdinalIgnoreCase)) value = arg[(Flag.Length + 1)..];
            else continue;
            return ulong.TryParse(value?.Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;
        }
        return null;
    }

    private static bool CloseNative(ulong window)
    {
        // An HWND carries a uniqueness count bumped each time its slot is freed, so a stale one fails here
        // (ERROR_INVALID_WINDOW_HANDLE) rather than reaching another window: no class check is needed on Windows.
        if (OperatingSystem.IsWindows()) return PostMessageW((nint)window, WM_CLOSE, 0, 0) != 0;
        if (OperatingSystem.IsLinux()) return Xcb.Close(window);
        return false;
    }

    private const uint WM_CLOSE = 0x0010;
    [DllImport("user32")] private static extern int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// WM_DELETE_WINDOW, as the window manager's close sends it, to the launcher's window — through xcb, not Xlib: Xlib
    /// reports a window that is gone (the launcher's timeout passed first) to its default error handler, which EXITS
    /// the process, so the app died as its first window appeared (measured under Xvfb). xcb returns the error as a value.
    /// </summary>
    private static class Xcb
    {
        private const string Lib = "libxcb.so.1";
        private const byte ClientMessage = 33;
        private const uint AtomString = 31;    // XCB_ATOM_STRING
        private const uint AtomWmClass = 67;   // XCB_ATOM_WM_CLASS

        [StructLayout(LayoutKind.Sequential)]
        private struct Cookie { public uint Sequence; }

        [StructLayout(LayoutKind.Sequential)]
        private struct InternAtomReply
        {
            public byte ResponseType;
            public byte Pad0;
            public ushort Sequence;
            public uint Length;
            public uint Atom;
        }

        [DllImport(Lib)] private static extern nint xcb_connect(nint displayName, nint screen);
        [DllImport(Lib)] private static extern int xcb_connection_has_error(nint c);
        [DllImport(Lib)] private static extern void xcb_disconnect(nint c);
        [DllImport(Lib)] private static extern Cookie xcb_intern_atom(nint c, byte onlyIfExists, ushort nameLength, byte[] name);
        [DllImport(Lib)] private static extern nint xcb_intern_atom_reply(nint c, Cookie cookie, out nint error);
        [DllImport(Lib)] private static extern Cookie xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
        [DllImport(Lib)] private static extern nint xcb_get_property_reply(nint c, Cookie cookie, out nint error);
        [DllImport(Lib)] private static extern nint xcb_get_property_value(nint reply);
        [DllImport(Lib)] private static extern int xcb_get_property_value_length(nint reply);
        [DllImport(Lib)] private static extern Cookie xcb_send_event_checked(nint c, byte propagate, uint destination, uint eventMask, byte[] ev);
        [DllImport(Lib)] private static extern nint xcb_request_check(nint c, Cookie cookie);
        [DllImport(Lib)] private static extern int xcb_flush(nint c);
        [DllImport("libc")] private static extern void free(nint pointer);

        public static bool Close(ulong window)
        {
            if (window > uint.MaxValue) return false;   // an X id is 32 bits
            var c = xcb_connect(0, 0);                    // never null: a failed connection is an object with an error
            if (c == 0) return false;
            try
            {
                if (xcb_connection_has_error(c) != 0) return false;
                var w = (uint)window;
                // Only the launcher's own window: an id the launcher freed when it went is the next client's to reuse.
                if (!IsStartupScreen(c, w)) return false;
                var protocols = Atom(c, "WM_PROTOCOLS");
                var delete = Atom(c, "WM_DELETE_WINDOW");
                if (protocols == 0 || delete == 0) return false;
                // xcb_client_message_event_t: response_type, format, sequence, window, type, then data32[5].
                var ev = new byte[32];
                ev[0] = ClientMessage;
                ev[1] = 32;
                BitConverter.TryWriteBytes(ev.AsSpan(4), w);
                BitConverter.TryWriteBytes(ev.AsSpan(8), protocols);
                BitConverter.TryWriteBytes(ev.AsSpan(12), delete);   // data32[1], the time, stays 0: CurrentTime
                var error = xcb_request_check(c, xcb_send_event_checked(c, 0, w, 0, ev));
                if (error != 0)
                {
                    free(error);
                    return false;
                }
                xcb_flush(c);
                return true;
            }
            finally
            {
                xcb_disconnect(c);
            }
        }

        private static bool IsStartupScreen(nint c, uint window)
        {
            var reply = xcb_get_property_reply(c, xcb_get_property(c, 0, window, AtomWmClass, AtomString, 0, 64), out var error);
            if (error != 0) free(error);   // BadWindow: it is gone
            if (reply == 0) return false;
            try
            {
                var length = xcb_get_property_value_length(reply);
                return length > 0
                    && (Marshal.PtrToStringAnsi(xcb_get_property_value(reply), length) ?? "").Contains(WindowClass, StringComparison.Ordinal);
            }
            finally
            {
                free(reply);
            }
        }

        private static uint Atom(nint c, string name)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(name);
            var reply = xcb_intern_atom_reply(c, xcb_intern_atom(c, 0, (ushort)bytes.Length, bytes), out var error);
            if (error != 0) free(error);
            if (reply == 0) return 0;
            try { return Marshal.PtrToStructure<InternAtomReply>(reply).Atom; }
            finally { free(reply); }
        }
    }
}
