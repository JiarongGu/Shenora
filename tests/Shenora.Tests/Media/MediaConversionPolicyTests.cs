using Shenora;
using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Modules.Media;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Media;

/// <summary>
/// The conversion pipeline's POLICY: what the kit CLAIMS, intersected with what the DEVICE can do.
///
/// <para>
/// 🔴 <b>These two questions were one, and the fusion cost a day on 2026-08-13.</b> <c>CanConvert</c>
/// answered by CONSTRUCTING the converter's decoder and encoder, which produced both failures in the same
/// evening: an over-claim (a promise made from an encoder alone, so the muxer failed after accepting a
/// track and spending the walk) and an under-claim (a refusal of a codec that merely could not open a
/// session without its file's ESDS). Every test here pins one half of the split that replaced it.
/// </para>
/// <para>
/// ⚠ The pipeline had NO direct tests before this file. It is the type an app composes its codecs on.
/// </para>
/// </summary>
public sealed class MediaConversionPolicyTests
{
    /// <summary>A converter that records whether it was ever ASKED — the point of a cheap "no".</summary>
    private sealed class Counting
    {
        public int Asked;

        public MediaConversionMiddleware Middleware => (source, _) =>
        {
            Asked++;
            return null;
        };
    }

    /// <summary>A stand-in device. ⚠ Only what it is ASKED about, so a wrong question shows up as false.</summary>
    private sealed class FakeDevice(params string[] decodable) : IMediaCapability
    {
        private readonly HashSet<MediaStreamCodec> _decodable = new(decodable.Select(n => (MediaStreamCodec)n));

        public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => _decodable;

        public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => _decodable;
    }

