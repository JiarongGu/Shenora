# Media — the harvest, and what it says to build

**Status: HARVEST + DESIGN, nothing built.** Written 2026-08-03 for `TASKS.md` D1–D5. D1's instruction
is the reason this doc exists and leads with evidence: *three consumers means three existing
implementations, and the design is IN them, not ahead of them.* Retire this doc once the work lands
(`docs/README.md`'s rule) — the WHYs move to `docs/DECISIONS.md`, the surface to `ARCHITECTURE.md`.

Owner direction (2026-08-02): *"we also need to add Media library into roadmap (this also why I push for
interface library merge, because 3 of my application will need this)"*. Sonora may be named (D12); the
other two are the **video-library sibling** and the **business-manager sibling**.

## §0 Evidence — what the three actually do

Read before designing, per `generic-library.md`'s "go and look". Everything below is from the source,
not from memory.

| | Video-library sibling | Sonora (public) | Business-manager sibling |
|---|---|---|---|
| Probe | `MediaService.ProbeAsync` → ffprobe `-show_format -show_streams` (whole JSON) → `MediaInfo { DurationSeconds, Width, Height, Codec, AudioCodec, FrameRate, Tags }` | `MediaProbe.TryGetInfo` → ffprobe **targeted** `-show_entries stream=codec_name format=duration` → `(double? DurationSec, string? Codec)`. **WAV is parsed from the RIFF header — no process at all** | none: it does not probe media, it **generates** it |
| Tool discovery | `MediaEngineToolLocator`: its own `{APP}_FFMPEG_DIR` env var → bundled `tools/ffmpeg` next to the exe → PATH (directory scan) | `FfmpegLocator`: `SONORA_FFMPEG_DIR` → bundled `tools/ffmpeg` → PATH (`-version` runnability probe) | provisions Node+FFmpeg for ONE method; the other needs neither |
| Thumbnail | ffmpeg: seek 10 % of duration capped at 10 s, `-frames:v 1`, `scale=480:-2`, `-q:v 3` → JPEG | ImageSharp resize → **WebP q80**, DOWNSCALE-ONLY, ×2 ladder (360 / 720 / 1440) | n/a |
| Cache key | `sha256(path \| length \| mtimeTicks)` truncated to 16 hex | `{key}_{size}_{mtimeTicks}.webp` on disk + a 10-minute in-memory memo | content-addressed asset rows in the app's SQLite |
| Missing tool | `IsAvailable == false` ⇒ no thumbnails, no probed metadata, **nothing fails** | ffprobe absent ⇒ durations stay null, **nothing breaks** | `CheckAsync` reports availability **per method** |
| Cache governance | none — a private folder per service | `CacheRegistry`: every regenerable cache inventoried, capped, sweepable | n/a |

### The four convergences

1. **Tool discovery is the same three-step ladder, written twice.** `{APP}_FFMPEG_DIR` → bundled
   `tools/ffmpeg` beside the executable → PATH. And this is not inference: **Sonora's own XML calls it
   "(family MediaToolLocator)"** and its `MediaProbe` repeats "Resolution order for ffprobe follows the
   family MediaToolLocator". One sibling documenting that it reimplemented another's component is the
   two-consumer bar met **on evidence**, exactly like the two independent launchers in
   `2026-08-02-shenora-app-update-design.md` §0.
2. **Degrade, never fail.** Both make "no ffmpeg here" a first-class state rather than an error, and
   both say so in the type's own doc. Neither throws; callers get `false`/`null` and carry on.
3. **The cache key is identity + mtime.** Different encodings, identical rule: replaced source bytes
   must produce a different key. Neither trusts a path alone.
4. **A probe result is BEST-EFFORT and every field is nullable.** The video sibling's `MediaInfo` says
   it in a comment ("All fields are best-effort; any may be null/0"); Sonora's return type is a tuple of
   two nullables. Same admission, reached separately.

### The five disagreements — the interesting part, as D1 predicted

1. **What a probe RETURNS.** One fat record from the whole ffprobe JSON, versus a narrow tuple from a
   targeted query because duration+codec is all that consumer ever needs and the narrow query is
   cheaper. **A kit that ships only the fat shape taxes the narrow consumer on every call.**
