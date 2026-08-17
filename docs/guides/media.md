# Media playback

> **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never
> restated** — that is the rule D57 was written to keep (five design docs were retired precisely
> because a third copy of the reasoning goes stale while nobody notices).
> Migrating an existing app? Start at [ADOPTION.md](../ADOPTION.md).

## What this is, in one line

**Three things: a segmenting engine, a media interface to your frontend, and a conversion interface.**
🔴 **A CODEC is not one of them.** The kit ships no codec and no binary — not as a NuGet payload, not as
a download, not behind a path option (D51) — and that is a statement about what this tier IS rather than a
limitation it is working around.

**There IS a default conversion engine, and it is the platform's own decoders wired up** (D70) — which is
not a codec, and is why both halves of that sentence are true at once. Its reach is exactly D59's line —
*what the device decodes and its webview refuses* — and no wider. Past that line the engine is yours, and
`MediaConversionOptions.Convert` is where it goes.

> ⚠ **`Convert` is OPTIONAL — it was `required` in earlier builds.** If you adopted one, nothing breaks,
> but you can likely delete your converter: what makes a kit default defensible is D59's boundary, stated
> here rather than enforced by making every adopter type the same line (D70).

What you get for that: the planner that decides whether a file can play as-is, the route that serves it
with correct ranges on every shell, the mission scheduling and derived cache key around a conversion, and
the page-side player joint. **You write the engine call. The kit is everything either side of it.**

## Media playback — not a stage, and not a wiring job

A media player whose lifecycle lives in .NET, rendering through your page's own `<video>`/`<audio>`
element. **Sources are passed straight through** — nothing probed, nothing converted — because a file
the device can already decode needs none of that.

**The host half is already there** (D64): `ShenoraApplication.CreateBuilder(...).Build()` registers
`IMediaPlayer` and the route that carries the page's reports back. Inject it and call `OpenAsync` /
`PlayAsync` / `SeekAsync`. The only thing you write is the page half:

```tsx
const ref = useRef<HTMLVideoElement>(null);
useMediaPlayer(ref);
return <video ref={ref} playsInline />;
```

That is the whole integration. The element becomes the display and the sound; .NET owns the lifecycle,
decides whether a file can play as-is, and points at a conversion when it cannot.

> ⚠ **If you wrote the page-side reporting route yourself against an early build, DELETE it** —
> `MediaPlayerModule` answers on `SHENORA.MEDIA` and yours would collide. Shipping both ends and no joint
> is what made that necessary, and it is the failure shape to remember: the page posted reports to a
> module nothing answered, so `OpenAsync` — which completes on the page's first report and on nothing
> else — simply never returned. No exception, no log line, and an element that was visibly playing.

**Add conversion only when the device and its webview disagree** — which is the entire job of the media
pipeline (D59): your hardware decodes AC-3, the `<video>` element will not touch it, and something has to
bridge the two.

```csharp
// name the roots it may read from…
builder.UseMediaPlayer(x => x.Access = new MediaAccessOptions
{
    Resolve = static _ => null,   // unused here — see MediaPlayerOptions.Access's remarks
    AllowedRoots = [libraryDir],
    CacheRoot = "",                // "" = let UseMediaPlayer default it under Paths.DataArea
});

using var app = builder.Build();
// …and mount the route on every webview the app hosts
app.UseMediaPlayer();
app.Run();
```

> ⚠ **`AllowedRoots` is the one thing the kit will not choose for you**, which is why conversion is what
> you opt into. It is the containment boundary that stops a page-supplied path escaping into the rest of
> the disk — the security decision and "do I need conversion?" are the same decision (D61). It lives on
> `MediaAccessOptions` now, the ONE place every media delivery path states containment and cache location —
> `MediaConversionOptions` and `SegmentStreamOptions` carry the same `Access` object.

**Two phases because there are genuinely two**, the same split ASP.NET draws between registering a service
and mounting its middleware: the interceptor is created *with* the webview, so it cannot exist while
services are being registered. The mount call is a no-op when you named no roots, so it is safe to make
unconditionally.

