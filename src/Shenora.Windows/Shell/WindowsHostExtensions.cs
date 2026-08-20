using System.Runtime.InteropServices;
using Shenora;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.FileDialog;
using Shenora.Modules.Media;
using Shenora.Core.Shell;
using Shenora.Engine.Files;

namespace Shenora.Windows;

/// <summary>Single-instance behavior for <see cref="WindowsHostOptions.SingleInstance"/>.</summary>
public sealed class SingleInstanceHostOptions
{
    /// <summary>
    /// What "one instance" is scoped to. Null = the app's install root
    /// (<see cref="ShenoraPaths.RootDir"/>), so distinct installs coexist.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Argument a restart-relaunch passes so the gate waits for the outgoing instance instead of
    /// treating the overlap as a double-launch (the restart-through-launcher pattern).
    /// </summary>
    public string RestartArgument { get; init; } = "--restarted";

    /// <summary>
    /// How long a restart-relaunch waits for its predecessor's mutex — a graceful shutdown can spend
    /// many seconds draining before the mutex releases.
    /// </summary>
    public TimeSpan RestartWaitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// What a LOSING launch does before exiting. Null = <see cref="SingleInstanceGuard.BroadcastActivate"/>
    /// (the running instance comes to the front). A custom callback replaces that entirely, and receives
    /// the guard so it can still broadcast.
    /// </summary>
    public Action<ShenoraApplication, SingleInstanceGuard>? OnSecondInstance { get; init; }
}

/// <summary>Main-window state persistence for <see cref="WindowsHostOptions.WindowState"/>.</summary>
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

/// <summary>Inputs for <see cref="WindowsHostExtensions.UseWindows"/>.</summary>
public sealed class WindowsHostOptions
{
    /// <summary>
    /// Creates the main window once services are available — create it, don't show it or place it. The
    /// runner shows it via the message loop, applying any saved geometry after this factory returns.
    /// </summary>
    public required Func<IServiceProvider, Form> MainForm { get; init; }

    /// <summary>
    /// Process-init settings (DPI mode, crash handling…). Null = defaults with
    /// <see cref="WinFormsBootstrapOptions.ApplicationName"/> filled from the application name.
    /// A provided instance is used as-is.
    /// </summary>
    public WinFormsBootstrapOptions? Bootstrap { get; init; }

    /// <summary>
    /// Single-instance gate, on by default — single-writer databases and the WebView2 user-data lock
    /// corrupt under a second instance. Null for a deliberately multi-instance app.
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
public static class WindowsHostExtensions
{
    /// <summary>
    /// Make this a WinForms-hosted application: registers the runner <see cref="ShenoraApplication.Run"/>
    /// executes — single-instance gate, <see cref="WinFormsBootstrap.Initialize"/>, lifecycle hooks,
    /// main-form creation (+ optional window-state persistence), the message loop, and ordered shutdown.
    /// </summary>
    public static ShenoraApplicationBuilder UseWindows(this ShenoraApplicationBuilder builder,
        WindowsHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IShenoraRunner, WinFormsRunner>();

        // The native desktop services every WinForms app gets (TryAdd — an app registration wins).
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
        // The page's ROUTE to those dialogs, registered here rather than centrally because THIS is where
        // the platform implementation exists (D64): a shell without native dialogs registers neither, and
        // the page learns that from the ready handshake's capability list (D36).
        builder.Services.AddShenoraFileDialogs();

        // Everything below is registered LAZILY, so an app that never asks never pays: each of these
        // constructors builds real machinery (a media pipeline, an audio graph).
        builder.Services.TryAddSingleton<IPlaybackSession>(sp =>
            new WindowsPlaybackSession(sp.GetService<ILogger<WindowsPlaybackSession>>()));

        // "Who is holding this file open?" — the Restart Manager, hence a portable contract and a Windows
        // implementation (D19/D20).
        // ⚠ REGISTERING IT IS THE WHOLE POINT: unregistered, FileUpdateQueueOptions.LockInspector stays
        // null and a locked file reports "cannot tell" instead of naming the process — and since empty
        // legitimately MEANS "cannot tell", the degraded answer is indistinguishable from the honest one.
        builder.Services.TryAddSingleton<IFileLockInspector, RestartManagerLockInspector>();

        // What THIS MACHINE decodes and encodes — the kit ships the QUESTION, never a codec list (D42).
        // Singleton because it caches.
        builder.Services.TryAddSingleton<Shenora.Modules.Media.IMediaCapability>(sp =>
            new WindowsMediaCapability(sp.GetService<ILogger<WindowsMediaCapability>>()));

        // The HOST-OWNED PLAYER (D54) — Media Foundation through Windows.Media.Playback.
        //
        // 🔴 REGISTERED BY ITS OWN TYPE, NOT AS IMediaPlayer. The default IMediaPlayer stays the
        // PAGE-BACKED one (D58), which `useMediaPlayer(ref)` in @shenora/react binds to; grabbing
        // IMediaPlayer here would move audio out of the page's element and land PLAYER_REPORT on a player
        // with no Report to take — MediaPlayerModule short-circuits, so nothing fails, it quietly stops
        // working. So the native player is OPT-IN, resolved by name:
        //
        //     var player = services.GetRequiredService<WindowsMediaPlayer>();
        //     using var link = player.ReportTo(services.GetRequiredService<IPlaybackSession>());
        builder.Services.TryAddSingleton(sp =>
            new WindowsMediaPlayer(sp.GetService<ILogger<WindowsMediaPlayer>>()));

        // D20: expose the PORTABLE face of each split service beside the Windows one, resolving to the
        // SAME singleton, so app logic can inject Shenora contracts and compile with no Windows reference.
        builder.Services.TryAddSingleton<IUiInteraction>(sp => sp.GetRequiredService<IFormInteraction>());
        builder.Services.TryAddSingleton<IUrlLauncher>(sp => sp.GetRequiredService<IShellLauncher>());

        // ⚠ The UI dispatcher must resolve the main form LAZILY: this provider is built before the runner
        // creates the form, so anything captured here captures null.
        builder.Services.TryAddSingleton<IUiDispatcher>(sp =>
            new MainFormUiDispatcher(sp.GetRequiredService<IFormInteraction>()));
        return builder;
    }
}

/// <summary>The WinForms run sequence. The ORDER is load-bearing — see <c>docs/design/shells.md</c>.</summary>
internal sealed class WinFormsRunner : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        var options = app.Services.GetRequiredService<WindowsHostOptions>();

