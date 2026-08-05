using System.Text;
using System.Text.Json;
using Shenora.Core;

namespace Shenora.Sample.Maui;

/// <summary>
/// Two seam tests that can only be asked of a REAL page in a real webview, both filed by the first adopter
/// against 0.9.1 and both invisible to every gate the repo had:
/// <list type="number">
/// <item>what headers the page actually RECEIVES from a route (they were arriving duplicated), and</item>
/// <item>whether a top-level NAVIGATION still works once a route is registered.</item>
/// </list>
/// <para>
/// ⚠ <b>Why neither showed up before.</b> <see cref="MediaRangeProbe"/> serves media into an already-loaded
/// page and never reloads, and it asserted the BODY it got back rather than the headers that came with it. So
/// the sample exercised the one path where both defects are silent. That is the reusable lesson: a probe that
/// only checks the payload cannot see the envelope, and a probe that never navigates cannot see navigation.
/// </para>
/// <para>
/// Everything here reports as TEXT through the device log (<c>mobile-shells.md</c>: a screenshot cannot
/// report a header, and a failed page renders as a picture of an error rather than a value).
/// </para>
/// </summary>
internal static class PageProbe
{
	/// <summary>How long to wait for an in-page async result before calling it a failure.</summary>
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// A JS global the page never defines, used as the hand-off slot for an async result.
	/// <c>EvaluateJavaScriptAsync</c> does not await promises, so the pattern is: start the work, park the
	/// answer here, poll for it.
	/// </summary>
	private const string Slot = "__shenoraProbe";

	/// <summary>
	/// A global set immediately BEFORE a reload. A real navigation destroys the JS context, so finding it
	/// still set afterwards means the document never went away — see <see cref="CheckReloadAsync"/>.
	/// </summary>
	private const string StaleMark = "__shenoraPreReload";

	/// <summary>The diagnostic route's path — its own, so it cannot be confused with the media route.</summary>
	private const string HeaderRoutePath = "/hdr-probe";

	/// <summary>The body every diagnostic variant returns. Short, fixed, and its length is the assertion.</summary>
	private static readonly byte[] ProbeBody = Encoding.ASCII.GetBytes("SHENORA-HEADER-PROBE-BODY-32-CHR");

