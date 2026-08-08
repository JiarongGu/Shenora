using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Modules.Platform;
using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine.Missions;
using Shenora.Core.Ipc;

namespace Shenora;

/// <summary>
/// Composing <see cref="IMediaPlayer"/> with the rest of the shell.
/// <para>
/// 🔴 <b>In the ROOT <c>Shenora</c> namespace on purpose, with the types it composes left where they
/// live.</b> An extension belongs with the type it EXTENDS — .NET's own rule, which is why
/// <c>IServiceCollection</c> extensions ship in <c>Microsoft.Extensions.DependencyInjection</c> rather
/// than in each library's namespace. These extend <see cref="ShenoraApplicationBuilder"/> and
/// <see cref="ShenoraApplication"/>, so an app that already wrote <c>using Shenora;</c> to name the
/// builder gets <c>UseMediaPlayer</c> with no second import. (Owner, 2026-08-08. The friction was real:
/// the desktop sample had to add <c>using Shenora.Modules.Media;</c> solely to call
/// <c>app.UseMediaPlayer()</c>.)
/// </para>
/// </summary>
public static class MediaPlayerExtensions
{
    /// <summary>
    /// Register the kit's media player.
    /// <code>
    /// builder.UseMediaPlayer();                                  // pass sources straight through
    /// builder.UseMediaPlayer(x => x.AllowedRoots = [library]);   // …and repair what the webview refuses
    /// </code>
    /// <para>
    /// <b>It registers the WHOLE host half — the player and the IPC module that receives the page's
    /// reports.</b> The only thing left is <c>useMediaPlayer(ref)</c> in <c>@shenora/react</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>It used to be one of three pieces, and the third was yours.</b> Until 2026-08-07 nothing
    /// answered <c>PLAYER_REPORT</c>, so <see cref="IMediaPlayer.OpenAsync"/> — which completes on the
    /// page's first report and on nothing else — waited forever with no exception and no log line. The
    /// route now ships as <see cref="MediaPlayerModule"/>, registered here BY THE FEATURE rather than
    /// by the dispatcher, so a core never has to know a feature's name (D65). Delete any route you wrote
    /// against an earlier build; two facades on one module is a duplicate the dispatcher rejects.
    /// </para>
    /// <para>
    /// <b>The zero-argument call is the point, not a convenience.</b> Owner, 2026-08-07: *"unless we need a
    /// custom decoder, we dont need to have this complex play logic."* A file the device can already decode
    /// needs no probe, no plan and no URL rewriting, so with no configuration this wires a player that
    /// passes sources straight through — and the probe/plan/convert machinery stays out of the way until
    /// something asks for it.
    /// </para>
    /// <para>
    /// 🔴 **Where the two overloads split is a SECURITY line, which is why it is also the right ergonomic
    /// one.** Everything else can be defaulted — the cache root from
    /// <see cref="ShenoraApplicationBuilder.Paths"/>, the module name, the converter from whatever
    /// <see cref="IMediaAudioConversion"/> the shell registered. <see cref="MediaPlayerOptions.AllowedRoots"/>
    /// cannot: it is the containment boundary, and a kit that chose one would be making a data-access
    /// decision for the app. So conversion is exactly what you opt into by configuring.
    /// </para>
    /// <para>
    /// <b>It binds the PAGE-BACKED <see cref="MediaPlayer"/>, never the native one, and that is deliberate.</b>
    /// A hybrid framework rendering through the page is the normal case (D58), and binding
    /// a shell's native player here would silently move the audio out of the page's own element — so a
    /// React UI bound to that element would show nothing playing. **Background playback is opt-in**: it
    /// needs the app's own <c>AVAudioSession</c> and <c>UIBackgroundModes</c> anyway, so an app that wants
    /// it resolves the shell's player explicitly.
    /// </para>
    /// <para>
    /// ⚠ **This CONFIGURES the provider; mounting its route is the second phase.** With
    /// <see cref="MediaPlayerOptions.AllowedRoots"/> set, call
    /// <see cref="UseMediaPlayer(IWebViewInterceptor, IServiceProvider)"/> once the webview exists — the
    /// interceptor is created with the webview, so it cannot exist here. That is the same split ASP.NET
    /// draws between registering a service and mounting its middleware, and the kit's other route
    /// providers (<c>UseFiles</c>, <c>UseMediaConversion</c>) already read this way.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. Set <see cref="MediaPlayerOptions.AllowedRoots"/> to enable conversion.</param>
    public static ShenoraApplicationBuilder UseMediaPlayer(
        this ShenoraApplicationBuilder builder,
        Action<MediaPlayerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseMediaPlayer((options, _) => configure?.Invoke(options));
    }

