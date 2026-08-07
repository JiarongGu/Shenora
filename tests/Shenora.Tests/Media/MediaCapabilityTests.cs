using Shenora.Media;

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
        IReadOnlySet<string> DecodableAudio,
        IReadOnlySet<string> EncodableAudio,
        IReadOnlySet<string> DecodableVideo,
        IReadOnlySet<string> EncodableVideo) : IMediaCapability
    {
        private static readonly IReadOnlySet<string> None = new HashSet<string>();

        public IReadOnlySet<string> Decodable(MediaStreamKind kind) => kind switch
        {
            MediaStreamKind.Audio => DecodableAudio,
            MediaStreamKind.Video => DecodableVideo,
            _ => None,
        };

        public IReadOnlySet<string> Encodable(MediaStreamKind kind) => kind switch
        {
            MediaStreamKind.Audio => EncodableAudio,
            MediaStreamKind.Video => EncodableVideo,
            _ => None,
        };
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>What the iPhone 17 Pro answered: AC-3 and E-AC-3 decode, AAC both ways.</summary>
    private static Device Iphone => new(
        DecodableAudio: Set("aac", "ac3", "eac3", "mp3", "alac"),
        EncodableAudio: Set("aac"),
        DecodableVideo: Set("h264", "hevc"),
        EncodableVideo: Set("h264"));

    /// <summary>What the AOSP emulator answered: no AC-3 at all, AAC both ways.</summary>
    private static Device Aosp => new(
        DecodableAudio: Set("aac", "flac", "mp3", "opus", "vorbis"),
        EncodableAudio: Set("aac", "flac", "opus"),
        DecodableVideo: Set("h264", "hevc", "vp8", "vp9", "av1"),
        EncodableVideo: Set("h264", "vp8"));

    /// <summary>A policy describing what the WEBVIEW plays — deliberately narrower than either device.</summary>
    private static MediaPlaybackPolicy WebviewPolicy => new()
    {
        Containers = Set(".mp4", ".m4a", ".webm"),
        VideoCodecs = Set("h264", "vp9"),
        AudioCodecs = Set("aac", "opus"),
    };

    [Fact]
    public void The_encode_flags_come_from_the_device()
    {
        var policy = WebviewPolicy.WithDeviceEncoders(Iphone);

        Assert.True(policy.CanEncodeAudio);
        Assert.True(policy.CanEncodeVideo);
    }

    [Fact]
    public void A_device_that_encodes_nothing_turns_both_flags_off()
    {
        var mute = Iphone with { EncodableAudio = Set(), EncodableVideo = Set() };

        var policy = WebviewPolicy.WithDeviceEncoders(mute);

        Assert.False(policy.CanEncodeAudio);
        Assert.False(policy.CanEncodeVideo);
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
        Assert.DoesNotContain("ac3", policy.AudioCodecs);
        Assert.Equal(WebviewPolicy.AudioCodecs, policy.AudioCodecs);
        Assert.Equal(WebviewPolicy.Containers, policy.Containers);
        Assert.Equal(WebviewPolicy.VideoCodecs, policy.VideoCodecs);
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

        // The policy's CanEncodeAudio is only half the question — the device must also be able to DECODE
        // what the file holds, which is per-codec and is what CanRepairAudio asks.
        var policy = WebviewPolicy.WithDeviceEncoders(device);
        if (!device.CanRepairAudio("ac3")) policy = policy with { CanEncodeAudio = false };

        Assert.Equal(expected, MediaPlaybackPlanner.Plan(probe, policy).Action);
    }

    [Fact]
    public void Repairing_a_codec_needs_BOTH_a_decoder_for_it_and_some_encoder()
    {
        Assert.True(Iphone.CanRepairAudio("ac3"));          // decodes ac3, encodes aac
        Assert.False(Aosp.CanRepairAudio("ac3"));           // no ac3 decoder at all

        var noEncoder = Iphone with { EncodableAudio = Set() };
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

    private sealed class Stub(string codec) : IMediaAudioConversionRun
    {
        public string Codec { get; } = codec;
        public ReadOnlyMemory<byte> OutputConfig => new byte[] { 0x11, 0x90 };
        public int OutputFramesPerPacket => 1024;
        public int OutputSampleRate => 48000;
        public int OutputChannels => 2;
        public IReadOnlyList<ReadOnlyMemory<byte>> Push(ReadOnlyMemory<byte> frame) => [];
        public IReadOnlyList<ReadOnlyMemory<byte>> Drain() => [];
        public void Dispose() { }
    }

    /// <summary>A converter that handles exactly one codec and declines everything else.</summary>
    private static MediaAudioMiddleware Handles(string codec, string tag) =>
        (source, _) => string.Equals(source.Codec, codec, StringComparison.OrdinalIgnoreCase) ? new Stub(tag) : null;

    /// <summary>
    /// 🔴 <b>The reason this is a pipeline rather than a replaceable implementation.</b> An app that adds a
    /// converter keeps the kit's built-in one behind it — so a consumer who only wanted a better DTS decoder
    /// does not have to re-provide AC-3 and everything else the device already did for free.
    /// </summary>
    [Fact]
    public void A_consumers_converter_is_ADDED_to_the_chain_not_a_replacement()
    {
        var pipeline = new MediaAudioConversion();
        pipeline.Use(Handles("ac3", "built-in"));      // the kit's platform converter
        pipeline.Use(Handles("dts", "the app's"));     // what a consumer adds

        Assert.True(pipeline.CanConvert("ac3"));       // still there
        Assert.True(pipeline.CanConvert("dts"));       // and the new one works
        Assert.False(pipeline.CanConvert("truehd"));   // neither claims this
    }

    /// <summary>
    /// Later registrations are asked FIRST: an app adding a converter for a codec the default already
    /// handles means to OVERRIDE it, not to be consulted after the default has said yes.
    /// </summary>
    [Fact]
    public void A_later_converter_overrides_an_earlier_one_for_the_same_codec()
    {
        var pipeline = new MediaAudioConversion();
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
        var pipeline = new MediaAudioConversion();
        pipeline.Use(Handles("ac3", "built-in"));
        var extra = pipeline.Use(Handles("dts", "the app's"));

        Assert.True(pipeline.CanConvert("dts"));
        extra.Dispose();

        Assert.False(pipeline.CanConvert("dts"));
        Assert.True(pipeline.CanConvert("ac3"), "removing one converter must not disturb the others");
    }

    [Fact]
    public void An_empty_pipeline_declines_everything_rather_than_throwing()
    {
        var pipeline = new MediaAudioConversion();
        Assert.False(pipeline.CanConvert("ac3"));
        Assert.Null(pipeline.Begin(new MediaStreamInfo(MediaStreamKind.Audio, "ac3"), default));
    }
}
