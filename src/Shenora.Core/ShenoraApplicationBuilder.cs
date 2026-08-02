using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shenora.Core;

/// <summary>
/// Composes a Shenora application: paths + environment (resolved up front so registrations can use
/// them), service registrations, modules, and lifecycle hooks. Created by
/// <see cref="ShenoraApplication.CreateBuilder(string[])"/>; host packages contribute through
/// extension methods (e.g. Shenora.Windows <c>UseWinForms</c>, Shenora.Windows
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

        Services.AddSingleton(Environment);
        Services.AddSingleton(Paths);
        foreach (var module in _modules) module.ConfigureServices(Services);
        // Framework plumbing every app gets — the in-process pub/sub bus that modules, services,
        // and the transport bridges share (design §4). TryAdd LAST so an app or module
        // registration wins.
        Services.TryAddSingleton<IEventBus, EventBus>();

        return new ShenoraApplication(ApplicationName, Args, Environment, Paths,
            Services.BuildServiceProvider());
    }
}
