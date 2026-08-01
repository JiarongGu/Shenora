using Shenora.Core;
using Shenora.Ipc;

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
/// this class never names a Windows type and never references <c>Shenora.WinForms</c>.
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
    IWorkScheduler scheduler,
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

            default:
                throw UnknownType(request);
        }
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
        _ = scheduler.SubmitAsync(new WorkRequest
        {
            Kind = kind,
            Claims = [PathClaims.Exclusive(path)],
            // A budget lane the app configured at startup — see Program.cs. Named through a constant
            // there rather than a literal here would be better still in a real app: an unknown lane
            // name is CREATED at the default capacity rather than rejected.
            Lanes = [new WorkLane(WorkLanes.DemoIo)],
            Run = async work =>
            {
                // Real mutation of the claimed path, so the exclusion is doing something observable
                // rather than being asserted by a comment — and STAMPED, so the log proves both halves
                // at once: two entries for one path never overlap, while entries for different paths
                // do. A demo that only proves exclusion would pass on a fully serial scheduler.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.AppendAllTextAsync(path, Stamp(work.WorkId, kind, "in"), work.Cancellation);
                await Task.Delay(TimeSpan.FromSeconds(1.5), work.Cancellation);
                await File.AppendAllTextAsync(path, Stamp(work.WorkId, kind, "out"), work.Cancellation);
            },
        });

    private static string Stamp(string workId, string kind, string edge) =>
        $"{DateTimeOffset.Now:HH:mm:ss.fff}  {workId,-4} {kind,-9} {edge}\n";
}

/// <summary>Lane names the app configures once at startup and references by constant everywhere else.</summary>
public static class WorkLanes
{
    /// <summary>The demo's IO budget — capacity set in the desktop composition root.</summary>
    public const string DemoIo = "demo-io";
}
