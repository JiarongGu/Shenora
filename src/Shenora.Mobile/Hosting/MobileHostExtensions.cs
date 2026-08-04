using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Dispatching;
using Shenora.Core;

namespace Shenora.Mobile;

/// <summary>Registers the MAUI shell services on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class MobileHostExtensions
{
    /// <summary>
    /// Make this a MAUI-hosted application: registers the shell contracts this platform can honour
    /// (<see cref="IClipboardService"/>, <see cref="IUrlLauncher"/>, <see cref="IUiInteraction"/>,
    /// <see cref="IFileDialogs"/>, <see cref="IUiDispatcher"/>), each with <c>TryAdd</c> so an app
    /// registration wins.
    /// <para>
    /// <b>It registers NO <see cref="IShenoraRunner"/>, deliberately.</b> MAUI owns the loop, so
    /// <see cref="ShenoraApplication.Run"/> — contractually "blocks until shutdown" — has no honest
    /// implementation here. Drive <see cref="ShenoraApplication.Start"/> and
    /// <see cref="ShenoraApplication.Stop"/> from the app's own lifecycle instead; both are
    /// idempotent precisely because Android recreates an activity on a configuration change.
    /// </para>
    /// <para>
    /// The services that are NOT here are the point of the capability rule: no drop zones, no tray,
    /// no secondary windows, no window state. Those are absent on this platform rather than
    /// implemented differently, and portable logic asking for one gets a named refusal
    /// (<see cref="ShellCapability"/>) rather than a null or a silent nothing.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="dispatcher">
    /// The MAUI dispatcher UI work marshals to — typically <c>Application.Current.Dispatcher</c>, or
    /// the hosting page's. Required rather than resolved, because Core has no way to find it and a
    /// silently-missing UI dispatcher swallows UI work.
    /// </param>
    /// <param name="onError">Reports a failure from posted UI work or a backgrounded URL open.</param>
    public static ShenoraApplicationBuilder UseMobile(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dispatcher);

        builder.Services.TryAddSingleton<IUiDispatcher>(_ => new MobileUiDispatcher(dispatcher, onError));
        builder.Services.TryAddSingleton<IClipboardService, MobileClipboardService>();
        builder.Services.TryAddSingleton<IUrlLauncher>(_ => new MobileUrlLauncher(onError));
        builder.Services.TryAddSingleton<IUiInteraction, MobileUiInteraction>();
        builder.Services.TryAddSingleton<IFileDialogs, MobileFileDialogs>();

        // The system media transport surface. ONE NAME, two entirely separate bodies — Android registers a
        // MediaSession, iOS writes two process-wide singletons and shares no code with it at all. That is
        // unlike every service above, which really is one class for both platforms; the shared NAME is what
        // keeps this registration, the docs and the metadata baselines symmetrical anyway.
        //
        // LAZY, because both constructors touch platform state an app that never plays anything should not
        // pay for. DI disposes it, which matters on iOS: its command targets are attached to a shared
        // command center and would outlive the object otherwise.
        //
        // No log sink is passed, deliberately: `onError` takes an Exception, and wrapping a diagnostic line
        // in one would report ordinary information as a fault. An app that wants these diagnostics
        // registers its own instance with a sink — `TryAdd` means an app registration wins, which is the
        // same escape hatch every other service here has.
#if !ANDROID && !IOS && !MACCATALYST
        // A hard COMPILE error rather than a missing registration: without this a fourth shell would build
        // clean and fail at the INJECTION SITE, with nothing naming which platform forgot to implement it.
        // Same fail-closed choice as MobileWebViewInterceptor's undeclared range delivery.
#error Shenora.Mobile: this platform has no MobilePlaybackSession. Add one under Services/, or register a stub that throws ShellCapability.NotSupported.
#endif
        builder.Services.TryAddSingleton<IPlaybackSession>(_ => new MobilePlaybackSession());

        return builder;
    }
}
