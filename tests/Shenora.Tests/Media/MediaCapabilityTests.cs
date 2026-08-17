using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// Folding a DEVICE's measured codec support into the app's playback policy.
///
/// <para>
/// The values in these cases are the ones actually measured on 2026-08-07 — an iPhone 17 Pro (iOS 26.5.2)
/// and an API 36 AOSP emulator — because the point of the whole feature is that the two answers DIFFER, and
/// a test using invented sets could not show that.
/// </para>
/// </summary>
public class MediaCapabilityTests
{
    private sealed record Device(
        IReadOnlySet<MediaStreamCodec> DecodableAudio,
        IReadOnlySet<MediaStreamCodec> EncodableAudio,
        IReadOnlySet<MediaStreamCodec> DecodableVideo,
        IReadOnlySet<MediaStreamCodec> EncodableVideo) : IMediaCapability
    {
        private static readonly IReadOnlySet<MediaStreamCodec> None = new HashSet<MediaStreamCodec>();

        public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => kind switch
        {
            MediaStreamKind.Audio => DecodableAudio,
            MediaStreamKind.Video => DecodableVideo,
            _ => None,
        };

        public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => kind switch
        {
            MediaStreamKind.Audio => EncodableAudio,
            MediaStreamKind.Video => EncodableVideo,
            _ => None,
        };
    }

    /// <summary>A codec set. Case-insensitivity and the profile rule live in MediaStreamCodec itself.</summary>
    private static IReadOnlySet<MediaStreamCodec> Codecs(params string[] names) =>
        new HashSet<MediaStreamCodec>(names.Select(n => (MediaStreamCodec)n));

    /// <summary>Container EXTENSIONS — strings, not codecs.</summary>
    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>What the iPhone 17 Pro answered: AC-3 and E-AC-3 decode, AAC both ways.</summary>
    private static Device Iphone => new(
        DecodableAudio: Codecs("aac", "ac3", "eac3", "mp3", "alac"),
        EncodableAudio: Codecs("aac"),
        DecodableVideo: Codecs("h264", "hevc"),
        EncodableVideo: Codecs("h264"));

    /// <summary>What the AOSP emulator answered: no AC-3 at all, AAC both ways.</summary>
    private static Device Aosp => new(
        DecodableAudio: Codecs("aac", "flac", "mp3", "opus", "vorbis"),
        EncodableAudio: Codecs("aac", "flac", "opus"),
        DecodableVideo: Codecs("h264", "hevc", "vp8", "vp9", "av1"),
        EncodableVideo: Codecs("h264", "vp8"));

    /// <summary>A policy describing what the WEBVIEW plays — deliberately narrower than either device.</summary>
    private static MediaPlaybackPolicy WebviewPolicy => new()
    {
        Containers = Set(".mp4", ".m4a", ".webm"),
        Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<MediaStreamCodec>>
        {
            [MediaStreamKind.Video] = Codecs("h264", "vp9"),
            [MediaStreamKind.Audio] = Codecs("aac", "opus"),
        },
    };

    [Fact]
    public void The_encode_flags_come_from_the_device()
    {
        var policy = WebviewPolicy.WithDeviceEncoders(Iphone);

        Assert.True(policy.CanEncode(MediaStreamKind.Audio));
    }

    /// <summary>
    /// 🔴 <b>A device that encodes video does NOT make the plan promise a video transcode</b>, because the
    /// kit ships no video conversion — <c>Mp4Remuxer</c> copies video and carries only H.264 and HEVC.
    /// <para>
    /// This was the opposite way round until 2026-08-09 and is D63's fourth instance: Android's
    /// <c>MediaCodecList</c> reports video encoders, so the planner answered <c>Transcode (video)</c> and
    /// the only engine behind it dropped the track. **A plan naming a conversion nothing implements is
    /// worse than a missing capability, because the plan says the word.**
    /// </para>
    /// </summary>
    [Fact]
    public void A_device_that_ENCODES_video_does_not_make_the_KIT_able_to_transcode_it()
    {
        Assert.NotEmpty(Iphone.Encodable(MediaStreamKind.Video));

        var policy = WebviewPolicy.WithDeviceEncoders(Iphone);

        Assert.False(policy.CanEncode(MediaStreamKind.Video));
    }

    /// <summary>
    /// The seam that keeps the refusal above from being a ceiling: an app supplying its own engine through
    /// <c>MediaConversionOptions.Convert</c> says so, and gets the plan it can actually honour.
    /// </summary>
    [Fact]
    public void An_app_with_its_own_video_engine_says_so_and_gets_video_back()
    {
        var policy = WebviewPolicy.WithDeviceEncoders(
            Iphone, new HashSet<MediaStreamKind> { MediaStreamKind.Audio, MediaStreamKind.Video });

        Assert.True(policy.CanEncode(MediaStreamKind.Video));
        Assert.True(policy.CanEncode(MediaStreamKind.Audio));
    }

