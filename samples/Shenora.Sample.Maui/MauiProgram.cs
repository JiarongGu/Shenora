using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Mobile;
using Shenora.Sample.Logic;

namespace Shenora.Sample.Maui;

public static class MauiProgram
{
	/// <summary>
	/// Everything Shenora, for the whole process. Held statically because MAUI owns the loop: there
	/// is no <c>Run()</c> to scope it to, and Android recreates the ACTIVITY on a configuration
	/// change while the process — and this — survives.
	/// </summary>
	public static ShenoraApplication? Shenora { get; private set; }

	/// <summary>
	/// One tag for everything this sample logs, so a whole run reads with
	/// <c>adb logcat -s SHENORA:V</c> instead of being lost in the platform's noise.
	/// </summary>
	public const string LogTag = "SHENORA";

	/// <summary>
	/// The ONE platform-conditional line in this sample, and it earns the <c>#if</c>: a device log is
	/// the only way to see what a mobile host did, and each platform has its own sink. Everything else
	/// here — including all of <c>Shenora.Mobile</c> — compiles for both without a single directive,
	/// which is the portability claim actually being tested.
	/// </summary>
	public static void Log(string message)
	{
#if ANDROID
		global::Android.Util.Log.Info(LogTag, message);
#else
		// iOS lands here deliberately. The obvious choice was `Foundation.NSLog`, and it does not
		// exist: .NET 10's `Microsoft.iOS.dll` exposes no NSLog at all (checked by searching the ref
		// assembly after the compiler rejected it — CS0234). The runtime routes Console to the system
		// log on iOS, so this is both the available answer and the one `dev.mjs mac log` reads back.
		Console.WriteLine($"[{LogTag}] {message}");
#endif
	}

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		var app = builder.Build();
		BuildShenora();
		return app;
	}

	private static void BuildShenora()
	{
		if (Shenora is not null) return;   // idempotent, for the same reason Start/Stop are

		Log("building the Shenora application…");

		// No --app-root: the launcher contract is a desktop packaging concern. Android hands the app
		// a private data directory and that is the only writable root, so it IS the app root.
		var shenora = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
		{
			ApplicationName = "Shenora.Sample.Maui",
			Paths = new ShenoraPathsOptions { ExplicitRoot = FileSystem.AppDataDirectory },
		});

		// The MAUI shell: the Core contracts this platform can honour. It registers NO runner —
		// MAUI owns the loop, so Start/Stop are driven from App.
		//
		// Dispatcher.GetForCurrentThread(), NOT Application.Current.Dispatcher. Found by running it:
		// Application.Current is still NULL inside CreateMauiApp — builder.Build() constructs the
		// MauiApp but the Application instance does not exist yet — so the obvious line crashed the
		// process on startup with "No MAUI dispatcher". This method runs on the Android main thread,
		// which IS the UI thread, so asking the thread directly is both correct and earlier-safe.
		var dispatcher = Dispatcher.GetForCurrentThread()
			?? throw new InvalidOperationException("CreateMauiApp is not running on a dispatcher thread.");
		shenora.UseMobile(dispatcher, ex => Log($"UI work failed: {ex}"));

		// Opt-in, exactly as the desktop sample does.
		shenora.Services.AddShenoraOperations();

		shenora.Services.AddSingleton<IMissionScheduler>(sp => new MissionScheduler(new MissionSchedulerOptions
		{
			GlobalLaneCapacity = 4,
			Scopes = [PathClaims.Scope],
			Observers = [new MissionOperationObserver(
				sp.GetRequiredService<IOperationRegistry>(), PortableSampleFacade.Module)],
			Log = Log,
		}));
		shenora.Services.AddSingleton<IFileUpdateQueue>(_ =>
			new FileUpdateQueue(new FileUpdateQueueOptions { Log = Log }));

		// THE POINT OF THIS SAMPLE: the same facade the desktop sample hosts, from the same net10.0
		// assembly, with no Windows anywhere in the graph. If D20's portability were only a claim,
		// this line would not compile.
		shenora.Services.AddModuleFacade<PortableSampleFacade>();
		// Mobile-only, and the reason is measured: `mac safari-eval` cannot be installed on this build Mac
		// and WebKit does not forward a page's console.log to the unified log, so this is the only way page
		// state arrives as TEXT rather than as pixels. See PageDiagFacade.
		shenora.Services.AddModuleFacade<PageDiagFacade>();
		// The SAME line the desktop sample writes, over the SAME routes — the mobile shell's IFileDialogs
		// is what differs, and the page never learns which. What the page DOES learn is which of the four
		// routes this shell will honour, from the capabilities advertised in MainPage's handshake.
		shenora.Services.AddShenoraFileDialogs();
		shenora.Services.AddMessageDispatcher();

		// The on-device probe for Start's idempotency. It must appear exactly ONCE per process.
		// What that measurement actually found, kept because it corrects a claim rather than
		// confirming one: neither a dark-mode switch (MainActivity declares ConfigurationChanges for
		// UiMode) nor a home-and-return with always_finish_activities=1 recreated the activity at
		// all — MainActivity.OnCreate logged #1 only. MAUI's Window is process-scoped, so
		// Window.Created is once-per-process and this hook never had the chance to re-run.
		shenora.OnStarting(_ => Log("lifecycle hook: OnStarting — must appear ONCE per process"));
		shenora.OnStopping(_ => Log("lifecycle hook: OnStopping"));

		Shenora = shenora.Build();
		Log("Shenora application built");
	}
}
