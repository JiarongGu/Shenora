namespace Shenora.Mobile;

/// <summary>
/// Answers the one lifecycle question a mobile window teardown cannot answer from MAUI alone: is this
/// destruction a RECREATION (the platform rebuilding the same window for a configuration change) or a
/// real shutdown?
/// <para>
/// The distinction is load-bearing for the <c>Window.Destroying → ShenoraApplication.Stop()</c>
/// wiring: on Android, a configuration change the manifest does not declare (font scale, locale —
/// MAUI's template covers orientation and theme, not these) destroys and recreates the window
/// mid-session. Treating that as shutdown cancels every in-flight request — measured on a device: a
/// save whose picker was open completed as <c>OPERATION_CANCELLED</c> with the user's chosen file
/// created and left empty. Skip the stop when this answers true; <c>Start</c> is idempotent, so the
/// recreated window's <c>Created</c> costs nothing.
/// </para>
/// <para>
/// iOS has no equivalent — a scene teardown there is a real teardown — so this answers false, and
/// the wiring stays one line on both platforms.
/// </para>
/// </summary>
public static class MobileWindowLifecycle
{
    /// <summary>True while the current activity is being destroyed only to be recreated.</summary>
    public static bool IsRecreating =>
#if ANDROID
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.IsChangingConfigurations == true;
#else
        false;
#endif
}