    /// <summary>
    /// Configure the player AND substitute its collaborators, in one place — most usefully the
    /// <see cref="IMediaPlayer"/> itself:
    /// <code>
    /// builder.UseMediaPlayer((x, services) =>
    /// {
    ///     x.AllowedRoots = [libraryDir];
    ///     services.AddSingleton&lt;IMediaPlayer&gt;(sp => sp.GetRequiredService&lt;WindowsMediaPlayer&gt;());
    /// });
    /// </code>
    /// <para>
    /// 🔴 <b>The guarantee is that YOUR registration wins — not the ordering, which was measured and turned
    /// out not to be load-bearing.</b> Microsoft DI resolves the LAST descriptor for a service, and
    /// everything the kit adds is <c>TryAdd</c>, so an app's registration wins whether it lands before the
    /// kit's (which then no-ops) or after it (which then loses the resolve). What the callback removes is
    /// having to KNOW any of that.
    /// </para>
    /// <para>
    /// The callback still runs FIRST, and that buys the other half: exactly ONE registration exists, so
    /// <c>GetServices&lt;IMediaPlayer&gt;()</c> returns your player alone and the kit's default factory is
    /// never even built. Registering afterwards would leave the kit's default SHADOWED but present, which
    /// anything enumerating the service would still see.
    /// </para>
    /// <para>
    /// ⚠ <b>Substituting the player is the sanctioned way to get the NATIVE one, and it is opt-in for a
    /// reason.</b> The default binds the page-backed <see cref="MediaPlayer"/> deliberately (D58): a
    /// shell's native player moves the audio out of the page's own element, so a React UI bound to that
    /// element would show nothing playing. An app that wants background playback needs its own
    /// <c>AVAudioSession</c>/<c>UIBackgroundModes</c> anyway, so it is already making that decision.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Receives the options and the container, before the kit registers anything.</param>
    public static ShenoraApplicationBuilder UseMediaPlayer(
        this ShenoraApplicationBuilder builder,
        Action<MediaPlayerOptions, IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MediaPlayerOptions();
        var paths = builder.Paths;
        configure(options, builder.Services);

        builder.Services.TryAddSingleton(options);

        // 🔴 THE FEATURE REGISTERS ITS OWN IPC MODULE (D65). The route that carries the page's
        // `PLAYER_REPORT` back is part of this feature, not something the dispatcher should know the name
        // of — a core that enumerated its features would have to be edited every time one was added.
        // ⚠ `TryAddEnumerable` with the TWO-TYPE overload: given a factory whose implementation type is
        // the SERVICE type it throws "indistinguishable from other services registered for IIpcModule",
        // because it has nothing to compare for duplicates. Naming the implementation is what makes
        // registering twice a no-op rather than a duplicate module the dispatcher rejects.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, MediaPlayerModule>(sp =>
            new MediaPlayerModule(
                sp.GetRequiredService<IMediaPlayer>(),
                sp.GetRequiredService<MediaPlayerOptions>(),
                sp.GetService<ILogger<MediaPlayerModule>>())));

        builder.Services.TryAddSingleton<IMediaPlayer>(services =>
        {
            // ⚠ Resolved from DI, never the captured instance: `TryAddSingleton` no-ops when the app
            // registered its own options, and defaulting onto an object nothing reads is a silent no-op.
            var resolved = services.GetRequiredService<MediaPlayerOptions>();
            // Defaulted here rather than at `Use…` time so REGISTRATION touches no disk (D64) — the whole
            // engine can then be on by default. `Paths.DataArea` CREATES the directory it names, and only
            // an app that actually named `AllowedRoots` has anything to put in it.
            if (resolved.AllowedRoots.Count > 0 && string.IsNullOrWhiteSpace(resolved.CacheRoot))
            {
                resolved.CacheRoot = paths.DataArea("media");
            }
            return new MediaPlayer(services.GetRequiredService<IEventBus>(), resolved);
        });

        return builder;
    }

    /// <summary>
    /// **Mount the player's route on the webview.** The second half of the standard two-phase shape:
    /// configure the provider at builder time, mount it on the pipeline when the pipeline exists.
    /// <code>
    /// builder.UseMediaPlayer(x => x.AllowedRoots = [library]);   // configure the provider
    /// …
    /// interceptor.UseMediaPlayer(services);                      // mount it
    /// </code>
    /// <para>
    /// <b>Two calls because there are genuinely two phases, not because the API gave up.</b> The
    /// interceptor is created WITH the webview, so it cannot exist while services are being registered —
    /// the same reason ASP.NET separates service registration from <c>UseStaticFiles</c>. This sits beside
    /// <c>UseFiles</c>, <c>UseMediaConversion</c> and <c>UseSegmentStream</c> and reads like all of them:
    /// a route provider being mounted.
    /// </para>
    /// <para>
    /// **A no-op returning <c>null</c> when <see cref="MediaPlayerOptions.AllowedRoots"/> is empty**, so it
    /// is safe to call unconditionally — an app that never turns conversion on pays one dictionary lookup.
    /// </para>
    /// <para>
    /// It uses the kit's defaults throughout — <see cref="MediaContainerWriterExtensions.ToConverter"/> fed by whatever
    /// <see cref="IMediaAudioConversion"/> the shell registered (D59), the cache root
    /// <c>UseMediaPlayer</c> chose, and a URL convention the player and the route agree on **because both
    /// read the same options object**, so the emitter and the matcher cannot drift apart.
    /// </para>
    /// </summary>
    /// <summary>
    /// **Mount the player's route on every webview the app hosts** — the <c>app.Use*()</c> phase (D64),
    /// and the call an adopter should write:
    /// <code>
    /// using var app = builder.Build();
    /// app.UseMediaPlayer();      // no `services` argument: the app already holds the provider
    /// app.Run();
    /// </code>
    /// <para>
    /// 🔴 <b>This is what the two-phase shape below was apologising for.</b> The second phase was
    /// <c>interceptor.UseMediaPlayer(services)</c> — the caller fetching an inner object and handing the
    /// provider BACK in. The PHASES were always right (an interceptor is created with its webview); the
    /// receiver was not. ASP.NET's second phase is <c>app.Use*()</c>, where the app holds the provider,
    /// and now so is this. The per-interceptor overload stays for one webview that must differ.
    /// </para>
    /// </summary>
    /// <returns>The app, so calls chain.</returns>
    public static ShenoraApplication UseMediaPlayer(this ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(interceptor => interceptor.UseMediaPlayer(app.Services));
    }

    /// <param name="interceptor">The shell's interceptor, once the webview exists.</param>
    /// <param name="services">The built provider — the same one <c>UseMediaPlayer</c> registered into.</param>
    /// <returns>Dispose to remove the route. <c>null</c> when no conversion was configured.</returns>
    public static IDisposable? UseMediaPlayer(this IWebViewInterceptor interceptor, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<MediaPlayerOptions>();
        if (options.AllowedRoots.Count == 0) return null;

        // ONE convention, defined once in MediaPlayerRoute and used from both ends here — the encoder and
        // the decoder cannot drift because they are the same code, and it has a round-trip test.
        options.ResolveUri ??= (source, plan) =>
            plan is null || plan.Action == MediaPlaybackAction.Direct
                ? source
                : MediaPlayerRoute.Build(source, plan.Action);
        return interceptor.UseMediaConversion(
            services.GetRequiredService<IMissionScheduler>(),
            services.GetRequiredService<IEventBus>(),
            new MediaConversionOptions
            {
                // Both ends of the convention come from MediaPlayerRoute, so the encoder above and these
                // decoders are literally the same code — see its remarks for why that matters.
                Resolve = uri => MediaPlayerRoute.SourceOf(uri.PathAndQuery),
                ResolveAction = uri => MediaPlayerRoute.ActionOf(uri.PathAndQuery),
                // BOTH seams resolved from DI, so registering one is enough to have it used — the rule
                // D59 and the lock-inspector defect were both about. A consumer's native muxer replaces
                // only the muxing stage; their codec replaces only the codec. Reads as what it is now that
                // wrapping a writer lives on the INTERFACE: take a muxer, make it a converter.
                Convert = (services.GetService<IMediaContainerWriter>() ?? new Mp4Remuxer())
                    .ToConverter(services.GetService<IMediaAudioConversion>()),
                CacheRoot = options.CacheRoot!,
                AllowedRoots = options.AllowedRoots,
                Module = options.Module,
            });
    }

    /// <summary>
    /// Keep the OS transport surface telling the truth: report the PLAYER's own state to
    /// <paramref name="session"/> whenever it changes.
    /// <para>
    /// <b>This closes a gap D54 names explicitly.</b> Before the host owned a player,
    /// <see cref="IPlaybackSession"/> published whatever the app claimed — so a lock screen could say
    /// "playing" while the audio had stalled, ended or failed, and nothing reconciled the two. When the
    /// host does own the player, what it reports is what is actually happening, and this is the one line
    /// that makes that so.
    /// </para>
    /// <para>
    /// <b>⚠ It calls <see cref="IPlaybackSession.Report"/> and never
    /// <see cref="IPlaybackSession.Publish"/>, deliberately.</b> A player knows a position, a rate and a
    /// duration; it does not know a title, a subtitle or artwork. <c>Publish</c> takes a WHOLE
    /// <see cref="PlaybackInfo"/>, so a bridge that published what it knows would blank the metadata the
    /// app had already set — the exact trap <c>IosPlaybackSession</c> documents for partial updates.
    /// Metadata stays the app's to publish; this carries state and position only.
    /// </para>
    /// <para>
    /// It follows that <b>the app should still publish a <see cref="PlaybackInfo.Duration"/></b>: the
    /// player learns one on open, but sending it from here would mean sending a whole info record.
    /// </para>
    /// <para>
    /// ⚠ <b>Raised on the platform's thread, not the UI thread</b> — see
    /// <see cref="IMediaPlayer.StateChanged"/>. That is fine for this: every
    /// <see cref="IPlaybackSession"/> implementation is safe to call from any thread, and each marshals
    /// internally where its platform demands it. An app doing more in its own handler must marshal itself.
    /// </para>
    /// </summary>
    /// <param name="player">The player to follow.</param>
    /// <param name="session">The transport surface to keep in step.</param>
    /// <returns>
    /// A handle that stops the reporting. Dispose it when the pairing ends — both objects are singletons
    /// that outlive any one screen, so a subscription nobody drops is a subscription that keeps writing to
    /// the lock screen after the feature using it has gone.
    /// </returns>
    public static IDisposable ReportTo(this IMediaPlayer player, IPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(session);

        void OnChanged(MediaPlayerStatus status)
        {
            // Empty means the source is gone, which is what Clear() means — as distinct from reporting
            // Stopped, which leaves the app on the lock screen with a resumable item.
            if (status.State == MediaPlayerState.Empty)
            {
                session.Clear();
                return;
            }

            session.Report(new PlaybackProgress
            {
                State = ToPlaybackState(status.State),
                Position = status.Position,
                // Passed through as the app's real speed even when paused: PlaybackProgress documents that
                // every shell derives the PUBLISHED speed from the state and ignores this otherwise.
                Rate = status.Rate,
            });
        }

        player.StateChanged += OnChanged;
        return new Unsubscriber(() => player.StateChanged -= OnChanged);
    }

    /// <summary>
    /// The player's state in the vocabulary the OS renders.
    /// <para>
    /// ⚠ <b><see cref="MediaPlayerState.Ended"/> and <see cref="MediaPlayerState.Failed"/> both become
    /// <see cref="PlaybackState.Stopped"/>, and that is not information being lost.</b> A transport
    /// surface has no "it broke" state to render — the four states in <see cref="PlaybackState"/> are what
    /// every platform can draw. Telling the user WHY something stopped is the app's job, in the app's own
    /// UI, where there is room to say it.
    /// </para>
    /// <para>
    /// <see cref="MediaPlayerState.Opening"/> maps to <see cref="PlaybackState.Buffering"/> for the reason
    /// that state exists: the OS should show a spinner rather than a stale elapsed time, and opening is
    /// exactly a wait with no position to advance.
    /// </para>
    /// </summary>
    private static PlaybackState ToPlaybackState(MediaPlayerState state) => state switch
    {
        MediaPlayerState.Playing => PlaybackState.Playing,
        MediaPlayerState.Paused => PlaybackState.Paused,
        MediaPlayerState.Opening or MediaPlayerState.Buffering => PlaybackState.Buffering,
        // Ended, Failed, Empty (handled by the caller) and anything a later version adds. Stopped is the
        // safe default: it is the state that claims the least.
        _ => PlaybackState.Stopped,
    };

    private sealed class Unsubscriber(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        // Idempotent: disposing twice must not detach a handler a LATER ReportTo re-attached, which is
        // what a naive implementation does when a caller disposes in both a finally and a Dispose.
        public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
    }
}
