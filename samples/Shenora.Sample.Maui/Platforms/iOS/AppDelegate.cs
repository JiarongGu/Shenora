using Foundation;

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
}
