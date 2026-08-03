using Shenora.Core;

namespace Shenora.Sample.Maui;

/// <summary>
/// DM1 — the one capability the whole media design rests on: answering an HTTP <c>Range</c> through
/// the mobile webview's resource seam with REAL response headers, so a <c>&lt;video&gt;</c> element
/// both plays and SEEKS. Everything else in the media backlog is contracts around this; until it works
/// on a device they are contracts around a capability the kit does not have.
/// <para>
/// It lives in the sample, not in a package, on purpose. The media packages come last (D40/D41) and
/// this exists to tell them what to be shaped like — the ONE thing being proven here is the transport.
/// </para>
/// <para>
/// ⚠ A note that corrects this repo's own record: the earlier finding was that the portable seam
/// "takes four arguments and there is no <c>ResponseHeaders</c>", which made a per-platform
/// implementation through <c>e.PlatformArgs</c> look mandatory. That read one overload as the whole
/// set. <c>Microsoft.Maui.Controls</c> 10.0.20 also has
/// <c>SetResponse(int, string, IReadOnlyDictionary&lt;string,string&gt;?, Stream?)</c> on BOTH mobile
/// TFMs — verified by compiling, and every header survives to the native response. <c>PlatformArgs</c>
/// (Android's settable native <c>WebResourceResponse</c>, iOS's <c>UrlSchemeTask</c>) turned out not to
/// be needed at all; it stays here behind <c>mode=native</c> as the documented escape hatch.
/// </para>
/// <para>
/// ⚠⚠ <b>THE RESULT, and it is the reason a per-platform media package is load-bearing rather than
/// tidy: the two shells need OPPOSITE BODIES for the same portable request.</b> Both were measured on a
/// device with an explicit <c>fetch</c>, not inferred from a player's behaviour.
/// </para>
/// <list type="bullet">
/// <item><b>Android</b> applies the <c>Range</c> START itself to whatever body it is handed, and ignores
/// the range end. So the handler must NOT slice — return the whole resource
/// (<c>mode=noskip</c>). Slice it and the offset lands twice: <c>bytes=4-11</c> came back as four bytes
/// of file bytes 8-11, and a player asking for a file's tail got an empty body and retried forever.</item>
/// <item><b>iOS</b> passes the body through verbatim — the same <c>noskip</c> response returned all
/// 474 744 bytes from offset 0 for every range asked. So the handler MUST slice, which is ordinary
/// correct HTTP (<c>mode=portable</c>): <c>bytes=4-11</c> then returns exactly eight bytes,
/// <c>"ftypisom"</c>.</item>
/// </list>
/// <para>
/// Everything ELSE is identical on both: the same relative URL, the same portable <c>SetResponse</c>,
/// the same 206 with <c>Content-Range</c> and <c>Accept-Ranges</c>. Only the body differs — which is
/// exactly the kind of divergence the portable contract has to hide.
/// </para>
/// </summary>
internal sealed class MediaRangeProbe
{
	/// <summary>
	/// The route this answers: <c>app://media/?src=&lt;name&gt;</c>.
	/// <para>
	/// The APP SCHEME, not an https virtual host — iOS intercepts only the app scheme, and an
	/// arbitrary https host goes to the real network there (proven on the simulator; it overturned
	/// the opposite recommendation). A ROUTE with a payload rather than a scheme per media kind, for
	/// the same reason the kit's IPC is one transport with <c>module</c>+<c>type</c>: a second kind
	/// costs nothing.
	/// </para>
	/// </summary>
	private const string RouteScheme = "app";

	private const string RouteHost = "media";

	/// <summary>
	/// The SECOND URL form the probe answers: <c>https://shenora.media/?src=…</c>, an https virtual
	/// host rather than the app scheme.
	/// <para>
	/// It is here because the two platforms disagree about which forms even reach the seam: an earlier
	/// device probe saw BOTH an https host and the app scheme intercepted on Android, and on iOS only
	/// the app scheme — an arbitrary https host went to the real network. So the app scheme is the one
	/// that works everywhere for INTERCEPTION. Whether it works everywhere for PLAYBACK is a different
	/// question, and this is how it gets asked.
	/// </para>
	/// </summary>
	private const string VirtualHost = "shenora.media";

