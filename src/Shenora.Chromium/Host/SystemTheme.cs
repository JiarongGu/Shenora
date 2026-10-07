using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Chromium.Host;

/// <summary>
/// The operating system's app theme, as each states it: Windows' <c>AppsUseLightTheme</c>, macOS's
/// <c>AppleInterfaceStyle</c>, and GNOME's <c>color-scheme</c> (then a <c>-dark</c> GTK theme) on Linux. Null when it
/// does not say. Read once where it is needed, never followed.
/// </summary>
internal static unsafe class SystemTheme
{
    public static bool? IsDark()
    {
        try
        {
#if CEF_MACOS
            return MacIsDark();
#elif CEF_LINUX
            return LinuxIsDark();
#else
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
#endif
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

#if CEF_MACOS
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;

    // Set only while dark: absent is light.
    private static bool? MacIsDark()
    {
        var library = NativeLibrary.Load(CoreFoundation);
        var anyApplication = *(nint*)NativeLibrary.GetExport(library, "kCFPreferencesAnyApplication");
        var key = Encoding.UTF8.GetBytes("AppleInterfaceStyle\0");
        nint name;
        fixed (byte* k = key) name = CFStringCreateWithCString(0, k, Utf8Encoding);
        if (name == 0) return null;
        try
        {
            var value = CFPreferencesCopyAppValue(name, anyApplication);
            if (value == 0) return false;
            try
            {
                var text = new byte[32];
                fixed (byte* t = text)
                    if (CFStringGetCString(value, t, text.Length, Utf8Encoding) == 0) return null;
                var end = Array.IndexOf(text, (byte)0);
                return Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end).Equals("Dark", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(name);
        }
    }

    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFPreferencesCopyAppValue(nint key, nint application);
    [DllImport(CoreFoundation)] private static extern byte CFStringGetCString(nint value, byte* buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
#endif

#if CEF_LINUX
    private const string Gio = "libgio-2.0.so.0", GLib = "libglib-2.0.so.0", GObject = "libgobject-2.0.so.0";
    private const string Schema = "org.gnome.desktop.interface";

    // A schema that is not installed is looked up first: g_settings_new on one ABORTS the process.
    private static bool? LinuxIsDark()
    {
        var source = g_settings_schema_source_get_default();
        if (source == 0) return null;
        var schema = g_settings_schema_source_lookup(source, Utf8(Schema), 1);
        if (schema == 0) return null;
        try
        {
            var settings = g_settings_new_full(schema, 0, null);
            try
            {
                if (Read(schema, settings, "color-scheme") is { } scheme && scheme != "default")
                    return scheme == "prefer-dark";
                return Read(schema, settings, "gtk-theme") is { } theme && theme.EndsWith("-dark", StringComparison.OrdinalIgnoreCase) ? true : null;
            }
            finally
            {
                g_object_unref(settings);
            }
        }
        finally
        {
            g_settings_schema_unref(schema);
        }
    }

    private static string? Read(nint schema, nint settings, string key)
    {
        if (g_settings_schema_has_key(schema, Utf8(key)) == 0) return null;
        var value = g_settings_get_string(settings, Utf8(key));
        if (value == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            g_free(value);
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    [DllImport(Gio)] private static extern nint g_settings_schema_source_get_default();
    [DllImport(Gio)] private static extern nint g_settings_schema_source_lookup(nint source, byte[] id, int recursive);
    [DllImport(Gio)] private static extern int g_settings_schema_has_key(nint schema, byte[] key);
    [DllImport(Gio)] private static extern void g_settings_schema_unref(nint schema);
    [DllImport(Gio)] private static extern nint g_settings_new_full(nint schema, nint backend, byte[]? path);
    [DllImport(Gio)] private static extern nint g_settings_get_string(nint settings, byte[] key);
    [DllImport(GObject)] private static extern void g_object_unref(nint instance);
    [DllImport(GLib)] private static extern void g_free(nint memory);
#endif
}