        // Single-instance gate FIRST — before any lifecycle hook takes an OS lock (the WebView2 prewarm
        // takes the user-data-folder lock), and so a losing launch answers instantly.
        SingleInstanceGuard? guard = null;
        if (options.SingleInstance is { } single)
        {
            guard = new SingleInstanceGuard(app.ApplicationName, single.Scope ?? app.Paths.RootDir);
            var wait = app.Args.Contains(single.RestartArgument, StringComparer.Ordinal)
                ? single.RestartWaitTimeout
                : TimeSpan.Zero;
            // Only AlreadyRunning stops the launch; Unverified means the guard failed open.
            if (guard.TryAcquire(wait) is SingleInstanceResult.AlreadyRunning)
            {
                if (single.OnSecondInstance is { } onSecond) onSecond(app, guard);
                else guard.BroadcastActivate();
                guard.Dispose();
                return;
            }
        }

        try
        {
            // Before any control exists — the DPI/text-rendering settings reject a later call.
            if (!options.SkipProcessInit)
            {
                WinFormsBootstrap.Initialize(options.Bootstrap
                    ?? new WinFormsBootstrapOptions { ApplicationName = app.ApplicationName });
            }

            // The hook sequence lives on ShenoraApplication (Start/Stop) so every runner shares one
            // ordering, one start/stop asymmetry and one idempotency rule.
            try
            {
                app.Start();

                using var form = options.MainForm(app.Services);

                // The native services need the main window (dialog ownership + modal blocking).
                app.Services.GetService<IFormInteraction>()?.SetMainForm(form);

                if (options.WindowState is { } windowState)
                {
                    // AttachTo owns the apply-before-show / save-on-closed ordering.
                    new WindowStateManager(windowState.Store(app.Services), windowState.Options).AttachTo(form);
                }

                // A 2nd launch of this scope broadcasts the guard's activation message; bring the main
                // window to the front when it arrives. A message filter, not a WndProc hook, so ANY Form
                // works with no base-class requirement.
                ActivateMessageFilter? filter = null;
                if (guard?.ActivateMessageId is { } activateMessageId)
                {
                    filter = new ActivateMessageFilter(form, activateMessageId);
                    Application.AddMessageFilter(filter);
                }
                else if (guard is not null)
                {
                    // 🔴 SAY SO. `RegisterWindowMessage` returned 0, so there is no channel and this app
                    // will NOT come to the front when a second launch activates it. Single instance still
                    // works — the mutex is the real guard — which is what makes it worth a line: the user
                    // double-clicks, the second process exits quietly, and the app looks broken with no
                    // trace anywhere. A WARNING, not Debug: only someone reading logs can act on it.
                    // ⚠ Not covered by a test — forcing it means exhausting a session-wide OS resource,
                    // which no test may do to the machine it runs on.
                    app.Services.GetService<ILogger<SingleInstanceGuard>>()?.LogWarning(
                        "The single-instance ACTIVATE channel is unavailable (RegisterWindowMessage "
                        + "returned 0): a second launch will exit quietly instead of bringing this window "
                        + "to the front. Single instance itself is unaffected. Usually a session out of "
                        + "atom-table space — signing out clears it.");
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
                app.Stop();
            }
        }
        finally
        {
            // Released LAST and explicitly, so a --restarted relaunch waiting on the mutex gets it the
            // moment shutdown work is done rather than at process teardown.
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
