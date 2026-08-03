using Shenora.Core;
using Shenora.Ipc;
using Shenora.Media;

namespace Shenora.Sample.Logic;

/// <summary>
/// Application logic that runs on the desktop shell today and could run on another shell tomorrow —
/// the shape D20 exists to make possible, and the reason this assembly targets plain
/// <c>net10.0</c>.
/// <para>
/// Every native capability it uses arrives through a platform-neutral contract from
/// <c>Shenora.Core</c>: <see cref="IFileDialogs"/>, <see cref="IClipboardService"/>,
/// <see cref="IUrlLauncher"/>, <see cref="IUiDispatcher"/>. The desktop app supplies the WinForms
/// implementations (<c>UseWinForms</c> registers both the Windows and the portable face of each), so
/// this class never names a Windows type and never references <c>Shenora.Windows</c>.
/// </para>
/// <para>
/// Contrast with the desktop sample's own <c>SampleFacade</c>, which keeps the genuinely
/// desktop-only routes (reveal-in-Explorer, secondary windows on their own STA threads). That split
/// is the point: portable logic here, platform-bound composition there.
/// </para>
/// </summary>
public sealed class PortableSampleFacade(
    IFileDialogs dialogs,
    IClipboardService clipboard,
    IUrlLauncher urls,
    IUiDispatcher ui,
    IMissionScheduler scheduler,
    IFileUpdateQueue updates,
    ShenoraPaths paths) : BaseFacade
{
    /// <summary>The reserved module name for the portable half of the sample.</summary>
    public const string Module = "SAMPLE_LOGIC";

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            // Pure logic — no host capability at all.
            case "ECHO":
                var text = PayloadHelper.GetRequiredValue<string>(request.Payload, "text");
                return new { Echoed = text.ToUpperInvariant(), Length = text.Length };

            // A file picker exists on every host worth shipping to; only the implementation differs.
            case "PICK_FILE":
                var picked = await dialogs.OpenFileAsync(new FileDialogOptions
                {
                    Title = "Pick any file",
                    RememberPathKey = "portable-sample-pick",
                });
                return new { picked.Success, picked.FilePath };

            // The counterpart, and the more interesting one: SAVE is universal only because the HOST
            // does the writing. This one route runs unchanged on three shells that express saving in
            // three completely different ways — a WinForms SaveFileDialog plus an atomic replace,
            // Android's ACTION_CREATE_DOCUMENT, and an iOS export picker — and this code cannot tell
            // which. Note what portable logic never does here: name a path, or open a file itself.
            case "SAVE_TEXT":
                var body = PayloadHelper.GetRequiredValue<string>(request.Payload, "text");
                var saved = await dialogs.SaveAsync(
                    new FileDialogOptions
                    {
                        Title = "Save the sample text",
                        FileName = "shenora-sample",
                        DefaultExtension = "txt",
                        RememberPathKey = "portable-sample-save",   // honoured on the desktop, ignored on mobile
                    },
                    // Deliberately SLOW, in steps. An instant write would demo nothing: the guarantee
                    // being shown is that an interrupted save leaves the user's previous file intact, and
                    // that only means something when the write takes real time. Also proves the stream
                    // is genuinely streamed rather than buffered by the contract.
                    async (stream, ct) =>
                    {
                        await using var writer = new StreamWriter(stream, leaveOpen: true);
                        for (var line = 1; line <= 5; line++)
                        {
                            await writer.WriteLineAsync($"{line,2}. {body}");
                            await writer.FlushAsync(ct);
                            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                        }
                    },
                    cancellationToken);
                // FilePath is null on mobile BY CONTRACT (a grant, not an address), so the page must not
                // treat its absence as failure — which is exactly what this route reports back.
                return new { saved.Success, saved.FilePath };

            case "COPY_TEXT":
                await clipboard.SetTextAsync(PayloadHelper.GetRequiredValue<string>(request.Payload, "text"));
                return null;

            case "READ_CLIPBOARD":
                return new { Text = await clipboard.GetTextAsync() };

            // http/https only — the contract rejects anything else, on any host.
            case "OPEN_URL":
                urls.OpenUrl(PayloadHelper.GetRequiredValue<string>(request.Payload, "url"));
                return null;

            // Marshalling to the UI thread is a CONTRACT here, not a WinForms call: this compiles
            // with no Windows reference and would work over any host's dispatcher.
            case "UI_STATE":
                return new { State = ui.State.ToString(), OnUiThread = ui.IsOnUiThread };

            // Scheduling is portable too (Shenora.Core's Work layer), so it belongs on this side of
            // the split. Four items go in at once: two contend for ONE path and must serialize, two
            // are disjoint and must overlap. Nothing is awaited here — the route returns immediately
            // and the page watches the operations list, which is the D23 shape for anything slow.
            case "SCHEDULE_DEMO":
                return ScheduleDemo();

            // The media decision, from PORTABLE logic — which is the point of the route existing (D41).
            // `Shenora.Media` is net10.0, so this compiles here, on the desktop shell and on both mobile
            // shells with no `#if`; the moment someone reaches for a platform media type instead, THIS
            // project stops compiling. That is the tripwire, and it is armed rather than described.
            //
            // Note what the app supplies and what the kit does not: every codec and container below is the
            // APP's policy. The kit ships no list, because the right one differs per player and, on
            // Android, per DEVICE (D42).
            case "PLAN_PLAYBACK":
                return PlanPlayback(request);

            // The composition an adopter actually builds: expensive work in parallel, the filesystem
            // change landed through the queue. Two chains go in at once and their COMMITS serialize
            // while their staging does not — which is the whole argument for the file queue existing
            // alongside claims.
            case "CHAIN_DEMO":
                return ChainDemo();

            default:
                throw UnknownType(request);
        }
    }

    /// <summary>
    /// What a browser-ish player can open. <b>The APP's policy, not the kit's</b> — `Shenora.Media` ships
    /// no codec list on purpose, because the correct one differs per player and, on Android, per DEVICE
    /// (codec support is vendor-declared, which is why <c>MediaCodecList</c> is a runtime query). A list
    /// baked into the kit would be one app's guess frozen into everyone's planner.
    /// </summary>
    private static readonly MediaPlaybackPolicy BrowserPolicy = new()
    {
        Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm" },
        VideoCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp8", "vp9", "av1" },
        AudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "opus", "vorbis", "flac" },
        // This sample ships no engine, so it can convert nothing — and saying so honestly is the point:
        // the planner then answers `Unsupported` instead of promising a transcode nobody can perform.
        CanEncodeVideo = false,
        CanEncodeAudio = false,
    };

    /// <summary>
    /// Runs the playability decision over a container + codecs the page names. Pure — no file is opened,
    /// nothing is probed here — so it behaves identically on all three shells.
    /// </summary>
    private static object PlanPlayback(IpcRequest request)
    {
        var container = PayloadHelper.GetOptionalValue<string>(request.Payload, "container");
        var video = PayloadHelper.GetOptionalValue<string>(request.Payload, "video");
        var audio = PayloadHelper.GetOptionalValue<string>(request.Payload, "audio");

        var streams = new List<MediaStreamInfo>();
        if (video is { Length: > 0 }) streams.Add(new MediaStreamInfo(MediaStreamKind.Video, video));
        if (audio is { Length: > 0 }) streams.Add(new MediaStreamInfo(MediaStreamKind.Audio, audio));

        var plan = MediaPlaybackPlanner.Plan(
            new MediaProbeResult { Container = container, Streams = streams }, BrowserPolicy);

        return new
        {
            Action = plan.Action.ToString(),
            plan.ContainerOpens,
            plan.Reason,
            // Per-STREAM, which is the whole reason the planner is shaped this way: the page can see that
            // only the audio is the problem rather than being told the file is unplayable (D42).
            Streams = plan.Streams.Select(s => new
            {
                Kind = s.Stream.Kind.ToString(),
                s.Stream.Codec,
                s.DecodesNatively,
                s.NeedsReEncode,
            }),
        };
    }

    /// <summary>
    /// Submits the demo batch and returns at once. What proves the point is the ORDER the page sees:
    /// the two <c>CONTENDED</c> operations never run at the same time, while a <c>DISJOINT</c> one
    /// runs alongside them.
    /// </summary>
    private object ScheduleDemo()
    {
        var root = paths.DataArea("work-demo");
        var contended = Path.Combine(root, "contended.txt");

        Submit("CONTENDED", contended);
        Submit("CONTENDED", contended);
        Submit("DISJOINT", Path.Combine(root, "a.txt"));
        Submit("DISJOINT", Path.Combine(root, "b.txt"));

        return new { Submitted = 4, Root = root };
    }

    private void Submit(string kind, string path) =>
        // Deliberately not awaited: SubmitAsync completes when the WORK does. A caller error (an
        // unregistered claim scope, a disposed scheduler) still throws right here, synchronously,
        // which is why this is a plain call and not a fire-and-forget Task.Run.
        _ = scheduler.SubmitAsync(new MissionDefinition
        {
            Kind = kind,
            Claims = [PathClaims.Exclusive(path)],
            // A budget lane the app configured at startup — see Program.cs. Named through a constant
            // there rather than a literal here would be better still in a real app: an unknown lane
            // name is CREATED at the default capacity rather than rejected.
            Lanes = [new MissionLane(MissionLanes.DemoIo)],
            Run = async (mission, ct) =>
            {
                // Real mutation of the claimed path, so the exclusion is doing something observable
                // rather than being asserted by a comment — and STAMPED, so the log proves both halves
                // at once: two entries for one path never overlap, while entries for different paths
                // do. A demo that only proves exclusion would pass on a fully serial scheduler.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.AppendAllTextAsync(path, Stamp(mission.MissionId, kind, "in"), ct);
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                await File.AppendAllTextAsync(path, Stamp(mission.MissionId, kind, "out"), ct);
            },
        });

    /// <summary>
    /// Two chains, each "stage a file, then land it". The staging steps overlap; the commits do not,
    /// because both updates go through one partition of the file queue. Note what is NOT here: a path
    /// claim on the target. The queue is the only writer, so exclusivity comes from it — claims are
    /// for missions that must not even COMPUTE at the same time, which is a different question.
    /// </summary>
    private object ChainDemo()
    {
        var root = paths.DataArea("chain-demo");
        Submit("alpha");
        Submit("beta");
        return new { Submitted = 2, Root = root };

        void Submit(string name) => _ = scheduler.SubmitAsync(MissionChain.Sequence($"CHAIN:{name}",
            new MissionStep("stage", async (mission, chain, ct) =>
            {
                var temp = Path.Combine(root, $"{name}.tmp");
                Directory.CreateDirectory(root);
                await File.WriteAllTextAsync(temp, Stamp(mission.MissionId, name, "staged"), ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);   // stand-in for the expensive part
                // The reason a chain exists at all: step 2 needs what step 1 produced.
                chain.Set("temp", temp);
            }),
            new MissionStep("land", async (mission, chain, ct) =>
            {
                var temp = chain.Get<string>("temp")!;
                var result = await updates.ApplyAsync(new FileUpdate
                {
                    Changes = [new FileChange.Replace(temp, Path.Combine(root, $"{name}.txt"))],
                    // One writer for this tree: two chains staging in parallel still land one at a time.
                    Partition = root,
                    Retry = new RetryPolicy(),
                }, ct);
                result.ThrowIfFailed();   // a failed landing must fail the mission, not be swallowed
                await File.AppendAllTextAsync(Path.Combine(root, "landed.log"),
                    Stamp(mission.MissionId, name, "landed"), ct);
            })));
    }

    private static string Stamp(string missionId, string kind, string edge) =>
        $"{DateTimeOffset.Now:HH:mm:ss.fff}  {missionId,-4} {kind,-9} {edge}\n";
}

/// <summary>Lane names the app configures once at startup and references by constant everywhere else.</summary>
public static class MissionLanes
{
    /// <summary>The demo's IO budget — capacity set in the desktop composition root.</summary>
    public const string DemoIo = "demo-io";
}