    /// <summary>
    /// ⚠ Claiming a kind the DEVICE cannot encode does not conjure one — both halves are required, which is
    /// the same "decode AND encode" rule <c>CanRepair</c> already states for codecs.
    /// </summary>
    [Fact]
    public void Claiming_a_kind_the_device_cannot_encode_still_answers_no()
    {
        var mute = Iphone with { EncodableVideo = Codecs() };

        var policy = WebviewPolicy.WithDeviceEncoders(
            mute, new HashSet<MediaStreamKind> { MediaStreamKind.Audio, MediaStreamKind.Video });

        Assert.False(policy.CanEncode(MediaStreamKind.Video));
    }

    [Fact]
    public void A_device_that_encodes_nothing_turns_both_flags_off()
    {
        var mute = Iphone with { EncodableAudio = Codecs(), EncodableVideo = Codecs() };

        var policy = WebviewPolicy.WithDeviceEncoders(mute);

        Assert.False(policy.CanEncode(MediaStreamKind.Audio));
        Assert.False(policy.CanEncode(MediaStreamKind.Video));
    }

    /// <summary>
    /// 🔴 <b>The assertion the whole design turns on.</b> An iPhone DECODES AC-3; no browser plays it. If
    /// folding the device's capability widened the policy's codec SETS, the planner would call an AC-3 file
    /// <see cref="MediaPlaybackAction.Direct"/> — a file that serves perfectly and plays silent, which is
    /// the exact failure the translation layer exists to prevent.
    /// </summary>
    [Fact]
    public void A_device_that_DECODES_a_codec_does_not_make_the_PLAYER_able_to_play_it()
    {
        var policy = WebviewPolicy.WithDeviceEncoders(Iphone);

        Assert.Contains("ac3", Iphone.Decodable(MediaStreamKind.Audio));
        Assert.DoesNotContain("ac3", policy.CodecsFor(MediaStreamKind.Audio));
        Assert.Equal(WebviewPolicy.CodecsFor(MediaStreamKind.Audio), policy.CodecsFor(MediaStreamKind.Audio));
        Assert.Equal(WebviewPolicy.Containers, policy.Containers);
        Assert.Equal(WebviewPolicy.CodecsFor(MediaStreamKind.Video), policy.CodecsFor(MediaStreamKind.Video));
    }

    /// <summary>
    /// The end-to-end point of slice 3's measurement: the SAME file gets a different honest verdict on the
    /// two devices, and neither verdict is a guess.
    /// </summary>
    [Theory]
    [InlineData(true, MediaPlaybackAction.Transcode)]   // iPhone: decodes AC-3, encodes AAC -> repairable
    [InlineData(false, MediaPlaybackAction.Unsupported)] // AOSP: cannot decode AC-3 -> say so honestly
    public void An_h264_plus_ac3_mkv_is_decided_by_what_the_DEVICE_can_do(bool onIphone, MediaPlaybackAction expected)
    {
        var device = onIphone ? Iphone : Aosp;
        var probe = new MediaProbeResult
        {
            Container = ".mkv",
            Streams =
            [
                new MediaStreamInfo(MediaStreamKind.Video, "h264"),
                new MediaStreamInfo(MediaStreamKind.Audio, "ac3"),
            ],
        };

        // The policy's encodable set is only half the question — the device must also be able to DECODE
        // what the file holds, which is per-codec and is what CanRepairAudio asks.
        var policy = WebviewPolicy.WithDeviceEncoders(device);
        if (!device.CanRepairAudio("ac3")) policy = policy with { Encodable = new HashSet<MediaStreamKind>() };

        Assert.Equal(expected, MediaPlaybackPlanner.Plan(probe, policy).Action);
    }

    [Fact]
    public void Repairing_a_codec_needs_BOTH_a_decoder_for_it_and_some_encoder()
    {
        Assert.True(Iphone.CanRepairAudio("ac3"));          // decodes ac3, encodes aac
        Assert.False(Aosp.CanRepairAudio("ac3"));           // no ac3 decoder at all

        var noEncoder = Iphone with { EncodableAudio = Codecs() };
        Assert.False(noEncoder.CanRepairAudio("ac3"));      // decodes it, nowhere to put the result

        Assert.False(Iphone.CanRepairAudio("dts"));         // neither device decodes DTS
        Assert.False(Iphone.CanRepairAudio(""));
    }

    [Fact]
    public void Codec_names_are_matched_case_insensitively_like_every_other_set_here()
    {
        Assert.True(Iphone.CanRepairAudio("AC3"));
    }

    // ── the conversion PIPELINE ───────────────────────────────────────────────────────────────────────

