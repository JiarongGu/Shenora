using Shenora.Core.Shell;

namespace Shenora.Mobile;

/// <summary>
/// Holds the app's window at an orientation, through the platform's own control rather than the page's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Android</b> holds it outright: <c>Activity.RequestedOrientation</c> is a real lock the platform
/// enforces for as long as it is set.
/// </para>
/// <para>
/// 🔴 <b>iOS NEEDS ONE LINE FROM THE APP, and cannot be made to work without it.</b>
/// <c>requestGeometryUpdate</c> rotates the window, but UIKit then INTERSECTS that with what the app
/// says it supports — so the next device rotation undoes a rotation nothing is backing. The app's answer
/// comes from its own <c>UIApplicationDelegate</c>, which a library cannot override:
/// <code>
/// public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations(
///     UIApplication application, UIWindow? forWindow)
///     =&gt; MobileWindowOrientation.SupportedInterfaceOrientations;
/// </code>
/// <b>That override IS the opt-in</b> — <see cref="IsSupported"/> goes true once UIKit has asked, so an
/// app that has not wired it advertises the capability as absent rather than accepting a lock it cannot
/// hold (D39/D36). ⚠ An earlier version of this type refused iOS outright and said so here; that was
/// honest but it cost an adopter their portrait lock, because they deleted a working implementation to
/// take a capability that is absent on one of their two shells.
/// </para>
/// <para>
/// ⚠ <b><c>Info.plist</c> is still the ceiling.</b> UIKit intersects this mask with
/// <c>UISupportedInterfaceOrientations</c>, so an orientation missing there can never be locked TO — and
/// <see cref="Unlock"/> hands the decision back to that list rather than to everything.
/// </para>
/// </remarks>
public sealed class MobileWindowOrientation : IWindowOrientation
{
#if IOS
    /// <summary>
    /// What the app's delegate must return, and the flag that proves it does.
    /// <para>
    /// 🔴 <b>READING IT IS THE SIGNAL.</b> There is no way to ask UIKit whether an app overrode its
    /// delegate, and a bool the app sets separately can disagree with what the delegate actually returns —
    /// two declarations of one fact. This cannot: the only reason to read this property is to return it.
    /// </para>
    /// </summary>
    private static volatile bool _consulted;

    /// <summary>The mask currently in force — <see cref="UIKit.UIInterfaceOrientationMask.All"/> until
    /// something locks, which is what hands the decision to <c>Info.plist</c>.</summary>
    private static UIKit.UIInterfaceOrientationMask _mask = UIKit.UIInterfaceOrientationMask.All;

    /// <summary>
    /// The orientations this app supports right now. <b>Return this from your
    /// <c>UIApplicationDelegate.GetSupportedInterfaceOrientations</c></b> — see the type's remarks for the
    /// one line, and note that returning it is also what turns <see cref="IsSupported"/> on.
    /// </summary>
    public static UIKit.UIInterfaceOrientationMask SupportedInterfaceOrientations
    {
        get { _consulted = true; return _mask; }
    }
#endif

    /// <summary>
    /// True when this shell can actually hold an orientation — the honest answer to advertise as
    /// <see cref="ShellCapability.WindowOrientation"/>.
    /// <para>
    /// ⚠ <b>On iOS this is FALSE until UIKit has asked the app what it supports</b>, which is how the kit
    /// knows the delegate hook exists. UIKit asks while the window is being set up, long before a page
    /// loads — but a page that reads this absurdly early would see <c>false</c>, and absent is the safe
    /// direction: the page keeps its own behaviour instead of calling a lock nothing can hold.
    /// </para>
    /// </summary>
    public static bool IsSupported =>
#if ANDROID
        true;
#elif IOS
        _consulted;
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
#elif IOS
        // ⚠ The FAMILY, matching Android's Sensor* choice: a phone held the other way up should follow its
        // user rather than sit 180° from them. `Info.plist` is the ceiling, so an app that does not list
        // upside-down simply never gets it and this costs nothing.
        Apply(orientation == Core.Shell.WindowOrientation.Portrait
            ? UIKit.UIInterfaceOrientationMask.Portrait | UIKit.UIInterfaceOrientationMask.PortraitUpsideDown
            : UIKit.UIInterfaceOrientationMask.Landscape);
#else
        _ = orientation;
        throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
            "this shell cannot hold an orientation.");
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
#elif IOS
        // `All` is not "every orientation" here — UIKit intersects it with `Info.plist`, so this hands the
        // decision back to the app's own declared set, which is the peer of Android's `Unspecified`.
        Apply(UIKit.UIInterfaceOrientationMask.All);
#else
        throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
            "this shell cannot hold an orientation.");
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

#if IOS
    /// <summary>
    /// Publish the new mask, tell UIKit the answer changed, and ask the scene to rotate.
    /// <para>
    /// 🔴 <b>ALL THREE, and the first two are what an attempt built on
    /// <c>requestGeometryUpdate</c> alone is missing.</b> The mask is what the app's delegate returns, so
    /// setting it is the lock; <c>SetNeedsUpdateOfSupportedInterfaceOrientations</c> is what makes UIKit
    /// re-ask instead of keeping its cached answer; and only then does the geometry request have anything
    /// backing it. Rotate without the first two and the next device rotation undoes it.
    /// </para>
    /// </summary>
    private static void Apply(UIKit.UIInterfaceOrientationMask mask)
    {
        _mask = mask;

        if (!_consulted)
        {
            // 🔴 NAMED, never silent. The mask is set either way — if the app wires its delegate later,
            // this lock is already in force — but a page told "locked" while nothing can hold it is the
            // exact failure this capability exists to avoid, and it is invisible from the glass.
            throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, MauiShellNames.Shell,
                "this app's UIApplicationDelegate does not return "
                + "MobileWindowOrientation.SupportedInterfaceOrientations, so iOS has never been told what "
                + "the app supports and any rotation would be undone by the next one. Override "
                + "GetSupportedInterfaceOrientations to return it — one line, and it is what turns "
                + "IsSupported on.");
        }

        // ⚠ Read at CALL time for the same reason Android reads its activity here: the key window changes,
        // and a captured one would be dead the first time it mattered.
        var window = UIKit.UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIKit.UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(candidate => candidate.IsKeyWindow);

        // 🔴 THE LOCK IS ALREADY IN FORCE ABOVE — everything below only makes it take effect NOW rather
        // than at the next rotation, and both calls are iOS 16+ while this package supports 15.0. So an
        // iOS 15 device gets the lock and no immediate turn, which is weaker but not wrong, and is the
        // reason this is a version check rather than a raised minimum.
        if (!OperatingSystem.IsIOSVersionAtLeast(16)) return;

        if (window?.RootViewController is { } root)
        {
            // Makes UIKit ask again. Without it the cached mask stands and the rotation below is refused.
            root.SetNeedsUpdateOfSupportedInterfaceOrientations();
        }

        if (window?.WindowScene is { } windowScene)
        {
            // ⚠ The error callback is REQUIRED reading, not optional: a refused geometry request is
            // reported here and nowhere else, so swallowing it is how "the lock did nothing" becomes
            // undiagnosable. It is not thrown — this runs after the mask is already in force, and the
            // system may legitimately refuse the immediate rotation while still honouring the lock.
            windowScene.RequestGeometryUpdate(
                new UIKit.UIWindowSceneGeometryPreferencesIOS(mask),
                error => System.Diagnostics.Debug.WriteLine(
                    $"[Shenora] the window scene refused the orientation request: {error.LocalizedDescription}"));
        }
    }
#endif
}
