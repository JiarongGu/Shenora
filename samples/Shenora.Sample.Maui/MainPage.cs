using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Mobile;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

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

	/// <summary>
	/// The background-transfer hooks, held so <see cref="OnUnloaded"/> can take them off again. MAUI's
	/// Window outlives this page, so an anonymous subscription would survive it — see the call site.
	/// </summary>
	private EventHandler? _onWindowStopped;
	private EventHandler? _onWindowResumed;

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
	private const IslandSurface IslandClaimant = IslandSurface.LiveActivity;

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
		}, AppCallback.Logger(MauiProgram.Log));

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
			// app composed: no WindowCommandModule and no DropZoneManager here, and on a phone there
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
			Log = AppCallback.Logger(MauiProgram.Log),
		});
		_bridge.Attach();
		MauiProgram.Log("bridge attached — waiting for the page handshake");

		// 🔴 THE BACKGROUND TRANSFER, AND IT IS THE KIT'S NOW — this sample no longer carries its own copy.
		// Until 2026-08-12 the logic lived here as `BackgroundHandoffProbe`, which is how it was proven on
		// both shells; `BackgroundPlaybackTransfer` is that behaviour promoted, with the four measured traps
		// encoded and nine tests behind it. What remains below is the two things only an APP can supply: the
		// lifecycle hooks, and the mapping from what it served to something a native player can open.
		//
		// This is the only media job a page provably cannot do for itself — measured 2026-08-11/12: the page
		// cannot START audio while backgrounding (no user activation survives), and an already-playing
		// <audio> is suspended after ~15 s anyway, while the NATIVE player ran 45 s hidden.
		//
		// ⚠ `Stopped`/`Resumed`, not `Deactivated`/`Activated`. The latter pair also fires for a dialog or a
		// notification shade — a transfer on those would move audio out of the page while the app is still on
		// screen. Stopped/Resumed map to Android's onStop/onResume and iOS's didEnterBackground/
		// willEnterForeground, which is the pair that means "gone".
		//
		// ⚠ And `Stopped` firing AFTER the app is already hidden is FINE here, which is the whole reason this
		// works: a native player is not subject to the webview's autoplay policy, so there is no race to lose.
		// The page-side handoff had to fire before the app hid, and that is precisely why it could not work.
		if (Window is { } window && NativePlayer(services) is { } nativePlayer)
		{
			var transfer = new BackgroundPlaybackTransfer(
				// The PAGE-BACKED player, which is what plain `IMediaPlayer` resolves to (the shells register
				// their own by type). It knows the playhead because index.html posts PLAYER_REPORT.
				services.GetRequiredService<IMediaPlayer>(),
				nativePlayer,
				new BackgroundPlaybackOptions
				{
					// The app's ONE job, and this sample's answer is the route it already owns: the page
					// plays `/media?<base64>`, which `MediaRangeProbe.Resolve` maps to a staged file on the
					// way in. Handing back the last file it served is that same mapping, read out.
					// ⚠ A field read — it is asked at background time and must not block.
					ResolveNativeSource = () => _media.LastServedFile,
					Log = AppCallback.Logger(MauiProgram.Log),
				});

			// ⚠ KEPT IN FIELDS AND REMOVED ON UNLOAD, which is not tidiness. `OnLoaded` re-runs after an
			// unload (it guards on `_bridge`, which `OnUnloaded` nulls) and MAUI's Window is PROCESS-scoped,
			// so subscribing anonymously would leave the old page's handler attached and put TWO transfers on
			// one transition — trap #2, "two owners", arriving structurally instead of in the logic.
			//
			// ⚠ And each one CATCHES, because an `async` lambda on an event is `async void`: with no caller on
			// the stack a throw is an unhandled UI-thread exception that takes the process down, not a failed
			// transfer. `BackgroundPlaybackTransfer` already reports faults as `Failed` rather than throwing,
			// so this guards what is left — a disposed player, or the log sink itself.
			_onWindowStopped = async (_, _) =>
			{
				try
				{
					var result = await transfer.ToBackgroundAsync();
					MauiProgram.Log($"HANDOFF: {result.Outcome} at {result.Position.TotalSeconds:F2}s"
						+ (result.Detail is { } detail ? $" — {detail}" : string.Empty));
				}
				catch (Exception ex) { MauiProgram.Log($"HANDOFF: THREW — {ex.GetType().Name}: {ex.Message}"); }
			};
			_onWindowResumed = async (_, _) =>
			{
				try
				{
					var result = await transfer.ToForegroundAsync();
					MauiProgram.Log($"HANDBACK: {result.Outcome} at {result.Position.TotalSeconds:F2}s"
						+ (result.Detail is { } detail ? $" — {detail}" : string.Empty));
				}
				catch (Exception ex) { MauiProgram.Log($"HANDBACK: THREW — {ex.GetType().Name}: {ex.Message}"); }
			};
			window.Stopped += _onWindowStopped;
			window.Resumed += _onWindowResumed;
		}
		else
		{
			// Said out loud, because a shell with no native player is a legitimate configuration and silence
			// here would read exactly like a transfer that ran and did nothing (D63).
			MauiProgram.Log("background transfer: NOT wired — this shell registers no native player");
		}

		// Stage the media clips out of the app package. Fire-and-forget with a GUARD, never a bare
		// async void: this runs with no caller on the stack, so an unhandled throw here is an
		// unhandled UI-thread exception rather than a failed copy.
		_ = Task.Run(async () =>
		{
			try { await _media.PrepareAsync(_webView, MauiProgram.Shenora!.Pipeline); }
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

					// ⚠ THREE THROWAWAY PROBES HAVE RUN AND BEEN DELETED HERE — `ResponseDisposalProbe`,
					// `LazyBodyProbe` and `ThrowingBodyProbe` (2026-08-12/13) — asking the two things a LAZY
					// response body raises that nothing shipped can reach. Kept as ONE note rather than three,
					// because the earlier two had drifted into stale claims 60 lines apart:
					//   • What a shell does with a body that throws MID-RESPONSE. ✅ ANDROID IS FIXED — its
					//     handover translates the throw into a `Java.IO.IOException` and the page gets a
					//     visible failed load. `ThrowingBodyProbe` was the A/B, and it ran THREE arms because
					//     the wrapper BRANCHES on the exception's type: a managed `System.IO` exception, a
					//     PEERED `Java.Lang.SecurityException` and a `Java.IO.IOException` from the body
					//     itself. The middle one is why three: an earlier wrapper rethrew every peered
					//     throwable and that arm still killed the app. iOS still commits a short body
					//     silently, and Windows is unmeasured; both open in `TASKS.md`.
					//   • Whether an ABANDONED body leaks its source handle — and the two shells answer
					//     UNEQUALLY. Android really disposes one, re-confirmed THROUGH the wrapper; iOS never
					//     PRODUCED one (every window it asks for is small and it drains each), so iOS is proven
					//     only as "this request pattern cannot leak" and a LARGE abandoned window there is
					//     untested. The same run also measured that the platform never touches `Length`,
					//     `Position`, `CanSeek` or `Seek` on a body it is handed — all four counters zero.
					// Every number and raw log is in `.claude/knowledge/mobile-shells.md`. Do not re-add a
					// probe to answer these again — re-add one when the iOS or Windows half gets a FIX to prove.

					// 🔴 THE CONVERSION ROUTE, whole, on a device. Everything under `UseMediaConversion` was
					// covered by unit tests against fakes and had never run end to end on hardware — and the
					// engine below is the kit's OWN (`Mp4Remuxer` + this platform's converter), which is the
					// "no engine on mobile" gap being tested as a one-liner rather than argued about.
					var services = MauiProgram.Shenora?.Services;
					if (services is not null && _media.SourceRoot is { } sourceRoot)
					{
						// 🔴 THE COMPUTED-REMUX ROUTE, and it is registered BEFORE the conversion route because
						// that ordering IS the D71 routing decision: middleware run in registration order, so
						// reversed, the conversion route answers everything its own Resolve matches and a
						// plannable film would 503 through a whole transcode while this one became dead code
						// that still passed every test of its own. (Here the two claim different paths, so the
						// order is not load-bearing — it is written this way because it is what an adopter copies.)
						//
						// Everything under it — planning, the byte map, the range answers, the fall-through —
						// was proven by unit tests against fakes and had never met a webview. A unit test cannot
						// show that a media element ACCEPTS an MP4 that does not exist.
						using var remuxLog = RemuxRouteProbe.LogRequests(interceptor, MauiProgram.Log);
						// ⚠ The scheduler is where the metadata WALK runs now (2026-08-13), so the FIRST
						// request for a clip answers 503 while it walks — see RemuxRouteProbe.Register.
						using var remux = RemuxRouteProbe.Register(
							interceptor,
							services.GetRequiredService<Shenora.Engine.Missions.IMissionScheduler>(),
							sourceRoot, MauiProgram.Log);
						// 🔴 FIRST, AND THE ORDER IS THE MEASUREMENT: a plan is cached per source for the
						// life of the route, so this is the only moment at which a media element can be
						// pointed at a source whose first request is genuinely the route's 503.
						MauiProgram.Log(await RemuxRouteProbe.CheckFirstRequestAsync(
							_webView, sourceRoot, RemuxRouteProbe.Fixture, MauiProgram.Log));
						MauiProgram.Log(await RemuxRouteProbe.CheckAsync(
							_webView, sourceRoot, RemuxRouteProbe.Fixture, MauiProgram.Log));
						MauiProgram.Log(await RemuxRouteProbe.CheckColdSeekAsync(
							_webView, RemuxRouteProbe.Fixture, MauiProgram.Log));

						// 🔴 AND THE SAME TWO BARS ON A FILM PAST THE OLD 64 MiB CEILING, which is the claim
						// deleting that ceiling has to earn: every computed-remux measurement before
						// 2026-08-13 was made under it. The fixture is pushed onto the device rather than
						// committed (78 MB) — see RemuxRouteProbe.BigFixture for both commands — so its
						// absence SKIPS loudly instead of reading as a route that declined a film.
						if (RemuxRouteProbe.IsStaged(sourceRoot, RemuxRouteProbe.BigFixture))
						{
							// 🔴 BEFORE the others, for the same reason CheckFirstRequestAsync runs before
							// them above: a plan is cached per source, so this is the only moment the big
							// film is genuinely UNPLANNED — which is the whole state D72's claim is about.
							// Warming `Fixture` here instead would prove the cache, not the warm.
							MauiProgram.Log(await RemuxRouteProbe.CheckWarmedAsync(
								remux, _webView, sourceRoot, RemuxRouteProbe.BigFixture, MauiProgram.Log));
							MauiProgram.Log(await RemuxRouteProbe.CheckAsync(
								_webView, sourceRoot, RemuxRouteProbe.BigFixture, MauiProgram.Log));
							MauiProgram.Log(await RemuxRouteProbe.CheckColdSeekAsync(
								_webView, RemuxRouteProbe.BigFixture, MauiProgram.Log));
						}
						else
						{
							MauiProgram.Log($"REMUX-BIG: SKIPPED — {RemuxRouteProbe.BigFixture} is not staged "
								+ "on this device; see RemuxRouteProbe.BigFixture for the ffmpeg + push commands");
						}

						// 🔴 GIVE THE PLATFORM CONVERTER A LOG SINK, which the SHELL deliberately cannot.
						// `MobileHostExtensions` registers these with no sink — an app that wants their
						// diagnostics registers its own, and later registrations are asked FIRST. Without
						// this the converters are mute on both mobile shells, and a picture that is silently
						// dropped says only `dropped:["mpeg4"]`: the codec, and nothing about why. That cost
						// three device round-trips on 2026-08-13.
						if (services.GetService<Shenora.Modules.Media.IMediaStreamConversion>()
							is Shenora.Modules.Media.MediaConversionPipeline pipeline)
						{
#if IOS
							Shenora.iOS.IosMediaVideoConversion.Use(pipeline, AppCallback.Logger(MauiProgram.Log));
#elif ANDROID
							Shenora.Android.AndroidMediaVideoConversion.Use(pipeline, AppCallback.Logger(MauiProgram.Log));
#endif
						}

						// 🔴 THE SEGMENT ROUTE (D71 piece 3), and this is its FIRST contact with a real
						// encoder and a real ManagedMediaSource. Everything in that tier is unit-tested
						// against a FAKE IMediaStreamConversion, which by construction cannot answer whether
						// the platform encoder reorders, whether OutputConfig arrives before the init
						// segment is written, or whether the fragments the kit produces are ACCEPTED. See
						// SegmentRouteProbe for why those three and not others.
						using (var segments = SegmentRouteProbe.Register(
							interceptor,
							services.GetService<Shenora.Modules.Media.IMediaStreamConversion>(),
							sourceRoot,
							Path.Combine(FileSystem.CacheDirectory, "segments"),
							MauiProgram.Log))
						{
							MauiProgram.Log(await SegmentRouteProbe.CheckAsync(_webView, sourceRoot, MauiProgram.Log));
							// The SAME route over a 60 s source, for one question the short fixture cannot
							// reach: `endstreaming`. Its 6 s left `ManagedMediaSource.streaming` true, which
							// says the source was never given enough to want to stop — not that it will not.
							MauiProgram.Log(await SegmentRouteProbe.CheckAsync(
								_webView, sourceRoot, MauiProgram.Log, SegmentRouteProbe.LongFixture));

							// The SAME stream through the shipped `bindSegmentStream`, so the module an
							// adopter gets is the one a device actually runs (D63). The hand-written check
							// above stays as the control.
							MauiProgram.Log(await SegmentRouteProbe.CheckKitBinderAsync(
								_webView, sourceRoot, MauiProgram.Log));

							// A run that STARTS at segment 1 — the seek shape, asked of the engine directly
							// because the page cannot force it without racing the cache.
							MauiProgram.Log(SegmentRouteProbe.CheckSeekRun(
								services.GetService<Shenora.Modules.Media.IMediaStreamConversion>(),
								sourceRoot, Path.Combine(FileSystem.CacheDirectory, "segments"), MauiProgram.Log));

							// Does this platform's video encoder REORDER? Only reachable where the shell
							// converts a picture at all, which is the phone and not the simulator.
							MauiProgram.Log(await SegmentRouteProbe.CheckReencodedPictureAsync(
								services.GetService<Shenora.Modules.Media.IMediaStreamConversion>(),
								sourceRoot, Path.Combine(FileSystem.CacheDirectory, "segments"), MauiProgram.Log));
							// D71 piece 5, in the same window: the route must still be alive to answer, and
							// the segments it produced are what gets merged.
							MauiProgram.Log(await SegmentRouteProbe.MergeAsync(segments, sourceRoot, MauiProgram.Log));
						}

						using var route = ConversionRouteProbe.Register(
							interceptor,
							services.GetRequiredService<Shenora.Engine.Missions.IMissionScheduler>(),
							services.GetRequiredService<Shenora.Core.Events.IEventBus>(),
							services.GetService<Shenora.Modules.Media.IMediaStreamConversion>(),
							sourceRoot, MauiProgram.Log);
						if (route is not null)
						{
							// The fixture whichever converter this shell claims: Android takes mp3, iOS AC-3.
							// 🔴 THE `clip-video-*` PAIR, and the "video" in the name is the point: these carry an
							// h264 track beside the soundtrack MP4 cannot hold, so the route must COPY the video
							// while converting the audio — and the probe then plays the output and demands a
							// picture. The plain `clip-mp3/ac3.mkv` fixtures are audio-only (built with `-vn` for
							// TranscodeProbe), so this route "passed" for a fortnight without ever carrying a
							// video track through a conversion.
							var conversion = services.GetService<Shenora.Modules.Media.IMediaStreamConversion>();
							var fixture = conversion?.CanConvert(Shenora.Modules.Media.MediaStreamKind.Audio, "mp3") == true ? "clip-video-mp3.mkv" : "clip-video-ac3.mkv";
							MauiProgram.Log(await ConversionRouteProbe.CheckAsync(_webView, sourceRoot, fixture, MauiProgram.Log));
							// 🔴 THE PICTURE CONVERSION, and it must be proven by PLAYBACK rather than by the
							// READY event. A converted file whose `avcC` is wrong, or whose frames were left
							// Annex-B, still converts, still caches and still fires READY — and then shows a
							// blank rectangle. `size != 0x0` is the only assertion that can tell those apart.
							// 🔴 ASK THE SEAM WHICH PICTURE FIXTURE THIS SHELL CAN ACTUALLY CONVERT, rather
							// than hard-coding one per platform. Both shells then prove the same path with
							// whichever codec they really decode — Android takes mpeg4, and an iPhone 17 Pro
							// takes h263 because it has NO MPEG-4 Part 2 decoder (47 bytes of ESDS present
							// and VTDecompressionSession still refuses, measured 2026-08-13).
							// ⚠ The list is ordered by preference, not by platform: the day a shell gains an
							// mpeg4 decoder it starts using the first entry with no edit here, and a shell
							// that gains neither SKIPS with the codecs named instead of failing.
							var pictureFixture = new[] { ("mpeg4", "clip-mpeg4-aac.mkv"), ("h263", "clip-h263-aac.mkv") }
								.FirstOrDefault(f => conversion?.CanConvert(
									Shenora.Modules.Media.MediaStreamKind.Video, f.Item1) == true);
							if (pictureFixture.Item2 is not null)
							{
								MauiProgram.Log($"[CONVERT-PICTURE] this shell converts {pictureFixture.Item1} "
									+ $"— using {pictureFixture.Item2}");
								MauiProgram.Log(await ConversionRouteProbe.CheckAsync(
									_webView, sourceRoot, pictureFixture.Item2, MauiProgram.Log));
							}
							else
							{
								MauiProgram.Log("CONVERT-PICTURE: SKIPPED — this shell converts neither mpeg4 "
									+ "nor h263, so the route DROPS the track and refuses, which is correct.");
							}
							// And the REFUSAL, which needs its own case: a source whose video the kit cannot
							// carry must fail loudly and name the codec rather than serve the audio-only file
							// a remux happily produces. Testing only the success path is how that shipped.
							MauiProgram.Log(await ConversionRouteProbe.CheckRefusalAsync(
								_webView, services.GetRequiredService<IEventBus>(), sourceRoot, MauiProgram.Log));
						}
					}
				}
				// The app-level pipeline route, declared in MauiProgram BEFORE this webview existed. It needs
				// no interceptor handle here, and that is precisely the point being proven.
				MauiProgram.Log(await PageProbe.CheckAppPipelineAsync(_webView, MauiProgram.Log));
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
			// 🔴 BY ITS OWN TYPE, not IMediaPlayer (2026-08-08). The shells no longer claim IMediaPlayer —
			// that resolves to the PAGE-BACKED player everywhere now — and asking for the contract here
			// would not merely test the wrong object, it would HANG: the page-backed player's OpenAsync
			// completes on the page's first PLAYER_REPORT, and there is no element behind this probe to
			// send one. Absent-by-design and quietly-wrong look identical from the outside (D63), so the
			// probe must name the type it means.
			//
			// GetService, not GetRequiredService: a shell may ship no native player, and the probe reports
			// that as a fact rather than a failure.
			try
			{
				// ⚠ ONE ARM PER PLATFORM, not one arm for "mobile". The types are genuinely different now
				// (D66-era restructure), so a shared arm naming either of them fails to compile on the
				// other — which is exactly what a blanket rename did here, and what building on the Mac
				// caught. There is no shared name left to reach for, and that is the point.
#if ANDROID
				await MediaPlayerProbe.RunAsync(services.GetService<Shenora.Android.AndroidMediaPlayer>(), MauiProgram.Log);
				// The TRANSCODE tier, after the player — it asserts its output by PLAYING it, so it needs the
				// same player and there is no point running it if the player itself did not work.
				//
				// ⚠ Its own pipeline, WITH A LOG, rather than the registered singleton. The host registers the
				// converter without a sink (nothing threads one through `MobileHostExtensions`), and a codec
				// failure then reaches `Mp4Remuxer`, which reports `SourceUnreadable "malformed source"` —
				// blaming the FILE for a fault belonging to the codec. This is the app doing what an adopter
				// debugging the same thing would have to do.
				var diagnosticPipeline = new Shenora.Modules.Media.MediaConversionPipeline();
				Shenora.Android.AndroidMediaAudioConversion.Use(diagnosticPipeline, AppCallback.Logger(MauiProgram.Log));
				await TranscodeProbe.RunAsync(diagnosticPipeline,
					services.GetService<Shenora.Android.AndroidMediaPlayer>(), MauiProgram.Log);
#elif IOS || MACCATALYST
				await MediaPlayerProbe.RunAsync(services.GetService<Shenora.iOS.IosMediaPlayer>(), MauiProgram.Log);
				// Same as the Android arm: its own pipeline WITH a log, because the host registers the
				// converter without one and a codec failure is otherwise reported as a malformed file.
				var diagnosticPipeline = new Shenora.Modules.Media.MediaConversionPipeline();
				Shenora.iOS.IosMediaAudioConversion.Use(diagnosticPipeline, AppCallback.Logger(MauiProgram.Log));
				await TranscodeProbe.RunAsync(diagnosticPipeline,
					services.GetService<Shenora.iOS.IosMediaPlayer>(), MauiProgram.Log);
#else
				await MediaPlayerProbe.RunAsync(null, MauiProgram.Log);
#endif
				// 🔴 DEAD LAST, AFTER EVERY NATIVE PLAYER PROBE, and the ordering is a measured requirement
				// rather than tidiness. Started earlier, the page's <audio> is PAUSED within a second —
				// `IMediaPlayer` opens a file, takes the audio session, and the page loses it. Measured on
				// an iPhone 2026-08-09: `audio playing t=0.00` … `audio PAUSED — t=2.86s`, immediately
				// before the transcode probe. That interaction is correct behaviour and it makes any
				// background-audio measurement started before it meaningless.
				MauiProgram.Log(await PageProbe.StartBackgroundAudioAsync(_webView, MauiProgram.Log));

				// After that one for the same audio-session reason, and it drives a different element: the
				// <video>, which is what `reportPlayer` is wired to and therefore what the host's believed
				// position comes from. Leaves playback RUNNING on purpose — the measurement is what
				// `HANDOFF` says when something backgrounds the app next.
				MauiProgram.Log(await PlayheadProbe.ArmAsync(
					_webView,
					MauiProgram.Shenora?.Services?.GetService<Shenora.Modules.Media.IMediaPlayer>(),
					MauiProgram.Log));
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
			// what an adopting app does and it also proves the registration in UseAndroid/UseIOS picked the right
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
		CodecProbe.CrossCheck(services.GetRequiredService<Shenora.Modules.Media.IMediaCapability>(),
			services.GetService<Shenora.Modules.Media.IMediaStreamConversion>(), MauiProgram.Log);

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


	/// <summary>
	/// The NATIVE player for this shell, resolved BY ITS OWN TYPE — the shells deliberately do not register
	/// it as <c>IMediaPlayer</c> (2026-08-08), because the default `IMediaPlayer` is the PAGE-BACKED one and a
	/// shell that claimed the interface silently stopped `useMediaPlayer(ref)` working.
	/// <para>
	/// ⚠ One arm per platform rather than one for "mobile": the types are genuinely different and there is no
	/// shared name left to reach for.
	/// </para>
	/// </summary>
	private static Shenora.Modules.Media.IMediaPlayer? NativePlayer(IServiceProvider services) =>
#if ANDROID
		services.GetService<Shenora.Android.AndroidMediaPlayer>();
#elif IOS || MACCATALYST
		services.GetService<Shenora.iOS.IosMediaPlayer>();
#else
		null;
#endif

	private void OnUnloaded(object? sender, EventArgs e)
	{
		MauiProgram.Log("page unloaded — disposing the bridge");
		// FIRST, and off the WINDOW rather than this page: the Window outlives MainPage, so a handler left
		// attached here would keep driving a transfer whose player and media route have just been disposed —
		// and would be joined by a second one when the page reloads.
		if (Window is { } window)
		{
			if (_onWindowStopped is { } stopped) window.Stopped -= stopped;
			if (_onWindowResumed is { } resumed) window.Resumed -= resumed;
		}
		_onWindowStopped = null;
		_onWindowResumed = null;

		_media.Dispose();
		_safeArea.Dispose();
		_bridge?.Dispose();
		_bridge = null;
	}
}
