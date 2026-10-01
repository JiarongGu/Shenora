namespace Shenora.Tests.Sessions;

/// <summary>
/// Which aborted navigation a session browser hands to the session waiting on its own. Starting a navigation while
/// another loads aborts that one, and its completion can arrive after the new one began: a pool reset, and a navigation
/// after the soft cap, returned before their page loaded. Each shell keeps the rule in a copy of its own (neither sees
/// the other's internals), so every case runs against both.
/// </summary>
public class NavigationChainTests
{
    public static TheoryData<string> Shells => new() { "WebView2", "Chromium" };

    private sealed record Chain(Action<string> Requested, Action<string> Started, Func<string, bool, bool> Replaced);

    private static Chain Make(string shell)
    {
        if (shell == "WebView2")
        {
            var windows = new Shenora.Windows.NavigationChain();
            return new(windows.Requested, windows.Started, windows.Replaced);
        }
        var chromium = new Shenora.Chromium.Host.NavigationChain();
        return new(chromium.Requested, chromium.Started, chromium.Replaced);
    }

    [Theory, MemberData(nameof(Shells))]
    public void The_abort_of_the_navigation_it_replaced_is_not_its_own_before_or_after_it_starts(string shell)
    {
        var chain = Make(shell);
        chain.Requested("https://new.example/");
        Assert.True(chain.Replaced("https://old.example/slow", true));
        chain.Started("https://new.example/");
        Assert.True(chain.Replaced("https://old.example/slow", true));
    }

    // The review's case: the pool's policy cancels a redirect to an authority its guard never vetted, and a download
    // can sit behind a redirect. Each failure carries the HOP's address; skipped, the wait ran to its cap.
    [Theory, MemberData(nameof(Shells))]
    public void A_refused_or_downloaded_redirect_hop_is_its_own(string shell)
    {
        var chain = Make(shell);
        chain.Requested("https://a.example/login");
        chain.Started("https://a.example/login");
        chain.Started("https://b.example/next");
        Assert.False(chain.Replaced("https://b.example/next", true));
    }

    [Theory, MemberData(nameof(Shells))]
    public void The_engine_s_own_spelling_of_the_address_is_its_own(string shell)
    {
        var chain = Make(shell);
        chain.Requested("https://bücher.example/");
        chain.Started("https://xn--bcher-kva.example/");
        Assert.False(chain.Replaced("https://xn--bcher-kva.example/", true));
        chain.Requested("https://new.example");
        Assert.False(chain.Replaced("https://new.example/", true));   // alike once parsed
    }

    [Theory, MemberData(nameof(Shells))]
    public void Any_other_failure_is_its_own(string shell)
    {
        var chain = Make(shell);
        chain.Requested("https://new.example/");
        Assert.False(chain.Replaced("https://old.example/slow", false));
    }

    [Theory, MemberData(nameof(Shells))]
    public void Before_any_Navigate_nothing_was_replaced(string shell)
    {
        var chain = Make(shell);
        chain.Started("https://page.example/");
        Assert.False(chain.Replaced("https://other.example/", true));
    }

    [Theory, MemberData(nameof(Shells))]
    public void A_new_Navigate_forgets_the_last_one_s_hops(string shell)
    {
        var chain = Make(shell);
        chain.Requested("about:blank");
        chain.Started("https://page.example/");
        chain.Requested("about:blank");   // the pool's reset between leases
        Assert.True(chain.Replaced("https://page.example/", true));
        Assert.False(chain.Replaced("about:blank", true));
    }

    [Theory, MemberData(nameof(Shells))]
    public void The_address_asked_for_outlasts_a_long_chain(string shell)
    {
        var chain = Make(shell);
        chain.Requested("https://asked.example/");
        for (var i = 0; i < 40; i++) chain.Started($"https://hop{i}.example/");
        Assert.False(chain.Replaced("https://asked.example/", true));
        Assert.False(chain.Replaced("https://hop39.example/", true));
        Assert.True(chain.Replaced("https://hop0.example/", true));   // the oldest went
    }
}
