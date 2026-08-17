using Shenora.Core.WebView;
using Shenora.Engine.Missions;
using Shenora.Modules.Media;

using Shenora;
namespace Shenora.Sample.Maui;

/// <summary>
/// Does <c>UseComputedRemux</c> — an MP4 <b>that has never been produced</b>, answered over HTTP ranges out
/// of a computed layout — actually satisfy a real media element on a real device?
///
/// <para>
/// 🔴 <b>Nothing below this route had ever met a webview.</b> Planning, range arithmetic and the route's
/// fall-through were all proven by unit tests against fakes, and a unit test cannot show that an
/// <c>&lt;video&gt;</c> ACCEPTS the result. The two assertions here are the two a unit test structurally
/// cannot make:
/// </para>
/// <list type="number">
/// <item><b>A DECODED PICTURE.</b> <c>videoWidth x videoHeight</c> non-zero, a real <c>duration</c>, and
/// <c>currentTime</c> advancing. ⚠ <c>size=0x0</c> with <c>readyState=4</c> and NO error is this tier's
/// standing silent failure — an undecodable picture beside a decodable soundtrack raises nothing at all, so
/// the page gets sound and a blank rectangle. It is a FAIL here, never a pass.</item>
/// <item><b>A COLD SEEK INTO A REGION THAT WAS NEVER PRODUCED</b> — see <see cref="CheckColdSeekAsync"/>.
/// That is the entire point of computing a layout instead of writing a file, and it is the one claim the
/// segment path cannot make.</item>
/// <item><b>WHAT AN ELEMENT DOES WITH THE ROUTE'S <c>503</c></b> — see <see cref="CheckFirstRequestAsync"/>.
/// The route's premise is one ordinary <c>&lt;video src&gt;</c>, a <c>&lt;video&gt;</c> is not a polling
/// loop, and the kit's own justification for 503-over-404 is a claim about media elements. A test against a
/// <c>fetch</c> cannot answer it.</item>
/// </list>
///
/// <para>
/// ⚠ <b>The fixture has to be one the route can PLAN, and none of the sample's older Matroska clips is.</b>
/// <c>Mp4Remuxer.Plan</c> is lossless by contract: every stream the source offers must be carriable by MP4
/// as-is, which means H.264/HEVC video and AAC audio. Every <c>clip-*.mkv</c> staged before 2026-08-12 was
/// built for the CONVERSION tier and therefore deliberately carries something MP4 cannot hold (mp3, AC-3,
/// mpeg4, mpeg2video) — so all six plan to <c>null</c> and fall through, which is the route working exactly
/// as designed and proves nothing about it. Hence <c>clip-h264-aac.mkv</c>: the container-repair case, where
/// copying is all that is needed.
/// </para>
/// </summary>
internal static class RemuxRouteProbe
{
	/// <summary>
	/// The route the page asks for. <b>Its own path, deliberately</b> — <c>/media</c> keeps serving the
	/// existing clips through <c>UseFiles</c> and <c>/converted</c> keeps its own behaviour, so this probe
	/// adds a case rather than changing the meaning of one every other probe depends on.
	/// </summary>
	private const string RoutePath = "/remux";

	/// <summary>
	/// The one COMMITTED fixture this route can plan — H.264 video + AAC audio in Matroska, 60 s. See the
	/// type's remarks for why none of the older clips can stand in for it.
	/// </summary>
	public const string Fixture = "clip-h264-aac.mkv";

	/// <summary>
	/// 🔴 <b>A film past the old 64 MiB ceiling — and it is NOT in the repo, deliberately.</b> The ceiling
	/// this route used to decline over was deleted on 2026-08-13, so "a big film plays" is the claim that
	/// removal has to earn on a device; every measurement before it was made under the limit. A ~78 MB
	/// fixture must not be committed, so it is BUILT and PUSHED, the convention
	/// <c>.claude/knowledge/mobile-shells.md</c> already uses for the other clips:
	/// <code>
	/// ffmpeg -y -f lavfi -i testsrc=size=640x360:rate=30:duration=1000 \
	///        -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1000 \
	///        -c:v libx264 -preset veryfast -pix_fmt yuv420p -b:v 950k \
	///        -c:a aac -b:a 128k -movflags +faststart big-src.mp4
	/// ffmpeg -y -i big-src.mp4 -c copy -f matroska clip-big-h264-aac.mkv
	/// </code>
	/// → 81,635,953 bytes, 1000.02 s, planning to 82,843,185 bytes and 76,876 samples. Two steps rather than
	/// one because that is the PROVEN provenance of <see cref="Fixture"/> (encode to MP4, then copy into
	/// Matroska), and this fixture's whole job is to be the same file only bigger. Then into the app's own
	/// staging root (Android; the iOS simulator's container takes a plain copy into
	/// <c>&lt;data container&gt;/Library/Caches/media/</c>):
	/// <code>
	/// adb push clip-big-h264-aac.mkv /data/local/tmp/ &amp;&amp; adb shell chmod 644 /data/local/tmp/clip-big-h264-aac.mkv
	/// adb shell run-as com.shenora.sample.maui mkdir -p /data/user/0/com.shenora.sample.maui/cache/media
	/// adb shell run-as com.shenora.sample.maui cp /data/local/tmp/clip-big-h264-aac.mkv \
	///     /data/user/0/com.shenora.sample.maui/cache/media/clip-big-h264-aac.mkv
	/// </code>
	/// ⚠ <b>The SIZE comes from the duration, and it has to — asking for bitrate does not work here.</b>
	/// <c>testsrc</c> is compressible enough that x264 simply undershoots: <c>-b:v 950k</c> over 600 s
	/// delivered 653 kbps and 46.7 MiB, and adding <c>-minrate/-maxrate/-bufsize</c> changed nothing (VBV
	/// CAPS a bitrate, it does not pad to fill one — that needs <c>-nal-hrd cbr</c>). 1000 s at the bitrate
	/// x264 actually produces is what clears the ceiling. ⚠ A fixture UNDER 64 MiB proves exactly what the
	/// small one already did, so CHECK the byte count before trusting a run.
	/// ⚠ Absent, every big-film check SKIPS and says so (<see cref="IsStaged"/>): a device-staged fixture
	/// cannot be a silent precondition, which is the trap <c>MediaRangeProbe.EnsureStagedAsync</c> records.
	/// </summary>
	public const string BigFixture = "clip-big-h264-aac.mkv";

