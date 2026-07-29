using Shenora.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.Core;

public class ShenoraApplicationTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    private static ShenoraApplicationOptions Options(
        string[]? args = null, ShenoraPathsOptions? paths = null,
        params (string Name, string Value)[] vars) => new()
    {
        ApplicationName = "Shenora.Tests.App",
        Args = args,
        Paths = paths,
        BaseDirectory = @"C:\MyApp",
        GetEnvironmentVariable = Env(vars),
    };

    [Fact]
    public void CreateBuilder_resolves_paths_environment_and_name()
    {
        var builder = ShenoraApplication.CreateBuilder(Options());
        Assert.Equal("Shenora.Tests.App", builder.ApplicationName);
        Assert.Empty(builder.Args);
        Assert.Equal(@"C:\MyApp", builder.Paths.RootDir);
        Assert.False(builder.Environment.IsDevelopment);
        // Environment anchors at the resolved ROOT (where the .dev marker lives in bundles).
        Assert.Equal(builder.Paths.RootDir, builder.Environment.BaseDirectory);
    }

    [Fact]
    public void Default_application_name_falls_back_to_the_entry_assembly()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            BaseDirectory = @"C:\MyApp",
            GetEnvironmentVariable = Env(),
        });
        Assert.False(string.IsNullOrWhiteSpace(builder.ApplicationName));
    }

    [Fact]
    public void App_root_argument_fills_the_explicit_root()
    {
        var builder = ShenoraApplication.CreateBuilder(Options(args: ["--app-root", @"D:\Install"]));
        Assert.Equal(@"D:\Install", builder.Paths.RootDir);
        Assert.Equal(["--app-root", @"D:\Install"], builder.Args);
    }

    [Fact]
    public void App_set_explicit_root_wins_over_the_argument()
    {
        var builder = ShenoraApplication.CreateBuilder(Options(
            args: ["--app-root", @"D:\Install"],
            paths: new ShenoraPathsOptions { ExplicitRoot = @"E:\Chosen" }));
        Assert.Equal(@"E:\Chosen", builder.Paths.RootDir);
    }

    [Fact]
    public void Other_paths_options_survive_the_app_root_merge()
    {
        var builder = ShenoraApplication.CreateBuilder(Options(
            args: ["--app-root", @"D:\Install"],
            paths: new ShenoraPathsOptions { DataFolderName = "userdata" }));
        Assert.Equal(@"D:\Install", builder.Paths.RootDir);
        Assert.Equal(@"D:\Install\userdata", builder.Paths.DataDir);
    }

    [Fact]
    public void Environment_detection_honors_the_env_var_seam()
    {
        var builder = ShenoraApplication.CreateBuilder(Options(
            vars: ("DOTNET_ENVIRONMENT", "Development")));
        Assert.True(builder.Environment.IsDevelopment);
    }

    [Fact]
    public void Build_registers_environment_and_paths_as_services()
    {
        var builder = ShenoraApplication.CreateBuilder(Options());
        using var app = builder.Build();
        Assert.Same(builder.Environment, app.Services.GetRequiredService<ShenoraEnvironment>());
        Assert.Same(builder.Paths, app.Services.GetRequiredService<ShenoraPaths>());
        Assert.Same(builder.Environment, app.Environment);
        Assert.Same(builder.Paths, app.Paths);
    }

    private sealed class RecordingModule(List<string> log, string name, Action<IServiceCollection>? configure = null) : IShenoraModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
            log.Add(name);
            configure?.Invoke(services);
        }
    }

    private sealed class ModuleService;

    [Fact]
    public void Modules_configure_services_in_registration_order()
    {
        var log = new List<string>();
        var builder = ShenoraApplication.CreateBuilder(Options());
        builder
            .AddModule(new RecordingModule(log, "first", s => s.AddSingleton<ModuleService>()))
            .AddModule(new RecordingModule(log, "second"));

        Assert.Empty(log); // deferred to Build()
        using var app = builder.Build();
        Assert.Equal(["first", "second"], log);
        Assert.NotNull(app.Services.GetRequiredService<ModuleService>());
    }

    [Fact]
    public void Build_can_only_be_called_once()
    {
        var builder = ShenoraApplication.CreateBuilder(Options());
        using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private sealed class RecordingHook(List<string> log, string name) : IShenoraLifecycleHook
    {
        public void OnStarting(ShenoraApplication app) => log.Add(name + ".starting");
        public void OnStopping(ShenoraApplication app) => log.Add(name + ".stopping");
    }

    [Fact]
    public void Lifecycle_hooks_resolve_in_registration_order_and_delegates_only_fire_their_side()
    {
        var log = new List<string>();
        var builder = ShenoraApplication.CreateBuilder(Options());
        builder.OnStarting(_ => log.Add("delegate.starting"));
        builder.Services.AddSingleton<IShenoraLifecycleHook>(new RecordingHook(log, "hook"));
        builder.OnStopping(_ => log.Add("delegate.stopping"));

        using var app = builder.Build();
        var hooks = app.Services.GetServices<IShenoraLifecycleHook>().ToArray();
        Assert.Equal(3, hooks.Length);

        foreach (var hook in hooks) hook.OnStarting(app);
        for (var i = hooks.Length - 1; i >= 0; i--) hooks[i].OnStopping(app);
        Assert.Equal(
            ["delegate.starting", "hook.starting", "delegate.stopping", "hook.stopping"],
            log);
    }

    [Fact]
    public void Run_without_a_runner_throws_an_actionable_message()
    {
        using var app = ShenoraApplication.CreateBuilder(Options()).Build();
        var ex = Assert.Throws<InvalidOperationException>(app.Run);
        Assert.Contains("UseWinForms", ex.Message);
    }

    private sealed class RecordingRunner : IShenoraRunner
    {
        public ShenoraApplication? Ran { get; private set; }
        public void Run(ShenoraApplication app) => Ran = app;
    }

    [Fact]
    public void Run_invokes_the_registered_runner()
    {
        var runner = new RecordingRunner();
        var builder = ShenoraApplication.CreateBuilder(Options());
        builder.Services.AddSingleton<IShenoraRunner>(runner);
        using var app = builder.Build();
        app.Run();
        Assert.Same(app, runner.Ran);
    }

    private sealed class DisposableService : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Dispose_disposes_the_service_provider()
    {
        var builder = ShenoraApplication.CreateBuilder(Options());
        builder.Services.AddSingleton<DisposableService>();
        var app = builder.Build();
        var service = app.Services.GetRequiredService<DisposableService>();
        app.Dispose();
        Assert.True(service.Disposed);
    }
}
