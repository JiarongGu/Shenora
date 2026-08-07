using Shenora.Sample.Desktop;
using Shenora.Windows;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Samples;

/// <summary>
/// The poll/capture logic over the flow's controller seam (<see cref="CookieLoginDriver.Hooks"/>) —
/// no live browser, the pool-seam precedent. The invariants under test are the sibling
/// post-mortems: freshness gating (a STALE auth cookie must not capture), the final read on
/// close (with the same gate — no anonymous blob), silent-refresh reveal timing, and the
/// identity-keyed baseline.
/// </summary>
public class CookieLoginDriverTests
{
    private sealed class FakeBrowser
    {
        public List<SessionCookie> Jar = [];
        public readonly List<string> Navigated = [];
        public readonly List<bool> Loading = [];
        public int Reveals;
        public int Reads;
        public Action<FakeBrowser>? OnRead; // mutate the jar / trip tokens per read (read 1 = the baseline)

        public CookieLoginDriver.Hooks Hooks => new()
        {
            ReadCookies = (_, _) =>
            {
                Reads++;
                OnRead?.Invoke(this);
                return Task.FromResult((IReadOnlyList<SessionCookie>)Jar.ToList());
            },
            Navigate = (url, _) => { Navigated.Add(url); return Task.CompletedTask; },
            Reveal = () => Reveals++,
            SetLoading = Loading.Add,
        };
    }

    private static CookieLoginDriver CreateFlow(
        TimeSpan? revealDelay = null, bool captureAll = true, params string[] patterns) =>
        new(new CookieLoginDriverOptions
        {
            LoginUrl = "https://login.example.com/signin",
            CookieReadUrl = "https://api.example.com/",
            AuthCookiePatterns = patterns.Length > 0 ? patterns : ["^auth_token$"],
            PollInterval = TimeSpan.FromMilliseconds(10),
            RevealDelay = revealDelay ?? TimeSpan.Zero,
            CaptureAllCookies = captureAll,
        });

    private static SessionCookie Auth(string value, string domain = ".example.com") =>
        new("auth_token", value, domain, "/");

    [Fact]
    public async Task A_stale_auth_cookie_never_captures_and_close_stays_gated()
    {
        // The dead-session incident: an EXPIRED auth cookie still in the persistent profile.
        // Merely existing (unchanged vs the baseline) must not count — before OR after close.
        var browser = new FakeBrowser { Jar = [Auth("stale")] };
        using var cts = new CancellationTokenSource();
        browser.OnRead = b => { if (b.Reads == 3) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateFlow().DriveAsync(browser.Hooks, cts.Token));

        Assert.True(browser.Reads >= 4); // baseline + polls + the final read on close
        Assert.Equal(1, browser.Reveals); // revealed once (grace elapsed), never repeated
    }

    [Fact]
    public async Task A_changed_auth_cookie_value_captures_without_reveal()
    {
        // Silent refresh: the profile's auto-sign-in re-sets the cookie → success while the
        // window is still off-screen ("no interaction ⇒ no window").
        var browser = new FakeBrowser { Jar = [Auth("old"), new SessionCookie("theme", "dark", ".example.com", "/")] };
        browser.OnRead = b => { if (b.Reads == 2) b.Jar[0] = Auth("fresh"); };

        var blob = await CreateFlow(revealDelay: TimeSpan.FromHours(1)).DriveAsync(browser.Hooks, CancellationToken.None);

        Assert.NotNull(blob);
        Assert.Equal(0, browser.Reveals);
        Assert.Equal(["https://login.example.com/signin"], browser.Navigated);
        Assert.Equal([true, false], browser.Loading.Take(2)); // loading dropped once navigation settled

        var cookies = CookieLoginDriver.ReadBlob(blob!);
        Assert.Equal(2, cookies.Count); // CaptureAllCookies default: the whole jar
        Assert.Equal("fresh", cookies.Single(c => c.Name == "auth_token").Value);
    }

    [Fact]
    public async Task A_new_auth_cookie_captures()
    {
        var browser = new FakeBrowser(); // signed out — empty jar at baseline
        browser.OnRead = b => { if (b.Reads == 2) b.Jar.Add(Auth("v1")); };

        var blob = await CreateFlow().DriveAsync(browser.Hooks, CancellationToken.None);

        Assert.NotNull(blob);
        Assert.Equal("v1", CookieLoginDriver.ReadBlob(blob!).Single().Value);
    }

    [Fact]
    public async Task Same_name_on_another_domain_is_a_new_cookie()
    {
        // The baseline is keyed by (domain, path, name) — a same-named cookie appearing on a
        // DIFFERENT domain is a fresh set, not the stale one.
        var browser = new FakeBrowser { Jar = [Auth("v", domain: ".old.example.com")] };
        browser.OnRead = b => { if (b.Reads == 2) b.Jar.Add(Auth("v", domain: ".example.com")); };

        var blob = await CreateFlow().DriveAsync(browser.Hooks, CancellationToken.None);

        Assert.NotNull(blob);
    }

    [Fact]
    public async Task The_final_read_on_close_captures_a_login_finished_just_before()
    {
        // The hold-close rationale: the user logs in and closes the window in one motion — the
        // close is held, and the flow's ONE final read still captures the fresh session.
        var browser = new FakeBrowser();
        using var cts = new CancellationTokenSource();
        browser.OnRead = b =>
        {
            if (b.Reads == 2) cts.Cancel();          // "window closed" while signed out…
            if (b.Reads == 3) b.Jar.Add(Auth("v1")); // …but the final read finds the fresh cookie
        };

        var blob = await CreateFlow().DriveAsync(browser.Hooks, cts.Token);

        Assert.NotNull(blob);
        Assert.Equal("v1", CookieLoginDriver.ReadBlob(blob!).Single().Value);
    }

    [Fact]
    public async Task CaptureAllCookies_false_captures_only_the_matching_cookies()
    {
        var browser = new FakeBrowser { Jar = [new SessionCookie("theme", "dark", ".example.com", "/")] };
        browser.OnRead = b => { if (b.Reads == 2) b.Jar.Add(Auth("v1")); };

        var blob = await CreateFlow(captureAll: false).DriveAsync(browser.Hooks, CancellationToken.None);

        var cookies = CookieLoginDriver.ReadBlob(blob!);
        Assert.Equal("auth_token", Assert.Single(cookies).Name);
    }

    [Fact]
    public async Task Patterns_match_the_name_case_insensitively_as_regex()
    {
        var browser = new FakeBrowser();
        browser.OnRead = b => { if (b.Reads == 2) b.Jar.Add(new SessionCookie("JSESSIONID", "v", ".example.com", "/")); };

        var blob = await CreateFlow(patterns: "session").DriveAsync(browser.Hooks, CancellationToken.None);

        Assert.NotNull(blob); // substring semantics: "session" ~ "JSESSIONID"
    }

    [Fact]
    public void Construction_validates_the_options()
    {
        Assert.Throws<ArgumentException>(() => CreateFlow(patterns: "(")); // invalid regex fails FAST, not mid-login
        Assert.Throws<ArgumentException>(() => new CookieLoginDriver(new CookieLoginDriverOptions
        {
            LoginUrl = "https://x",
            CookieReadUrl = "https://x",
            AuthCookiePatterns = [],
        }));
        Assert.Throws<ArgumentException>(() => new CookieLoginDriver(new CookieLoginDriverOptions
        {
            LoginUrl = "https://x",
            CookieReadUrl = "https://x",
            AuthCookiePatterns = ["a"],
            PollInterval = TimeSpan.Zero,
        }));
    }
}