2. **Who decodes the picture — and "thumbnail" means two different operations.** The video sibling
   *extracts a frame* by shelling out to ffmpeg; Sonora *resizes an image* with a managed decoder,
   because its sources are cover scans, not video. Same word, different pipeline, different dependency.
   This is D35's shape (`OpenFolderAsync`) arriving in a new area, and it is the single most important
   finding here.
3. **DOWNSCALE-ONLY is a measured rule that only ONE of them has.** Sonora refuses to re-encode a
   source already inside the target box and records why: Max-resizing a 560 px jacket to 720 measured
   **73 KB WebP against the 37 KB original — bigger AND blurrier**, and the re-encode is the slow part.
   The video sibling always re-encodes at `scale=480:-2`. A merged implementation must carry the rule.
4. **Each locator has a bug the other fixed.** The directory scan is cheap but never verifies the binary
   RUNS; the `-version` probe verifies but had to learn that **both pipes must be drained before
   `WaitForExit`** or `ffmpeg -version`'s multi-KB build banner fills the OS pipe buffer, deadlocks the
   child, and the probe wrongly concludes ffmpeg is unavailable. `extraction-sources.md` says merge, not
   pick: cheap scan as the fast path, runnability check for the verdict, drain-before-wait mandatory.
   Sonora also learned the same lesson on the READ side — `Image.LoadAsync(path)` opens a small-buffer
   stream, and over SMB that measured **19.9 s cold for a 5.5 MB file versus ~0.5 s** with a 1 MB
   sequential-scan buffer.
5. **The third sibling INVERTS the problem, and that is why it counts as a consumer.** It never probes a
   file. It renders an HTML composition to MP4 through a method-pluggable `IVideoProvider` (in-WebView
   WebCodecs by default, headless-Chrome+FFmpeg as an opt-in that must be provisioned first), then
   `RegisterBytes(...)` into a content-addressed store and hands the page back a resolvable URL. **Its
   need from a media library is "take these bytes, store them, give me a handle", not "tell me about
   this path".** Any surface that assumes media is a file you interrogate excludes it.

### §0b PLAYBACK — the actual driver, and the two siblings chose OPPOSITE strategies

Owner direction (2026-08-03): *"mostly you need to focus for a more native support video/audio player
for this, web does not have full support on video/audio types"*. That is the requirement, and the first
draft of this doc got it wrong by ruling playback out on D21 grounds. The evidence was already here:

**The problem, stated by the code.** A webview plays only what its bundled media stack decodes. Sonora's
`PlaybackPlanner` names the boundary exactly: *"Browsers decode mp3/aac/flac/vorbis/opus/PCM natively —
those stream as-is (hi-res included, untouched). Everything else (alac, ape, wavpack, tak, wma, DSD, …)"*
cannot play. The video sibling's `PlaybackModes` names the video half: HEVC / VC-1 / MPEG-2 can't decode,
and H.264-in-MKV can't play either **even though the codec is fine**, because the container isn't.

| | Video-library sibling | Sonora |
|---|---|---|
| Strategy | **Native player.** LibVLC surface layered into the app window, web UI on top (a dedicated player project, ~5.9k lines); libmpv for a shader path | **Convert to something the web CAN play.** Lossless FLAC transcode cache for audio; HLS segmenter for streaming |
| Decision layer | `PlaybackPlanner` → `PlaybackModes { direct, remux, transcode, unsupported }` + `PlaybackPlan` | `PlaybackPlanner.NeedsTranscode(codec, file)` |
| Fallback when it cannot be made playable | `unsupported` ⇒ hand off to the OS player | transcode always possible (FLAC target) |

**The convergence is the DECISION, and again one sibling credits the other.** Sonora's `PlaybackPlanner`
XML opens *"(a sibling project's PlaybackPlanner, audio-only)"* — the second instance in this harvest of
a sibling documenting that it reimplemented the other's component. Both:
- probe for the **real** codec because **extensions lie** — Sonora's `MediaProbe` says so explicitly
  (*"e.g. 'alac' inside .m4a — extensions lie, and the playback planner needs the truth"*);
