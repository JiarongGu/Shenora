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
- `SegmentPlan.EncoderCuts(starts, total)` — explicit whole-second boundaries, which is what a HEAD RAMP is.

🔴 **The plan STATES which it is (`SegmentPlan.Origin`), and the run reads that to decide whether it may
copy.** It used to infer it — "is this a grid?" meant "must I re-encode?" — which held only while every
non-grid plan came from the source's own keyframes. A ramp is a third shape, and under the old inference a
run would have copied onto it and slipped every cut to the next source keyframe.

### The head is short, because segment 0 is the whole startup budget

`SegmentStreamOptions.HeadSegmentSeconds` defaults to `1, 2, 4` before the steady length. A page cannot play
until `init.mp4` arrives, that request drives segment 0, and **a VOD playlist starts at segment 0** — the
"begin three target durations from the end" rule is a LIVE one. A uniform six-second stream therefore spends
six seconds producing before the first frame.

⚠ **`EXT-X-TARGETDURATION` is an upper bound**, so a short lead-in is ordinary playlist and the tag still
states the steady length. ⚠ **A ramp, not short segments throughout**: each segment costs a request, and a
keyframe every second measurably raises the bitrate the same picture needs. ⚠ **And it is a REQUEST** — a
copied picture is cut where the source already has keyframes, so a ten-second GOP gives a ten-second first
segment however short the head asks for; the ramp changes which keyframes are chosen, never where they are.

A head length that is not a whole multiple of the encoders' one-second interval, or one longer than the
steady length, is refused when the route is built — the same policy a fractional grid gets, for the same
reason.

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

### Every part is PUBLISHED, and that is worth a segment of startup

🔴 **A run writes `seg{k}.m4s.part` and renames it into place once whole** (`SegmentRunRequest.PartialExtension`),
so the final name appears only when the bytes are. `IsComplete` is then just "does it exist and is it
non-empty" — the producer states the answer instead of the consumer inferring it.

**What it replaced, and why the cost was invisible.** Completeness used to mean *the NEXT segment exists, or
the run has ended* — the only signal available when a progressive muxer creates a file as it starts writing.
But a page cannot play until `init.mp4` arrives, and that request drives segment 0: so nothing played until
segment 0 **and** the opening of segment 1 had been produced. The whole second segment was latency nobody
was reading. ⚠ It also survived every test, because a fake engine that writes all its segments and exits
satisfies both rules at once; the pair that discriminates needs one published segment and a LIVE producer.

**Two things fall out.** Crash recovery is now a sweep of `*.part` rather than deleting the
highest-numbered segment on every open — that used to throw away a good segment almost every time, and
could only ever see a torn file at the tail. And an out-of-order producer becomes expressible at all, since
"which segments exist" stops having to be a contiguous run from the window start.

⚠ **It narrows the picture-stall detector.** That check read a still-open first segment to catch an encoder
writing no frames; a part being written is now hidden, so it catches only a finished window start with no
picture. A run that publishes nothing at all falls to `WaitBudget` and answers `503` without advancing the
encoder ladder. Reading the `.part` instead would judge a file mid-write, where "no picture yet" and "no
picture ever" are the same bytes.

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

## Where a source may come from — two doors, and they are not the same shape

A delivery route reads something the PAGE asked for, so every route needs an answer to "may I read this?".
There are two, and which one applies depends on whether the thing named is a path or a url:

| | **Local** | **Remote** |
|---|---|---|
| Named by | the page, via the app's `Resolve` | the app only — the page names a HANDLE |
| Guarded by | `MediaAccessOptions.AllowedRoots` containment | `MediaSourceRegistry` — the handle was issued or it was not |
| Fails closed as | empty roots serve nothing | no registry means no remote source at all |
| Read through | `MediaByteSource.ForFile` | `RemoteMediaSource.Open`, the app's own |

