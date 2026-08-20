using Shenora.Core.Shell;
using Shenora.Modules.Platform.Activities;

namespace Shenora.Modules.Platform;

/// <summary>
/// What a live status surface should currently say — the OS-rendered strip an app puts a long-running job on
/// (iOS calls it a Live Activity and shows it in the Dynamic Island and on the lock screen).
/// <para>
/// ⚠ <b>The fields are FIXED:</b> a Swift struct the kit ships mirrors this one field for field, and
/// widening one side alone fails SILENTLY — the surface does not appear, or shows stale values. A tripwire
/// test keeps the mirror honest.
/// </para>
/// </summary>
public sealed record LiveActivityState
{
    /// <summary>The primary line — what is happening. "Converting", "Downloading", "Exporting".</summary>
    public string? Title { get; init; }

    /// <summary>The secondary line — what it is happening to, or how far along in words.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Completion from 0 to 1, or null for work whose length is unknown. Null means INDETERMINATE and the
    /// view renders a spinner rather than an empty bar — <c>0.0</c> cannot say which.
    /// </summary>
    public double? Progress { get; init; }
}

/// <summary>
/// How the surface should LOOK, described once when it starts and read at runtime by the generic widget
/// the kit ships, so no Swift is written (D69). An app supplying its own SwiftUI views
/// (<c>ShenoraLiveActivityViews</c>) ignores this.
///
/// <para>
/// ⚠ <b>APPEARANCE IS FIXED FOR THE ACTIVITY'S LIFETIME.</b> ActivityKit splits an activity into static
/// <c>ActivityAttributes</c> and a dynamic <c>ContentState</c>; this record crosses as attributes, so it
/// cannot change after <see cref="ILiveActivities.Start"/>. What changes belongs in
/// <see cref="LiveActivityState"/>.
/// </para>
/// </summary>
public sealed record LiveActivityAppearance
{
    /// <summary>
    /// An SF Symbol name (<c>arrow.down.circle.fill</c>, <c>waveform</c>). Filled symbols read on the
    /// Island's compact region; outlines do not. ⚠ A name the minimum OS does not have renders NOTHING.
    /// </summary>
    public string Symbol { get; init; } = "circle.fill";

    /// <summary>A <c>#RRGGBB</c> accent for the symbol and progress bar. Null uses the system accent.</summary>
    public string? Tint { get; init; }
}

/// <summary>
/// Starting, updating and ending a live status surface — the OS strip that shows a long-running job while the
/// app is in the background.
/// <para>
/// ⚠ <b>Implemented on iOS only today.</b> A shell that cannot honour it refuses by name through
/// <see cref="ShellCapability"/> rather than silently doing nothing — ask <see cref="Unavailable"/> first.
/// </para>
/// </summary>
public interface ILiveActivities
{
    /// <summary>
    /// Why this device cannot host one right now — the OS version, or the user having switched them off in
    /// Settings. Null when they are available.
    /// </summary>
    string? Unavailable { get; }

    /// <summary>
    /// Begin a surface and return its handle, or null when one could not be started (see
    /// <see cref="Unavailable"/> first — that is the answer most of the time). The handle is opaque and
    /// only meaningful to <see cref="Update"/> and <see cref="End"/>.
    /// </summary>
    /// <param name="state">What it should say right now.</param>
    /// <param name="appearance">
    /// How it should look, read by the kit's generic widget. Null takes the defaults.
    /// </param>
    /// <param name="presentation">
    /// WHAT to draw, as an element tree the widget interprets at runtime. Null keeps the kit's built-in
    /// arrangement, and so does any surface left unset — so restyling the Island's pill does not mean
    /// restating the lock-screen card.
    /// </param>
    string? Start(LiveActivityState state,
                  LiveActivityAppearance? appearance = null,
                  Presentation? presentation = null);

    /// <summary>
    /// Replace what the surface says. ⚠ The WHOLE state, not a delta — the platform takes a complete
    /// content object, so a partial update blanks the fields it omits.
    /// </summary>
    void Update(string handle, LiveActivityState state);

    /// <summary>Take the surface down. A handle that is already ended, or was never valid, is ignored.</summary>
    void End(string handle);

    /// <summary>
    /// The push token for this surface, so a SERVER can advance it while the app is not running. Null when
    /// the platform has no such mechanism, when one has not been issued yet, or when the handle is unknown.
    /// The APNs connection, the payload and the server that sends it stay the app's.
    ///
    /// <para>
    /// 🔴 <b>A limit that is easy to mistake for a bug:</b> <see cref="Update"/> runs IN THE APP'S PROCESS,
    /// so an activity swiped away with the app freezes at its last value — the card outlives the app, the
    /// update loop does not. A push is the only way to advance it from there.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>It is not available immediately.</b> The system issues the token asynchronously after the
    /// activity starts, so the first call right after <see cref="Start"/> normally answers null — poll, or
    /// read it when you are about to register the activity with your server.
    /// </para>
    /// </summary>
    string? PushToken(string handle);
}
