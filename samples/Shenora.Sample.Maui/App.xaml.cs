namespace Shenora.Sample.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage());

		// THE LIFECYCLE PAIR, driven by the platform — the shape UseHeadless deliberately does not
		// cover. MAUI owns the loop, so ShenoraApplication.Run (contractually "blocks until
		// shutdown") has no honest implementation here; Start/Stop do.
		//
		// MEASURED, not assumed: Window.Created fires ONCE per process here. MAUI's Window is
		// process-scoped and MainActivity declares ConfigurationChanges for orientation/UI-mode, so a
		// theme switch recreates nothing, and a home-and-return did not re-enter this either (the
		// activity was instrumented to check — MainActivity.OnCreate logged #1 only). Start's
		// idempotency is therefore insurance for hosts that wire it somewhere activity-scoped, not a
		// fix for this wiring.
		window.Created += (_, _) =>
		{
			MauiProgram.Log("window created -> ShenoraApplication.Start()");
			MauiProgram.Shenora?.Start();
		};
		window.Destroying += (_, _) =>
		{
			MauiProgram.Log("window destroying -> ShenoraApplication.Stop()");
			MauiProgram.Shenora?.Stop();
		};

		return window;
	}
}