**Both doors hand the engine a `MediaByteSource` — a LABEL and an opener, never an address.** Where the
bytes live is a transport question, so `ISegmentEngine` takes the opener and the kit ships no transport:
a local file, a mounted LAN share and a ranged HTTP reader differ only in that function. ⚠ The stream it
returns **must be seekable and report `Length`** — Matroska states where a frame lives rather than
streaming it in order, so a forward-only body cannot be indexed at all; the engine refuses it by name
rather than letting it read as a malformed container.

⚠ **A remote source with no opener is refused at the MANIFEST.** The playlist is derived from the duration,
which `RemoteMediaSource` lets the app supply — so serving one for bytes nobody can read hands the page a
complete playlist whose every entry `503`s for ever.

### The kit ships the ADAPTER, not the transport (D78)

`MediaByteSource.ForRanges(label, length, fetch)` turns *"fetch bytes `[offset, offset+count)`"* into the
seekable stream the demuxer needs. The app writes the fetch — over its own client, its own auth, its own
retry policy — and the kit writes everything else.

🔴 **The buffering is why this ships here, and it is not an optimisation.** The EBML parser reads varints
**one `ReadByte` at a time**, so the adapter an app writes first — a fetch per read — costs a round trip
**per byte**, and it is *unusable* rather than slower. A local `FileStream` buffers for free, so porting
from `ForFile` gives no warning at all. `RangeFetchStream` keeps a 256 KB window, serves reads at or above
that size straight from the source, and **fetches nothing on `Seek`** — a Cues-driven read seeks far more
often than it reads.

**Measured on the 456 KB fixture: 4 fetches to produce the whole plan — the same count over a fake
transport and over a REAL HTTP server** (`RealSourceSegmentTests`, `RangeFetchOverHttpTests`, the latter a
loopback socket speaking HTTP/1.1 to a real `HttpClient`). ⚠ That number proves the adapter BUFFERS, not
that the index was used — at this size a full walk is also a couple of fetches, and the absent *"walking its
clusters"* line is what proves the index.

🔴 **The one real-server failure that is otherwise SILENT is a server ignoring `Range` and answering `200`
with the whole file.** Every other way a fetch goes wrong is loud — a throw, a short body, no bytes — but a
`200` satisfies `EnsureSuccessStatusCode` and every length check, and the demuxer is then handed the START
of the file believing it is elsewhere; the result reads as corrupt media and blames the file. The kit is
given a bare `Stream` and cannot see a status code, so it checks what it can: **a range starting past zero
that comes back with the EBML magic**, which is legitimate only at offset 0. Format-specific deliberately —
a specific detector that fires beats a general one that cannot. Proven against a real server configured to
misbehave, and proven QUIET against an honest one whose file genuinely opens with that magic.

⚠ **The length must be known up front**, because Matroska is read by offset from the END — SeekHead, then
Cues. Over HTTP it is `Content-Length`, from a HEAD or from any one ranged response's `Content-Range`.
**What the kit still does not ship is the transport itself** (D78): no `HttpClient` appears anywhere in
`src/`, so auth, refresh, proxies and redirects stay the app's, and no url ever reaches the kit.

**Containment cannot guard a url**, which is why the second door is not the first one widened. `AllowedRoots`
answers "is this path inside a directory I trust", and a url has no such relation to anything. The conversion
route asks the app to judge instead (`AllowRemoteSource`, a predicate over the url); `SegmentStream` inverts
it so there is nothing to judge.

🔴 **The inversion is strictly tighter, and that is the whole argument for it.** A predicate over a
page-supplied url can be WRONG — and wrong means the host fetches an address the page could not reach
itself, with the host's network position. A handle that was never issued cannot be guessed, so the page
cannot express a source the app did not authorise. The predicate stays on the conversion route because it
shipped there; new doors take the registry.

