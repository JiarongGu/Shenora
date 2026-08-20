using Shenora.Modules.Platform;
using Shenora.Core.Shell;
using Shenora.Modules.Platform.Activities;

using Shenora;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="ILiveActivities"/>: an honest "not here" rather than no registration at all, so
/// portable app logic can read <see cref="ILiveActivities.Unavailable"/> and take the other branch — not
/// catch a missing-service throw at the injection site, nor a
/// <see cref="ShellCapability.NotSupported"/> from a surface it only wanted to report progress on.
/// <para>
/// Not a placeholder: Android's analogue is a foreground-service progress notification, whose icon,
/// channel and importance are app design decisions the kit does not take (D13/D15). The media case is
/// already covered by <see cref="IPlaybackSession"/>.
/// </para>
/// </summary>
public sealed class AndroidLiveActivities : ILiveActivities
{
    /// <inheritdoc />
    public string? Unavailable =>
        "Android has no live status surface in this kit yet. The media case is covered by IPlaybackSession; "
        + "for other long-running work, post your own foreground-service notification.";

    /// <inheritdoc />
    public string? Start(LiveActivityState state,
                         LiveActivityAppearance? appearance = null,
                         Presentation? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Accepted and ignored: this platform starts no surface for them to describe. They are here so
        // portable app logic compiles unchanged against every shell.
        _ = appearance;
        _ = presentation;
        // Null is the contract's "could not start"; `Unavailable` says why.
        return null;
    }

    /// <inheritdoc />
    public void Update(string handle, LiveActivityState state) { }

    /// <inheritdoc />
    public void End(string handle) { }

    /// <inheritdoc />
    /// <remarks>Always null: this shell starts no surface, so there is nothing for a server to advance.</remarks>
    public string? PushToken(string handle) => null;
}
