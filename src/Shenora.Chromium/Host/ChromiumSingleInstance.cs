using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// CEF would not start because another process owns its data folder, and CEF handed this launch to it
/// (<c>CEF_RESULT_CODE_NORMAL_EXIT_PROCESS_NOTIFIED</c>). The Chromium shell exits quietly on it, since the running
/// shell treats the hand-over as a later launch; a host that owns its UI thread (<see cref="ChromiumEngine"/>) cannot
/// run, and says why.
/// </summary>
internal sealed class ChromiumAlreadyRunningException(string cache)
    : InvalidOperationException($"Another process of this app is running with Chromium's data folder {cache}, and Chromium handed this launch to it. "
        + "Each running instance needs a data folder of its own (UserDataFolder).")
{
}

/// <summary>
/// An <see cref="ILogger"/> that forwards to one supplied later: the gate runs as the app is composed, before its
/// logging exists, and its guard reports for the rest of the run.
/// </summary>
internal sealed class DeferredLogger : ILogger
{
    private ILogger? _target;

    public ILogger? Target
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, value);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Target?.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => Target?.IsEnabled(logLevel) ?? false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Target?.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>
/// The Chromium shell's single-instance gate (<see cref="ChromiumHostOptions.SingleInstance"/>), once per process. It
/// runs before CEF starts, which is during <see cref="ChromiumHostExtensions.UseChromium"/> when the app runs from its
/// layout (D87) and in the runner otherwise: CEF takes its own data folder's lock as it starts, and a losing launch
/// must answer before building anything.
/// <para>
/// Behind it stands CEF's own rule, one process per data folder: a launch that passes the gate but shares the folder
/// (the gate turned off, or a <see cref="SingleInstanceHostOptions.Scope"/> narrower than the folder) is handed to the
/// running process by CEF, and arrives at <see cref="Relaunched"/> as the gate's would.
/// </para>
/// </summary>
internal sealed class ChromiumSingleInstance
{
    /// <summary>The process's one.</summary>
    public static readonly ChromiumSingleInstance Process = new();

    private readonly Lock _gate = new();
    private readonly DeferredLogger _log = new();
    private SingleInstanceGuard? _guard;
    private SingleInstanceResult? _result;
    private Action<SingleInstanceLaunch>? _activated;

    /// <summary>What the gate found; null before it ran, or with none.</summary>
    public SingleInstanceResult? Result
    {
        get { lock (_gate) return _result; }
    }

    /// <summary>
    /// Take the scope, once: true to start, false when another instance owns it. <paramref name="options"/> null is
    /// no gate. Waits for a predecessor when the arguments carry <see cref="SingleInstanceHostOptions.RestartArgument"/>.
    /// <paramref name="log"/>, when given, is where the guard reports from now on, however early it was made.
    /// </summary>
    public bool Enter(SingleInstanceHostOptions? options, string applicationName, ShenoraPaths paths, IReadOnlyList<string> args, ILogger? log)
    {
        if (log is not null) _log.Target = log;
        if (options is null) return true;
        lock (_gate)
        {
            if (_result is null)
            {
                _guard = new SingleInstanceGuard(applicationName, options.Scope ?? paths.RootDir, _log);
                var wait = args.Contains(options.RestartArgument, StringComparer.Ordinal) ? options.RestartWaitTimeout : TimeSpan.Zero;
                _result = _guard.TryAcquire(wait);
            }
            return _result is not SingleInstanceResult.AlreadyRunning;
        }
    }

    /// <summary>
    /// The losing launch, in the runner: the running instance is asked to come forward (or the app's own
    /// <see cref="SingleInstanceHostOptions.OnSecondInstance"/> runs), and the scope is let go.
    /// </summary>
    public void Lose(ShenoraApplication app, SingleInstanceHostOptions options)
    {
        var guard = _guard!;
        try
        {
#if CEF_WINDOWS
            // This launch holds the foreground, which Windows lets the running instance take only when handed.
            WindowsForeground.AllowAny();
#endif
            if (options.OnSecondInstance is { } onSecond) onSecond(app, guard);
            else guard.ActivateRunning(app.Args);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// The running instance, once its main window is open: each later launch, from the gate's channel or from CEF,
    /// goes to <paramref name="activated"/>. Returns false when the channel could not be opened (logged); CEF's
    /// hand-over still arrives.
    /// </summary>
    public bool Listen(Action<SingleInstanceLaunch> activated, ILogger? log)
    {
        Volatile.Write(ref _activated, activated);
        var guard = _guard;
        if (guard is null || _result is not SingleInstanceResult.Acquired) return true;
        try
        {
            guard.Listen(activated);
            return true;
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] The single-instance channel could not be opened: a later launch will exit "
                + "without bringing this window to the front. Single instance itself is unaffected.", LogLevel.Warning, ex);
            return false;
        }
    }

    /// <summary>A later launch CEF handed over (CEF's UI thread). Before the main window is open there is nothing to
    /// bring forward, and it is dropped.</summary>
    public void Relaunched(SingleInstanceLaunch activation) => Volatile.Read(ref _activated)?.Invoke(activation);

    /// <summary>Let the scope go: last in shutdown, on the thread that took it.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _guard?.Dispose();
            _guard = null;
            _result = null;
            Volatile.Write(ref _activated, null);
        }
    }
}
