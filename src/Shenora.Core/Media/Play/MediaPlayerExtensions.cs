using Shenora.Core;

namespace Shenora.Media;

/// <summary>
/// Composing <see cref="IMediaPlayer"/> with the rest of the shell.
/// </summary>
public static class MediaPlayerExtensions
{
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
    /// app had already set — the exact trap <c>MobilePlaybackSession</c> documents for partial updates.
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
