using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Core;

/// <summary>Inputs for <see cref="ShenoraApplication.CreateBuilder(ShenoraApplicationOptions)"/>.</summary>
public sealed class ShenoraApplicationOptions
{
    /// <summary>The process command-line arguments (pass <c>Main</c>'s <c>args</c>).</summary>
    public string[]? Args { get; init; }

    /// <summary>
    /// Stable app identifier used for crash-dialog titles and single-instance channel names.
    /// Defaults to the entry assembly's name.
    /// </summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Path-layout inputs. When <see cref="ShenoraPathsOptions.ExplicitRoot"/> is unset, a
    /// <c>--app-root</c> command-line value (the family's launcher contract, see
    /// <see cref="AppRootArgument"/>) fills it in automatically; an app-set value wins over the
    /// argument (setting it is an explicit opt-out of the launcher contract).
    /// </summary>
    public ShenoraPathsOptions? Paths { get; init; }

    /// <summary>Base directory override (defaults to <c>AppContext.BaseDirectory</c>). Test seam.</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Environment-variable reader override. Test seam; production callers omit it.</summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }
}

/// <summary>
/// The composed application: the resolved environment/paths, the built service provider, and the
/// run loop. Create through <see cref="CreateBuilder(string[])"/>:
/// <code>
/// var builder = ShenoraApplication.CreateBuilder(args);
/// builder.UseWinForms(new WinFormsHostOptions { MainForm = sp => new MainForm(sp) }); // Shenora.WinForms
/// builder.PrewarmWebView2(app => new WebViewEnvironmentOptions {                      // Shenora.WebView2
///     UserDataFolder = app.Paths.DataArea("webview2"),
///     IsDevelopment = app.Environment.IsDevelopment,
/// });
/// using var app = builder.Build();
/// app.Run();
/// </code>
/// </summary>
public sealed class ShenoraApplication : IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    internal ShenoraApplication(string applicationName, IReadOnlyList<string> args,
        ShenoraEnvironment environment, ShenoraPaths paths, ServiceProvider provider)
    {
        ApplicationName = applicationName;
        Args = args;
        Environment = environment;
        Paths = paths;
        _provider = provider;
    }

    /// <summary>Stable app identifier (see <see cref="ShenoraApplicationOptions.ApplicationName"/>).</summary>
    public string ApplicationName { get; }

    /// <summary>The command-line arguments the app was started with (never null).</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>The detected runtime environment.</summary>
    public ShenoraEnvironment Environment { get; }

    /// <summary>The resolved on-disk layout authority.</summary>
    public ShenoraPaths Paths { get; }

    /// <summary>The application's root service provider.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Create a builder from the process arguments with default options.</summary>
    public static ShenoraApplicationBuilder CreateBuilder(string[]? args = null) =>
        CreateBuilder(new ShenoraApplicationOptions { Args = args });

    /// <summary>Create a builder: resolve paths (honoring <c>--app-root</c>), detect the environment.</summary>
    public static ShenoraApplicationBuilder CreateBuilder(ShenoraApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = options.Args ?? [];
        var baseDirectory = options.BaseDirectory ?? AppContext.BaseDirectory;

        var pathsOptions = options.Paths ?? new ShenoraPathsOptions();
        if (string.IsNullOrEmpty(pathsOptions.ExplicitRoot))
        {
            // The launcher's --app-root fills ExplicitRoot only when the app left it unset (an
            // app-set root is an explicit opt-out). Empty sentinel = flag absent or blank.
            var argRoot = AppRootArgument.Resolve(args, string.Empty);
            if (argRoot.Length > 0)
            {
                // `with`, not a hand-copied initializer (P5.5 H6). The previous version restated all six
                // properties, so adding a seventh option to ShenoraPathsOptions would have silently
                // dropped it for every launch that passed --app-root.
                pathsOptions = pathsOptions with { ExplicitRoot = argRoot };
            }
        }

        var paths = ShenoraPaths.Resolve(pathsOptions, baseDirectory, options.GetEnvironmentVariable);
        // Environment detection anchors at the resolved ROOT (not the exe folder): in packaged
        // bundles the .dev marker sits at the install root beside the launcher — the folder the
        // user sees — not in libs/ beside the runtime exe.
        var environment = ShenoraEnvironment.Detect(paths.RootDir, options.GetEnvironmentVariable);
        var name = options.ApplicationName
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "Application";

        return new ShenoraApplicationBuilder(name, args, environment, paths);
    }

    /// <summary>
    /// Run to completion via the registered <see cref="IShenoraRunner"/> (blocks until shutdown).
    /// </summary>
    public void Run()
    {
        var runner = Services.GetService<IShenoraRunner>()
            ?? throw new InvalidOperationException(
                "No IShenoraRunner is registered. Reference a host package and call its builder " +
                "extension (e.g. UseWinForms from Shenora.WinForms) before Build(), or register " +
                "an IShenoraRunner yourself.");
        runner.Run(this);
    }

    /// <summary>
    /// Dispose the service provider (and with it every owned singleton).
    /// <para>
    /// Prefer <see cref="DisposeAsync"/> when any singleton might be async-only: Microsoft DI's
    /// synchronous <c>Dispose</c> THROWS <see cref="InvalidOperationException"/> for a captured
    /// disposable that implements only <see cref="IAsyncDisposable"/>. Shenora's own
    /// <c>RenderSession</c> and <c>StreamingSession</c> are exactly that shape, so registering one as a
    /// singleton used to crash the documented <c>using var app = builder.Build(); app.Run();</c>
    /// shutdown — after the message loop had already exited, i.e. a crash dialog on every clean quit
    /// with no way for a consumer to work around it (P5.5 H2).
    /// </para>
    /// </summary>
    public void Dispose() => _provider.Dispose();

    /// <summary>
    /// Dispose the service provider asynchronously — the safe shutdown for an app whose singletons may
    /// be <see cref="IAsyncDisposable"/>-only. Use <c>await using var app = builder.Build();</c>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_provider is IAsyncDisposable asyncProvider)
        {
            await asyncProvider.DisposeAsync().ConfigureAwait(false);
            return;
        }
        _provider.Dispose();
    }
}
