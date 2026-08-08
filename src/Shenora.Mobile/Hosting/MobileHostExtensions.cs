using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Modules.Platform;
using Shenora.Modules.FileDialog;
using Shenora.Modules.Media;
using Shenora.Core.Shell;

// 🔴 THE ONE PLATFORM-SPECIFIC THING IN THIS FILE, deliberately gathered into a single block.
//
// This file is SHARED source: it compiles into both Shenora.Android and Shenora.iOS, because MAUI hosting
// is genuinely the same on each — that is what `Shenora.Mobile` is FOR (owner, 2026-08-08). The
// implementations it registers are NOT the same, and they no longer pretend to be: each lives in its own
// shell project under `Platforms/<Platform>/`, in its own namespace, named for its platform, exactly as
// `WindowsMediaPlayer` is.
//
// Aliasing them here keeps every registration below platform-agnostic and readable. The alternative —
// an `#if` around each `TryAddSingleton` — would put six conditionals through the middle of the
// composition and hide what is actually one substitution.
#if ANDROID

using PlatformFileDialogs = Shenora.Android.AndroidFileDialogs;
using PlatformLiveActivities = Shenora.Android.AndroidLiveActivities;
using PlatformMediaAudioConversion = Shenora.Android.AndroidMediaAudioConversion;
using PlatformMediaCapability = Shenora.Android.AndroidMediaCapability;
using PlatformMediaPlayer = Shenora.Android.AndroidMediaPlayer;
using PlatformPlaybackSession = Shenora.Android.AndroidPlaybackSession;
#elif IOS || MACCATALYST

using PlatformFileDialogs = Shenora.iOS.IosFileDialogs;
using PlatformLiveActivities = Shenora.iOS.IosLiveActivities;
using PlatformMediaAudioConversion = Shenora.iOS.IosMediaAudioConversion;
using PlatformMediaCapability = Shenora.iOS.IosMediaCapability;
using PlatformMediaPlayer = Shenora.iOS.IosMediaPlayer;
using PlatformPlaybackSession = Shenora.iOS.IosPlaybackSession;
#endif

namespace Shenora.Mobile;

