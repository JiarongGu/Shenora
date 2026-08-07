using System.Runtime.InteropServices;
using Shenora.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shenora.Windows;

/// <summary>Single-instance behavior for <see cref="WinFormsHostOptions.SingleInstance"/>.</summary>
public sealed class SingleInstanceHostOptions
{
    /// <summary>
    /// What "one instance" is scoped to. Null = the app's install root
    /// (<see cref="ShenoraPaths.RootDir"/>), the family default — distinct installs coexist.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Argument a restart-relaunch passes so the gate waits for the outgoing instance instead of
    /// treating the overlap as a double-launch (the restart-through-launcher pattern).
    /// </summary>
    public string RestartArgument { get; init; } = "--restarted";

    /// <summary>
    /// How long a restart-relaunch waits for its predecessor's mutex. The family-proven budget:
    /// a graceful shutdown can spend many seconds draining before the mutex releases.
    /// </summary>
    public TimeSpan RestartWaitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// What a LOSING launch does before exiting. Null = the family default,
    /// <see cref="SingleInstanceGuard.BroadcastActivate"/> (the running instance comes to the
    /// front). A custom callback replaces that entirely — it receives the guard so it can still
    /// broadcast if it wants to (e.g. after showing an "already running" notice).
    /// </summary>
    public Action<ShenoraApplication, SingleInstanceGuard>? OnSecondInstance { get; init; }
}

/// <summary>Main-window state persistence for <see cref="WinFormsHostOptions.WindowState"/>.</summary>
public sealed class WindowStateHostOptions
{
    /// <summary>
    /// Where the state lives (e.g. a <see cref="JsonFileWindowStateStore"/> under one of the
    /// app's data areas). Required — the framework does not invent a storage location.
    /// </summary>
    public required Func<IServiceProvider, IWindowStateStore> Store { get; init; }

    /// <summary>Sizing defaults/minimums. Null = <see cref="WindowStateOptions"/> defaults.</summary>
    public WindowStateOptions? Options { get; init; }
}

/// <summary>Inputs for <see cref="WinFormsHostExtensions.UseWinForms"/>.</summary>
public sealed class WinFormsHostOptions
{
    /// <summary>
    /// Creates the main window once services are available. The runner shows it via the message
    /// loop — create it, don't show it. When <see cref="WindowState"/> is set the runner applies
    /// the saved geometry after this factory returns, so the factory should not place the form
    /// itself.
    /// </summary>
    public required Func<IServiceProvider, Form> MainForm { get; init; }

    /// <summary>
    /// Process-init settings (DPI mode, crash handling…). Null = defaults with
    /// <see cref="WinFormsBootstrapOptions.ApplicationName"/> filled from the application name.
    /// A provided instance is used as-is.
    /// </summary>
    public WinFormsBootstrapOptions? Bootstrap { get; init; }

    /// <summary>
    /// Single-instance gate. Enabled by default (every family app needs it — single-writer
    /// databases and the WebView2 user-data lock corrupt under a second instance); set null for
    /// a deliberately multi-instance app.
    /// </summary>
    public SingleInstanceHostOptions? SingleInstance { get; init; } = new();

    /// <summary>Main-window geometry persistence. Null = the app manages its own (or none).</summary>
    public WindowStateHostOptions? WindowState { get; init; }

    /// <summary>Test seam: replaces the blocking <c>Application.Run(form)</c> call.</summary>
    internal Action<Form>? MessageLoop { get; init; }

    /// <summary>Test seam: skip <see cref="WinFormsBootstrap.Initialize"/> (process-global, once).</summary>
    internal bool SkipProcessInit { get; init; }
}