- fall back to the **extension guess** when no probe is available, and deliberately do NOT punish that
  case with a needless transcode;
- treat "web-playable" as a **set membership test over codec names**, not a file-type test.

**THE FINDING THAT DECIDES THE DESIGN: the native surface is a mechanism this kit already ships.**
`NativePlayerManager`'s own header says it uses *"the same technique as `DropZoneManager` (CSS→physical-px
via `DpiHelper`, then PointToScreen→PointToClient → SetBounds on the parent form)"*, and it carries a
`#region Bounds math (same CSS→physical→form conversion as DropZoneManager)`. Shenora already ships
`DropZoneManager` doing exactly that — transparent native overlays synced to page-element rects, per-monitor
DPI conversion, `DpiChanged` re-apply, zones cleared on `ContentLoading`. **A native video surface is that
same mechanism with a different payload**, which is why this belongs in the kit and not in each app.

Its host seams are also things Shenora already has better versions of: `INativePlayerIpc.SendNotification`
is `IEventBus`/`NotificationPump`; `INativePlayerLog` is `ILogger`; and `IPlayerHostForm.IsAppMaximized` is
**literally `IAppMaximizable`**. That sibling had to define three interfaces to stay a leaf — an adopter on
Shenora defines none.

### §0c MOBILE — no sibling evidence exists, so the platforms were asked directly

Owner direction (2026-08-03), and it is the motivation for the whole item: *"mostly no mobile thats an
issue, so thats why we here"*. All three sibling implementations are DESKTOP, so there is no code to
harvest for this half. Rather than design it from a principle — the mistake the first draft of this doc
made on scope — the platform APIs were **verified by compiling** against `net10.0-android` and
`net10.0-ios`, the technique that settled the save picker.

**Everything below compiled clean with NO extra NuGet package — base platform SDK only:**

| Capability | Android | iOS |
|---|---|---|
| Video surface | `Android.Widget.VideoView`, or `MediaPlayer.SetDisplay(SurfaceView.Holder)` for full control | `AVPlayerLayer.FromPlayer(player)` added to **any `UIView`'s layer tree** |
| Full-screen handoff | (platform intent) | `AVKit.AVPlayerViewController` |
| "Can this be decoded?" | `MediaCodecList(AllCodecs).GetCodecInfos()` | `AVAsset.Playable` — a per-asset verdict |
| Duration / metadata | `MediaMetadataRetriever.ExtractMetadata(MetadataKey.Duration)` | `AVAsset.Duration.Seconds` |
| Thumbnail | `MediaMetadataRetriever.FrameAtTime` | `AVAssetImageGenerator.FromAsset(asset)` |

**THE ASYMMETRY IS THE OPPOSITE OF WHAT I ASSUMED, and it is the most useful thing in this harvest.**
On mobile **the platform IS the engine** — playback, probing and thumbnails all arrive with no
dependency, no bundled binary and no licence question. On **Windows** there is no comparable built-in
engine reachable from a WinForms host, which is precisely why both desktop siblings had to bundle
LibVLC or ffmpeg themselves. So the constraint the kit must model is *"the desktop needs an
app-supplied engine; mobile does not"*, not the usual "mobile is the limited one".

Two consequences that change §1 rather than decorate it:

1. **The playability DECISION is a codec-set TABLE on Windows and a QUESTION on mobile.** There is
   nothing to ask a webview, so the desktop must carry the set the two siblings both hand-maintain —
   while Android and iOS can ask the platform per asset. Same contract, genuinely different
   implementations, and this time the mobile side is the more accurate one. That is D33/D35's shape with
   the usual polarity reversed.
2. **Layering is structurally EASIER on mobile.** Windows needed CSS→physical→form bounds math plus
   `SetWindowPos` and a `DpiChanged` re-apply — the `DropZoneManager` machinery. On Android the surface
   is a sibling `View` in the tree; on iOS it is a sublayer of a `UIView` the app already owns. The
   portable contract must therefore not be shaped around the Windows overlay hack, or it will make the
   two easy platforms implement a workaround they do not need.

