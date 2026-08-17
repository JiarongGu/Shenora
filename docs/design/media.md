# The media tier — as built

**Maintainer-facing.** What the pieces are, how they compose, and what each one promises. For USING the
tier read [`../guides/media.md`](../guides/media.md); for WHY any of it is this way read the decisions
linked below — **this doc states the design, never the rationale** (D77), so a claim here that needs
defending belongs in a `D<n>` and a link.

## The four stages, and the question each answers

| Folder | Question | Key types |
|---|---|---|
| `Probe/` | what IS this file? | `MatroskaProbe` → `MediaProbeResult` |
| `Plan/` | what SHOULD happen to it? | `MediaPlaybackPlanner` → `MediaPlaybackAction` |
| `Engine/` | how are the bytes PRODUCED? | `Mp4Remuxer` · `DefaultSegmentEngine` · `Mp4FragmentWriter` |
| `Deliver/` | how do they REACH the page? | `UseComputedRemux` · `UseMediaConversion` · `UseSegmentStream` |

The split is by question rather than by feature, so containment and cache location are stated once
(`MediaAccessOptions`) instead of once per route (D71).

## The planner chooses on what the PRODUCER can promise (D71)

Never on the platform. `MediaPlaybackAction` draws the line:

- **`Direct`** — the webview can already play it. No kit involvement beyond serving bytes.
- **`Remux`** — every stream can be carried untouched, so the output's length AND its byte↔time map are
  derivable from the source index before any work is done. That makes it a COMPUTED file: any range is
  serviceable cold, so it ships as one plain `<video src>` over 206s.
- **`Transcode`** — a re-encoder can promise neither, so it gets segments and a manifest.
- **`Unsupported`** — nothing here can help; say so rather than half-doing it.

## Producing: copy first, convert only what cannot be copied (D76)

**One predicate decides it for both writers** — `Mp4Carriage`, asked of the raw Matroska CodecID. MP4
carries H.264, HEVC and AAC untouched, and Matroska already stores them in the length-prefixed form MP4
uses, so a copy is a byte move.

```
Mp4Remuxer          whole file    every stream copied, or the source is refused
DefaultSegmentEngine  fragments   per TRACK: copy what MP4 carries, convert the rest
```

**A copied track cannot hit a fixed grid**, because it keeps the keyframes the original encoder chose. So
where the cuts fall is a `SegmentPlan`:

- `SegmentPlan.Grid(seconds, total)` — uniform, what a RE-ENCODING run produces (the kit's platform
  encoders emit a keyframe every second, D75).
- `SegmentPlan.Cuts(starts, total)` — explicit, derived from the source's own keyframes.

`ISegmentEngine.PlanSegments` returns the plan or null for "I will hit your grid", and `SegmentStream`
hands that same object to the manifest AND to every run — one object, so the playlist and the producer
cannot disagree.

⚠ **A copied picture and a uniform plan cannot both hold**, so a run handed a grid re-encodes instead. The
two decisions are made in different places (the plan when the manifest is built, the copy when the run
starts) and letting them disagree slips every cut to the next source keyframe.

**The init segment is written BESIDE THE FIRST FRAGMENT, never ahead of the run.** `OutputConfig` is
knowable only once an encoder has produced output, and an init segment carrying an empty one yields a movie
that opens and plays nothing. So the route answers `503 Retry-After: 1` for `init.mp4` until it lands, and a
page following `#EXT-X-MAP` must tolerate that exactly as it does for a segment.

⚠ **A re-encoded track is cut on a whole number of seconds and a fractional grid is REFUSED, not rounded.**
What makes a grid hittable is that both platform encoders emit a keyframe every second — a coupling that
lives in those two files and nowhere else, which is why no forced-keyframe API was needed. A 2.5-second grid
puts boundaries where no keyframe exists, and those segments PLAY: only a seek misbehaves, which is why it
is refused at composition time rather than discovered later.

## The four seams, which are NOT interchangeable

They look alike and differ in WHEN their output is usable — the reason all four exist:

| Seam | Shape | Output usable |
|---|---|---|
| `IMediaStreamConversion` | THE PRIMITIVE — one stream in, one out, frame by frame | per frame |
| `IMediaContainerWriter` | a muxer, e.g. `Mp4Remuxer` | when the file is complete |
| `MediaConversionOptions.Convert` | ONE FINISHED FILE, a delegate an app can replace | when the file is complete |
| `ISegmentEngine` | a ROLLING WINDOW of numbered pieces, started anywhere, killed on dispose | seconds in |

An hour-long source is an hour-long wait through the third and a few seconds through the fourth. The kit
ships a default for each; an app past the platform's reach replaces one (D42, D51, D70).

