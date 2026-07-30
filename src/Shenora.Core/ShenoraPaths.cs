namespace Shenora.Core;

/// <summary>Inputs for <see cref="ShenoraPaths.Resolve"/>. All family-proven conventions, parameterized.</summary>
public sealed class ShenoraPathsOptions
{
    /// <summary>
    /// Explicit root (normally from <see cref="AppRootArgument.Resolve"/>). Wins over everything.
    /// </summary>
    public string? ExplicitRoot { get; init; }

    /// <summary>
    /// Env var name a launcher sets to pin the root (e.g. <c>MYAPP_ROOT_DIR</c>). Wins over
    /// detection when set and non-empty.
    /// </summary>
    public string? RootEnvironmentVariable { get; init; }

    /// <summary>
    /// Env var name the HOST sets when spawning a child process so both share one data dir
    /// (e.g. <c>MYAPP_DATA_DIR</c>) — without it each exe resolves its own and shared stores
    /// diverge (a live incident in the source app).
    /// </summary>
    public string? DataEnvironmentVariable { get; init; }

    /// <summary>
    /// Exe-subfolder names whose PARENT is the bundle root: packaged bundles put the runtime exe
    /// in <c>libs/</c> beside the launcher, so running from such a folder means the root is one up.
    /// </summary>
    public IReadOnlyList<string> ExecutableSubfolders { get; init; } = ["libs", "lib"];

    /// <summary>Folder name for user/runtime data under the root.</summary>
    public string DataFolderName { get; init; } = "data";

    /// <summary>Folder name for bundled read-only resources under the root.</summary>
    public string ResourcesFolderName { get; init; } = "res";
}

/// <summary>
/// The app's on-disk layout authority — the ONE place that computes where things live, so nothing
/// does ad-hoc <c>Path.Combine</c> against scattered bases. Anchored at the PORTABLE bundle root
/// (beside the exe/launcher) so copying the app folder moves all its data with it — deliberately
/// NOT <c>%APPDATA%</c> (roaming, scattered, non-portable; the family rule). Apps that want an
/// installed-style data home (<c>%LocalAppData%</c>) opt in by passing it through the data env
/// var / an explicit override instead.
///
/// Root resolution order: <see cref="ShenoraPathsOptions.ExplicitRoot"/> (the <c>--app-root</c>
/// launcher arg) → the root env var → libs-parent detection (exe inside <c>libs/</c> ⇒ parent)
/// → the base directory itself. Data: the data env var → <c>&lt;root&gt;/data</c>.
/// </summary>
public sealed class ShenoraPaths
{
    private ShenoraPaths(string rootDir, string dataDir, string resourcesDir)
    {
        RootDir = rootDir;
        DataDir = dataDir;
        ResourcesDir = resourcesDir;
    }

    /// <summary>The portable bundle root (launcher + <c>libs/</c> + <c>res/</c> + <c>data/</c>).</summary>
    public string RootDir { get; }

    /// <summary>The user/runtime data directory (<c>&lt;root&gt;/data</c> unless overridden).</summary>
    public string DataDir { get; }

    /// <summary>Bundled read-only resources (<c>&lt;root&gt;/res</c>). Not auto-created — it ships with the app.</summary>
    public string ResourcesDir { get; }

    /// <summary>
    /// A purpose-named area under <see cref="DataDir"/> (e.g. <c>config</c>, <c>db</c>, <c>cache</c>,
    /// <c>logs</c>, <c>webview2</c>), created on first access. Apps define their own area names —
    /// the framework deliberately ships none (no app vocabulary in the library).
    /// </summary>
    public string DataArea(string name)
    {
        var p = Path.Combine(DataDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>
    /// Resolve the layout. <paramref name="baseDirectory"/> defaults to
    /// <c>AppContext.BaseDirectory</c>; the env-reader seam exists for tests.
    /// </summary>
    public static ShenoraPaths Resolve(ShenoraPathsOptions? options = null,
        string? baseDirectory = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        var opt = options ?? new ShenoraPathsOptions();
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;

        var root = ResolveRoot(opt, baseDir, env);
        var data = opt.DataEnvironmentVariable is { Length: > 0 } dv && env(dv) is { Length: > 0 } dataOverride
            ? dataOverride
            : Path.Combine(root, opt.DataFolderName);

        // ABSOLUTIZE both, once, here (P5.5 H2). A relative root or data override — a launcher passing
        // `--app-root ..\install`, or an app config holding a relative path — otherwise makes every
        // derived path follow the PROCESS WORKING DIRECTORY. That matters because this kit moves the
        // CWD itself: the file dialogs deliberately set RestoreDirectory = false (directory memory is
        // ours, per-key and cross-session), so the first Open/Save dialog relocates the CWD to whatever
        // the user browsed to — and from then on the SAME DataDir string resolves to a different
        // physical folder, splitting the app's data mid-session. It also defeats
        // SingleInstanceGuard's channel hashing: two spellings of one install hash differently, so a
        // second instance can start against the single-writer WebView2 folder.
        return new ShenoraPaths(
            Path.GetFullPath(root),
            Path.GetFullPath(data),
            Path.GetFullPath(Path.Combine(root, opt.ResourcesFolderName)));
    }

    private static string ResolveRoot(ShenoraPathsOptions opt, string baseDir, Func<string, string?> env)
    {
        if (opt.ExplicitRoot is { Length: > 0 } explicitRoot) return explicitRoot;
        if (opt.RootEnvironmentVariable is { Length: > 0 } rv && env(rv) is { Length: > 0 } envRoot) return envRoot;

        var trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folder = Path.GetFileName(trimmed);
        if (opt.ExecutableSubfolders.Any(s => string.Equals(folder, s, StringComparison.OrdinalIgnoreCase))
            && Directory.GetParent(trimmed) is { } parent)
        {
            return parent.FullName;
        }
        return baseDir;
    }
}