⚠ **What this does NOT establish.** That these APIs exist is not that they work in a MAUI
`HybridWebView` composition, and it is certainly not that the codecs an app wants are present on a given
device (`MediaCodecList` is a device-by-device answer, which is the whole reason it is queryable). Those
need a device run, exactly as the save picker did — and the save picker is the precedent for how much a
device run finds that a build cannot.

### What the evidence says NOT to lift

The video sibling's `MediaService` is 669 lines and **only about four of its fifteen methods are
generic** (probe, extract-a-frame, thumbnail, cache key). The rest is product: raw BGRA frame windows
for AI processing, ONNX/NCNN upscale + face-restore engines, encoder selection, audio extraction at
16 kHz for a speech model, in-place sharpen, segment cutting, playback planning and transcode
preparation. That is the strongest possible support for D4 — the scope boundary is not a guess, it is
where these three stop agreeing.

## §1 The claim — PLAYABILITY is the centre, and it splits into five parts

Reordered from the first draft, which treated thumbnails as the centre and playback as out of scope.
The evidence says the reverse: thumbnails are the easy part, and "the web cannot play this file" is the
problem all three consumers actually have.

1. **The playability DECISION (portable, pure).** Given a probed codec/container — or only an extension
   when nothing probed it — what must happen: stream as-is, remux the container, transcode, hand to a
   native surface, or admit defeat. Written twice already, and the two agree on the shape while
   disagreeing on the verdicts. **Pure function, no I/O, fully unit-testable** — the same profile as
   `ManifestDiff`, which is why that one is sabotage-verified in one place.
2. **A native media SURFACE (portable contract, per-platform implementation).** A region the app hands
   over so an engine can draw into it, positioned from the page's CSS rects and kept in sync through
   DPI changes and resizes. **On Windows this is `DropZoneManager`'s mechanism with a different
   payload** — the kit already owns the hard part. On mobile it is a platform view behind the same
   contract.
3. **A probe RESULT shape (portable data).** Nullable, best-effort, narrow-or-fat — see disagreement 1.
   The decision layer in (1) consumes this, which is why "extensions lie" matters: the whole point of
   probing is that the planner needs the truth.
4. **A content-keyed cache with governance (portable).** Key from identity + mtime; inventory, cap and
   sweep as a seam. Sonora is the only one with the governance half and it is the piece the others
   visibly lack.
5. **Producing pixels for a THUMBNAIL (per-platform).** Media Foundation / WIC on Windows,
   `MediaMetadataRetriever` + `ThumbnailUtils` on Android, AVFoundation + `QLThumbnailGenerator` on iOS
   — plus, on the desktop, an external ffmpeg the APP supplies.

**Placement — superseded by D40 (owner, 2026-08-03): media is its OWN package, `Shenora.Media`.** An
earlier revision of this section said "no new package (D2)"; the owner's call overrides it and the
reasoning is stronger than the one it replaces. The argument is **dependencies, not size**: `Shenora.Core`
costs a consumer exactly two abstraction packages today, EVERYTHING references Core, and an image codec or
container parser is real shipped bytes rather than an Evergreen system component — so it passes D37's
"does a consumer experience this boundary?" test that the Windows split failed. Full reasoning in D40.

**The set is NINE packages**, and the split runs the whole way down:

| Package | Holds | A consumer declines it by… |
|---|---|---|
| `Shenora.Core` | the **generic connection functions** — `WebViewResourceRequest`/`Response`/`ByteRange`, moved out of `Shenora.Windows`. Not media-specific: the bundle seam and app schemes use them, and they carry no dependencies | — (everyone takes Core) |
| `Shenora.Media` | parts 1, 3, 4 — the playability decision, the probe result shape, the cache key + inventory seam, the surface/thumbnail contracts | not doing media at all |
| `Shenora.Media.Windows` `.Android` `.iOS` | parts 2, 5 — the native surface, pixel production, and the platform's own playability answer. The natural home for whichever engine binding a consumer opts into | not doing media **on that platform** |