/// <summary>Registers the MAUI shell services on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class MobileHostExtensions
{
    /// <summary>
    /// 🔴 <b>Named for the PLATFORM, not the category — <c>UseAndroid</c> on Android and
    /// <c>UseIOS</c> on iOS, over one shared body (D65).</b> D37 made the package set one-per-platform
    /// and the ENTRY POINTS never followed: this was <c>UseMobile</c>, a category name serving two
    /// packages that ship, build and are consumed separately. A platform is the one thing an adopter
    /// genuinely picks, so it is the one call that earns a name.
    /// <para>
    /// ⚠ <b>It is the ONE place the two mobile surfaces deliberately differ</b>, which costs the trick
    /// that let the Android API baseline gate iOS from a Windows host. Accepted rather than worked
    /// around (owner, 2026-08-07: the build story is changing anyway) — revisit the arrangement with
    /// the build toolkit rather than engineering around today's packaging.
    /// </para>
    /// <para>
    /// Make this a MAUI-hosted application: registers the shell contracts this platform can honour
    /// (<see cref="IClipboardService"/>, <see cref="IUrlLauncher"/>, <see cref="IUiInteraction"/>,
    /// <see cref="IFileDialogs"/>, <see cref="IUiDispatcher"/>), each with <c>TryAdd</c> so an app
    /// registration wins.
    /// </para>
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
#if ANDROID
    public static ShenoraApplicationBuilder UseAndroid(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
#elif IOS || MACCATALYST
    public static ShenoraApplicationBuilder UseIOS(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
#else
    // A hard COMPILE error rather than a category-named fallback: this source compiles into
    // Shenora.Android and Shenora.iOS and nothing else, so a third target reaching here means someone
    // added a platform without naming its entry point — and a category-named shim (which is what this
    // used to be) would let that ship looking deliberate. Same fail-closed choice as
    // PlatformPlaybackSession's guard a few lines down.
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
        // The page's ROUTE to them, registered where the platform implementation is (D64). ⚠ Two of the
        // four routes are DESKTOP capabilities and refuse here with CapabilityNotSupported (D35) — which
        // is why the facade ships on this shell at all rather than being withheld: the page asks the
        // handshake what this shell can honour and renders accordingly (D36), and a refusal is a real
        // answer where an absent module would just look broken.
        builder.Services.UseShenoraFileDialogs();

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
#error Shenora.Mobile: this platform has no PlatformPlaybackSession. Add one under Services/, or register a stub that throws ShellCapability.NotSupported.
#endif
        builder.Services.TryAddSingleton<IPlaybackSession>(_ => new PlatformPlaybackSession());

        // The live status surface. Registered on BOTH shells even though only iOS can do it, because the
        // contract carries its own "cannot" channel (`Unavailable`) — so portable logic asks and branches
        // instead of catching, and Android answers with a reason rather than failing at the injection site
        // with a message about a missing service.
        builder.Services.TryAddSingleton<ILiveActivities>(_ => new PlatformLiveActivities());

        // What THIS DEVICE can decode and encode. Registered on both shells because the answer differs
        // between them AND between devices of the same platform — Android codec support is vendor-declared,
        // which is why MediaCodecList is a runtime query. Before this, an app filling MediaPlaybackPolicy
        // had to GUESS those sets; the kit still ships no codec list, it ships the question (D42).
        //
        // Singleton because both implementations cache: the Android walk allocates a Java object per codec
        // and the iOS one builds a converter per candidate, and neither answer can change while the process
        // runs.
        builder.Services.TryAddSingleton<Shenora.Modules.Media.IMediaCapability>(_ => new PlatformMediaCapability());

#if ANDROID || IOS || MACCATALYST
        // The transcode tier — the soundtrack half of D59's device→webview gap. Registered on BOTH mobile
        // shells since 2026-08-07: Android chains a MediaCodec decoder → AAC encoder, iOS chains two
        // AudioConverters through PCM. Windows has none yet and says so by absence.
        //
        // ⚠ Deliberately WITHOUT the #error guard the playback session uses: that guard means "every shell
        // MUST answer this", and this contract is genuinely optional — Mp4Remuxer takes it as a nullable,
        // and a shell without one gets container repair plus a REPORTED drop (MediaRemuxerResult.Dropped),
        // which is honest rather than silent.
        //
        // NOT a singleton: each Begin() holds two real codec instances, and a device has only a handful.
        // Sharing the FACTORY is fine; sharing a run would not be.
        builder.Services.TryAddSingleton<Shenora.Modules.Media.IMediaAudioConversion>(_ =>
        {
            // The PIPELINE is registered, with this platform is converter already in it. An app adds its
            // own with pipeline.Use(...) and keeps this one behind it, rather than replacing the lot.
            var pipeline = new Shenora.Modules.Media.MediaAudioPipeline();
            PlatformMediaAudioConversion.Use(pipeline);
            return pipeline;
        });
#endif

#if IOS || MACCATALYST || ANDROID
        // The HOST-OWNED PLAYER (D54). BOTH mobile shells now, and the type name is the same on each —
        // AVPlayer behind it on iOS, android.media.MediaPlayer on Android, and MediaPlayerBase holding the
        // state machine they share.
        //
        // iOS came FIRST because that is where the gap is provable rather than argued — the system pauses a
        // backgrounded <video> outright, and AVPlayer keeps going. Android's gap is narrower but real: the
        // platform decodes a superset of what the webview does (PlatformMediaCapability reports which), and
        // playback outlives the page. Windows landed the same week (WindowsMediaPlayer).
        //
        // Singleton to match IPlaybackSession, and for the same reason: it is a handle on a process-wide
        // facility, so two of them would fight over the audio session and the Now Playing surface.
        //
        // 🔴 BY ITS OWN TYPE, NOT AS IMediaPlayer (owner, 2026-08-08) — a BREAKING change from the original
        // registration, and the rule is now the same on every shell. The default IMediaPlayer is the
        // PAGE-BACKED MediaPlayer because rendering through the page is the normal case (D58); a shell that
        // claimed IMediaPlayer moved the audio out of the page's element, and the page's PLAYER_REPORT then
        // landed on a native player with no Report to take — MediaPlayerModule short-circuits, so
        // `useMediaPlayer(ref)` did not fail, it quietly stopped working. That is the silent degradation
        // this kit treats as the worse outcome, and it also made MediaPlayerExtensions' own documentation
        // false on this shell.
        //
        //     var player = services.GetRequiredService<PlatformMediaPlayer>();   // opt in by name
        builder.Services.TryAddSingleton(_ => new PlatformMediaPlayer());
#endif

        return builder;
    }
}
