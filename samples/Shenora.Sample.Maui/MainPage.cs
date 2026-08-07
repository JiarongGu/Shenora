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
	private readonly MobileSafeArea _safeArea;

	/// <summary>The page's background, matched by the splash colour — see the csproj's comment.</summary>
	private static readonly Color Shell = Color.FromArgb("#14161A");

	/// <summary>Which of iOS's two Island mechanisms this run claims. They cannot both be used — see the
	/// comment at the call site, which records the three deploys that cost us.</summary>
	private enum IslandSurface { NowPlaying, LiveActivity }

	/// <summary>
	/// The sample's choice: <b>NowPlaying</b>, which is the one that actually reaches the Dynamic Island.
	/// <para>
	/// 🔴 Not a preference — the Live Activity path cannot render on a device at all (the widget `.appex`
	/// built by `swiftc` exits before serving; see `TASKS.md`). Now Playing needs no extension, and a
	/// public sibling proved it on the Island for a player. It is also what Apple intends for playback.
	/// </para>
	/// </summary>
	private const IslandSurface IslandClaimant = IslandSurface.NowPlaying;

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

		// The safe-area capability, opt-in like every other kit cluster and configured here rather than
		// assumed. Everything in the options is individually declinable — this sample takes all four so
		// the whole thing is exercised on a device; an app that wants only the default takes only that.
		//
		// ⚠ It is constructed with the WEBVIEW, before Loaded, on purpose: the default and the splash go
		// out at document start, and the entire reason they exist is that the platform's real numbers do
		// not arrive until after the first paint.
		_safeArea = new MobileSafeArea(_webView, new SafeAreaOptions
		{
			// A guess that is right on most phones, replaced by the platform's real numbers the moment
			// they arrive. Without it the first screen lays out against zero and renders under the status
			// bar — measured on Android, where env() reports 0 for the whole first page load.
			Default = new SafeAreaInsets(24, 0, 24, 0),
			// Painted behind the inset strips so they match the page instead of showing whatever is
			// behind the webview.
			Color = "#14161a",
			// The correction from default to measured EASES rather than snaps.
			Settle = TimeSpan.FromMilliseconds(180),
			// And the belt-and-braces answer: cover the page until the real numbers land. It dismisses
			// itself after SplashTimeout whether or not they ever do.
			Splash = true,
		}, MauiProgram.Log);

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

			// The two adopter-filed seam tests, and they run HERE — after a route is registered — because
			// that is the precondition for both. Give the page a moment to finish its own load first: a
			// reload probe that fires mid-load would be testing the wrong navigation.
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(3));
				if (_media.Interceptor is { } interceptor)
				{
					using var headerRoute = PageProbe.RegisterHeaderRoute(interceptor, MauiProgram.Log);
					MauiProgram.Log(await PageProbe.CheckResponseHeadersAsync(_webView, MauiProgram.Log));
				}
				// Media BEFORE the reload probe: the reload replaces the document, so anything asserted
				// about the page's <video> has to happen while that document is still the one under test.
				MauiProgram.Log(await PageProbe.CheckMediaAsync(_webView, MauiProgram.Log));
				// And the same thing through the page's OWN button, because the two are different code and
				// only the synthetic one was ever exercised — see CheckUiPlaybackAsync.
				MauiProgram.Log(await PageProbe.CheckUiPlaybackAsync(_webView, MauiProgram.Log));
				// To sabotage-verify the gate below, wrap it in:
				//     using var s = PageProbe.SabotageMainDocument(_media.Interceptor!, MauiProgram.Log);
				// Done 2026-08-05: FAIL with `title=|nodes=5|text=Not Found`, PASS without.
				MauiProgram.Log(await PageProbe.CheckReloadAsync(_webView, MauiProgram.Log));
			}
			catch (Exception ex) { MauiProgram.Log($"[NAV] probe threw — {ex}"); }

			// The HOST-OWNED player (D54). It runs HERE for a precondition, not for tidiness: it opens the
			// STAGED clip, so it cannot start before PrepareAsync above has copied it out of the app
			// package — and after the page probes, because two things making sound at once would make
			// either result unreadable.
			//
			// GetService, not GetRequiredService: the player is deliberately ABSENT on Android and Windows,
			// and the probe reports that as a fact rather than a failure.
			try
			{
				await MediaPlayerProbe.RunAsync(services.GetService<Shenora.Media.IMediaPlayer>(), MauiProgram.Log);
			}
			catch (Exception ex) { MauiProgram.Log($"PLAYER: probe threw — {ex}"); }
		});

		// 🔴 THE TWO ISLAND CLAIMANTS ARE MUTUALLY EXCLUSIVE, and running both is why the Dynamic Island
		// showed "a long bar that only opens the app" on a device for three deploys (2026-08-07).
		//
		// iOS has TWO mechanisms that reach the Island and they are easy to confuse:
		//   · NOW PLAYING (MPNowPlayingInfoCenter + MPRemoteCommandCenter) — the system's MEDIA
		//     presentation. Apple's own look, and what a player is SUPPOSED to use.
		//   · LIVE ACTIVITY (ActivityKit + a widget extension) — a custom, app-drawn card, intended for
		//     deliveries, timers, scores.
		//
		// An app publishing a Now Playing session takes the Island for the media presentation, and a Live
		// Activity started alongside it has nowhere to render. The tell is exact and was documented by a
		// sibling before we hit it: *"the island falls back to the app icon, and tapping it can only open
		// the app"*. This sample published BOTH at startup, so the Island it showed was never the widget's.
		//
		// So the sample picks one, deliberately, and says which. It is a SAMPLE — an adopting app has the
		// same choice to make, and the kit ships both capabilities precisely because different apps want
		// different ones.
		if (IslandClaimant == IslandSurface.NowPlaying)
		{
			// The system media transport surface. Resolved from DI rather than constructed, because that is
			// what an adopting app does and it also proves the registration in UseMobile picked the right
			// implementation for this platform.
			PlaybackSessionProbe.Run(services.GetRequiredService<IPlaybackSession>(), MauiProgram.Log);
		}
		else
		{
			MauiProgram.Log("[PLAYBACK] SKIPPED — this run claims the Island with a Live Activity instead; "
				+ "publishing a Now Playing session too would take the Island and leave the widget unrendered.");
		}

		// What this DEVICE can decode and encode. Nothing in the kit depends on it — it is a MEASUREMENT,
		// and it is here rather than in a one-off script because the answer is per-device (Android's codec
		// support is vendor-declared) and therefore has to be re-askable on whatever hardware turns up.
		MauiProgram.Log(CodecProbe.Question);
		CodecProbe.Run(MauiProgram.Log);
		// And the KIT's answer to the same question, from DI, so the two independent queries can be
		// compared. A contract that disagrees with the platform is worse than no contract.
		CodecProbe.CrossCheck(services.GetRequiredService<Shenora.Media.IMediaCapability>(),
			services.GetService<Shenora.Media.IMediaAudioConversion>(), MauiProgram.Log);

		// The live status surface. Fire-and-forget with a GUARD, never a bare async void — same rule as
		// the media staging above.
		if (IslandClaimant == IslandSurface.LiveActivity)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					await LiveActivityProbe.RunAsync(services.GetRequiredService<ILiveActivities>(), MauiProgram.Log);
				}
				catch (Exception ex) { MauiProgram.Log($"[ACTIVITY] probe threw: {ex}"); }
			});
		}

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
		_safeArea.Dispose();
		_bridge?.Dispose();
		_bridge = null;
	}
}
