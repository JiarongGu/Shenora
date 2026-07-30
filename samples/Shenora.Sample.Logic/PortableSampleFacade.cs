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
    IUiDispatcher ui) : BaseFacade
{
    /// <summary>The reserved module name for the portable half of the sample.</summary>
    public const string Module = "SAMPLE_LOGIC";

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override async Task<object?> RouteMessageAsync(IpcRequest request)
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

            default:
                throw new OperationException(IpcErrorCodes.NoHandler,
                    new Dictionary<string, string> { ["module"] = Module, ["type"] = request.Type });
        }
    }
}
