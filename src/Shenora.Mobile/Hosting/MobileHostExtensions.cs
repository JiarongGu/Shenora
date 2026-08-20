using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Modules.Platform;
using Shenora.Modules.FileDialog;
using Shenora.Modules.Media;
using Shenora.Core.Shell;

// Shared source: this file compiles into BOTH Shenora.Android and Shenora.iOS. The implementations differ
// per shell and these aliases are the only place that shows.
#if ANDROID

using PlatformFileDialogs = Shenora.Android.AndroidFileDialogs;
using PlatformLiveActivities = Shenora.Android.AndroidLiveActivities;
using PlatformMediaAudioConversion = Shenora.Android.AndroidMediaAudioConversion;
using PlatformMediaVideoConversion = Shenora.Android.AndroidMediaVideoConversion;
using PlatformMediaCapability = Shenora.Android.AndroidMediaCapability;
using PlatformMediaPlayer = Shenora.Android.AndroidMediaPlayer;
using PlatformPlaybackSession = Shenora.Android.AndroidPlaybackSession;
#elif IOS || MACCATALYST

using PlatformFileDialogs = Shenora.iOS.IosFileDialogs;
using PlatformLiveActivities = Shenora.iOS.IosLiveActivities;
using PlatformMediaAudioConversion = Shenora.iOS.IosMediaAudioConversion;
using PlatformMediaVideoConversion = Shenora.iOS.IosMediaVideoConversion;
using PlatformMediaCapability = Shenora.iOS.IosMediaCapability;
using PlatformMediaPlayer = Shenora.iOS.IosMediaPlayer;
using PlatformPlaybackSession = Shenora.iOS.IosPlaybackSession;
#endif

namespace Shenora.Mobile;

