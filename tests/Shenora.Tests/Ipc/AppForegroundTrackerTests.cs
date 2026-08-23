using Shenora.Core.Events;
using Shenora.Mobile;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The half of the foreground report that a device run is worst at checking: not "does a background
/// arrive" — one press answers that — but the cases that only appear after a rotation, a second window
/// or a launch, each of which reports a transition that never happened.
/// <para>
/// The type under test ships in <c>src/Shenora.Mobile/</c> and is compiled into this suite by a linked
/// <c>&lt;Compile&gt;</c>, because that folder has no project to reference.
/// </para>
/// </summary>
public class AppForegroundTrackerTests
{
    /// <summary>The events a tracker's reports actually produced, in order.</summary>
    private sealed class Recording
    {
        private readonly List<EventMessage> _seen = [];
        public AppLifecycle Lifecycle { get; }

        public Recording()
        {
            var bus = new EventBus();
            bus.SubscribeToAll(message =>
            {
                lock (_seen) _seen.Add(message);
                return Task.CompletedTask;
            });
            Lifecycle = new AppLifecycle(bus);
        }

        /// <summary>Everything published so far.</summary>
        public IReadOnlyList<EventMessage> Messages { get { lock (_seen) return [.. _seen]; } }

        /// <summary>
        /// The transition types seen so far, once the fire-and-forget emits have landed.
        /// <para>
        /// ⚠ It SETTLES after reaching <paramref name="expected"/> rather than returning on the count.
        /// Every assertion here is "exactly these", and returning the moment the expected number arrives
        /// makes a test that publishes one too many pass whenever it wins the race — which it did: a
        /// sabotage that emitted three events was caught only because the reader happened to be late.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<string>> TypesAsync(int expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                lock (_seen)
                {
                    if (_seen.Count >= expected) break;
                }
                await Task.Delay(10);
            }
            await Task.Delay(50);
            lock (_seen) return [.. _seen.Select(m => m.Type)];
        }
    }

    [Fact]
    public async Task A_background_and_return_reports_exactly_one_pair()
    {
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: false);
        tracker.Shown();

        Assert.Equal([AppLifecycle.StoppedType, AppLifecycle.ResumedType], await recording.TypesAsync(2));
    }

    [Fact]
    public async Task A_tracker_is_seeded_ON_SCREEN_so_the_FIRST_signal_it_ever_sees_can_be_the_departure()
    {
        // 🔴 The page builds this while the app is up, so the show that put it there happened before
        // anything was listening. Counting from zero would swallow the first departure — and a feature
        // that reports nothing until the second background is indistinguishable from one that is broken,
        // which is the failure this whole path exists to fix.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: false);

        Assert.Equal([AppLifecycle.StoppedType], await recording.TypesAsync(1));
    }

    [Fact]
    public async Task A_RECREATION_reports_nothing_at_all()
    {
        // A rotation, a font-scale change, a locale change: the platform destroys the window and builds
        // it again. Reported, it reads as an absence — and the resume carries a DURATION, so a page whose
        // rule is `away > 30s → reconnect` reconnects on every rotation instead.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: true);
        tracker.Shown();

        await Task.Delay(50);
        Assert.Empty(await recording.TypesAsync(0));
    }

    [Fact]
    public async Task A_recreation_does_not_disarm_the_next_REAL_departure()
    {
        // 🔴 The half of the recreation rule that a rotation on a device would not reveal: suppressing
        // the report must leave the tracker in the state it was in, or the next real background is
        // suppressed too — silently, and only after the device has been rotated once.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: true);
        tracker.Shown();

        tracker.Hidden(forRecreation: false);

        Assert.Equal([AppLifecycle.StoppedType], await recording.TypesAsync(1));
    }

    [Fact]
    public async Task A_repeated_hide_does_not_re_report_a_departure()
    {
        // Two reporters on one process-scoped Window — which a page rebuild produces — each deliver the
        // same transition. The kit displaces the older one, and this is what makes that a belt rather
        // than the only strap.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: false);
        tracker.Hidden(forRecreation: false);

        await Task.Delay(50);
        Assert.Equal([AppLifecycle.StoppedType], await recording.TypesAsync(1));
    }

    [Fact]
    public async Task A_repeated_show_does_not_re_report_a_return()
    {
        // The platform can raise the same state twice — a re-attach, a second subscription that has not
        // been displaced yet. A duplicate RESUMED is worse than noise: it carries a null duration, which
        // a page reads as "I could not measure it" and reconnects on.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: false);
        tracker.Shown();
        tracker.Shown();

        await Task.Delay(50);
        Assert.Equal([AppLifecycle.StoppedType, AppLifecycle.ResumedType], await recording.TypesAsync(2));
    }

    [Fact]
    public async Task The_pair_carries_the_duration_the_page_cannot_measure()
    {
        // The tracker decides WHETHER to report; this is the one assertion that it is still wired to the
        // reporter that owns the number, rather than emitting two bare signals.
        var recording = new Recording();
        var tracker = new AppForegroundTracker(recording.Lifecycle);

        tracker.Hidden(forRecreation: false);
        await Task.Delay(60);
        tracker.Shown();

        await recording.TypesAsync(2);
        var report = Assert.IsType<AppLifecycleReport>(
            recording.Messages.Last(m => m.Type == AppLifecycle.ResumedType).Payload);
        Assert.NotNull(report.BackgroundMilliseconds);
        Assert.True(report.BackgroundMilliseconds >= 40,
            $"expected at least the elapsed delay, got {report.BackgroundMilliseconds}ms");
    }
}
