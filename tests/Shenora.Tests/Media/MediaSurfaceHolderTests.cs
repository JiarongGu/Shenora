using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// <see cref="MediaSurfaceHolder"/> — the handle/player rendezvous, and the reason it is a separate type.
/// <para>
/// 🔴 <b>THIS FILE IS THE POINT OF THE EXTRACTION.</b> The logic used to live inside
/// <c>MediaSurfaceView</c>, which compiles only for the android/ios TFMs while this suite is
/// <c>net10.0</c> — so nothing in the gate could construct it, and the handle-arrives-first case shipped
/// broken while the XML claimed <i>"whichever comes second completes the pair"</i>. A device found it.
/// </para>
/// <para>
/// ⚠ <b>Every case here is one whose only symptom is a BLACK RECTANGLE.</b> The player still opens the
/// file, still reports a moving clock and still decodes — into nowhere. So these assert which handle each
/// player was given and in what order, never merely that nothing threw.
/// </para>
/// </summary>
public class MediaSurfaceHolderTests
{
    /// <summary>
    /// Records what it was handed, because the ORDER of attach/detach is the whole contract. Everything
    /// below <see cref="AttachSurfaceCore"/> is the minimum <see cref="MediaPlayerBase"/> demands — this
    /// player never opens anything.
    /// </summary>
    private sealed class RecordingPlayer : MediaPlayerBase
    {
        public List<object?> Surfaces { get; } = [];

        protected override void AttachSurfaceCore(object? surface) => Surfaces.Add(surface);

        protected override TimeSpan PositionCore => TimeSpan.Zero;
        protected override TimeSpan? DurationCore => null;
        protected override void OpenCore(MediaSource source, Uri uri) { }
        protected override void ApplyStartAt(TimeSpan position) { }
        protected override void PlayCore(double rate) { }
        protected override void PauseCore() { }
        protected override Task SeekCore(TimeSpan position) => Task.CompletedTask;
        protected override void ApplyRateCore(double rate) { }
        protected override void TeardownCore() { }
    }

    [Fact]
    public void A_handle_that_arrives_BEFORE_the_player_is_kept_and_handed_over()
    {
        // 🔴 THE DEFECT THAT REACHED A DEVICE. The platform realizes its surface when the layout does,
        // which on both shells can precede the page's own load — so this ordering is not exotic.
        var holder = new MediaSurfaceHolder();
        var handle = new object();
        var player = new RecordingPlayer();

        holder.SetHandle(handle);      // the platform got there first
        holder.Player = player;        // the app wires up afterwards

        Assert.Equal([handle], player.Surfaces);
    }

    [Fact]
    public void A_handle_that_arrives_AFTER_the_player_reaches_it_too()
    {
        // The other ordering, which is the one that always worked — kept so a fix to the case above
        // cannot quietly break the case below it.
        var holder = new MediaSurfaceHolder();
        var handle = new object();
        var player = new RecordingPlayer();

        holder.Player = player;
        holder.SetHandle(handle);

        Assert.Equal([handle], player.Surfaces);
    }

    [Fact]
    public void Replacing_the_player_DETACHES_the_outgoing_one_before_the_incoming_one_draws()
    {
        // ⚠ The ORDER is the assertion, not the end state: two players holding one surface is a torn
        // picture, so the outgoing must be released before the incoming is given anything.
        var holder = new MediaSurfaceHolder();
        var handle = new object();
        var first = new RecordingPlayer();
        var second = new RecordingPlayer();

        holder.Player = first;
        holder.SetHandle(handle);
        holder.Player = second;

        Assert.Equal([handle, null], first.Surfaces);
        Assert.Equal([handle], second.Surfaces);
    }

    [Fact]
    public void Setting_the_SAME_player_again_does_nothing()
    {
        // A caller re-running its wiring on every layout pass must not tear down a live picture — the
        // detach/re-attach would be invisible in the end state and a flicker on the glass.
        var holder = new MediaSurfaceHolder();
        var handle = new object();
        var player = new RecordingPlayer();

        holder.Player = player;
        holder.SetHandle(handle);
        holder.Player = player;

        Assert.Equal([handle], player.Surfaces);
    }

    [Fact]
    public void Clearing_the_player_releases_the_surface()
    {
        var holder = new MediaSurfaceHolder();
        var player = new RecordingPlayer();

        holder.Player = player;
        holder.SetHandle(new object());
        holder.Player = null;

        Assert.Equal(null, player.Surfaces[^1]);
    }

    [Fact]
    public void A_destroyed_surface_is_taken_away_from_the_player_and_FORGOTTEN()
    {
        // 🔴 BOTH HALVES. Detaching without forgetting leaves a dead handle to be re-offered to the NEXT
        // player, which is the released-buffer case — a native crash on some Android devices rather than
        // a blank view.
        var holder = new MediaSurfaceHolder();
        var first = new RecordingPlayer();
        var dead = new object();

        holder.Player = first;
        holder.SetHandle(dead);
        holder.SetHandle(null);        // the platform is tearing the surface down

        Assert.Equal([dead, null], first.Surfaces);
        Assert.Null(holder.Handle);

        var second = new RecordingPlayer();
        holder.Player = second;
        Assert.Empty(second.Surfaces);
    }

    [Fact]
    public void With_no_player_a_handle_is_remembered_rather_than_dropped()
    {
        // The state the first test depends on, asserted directly so a failure there says WHICH half broke.
        var holder = new MediaSurfaceHolder();
        var handle = new object();

        holder.SetHandle(handle);

        Assert.Same(handle, holder.Handle);
    }
}