> ⚠ **The second phase moved, and if you adopted an earlier build you must change one line.** It used to
> be `_route = interceptor.UseMediaPlayer(services)` — you fetched the shell's interceptor and handed the
> provider back in. It is `app.UseMediaPlayer()` now, with no argument, because the app already holds the
> provider (D64). **The phases were always right; the receiver was not.** The per-interceptor overload
> still exists for one webview that must genuinely differ from the rest of the app.
>
> 🔴 **It also means MORE than it used to.** `app.Use…()` describes the pipeline for **every** webview the
> app hosts — secondary windows and auxiliary session browsers included. Those previously got nothing
> unless you wired each one again by hand, which was invisible: a window serving no routes looks exactly
> like a window whose routes were never needed.

**What you get without writing a converter:** container repair (Matroska → MP4, every frame copied
untouched) plus a soundtrack transcode through *your device's own codecs* where the shell registers them.
**If you have a better encoder**, add it to the same pipeline and everything above uses it — the default
converter, the player, the segment engine — with no other change:

```csharp
pipeline.Use((source, codecPrivate) => source.Codec is "ac3" ? MyDecoder.Begin(source) : null);
```

⚠ **The kit ships no codec and no engine, ever** (D51) — every byte of decoding is the platform's. Where
the device cannot decode it either, there is nothing to bridge and the honest answer is a refusal.

> **Need playback the page element cannot give you?** Resolve the shell's NATIVE player instead —
> `IosMediaPlayer` on iOS, `AndroidMediaPlayer` on Android, `WindowsMediaPlayer` on the desktop. On iOS the gap is absolute: the system
> pauses a `<video>` the moment the app backgrounds, so background audio also needs your app's own
> `AVAudioSession` + `UIBackgroundModes: [audio]`. On Windows it is narrower — playback that survives the
> webview, and the platform's whole codec set rather than the webview's subset.
>
> ⚠ **Resolve them BY NAME, not as `IMediaPlayer`** — `services.GetRequiredService<WindowsMediaPlayer>()`.
> `IMediaPlayer` is the page-backed player on every shell, deliberately, because rendering through the
> page is the normal case. Both types implement the contract, so everything you call is identical:
>
> ```csharp
> var player = services.GetRequiredService<WindowsMediaPlayer>();
> using var link = player.ReportTo(services.GetRequiredService<IPlaybackSession>());
> ```

### Bringing your own converter (FFmpeg or anything else)

🔴 **ON ANDROID AND iOS YOU WRITE NOTHING — the default engine is already wired.** Both mobile shells
register `IMediaStreamConversion` themselves, and `UseMediaPlayer` resolves the muxer and the codec seam out
of DI, so the two calls in the previous section ARE the conversion setup. There is no third step.

```csharp
builder.UseMediaPlayer(x => x.Access = new MediaAccessOptions
{
    Resolve = static _ => null, AllowedRoots = [libraryDir], CacheRoot = "",
});   // …and app.UseMediaPlayer().
// That is the whole thing: Mp4Remuxer + this platform's decoders, resolved for you.
```

On the desktop the same two calls give you **container repair** — Windows registers no
`IMediaStreamConversion`, and says so by absence rather than by pretending. A dropped soundtrack is
**reported**, never silent (`MediaRemuxerResult.Dropped`, and the route emits `FAILED` with
`MediaConversionErrorCodes.UnsupportedCodec` naming the codec).

## What a whole adoption looks like

The two calls above are the *player*. This is the **delivery** side end to end — an app that lets a user
open a file the webview refuses, and a page that stays one plain `<video>`. Nothing here is aspirational;
it is what the sample does.

