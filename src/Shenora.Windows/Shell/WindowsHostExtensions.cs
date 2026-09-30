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
using Shenora.Chromium;

namespace Shenora.Windows;

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

    /// <summary>
    /// Run Chromium beside the WinForms loop, for <see cref="ChromiumView"/> controls (D83): CEF's message loop runs
    /// on a thread of its own. The runner answers CEF's subprocesses before the single-instance gate, starts the
    /// engine after the process init and before the first hook, and stops it after the loop, once every window's
    /// browser has closed, <see cref="SecondaryWindows"/> included. Call it beside <see cref="UseWindows"/>, in
    /// either order; resolve the <see cref="ChromiumEngine"/> for each view.
    /// <para>
    /// ⚠ <b>The app's own project must reference the <c>Shenora.Chromium</c> package.</b> Its build fetches CEF and
    /// lays the app out beside CEF's launcher, and a reference that only reaches it through this package does not
    /// run that build, so the engine would find no CEF beside the app and say so as it starts.
    /// </para>
    /// </summary>
    /// <param name="builder">The app being composed.</param>
    /// <param name="options">Where the pages come from, where CEF keeps its cache, and development settings.</param>
    public static ShenoraApplicationBuilder UseChromiumEngine(this ShenoraApplicationBuilder builder, ChromiumEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.TryAddSingleton(sp => new ChromiumEngine(options, sp.GetService<ILogger<ChromiumEngine>>()));
        return builder;
    }
}

/// <summary>The WinForms run sequence. The ORDER is load-bearing — see <c>docs/design/shells.md</c>.</summary>
internal sealed class WinFormsRunner : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        var options = app.Services.GetRequiredService<WindowsHostOptions>();

        // A Chromium engine (UseChromiumEngine) answers CEF's subprocesses before anything: an app running an exe of
        // its own with no launcher beside it is every one of them, and one that went on would meet the single-instance
        // gate and exit. The build's layout never gets here as a subprocess: its subprocesses run through the launcher.
        var engine = app.Services.GetService<ChromiumEngine>();
        if (engine is not null && ChromiumEngine.RunIfSubprocess(out var subprocessExit))
        {
            Environment.Exit(subprocessExit);
            return;
        }

        // Single-instance gate FIRST — before any lifecycle hook takes an OS lock (the WebView2 prewarm
        // takes the user-data-folder lock), and so a losing launch answers instantly.
        SingleInstanceGuard? guard = null;
        var owned = false;
        if (options.SingleInstance is { } single)
        {
            guard = new SingleInstanceGuard(app.ApplicationName, single.Scope ?? app.Paths.RootDir,
                app.Services.GetService<ILogger<SingleInstanceGuard>>());
            var wait = app.Args.Contains(single.RestartArgument, StringComparer.Ordinal)
                ? single.RestartWaitTimeout
                : TimeSpan.Zero;
            // Only AlreadyRunning stops the launch; Unverified means the guard failed open, and owns nothing to listen on.
            var result = guard.TryAcquire(wait);
            owned = result is SingleInstanceResult.Acquired;
            if (result is SingleInstanceResult.AlreadyRunning)
            {
                // This launch holds the foreground, which Windows lets the running instance take only when handed.
                AllowSetForegroundWindow(ASFW_ANY);
                if (single.OnSecondInstance is { } onSecond) onSecond(app, guard);
                else guard.ActivateRunning(app.Args);
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

            // After the process init, which must be the first to set the DPI mode; before any hook or form, since
            // a ChromiumView opens its browser as its handle is created.
            engine?.Start(app);

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

                // A later launch of this scope reaches the guard's channel: bring the main window to the front, then
                // hand the app its arguments. Listening once the form is SHOWN, when its handle exists to marshal
                // to; a launch before that waits for the channel to open.
                if (owned && guard is not null && options.SingleInstance is { } listening)
                {
                    form.Shown += (_, _) => ListenForLaterLaunches(app, form, guard, listening);
                }
                if (options.MessageLoop is { } loop) loop(form);
                else Application.Run(form);
            }
            finally
            {
                app.Stop();
            }
        }
        finally
        {
            try
            {
                if (engine is not null)
                {
                    // Every browser closes before CEF does. A secondary window outlives the main loop on a thread of
                    // its own, so close them; Stop waits, briefly, for their browsers.
                    app.Services.GetService<SecondaryWindows>()?.CloseAll();
                    engine.Stop();
                }
            }
            finally
            {
                // Released LAST and explicitly, so a --restarted relaunch waiting on the mutex gets it the
                // moment shutdown work is done rather than at process teardown, even when that work threw.
                guard?.Dispose();
            }
        }
    }

    private static void ListenForLaterLaunches(ShenoraApplication app, Form form, SingleInstanceGuard guard, SingleInstanceHostOptions options)
    {
        var log = app.Services.GetService<ILogger<SingleInstanceGuard>>();
        // The one marshalling owner: a form closing or gone answers false rather than throwing on the channel's thread.
        var ui = new WinFormsUiDispatcher(form, ex => log?.LogWarning(ex, "Bringing the window forward for a later launch failed."));
        try
        {
            guard.Listen(launch => ui.Post(() =>
            {
                BringForward(form);
                if (options.OnActivated is { } onActivated)
                {
                    AppCallback.Run(() => onActivated(app, launch),
                        ex => log?.LogError(ex, "The app's handling of a later launch (OnActivated) threw."));
                }
            }));
        }
        catch (Exception ex)
        {
            // 🔴 SAY SO. With no channel this app will NOT come to the front when a later launch asks. Single
            // instance still works — the mutex is the real guard — which is what makes it worth a line: the user
            // double-clicks, the second process exits quietly, and the app looks broken with no trace anywhere.
            app.Services.GetService<ILogger<SingleInstanceGuard>>()?.LogWarning(ex,
                "The single-instance channel could not be opened: a later launch will exit without bringing this "
                + "window to the front. Single instance itself is unaffected.");
        }
    }

    // Restored if minimized, shown if hidden (close-to-tray), then the foreground, which the later launch handed over.
    private static void BringForward(Form form)
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

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
