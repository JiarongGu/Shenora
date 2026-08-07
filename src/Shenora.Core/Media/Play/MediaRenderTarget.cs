namespace Shenora.Media;

/// <summary>
/// Where a player's output actually lands — the display and the sound. A <c>&lt;video&gt;</c> or
/// <c>&lt;audio&gt;</c> element in the page is the one the kit is built around; anything that can be told
/// to load a URL and report back qualifies.
/// <para>
/// <b>This is the seam that makes the webview a DEVICE rather than a separate architecture.</b> Before it,
/// the kit had two disconnected media stories: <c>Serve/</c> handed bytes to an element the page drove
/// itself, and <c>Play/</c> was a native player. They shared a namespace and nothing else. With this, both
/// are the same thing — a lifecycle owned in .NET, and an output surface — and the page's element is
/// simply the surface the framework knows best.
/// </para>
/// <para>
/// <b>⚠ A target RENDERS; it does not decide.</b> It is told which URL to load, when to play, where to
/// seek. What to load — whether a source plays as-is, needs a container repair, or cannot play at all — is
/// <see cref="MediaPlayer"/>'s job, because that decision needs a probe, a policy and a device
/// capability query, and none of those belong in a display. A target that starts choosing formats has
/// become a player.
/// </para>
/// <para>
/// Every method is async because the canonical implementation crosses an IPC boundary into a page. A
/// target that is local and synchronous returns completed tasks and loses nothing.
/// </para>
/// </summary>
public interface IMediaRenderTarget
{
    /// <summary>
    /// Point the surface at a URL and get it ready, without playing.
    /// <para>
    /// ⚠ Completing this task means the REQUEST reached the surface, not that the media is ready — a page
    /// element loads asynchronously and says so through <see cref="Reported"/>. Readiness is a
    /// <see cref="MediaPlayerState.Paused"/> report; failure is <see cref="MediaPlayerState.Failed"/>.
    /// </para>
    /// </summary>
    /// <param name="uri">What to load. Already resolved — this is the URL that will actually be fetched.</param>
    /// <param name="startAt">Where to begin. <see cref="TimeSpan.Zero"/> for the start.</param>
    /// <param name="cancellationToken">Abandons the request.</param>
    Task LoadAsync(string uri, TimeSpan startAt, CancellationToken cancellationToken = default);

    /// <summary>Start or resume output.</summary>
    Task PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Hold at the current position.</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Move to an absolute position.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Set the playback speed multiplier.</summary>
    Task SetRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release the source. The surface goes back to having nothing loaded — for a page element, that is
    /// clearing <c>src</c> and calling <c>load()</c>, which is what actually frees the buffer.
    /// </summary>
    Task UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The surface saying what it is doing — the only source of truth for position, because the element is
    /// the thing that is actually advancing.
    /// <para>
    /// ⚠ <b>Report on TRANSITIONS, not on a timer.</b> A page element fires <c>timeupdate</c> about four
    /// times a second, and forwarding every one across IPC is the mistake this contract exists to avoid:
    /// it costs battery and bandwidth to tell the host something it can extrapolate. Forward
    /// <c>loadedmetadata</c>, <c>canplay</c>, <c>play</c>, <c>pause</c>, <c>waiting</c>, <c>seeked</c>,
    /// <c>ended</c> and <c>error</c>. That is all.
    /// </para>
    /// <para>
    /// ⚠ Raised on whatever thread the transport delivers on. A throwing handler must not escape into it.
    /// </para>
    /// </summary>
    event Action<MediaPlayerStatus>? Reported;
}
