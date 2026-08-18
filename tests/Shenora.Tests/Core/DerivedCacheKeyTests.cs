using Shenora.Engine;

namespace Shenora.Tests.Core;

/// <summary>
/// 🔴 THE FORMAT IS THE CONTRACT, so it is pinned by VALUE rather than by property.
/// <para>
/// This type is public because an adopter's cache key must AGREE with the kit's byte for byte — the
/// first adoption harvest (Yaorin, 0.10.0 → 0.11.0) found it made internal and had to re-derive it by
/// hand. Every knob below (separator normalisation, case, field order, tick precision, the 8-byte
/// truncation) looks optional and produces a <i>valid-looking</i> key that matches nothing when it
/// drifts, which silently orphans every cached artefact on every device with no error anywhere.
/// </para>
/// <para>
/// ⚠ So a change that fails these tests is not a test to update — it is a BREAKING change to every
/// deployed cache, and it belongs in <c>CHANGELOG.md</c> under <c>### Breaking</c> with a note that
/// existing caches are orphaned.
/// </para>
/// </summary>
public class DerivedCacheKeyTests
{
    // A fixed instant, so the pinned values below cannot drift with the clock or the machine's zone.
    private static readonly DateTime Mtime = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_key_format_is_pinned_by_value()
    {
        // 🔴 GOLDEN VALUES. If one of these changes, read this class's summary before touching it.
        Assert.Equal("051050403c46356d", DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime));
        Assert.Equal("f3b28da7ebdc6d09", DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime, "hls"));
    }

    [Fact]
    public void One_file_spelled_several_ways_is_ONE_key()
    {
        // Separator and case normalisation: a Windows path arrives spelled both ways from a picker, a
        // config file and a URL, and three cache entries for one file is the bug this prevents.
        var canonical = DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime);
        Assert.Equal(canonical, DerivedCacheKey.For(@"C:\media\film.mkv", 1234, Mtime));
        Assert.Equal(canonical, DerivedCacheKey.For("C:/Media/Film.MKV", 1234, Mtime));
    }

    [Fact]
    public void Length_and_mtime_and_variant_each_change_the_key()
    {
        var baseline = DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime);

        // Length and mtime fail differently — a copy preserves mtime while changing bytes, an edit
        // changes bytes without moving the length — which is why both are in the key.
        Assert.NotEqual(baseline, DerivedCacheKey.For("C:/media/film.mkv", 1235, Mtime));
        Assert.NotEqual(baseline, DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime.AddTicks(1)));
        // And a variant, so a thumbnail can never be served as a converted stream.
        Assert.NotEqual(baseline, DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime, "thumb-720"));
    }

    [Fact]
    public void A_local_mtime_keys_the_same_as_its_UTC_instant()
    {
        // The same moment expressed in local time must not produce a second cache entry.
        var local = Mtime.ToLocalTime();
        Assert.Equal(DerivedCacheKey.For("C:/media/film.mkv", 1234, Mtime),
                     DerivedCacheKey.For("C:/media/film.mkv", 1234, local));
    }

    [Fact]
    public void A_blank_path_or_a_negative_length_is_a_caller_bug()
    {
        Assert.Throws<ArgumentException>(() => DerivedCacheKey.For("  ", 1, Mtime));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedCacheKey.For("C:/x", -1, Mtime));
    }
}
