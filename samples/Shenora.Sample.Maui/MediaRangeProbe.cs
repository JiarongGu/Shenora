using Shenora.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Serves media to a <c>&lt;video&gt;</c> element — **through <see cref="MediaWebViewRoute"/> from this
/// platform's media package**, which is the whole of the wiring an adopter writes.
/// <para>
/// The history is worth keeping, because it is the shape of the mistake: this file used to BE the
/// implementation. It proved on an Android emulator and an iOS simulator that a real file plays and seeks
/// (D44), and that proof was mistaken for the library existing. Then the range logic moved into
/// <c>Shenora.Media</c> and this called it directly — better, but it still meant every app repeating the
/// platform's body rule. Now the platform package supplies that, and what is left here is only what
/// genuinely belongs to an APP.
/// </para>
/// <para>
/// ⚠ Note what this class NO LONGER decides: whether to slice the response body. Android and iOS need
/// opposite answers, and an app has no business knowing which one it is running on — the reference in the
/// csproj settles it at compile time.
/// </para>
/// </summary>
internal sealed class MediaRangeProbe
{
	/// <summary>The clips this sample will serve, and therefore the ONLY things it will serve.</summary>
	private static readonly string[] Clips = ["clip-faststart.mp4", "clip-tailmoov.mp4"];

	/// <summary>
	/// The route: <c>video</c>, carrying an ENCODED PAYLOAD rather than a readable path —
	/// <c>video?&lt;base64url of JSON&gt;</c>.
	/// <para>
	/// One route with a payload rather than a scheme per media kind, for the same reason the kit's IPC is
	/// one transport with <c>module</c>+<c>type</c>: one registration, a new kind costs nothing, and the
	/// payload can carry a source, a container preference or a cache key instead of just a name.
	/// </para>
	/// <para>
	/// ⚠ The page writes it RELATIVE (<c>/video?…</c>), so it resolves to <c>app://0.0.0.1/video?…</c> on
	/// iOS — the app scheme, the only thing iOS intercepts — and <c>https://0.0.0.1/video?…</c> on Android,
	/// whose media pipeline refuses a non-standard scheme outright. A literal <c>app://video?…</c> is
	/// therefore right on one shell and broken on the other, which is why the route is matched by PATH here
	/// and the scheme is never asserted.
	/// </para>
	/// </summary>
	private const string RoutePath = "/video";

	private readonly Action<string> _log;
	private MediaServingOptions? _serving;

	public MediaRangeProbe(Action<string> log) => _log = log;

	/// <summary>
	/// Stage the clips out of the app package, then publish the serving options.
	/// <para>
	/// Published LAST, so a request arriving mid-copy is refused rather than reading a half-written file —
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

		// The app declares WHERE media may come from; the library enforces containment. No BodyMode here —
		// the platform package overrides it, and passing one would be a copy-pasted desktop setting quietly
		// breaking one of the two shells.
		_serving = new MediaServingOptions { AllowedRoots = [root] };
		_log($"media: ready ({MediaWebViewRoute.PlatformBodyMode} on this platform) — "
			+ "the page may load /video?<encoded payload>");
	}

	/// <summary>
	/// The seam handler, and this is the entire adopter-facing shape: hand the event to
	/// <see cref="MediaWebViewRoute.TryServe"/> with a resolver that reads YOUR url form.
	/// </summary>
	public void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
	{
		if (_serving is not { } serving) return;   // staging has not finished — not ours to answer yet

		var served = MediaWebViewRoute.TryServe(e, Resolve, "video/mp4", serving);
		if (served) _log($"media -> {e.Method} {e.Uri}  [{MediaWebViewRoute.PlatformBodyMode}]");
	}

	/// <summary>
	/// The app's URL shape, and the only media code an app really has to write.
	/// <para>
	/// Returning null means "not a media request", so the handler leaves the event completely alone.
	/// Whatever this DOES return is still authorised against the allowed roots by the library, so being
	/// generous here cannot widen what is reachable.
	/// </para>
	/// </summary>
	private string? Resolve(Uri uri)
	{
		// Match on a PARSED Uri, never a string prefix — and match the ROUTE, never the scheme. The
		// platform normalises `app://video?x` to `app://video/?x` (it inserts a `/` before the query), so a
		// StartsWith on the literal text misses every request while looking correct. And the same relative
		// url arrives as two different schemes on the two shells, so asserting one would break the other.
		//
		// `app://video?x` puts "video" in the HOST; `/video?x` puts it in the PATH. Accept both, because the
		// first is the sample's control for why the second is required.
		var isRoute = uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(uri.Host, "video", StringComparison.OrdinalIgnoreCase);
		if (!isRoute) return null;

		// The payload is the whole query: `?<base64url of JSON>`, no parameter name.
		var encoded = uri.Query.TrimStart('?');
		if (encoded.Length == 0) return null;

		string? name;
		try
		{
			var padded = encoded.Replace('-', '+').Replace('_', '/');
			padded += new string('=', (4 - padded.Length % 4) % 4);
			var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
			// ⚠ LOG WHAT IT DECODED TO. That is the price of an opaque payload: the response body cannot
			// explain a refusal (no exception text on the wire, ever), so the host log is the only place a
			// wrong payload can be diagnosed.
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

		// An app-level allow-list on top of the library's containment. This sample knows exactly two
		// filenames, so anything else is refused before a path is even formed.
		if (name is null || !Clips.Contains(name, StringComparer.Ordinal)) return null;

		return Path.Combine(_serving!.AllowedRoots[0], name);
	}
}
