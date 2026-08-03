using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The authorization half of media serving. A media URL carries a path supplied BY THE PAGE, so these are
/// the tests that stand between a page and the disk — and they are pure, which is the point: a security
/// check reachable only through a live webview is a security check nobody runs.
/// </summary>
public class MediaAccessTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "shenora-media-root");

    private static MediaServingOptions Allowing(params string[] roots) =>
        new() { AllowedRoots = roots };

    [Fact]
    public void A_file_inside_an_allowed_root_resolves()
    {
        var wanted = Path.Combine(Root, "clip.mp4");

        var resolved = MediaAccess.ResolveLocal(wanted, Allowing(Root));

        Assert.Equal(Path.GetFullPath(wanted), resolved);
    }

    /// <summary>
    /// The default must serve NOTHING. A handler wired up before its roots are configured has to refuse,
    /// because the alternative default is the whole filesystem.
    /// </summary>
    [Fact]
    public void With_no_allowed_roots_nothing_resolves_at_all()
    {
        Assert.Null(MediaAccess.ResolveLocal(Path.Combine(Root, "clip.mp4"), new MediaServingOptions()));
    }

    [Fact]
    public void A_file_outside_every_allowed_root_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else", "clip.mp4");
        Assert.Null(MediaAccess.ResolveLocal(elsewhere, Allowing(Root)));
    }

    /// <summary>
    /// Traversal, refused before the filesystem is consulted. A `..` that happens to resolve back inside
    /// the root would pass a containment test, and allowing it means the URL shape is no longer what is
    /// being authorised.
    /// </summary>
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("sub/../../secrets.txt")]
    [InlineData(@"sub\..\..\secrets.txt")]
    public void Traversal_segments_are_refused(string relative)
    {
        Assert.Null(MediaAccess.ResolveLocal(Path.Combine(Root, relative), Allowing(Root)));
    }

    /// <summary>
    /// ⚠ The one a prefix comparison gets wrong. Without the separator appended, <c>…-evil</c> passes as a
    /// child of the root — a real defect the desktop provider had to be fixed for, and the reason this
    /// logic was generalised rather than written a second time.
    /// </summary>
    [Fact]
    public void A_sibling_directory_sharing_the_roots_PREFIX_does_not_pass_as_a_child()
    {
        var evilTwin = Root + "-evil";
        var wanted = Path.Combine(evilTwin, "clip.mp4");

        Assert.Null(MediaAccess.ResolveLocal(wanted, Allowing(Root)));
    }

    [Fact]
    public void Several_roots_are_each_honoured()
    {
        var second = Path.Combine(Path.GetTempPath(), "shenora-media-second");
        var wanted = Path.Combine(second, "clip.mp4");

        Assert.Equal(Path.GetFullPath(wanted), MediaAccess.ResolveLocal(wanted, Allowing(Root, second)));
    }

    /// <summary>A malformed ROOT disqualifies itself; it must not disqualify the whole request.</summary>
    [Fact]
    public void One_unusable_root_does_not_block_a_good_one()
    {
        var resolved = MediaAccess.ResolveLocal(Path.Combine(Root, "clip.mp4"), Allowing("", Root));
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "clip.mp4")), resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_path_is_refused_rather_than_defaulted(string? requested)
    {
        Assert.Null(MediaAccess.ResolveLocal(requested, Allowing(Root)));
    }

    // ── the remote half: an SSRF surface, so the DEFAULT is what matters ──────────────────────────────

    /// <summary>
    /// Forgetting to configure a remote policy must DENY. That default is the difference between a missing
    /// feature and an SSRF hole: the host can reach addresses the page cannot.
    /// </summary>
    [Fact]
    public void With_no_remote_policy_every_remote_source_is_denied()
    {
        var options = new MediaServingOptions();

        Assert.False(MediaAccess.IsRemoteAllowed(new Uri("https://example.test/a.mp4"), options));
        Assert.False(MediaAccess.IsRemoteAllowed(new Uri("http://169.254.169.254/latest/meta-data"), options));
    }

    [Fact]
    public void The_apps_predicate_decides_when_one_is_supplied()
    {
        var options = new MediaServingOptions
        {
            AllowRemote = uri => uri.Host.EndsWith(".allowed.test", StringComparison.OrdinalIgnoreCase),
        };

        Assert.True(MediaAccess.IsRemoteAllowed(new Uri("https://cdn.allowed.test/a.mp4"), options));
        Assert.False(MediaAccess.IsRemoteAllowed(new Uri("https://evil.test/a.mp4"), options));
    }

    /// <summary>
    /// A throwing policy must not become an ALLOW. The predicate is app code reached with no caller on the
    /// stack, and "the policy did not say yes" is the only safe reading of a failure.
    /// </summary>
    [Fact]
    public void A_throwing_remote_policy_denies_rather_than_permits()
    {
        var options = new MediaServingOptions
        {
            AllowRemote = _ => throw new InvalidOperationException("policy blew up"),
        };

        Assert.False(MediaAccess.IsRemoteAllowed(new Uri("https://example.test/a.mp4"), options));
    }

    [Fact]
    public void A_null_source_is_denied()
    {
        var options = new MediaServingOptions { AllowRemote = _ => true };
        Assert.False(MediaAccess.IsRemoteAllowed(null, options));
    }
}