## Delivery, and why registration ORDER is load-bearing (D73)

`UseComputedRemux` must precede `UseMediaConversion`, or the conversion route answers every request its own
`Resolve` matches and **the computed route becomes dead code that still passes all of its own tests**.
Nothing enforces this today; a test or an analyser is the wanted fix, not a composite helper that hides
the ordering.

Three more from the same composition audit (D73), each silent rather than loud:

- **Diagnostics require a DOWNCAST**, so a shell's converters are mute until an app casts the pipeline —
  a composition that looks complete and reports nothing.
- **The conversion pipeline degrades silently without an `IMediaCapability`**, rather than saying it
  cannot judge what the device supports.
- **Three options types must share ONE `MediaAccessOptions` and ONE `IMissionScheduler`.** Separate
  instances compile, run, and give each route its own containment and its own queue.

Each route answers a not-ready request the same way — `503 Retry-After: 1` — because the alternative (a
404) ends playback permanently for a source that is merely still being planned.

### Serving the bytes: lazy, and never on the resource thread

🔴 **A body is LAZY — a `BoundedBodyStream` over a real `FileStream`, opened when the response is built and
read as the browser pulls.** ⚠ **An output-size ceiling cannot bound the WALK that computes it**: the old
cap was checked AFTER the walk, against a number the walk produces. Anyone reintroducing a bound wants a
PRE-walk figure.

🔴 **Never block a webview's resource thread, at any size.** Measured rather than inferred — one blocking
read there deadlocked the iOS main thread. So the walk is a MISSION, and the first request for an unplanned
source answers `503 Retry-After: 1` rather than waiting.

✅ **The `Remux` arm is proven on hardware, including the claim the whole design turns on:** a **cold seek to
80 %** lands and plays on, with nothing produced before or after it. That is what "a computed file" buys —
any range is serviceable without having produced a byte of the rest.

## The stream becomes the file (D71 piece 5)

**"We have every segment" and "we have the finished file" are ONE state.** `init.mp4` followed by every
fragment in plan order *is* a valid fMP4, so `ISegmentStreamRoute.MergeAsync` is a byte copy — there is no
second production, and no re-encode.

- `IsComplete(source)` is a **checkable predicate**: every part exists and is non-empty. ⚠ Non-empty, not
  merely present — a run killed mid-write leaves a file that exists and holds nothing.
- 🔴 **The destination may not be inside the segment cache, and the route refuses it.** The two have
  opposite policies: the cache is swept oldest-used-first under a byte cap, a persisted artifact is evicted
  by nothing. Writing one into the other means ordinary playback silently deletes a file someone waited
  for, surfacing later as a download that used to work.
- **The APP asks, in .NET** — the same shape D72 settled for warming a plan. A page that has been streaming
  keeps streaming; an app that wants the file calls this and points at it, where playback is `Direct`.

## Timelines: the one calculation that is easy to get wrong

Three clocks meet in the segment writer, and one rule for all of them produces a stream that appends
cleanly and buffers nothing:

- a **copied** track keeps the SOURCE's clock (`SourceTimeline` reduces Matroska's ns-per-tick to an exact
  MP4 timescale — the naive division truncates);
- a **converted picture** is timed in microseconds from what the encoder stamped;
- a **converted soundtrack** is timed on its own sample rate from the PACKET COUNT, because an audio
  encoder stamps nothing.

So a cut boundary travels in SECONDS and each channel converts it into its own timescale. Comparing one
channel's times against another's is how the sound side of every cut lands somewhere other than the video.

## What is deliberately absent

- **No engine bytes ship, ever** (D51) — an app supplies one through a seam or a `ResourcePack`.
- **No segment engine on the desktop until something registers a converter** (D75) — the kit ships none for
  Windows, so out of the box the desktop's answer is the computed-remux route. ⚠ That is a fact about what
  the kit provides, not a platform rule: an app supplying its own decoding library gets the engine there.
- **No thumbnail type spanning both mechanisms** (D43) — extracting a frame needs a decoder, resizing an
  image needs an image codec; they are different capabilities wearing one word.

## What IS proven, and how

The pump, the cutting and the fragment bytes are unit-tested against a FAKE `IMediaStreamConversion`, which
proves the loop and nothing about a real file. `RealSourceSegmentTests` closes that gap in the suite: it
drives the engine over a real 60 s H.264/AAC Matroska with a conversion that RECORDS every call, and asserts
it was never consulted — D76's whole claim in one assertion.

Measured once, on 2026-08-15, against a real decoder and a real browser (Chromium 151):