⚠ **A url is a credential carrier, and the segment tier now handles that STRUCTURALLY rather than by care.**
An engine is handed a `MediaByteSource`, which has a label and a function and no address at all — the app
closes over its own url inside the opener, so there is nothing for a careless interpolation to reach.
Diagnostics print the label. (`Path.GetFileName` was never a substitute: it sanitises a path by splitting on
separators, and a query string has none, so `?sig=…` survives it whole. That is still true wherever a url
is still handled directly — the conversion route's `AllowRemoteSource` predicate.)

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

#### The INDEX first — the walk is the fallback, not the plan

🔴 **A source's keyframes come from its own Cues element when it has a usable one**, reached through the
`SeekHead` at the front. That is the entire question `PlanSegments` asks, and the walk below exists only to
answer it the expensive way. ⚠ **A `SeekHead` may point at a second `SeekHead`** — MKVToolNix writes that
whenever an in-place header edit outgrows its reserved space — so it is followed once; a reader handling one
level reports "no index" for ordinary files.

**Both paths share ONE implementation of `KeyFrameStarts`**, so a file planned from its index and the same
file planned from a walk cut in identical places. That is not a nicety: a cache entry produced one way and a
manifest written the other would otherwise disagree about where every segment starts.

🔴 **A BROKEN index is worse than an absent one, so the checks are the feature.** Absent falls back and
everything works; broken puts every boundary where no decoder can start, in a stream whose bytes are valid
and whose manifest agrees with itself. Refused: fewer than two points, non-ascending times, a last cue past
the declared duration, cues for a different track (every audio frame is a sync sample, so an audio index says
"cut anywhere"), and positions that do not land on a Cluster — the tell for the absolute-vs-segment-relative
mix-up, which is otherwise structurally perfect and points at nothing.

⚠ **Cues are OPTIONAL** and their real-world prevalence is not something this repo has measured. mkvmerge and
ffmpeg both write them by default; live muxes, interrupted recordings and truncated downloads do not have
them. The walk is therefore permanent.

#### A run indexes as it WRITES, so no walk is left before first paint

Planning stopped walking when Cues arrived; producing did not — a run indexed every cluster to the end of
the file before emitting a fragment, on the request that starts it. It now indexes far enough to open its
first segment and asks for more as the pump consumes what it has (`MatroskaSampleReader.ReadSamplesUntil`,
resumable, stopping only on a CLUSTER boundary since block times are relative to their cluster).

🔴 **The hazard is `SampleTiming.Derive`, and it is guarded rather than assumed.** It SORTS, and takes the
presentation shift as a maximum over everything it is given — so a B-frame stream derived per chunk could
get a different decode order, or a different shift, either side of a seam. That appends without error and
plays wrongly. So: the shift is taken from the FIRST chunk and never changed; a decode time may not go
backwards past what is already written; a negative composition offset is clamped; and a stream that reorders
across a chunk boundary is reported once by name. Real reorder depth is a handful of frames against a chunk
of a segment or more, so the margin is large — but it is checked, not trusted.

⚠ **A run keeps one sample of lookahead** before consuming its last known frame: a copied sample's duration
is the gap to its SUCCESSOR, so a frame taken while it is the last one known would get the track's declared
duration instead of its real gap.

✅ **Pinned by a differential test on a real B-frame clip** — every fragment must open at a decode time a
full walk plus a whole-track derivation also produces, with the control derived independently. It also
asserts the fixture really is reordered, so replacing the clip cannot make the test go quiet.

#### What the walk costs, and why there is NO frame-index cache

**Measured 2026-08-20** on a 5 min / 166 MB / 4.6 Mbps Matroska (20,121 samples), counting the DISTINCT
4 KiB pages the reads land in — that is what a cold open must fetch, and it is exact where timing a "cold"
read is not reproducible:

| read buffer | warm | syscalls | pages the OS must fetch |
|---|---|---|---|
| unbuffered | 268 ms | 108,907 | 34 MB — **20 %** of the file |
| **4 KiB (the default, and what ships)** | **66 ms** | 7,201 | 59 MB — **34 %** |
| 64 KiB | 82 ms | 2,405 | 167 MB — **96 %** |
| 1 MiB | 60 ms | 165 | 173 MB — **99 %** |

🔴 **A BIGGER BUFFER MAKES THE COLD READ WORSE, WHICH IS THE OPPOSITE OF THE INSTINCT.** The walk seeks past
every frame payload and reads only block headers — it asks for **149 KB in total, 7.4 bytes per sample** —
so a large buffer drags in exactly the bytes it is skipping, and buys no time back. The invariant lives on
the `File.OpenRead` call in `Mp4Remuxer`, where someone would change it.

**An earlier run, 2026-08-15**, on a 10 min / 89 MiB H.264+AAC Matroska: **65 ms for 40,841 samples** warm,
against a two-hour index estimated at **~51 MiB** — the figure that first showed the walk is cheap and the
INDEX is expensive, which is the opposite of the assumption that filed the question.
⚠ **The two runs were not controlled against each other**, so read the comparison as suggestive only: 40,841
samples in 65 ms against 20,121 in 66 ms says time tracks **buffer misses, not sample count**, and misses
track sample SPACING — at 1.2 Mbps consecutive headers share a 4 KiB page, at 4.6 Mbps each gets its own.
That predicts cold cost rises with BITRATE rather than duration, and it is untested.

**To re-run**, which needs rebuilding a probe — none ships, and none should:
- Fixture: `ffmpeg -f lavfi -i testsrc2=size=1280x720:rate=24:duration=300 -f lavfi -i
  sine=frequency=440:duration=300 -c:v libx264 -preset ultrafast -b:v 4500k -g 240 -c:a aac -b:a 128k
  out.mkv` — the bitrate is the variable that matters, per the note above.
- Probe: a counting `Stream` over a `FileStream` opened `bufferSize: 1` (so every read reaches the counter
  rather than a buffer that would hide the pattern), optionally a `BufferedStream` above it to model a
  given buffer, driven through `MatroskaSampleReader.ReadHeader()` + `ReadSamples()`. Record the set of
  `offset / 4096` pages touched; `Shenora.Tests` already has `InternalsVisibleTo`.

**So the cache question is closed: do not build one.** It could only help a SECOND walk of the same source,
and there is never one — `IComputedRemuxRoute.PlanAsync` already caches the layout by identity, so a planned
source "answers from the cache without touching the file". A frame index would cost ~51 MiB of RAM for a
two-hour file to serve a case that cannot occur.

**A remux is TWO PASSES, and the second is affordable because it is a byte move.** A player needs the
sample table (`moov`) before it can seek, and that table cannot be written until every frame's size and
position are known — streaming the output as it is read would put `moov` at the END, where the file plays
from the start and cannot seek until fetched whole. Measured at roughly **1.2–1.4 GB/s in memory**
(31.3 MB / 4,000 frames in 22–26 ms), excluding file I/O.

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

## First load does not scale with the file — measured 2026-08-21

iPhone 16 Pro simulator, iOS 26.x, one run each. Times are from the PAGE, per term: `tManifest` is the
manifest fetch (probe + plan), `tInit` is the `init.mp4` fetch — **which starts production and does not
answer until segment 0 is whole** — and `tSeg0` is the segment fetch that follows.

| fixture | duration / size | segments | tManifest | tInit | tSeg0 | tries |
|---|---|---|---|---|---|---|
| `clip-video-ac3.mkv` | 6.5 s | 3 | 22 ms | 110 ms | 3 ms | 1 |
| `clip-h264-aac.mkv` | 60 s · 488 KB | 12 | 7 ms | 57 ms | 6 ms | 1 |
| **`clip-big-h264-aac.mkv`** | **~1000 s · 78 MB** | **120** | **18 ms** | **55 ms** | 19 ms | 1 |

🔴 **THE BOTTOM ROW IS THE CLAIM: a file 160× longer and 160× larger costs the same.** That is what the
Cues plan and the index-as-you-write run were for, and it is the shape — flat, not merely fast — that says
so. ⚠ **A full-file walk is structurally incompatible with 18 ms**: the walk touches about a third of the
file's pages (see the table below), so ~26 MB of seeking reads would have to complete in that window.

⚠ **`tries=1` on every produced resource, so nothing ever answered `503`.** The route BLOCKED inside the
first request rather than making the page poll — production now finishes faster than one round trip, which
is atomic publish and the short first segment together.

🔴 **WHAT THIS DOES NOT SHOW: there is no BEFORE number.** Every reading here is of the current code, so the
improvement is argued from the flatness and from what the old path provably did, not from an A/B on one
machine. That gap is unchanged by the hardware run below.

### On a real iPhone — measured 2026-08-22

iPhone 17 Pro, iOS 26.6, installed and launched over `mac device`. Same probe, same terms.

| fixture | duration / size | segments | tManifest | tInit | tSeg0 | appended | tries |
|---|---|---|---|---|---|---|---|
| `clip-video-ac3.mkv` | 6.5 s | 3 | 21 ms | 5 ms | 2 ms | 3/3 | 1 |
| `clip-h264-aac.mkv` | 60 s · 488 KB | 12 | 13 ms | 3 ms | 2 ms | 12/12 | 1 |
| **`clip-big-h264-aac.mkv`** | **~1000 s · 78 MB** | **120** | **22 ms** | **4 ms** | **3 ms** | **120/120** | 1 |

🔴 **THE FLATNESS HOLDS ON HARDWARE.** A file 160× longer and 160× larger costs 22 ms against 13 ms to
plan, and its `init.mp4` and first segment are the same 3–4 ms as the small one's. `buffered=0.09-861.44`,
`tries=1` on all 120 — nothing ever answered `503`.

**The phone is also FASTER than the simulator, by an order of magnitude on the term that dominated there:**
`tInit` is 3–5 ms here against 55–57 ms. That fetch starts production and waits for segment 0 to be whole,
so the simulator figure was measuring a virtualised filesystem and an x86 decode more than anything the kit
does — worth remembering before another simulator number is read as a proxy for a phone.

⚠ **`tFirstFrame` is deliberately NOT in this table** — 81 ms for the small file against 954 ms for the big
one, which is not a first-paint result. The probe appends EVERY segment before waiting for a frame, so it
scales with segment COUNT (12 against 120) and measures the probe's own loop.

⚠ Still no BEFORE number, on any machine.

### The spill shape, on a real iPhone — measured 2026-08-23

A source the writer cannot cut — one keyframe, 25 s, 80,839,033 bytes — makes the run exceed its memory
bound and write the segment out in several fragments. Both halves in one run on iPhone 17 Pro / iOS 26.6:

```
segments: the lead track has held 67115613 bytes without reaching a keyframe past the
          segment end, so this segment is being written out in parts to bound memory
SEGMENTS[clip-spill.mkv]: segments=1 | seg0Bytes=80841341 | appendInit=ok | appendSeg0=ok
                          appendedSegments=1/1 | buffered=0.00-25.00 | frame=1920x1080 | tries=1
```

🔴 **A `ManagedMediaSource` accepts a multi-fragment segment** — it buffers the whole 25 s and renders a
1080p frame from it. That is the shape nothing but Chromium had ever been handed, and the one the memory
guard produces whenever a source offers no cut point.

⚠ **The bounds are what make this fixture work, and the window between them is narrow.** Over
`MaxCopiedSegmentSeconds` (~30 s) the plan is refused as uncopyable and the run RE-ENCODES instead — a
different path; under `MaxPendingBytes` (64 MB) nothing spills at all.

**On Android too — measured 2026-08-23**, same fixture, MuMu Player 12 (Android 12, x64):

| | iOS 26.6 (iPhone 17 Pro) | Android 12 (emulator) |
|---|---|---|
| held before spilling | 67,115,613 bytes | **67,115,613 bytes** |
| appended | 1/1 | 1/1 |
| buffered | 0.00–25.00 | 0.00–25.00 |
| frame | 1920x1080 | 1920x1080 |
| attached by | `srcObject` | `objectURL` |
| first frame | 418 ms | 40,084 ms |

🔴 **The byte count is identical to the digit**, which is what a deterministic guard should look like — the
spill point is decided by the writer, not by the platform. Both shells accept the multi-fragment segment.
⚠ **The 40 s first frame is the EMULATOR**, software-decoding 1080p at ~26 Mbps; it is not a claim about
Android hardware, and `tSeg0=33 s` there against 66 ms on the phone says the same thing about production.

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