/// <summary>Registers the WinForms host loop on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class WinFormsHostExtensions
{
    /// <summary>
    /// Make this a WinForms-hosted application: registers the runner that
    /// <see cref="ShenoraApplication.Run"/> executes — single-instance gate (with the
    /// <c>--restarted</c> widened-wait handoff), <see cref="WinFormsBootstrap.Initialize"/>,
    /// lifecycle hooks, main-form creation (+ optional window-state persistence and
    /// activate-on-second-launch), the message loop, and ordered shutdown.
    /// </summary>
    public static ShenoraApplicationBuilder UseWinForms(this ShenoraApplicationBuilder builder,
        WinFormsHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IShenoraRunner, WinFormsRunner>();

        // The native desktop services every WinForms app gets (TryAdd — an app registration
        // wins). The runner registers the main form on IFormInteraction; dialogs pick up an
        // app-registered IFileDialogPathStore for cross-session directory memory.
        builder.Services.TryAddSingleton<IFormInteraction, FormInteraction>();
        builder.Services.TryAddSingleton<IShellLauncher, ShellLauncher>();
        builder.Services.TryAddSingleton<IClipboardService, ClipboardService>();
        builder.Services.TryAddSingleton<IFileDialogs>(sp => new FileDialogs(
            new FileDialogsOptions
            {
                Interaction = sp.GetService<IFormInteraction>(),
                PathStore = sp.GetService<IFileDialogPathStore>(),
            },
            sp.GetService<ILogger<FileDialogs>>()));

        // The system media transport surface, registered LAZILY like everything else here — a
        // WindowsPlaybackSession creates a MediaPlayer in its constructor, and an app that never plays
        // anything should not pay for a media pipeline just by calling UseWinForms(). DI disposes it.
        builder.Services.TryAddSingleton<IPlaybackSession>(sp =>
            new WindowsPlaybackSession(message =>
                sp.GetService<ILogger<WindowsPlaybackSession>>()?.LogDebug("{Message}", message)));

        // "Who is holding this file open?" — Windows answers with the Restart Manager, which is why the
        // CONTRACT is portable and this implementation is not (D19/D20, and D31's two-mechanisms split).
        //
        // ⚠ IT WAS NEVER REGISTERED until 2026-08-07. RestartManagerLockInspector shipped, was documented,
        // was tested — and no container ever built one, so FileUpdateQueueOptions.LockInspector was always
        // null and a locked file reported "cannot tell" instead of naming the process. Nothing failed:
        // `WhoHolds` empty legitimately MEANS "cannot tell", so the degraded answer was indistinguishable
        // from the honest one. Same failure mode as D59 — a capability that is ABSENT rather than broken
        // produces no error, no log line and no failing test.
        builder.Services.TryAddSingleton<IFileLockInspector, RestartManagerLockInspector>();

        // What THIS MACHINE decodes and encodes. Registered here for the same reason both mobile shells
        // register theirs: the kit ships the QUESTION, never a codec list (D42), and a shell that cannot
        // answer it pushes the guess back onto every app. Windows was the one shell that answered nothing.
        //
        // Singleton because it caches — the codec set cannot change while the process runs (an installed
        // extension needs a restart), and each query walks the platform's MFT list.
        builder.Services.TryAddSingleton<Shenora.Media.IMediaCapability>(sp =>
            new WindowsMediaCapability(message =>
                sp.GetService<ILogger<WindowsMediaCapability>>()?.LogDebug("{Message}", message)));

        // D20: expose the PORTABLE face of each split service beside the Windows one, resolving to
        // the SAME singleton — so an app's own logic can inject Shenora.Core contracts, compile
        // without a Windows reference, and still get these implementations at runtime.
        builder.Services.TryAddSingleton<IUiInteraction>(sp => sp.GetRequiredService<IFormInteraction>());
        builder.Services.TryAddSingleton<IUrlLauncher>(sp => sp.GetRequiredService<IShellLauncher>());

        // The UI dispatcher must resolve the main form LAZILY: this provider is built before the
        // runner creates the form, so anything captured here would capture null.
        builder.Services.TryAddSingleton<IUiDispatcher>(sp =>
            new MainFormUiDispatcher(sp.GetRequiredService<IFormInteraction>()));
        return builder;
    }
}

