using Microsoft.Extensions.DependencyInjection;
using Shenora;

namespace Shenora.Tests.Core;

/// <summary>
/// The no-UI runner. What it really closes is that <see cref="ShenoraApplication.Run"/> used to
/// throw unless a Windows package was referenced, so Core's application-host half was
/// Windows-only in practice — these run with no WinForms type anywhere.
/// <para>
/// Every test supplies an already-cancelled or promptly-cancelled stop token and turns process
/// signals OFF: the wait is real, so an unbounded one would HANG the suite rather than fail it.
/// </para>
/// </summary>
public class HeadlessRunnerTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.Headless",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });

    private sealed class RecordingHook(string name, List<string> log) : IShenoraLifecycleHook
    {
        public void OnStarting(ShenoraApplication app) => log.Add($"start:{name}");
        public void OnStopping(ShenoraApplication app) => log.Add($"stop:{name}");
    }

    [Fact]
    public void Run_starts_hooks_in_order_and_stops_them_in_REVERSE_order()
    {
        var log = new List<string>();
        var builder = Builder();
        builder.Services.AddSingleton<IShenoraLifecycleHook>(new RecordingHook("a", log));
        builder.Services.AddSingleton<IShenoraLifecycleHook>(new RecordingHook("b", log));
        builder.UseHeadless(new HeadlessRunnerOptions
        {
            StopToken = new CancellationToken(canceled: true),
            StopOnProcessSignals = false,
        });
        using var app = builder.Build();

        app.Run();

        // Reverse on the way out is the documented contract, and a same-order bug would still look
        // plausible in a log of two — so assert the exact sequence.
        Assert.Equal(["start:a", "start:b", "stop:b", "stop:a"], log);
    }

    [Fact]
    public void A_throwing_stop_hook_does_not_stop_the_others()
    {
        var log = new List<string>();
        var builder = Builder();
        builder.Services.AddSingleton<IShenoraLifecycleHook>(new RecordingHook("first", log));
        builder.OnStopping(_ => throw new InvalidOperationException("shutdown glue exploded"));
        builder.UseHeadless(new HeadlessRunnerOptions
        {
            StopToken = new CancellationToken(canceled: true),
            StopOnProcessSignals = false,
        });
        using var app = builder.Build();

        app.Run();

        // The thrower is registered second, so it runs FIRST on the way out — "first" only gets its
        // stop if the throw was swallowed. Never-block-close.
        Assert.Contains("stop:first", log);
    }

    [Fact]
    public void A_throwing_start_hook_surfaces_but_shutdown_still_runs()
    {
        var log = new List<string>();
        var builder = Builder();
        builder.Services.AddSingleton<IShenoraLifecycleHook>(new RecordingHook("early", log));
        builder.OnStarting(_ => throw new InvalidOperationException("startup glue exploded"));
        builder.UseHeadless(new HeadlessRunnerOptions
        {
            StopToken = new CancellationToken(canceled: true),
            StopOnProcessSignals = false,
        });
        using var app = builder.Build();

        // A hook that cannot start is a startup FAILURE and the app must see it (WinFormsRunner has
        // the same asymmetry: OnStarting unguarded, OnStopping guarded).
        Assert.Throws<InvalidOperationException>(app.Run);

        // …and the already-started hook is still stopped — the contract says OnStopping runs even
        // when startup failed partway.
        Assert.Equal(["start:early", "stop:early"], log);
    }

    [Fact]
    public async Task Cancelling_the_stop_token_ends_a_blocked_run()
    {
        using var stop = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        var builder = Builder();
        builder.OnStarting(_ => started.Set());
        builder.UseHeadless(new HeadlessRunnerOptions { StopToken = stop.Token, StopOnProcessSignals = false });
        using var app = builder.Build();

        var run = Task.Run(app.Run);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the runner never reached OnStarting");
        Assert.False(run.IsCompleted, "Run returned without waiting — it is supposed to block");

        stop.Cancel();

        // BOUNDED, always, and via WaitAsync rather than a blocking Wait: if the runner ever stops
        // observing the token this FAILS with a TimeoutException instead of hanging the whole suite
        // (the standing rule for anything awaiting a cancellable operation here — and a blocking
        // wait in a test is its own deadlock risk, xUnit1031).
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Registering_process_signal_handlers_does_not_break_a_normal_run()
    {
        // The default path (StopOnProcessSignals = true) registers real SIGINT/SIGTERM handlers and
        // disposes them before returning. Nothing here can deliver a signal, so what this pins is
        // that the registration itself is harmless and the run still completes.
        var builder = Builder();
        builder.UseHeadless(new HeadlessRunnerOptions { StopToken = new CancellationToken(canceled: true) });
        using var app = builder.Build();

        app.Run();
    }

    [Fact]
    public void Without_a_runner_Run_still_names_the_fix()
    {
        // The gap this whole file closes — kept so the diagnostic itself stays honest.
        using var app = Builder().Build();

        var error = Assert.Throws<InvalidOperationException>(app.Run);

        Assert.Contains("IShenoraRunner", error.Message, StringComparison.Ordinal);
    }
}
