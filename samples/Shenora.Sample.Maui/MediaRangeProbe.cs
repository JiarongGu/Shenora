using Shenora;
using Shenora.Mobile;
using Shenora.Engine.Update;
using Shenora.Core.WebView;

namespace Shenora.Sample.Maui;

/// <summary>
/// Serving local media to the page — and after the D45 re-layering, this is the whole of it: declare where
/// files may come from, say how your route maps to one, and let the shell's interceptor plus
/// <c>Shenora</c>'s file middleware do the rest.
/// <para>
/// Worth keeping the history, because it is the shape of the mistake this file went through. It used to BE
/// the range implementation (proven on both devices — D44), then it called <c>Shenora.Media</c>'s range
/// server, then a media PLATFORM package. Each step was better and each still had an app repeating something
/// the kit should own. What is left now decides nothing about ranges, containment, content types or the
/// platform's body rule.
/// </para>
/// <para>
/// ⚠ Note what is NOT used here: anything from <c>Shenora.Modules.Media</c>. Serving a file the platform can
/// already decode needs no media middleware at all — that is D45's "the interceptor without the media bundle
/// should still load video/image/audio". The media tier is a FURTHER middleware, for the files this cannot
/// serve. (This said "media package" until 2026-08-10; there is no media package since D53 — it is a
/// namespace inside <c>Shenora</c>, so the point is about what the app REFERENCES in code, not about ids.)
/// </para>
/// </summary>
internal sealed class MediaRangeProbe : IDisposable
{
	/// <summary>The clips this sample serves, and therefore the ONLY things it serves.</summary>
	private static readonly string[] Clips = ["clip-faststart.mp4", "clip-tailmoov.mp4"];

	/// <summary>
	/// The route: <c>media</c>, carrying an ENCODED PAYLOAD — the shape <c>@shenora/react</c>'s
	/// <c>mediaUrl()</c> produces.
	/// <para>
	/// ⚠ Matched by PATH, never by scheme. The page writes it relative, so the same url arrives as
	/// <c>app://0.0.0.1/media?…</c> on iOS and <c>https://0.0.0.1/media?…</c> on Android — asserting either
	/// scheme would break the other shell (D44's matrix).
	/// </para>
	/// </summary>
	private const string RoutePath = "/media";

	private readonly Action<string> _log;
	private MobileWebViewInterceptor? _interceptor;
	private IDisposable? _route;
	private string? _root;
	private volatile string? _lastServed;

	public MediaRangeProbe(Action<string> log) => _log = log;

	/// <summary>
	/// The shell's interceptor, once <see cref="Attach"/> has built it — so a second probe can add its
	/// own middleware to the SAME pipeline rather than constructing a second interceptor over one webview.
	/// (Two interceptors would mean two <c>WebResourceRequested</c> subscriptions, which is exactly the
	/// last-writer-wins hazard the desktop host's single-subscription comment warns about.)
	/// </summary>
	public IWebViewInterceptor? Interceptor => _interceptor;

	/// <summary>Where the staged clips live — the path is known from <see cref="Attach"/>, and
	/// <see cref="PrepareAsync"/> is what puts the files in it, so a second route can serve the SAME files
	/// without re-deriving the path.</summary>
	public string? SourceRoot => _root;

	/// <summary>
	/// The last file this route actually served, or <c>null</c> before the page has asked for one.
	///
	/// <para>
	/// 🔴 <b>This is the app's half of <c>BackgroundPlaybackOptions.ResolveNativeSource</c>, and it lives
	/// HERE because this is the one place that already knows the answer.</b> The page plays
	/// <c>/media?&lt;base64&gt;</c> — a route this app serves through the interceptor — and a native player
	/// cannot fetch that. Something has to map "what the page is playing" back to "a file this device can
	/// open", and <see cref="Resolve"/> is literally that mapping, in the direction the request already
	/// travels. Recording its answer costs one field.
	/// </para>
	/// <para>
	/// ⚠ <b>Written from the interceptor's thread and read at background time from the app's</b>, hence
	/// <c>volatile</c>: a reference assignment is already atomic, and this only forbids the compiler and the
	/// CPU from caching a stale value across the two.
	/// </para>
	/// </summary>
	public string? LastServedFile => _lastServed;

