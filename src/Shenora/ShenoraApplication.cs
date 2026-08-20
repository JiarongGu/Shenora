using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shenora;

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
    /// <c>--app-root</c> command-line value (see <see cref="AppRootArgument"/>) fills it in
    /// automatically; an app-set value wins over the argument.
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
/// builder.UseWindows(new WindowsHostOptions { MainForm = sp => new MainForm(sp) }); // Shenora.Windows
/// builder.PrewarmWebView2(app => new WebViewEnvironmentOptions {                      // Shenora.Windows
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
    private IShenoraLifecycleHook[]? _hooks;
    private bool _started;
    private bool _stopped;

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

    /// <summary>
    /// The resource pipeline every webview this app hosts receives — the second phase of the ASP.NET
    /// minimal-hosting shape (D64). Declare it on the BUILT app, before the first window exists:
    /// <code>
    /// using var app = builder.Build();
    /// app.UseFiles(new WebViewFileOptions { … });   // order matters, like app.UseAuthentication()
    /// app.UseMediaPlayer();
    /// app.Run();
    /// </code>
    /// <para>
    /// A shell hands this to each webview as it builds one, so an app never calls
    /// <see cref="Core.WebView.WebViewPipeline.ApplyTo"/> itself.
    /// </para>
    /// </summary>
    public Core.WebView.WebViewPipeline Pipeline =>
        _provider.GetRequiredService<Core.WebView.WebViewPipeline>();

    /// <summary>
    /// Append a raw step to <see cref="Pipeline"/> — the primitive behind <c>app.UseFiles(…)</c> and the
    /// escape hatch for a route the kit ships no helper for. Returns the app, so calls chain.
    /// </summary>
    public ShenoraApplication Use(Action<Core.WebView.IWebViewInterceptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Pipeline.Use(configure);
        return this;
    }

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
            // Empty sentinel = flag absent or blank.
            var argRoot = AppRootArgument.Resolve(args, string.Empty);
            if (argRoot.Length > 0)
            {
                pathsOptions = pathsOptions with { ExplicitRoot = argRoot };
            }
        }

        var paths = ShenoraPaths.Resolve(pathsOptions, baseDirectory, options.GetEnvironmentVariable);
        // Anchored at the resolved ROOT, not the exe folder: in packaged bundles the .dev marker sits at
        // the install root beside the launcher, not in libs/ beside the runtime exe.
        var environment = ShenoraEnvironment.Detect(paths.RootDir, options.GetEnvironmentVariable);
        var name = options.ApplicationName
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "Application";

        return new ShenoraApplicationBuilder(name, args, environment, paths);
    }

    /// <summary>
    /// Invoke <see cref="IShenoraLifecycleHook.OnStarting"/> on every registered hook, in
    /// registration order. <b>IDEMPOTENT — the second call does nothing</b>, because a platform-owned
    /// loop offers several plausible places to start from and some of them re-enter (an activity's
    /// <c>OnCreate</c>/<c>OnResume</c> fire per activity instance).
    /// <para>
    /// A runner calls this for you; call it DIRECTLY only from a host whose PLATFORM owns the loop and
    /// therefore cannot use <see cref="Run"/> (a mobile activity).
    /// </para>
    /// <para>
    /// NOT guarded: a hook that cannot start is a startup failure and the app must see it. Pair it
    /// with <see cref="Stop"/> in a <c>finally</c> — <see cref="IShenoraLifecycleHook"/>'s contract
    /// is that stopping runs even when starting failed partway.
    /// </para>
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _hooks = Services.GetServices<IShenoraLifecycleHook>().ToArray();
        foreach (var hook in _hooks) hook.OnStarting(this);
    }

    /// <summary>
    /// Invoke <see cref="IShenoraLifecycleHook.OnStopping"/> in REVERSE registration order, each
    /// step guarded so one failing hook cannot mask the others or block shutdown. <b>IDEMPOTENT</b>,
    /// and safe to call when <see cref="Start"/> never ran or threw partway.
    /// </summary>
    public void Stop()
    {
        // ⚠ A stop BEFORE any start must not latch: a platform that signals "stopped" before it ever
        // signalled "started" (an activity destroyed during a failed launch) would otherwise permanently
        // disarm the real shutdown that came later.
        if (!_started || _stopped) return;
        _stopped = true;
        // `_hooks` is null only if Start threw before assigning it; there is then nothing to stop.
        var hooks = _hooks ?? [];
        for (var i = hooks.Length - 1; i >= 0; i--)
        {
            // Through AppCallback rather than a bare `catch { }`: a hook must never block shutdown, but
            // swallowing SILENTLY leaves "my cleanup did not run" with no diagnostic at all.
            AppCallback.Run(() => hooks[i].OnStopping(this), ex =>
                Services.GetService<ILoggerFactory>()?.CreateLogger<ShenoraApplication>()
                    .LogError(ex, "A lifecycle hook threw while stopping; shutdown continued."));
        }
    }

    /// <summary>
    /// Run to completion via the registered <see cref="IShenoraRunner"/> (blocks until shutdown).
    /// </summary>
    public void Run()
    {
        var runner = Services.GetService<IShenoraRunner>()
            ?? throw new InvalidOperationException(
                "No IShenoraRunner is registered. Reference a host package and call its builder " +
                "extension (e.g. UseWindows from Shenora.Windows) before Build(), or register " +
                "an IShenoraRunner yourself.");
        runner.Run(this);
    }

    /// <summary>
    /// Dispose the service provider (and with it every owned singleton).
    /// <para>
    /// ⚠ Prefer <see cref="DisposeAsync"/> when any singleton might be async-only: Microsoft DI's
    /// synchronous <c>Dispose</c> THROWS <see cref="InvalidOperationException"/> for a captured
    /// disposable that implements only <see cref="IAsyncDisposable"/> — which <c>RenderSession</c> and
    /// <c>StreamingSession</c> are, so a <c>using var app</c> shutdown crashes after the message loop
    /// has already exited.
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
