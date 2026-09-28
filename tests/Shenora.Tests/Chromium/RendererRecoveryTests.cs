using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>A crashed renderer's recovery, the WebView2 shell's policy, decided without a renderer.</summary>
public class RendererRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_crash_reloads_but_not_a_second_within_the_cooldown()
    {
        var recovery = new RendererRecovery(_ => { });

        Assert.True(recovery.ShouldReload(T0));
        Assert.False(recovery.ShouldReload(T0 + TimeSpan.FromSeconds(5)));
        Assert.True(recovery.ShouldReload(T0 + TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void After_the_budget_it_stops_and_says_so_once()
    {
        var said = new List<string>();
        var recovery = new RendererRecovery(said.Add);
        var at = T0;
        for (var i = 0; i < RendererRecovery.MaxReloads; i++, at += TimeSpan.FromSeconds(11)) Assert.True(recovery.ShouldReload(at));

        Assert.False(recovery.ShouldReload(at));
        Assert.False(recovery.ShouldReload(at + TimeSpan.FromSeconds(11)));
        Assert.Single(said, s => s.Contains("no longer reloading", StringComparison.Ordinal));
    }

    [Fact]
    public void A_successful_load_restores_the_budget()
    {
        var recovery = new RendererRecovery(_ => { });
        var at = T0;
        for (var i = 0; i < RendererRecovery.MaxReloads; i++, at += TimeSpan.FromSeconds(11)) recovery.ShouldReload(at);

        recovery.LoadSucceeded();

        Assert.True(recovery.ShouldReload(at + TimeSpan.FromSeconds(11)));
    }
}