	/// <summary>
	/// Copy ONE packaged fixture into the staging root if it is not already there, and return its path.
	///
	/// <para>
	/// 🔴 <b>A PROBE MUST STAGE WHAT IT NEEDS — depending on another probe's side effect is a first-run
	/// bug that hides forever afterwards.</b> <see cref="ConversionRouteProbe"/> asks for
	/// <c>clip-mp3.mkv</c>, which only <see cref="TranscodeProbe"/> ever wrote, and it runs LATER. So on a
	/// cold install the source did not exist and the route correctly answered 404, while every subsequent
	/// run found the file the previous run had left behind and passed. Measured 2026-08-10: uninstall,
	/// install, first launch → <c>CONVERT: FAIL status=404</c>; relaunch → <c>PASS 325433 bytes</c>.
	/// </para>
	/// <para>
	/// ⚠ <b>That is also why it "failed on one emulator and passed on another".</b> Nothing about the
	/// devices differed — one had run the app before. A per-device difference is the seductive reading
	/// (WebView 133 vs 110 was the standing suspicion) and it was wrong; the state that differed was on
	/// DISK, not in the platform.
	/// </para>
	/// </summary>
	public static async Task<string> EnsureStagedAsync(string root, string clip, Action<string> log)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(clip);
		ArgumentNullException.ThrowIfNull(log);

		Directory.CreateDirectory(root);
		var destination = Path.Combine(root, clip);
		if (File.Exists(destination)) return destination;

