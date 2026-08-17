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
		// Window.Created fires once per process for the config changes the MANIFEST declares
		// (orientation, theme) — but a change outside that list (font scale, locale) recreates the
		// window mid-session, measured on a device 2026-08-17. That is why Stop asks IsRecreating:
		// treating a recreation as shutdown cancelled every in-flight request, and a save whose
		// picker was open came back OPERATION_CANCELLED with the chosen file created and left empty.
		// Start's idempotency is what makes the recreated window's Created event free.
		window.Created += (_, _) =>
		{
			MauiProgram.Log("window created -> ShenoraApplication.Start()");
			MauiProgram.Shenora?.Start();
		};
		window.Destroying += (_, _) =>
		{
			if (Shenora.Mobile.MobileWindowLifecycle.IsRecreating)
			{
				MauiProgram.Log("window destroying (recreation) -> keeping Shenora alive");
				return;
			}
			MauiProgram.Log("window destroying -> ShenoraApplication.Stop()");
			MauiProgram.Shenora?.Stop();
		};

		return window;
	}
}
