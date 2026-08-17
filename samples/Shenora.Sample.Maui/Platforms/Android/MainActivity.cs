using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Shenora.Sample.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private static int _creations;

	/// <summary>
	/// Instrumented ONLY to tell two things apart that look identical from the outside: "the activity
	/// was recreated and MAUI's Window survived it" versus "the activity was never recreated at all".
	/// Without this the idempotency of <c>ShenoraApplication.Start</c> cannot be said to be PROVEN on
	/// device — it can only be assumed, which is the failure mode this repo keeps paying for.
	/// </summary>
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		MauiProgram.Log($"MainActivity.OnCreate #{++_creations} (savedState: {savedInstanceState is not null})");
		base.OnCreate(savedInstanceState);
	}

	/// <summary>
	/// The one line of app wiring the kit's file dialogs need on Android (docs/ADOPTION.md): the relay
	/// owns its request codes and the FRAMEWORK routes results here — the only channel measured to
	/// survive activity recreation, because a MAUI activity does not round-trip the AndroidX instance
	/// state the registry mechanism depends on.
	/// </summary>
	protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
	{
		Shenora.Android.ActivityResultRelay.Deliver(requestCode, (int)resultCode, data);
		base.OnActivityResult(requestCode, resultCode, data);
	}
}