		await using var source = await FileSystem.OpenAppPackageFileAsync($"wwwroot/media/{clip}").ConfigureAwait(false);
		await using var target = File.Create(destination);
		await source.CopyToAsync(target).ConfigureAwait(false);
		await target.FlushAsync().ConfigureAwait(false);
		log($"media: staged {clip} -> cache ({target.Length} bytes)");
		return destination;
	}

	/// <summary>
	/// Subscribe to the webview and register the route. <b>Call this from the page CONSTRUCTOR</b>, before
	/// <c>Content = webView</c>.
	/// </summary>
	/// <remarks>
	/// 🔴 <b>CONSTRUCTOR TIME, NOT <c>Loaded</c> — and this sample had it wrong until 2026-08-21.</b> The
	/// interceptor's constructor is where <c>WebResourceRequested</c> is subscribed, so building it in
	/// <c>Loaded</c> is after the webview has navigated: the DOCUMENT and every asset are served by the
	/// platform and only requests the page makes LATER ever reach the pipeline. It hid here for exactly the
	/// reason it hides in a real app — <c>/media</c> is a late request, so nothing this probe measures was
	/// ever affected. The kit now says so out loud (<c>MobileWebViewInterceptor</c> warns once), and this
	/// sample was the first thing its warning caught. Same reason <c>_safeArea</c> is built in the page
	/// constructor.
	/// <para>
	/// ⚠ The route is registered here too, not deferred until the clips are staged. Its allow-list is a path
	/// this can compute synchronously; <see cref="PrepareAsync"/> then fills that directory. A request
	/// arriving mid-copy finds a handler and no file, which <c>UseFiles</c> answers as a 404 — strictly
	/// better than the old ordering, where it found no route and the platform served a page-shaped 404.
	/// </para>
	/// </remarks>
	/// <param name="webView">The page's webview — this is the app's ORDINARY window, not a probe-owned one.</param>
	/// <param name="pipeline">
	/// The application's <see cref="WebViewPipeline"/>. Required rather than optional so the choice is made
	/// at the call site instead of forgotten — the same reason the interceptor's own parameter is required.
	/// </param>
	public void Attach(HybridWebView webView, WebViewPipeline pipeline)
	{
		ArgumentNullException.ThrowIfNull(pipeline);

		var root = Path.Combine(FileSystem.CacheDirectory, "media");
		Directory.CreateDirectory(root);
		_root = root;

		// 🔴 THE APP'S PIPELINE, and it used to be a fresh empty one. The comment justifying that said "this
		// probe owns an isolated webview", which was simply not true — this is handed the page's MAIN
		// webview, so this is the ordinary window the same comment said should pass `app.Pipeline`.
		// The cost of the mistake was not a wrong measurement, it was ABSENCE: `app.Use(…)` reached no
		// webview on Android or iOS, so the mobile half of D64's pipeline surface had never once executed —
		// and an unapplied pipeline is indistinguishable from one whose routes nothing requested (D63).
		// Corrected 2026-08-09. Isolation is still available and still correct for a probe that genuinely
		// owns its webview: pass `new WebViewPipeline()`.
		_interceptor = new MobileWebViewInterceptor(webView, pipeline, AppCallback.Logger(_log));

		// THE WHOLE WIRING. No BodyMode, no content-type table, no range arithmetic, no containment check —
		// UseFiles reads the platform's delivery rule off the interceptor so it cannot be passed in wrong.
		_route = _interceptor.UseFiles(new WebViewFileOptions
		{
			AllowedRoots = [root],
			Resolve = Resolve,
		});

		_log($"media: attached ({_interceptor.RangeDelivery} on this platform) before the webview navigated");
	}

	/// <summary>
	/// Stage the clips out of the app package. <see cref="Attach"/> must have run first — it owns the root
	/// and the route.
	/// </summary>
	public async Task PrepareAsync()
	{
		var root = _root ?? throw new InvalidOperationException(
			"MediaRangeProbe.Attach must run first — it owns the media root and the route.");

		foreach (var clip in Clips)
		{
			var destination = Path.Combine(root, clip);
			if (File.Exists(destination))
			{
				_log($"media: {clip} already in cache ({new FileInfo(destination).Length} bytes)");
				continue;
			}

			await using var source = await FileSystem.OpenAppPackageFileAsync($"wwwroot/media/{clip}");
			await using var target = File.Create(destination);
			await source.CopyToAsync(target);
			await target.FlushAsync();
			_log($"media: staged {clip} -> cache ({target.Length} bytes)");
		}

		_log("media: ready — the page may load /media?<payload>");
	}

	/// <summary>
	/// The app's URL shape, and genuinely the only media code an app has to write.
	/// <para>
	/// Null means "not a media request", so the pipeline falls through to the platform. Whatever this DOES
	/// return is still authorised against the allowed roots, so being generous here cannot widen what is
	/// reachable.
	/// </para>
	/// </summary>
	private string? Resolve(Uri uri)
	{
		if (!uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase)) return null;

		// The payload is the whole query: `?<base64url of JSON>`, no parameter name — what mediaUrl() emits.
		var encoded = uri.Query.TrimStart('?');
		if (encoded.Length == 0) return null;

		string? name;
		try
		{
			var padded = encoded.Replace('-', '+').Replace('_', '/');
			padded += new string('=', (4 - padded.Length % 4) % 4);
			var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
			// ⚠ LOG WHAT IT DECODED TO. That is the price of an opaque payload: the response body cannot
			// explain a refusal, so the host log is the only place a wrong payload can be diagnosed.
			_log($"media: payload decoded -> {json}");
			name = System.Text.Json.JsonDocument.Parse(json).RootElement
				.TryGetProperty("src", out var src) ? src.GetString() : null;
		}
		catch (Exception ex)
		{
			// A malformed payload is a REFUSAL, not a crash — it is page-supplied input.
			_log($"media: payload could not be decoded ({ex.GetType().Name})");
			return null;
		}

		// An app-level allow-list on top of the kit's containment. This sample knows exactly two filenames.
		if (name is null || !Clips.Contains(name, StringComparer.Ordinal)) return null;
		// Remembered for the background transfer — see LastServedFile. Recorded AFTER the allow-list, so a
		// refused name can never become something a native player is later asked to open.
		var file = Path.Combine(_root!, name);
		_lastServed = file;
		return file;
	}

	public void Dispose()
	{
		_route?.Dispose();
		_interceptor?.Dispose();
	}
}
