using Shenora;
using Shenora.Engine;

namespace Shenora.Tests.Core;

/// <summary>
/// The cache key all three surveyed implementations arrived at independently. The tests that matter are
/// the ones a path-only key would pass — because a path-only key is the bug, and it looks fine until a
/// user replaces a file.
/// </summary>
public class DerivedCacheKeyTests
{
    private static readonly DateTime Monday = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_same_source_produces_the_same_key()
    {
        Assert.Equal(DerivedCacheKey.For("C:/media/a.mkv", 100, Monday),
                     DerivedCacheKey.For("C:/media/a.mkv", 100, Monday));
    }

    /// <summary>
    /// The whole reason mtime is in the key. A path-only key survives an overwrite and then serves
    /// yesterday's conversion of a file the user has replaced — a stale-cache bug that reads as corruption
    /// and never shows up in testing, because a test rarely overwrites its own fixture in place.
    /// </summary>
    [Fact]
    public void Replacing_the_file_changes_the_key_even_at_the_same_path_and_size()
    {
        var before = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday);
        var after = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday.AddSeconds(1));

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Length is in the key as well as mtime because the two fail differently: a copy or a restore can
    /// preserve an mtime while changing the bytes.
    /// </summary>
    [Fact]
    public void A_changed_length_changes_the_key_even_when_the_mtime_did_not_move()
    {
        Assert.NotEqual(DerivedCacheKey.For("C:/media/a.mkv", 100, Monday),
                        DerivedCacheKey.For("C:/media/a.mkv", 101, Monday));
    }

    /// <summary>
    /// One source, several derived forms. Without the variant a thumbnail could be served as a converted
    /// stream — same source, same key.
    /// </summary>
    [Fact]
    public void Different_variants_of_one_source_never_collide()
    {
        var mp4 = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday, "mp4");
        var thumb = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday, "thumb-720");
        var none = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday);

        Assert.NotEqual(mp4, thumb);
        Assert.NotEqual(mp4, none);
    }

    /// <summary>
    /// On Windows the same file legitimately arrives spelled several ways, and each spelling would
    /// otherwise get its own cache entry — duplicated work that is easy to ship and hard to notice.
    /// </summary>
    [Theory]
    [InlineData(@"C:\media\a.mkv")]
    [InlineData("c:/MEDIA/A.mkv")]
    [InlineData(@"c:\Media\a.MKV")]
    public void Separator_and_case_variants_of_one_path_share_a_key(string spelling)
    {
        Assert.Equal(DerivedCacheKey.For("C:/media/a.mkv", 100, Monday),
                     DerivedCacheKey.For(spelling, 100, Monday));
    }

    /// <summary>Different files must not share an entry, however similar their names.</summary>
    [Fact]
    public void Different_paths_produce_different_keys()
    {
        Assert.NotEqual(DerivedCacheKey.For("C:/media/a.mkv", 100, Monday),
                        DerivedCacheKey.For("C:/media/b.mkv", 100, Monday));
    }

    /// <summary>
    /// A local timestamp and its UTC equivalent are the same instant, so they must not produce two entries
    /// for one file — the kind of duplication that appears only on a machine outside UTC.
    /// </summary>
    [Fact]
    public void The_same_instant_in_another_kind_gives_the_same_key()
    {
        var utc = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday);
        var asLocal = DerivedCacheKey.For("C:/media/a.mkv", 100, Monday.ToLocalTime());

        Assert.Equal(utc, asLocal);
    }

    [Fact]
    public void The_key_is_filesystem_safe_and_a_fixed_length()
    {
        var key = DerivedCacheKey.For(@"C:\media\a file with spaces & 中文.mkv", 100, Monday, "mp4");

        Assert.Equal(16, key.Length);
        Assert.True(key.All(char.IsAsciiHexDigitLower), key);
    }

    [Fact]
    public void An_empty_path_or_negative_length_is_refused_rather_than_hashed()
    {
        Assert.Throws<ArgumentException>(() => DerivedCacheKey.For("  ", 100, Monday));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedCacheKey.For("C:/a.mkv", -1, Monday));
    }
}