	/// <summary>
	/// 🔴 <b>A name the route ACCEPTS and no file answers — the <c>404</c> CONTROL, and it is load-bearing for
	/// the only claim in this area that was ever argued rather than measured.</b>
	/// <c>MediaConversionExtensions.NotReadyYet</c> chose <c>503</c> over <c>404</c> because "404 would tell a
	/// media element to give up permanently", so what an element does with each of them is the comparison that
	/// settles it — and both arms have to run on the SAME element, in the SAME document, minutes apart at most,
	/// or the numbers are from two different worlds. It is in the allow-list on purpose: that makes the route's
	/// own <c>NotFound()</c> answer the 404, rather than the platform's asset handler answering for a path
	/// nothing claimed, which is a different response with different headers.
	/// </summary>
	public const string AbsentFixture = "clip-absent-h264-aac.mkv";

	/// <summary>The slot the page parks an async answer in. Its OWN, not <c>PageProbe</c>'s — the two probes
	/// drive the same element and a shared slot means each can report the other's answer.</summary>
	private const string Slot = "__shenoraRemux";

	/// <summary>
	/// How long to wait for one in-page answer. ⚠ Deliberately longer than <c>PageProbe</c>'s 10 s: iOS
	/// reads a container in HUNDREDS of tiny ranges (4–512 bytes) before it streams forward, so a budget
	/// tuned to Android's single large request would report "no answer" for a shell that is merely chattier.
	/// </summary>
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// How many requests <see cref="LogRequests"/> has seen since the app started, and how many of those it
	/// answered <c>503</c> and <c>404</c>.
	///
	/// <para>
	/// 🔴 <b>THE REQUEST COUNT exists for a question a log line cannot answer for a verdict: does a
	/// <c>&lt;video&gt;</c> RETRY a <c>503</c>?</b> "It never recovered" and "it retried and the retries also
	/// failed" are opposite findings that look identical from the element's own state, so counting the
	/// requests the route answered is the only mechanical way to tell them apart.
	/// </para>
	/// <para>
	/// 🔴 <b>THE STATUS COUNTS ARE THE GATE'S PRECONDITIONS, and they exist because the gate could otherwise
	/// print the OPPOSITE of its own finding as a PASS.</b> <see cref="CheckFirstRequestAsync"/> measures what
	/// an element does with a <c>503</c> and with a <c>404</c> — but nothing in a TIMELINE says which status
	/// produced it. Anything that plans the fixture first turns the "503 arm" into a <c>206</c>, whose timeline
	/// contains <c>PLAYING@</c>, which used to read as <i>"the element recovered from the 503 BY ITSELF"</i> —
	/// the exact false claim this gate exists to prevent, phrased as a PASS and ready to be copied into the
	/// docs. Likewise the 404 arm silently becomes a measurement of the PLATFORM's asset handler if
	/// <see cref="AbsentFixture"/> leaves <c>Resolve</c>'s allow-list, or a measurement of a working file if
	/// one ever lands at that name. <b>So each arm asserts that the ROUTE actually answered the status it
	/// claims to be measuring, and FAILS naming the cause when it did not.</b> Sabotage-verified in both
	/// directions on a device (2026-08-13) — see the two arms' own remarks.
	/// </para>
	/// </summary>
	private static int _requests;

	/// <summary>How many <c>503</c>s the route has answered — see <see cref="_requests"/>.</summary>
	private static int _notReady;

	/// <summary>How many <c>404</c>s the route has answered — see <see cref="_requests"/>.</summary>
	private static int _notFound;

	/// <summary>
	/// Is <paramref name="fixture"/> already in the staging root? <b>For a fixture pushed onto the device by
	/// hand</b> — see <see cref="BigFixture"/>, which is too big to commit — so a caller can SKIP loudly
	/// instead of reporting the 404 an unstaged source produces, which reads as the route declining it.
	/// </summary>
	public static bool IsStaged(string sourceRoot, string fixture) =>
		File.Exists(Path.Combine(sourceRoot, fixture));

