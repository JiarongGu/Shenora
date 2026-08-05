using Shenora.Core;
using Shenora.Mobile;

namespace Shenora.Sample.Maui;

/// <summary>
/// Serving local media to the page — and after the D45 re-layering, this is the whole of it: declare where
/// files may come from, say how your route maps to one, and let the shell's interceptor plus
/// <c>Shenora.Core</c>'s file middleware do the rest.
/// <para>
/// Worth keeping the history, because it is the shape of the mistake this file went through. It used to BE
/// the range implementation (proven on both devices — D44), then it called <c>Shenora.Media</c>'s range
/// server, then a media PLATFORM package. Each step was better and each still had an app repeating something
/// the kit should own. What is left now decides nothing about ranges, containment, content types or the
/// platform's body rule.
/// </para>
/// <para>
/// ⚠ Note what is NOT referenced here: <c>Shenora.Media</c>. Serving a file the platform can already decode
/// needs no media package at all — that is D45's "the interceptor without the media bundle should still load
/// video/image/audio". The media package is a further middleware, for the files this cannot serve.
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

	public MediaRangeProbe(Action<string> log) => _log = log;

	/// <summary>
	/// The shell's interceptor, once <see cref="PrepareAsync"/> has built it — so a second probe can add its
	/// own middleware to the SAME pipeline rather than constructing a second interceptor over one webview.
	/// (Two interceptors would mean two <c>WebResourceRequested</c> subscriptions, which is exactly the
	/// last-writer-wins hazard the desktop host's single-subscription comment warns about.)
	/// </summary>
	public IWebViewInterceptor? Interceptor => _interceptor;

	/// <summary>
	/// Stage the clips out of the app package, then wire the route.
	/// <para>
	/// The route is registered LAST, so a request arriving mid-copy finds no handler rather than a
	/// half-written file — the same write-the-marker-last ordering <c>UpdateStage</c> uses.
	/// </para>
	/// </summary>
	public async Task PrepareAsync(HybridWebView webView)
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

		_root = root;
		_interceptor = new MobileWebViewInterceptor(webView, _log);

		// THE WHOLE WIRING. No BodyMode, no content-type table, no range arithmetic, no containment check —
		// UseFiles reads the platform's delivery rule off the interceptor so it cannot be passed in wrong.
		_route = _interceptor.UseFiles(new WebViewFileOptions
		{
			AllowedRoots = [root],
			Resolve = Resolve,
		});

		_log($"media: ready ({_interceptor.RangeDelivery} on this platform) — the page may load /media?<payload>");
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
		return Path.Combine(_root!, name);
	}

	public void Dispose()
	{
		_route?.Dispose();
		_interceptor?.Dispose();
	}
}