**Why the implementations are NOT in the shell packages** (this section said the opposite until the owner
corrected it): `Shenora.Windows` is not optional for a desktop app on this kit, so media dependencies
placed there would tax every consumer — a tray utility that never plays a file would still restore an
engine binding. `Shenora.Media.Windows` is optional. **The reason Media splits from Core is the reason
Media.Windows splits from Windows**, and the first draft applied that argument once and stopped. D37 is
untouched: its rule is one SHELL per platform, and these are a FEATURE's platform implementations, which
its own "does a consumer experience this boundary?" test admits.

And this remains the sharpest justification for the per-platform shape generally: a native video surface is
implemented three completely different ways and must not leak into app logic.

⚠ **One edge to determine when building:** whether `Shenora.Media.{Platform}` references its shell package
or only `Shenora.Core` + the platform SDK. A surface needs UI-thread marshalling (`IUiDispatcher`,
portable) and a parent control (platform). If it needs nothing else from the shell, that edge should not
exist — a declared dependency nothing crosses is one of the five defects the review checklist hunts for.

## §2 What must NOT leak in (D4) — the line is the ENGINE, not playback

The first draft drew this line at "no playback" and that was wrong: the kit owes the SURFACE, the
DECISION and the SEAMS, because without them every app re-derives the DPI-correct bounds sync and the
codec-set test. What the kit must never do is **be** the player:

