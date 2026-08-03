using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Mobile;

namespace Shenora.Sample.Maui;

/// <summary>
/// The whole MAUI host, in one page: a <see cref="HybridWebView"/> serving the bundle from
/// <c>Resources/Raw/wwwroot</c>, and a <see cref="MobileIpcBridge"/> carrying the same envelope the
/// desktop shell speaks.
/// <para>
/// Code, not XAML, deliberately — the interesting part is the four lines of wiring, and a XAML file
/// would bury them in markup that has nothing to do with what is being proven.
/// </para>
/// </summary>
public sealed class MainPage : ContentPage
{
	private readonly HybridWebView _webView;
	private readonly MediaRangeProbe _media = new(MauiProgram.Log);
	private MobileIpcBridge? _bridge;

	/// <summary>The page's background, matched by the splash colour — see the csproj's comment.</summary>
	private static readonly Color Shell = Color.FromArgb("#14161A");

	public MainPage()
	{
		Title = "Shenora MAUI sample";
		// The third surface in the no-white-flash chain: splash -> PAGE -> the HTML body. Leave this
		// unset and the default (white in a light theme) shows through for the moment between the
		// splash handing over and the web content painting.
		BackgroundColor = Shell;

		_webView = new HybridWebView
		{
			// Both are the defaults; set explicitly because they ARE the content contract for the
			// BUNDLE on this shell — the platform serves `wwwroot` itself.
			//
			// (An earlier version of this comment added "and no request-interception seam exists to
			// change it". That was wrong, and it is the reason the media work started a session late:
			// `WebResourceRequested` below is exactly such a seam, and it is what serves media.)
			HybridRoot = "wwwroot",
			DefaultFile = "index.html",
		};
		Content = _webView;

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	private void OnLoaded(object? sender, EventArgs e)
	{
		if (_bridge is not null) return;

		var services = MauiProgram.Shenora?.Services;
		if (services is null)
		{
			MauiProgram.Log("ERROR: no Shenora application — the page loaded before MauiProgram built it");
			return;
		}

		// Construct then Attach, the same order the desktop bridge documents: buffering starts at
		// construction so anything emitted while the page is still loading survives.
		_bridge = new MobileIpcBridge(_webView, new MobileIpcBridgeOptions
		{
			Dispatcher = services.GetRequiredService<IMessageDispatcher>(),
			EventBus = services.GetRequiredService<IEventBus>(),
			// What this shell can do, answered in the handshake so ONE page can ship to both shells.
			// Declared by the app rather than guessed by the kit, because it depends on what this
			// app composed: no WindowCommandFacade and no DropZoneManager here, and on a phone there
			// is no window chrome to draw and no OS drag-and-drop to receive.
			Shell = new ShellInfo
			{
				// The real platform, not the framework — "android" / "ios", the peer of the desktop
				// sample's "winforms". Reporting "maui" named the build system, which is the same
				// mistake the packages themselves stopped making when they split by platform; the two
				// faces do not even share a web engine (Chromium's WebView vs WKWebView).
				//
				// DeviceInfo rather than an #if: MAUI already knows, and this file is shared source.
				Name = DeviceInfo.Current.Platform.ToString().ToLowerInvariant(),
				Capabilities = [ShellCapability.FilePicker, ShellCapability.LocalFiles],
			},
			OnClientReady = request => MauiProgram.Log($"client READY (handshake id={request.Id})"),
			Log = MauiProgram.Log,
		});
		_bridge.Attach();
		MauiProgram.Log("bridge attached — waiting for the page handshake");

		// Stage the media clips out of the app package. Fire-and-forget with a GUARD, never a bare
		// async void: this runs with no caller on the stack, so an unhandled throw here is an
		// unhandled UI-thread exception rather than a failed copy.
		_ = Task.Run(async () =>
		{
			try { await _media.PrepareAsync(_webView); }
			catch (Exception ex) { MauiProgram.Log($"media: staging FAILED — {ex}"); }
		});

		// A heartbeat on the bus, so the NOTIFICATION direction is visible on screen rather than
		// merely wired: the desktop sample proves the same path with its 1 Hz tick source.
		_ = Task.Run(async () =>
		{
			var bus = services.GetRequiredService<IEventBus>();
			for (var tick = 1; ; tick++)
			{
				await Task.Delay(TimeSpan.FromSeconds(2));
				bus.Emit("SAMPLE_LOGIC", "TICK", new { tick, at = DateTime.UtcNow.ToString("HH:mm:ss") });
			}
		});
	}

	private void OnUnloaded(object? sender, EventArgs e)
	{
		MauiProgram.Log("page unloaded — disposing the bridge");
		_media.Dispose();
		_bridge?.Dispose();
		_bridge = null;
	}
}
