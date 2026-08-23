using Shenora.Core.Events;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The lifecycle reporter — which exists for ONE number the page cannot measure, so almost everything
/// worth asserting is about that number being right or honestly absent.
/// </summary>
public class AppLifecycleTests
{
    private sealed class Recording
    {
        private readonly List<EventMessage> _seen = [];
        public EventBus Bus { get; }
        public IReadOnlyList<EventMessage> Seen { get { lock (_seen) return [.. _seen]; } }

        public Recording()
        {
            Bus = new EventBus();
            Bus.SubscribeToAll(message =>
            {
                lock (_seen) _seen.Add(message);
                return Task.CompletedTask;
            });
        }

        /// <summary>The report on the last RESUMED event, waiting for the fire-and-forget emit.</summary>
        public async Task<AppLifecycleReport> ResumeAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var resumed = Seen.LastOrDefault(m => m.Type == AppLifecycle.ResumedType);
                if (resumed is not null) return Assert.IsType<AppLifecycleReport>(resumed.Payload);
                await Task.Delay(10);
            }
            Assert.Fail("no RESUMED event was published");
            return null!;
        }
    }

    [Fact]
    public async Task A_resume_reports_HOW_LONG_the_app_was_away()
    {
        // The whole reason this type exists: a throttled, possibly frozen page cannot time its own
        // absence, and the duration is what an app branches on.
        var recording = new Recording();
        var lifecycle = new AppLifecycle(recording.Bus);

        lifecycle.ReportStopped();
        await Task.Delay(60);
        lifecycle.ReportResumed();

        var report = await recording.ResumeAsync();
        Assert.NotNull(report.BackgroundMilliseconds);
        // A generous floor rather than a window: this asserts the clock RAN, not how fast the box is.
        Assert.True(report.BackgroundMilliseconds >= 40,
            $"expected at least the elapsed delay, got {report.BackgroundMilliseconds}ms");
    }

    [Fact]
    public async Task A_resume_with_NO_preceding_stop_reports_null_rather_than_zero()
    {
        // 🔴 Null and zero are different answers and the difference is load-bearing. The first resume
        // after launch has nothing to measure; reporting 0 would read as "away for no time", so a page
        // whose rule is `away > 30s → reconnect` would skip the reconnect exactly once, at startup,
        // which is the one time its socket definitely does not exist yet.
        var recording = new Recording();
        var lifecycle = new AppLifecycle(recording.Bus);

        lifecycle.ReportResumed();

        Assert.Null((await recording.ResumeAsync()).BackgroundMilliseconds);
    }

    [Fact]
    public async Task A_SECOND_stop_without_a_resume_keeps_the_first_timestamp()
    {
        // Otherwise a shell that reports the transition twice — or a Window subscribed to twice, which
        // MAUI's process-scoped Window makes easy — restarts the clock and the page is told it was away
        // for the gap between the two stops instead of for the whole absence.
        var recording = new Recording();
        var lifecycle = new AppLifecycle(recording.Bus);

        lifecycle.ReportStopped();
        await Task.Delay(60);
        lifecycle.ReportStopped();
        lifecycle.ReportResumed();

        var report = await recording.ResumeAsync();
        Assert.True(report.BackgroundMilliseconds >= 40,
            $"the second stop restarted the clock: {report.BackgroundMilliseconds}ms");
    }

    [Fact]
    public async Task The_measurement_is_CONSUMED_so_a_second_resume_does_not_repeat_it()
    {
        var recording = new Recording();
        var lifecycle = new AppLifecycle(recording.Bus);

        lifecycle.ReportStopped();
        await Task.Delay(30);
        lifecycle.ReportResumed();
        Assert.NotNull((await recording.ResumeAsync()).BackgroundMilliseconds);

        // A resume the shell reports again — a duplicate subscription, a re-attach — must not re-serve
        // the old duration as though the app had just come back from it.
        lifecycle.ReportResumed();
        Assert.Null((await recording.ResumeAsync()).BackgroundMilliseconds);
    }

    [Fact]
    public async Task Both_transitions_are_published_under_the_lifecycle_module()
    {
        var recording = new Recording();
        var lifecycle = new AppLifecycle(recording.Bus);

        lifecycle.ReportStopped();
        lifecycle.ReportResumed();
        await recording.ResumeAsync();

        Assert.All(recording.Seen, message => Assert.Equal(AppLifecycle.Module, message.Module));
        Assert.Contains(recording.Seen, m => m.Type == AppLifecycle.StoppedType);
        // STOPPED is signal-only: a payload would be a number nobody can act on yet.
        Assert.Null(recording.Seen.First(m => m.Type == AppLifecycle.StoppedType).Payload);
    }
}
