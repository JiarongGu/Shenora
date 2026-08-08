using Shenora;   // the portable contracts (IFileDialogs, IUrlLauncher…) live here since D20
using Shenora.Windows;
using Shenora.Modules.FileDialog;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample backend module — the shape an app's facades take: one class per module, services
/// from DI, expected failures as structured <see cref="OperationException"/>s, payload reads
/// through <see cref="PayloadHelper"/>.
/// </summary>
internal sealed class SampleModule(
    IFileDialogs dialogs,
    IShellLauncher shell,
    SecondaryWindows windows,
    IEventBus events,
    MainForm mainForm,
    IUiDispatcher ui) : ModuleBase(events: events)
{
    // ⚠ This used to take an IIpcRequestTracker and forward it, and the sample was the ONLY thing in the
    // repo that did — no kit module ever had, so `LIST` and `CANCEL` saw this app's routes and nothing
    // else. Tracking is the dispatcher's now, so a module gets it by being dispatched at all. That the
    // parameter could go without a single route changing IS the acceptance evidence.

    /// <summary>
    /// The SLOW route's independent "it actually started" signal — a substring of the native
    /// window's TITLE (never the page's HTML title; this app is frameless and draws its own title
    /// bar in React, but the underlying Win32 window still has a real caption `GetWindowText` can
    /// read). This exists because a click can land on the WebView2 render surface — which spans the
    /// whole client area — WITHOUT ever reaching the intended button (stale fraction coordinates, a
    /// moved layout, a disabled control): `win-input` would still report "click ok" in that case,
    /// so `devtools/ui-responsiveness` cannot trust the click alone as proof the operation ran. The
    /// title is set HERE, synchronously, on the UI thread, BEFORE either shape's slow work begins —
    /// deliberately, because `block` freezes this very thread for the rest of the route, and Win32
    /// caches a window's title in shared, cross-process-readable state that a foreign process can
    /// read even while the owning thread is unresponsive (the same reason Alt-Tab/Task Manager still
    /// show a hung app's title). Setting it AFTER the freeze began would be too late to observe.
    /// Kept in sync with the literal `--title-contains` default in `devtools/ui-responsiveness`.
    /// </summary>
    private const string RunningTitleMarker = "SLOW running";

    public override string ModuleName => "SAMPLE";

    protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
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
                var picked = await dialogs.OpenFileAsync(new OpenFileOptions
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
                    // Set BEFORE the freeze, on the UI thread we are already (synchronously) on —
                    // see RunningTitleMarker's doc for why this ordering is load-bearing.
                    mainForm.Text = $"Shenora Sample - {RunningTitleMarker} (block)";
                    // DELIBERATELY THE WRONG SHAPE, kept as the demonstration: heavy work left in the
                    // route's synchronous segment. The window stops repainting for the duration —
                    // including the 1 Hz tick — which is exactly why `invoke` is reserved for calls
                    // that are quick AND UI-thread-safe. Do not copy this into an app.
                    Thread.Sleep(totalMs);
                    // Still the UI thread, still synchronous — no marshalling needed for the reset either.
                    mainForm.Text = "Shenora Sample";
                    return new { Mode = mode, RanOnUiThread = onUiThread };
                }

                // Same signal for the streamed shape, set synchronously before the handoff below —
                // ctx.Run's own Start() call is synchronous, so this still runs on the UI thread.
                mainForm.Text = $"Shenora Sample - {RunningTitleMarker} (stream)";

                // 🔴 THE WHOLE ROUTE NOW — and what is NOT here is D66 (2026-08-08). There is no
                // handoff, no options record, no second id to return, and nothing that declares this
                // route "long-running". It is an ordinary await, and the request simply takes a while.
                //
                // The token is the REQUEST's, which is now the right one to observe rather than a
                // trap: CANCEL targets the request id, so aborting from the page cancels exactly this.
                // The old shape had to invent an operation token precisely because the request's own
                // died with the response — the merge removed that gap instead of working around it.
                //
                // Nothing is emitted at all unless this outlives the grace period, so the fast case
                // stays silent without the route knowing anything about it.
                try
                {
                    const int steps = 6;
                    for (var step = 1; step <= steps; step++)
                    {
                        await Task.Delay(totalMs / steps, cancellationToken).ConfigureAwait(false);
                        // The general shape, not the percent special case (adopters copy this sample):
                        // Value/Total/Unit in the app's own terms — a UI renders a ratio because Total
                        // is set, never because the kit assumed percent.
                        context.Report(new IpcProgress(step, steps, "steps"),
                            new IpcLabel(Text: $"step {step}/{steps} (onUiThread: {Application.MessageLoop})"));
                    }
                }
                finally
                {
                    // finally, not a trailing statement: this body can also exit via cancellation or a
                    // fault, and the title must not stick at "running" on either path. Marshalled
                    // through the ONE seam rather than touching the Form from a background thread.
                    ui.Post(() => mainForm.Text = "Shenora Sample");
                }

                // ONE id, and it is the request's own — the page already has it.
                return new { Mode = mode, RanOnUiThread = onUiThread, RequestId = context.RequestId };

            default:
                // ModuleBase owns the unknown-type shape now — an app no longer retypes it.
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
