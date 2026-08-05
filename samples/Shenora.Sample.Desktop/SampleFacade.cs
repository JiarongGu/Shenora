using Shenora.Core;   // the portable contracts (IFileDialogs, IUrlLauncher…) live here since D20
using Shenora.Ipc;
using Shenora.Windows;

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
    IEventBus events,
    IOperationRegistry operations,
    MainForm mainForm,
    IUiDispatcher ui) : BaseFacade(events: events, operations: operations)
{
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

                // The right shape, now one call: Run owns the handoff, the guarded body, the terminal
                // transition and the token. What the sample used to hand-roll here — Task.Run, a catch
                // that existed only to stop an unobserved fault, ConfigureAwait(false) and a hardcoded
                // "SAMPLE" literal — is the kit's job as of 0.2.0 (D23).
                //
                // The body gets the OPERATION's own token (via `operation`/`ct`), never the request's:
                // this route still does not observe `cancellationToken` (the request's lifetime) —
                // work handed off outlives the request by design, and capturing the request token would
                // kill a long operation the moment the page navigated. Using the operation's own token
                // also means the CANCEL route (OperationsFacade) can now actually stop this work, which
                // the old hand-rolled version could not offer.
                var operationId = context.Run(
                    new OperationOptions { Kind = "SLOW", Cancellable = true, Title = new OperationLabel(Text: "Slow work") },
                    async (operation, ct) =>
                    {
                        // finally, not just a trailing statement after the loop: this body can also
                        // exit via OperationCanceledException (a CANCEL request) or a fault, and the
                        // title must not stick at "running" forever on either of those paths either.
                        // Off the UI thread here (ConfigureAwait(false) below, per D23/ipc-contracts),
                        // so the reset is marshalled through the ONE seam rather than touching the
                        // Form directly from a background thread.
                        try
                        {
                            const int steps = 6;
                            for (var step = 1; step <= steps; step++)
                            {
                                await Task.Delay(totalMs / steps, ct).ConfigureAwait(false);
                                // The general shape, not the percent special case (adopters copy this
                                // sample): Value/Total/Unit in the app's own terms — a UI renders a
                                // ratio because Total is set, never because the kit assumed percent.
                                operation.Report(new OperationProgress(step, steps, "steps"),
                                    new OperationLabel(Text: $"step {step}/{steps} (onUiThread: {Application.MessageLoop})"));
                            }
                        }
                        finally
                        {
                            ui.Post(() => mainForm.Text = "Shenora Sample");
                        }
                    });
                return new { Mode = mode, RanOnUiThread = onUiThread, OperationId = operationId };

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
