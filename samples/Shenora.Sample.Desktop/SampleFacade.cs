using Shenora.Ipc;
using Shenora.WinForms;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample backend module — the shape an app's facades take: one class per module, services
/// from DI, expected failures as structured <see cref="OperationException"/>s, payload reads
/// through <see cref="PayloadHelper"/>.
/// </summary>
internal sealed class SampleFacade(
    IFileDialogs dialogs,
    IShellLauncher shell,
    SecondaryWindows windows) : BaseFacade
{
    public override string ModuleName => "SAMPLE";

    protected override async Task<object?> RouteMessageAsync(IpcRequest request)
    {
        switch (request.Type)
        {
            // React → typed .NET handler → typed response (the e2e round-trip subject).
            case "ECHO":
                var text = PayloadHelper.GetRequiredValue<string>(request.Payload, "text");
                return new { Echoed = text.ToUpperInvariant(), Length = text.Length };

            // Structured-error demo: the client sees { code: "SAMPLE_FAILURE", parameters: { reason } }.
            case "FAIL":
                throw new OperationException("SAMPLE_FAILURE", "reason", "requested by the client");

            // Native file dialog (P4.3) — a human picks; not driven by the automated e2e.
            case "PICK_FILE":
                var picked = await dialogs.OpenFileAsync(new FileDialogOptions
                {
                    Title = "Pick any file",
                    RememberPathKey = "sample-pick",
                });
                return picked;

            // Reveal the picked path in Explorer (P4.3) — manual demo.
            case "REVEAL":
                shell.RevealInExplorer(PayloadHelper.GetRequiredValue<string>(request.Payload, "path"));
                return null;

            // Secondary window on its own STA thread (P4.5) — driven by the e2e over CDP.
            case "OPEN_PANEL":
                var opened = windows.Open("panel", new SecondaryWindowOptions
                {
                    CreateForm = () => CreatePanelForm(),
                });
                return new { Opened = opened };

            case "HAS_PANEL":
                return new { Open = windows.HasWindow("panel") };

            case "CLOSE_PANEL":
                windows.Close("panel");
                return null;

            default:
                throw new OperationException(IpcErrorCodes.NoHandler,
                    new Dictionary<string, string> { ["module"] = "SAMPLE", ["type"] = request.Type });
        }
    }

    private static Form CreatePanelForm()
    {
        var form = new Form
        {
            Text = "Shenora Sample — panel",
            Size = new Size(360, 200),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = MainForm.Background,
            ShowInTaskbar = true,
        };
        form.Controls.Add(new Label
        {
            Text = "神阙 secondary window\r\n(own STA thread, own message pump)",
            ForeColor = Color.FromArgb(127, 209, 140),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        return form;
    }
}
