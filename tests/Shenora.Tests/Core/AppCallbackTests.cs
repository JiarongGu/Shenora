using Microsoft.Extensions.Logging;
using Shenora;

namespace Shenora.Tests.Core;

/// <summary>
/// The one guard for app-supplied code invoked where an escaping exception is fatal rather than
/// catchable (P5.5 H2). Small surface, but every rule here has an incident behind it — see the type's
/// own docs.
/// </summary>
public class AppCallbackTests
{
    [Fact]
    public void Run_reports_success_and_runs_the_work()
    {
        var ran = false;
        Assert.True(AppCallback.Run(() => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void Run_swallows_a_throwing_callback_and_reports_false()
    {
        Exception? reported = null;

        var ok = AppCallback.Run(() => throw new InvalidOperationException("app bug"), ex => reported = ex);

        // False, not an exception: the caller is a UI-thread event handler with nothing above it.
        Assert.False(ok);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void Run_swallows_a_throwing_error_reporter_too()
    {
        // A failure reporter that throws must not become the crash it reports — otherwise the guard
        // just moves the fatal throw one frame outward.
        var ok = AppCallback.Run(
            () => throw new InvalidOperationException("app bug"),
            _ => throw new ObjectDisposedException("the log sink"));

        Assert.False(ok);
    }

    [Fact]
    public void Run_needs_no_error_sink()
    {
        Assert.False(AppCallback.Run(() => throw new InvalidOperationException("app bug")));
    }

    [Fact]
    public void RunOrDefault_returns_the_callbacks_answer()
    {
        Assert.True(AppCallback.RunOrDefault(() => true, fallback: false));
        Assert.Equal(42, AppCallback.RunOrDefault(() => 42, fallback: -1));
    }

    [Fact]
    public void RunOrDefault_falls_back_when_the_callback_throws()
    {
        Exception? reported = null;

        // The fallback is a POLICY: WndProcHook falls back to false = "did not handle this message",
        // so the window keeps working and the message falls through to the real handling.
        var handled = AppCallback.RunOrDefault<bool>(
            () => throw new InvalidOperationException("hook bug"), fallback: false, ex => reported = ex);

        Assert.False(handled);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void Null_work_is_a_caller_bug_not_a_swallowed_one()
    {
        // The guard covers the APP's mistakes, not the kit's: passing no callback at all is a
        // programming error here and must surface loudly.
        Assert.Throws<ArgumentNullException>(() => AppCallback.Run(null!));
        Assert.Throws<ArgumentNullException>(() => AppCallback.RunOrDefault<int>(null!, 0));
    }

    [Fact]
    public void Log_writes_through_the_apps_logger()
    {
        var lines = new List<string>();

        AppCallback.Log(AppCallback.Logger(lines.Add), () => "hello");

        Assert.Equal(["hello"], lines);
    }

    [Fact]
    public void Log_does_not_even_BUILD_the_message_when_there_is_nowhere_to_write_it()
    {
        var built = 0;

        // Null sink: several call sites interpolate WebView2/COM properties that throw once the
        // underlying object is gone, so "not written" has to mean "not composed" as well.
        AppCallback.Log(null, () => { built++; return "never"; });
        // And a logger that says no to this level, which is what an app's real filtering does.
        AppCallback.Log(new LevelFilter(LogLevel.Warning), () => { built++; return "never"; });

        Assert.Equal(0, built);
    }

    [Fact]
    public void Log_swallows_a_throwing_logger_and_a_throwing_message()
    {
        // Both sit inside a catch that exists to stop a failure escaping, so neither may become one.
        AppCallback.Log(AppCallback.Logger(_ => throw new InvalidOperationException("sink bug")), () => "x");
        AppCallback.Log(AppCallback.Logger(_ => { }), () => throw new InvalidOperationException("message bug"));
    }

    [Fact]
    public void The_delegate_logger_carries_the_EXCEPTION_and_not_just_the_message()
    {
        // 🔴 The reason this kit logs through ILogger at all. A delegate sink takes one string, so an
        // adapter that dropped the exception would give back the identity — type, stack, inner chain —
        // that the change existed to preserve, and the loss would be invisible.
        var lines = new List<string>();
        var failure = new InvalidOperationException("outer", new FormatException("inner"));

        AppCallback.Log(AppCallback.Logger(lines.Add), () => "open failed", LogLevel.Warning, failure);

        var line = Assert.Single(lines);
        Assert.StartsWith("open failed", line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.Contains(nameof(FormatException), line);
    }

    [Fact]
    public void The_delegate_logger_reports_every_level_but_None_enabled()
    {
        // It has no filtering of its own to offer; None is the sentinel meaning "log nothing".
        var logger = AppCallback.Logger(_ => { });

        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.NotNull(logger.BeginScope("anything"));   // the contract forbids returning null
        Assert.Throws<ArgumentNullException>(() => AppCallback.Logger(null!));
    }

    [Fact]
    public void A_reported_failure_defaults_to_Warning_and_a_plain_trace_to_Debug()
    {
        // 🔴 The whole kit's diagnostics ride on this default, and getting it wrong is INVISIBLE: at Debug a
        // swallowed failure is filtered out by an app's default `Information` and simply never appears.
        var seen = new List<LogLevel>();
        var logger = new LevelRecorder(seen);

        AppCallback.Log(logger, () => "just tracing");
        AppCallback.Log(logger, () => "it threw", exception: new InvalidOperationException());
        AppCallback.Log(logger, () => "explicit wins", LogLevel.Error, new InvalidOperationException());

        Assert.Equal([LogLevel.Debug, LogLevel.Warning, LogLevel.Error], seen);
    }

    /// <summary>Records the level each line was written at.</summary>
    private sealed class LevelRecorder(List<LogLevel> seen) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) => seen.Add(logLevel);
    }

    /// <summary>An app logger that answers one level only — the fake the laziness test asserts is USED.</summary>
    private sealed class LevelFilter(LogLevel enabled) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= enabled;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("a disabled level must never reach the sink");
    }
}
