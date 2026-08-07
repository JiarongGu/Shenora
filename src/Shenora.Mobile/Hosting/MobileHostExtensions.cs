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

        // The live status surface. Registered on BOTH shells even though only iOS can do it, because the
        // contract carries its own "cannot" channel (`Unavailable`) — so portable logic asks and branches
        // instead of catching, and Android answers with a reason rather than failing at the injection site
        // with a message about a missing service.
        builder.Services.TryAddSingleton<ILiveActivities>(_ => new MobileLiveActivities());

        // What THIS DEVICE can decode and encode. Registered on both shells because the answer differs
        // between them AND between devices of the same platform — Android codec support is vendor-declared,
        // which is why MediaCodecList is a runtime query. Before this, an app filling MediaPlaybackPolicy
        // had to GUESS those sets; the kit still ships no codec list, it ships the question (D42).
        //
        // Singleton because both implementations cache: the Android walk allocates a Java object per codec
        // and the iOS one builds a converter per candidate, and neither answer can change while the process
        // runs.
        builder.Services.TryAddSingleton<Shenora.Media.IMediaCapability>(_ => new MobileMediaCapability());

        return builder;
    }
}
