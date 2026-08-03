using Shenora.Core;
using Shenora.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Serves media to a <c>&lt;video&gt;</c> element on the mobile shells — **through
/// <see cref="MediaRangeServer"/>, not through its own code**.
/// <para>
/// This file used to BE the implementation: it proved on an Android emulator and an iOS simulator that a
/// real file plays and seeks through the webview's resource seam (D44). That proof stands, but a sample
/// cannot ship — so the range logic now lives in <c>Shenora.Media</c> and this is what an adopter's own
/// handler looks like: match a route, authorise the source, call the server, hand the result over. Roughly
/// twenty lines, and none of them decide anything about ranges.
/// </para>
/// <para>
/// What is left here is exactly what belongs to an APP: which route it answers, where its media lives, and
/// which <see cref="MediaBodyMode"/> its platform needs. That last one is the one asymmetry the library
/// cannot hide yet — until <c>Shenora.Media.Android</c>/<c>.iOS</c> exist, the app names it.
/// </para>
/// </summary>
internal sealed class MediaRangeProbe
{
	/// <summary>
	/// The route: <c>app://media/?src=…</c>, or the same path on the page's own origin.
	/// <para>
	/// ⚠ The origin-relative form is the one that works on BOTH shells, and that was measured: Android
	/// intercepts <c>app://</c> and then its media pipeline REFUSES it, while iOS intercepts only
	/// <c>app://</c>. A path on the page's own origin is intercepted and media-capable on both by
	/// construction, because it is what the platform already serves the bundle from.
	/// </para>
	/// </summary>
	private const string RouteScheme = "app";

	private const string RouteHost = "media";

	/// <summary>A RESERVED path on whatever origin the page is on. Reserved because it shadows the bundle.</summary>
	private const string OriginPath = "/shenora-media/";

	private static readonly string[] Clips = ["clip-faststart.mp4", "clip-tailmoov.mp4"];

	private readonly Action<string> _log;
	private MediaServingOptions? _serving;

	public MediaRangeProbe(Action<string> log) => _log = log;

	/// <summary>
	/// Stage the clips out of the app package, then publish the serving options.
	/// <para>
	/// Published LAST so a request arriving mid-copy is refused rather than reading a half-written file —
	/// the same write-the-marker-last ordering <c>UpdateStage</c> uses.
	/// </para>
	/// </summary>
	public async Task PrepareAsync()
	{
		var root = Path.Combine(FileSystem.CacheDirectory, "media");
		Directory.CreateDirectory(root);

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

		// The app declares WHERE media may be served from; the library enforces containment. One root here,
		// and nothing outside it is reachable however the page spells the path.
		_serving = new MediaServingOptions { AllowedRoots = [root] };
		_log("media: ready — the page may now load /shenora-media/?src=<clip>");
	}

	/// <summary>The seam handler. Matches the route, authorises, delegates, logs.</summary>
	public void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
	{
		// Match a PARSED Uri, never a string prefix: the platform normalises `app://media?x` to
		// `app://media/?x`, so a StartsWith test misses every request while looking correct.
		var isAppRoute = string.Equals(e.Uri.Scheme, RouteScheme, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(e.Uri.Host, RouteHost, StringComparison.OrdinalIgnoreCase);
		var isOriginPath = e.Uri.AbsolutePath.StartsWith(OriginPath, StringComparison.OrdinalIgnoreCase);
		if (!isAppRoute && !isOriginPath) return;   // not ours — leave it entirely alone, including Handled

		var range = e.Headers.TryGetValue("Range", out var header) ? header : null;
		_log($"media <- {e.Method} {e.Uri}  Range: {range ?? "(none)"}");

		try
		{
			Answer(e, range);
		}
		catch (Exception ex)
		{
			// The diagnosis goes to the HOST log and never into the response: page script can read a body,
			// and a media failure is the likeliest of all of them to carry a real filesystem path.
			_log($"media !! failed: {ex}");
			Send(e, WebViewResourceResponse.NotFound(), "500 -> fixed 404");
		}
	}

	private void Answer(WebViewWebResourceRequestedEventArgs e, string? range)
	{
		if (_serving is not { } serving)
		{
			Send(e, WebViewResourceResponse.NotFound(), "404 (staging has not finished)");
			return;
		}

		var name = e.QueryParameters.TryGetValue("src", out var requested) ? requested : null;
		if (name is null || !Clips.Contains(name, StringComparer.Ordinal))
		{
			// An app-level allow-list ON TOP of the library's containment. Belt and braces: this sample knows
			// exactly two filenames, so anything else is refused before a path is even formed.
			Send(e, WebViewResourceResponse.NotFound(), $"404 (unknown src {name ?? "(absent)"})");
			return;
		}

		// The APP chooses the body rule for its platform — the one thing the portable library cannot decide
		// yet (D44). `mode` is a query parameter purely so one build can demonstrate both.
		var mode = e.QueryParameters.TryGetValue("mode", out var m) && m == "unsliced"
			? MediaBodyMode.Unsliced
			: MediaBodyMode.Sliced;

		var path = MediaAccess.ResolveLocal(Path.Combine(serving.AllowedRoots[0], name), serving);
		if (path is null)
		{
			Send(e, WebViewResourceResponse.NotFound(), "404 (refused by containment)");
			return;
		}

		// Everything about ranges — 200 / 206 / 416, Content-Range, Accept-Ranges, Content-Length, and the
		// per-platform body rule — is the library's from here.
		var request = new WebViewResourceRequest { Uri = e.Uri, Method = e.Method, Headers = e.Headers };
		var response = MediaRangeServer.Serve(request, path, "video/mp4", serving with { BodyMode = mode });

		Send(e, response, $"{response.StatusCode} [{mode}]");
	}

	/// <summary>
	/// Hand the kit's portable response to MAUI's portable seam.
	/// <para>
	/// ⚠ <c>SetResponse</c> with a HEADER DICTIONARY — the overload an earlier version of this repo believed
	/// did not exist, which is why <c>e.PlatformArgs</c> looked mandatory. It is not: every header survives
	/// to the native response on both mobile platforms (D44).
	/// </para>
	/// </summary>
	private void Send(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse response, string note)
	{
		e.SetResponse(response.StatusCode, response.ReasonPhrase, response.Headers, response.Content);
		e.Handled = true;
		_log($"media -> {note}  {string.Join(", ", response.Headers.Select(h => $"{h.Key}: {h.Value}"))}");
	}
}
