using Foundation;
using Shenora.Mobile;
using UIKit;

namespace Shenora.Sample.Maui;

/// <summary>
/// The iOS entry object, and the peer of <c>Platforms/Android/MainApplication</c>: both do nothing
/// but hand MAUI the app that <see cref="MauiProgram.CreateMauiApp"/> composed. That the two heads
/// are this thin is the point — the shell, the IPC bridge and the whole of
/// <c>Shenora.Sample.Logic</c> are shared, and only the platform's own bootstrap differs.
/// </summary>
[Register(nameof(AppDelegate))]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	/// <summary>
	/// The one line iOS orientation cannot work without, and the reason this head is no longer quite
	/// empty.
	/// <para>
	/// 🔴 <b>A library cannot override this</b> — UIKit asks the APP's delegate what it supports, and
	/// intersects the answer with every rotation. Without it <c>requestGeometryUpdate</c> turns the window
	/// and the next device rotation turns it straight back, which is a request rather than a lock.
	/// Returning the kit's mask is also what makes <c>MobileWindowOrientation.IsSupported</c> true here,
	/// so an app that omits it advertises the capability as ABSENT rather than accepting a lock nothing
	/// holds.
	/// </para>
	/// <para>
	/// ⚠ <c>Info.plist</c> remains the ceiling: this mask is intersected with
	/// <c>UISupportedInterfaceOrientations</c>, so an orientation missing there can never be locked to.
	/// </para>
	/// <para>
	/// ⚠ Exported, not overridden: <c>MauiUIApplicationDelegate</c> implements the delegate PROTOCOL, so there is no
	/// virtual method to override, and an <c>override</c> does not compile.
	/// </para>
	/// </summary>
	[Export("application:supportedInterfaceOrientationsForWindow:")]
	public UIInterfaceOrientationMask GetSupportedInterfaceOrientations(UIApplication application, UIWindow? forWindow) =>
		MobileWindowOrientation.SupportedInterfaceOrientations;
}
