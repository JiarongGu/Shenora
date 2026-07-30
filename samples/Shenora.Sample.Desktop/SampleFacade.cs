using Shenora.Core;   // the portable contracts (IFileDialogs, IUrlLauncher…) live here since D20
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
    SecondaryWindows windows,
    IEventBus events) : BaseFacade
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

            // ── The two shapes of a slow route, side by side (P6.3a) ─────────────────────────────
            // This exists to MEASURE the claim the one-way path rests on, not to assert it: a
            // route's synchronous segment runs on the host's UI THREAD, because the dispatch
            // pipeline preserves the caller's synchronization context by design. `Application
            // .MessageLoop` is true only on a thread running a WinForms message pump, so the flag
            // below is the proof, reported to the page rather than reasoned about.
            case "SLOW":
                var mode = PayloadHelper.GetOptionalValue<string>(request.Payload, "mode") ?? "stream";
                var totalMs = PayloadHelper.GetOptionalValue<int?>(request.Payload, "ms") ?? 3000;
                var onUiThread = Application.MessageLoop;

                if (mode == "block")
                {
                    // DELIBERATELY THE WRONG SHAPE, kept as the demonstration: heavy work left in the
                    // route's synchronous segment. The window stops repainting for the duration —
                    // including the 1 Hz tick — which is exactly why `invoke` is reserved for calls
                    // that are quick AND UI-thread-safe. Do not copy this into an app.
                    Thread.Sleep(totalMs);
                    return new { Mode = mode, RanOnUiThread = onUiThread };
                }

                // The right shape: hand the work OFF, return immediately, stream progress as
                // notifications. The background body must NOT capture the UI context (see
                // .claude/knowledge/ipc-contracts.md) or it would put the work back on the thread
                // this exists to free.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        const int steps = 6;
                        for (var step = 1; step <= steps; step++)
                        {
                            await Task.Delay(totalMs / steps).ConfigureAwait(false);
                            await events.EmitAsync("SAMPLE", "SLOW_PROGRESS",
                                new { Step = step, Steps = steps, OnUiThread = Application.MessageLoop })
                                .ConfigureAwait(false);
                        }
                        await events.EmitAsync("SAMPLE", "SLOW_DONE", new { Ok = true }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // An unguarded Task.Run body makes any fault an UNOBSERVED task exception —
                        // the same defect the streaming sample route was fixed for in P5.5 H9.
                        Console.Error.WriteLine($"[sample] SLOW stream failed: {ex}");
                    }
                });
                return new { Mode = mode, RanOnUiThread = onUiThread };

            default:
                // BaseFacade owns the unknown-type shape now — an app no longer retypes it.
                throw UnknownType(request);
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
