using Shenora.Core.Events;
using Shenora.Windows;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// What a session PUBLISHES: the identity it publishes under, and the payload shapes a subscriber
/// receives.
/// <para>
/// 🔴 <b>WHAT THESE DO NOT COVER, and it is the larger half.</b> Which <c>CoreWebView2</c> event maps to
/// which <see cref="SessionEvents"/> type — the wiring in <c>SessionBrowser.WireSessionEvents</c> — needs
/// a live browser and is sample/e2e territory. <b>Measured, not assumed</b> — deleting the whole
/// <c>DOMContentLoaded</c> handler from that method still compiles and all 23 tests across this file and
/// <c>SessionHookTests</c> still pass. So a green run here says the SCOPE and the CONTRACT are right, not
/// that the browser reports anything.
/// </para>
/// <para>
/// What IS covered end-to-end is the identity lifecycle, because the pool's factory/reset seams let a
/// lease happen without a browser — and that is where the subtle bug lives (a pooled browser outliving
/// the lease that borrowed it).
/// </para>
/// </summary>
public class SessionEventTests
{
    private sealed class Fixture : IDisposable
    {
        public Form Anchor { get; } = new() { ShowInTaskbar = false };

        public Fixture() => _ = Anchor.Handle;

        /// <summary>The instance the factory handed out last — tests that read its scope while idle.</summary>
        public RenderSessionPool.PoolInstance? LastInstance;

        public RenderSessionPool CreatePool() =>
            new(new RenderSessionPoolOptions
            {
                Anchor = Anchor,
                Capacity = 1,   // ONE instance, so the second lease is guaranteed to be the recycled one
                Browser = new SessionBrowserOptions
                {
                    ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"),
                },
            })
            {
                InstanceFactoryOverride = _ => Task.FromResult(
                    LastInstance = new RenderSessionPool.PoolInstance(
                        new Form { ShowInTaskbar = false }, new WebView2Control())),
                ResetOverride = _ => Task.FromResult(true),
            };

        public void Pump(int rounds = 40)
        {
            for (var i = 0; i < rounds; i++) { Application.DoEvents(); Thread.Sleep(5); }
        }

        public void Dispose() => Anchor.Dispose();
    }

    // ── The identity ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_session_id_is_distinct()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => SessionBrowser.NewSessionId()).ToList();

