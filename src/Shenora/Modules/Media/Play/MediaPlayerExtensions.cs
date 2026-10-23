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

/// <summary>Composing <see cref="IMediaPlayer"/> with the rest of the shell — in the ROOT <c>Shenora</c>
/// namespace, beside <see cref="ShenoraApplicationBuilder"/> and <see cref="ShenoraApplication"/>.</summary>
public static class MediaPlayerExtensions
{
    /// <summary>
    /// Register the page-backed media player and the IPC module that receives the page's reports. With no
    /// configuration, sources pass straight through; set
    /// <see cref="MediaAccessOptions.AllowedRoots"/> to opt into probing, planning and conversion.
    /// <code>
    /// builder.UseMediaPlayer();                                        // pass sources through
    /// builder.UseMediaPlayer(x => x.Access = new MediaAccessOptions    // …or repair what the webview refuses
    /// {
    ///     Resolve = static _ => null,
    ///     AllowedRoots = [library],
    ///     CacheRoot = "",                                              // "" = default under Paths.DataArea
    /// });
    /// </code>
    /// <para>
    /// ⚠ <b>This CONFIGURES the provider; mounting its route is a second phase.</b> With
    /// <see cref="MediaAccessOptions.AllowedRoots"/> set, call
    /// <see cref="UseMediaPlayer(IWebViewInterceptor, IServiceProvider)"/> once the webview exists.
    /// </para>
    /// Design and rationale: <c>docs/design/media.md</c>; D58, D65, D73.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. Set <see cref="MediaAccessOptions.AllowedRoots"/> (via <see cref="MediaPlayerOptions.Access"/>) to enable conversion.</param>
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
    ///     x.Access = new MediaAccessOptions { Resolve = static _ => null, AllowedRoots = [libraryDir], CacheRoot = "" };
    ///     services.AddSingleton&lt;IMediaPlayer&gt;(sp => sp.GetRequiredService&lt;WindowsMediaPlayer&gt;());
    /// });
    /// </code>
    /// <para>
    /// Your registration wins: the callback runs FIRST, so the kit's default factory is never built. The
    /// sanctioned way to substitute the NATIVE player (D58).
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

