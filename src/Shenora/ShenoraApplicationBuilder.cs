using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
// The framework's own layers, defaulted in Build() (D64/D65). ⚠ The COMPOSITION ROOT is the one place
// allowed to reach every layer — that is what a composition root IS. Nothing else crosses upward: a core
// never names a module, which is why `AddMessageDispatcher` lists no facade.
using Shenora.Core.Events;            // core — the event pipeline
using Shenora.Core.Ipc;               // core — the message contract
using Shenora.Engine.Files;           // engine — the file queue
using Shenora.Engine.Missions;        // engine — the scheduler
using Shenora.Modules.Media;          // module — the player
using Shenora.Modules.Requests;     // module — the operation registry's registration

namespace Shenora;

/// <summary>
/// Composes a Shenora application: paths + environment (resolved up front so registrations can use
/// them), service registrations, modules, and lifecycle hooks. Created by
/// <see cref="ShenoraApplication.CreateBuilder(string[])"/>; host packages contribute through
/// extension methods (e.g. Shenora.Windows <c>UseWindows</c>, Shenora.Windows
/// <c>PrewarmWebView2</c>) so the packages never reference each other — the app composes them
/// (the design contract's downward-only dependency rule).
/// </summary>
public sealed class ShenoraApplicationBuilder
{
    private readonly List<IShenoraModule> _modules = [];
    private bool _built;

    internal ShenoraApplicationBuilder(string applicationName, IReadOnlyList<string> args,
        ShenoraEnvironment environment, ShenoraPaths paths)
    {
        ApplicationName = applicationName;
        Args = args;
        Environment = environment;
        Paths = paths;

        // 🔴 REGISTERED HERE, not in Build(), and that is what lets a capability be an ordinary
        // `IServiceCollection` extension (D64). `AddShenoraFileSystem()` and friends need the app's
        // storage layout to default a directory; with paths available from the moment the builder
        // exists, they resolve it from DI inside their factory instead of needing a `builder` argument.
        // That is the difference between `builder.Services.AddX()` — ASP.NET's shape — and a bespoke
        // `builder.UseX()` that exists only because the extension could not reach `Paths`.
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

    /// <summary>Add a module (applied at <see cref="Build"/>, in registration order).</summary>
    public ShenoraApplicationBuilder AddModule(IShenoraModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
        return this;
    }

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

        foreach (var module in _modules) module.ConfigureServices(Services);

        // Framework plumbing every app gets — the in-process pub/sub bus that modules, services,
        // and the transport bridges share (design §4). TryAdd LAST so an app or module
        // registration wins.
        Services.TryAddSingleton<IEventBus, EventBus>();

        // 🔴 THE FRAMEWORK ITSELF, defaulted LAST (D64) — the same thing
        // `WebApplication.CreateBuilder` does when it brings Kestrel without anyone calling
        // `AddKestrel()`. Each engine is registered by calling the SAME public method an app calls to
        // configure it, so the default path and the explicit path are literally one piece of code and
        // cannot drift — the property that stops a default from quietly meaning something else.
        //
        // ⚠ ORDER IS THE WHOLE MECHANISM: these run AFTER the app's own registrations and every one of
        // them is `TryAdd`, so an app that called `UseMissions(x => …)` earlier has already registered
        // its options and the call below no-ops. Defaulting in the CONSTRUCTOR instead would invert
        // that and silently ignore every configuration call the app makes.
        //
        // ⚠ And none of it touches a disk, a thread or a handle: each engine constructs inside its DI
        // factory, so registration is free and nothing is provisioned until something asks for it.
        // That is the precondition that makes defaulting safe at all, not an optimisation.
        this.UseMissions();
        this.UseFileSystem();
        this.UseMediaPlayer();
        // The IPC core and the operations feature. Both are portable — the dispatcher is a core, and
        // `OperationRegistry` needs only `IEventBus`, registered a few lines up — so neither needs a
        // platform and neither waits to be asked. ⚠ Anything that DOES need a platform is registered by
        // that platform's shell instead (`UseWindows`/`UseAndroid`/`UseIOS`), because only a shell knows
        // whether it can satisfy it; a platform that cannot registers nothing and the page learns that
        // from the ready handshake's capability list (D36) rather than from silence.
        Services.AddShenoraRequests();
        Services.AddMessageDispatcher();

        return new ShenoraApplication(ApplicationName, Args, Environment, Paths,
            Services.BuildServiceProvider());
    }
}
