using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora;

/// <summary>Inputs for <see cref="HeadlessHostExtensions.UseHeadless"/>.</summary>
public sealed class HeadlessRunnerOptions
{
    /// <summary>
    /// An app-owned stop signal — a supervisor, a "quit" route, a test. Cancelling it ends the run
    /// and the shutdown hooks fire. A token that is ALREADY cancelled is fine and means "start,
    /// then stop immediately", which is the shape a test wants.
    /// </summary>
    public CancellationToken StopToken { get; init; }

    /// <summary>
    /// Also stop on SIGINT (Ctrl+C) and SIGTERM. On by default because the alternative is worse than
    /// it looks: without it the runtime terminates the process on the signal, so
    /// <see cref="IShenoraLifecycleHook.OnStopping"/> never runs and everything the family relies on
    /// it for — releasing a lock, flushing state, letting a <c>--restarted</c> relaunch through —
    /// is silently skipped. Set false only when the host already owns signal handling.
    /// </summary>
    public bool StopOnProcessSignals { get; init; } = true;

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>Registers the headless run loop on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class HeadlessHostExtensions
{
    /// <summary>
    /// Make this an application with NO UI loop: <see cref="ShenoraApplication.Run"/> invokes the
    /// lifecycle hooks and then blocks until a stop signal, with ordered shutdown after it.
    /// <para>
    /// Why the kit ships this at all. <see cref="ShenoraApplication.Run"/> throws without an
    /// <see cref="IShenoraRunner"/>, and the only implementation lived in <c>Shenora.Windows</c> —
    /// so Core's application-host half was Windows-only IN PRACTICE even though every type in it is
    /// portable, and the D3 transport spike had to bypass the builder entirely and wire DI by hand.
    /// An app could always write the one-method interface itself; this removes the reason to.
    /// </para>
    /// <para>
    /// WHAT THIS IS NOT FOR: a host whose PLATFORM owns the loop (a mobile activity, a MAUI app).
    /// <see cref="IShenoraRunner.Run"/> is contractually "blocks until shutdown", which such a host
    /// cannot honour — it needs its own runner that starts the hooks and returns. Said here rather
    /// than discovered, because "headless" reads like it covers that case and it does not.
    /// </para>
    /// </summary>
    public static ShenoraApplicationBuilder UseHeadless(this ShenoraApplicationBuilder builder,
        HeadlessRunnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolved = options ?? new HeadlessRunnerOptions();
        builder.Services.AddSingleton(resolved);
        builder.Services.AddSingleton<IShenoraRunner>(_ => new HeadlessRunner(resolved));
        return builder;
    }
}

/// <summary>
/// The no-UI run sequence: lifecycle hooks, block, ordered shutdown. Deliberately the same shape as
/// <c>WinFormsRunner</c> minus everything Windows — the single-instance gate, process init and the
/// message loop are that host's concerns, not this one's.
/// </summary>
internal sealed class HeadlessRunner(HeadlessRunnerOptions options) : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The hook sequence itself belongs to ShenoraApplication — ordering, the start/stop asymmetry
        // and idempotency are one contract, and two runners hand-rolling it was already one copy too
        // many (a third, for a platform that owns its own loop, is what made this obvious).
        try
        {
            app.Start();
            WaitForStop();
        }
        finally
        {
            app.Stop();
        }
    }

    private void WaitForStop()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(options.StopToken);

        // Registrations are disposed before the method returns, so nothing survives to fire against a
        // torn-down run — a second Run in the same process (a test host does exactly that) would
        // otherwise inherit the first one's handlers.
        var signals = new List<IDisposable>();
        try
        {
            if (options.StopOnProcessSignals)
            {
                foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
                {
                    // Cancel = true is the load-bearing line: it tells the runtime NOT to terminate the
                    // process itself, which is what gives the shutdown hooks below a chance to run at
                    // all. Without it the default handler wins and the ordered shutdown is skipped.
                    signals.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        Log($"[Shenora] {context.Signal} received — stopping.");
                        // Guarded: the source is disposed the moment the wait below returns, and a
                        // second signal arriving in that window would otherwise throw on a background
                        // thread with nobody to catch it.
                        try { stop.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }));
                }
            }

            // WaitHandle, not Task.Delay(Infinite).Wait() — this method IS the application's main
            // thread and is meant to park it. An already-cancelled token returns immediately.
            stop.Token.WaitHandle.WaitOne();
        }
        finally
        {
            foreach (var registration in signals) registration.Dispose();
        }
    }

    private void Log(string message) => AppCallback.Log(options.Log, () => message);
}