**1. The container, once.** `Add` is the service-collection level; `Use` is the pipeline (D73, following
D66's rule). One `MediaAccessOptions` is registered because three options types must share it — containment
stated once is a security boundary, and three copies is how it drifts.

```csharp
builder.Services.AddShenoraMedia(new MediaAccessOptions
{
    Resolve      = uri => MyRouteToSourceFile(uri),   // null = not mine
    AllowedRoots = [libraryDir],                      // EMPTY serves NOTHING — fail-closed
    CacheRoot    = convertedDir,                      // unused by the computed route; the others need it
    Log          = line => MyLog(line),               // ⚠ reaches the PLATFORM CONVERTERS too
});
```

⚠ **Set `Log` even if you throw it away in release.** Without it this kit's platform converters are mute,
and a picture that cannot be converted then says only `dropped:["mpeg4"]` — the codec, and nothing about
why. That silence cost three device round-trips in one session.

**2. The routes, in this order.** 🔴 **The order is load-bearing and nothing enforces it.** Registered the
other way round, the conversion route answers every request its own `Resolve` matches, so a plannable film
would `503` through a whole transcode and the computed route becomes dead code *that still passes every test
of its own*.

```csharp
var access = services.GetRequiredService<MediaAccessOptions>();
var sched  = services.GetRequiredService<IMissionScheduler>();

using var computed   = interceptor.UseComputedRemux(sched, access);          // FIRST — serves what it can plan
using var conversion = interceptor.UseMediaConversion(sched, events, new MediaConversionOptions
{
    Access     = access,                                                    // the SAME object
    Conversion = services.GetService<IMediaStreamConversion>(),
});                                                                          // then the rest
```

A source the computed route cannot plan **falls through** to the conversion route — that fall-through *is*
the split, not an error path.

**3. Warm the plan before you show the player.** This is what keeps the page plain (D72). A source nobody
has planned answers `503` while the metadata walk runs, and a media element cannot ride that out: measured
on both mobile shells, it errors within ~70 ms and never retries. So the wait moves earlier, into your code,
which already knows what it is about to play.

```csharp
if (await computed.PlanAsync(path, ct) is MediaPlanOutcome.Ready)
    ShowPlayer(url);          // its FIRST request is a 206 — ~1.9 s for a 79 MiB film, cached after
```

`PlanAsync` answers four things you act on differently: `Ready`, `Unplannable` (remote, or the output would
lose a stream — send it to the conversion path), `Refused` (outside `AllowedRoots`, or no such file — your
bug, not a retry), `Failed` (retryable, and nothing is remembered).

**4. The page.** No manifest, no readiness event, no retry loop, no kit JavaScript:

```html
<video src="app://0.0.0.1/media/remux?film.mkv"></video>
```

⚠ **Ask before you assume, in either direction.** `IMediaStreamConversion.CanConvert` answers *claim ∩
device* — what this kit offers, intersected with what the hardware reports. An iPhone 17 Pro decodes `h263`
and **not** `mpeg4`; an Android device decodes mp3/flac/vorbis and not ac3/eac3/dts. Calling a supported
codec unsupported is as wrong as the reverse, and both have happened here.

**Only a hand-rolled route needs to pass the seam itself**, because `UseMediaConversion` reads no DI:

```csharp
interceptor.UseMediaConversion(scheduler, events, new MediaConversionOptions
{
    Access = new MediaAccessOptions
    {
        Resolve   = uri => MyRouteToSourceFile(uri),      // null = not a conversion request
        AllowedRoots = [libraryDir],                      // EMPTY means nothing is servable — fail-closed
        CacheRoot = convertedDir,
    },
    // The default engine: the kit's muxer plus this platform's decoders. No `Convert` needed.
    Conversion = services.GetService<IMediaStreamConversion>(),
});
```

**Know where the line is before you reach past it.** Measured 2026-08-10: an Android device decodes
mp3/flac/vorbis and not ac3/eac3/dts/alac, while an iPhone decodes ac3/eac3. Ask `IMediaCapability`; do not
assume, in either direction — the kit's job is to bridge what the platform CAN do, so calling a supported
codec unsupported is as wrong as the reverse.

⚠ Setting both `Convert` and `Conversion` **throws at registration**: the second configures the
default engine, so a custom `Convert` would make it dead configuration. Compose them instead —
`myMuxer.ToConverter(conversion)` takes both.

Reach for an external engine only for codecs **no** platform decodes. Then `Convert` is yours:

```csharp
Convert = async (request, ct) =>
{
    // `request.DestinationPath` is a TEMPORARY path the kit publishes only if you return without
    // throwing — so write there, never over the source, and let a failure throw.
    var psi = new ProcessStartInfo(myFfmpegPath)
    {
        ArgumentList = { "-nostdin", "-i", request.SourcePath,
                         "-c:v", "copy", "-c:a", "aac", "-y", request.DestinationPath },
        RedirectStandardError = true,
    };
    using var proc = Process.Start(psi)!;
    await proc.WaitForExitAsync(ct);          // honour the token: this is what makes shutdown prompt

    // 🔴 EXIT 0 IS NOT EVIDENCE. Measured in this repo: an encoder accepted every frame, wrote
    // `video:0KiB` and exited 0. VERIFY THE OUTPUT before returning, or you publish an empty file to
    // a cache that will keep serving it.
    if (proc.ExitCode != 0 || new FileInfo(request.DestinationPath).Length < 8 * 1024)
        throw new InvalidOperationException($"conversion produced nothing for {request.SourcePath}");
};
```

Four things that are yours and worth knowing before you start:

- **Where the binary comes from is your call** — a system install, one you ship, or one you fetch. ⚠ If you
  ship or download FFmpeg you become a distributor of its licence: pin an **LGPL** build (several
  third-party builds are GPL, which is a far bigger commitment) and keep it dynamically linked.
- 🔴 **iOS forbids `fork`/`exec`,** so a CLI converter is impossible there — it would have to be linked
  in-process. Prefer fixing what the platform already decodes on iOS.
- **The route answers `503` + `Retry-After` while the conversion runs**, because it is scheduled as a
  mission rather than done on the request thread. Your page's fetch must poll; that is the correct shape
  for work that outlives a request, not an error.
- **The kit already owns the hard parts** — one conversion per source (`PathClaims`), atomic publish, a
  derived cache key, and cancellation. You are writing the engine call, not the plumbing.

---

## The segmenting engine — playing a file that has to be converted WHILE it plays

The conversion route above finishes a file and then serves it, which is right when the wait is short and
wrong when it is a two-hour film. The segment tier is the other answer: a synthetic HLS manifest computed
from the DURATION alone, with segments produced on demand as the player asks for them, so playback starts
in seconds and the conversion never has to complete.

**Which one a source takes is YOUR registration decision, not an arbitration the kit performs** (D75).
The two routes are independent middleware; you give them non-overlapping `Resolve` predicates and that is
the whole routing rule. The shape of the choice is D71's: a producer that can state a length can be a
computed file served over ranges, and a producer that can promise nothing gets the time grid.

**Host — the engine, then the route:**

```csharp
// Null conversion is ACCEPTED and means IsAvailable = false — see the platform note below.
var engine = SegmentEngine.Default(services.GetService<IMediaStreamConversion>(), log);

var route = interceptor.UseSegmentStream(engine, new SegmentStreamOptions
{
    RoutePath      = "/shenora-hls/",   // default
    SegmentSeconds = 6.0,               // default
    Access = new MediaAccessOptions
    {
        Resolve      = uri => MyRouteToSourceFile(uri),   // null = not a segment request
        AllowedRoots = [libraryDir],                      // EMPTY means nothing is servable
        CacheRoot    = segmentCacheDir,
    },
}, log);
```

🔴 **`UseSegmentStream` registers NOTHING when the engine is unavailable, and that is the feature.** It
returns a stub route instead of throwing, so the call site is identical on a shell with no conversion
engine — the page gets an honest "not ready" rather than the app failing to compose. Check
`engine.IsAvailable` if you want to hide the UI that leads there.

**Page — one call, no hook:**

```ts
import { bindSegmentStream } from '@shenora/react';

const binding = await bindSegmentStream({ manifest: '/shenora-hls/film/index.m3u8', element: video });
// …later, on unmount:
binding.dispose();
```

It resolves once the init segment is appended — the first playable moment — and keeps fetching behind
you until every segment is in or you dispose it. `binding` reports `attachedBy`, `codecs`, `appended` and
`streaming`. **There is deliberately no `useSegmentStream` hook**: this needs no React, and a hook would
add a lifecycle without adding a capability. Call it from an effect.

### The traps, each of which was measured rather than reasoned

- 🔴 **The codecs string is read from the INIT SEGMENT, never assumed.** The track set is a fact about the
  DEVICE, not the file: the same source yields a two-track init on iOS and a video-only one on Android, and
  a mismatch kills the FIRST append and plays nothing — silently. `codecsFromInitSegment` is what the
  binder uses; if it answers `null`, do not open a buffer.
- **Attachment is feature-detected, not branched on the OS.** iOS takes `srcObject` (a
  `ManagedMediaSource` is a valid handle) and Chromium refuses it outright and wants an object URL —
  which one works is a property of the MediaSource, not of the platform. `binding.attachedBy` tells you
  which happened.
- ⚠ **`streaming: false` is an instruction, not a status.** iOS's `ManagedMediaSource` really does raise
  `endstreaming` (measured: never at 6 s buffered, fired at 60 s), and fetching through it is the exact
  misuse that source type exists to detect — the penalty there is the source being torn down. Absent
  signals mean "always streaming", which is every other platform.
- **fMP4 segments only, never MPEG-TS.** `isTypeSupported('video/mp2t')` answers `true` on both mobile
  shells and cannot be trusted; a MediaSource append failure is silent.
- ⚠ **`CacheRoot` is swept** — oldest-used-first under `CacheCapBytes` (2 GB default). A file you intend to
  KEEP must live outside it, and `MergeAsync` refuses a destination inside it for that reason.
- **The init segment is produced, not pre-existing.** A page asking before production starts gets a
  "not ready" 503 — never a 404, and never an empty file, which would poison the buffer.

## Background playback — handing off when the app leaves the screen

A page-backed player stops when the page does: both mobile platforms suspend a backgrounded webview within
seconds, and a page cannot START audio while backgrounded at all (pressing HOME is not a user gesture).
`BackgroundPlaybackTransfer` moves the playhead to the shell's own native player on the way out, and back
on the way in.

```csharp
var transfer = new BackgroundPlaybackTransfer(
    services.GetRequiredService<IMediaPlayer>(),   // the page-backed player UseMediaPlayer registered
    nativePlayer,                                  // AndroidMediaPlayer / IosMediaPlayer, resolved BY TYPE
    new BackgroundPlaybackOptions
    {
        // Asked at BACKGROUND time, on your thread: it must not block.
        ResolveNativeSource = () => _lastServedFile,
    });

window.Stopped += async (_, _) => { try { await transfer.ToBackgroundAsync(); } catch { /* see below */ } };
window.Resumed += async (_, _) => { try { await transfer.ToForegroundAsync(); } catch { /* see below */ } };
```

- 🔴 **`Stopped`/`Resumed`, NOT `Deactivated`/`Activated`.** The latter also fire for a dialog or the
  notification shade, which would move audio out of a page that is still on screen.
- **The native player is resolved by its concrete type on purpose.** The shells do not register it as
  `IMediaPlayer`, because the default one must stay the page-backed player or `useMediaPlayer(ref)`
  silently drives the wrong thing.
- ⚠ **Wrap each handler in try/catch and unsubscribe on unload.** An `async` lambda on an event is
  `async void`: an escape is an unhandled UI-thread exception, not a failed transfer. And MAUI's `Window`
  is process-scoped, so a handler left attached puts TWO transfers on the next transition.
- **A playback that FINISHED while you were away is parked, not restarted** — handing its position back
  would seek a finished element to its own duration and rewind it. That is the `Finished` outcome, and it
  pauses rather than plays.
- ⚠ **How long it survives is UNMEASURED past ~45 s** (Android 45 s, iOS 43 s, against a 60 s clip, on an
  emulator and a simulator — both gentler than a handset). Minutes are nobody's claim yet. A foreground
  service is the APP's to post; that split is what `IPlaybackSession` documents.

---
