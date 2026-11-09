#if CEF_LINUX
using System.Reflection;
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// What CEF needs of a Linux process: <c>libcef.so</c> from where the build laid it out, and the helper every
/// subprocess runs, <c>&lt;App&gt;-helper</c>, beside it. The helper is native because Chromium's zygote forks the
/// renderers, and a process running .NET must not be forked. The runtime is beside the app, or in <c>engine/</c> beside
/// the app's <c>lib/</c> when the app was published with <c>ShenoraChromiumPublishFolders</c> (D96).
/// </summary>
internal static class LinuxPlatform
{
    /// <summary>The folder holding CEF's runtime and the helper: the app's own, else the <c>engine/</c> beside its
    /// <c>lib/</c>. When neither holds <c>libcef.so</c> the app's own, so a missing runtime is named at the path a
    /// flat layout would put it.</summary>
    internal static string EngineOf(string baseDirectory, Func<string, bool> exists)
    {
        if (exists(Path.Combine(baseDirectory, "libcef.so"))) return baseDirectory;
        return RootOfLib(baseDirectory) is { } root && exists(Path.Combine(root, "engine", "libcef.so"))
            ? Path.Combine(root, "engine")
            : baseDirectory;
    }

    /// <summary>The folder the app's executable <c>&lt;App&gt;</c> is in: the app's own, or the root above its
    /// <c>lib/</c> in a folders publish.</summary>
    internal static string RootOf(string baseDirectory, Func<string, bool> exists) =>
        RootOfLib(baseDirectory) is { } root && exists(Path.Combine(root, "engine", "libcef.so")) ? root : baseDirectory;

    private static string? RootOfLib(string baseDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(baseDirectory);
        return string.Equals(Path.GetFileName(trimmed), "lib", StringComparison.Ordinal) ? Path.GetDirectoryName(trimmed) : null;
    }

    private static string Engine => EngineOf(AppContext.BaseDirectory, File.Exists);

    /// <summary>The CEF library the build laid out: beside the app, or in <c>engine/</c>.</summary>
    public static string Library => Path.Combine(Engine, "libcef.so");

    /// <summary>The app's name: its assembly, <c>&lt;App&gt;.App</c>, without <c>.App</c>. The same whichever of the
    /// app's executables started, so it names what must not depend on that.</summary>
    public static string AppName
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly()?.GetName().Name ?? "App";
            return assembly.EndsWith(".App", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
        }
    }

    /// <summary>The helper every CEF subprocess runs, <c>&lt;App&gt;-helper</c>, beside <c>libcef.so</c>, which it loads
    /// from its own folder.</summary>
    public static string Helper => Path.Combine(Engine, AppName + "-helper");

    /// <summary>
    /// The executable that names the app to the desktop: the layout's <c>&lt;App&gt;</c>, else the one the OS ran. It
    /// is CEF's argv[0], and GTK names the process's windows (its dialogs) after argv[0], so the main window takes the
    /// same <see cref="ProgramName"/> and <see cref="ProgramClass"/>: a dock then groups the window and its dialogs as
    /// one app, and matches them to the app's <c>.desktop</c> file, whether it was started as <c>&lt;App&gt;</c>, as
    /// <c>&lt;App&gt;.App</c> or through <c>dotnet</c>. Under an apphost, .NET's own argv[0] is the managed <c>.dll</c>.
    /// </summary>
    public static string Executable =>
        Path.Combine(RootOf(AppContext.BaseDirectory, File.Exists), AppName) is var laidOut && File.Exists(laidOut)
            ? laidOut
            : Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    /// <summary>WM_CLASS's name and the Wayland app id: the executable's file name.</summary>
    public static string ProgramName => Path.GetFileName(Executable);

    /// <summary>WM_CLASS's class, by GTK's rule for its own windows: the name with its first letter upper-cased.</summary>
    public static string ProgramClass => ClassOf(ProgramName);

    internal static string ClassOf(string name) =>
        name.Length > 0 && char.IsAsciiLetterLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;

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