	/// <summary>
	/// The THIRD URL form, and — on the Android evidence — the only one that can work on both shells:
	/// a reserved PATH on the page's OWN origin, reached by a relative URL so the page never names a
	/// scheme or a host at all.
	/// <para>
	/// It exists because the two platforms disagree in opposite directions. Android intercepts both
	/// forms but its MEDIA pipeline refuses <c>app://</c> outright
	/// (<c>MEDIA_ERR_SRC_NOT_SUPPORTED</c>, instantly, even for a 200 with a correct
	/// <c>Content-Type</c> — verified against a bundle control that played the same file); iOS
	/// intercepts ONLY <c>app://</c> and lets an arbitrary https host go to the real network. There is
	/// therefore no fixed scheme that works on both — but the PAGE'S OWN ORIGIN is intercepted and
	/// media-capable on both by construction, because it is what the platform already serves the bundle
	/// from. Relative, so it is `https://0.0.0.1/…` on Android and `app://0.0.0.1/…` on iOS without the
	/// page knowing which.
	/// </para>
	/// </summary>
	private const string OriginPath = "/shenora-media/";

	/// <summary>
	/// The clips shipped as <c>MauiAsset</c>s, and — because the PAGE supplies the name — the entire
	/// set of things this will serve.
	/// <para>
	/// That allow-list is the point, not a shortcut. A media URL whose payload is a path is exactly the
	/// shape <c>EmbeddedResourceProvider.ResolveContained</c> exists for: page-supplied, and the first
	/// version of the desktop equivalent had no containment at all. DM4 generalises it (an app declares
	/// which roots are servable); a probe can afford the strictest possible version, so it uses it.
	/// </para>
	/// </summary>
	private static readonly string[] Clips =
	[
		// moov FIRST. Plays even from a server that ignores Range entirely — the CONTROL.
		"clip-faststart.mp4",
		// moov LAST. The player cannot start without reading the tail, so this one plays ONLY if the
		// range answer is genuinely correct — the TEST. A pair, because "the video played" on its own
		// would not distinguish working ranges from a whole-file 200.
		"clip-tailmoov.mp4",
	];

	private readonly Action<string> _log;
	private string? _cacheRoot;

	public MediaRangeProbe(Action<string> log) => _log = log;

	/// <summary>
	/// Copy the clips out of the app package and into the cache directory, once.
	/// <para>
	/// Because a range answer needs a LENGTH and a SEEK, and an app-package asset gives neither
	/// portably — on Android it is an <c>AssetManager</c> stream. Copying is also what a real media app
	/// does with anything it did not already have on disk, so the handler below reads an ordinary file,
	/// which is the case the design has to serve.
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