        Assert.Equal(100, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task A_RECYCLED_instance_is_leased_under_a_NEW_identity()
    {
        // 🔴 THE bug this design exists to prevent. Handlers are wired once, when the browser is built,
        // but the browser outlives the lease that borrowed it. Had the scope been captured there, lease
        // #2's page loads would publish under lease #1's id — and a subscriber that had not yet
        // unsubscribed would read another job's navigation as its own. Same class as two concurrent
        // sessions being indistinguishable, only displaced in time, which makes it far harder to see.
        //
        // ⚠ ALL THREE phases are asserted distinct, and that is the point: the identity is reassigned in
        // TWO places (on lease, and on return before the about:blank reset), so an assertion naming only
        // two of them cannot tell which one is doing the work. Measured — deleting the lease-time
        // assignment left an earlier version of this test GREEN, because return's alone still made every
        // lease's id differ. Each sabotage now collapses a different pair.
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();

        var first = await pool.LeaseAsync();
        var instance = fixture.LastInstance!;
        var duringFirstLease = first.Id;
        await first.DisposeAsync();
        fixture.Pump();                       // the return + reset runs on the anchor's message loop
        var whileIdle = instance.Scope;

        var second = await pool.LeaseAsync(); // capacity 1, so this IS the same browser
        try
        {
            string[] phases = [duringFirstLease, whileIdle, second.Id];
            Assert.All(phases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            Assert.Equal(3, phases.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_IDLE_instance_still_has_a_scope_rather_than_none()
    {
        // ⚠ Null is NOT "nobody is listening" on this bus — an unscoped emit is a GLOBAL BROADCAST that
        // reaches every subscriber of every scope. The about:blank reset between two leases raises
        // navigation events, so an idle instance with a null scope would deliver "you just navigated to
        // about:blank" to every session's subscriber. It gets an identity nobody holds instead.
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();

        // A never-leased instance already has one, so there is no window in which the scope is null.
        using var spare = new Form { ShowInTaskbar = false };
        using var spareWeb = new WebView2Control();
        Assert.False(string.IsNullOrWhiteSpace(new RenderSessionPool.PoolInstance(spare, spareWeb).Scope));

        // Then the idle state that actually occurs: read the INSTANCE, not a later lease. An earlier
        // version of this test leased a second time and asserted on that — which reassigns the scope
        // first, so it could never have observed the idle value it claimed to be about.
        var session = await pool.LeaseAsync();
        var idle = fixture.LastInstance!;
        var leaseId = session.Id;
        Assert.Equal(leaseId, idle.Scope);                     // while leased, the two agree

        await session.DisposeAsync();
        fixture.Pump();                                        // return → about:blank reset → re-pooled

        Assert.False(string.IsNullOrWhiteSpace(idle.Scope));
        Assert.NotEqual(leaseId, idle.Scope);
    }

    [Fact]
    public async Task A_subscriber_from_the_PREVIOUS_lease_hears_nothing_from_the_next()
    {
        // 🔴 THE DISCLOSURE INVARIANT, inherited from the interceptor test this replaces: that one
        // asserted a late `OnNetwork` throws, because a tap installed after the lease returned would
        // stream the NEXT tenant's API responses to the previous caller. There are no taps now, so what
        // has to hold is the scope — a subscription that outlives its lease must go DEAF, not get
        // re-pointed at whoever holds the browser next.
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();
        var bus = new EventBus();

        var first = await pool.LeaseAsync();
        var heard = new List<string>();
        using var subscription = bus.SubscribeToModule(SessionEvents.Module, first.Id,
            m => { heard.Add(m.Type); return Task.CompletedTask; });

        await first.DisposeAsync();
        fixture.Pump();
        var second = await pool.LeaseAsync();
        try
        {
            // The recycled browser publishes under the NEW lease's identity…
            await bus.EmitAsync(SessionEvents.Module, SessionEvents.NavigationCompleted,
                new SessionNavigationResult("https://next-tenants-page/", true, "Unknown"), scope: second.Id);

            Assert.Empty(heard);   // …and the previous caller hears none of it.
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_session_publishes_under_its_OWN_id_and_a_scoped_subscriber_hears_only_that()
    {
        // The subscription shape an adopter writes, exercised against the real bus rather than described
        // in a doc comment: two sessions, one bus, and each subscriber must hear exactly one of them.
        var bus = new EventBus();
        var mine = new List<string>();
        var theirs = new List<string>();

        using var _ = bus.SubscribeToModule(SessionEvents.Module, "session-a",
            m => { mine.Add(m.Type); return Task.CompletedTask; });
        using var __ = bus.SubscribeToModule(SessionEvents.Module, "session-b",
            m => { theirs.Add(m.Type); return Task.CompletedTask; });

        await bus.EmitAsync(SessionEvents.Module, SessionEvents.NavigationCompleted,
            new SessionNavigationResult("https://a/", true, "Unknown"), scope: "session-a");
        await bus.EmitAsync(SessionEvents.Module, SessionEvents.TitleChanged,
            new SessionSource("https://b/", "B"), scope: "session-b");

        Assert.Equal([SessionEvents.NavigationCompleted], mine);
        Assert.Equal([SessionEvents.TitleChanged], theirs);
    }

    // ── The catalogue ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_two_event_types_share_a_name()
    {
        // A duplicated constant would silently merge two events: subscribers to one would receive the
        // other, and nothing else in the build would notice.
        var types = typeof(SessionEvents).GetFields()
            .Where(f => f.IsLiteral && f.Name != nameof(SessionEvents.Module))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(types);                                            // the parser self-check
        Assert.Equal(types.Count, types.Distinct(StringComparer.Ordinal).Count());
        Assert.All(types, t => Assert.Matches("^[A-Z_]+$", t));            // wire-visible, so pin the shape
    }

    [Fact]
    public void The_module_name_carries_the_kit_prefix()
    {
        // The reserved prefix is how an app's own module can never collide with one the kit publishes.
        Assert.StartsWith("SHENORA.", SessionEvents.Module, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_keeps_REPEATED_headers_instead_of_collapsing_them()
    {
        // 🔴 Why Headers is a list and not a dictionary. `Set-Cookie` legitimately repeats — a login
        // response commonly sets a session cookie AND a CSRF cookie in two separate header lines — and a
        // map keyed by name keeps one and drops the rest, losing exactly what this event exists to carry.
        var response = new SessionResponse("https://login/", 302, "Found",
        [
            new("Set-Cookie", "sid=1; HttpOnly"),
            new("Set-Cookie", "csrf=2"),
            new("Location", "https://app/"),
        ], "");

        Assert.Equal(2, response.Headers.Count(h => h.Key == "Set-Cookie"));
    }

    [Fact]
    public void A_response_does_not_dump_its_headers_when_printed()
    {
        // Same hazard as SessionAuthRequest's redacting ToString, arrived at from the other direction: a
        // record prints every property, and this one's properties include session cookies. It happens to
        // be safe — a List<T> prints its TYPE, not its contents — but "happens to be" is why it is pinned
        // rather than assumed, since swapping the list for an array or a value tuple would leak silently.
        var printed = new SessionResponse("https://login/", 302, "Found",
            [new("Set-Cookie", "sid=SECRET-VALUE")], "").ToString();

        Assert.DoesNotContain("SECRET-VALUE", printed, StringComparison.Ordinal);
        Assert.Contains("https://login/", printed, StringComparison.Ordinal);   // still a useful diagnostic
    }

    [Fact]
    public void A_process_report_separates_the_ROUTINE_failures_from_the_terminal_ones()
    {
        // The event is published for every kind, unlike the onProcessFailed callback — a subscriber that
        // wants to log a recoverable GPU reset should be able to. `Terminal` is the whole difference, and
        // it comes from the same allow-list the callback uses.
        var gpu = new SessionProcessReport("GpuProcessExited", "Crashed", 1, Terminal: false);
        var dead = new SessionProcessReport("RenderProcessExited", "Crashed", 1, Terminal: true);

        Assert.False(gpu.Terminal);
        Assert.True(dead.Terminal);
        Assert.True(SessionBrowser.IsTerminal(Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited));
        Assert.False(SessionBrowser.IsTerminal(Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.GpuProcessExited));
    }
}