| Claim | How it was established |
|---|---|
| The copy is lossless | All **600** decoded frame hashes identical to the source, in order (`ffmpeg -f framehash`) |
| The fragments decode | `ffmpeg -v error -i merged.mp4 -f null -` — **zero errors** over the whole file |
| A real `MediaSource` accepts them | `isTypeSupported` true, init + 15 fragments appended with no error |
| …and PRESENTS them | **10 frames** through `requestVideoFrameCallback`, at mediaTimes `0.023, 0.123, …` |
| The cuts land where the manifest says | after seg0, `buffered=[0.000..4.023]` — the source's own keyframe |
| Nothing is lost across the joins | after all 15, `buffered=[0.000..60.023]`, contiguous |
| Picture and sound stay aligned | the presented frames carry the SOURCE's own times (`0.023, 0.123, …`) while the soundtrack is unshifted, and the buffered range shows no hole where a shift would put one |
| Seeking works | `currentTime=20.000`, `readyState=4` |
| The merged file plays as `Direct` | duration 60.246, seekable to the end, seek to 45 s — matching a reference fMP4 ffmpeg built from the same source |

⚠ **A B-frame source makes `ffprobe` report the picture starting 200 ms late** (`start_time=0.223` against a
soundtrack at 0). That is not a defect and it cost a session to establish: ffmpeg's OWN fragmented output from
the same file reports the same offset, because a reorder depth of 2 has to come from somewhere. The kit states
it with SIGNED version-1 `trun` offsets and keeps the source's true 23 ms start; ffmpeg shifts the decode
timeline instead and writes unsigned ones. Chromium — the target — presents the first frame at **0.023**.

And on iOS 26.3 (iPhone 16 Pro simulator, 2026-08-15), where the MediaSource is a different implementation
— `window.MediaSource` is **false** there and only `ManagedMediaSource` exists:

| Claim | How it was established |
|---|---|
| A copied picture decodes on iOS too | `frame=480x270` after append, on BOTH fixtures |
| Copy and convert compose in one run | `clip-video-ac3.mkv`: picture copied, AC-3 soundtrack converted, 2/2 segments accepted |
| Every segment of a 60 s source is accepted | `appendedSegments=10/10`, `buffered=0.02-60.02` contiguous |
| 🔴 **`endstreaming` FIRES, so the streaming gate is load-bearing** | 60 s buffered → `endstreaming=1`, `streaming=false`; 6 s buffered → `endstreaming=0`, `streaming=true` |
| The merged file is one openable file | `MERGE: PASS — 68997 bytes` |

⚠ **A 6-second clip reads as "`endstreaming` never fires", and that reading is wrong.** The source had not
declined to stop; it was never given enough to want to, and the two are indistinguishable from one run.
Anything measuring the stop half needs a buffer far larger than the events it is watching for — the
buffer threshold sits between 6 s and 60 s and is not pinned here.

🔴 **`endstreaming` ALSO fires the moment the stream is declared OVER, and a binder must not read it as
"the buffer is full".** Measured when the shipped binder ran the 6 s fixture on the phone: it appends
everything, calls `endOfStream`, and `endstreaming` arrives immediately with `streaming=false` — where
the hand-written probe, which never signals the end, sat at `endstreaming=0` on the same clip and the
same bytes. Two different causes, one event; treating it as a buffer signal alone would have a binder
resume fetching against a source that has finished.

And on Android (emulator, api 36, 2026-08-15) — the third implementation, a plain `MediaSource`:

| Claim | How it was established |
|---|---|
| The copied picture decodes here too | `frame=480x270`, both fixtures, `appendedSegments=10/10`, `buffered=0.00-60.02` |
| 🔴 **The track set is DEVICE-dependent, not source-dependent** | this device decodes no AC-3, so the soundtrack is dropped and the init carries video ALONE — 677 bytes against iOS's 1133 for the same source |
| …which is why the codecs are read from the init segment | `declaredCodecs=avc1.640015` for that fixture, one track, and it plays; the two-track default would have failed its first append |
| The derivation agrees across platforms | `avc1.640015` here, and the same string from the same `avcC` in the Windows unit test |
| Attachment differs by implementation | `attachedBy=objectURL` — Chromium refuses `srcObject` for a MediaSource, iOS requires it |

⚠ **`startstreaming`/`endstreaming` are 0 and `streaming` is `undefined` here, and that is correct**: they
are `ManagedMediaSource` members and Android has the plain one. A binder must treat their absence as
"always streaming", never as "never asked".

### The real iPhone is not the simulator, and the codec table is where it shows