			// The SAME logical name the page uses as `media/<clip>` relative to its own origin — one
			// file on disk, two consumers, so the bundle control cannot drift from what we serve.
			await using var source = await FileSystem.OpenAppPackageFileAsync($"wwwroot/media/{clip}");
			await using var target = File.Create(destination);
			await source.CopyToAsync(target);
			await target.FlushAsync();
			_log($"media: staged {clip} -> cache ({target.Length} bytes)");
		}

		// Published LAST, so a request arriving mid-copy is answered 404 rather than reading a
		// half-written file. Same ordering rule as UpdateStage's write-the-marker-last.
		_cacheRoot = root;
		_log("media: ready — the page may now load app://media/?src=<clip>");
	}

	/// <summary>
	/// The seam handler. Answers 200 / 206 / 416 / 404 with real headers, and LOGS every decision —
	/// the log is the evidence, because a player's own behaviour cannot say whether it got a header or
	/// guessed.
	/// </summary>
	public void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
	{
		// Match a PARSED Uri, never a string prefix. The platform normalises `app://media?src=x` to
		// `app://media/?src=x` — it inserts a `/` before the query — so a handler testing
		// `StartsWith("app://media?")` misses every request while looking correct.
		var isAppRoute = string.Equals(e.Uri.Scheme, RouteScheme, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(e.Uri.Host, RouteHost, StringComparison.OrdinalIgnoreCase);
		var isVirtualHost = string.Equals(e.Uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase);
		// A RESERVED path on whatever origin the page happens to be on. It must be reserved, because
		// this shadows the bundle: any real asset under the same prefix would become unreachable.
		var isOriginPath = e.Uri.AbsolutePath.StartsWith(OriginPath, StringComparison.OrdinalIgnoreCase);
		if (!isAppRoute && !isVirtualHost && !isOriginPath)
		{
			return;   // not ours — leave it entirely alone, including Handled.
		}

		var range = e.Headers.TryGetValue("Range", out var rangeHeader) ? rangeHeader : null;
		_log($"media <- {e.Method} {e.Uri}  Range: {range ?? "(none)"}");

		try
		{
			Answer(e, range);
		}
		catch (Exception ex)
		{
			// The response body NEVER carries exception text (ipc-contracts / P5.5 H3): page script can
			// read it, and a media handler's failure detail is the most likely of all of them to contain
			// a real filesystem path. The diagnosis goes here, to the host log, and nowhere else.
			_log($"media !! failed: {ex}");
			// The FAILURE path always answers portably. Whatever mode was asked for may be the thing
			// that just threw, so the fallback must not depend on it.
			Respond(e, WebViewResourceResponse.NotFound(), "text/plain", "portable");
		}
	}

	private const string ContentType = "video/mp4";

	private void Answer(WebViewWebResourceRequestedEventArgs e, string? rangeHeader)
	{
		var root = _cacheRoot;
		var name = e.QueryParameters.TryGetValue("src", out var requested) ? requested : null;
		// Which way of handing the answer to the webview to use. A query parameter rather than a
		// rebuild per attempt: the first device run answered 206 with correct headers and Android
		// still reported MEDIA_ERR_SRC_NOT_SUPPORTED, and there were three candidate causes. One
		// deploy that can run all of them is the difference between a bisect and a guessing game.
		var mode = e.QueryParameters.TryGetValue("mode", out var m) ? m : "portable";

		if (root is null || name is null || !Clips.Contains(name, StringComparer.Ordinal))
		{
			_log($"media -> 404 (src={name ?? "(absent)"}, ready={root is not null})");
			Respond(e, WebViewResourceResponse.NotFound(), "text/plain", mode);
			return;
		}

		var path = Path.Combine(root, name);
		var length = new FileInfo(path).Length;

		// `whole` deliberately ignores the Range and answers 200 with everything. It is the control
		// that separates "206 is the problem" from "the response never carried a usable Content-Type".
		if (mode == "whole" || !WebViewByteRange.TryParse(rangeHeader, length, out var range))
		{
			// No range, or one this deliberately declines (multi-range). Answering the whole resource
			// 200 is the correct reply to a Range a server chooses not to honour — and `Ok` is what
			// stamps `Accept-Ranges: bytes`, without which a player will not even ATTEMPT a seek.
			Respond(e, WebViewResourceResponse.Ok(ReadAll(path), ContentType), ContentType, mode,
				$"200 whole ({length} bytes)");
			return;
		}

		if (!range.IsSatisfiable(length))
		{
			// 416 carries `Content-Range: bytes */length` so the client learns the real size and can
			// retry — omitting it is what leaves a player retrying the same bad range forever.
			Respond(e, WebViewResourceResponse.RangeNotSatisfiable(length), ContentType, mode,
				$"416 (asked {rangeHeader})");
			return;
		}

		if (mode == "noskip")
		{
			// ⚠ THE ANDROID RULE, measured rather than guessed (see the class remarks): the platform
			// treats whatever body we return as the resource FROM OFFSET 0 and skips the Range START
			// off the front of it ITSELF — then ignores the range END and streams to EOF. Proven with
			// an explicit `fetch`: asking `bytes=4-11` of a 474744-byte file returned 474740 bytes
			// beginning "ftypisom", and `bytes=1000-1099` returned 473744.
			//
			// So a handler must NOT seek. If it does, the skip is applied a SECOND time: the same
			// `bytes=4-11` came back as 4 bytes of "isom" — file bytes 8-11, double-offset and short
			// by exactly the offset — and a media player asking for a file's tail got an empty body
			// and retried the identical range forever.
			//
			// The headers still describe a 206, because that is what tells the player ranges are
			// supported and what the total length is — and they describe what will REALLY be sent
			// (offset to EOF), not the range that was asked for, so Content-Length and Content-Range
			// stay consistent with each other instead of lying about a truncation that never happens.
			var toEof = new WebViewByteRange(range.From, length - 1);
			// Content-Length is the ONE header that must NOT come from the body we hand over: the body is
			// the whole file and the platform delivers `length - From` of it, so deriving the header from
			// the stream would advertise a size bigger than what arrives. Stated explicitly, and measured
			// — the page really did receive 473744 bytes for a `bytes=1000-` request on a 474744 file.
			Respond(e, WebViewResourceResponse.PartialContent(ReadAll(path), ContentType, toEof, length),
				ContentType, mode, $"206 UNSLICED body, headers say bytes {toEof.From}-{toEof.To}/{length}",
				contentLength: toEof.Length);
			return;
		}

		var slice = ReadSlice(path, range);
		Respond(e, WebViewResourceResponse.PartialContent(slice, ContentType, range, length), ContentType, mode,
			$"206 bytes {range.From}-{range.To}/{length}");
	}

	/// <summary>
	/// Hand the kit's portable response to the webview, and then SAY WHAT THE PLATFORM ACTUALLY BUILT.
	/// <para>
	/// The read-back is the point of this method. A player's own error tells you it refused the
	/// response, never why; MAUI's Android path returns whatever sits in <c>PlatformArgs.Response</c>
	/// after the event, so that object is the ground truth about what the portable call produced —
	/// above all whether a MIME type survived, since a native <c>WebResourceResponse</c> takes it as a
	/// constructor argument rather than reading it out of the header map.
	/// </para>
	/// </summary>
	private void Respond(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse response,
						 string contentType, string mode, string? note = null, long? contentLength = null)
	{
		var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase)
		{
			// Defaults to the body's own length, which is right whenever the body IS the response. The
			// one caller that overrides it is the Android unsliced path, where they differ on purpose.
			["Content-Length"] = (contentLength ?? response.Content.Length).ToString(),
		};

		if (mode == "native")
		{
			RespondNative(e, response, contentType, headers);
		}
		else
		{
			e.SetResponse(response.StatusCode, response.ReasonPhrase, headers, response.Content);
			e.Handled = true;
		}

		if (note is not null)
		{
			_log($"media -> {note} [{mode}]  {string.Join(", ", headers.Select(h => $"{h.Key}: {h.Value}"))}");
		}

		ReportPlatformResponse(e);
	}

	/// <summary>
	/// The <c>PlatformArgs</c> escape hatch — a native response built by hand, with everything the
	/// platform type takes as a first-class argument rather than as a header.
	/// </summary>
	private static void RespondNative(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse response,
									  string contentType, Dictionary<string, string> headers)
	{
#if ANDROID
		// Android takes mimeType and encoding SEPARATELY from the header map, which is exactly the
		// thing a header dictionary cannot express.
		e.PlatformArgs!.Response = new global::Android.Webkit.WebResourceResponse(
			contentType, "UTF-8", response.StatusCode, response.ReasonPhrase, headers, response.Content);
		e.Handled = true;
#elif IOS
		// iOS carries a full NSHTTPURLResponse — status line and all headers — through the scheme task.
		var task = e.PlatformArgs!.UrlSchemeTask;
		var keys = headers.Keys.Select(k => new global::Foundation.NSString(k)).ToArray();
		var values = headers.Values.Select(v => (global::Foundation.NSObject)new global::Foundation.NSString(v)).ToArray();
		// An explicit NSUrl, not the implicit Uri->NSUrl conversion: that operator is nullable and the
		// constructor's parameter is not, which is a real CS8604 on the iOS build (this repo runs at zero
		// warnings, and the sample's iOS face only compiles on the Mac — so it is the one place a warning
		// can hide from the Windows gate).
		task.DidReceiveResponse(new global::Foundation.NSHttpUrlResponse(
			new global::Foundation.NSUrl(e.Uri.AbsoluteUri), response.StatusCode, "HTTP/1.1",
			global::Foundation.NSDictionary.FromObjectsAndKeys(values, keys)));
		var buffer = new byte[response.Content.Length];
		response.Content.ReadExactly(buffer);
		task.DidReceiveData(global::Foundation.NSData.FromArray(buffer));
		task.DidFinish();
		e.Handled = true;
#endif
	}

	/// <summary>What the platform is really going to send, read back off the native object.</summary>
	private void ReportPlatformResponse(WebViewWebResourceRequestedEventArgs e)
	{
#if ANDROID
		try
		{
			var native = e.PlatformArgs?.Response;
			if (native is null) { _log("media    native: PlatformArgs.Response is NULL"); return; }
			var nativeHeaders = native.ResponseHeaders is null
				? "(null)"
				: string.Join(", ", native.ResponseHeaders.Select(h => $"{h.Key}: {h.Value}"));
			_log($"media    native: mime={native.MimeType ?? "(null)"} enc={native.Encoding ?? "(null)"} " +
				 $"status={native.StatusCode} reason={native.ReasonPhrase ?? "(null)"} hdrs=[{nativeHeaders}]");
		}
		catch (Exception ex)
		{
			_log($"media    native: could not be read back — {ex.GetType().Name}: {ex.Message}");
		}
#endif
	}

	// A MemoryStream, deliberately, and ONLY because these clips are ~464 KB each: it removes a
	// variable from the one question this probe exists to answer. A real implementation must stream —
	// the whole reason `WebViewResourceResponse.Content` is a Stream and not a byte[] is that the old
	// shape made a 4 GB file 4 GB of RAM. Whoever builds `Shenora.Media.{Platform}` must not copy this.
	private static MemoryStream ReadAll(string path) => new(File.ReadAllBytes(path), writable: false);

	private static MemoryStream ReadSlice(string path, WebViewByteRange range)
	{
		var buffer = new byte[range.Length];
		using var file = File.OpenRead(path);
		file.Seek(range.From, SeekOrigin.Begin);
		file.ReadExactly(buffer);
		return new MemoryStream(buffer, writable: false);
	}
}