/// <summary>Registers the MAUI shell services on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class MobileHostExtensions
{
    /// <summary>
    /// Make this a MAUI-hosted application — named for the platform, <c>UseAndroid</c> on Android and
    /// <c>UseIOS</c> on iOS over one shared body (D65). Registers the shell contracts this platform can
    /// honour (<see cref="IClipboardService"/>, <see cref="IUrlLauncher"/>, <see cref="IUiInteraction"/>,
    /// <see cref="IFileDialogs"/>, <see cref="IUiDispatcher"/>), each with <c>TryAdd</c> so an app
    /// registration wins. What this shell cannot honour — drop zones, tray, secondary windows, window
    /// state — gets a named refusal (<see cref="ShellCapability"/>), never a null or a silent nothing.
    /// <para>
    /// ⚠ <b>Registers NO <see cref="IShenoraRunner"/>:</b> MAUI owns the loop, so
    /// <see cref="ShenoraApplication.Run"/> — contractually "blocks until shutdown" — has no honest
    /// implementation here. Drive <see cref="ShenoraApplication.Start"/> and
    /// <see cref="ShenoraApplication.Stop"/> from the app's own lifecycle instead; both are idempotent,
    /// because Android recreates an activity on a configuration change.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="dispatcher">
    /// The MAUI dispatcher UI work marshals to — typically <c>Application.Current.Dispatcher</c>, or the
    /// hosting page's.
    /// </param>
    /// <param name="onError">Reports a failure from posted UI work or a backgrounded URL open.</param>
#if ANDROID
    public static ShenoraApplicationBuilder UseAndroid(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
#elif IOS || MACCATALYST
    public static ShenoraApplicationBuilder UseIOS(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
#else
#error Shenora.Mobile: this platform has no shell entry point. Add a Use<Platform>() arm above (D65).
#endif
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dispatcher);

        builder.Services.TryAddSingleton<IUiDispatcher>(_ => new MobileUiDispatcher(dispatcher, onError));
        builder.Services.TryAddSingleton<IClipboardService, MobileClipboardService>();
        builder.Services.TryAddSingleton<IUrlLauncher>(_ => new MobileUrlLauncher(onError));
        builder.Services.TryAddSingleton<IUiInteraction, MobileUiInteraction>();
        builder.Services.TryAddSingleton<IFileDialogs, PlatformFileDialogs>();
        // The page's ROUTE to them (D64). ⚠ Two of the four routes are DESKTOP capabilities and refuse here
        // with CapabilityNotSupported (D35); the page asks the handshake what this shell honours (D36).
        builder.Services.AddShenoraFileDialogs();

        // The system media transport surface: ONE NAME, two entirely separate bodies (Android registers a
        // MediaSession, iOS writes two process-wide singletons). Lazy — both constructors touch platform
        // state. ⚠ DI must dispose it: on iOS its command targets attach to a shared command center and
        // would outlive the object.
#if !ANDROID && !IOS && !MACCATALYST
#error Shenora.Mobile: this platform has no PlatformPlaybackSession. Add one under Services/, or register a stub that throws ShellCapability.NotSupported.
#endif
        builder.Services.TryAddSingleton<IPlaybackSession>(_ => new PlatformPlaybackSession());

        // The live status surface, registered on BOTH shells though only iOS can do it: the contract carries
        // its own `Unavailable` channel, so Android answers with a reason instead of a missing-service error.
        builder.Services.TryAddSingleton<ILiveActivities>(_ => new PlatformLiveActivities());

        // What THIS DEVICE can decode and encode — a runtime query, because the answer differs per platform
        // AND per device (D42). Singleton because both implementations cache.
        builder.Services.TryAddSingleton<Shenora.Modules.Media.IMediaCapability>(_ => new PlatformMediaCapability());

#if ANDROID || IOS || MACCATALYST
        // The transcode tier — the device→webview gap of D59, on both mobile shells.
        // ⚠ Optional, unlike the playback session above: Mp4Remuxer takes it as a nullable, so a shell with
        // no converter gets container repair plus a REPORTED drop (MediaRemuxerResult.Dropped), not a silent
        // one. The FACTORY is shared; a run is not — each Begin() holds two real codec instances and a
        // device has only a handful.
        builder.Services.TryAddSingleton<Shenora.Modules.Media.IMediaStreamConversion>(services =>
        {
            // The pipeline arrives with this platform's converters in it; an app adds its own with
            // pipeline.Use(...) and keeps these behind it.
            // 🔴 THE DEVICE IS HANDED IN, so `CanConvert` answers from what this hardware reports instead of
            // by BUILDING the converter's codecs on every ask — otherwise the kit both promises work from an
            // encoder alone and refuses a codec that only lacked its file's ESDS.
            var pipeline = new Shenora.Modules.Media.MediaConversionPipeline(
                services.GetService<Shenora.Modules.Media.IMediaCapability>());

            // ⚠ The SAME `Log` the media routes use. Absent, this is null and the converters are MUTE —
            // that silence is what costs a device round trip.
            var log = services.GetService<Shenora.Modules.Media.MediaAccessOptions>()?.Log;
            PlatformMediaAudioConversion.Use(pipeline, log);

            // The PICTURE half, on both shells: each platform decodes video codecs its own webview refuses,
            // which surfaces as sound with a blank picture and NO error.
            PlatformMediaVideoConversion.Use(pipeline, log);
            return pipeline;
        });
#endif

#if IOS || MACCATALYST || ANDROID
        // The HOST-OWNED PLAYER (D54) — AVPlayer on iOS, android.media.MediaPlayer on Android. Singleton
        // for the same reason IPlaybackSession is: a handle on a process-wide facility, so two would fight
        // over the audio session and the Now Playing surface.
        //
        // 🔴 REGISTERED BY ITS OWN TYPE, NOT AS IMediaPlayer. The default IMediaPlayer stays the PAGE-BACKED
        // MediaPlayer (D58); a shell claiming IMediaPlayer moves audio out of the page's element, the page's
        // PLAYER_REPORT lands on a native player with no Report to take, and `useMediaPlayer(ref)` does not
        // fail — it quietly stops working.
        //
        //     var player = services.GetRequiredService<PlatformMediaPlayer>();   // opt in by name
        builder.Services.TryAddSingleton(_ => new PlatformMediaPlayer());
#endif

        return builder;
    }
}
