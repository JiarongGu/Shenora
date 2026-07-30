using Shenora.Core;

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
}
