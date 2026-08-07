#if ANDROID
using Shenora;

namespace Shenora.Mobile;

/// <summary>
/// Android's <see cref="ILiveActivities"/>: an honest "not here", not a silent no-op.
/// <para>
/// <b>Why a real class rather than no registration at all.</b> With nothing registered, resolving
/// <see cref="ILiveActivities"/> on Android throws at the INJECTION site with a message about a missing
/// service — which tells the reader nothing about which shell, or that the answer is "this platform has no
/// such surface". The contract already has a first-class channel for exactly this
/// (<see cref="ILiveActivities.Unavailable"/>), so portable app logic can ask before it tries and take the
/// other branch. That is better than <see cref="ShellCapability.NotSupported"/> here: an app reporting
/// progress on a background job should not have to catch an exception to find out it cannot.
/// </para>
/// <para>
/// ⚠ <b>Not a placeholder awaiting the obvious port.</b> Android's analogue — a foreground-service progress
/// notification — is deliberately unbuilt (D15): for the media case it is already covered by
/// <see cref="IPlaybackSession"/>, and posting a notification means choosing an icon, a channel and an
/// importance, which are app design decisions the kit does not take (D13). It arrives when a real consumer
/// needs a NON-media progress surface here.
/// </para>
/// </summary>
public sealed class MobileLiveActivities : ILiveActivities
{
    /// <inheritdoc />
    public string? Unavailable =>
        "Android has no live status surface in this kit yet. The media case is covered by IPlaybackSession; "
        + "for other long-running work, post your own foreground-service notification.";

    /// <inheritdoc />
    public string? Start(LiveActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Null is the contract's "could not start", and Unavailable above says why. Silence would be the
        // one wrong answer.
        return null;
    }

    /// <inheritdoc />
    public void Update(string handle, LiveActivityState state) { }

    /// <inheritdoc />
    public void End(string handle) { }
}
#endif