/// <summary>
/// The WinForms run sequence — the composition the source apps each hand-rolled, in the order
/// their comments say is load-bearing.
/// </summary>
internal sealed class WinFormsRunner : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        var options = app.Services.GetRequiredService<WinFormsHostOptions>();

        // Single-instance gate FIRST — before any lifecycle hook or heavy init. Hooks may take
        // OS locks (the WebView2 environment prewarm takes the user-data-folder lock), and a
        // losing launch should answer instantly, not after building half an app.
        SingleInstanceGuard? guard = null;
        if (options.SingleInstance is { } single)
        {
            guard = new SingleInstanceGuard(app.ApplicationName, single.Scope ?? app.Paths.RootDir);
            var wait = app.Args.Contains(single.RestartArgument, StringComparer.Ordinal)
                ? single.RestartWaitTimeout
                : TimeSpan.Zero;
            if (!guard.TryAcquire(wait))
            {
                if (single.OnSecondInstance is { } onSecond) onSecond(app, guard);
                else guard.BroadcastActivate();
                guard.Dispose();
                return;
            }
        }

        try
        {
            // Process init before any control exists (the DPI/text-rendering settings reject
            // later calls — see WinFormsBootstrap).
            if (!options.SkipProcessInit)
            {
                WinFormsBootstrap.Initialize(options.Bootstrap
                    ?? new WinFormsBootstrapOptions { ApplicationName = app.ApplicationName });
            }

            // The hook sequence lives on ShenoraApplication (Start/Stop) so every runner — and a host
            // whose platform owns the loop and cannot use Run() at all — shares one ordering,
            // one start/stop asymmetry and one idempotency rule.
            try
            {
                app.Start();

                using var form = options.MainForm(app.Services);

                // The native services need the main window (dialog ownership + modal blocking).
                app.Services.GetService<IFormInteraction>()?.SetMainForm(form);

                if (options.WindowState is { } windowState)
                {
                    // Apply BEFORE the loop shows the form (geometry set after show causes a
                    // visible jump); persist on FormClosed, when the bounds are still readable.
                    // AttachTo owns the apply-before-show / save-on-closed ordering (P5.5 H4.5).
                    new WindowStateManager(windowState.Store(app.Services), windowState.Options).AttachTo(form);
                }

                // A 2nd launch of this scope broadcasts the guard's activation message; bring the
                // main window to the front when it arrives. A message filter (not a WndProc hook)
                // so ANY Form works — no base-class requirement on the app.
                ActivateMessageFilter? filter = null;
                if (guard is { ActivateMessageId: not 0 })
                {
                    filter = new ActivateMessageFilter(form, guard.ActivateMessageId);
                    Application.AddMessageFilter(filter);
                }
                try
                {
                    if (options.MessageLoop is { } loop) loop(form);
                    else Application.Run(form);
                }
                finally
                {
                    if (filter is not null) Application.RemoveMessageFilter(filter);
                }
            }
            finally
            {
                // Reverse order, each step guarded, and it runs even when startup failed partway —
                // all of that is ShenoraApplication.Stop's contract now.
                app.Stop();
            }
        }
        finally
        {
            // Released LAST and explicitly, so a --restarted relaunch waiting on the mutex gets
            // it the moment shutdown work is done rather than at process teardown.
            guard?.Dispose();
        }
    }
}

/// <summary>
/// Watches the UI thread's message queue for the single-instance activation broadcast and brings
/// the main window to the foreground (restoring it if minimized).
/// </summary>
internal sealed class ActivateMessageFilter(Form form, uint messageId) : IMessageFilter
{
    public bool PreFilterMessage(ref Message m)
    {
        if ((uint)m.Msg == messageId && !form.IsDisposed)
        {
            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }
            form.Show();
            form.Activate();
            form.BringToFront();
            SetForegroundWindow(form.Handle);
        }
        return false; // never consume — the broadcast is harmless to let through
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