	/// <summary>
	/// A route that answers with DELIBERATELY controlled headers, so the page's view of them can be
	/// attributed. Without it, "the page saw two content-types" cannot distinguish the kit setting one twice
	/// from the platform adding its own on top of ours.
	/// <para>
	/// Four variants, selected by <c>?v=</c>, differing only in which of the two suspect headers the kit
	/// supplies: <c>full</c> (both, what <c>UseFiles</c> does), <c>type</c>, <c>length</c>, <c>bare</c>
	/// (neither). Whatever appears under <c>bare</c> is the PLATFORM's, by elimination.
	/// </para>
	/// </summary>
	/// <returns>Dispose to remove the route.</returns>
	public static IDisposable RegisterHeaderRoute(IWebViewInterceptor interceptor, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(interceptor);

		return interceptor.Use((request, next, ct) =>
		{
			if (!request.Uri.AbsolutePath.StartsWith(HeaderRoutePath, StringComparison.OrdinalIgnoreCase))
				return next(request, ct);

			var variant = request.Uri.Query.TrimStart('?').Replace("v=", "", StringComparison.Ordinal);
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				// Always present, and it is the control: if THIS one ever arrives duplicated then the
				// platform copies every header, rather than only re-deriving the two well-known ones.
				["X-Shenora-Probe"] = variant,
			};
			if (variant is "full" or "type") headers["Content-Type"] = "application/x-shenora-probe";
			if (variant is "full" or "length")
				headers["Content-Length"] = ProbeBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

			log($"[NAV] hdr-probe serving variant '{variant}' with kit headers: {string.Join("; ", headers.Select(h => h.Key + '=' + h.Value))}");

			return Task.FromResult<WebViewResourceResponse?>(new WebViewResourceResponse
			{
				Content = new MemoryStream(ProbeBody, writable: false),
				StatusCode = 200,
				ReasonPhrase = "OK",
				Headers = headers,
			});
		});
	}

	/// <summary>
	/// <b>Probe 1 — the response the page really sees</b>, for each header variant.
	/// <para>
	/// Reporting headers in ORDER matters: the defect is a DUPLICATE, and a fetch that read
	/// <c>headers.get('content-length')</c> would collapse <c>0, 474744</c> to one value and see nothing
	/// wrong. So the verdict is built from <c>Headers.forEach</c>, where a repeated key arrives as one entry
	/// whose value contains a comma.
	/// </para>
	/// </summary>
	public static async Task<string> CheckResponseHeadersAsync(HybridWebView webView, Action<string> log)
	{
		var findings = new List<string>();

		foreach (var variant in new[] { "full", "type", "length", "bare" })
		{
			var seen = await FetchHeadersAsync(webView, $"{HeaderRoutePath}?v={variant}").ConfigureAwait(false);
			if (seen is null)
			{
				findings.Add($"{variant}: NO ANSWER");
				continue;
			}

			log($"[NAV] variant {variant} -> {seen}");
			var duplicated = seen
				.Split('|')[^1]
				.Split("; ", StringSplitOptions.RemoveEmptyEntries)
				.Where(pair => pair.Contains(", ", StringComparison.Ordinal))
				// ⚠ content-type is the ONE known-unavoidable duplicate and is exempted BY NAME, not by
				// loosening the check. MAUI's Android path derives the native mime type from our dictionary
				// and then passes the dictionary through too, and there is no overload that takes a content
				// type alongside headers — so the alternative is octet-stream, which no <video> will play.
				// Both values are identical, so nothing can be misled about the type. Everything else must
				// arrive once; content-LENGTH especially, where the two values differ and the first is 0.
				.Where(pair => !pair.StartsWith("content-type=", StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (duplicated.Length > 0) findings.Add($"{variant}: DUPLICATED {string.Join(" / ", duplicated)}");
		}

		return findings.Count == 0
			? "HEADERS: PASS — nothing duplicated except the platform's own content-type (see PlatformHeaders)"
			: $"HEADERS: FAIL — {string.Join("  |  ", findings)}";
	}

	/// <summary>
	/// <b>Probe 3 — D44's evidence, automated.</b> Loads each staged clip into the page's <c>&lt;video&gt;</c>
	/// and asserts the two things that only work when ranges are answered correctly: the duration RESOLVES,
	/// and a seek LANDS.
	/// <para>
	/// ⚠ <b>Both clips, and the second one is the whole point.</b> <c>clip-tailmoov.mp4</c> keeps its index at
	/// the END of the file, so it cannot open at all unless a tail range is answered properly — while
	/// <c>clip-faststart.mp4</c> plays perfectly even when the range handling is wrong. A gate that only ran
	/// the faststart clip would be green for every version of the bug D44 exists to prevent.
	/// </para>
	/// <para>
	/// This became a gate on 2026-08-05, when the mobile interceptor started dropping <c>Content-Length</c> on
	/// Android: that is a change to what a media pipeline receives, and D44's evidence had until then been a
	/// human reading log lines.
	/// </para>
	/// </summary>
	public static async Task<string> CheckMediaAsync(HybridWebView webView, Action<string> log)
	{
		var failures = new List<string>();

		foreach (var clip in new[] { "clip-faststart.mp4", "clip-tailmoov.mp4" })
		{
			var started = await EvaluateAsync(webView, $$"""
				(function(){
					var v = document.getElementById('vid');
					if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
					window.{{Slot}} = 'pending';
					v.onerror = function () {
						window.{{Slot}} = 'MEDIA-ERROR code=' + (v.error ? v.error.code : '?');
					};
					v.onloadedmetadata = function () { try { v.currentTime = 48; } catch (e) {
						window.{{Slot}} = 'SEEK-THREW ' + e;
					} };
					v.onseeked = function () {
						window.{{Slot}} = 'duration=' + v.duration.toFixed(2) + '|seeked=' + v.currentTime.toFixed(2);
					};
					v.src = '{{MediaUrl(clip)}}';
					v.load();
					return 'started';
				})()
				""").ConfigureAwait(false);

			if (started is null) { failures.Add($"{clip}: could not evaluate"); continue; }

			var result = await PollAsync(webView).ConfigureAwait(false);
			log($"[NAV] media {clip} -> {result ?? "NO ANSWER"}");

			if (result is null) { failures.Add($"{clip}: no answer within {Timeout.TotalSeconds:0}s"); continue; }
			if (!result.StartsWith("duration=", StringComparison.Ordinal)) { failures.Add($"{clip}: {result}"); continue; }
			// The clips are 60 s and the seek target is 48 s. Asserting the seek LANDED (not merely that the
			// event fired) is what distinguishes a served tail range from a player that gave up and clamped.
			if (!result.Contains("seeked=48", StringComparison.Ordinal))
				failures.Add($"{clip}: seek did not land — {result}");
		}

		return failures.Count == 0
			? "MEDIA: PASS — both clips resolved a duration and seeked to 48 s"
			: $"MEDIA: FAIL — {string.Join("  |  ", failures)}";
	}

	/// <summary>Fetch a url from inside the page and report status, byte count and every header, in order.</summary>
	private static async Task<string?> FetchHeadersAsync(HybridWebView webView, string url)
	{
		var started = await EvaluateAsync(webView, $$"""
			(function(){
				window.{{Slot}} = 'pending';
				fetch('{{url}}')
					.then(async function (r) {
						var pairs = [];
						r.headers.forEach(function (v, k) { pairs.push(k + '=' + v); });
						var body = await r.arrayBuffer();
						window.{{Slot}} = 'status=' + r.status + '|bytes=' + body.byteLength + '|' + pairs.join('; ');
					})
					.catch(function (e) { window.{{Slot}} = 'FETCH-THREW ' + e; });
				return 'started';
			})()
			""").ConfigureAwait(false);

		return started is null ? null : await PollAsync(webView).ConfigureAwait(false);
	}

	/// <summary>
	/// <b>Probe 2 — a top-level navigation with a route registered.</b> Reloads the document and asks the
	/// page what it became.
	/// <para>
	/// ⚠ <b>The assertion is that the page came BACK, and that it actually LEFT.</b> A failed main-frame
	/// navigation does not throw anywhere the host can see it — Chromium simply swaps in its own error
	/// document — so the only honest check reads the resulting DOM. And a check that merely finds a healthy
	/// document proves nothing on its own, because the PRE-navigation document is healthy too: the first
	/// version of this probe passed in 515 ms and might have been reading the page it never left. Hence
	/// <see cref="StaleMark"/>, a global that a real navigation destroys.
	/// </para>
	/// </summary>
	public static async Task<string> CheckReloadAsync(HybridWebView webView, Action<string> log)
	{
		var before = await EvaluateAsync(webView, Snapshot).ConfigureAwait(false);
		if (before is null) return "RELOAD: FAIL — could not read the page before reloading (nothing was proven)";
		log($"[NAV] before reload: {before}");

		// Stamp, then reload. Fire and forget by nature: the evaluation's own context is destroyed by the
		// navigation it starts, so a null return here means nothing either way.
		_ = await EvaluateAsync(webView,
			$"(function(){{ window.{StaleMark} = 1; location.reload(); return 'reloading'; }})()")
			.ConfigureAwait(false);

		// Poll rather than sleep once: a fixed delay is either flaky or slow, and this way the verdict can
		// say the page never came back rather than guessing.
		var deadline = DateTime.UtcNow + Timeout;
		string? after = null;
		while (DateTime.UtcNow < deadline)
		{
			await Task.Delay(500).ConfigureAwait(false);
			after = await EvaluateAsync(webView, Snapshot).ConfigureAwait(false);
			// BOTH conditions: a finished document AND one that is not the one we stamped.
			if (after is not null
				&& after.Contains("ready=complete", StringComparison.Ordinal)
				&& after.Contains("stamp=fresh", StringComparison.Ordinal)) break;
		}

		if (after is null) return "RELOAD: FAIL — the page stopped answering entirely after location.reload()";
		log($"[NAV] after reload: {after}");

		if (after.Contains("stamp=STALE", StringComparison.Ordinal))
			return $"RELOAD: INCONCLUSIVE — the document never navigated away within {Timeout.TotalSeconds:0}s, "
				+ $"so nothing about navigation was proven. after=[{after}]";

		// The bundle's own document has our title and a real DOM; Chromium's error page has neither.
		var recovered = after.Contains("ready=complete", StringComparison.Ordinal)
			&& !after.Contains("title=|", StringComparison.Ordinal)
			&& !after.Contains("ERR_", StringComparison.Ordinal);

		return recovered
			? $"RELOAD: PASS ({after})"
			: $"RELOAD: FAIL — a top-level navigation with a route registered did not restore the bundle. "
				+ $"before=[{before}] after=[{after}]";
	}

	/// <summary>
	/// What the page IS, as one line. Element count and body text are both here because an error document
	/// can be distinguished from ours by either, and printing both means a surprising verdict can be read
	/// rather than re-run.
	/// </summary>
	private static readonly string Snapshot = $$"""
		(function(){
			var b = document.body;
			return 'href=' + location.pathname
				+ '|ready=' + document.readyState
				+ '|stamp=' + (window.{{StaleMark}} ? 'STALE' : 'fresh')
				+ '|title=' + (document.title || '')
				+ '|nodes=' + document.querySelectorAll('*').length
				+ '|text=' + ((b && b.innerText) || '').replace(/\s+/g, ' ').slice(0, 60);
		})()
		""";

	/// <summary>Poll <see cref="Slot"/> until the async work parked an answer there.</summary>
	private static async Task<string?> PollAsync(HybridWebView webView)
	{
		var deadline = DateTime.UtcNow + Timeout;
		while (DateTime.UtcNow < deadline)
		{
			await Task.Delay(250).ConfigureAwait(false);
			var value = await EvaluateAsync(webView, $"window.{Slot}").ConfigureAwait(false);
			if (value is not null && value.Length > 0 && value != "pending" && value != "null") return value;
		}
		return null;
	}

	/// <summary>
	/// Evaluate on the UI thread and normalise the result.
	/// <para>
	/// ⚠ Two platform facts, both paid for once: the call MUST be marshalled (MAUI throws off the UI
	/// thread), and the result comes back JSON-ENCODED — a string arrives wrapped in quotes with its inner
	/// quotes escaped — so a raw comparison against an expected value silently never matches.
	/// </para>
	/// </summary>
	private static async Task<string?> EvaluateAsync(HybridWebView webView, string script)
	{
		try
		{
			var raw = await MainThread.InvokeOnMainThreadAsync(() => webView.EvaluateJavaScriptAsync(script))
				.ConfigureAwait(false);
			if (raw is null) return null;
			if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
			{
				raw = raw[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
								.Replace("\\n", " ", StringComparison.Ordinal)
								.Replace("\\\\", "\\", StringComparison.Ordinal);
			}
			return raw;
		}
		catch (Exception)
		{
			// A failed evaluation is a RESULT here, not an error to propagate: it is one of the ways a
			// broken navigation shows up, and the caller decides what it means.
			return null;
		}
	}

	/// <summary>
	/// The sabotage harness for <see cref="CheckReloadAsync"/>: claim the MAIN DOCUMENT and answer it with a
	/// 404, which is what a broken top-level navigation looks like from the page's side.
	/// <para>
	/// ⚠ Kept in the tree rather than applied and deleted, because this probe is the kind that is worthless
	/// unless it can be shown to fail. The FIRST attempt at sabotaging it did not work and looked like the
	/// probe passing: setting <c>Handled = true</c> without calling <c>SetResponse</c> leaves MAUI returning a
	/// null response, which Android reads as "not intercepted" and serves normally. A sabotage that does not
	/// break the thing under test proves nothing about the gate — read what the harness actually DID, not
	/// just the verdict it produced.
	/// </para>
	/// </summary>
	public static IDisposable SabotageMainDocument(IWebViewInterceptor interceptor, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(interceptor);
		return interceptor.Use((request, next, ct) =>
		{
			if (request.Uri.AbsolutePath is not ("/" or "" or "/index.html")) return next(request, ct);
			log($"[NAV] SABOTAGE: claiming the main document ({request.Uri.AbsolutePath}) with a 404");
			return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
		});
	}

	/// <summary>base64url of UTF-8, the encoding <c>mediaUrl()</c> emits and <see cref="MediaRangeProbe"/> decodes.</summary>
	public static string Base64Url(string json) =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');

	/// <summary>The media url this sample serves, for a probe that wants a REAL file rather than a fixture.</summary>
	public static string MediaUrl(string clip) => $"/media?{Base64Url(JsonSerializer.Serialize(new { src = clip }))}";
}
