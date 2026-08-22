using System.Text;
using System.Text.Json;
using Shenora;
using Shenora.Modules.Media;
using Shenora.Core.WebView;

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
	/// How long to wait between attempts after a <c>503</c> — the interval the kit's own not-ready answer
	/// advertises in <c>Retry-After</c> (<c>MediaConversionExtensions.NotReadyYet</c>).
	/// <para>
	/// ⚠ <b>The INTERVAL is shared; the retry LOOPS deliberately are not.</b> Their attempt budgets differ
	/// for a real reason — a metadata walk and a transcode are different waits — so
	/// <see cref="ConversionRouteProbe"/> and <see cref="RemuxRouteProbe"/> keep their own loops. What was
	/// worth sharing is the number, which had been two <c>TimeSpan.FromSeconds(1)</c> literals joined only
	/// by prose: a page tuned to one interval polling a route that advertises another is a contract that can
	/// drift while every test still passes. (The kit side already had exactly one owner.)
	/// </para>
	/// <para>
	/// ⚠ A real page should read <c>Retry-After</c> off the response rather than hardcode this — it is plain
	/// HTTP the page can already do, so the kit deliberately publishes no constant for it (D54). These probes
	/// read a flattened text report rather than a response object, which is why they cannot.
	/// </para>
	/// </summary>
	internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

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
			// 🔴 THIS ASSERTS A DECODED PICTURE AND ADVANCING TIME, not just metadata — because
			// "duration resolved and the seek landed" is a claim about BYTES AND RANGES, and a <video> can
			// satisfy both while never decoding a single frame. That gap shipped: the probe reported
			// `MEDIA: PASS` on a device where the owner could not play a video (2026-08-07). It is the same
			// mistake `ISegmentEngine` already names one layer down — "has a video stream" is the wrong
			// test, because a stream can be declared and carry no picture.
			//
			// The three signals, and each catches something the others do not:
			//   · videoWidth/videoHeight  — non-zero only once the DECODER has produced a frame's geometry.
			//                               An unsupported codec leaves them 0 with metadata intact.
			//   · readyState >= 2         — HAVE_CURRENT_DATA: there is decoded data AT the playhead.
			//   · currentTime ADVANCES    — the only proof it is actually playing rather than merely ready.
			//
			// ⚠ muted + playsInline are required, not cosmetic: iOS refuses programmatic play() without a
			// user gesture unless the element is muted and inline, and the refusal looks exactly like a
			// decode failure.
			var started = await EvaluateAsync(webView, $$"""
				(function(){
					var v = document.getElementById('vid');
					if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
					window.{{Slot}} = 'pending';
					v.muted = true; v.playsInline = true; v.setAttribute('playsinline','');
					v.onerror = function () {
						window.{{Slot}} = 'MEDIA-ERROR code=' + (v.error ? v.error.code : '?');
					};
					v.onloadedmetadata = function () { try { v.currentTime = 48; } catch (e) {
						window.{{Slot}} = 'SEEK-THREW ' + e;
					} };
					v.onseeked = function () {
						var at = v.currentTime;
						var p = v.play();
						if (p && p.catch) { p.catch(function (e) { window.{{Slot}} = 'PLAY-REJECTED ' + e; }); }
						setTimeout(function () {
							window.{{Slot}} = 'duration=' + v.duration.toFixed(2)
								+ '|seeked=' + at.toFixed(2)
								+ '|size=' + v.videoWidth + 'x' + v.videoHeight
								+ '|ready=' + v.readyState
								+ '|advanced=' + (v.currentTime - at).toFixed(2);
						}, 1200);
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

			// A picture, and time moving. Both are reported in the same line either way, so a failure says
			// WHICH half is missing rather than only that something is wrong.
			if (result.Contains("|size=0x0", StringComparison.Ordinal))
				failures.Add($"{clip}: NO DECODED PICTURE (size=0x0) — bytes served, nothing decoded — {result}");
			else if (Advanced(result) <= 0)
				failures.Add($"{clip}: decoded but NOT PLAYING (time did not advance) — {result}");
		}

		return failures.Count == 0
			? "MEDIA: PASS — both clips decoded a picture, seeked to 48 s, and played on"
			: $"MEDIA: FAIL — {string.Join("  |  ", failures)}";
	}

	/// <summary>How far the playhead moved during the probe's watch window. Negative/absent reads as zero.</summary>
	private static double Advanced(string result)
	{
		var at = result.IndexOf("|advanced=", StringComparison.Ordinal);
		if (at < 0) return 0;
		var value = result[(at + "|advanced=".Length)..];
		var end = value.IndexOf('|');
		if (end >= 0) value = value[..end];
		return double.TryParse(value, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
	}

	/// <summary>
	/// Drive the page's OWN play button and report what the page saw.
	///
	/// <para>
	/// 🔴 <b>The synthetic probe and the USER's path are different code, and only one of them was ever
	/// tested.</b> <see cref="CheckMediaAsync"/> sets <c>src</c> itself, mutes the element and plays it —
	/// and passed on a device where the owner reported the clips would not play (2026-08-07). The page's
	/// button does something else: it sets <c>src</c>, calls <c>load()</c>, then <c>play()</c> UNMUTED, and
	/// writes any rejection to a <c>&lt;div&gt;</c> nobody outside the phone can read.
	/// </para>
	/// <para>
	/// So this clicks the real button and lifts the page's own log into the DEVICE log, where a harness can
	/// read it.
	/// </para>
	/// <para>
	/// 🔴 <b>IT CANNOT PRODUCE A TRUSTED GESTURE, AND IT NOW SAYS SO INSTEAD OF FAILING.</b> A scripted
	/// <c>click()</c> is <c>isTrusted:false</c> and grants NO user activation — measured over CDP on
	/// WebView 133: <c>userActivation.isActive</c> reads <c>false</c> both before and after
	/// <c>b.click()</c>. So Chromium refuses the page's UNMUTED <c>play()</c>, exactly as specified, and
	/// this probe reported <c>UI-PLAY: FAIL</c> for a platform behaving correctly — which was filed as a
	/// shell divergence and cost a task entry.
	/// <b>A real user is unaffected:</b> driven with a genuine touch (<c>adb shell input tap</c>) and with
	/// trusted CDP input, the same button plays the same clip unmuted on the same build.
	/// </para>
	/// <para>
	/// ⚠ So a refusal with no activation is <b>INCONCLUSIVE, never FAIL</b> — the same rule
	/// <c>CodecProbe</c> already earned on a device: <i>a failed query must never be indistinguishable
	/// from a negative result.</i> The DECODE half is still asserted, so a clip that stops decoding still
	/// fails here; only the "did it start" half is unanswerable from an injected script.
	/// </para>
	/// <para>
	/// 🔴 <b>Two rules the script below obeys, both learned by breaking them here:</b> it carries NO
	/// <c>//</c> comment and NO backslash. <see cref="Safe"/> flattens the whole script onto ONE line before
	/// evaluating (WKWebView rejects multi-line), so a single <c>//</c> comments out everything after it and
	/// the probe reports "could not evaluate"; and iOS strips backslashes on the way in, so a regex like
	/// <c>/\s+/g</c> silently becomes <c>/s+/g</c>. Explanations belong out here, in C#.
	/// </para>
	/// <para>
	/// It also clears the earlier probe's handlers first: <see cref="CheckMediaAsync"/> assigns
	/// <c>onloadedmetadata</c>/<c>onseeked</c>/<c>onerror</c> on this same element and never removes them,
	/// so loading a clip re-fires ITS handlers, which overwrite the result slot with their own format —
	/// this probe then reports the previous probe's answer, in a well-formed line with plausible numbers.
	/// </para>
	/// </summary>
	public static async Task<string> CheckUiPlaybackAsync(HybridWebView webView, Action<string> log)
	{
		var started = await EvaluateAsync(webView, $$"""
			(function(){
				var b = document.getElementById('mfast');
				var v = document.getElementById('vid');
				if (!b || !v) { window.{{Slot}} = 'NO-BUTTON-OR-VIDEO'; return 'missing'; }
				v.onloadedmetadata = null; v.onseeked = null; v.onerror = null;
				try { v.pause(); } catch (e) {}
				v.removeAttribute('src'); v.load();
				v.muted = false;
				window.{{Slot}} = 'pending';
				b.click();
				var act = navigator.userActivation ? String(navigator.userActivation.isActive) : 'unsupported';
				setTimeout(function () {
					var el = document.getElementById('log');
					var raw = el ? (el.innerText || '') : '(no log element)';
					var tail = raw.slice(-700).split(String.fromCharCode(10)).join(' ')
						.split(String.fromCharCode(9)).join(' ');
					window.{{Slot}} = 'src=' + (v.currentSrc ? 'set' : 'EMPTY')
						+ '|err=' + (v.error ? v.error.code : '-')
						+ '|size=' + v.videoWidth + 'x' + v.videoHeight
						+ '|ready=' + v.readyState + '|paused=' + v.paused
						+ '|t=' + v.currentTime.toFixed(2)
						+ '|activation=' + act
						+ '|PAGELOG ' + tail;
				}, 2500);
				return 'clicked';
			})()
			""").ConfigureAwait(false);

		if (started is null) return "UI-PLAY: FAIL — could not evaluate";
		var result = await PollAsync(webView).ConfigureAwait(false);
		if (result is null) return "UI-PLAY: FAIL — no answer";

		log($"[NAV] ui-play -> {result}");
		var played = result.Contains("|paused=false", StringComparison.OrdinalIgnoreCase);
		var decoded = !result.Contains("|size=0x0", StringComparison.Ordinal);
		// `false` from a click this script issued itself: the platform never granted user activation, so a
		// refusal below is the autoplay policy working, not the shell failing.
		var noGesture = result.Contains("|activation=false", StringComparison.OrdinalIgnoreCase);

		if (played && decoded) return "UI-PLAY: PASS — the page's own button decoded and started playback";

		// ⚠ ORDER MATTERS: a clip that did not decode is a real fault whatever the gesture story, so that
		// case must be tested FIRST. Reversing these would hide a genuine media failure behind
		// "inconclusive" on every Android run — a probe that can no longer fail.
		if (!decoded || result.Contains("|err=", StringComparison.Ordinal)
			&& !result.Contains("|err=-", StringComparison.Ordinal))
			return $"UI-PLAY: FAIL — the clip did not decode: {result}";

		if (noGesture)
			return "UI-PLAY: INCONCLUSIVE — decoded, but playback needs a TRUSTED gesture this harness "
				+ $"cannot produce (an injected click() grants no user activation): {result}";

		return $"UI-PLAY: FAIL — {result}";
	}

	private const string AppPipelineRoutePath = "/app-pipeline-probe";
	private static readonly byte[] AppPipelineBody = Encoding.ASCII.GetBytes("SHENORA-APP-PIPELINE-REACHED");

	/// <summary>
	/// Declare a route through the APPLICATION's pipeline — <c>app.Use(…)</c>, D64 — rather than on one
	/// interceptor.
	/// <para>
	/// 🔴 <b>This is the mobile half of the pipeline surface, and until 2026-08-09 nothing had ever exercised
	/// it.</b> The mechanism compiled and `MobileWebViewInterceptor` took the pipeline as a required
	/// argument, but the sample handed it a FRESH one — so every `app.Use(…)` step reached zero webviews on
	/// Android and iOS, and the mobile API baselines are name-level so they could not see it either. That is
	/// D63's defect class exactly: a declared seam nothing consults, where ABSENT and WORKING look the same.
	/// </para>
	/// <para>
	/// ⚠ Must be called BEFORE the first webview is built — the pipeline FREEZES on first application, by
	/// design, so a step added later throws rather than silently serving some windows and not others.
	/// </para>
	/// </summary>
	public static void RegisterAppPipelineRoute(WebViewPipeline pipeline, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(pipeline);

		// 🔴 DOCUMENT WATCH — the measurement the iOS fragment-reload decision has been waiting for.
		// The open question is whether a reload at `/#/route` even ASKS the shell on iOS: the failure is
		// that no second document appears, and WKWebView keeps the previous page on screen, so nothing
		// distinguishes "the platform never requested it" from "it requested it and discarded the answer".
		// Nothing in the kit logs per-request, so this says so from the app side. Cheap, and it passes
		// every request straight through — it decides nothing.
		pipeline.Use(interceptor => interceptor.Use((request, next, ct) =>
		{
			var uri = request.Uri;
			if (uri.IsAbsoluteUri && (uri.Fragment.Length > 0 || uri.AbsolutePath is "/" or ""))
				log($"[DOC] request uri='{uri}' path='{uri.AbsolutePath}' frag='{uri.Fragment}'");
			return next(request, ct);
		}));

		// ⚠ The app deliberately does NOT answer the fragment document here any more. It did while that was
		// a hypothesis (2026-08-09), the experiment passed, and the repair moved into `Shenora.Mobile` where
		// every adopter gets it — so leaving the app's copy would mask the shell's and test nothing.

		pipeline.Use(interceptor => interceptor.Use((request, next, ct) =>
		{
			if (!request.Uri.AbsolutePath.StartsWith(AppPipelineRoutePath, StringComparison.OrdinalIgnoreCase))
				return next(request, ct);

			log("[PIPE] app-pipeline route serving — the step reached this webview");
			return Task.FromResult<WebViewResourceResponse?>(new WebViewResourceResponse
			{
				Content = new MemoryStream(AppPipelineBody, writable: false),
				StatusCode = 200,
				ReasonPhrase = "OK",
				Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Content-Type"] = "text/plain",
				},
			});
		}));
	}

	/// <summary>
	/// Ask the PAGE for the app-pipeline route, so the verdict is about a real request through the real
	/// webview rather than about the object graph.
	/// </summary>
	public static async Task<string> CheckAppPipelineAsync(HybridWebView webView, Action<string> log)
	{
		var seen = await FetchHeadersAsync(webView, AppPipelineRoutePath).ConfigureAwait(false);
		if (seen is null) return "APP PIPELINE: FAIL — the page never answered (timed out)";

		log($"[PIPE] {seen}");
		// The BYTE COUNT is the assertion, not the status. A 200 could come from anywhere — the page's own
		// bundle, a platform fallback — whereas this exact length only exists if OUR step ran.
		return seen.Contains($"bytes={AppPipelineBody.Length}", StringComparison.Ordinal)
			? "APP PIPELINE: PASS — an app.Use(…) step served a real request through the mobile webview"
			: $"APP PIPELINE: FAIL — the route did not answer with the step's body  [raw: {seen}]";
	}

	/// <summary>
	/// Start the page's AUDIO element, so the app has audio playing when someone backgrounds it.
	/// <para>
	/// 🔴 <b>The element type is the whole point.</b> iOS pauses a backgrounded <c>&lt;video&gt;</c> by design —
	/// the video track cannot render — so a background test driven from the video element measures that rule
	/// and says nothing about whether the host is configured correctly. An earlier run here reported a
	/// handoff that fired during the PROBE sequence and left the video paused, so by the time the app was
	/// actually backgrounded the handler returned early and measured nothing.
	/// </para>
	/// <para>
	/// After this, background the app and read the <c>audio t=</c> lines. On Android they STOP while it is
	/// away and the jump in <c>t</c> across the gap is the answer; on iOS they keep coming, throttled from
	/// 2 s to 3 s, and <c>t</c> advancing 1:1 with the timestamps is the answer.
	/// </para>
	/// <para>
	/// 🔴 <b>It LOOPS, because a single play of the 60 s clip gives this probe a CEILING it cannot report.</b>
	/// Without it, "survives 60 s" and "survives forever" produce the identical reading, and the difference
	/// is the whole question an adopter is asking. Looping is what let the same instrument read 319 s.
	/// (No earlier figure is known to have hit that ceiling — it was latent, not the cause of any of them.)
	/// </para>
	/// </summary>
	public static async Task<string> StartBackgroundAudioAsync(HybridWebView webView, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(log);

		// `loop` is set BEFORE the click: it is read at the END of a play, but setting it first means a run
		// backgrounded immediately still gets it. (Commentary lives out here — see Safe(): the script is
		// flattened to one line, so a `//` inside it swallows the rest of the program.)
		var started = await EvaluateAsync(webView, """
			(function(){
				var b = document.getElementById('maud');
				var a = document.getElementById('aud');
				if (!b || !a) return 'no-button';
				a.loop = true;
				b.click();
				return 'clicked';
			})()
			""").ConfigureAwait(false);

		if (started is null) return "BG AUDIO: FAIL — the page did not answer";
		await Task.Delay(2500).ConfigureAwait(false);

		var state = await EvaluateAsync(webView, """
			(function(){
				var a = document.getElementById('aud');
				return 'paused=' + a.paused + '|t=' + a.currentTime.toFixed(2) + '|err=' + (a.error ? a.error.code : '-');
			})()
			""").ConfigureAwait(false);

		log($"BG AUDIO: {started} -> {state}");
		return state is not null && state.Contains("paused=false", StringComparison.Ordinal)
			? "BG AUDIO: PLAYING — background the app now; `audio t=` lines stop, and the jump across the gap is the verdict"
			: $"BG AUDIO: NOT PLAYING — nothing to measure ({state})";
	}

	/// <summary>
	/// Fetch a url from the page and report just status and byte count — for a route whose HEADERS are not
	/// the subject, only whether real bytes came back.
	/// </summary>
	public static Task<string?> FetchConvertedAsync(HybridWebView webView, string url)
		=> FetchHeadersAsync(webView, url);

	/// <summary>
	/// 🔴 <b>PLAY a url in the page's own <c>&lt;video&gt;</c> and report whether a PICTURE came out.</b>
	/// <para>
	/// ⚠ <b>This exists because <c>fetch</c> is not playback, and the conversion route was verified only by
	/// fetch.</b> `CONVERT: PASS` asserted status 200 and a non-zero byte count — which a container holding
	/// no video track satisfies perfectly. That is the same gap that shipped once already: `MEDIA: PASS` on
	/// a device where the owner could not play a video (2026-08-07), which is why
	/// <see cref="CheckMediaAsync"/> asserts a decoded picture. The CONVERTED output deserves the same bar,
	/// and it matters more there: a remux drops any stream it cannot carry, so audio-only output is the
	/// route's most likely wrong answer.
	/// </para>
	/// <para>
	/// ⚠ <c>size=0x0</c> IS THE ONLY SIGNAL for an unsupported video codec — measured 2026-08-10: the
	/// element reports no error, reaches <c>readyState=4</c>, and simply has no picture. So the assertion is
	/// the geometry, never the absence of an error.
	/// </para>
	/// <para>
	/// Muted, because muted playback needs no user gesture on any shell — the one autoplay fact that is
	/// uniform (see <see cref="CheckUiPlaybackAsync"/> for the unmuted story).
	/// </para>
	/// </summary>
	public static async Task<string?> PlayUrlAsync(HybridWebView webView, string url)
	{
		var started = await EvaluateAsync(webView, $$"""
			(function(){
				var v = document.getElementById('vid');
				if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
				v.onloadedmetadata = null; v.onseeked = null; v.onerror = null;
				try { v.pause(); } catch (e) {}
				window.{{Slot}} = 'pending';
				v.muted = true; v.playsInline = true; v.setAttribute('playsinline','');
				v.onerror = function () {
					window.{{Slot}} = 'MEDIA-ERROR code=' + (v.error ? v.error.code : '?');
				};
				v.src = '{{url}}';
				v.load();
				var p = v.play();
				if (p && p.catch) { p.catch(function (e) { window.{{Slot}} = 'PLAY-REJECTED ' + e; }); }
				setTimeout(function () {
					var at = v.currentTime;
					setTimeout(function () {
						window.{{Slot}} = 'size=' + v.videoWidth + 'x' + v.videoHeight
							+ '|ready=' + v.readyState
							+ '|err=' + (v.error ? v.error.code : '-')
							+ '|advanced=' + (v.currentTime - at).toFixed(2);
					}, 1200);
				}, 800);
				return 'started';
			})()
			""").ConfigureAwait(false);

		return started is null ? null : await PollAsync(webView).ConfigureAwait(false);
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
	/// The fragment the hash arm reloads at. A route a hash router would own, so the URL is the real shape
	/// rather than a token — and one this sample's page ignores, so the only thing under test is whether the
	/// DOCUMENT comes back.
	/// </summary>
	private const string HashRoute = "#/probe-route";

	/// <summary>How one reload arm ended. The verdict compares two of these — see <see cref="CheckReloadAsync"/>.</summary>
	private enum ReloadOutcome
	{
		/// <summary>The document navigated away and the bundle came back. The only good answer.</summary>
		Recovered,
		/// <summary>It navigated, and what came back is the platform's error document, not ours.</summary>
		ErrorDocument,
		/// <summary>It never left. On the hash arm with a passing control, this IS the iOS failure.</summary>
		NeverNavigated,
		/// <summary>The page stopped answering evaluation entirely.</summary>
		Silent,
		/// <summary>Could not read the page BEFORE reloading, so the arm proved nothing either way.</summary>
		Unreadable,
		/// <summary>
		/// The arm never reached the URL it was supposed to test — the fragment did not take. A pass here
		/// would be a pass for the PLAIN shape wearing the fragment arm's name, which is the one way this
		/// gate could go back to being green about the wrong thing.
		/// </summary>
		Misaimed,
	}

	/// <summary>
	/// <b>Probe 2 — a top-level navigation with a route registered</b>, run TWICE: once at <c>/</c> and once
	/// at a <c>#fragment</c> URL.
	/// <para>
	/// ⚠ <b>The second arm is the whole point, and its absence is why this gate was green while the defect
	/// it was built for was real.</b> The first adopter filed a reload failure, this probe reproduced their
	/// setup and PASSED on Chromium 110 and again on 133 — because it reloaded at <c>/</c>. The trigger is a
	/// fragment: MAUI's request→asset mapping strips a query string and not a fragment, so <c>/#/library</c>
	/// looks for an asset named <c>#/library</c>, 404s, and Chromium reports
	/// <c>ERR_INVALID_RESPONSE</c> (2026-08-06). A hash router is what most SPAs in a webview use, so
	/// "reload survives" is only an interesting claim in that shape.
	/// </para>
	/// <para>
	/// <b>Two arms rather than one, because a single failing arm is not attributable.</b> The plain reload is
	/// the CONTROL: plain-pass + hash-fail is the platform defect and nothing else, while both failing means
	/// the harness or the route is broken and the fragment has been proven nothing about. That distinction
	/// costs one extra reload and is the difference between a verdict and a guess.
	/// </para>
	/// <para>
	/// ⚠ <b>The assertion is that the page came BACK, and that it actually LEFT.</b> A failed main-frame
	/// navigation does not throw anywhere the host can see it — Chromium simply swaps in its own error
	/// document — so the only honest check reads the resulting DOM. And a check that merely finds a healthy
	/// document proves nothing on its own, because the PRE-navigation document is healthy too: the first
	/// version of this probe passed in 515 ms and might have been reading the page it never left. Hence
	/// <see cref="StaleMark"/>, a global that a real navigation destroys.
	/// </para>
	/// <para>
	/// ⚠ <b><see cref="StaleMark"/> and <c>nodes</c> are LOAD-BEARING on iOS in a way they are not on
	/// Android.</b> There is no error document to notice there: WKWebView keeps the PREVIOUS page on screen
	/// when a provisional navigation fails, so the app looks perfectly healthy and "it rendered" is not
	/// evidence of anything. The stamp surviving is the only thing that says the reload never happened.
	/// </para>
	/// </summary>
	public static async Task<string> CheckReloadAsync(HybridWebView webView, Action<string> log)
	{
		// WHAT OUR DOCUMENT IS, read from the live page before anything is reloaded. Every arm then has to
		// come back to a document with this identity — see ReloadArmAsync for why recognising OUR page beats
		// recognising the platform's error page.
		var baseline = await EvaluateAsync(webView, Snapshot).ConfigureAwait(false);
		var expected = FieldOf(baseline, "title");
		if (string.IsNullOrEmpty(expected))
			return "RELOAD: INCONCLUSIVE — the page under test has no <title>, so there is nothing to "
				+ $"recognise it by after a navigation. baseline=[{baseline}]";
		log($"[NAV] the document under test is titled '{expected}' — every arm must come back to it");

		// The control FIRST: if the plain reload cannot be made to work, nothing the fragment arm reports
		// can be attributed to the fragment.
		var (plain, plainDetail) = await ReloadArmAsync(webView, log, "plain", hash: null, expected).ConfigureAwait(false);
		var (fragment, fragmentDetail) = await ReloadArmAsync(webView, log, "fragment", HashRoute, expected).ConfigureAwait(false);

		var detail = $"plain=[{plainDetail}] fragment=[{fragmentDetail}]";

		if (plain is ReloadOutcome.Recovered && fragment is ReloadOutcome.Recovered)
			return $"RELOAD: PASS — the bundle came back at `/` AND at `/{HashRoute}`. {detail}";

		if (plain is not ReloadOutcome.Recovered)
			return $"RELOAD: INCONCLUSIVE — the CONTROL arm did not survive a plain reload ({plain}), so "
				+ $"nothing was proven about the fragment arm ({fragment}). Fix the control first. {detail}";

		// ⚠ An arm that proved NOTHING must not be reported as a platform defect. This gate exists to make
		// a failure attributable, so the one thing it must never do is attribute its own harness trouble to
		// the platform. `Silent` is deliberately NOT here: a page that stops answering evaluation after the
		// reload is the iOS symptom itself, not a broken harness.
		if (fragment is ReloadOutcome.Misaimed or ReloadOutcome.Unreadable)
			return $"RELOAD: INCONCLUSIVE — the control passed, but the fragment arm never got to test "
				+ $"anything ({fragment}), so the platform has NOT been accused of anything. {detail}";

		// Control passed, fragment did not: attributable, and the two shapes fail differently.
		var symptom = fragment switch
		{
			ReloadOutcome.ErrorDocument =>
				"the platform answered the document request with a 404 and the webview swapped in its error "
				+ "page — the Android shape (the fragment is mapped into the asset name)",
			ReloadOutcome.NeverNavigated =>
				"the document never navigated away at all, SILENTLY, while the previous page stayed on "
				+ "screen — the iOS shape, which no screenshot can catch",
			ReloadOutcome.Silent => "the page stopped answering evaluation entirely after the reload",
			_ => $"{fragment}",
		};
		return $"RELOAD: FAIL — a plain reload survives and a reload at `/{HashRoute}` does not: {symptom}. {detail}";
	}

	/// <summary>
	/// One reload arm: optionally move to <paramref name="hash"/>, stamp, reload, and report what the page
	/// became.
	/// </summary>
	/// <param name="hash">The fragment to reload at, or null to reload wherever the page already is.</param>
	/// <param name="expectedTitle">
	/// The <c>&lt;title&gt;</c> of the document under test. Recovery is judged by coming BACK to it.
	/// </param>
	private static async Task<(ReloadOutcome Outcome, string Detail)> ReloadArmAsync(
		HybridWebView webView, Action<string> log, string arm, string? hash, string expectedTitle)
	{
		if (hash is not null)
		{
			// Assigning `location.hash` is a SAME-document navigation — it changes the URL without loading
			// anything, which is exactly what is wanted: the reload that follows is then the first request
			// the platform has ever had to answer for this URL. Done as its own evaluation so the URL has
			// certainly settled before the reload is asked for.
			_ = await EvaluateAsync(webView, $"(function(){{ location.hash = '{hash}'; return location.href; }})()")
				.ConfigureAwait(false);
			await Task.Delay(250).ConfigureAwait(false);
		}

		var before = await EvaluateAsync(webView, Snapshot).ConfigureAwait(false);
		if (before is null) return (ReloadOutcome.Unreadable, $"{arm}: could not read the page before reloading");
		log($"[NAV] {arm} before reload: {before}");

		// The arm must be AIMED before it is fired. Checked against the page's own `location.hash` rather
		// than assumed from the assignment above, because "the fragment silently did not take" and "the
		// fragment reload works" produce the same green verdict — and that is exactly the failure this
		// whole probe is being extended to stop repeating.
		if (hash is not null && !before.Contains($"hash={hash}", StringComparison.Ordinal))
			return (ReloadOutcome.Misaimed, $"{arm}: never reached '{hash}' — {before}");

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

		if (after is null) return (ReloadOutcome.Silent, $"{arm}: stopped answering after location.reload()");
		log($"[NAV] {arm} after reload: {after}");

		if (after.Contains("stamp=STALE", StringComparison.Ordinal))
			return (ReloadOutcome.NeverNavigated,
				$"{arm}: still the stamped document after {Timeout.TotalSeconds:0}s — {after}");

		// 🔴 RECOGNISE OUR OWN DOCUMENT — do NOT try to recognise the platform's error page. This check
		// used to be a blocklist ("an empty title, or ERR_ in the body text") and it passed a run that
		// was staring at Chromium's error document, on 2026-08-06, with the repair deliberately disabled.
		// BOTH of its signals failed at once, independently:
		//   · the error page's title is LOCALIZED and non-empty — this device reported `title=网页无法打开`,
		//     so `title=|` matched nothing. An English-locale device would have hidden this forever.
		//   · the body text was truncated to 60 chars ONE CHARACTER before the underscore — `net::ERR`,
		//     so `ERR_` matched nothing either.
		// An allow-check has no such holes: whatever the platform substitutes, it is not our page, and it
		// does not carry our title.
		var recovered = string.Equals(FieldOf(after, "title"), expectedTitle, StringComparison.Ordinal);

		return (recovered ? ReloadOutcome.Recovered : ReloadOutcome.ErrorDocument, $"{arm}: {after}");
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
				+ '|hash=' + (location.hash || '-')
				+ '|ready=' + document.readyState
				+ '|stamp=' + (window.{{StaleMark}} ? 'STALE' : 'fresh')
				+ '|title=' + (document.title || '')
				+ '|nodes=' + document.querySelectorAll('*').length
			+ '|text=' + ((b && b.innerText) || '')
				.split(String.fromCharCode(10)).join(' ')
				.split(String.fromCharCode(9)).join(' ')
				.slice(0, 120);
	})()
	""";
	// Two notes that belong to the script above but must NOT live INSIDE it — see EvaluateAsync, which
	// flattens every script to one line, so a `//` comment would swallow the rest of the program:
	//  · `hash` is load-bearing for the fragment arm. Without it NOTHING in a passing verdict says the
	//    reload URL carried a fragment at all, so an arm that silently lost its hash reads as a clean pass.
	//  · the text slice is 120, not 60. At 60 the platform's own error text came back as `net::ERR` — cut
	//    one character before the underscore, which silently defeated a check looking for `ERR_`. A
	//    diagnostic truncated mid-token is worse than no diagnostic: it reads as evidence.

	/// <summary>
	/// One <c>name=value</c> field out of a <see cref="Snapshot"/> line, or the empty string when absent.
	/// </summary>
	/// <remarks>
	/// ⚠ <c>text</c> is deliberately LAST in the snapshot, because it is the only field whose value can
	/// contain a <c>|</c> — a page's own text is not the probe's to escape. Read fields before it.
	/// </remarks>
	private static string FieldOf(string? snapshot, string name)
	{
		if (snapshot is null) return string.Empty;
		foreach (var field in snapshot.Split('|'))
		{
			if (field.StartsWith($"{name}=", StringComparison.Ordinal)) return field[(name.Length + 1)..];
		}
		return string.Empty;
	}

	/// <summary>Poll <see cref="Slot"/> until the async work parked an answer there.</summary>
	private static Task<string?> PollAsync(HybridWebView webView) => PollSlotAsync(webView, Slot);

	/// <summary>
	/// Poll a NAMED slot.
	/// <para>
	/// ⚠ It was <c>internal</c> until 2026-08-12 for a second probe file that drove the page the same way
	/// (<c>BackgroundHandoffProbe</c>, deleted when the kit's <c>BackgroundPlaybackTransfer</c> took the
	/// behaviour over). Nothing outside this file polls a slot now, so the widened visibility went back
	/// with it — an <c>internal</c> nobody calls is a seam that reads as load-bearing.
	/// </para>
	/// <para>
	/// 🔴 <b>INTERNAL AGAIN since 2026-08-14, and the note above is kept because its CONDITION is what
	/// changed rather than its reasoning.</b> <see cref="SegmentRouteProbe"/> drives the page the same way,
	/// so a caller exists again. The rule stands: narrow it the moment the last caller goes.
	/// </para>
	/// </summary>
	/// <param name="timeout">
	/// Overrides <see cref="Timeout"/>. ⚠ Needed by anything that waits on PRODUCTION rather than on the
	/// page: a segment run transcodes before it answers, which is minutes rather than the ten seconds a
	/// page-state probe needs.
	/// </param>
	internal static async Task<string?> PollSlotAsync(HybridWebView webView, string slot, TimeSpan? timeout = null)
	{
		var deadline = DateTime.UtcNow + (timeout ?? Timeout);
		while (DateTime.UtcNow < deadline)
		{
			await Task.Delay(250).ConfigureAwait(false);
			var value = await EvaluateAsync(webView, $"window.{slot}").ConfigureAwait(false);
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
	internal static async Task<string?> EvaluateAsync(HybridWebView webView, string script)
	{
		try
		{
			var raw = await MainThread.InvokeOnMainThreadAsync(() => webView.EvaluateJavaScriptAsync(Safe(script)))
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
			// ⚠ And on iOS this catch is NOT the safety net it looks like — see Safe().
			return null;
		}
	}

	/// <summary>
	/// A script WKWebView will accept, wrapped so that a runtime error inside it becomes a returned value
	/// instead of a thrown one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// 🔴 <b>Both halves of this are load-bearing, and the reason is that on iOS a failing evaluation KILLS
	/// THE APP — the <c>try/catch</c> in <see cref="EvaluateAsync"/> cannot catch it.</b> MAUI's
	/// <c>HybridWebViewHandler.MapEvaluateJavaScriptAsync</c> runs the evaluation as a fire-and-forget task
	/// and rethrows its failure onto the SYNCHRONIZATION CONTEXT
	/// (<c>Task.ThrowAsync</c> → <c>NSAsyncSynchronizationContextDispatcher.Apply</c>), which is a different
	/// stack from the one awaiting it. So the exception arrives on the UI thread with nothing above it,
	/// becomes an unhandled managed exception, and aborts the process with SIGABRT. Measured 2026-08-06 on
	/// the simulator: the whole probe suite took the app down ~7 s after launch, before a single verdict
	/// was logged, and the crash predates this file's two-arm rewrite.
	/// </para>
	/// <list type="number">
	/// <item><b>Flattened to ONE LINE.</b> WKWebView rejected the multi-line scripts outright with
	/// <c>SyntaxError: Unexpected EOF</c> at line 1 — the parse fails before any JS runs, so no in-page
	/// guard can help. ⚠ Consequence: a <c>//</c> comment inside a script would swallow the rest of the
	/// program. Keep script commentary in C#, outside the string.</item>
	/// <item><b>Wrapped in a JS try/catch.</b> That covers the other half — a RUNTIME error (a missing
	/// element, a revoked context mid-navigation) would otherwise take the same fatal path. Every script
	/// here is an EXPRESSION, so wrapping it in an IIFE preserves its value.</item>
	/// </list>
	/// </remarks>
	private static string Safe(string script)
	{
		// Newlines and tabs only, NOT every space: `.replace(/\s+/g, ' ')` in the snapshot contains a
		// single-space string LITERAL, and deleting whitespace rather than replacing it would corrupt it.
		var flat = script.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
		return $"(function(){{try{{return ({flat});}}catch(e){{return 'PROBE-THREW ' + e;}}}})()";
	}

	/// <summary>
	/// Serve the app's MAIN DOCUMENT from a <b>file on disk</b>, which is the one shape the kit's bridge-tag
	/// check could not see until 0.14 and still the shape a runtime-fetched bundle really has.
	///
	/// <para>
	/// 🔴 <b>Two arms, and each proves a different half.</b> Tagged: the document still reaches the page, so
	/// the check READ a <c>FileStream</c> and put its position back — a consumed body is a blank page and no
	/// handshake, which cannot be mistaken for a pass. Untagged: the check must WARN, which is the direction
	/// nothing had ever exercised on either shell.
	/// </para>
	/// <para>
	/// ⚠ <b>The untagged arm takes the app down with it, deliberately</b> — with no bridge there is no
	/// handshake, so no page-side probe can report and the only evidence is the HOST log. That is why this is
	/// opt-in per launch rather than part of the suite:
	/// <c>SIMCTL_CHILD_SHENORA_SAMPLE_DOC_FROM_DISK=tagged|untagged xcrun simctl launch booted …</c>
	/// </para>
	/// <para>
	/// ⚠ <b>It reads the packaged document rather than inventing one</b>, so the tagged arm is the real
	/// document and its tag is the real tag. Stripping is a plain string removal of the script reference —
	/// enough to make the check's own test fail, which is all the untagged arm needs.
	/// </para>
	/// </summary>
	public static IDisposable? ServeDocumentFromDisk(IWebViewInterceptor interceptor, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(interceptor);
		ArgumentNullException.ThrowIfNull(log);

		// 🔴 SAY WHAT IT SAW, ALWAYS. Returning silently when the variable is unset makes "the probe was not
		// asked" indistinguishable from "it was asked and the value never arrived" — and the second is the
		// likely one here, because the value has to survive a launcher that is not this process's shell.
		// Cost the first run of this probe: the app started, the page loaded, everything passed, and none of
		// it was evidence about the document at all.
		var mode = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_DOC_FROM_DISK");
		if (string.IsNullOrWhiteSpace(mode))
		{
			log("DOC-DISK: off — SHENORA_SAMPLE_DOC_FROM_DISK is unset, so the document is served as usual");
			return null;
		}

		var untagged = mode.Equals("untagged", StringComparison.OrdinalIgnoreCase);
		if (!untagged && !mode.Equals("tagged", StringComparison.OrdinalIgnoreCase))
		{
			log($"DOC-DISK: SKIPPED — SHENORA_SAMPLE_DOC_FROM_DISK='{mode}', expected tagged|untagged");
			return null;
		}

		string path;
		try
		{
			// The packaged document, copied to a real FILE — on iOS the bundle resource is already one, but
			// copying keeps both arms identical and gives the untagged arm something it may rewrite.
			using var packaged = FileSystem.OpenAppPackageFileAsync("wwwroot/index.html").GetAwaiter().GetResult();
			using var reader = new StreamReader(packaged, Encoding.UTF8);
			var html = reader.ReadToEnd();

			var tagged = html.Contains("hybridwebview.js", StringComparison.OrdinalIgnoreCase);
			if (!tagged)
			{
				// Says so rather than proceeding: without the tag the tagged arm proves nothing, and the
				// untagged arm would "pass" for the wrong reason.
				log("DOC-DISK: FAIL — the packaged index.html carries no bridge tag, so neither arm is valid");
				return null;
			}

			if (untagged) html = html.Replace("hybridwebview.js", "no-bridge-here.js", StringComparison.OrdinalIgnoreCase);

			path = Path.Combine(FileSystem.CacheDirectory, untagged ? "doc-untagged.html" : "doc-tagged.html");
			File.WriteAllText(path, html, Encoding.UTF8);
			log($"DOC-DISK: serving the main document from {path} ({(untagged ? "UNTAGGED — expect the kit to warn" : "tagged")})");
		}
		catch (Exception ex)
		{
			log($"DOC-DISK: FAIL — could not stage the document ({ex.GetType().Name}: {ex.Message})");
			return null;
		}

		return interceptor.Use((request, next, ct) =>
		{
			if (request.Uri.AbsolutePath is not ("/" or "" or "/index.html")) return next(request, ct);

			// 🔴 A REAL FileStream, which is the whole point — a MemoryStream here would exercise the path
			// that already worked. Not disposed by this probe: the kit owns a body once it answers with it.
			var body = File.OpenRead(path);
			return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.Ok(body, "text/html"));
		});
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
	/// <para>
	/// ⚠ <b>It cannot exercise the kit's bridge-tag check</b>, which runs only on a <c>200</c> that is
	/// <c>text/html</c> — <see cref="ServeDocumentFromDisk"/> is the probe for that.
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
