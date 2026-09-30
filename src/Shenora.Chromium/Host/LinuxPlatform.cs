#if CEF_LINUX
using System.Reflection;
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// What CEF needs of a Linux process: <c>libcef.so</c> from beside the app, where the build laid it out, and the helper
/// every subprocess runs, <c>&lt;App&gt;-helper</c>. The helper is native because Chromium's zygote forks the
/// renderers, and a process running .NET must not be forked.
/// </summary>
internal static class LinuxPlatform
{
    /// <summary>The CEF library the build laid out beside the app.</summary>
    public static string Library => Path.Combine(AppContext.BaseDirectory, "libcef.so");

    /// <summary>The helper every CEF subprocess runs, named for the app (its assembly, <c>&lt;App&gt;.App</c>, without
    /// <c>.App</c>), so it is the same whichever of the app's executables started.</summary>
    public static string Helper
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly()?.GetName().Name ?? "App";
            var app = assembly.EndsWith(".App", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
            return Path.Combine(AppContext.BaseDirectory, app + "-helper");
        }
    }

    private static int _prepared;

    /// <summary>Once per process, before CEF's first call: the binding's <c>libcef</c> is the library beside the app.</summary>
    public static void Prepare()
    {
        if (Interlocked.Exchange(ref _prepared, 1) == 1) return;
        if (!File.Exists(Library)) throw new DllNotFoundException($"CEF's library is not beside the app at {Library}.");
        var library = NativeLibrary.Load(Library);
        NativeLibrary.SetDllImportResolver(typeof(LinuxPlatform).Assembly, (name, _, _) => name == "libcef" ? library : 0);
    }
}
#endif
