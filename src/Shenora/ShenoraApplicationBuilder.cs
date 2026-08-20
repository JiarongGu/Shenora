using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
// The framework's own layers, defaulted in Build() (D64/D65). ⚠ The COMPOSITION ROOT is the one place
// allowed to reach every layer; nothing else crosses upward.
using Shenora.Core.Events;            // core — the event pipeline
using Shenora.Modules.Media;          // module — the player
using Shenora.Modules.Requests;     // module — the operation registry's registration

namespace Shenora;

/// <summary>
/// Composes a Shenora application: paths + environment (resolved up front so registrations can use
/// them), service registrations, modules, and lifecycle hooks. Created by
/// <see cref="ShenoraApplication.CreateBuilder(string[])"/>; host packages contribute through extension
/// methods (<c>UseWindows</c>, <c>PrewarmWebView2</c>) so the packages never reference each other.
/// </summary>
public sealed class ShenoraApplicationBuilder
{
    private bool _built;

    internal ShenoraApplicationBuilder(string applicationName, IReadOnlyList<string> args,
        ShenoraEnvironment environment, ShenoraPaths paths)
    {
        ApplicationName = applicationName;
        Args = args;
        Environment = environment;
        Paths = paths;

        // 🔴 REGISTERED HERE, not in Build(), so a capability's DI FACTORY can resolve them. `UseFileSystem`
        // and `UseMediaPlayer` default a directory from the app's storage layout, and `Paths.DataArea`
        // CREATES what it names — so they must read it INSIDE the factory, or merely registering the
        // capability would provision folders in every app that never uses it (D64).
        Services.AddSingleton(environment);
        Services.AddSingleton(paths);
    }

    /// <summary>Stable app identifier (crash-dialog titles, single-instance channel names…).</summary>
    public string ApplicationName { get; }

    /// <summary>The command-line arguments the app was started with (never null).</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>The detected runtime environment (anchored at <see cref="ShenoraPaths.RootDir"/>).</summary>
    public ShenoraEnvironment Environment { get; }

    /// <summary>The resolved on-disk layout authority.</summary>
    public ShenoraPaths Paths { get; }

    /// <summary>The service registrations. <see cref="Environment"/>, <see cref="Paths"/>, and
    /// the event bus (<see cref="IEventBus"/>, replaceable) are added automatically at
    /// <see cref="Build"/>.</summary>
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Register a startup callback (see <see cref="IShenoraLifecycleHook"/> for when it runs).</summary>
    public ShenoraApplicationBuilder OnStarting(Action<ShenoraApplication> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Services.AddSingleton<IShenoraLifecycleHook>(new DelegateLifecycleHook(callback, null));
        return this;
    }

    /// <summary>Register a shutdown callback (see <see cref="IShenoraLifecycleHook"/> for when it runs).</summary>
    public ShenoraApplicationBuilder OnStopping(Action<ShenoraApplication> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Services.AddSingleton<IShenoraLifecycleHook>(new DelegateLifecycleHook(null, callback));
        return this;
    }

    /// <summary>
    /// Apply the modules, build the service provider, and produce the application. Callable once —
    /// the provider owns the registrations from here on.
    /// </summary>
    public ShenoraApplication Build()
    {
        if (_built) throw new InvalidOperationException("Build() can only be called once per builder.");
        _built = true;

        // The in-process pub/sub bus every app gets. TryAdd LAST so an app or module registration wins.
        Services.TryAddSingleton<IEventBus, EventBus>();

        // 🔴 THE FRAMEWORK ITSELF, defaulted LAST (D64), each engine registered by calling the SAME
        // public method an app calls to configure it, so the default and explicit paths cannot drift.
        //
        // ⚠ ORDER IS THE WHOLE MECHANISM: these run AFTER the app's own registrations and every one is
        // `TryAdd`, so an app that called `UseMissions(x => …)` earlier has already registered its
        // options and the call below no-ops. Defaulting in the CONSTRUCTOR would invert that and
        // silently ignore every configuration call the app makes.
        this.UseMissions();
        this.UseFileSystem();
        this.UseMediaPlayer();
        // The IPC core and request tracking: both portable, so neither waits to be asked. ⚠ Anything
        // that needs a PLATFORM is registered by that platform's shell instead — only a shell knows
        // whether it can satisfy it, and the page learns what is missing from the ready handshake's
        // capability list (D36) rather than from silence.
        this.UseRequests();
        Services.UseMessageDispatcher();

        // The webview pipeline every window this app hosts will receive (D64) — registered here, not by
        // a shell, because it is portable and because `app.Use…()` must work before any shell has been
        // asked for a window.
        Services.TryAddSingleton<Core.WebView.WebViewPipeline>();

        return new ShenoraApplication(ApplicationName, Args, Environment, Paths,
            Services.BuildServiceProvider());
    }
}