    /// <summary>
    /// 🔴 <b>AN UNCLAIMED CODEC COSTS NOTHING TO REFUSE — the converter is never even asked.</b> That is the
    /// whole reason a declaration exists: answering "which pictures does this shell support?" used to build
    /// two hardware codec instances per codec, so nothing asked, and a shell with no video converter at all
    /// went unnoticed.
    /// </summary>
    [Fact]
    public void An_unclaimed_codec_is_refused_WITHOUT_asking_the_converter()
    {
        var converter = new Counting();
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(converter.Middleware, [new MediaStreamClaim(MediaStreamKind.Audio, "ac3")]);

        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));
        Assert.Equal(0, converter.Asked);
    }

    /// <summary>
    /// ⚠ <b>A CLAIM IS NOT A PROMISE.</b> The device still decides, which is the half that stops an
    /// over-claim: the kit offering `mpeg4` says nothing about whether this hardware can decode it.
    /// </summary>
    [Fact]
    public void A_claimed_codec_the_DEVICE_cannot_decode_is_still_refused()
    {
        var converter = new Counting();
        var pipeline = new MediaConversionPipeline(new FakeDevice("h263"));
        pipeline.Use(converter.Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);

        Assert.False(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));

        // 🔴 And not by building anything: the device answered, so the converter is never constructed. This
        // is the assertion that would have caught the real defect — a promise made from an encoder alone.
        Assert.Equal(0, converter.Asked);
    }

    /// <summary>
    /// The other direction, and it is the one that keeps the kit narrow: the device can, the kit does not
    /// OFFER, so the answer is no. A claim is final in the negative.
    /// </summary>
    [Fact]
    public void A_codec_the_device_CAN_decode_is_refused_when_the_kit_does_not_claim_it()
    {
        var pipeline = new MediaConversionPipeline(new FakeDevice("h264"));
        pipeline.Use(new Counting().Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);

        // h264 is exactly the case: every device decodes it, and the kit deliberately refuses to CONVERT it
        // because MP4 carries it and the remuxer copies it losslessly.
        Assert.False(pipeline.CanConvert(MediaStreamKind.Video, "h264"));
    }

    /// <summary>Both halves agreeing is the only yes.</summary>
    [Fact]
    public void A_claimed_codec_the_device_CAN_decode_is_accepted()
    {
        var pipeline = new MediaConversionPipeline(new FakeDevice("mpeg4", "h263"));
        pipeline.Use(new Counting().Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);

        Assert.True(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));
    }

    /// <summary>
    /// ⚠ <b>THE KIND IS PART OF THE CLAIM.</b> The same codec name can mean different things per kind, and
    /// a claim that ignored the kind would let an audio converter answer for a picture.
    /// </summary>
    [Fact]
    public void A_claim_for_one_KIND_does_not_answer_for_the_other()
    {
        var pipeline = new MediaConversionPipeline(new FakeDevice("mpeg4"));
        pipeline.Use(new Counting().Middleware, [new MediaStreamClaim(MediaStreamKind.Audio, "mpeg4")]);

        Assert.False(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));
    }

    /// <summary>
    /// 🔴 <b>BACK-COMPAT, and it is not cosmetic: a converter registered WITHOUT claims must still be asked
    /// about anything.</b> Every converter written before claims existed uses that overload, and treating
    /// "declared nothing" as "supports nothing" would silently disable all of them — absent is UNKNOWN,
    /// never NONE.
    /// </summary>
    [Fact]
    public void A_converter_registered_without_claims_is_still_asked_about_anything()
    {
        var converter = new Counting();
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(converter.Middleware);

        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));   // it declined
        Assert.Equal(1, converter.Asked);                                  // but it WAS asked
    }

    /// <summary>
    /// 🔴 <b>PER CONVERTER, not one flat list — this is the inconsistency a flat list would have created.</b>
    /// A claim-less converter beside a declaring one must keep its wildcard: otherwise <c>CanConvert</c>
    /// answers false for codecs <c>Begin</c> would happily convert, and the two disagree.
    /// </summary>
    [Fact]
    public void A_claim_less_converter_keeps_its_wildcard_beside_a_declaring_one()
    {
        var declaring = new Counting();
        var wildcard = new Counting();
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(declaring.Middleware, [new MediaStreamClaim(MediaStreamKind.Audio, "ac3")]);
        pipeline.Use(wildcard.Middleware);

        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));

        // Asked, because the wildcard registration covers it — the flat-list version would have refused
        // without asking either of them.
        Assert.True(wildcard.Asked > 0, "a converter registered without claims stopped being asked once a "
            + "sibling declared some — CanConvert and Begin now disagree");
    }

    /// <summary>
    /// 🔴 <b>THE DEFECT A DEVICE FOUND, and this test is why it will not come back.</b> A wildcard must fall
    /// back to ASKING the chain — never to the device's answer alone.
    ///
    /// <para>
    /// Measured on an iPhone 17 Pro, 2026-08-13: with the audio converter registered claim-less, its
    /// wildcard made every other claim moot and the device became the only gate. <c>h264</c> then reported
    /// <c>accepted=True</c> — every phone decodes it, and this kit deliberately refuses to CONVERT it
    /// because MP4 carries it and the remuxer copies it losslessly. A two-state claim check could not tell
    /// "declared" from "might handle it", and promising h264 means re-encoding a film that could have been
    /// remuxed.
    /// </para>
    /// </summary>
    [Fact]
    public void A_WILDCARD_falls_back_to_asking_rather_than_trusting_the_device()
    {
        var declaring = new Counting();
        var wildcard = new Counting();

        // The device decodes h264, as every real one does.
        var pipeline = new MediaConversionPipeline(new FakeDevice("h264", "mpeg4"));
        pipeline.Use(declaring.Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);
        pipeline.Use(wildcard.Middleware);

        // Nothing DECLARED h264, so the device's yes must not carry it. Both converters decline it.
        Assert.False(pipeline.CanConvert(MediaStreamKind.Video, "h264"));

        // And it got there by ASKING, which is the pre-claims behaviour the wildcard has to preserve.
        Assert.True(wildcard.Asked > 0, "a wildcard answered from the DEVICE instead of asking the chain — "
            + "that is the h264 over-claim measured on hardware");
    }

    /// <summary>
    /// ⚠ An EXPLICIT claim still beats a wildcard to the device, so declaring keeps its cheap answer even
    /// when a claim-less converter shares the chain.
    /// </summary>
    [Fact]
    public void An_explicit_claim_still_answers_from_the_device_when_a_wildcard_is_present()
    {
        var declaring = new Counting();
        var wildcard = new Counting();
        var pipeline = new MediaConversionPipeline(new FakeDevice("mpeg4"));
        pipeline.Use(declaring.Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);
        pipeline.Use(wildcard.Middleware);

        Assert.True(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));
        Assert.Equal(0, declaring.Asked);
        Assert.Equal(0, wildcard.Asked);
    }

    /// <summary>
    /// ⚠ <b>A removed converter takes its claims with it.</b> Leaving them behind would let
    /// <c>CanConvert</c> keep saying yes to a codec whose converter is gone — the "outlives the feature it
    /// served" bug the registration's removability exists to prevent.
    /// </summary>
    [Fact]
    public void Disposing_a_registration_removes_its_CLAIMS_too()
    {
        var pipeline = new MediaConversionPipeline(new FakeDevice("mpeg4"));
        var registration = pipeline.Use(new Counting().Middleware,
                                       [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);

        Assert.True(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));
        Assert.Single(pipeline.Claims);

        registration.Dispose();

        Assert.Empty(pipeline.Claims);
        Assert.False(pipeline.CanConvert(MediaStreamKind.Video, "mpeg4"));
    }

    /// <summary>
    /// The claims are INSPECTABLE, which is the cheap answer to "what does this shell support?" that nothing
    /// could ask before.
    /// </summary>
    [Fact]
    public void The_registered_claims_can_be_read_without_building_a_codec()
    {
        var converter = new Counting();
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(converter.Middleware,
            [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4"), new MediaStreamClaim(MediaStreamKind.Video, "h263")]);

        Assert.Equal(2, pipeline.Claims.Count);
        Assert.Contains(new MediaStreamClaim(MediaStreamKind.Video, "h263"), pipeline.Claims);
        Assert.Equal(0, converter.Asked);
    }

    /// <summary>
    /// ⚠ A container's spelling is not something a caller should have to match, so a claim compares
    /// case-insensitively — the same rule the codec tables use.
    /// </summary>
    [Fact]
    public void A_claim_matches_regardless_of_case()
    {
        var pipeline = new MediaConversionPipeline(new FakeDevice("MPEG4"));
        pipeline.Use(new Counting().Middleware, [new MediaStreamClaim(MediaStreamKind.Video, "mpeg4")]);

        Assert.True(pipeline.CanConvert(MediaStreamKind.Video, "MpEg4"));
    }

    // ── the registration ORDER, which was prose in three places and enforced nowhere ──────────────────

    /// <summary>
    /// 🔴 <b>Mounting the computed route BEHIND a conversion route on the same interceptor and options
    /// THROWS.</b> Both match through the same <c>Resolve</c> and the conversion route answers everything it
    /// matches, so the computed route would never serve — a plannable film transcodes instead of being
    /// remuxed, while every test of the computed route alone still passes. It used to log at Debug, which a
    /// default Information filter drops.
    /// </summary>
    [Fact]
    public void Mounting_the_computed_route_BEHIND_the_conversion_route_THROWS()
    {
        var access = Access();
        var interceptor = new FakeInterceptor();

        using var conversion = Conversion(interceptor, access);
        var thrown = Assert.Throws<InvalidOperationException>(() => interceptor.UseComputedRemux(Scheduler, access));

        Assert.Contains("mounted AFTER UseMediaConversion", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ The documented order is accepted — the half a check gets wrong.</summary>
    [Fact]
    public void The_documented_order_is_accepted()
    {
        var access = Access();
        var interceptor = new FakeInterceptor();

        using var computed = interceptor.UseComputedRemux(Scheduler, access);
        using var conversion = Conversion(interceptor, access);
    }

    /// <summary>
    /// ⚠ <b>Keyed per INTERCEPTOR.</b> One options object shared by two webviews mounts both routes on each,
    /// in order. The check was keyed per options object alone, so the second webview's computed route found
    /// the first webview's conversion route and was reported as misordered.
    /// </summary>
    [Fact]
    public void One_access_object_on_TWO_webviews_in_order_is_accepted()
    {
        var access = Access();
        var first = new FakeInterceptor();
        var second = new FakeInterceptor();

        using var computed1 = first.UseComputedRemux(Scheduler, access);
        using var conversion1 = Conversion(first, access);
        using var computed2 = second.UseComputedRemux(Scheduler, access);
        using var conversion2 = Conversion(second, access);
    }

    /// <summary>A conversion route that has been DISPOSED no longer stands in front of anything.</summary>
    [Fact]
    public void A_DISPOSED_conversion_route_does_not_block_the_computed_route()
    {
        var access = Access();
        var interceptor = new FakeInterceptor();

        Conversion(interceptor, access).Dispose();

        using var computed = interceptor.UseComputedRemux(Scheduler, access);
    }

    /// <summary>A conversion route disposed TWICE uncounts once, so a route mounted since still counts.</summary>
    [Fact]
    public void Disposing_a_conversion_route_TWICE_uncounts_it_once()
    {
        var access = Access();
        var interceptor = new FakeInterceptor();
        var first = Conversion(interceptor, access);
        using var second = Conversion(interceptor, access);

        first.Dispose();
        first.Dispose();

        Assert.Throws<InvalidOperationException>(() => interceptor.UseComputedRemux(Scheduler, access));
    }

    /// <summary>
    /// A scheduler that refuses the conversion's path claim fails EVERY submit, so the page is answered 503
    /// on every retry. That is the composition rather than the request: logged once per route, at Error.
    /// </summary>
    [Fact]
    public async Task A_submit_the_scheduler_refuses_is_logged_ONCE_at_Error()
    {
        using var root = TempDir.Create();
        File.WriteAllBytes(Path.Combine(root.Root, "clip.mkv"), [1]);
        var log = new RecordingLogger();
        await using var noPathScope = new MissionScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseMediaConversion(noPathScope, new EventBus(), new MediaConversionOptions
        {
            Access = new MediaAccessOptions
            {
                Resolve = uri => Path.Combine(root.Root, uri.Query.TrimStart('?')),
                AllowedRoots = [root.Root],
                CacheRoot = Path.Combine(root.Root, "cache"),
                Log = log,
            },
            Convert = (_, _) => Task.CompletedTask,
        });

        for (var i = 0; i < 3; i++)
            Assert.Equal(503, (await interceptor.AskAsync("https://x.local/media?clip.mkv"))!.StatusCode);

        var errors = log.Lines.Where(l => l.StartsWith("Error ", StringComparison.Ordinal)).ToArray();
        var error = Assert.Single(errors);
        Assert.Contains("could not submit a conversion", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Separate options objects are not compared: their predicates are opaque functions and may well not
    /// overlap, so refusing would break a legal composition.
    /// </summary>
    [Fact]
    public void Two_separate_access_objects_are_not_compared()
    {
        var interceptor = new FakeInterceptor();

        using var conversion = Conversion(interceptor, Access());
        using var computed = interceptor.UseComputedRemux(Scheduler, Access());
    }

    private static IDisposable Conversion(FakeInterceptor interceptor, MediaAccessOptions access) =>
        interceptor.UseMediaConversion(Scheduler, new EventBus(),
            new MediaConversionOptions { Access = access, Convert = (_, _) => Task.CompletedTask });

    private static MediaAccessOptions Access() => new()
    {
        Resolve = static _ => null,
        AllowedRoots = [Path.GetTempPath()],
        CacheRoot = Path.Combine(Path.GetTempPath(), "shenora-order-" + Guid.NewGuid().ToString("N")[..8]),
    };

    private static readonly MissionScheduler Scheduler =
        new(new MissionSchedulerOptions { GlobalLaneCapacity = 1, Scopes = [PathClaims.Scope] });
}