iPhone 17 Pro, iOS 26.6, 2026-08-15. It **confirms** the simulator on every append claim — `frame=480x270`,
10/10 segments, `buffered=0.02-60.02`, `endstreaming=1` with `streaming=false`, `attachedBy=srcObject` — so
those are hardware facts and not simulator artefacts. What differs is what the device can DO:

| | simulator | device |
|---|---|---|
| `kit decode video` | h264 | **h263** h264 |
| `convert video h263` | accepted=False | **accepted=True** |

🔴 **So a picture the kit RE-ENCODES is reachable only on hardware**, and it works: the conversion route
turned h263 into a file that decodes and plays (`size=352x288 ready=4 advanced=1.20`). ⚠ Never choose a
fixture from another device's table — that is per-DEVICE, and picking h263 off this one is exactly what a
simulator run cannot honour.

### The one defect a device found, and why it hid

**A converted soundtrack was timed from 0.0 s on any run that did not start at segment 0.** Converted
sound is timed by COUNTING PACKETS — an audio encoder stamps nothing — and the count restarts each run, so
a run producing segment N wrote its sound at zero while its copied picture sat at N's real start. Both
fragments are well-formed and both append without error; `SourceBuffer.buffered` is the INTERSECTION of the
tracks, so the page saw **0.07 s** of playable media and stalled.

🔴 **It is arithmetically invisible at segment 0**, where a relative clock and an absolute one agree — which
is where every test in the suite and every un-seeked run begins. Exposing it needs a SEEK *and* a soundtrack
the platform cannot carry, which together only happen on hardware.

Fixed in `SegmentRunWriter.TimeOf` by adding where the run began, in the channel's own timescale. Pinned two
ways, because one was not enough:

| | |
|---|---|
| `A_run_starting_past_segment_zero_…` | starts at segment 2, asserts the fragment's DECODE TIME; sabotage-verified both directions |
| `SegmentRouteProbe.CheckSeekRun` | asks the engine for a run at segment 1 on the device — `soundTicks=264600`, `expected≈264600` (6.0 s × 44100), `pictureTicks=6000` |

⚠ **The device reading that first followed the fix was CONFOUNDED and is not the evidence** — every run in
it started at `from=0`, so it showed no regression rather than a repair. The page cannot force a seek
reliably (whether a request joins a producing run is a race against the cache), which is why the probe asks
the engine directly.

⚠ **Assert the decode time, never the size.** A mistimed fragment is the correct length, carries the correct
samples, and appends without error — `Mp4FragmentReader.BaseDecodeTime` exists because *where* a fragment
sits is what a byte count cannot say.

### The encoder does NOT reorder — the last of D71's three questions

Measured on the iPhone, 2026-08-15. Reaching an encoder at all took D76 first: a picture MP4 can carry is
COPIED, so the only route to one is a source whose picture it cannot — and the simulator converts no video
whatsoever, while the phone converts h263. A GRID plan forces the re-encode, because `Pick` refuses to copy
when a run must hit uniform boundaries.

```
segments: picture (converted) from=0 of=60 read=60 emitted=60
REORDER: ran a re-encode over clip-h263-aac.mkv — 6 segment(s), 179659 picture bytes
```

**60 frames in, 60 out, and no reorder warning.** `SegmentRunWriter` fail-closes on a backwards
presentation time and DROPS the frame, so a reordering encoder would read as `emitted` below `read`; it
does not. The re-encode path also produces real fragments end to end on hardware, which nothing had shown.

⚠ **Frames in against frames out, never bytes** — the output is h264 where the input was h263, so byte
counts are not comparable. ⚠ And a codec table is per-DEVICE: h263 is this phone's answer, not iOS's.

### The shipped binder, on the phone

`bindSegmentStream` — the module an adopter actually gets — driving the same stream the hand-written probe
drives, 2026-08-15:

```
module=SHIPPED @shenora/react | attachedBy=srcObject | codecs=avc1.640015,mp4a.40.2
appended=2 | frame=480x270 | buffered=0.00-6.50 | streaming=false
```

It agrees with the control on every substantive point — the same attachment, the same codecs derived
independently from the same init segment, the same decoded frame, both segments — and differs only in
finishing the stream, which the control never does. **Keeping both is what made that difference legible**
rather than a mystery: one probe alone could not have said which behaviour was the module's and which was
the platform's.

## What is still NOT proven

- **That `OutputConfig` lands early enough for a RE-ENCODED picture's init segment.** The run above wrote
  fragments carrying picture bytes, but nothing appended them to a MediaSource — so the init segment's
  decoder configuration is inferred rather than exercised. Every append measured so far used a COPIED
  picture, whose configuration comes from the source rather than from an encoder.
