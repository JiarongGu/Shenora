using Shenora.Core.Shell;

namespace Shenora.Mobile;

/// <summary>
/// Holds the app's window at an orientation, through the platform's own control rather than the page's.
/// </summary>
/// <remarks>
/// 🔴 <b>ANDROID ONLY, and iOS REFUSES rather than pretending.</b> Android's
/// <c>Activity.RequestedOrientation</c> is a real lock the platform enforces for as long as it is set.
/// iOS has no equivalent an app-level service can honour: <c>requestGeometryUpdate</c> ROTATES the window
/// but the root view controller still reports the orientations it supports, so the next device rotation
/// undoes it — a request, not a lock. Shipping that behind the same method would be the D39 trap, an API
/// that compiles on both shells and is materially weaker on one. A page learns which shell it is on from
/// <see cref="ShellCapability.WindowOrientation"/>, never by sniffing the platform.
/// </remarks>
public sealed class MobileWindowOrientation : IWindowOrientation
{
    /// <summary>
    /// True when this shell can actually hold an orientation — the honest answer to advertise as
    /// <see cref="ShellCapability.WindowOrientation"/>.
    /// </summary>
    public static bool IsSupported =>
#if ANDROID
        true;
#else
        false;
#endif

    /// <inheritdoc />
    public void Lock(WindowOrientation orientation)
    {
#if ANDROID
        // ⚠ The FAMILY, not an edge: `Portrait` pins one way up, so a phone held upside down stays
        // rotated 180° from the user. `SensorPortrait`/`SensorLandscape` keep the axis and let the
        // platform pick the end, which is what "hold it portrait" actually means to a user.
        Apply(orientation == Core.Shell.WindowOrientation.Portrait
            ? global::Android.Content.PM.ScreenOrientation.SensorPortrait
            : global::Android.Content.PM.ScreenOrientation.SensorLandscape);
#else
        _ = orientation;
        throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
            "iOS cannot hold an orientation from app code — declare the app's supported orientations in "
            + "Info.plist, or take fullscreen in the page and use screen.orientation.lock() there.");
#endif
    }

    /// <inheritdoc />
    public void Unlock()
    {
#if ANDROID
        // `Unspecified` returns the decision to the system, which is not the same as `FullSensor`: the
        // latter overrides the user's own rotation lock, so an app that "unlocked" would start rotating
        // for a user who had asked their device not to.
        Apply(global::Android.Content.PM.ScreenOrientation.Unspecified);
#else
        throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
            "iOS cannot hold an orientation from app code — see Lock.");
#endif
    }

#if ANDROID
    private static void Apply(global::Android.Content.PM.ScreenOrientation requested)
    {
        // ⚠ Read the activity at CALL time. It is replaced by every configuration change, and this is
        // exactly the API a rotation-related change produces — a captured one would be dead the first
        // time it mattered.
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity)
        {
            throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
                "there is no current activity yet — call this from a page that is on screen.");
        }

        // The property is the platform's own lock: it survives until something sets it again, which is
        // why Unlock has to exist rather than being a scope.
        activity.RequestedOrientation = requested;
    }
#endif
}