        // The feature registers its own IPC module (D65). ⚠ `TryAddEnumerable` needs the TWO-TYPE overload:
        // given a factory whose implementation type IS the service type it has nothing to compare for
        // duplicates and THROWS "indistinguishable from other services registered for IIpcModule".
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, MediaPlayerModule>(sp =>
            new MediaPlayerModule(
                sp.GetRequiredService<IMediaPlayer>(),
                sp.GetRequiredService<MediaPlayerOptions>(),
                sp.GetService<ILogger<MediaPlayerModule>>(),
                // Optional by design: a shell with no picture surface is the ordinary desktop case, where
                // the page's own element already IS the picture.
                sp.GetService<IMediaSurface>())));

        builder.Services.TryAddSingleton<IMediaPlayer>(services =>
        {
            // ⚠ Resolved from DI, never the captured instance: `TryAddSingleton` no-ops when the app
            // registered its own options, and defaulting onto an object nothing reads is a silent no-op.
            var resolved = services.GetRequiredService<MediaPlayerOptions>();
            DefaultCacheRoot(resolved, paths);
            return new MediaPlayer(services.GetRequiredService<IEventBus>(), resolved);
        });

        return builder;
    }

    /// <summary>
    /// Give <see cref="MediaAccessOptions.CacheRoot"/> its default — <c>Paths.DataArea("media")</c> — when
    /// the app named <see cref="MediaAccessOptions.AllowedRoots"/> and left it blank. Deferred out of
    /// registration because <c>Paths.DataArea</c> CREATES the directory it names (D64).
    /// <para>
    /// 🔴 <b>Called from BOTH phases, and it must be:</b> the mount hands <c>CacheRoot</c> to
    /// <see cref="MediaConversionExtensions.UseMediaConversion"/>, which rejects a blank one. Replaces the
    /// whole <see cref="MediaAccessOptions"/> (every member is <c>init</c>-only); idempotent by the blank test.
    /// </para>
    /// </summary>
    private static void DefaultCacheRoot(MediaPlayerOptions options, ShenoraPaths paths)
    {
        if (options.Access.AllowedRoots.Count == 0 || !string.IsNullOrWhiteSpace(options.Access.CacheRoot))
        {
            return;
        }

        options.Access = new MediaAccessOptions
        {
            Resolve = options.Access.Resolve,
            AllowedRoots = options.Access.AllowedRoots,
            CacheRoot = paths.DataArea("media"),
            Module = options.Access.Module,
            Log = options.Access.Log,
        };
    }

    /// <summary>
    /// **Mount the player's route on every webview the app hosts** — the <c>app.Use*()</c> phase (D64),
    /// and the call an adopter should write:
    /// <code>
    /// using var app = builder.Build();
    /// app.UseMediaPlayer();      // no `services` argument: the app already holds the provider
    /// app.Run();
    /// </code>
    /// </summary>
    /// <returns>The app, so calls chain.</returns>
    public static ShenoraApplication UseMediaPlayer(this ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(interceptor => interceptor.UseMediaPlayer(app.Services));
    }

    /// <summary>
    /// **Mount the player's route on the webview.** The second half of the standard two-phase shape:
    /// configure the provider at builder time, mount it on the pipeline when the pipeline exists.
    /// <para>
    /// **A no-op returning <c>null</c> when <see cref="MediaAccessOptions.AllowedRoots"/> is empty**, so it
    /// is safe to call unconditionally.
    /// </para>
    /// </summary>
    /// <param name="interceptor">The shell's interceptor, once the webview exists.</param>
    /// <param name="services">The built provider — the same one <c>UseMediaPlayer</c> registered into.</param>
    /// <returns>Dispose to remove the route. <c>null</c> when no conversion was configured.</returns>
    public static IDisposable? UseMediaPlayer(this IWebViewInterceptor interceptor, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<MediaPlayerOptions>();
        if (options.Access.AllowedRoots.Count == 0) return null;

        // 🔴 Defaulted here too, not only in the player's factory — see `DefaultCacheRoot`.
        DefaultCacheRoot(options, services.GetRequiredService<ShenoraPaths>());

        // ONE convention for both ends, so the encoder and the decoder cannot drift.
        options.ResolveUri ??= (source, plan) =>
            plan is null || plan.Action == MediaPlaybackAction.Direct
                ? source
                : MediaPlayerRoute.Build(source, plan.Action);
        return interceptor.UseMediaConversion(
            services.GetRequiredService<IMissionScheduler>(),
            services.GetRequiredService<IEventBus>(),
            new MediaConversionOptions
            {
                ResolveAction = uri => MediaPlayerRoute.ActionOf(uri.PathAndQuery),
                // BOTH seams resolved from DI, so registering one is enough to have it used (D59).
                Convert = (services.GetService<IMediaContainerWriter>() ?? new Mp4Remuxer())
                    .ToConverter(services.GetService<IMediaStreamConversion>()),
                // ⚠ A FRESH `MediaAccessOptions`: this route reads URLs `MediaPlayerRoute` built for its
                // OWN convention, so `options.Access.Resolve` is INERT here.
                Access = new MediaAccessOptions
                {
                    Resolve = uri => MediaPlayerRoute.SourceOf(uri.PathAndQuery),
                    AllowedRoots = options.Access.AllowedRoots,
                    CacheRoot = options.Access.CacheRoot,
                    Module = options.Access.Module,
                    Log = options.Access.Log,
                },
            });
    }

    /// <summary>
    /// Keep the OS transport surface telling the truth: report the PLAYER's own state to
    /// <paramref name="session"/> whenever <see cref="IMediaPlayer.StateChanged"/> fires.
    /// <para>
    /// ⚠ <b>It calls <see cref="IPlaybackSession.Report"/> and never
    /// <see cref="IPlaybackSession.Publish"/>.</b> <c>Publish</c> takes a WHOLE
    /// <see cref="PlaybackInfo"/>, so publishing what a player knows would blank the title, subtitle and
    /// artwork the app had already set. Metadata stays the app's to publish, <b>including
    /// <see cref="PlaybackInfo.Duration"/></b>; this carries state and position only.
    /// </para>
    /// </summary>
    /// <param name="player">The player to follow.</param>
    /// <param name="session">The transport surface to keep in step.</param>
    /// <returns>
    /// A handle that stops the reporting. Dispose it when the pairing ends — both objects are singletons,
    /// so a subscription nobody drops keeps writing to the lock screen.
    /// </returns>
    public static IDisposable ReportTo(this IMediaPlayer player, IPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(session);

        void OnChanged(MediaPlayerStatus status)
        {
            // Empty means the source is gone — Clear(), not Stopped, which leaves a resumable item up.
            if (status.State == MediaPlayerState.Empty)
            {
                session.Clear();
                return;
            }

            session.Report(new PlaybackProgress
            {
                State = ToPlaybackState(status.State),
                Position = status.Position,
                // The app's real speed even when paused; every shell derives the PUBLISHED speed from the
                // state (see PlaybackProgress).
                Rate = status.Rate,
            });
        }

        player.StateChanged += OnChanged;
        return new Unsubscriber(() => player.StateChanged -= OnChanged);
    }

    /// <summary>
    /// Do what the OS's transport controls ask — the other half of <see cref="ReportTo"/>. Play, pause and their
    /// toggle, seek and the two skips go to <paramref name="player"/>; play on a finished item starts it again from
    /// the beginning; <see cref="PlaybackCommand.Stop"/> pauses,
    /// because closing would throw the source away. <see cref="PlaybackCommand.Next"/> and
    /// <see cref="PlaybackCommand.Previous"/> belong to a queue, which a player does not have, and are left to the app.
    /// <para>
    /// ⚠ <b>One player per session at a time.</b> Two obeying the same session both act on every command — a toggle
    /// then plays one and pauses the other. Dispose one pairing before making the next.
    /// </para>
    /// </summary>
    /// <param name="player">The player to drive.</param>
    /// <param name="session">The transport surface whose commands it takes.</param>
    /// <param name="log">Where a command the player refused is reported. Commands arrive on the platform's
    /// thread, so nothing is rethrown into it.</param>
    /// <returns>A handle that stops obeying. Dispose it when the pairing ends.</returns>
    public static IDisposable ObeyCommandsFrom(this IMediaPlayer player, IPlaybackSession session, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(session);

        void OnCommand(PlaybackCommandRequest request) => _ = ObeyAsync(player, session, request, log);

        session.CommandReceived += OnCommand;
        return new Unsubscriber(() => session.CommandReceived -= OnCommand);
    }

    private static async Task ObeyAsync(IMediaPlayer player, IPlaybackSession session, PlaybackCommandRequest request,
                                        ILogger? log)
    {
        try
        {
            var status = player.Status;
            var skip = request.Interval ?? session.SkipInterval;
            // Play on a FINISHED item starts it again, as a lock screen's play does everywhere else. Measured on an
            // iPhone without it: the player went to Playing at its end and played nothing.
            if (status.State == MediaPlayerState.Ended
                && request.Command is PlaybackCommand.Play or PlaybackCommand.TogglePlayPause)
            {
                await player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
                await player.PlayAsync().ConfigureAwait(false);
                return;
            }

            switch (request.Command)
            {
                case PlaybackCommand.Play:
                    await player.PlayAsync().ConfigureAwait(false);
                    break;
                case PlaybackCommand.Pause or PlaybackCommand.Stop:
                    await player.PauseAsync().ConfigureAwait(false);
                    break;
                case PlaybackCommand.TogglePlayPause when status.State is MediaPlayerState.Playing or MediaPlayerState.Buffering:
                    await player.PauseAsync().ConfigureAwait(false);
                    break;
                case PlaybackCommand.TogglePlayPause:
                    await player.PlayAsync().ConfigureAwait(false);
                    break;
                case PlaybackCommand.Seek when request.Position is { } at:
                    await player.SeekAsync(at).ConfigureAwait(false);
                    break;
                case PlaybackCommand.SkipForward:
                    await player.SeekAsync(status.Position + skip).ConfigureAwait(false);
                    break;
                case PlaybackCommand.SkipBackward:
                    await player.SeekAsync(status.Position > skip ? status.Position - skip : TimeSpan.Zero).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => $"[Shenora.Modules.Media] the player refused the {request.Command} command.",
                LogLevel.Warning, ex);
        }
    }

    /// <summary>One <see cref="ReportTo"/>-shaped report of where <paramref name="player"/> is NOW — for a session
    /// moving to it, which would otherwise show the last player's state until this one's next transition. Nothing for
    /// an empty player: that would clear what the app published.</summary>
    internal static void ReportNow(IMediaPlayer player, IPlaybackSession session)
    {
        var status = player.Status;
        if (status.State == MediaPlayerState.Empty) return;
        session.Report(Progress(status));
        // A transition between the read and the report reached the session first, through ReportTo, and this older
        // report would then stand until the next one — so the newer state is said again.
        var now = player.Status;
        if (now.State != status.State && now.State != MediaPlayerState.Empty) session.Report(Progress(now));

        static PlaybackProgress Progress(MediaPlayerStatus s) =>
            new() { State = ToPlaybackState(s.State), Position = s.Position, Rate = s.Rate };
    }

    /// <summary>
    /// The player's state in the vocabulary the OS renders. ⚠ <see cref="MediaPlayerState.Ended"/> and
    /// <see cref="MediaPlayerState.Failed"/> both become <see cref="PlaybackState.Stopped"/> — a transport
    /// surface has no "it broke" state to render, so telling the user WHY stays the app's job.
    /// </summary>
    private static PlaybackState ToPlaybackState(MediaPlayerState state) => state switch
    {
        MediaPlayerState.Playing => PlaybackState.Playing,
        MediaPlayerState.Paused => PlaybackState.Paused,
        MediaPlayerState.Opening or MediaPlayerState.Buffering => PlaybackState.Buffering,
        // Ended, Failed, Empty (handled by the caller) and anything a later version adds.
        _ => PlaybackState.Stopped,
    };

    private sealed class Unsubscriber(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        // Idempotent: disposing twice must not detach a handler a LATER ReportTo re-attached.
        public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
    }
}
