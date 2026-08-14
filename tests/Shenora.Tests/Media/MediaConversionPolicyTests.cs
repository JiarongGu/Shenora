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
        private readonly HashSet<string> _decodable = new(decodable, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> Decodable(MediaStreamKind kind) => _decodable;

        public IReadOnlySet<string> Encodable(MediaStreamKind kind) => _decodable;
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
    /// 🔴 <b>Registering the routes in the WRONG order now SAYS SO.</b> Middleware run in registration
    /// order, so a conversion route registered first answers everything its own <c>Resolve</c> matches: a
    /// plannable film transcodes instead of being remuxed, and the computed route becomes dead code that
    /// still passes every test of its own. That hazard lived in three comments and nothing an app runs.
    /// </summary>
    [Fact]
    public void Registering_the_computed_route_AFTER_the_conversion_route_reports_it()
    {
        var lines = new List<string>();
        var access = Access(lines.Add);
        var interceptor = new FakeInterceptor();

        using var conversion = interceptor.UseMediaConversion(Scheduler, new EventBus(),
            new MediaConversionOptions { Access = access, Convert = (_, _) => Task.CompletedTask });
        using var computed = interceptor.UseComputedRemux(Scheduler, access);

        Assert.Contains(lines, l => l.Contains("registered AFTER", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠ <b>And the RIGHT order stays silent</b> — the half a warning gets wrong. A gate that fires on the
    /// correct composition teaches its reader to ignore it, which `phase-workflow.md` records as its own
    /// defect class ("test where it must stay QUIET").
    /// </summary>
    [Fact]
    public void Registering_them_in_the_documented_order_says_NOTHING()
    {
        var lines = new List<string>();
        var access = Access(lines.Add);
        var interceptor = new FakeInterceptor();

        using var computed = interceptor.UseComputedRemux(Scheduler, access);
        using var conversion = interceptor.UseMediaConversion(Scheduler, new EventBus(),
            new MediaConversionOptions { Access = access, Convert = (_, _) => Task.CompletedTask });

        Assert.DoesNotContain(lines, l => l.Contains("registered AFTER", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠ <b>The marker is per OPTIONS OBJECT, not process-wide</b> — an app hosting two webviews with
    /// separate access options must not have one warn about the other's ordering.
    /// </summary>
    [Fact]
    public void Two_separate_access_objects_do_not_warn_about_each_other()
    {
        var lines = new List<string>();
        var first = Access(lines.Add);
        var second = Access(lines.Add);
        var interceptor = new FakeInterceptor();

        using var conversion = interceptor.UseMediaConversion(Scheduler, new EventBus(),
            new MediaConversionOptions { Access = first, Convert = (_, _) => Task.CompletedTask });
        using var computed = interceptor.UseComputedRemux(Scheduler, second);

        Assert.DoesNotContain(lines, l => l.Contains("registered AFTER", StringComparison.Ordinal));
    }

    private static MediaAccessOptions Access(Action<string> log) => new()
    {
        Resolve = static _ => null,
        AllowedRoots = [Path.GetTempPath()],
        CacheRoot = Path.Combine(Path.GetTempPath(), "shenora-order-" + Guid.NewGuid().ToString("N")[..8]),
        Log = log,
    };

    private static readonly MissionScheduler Scheduler =
        new(new MissionSchedulerOptions { GlobalLaneCapacity = 1, Scopes = [PathClaims.Scope] });
}
