using Shenora.Modules.Platform;
using Shenora.Core.Shell;
using Shenora.Modules.Platform.Activities;

using Shenora;

namespace Shenora.Android;

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
        // ⚠ Both are accepted and ignored, which is honest rather than lazy: this platform starts nothing,
        // so there is no surface for a look or an arrangement to describe. They exist so portable app
        // logic compiles unchanged against every shell — the same reason `Unavailable` says WHY.
        _ = appearance;
        _ = presentation;
        // Null is the contract's "could not start", and Unavailable above says why. Silence would be the
        // one wrong answer.
        return null;
    }

    /// <inheritdoc />
    public void Update(string handle, LiveActivityState state) { }

    /// <inheritdoc />
    public void End(string handle) { }

    /// <inheritdoc />
    /// <remarks>
    /// Always null, and for the ordinary reason rather than a missing implementation: this shell starts no
    /// surface, so there is nothing for a server to advance. ⚠ Not a <c>NotSupported</c> refusal — the
    /// contract's "cannot" channel is <see cref="Unavailable"/>, which already says why, and throwing from a
    /// getter an app polls would turn a documented absence into an error it cannot act on.
    /// </remarks>
    public string? PushToken(string handle) => null;
}