- **No media engine, and no shipping or downloading of one.** No LibVLC, no libmpv, no ffmpeg binary.
  Both siblings that use ffmpeg bundle their own beside their own executable — that is an application
  packaging decision, and a kit that shipped one would double every consumer's installer and pick their
  licence for them. The kit locates what the app provided (§0's three-step ladder) and reports absence.
- **No codec POLICY.** The direct-play sets differ between the two siblings and both are right for their
  domain: audio-only versus video containers. The kit ships the mechanism for "is this in the set" and
  the app owns the set.
- **No player CHROME.** The video sibling's ~5.9k lines are mostly UI — layered chrome, menus, toasts,
  a beauty panel. That is product, and D13 forbids the kit having a design system anyway.
- **No transcode ladder, no HLS packaging, no editing, no AI/upscale.** Those are products.

The test is D21's, and it now passes in the useful direction: *could a consumer build its own player on
these primitives without adopting our decisions?* The video sibling would keep LibVLC and its chrome;
Sonora would keep FLAC-transcoding and never create a surface at all. Both would delete their bounds
math and their locator.

### §0d The owner's proposal — transcode in C# and hand it to the webview

Owner, 2026-08-03: *"so we can build a transcode layer in c# and pass that to webview? since whatever the
player it use on mobile also has this logic"*. This is Sonora's strategy generalised, and probing it
turned up the single most consequential fact in this document.

**THE SEAM EXISTS ON MOBILE, and this repo's own docs said it did not.** `HybridWebView` in .NET 10 has
`WebResourceRequested`, and the args carry everything a ranged media response needs — verified by
compiling, not read from documentation:

| Member | Type / shape |
|---|---|
| `e.Uri` | the request |
| `e.Headers` | `IReadOnlyDictionary<string, string>` — **so the `Range` header is readable** |
| `e.SetResponse(...)` | `(int status, string reason, string contentType, Stream)` — **206 accepted** |
| `e.Handled` | claim the request |
| `e.PlatformArgs` | escape hatch to the native args |

`ADOPTION.md` told adopters the opposite ("`HybridWebView` has no request-interception seam… deferred
schemes have no role"), which would have made an adopter design around a limitation that no longer
exists. Corrected. And note the shape: `URI + headers in → status + content-type + stream out` is
**exactly** `WebViewDeferredScheme`'s existing contract, which the kit already ships with
`WebViewByteRange.TryParse` and `WebViewResourceResponse.PartialContent`. The abstraction is already
right; it is only Windows-only by accident of when it was written.

**So the proposal works on all three shells, with no HTTP server.** That is a genuinely better answer
than a native surface for the common case, and it keeps the frontend a `<video>` tag — the owner's
standing "make the frontend as universal as we can".

**One premise needs correcting, and it changes where the strategy applies.** Mobile players
(ExoPlayer/AVPlayer) **decode**; they do not transcode. So the logic a mobile player shares is *hardware
decoding*, not conversion. Transcoding on a phone means spending battery and thermal budget to produce
something *worse* than what the platform would have decoded natively for free — and HEVC→H.264 is often
slower than realtime on mobile silicon. That is the exact codec class that motivated this work, so the
distinction is load-bearing rather than pedantic.

**Which splits the work three ways, and the two siblings already named the split.** `PlaybackModes`
distinguishes `remux` from `transcode` for precisely this reason:

| Case | Cost | Where it belongs |
|---|---|---|
| **Direct** — webview can decode it | none | serve the original bytes; already possible today |
| **Remux** — codec fine, container wrong (H.264-in-MKV) | **stream copy, cheap** | the portable C# layer the owner is proposing. Biggest win per unit of effort |
| **Transcode** — codec unsupported | expensive; needs an engine | desktop fallback (no built-in engine to decode with). On mobile prefer the NATIVE surface, because the platform decodes it free |

**And transcode still needs an engine, which is the thing §2 says the kit must not ship.** Sonora
transcodes with a bundled ffmpeg. Shipping one for mobile means a native binary per ABI plus a GPL/LGPL
decision the kit must not make for its consumers. So: the kit ships the DECISION, the SERVING seam and
the CONTRACTS; the app brings any engine, on any platform.

**Net: the two strategies are complementary, not alternatives** — which is why both siblings have a
planner. Serving-to-the-webview is the default path and now reaches all three shells; the native surface
earns its place exactly where the platform can decode for free and transcoding would be the wrong trade.

### §0e Who actually benefits from what — and it is NOT evenly spread

Asked directly (owner, 2026-08-03): would a native/interception path let Sonora drop its transcode
layer? **No, and it is the consumer that most needs to keep it.** `PlaybackController` serves
`api/tracks/{id}/stream` over HTTP and calls `PlaybackPlanner.NeedsTranscode` at that point; the repo
carries `src/client/android` and `src/client/ios` (Capacitor), and `DESIGN.md` describes "HTTP(S) over
LAN… the server keeps serving the LAN… connected devices".

**Its playback clients are remote devices it does not control**, so a native engine on the SERVER cannot
help a browser on a phone across the LAN — the server has to send bytes the CLIENT can decode. Three
further reasons it stays: the webview-interception seam is irrelevant to a server-backed profile that
already has a real HTTP origin; it has no native surface today, so adding one is new work rather than a
deletion; and its transcode is a LOSSLESS cache, so repeat plays are already cheap and quality is
untouched, whereas native decode recurs on every play on every device.

**This corrects the two-consumer arithmetic in `TASKS.md`.** "Three of my applications will need this" is
true of media as a whole, but not of each part — and the parts have very different support:

| Kit piece | Video-library sibling | Sonora | Business-manager sibling |
|---|---|---|---|
| **Playability DECISION** | needs (has `PlaybackModes`) | needs (has `PlaybackPlanner`) | — |
| Probe (duration/codec) | needs (has one) | needs (has one, **with a WAV fast path the kit should take**) | — |
| Content-keyed cache | needs (has one) | **donor** (has the only governed one) | has its own, content-addressed |
| Thumbnail | needs | **donor** (downscale-only + the SMB lesson) | — |
| **Native surface** | needs (built ~5.9k lines) | no — remote clients | — |
| Serving seam (Range/206) | needs | no — has HTTP already | — |
| Byte-store → handle | — | — | **only consumer** |

**So the build order follows the evidence, not the enthusiasm:** the **playability decision** is the only
piece two consumers both have and both would delete, and it is a pure function with no I/O — the cheapest
thing to ship and the easiest to get right. The native surface has ONE existing consumer plus the stated
mobile need; the byte-store has one. Sonora is mostly a DONOR here, which is a different and equally
useful role — its `ThumbnailService`, `CacheRegistry` and WAV fast path are the best in the family.

### §0f THE SYNTHESIS — a transcode "layer" is a COMPOSITION of things the kit already ships

Owner, 2026-08-03: *"it just like how you process and read file"*. That is the resolution, and it matches
their own earlier direction quoted in `TASKS.md`: *"compress is just one single case, think about any
processing to file logic, video encoding, code building"*.

A transcode is not a new mechanism. It is: decide it is needed → derive a cache path → do a long
conversion → put the result there atomically → do not start it twice → serve it. **The kit already owns
four of those five**, and `Files.BeginReplace`'s XML already names the composition: *"Concurrency is the
caller's… hold a `MissionClaim` on the path — which is what `IMissionScheduler` is for, and a long
transform belongs there anyway."*

| Step | Who owns it |
|---|---|
| "Does this need converting?" | **the one new piece** — the playability decision (§1.1) |
| Cache path from identity + mtime | a small portable helper; both siblings hand-rolled the same rule (§0) |
| Run the long conversion | **`IMissionScheduler`** — already shipped, with lanes for budget |
| Don't transcode the same file twice | **`PathClaims.Exclusive`** — already shipped; Sonora hand-built the same idempotency ("an existing queued/running/paused job isn't duplicated") |
| Write the output without destroying anything | **`Files.BeginReplace`** — already shipped, atomic, and built for exactly this |
| Serve the result | **already shipped** — deferred scheme + `WebViewByteRange`/`PartialContent` on desktop, the same seam on mobile (§0d), or the app's own HTTP for a server-backed profile |
| Invoke the encoder | **the APP** — the kit ships no engine (§2, §4) |

**So the deliverable shrinks to the decision layer plus a documented composition**, which is the best
possible outcome: no new engine dependency, no new subsystem, and the pieces are already gate-covered and
sabotage-verified. An adopter's transcode module becomes planner (kit) + mission with a path claim (kit) +
`FileReplacement` (kit) + its own encoder call (theirs).

**Two corrections while here, since both were raised.** Sonora's server is not a transcode host — it
carries **19 modules** (Catalog, Scan, Metadata, Plugin, Proxy, Downloads, Jobs, Playlists, Llm, Auth,
Update…), and it exists because the NAS is storage-only while the PC has the compute and the LAN has
clients. And its ffmpeg is not transcode-only either: it backs **four** services — `MediaProbe`,
`HlsSegmenter`, `TranscodeJobHandler` and `WaveformService`. Removing transcoding would remove neither.

## §3 What the mobile probe settles, and what it leaves open

**Settled: the surface belongs on all three shells, and mobile is the cheap side.** §0c answers the
question the first draft could only park. The kit ships one surface contract; Windows implements it with
the `DropZoneManager` bounds machinery over an app-supplied engine, and the two mobile shells implement
it with a platform view and the platform's own engine.

**Settled: "no engine in the kit" survives contact with mobile, and gets easier.** On mobile there is
nothing to ship because the engine is the OS. On Windows the kit locates what the app provided and
reports absence. Neither case puts a binary in a package.

**Still open, and now the sharpest question:** *does the decision layer return a VERDICT or a
CAPABILITY QUERY?* Windows must answer from a hand-maintained codec set; mobile can ask the platform per
asset. A contract shaped as "give me the verdict for this file" fits both. A contract shaped as "here is
the set of things this host can decode" fits Windows and lies on mobile, where the honest answer is
device-specific and per-asset. The evidence points at the first shape; D2 should decide it explicitly
rather than inherit it.

**Still open:** whether "play this" and "give me a thumbnail of this" share one host contract or two.
Mobile answers both from the same object (`MediaMetadataRetriever`, `AVAsset`); Windows answers them from
different places (an engine versus an external ffmpeg). Do not let the Windows split dictate the surface.

## §4 Engine guidance — measured, because "small" needs a number

The kit ships no engine (§2), so this is guidance for an ADOPTER and for whatever the sample uses to
prove the seam. Measured 2026-08-03 by restoring the packages, not estimated.

**What exists.** `dotnet package search` over nuget.org: **LibVLC is the only engine with maintained
first-party natives for all three targets** — `VideoLAN.LibVLC.Windows` 3.0.23.1, `.Android` 3.6.5,
`.iOS` 3.6.1, all published by `videolan` — plus a maintained managed binding (`LibVLCSharp` 3.10,
2.1M downloads) that includes **`LibVLCSharp.WinForms`**, which is this kit's desktop shell exactly.
The ffmpeg alternatives do not clear the bar: `Sdcb.FFmpeg` ships a `runtime.windows-x64` only, and
`FFMpegCore`/`Xabe.FFmpeg` are process-SPAWNING wrappers — **dead on iOS, which forbids subprocesses**.
That constraint alone rules out the model both desktop siblings use today (`RunAsync(ffprobe, …)`).

**Where the bytes are** — `VideoLAN.LibVLC.Windows` 3.0.23.1, one architecture (x64):

| Item | Size | Note |
|---|---|---|
| `plugins/` | **96.3 MB** / 325 files | 94 % of the payload |
| ⤷ `codec/` | 37.4 MB | of which `libavcodec_plugin.dll` alone is **16.5 MB** |
| ⤷ `access/` | 14.7 MB | dominated by things a local player never uses (SRT is 8.2 MB across two) |
| ⤷ `demux/` | 9.0 MB / 44 files | a local player needs a handful |
| `libvlccore.dll` + `libvlc.dll` | **2.9 MB** | the actual engine |
| everything else | ~2.6 MB | headers, lua, hrtfs, import libs |
| **total per arch** | **101.8 MB** | the package ships x64 **and** x86 → 410 MB restored |

**So the engine core is ~3 MB and the breadth is ~16.5 MB of ffmpeg inside one plugin.** Plugins are
scanned from a directory at runtime, so an app ships the subset it needs: core + `avcodec` + a few
demuxers + one video and one audio output lands around **25–30 MB per architecture**, ~35 MB with
subtitle rendering (freetype 3.0 + libass 3.3). That is 3–4× smaller than shipping the package as-is.

**The honest conclusion: there is no engine that is both small and broad, because breadth IS ffmpeg.**
Anything advertising small-and-good either wraps the same 16 MB of avcodec or decodes less. What varies
is how much of it you must ship — and that is where the decision layer pays for itself:

| Target | Engine cost | Why |
|---|---|---|
| **Android / iOS** | **0 MB** | The platform decodes (§0c). Shipping LibVLC here would duplicate hardware decoders the OS already exposes, and burn battery doing it in software |
| **Desktop, remux-only** | small | Container fixes need demux+mux, no decoders — and an ffmpeg built without encoders avoids the GPL components entirely |
| **Desktop, broad codec support** | ~25–30 MB/arch | core + avcodec + a pruned plugin set |

⚠ **Not verified here, and each needs checking before it is relied on:** the Android and iOS native
package sizes (not restored — Android carries 4 ABIs, so expect it to be the largest); and the licence
position per build. LibVLC is LGPL 2.1+ but some plugins are GPL, and ffmpeg is LGPL only when built
without its GPL parts (x264/x265 are encoders, so a decode/remux-only build is the easier case). **The
kit must not make that choice for a consumer** — which is the other reason §2 keeps engines out.

## §5 Other open questions this harvest does not answer

- **Does the kit express "extract a frame" and "resize an image" as one operation or two?** Disagreement
  2 says two pipelines; it does not say two API surfaces. Deciding this needs the D2 pass.
- **Does the media PICKER belong here at all?** `MediaPicker.PickPhotosAsync` exists in MAUI Essentials
  (verified by compiling, 2026-08-02) and D35 named "let the user hand me some media" as one of three
  portable intents. It may fit better beside `IFileDialogs.SaveAsync`, which is now the proven pattern
  for a host-mediated file operation.
- **D5's verify step is semantic, not exact.** `UpdateStage`'s answer (compare a SHA-256) cannot apply:
  a re-encode is not byte-predictable. "Valid" here means does it decode, is the duration within
  tolerance, are the expected streams present — and only the caller knows the tolerance. The mechanism
  is `Files.BeginReplace`; what is media-specific is the predicate.
