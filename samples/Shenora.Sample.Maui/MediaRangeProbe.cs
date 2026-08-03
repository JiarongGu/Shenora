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

	/// <summary>A RESERVED path on whatever origin the page is on. Reserved because it shadows the bundle.</summary>
	private const string OriginPath = "/shenora-media/";

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
			+ "the page may load /shenora-media/?src=<clip>");
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
		// Match on a PARSED Uri, never a string prefix: the platform normalises `app://media?x` to
		// `app://media/?x`, so a StartsWith test misses every request while looking correct.
		var isOriginPath = uri.AbsolutePath.StartsWith(OriginPath, StringComparison.OrdinalIgnoreCase);
		var isAppRoute = string.Equals(uri.Scheme, "app", StringComparison.OrdinalIgnoreCase)
			&& string.Equals(uri.Host, "media", StringComparison.OrdinalIgnoreCase);
		if (!isOriginPath && !isAppRoute) return null;

		var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
		var name = query["src"];
		// An app-level allow-list on top of the library's containment. This sample knows exactly two
		// filenames, so anything else is refused before a path is even formed.
		if (name is null || !Clips.Contains(name, StringComparer.Ordinal)) return null;

		return Path.Combine(_serving!.AllowedRoots[0], name);
	}
}