	/// <summary>
	/// Register the computed-remux route.
	///
	/// <para>
	/// 🔴 <b>Call this BEFORE <c>UseMediaConversion</c>.</b> Middleware run in registration order, so
	/// reversed, the conversion route answers every request its own <c>Resolve</c> matches — a plannable film
	/// would answer <c>503</c> while a whole transcode ran and this route would be dead code that still
	/// passed every test of its own. (In this sample the two routes claim different PATHS, so the ordering is
	/// not load-bearing here; it is written this way because it is what an adopter must copy.)
	/// </para>
	/// </summary>
	/// <param name="interceptor">The page's interceptor — the same pipeline every other route uses.</param>
	/// <param name="scheduler">
	/// The app's ONE scheduler, the same instance <see cref="ConversionRouteProbe"/> hands the conversion
	/// route. The route walks a source's metadata in a mission and answers <c>503 Retry-After: 1</c> until the
	/// plan lands, so <b>the FIRST request for a clip is a 503 rather than bytes</b> — a page must retry.
	/// The host-side controls below do (<see cref="WaitForPlanAsync"/>, at the same one-second interval
	/// <see cref="ConversionRouteProbe.CheckAsync"/> polls at); what a MEDIA ELEMENT does with that 503 is not a
	/// thing a host can retry on the element's behalf, and it is measured rather than assumed — see
	/// <see cref="CheckFirstRequestAsync"/>.
	/// </param>
	/// <param name="sourceRoot">Where <see cref="MediaRangeProbe"/> staged the clips.</param>
	/// <returns>
	/// The route handle. Dispose to remove the route and drop the layouts it planned — and KEEP it, because
	/// <see cref="IComputedRemuxRoute.PlanAsync"/> is how an app warms a source so its page never meets the
	/// <c>503</c> at all (D72). <see cref="CheckWarmedAsync"/> is this sample's consumer of that, and the
	/// reason it exists: an extension point nothing calls is indistinguishable from one that does not work.
	/// </returns>
	public static IComputedRemuxRoute Register(IWebViewInterceptor interceptor, IMissionScheduler scheduler,
		string sourceRoot, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(interceptor);
		ArgumentNullException.ThrowIfNull(scheduler);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(log);

		// ⚠ Unused by this route — there is no artifact to cache — and `required`, because the routes that DO
		// write need it and containment is stated once for all of them. Given its own directory anyway rather
		// than pointed at the conversion cache, so a stray write could never land among converted outputs.
		var cache = Path.Combine(FileSystem.CacheDirectory, "remux");
		Directory.CreateDirectory(cache);

		return interceptor.UseComputedRemux(scheduler, new MediaAccessOptions
		{
			Resolve = uri =>
			{
				if (!uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase)) return null;

				// `?<name>` plus an optional `&<nonce>`. The nonce is what makes the COLD seek cold: the
				// webview caches a response per URL, so re-requesting the same one can be answered without
				// the route ever seeing it, and a seek served out of the page's cache proves nothing about a
				// region being materialised on demand.
				var name = uri.Query.TrimStart('?').Split('&')[0];
				// The app-level allow-list on top of the kit's containment: three names here — two clips and
				// one that deliberately does not exist (the 404 control). ⚠ Adding one
				// without adding it HERE produces a 404 that reads as a planning failure — the mistake
				// ConversionRouteProbe's own allow-list records three times in one day.
				return name == Fixture || name == BigFixture || name == AbsentFixture
					? Path.Combine(sourceRoot, name)
					: null;
			},
			AllowedRoots = [sourceRoot],
			CacheRoot = cache,
			Log = AppCallback.Logger(log),
		});
	}

	/// <summary>
	/// Log every request this route answers, with its <c>Range</c> header and the status and length that came
	/// back.
	///
	/// <para>
	/// 🔴 <b>Registered in FRONT of the route so the two shells' request patterns are readable rather than
	/// inferred</b>, and it is the only way to tell the two failures apart: Android issues ONE large request
	/// (its delivery is <c>Unsliced</c>, so the platform applies the range start itself), while iOS issues
	/// hundreds of tiny ones. A stall on iOS with no second request means the FIRST answer was wrong; a stall
	/// with hundreds of requests means serving one is too expensive. Those need completely different fixes and
	/// look identical from the page.
	/// </para>
	/// <para>
	/// It decides nothing — every request passes straight through.
	/// </para>
	/// </summary>
	public static IDisposable LogRequests(IWebViewInterceptor interceptor, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(interceptor);
		ArgumentNullException.ThrowIfNull(log);

		var count = 0;
		return interceptor.Use(async (request, next, ct) =>
		{
			if (!request.Uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase))
				return await next(request, ct).ConfigureAwait(false);

			var range = request.GetHeader("Range") ?? "(none)";
			var response = await next(request, ct).ConfigureAwait(false);
			// The whole-app totals — see the fields. ⚠ Counted from the RESPONSE rather than from what the
			// probe expected: an assertion built on what a probe intended to happen cannot catch the case
			// where something else happened, which is the entire point of these three counters.
			Interlocked.Increment(ref _requests);
			if (response?.StatusCode == 503) Interlocked.Increment(ref _notReady);
			else if (response?.StatusCode == 404) Interlocked.Increment(ref _notFound);
			var n = Interlocked.Increment(ref count);
			var length = response?.Headers.TryGetValue("Content-Length", out var len) == true ? len : "-";
			var contentRange = response?.Headers.TryGetValue("Content-Range", out var cr) == true ? cr : "-";
			log($"[REMUX] #{n} range={range} -> status={response?.StatusCode.ToString() ?? "FELL THROUGH"} "
				+ $"len={length} content-range={contentRange}");
			return response;
		});
	}

	/// <summary>
	/// 🔴 <b>THE RELEASE GATE FOR THE 503, AND IT MEASURES AN ELEMENT RATHER THAN A CLIENT.</b> Point the
	/// page's <c>&lt;video&gt;</c> at a source NOBODY HAS PLANNED, so its very first request is the route's
	/// <c>503 Retry-After: 1</c>, and report what the element does with it — second by second, then after the
	/// plan has landed, then after the <c>src</c> is re-pointed.
	///
	/// <para>
	/// ⚠ <b>Why it cannot be inferred from anything already measured:</b> this route's whole premise (D71) is
	/// ONE ordinary <c>&lt;video src&gt;</c> — no manifest, no JS player, no polling — and <b>a media element
	/// is not a polling loop.</b> Every 503 consumer in this repo is a C#-driven <c>fetch</c> loop, and
	/// <c>MediaConversionExtensions.NotReadyYet</c> justifies 503 over 404 by saying "404 would tell a media
	/// element to give up permanently", which is a claim ABOUT an element that no element had ever been
	/// asked. If the element gives up on the 503 too, then 503 and 404 are behaviourally identical to it and
	/// the 503's advantage survives only for a <c>fetch</c> client — worth knowing either way, and it is a
	/// finding rather than something for a probe to work around.
	/// </para>
	/// <para>
	/// <b>Three stages, because they separate three different worlds:</b> (1) a 12 s timeline of
	/// <c>readyState</c> / geometry / <c>error.code</c> / <c>networkState</c> while the plan is walked and
	/// lands — <b>does it EVER recover on its own?</b>; (2) the host's own retry loop, proving the route now
	/// serves that source (so a stage-1 failure cannot be blamed on the route); (3) the SAME element
	/// re-pointed at the SAME source, which is the difference between "the element gave up permanently" (a
	/// design flaw) and "the element needs re-pointing" (a documented page contract).
	/// </para>
	/// <para>
	/// ⚠ It must run BEFORE <see cref="CheckAsync"/> for the same fixture, and it is the only probe here with
	/// an ordering requirement: a plan is cached per source identity for the life of the route, so once
	/// anything has planned the clip there is no first request left to measure. The
	/// <see cref="_requests"/> count either side of the assignment is what says whether the element RETRIED.
	/// </para>
	/// </summary>
	public static async Task<string> CheckFirstRequestAsync(HybridWebView webView, string sourceRoot,
		string fixture, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(log);

		try
		{
			await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return $"REMUX-FIRST: FAIL — could not stage {fixture} ({ex.GetType().Name}: {ex.Message})";
		}

		// 🔴 THE 404 CONTROL FIRST, on the SAME element — see AbsentFixture. Without it, "the element gives up
		// on a 503" is a fact with nothing to compare it to, and the claim actually in dispute is whether 503
		// buys anything over 404 for a media element.
		var beforeAbsent = Volatile.Read(ref _notFound);
		var absent = await PlayAsync(webView, ArmScript(Url(AbsentFixture, "absent"), seconds: 4)).ConfigureAwait(false);
		var absent404s = Volatile.Read(ref _notFound) - beforeAbsent;
		log($"[REMUX-FIRST] 404 control -> {absent ?? "NO ANSWER"} [the route answered {absent404s} 404(s)]");

		// 🔴 ASSERT THE CONTROL, do not merely print it. This arm is the whole reason the gate is worth
		// anything, and it has two silent failure modes: the route stops answering the name (then the
		// PLATFORM's asset handler answers, whose response is not the one being compared), or a real file
		// lands there (then the route plans it, answers 206, and the element PLAYS). The 404 COUNT catches
		// both — a plausible-looking timeline does not.
		if (absent404s == 0)
			return $"REMUX-FIRST: FAIL — the 404 CONTROL never got a 404 from this route, so the comparison "
				+ $"below would be against something else entirely. Either {AbsentFixture} left Resolve's "
				+ $"allow-list (the platform's asset handler answered instead) or a real file now exists at "
				+ $"that name (the route planned it). Control said: {absent ?? "NO ANSWER"}";
		if (absent is null || !absent.Contains("ERROR@", StringComparison.Ordinal)
			|| !absent.Contains("err=4", StringComparison.Ordinal))
			return $"REMUX-FIRST: FAIL — the 404 control did not produce the error it exists to establish "
				+ $"(expected ERROR@ and err=4): {absent ?? "NO ANSWER"}";

		var before = Volatile.Read(ref _requests);
		var beforeNotReady = Volatile.Read(ref _notReady);
		var timeline = await PlayAsync(webView, ArmScript(Url(fixture, "first"), seconds: 12)).ConfigureAwait(false);
		var during = Volatile.Read(ref _requests) - before;
		var notReady = Volatile.Read(ref _notReady) - beforeNotReady;

		log($"[REMUX-FIRST] timeline -> {timeline ?? "NO ANSWER"}");
		log($"[REMUX-FIRST] the route answered {during} request(s) in those 12 s, {notReady} of them 503 — a "
			+ "count of 1 request means the element did NOT retry, whatever its own state says");
		if (timeline is null)
			return "REMUX-FIRST: FAIL — the page never reported a timeline (the harness, not the element)";

		// 🔴 THE ORDERING REQUIREMENT, AS A MECHANISM. Prose said "run this before CheckAsync" and prose
		// cannot fail: with the fixture already planned the first request is a 206, the timeline contains
		// PLAYING@, and the verdict below would announce that the element RECOVERED FROM THE 503 BY ITSELF —
		// a false claim, in the words of a PASS, in the one probe whose job is to prevent it.
		if (notReady == 0)
			return $"REMUX-FIRST: FAIL — no 503 was answered for {fixture} in that window, so this measured a "
				+ $"source that was ALREADY PLANNED and says nothing about the 503. Something planned it before "
				+ $"this probe ran: it must run before CheckAsync (and before any other request) for the same "
				+ $"fixture. Timeline was: {timeline}";

		// The host's own loop, which is the CONTROL: it proves the route serves this source now, so nothing
		// above can be blamed on a route that never planned anything.
		var served = await WaitForPlanAsync(webView, fixture, log).ConfigureAwait(false);
		if (served is null || !served.Contains("status=20", StringComparison.Ordinal))
			return $"REMUX-FIRST: FAIL — the route never served {fixture} even to a retrying client "
				+ $"({served ?? "NO ANSWER"}), so the element's behaviour above proves nothing";

		// 🔴 THE SAME ELEMENT, RE-POINTED. Not a fresh one: an element that recovers only when replaced is a
		// different (and much worse) contract than one that recovers on a new src.
		var repointed = await PlayAsync(webView, $$"""
			(function(){
				var v = document.getElementById('vid');
				if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
				window.{{Slot}} = 'pending';
				v.onerror = null; v.onloadedmetadata = null; v.onplaying = null;
				v.muted = true; v.playsInline = true;
				v.src = '{{Url(fixture, "repoint")}}';
				v.load();
				var p = v.play();
				if (p && p.catch) { p.catch(function (e) { window.{{Slot}} = 'PLAY-REJECTED ' + e; }); }
				setTimeout(function () {
					var at = v.currentTime;
					setTimeout(function () {
						window.{{Slot}} = 'size=' + v.videoWidth + 'x' + v.videoHeight
							+ '|dur=' + v.duration
							+ '|ready=' + v.readyState
							+ '|err=' + (v.error ? v.error.code : '-')
							+ '|advanced=' + (v.currentTime - at).toFixed(2);
					}, 1400);
				}, 900);
				return 'started';
			})()
			""").ConfigureAwait(false);
		log($"[REMUX-FIRST] re-pointed -> {repointed ?? "NO ANSWER"}");

		var recoveredAlone = timeline.Contains("PLAYING@", StringComparison.Ordinal);
		var erroredOn503 = timeline.Contains("ERROR@", StringComparison.Ordinal);
		var replays = repointed is not null
			&& repointed.StartsWith("size=", StringComparison.Ordinal)
			&& !repointed.Contains("size=0x0", StringComparison.Ordinal)
			&& Advanced(repointed) > 0;

		// ⚠ Reachable only past the `notReady == 0` gate above, which is what makes it a finding rather than a
		// flattering reading of a 206.
		if (recoveredAlone)
			return $"REMUX-FIRST: PASS — and BETTER than the route documents: the element recovered from the "
				+ $"503 BY ITSELF after {during} request(s), {notReady} of them 503 [{timeline}] "
				+ $"[404 arm, {absent404s} route 404(s): {absent}]";
		if (replays)
			return $"REMUX-FIRST: PASS (as documented) — the element {(erroredOn503 ? "ERRORED on" : "stalled at")} "
				+ $"the 503 and never recovered on its own ({during} request(s) in 12 s, {notReady} of them 503), "
				+ $"and re-pointing src AFTER the plan landed played it: {repointed} [503 timeline: {timeline}] "
				+ $"[404 arm, same element, {absent404s} route 404(s): {absent}]";
		return $"REMUX-FIRST: FAIL — the element did not recover even when re-pointed at a source the route "
			+ $"was serving: re-point={repointed ?? "NO ANSWER"} [503 timeline: {timeline}] [control: {served}] "
			+ $"[404 arm: {absent}]";
	}

	/// <summary>
	/// <b>Bar 1 — a decoded picture out of a file that does not exist.</b> Play the route's URL from the
	/// start and report the RAW values: geometry, duration, readyState, error code, how far the playhead
	/// moved, and what the element believes is seekable.
	/// </summary>
	/// <param name="fixture">
	/// Which staged clip to play — <see cref="Fixture"/>, or <see cref="BigFixture"/> for a film past the
	/// ceiling this route used to decline over. ⚠ A parameter rather than the constant it used to read,
	/// because a size claim tested only against a 468 KB clip is a claim about 468 KB.
	/// </param>
	public static async Task<string> CheckAsync(HybridWebView webView, string sourceRoot, string fixture,
		Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(log);

		// Stage its own source. A missing file is a plain 404 from the route, which reads exactly like "the
		// route declined this source" — the confusion that sent a conversion-tier 404 to TASKS.md as a kit
		// defect twice. ⚠ A fixture pushed onto the device by hand (see BigFixture) is already there, and
		// EnsureStagedAsync returns it untouched rather than looking in the app package for it.
		try
		{
			await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return $"REMUX: FAIL — could not stage {fixture} ({ex.GetType().Name}: {ex.Message}). The route "
				+ "would answer 404 for this, which reads as a planning failure.";
		}

		// The fetch FIRST, because it separates two failures a play() cannot: a route that never answered
		// (404 / fell through — the fixture is unplannable) from one that answered bytes the decoder rejected.
		// 🔴 AND IT RETRIES THE 503, because that is the route WORKING rather than failing — see
		// WaitForPlanAsync. The version of this probe that did not was written when the walk ran inline, and
		// it would have reported a kit defect for doing the right thing (the same false verdict
		// ConversionRouteProbe.CheckAsync records for the conversion route on 2026-08-09).
		var fetched = await WaitForPlanAsync(webView, fixture, log).ConfigureAwait(false);
		log($"[REMUX] fetch -> {fetched ?? "NO ANSWER"}");
		if (fetched is null) return "REMUX: FAIL — the page never answered the fetch";
		if (fetched.Contains("status=404", StringComparison.Ordinal))
			return $"REMUX: FAIL — the route DECLINED {fixture} (404), so no layout was planned. Either the "
				+ $"source is not carriable whole (Plan is lossless) or Resolve's allow-list is missing it: {fetched}";
		if (fetched.Contains("status=503", StringComparison.Ordinal))
			return $"REMUX: FAIL — still 503 after {PlanAttempts}s of retrying, so the metadata walk never "
				+ $"landed an answer for {fixture}: {fetched}";
		// ⚠ BYTES, not just a status. A 206 proves the route answered; only a body proves it produced
		// anything — the same assertion, for the same reason, as the conversion probe's byte count.
		if (fetched.Contains("firstChunk=0", StringComparison.Ordinal)
			|| fetched.Contains("firstChunk=-1", StringComparison.Ordinal))
			return $"REMUX: FAIL — the route answered but no bytes came out of the body: {fetched}";

		var result = await PlayAsync(webView, $$"""
			(function(){
				var v = document.getElementById('vid');
				if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
				v.onloadedmetadata = null; v.onseeked = null; v.onerror = null;
				try { v.pause(); } catch (e) {}
				v.removeAttribute('src'); v.load();
				window.{{Slot}} = 'pending';
				v.muted = true; v.playsInline = true; v.setAttribute('playsinline','');
				v.onerror = function () { window.{{Slot}} = 'MEDIA-ERROR code=' + (v.error ? v.error.code : '?'); };
				v.src = '{{Url(fixture, "p")}}';
				v.load();
				var p = v.play();
				if (p && p.catch) { p.catch(function (e) { window.{{Slot}} = 'PLAY-REJECTED ' + e; }); }
				setTimeout(function () {
					var at = v.currentTime;
					setTimeout(function () {
						window.{{Slot}} = 'size=' + v.videoWidth + 'x' + v.videoHeight
							+ '|dur=' + v.duration
							+ '|ready=' + v.readyState
							+ '|err=' + (v.error ? v.error.code : '-')
							+ '|t=' + v.currentTime.toFixed(2)
							+ '|advanced=' + (v.currentTime - at).toFixed(2)
							+ '|seekable=' + (v.seekable.length ? v.seekable.end(0).toFixed(2) : 'none')
							+ '|buffered=' + (v.buffered.length ? v.buffered.end(0).toFixed(2) : 'none');
					}, 1400);
				}, 900);
				return 'started';
			})()
			""").ConfigureAwait(false);

		log($"[REMUX] play -> {result ?? "NO ANSWER"}");
		if (result is null) return $"REMUX: FAIL — served {fetched} but the page never reported playback";
		if (!result.StartsWith("size=", StringComparison.Ordinal))
			return $"REMUX: FAIL — the computed remux would not play: {result} [fetch: {fetched}]";

		// 🔴 THE GEOMETRY, not the absence of an error. An undecodable picture beside a decodable soundtrack
		// reaches readyState 4 and raises nothing, so this is the ONLY signal — and a probe asserting
		// `!v.error` would report PASS over a blank rectangle.
		if (result.Contains("|size=0x0", StringComparison.Ordinal) || result.StartsWith("size=0x0", StringComparison.Ordinal))
			return $"REMUX: FAIL — NO DECODED PICTURE (size=0x0) from a file the route says it planned. "
				+ $"The layout describes bytes no decoder accepted: {result} [fetch: {fetched}]";
		if (Advanced(result) <= 0)
			return $"REMUX: FAIL — decoded but NOT PLAYING (currentTime did not advance): {result}";

		return $"REMUX: PASS — an MP4 that was never produced decoded and played ({result}) [fetch: {fetched}]";
	}

	/// <summary>
	/// <b>Bar 2 — and it is the whole reason the layout is computed rather than written.</b> On a FRESH
	/// element and a FRESH url, seek to 80 % of the duration before anything has played, then play from
	/// there.
	///
	/// <para>
	/// 🔴 <b>Nothing has produced that region — nothing has produced any region.</b> A segment path has a
	/// production frontier, so a cold seek past it either stalls until the frontier arrives or restarts from
	/// the beginning; a computed layout has no frontier at all, so the bytes at 80 % are exactly as cheap as
	/// the bytes at 0 %. If this stalls, restarts, or lands somewhere other than where it was sent, that is
	/// the finding.
	/// </para>
	/// <para>
	/// ⚠ The nonce matters. Without it the webview may answer the second load out of its own cache, and a
	/// seek served from the page's cache measures the cache rather than the route.
	/// </para>
	/// </summary>
	/// <param name="fixture">
	/// The clip to seek into. ⚠ <b>It must already be PLANNED</b> — call <see cref="CheckAsync"/> for the same
	/// fixture first. A cold seek into a source whose first request is still a 503 measures the 503, which is
	/// <see cref="CheckFirstRequestAsync"/>'s job and a different question.
	/// </param>
	public static async Task<string> CheckColdSeekAsync(HybridWebView webView, string fixture, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(log);

		var result = await PlayAsync(webView, $$"""
			(function(){
				var v = document.getElementById('vid');
				if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
				v.onloadedmetadata = null; v.onseeked = null; v.onerror = null;
				try { v.pause(); } catch (e) {}
				v.removeAttribute('src'); v.load();
				window.{{Slot}} = 'pending';
				v.muted = true; v.playsInline = true; v.setAttribute('playsinline','');
				v.onerror = function () { window.{{Slot}} = 'MEDIA-ERROR code=' + (v.error ? v.error.code : '?'); };
				v.onloadedmetadata = function () {
					window.{{Slot}}Target = v.duration * 0.8;
					try { v.currentTime = window.{{Slot}}Target; }
					catch (e) { window.{{Slot}} = 'SEEK-THREW ' + e; }
				};
				v.onseeked = function () {
					if (window.{{Slot}} !== 'pending') { return; }
					var at = v.currentTime;
					var p = v.play();
					if (p && p.catch) { p.catch(function (e) { window.{{Slot}} = 'PLAY-REJECTED ' + e; }); }
					setTimeout(function () {
						window.{{Slot}} = 'target=' + window.{{Slot}}Target.toFixed(2)
							+ '|landed=' + at.toFixed(2)
							+ '|t=' + v.currentTime.toFixed(2)
							+ '|advanced=' + (v.currentTime - at).toFixed(2)
							+ '|size=' + v.videoWidth + 'x' + v.videoHeight
							+ '|ready=' + v.readyState
							+ '|err=' + (v.error ? v.error.code : '-')
							+ '|dur=' + v.duration
							+ '|paused=' + v.paused;
					}, 1600);
				};
				v.src = '{{Url(fixture, "s")}}';
				v.load();
				return 'started';
			})()
			""").ConfigureAwait(false);

		log($"[REMUX] cold seek -> {result ?? "NO ANSWER"}");
		if (result is null)
			return $"REMUX-SEEK: FAIL — no answer within {Timeout.TotalSeconds:0}s. A cold seek that never "
				+ "completes IS the stall this bar exists to catch.";
		if (!result.StartsWith("target=", StringComparison.Ordinal))
			return $"REMUX-SEEK: FAIL — {result}";

		// Where it LANDED against where it was sent. A player that gave up and restarted reports ~0 here,
		// which is the "it silently played from the beginning" failure — and it looks like success on screen.
		var target = Field(result, "target");
		var landed = Field(result, "landed");
		if (target <= 0) return $"REMUX-SEEK: FAIL — the element never resolved a duration to seek within: {result}";
		if (Math.Abs(landed - target) > 1.5)
			return $"REMUX-SEEK: FAIL — sent to {target:F2}s and landed at {landed:F2}s — a cold seek into an "
				+ $"unproduced region did not land: {result}";
		if (result.Contains("|size=0x0", StringComparison.Ordinal))
			return $"REMUX-SEEK: FAIL — landed, but NO DECODED PICTURE at {landed:F2}s: {result}";
		if (Advanced(result) <= 0)
			return $"REMUX-SEEK: FAIL — landed at {landed:F2}s and playback did NOT continue from there: {result}";

		return $"REMUX-SEEK: PASS — a cold seek to {landed:F2}s of a file that was never produced decoded and "
			+ $"played on ({result})";
	}

	/// <summary>
	/// 🔴 <b>D72 ON A DEVICE: warm the plan from the APP, then prove the page's FIRST request is a 206.</b>
	/// The consumer of <see cref="IComputedRemuxRoute.PlanAsync"/>, and the answer to why this kit publishes no
	/// readiness event — a page that must subscribe to one and set <c>src</c> from a handler is no longer a
	/// plain <c>&lt;video src&gt;</c>, and at that integration cost segments are strictly more capable.
	/// <para>
	/// ⚠ <b>It asks ONCE, and that is the entire test.</b> Every other probe here goes through
	/// <see cref="WaitForPlanAsync"/>, which retries past the <c>503</c> — the behaviour this one must show is
	/// ABSENT. A retry loop anywhere in this method would make it pass whether warming worked or not, which is
	/// the shape of a probe that reports the opposite of its own finding.
	/// </para>
	/// <para>
	/// ⚠ <b>A NONCE-free URL would prove nothing.</b> The webview caches a response per URL, so re-asking one
	/// an earlier probe already fetched could be answered without the route ever seeing it. This uses its own
	/// nonce for the same reason <see cref="CheckColdSeekAsync"/> does.
	/// </para>
	/// </summary>
	public static async Task<string> CheckWarmedAsync(IComputedRemuxRoute route, HybridWebView webView,
		string sourceRoot, string fixture, Action<string> log)
	{
		ArgumentNullException.ThrowIfNull(route);
		ArgumentNullException.ThrowIfNull(log);

		try
		{
			await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return $"REMUX-WARM: FAIL — could not stage {fixture} ({ex.GetType().Name})";
		}

		var source = Path.Combine(sourceRoot, fixture);
		var started = DateTime.UtcNow;
		var outcome = await route.PlanAsync(source, CancellationToken.None).ConfigureAwait(false);
		var took = (DateTime.UtcNow - started).TotalSeconds;
		log($"[REMUX-WARM] PlanAsync({fixture}) -> {outcome} in {took:F2}s");

		if (outcome != MediaPlanOutcome.Ready)
			return $"REMUX-WARM: FAIL — warming answered {outcome} for {fixture}, so the app has nothing to "
				+ "point an element at; a Ready was expected for a fixture the other probes plan happily";

		// ONE ask, with a nonce so it cannot be served out of the page's cache. No retry: a 503 here is the
		// finding.
		var nonce = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var report = await FetchRangeAsync(webView, Url(fixture, nonce)).ConfigureAwait(false);
		log($"[REMUX-WARM] first request -> {report ?? "NO ANSWER"}");

		if (report is null) return "REMUX-WARM: FAIL — the page never answered the fetch";
		if (report.Contains("status=503", StringComparison.Ordinal))
			return $"REMUX-WARM: FAIL — the FIRST request after warming was still a 503, which is D72's claim "
				+ $"failing: warming did not reach the cache the request path reads ({report})";
		if (!report.Contains("status=206", StringComparison.Ordinal))
			return $"REMUX-WARM: FAIL — expected 206 on the first request after warming ({report})";

		return $"REMUX-WARM: PASS — the app warmed {fixture} in {took:F2}s and the page's FIRST request was a "
			+ $"206, with no 503 and no retry anywhere ({report})";
	}

	/// <summary>
	/// One ARM of <see cref="CheckFirstRequestAsync"/>: point the page's element at <paramref name="url"/> and
	/// park a TIMELINE of its raw state — every event it raises, plus a sample at 0.3 s and then at doubling
	/// intervals out to <paramref name="seconds"/>.
	/// <para>
	/// ⚠ <b>ONE script for both arms, deliberately.</b> The 404 and the 503 arms are an A/B on the same
	/// element, so any difference between how they are DRIVEN is a difference in the result that says nothing
	/// about the status code — the trap `phase-workflow.md` records as A/B-ing the harness instead of the code.
	/// </para>
	/// <para>
	/// ⚠ <b>The ONE difference between the arms is <paramref name="seconds"/> — 4 for the 404, 12 for the 503 —
	/// and it is stated rather than hidden behind the word "identical".</b> Only the 503 arm has a reason to
	/// watch longer: <c>Retry-After: 1</c> invites a retry and the plan lands within it, so 12 s covers several
	/// chances to recover, while a 404 is final by definition and a 5th sample of the same frozen state
	/// establishes nothing. Both arms sample at the SAME points (0.3/1/2/4 s) over the window they share, so
	/// the comparison is between like readings; the 503 arm simply has three more afterwards.
	/// </para>
	/// <para>
	/// ⚠ <b><c>error.message</c> is sampled too, and it is the one field that is NOT comparable across shells:</b>
	/// it is UA-specific and non-normative (Chromium writes <c>MEDIA_ELEMENT_ERROR: …</c>, WebKit writes its
	/// own or nothing), so no page can portably branch on it. It is here because leaving it out let a report
	/// claim two timelines were identical without having looked at every field either of them had.
	/// </para>
	/// </summary>
	private static string ArmScript(string url, int seconds)
	{
		var samples = new[] { 300, 1000, 2000, 4000, 8000, 12000 }
			.Where(ms => ms < seconds * 1000).Append(seconds * 1000);
		return $$"""
			(function(){
				var v = document.getElementById('vid');
				if (!v) { window.{{Slot}} = 'NO-VIDEO-ELEMENT'; return 'no element'; }
				v.onloadedmetadata = null; v.onseeked = null; v.onerror = null; v.onplaying = null;
				try { v.pause(); } catch (e) {}
				v.removeAttribute('src'); v.load();
				window.{{Slot}} = 'pending';
				v.muted = true; v.playsInline = true; v.setAttribute('playsinline','');
				var t0 = Date.now();
				var marks = [];
				var at = function () { return ((Date.now() - t0) / 1000).toFixed(2); };
				var state = function () {
					return 'ready=' + v.readyState + ' net=' + v.networkState
						+ ' size=' + v.videoWidth + 'x' + v.videoHeight
						+ ' dur=' + v.duration + ' err=' + (v.error ? v.error.code : '-')
						+ ' msg=[' + (v.error && v.error.message ? v.error.message : '') + ']'
						+ ' t=' + v.currentTime.toFixed(2);
				};
				v.onerror = function () { marks.push('ERROR@' + at() + ' ' + state()); };
				v.onloadedmetadata = function () { marks.push('METADATA@' + at() + ' ' + state()); };
				v.onplaying = function () { marks.push('PLAYING@' + at() + ' ' + state()); };
				v.src = '{{url}}';
				v.load();
				var p = v.play();
				if (p && p.catch) { p.catch(function (e) { marks.push('PLAY-REJECTED@' + at() + ' ' + e.name); }); }
				var last = {{seconds * 1000}};
				[{{string.Join(", ", samples)}}].forEach(function (ms) {
					setTimeout(function () {
						marks.push('t+' + (ms / 1000).toFixed(1) + 's ' + state());
						if (ms === last) { window.{{Slot}} = marks.join(' || '); }
					}, ms);
				});
				return 'started';
			})()
			""";
	}

	/// <summary>The route url for one attempt. The nonce defeats the page's own response cache.</summary>
	private static string Url(string fixture, string nonce) =>
		$"{RoutePath}?{fixture}&{nonce}{DateTime.UtcNow.Ticks}";

	/// <summary>How many one-second attempts <see cref="WaitForPlanAsync"/> gives a metadata walk.</summary>
	private const int PlanAttempts = 25;

	/// <summary>
	/// 🔴 <b>RETRY THE 503 UNTIL THE PLAN LANDS — the loop the route's contract requires of a client, and the
	/// piece this probe was missing.</b> The first request for an unplanned source submits a mission and
	/// answers <c>503 Retry-After: 1</c>, so a client that reads that as a failure reports one for the route
	/// doing exactly what it says it does.
	///
	/// <para>
	/// ⚠ <b>What this shares with <see cref="ConversionRouteProbe.CheckAsync"/>'s loop is the INTERVAL, and
	/// that is the part that is a contract</b> — one second, because <c>NotReadyYet</c> sends
	/// <c>Retry-After: 1</c> and copies of a number can drift apart while every test passes. ⚠ <b>It is
	/// otherwise a second copy of eight lines, and the ATTEMPT BUDGET deliberately differs</b> (25 here, 20
	/// there): the two are waiting for different work — a metadata walk of a film versus a whole transcode —
	/// and a budget shared between them would be a number tuned for neither. Extracting the loop was
	/// considered and refused: it would put a helper between two probes to save eight lines while making the
	/// one value that must NOT be shared look shared.
	/// </para>
	/// <para>
	/// 🔴 <b>A RANGE, AND ONLY ITS FIRST CHUNK — and that is about the harness rather than the route.</b>
	/// Under <c>WebViewRangeDelivery.Unsliced</c> (Android) the platform hands the page the WHOLE output
	/// whatever range was asked for: measured 2026-08-13, a <c>Range: bytes=0-65535</c> fetch of the 82,843,185
	/// byte film delivered all 82,843,185 of them in 117,285 reads of 2 KiB, taking 26–31 s — over
	/// <c>PageProbe</c>'s budget and right at this one's, so the probe would report NO ANSWER about a route
	/// that answered perfectly. Reading ONE chunk and cancelling proves the three things a media element needs
	/// — a <c>206</c>, a real total in <c>Content-Range</c>, and bytes that actually flow — in under a second.
	/// </para>
	/// <para>
	/// ⚠ <b>Its OWN slot and its OWN 30 s budget, not <c>PageProbe</c>'s 10 s.</b> A big film's request costs
	/// a whole-output read on Android, which can outlast the shared budget — and "no answer" from a probe
	/// whose timeout was tuned to a 468 KB clip is a harness verdict wearing a route's name.
	/// </para>
	/// </summary>
	/// <returns>The last report the page produced, or null if it never answered at all.</returns>
	private static async Task<string?> WaitForPlanAsync(HybridWebView webView, string fixture, Action<string> log)
	{
		string? report = null;
		for (var attempt = 0; attempt < PlanAttempts; attempt++)
		{
			report = await FetchRangeAsync(webView, Url(fixture, "f")).ConfigureAwait(false);
			if (report is null || !report.Contains("status=503", StringComparison.Ordinal)) break;
			if (attempt == 0)
				log($"[REMUX] 503 + Retry-After — the metadata walk for {fixture} is running as a mission; polling…");
			await Task.Delay(PageProbe.RetryAfter).ConfigureAwait(false);
		}
		return report;
	}

	/// <summary>
	/// Ask a url for its first 64 KiB from inside the page and report the status, the FIRST CHUNK's size and
	/// every header — the CONTROL that separates "the route never answered" from "it answered bytes the
	/// decoder rejected" (the lesson `/media`'s 404s taught: <c>err=4</c> is not a codec verdict).
	/// </summary>
	private static async Task<string?> FetchRangeAsync(HybridWebView webView, string url)
	{
		var started = await PageProbe.EvaluateAsync(webView, $$"""
			(function(){
				window.{{Slot}} = 'pending';
				fetch('{{url}}', { headers: { 'Range': 'bytes=0-65535' } })
					.then(async function (r) {
						var pairs = [];
						r.headers.forEach(function (v, k) { pairs.push(k + '=' + v); });
						var first = 0;
						try {
							var reader = r.body.getReader();
							var chunk = await reader.read();
							first = chunk.value ? chunk.value.byteLength : 0;
							await reader.cancel();
						} catch (e) { first = -1; }
						window.{{Slot}} = 'status=' + r.status + '|firstChunk=' + first + '|' + pairs.join('; ');
					})
					.catch(function (e) { window.{{Slot}} = 'FETCH-THREW ' + e; });
				return 'started';
			})()
			""").ConfigureAwait(false);
		return started is null ? null : await PollAsync(webView).ConfigureAwait(false);
	}

	/// <summary>Start a script and poll <see cref="Slot"/> until it parks an answer.</summary>
	private static async Task<string?> PlayAsync(HybridWebView webView, string script)
	{
		var started = await PageProbe.EvaluateAsync(webView, script).ConfigureAwait(false);
		return started is null ? null : await PollAsync(webView).ConfigureAwait(false);
	}

	/// <summary>Poll <see cref="Slot"/> until whatever was started parks an answer there.</summary>
	private static async Task<string?> PollAsync(HybridWebView webView)
	{
		var deadline = DateTime.UtcNow + Timeout;
		while (DateTime.UtcNow < deadline)
		{
			await Task.Delay(300).ConfigureAwait(false);
			var value = await PageProbe.EvaluateAsync(webView, $"window.{Slot}").ConfigureAwait(false);
			if (value is not null && value.Length > 0 && value != "pending" && value != "null") return value;
		}
		return null;
	}

	/// <summary>One numeric <c>name=value</c> field out of a report line; 0 when absent or unparsable.</summary>
	private static double Field(string report, string name)
	{
		foreach (var field in report.Split('|'))
		{
			if (!field.StartsWith($"{name}=", StringComparison.Ordinal)) continue;
			return double.TryParse(field[(name.Length + 1)..], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
		}
		return 0;
	}

	/// <summary>How far the playhead moved during the watch window. Absent or negative reads as zero.</summary>
	private static double Advanced(string report) => Field(report, "advanced");
}
