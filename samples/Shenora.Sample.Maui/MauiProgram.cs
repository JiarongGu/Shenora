using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Mobile;
using Shenora.Sample.Logic;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

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

		// The shell's PICTURE surface (D80): registers MediaSurfaceView's platform handler and makes the
		// webview see-through. ⚠ Opt-in, and this sample takes it so the seam has a real consumer on a
		// device rather than only in tests (D63) — the page's own CSS still has to leave the hole.
		builder.UseShenoraMediaSurface();

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
		// Named for the PLATFORM (D65) — the shell call is the one thing an adopter genuinely picks, so
		// it says which platform it is picking. A multi-targeted app writes the `#if`; a single-platform
		// one writes one line and never sees this.
#if ANDROID
		// ⚠ BEFORE UseAndroid, and that ordering is the mechanism: the shell registers the player with
		// `TryAddSingleton`, so an app registration WINS. This sample registers no ILoggerFactory, so the
		// DI-resolved logger the shell would otherwise use is null and the player is MUTE — and a mute
		// player cannot tell you whether a picture surface was ever attached to it, which is the one
		// question a black rectangle raises. Same reasoning as the back/lifecycle coordinators below.
		shenora.Services.AddSingleton(new Shenora.Android.AndroidMediaPlayer(AppCallback.Logger(Log)));
		shenora.UseAndroid(dispatcher, ex => Log($"UI work failed: {ex}"));
#elif IOS || MACCATALYST
		shenora.UseIOS(dispatcher, ex => Log($"UI work failed: {ex}"));
#endif

		// The service half of the picture surface. Registered with no views — MainPage attaches those when
		// it is built, because DI is composed before any page exists.
		shenora.Services.AddShenoraMediaSurface();


		// The scheduler and the file queue are ALREADY REGISTERED (D64); this only says where the sample
		// disagrees with a default. The file queue is left entirely alone — its defaults are right.
		shenora.UseMissions(options =>
		{
			options.GlobalLaneCapacity = 4;
			options.Scopes = [PathClaims.Scope];
			options.Log = AppCallback.Logger(Log);
		});
		// ⚠ The observer needs a SERVICE, so it attaches once a provider exists rather than in the
		// options above. Shenora must never learn what an operation is (D19/D20), so this mapping stays
		// the app's — it is the whole cost of the pairing, and it is the same on both samples.
		shenora.OnStarting(app =>
			app.Services.GetRequiredService<MissionSchedulerOptions>().Observers =
				[new MissionEventPublisher(
					app.Services.GetRequiredService<IEventBus>(), PortableSampleModule.Module)]);

		// THE POINT OF THIS SAMPLE: the same facade the desktop sample hosts, from the same net10.0
		// assembly, with no Windows anywhere in the graph. If D20's portability were only a claim,
		// this line would not compile.
		shenora.Services.AddIpcModule<PortableSampleModule>();
		// The system back gesture. 🔴 The PAIR: this registers the coordinator and its routes, and
		// MainPage constructs the MobileBackNavigation that actually raises a press — registered alone,
		// the page's INTERCEPT would be accepted while no press ever arrived, which is the D63 shape
		// where absent is indistinguishable from working.
		// ⚠ The logger is passed EXPLICITLY, and that is not decoration: this sample registers no
		// ILoggerFactory, so the fallback leaves both coordinators mute. Measured on the emulator — a run
		// where the events reached the page correctly and the host logged nothing, which reads exactly
		// like a broken feature.
		shenora.Services.AddShenoraBackNavigation(log: AppCallback.Logger(Log));
		// Foreground transitions, and how long the app was away. Same PAIR shape: MainPage constructs the
		// MobileAppLifecycle that reports them, or this publishes nothing and a page waits for ever.
		shenora.Services.AddShenoraAppLifecycle(AppCallback.Logger(Log));
		// Holding the window at an orientation. ⚠ Unlike the pair above there is NO page-side half to
		// construct — the shell's implementation comes from UseAndroid/UseIOS — but the capability must
		// still be advertised, which MainPage does from MobileWindowOrientation.IsSupported.
		shenora.Services.AddShenoraWindowOrientation();
		// Mobile-only, and the reason is measured: `mac safari-eval` cannot be installed on this build Mac
		// and WebKit does not forward a page's console.log to the unified log, so this is the only way page
		// state arrives as TEXT rather than as pixels. See PageDiagModule.
		shenora.Services.AddIpcModule<PageDiagModule>();
		// ⚠ NOTHING here registers the kit's dialog routes, the dispatcher or the operations registry —
		// Build() and UseAndroid/UseIOS do (D64/D65). What the page still learns from the handshake is
		// which of the four dialog routes THIS shell will honour; two of them are desktop-only (D35).

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

		// The pipeline surface, declared HERE because it must precede the first webview — the pipeline
		// freezes on first application by design. This is the mobile counterpart of the desktop sample's
		// `INTERCEPTOR SEAM` probe, and it exists because the mobile half had never actually run: the
		// sample handed every interceptor a fresh pipeline, so `app.Use(…)` reached nothing on Android or
		// iOS while compiling perfectly (D63 — absent is indistinguishable from working).
		PageProbe.RegisterAppPipelineRoute(Shenora.Pipeline, Log);
	}
}
