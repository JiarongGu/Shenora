namespace Shenora.Core;

/// <summary>
/// What a live status surface should currently say — the OS-rendered strip an app puts a long-running job on
/// (iOS calls it a Live Activity and shows it in the Dynamic Island and on the lock screen).
/// <para>
/// <b>The fields are FIXED and deliberately few.</b> This shape is mirrored, field for field, by a Swift
/// struct the kit ships — because the two sides have to agree exactly and when they do not the failure is
/// SILENT: the surface simply does not appear, or shows stale values. A tripwire test keeps the mirror
/// honest, which is the same protection the C#⇄TS IPC wire already has. An app that needs a field this does
/// not have should say so rather than working around it; widening the record and its Swift twin together is
/// a small change, and widening one alone is the bug.
/// </para>
/// </summary>
public sealed record LiveActivityState
{
    /// <summary>The primary line — what is happening. "Converting", "Downloading", "Exporting".</summary>
    public string? Title { get; init; }

    /// <summary>The secondary line — what it is happening to, or how far along in words.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Completion from 0 to 1, or null for work whose length is unknown.
    /// <para>
    /// Null means INDETERMINATE and the view is expected to render a spinner rather than an empty bar —
    /// which is a real distinction, because a download with no content-length and a download that has just
    /// started are different things and 0.0 cannot say which.
    /// </para>
    /// </summary>
    public double? Progress { get; init; }
}

/// <summary>
/// Starting, updating and ending a live status surface — the OS strip that shows a long-running job while the
/// app is in the background.
/// <para>
/// ⚠ <b>Implemented on iOS only, today, and that is a deliberate stopping point rather than an oversight.</b>
/// The obvious portable analogue — an Android foreground-service progress notification — is already covered
/// for the media case by <see cref="IPlaybackSession"/>, and building a second implementation before a real
/// consumer needs it is what D15 exists to prevent. The contract lives here in <c>Shenora.Core</c> anyway so
/// app logic compiles against it with no platform reference (D19/D20); a shell that cannot honour it refuses
/// by name through <see cref="ShellCapability"/> rather than silently doing nothing.
/// </para>
/// <para>
/// <b>The app supplies the VIEWS, in Swift, and nothing else.</b> A Live Activity's UI is a SwiftUI view in a
/// widget extension — an OS requirement, not a .NET limitation — and it is the app's design system anyway,
/// which the kit does not ship (D13). Everything around it is the kit's: this contract, the state mirror, the
/// C-callable lifecycle shim, and the build step that compiles the extension. See
/// <c>.claude/knowledge/mobile-shells.md</c> for the measured mechanics.
/// </para>
/// </summary>
public interface ILiveActivities
{
    /// <summary>
    /// Whether this device can host one right now, and why not when it cannot — the OS version, or the user
    /// having switched them off in Settings, which are different answers an app may want to report
    /// differently. Null when they are available.
    /// </summary>
    string? Unavailable { get; }

    /// <summary>
    /// Begin a surface and return its handle, or null when one could not be started (see
    /// <see cref="Unavailable"/> first — that is the answer most of the time).
    /// <para>
    /// The handle is opaque and only meaningful to <see cref="Update"/> and <see cref="End"/>. It is a
    /// string because the platform's own identifier is one, and inventing a wrapper would just hide it.
    /// </para>
    /// </summary>
    string? Start(LiveActivityState state);

    /// <summary>
    /// Replace what the surface says. The WHOLE state, not a delta — the platform takes a complete content
    /// object, so a partial update would blank the fields it omitted.
    /// <para>
    /// The app holds its own last state and uses a record <c>with</c> expression, which is why this takes no
    /// mutation callback: <c>Update(h, state = state with { Progress = 0.6 })</c> reads better than any
    /// closure API and keeps the kit from having to remember state on the app's behalf.
    /// </para>
    /// </summary>
    void Update(string handle, LiveActivityState state);

    /// <summary>Take the surface down. A handle that is already ended, or was never valid, is ignored.</summary>
    void End(string handle);
}