    private sealed class Stub(string codec) : IMediaStreamConversionRun
    {
        public string Codec { get; } = codec;
        public ReadOnlyMemory<byte> OutputConfig => new byte[] { 0x11, 0x90 };
        public int OutputFramesPerPacket => 1024;
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac", Channels: 2, SampleRate: 48000);
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame) => [];
        public IReadOnlyList<MediaFrame> Drain() => [];
        public void Dispose() { }
    }

    /// <summary>A converter that handles exactly one codec and declines everything else.</summary>
    private static MediaConversionMiddleware Handles(string codec, string tag) =>
        (source, _) => string.Equals(source.Codec, codec, StringComparison.OrdinalIgnoreCase) ? new Stub(tag) : null;

    /// <summary>
    /// 🔴 <b>The reason this is a pipeline rather than a replaceable implementation.</b> An app that adds a
    /// converter keeps the kit's built-in one behind it — so a consumer who only wanted a better DTS decoder
    /// does not have to re-provide AC-3 and everything else the device already did for free.
    /// </summary>
    [Fact]
    public void A_consumers_converter_is_ADDED_to_the_chain_not_a_replacement()
    {
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(Handles("ac3", "built-in"));      // the kit's platform converter
        pipeline.Use(Handles("dts", "the app's"));     // what a consumer adds

        Assert.True(pipeline.CanConvert(MediaStreamKind.Audio, "ac3"));       // still there
        Assert.True(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));       // and the new one works
        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "truehd"));   // neither claims this
    }

    /// <summary>
    /// Later registrations are asked FIRST: an app adding a converter for a codec the default already
    /// handles means to OVERRIDE it, not to be consulted after the default has said yes.
    /// </summary>
    [Fact]
    public void A_later_converter_overrides_an_earlier_one_for_the_same_codec()
    {
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(Handles("ac3", "built-in"));
        pipeline.Use(Handles("ac3", "the app's"));

        using var run = pipeline.Begin(new MediaStreamInfo(MediaStreamKind.Audio, "ac3"), default);
        Assert.Equal("the app's", Assert.IsType<Stub>(run).Codec);
    }

    /// <summary>
    /// Removable for the same reason a route is: a converter outliving the feature it served would answer
    /// for the next one.
    /// </summary>
    [Fact]
    public void Disposing_a_registration_removes_that_converter_and_leaves_the_rest()
    {
        var pipeline = new MediaConversionPipeline();
        pipeline.Use(Handles("ac3", "built-in"));
        var extra = pipeline.Use(Handles("dts", "the app's"));

        Assert.True(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));
        extra.Dispose();

        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "dts"));
        Assert.True(pipeline.CanConvert(MediaStreamKind.Audio, "ac3"), "removing one converter must not disturb the others");
    }

    [Fact]
    public void An_empty_pipeline_declines_everything_rather_than_throwing()
    {
        var pipeline = new MediaConversionPipeline();
        Assert.False(pipeline.CanConvert(MediaStreamKind.Audio, "ac3"));
        Assert.Null(pipeline.Begin(new MediaStreamInfo(MediaStreamKind.Audio, "ac3"), default));
    }

    /// <summary>
    /// 🔴 The matching rule, which is the whole design. A capability with NO profile covers ANY profile —
    /// that is what keeps every device that reports bare names working exactly as before. A capability
    /// WITH one covers only that profile — which is what makes the Main-10-on-a-Main-decoder case
    /// expressible at all. Getting this backwards would silently un-play files that work today.
    /// </summary>
    [Theory]
    [InlineData("hevc", null, "hevc", null, true)]        // bare vs bare
    [InlineData("hevc", null, "hevc", "Main 10", true)]   // bare capability covers a profiled stream
    [InlineData("hevc", "Main 10", "hevc", "Main 10", true)]
    [InlineData("hevc", "Main", "hevc", "Main 10", false)] // THE BUG: Main decoder, Main 10 stream
    [InlineData("hevc", "Main 10", "hevc", null, false)]  // a profiled capability does NOT cover unknown
    [InlineData("HEVC", null, "hevc", null, true)]        // case-insensitive on the name
    [InlineData("hevc", "MAIN 10", "hevc", "main 10", true)] // …and on the profile
    [InlineData("h264", null, "hevc", null, false)]
    public void The_profile_matching_rule_is_asymmetric(
        string capName, string? capProfile, string streamName, string? streamProfile, bool expected)
    {
        var capability = new MediaStreamCodec(capName, capProfile);
        var stream = new MediaStreamCodec(streamName, streamProfile);

        Assert.Equal(expected, capability.Matches(stream));
        Assert.Equal(expected, new HashSet<MediaStreamCodec> { capability }.Covers(stream));
    }

    /// <summary>A bare string still works everywhere a codec is expected — the implicit conversion.</summary>
    [Fact]
    public void A_bare_string_is_still_a_codec()
    {
        MediaStreamCodec codec = "aac";

        Assert.Equal("aac", codec.Name);
        Assert.Null(codec.Profile);
        Assert.Equal("aac", codec.ToString());
        Assert.Equal("hevc/Main 10", new MediaStreamCodec("hevc", "Main 10").ToString());
    }
}
