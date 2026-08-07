using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The playability decision two sibling apps hand-rolled. Pure inputs and outputs, so the cases worth
/// writing are the ones where a plausible-but-wrong implementation still looks right — which for this
/// decision is nearly all of them, because the naive version passes every file anyone tests with.
/// </summary>
public class MediaPlaybackPlannerTests
{
    /// <summary>A policy shaped like a browser's: mp4/webm, H.264/VP9, AAC/Opus, and it can re-encode.</summary>
    private static MediaPlaybackPolicy Browser(bool canEncodeVideo = true, bool canEncodeAudio = true) => new()
    {
        Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm" },
        Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<string>>
        {
            [MediaStreamKind.Video] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp9" },
            [MediaStreamKind.Audio] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "opus" },
        },
        Encodable = new HashSet<MediaStreamKind>(
            (canEncodeVideo ? [MediaStreamKind.Video] : Array.Empty<MediaStreamKind>())
            .Concat(canEncodeAudio ? [MediaStreamKind.Audio] : Array.Empty<MediaStreamKind>())),
    };

    private static MediaProbeResult Probe(string? container, params MediaStreamInfo[] streams) =>
        new() { Container = container, Streams = streams };

    private static MediaStreamInfo Video(string? codec) => new(MediaStreamKind.Video, codec);
    private static MediaStreamInfo Audio(string? codec) => new(MediaStreamKind.Audio, codec);

    [Fact]
    public void A_playable_container_with_playable_streams_is_served_untouched()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4", Video("h264"), Audio("aac")), Browser());

        Assert.Equal(MediaPlaybackAction.Direct, plan.Action);
        Assert.True(plan.ContainerOpens);
        Assert.All(plan.Streams, s => Assert.False(s.NeedsReEncode));
    }

    /// <summary>
    /// THE case a per-file boolean gets wrong, and the reason this planner is per-stream (D42): picture
    /// with no sound. H.264 video decodes perfectly, AC-3 audio does not because licensed audio is not in
    /// every platform's mandatory set. The cheap fix is to copy the video and re-encode only the sound —
    /// a `CanPlay(file) -> bool` would have thrown that away.
    /// </summary>
    [Fact]
    public void Licensed_audio_in_a_playable_container_transcodes_the_AUDIO_and_says_which_stream_forced_it()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4", Video("h264"), Audio("ac3")), Browser());

        Assert.Equal(MediaPlaybackAction.Transcode, plan.Action);
        // The verdict alone is not enough — the app has to know only the audio needs work, or it re-encodes
        // a perfectly good picture.
        var video = Assert.Single(plan.Streams, s => s.Stream.Kind == MediaStreamKind.Video);
        var audio = Assert.Single(plan.Streams, s => s.Stream.Kind == MediaStreamKind.Audio);
        Assert.False(video.NeedsReEncode);
        Assert.True(audio.NeedsReEncode);
        Assert.Contains("audio only", plan.Reason);
        Assert.Contains("ac3", plan.Reason);
    }

    /// <summary>
    /// The other case a codec-only planner inverts: an MKV carrying entirely ordinary streams. Every codec
    /// is decodable, so a codec-first implementation calls it playable — and then the player cannot open
    /// the container. The right answer is a REMUX, which copies both streams and costs no quality.
    /// </summary>
    [Fact]
    public void An_unopenable_container_with_fine_streams_is_a_REMUX_not_a_transcode()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mkv", Video("h264"), Audio("aac")), Browser());

        Assert.Equal(MediaPlaybackAction.Remux, plan.Action);
        Assert.False(plan.ContainerOpens);
        Assert.All(plan.Streams, s => Assert.False(s.NeedsReEncode));
    }

    /// <summary>
    /// A missing probe is a NORMAL state — the probe is an external tool the app may not have installed —
    /// and both donors are explicit that it must not cost a needless re-encode. The container alone decides.
    /// </summary>
    [Fact]
    public void An_unprobed_file_in_a_playable_container_is_direct_not_transcoded()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4"), Browser());

        Assert.Equal(MediaPlaybackAction.Direct, plan.Action);
        Assert.Empty(plan.Streams);
        Assert.Contains("nothing probed", plan.Reason);
    }

    [Fact]
    public void An_unprobed_file_in_an_unopenable_container_still_only_needs_a_remux()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mkv"), Browser());
        Assert.Equal(MediaPlaybackAction.Remux, plan.Action);
    }

    /// <summary>A named container the policy does not list must not be confused with an ABSENT one.</summary>
    [Fact]
    public void A_completely_unknown_file_is_remuxed_rather_than_called_direct()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(container: null), Browser());

        Assert.Equal(MediaPlaybackAction.Remux, plan.Action);
        Assert.False(plan.ContainerOpens);
        Assert.Contains("unknown container", plan.Reason);
    }

    /// <summary>
    /// An unnamed codec gets the benefit of the doubt: guessing "broken" on missing information turns
    /// absent tooling into failed playback.
    /// </summary>
    [Fact]
    public void A_stream_with_no_reported_codec_is_treated_as_decodable()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4", Video(null), Audio(null)), Browser());

        Assert.Equal(MediaPlaybackAction.Direct, plan.Action);
        Assert.All(plan.Streams, s => Assert.True(s.DecodesNatively));
    }

    /// <summary>
    /// Subtitles are droppable — a player that cannot render them still plays the film. Letting them vote
    /// would transcode a file for a stream nobody needs.
    /// </summary>
    [Fact]
    public void A_subtitle_or_unknown_stream_never_forces_a_conversion()
    {
        var probe = Probe(".mp4", Video("h264"), Audio("aac"),
            new MediaStreamInfo(MediaStreamKind.Subtitle, "hdmv_pgs_subtitle"),
            new MediaStreamInfo(MediaStreamKind.Unknown, "bin_data"));

        var plan = MediaPlaybackPlanner.Plan(probe, Browser());

        Assert.Equal(MediaPlaybackAction.Direct, plan.Action);
        Assert.Equal(4, plan.Streams.Count);
        Assert.All(plan.Streams, s => Assert.False(s.NeedsReEncode));
    }

    /// <summary>
    /// Without an encoder the honest answer is a refusal, so the app can hand the file to an external
    /// player. Promising a transcode it cannot perform is the worse outcome.
    /// </summary>
    [Fact]
    public void No_encoder_for_the_offending_stream_is_UNSUPPORTED_rather_than_a_promise()
    {
        var noAudioEncoder = Browser(canEncodeAudio: false);

        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4", Video("h264"), Audio("ac3")), noAudioEncoder);

        Assert.Equal(MediaPlaybackAction.Unsupported, plan.Action);
        Assert.Contains("ac3", plan.Reason);
    }

    /// <summary>
    /// The kinds are checked against their OWN set. A planner with one merged codec list would call this
    /// file playable — "aac" is decodable, after all — while the video stream is what cannot decode.
    /// </summary>
    [Fact]
    public void Video_and_audio_are_checked_against_SEPARATE_sets()
    {
        // "aac" as a VIDEO codec is nonsense, and that is the point: it appears in AudioCodecs only.
        var plan = MediaPlaybackPlanner.Plan(Probe(".mp4", Video("aac")), Browser());

        Assert.Equal(MediaPlaybackAction.Transcode, plan.Action);
        Assert.True(Assert.Single(plan.Streams).NeedsReEncode);
    }

    /// <summary>Both streams undecodable is still one verdict, and the reason names both.</summary>
    [Fact]
    public void When_both_streams_need_work_the_reason_names_both()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".mkv", Video("mpeg2video"), Audio("dts")), Browser());

        Assert.Equal(MediaPlaybackAction.Transcode, plan.Action);
        Assert.Contains("mpeg2video", plan.Reason);
        Assert.Contains("dts", plan.Reason);
    }

    /// <summary>Codec and container matching is case-insensitive: probes disagree on casing.</summary>
    [Fact]
    public void Matching_ignores_case_because_probes_disagree_on_it()
    {
        var plan = MediaPlaybackPlanner.Plan(Probe(".MP4", Video("H264"), Audio("AAC")), Browser());
        Assert.Equal(MediaPlaybackAction.Direct, plan.Action);
    }

    [Fact]
    public void Plan_refuses_null_arguments_rather_than_answering_from_nothing()
    {
        Assert.Throws<ArgumentNullException>(() => MediaPlaybackPlanner.Plan(null!, Browser()));
        Assert.Throws<ArgumentNullException>(() => MediaPlaybackPlanner.Plan(Probe(".mp4"), null!));
    }
}
