# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react` and `@shenora/cli`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Released versions are listed newest first.

🔴 **`## Unreleased` states the END STATE of the release, not the order it was built in** (changed
2026-08-09). It used to be kept in landing order, on the reasoning that the entries narrate one version
being built — and that is exactly what went wrong: a shape reworked twice before shipping got two entries,
the earlier one naming types the later one had renamed, and **this file is exempt from every prose gate by
construction**, so nothing could see it. When a change supersedes an earlier entry in the SAME unreleased
version, REWRITE that entry rather than appending beside it. Released sections are frozen and stay as they
were published.

🔴 **AN ENTRY BELONGS UNDER `### Breaking` ONLY IF ITS OLD SIDE WAS ACTUALLY RELEASED, AND THAT IS
CHECKABLE:** `git grep <old-name> <last-tag> -- src/`. If the name was introduced in the same unreleased
window, nobody can migrate from it and the entry is development churn wearing a migration note — describe
the FINAL shape under `### Added` instead. Five entries failed this test on 2026-08-09 (the media seams
reshape, which said so in its own text; `WithDeviceEncoders`' extra parameter; the mobile shells' type
split; the native-player registration; the whole activity-drawing vocabulary, renamed twice before it
shipped). **This matters beyond tidiness: `### Breaking` is the SemVer gate at 1.0, so padding it with non-breaks is how a real break gets
lost in the noise.**

**Each `###` heading appears AT MOST ONCE per version** — append to the existing group, never open a
second one. `## Unreleased` had grown two separate `### Breaking` lists (P5.5 H7), which is worse
here than untidy: that heading is the SemVer gate at 1.0, so a reader scanning it would have stopped
at the first list and missed five more breaking changes.

## 0.12.0 — 2026-08-20

### Breaking

- **`ISegmentEngine` reads through an OPENER, not a path** — `DurationOf`, `HasPicture` and `PlanSegments`
  now take a `MediaByteSource`, and `SegmentRunRequest.SourcePath` (a `string`) is now
  `SegmentRunRequest.Source` (a `MediaByteSource`). `HasRenderedPicture` still takes a path, because it
  reads a fragment the engine itself just wrote into its own output directory.
  - **Why:** the kit's own engine called `File.OpenRead` in two places, so a source that was not a local
    file could be *described* and never produced from — `SegmentStream` would answer a manifest for a
    registered remote source and then report *"the production run failed"* on every segment. Where the
    bytes live is a transport question and the tier had no seam for it.
  - **Migrating a call site:** `MediaByteSource.ForFile(path)` is the local case and behaves exactly as
    before. A custom `ISegmentEngine` changes three signatures and reads `request.Source`.
  - ⚠ The stream an opener returns **must be seekable and must report `Length`** — Matroska states where a
    frame lives rather than streaming it in order, so a forward-only body cannot be indexed at all. The
    engine now says so by name instead of reporting the source as unreadable Matroska.
  - **`RemoteMediaSource.Open` is new and optional**, and a registered source without one is refused at
    the MANIFEST rather than at its first segment: a manifest is derived from the duration, which is
    suppliable, so serving one anyway hands the page a complete playlist whose every entry `503`s for
    ever. `Url` is now identity only — the address stays inside the app's own opener closure, which is
    what keeps it out of a kit log line by construction rather than by care.

- **A segment stream now opens with a SHORT first segment by default** — `SegmentStreamOptions.HeadSegmentSeconds`,
  defaulting to `[1, 2, 4]` before the steady `SegmentSeconds`. Set it to `[]` for the old uniform stream.
  - **Why: segment 0 is the entire startup budget.** A page cannot play until the init segment arrives,
    that request drives segment 0, and a VOD playlist starts there — the "begin three target durations from
    the end" rule is a LIVE one. So the previous uniform six seconds meant six seconds of production before
    the first frame. `EXT-X-TARGETDURATION` is an upper bound, so a short lead-in is ordinary playlist and
    it still states the steady length.
  - **A ramp rather than short segments throughout**, because short segments cost a request each and cost
    bitrate: a keyframe every second measurably raises what the same picture needs.
  - ⚠ **It is a REQUEST, not a promise.** A copied picture is cut where the SOURCE has keyframes, so a
    ten-second GOP still gives a ten-second first segment. The ramp changes which keyframes are chosen, not
    where they are.
  - **Refused at composition time**: a head length that is not a whole multiple of the encoders' one-second
    keyframe interval, or one longer than the steady length. Same policy as a fractional grid, and for the
    same reason — those segments play, and only seeking misbehaves.
- **`ISegmentEngine.PlanSegments` takes a `SegmentLengths` instead of a `double`**, since the head ramp is
  part of what the caller is asking for and only the engine knows where the boundaries can land.
- **`SegmentPlan` now states its `Origin`** (new `SegmentBoundaries`: `Grid`, `SourceKeyFrames`,
  `EncoderCuts`), and a run reads it to decide whether it may COPY the picture.
  - **Why: the run used to INFER that from "is this a grid?"**, which held only while every non-grid plan
    came from the source's own keyframes. A head ramp is a third shape — explicit boundaries an encoder can
    hit — and under the old inference a run would have copied onto it, slipping every cut to the next source
    keyframe. The segments still play; only a seek shows it. New factory `SegmentPlan.EncoderCuts`.
- **A segment engine must now PUBLISH ATOMICALLY, and the segment route serves a part the moment it
  exists.** A run writes `seg{k}.m4s.part` (new `SegmentRunRequest.PartialExtension`) and renames it into
  place once whole; the same goes for `init.mp4`.
  - **Why: it was costing a whole segment of startup latency.** Completeness used to be *inferred* — a
    segment was servable only once the NEXT one existed, or the run had ended — because a progressive muxer
    creates a file when it *starts* writing it. A page cannot play until `init.mp4` arrives, that request
    drives segment 0, so nothing played until segment 0 **and** the opening of segment 1 had been produced.
    Renaming makes the producer state the answer instead. (The same inference, and the same cost, is visible
    in other just-in-time transcoders; ffmpeg's equivalent is `-hls_flags +temp_file`.)
  - **Only a custom `ISegmentEngine` has to change** — the kit's own engine already does this. An engine
    that writes in place will now have truncated fragments served, which appends without error and plays for
    a fraction of a second.
  - **Crash recovery got simpler and stopped destroying good work.** The route used to delete the
    highest-numbered segment on every open of every source, on the reasoning that a kill leaves a file that
    exists, is non-empty and is short. It now deletes `*.part` files, which is exactly the set that can be
    torn — so an interrupted run costs nothing beyond the part it was mid-way through.
  - ⚠ **One check narrowed:** the picture-stall detector used to inspect a still-open first segment, and a
    part being written is now hidden. It still catches a finished window start with no picture; a run that
    publishes *nothing at all* now falls to `WaitBudget` and answers `503` without advancing the encoder
    ladder. Stated on the method rather than papered over.

### Added

- **`MediaByteSource.ForRanges` — a remote media source needs only a byte-range fetch now** (D78). Supply
  `(offset, count, ct) => Task<Stream>` over your own client and the kit supplies the seekable, buffered
  stream the demuxer needs; it is what `RemoteMediaSource.Open` wants, so the two compose directly.
  - **The buffering is the reason it ships, and it is not an optimisation.** Matroska is parsed by EBML
    varint — one `ReadByte` at a time — so the adapter an app writes first costs one round trip **per byte**.
    A local `FileStream` buffers for free, so porting from `ForFile` gives no warning that the naive version
    is unusable rather than merely slower. Measured at **4 fetches to plan a 456 KB file**.
  - **The kit still ships no transport.** No `HttpClient` appears anywhere in `src/`: auth, refresh, proxies,
    redirects and retry stay the app's, and no url ever reaches the kit — which is what keeps a credential
    out of a kit log line by construction rather than by care.
  - ⚠ **The source's length must be known up front** (`Content-Length`, or any ranged response's
    `Content-Range`): Matroska is read by offset from the END, so a source that cannot state its size cannot
    be indexed at all.
  - 🔴 **A server that ignores `Range` and answers `200` with the whole file is refused by name.** It is the
    one such failure that is otherwise silent — it satisfies `EnsureSuccessStatusCode` and every length check
    while handing the demuxer the START of the file, which then reads as corrupt media. Proven against a real
    HTTP server configured to misbehave, and proven quiet against an honest one.
  - **`docs/guides/media.md` carries the fetch to copy**, including the `ResponseHeadersRead` that stops
    `HttpClient` buffering a whole film to answer a 256 KB range.

- **`@shenora/cli` can drive a Mac that is somewhere else.** Every `ios` verb takes `--host user@mac.local`
  (or `SHENORA_IOS_HOST`, or a `remote` block in `shenora.deploy.json`), because the adopter this kit is
  pitched at is a .NET developer on Windows whose Mac is on the LAN rather than under the desk. `ios.ts`
  used to ask `/bin/sh` about Xcode and `node:fs` about the build output in adjacent lines, which is only
  correct while both are the same machine; a `Target` seam now separates "run a command there" from "ask
  about ITS filesystem", with a local and an ssh implementation.
  - **`ios doctor` diagnoses the TRANSPORT before anything else**, and the ordering is the whole value: a
    Mac that is merely asleep answers every probe with silence, so without this the report reads
    `MISSING Xcode`, `MISSING .NET SDK` — confident and completely wrong about a machine that is fine. It
    separates the six causes that all present as "cannot connect": asleep, Remote Login off, key not
    authorised, an `.local` name that does not resolve, ssh's auth-retry budget, no ssh client here.
  - **A device build is handed to the Mac's GUI session**, because `codesign` cannot use a login-keychain
    key from an ssh session — a different audit session, so signing dies with `errSecInternalComponent`
    whatever you sign. That includes `ios build`, which reads as "just a build" and is in fact the one
    command with no unsigned path through it at all.
  - ⚠ **The on-Mac half is UNVERIFIED.** Everything provable without a Mac is tested — the ssh command
    ceiling, the GUI script's shape, the six-way diagnosis, the path spelling — and three diagnosis
    branches were driven end to end against real ssh. A build, sign, install and launch against actual
    hardware has not run.

- **`shenora ios provision` — mint the signing profiles a device build needs.** The .NET iOS SDK
  *consumes* provisioning profiles and never creates one, so a bundle id nobody has provisioned fails
  with *"Could not find any available provisioning profiles"* — an error about your app, caused by the
  absence of a step the toolchain does not offer at all. This drives `xcodebuild
  -allowProvisioningUpdates` against a throwaway Xcode project, once per bundle id, through the Mac's own
  login session (Xcode's stored Apple ID session has the same audit-session problem as the keychain).
  - **Extensions are included by naming them**, because an extension is provisioned separately from its
    container and forgetting one fails at the very END of a device install with an error naming the app.
  - **The team id is READ OFF the Mac's signing certificate** rather than required in config. It
    identifies a developer account, and `shenora.deploy.json` is normally tracked — requiring it there
    would mean either publishing it or being unable to provision. This file already holds that no
    machine-specific fact belongs in config; a team id is one.
  - **It reports what is ON DISK afterwards, not what `xcodebuild` said.** A build can succeed against a
    profile it already had, so a zero exit does not mean a profile now exists for the id that was asked
    for — and "provisioned successfully" followed by a device build failing for want of a profile is
    exactly the false success this CLI exists to prevent.

- **`shenora ios push` — send this working tree to the Mac.** Without it every other remote command was a
  claim about code that might not be there: the Mac built whatever its checkout happened to hold, and
  nothing said so. It sends what git lists as source — tracked plus not-ignored — so `bin/`, `obj/` and
  `node_modules` stay here (measured on this repo: 626 files against 23,882 on disk), and **uncommitted
  edits travel**, because the obvious `git push` implementation would have the Mac build HEAD and the fix
  you just made would never arrive. It adds and overwrites; it does not delete.

- **`shenora inspect` — a device that answers back.** *(Named `diag` while it was being built; renamed
  before it ever shipped, so nothing to migrate. `diag` abbreviated a SCENARIO — "I am diagnosing" —
  where `inspect` is what every other toolchain calls attaching to a running app and looking inside, so
  an adopter guesses it without reading this. Running a command on the Mac moved to `ios exec`, where the
  target and its `--host` resolution already live: it acts on the Mac, not on a phone, and grouping it
  here made one command mean two unrelated things.)* `inspect serve` starts a service, a phone on the LAN opens
  the printed URL and **polls**; `inspect devices|report|eval` drive it from another terminal, and `ios exec`
  runs a command on the Mac. The direction is the trick: a webview cannot be dialled into — no port, no
  agent — so the only channel that exists is one the page itself opens. It ships inside nothing, because
  it runs arbitrary JS in whatever page polls it, and a diagnostic hosted inside the app dies with it
  exactly when it is needed.
  - **The page IS the console when you open it here.** On loopback it shows what to do next, which
    devices have checked in, and two controls: run an expression in the device's page, or run a command
    on the Mac over ssh. That last route existed from the start and **nothing called it** — reachable
    only by hand-written `curl`, which is a capability indistinguishable from a broken one. Opened from
    the LAN the operator half is absent entirely; the hiding is cosmetic and the SERVER is the boundary,
    which is checked from a genuinely remote machine rather than asserted.
  - **A failed `inspect eval` now exits non-zero.** It printed `(threw) …` and exited 0, so
    `inspect eval … && next-step` marched on after a failed probe — the same false success this CLI polices
    in builds, arriving through the one command whose entire job is telling you the truth about a device.
    Found by running it against a real WebKit rather than a fake.
  - **Split by trust, not convenience.** Queueing work, reading results and running an ssh command decide
    what RUNS, so they are loopback-only, checked against the socket's own address and never a header; a
    request for one from off-box gets 404, not 403. Polling and reporting stay open, because the device
    you most need to diagnose is routinely the one that cannot authenticate. Proven against a live server
    from this machine's own LAN address, and sabotage-verified.

- **`SegmentStream` can stream a REMOTE source, and the page cannot name one.** `SegmentStreamOptions`
  gains `Sources`, a `MediaSourceRegistry` the app registers a `RemoteMediaSource` with and gets an opaque
  handle back; the route serves `~remote/{handle}/…` and nothing else. That inversion is strictly tighter
  than the url predicate the conversion route uses: a policy judging a page-supplied url can be **wrong**,
  and being wrong means the host fetches an address the page could not reach itself. A handle that was
  never issued cannot be guessed. Null — the default — means local files only. Found by an adopter who
  had to fork the route to get it, because the alternative for a track the webview refuses was a
  server-side transcode of a file the device's own engine reads fine.
  - **The url is treated as a secret and the label is not.** A remote media url routinely carries
    credentials, and `Path.GetFileName` — which sanitises a local path — leaves a query string completely
    intact, so every existing diagnostic was one interpolation away from logging a signature. A test plants
    a token in a url, drives the route and reads the log back.
  - **`Duration` and `HasPicture` are suppliable**, because probing a remote source costs two engine
    launches reading a network header before the first manifest can be answered — and whoever registered
    the source usually has both already. Unset still probes.
  - **`Identity` keys the cache when the url rotates.** A presigned url is a different string every hour
    for the same film; keyed on the url it re-segments from scratch each time while the old copies wait for
    the sweep.

- **`@shenora/cli` predicts the Xcode/bindings mismatch instead of discovering it at minute twenty.**
  `ios doctor` gains a `device signing` row and an `ios bindings` row; the second is the one that
  matters, because every other row can say `ok` on a Mac that cannot build at all — the workload and
  Xcode are each fine and only their PAIRING fails, at build time. Measured: no workload band ships a
  pack for Xcode 26.3 (only 26.0, 26.6, 27.0), so `dotnet workload update` merely changes WHICH Xcode
  is demanded. The fix is an asymmetry — bindings NEWER than the SDK name APIs that do not exist,
  bindings OLDER are fine — so the doctor names the newest band the Xcode can satisfy, and a failed
  build prints that exact `-p:TargetPlatformVersion=` pin rather than only offering `--simulator`. A
  **device** build works this way; it is a dev-loop unblock and deliberately a visible one, since
  building against older bindings moves a missing API from compile time to runtime.
- **`shenora.deploy.json` takes `iosTfm` beside `androidTfm`.** The unqualified `tfm` still works and
  is still read as the iOS one — which is exactly how it bit an adopter, so a TFM naming the other
  platform is now refused up front with the field and the fix.

- **`@shenora/react` names the media CONVERSION wire it was always meant to.** `MediaConversionEvents`
  (`sourceProgress`/`ready`/`failed`), `MediaConversionErrorCodes.unsupportedCodec` and
  `MEDIA_PLAYER_STATUS` are exported constants now. The host published all four and this changelog told
  a page to *"wait on READY before setting its element's src, and branch on FAILED's reason"* — while
  the client named none of them, so every page typed the raw strings, which is exactly the divergence
  `WireMirrorTests` exists to prevent. It could not see this one: the mirror was a hand-written list of
  families with no check that the list was COMPLETE — the same allow-list shape that had left nine
  types unpublished from `wire.md`. That check now exists, reading the generated (and gated)
  `docs/reference/wire.md`, so a new wire family must be mirrored or declared host-only. Sessions are
  declared host-only, with the reason.

### Fixed

- **`shenora android build` never resolved a JDK, so `android doctor` went green and the publish then died.**
  `cmdDeploy` resolved one, refused without it and passed it as `JAVA_HOME`; `cmdBuild` did none of the three
  and published with no environment at all — inheriting whatever the shell happened to carry. On a Windows box
  with Android Studio and no global `JAVA_HOME`, `doctor` printed the `jbr` path it found and the very next
  command failed `error XA5300: The Java SDK directory could not be found`.
  - **A green check that does not predict the command it checks is worse than no check** — it sends the reader
    to distrust the SDK install, which is the one thing that was fine.
  - Reported by an adopter, and present in published `@shenora/cli@0.11.0` as well as the tree.

- **Android's H.264 encoder was configured at roughly a thirtieth of its intended bitrate.**
  `AndroidMediaVideoConversion` computed `w * h * 3 / 10` — 0.3 bits per PIXEL, with no frame-rate factor —
  where the intent was 0.15 bits per pixel per FRAME. 720p30 fell through to the 400 kbps floor and 1080p30
  got 622 kbps. Now `w * h * fps * 15 / 100`: 4.1 Mbps and 9.3 Mbps respectively.
  - **What proves it was the code and not the comment: the upper clamp was unreachable.** Hitting the
    12 Mbps ceiling needed a 40-megapixel frame, and a clamp that can never fire at either end is the
    signature of a lost `× fps`.
  - ⚠ **Newly reachable for ordinary content.** A grid or head-ramp plan re-encodes the picture even when it
    could have been copied, so 1080p H.264 — previously copied straight past this encoder — now meets it.
  - ⚠ **Arithmetic only; not verified on hardware.**

- **`MatroskaProbe` reported `"vfw"` for a whole family of real files, so they were transcoded when a
  lossless remux would have served them.** Matroska has native ids for h264, HEVC, MPEG-2, MPEG-4 Part 2,
  VP8/9 and AV1; everything else uses the Video-for-Windows wrapper with the true codec as a FourCC inside a
  `BITMAPINFOHEADER`. The translation for that existed and was correct — but `ReadTracks` called the
  one-argument `CodecNameOf` and the probe had no `CodecPrivate` element id at all, so it could never reach
  it. The probe now reads the first 20 bytes of `CodecPrivate` (enough for the FourCC at offset 16) and
  reports what it names.
  - **The costly case is not the obvious one.** An XviD file reported as `vfw` was re-encoded, which it
    needed anyway. **H.264 in a VfW wrapper is decodable**, so reporting `vfw` made the planner answer
    `Transcode` for a file `Remux` would have served — slow and lossy instead of fast and lossless.
  - ⚠ **Every engine-side caller already passed the private data**, which is why nothing noticed: only the
    PROBE path was wrong, and the tests exercised the translating overload directly rather than through
    `Read`. There are now cases that go through `Read`, and they are sabotage-verified.

- 🔴 **The mobile clipboard's MULTI-FORMAT paths threw on every call, on iOS.** `SetAsync`/`GetAsync`
  reach `UIPasteboard` directly, and UIKit refuses off the main thread —
  `UIKitThreadAccessException: you are calling a UIKit method that can only be invoked from the UI
  thread`. They now marshal.
  - **Only the formats path was affected, which is why nothing noticed.** `SetTextAsync`/`GetTextAsync`
    go through MAUI's own `Clipboard.Default`, which marshals internally; the two that reach the platform
    themselves had nothing doing it for them. A compile cannot see this and the text path hides it.
  - ⚠ **An async API that must be called from the UI thread is a trap the caller cannot see**: the
    signature says "await me", so awaiting it from a background thread — what `Task.Run` and every
    library continuation give you — is the natural thing to write. The kit marshals rather than
    documenting a rule nobody reads at the call site.
  - Found by a new `[CLIPBOARD]` startup probe in the MAUI sample, on its FIRST run. With it fixed, iOS
    answers the questions that filed the task: text round-trips, `text/html` and an app's own
    `application/…` type both return off one item, and `xcrun simctl pbpaste` — a foreign reader —
    prints the written sentinel.

🧭 **Found by taking the remote path to a REAL Mac.** Every one of these passed a green gate, and none of
them could have been found by reading — which is the argument for the trip rather than a note about it.

- **The Xcode-mismatch advice named a flag that cannot work.** `ios doctor` and the build-failure handler
  both said to pin `-p:TargetPlatformVersion=<band>`. `-p:` sets a **global** MSBuild property, so it
  reaches every project in the graph including the plain `net10.0` ones, which have no target platform at
  all and fail with `MSB4184 … "targetPlatformIdentifier" cannot have zero length` — an error naming
  neither iOS nor the version that caused it. The pin belongs in the iOS head's `.csproj`, where it
  applies to that project alone; both messages now say so. **The band selection itself was right**: the
  build succeeded against bindings 26.0 on an Xcode whose SDK is 26.2.
- **A remote build was declared stale immediately after succeeding.** The freshness check read the
  `.app`'s `Info.plist` on the stated rule that it "is rewritten every build". Measured on the Mac: after
  a successful incremental build the `.app` was **34 seconds** old and its `Info.plist` was **3.9 days**
  old, so the CLI refused to install a build that had just succeeded on screen. It now takes the newest
  mtime anywhere in the bundle — no single file inside a build output is a clock — and allows 30 s of
  skew for a remote target, because two machines means two clocks.
- **`dotnet build` was handed the CHECKOUT ROOT, not the project.** One helper answered "the project's
  directory" locally and "the repo root" remotely, so a remote build went off to compile the solution
  found there — the Windows sample and the test project — and failed with `NETSDK1100: To build a project
  targeting Windows on this operating system`, on a Mac that was working perfectly.
- **A missing ssh key file was diagnosed as a key the Mac refused.** ssh reports both: it warns that it
  could not open the identity file, then reports the denial that follows from having none to offer.
  Matched on the denial, the advice was "append your public key to the Mac's `authorized_keys`" — sending
  you to configure the wrong computer for a file missing on this one. Now its own verdict.
- 🔴 **`ios doctor` reported two things MISSING that were present**, which is the worst answer a doctor
  can give: it sends someone to fix what is already fixed, and keeps saying so afterwards. Both were
  found by an owner adding an Apple ID and the row not changing.
  - **A remote `probe` applied `set -o pipefail`; the local one never has.** So `xcodebuild -version |
    head -1` — where `head` closes the pipe and `xcodebuild` dies of SIGPIPE — had that promoted to the
    pipeline's status, and doctor reported **`MISSING Xcode` on a Mac running Xcode 26.3**. Racy, too:
    the same Mac answered correctly on one run and "not installed" on the next, depending on whether the
    producer noticed the closed pipe. `pipefail` is right for a command whose failure matters and wrong
    for a probe, where failure is an ANSWER — and one capability probe must not give two answers
    depending on which transport ran it.
  - **The Apple ID count looked for an email address.** Xcode stores an `identifier` UUID, so a
    signed-in account read as none. It now counts records inside the list — and the empty-list case is
    pinned too, because the preference key exists either way, so its presence proves nothing.

- **`ios log --device` hung for minutes and then reported a failure it had not had.** Two faults in one
  command, both found the first time it ran against a phone. `head -N` closes the pipe but
  `devicectl --console` does not die of the SIGPIPE — it stays attached, so the pipeline never ends and
  the command runs until something outside kills it (measured: five minutes after printing its forty
  lines). And the status it exits with when that finally happens is not one the success list knew, so a
  console attach that had streamed the app's whole startup ended by printing *"could not attach a console
  to the device"*. It is now bounded by TIME — a console attach is bounded by how long you want to watch,
  not by how much it says — and the three statuses that mean success are all accepted: `124` (the bound
  firing, the normal end), `141` (SIGPIPE, when the app is chatty enough to fill `-n` first) and `0`.
- **A failed DEVICE build printed nothing at all.** `target.gui` cannot stream — its script runs detached
  in the Mac's own login session, so the log exists only as a return value — and the device path never
  printed it. The result was the single line *"the build failed — see the output above"* with nothing
  above it: a tool reporting a failure it declines to explain. The publish path already printed its log
  and this one did not, which is the shape of every second-call-site bug.
- **"Could not find any available provisioning profiles" now says what it means** — that no profile
  matches THIS bundle id, not that the Mac has none — and names the Apple ID step that creates one.
- **`ios push` deletes what it previously sent and would no longer send.** The first version only added
  and overwrote, on the reasoning that a tool should not `rm` over the network. It broke the very first
  real build: the Mac's older checkout still held files this kit had since renamed, so both copies
  survived, `IFileLockInspector` existed twice, and the KIT failed to compile with three errors on a tree
  that is clean here. A stale source file is not clutter, it is a second definition. Deletion is bounded
  by a manifest this tool writes, so it can only ever remove paths it put there; on a first push into an
  existing checkout, git's own index serves as that manifest — which is exactly the case that broke.
- **`deploy --simulator` announced "running in the simulator" for an app that crashed on startup.**
  `simctl launch` prints a pid and exits 0 whether or not the process survives, so "launched" was being
  read as "running" — and the app died every time. Caught by screenshotting the result and finding the
  simulator sitting on its home screen. It now checks the pid three seconds later and, when the app is
  gone, says so and prints the crash. Precisely the false-success class this CLI's README claims to have
  closed, reached from a direction none of the existing checks watched.
- **`--key` and `SHENORA_IOS_KEY` are new**, because there was no way to name a project-scoped ssh key
  without committing the hostname beside it: `shenora.deploy.json` is normally tracked. A tool whose only
  configured path leaks is a tool people configure wrongly.

- **`SegmentStream` ignored `MediaAccessOptions.Log`.** That option says it is stated once for every
  delivery path and the conversion route beside it reads it, but the segment route consulted only its own
  optional parameter — so an app that configured the shared sink got diagnostics from one route and silence
  from the other, with nothing to say which. Found while writing the remote-source leak test, whose first
  run captured no lines at all.

🧭 **The first ADOPTION HARVEST (D15), from Yaorin's 0.10.0 → 0.11.0 upgrade.** Three findings, and the
first two are things only an adopter could have found — the kit's own tests and gates were green for
both.

- 🔴 **`DerivedCacheKey` is PUBLIC again**, restoring what 0.11.0 removed. It was demoted because "every
  consumer is in the same assembly", which is unfalsifiable from inside this repo: the adopter's
  on-device HLS route keyed its segment directories with it, and the removal left them carrying a
  byte-for-byte copy with a comment forbidding edits. ⚠ **The value was never the SHA-256** — it is that
  an app's key AGREES with the kit's, and every knob (separator normalisation, case, field order, tick
  precision, the 8-byte truncation) yields a *valid-looking* key that matches nothing when it drifts,
  silently orphaning every cached artefact on every device with no error anywhere. The exact format is
  now pinned by golden values in `DerivedCacheKeyTests`, so it is defended rather than described.
- **`MediaSource.Uri` accepts a `file:` URL.** `new Uri(path).AbsoluteUri` — the obvious thing for a
  .NET caller to hand over — was refused as *"not a file path or an absolute URL"* while being both,
  because the guard read `!parsed.IsFile`. It costs nothing to accept: the rooted-path branch already
  produced a `file:` URI, so every consumer downstream was handling one. The rejection message now
  names what is actually left (a relative string) and the fix, instead of the two things the rejected
  input already was.
- **A generated `docs/reference/namespace-moves.md`** — old fully-qualified name → new one for every
  type that changed namespace, from the API baselines (`dev.mjs namespace-moves <tag>`, and a release
  step). The 0.11.0 notes carried the PACKAGE fold and the adopter still met one `CS0246` per type,
  each a grep through the kit's source, because a fold re-namespaces within the package too: **154
  types moved where the notes named five.**

### Changed

- **A production run now indexes the source AS IT WRITES, instead of walking every cluster first.** It
  indexes far enough to open its first segment, then asks for more as the pump consumes what it has.
  - **Why: this was the last whole-file walk on the first-paint path.** A page cannot play until the init
    segment arrives, that request starts the run, and the run used to index every cluster to the end of the
    file before emitting a single fragment. Planning stopped walking when Cues arrived; producing did not.
  - ⚠ **The risk this buys is real and is guarded rather than assumed.** `SampleTiming.Derive` sorts, and
    takes the presentation shift as a maximum over everything it is given — so a B-frame stream derived per
    chunk could get a different decode order or a different shift either side of a seam, which appends
    without error and plays wrongly. The shift is therefore taken from the FIRST chunk and never changed;
    decode times may not go backwards past what is already written; a negative composition offset is
    clamped; and a stream that reorders across a chunk boundary is reported once by name.
  - **Pinned by a differential test on a real B-frame clip**: every fragment must open at a decode time
    that a full walk plus a whole-track derivation also produces. The control is derived independently
    rather than by comparing the engine with itself, and the test asserts the fixture really is reordered
    so it cannot go quiet if the clip is ever replaced.
  - **A run keeps one sample of lookahead** before consuming its last known frame, because a copied sample's
    duration is the gap to its successor — without it, every chunk's final frame would take the track's
    declared duration instead of its real one.

- **A segment stream now plans from the source's OWN keyframe index instead of walking every cluster to
  rediscover it.** `SeekHead` → `Cues` gives the keyframe times directly, in two small reads. The walk it
  replaces seeks past every frame in the file to read block headers and touches about a third of its pages
  — on the request that answers the first manifest, before any segment can be produced. No API changed.
  - **The walk remains the answer whenever the index is absent or untrustworthy**, and that is the larger
    half of this change: Cues are optional in Matroska, and a live mux, an interrupted recording or a
    truncated download all lack them. Both paths share one implementation of the greedy-forward boundary
    rule, so a file planned either way cuts in exactly the same places — pinned against a real ffmpeg-muxed
    clip, where the index and a full walk produce identical keyframe times.
  - **A broken index is refused rather than believed**, because it is worse than an absent one: absent
    falls back, broken puts every boundary where no decoder can start and nothing downstream can notice.
    Refused are an index with fewer than two points, times that do not ascend, a last cue past the declared
    duration, cues describing a different track, and — the one that is otherwise invisible — positions that
    do not land on a Cluster, which is what an absolute-vs-segment-relative mix-up produces.
  - **A `SeekHead` pointing at a second `SeekHead` is followed.** MKVToolNix writes that layout whenever an
    in-place header edit outgrows its reserved space; it is spec-legal, it is common, and handling only one
    level reports "no index" for files that have a good one.

## 0.11.0 — 2026-08-17

### Breaking

📋 **UPGRADING FROM 0.10.0? `docs/reference/namespace-moves.md` is the mechanical list** — old
fully-qualified name → new one, for all **154** types that changed namespace, generated from the API
baselines. The package-fold table below names the PACKAGES (`Shenora.Core` → `Shenora`), and the first
adopter found that following it alone still costs one `CS0246` per type, because the fold also
re-namespaced within the package (`Shenora.Core.IEventBus` → `Shenora.Core.Events.IEventBus`). Read the
generated list first and the prose below for the reasoning.

🔴 **A bare `Session…` name now means SHARED by every session kind; one that belongs to a single kind
carries that kind.** Six types were named for the area and served exactly one session type, which is the
opposite of what the name promised — `SessionResult` was returned only by an interactive session, and
`SessionFrame` only by a streaming one.

| was | now |
|---|---|
| `SessionResult` | `InteractiveSessionResult` |
| `SessionErrorCodes` | `InteractiveSessionErrorCodes` |
| `SessionFrame` | `StreamingSessionFrame` |
| `SessionFrameFormat` | `StreamingSessionFrameFormat` |
| `SessionEnded` | `StreamingSessionEnded` |
| `SessionEndReason` | `StreamingSessionEndReason` |

The names that stayed bare are the ones that earn it: `SessionBrowserOptions` and its five hook payloads,
`SessionController`, `SessionCookie`, `SessionViewport`, `SessionPointerAction`, `SessionEvents` — each
used by more than one session kind. ⚠ The error-code **values** are unchanged (`SESSION_BUSY` and
friends): they cross the IPC error contract through `ThrowIfFailed`, so a page matching on them is
unaffected.

Three files were renamed for the same reason and break nothing: `WinFormsHost.cs` named a type deleted
before 0.10.0 (→ `WindowsHostExtensions.cs`), and the two samples still carried the `Facade` vocabulary
D65 retired (→ `SampleModule.cs`, `PortableSampleModule.cs`).

🔴 **An interactive session takes a whole `SessionBrowserOptions` now, exactly like every other
session.** `InteractiveSessionOptions.{ProfileDirectory, Events, ObserveResponse}` are GONE; set
`Browser` instead.

```csharp
// before
new InteractiveSessionOptions { Anchor = form, ProfileDirectory = path, Events = bus }
// after
new InteractiveSessionOptions { Anchor = form,
    Browser = new SessionBrowserOptions { ProfileDirectory = path, Events = bus } }
```

It built its browser options INTERNALLY and forwarded two fields by hand, so an interactive session
could not serve the app's own bundle, could not take a request filter, could not take any of the five
hooks, and was SILENT — no logger reached it. `RenderSessionPool` and `StreamingSession` both took the
whole object already; this is the odd one out being brought into line, and `SessionBrowserOptions` is a
`record` so the session `with`-overrides the ONE field it owns (`KeepAliveInBackground`, from
`RevealImmediately`) and inherits everything else by construction. ⚠ Copying field-by-field was the
obvious fix and the wrong one: it works the day it is written and silently drops the next option added.
A reflection test now walks every property and fails if one stops passing through.

**Nine public names removed, none of which a consumer could use.** Every one was checked against
`src/`, `samples/` and `tests/` first; `Shenora.Tests` and `Shenora.Sample.Maui` see internals, so
"the tests need it" is not a reason to be public here.

- **`IShenoraModule` + `ShenoraApplicationBuilder.AddModule` are DELETED.** Zero implementations
  anywhere — in the kit, both samples, all three shells — and no mention in any doc; the only one that
  ever existed was a test double. It was also the **third** meaning of "module" on the front door,
  beside `IIpcModule`/`ModuleBase`/`MapModule` and the `Shenora.Modules.*` layer. If you had one:
  `builder.AddModule(new FooModule())` → `FooModule.Configure(builder.Services)`, which is how the kit
  and both samples already slice.
- **`SessionBrowser` → internal.** It was `public static` with **every member internal** — its baseline
  row had no members at all, so nothing on it was callable.
- **`WebView2Interceptor` → internal.** Public with an internal constructor and no members beyond
  `IWebViewInterceptor`, which every consumer already goes through.
- **`Mp4Layout`, `Mp4SampleSpan`, `Mp4LayoutReader`, `Mp4Remuxer.Plan` → internal** (~24 rows). The
  sibling `Mp4LayoutRangeStream` was already internal and the plan seam is documented as test-only, so
  the boundary was already drawn one type over. For a byte↔time map of your own, `IComputedRemuxRoute`
  is public and stays.
- **`DerivedCacheKey` → internal**, and moved out of the SemVer surface entirely: every consumer is in
  the same assembly, and a SHA-256 over caller-supplied values is `crypto.subtle.digest` — the one
  member in that layer that cleared no part of the native-capability test.
- **`EventBus.GetHandlerCount()` and `MapRegisteredModulesLazily` → internal.** Neither had a consumer
  outside its own file or tests; `WebViewPipeline` already refuses the identical count member in
  writing.

⚠ Removing those types left five words in `surface-lexicon.txt` with nothing to justify them, and the
vocabulary gate said so: *"an allow-list that only grows reviews nothing."* `Cache`, `Derived`,
`Reader`, `Sample` and `Span` are gone from it.

🔴 **`IFileUpdateQueue` gains `RecoverAsync`** — crash recovery is now callable from what the framework
actually registers. Implementing your own queue? Add the method; it may `return Task.FromResult(0)` if
you keep no journal.

**Why it had to move.** `Build()` registers the queue **with a journal on by default**, and only the
interface — while `RecoverAsync` lived on the concrete type. So
`app.Services.GetRequiredService<IFileUpdateQueue>().RecoverAsync()` did not compile, which is why the
guide's own example hand-constructed a `new FileUpdateQueue(...)` and side-stepped DI. A downcast is not
a fix either: it fails **silently** the moment an app registers its own queue, which `UseFileSystem`
explicitly invites. Meanwhile a journal nobody replays is a directory that fills up while interrupted
updates stay un-rolled-back.

`docs/guides/file-updates.md` now shows the DI call, and a test resolves the interface from a built app
and calls it — the suite previously could not fail on this.

🔴 **The two `Mp4Remuxer.Remux` overloads that omitted the conversion are GONE.** `Remux(string, string,
CancellationToken)` and `Remux(Stream, Stream, CancellationToken)` are removed; pass the conversion
explicitly — `conversion: null` if you really want none.

**Why they could not stay.** D59 is about exactly this: *"the overload every adoption example wired
passed `conversion: null`, so a registered conversion was never called and the remux simply dropped the
soundtrack — a film that played SILENTLY."* The shortest, most discoverable call on the type still did
that, and it returns `Succeeded` while the loss is reported only in `MediaRemuxerResult.Dropped`. Making
the parameter unavoidable is what stops the defect recurring; `null` is now a decision you type.

🔴 **`UseErrorHandler()` / `UseLogging()` now REFUSE a dispatcher they cannot log through**, instead of
silently logging nowhere. If you wrap the dispatcher — for metrics, tracing, an app-side guard — its own
logger is unreachable (that lookup only works on the kit's concrete `MessageDispatcher`), so the call
now throws at **composition** with a message naming your type. Pass a logger:
`decorated.UseErrorHandler(logger)`, or `NullLogger.Instance` for deliberate silence.

**What it was doing before.** Falling back to `NullLogger`. So behind a decorator every unhandled
exception was mapped to `UNKNOWN_ERROR` for the client **and logged nowhere at all** — the one place the
kit promises the detail stays host-side. Nothing indicated it; the pipeline worked, the client got its
error code, and the diagnostic simply did not exist. Decorators are positively encouraged by
`IModuleRegistry`'s own documentation, so this was a supported shape.

Unaffected: the kit's own composition (`UseMessageDispatcher` wires the handler on the concrete
dispatcher before any decoration) and every call on a concrete `MessageDispatcher`, which is all of them
in this repo bar one test.

🔴 **Codec identity is a `MediaStreamCodec`, not a bare `string`** — across `IMediaCapability`,
`MediaPlaybackPolicy`, `IMediaStreamConversion.CanConvert`, `IMediaContainerWriter.CanCarry`,
`MediaStreamClaim` and `CanRepair`. A bare string still works everywhere (`MediaStreamCodec codec =
"aac";`), so most call sites are unchanged; **collection types are not** — `IReadOnlySet<string>` becomes
`IReadOnlySet<MediaStreamCodec>`.

**Why a name was never enough, and the kit already said so.** `MediaStreamInfo.Profile` exists because
*"HEVC `Main 10` is a different capability from the `hevc` a device advertises, so a codec name alone can
say 'supported' about a stream that will not decode"* — and the planner then matched on the name alone.
A Main-10 file on a Main-only decoder was planned `Direct` and rendered nothing, with no error anywhere.

**THE MATCHING RULE, which is the whole design.** A capability with **no** profile matches **any**
profile; one **with** a profile matches only that. So every device that reports bare names behaves
exactly as before — and a device that can name `hevc/Main 10` can finally say so. ⚠ It is asymmetric on
purpose: the capability side may be broad, the stream side is the concrete thing being asked about.

**Android now reports profiles.** `MediaCodecList` hands back `ProfileLevels` and the shell discarded
them; it now adds the bare name **and** every profile it can name. Unrecognised profile constants add
nothing rather than inventing vocabulary.

⚠ The type is `MediaStreamCodec`, not `MediaCodec`, because **`Android.Media.MediaCodec` is the platform
SDK's own type** — the shorter name collided on the one platform that most needs this to work.

🔴 **`MissionExecution` and `MissionRecord` now carry the caller's own `Key`** (defaulted, appended —
source-compatible, but a positional-record change so **recompile**).

**Why nothing an app received could be recognised.** `MissionKey` is documented as the caller-chosen
identity and `IsActive(MissionKey)` treats it as *the* handle, but it was dropped at construction — so a
mission body, an `IMissionObserver` callback, an `IMissionPolicy` and every `Snapshot()` row carried only
`MissionId`, which is the scheduler's and is per-process. An app could not build a `missionId → my item`
map at all. The one real consumer proves it: the sample publisher emits `missionId` + `kind`, and the
guide tells the page to fold by an opaque `m7` the app never saw.

The only workaround was encoding instance identity into `Kind`, which is documented as a *type*.

**`MissionRecord` matters more**, because the kit ships no store (D28) — that record is the wire format
between the kit and every adopter's storage, and `MissionId` does not survive a restart. A `rehydrate`
callback can now key on what the app itself chose.

**Two placement corrections — namespace only, every type keeps its name.**

| was | is | why |
|---|---|---|
| `Shenora.Engine.Files.IFileLockInspector` | `Shenora.Core.Shell` | a SHELL implements it |
| `Shenora.Engine.Files.FileLockHolder` | `Shenora.Core.Shell` | travels with the contract |
| `Shenora.Engine.Missions.RetryPolicy` | `Shenora.Engine` | both engines use it; neither owns it |

**`IFileLockInspector`** is implemented by `Shenora.Windows`, and the rule is stated three times — in
`generic-library.md` (*"If a SHELL implements it, the contract lives in Core — full stop"*), in D48
(*"SPLIT BACK OUT because a shell must be able to implement a Core contract without reaching outward for
it"*), and in the file's own header. The tree disagreed: the shell opened with
`using Shenora.Engine.Files;`, exactly the edge D48 says was designed out. It now opens with
`using Shenora.Core.Shell;`.

**`RetryPolicy`** made `Engine.Files` depend on `Engine.Missions` for a type that names no mission
vocabulary and that both engines apply with the same loop — against a design whose stated point (D30) is
that the two compose and *"neither knows about the other"*. An app using only the file queue had to write
`using Shenora.Engine.Missions;` to name its retry policy.

⚠ Inside the kit, nothing else needed a `using` change: `Engine.Files` and `Engine.Missions` are children
of `Engine`, so `RetryPolicy` resolves for both without one.

🔴 **`MediaStreamPlan`'s two booleans become one `MediaStreamVerdict`.**
`MediaStreamPlan(stream, DecodesNatively, NeedsReEncode)` →
`MediaStreamPlan(stream, MediaStreamVerdict)`, with `Plays` as a convenience for "does not force a
transcode".

**Why two bools were lossy.** The planner set `DecodesNatively: true` for three *different* reasons — a
codec the policy lists, a codec that is UNNAMED and was given the benefit of the doubt, and a subtitle
recorded but never counted — and its own doc admitted the conflation. A consumer could not tell a
certainty from a guess, which matters: `Assumed` means the planner does not know, and an app that must
not guess can now refuse.

`NeedsReEncode` was simply the negation, so the pair encoded three states in four bit patterns while the
planner knew four.

⚠ **No `Dropped` member**, though the audit suggested one: the planner never drops an individual stream
today — an unencodable stream makes the whole FILE `Unsupported`. Adding an enum member later is
additive; shipping one nothing can produce would be a value the kit ignores.

🔴 **`WindowState.Maximized` (a `bool`) becomes `WindowState.Placement` (a `WindowPlacement` enum)**,
and `IAppMaximizable.IsAppMaximized` becomes `AppPlacement`. `OptimizedForm.IsAppMaximized` goes with it.

```csharp
if (form.AppPlacement == WindowPlacement.Maximized) { … }
```

**Why a bool could not stay.** The third state is real — a media or streaming window wants FULL SCREEN
(the whole monitor, no work-area inset), which is not the same thing as maximized. `OptimizedForm`
already has most of the machinery, and today an app doing it by hand gets its state **persisted as
"maximized" and restored wrong**. `WindowPlacement` ships with `Normal` and `Maximized` only, because
adding an enum member later is ADDITIVE — widening a `bool` is not.

⚠ **`WindowState` IS the on-disk format.** State saved by 0.10.0 carries a `maximized` boolean the new
record does not read, so such a window opens **windowed once** and is correct from the next save on.
Geometry is unaffected and nothing throws.

**`WindowStateManager.ToPhysical` (both overloads) and `ToLogical` are now `internal`.** They returned a
naked 5-tuple whose arity is part of every caller's destructuring, and they had no consumer outside the
assembly — leaving them public would have made this change break callers twice. `IsVisible` stays public;
"can the user reach this rect?" is a question an app legitimately asks.

**`UseHeadless` takes a configure callback like every other `Use…`.**
`UseHeadless(HeadlessRunnerOptions?)` → `UseHeadless(Action<HeadlessRunnerOptions>?)`, and
`HeadlessRunnerOptions`' three properties become `{ get; set; }`.

```csharp
builder.UseHeadless(x => { x.StopToken = token; x.StopOnProcessSignals = false; });
```

**Why consistency is worth a break here.** `UseMissions`, `UseFileSystem`, `UseRequests` and
`UseMediaPlayer` all take `Action<TOptions>` over mutable options; `UseHeadless` was the only one taking
a built object. The composition surface is the first thing an adopter learns, so one member disagreeing
with four costs more than it looks. And it could not be fixed later: `init` compiles to a `set_X` with an
`IsExternalInit` modreq, so removing it is a **binary** break — free now, a deprecation cycle after 1.0.

**`IWebViewResourceProvider` gains `BeginWarmup()`** as a **default interface member** — existing
implementations need no change.

**Why it had to be on the contract.** The kit's own startup call reached past the interface:
`(GetRequiredService<IWebViewResourceProvider>() as EmbeddedResourceProvider)?.BeginWarmup()`, with no
`else`. So an app that registered its own provider — the reason the interface is public at all — got **no
warmup and no diagnostic**. That is the same shape as the `dispatcher is MessageDispatcher` defect this
repo already recorded, which "silently dropped three whole modules". A default body means a provider with
nothing to warm still writes nothing, and an added interface member is impossible after 1.0.

**`SessionController`'s four taps return `IDisposable`.** `OnMessage`, `OnDownload`, `OnNewWindow` and
`OnNavigation` were `void`, so a listener could be added and never removed. Source-compatible —
`controller.OnMessage(h);` still compiles — but **binary**-breaking, so recompile.

`StreamingSession.Controller` is public and lives for the whole session, so a viewer that observes
navigations for a while had no supported way to stop and the handler list only grew. `RenderSession`'s
equivalent taps already returned `IDisposable`; this makes the two halves of one package agree.

⚠ **Not unit-tested, and worth saying rather than implying.** `SessionController`'s constructor
subscribes to `CoreWebView2` events, so it cannot be built without a live browser — the change is
compile-enforced and mirrors `RenderSession`'s existing pattern, whose unsubscribe is not directly
covered either.

🔴 **`IMediaPlayer.Rate` is now `SetRateAsync(rate, ct)`.** Read the current value from
`Status.Rate`, which is where it always actually lived.

**Why a setter could not stay.** Every other operation on the interface is `Task …Async(…, ct)`. The
platform call behind a rate change can fail, can take time, and can be cancelled — a property setter
expresses none of those, and the IPC route had to fabricate the await
(`Drive(p => { p.Rate = rate; return Task.CompletedTask; })`). `MediaPlayerBase` pushed the same shape
down to every shell. Turning it into a method later would break the interface, the abstract base, four
shell players and every caller at once.

⚠ An out-of-range rate still throws **synchronously** rather than returning a faulted task: it is a
caller bug, not a platform outcome, so it should surface without an await.

🔴 **`SessionFrame` no longer names one encoding.** `SessionFrame(byte[] Jpeg, int Width, int Height)`
→ `SessionFrame(byte[] Bytes, SessionFrameFormat Format, int Width, int Height)`, and
`StreamingSessionOptions.JpegQuality` → `FrameFormat` + `FrameQuality`.

**Why it could not wait.** The screencast offers JPEG *and* PNG; the kit picked one and wrote it into a
member name. Two of the four use cases that justified naming the type `StreamingSession` in the first
place — visual capture and a preview pane — are exactly the ones that want lossless. And because
`SessionFrame` is a positional record, adding `Format` later changes its arity and `Deconstruct`, so
every consumer breaks then instead of now.

**The format travels WITH the frame**, not only in the options, because a consumer pumping frames to a
transport needs to label the payload. The desktop sample shows why: its viewer builds
`data:image/${format};base64,…` — hardcoding `image/jpeg` there would silently render a PNG wrong.

🔴 **`IShellLauncher.LaunchProcess` takes `ProcessLaunchOptions` and returns the process id.**
`void LaunchProcess(string, string?, string?)` → `int? LaunchProcess(ProcessLaunchOptions)`.

```csharp
launcher.LaunchProcess(new ProcessLaunchOptions { ExecutablePath = exe, Arguments = args });
```

**Why now.** Every plausible next requirement was a signature change: an elevation verb — and this kit
ships an **updater**, the classic elevation need — environment variables, an argument *list* instead of a
hand-quoted string, a window style. On a record each of those is an added property, which is additive.
And `void` foreclosed the **process id**, which a supervising app needs to wait on or kill what it
launched; `void` → `int` is a binary break even though it is source-compatible.

`null` is returned when the shell satisfied the request without starting a process (it handed the work to
an already-running instance). ⚠ It is not a failure — a launch that did not happen still throws.

🔴 **`SingleInstanceGuard.TryAcquire` returns `SingleInstanceResult`, not `bool`**, and
`ActivateMessageId` is `uint?`.

`if (guard.TryAcquire())` → `if (guard.TryAcquire() is not SingleInstanceResult.AlreadyRunning)`, which
is the historical behaviour: only `AlreadyRunning` stops a launch.

**Why the bool was lossy.** There are THREE outcomes and it could express two. The guard **fails open**
— if the OS refuses to answer it returns "carry on" — so "I own this scope" and "nobody could tell me"
were the same `true`. An app whose reason for being single-instance is a single-writer database or a
profile lock may want to refuse, warn, or open read-only when the guard is `Unverified`, and it had no
way to ask. Apps with nothing at stake treat `Acquired` and `Unverified` alike and are unaffected.

`ActivateMessageId` had the same problem one member over: `0` meant BOTH "`TryAcquire` has not run yet"
and "`RegisterWindowMessage` failed", which the property's own docs admitted while the compensating
warning lived in an `internal` runner an adopter never sees. It is now `uint?` — `null` is "no channel".
The failure is real, not theoretical: the session's global atom table can be exhausted, measured on this
dev machine.

🔴 **`OptimizedForm.WndProcHook` now receives the whole `Message` and answers with a result.**
`Func<int, bool>` → `Func<Message, IntPtr?>`. Return `null` to let the message fall through; return a
value to mark it handled, and that value becomes `Message.Result` (`IntPtr.Zero` = "handled, nothing to
report").

**Why the old shape could not do the job.** It received the message ID and nothing else — no `WParam`,
no `LParam`, no way to set a result. That rules out every real reason to hook a window procedure:
`WM_COPYDATA`, `WM_POWERBROADCAST`, `WM_DEVICECHANGE`, `WM_SETTINGCHANGE`, `WM_ENDSESSION`, any
`RegisterWindowMessage` channel carrying a payload. It was an artefact of a closure, not a design: `m`
is a `ref` parameter and cannot be captured by the guard's lambda (CS1628), so only the `int` was
copied out. The hook now answers with a value instead of assigning `m.Result`, because it is handed a
COPY — a write to that copy would be discarded silently, which is the worst shape available.

🔴 **`WebViewHostOptions`' three event-policy hooks now RETURN whether they handled the event.**
`OnDownloadStarting`, `OnPermissionRequested` and `OnProcessFailed` change from
`Action<TArgs>` to `Func<TArgs, bool>` — return `true` for "handled, do not apply the built-in
policy", `false` to fall through to it. A throw still counts as not-handled and is logged.

Migration is one `return` per hook: `x => { … }` becomes `x => { …; return true; }`.

**Why this could not stay as it was.** The kit inferred "handled" from "did not throw", so an app had
no way to observe an event and still get the built-in policy — the only spelling for that was to
**throw**. It was worst on `OnProcessFailed`, where the early return sits above both the diagnostic log
and the whole auto-reload block: merely attaching crash telemetry **silently disabled
`ReloadOnRenderProcessFailure`, `AutoReloadCooldown` and `MaxAutoReloads`**, three options that stayed
set and did nothing. If that was you, the fix is `return false;`.

⚠ **Not covered by a unit test, and said so rather than implied.** The hooks are wired inside
`WireEventPolicies`, which needs a live `CoreWebView2`; the signature change itself is compile-enforced
and the semantics are proven only by the sample e2e.

**`WinFormsUiDispatcher` and `MobileUiDispatcher` now derive from `UiDispatcherBase`.** Source-compatible
— every member is still there and still called the same way — but the inherited ones now live on the base,
so code compiled against 0.10.0 must be **recompiled** rather than dropped in. Nothing to change in your
source. (Subclassing either is not affected: both were and remain `sealed`.)

🔴 **The conversion seam is one path for every stream kind, and two of its members changed on you.**

`MediaConversionOptions.AudioConversion` → **`Conversion`**, now typed `IMediaStreamConversion`.

**`MediaStreamInfo` gained `Width`, `Height` and `FrameRate`** (optional, at the end) — what configures a
platform video codec, exactly as rate and channels configure an audio one. Positional construction with
the first five arguments is unaffected; anything constructing it with ALL arguments positionally now
needs the new ones, or named arguments.

⚠ **The rest of that reshape is NOT a break and has moved to `### Added`.** `IMediaAudioConversion`,
`IMediaAudioConversionRun`, `MediaAudioPipeline`, `MediaAudioMiddleware` and `IMediaContainerWriter` were
all introduced AFTER v0.10.0 and renamed before shipping, so nobody can migrate from them — checked the
way this file's own header says to (`git grep <name> v0.10.0 -- src/` finds nothing). Listing development
churn here is how a real break gets lost in the noise.

🔴 **The package set, the namespaces and the entry points all moved. This is the whole migration:**

```diff
- <PackageReference Include="Shenora.Core"  Version="…" />
- <PackageReference Include="Shenora.Ipc"   Version="…" />
- <PackageReference Include="Shenora.Media" Version="…" />
- <PackageReference Include="Shenora.IO"    Version="…" />
- <PackageReference Include="Shenora.IO.Compression" Version="…" />
+ <PackageReference Include="Shenora"       Version="…" />
```

| namespace was | namespace is |
|---|---|
| `Shenora.Core` | `Shenora` |
| `Shenora.Ipc` | `Shenora.Core.Ipc` |
| `Shenora.Media` | `Shenora.Modules.Media` |
| `Shenora.IO` | `Shenora.Engine.Files` |
| `Shenora.IO.Compression` (the updater half) | `Shenora.Engine.Update` |
| `Shenora.IO.Compression` (`ZipExtraction`, `ResourcePack`) | `Shenora.Engine.Compression` |
| (missions, previously flat in `Shenora.Core`) | `Shenora.Engine.Missions` |

⚠ **`Shenora.IO.Compression` splits in two, and `ZipUpdateSource` goes with the UPDATER**, not with the
other zip types — it is an `IUpdateSource` first and a zip reader second. Everything else keeps its name;
only the namespace on the `using` changes.

**Why `Engine` and not `Modules`.** `Modules/` means a capability carried to the PAGE (D65) — every other
member of it has an IPC surface and most have a platform half. The updater has neither: no
`ModuleBase`, no route, nothing in any shell, and `UpdateStage` states outright that it is *"portable —
no native code and nothing platform-specific"*. It sits beside `Engine/Files`, which solves the adjacent
problem. Extraction moved out of the updater's folder for the same reason one level down: `ZipExtraction`
and `ResourcePack` name no update, and a font set is not part of the self-updater.

- **The package set is `Shenora` + ONE shell (`Shenora.Windows` / `.Android` / `.iOS`) + `@shenora/react`**,
  plus the native `Shenora.Launcher` and the build-time `@shenora/cli`. **There is no optional-feature tier
  at all** (D53/D55): a capability gets a folder, never a package id. The reason is identity rather than
  size — a nuget.org listing of single-domain libraries makes a claim about the product nobody meant.
- **The layers are the folder structure and the namespaces** (D65): `Core/` is the CONTRACT (Ipc · Events ·
  WebView · Shell), `Engine/` is the BRAIN (Missions · Files), `Modules/` BRIDGE .NET to the web (Media ·
  FileDialog · Platform · Requests · Update).
- Each fold was proven a PURE move against the API baselines — symbols accounted for in both directions,
  none added, changed or lost.

- 🔴 **The pipeline phase moves onto the built application (D64).** `app.UseFiles(…)`,
  `app.UseMediaPlayer()`, `app.MapModule<T>()` and `app.Use(…)`:
  ```csharp
  using var app = builder.Build();
  app.UseFiles(new WebViewFileOptions { … });   // order matters, like app.UseAuthentication()
  app.UseMediaPlayer();
  app.Run();
  ```
  - 🔴 **A real change in meaning, adopted deliberately: a step describes the pipeline for EVERY webview
    the app hosts.** Secondary windows and auxiliary session browsers previously got nothing unless the app
    wired each one by hand — invisible, because a window serving no routes looks exactly like a window
    whose routes were never needed. Per-interceptor overloads stay for one webview that must differ.
  - **`WebViewPipeline` FREEZES on first use by a webview**, and throws with a message naming the fix if a
    step is declared afterwards. A window opened later still gets every step.

- 🔴 **The FACADE vocabulary is gone: a module's root type is `XxxModule` (D65).** The rename most likely
  to hit you, because `BaseFacade` is what an adopter's own IPC modules derived from.

  | was | is |
  |---|---|
  | `BaseFacade` | `ModuleBase` |
  | `IModuleFacade` | `IIpcModule` |
  | `services.AddModuleFacade<T>()` | `services.AddIpcModule<T>()` |
  | `FileDialogFacade` | `FileDialogModule` |
  | `DropZoneFacade` | `DropZoneModule` (`Shenora.Windows`) |
  | `WindowCommandFacade` | `WindowCommandModule` (`Shenora.Windows`) |

  **Migration is a rename and nothing else** — no member, signature or behaviour changed.

- 🔴 **"Operations" is MERGED INTO `IpcRequest` and no longer exists (D66).** Not a rename: the entity was
  duplicating the request that caused it, and every consumer paid for the join.
  - **Deleted:** `OperationOptions`, `IOperation`, `IOperationRegistry`, `OperationRegistry`,
    `OperationEvents`, `OperationsModule`, `AddShenoraOperations`, `IModuleContext.Start`/`Run`, the
    `Waiting` status and everything around it (`WaitReason`, `Wait`/`Resume`, `Dismiss`, `RequestResume`,
    `RequestWait`, `Find`, the `WAIT`/`RESUME`/`DISMISS` routes and their events).
  - **What replaces them** is the genuinely-new part — the LIVE STATE of a request: `IpcRequestState`,
    `IpcRequestStatus`, `IpcProgress`, `IpcLabel`, `IIpcRequestTracker`, `IpcRequestsModule`,
    `AddShenoraRequests`. Wire: `SHENORA.REQUESTS`, `REQUEST_UPDATED`/`REQUEST_REMOVED`, and `CANCEL`
    carries a **`requestId`** — the id the page already had when it sent the request.
  - **`IModuleContext` is now PER REQUEST** and gains `RequestId` + `Report(progress, detail)`. A route
    reports progress with no id, because there is only one.
  - 🔴 **THE GRACE PERIOD replaces the declaration.** Every request is tracked automatically and nothing is
    emitted unless one outlives `IpcRequestTrackerOptions.GracePeriod` (50 ms). A request that finishes
    inside the window leaves no event, no history entry and no wire traffic — which is what makes tracking
    everything affordable. ⚠ It never delays the RESPONSE; it suppresses notifications only.
  - **Migrate:** delete the `OperationOptions` and the `Run(...)` wrapper; `await` the work in the route and
    call `context.Report(...)`. Observe the route's own `cancellationToken` — `CANCEL` now targets it.
    Client: `useShenoraOperations` → `useShenoraRequests`, `createOperationsStore` → `createRequestsStore`,
    `status` → `state`, `kind` → `type`; `title` and `cancellable` are gone.
  - **`ModuleBase`'s third constructor parameter (`IIpcRequestTracker? requests`) is GONE** —
    `base(logger, events, requests)` becomes `base(logger, events)`. A module that used it GAINS tracking,
    because the dispatcher now does the work.
  - **A CORE module is CONFIGURED by the application's setup, never added to it.** The registration is
    `internal`; the app-facing surface is **`builder.UseRequests(x => …)`**, beside
    `UseMissions`/`UseFileSystem`/`UseMediaPlayer`.

- 🔴 **`OperationException` → `ShenoraException`, `OperationError` → `ShenoraError`.** The framework's ONE
  structured error — a code plus interpolation parameters, the only exception whose details cross the
  bridge. It never had anything to do with the "operation" concept; it inherited the word from the
  subsystem it was written beside.
  ```diff
  - throw new OperationException("IMPORT_FAILED", "file", name);
  + throw new ShenoraException("IMPORT_FAILED", "file", name);
  - catch (err) { if (err instanceof OperationError) show(t(`errors.${err.code}`)); }
  + catch (err) { if (err instanceof ShenoraError) show(t(`errors.${err.code}`)); }
  ```
  ⚠ **`error.name` changes too**, so client code matching on the name STRING rather than `instanceof` needs
  updating. Matching on `code` was always the intended path and is unaffected.

- 🔴 **EVERY kit module moved onto a reserved `SHENORA.` prefix.** A WIRE break: both halves must move
  together or they fail with `UNKNOWN_MODULE` while compiling perfectly on each side.

  | was | now |
  |---|---|
  | `FILE_DIALOGS` | `SHENORA.DIALOGS` |
  | `OPERATIONS` | `SHENORA.REQUESTS` |
  | `WINDOW` | `SHENORA.WINDOW` |
  | `DROP_ZONE` | `SHENORA.DROPZONE` |
  | `MEDIA` | `SHENORA.MEDIA` |

  **Migration:** update any hard-coded module string. If you use `@shenora/react`'s clients you change
  nothing — they carry the names. The handshake's bare `SHENORA` is unchanged.
  **The point is what it gives BACK: those plain names are now yours.** A reserved prefix cannot collide
  with an app's, which is also what makes registering the kit's own modules by default safe (D64).

- **`NO_HANDLER` split in two.** It still means nothing claimed the MODULE name; **`NO_ROUTE` (new) means
  the module answered and has no such type.** The two need OPPOSITE fixes — wire the module up, versus
  correct a route name — and they were indistinguishable on the wire. A client branching on `NO_HANDLER`
  for a bad route name should read `NO_ROUTE`; `IpcErrorCodes.noRoute` is exported from `@shenora/react`.

- **The shell entry points are named for the PLATFORM (D65).** `UseWinForms()` → **`UseWindows()`**,
  `UseMobile()` → **`UseAndroid()` / `UseIOS()`**; `WinFormsHostOptions` → `WindowsHostOptions`,
  `WinFormsHostExtensions` → `WindowsHostExtensions`. D37 made the package set one-per-platform in 0.5.0
  and the calls never followed. ⚠ A multi-targeted MAUI app now writes an `#if`; a single-platform app
  writes one line and never notices.

- **`AddMessageDispatcher` → `UseMessageDispatcher`.** One rename; no receiver moved and no signature
  changed. The rule: **`Use` means a wider configuration INCLUDING its pipeline; `Add` is the
  service-collection level only.** ⚠ `AddIpcModule<T>`, `AddShenoraFileDialogs` and `AddShenoraRequests`
  keep `Add` and are unchanged — each is plain DI registration.

- 🔴 **`MobileWebViewInterceptor` now takes the app's `WebViewPipeline` as a REQUIRED second constructor
  argument.** Migration: `new MobileWebViewInterceptor(webView, app.Pipeline, log)`. Pass a fresh
  `new WebViewPipeline()` for a webview that must deliberately serve nothing.
  ⚠ **No gate catches this one** — the mobile API baselines are NAME-level and the mobile samples do not
  build on a Windows host, so this entry is the only warning an adopter gets.

- **`MissionScheduler` now implements `IDisposable` as well as `IAsyncDisposable`.** Additive for callers,
  listed because it changes what `using var app = …` does. See `### Fixed` — it was a crash.

- 🔴 **Media containment is stated once: `MediaAccessOptions`.** `MediaConversionOptions` no longer carries
  its own `AllowedRoots`, `CacheRoot`, `Resolve`, `Module` or `Log` — it takes an `Access` object instead.
  **Migration:** move those five into `Access = new MediaAccessOptions { … }`. The compiler names every site.
  ⚠ **Only `MediaConversionOptions` is a migration** — `SegmentStreamOptions` and `MediaPlayerOptions` are
  new in this release and were never shaped any other way.
  **Why:** `AllowedRoots` is a security boundary, and it was heading for three separate declarations that
  could drift.
  ⚠ **`MediaPlayerOptions.Access` is the one place this is `{ get; set; }` rather than `{ get; init; }`** —
  unlike the other two types, it is configured through `UseMediaPlayer((options, services) => …)`'s callback
  AFTER construction, and an `init` accessor cannot be assigned outside an object initializer. It also stays
  free to swap the whole object once `CacheRoot` needs the `paths.DataArea("media")` default `UseMediaPlayer`
  has always applied when none was named. `MediaAccessOptions.Resolve` is `required` even here, though this
  particular `Access` never calls it — `MediaPlayerOptions` resolves its route through `MediaPlayerRoute`
  instead, so `static _ => null` is the correct value to give it.

🔴 **`IClipboardService` carries a whole clipboard ITEM, not one format at a time.**
`SetImageFromFileAsync(string)` and `TrySaveImageToFileAsync(string)` are GONE; `GetAsync`, `SetAsync`
and `ClearAsync` take a new `ClipboardContent` — `Text`, `Files`, and a `Formats` map keyed by media
type. `SetTextAsync`/`GetTextAsync` are unchanged and are now documented shorthands for the same
operation.

```csharp
await clipboard.SetAsync(new ClipboardContent {
    Text    = "1,2,3",
    Formats = new Dictionary<string, ReadOnlyMemory<byte>> {
        [ClipboardContent.Html]     = Encoding.UTF8.GetBytes("<table>…</table>"),
        [ClipboardContent.PngImage] = chart,
        ["application/x-myapp-cells"] = mine,   // your OWN representation, carried verbatim
    },
});
```

🔴 **Why one format at a time was WRONG, not merely limited.** A clipboard holds one item offering
several representations, so every platform's set REPLACES the lot — calling the text setter and then
the image setter left the image and silently discarded the text. "Copy this as text AND as a picture",
which is what an ordinary application's Copy does, could not be expressed and the attempt failed with
no error. A test now sets text + HTML + PNG in one call and reads all three back.

**The `Formats` map is open on purpose.** `Text` and `Files` are named because every platform has a
first-class API for them; everything else is keyed by media type so an app can carry its own
representation — its document model, a structure a paste round-trips losslessly — without the kit
becoming the registrar of every format anyone wants. Same reasoning as `ShellCapability`'s capability
strings, and the same shape as the web's own `ClipboardItem`.

⚠ **Two defects went with the old shape.** `TrySaveImageToFileAsync` hardcoded `ImageFormat.Png`, so
`TrySaveImageToFileAsync("shot.jpg")` wrote **PNG bytes into a `.jpg`**; and both image members were
file-shaped, so an app holding bytes had to round-trip a temp file to copy one, and write-then-read one
to paste. Reading a picture now returns the bytes that were actually put there rather than a re-encode,
which is what preserves an alpha channel — a transparent screenshot used to come back on a black
background.

**Windows translates; it does not just store.** A PNG filed under `"image/png"` is invisible to
Explorer, Word and every browser, so the shell writes the `PNG` format *and* a `CF_BITMAP` for older
readers, and wraps HTML in `CF_HTML` with its byte-offset header — get that header wrong and the paste
silently truncates. An unrecognised media type is stored verbatim, because a private format is only
ever read back by the app that wrote it.

**Mobile now uses the PLATFORM pasteboard, not Essentials.** iOS carries text, HTML, PNG and arbitrary
UTIs through `UIPasteboard`; Android carries text and HTML through `ClipData.NewHtmlText`. ⚠ Android
refuses other byte formats — every one travels as a `content://` URI needing a `ContentProvider` the
**app** declares — and **both refuse `Files`**, which is a desktop idea no pasteboard expresses. Each
refusal names what was asked for and why (D33).
⚠ **The mobile paths are NOT device-verified** — they compile for both TFMs and the contract is proven
on the desktop shell; a device run is open in `TASKS.md`.

🔴 **Every diagnostic sink is an `ILogger`, not an `Action<string>`** — all 16 `Log` properties across
the kit and both mobile shells, plus `MediaPlayerBase`'s constructor, `SegmentEngine.Default`,
`UseSegmentStream`, `BoundedBodyStream`, and the three Windows shell types
(`WindowsMediaPlayer`, `WindowsMediaCapability`, `WindowsPlaybackSession`). `AppCallback.Log` takes one
now too, and gains a `level` and an `exception`. ⚠ `MediaPlayerBase`'s `protected Log` gains an optional
`Exception?` — source-compatible for a subclass, but recompile.

**Migration is one call if your sink is a delegate:** `Log = Console.WriteLine` →
`Log = AppCallback.Logger(Console.WriteLine)`. An app with real logging infrastructure passes its own
logger instead and gets what the adapter cannot give it — filtering, categories, structured fields.

**Why a delegate could not stay.** `Action<string>` carries a string and nothing else: no level, no
event id, no structured fields, and **no exception object**. So a kit diagnostic reporting a caught
failure could only interpolate `ex.GetType().Name` into a line, throwing away the type, the stack and the
inner chain — the identity a diagnostic exists to preserve. It also split an app's diagnostics between
two mechanisms: three types already took `ILogger`, `MessageDispatcher` resolved `ILogger<T>` from DI,
and everything else did not — so `UseLogging()` configured a pipeline most of the kit could not write to.

**All 58 of those sites now pass the exception itself** — the whole reason for the change, and it is
what your sink receives instead of a type name flattened into a string.

⚠ **`ILogger` is a new name on the public surface but NOT a new dependency** —
`Microsoft.Extensions.Logging.Abstractions` was already referenced and already on the surface via
`UseErrorHandler(ILogger)`.

🔴 **YOUR MINIMUM LEVEL NOW APPLIES, AND ORDINARY TRACING IS `Debug`.** A delegate got every line
unconditionally — there was no level to filter on. A real logger has one, and the default host builder
filters at `Information`, so an app that swaps `Log = Console.WriteLine` for its own `ILogger` sees the
kit's ordinary tracing only once it enables `Debug` for the `Shenora.*` categories. Nothing fails; those
lines simply stop. `AppCallback.Logger(…)` reports every level enabled, so a delegate sink behaves as it
did.

⚠ **A swallowed FAILURE is `Warning`, so it survives that default filter** — that is the level the kit
picks whenever a diagnostic carries an exception, which is what "something unexpected happened and we
carried on" means. The rule lives in `AppCallback.Log` alone (pass an explicit `level` to override), so
no per-type helper restates it.

**And the kit's own tag moves where it belongs.** The three shell players used to wrap the delegate to
prefix `[Shenora.Windows]`/`[Shenora.Android]`/`[Shenora.iOS]`; the shells now pass
`ILogger<WindowsMediaPlayer>` straight through, so the CATEGORY carries the origin and a structured sink
sees a real one instead of a string prefix.

🔴 **EVERY session observation tap is GONE** — `SessionController.OnMessage`/`OnDownload`/`OnNewWindow`/
`OnNavigation`, and `RenderSession.OnNetwork`/`OnMessage` with the `SessionApiCall` record. What the
browser does is now published on the app's `IEventBus` as `SessionEvents`, scoped by the session's id:

```csharp
// before
controller.OnNavigation(url => …);
// after — and the catalogue is far wider than four
bus.Subscribe(SessionEvents.Module, SessionEvents.NavigationStarting, session.Id, m => …);
```

**Why they could not stay.** They were a second subscription idiom sitting next to the kit's own bus,
which already has scope matching, wildcards and guarded handlers — and the kit should have ONE answer to
"how do I observe this", exactly as it has one answer to "how do I undo a registration".

⚠ **`RenderSession.OnNetwork` lost nothing in the move**, which was worth checking rather than assuming:
its body sample is carried by `SessionResponse.BodySample` (ask for one with
`SessionBrowserOptions.ResponseBodySample`), and the read is still an asynchronous best-effort — an
`EventMessage.Payload` is `object?`, so there was never anything a callback could carry that an event
could not. `OnMessage`'s `WebMessageAsJson` fallback moved too: a page posting an object rather than a
string is reported, not dropped.

⚠ Removing `SessionApiCall` left `Api` and `Call` in `surface-lexicon.txt` with nothing to justify them,
and the vocabulary gate said so in both directions; both are gone. `Navigation` was added for
`SessionNavigationResult` — the browser's own word, already first-class in this surface as
`NavigationGuard`/`NavigationTimeout`/`NavigateAsync` and only now needing a type.

**On a POOLED session the scope also replaced a guard.** `OnNetwork`/`OnMessage` threw
`ObjectDisposedException` after the lease returned, because a tap installed late would have streamed the
next tenant's traffic to the previous caller. That cannot arise now — the recycled browser publishes
under a new `Id`, so a subscription outliving its lease goes deaf instead of being re-pointed.

🔴 **One of them was actively harmful.** `SessionController` set `e.Handled = true` on
`NewWindowRequested` unconditionally, on top of `SessionBrowser`'s own popup policy — and being wired
second, it ran second, so allowing a popup could not survive it. An app setting the new
`OnWindowRequest` hook to ALLOW was therefore silently overruled on `InteractiveSession`, the one session
type a human is looking at. The hook is now the single owner of that decision.

### Added

- **`useDropZone` takes an `onError`, and `IpcRequestRoutes` is exported.** Drop-zone failures could
  only ever reach the console — the last error path in `@shenora/react` with no app sink, beside
  `bridge.ts`'s `onPostError`, `store.ts`'s `onError` and `segmentBinder.ts`'s `onDiagnostic` — and it is
  the one whose failure is INVISIBLE: the page renders correctly and files simply do not drop.
  `IpcRequestRoutes` was the route half of a wire whose module name and event names were already
  exported, so cancelling a request without `useShenoraRequests` meant hard-coding `'CANCEL'`.

- **The window-chrome and drop-zone wire vocabularies are CONSTANTS now, and the mirror test reads
  them** — `WindowCommandModule.{Minimize,ToggleMaximize,Close,IsMaximized,StartDrag,StartResize,
  SetTheme,SetCaptionButtons}Type`, `DropZoneModule.{Register,Update,Unregister,Show}Type` and
  `DropZoneManager.{DragEnter,DragLeave,FileDrop}Event`. Both modules switched on bare string literals,
  so the two halves of those wires were the only ones with nothing comparing them — and their failure
  is the silent kind: a frameless window whose `START_DRAG` drifted still renders perfectly and simply
  stops dragging. `WireMirrorTests` now pins window commands, drop-zone routes AND events, the media
  player's command set, and the four IPC ENVELOPES themselves. A page that names routes directly can
  use the constants instead of literals.

- 🔴 **A session browser now REPORTS what it does — `SessionEvents`, published on the app's
  `IEventBus`.** Ten event types where there were four taps: `RESPONSE_RECEIVED`,
  `NAVIGATION_STARTING`, `NAVIGATION_COMPLETED`, `DOM_CONTENT_LOADED`, `SOURCE_CHANGED`,
  `TITLE_CHANGED`, `WEB_MESSAGE`, `DOWNLOAD_STARTING`, `WINDOW_CLOSE_REQUESTED`, `PROCESS_FAILED`.
  Set `SessionBrowserOptions.Events` to turn them on; with no bus nothing is wired and nothing is paid.

  ```csharp
  using var _ = bus.SubscribeToModule(SessionEvents.Module, session.Id, m => { … });
  ```

  **Each event answers a need the surface could not.** A redirect-driven load was invisible unless you
  awaited your own `NavigateAsync`; an SPA route change fires no navigation at all; a driver waiting for
  "the document exists" had to poll with a script; and a dead renderer was an options callback nobody
  else could see.

  🔴 **`RESPONSE_RECEIVED` is the honest primitive behind "tell me when a cookie changes."** Measured
  against the SDK: `CoreWebView2CookieManager` raises NO events, so there is nothing to forward. A
  response carries `Set-Cookie` as it happens along with the 302 that usually accompanies it — one
  mechanism serving cookie capture, redirect tracing and API observation, none of them named for a
  scenario. ⚠ It does **not** see a cookie set by JS (`document.cookie`); read the jar with
  `GetCookiesAsync` for that. It is also the only event here that fires per SUBRESOURCE, so it is OFF
  until `SessionBrowserOptions.ObserveResponse` selects which URIs are worth reporting.

- **Every session type now has an identity — `RenderSession.Id`, `StreamingSession.Id`,
  `SessionController.Id`.** It is the scope its events publish under. ⚠ On a pooled session the id
  belongs to the **lease**, not to the recycled browser: the same instance is leased again under a new
  one, so a subscription that outlives its session stops receiving anything rather than quietly picking
  up the next tenant's pages.

- 🔴 **The page can reach the native clipboard — `AddShenoraClipboard()`, `SHENORA.CLIPBOARD`, and
  `useClipboard()` / `ClipboardAccess` in `@shenora/react`.** Opt-in, and deliberately NOT defaulted on
  the way the file dialogs now are. ⚠ The client class is `ClipboardAccess`, not `Clipboard`: the DOM
  already has a global by that name (the type of `navigator.clipboard`), and an import that shadows it
  would make the web's own clipboard unnameable in that file — a collision worth avoiding while the name
  has never shipped, since after 1.0 it would cost a break.

  ```tsx
  const { clipboard, canCopyFiles } = useClipboard();
  <button onClick={() => navigator.clipboard.writeText(name)}>Copy name</button>
  {canCopyFiles && <button onClick={() => clipboard.write({ text: name, files: [path] })}>Copy file</button>}
  ```

  🔴 **It is not a replacement for `navigator.clipboard`, and the routes are scoped so it cannot become
  one.** The page runs in a real browser: gesture-driven copy of text or an image already works there and
  should stay there. Two things the web cannot do are the whole justification — **files**, which no web
  API can put on a clipboard, and **access with no user gesture, focus or permission**, which
  `navigator.clipboard.read()` requires and a host does not.

  ⚠ **And the choice is per-COPY, not per-format**, because a clipboard set is atomic: one item, last
  writer wins. An item that includes files must be written entirely through the host — writing its text
  half with `navigator.clipboard` and the files here leaves only the files, silently. That is why the
  routes carry the whole item rather than the file list alone.

  🔴 **Think before opting in.** `READ` lets the page read the user's clipboard at any moment with no
  prompt — a capability the web withholds on purpose, since a clipboard routinely holds a password
  copied from somewhere else. Do not mount it for a page that renders third-party content.

  **`ShellCapability.ClipboardFiles` / `ShellCapabilities.clipboardFiles`** is the one part worth
  branching on: a phone's pasteboard has no file list, so gate the control rather than catching the
  refusal. Bytes cross as base64, so a large picture is a large message — hand the host a path instead.

- 🔴 **`SegmentEngine.Default(conversion, log)` — the kit's segment engine is now REACHABLE.** D71's
  primary path shipped with no public entry point: `UseSegmentStream` requires you to bring an
  `ISegmentEngine`, and the only implementation was `internal`. So `docs/guides/media.md` told you that you
  need "a segmenting engine" and the kit gave you no way to get one — the feature was complete and
  unusable at the same time.
  - A **factory**, not a public class, so the engine's shape stays out of the SemVer surface while the
    capability is reachable: you need an `ISegmentEngine` to mount the route, not the concrete type.
  - `conversion: null` is accepted and means the engine reports `IsAvailable = false`, so the route
    answers "not complete" rather than throwing — the honest answer on a platform with no codecs, and it
    means no platform branch to ask the question.
  - The MAUI device probe now reaches it through this same factory rather than `InternalsVisibleTo`.
    ⚠ That grant is **narrowed, not removed**: the probe still borrows `Mp4FragmentReader` and
    `SegmentRunWriter` to read back what it produced, and both stay internal.

- 🔴 **`bindSegmentStream(options)` (`@shenora/react`) — the segment route's page half, D71 piece 4b.**
  Opens a `SourceBuffer` against the host's playlist, keeps it fed, and stops when the platform says
  stop. Returns a `SegmentBinding` you `dispose()`.
  ```ts
  const binding = await bindSegmentStream({ manifest: '/shenora-hls/film.mkv/index.m3u8', element: video });
  // …later
  binding.dispose();
  ```
  Every rule in it was measured on three implementations rather than read off the spec, because they
  disagree in ways the spec permits:
  - **Attachment is not portable.** iOS takes `srcObject`; Chromium refuses a MediaSource there and
    wants an object URL. Feature-detected — `binding.attachedBy` says which was accepted.
  - **The codecs come from the init segment**, via `codecsFromInitSegment`. The track set is a fact
    about the DEVICE, not the source: the same file yields two tracks on iOS and one on Android, which
    cannot decode its AC-3.
  - **The streaming gate is honoured.** `endstreaming` fires on iOS once enough is buffered and
    fetching past it is the misuse `ManagedMediaSource` exists to detect; a plain `MediaSource` has no
    such signal, and its absence means "always streaming".
  - A `503` from the host is "still producing", not a failure — the round simply ends.

  ⚠ `options.globals` and `options.fetch` are injectable, which is what makes the imperative half
  testable at all: jsdom has no MediaSource, and a fake now drives every branch.

- 🔴 **`codecsFromInitSegment(init)` (`@shenora/react`) — read the `SourceBuffer` codecs out of the
  init segment instead of guessing them.** `segmentMimeType()`'s default names TWO tracks
  (`avc1.640028,mp4a.40.2`), and **a source with no soundtrack cannot be played through it**: measured
  against Chromium 151 on the kit's own segments, a video-only init segment appended to a buffer opened
  with that default fails the FIRST append and plays nothing, while the same bytes opened as
  `avc1.640015` play. The page already fetches the init segment (`#EXT-X-MAP`), so the codecs can come
  from the thing they describe — no host change, no manifest change.
  ```ts
  const init = new Uint8Array(await (await fetch(manifest.initUri!)).arrayBuffer());
  const codecs = codecsFromInitSegment(init);          // "avc1.640015,mp4a.40.2", or one track, or null
  const buffer = source.addSourceBuffer(segmentMimeType(codecs!));
  ```
  Null means no track could be read — treat it as "do not open a SourceBuffer", not as "use the default".
  ⚠ The TRACK SET is the part that has to be right; the same measurement showed profile and level are
  barely checked (High 2.1 content played through a buffer opened as Baseline 3.0).

- 🔴 **THE FRAMEWORK IS ON BY DEFAULT (D64).** `Build()` registers missions, the file system and the media
  player; `UseMissions`, `UseFileSystem` and `UseMediaPlayer` now only CONFIGURE them — the way
  `WebApplication.CreateBuilder` brings Kestrel without anyone calling `AddKestrel()`. An explicit call
  still wins: it registers first, and the defaults are `TryAdd`.
  - **Why it is safe:** none of these does anything until the frontend asks over IPC or requests a URL.
    They are interceptors on the kit's three fixed pipelines, so one nothing routes to is inert by
    construction. Containment (`AllowedRoots`, `WebViewFileOptions`) is unchanged and still fails closed —
    `UseMediaPlayer()` still refuses to guess a root, which is why conversion remains opt-in.
  - ⚠ **Registration touches NO disk.** Building a journal and locker at registration time would have given
    every app a `journal/` and a `locks/` folder it never asked for, because `Paths.DataArea` CREATES the
    directory it names. Both construct inside the DI factory, pinned by a test.
  - ⚠ **Nothing to migrate from 0.10.0** — none of these three entry points existed in that release. What a
    0.10.0 adopter actually renames is `AddShenoraOperations`, `UseWinForms` and `UseMobile`, each under
    `### Breaking` above.

- **`UseSegmentStream` now returns `ISegmentStreamRoute`** (still an `IDisposable`, so `using var` call
  sites are unchanged) — the handle for D71's piece 5: **a finished stream becomes ONE file.**
  `IsComplete(source)` is a checkable predicate (every part present AND non-empty), and
  `MergeAsync(source, destination)` writes `init.mp4` followed by every fragment in plan order, which is a
  valid fragmented MP4 — **a byte copy, not a second production.** The app asks in .NET and the page
  contract does not change; playback then points at the file and is `Direct`.
  - 🔴 **The destination may NOT be inside the segment cache, and the route refuses it** rather than
    documenting the hazard. The cache is swept oldest-used-first under a byte cap; a persisted artifact is
    evicted by nothing. Writing one into the other means ordinary playback silently deletes a file someone
    waited for.
  - ⚠ **Proven by unit tests over the bytes and the order, not by a player.** Whether a real player opens
    the merged file is the same device run the copied-picture path owes.

- 🔴 **The kit now ships a DEFAULT segment engine, so `UseSegmentStream` works out of the box on mobile**
  (D71 piece 3). `ISegmentEngine` had shipped as a seam with no implementation; supplying one was the app's
  job, and most apps have no way to write a fragmenting muxer. The default is composition only — the
  demuxer the kit already had for remuxing, the codecs the shell already registers for conversion, and a new
  fMP4 fragment writer — so **no engine bytes ship and no licence is inherited**. An app past the platform's
  reach still supplies its own through the same seam.
  - **Segments are fMP4 (`seg{k}.m4s` + `init.mp4`), not MPEG-TS.** `isTypeSupported('video/mp2t')`
    answered `true` on both mobile shells and that claim is not trusted — `canPlayType` produced exactly
    such a `true` for HLS the same day, and a MediaSource append failure is silent. fMP4 is what
    `MediaSource`/`ManagedMediaSource` actually consume, and it makes `HasRenderedPicture` answerable: the
    sample sizes are in the file, where MPEG-TS only ever declared the stream in its PMT.
  - The manifest is **HLS version 7 with `#EXT-X-MAP`**, which the segment format requires — an
    `#EXT-X-MAP` is illegal below version 6, so the two move together.
  - **The init segment is PRODUCED, not stored.** Its decoder configuration is knowable only once an
    encoder has emitted output, so the engine writes it beside its first fragment and the route answers
    `503 Retry-After: 1` until then — the same not-ready reply the conversion and computed-remux routes give.
    A page following `#EXT-X-MAP` must tolerate that, exactly as it does for a segment.
  - 🔴 **It COPIES every stream MP4 can carry and re-encodes only what it cannot** (D76) — which is the
    difference between the engine working and not. The platform video encoders offer h263/mpeg4/mpeg2video,
    none of which a webview decodes, so a re-encode-everything engine produced **sound-only segments for
    essentially every real film**. An H.264 or HEVC track needs no encoder at all: Matroska already stores it
    in the length-prefixed form MP4 uses, which is why `Mp4Remuxer` can copy it and a fragment can carry the
    same bytes. The common case — H.264 picture, AC-3 soundtrack — now spends ONE codec, on the sound.
  - **Where the cuts are is a `SegmentPlan`, not a fixed grid.** A copied track keeps the keyframes the
    ORIGINAL encoder chose, so `ISegmentEngine.PlanSegments` reports the boundaries it will actually produce
    and `SegmentRunRequest` carries that plan (in place of a `SegmentSeconds` number). The manifest states
    each real length in `#EXTINF` and the longest in `#EXT-X-TARGETDURATION`. **Returning null means "I will
    hit your grid"**, which is what a re-encoding engine answers, so an app-supplied engine implements one
    line. A page needs no change: `nextSegment` already walked the playlist's own durations.
  - **A whole-second grid is still required of a RE-ENCODED track** — the kit's encoders emit a keyframe every
    second, so only whole multiples land on one, and a fractional grid is refused rather than producing
    segments that play and misbehave only when seeked. Boundaries taken from a source's own keyframes are
    exempt, being real by construction.
  - ⚠ **A source whose keyframes are more than 30 s apart is re-encoded instead of copied.** A fragment is
    held whole in memory, so copying such a stream would build one buffer of hundreds of megabytes.
  - `SegmentRunRequest` carries `InitSegmentName` and `SegmentExtension`: the file names are part of the
    engine contract, so a third-party engine needs them rather than a comment describing them.
  - ⚠ **Desktop reports `IsAvailable = false`**, because `Shenora.Windows` implements no
    `IMediaStreamConversion`. That is the honest answer rather than a broken one: WebView2 serves byte
    ranges properly, so the desktop's path is the computed-remux route. (A copy-only run needs no codec, but
    an all-carriable source belongs on that route anyway.)
  - ⚠ **Proven against a fake codec, not on a device.** The pump, the copying, the cutting, the seeking and
    the fragment bytes are unit-tested end to end; whether the PLATFORM's encoders behave as the fake does —
    and whether a real `avcC` copied into a fragment satisfies a real `MediaSource` — is not yet measured on
    hardware.
  - ⚠ **`segmentMimeType()`'s default codec string is a default, not a guarantee.** A copied picture keeps the
    source's profile and level, and an HEVC source arrives as `hvc1`; the family is what an implementation
    checks, so any H.264 profile plays through the default and HEVC needs its own string.

- 🔴 **`UiDispatcherBase` — the shell-independent half of `IUiDispatcher`, implemented once.** The two
  shipped dispatchers were deliberate member-for-member mirrors, on the reasoning that *"the invariants
  are the CONTRACT, not the platform"* — which is the argument for stating them once rather than twice.
  A shell now supplies three hooks (`State`, `IsOnUiThread`, `TryPost`) and inherits everything a caller
  can observe: the guarded inline and posted paths, the load-bearing `(Action)` cast that stops
  `Post(Func<Task>)` recursing into an uncatchable `StackOverflowException`, the state-shaped failures,
  and the cancellation-observing awaits. **An app implementing `IUiDispatcher` for its own host should
  derive from this** instead of re-deriving the invariants.
  - Behaviour is unchanged on both shells, including which exception a refused post surfaces: `TryPost`
    hands the platform's own failure back, so WinForms still faults with what `BeginInvoke` threw and
    MAUI still reports `ObjectDisposedException` when `Dispatch` returns false.

- **`AppCallback.RunAsync(Func<Task>, Action<Exception>?)`** — the async form of the guarded app-callback
  helper, for the fire-and-forget UI post whose exceptions have no caller left to catch them. It keeps
  `ConfigureAwait(true)` deliberately, because every caller is already on the UI thread and the
  continuation must stay there. Both `IUiDispatcher` implementations now use it instead of each carrying
  a private `RunGuardedAsync` plus a private `Report` byte-identical to `AppCallback`'s own.

- 🔴 **ONE conversion seam for every stream kind** — `IMediaStreamConversion` / `IMediaStreamConversionRun`
  / `MediaConversionPipeline` / `MediaConversionMiddleware`, plus `IMediaContainerWriter`. A converter
  DECLINES a kind it does not handle (`source.Kind`) rather than being registered into a per-kind
  registry, so there is nothing to register into the wrong one. `Push`/`Drain` speak `MediaFrame` (bytes,
  presentation time, keyframe flag) rather than bare buffers: video needs a sync-sample table and
  composition offsets, and only the encoder knows them. Audio passes `IsKeyframe: true`, which is a value
  rather than a different shape.
  - **What this buys:** a picture the device decodes and its webview refuses — measured `mpeg4` on API 36,
    which reaches `readyState = 4` with **no error** and a 0×0 picture — is re-encoded to H.264 instead of
    being served as sound over a blank rectangle. A file whose picture cannot be produced is REFUSED, not
    quietly shipped without it.
  - ⚠ Described here rather than under `### Breaking` because an audio-only ancestor of this seam existed
    only WITHIN this unreleased window: there is no released name to migrate from. The two members that
    genuinely changed for a 0.10.0 adopter (`MediaConversionOptions.Conversion` and `MediaStreamInfo`'s
    new members) are under `### Breaking`.

- 🔴 **iOS HAS a picture converter now** — `IosMediaVideoConversion` (VideoToolbox:
  `VTDecompressionSession` → `VTCompressionSession` → H.264), registered on both mobile shells beside the
  audio converter, with both declining what they do not handle so one pipeline serves both kinds.
  - ⚠ **What it does NOT do, measured rather than assumed:** convert `mpeg4` on an iPhone 17 Pro / iOS 26.6.
    **That device has no MPEG-4 Part 2 decoder** — 47 bytes of ESDS present and `VTDecompressionSession`
    still refuses — so the track is dropped and the conversion refused, which is the correct answer and the
    one the kit gave before this converter existed. `h263` creates a session on the same device, so the
    converter itself works; the codec is the limit, not the code.
  - **So the honest summary is a SEAM, not a capability:** the kit now asks iOS the picture question instead
    of never asking, and iOS answers per codec. A device or OS that carries an `mp4v` decoder gets the
    conversion for free.
  - **The gap was measured, and the reason it stayed open was a claim contradicted by its own evidence.**
    Both `TASKS.md` and the registration site said iOS needed no picture converter because *"iOS decodes
    what its webview accepts"*. The opposite was already recorded: this device decodes `mpeg4` and **its own
    webview refuses it**, so a page got sound and a blank picture with **no error at all**.
  - ⚠ **VideoToolbox emits AVCC (length-prefixed) samples natively**, so none of the Android peer's Annex-B
    splitting applies here — the `avcC` is rebuilt from the encoder's own parameter sets, and keyframes come
    from `NotSync` INVERTED (absent means sync). ⚠ **Not yet proven on hardware** — it compiles and is
    registered; the device run is the outstanding step, and the simulator is not evidence for codecs here.

- 🔴 **`BackgroundPlaybackTransfer` — playback that survives the app going away**, by moving the playhead
  from the page's element to the platform's own player and back. The one media job a page provably cannot
  do for itself, which is what makes it the kit's (D54): measured, a page `<audio>` already playing is
  suspended after **~15.3 s** in the background while the native player ran **45 s** with no foreground
  service, and the page cannot even START audio at background time (`NotAllowedError` — user activation is
  transient and pressing HOME is not a gesture).

  ```csharp
  var transfer = new BackgroundPlaybackTransfer(
      services.GetRequiredService<IMediaPlayer>(),          // the page-backed player
      services.GetRequiredService<AndroidMediaPlayer>(),    // the shell's own, resolved BY TYPE
      new BackgroundPlaybackOptions { ResolveNativeSource = () => currentFile });

  window.Stopped += async (_, _) => await transfer.ToBackgroundAsync();
  window.Resumed += async (_, _) => await transfer.ToForegroundAsync();
  ```

  - **Your half is `ResolveNativeSource` and nothing else.** The page plays a URL your own routes serve and
    a native player cannot fetch that, so something must map it to a file the device can open — the same
    knowledge `MediaAccessOptions.Resolve` already encodes.
  - **iOS also needs `UIBackgroundModes: [audio]`** and the active `AVAudioSession` the shell's player takes.
  - 🔴 **A playback that FINISHES while you are away parks the page at the end rather than restarting it.**
    Seeking a 60 s element to 60.00 rewinds it and the follow-up `play()` runs the opening titles.
  - ⚠ **It reports what happened; it does not promise a window.** Measured to ~45 s on Android and ~43 s on
    iOS with clips that then ended — **minutes are unmeasured**, and a foreground service remains the app's
    to post.
  - Requires the page half (`useMediaPlayer(ref)` in `@shenora/react`) in BOTH directions: the transfer
    reads `IMediaPlayer.Status`, which the page's `PLAYER_REPORT` feeds, and hands back by driving the
    element with `PLAYER_SEEK`/`PLAYER_PLAY`. An app that reports but ignores commands gets a handback that
    silently moves nothing.

- 🔴 **A DEFAULT CONVERSION ENGINE, and it is the PLATFORM's own hardware.** `MediaConversionOptions` takes
  `Conversion` — the codec seam your shell already registers — and `Convert` is no longer `required`: omit
  it and the kit runs `Mp4Remuxer` joined to those decoders. **Zero shipped codec bytes**, because this is
  wiring rather than a codec, so D51 stands unamended; code that sets `Convert` behaves identically.

  ```csharp
  Conversion = services.GetService<IMediaStreamConversion>(),   // the platform's decoders
  // Convert: omitted — the kit supplies the engine.
  ```
  - ⚠ **Its reach is D59's line and no wider — what the DEVICE decodes and its WEBVIEW refuses.** Measured
    2026-08-10: an API 36 Android device decodes mp3/flac/vorbis and NOT ac3/eac3/dts/alac; an iPhone
    decodes ac3/eac3 — ask `IMediaCapability`. **Past that line the work is yours**, which is what `Convert`
    is now for. Omit `AudioConversion` as well and the default repairs CONTAINERS, needing no codecs at all.
  - 🔴 **AND A DROPPED STREAM IS NOW A FAILURE, not a caveat on a success.** Losing a soundtrack reports
    `FAILED` with `reason: UNSUPPORTED_CODEC` plus the codecs, and **caches nothing**; it used to `Commit`
    first and put `dropped` beside `READY`, serving — and caching for ever — a SILENT FILM as a 200.
    **Wire change:** `READY` no longer carries `dropped`, the codecs travel on `FAILED`, and
    `MediaConversionErrorCodes` + `MediaConversionEvents` join `docs/reference/wire.md`.
  - ⚠ **The host log separates the two CAUSES, which need opposite responses.** Dropped WITH a codec seam
    supplied is genuinely unsupported on this device; dropped with NONE means the platform was never asked
    — the adopter's composition rather than the file — and the message says to set `AudioConversion` before
    concluding anything about the codec.
  - ⚠ **Setting both `Convert` and `AudioConversion` THROWS at registration** rather than silently
    preferring one: the second configures the default engine, so a custom `Convert` would make it dead
    configuration (D63).

- 🔴 **`Mp4Remuxer.Plan(source)` — the remuxer can describe the file it WOULD write, without writing it.**
  Returns an `Mp4Layout` (`Header`, `Samples`, `TotalLength`) after ONE metadata pass, or `null` when the
  source cannot be described that way. A remux copies frames, so the output follows from the source's frame
  index and every byte has a known provenance before any of it exists — which is what makes
  `UseComputedRemux` able to answer a `206` with a real total, cold.

  ```csharp
  var layout = Mp4Remuxer.Plan(source);          // null → this source belongs on the segment path
  // layout.TotalLength      — the whole file, before a byte of it exists
  // layout.Header           — ftyp + moov + the mdat box header, i.e. the output's first Header.Length bytes
  ```

  - ⚠ **`null` is a ROUTING answer, not an error** — unreadable, not Matroska, no carriable stream, or a
    missing decoder configuration. It means "not this path", and the caller falls through to the next route.
  - **The plan describes the PURE COPY**, which for a source it accepts is the only write the remuxer makes;
    the plan and the write share one pipeline and one header composition, because two implementations of the
    same layout would drift into serving bytes the writer would not have produced.
  - ⚠ **Cost is a PEAK, not a total:** one walk of the clusters (metadata, never payloads) — ~110–150 MB for
    a two-hour film. `MatroskaSampleReader.ReadSamples` takes a `CancellationToken` and checks it, so the
    walk is abandonable.
  - 🔴 **`Mp4LayoutReader.CopyRange(layout, source, start, endInclusive, destination)` reads any byte range
    of that file without building it.** `endInclusive` matches HTTP `Range` semantics so a route passes the
    header's two numbers straight through. Header bytes come verbatim from `layout.Header`; the rest is a
    binary search into `layout.Samples` plus one seek-and-copy per overlapping span, so only the source
    bytes the range covers are ever read. **A range ordinarily starts AND ends mid-sample** — an element
    picks offsets with no idea where a frame begins. Zero-length spans (a degenerate laced frame) are
    stepped past rather than matched on offset alone, and an out-of-range ask throws rather than returning
    silence.
  - ⚠ **`CopyRange` trusts `source` to be the stream `layout` was planned from, deliberately.** A layout
    carries no identity because `Plan` takes a `Stream`, and an in-memory or network source has none to
    carry. That check belongs to whoever has a PATH — the route, keyed the way `SegmentStream` and
    `MediaConversion` already key their caches (`DerivedCacheKey.For(path, length, mtime)`).

- 🔴 **`interceptor.UseComputedRemux(IMissionScheduler, MediaAccessOptions)` — a container repair served as
  one ordinary URL, over ranges, without the file ever being written.** The route that joins `Mp4Remuxer.Plan`
  to `Mp4LayoutRangeStream`, and the payoff of D71: a page points one `<video src>` at it, requests answer
  `206` with a real `Content-Range` total, the whole timeline is seekable, and a seek to the last minute is
  serviceable cold. Nothing is transcoded and nothing reaches disk. **Any size — there is no ceiling.**

  🔴 **WARM THE PLAN FROM APP CODE, AND THE PAGE STAYS ONE PLAIN `<video src>` (D72).** A source nobody has
  planned answers `503 Retry-After: 1` while the metadata walk runs, and a media element cannot ride that out
  — measured on both mobile shells, it errors within ~70 ms (`error.code 4`, `networkState 3`) and never
  retries. So the wait moves EARLIER than the request, into the app, which already knows what it is about to
  play. **There is deliberately no readiness event and no page-side retry loop:** a page that must subscribe
  and set `src` from a handler is no longer a plain element, and at that integration cost segments are
  strictly more capable. So `UseComputedRemux` returns an `IComputedRemuxRoute` — keep it, do not just
  `using` it:

  ```csharp
  using var computed   = interceptor.UseComputedRemux(scheduler, access);              // FIRST — serves what it can plan
  using var conversion = interceptor.UseMediaConversion(scheduler, events, options);   // then the rest

  if (await computed.PlanAsync(path, ct) is MediaPlanOutcome.Ready)                    // ~2 s for a 79 MiB film
      ShowPlayer(url);                                                                 // its first request is a 206
  ```

  - **`PlanAsync` answers one of four things an app acts on differently** (`MediaPlanOutcome`): `Ready`;
    `Unplannable` (remote, or the output would lose a stream — use the conversion or segment path); `Refused`
    (outside `AllowedRoots`, or no such file — an app bug, not a retry); `Failed` (no answer; retryable, and
    nothing is remembered).
  - 🔴 **REGISTER IT BEFORE THE CONVERSION ROUTE.** Middleware run in registration order, so the other way
    round the conversion route answers everything its own `Resolve` matches and this one becomes dead code
    that still passes its own tests. **A source it cannot plan FALLS THROUGH, and that fall-through IS the
    D71 split** between a computed file and a re-encode.
  - ⚠ **`PlanAsync` applies the request path's authorisation, not a shortened one** — same remote check, same
    containment, same identity key, one implementation. A warm entry point that skipped it would be a way to
    make the kit walk any file the process can read. Cancelling stops the WAIT, not the walk.
  - **Caching and failure:** the layout is cached per source IDENTITY (`DerivedCacheKey` over path, length and
    mtime), and a body-production failure DROPS the cached plan so the next request re-plans. `Content-Type`
    is the OUTPUT's container (`video/mp4`), never the source file's. `RangeDelivery` is honoured for a
    computed body through the same single implementation `UseFiles` uses (D44).
  - ✅ **Proven on hardware, both shells** — a 60 s Matroska plays at `seekable=60.02` with a cold seek to
    80 % landing, and a 79 MiB film cold-seeks to 800 s. The walk runs in an `IMissionScheduler` mission
    because both mobile shells resolve a webview resource SYNCHRONOUSLY; blocking that thread deadlocked the
    iOS main thread, which is why the scheduler is a parameter rather than an option.

- 🔴 **`IMediaPlayer` — the host plays, the page drives (D54).** A portable contract in `Shenora`
  (`Shenora.Modules.Media`) with one implementation per shell, the same shape as `IPlaybackSession`:
  `OpenAsync`/`PlayAsync`/`PauseAsync`/`SeekAsync`/`CloseAsync`, a `Status` snapshot, a settable `Rate`,
  and a `StateChanged` event raised on transitions rather than on a timer. **Why it exists:** iOS PAUSES a
  `<video>` the moment the app backgrounds — the video track cannot render — and a native player is not
  subject to that. No amount of JavaScript closes that gap.
  - **All three shells ship one:** `IosMediaPlayer` (AVPlayer), `AndroidMediaPlayer`
    (`android.media.MediaPlayer` — deliberately NOT ExoPlayer, an engine D51 forbids shipping),
    `WindowsMediaPlayer` (Media Foundation; needs the versioned TFM and refuses by name on plain
    `net10.0-windows`). Each is registered BY ITS OWN TYPE — opt in by name. **`MediaPlayerBase`** holds the
    state machine with the platform left abstract, so a shell writes ~40 lines instead of ~150.
  - 🔴 **`MediaPlayer` + `MediaPlayerEvents` — the interceptor's media route is the PLAYER's output pipe
    (D58).** `MediaPlayer` owns probe → plan → resolve-the-URL and publishes to the page over `IEventBus`,
    so a media request is a question **.NET** answers and the page never decides anything about format.
    **This is what makes a consumer's own converter reusable by the player** — the resolved URL points at
    the conversion route, so nobody writes a second converter to get a player.
  - **`useMediaPlayer(ref)` in `@shenora/react`** binds a `<video>`/`<audio>` element to the host's player
    and posts one `PLAYER_REPORT` per element TRANSITION — never on `timeupdate`. New exports:
    `useMediaPlayer`, `MEDIA_PLAYER_MODULE`, `MEDIA_PLAYER_REPORT`, `MediaPlayerCommands`, and the
    `MediaPlayerReport` / `MediaPlayerReportState` / `UseMediaPlayerOptions` types.
  - **The page can also DRIVE the host's player over IPC** — `PLAYER_LOAD`, `PLAYER_PLAY`, `PLAYER_PAUSE`,
    `PLAYER_SEEK`, `PLAYER_RATE`, `PLAYER_UNLOAD`, `PLAYER_STATUS` on `MediaPlayerModule`. **The same verbs
    as `MediaPlayerEvents`, and the CHANNEL is the direction**: an EVENT named `PLAYER_PLAY` is the host
    telling the page's element to play; a REQUEST of that name is the page telling the host's player to.
    ⚠ A drive command with no registered player FAILS (`MEDIA_PLAYER_UNAVAILABLE`) where a report with no
    player is ignored — a report describes the page's own element, a command is something it WAITS for.
  - **`player.ReportTo(session)`** keeps the OS transport surface honest — `IPlaybackSession` used to
    publish whatever the app CLAIMED, so a lock screen could say "playing" while the audio had stalled.
    ⚠ It calls `Report` and never `Publish`, so the app's metadata is not blanked.
  - **`MediaPlayerOptions.OpenTimeout` (30 s)** — `OpenAsync` completes on the page's first non-`Opening`
    report and on nothing else, so an app whose `PLAYER_REPORT` route is missing got an await that never
    returned. The message names the route, the module to route it on, and the knob to raise;
    `TimeSpan.Zero` restores the unbounded wait.

- **`services.AddShenoraMedia(access)` — the media tier's CONTAINER half, following the kit's own
  `Add`/`Use` rule (D73).** `Add` is the service-collection level; `Use` is a wider configuration including
  its pipeline — the rule D66 shipped with the `AddMessageDispatcher` → `UseMessageDispatcher` rename. It
  registers ONE `MediaAccessOptions` (`TryAdd`, so your own registration wins), which is what makes the
  sharing three delivery routes need automatic instead of something each construction site has to remember.
  ```csharp
  builder.Services.AddShenoraMedia(new MediaAccessOptions
  {
      Resolve = uri => MyRouteToSourceFile(uri),
      AllowedRoots = [libraryDir],      // EMPTY serves NOTHING — fail-closed
      CacheRoot = convertedDir,
      Log = line => MyLog(line),        // ⚠ reaches the PLATFORM CONVERTERS too — see below
  });
  ```
  - 🔴 **SET `Log`, even if you discard it in release.** The mobile shells register their platform converters
    with no sink, so without one they are MUTE — and a picture that cannot be converted then reports only
    `dropped:["mpeg4"]`: the codec, and nothing about why. Reaching those lines used to require
    `GetService<IMediaStreamConversion>() as MediaConversionPipeline` and a re-registration; the shells
    resolve this object's `Log` now, so that downcast is no longer the only way in. It is deliberately the
    SAME sink the routes use, so an app configures the tier's diagnostics once.
  - **Full worked example — the `Add`, both routes in order, the warm, and the page — is
    `docs/guides/media.md`'s "What a whole adoption looks like".**

- **The media tier, whole — `Probe/` → `Plan/` → `Engine/` → `Deliver/` (`Shenora.Modules.Media`).** The
  TRANSLATION LAYER for the web (D52): the minimum transformation that makes a file the user already has
  playable in a webview, and never more.
  - **`MatroskaProbe`** answers "what is inside this file?" in managed code with no external tool, reading
    the HEADER only under a bounded budget. Returns the same `MediaProbeResult` the planner takes, or
    **null** for "I could not tell" — an ordinary answer rather than a failure.
  - **`Mp4Remuxer`** rewrites a Matroska file as MP4 with **every frame copied untouched**. The video in an
    ordinary `.mkv` is almost always H.264 or HEVC and the device decodes both in hardware; what the
    webview refuses is the BOX. No decoding, no encoding, no shipped binary, no licence weight.
    ⚠ **B-frames and laced blocks are handled** — the two things a remuxer usually gets wrong, and both
    produce a file that validates while mangling real content.
  - **`IMediaStreamConversion` — the TRANSCODE tier**, per-FRAME rather than per-file (a two-hour
    soundtrack is gigabytes as PCM). **Both mobile shells ship one**: Android through `MediaCodec`, iOS
    through `AudioConverter`/VideoToolbox. An H.264 + AC-3 film the remuxer alone refuses becomes fully
    playable — the picture is still copied, only the soundtrack goes through the device's codecs.
    ⚠ Zero outputs from `Push` is NORMAL (codecs buffer), and 🔴 **`Drain` is not optional** — skip it and
    the tail stays inside the codec, producing a well-formed file whose audio stops early.
  - 🔴 **Conversion is a MIDDLEWARE PIPELINE, not a replaceable implementation** —
    `MediaConversionPipeline` with `Use(...)`, the shape `IWebViewInterceptor` already has. An app
    supplying its own converter **adds it to the chain and keeps the kit's behind it**, so wanting a better
    DTS decoder does not mean re-providing AC-3 and AAC. **`IMediaContainerWriter`** is the muxer's own
    seam, so a consumer can replace the muxer and keep the kit's demuxing and timing, or vice versa.
  - **`IMediaCapability` + `MediaCapabilityExtensions`** — what THIS DEVICE can decode and encode, asked at
    runtime. `MediaPlaybackPolicy` is still the app's and the kit still ships no codec list (D42), but "the
    app's" had meant "the app GUESSES". **The kit now ships the QUESTION rather than the answer.**
    🔴 It reports the PLATFORM's stack, which is NOT what the webview will play — a device routinely
    decodes more than its browser plays, and **that gap is the transcode tier's whole reason to exist**.
    ⚠ **`WithDeviceEncoders` intersects the device's answer with what your PIPELINE can convert, and
    defaults to the kit's own reach — AUDIO.** The two are different questions and they disagree on
    Android, where `MediaCodecList` reports video encoders the kit has no engine behind: without the
    intersection a VP9 film planned as `Transcode (video)` and the remuxer then dropped the track. If you
    supply your own engine, name what it can really do: `WithDeviceEncoders(device, [Audio, Video])`.
  - **`interceptor.UseSegmentStream(…)` + `ISegmentEngine`** — play a source the webview cannot decode
    WITHOUT converting it first. The route publishes an HLS manifest computed from the duration alone, so
    the scrub bar is the right length and a seek is expressible before a single segment exists. An
    hour-long source is an hour-long wait through whole-file conversion and a few seconds through this.

- 🔴 **A Live Activity needs NO SWIFT in the adopting app (D69).** `<ShenoraLiveActivity>true</…>` is the
  whole adoption: the kit compiles its own generic widget into the extension, and what it draws comes from
  C# — config the Swift side reads at RUNTIME, not Swift the build generates.
  - **`Components.ProgressCard` / `StatusCard` / `CounterCard`** — a complete, proportioned activity in ONE
    call, because an adopter's activity is usually one of three shapes whose metrics are fiddly to get
    right and invisible when wrong. Each returns an ordinary `Presentation`, so `with` overrides any
    surface and no rendering path is hidden.
  - **`LiveActivityAppearance`** — an SF Symbol and a `#RRGGBB` tint. **`Presentation`** — one element per
    Island SURFACE, with `{title}` / `{subtitle}` / `{progress}` bound at every RENDER; a surface left
    unset keeps the kit's own arrangement, so you can adopt one at a time. (*Surface*, not *region*: iOS
    spends "region" on the three sub-views it slices the expanded one into.) Both cross as ActivityKit
    *attributes*, so they are fixed for the activity's lifetime.
  - **`Layout`** is the container — the `div` a web developer would reach for, because this kit's adopters
    write React — carrying flexbox's two axes: **`Justify`** (`Start` / `Center` / `End` / `SpaceBetween`)
    along the axis and **`Align`** (`Leading` / `Center` / `Trailing` / `Fill`) across it. With `Text` /
    `Icon` / `ProgressBar` / `Spacer` that is the whole vocabulary, in
    `Shenora.Modules.Platform.Activities`; `Text` and `Icon` take their content positionally
    (`new Icon("bolt.fill")`). ⚠ **There is no grid, and that is a decision rather than a gap** — if a
    design needs columns that agree ACROSS rows, say so, because that measurement is what earns it.
  - 🔴 **`Cutout` — the sensor housing as a placeholder, and it is what lets an app describe the Island as
    ONE panel.** iOS hands the expanded presentation to the widget as three SEPARATE views and nothing
    drawn in one can cross into another, so the kit splits the panel instead: children before the cutout go
    to the leading view, after it to the trailing view, the rest to the strip below. Outside the Island a
    cutout is simply flexible blank space.
  - A text names a **ROLE**, not a font (`Headline` / `Body` / `Caption` / `Value`) — D13 holding, since a
    `Style` property would be the first brick of a design system the kit must not become.
  - **Setting `ShenoraLiveActivityViews` still wins**: an app's own SwiftUI is a first-class path, not an
    escape hatch. **`ILiveActivities.PushToken`** exposes the one part of push updates an app cannot reach
    from C# — ⚠ its path needs an App ID with the Push Notifications entitlement, which a free team cannot
    create, so the seam is exercised and the token itself is not.

- 🔴 **`@shenora/cli` — a second npm package, and the `shenora` binary (D67).** Take a built app onto a
  simulator or a real device with **no Xcode project of your own**, in the shape `cap`/`electron` adopters
  expect. `npm i -D @shenora/cli`, then `shenora init` · `copy`/`sync` · `ios …` · `android …`.
  Configuration is `shenora.deploy.json`, searched upward from the cwd so a monorepo runs it anywhere.
  - **It is a `devDependency` and ships inside NOTHING you deploy**, which is why it does not contradict
    "a capability gets a folder, never a package" — that rule governs what an app carries at RUN TIME.
  - **iOS:** `doctor|devices|simulators|deploy|log|shot|build`. `build` runs `dotnet publish`, Release by
    default, `-p:ArchiveOnBuild=true` so the SDK packages an `.ipa`. Verified end to end on an iPhone 17
    Pro over the LAN with no cable.
  - **Android, and it runs on WINDOWS** where most .NET Android work happens: `doctor|devices|deploy|log|build`.
    **It finds the JDK and `adb`** instead of demanding `JAVA_HOME` (Android Studio ships one in `jbr/` and
    sets no variable, so the common case is a machine that HAS one and cannot say where).
    🔴 **`android log` filters by PID, not by tag** — a tag filter has to know how the app logs, and there
    is no right default.
  - ⚠ **The config describes your PROJECT; the command line describes your MACHINE.** Anything after `--`
    is passed to `dotnet build`, deliberately not a config field: a committed override silences a
    machine-specific mismatch for everyone who clones the repo.

- **`UseMissions`, `UseFileSystem` and `UseMediaPlayer` gain an `(options, services)` overload** —
  configure a capability and SUBSTITUTE its collaborators in one place.
  ```csharp
  builder.UseMediaPlayer((x, services) =>
  {
      x.Access = new MediaAccessOptions { Resolve = static _ => null, AllowedRoots = [libraryDir], CacheRoot = "" };
      services.AddSingleton<IMediaPlayer>(sp => sp.GetRequiredService<WindowsMediaPlayer>());
  });
  ```
  Substituting a kit default already worked; what an app could not do is KNOW that, which took reading the
  kit's source to learn these are `TryAdd`.

- **`EventMessage.CoalesceKey` / `IpcNotification.CoalesceKey`** — an event may declare that it SUPERSEDES
  an earlier undelivered one with the same module, type, scope and key. The pump has always coalesced
  ROUND TRIPS while still carrying every payload inside them, so a request reporting progress in a tight
  loop cost the page a hundred fold operations to render one number.
  ⚠ **Opt-in, and it must stay so:** the pump cannot tell a snapshot from a delta, and coalescing deltas
  loses data — so only the emitter may set a key.

- **`ResourcePack` + `ResourcePackOptions` (`Shenora.Modules.Update.Compression`)** — a named, versioned
  set of files an app needs ON DISK at runtime: a native binary for the current ABI, a model, a font set.
  Delivered as one archive, extracted under the kit's existing containment and limits, and marked ready
  **last** so a half-extracted pack is never used.
  ```csharp
  var pack = new ResourcePack("engine", "7.1.2", new ResourcePackOptions { Root = ShenoraPaths.Data });
  await pack.StageAsync(File.OpenRead(archivePath));   // no-op once ready
  var exe = pack.PathOf("arm64-v8a/libengine.so");     // null if absent, escaping, or not ready
  pack.PruneOthers();                                  // at STARTUP — the old one is still loaded at stage time
  ```
  ⚠ **This is also how you use a copyleft payload with an MIT kit (D51).** Shenora ships no engine and will
  never redistribute a GPL/LGPL binary from an MIT package — that would hand attribution and relinking
  duties to every consumer. Supply it yourself and the obligation stays with your app, where the choice
  was made.

- **`WebViewResourceRequest.IsRootWithFragment(Uri)`** — true when a request is for the site root and
  carries a `#fragment`. It is what the mobile reload repair keys on, and it is public because a middleware
  answering the root itself needs to recognise the same shape. `WebViewResourceRequest.Uri` now documents
  that it CARRIES a fragment, and which readings survive it: `AbsolutePath` is safe, `ToString()` and
  `PathAndQuery` mis-resolve — and the trap is that the safe reading also HIDES the fragment.

### Fixed

🔴 **The 2026-08-17 full-codebase review — every finding below was adversarially verified against
source before it was fixed, and each fix carries a test that fails on the code it replaced** (where a
unit test can reach it; the two mobile-shell items compile for both TFMs and await the already-tracked
device run).

- **`MissionResult.Attempts` was wrong for every FAILED mission** — the retry loop only returned a
  count on success, so a mission that failed after three attempts reported 0 (or 1 on the commit path),
  against the property's own "attempts actually made" contract. The entry's live attempt counter is the
  answer on the throw paths now.
- **Cancelling a PENDING mission could hang its `SubmitAsync` task forever.** The cancellation check
  lived only inside dispatch, which runs on submit, completion and lane change — none of which need
  ever come. The token now wakes dispatch itself; the class doc lists the new trigger.
- **A durable mission's store writes could arrive out of order.** The Queued append was fire-and-forget
  while Running/Remove were awaited, so a slow store could receive Queued last — a phantom record that
  recovery re-executes. Every later write now chains behind the entry's own Queued gate (a gate, not a
  settable field: the entry is dispatchable inside the submit lock, before the append starts — the
  phase review caught a pre-cancelled token reaching the forget path first). A mission cancelled while
  pending also removes its record now: the caller said no, so the next boot must not run it.
- **One bad IPC module registration silently killed every DI-registered module until restart.** The
  module map's `Lazy` cached its exception (a duplicate `ModuleName`, one facade factory throwing
  once), so every later request answered `UNKNOWN_ERROR`. The map builds `PublicationOnly` now — a
  transient failure heals on the next dispatch.
- **Closing a frameless window while minimized corrupted its saved geometry.** The Minimized fallback
  overwrote the window's own restore truth with `Form.RestoreBounds` — which holds the WORK-AREA rect
  after a manual maximize — so the next launch re-derived the restore target from it and restore-down
  became a permanent no-op. Sabotage-verified against a shown window (the first pinning test passed on
  a handle-only form and was rewritten).
- **The iOS pasteboard dropped every custom format on read-back.** The write side accepts any media
  type verbatim; the read side probed only `public.png`/`public.html`, so an app's own
  `application/…` payload vanished — against `ClipboardContent.Formats`' lossless round-trip. The
  read now enumerates the pasteboard's own types, bounded to media-type shapes so a foreign app's
  multi-megabyte copy is never materialized wholesale.
- **`IosPlaybackSession.Dispose` never cleared the lock screen** — `_disposed` was set before
  `Clear()`, whose first line returns on it. Deterministic on every dispose; the stale Now Playing
  entry stayed pinned.
- **The Android media converters lost frames silently.** A decoder with no free input buffer dropped
  the frame (and, at drain, the end-of-stream marker — truncating held-back frames) with no report,
  while the encoder side reported the identical situation. Drops are reported now and the drain
  retries the EOS queue over a bounded window.
- **`createShenoraStore` rendered permanently stale state after a remount.** The snapshot-once flag
  never reset when the last subscriber left, so a later mount skipped the reload — and entries the
  host evicted during the silent gap could never correct. Detach resets it now, and a superseded
  epoch's late snapshot answer is dropped rather than clobbering the fresh one.
- **`bindSegmentStream` leaked its object URL on two failure paths** — `addSourceBuffer` refusing the
  codecs and the init append failing both rejected before the caller held a binding to dispose. All
  three pre-return failure points revoke now. (An earlier fix claimed "every failure past the mint
  revokes"; its own diff covered only the open wait.)
- **Every AAC `esds` this kit wrote declared its length three bytes short** — the trailing term
  budgeted 3 bytes for a 6-byte SLConfigDescriptor. Byte-accounted independently and pinned; strict
  parsers no longer see a malformed descriptor.
- **`shenora ios build`/`deploy` could ship yesterday's binary as today's.** No staleness guard, while
  the Android half had just gained one for the same incident class — an incremental publish that
  produced nothing reported the previous artifact, size and all, and deploy installed it. Both find
  helpers take the build's start stamp now (a `.app` is clocked by its `Info.plist` — a directory's
  mtime survives a rebuild), and the message says STALE when a leftover is what it found.
- **Three iOS verbs read a tool failure as a fact about the machine** — `simulators` (a broken
  `xcode-select` printed "no simulators installed"), `doctor`'s signing check (a locked keychain read
  as "none — go to Xcode Settings"), and the extension pre-check (a `codesign` failure blocked a
  device install claiming a missing entitlement). Each now names the failing tool instead.
- **Disposing one pipeline registration could remove two.** `WebViewResourcePipeline` filtered by
  reference equality, so the same delegate object registered twice lost BOTH slots on the first
  dispose and the second handle's dispose was a silent no-op. Removal takes one slot now.
- **The launcher could terminate instead of reporting "update not applied".** The staged-overlay walk
  advances a directory iterator whose `error_code` overload guards construction only — a mid-walk
  filesystem error threw through `main` with no catch anywhere. One exception boundary now turns any
  escape into the `failure` result the design promises, and the app still launches.
- **The Android save picker died with its Activity — measured on a device, and the obvious fix was
  refuted there too.** A recreation while the picker was open (a locale or font-scale change, an
  adopter manifest without MAUI's `ConfigurationChanges` defaults) orphaned the result callback and
  `SaveAsync` hung forever. The AndroidX-registry answer cannot work on a MAUI host: the recreated
  activity's bundle carries no AndroidX saved-state section at all, so the registry's restored
  request-code map is empty and the arriving result falls through the legacy path unseen. What the
  same run proved DOES survive recreation is the framework's own routing — so `ActivityResultRelay`
  now owns its request codes over `StartActivityForResult`, and results reach it through a documented
  one-line `OnActivityResult` forward in the adopter's MainActivity (`docs/guides/mobile.md`).
  Two lifecycle repairs landed with it, both measured in the same scenario:
  `MobileWindowLifecycle.IsRecreating` lets the `Window.Destroying` wiring tell a recreation from a
  shutdown (treating it as shutdown cancelled every in-flight request), and the mobile IPC bridge
  sets the new `IpcHostBridgeOptions.CancelInFlightOnDispose = false` — the bridge dies with its
  PAGE on every recreation, and work the page started now runs to completion; the response is
  dropped with the page, the user's file is not. End-to-end on an API 36 emulator: host destroyed
  and recreated mid-picker, save completed, file intact. Process DEATH is the stated boundary — the
  awaiting task dies with the process, so the caller's cancellation token is the only honest escape
  past it.
- **Small contract repairs**: `FileChange.Move.Overwrite` is scoped to FILE destinations and the
  directory refusal names the rule instead of surfacing as a bare `IOException`; the default
  `OpenReadAsync` returns the documented null when the file vanishes between its existence check and
  the open; the request tracker disposes its linked cancellation source on the announced completion
  path (it leaked one per tracked request, bounded by `MaxHistory`); a malformed Matroska EBML header
  size is refused instead of seeking backwards; the mission scheduler's dispose paths release pending
  missions' keys so `IsActive` stops answering true for work that no longer exists.

- **`useShenora()` returned a fresh object on every render**, so the natural
  `const shenora = useShenora(); useEffect(…, [shenora])` re-ran every render — a subscribe/unsubscribe
  cycle per frame in the worst case. It is memoized now, and its identity changes only when the bridge
  does or when `isAvailable` flips, which are the two moments a consumer means to react to.

- **The three BUILD commands looked for their artifact one level too high** when `project` names a
  directory rather than a `.csproj` — which `dotnet` accepts and this CLI passes straight through.
  `shenora copy` had this fixed; `android build`, `ios build` and `ios deploy` each kept their own
  `path.dirname(cfg.project)`, so a successful publish reported *"the publish reported success but no
  .apk appeared under src/bin/…"* about a folder that could never hold one. One shared `projectDir`
  now, pinned by tests at the helper and at `cmdCopy`.

- **`shenora ios` blamed your config for your typo.** Typing a group name to see its verbs — or
  mistyping one (`ios delpoy`) — ran the config lookup first and answered *"no shenora.deploy.json here
  or in any parent directory"*: true, and about something you did not ask. The verb is checked first
  now, an unknown GROUP names itself too, and the routing has tests at all — it could not be tested
  before, because `cli.ts` invoked itself at module scope.

🔴 **`@shenora/cli` — six commands answered a question nobody asked.** Every one took a signal meaning
*"I could not do this"* and presented it as a fact about your project, your phone or your build. They are
grouped because the shape is the point: each was silent, confident and wrong, and none of them failed.

- **`shenora sync` could not run on Windows at all.** It shelled out to `/bin/sh` — only to reach
  `| tail -20` — so it died before `dotnet` was invoked, then reported "restore failed — see the output
  above" above nothing. Measured: `spawnSync('/bin/sh', …)` is ENOENT here; the direct spawn returns 33
  lines. Windows is not an edge case for this CLI — the Android half exists because most .NET Android
  work happens there.
- **`shenora android build --aab` could hand back an APK.** `findPackage` preferred `-Signed.apk`
  unconditionally, so a leftover APK in the publish directory was reported as the artifact, size and
  all — and uploaded to Play as the bundle you thought you built. It never substitutes across formats
  now, in either direction.
- **Any Android build could hand back the PREVIOUS build's output.** The publish directory is not cleaned
  between runs, so a build that produced nothing returned the last one's artifact while every downstream
  step believed it had succeeded. An artifact older than the build that supposedly produced it is now
  rejected and reported as stale, by name.
- **A `devicectl` failure was reported as "no iPhone is connected".** Every failure path collapsed to an
  empty list, so a broken reader became a confident statement about your hardware and sent you to check a
  cable. It now says the tool failed and prints devicectl's own message — which the old `2>/dev/null` was
  discarding. ⚠ An empty list from a working devicectl still means exactly what it says.
- **`shenora ios log` could not tell "your app logged nothing" from "the log reader failed".** The exit
  status was discarded, so no booted simulator printed a header, then silence, then exit 0. Three
  outcomes now, not two: a failure names itself, and an empty window says which window it looked at.
- **A mistyped `--simulator` installed to whatever else was running.** The boot failure was swallowed by
  `|| true` — there for a real reason, since booting an already-booted simulator exits non-zero — and a
  bad name went with it. `install` and `launch` also address the NAMED device now rather than `booted`,
  which means "whichever simctl picks" when two are running.
- **A missing tool or a fired timeout produced a bare exit code with no output.** `spawnSync` reports
  both through `error`, which nothing read, so a missing `dotnet` surfaced as whatever the caller assumed
  a non-zero exit meant. Both are named now. The timeout half mattered most: it exists because `adb`
  hangs where "the user cannot tell it apart from a slow build", and its firing was itself silent.
- **`copy`/`sync` staged the bundle one level too high when `project` named a DIRECTORY** — legitimate
  config, since `dotnet` accepts one, but `path.dirname` on a directory yields its parent. Every
  containment guard passed, the wrong directory was deleted to make room, and the app shipped with no web
  assets.
- **`--` passthrough: the Android half ignored it, and the iOS half mangled it.** The help text promises
  "anything after `--` goes straight to `dotnet build`" directly beneath the Android commands, which
  never read it — while `--aab`, typed *before* the separator, kept working, so the feature looked
  half-alive. On iOS the arguments were joined into one string and re-split by the shell, so
  `-p:Title=Hello World` (or any path with a space) arrived at `dotnet` in pieces. `splitArgs` now yields
  an ARRAY: Android spreads it into argv with no shell involved, iOS quotes each argument separately.
  Both halves also read their own flags from the pre-separator half now, so a passthrough token can no
  longer be mistaken for a device serial.

- **`useDropZone` minted a `crypto.randomUUID()` on every render and discarded it.** `useRef(newZoneId())`
  evaluates its argument each render and keeps only the first. Invisible, because the value was always
  correct. ⚠ `zoneId` and `dropClassName` are documented as first-render-only now — the latter
  deliberately, because the hover effect captures the class for its cleanup while the drop path reads it
  live, so a mid-hover change would leave a stale class stuck on the element.

- **`ShenoraEventBus.getSubscriptionCount(module)` answered the GLOBAL total.** The guard read
  `if (module && type)`, so passing a module on its own fell through to the count-everything branch — a
  plausible number, for a different question, with nothing to indicate the substitution. It now counts
  what would receive any event of that module: its exact-type subscriptions, its whole-module ones, and
  the catch-alls. A module whose name prefixes another (`APP` vs `APPLE`) no longer borrows its
  subscriptions either, because the exact map is keyed `module\0type` and the scan includes the
  separator. Fixed at runtime rather than with an overload signature: this package ships JavaScript, and
  a type-level restriction would leave a JS consumer getting exactly the same wrong number.

- **The dev interceptor stopped recording after `configureBridge()`.** `installDevInterceptor` wraps a
  specific bridge INSTANCE but keyed its idempotency on the window global merely existing, so once the
  default bridge was replaced the tool watched the disposed one — looking installed, recording nothing,
  and driving a dead bridge from `window.__shenora.call`. Idempotency now keys on which bridge and bus
  were wrapped.

- 🔴 **`InteractiveSession.ClearProfile` reported a successful logout it had not performed.** It
  swallowed every failure and returned `void`, and the commonest failure is a profile still LOCKED by a
  session window that has not finished closing — so the app said "signed out", the cookies survived, and
  the next session walked straight back in. That is the exact incident the method's own docs cite as its
  reason for existing. **It now returns `bool`** (true = the tree is gone, including "was never there").
  A statement-style call still compiles; check the result wherever you tell a user they signed out.

  It also **refuses a volume root**. The existing `..` guard stopped a path climbing OUT of the sessions
  tree but said nothing about one that never pointed inside it, and `Path.Combine(root, "")` collapsing
  to a drive is the realistic way to get there — into a recursive delete that swallowed its errors.

- **The streaming sample leaked a whole session when a renderer died.** Its `OnEnded` cleared the handle
  without disposing, so the off-screen window and the browser process holding the profile lock survived
  for the life of the app, with nothing left pointing at either. `OnEnded`'s docs now say plainly that
  disposing is the caller's job there — and why it deliberately does not hand you the session (it can
  fire during `StartAsync`, before one exists).

- 🔴 **An interactive session window could refuse `Application.Exit` — and could become unclosable.**
  `SessionController` held the user's close so a driver gets its final cookie read, but the rule was
  "veto whenever the flow has not finished", which vetoed *every* close for *every* reason. So a session
  window kept the whole app alive on exit, and a driver awaiting something that never completed left a
  modal window nothing could dismiss. The hold is now spent after ONE use — a second close means the user
  has said it twice — and applies only to `CloseReason.UserClosing`, so `Application.Exit`, a Windows
  shutdown and Task Manager pass straight through.

- 🔴 **A cancelled session freed its busy gate while its window was still on screen.** Completing the
  caller and releasing the gate were one action. The caller took "cancelled" as "finished", called
  `ClearProfile` against a profile the live browser still held — throwing into a swallow — and a second
  `RunAsync` walked past the gate to open a SECOND window on the same profile. The caller is still
  answered the instant it cancels (that half was right: it is what stops a never-pumped UI post hanging
  it forever), but the gate now belongs to whoever owns a window and opens when that window is gone.

- **A session cancelled before its window appeared left the app's splash up.** The `Shown` handler's
  cancellation check returned before the `try`, skipping the finally that holds the only unconditional
  `OnLoading(false)` — after `OnLoading(true)` had already run. With `LoadingFallbackTimeout = Zero`,
  which is documented as supported, no timer rescued it either. ⚠ Not covered by a test: everything past
  that line needs a live WebView2 and a modal loop.

- 🔴 **`@shenora/react`'s typed-payload checking was OFF at every shipped call site.**
  `BaseModuleService.send` takes the response as a type argument, and TypeScript has no partial
  type-argument inference — so naming it made the ROUTE parameter fall back to its default (the union of
  every key) and `payload` widen to the union of every route's payload. Verified with `tsc`: an
  identical wrong payload is a TS2353 without the type argument and compiles clean with it.

  ```ts
  openFile(): Promise<FileDialogResult> { return this.send('OPEN_FILE', { payload: { options } }); }  // checked
  openFile() { return this.send<FileDialogResult>('OPEN_FILE', { payload: { options } }); }           // NOT
  ```

  All eight shipped call sites used the second form, so the feature checked nothing anywhere it was
  actually used — while its own `@ts-expect-error` pin passed, because that pin uses the inferred form.
  **The response now comes from the method's declared return type**, which every one of them already
  had. No signature change; if you wrote your own service, drop the type argument and declare the return
  type.
  ⚠ Pinned by a SOURCE check (`WireMirrorTests`), because the broken form compiles — that is the defect,
  so no type-level assertion can catch it.

- **Fourteen exported TYPES were unpinned, so deleting any of them would have broken consumers
  silently.** `@shenora/react`'s barrel is pinned twice — a runtime array and a type-only tuple — because
  a type has no runtime binding and the runtime check is structurally blind to it. Nothing checked the
  TUPLE, though, so every type added after it was written was simply absent from it: the
  segment-binder's six, the media-player's three, the dev interceptor's three, and two more. All listed
  now, and a new check compares the two sets so the fifteenth cannot repeat it.

- **A session's popup and permission policy is now the APP's to set** — `OnWindowRequest` and
  `OnPermissionRequest` on `SessionBrowserOptions`. Both **default to exactly today's behaviour**
  (suppress every popup, deny every permission), so nothing changes for an existing app; what was
  missing was any way to disagree. A session driving your OWN page may legitimately want clipboard read;
  a co-browse flow may legitimately open a popup.
  ⚠ Their safe direction is the opposite of the three hooks below: there a throwing hook must keep the
  page MOVING, here it must keep REFUSING. A buggy policy must not become an open door.

- 🔴 **Three browser prompts could WEDGE a session forever, and now can't.** `ScriptDialogOpening`,
  `BasicAuthenticationRequested` and `ClientCertificateRequested` were all unhandled — which makes
  WebView2 raise its OWN modal, against a window that is off-screen, so nothing can ever answer it and
  the page stops for good. All three are handled now whether or not you supply a hook, and **the
  DEFAULTS are the fix**: dismiss the dialog, cancel the challenge, cancel the certificate.

  ```csharp
  new SessionBrowserOptions {
      OnScriptDialog      = d => { d.Accept = true; d.ResultText = "42"; },   // null = dismiss
      OnAuthRequest       = c => { c.UserName = u; c.Password = p; },         // null = cancel
      OnCertificateRequest = r => r.SelectedIndex = 0,                        // null = cancel
  }
  ```

  **Hooks, not events** — one owner, and the handler acts ON the argument the way a web event does,
  rather than returning a verdict. A throwing hook lands on the safe default instead of escaping into a
  WebView2 event, where it would be an unhandled UI-thread crash.
  ⚠ **Measured against the SDK: `ScriptDialogOpening` and `BasicAuthenticationRequested` have no
  `Handled` property** — subscribing is itself the suppression, so those handlers must exist even when
  they look like they do nothing.
  ⚠ `SessionAuthRequest` overrides `ToString()` to redact — a record prints every property, and that one
  holds a password.

- 🔴 **`SessionBrowserOptions.RequestFilter` fails OPEN, and two docs called it the enforcement seam.**
  A throw from the filter allows the request — deliberately, because it runs on every subresource of
  every page and failing closed on one buggy predicate would blank the page. But `RenderSessionPoolOptions`
  told adopters the opposite in two places ("the async guard is a pre-check; the request filter is the
  enforcement seam"), so an app that put its whole SSRF blocklist there had a policy that **stopped
  blocking the first time one edge case threw** — silently, because the catch logged nothing.
  The docs now say what it is: a sieve for BREADTH, while the navigation guard and the kit's own
  cross-origin cancellation are what hold, both failing closed.
  ⚠ **And the throw is reported** — once per session, naming the consequence ("the request was ALLOWED…
  if this filter is your blocking policy, it is not blocking"). Once, because it runs per subresource.

- 🔴 **A store selector whose CLOSURE changed returned the previous selector's value.** The result was
  memoized against STATE identity alone, so with the store untouched between renders the cache hit and
  handed back the old answer: a list row doing `useShenoraRequests(s => s.byId[id])` whose `id` prop
  changes — virtualised reuse, a route change — rendered the PREVIOUS row's data until some unrelated
  event happened to replace the state. It compares the selector's RESULT now (identity, then one level
  of own keys) and reuses the previous reference only when equivalent.
  ⚠ **No dependency, and the derived-object ergonomic is kept.** An inline `s => ({ n: s.items.length })`
  still does not loop — the shallow step is what allows it — which is more than zustand v5 gives without
  an opt-in `useShallow`. A selector needing DEEP comparison is selecting too much.

- 🔴 **A failed segment fetch reported NOTHING by default.** `bindSegmentStream`'s diagnostics all went
  through the OPTIONAL `onDiagnostic`, so with none supplied — the default, and the shape of the public
  API — a segment answering 500 or an `appendBuffer` `QuotaExceededError` produced no console output, no
  rejection and no state change. Playback simply stalled with nothing anywhere to explain it, while every
  other error path in the package (`onPostError`, the store's `onError`, the event bus) already defaults
  to `console.error`. Failures now do too. ⚠ Only when no handler was supplied — a caller that took the
  seam owns its reporting and is not double-logged.

- 🔴 **`bindSegmentStream` could wait forever for a MediaSource that never opened.** Its only rejection
  path listened for `error` — **which `MediaSource` does not fire**; the spec's events are `sourceopen`,
  `sourceended` and `sourceclose`. An attachment that closed rather than opening (the element detached
  before load, an attachment refused) left the caller's `await` pending with no error, no diagnostic, no
  binding to dispose, and the object URL never revoked. It listens for `sourceclose` now, with a 10 s
  deadline covering whatever is neither, and every failure past the mint revokes the URL.
  ⚠ `revokeObjectURL` joins `createObjectURL` as an injectable option, so that revoke is assertable
  rather than hoped for.
  ⚠ **`SegmentBinderError` is not thrown for literally every failure**, and its doc said it was: a
  `TypeError` from the manifest fetch and a `RangeError` from a truncated init segment both propagate as
  themselves. Corrected.

- 🔴 **`SessionController.NavigateAsync` had no time limit.** `NavigationCompleted` never fires if the
  renderer dies mid-load, so a co-browse navigate could wait forever — `DisposeAsync` does not complete
  it either, and the sample passes no token. It is capped at 30 s now, the same soft cap
  `RenderSession.NavigateAsync` has always had: the cap completes the wait rather than throwing, because
  a slow load is not an error, while a caller's own token still surfaces as cancellation.
  ⚠ Not unit-tested — the path needs a live renderer death. It mirrors the sibling's proven shape.

- 🔴 **A superseded `PLAYER_LOAD` no longer seeks the NEXT track to the old one's position.**
  `useMediaPlayer` registers the `startAt` seek on `loadedmetadata` with `{ once: true }`, which removes
  a listener only when it FIRES — and a second load calls `element.load()`, which ABORTS the first, so
  its metadata event never comes and its listener survives. Load A at 10:00, then B at 0:00 before A's
  metadata lands, and B starts ten minutes in: B sets no listener of its own, and A's is still attached.
  A pending seek is now cancelled by the next load and by the effect's cleanup.

- 🔴 **`bindSegmentStream` leaked one `error` listener per appended segment.** Same `{ once: true }`
  cause from the other side: the success path fires `updateend`, so the `error` listener was never
  removed and each retained a settled reject closure. A two-hour stream at six-second segments
  accumulates ~1,200 of them on one `SourceBuffer`, `dispose()` shed none, and a later real error
  invoked every one. Both listeners now come off on either outcome.

- 🔴 **A routine GPU recovery no longer throws away the whole render pool or kills a live co-browse
  pane.** `ProcessFailed` fires for the entire Chromium process tree and the session stack treated every
  kind as "the renderer died" — so a GPU-driver TDR (routine on Windows; Chromium self-heals) discarded
  every warm instance at seconds-per-instance to rebuild, and completed a `StreamingSession`'s frame
  channel permanently over a page that was still running. `RenderProcessUnresponsive` fires while a
  renderer is merely BUSY, and `FrameRenderProcessExited` is one out-of-process iframe, not the
  document. Only `RenderProcessExited` and `BrowserProcessExited` now reach
  `onProcessFailed`, which is what its own doc always promised. `WebViewHost` already filtered this way.
  ⚠ **Every kind is still LOGGED, and the line now carries the diagnostic fields** — exit code, process
  description, failing module. Sessions run unattended, so that line is the only signal an adopter gets,
  and `{Kind} ({Reason})` alone names the event while withholding its cause.

- 🔴 **`StreamingSession.StartAsync` and a render-pool lease could hang forever, cancellation included.**
  Both marshal their work onto the UI thread and check the `CancellationToken` only INSIDE the posted
  body — which is unreachable if nothing pumps. `BeginInvoke` succeeds whenever the handle exists,
  including after `Application.Run` has returned, so `StartAsync(options, ct)` never returned **even
  with `ct` already cancelled**. Worse for the pool: the lease holds a capacity permit while it waits,
  so `Dispose()` could not free it and the slot was gone for the process lifetime.
  The token now reaches the returned task, as `InteractiveSession.RunAsync` already did.
  ⚠ **Cancelling after work has begun tears the instance down rather than leaking it** — handing
  ownership over is what completing the task means, so a completion that loses the race is a teardown
  obligation. Without that the fix would trade a hang for a leaked browser process holding the profile
  lock, which is the worse of the two.

- 🔴 **`InteractiveSession.ComposeProfileDirectory` accepted segments Windows normalises away.** The
  per-account profile directory is the session stack's isolation boundary — two accounts sharing a
  directory share a cookie jar — and every check it made was a blocklist. Measured with `GetFullPath`
  against a root of `C:/root`:

  | segment | resolved to |
  |---|---|
  | `"..."`, `"...."`, `".. ."`, `" . "` | **the root itself** |
  | `"acct."`, `"acct "`, `"acct.."` | `C:/root/acct` — the same jar as `"acct"` |

  Every one passed the empty, separator, `.`/`..`, invalid-character and reserved-name tests (a dot and
  a space are both legal file-name characters), **and the containment check**, because the root does
  start with the root. So an account id of `"..."` returned the whole sessions tree — and `ClearProfile`
  on it would delete every other account's profile. A segment must now survive Windows' own
  normalisation unchanged, which is asked of the OS rather than enumerated.
  ⚠ **Two ids differing only in CASE are still one directory**, because the filesystem says so and this
  cannot overrule it — now documented on the method. Fold or encode case-sensitive ids.

- 🔴 **The render pool's redirect policy compared the HOST, which excludes the port.** Its own doc gave
  `302 → http://127.0.0.1:8080/admin` as the hop it exists to close, and that hop was allowed whenever
  the vetted origin was also loopback — which the shipped sample's guard (`uri.IsLoopback`) makes the
  normal case. It compares `Uri.Authority` now: the port counts, and a default port is still omitted so
  the documented `http` → `https` allowance is unchanged.
  ⚠ **Main frame only, and now said so out loud** — a cross-origin IFRAME is a subresource, which is the
  request filter's job (`SessionBrowserOptions.RequestFilter`), not this event's.
  The rule is extracted as a testable unit; it had none, which is how a defect sat behind a comment
  naming the very hop it failed to stop.

- 🔴 **A release whose only change is DELETING files now stages and applies.** `FetchAsync` returned
  not-pending whenever the diff had no additions and no updates, so such a release never staged and never
  applied — the dropped files stayed on disk forever with no error anywhere, and a
  dropped-but-still-present assembly is still loadable, which is usually the reason a release drops one.
  ⚠ **`CommitAsync` no longer throws `ArgumentException` for an empty manifest** — SemVer surface, so it
  is called out, though nothing could have depended on it except the defect. **The guard was checking the
  wrong object**: an empty manifest is dangerous because an applier reads it as "everything was removed",
  and the manifest an applier reads is `staged/manifest.json`, the full RELEASE. `CommitAsync`'s parameter
  is the CHANGESET, which a removals-only release legitimately leaves empty. The real danger is still
  refused, by the check that always owned it (`staged/manifest.json` must parse and list files).
  ⚠ The adjacent `FetchAsync_stages_nothing_when_already_up_to_date` LOOKED like coverage and is not: same
  manifest both sides means nothing to download *and* nothing to remove, so not-pending is right there.

- 🔴 **One failed `WebViewHost.InitializeAsync` no longer kills the host for the life of the process.**
  It caches its task to be idempotent, and a FAULTED task cached is a window that can never open again —
  while the error it hands back says *"start again"*. A Retry button re-awaited the same failure.
  **The timeout was the path that broke**, which is the one that message belongs to: its `catch` filter
  ran instead of the general handler and never cleared the cache, so a transient zombie-lock on the
  user-data folder — the exact failure the timeout exists for — was permanent. Now a faulted attempt is
  simply never handed back.
  ⚠ **The obvious fix does not work and a regression test caught it**: clearing the field from inside the
  sequence is too late when the failure happens before the first suspension, because the task completes
  before the assignment does — so the corpse is cached on the way out anyway. The cache is asked at CALL
  time instead.

- 🔴 **`useMediaPlayer` now reports when the page is HIDDEN, so backgrounding hands off the real
  playhead.** It reports on transitions only — deliberately, since `timeupdate` fires ~4×/second — which
  meant the host's believed position was whatever the last transition left. For steady playback that is
  **the moment playback started**, so `BackgroundPlaybackTransfer` handed the native player a position
  ~20 s stale and the user resumed from the beginning.
  Measured on an Android emulator, same procedure before and after: page at 19.79 s →
  `HANDOFF: TookOver at 0.01s`; with the fix, backgrounding at ~32 s → `HANDOFF: TookOver at 32.08s`.
  ⚠ The platform's `pause` at background time DOES fire, but not in time to cross IPC before the process
  is frozen — which is why `visibilitychange` is the signal and not `pause`. It costs ONE report per
  background, so `timeupdate` stays absent.

- 🔴 **A recovered mission no longer re-runs on every subsequent boot, forever.**
  `MissionScheduler.RecoverAsync` resubmits a durable record through `SubmitAsync`, which mints a **new**
  mission id — so the completed mission's cleanup removed the new id and never the recovered record's own.
  The old record survived, `LoadAsync` returned it again next boot, and the work ran again, indefinitely.
  Setting `Durable = true` on the rehydrated definition did not help: it added a second record under the
  new id, and only that one was cleaned up.
  - **The store grew without bound and the work repeated silently** — the unbounded version of exactly the
    loop `RecoveryPolicy` exists to prevent, reached through `Queued` instead of `Running`.
  - The record is now removed **after** a successful resubmit — never before, because durability is a
    best-effort overlay on execution, so a crash in that window costs a duplicate rather than a lost mission.
  - **Why it survived review:** the test covering this path asserted that the `Running` record was removed
    and said nothing about the `Queued` one, which is removed on a different branch. Nothing outside the
    test suite implements `IMissionQueueStore`, so the whole durability half had no real consumer.
- **One unrecoverable record no longer abandons the rest of the recovery pass.** `SubmitAsync` is not
  `async`, so an unusable rehydrated definition — a missing `Run`, an unregistered claim scope — threw
  **synchronously** out of the loop. Every later record was left unrecovered *and* unremoved, so the next
  boot repeated the whole thing. Such a record is now logged, dropped, and the pass continues.
- **`MissionExecution.MissionId` and `MissionRecord.MissionId` are documented as PER-PROCESS.** Both
  previously claimed the id was *"stable across a restart"*; recovery resubmits under a new one, so it
  cannot key state that must survive a restart. Use `MissionDefinition.Key` for an identity you chose.
  (No signature change — the docs now match the behaviour, which the fix above did not alter.)

- 🔴 **`app.UseMediaPlayer()` no longer throws when you follow the documented setup.** The pair
  `docs/guides/media.md` tells you to write — `builder.UseMediaPlayer(x => x.Access = new MediaAccessOptions
  { …, CacheRoot = "" })`, then `app.UseMediaPlayer()` — failed with
  `ArgumentException: options.Access.CacheRoot`. The blank cache root is how you ask for the free default
  under `Paths.DataArea("media")`, but that default was applied only inside the `IMediaPlayer` factory,
  while the mount reads the options directly and hands the still-blank value to `UseMediaConversion`, which
  rejects it. It is now applied by both phases from one owner.
  - **It fired for exactly the apps that followed the guide.** Resolving `IMediaPlayer` yourself first hid
    it; composing IPC did not (the dispatcher resolves its modules lazily). If you worked around this by
    naming an explicit `CacheRoot`, that still works and needs no change.

- **Windows: a resource response abandoned during startup or teardown leaked an OS file handle.**
  `WebViewHost` marshals the response build to the UI thread, and disposed the body only when
  `CreateWebResourceResponse` itself failed — so the two paths where that build never runs at all (the
  marshal declines because there is no handle yet or the control is gone, or it throws) dropped the body
  with nothing left to close it. Since 0.9.1 that body is lazy over a real `FileStream`, so the leak held a
  file handle until finalization, which on Windows also blocks deleting or moving the file being served.
  Both paths are races, so they arrive in bursts rather than singly.

- **Six shipped XML docs documented the wrong member, and one shipped none.** A declaration inserted at the
  top of a file adopts the doc block above it, so `MediaPlanOutcome` carried `ComputedRemuxExtensions`'
  entire design essay (leaving the call you write documented in five words),
  `UseMediaPlayer(IWebViewInterceptor, IServiceProvider)` shipped with no summary at all, and
  `MediaConversionPipeline`'s stranded copy still described the two-state claim check that three-state
  `Ask` replaced. Each is now attached to the member it describes, and `dev.mjs doc-drift` fails on any doc
  comment carrying two `<summary>` elements so it cannot recur.

- **`IWebViewInterceptor.Use` now states what blocking actually costs on mobile.** It said a blocking
  middleware stops the webview painting; on Android and iOS the shell resolves the pipeline synchronously on
  the main thread, so a middleware that `await`s anything without `ConfigureAwait(false)` **deadlocks the
  app** — and the symptom names nothing (the app stays alive and simply stops answering). The kit's own
  fragment repair did exactly this once and it blocked an adopter's bug for three days; the contract now
  says so where a middleware author reads it.

- 🔴 **SECURITY: an update manifest could write and delete files OUTSIDE the tree being updated.**
  `UpdateManifest`'s `files[].path` is the one input this kit takes from a remote server, and nothing
  validated it. A ROOTED path made `Path.Combine` discard the root it was combined with (and C++'s
  `std::filesystem::operator/` does the same), while a `..` segment escaped the ordinary way — reaching
  `File.Create` when staging, `File.Delete` when applying, and `fs::remove` in `Shenora.Launcher`, which
  may run elevated. Neither hash verification (it checks CONTENT, never the PATH) nor the staged-tree
  intrusion check could see it: an escaped file is not in the directory the check walks, and is then
  looked for at the same escaped location and found.
  - **Fixed in one owner per language.** `ManifestDiff.IsSafeRelativePath` refuses a rooted path or a
    `..` segment; `UpdateManifest.Parse` and `ManifestDiff.Compute` both reject such a manifest whole, and
    `UpdateStage` resolves every manifest path through `Path.GetFullPath` + `PathClaims.IsContained`
    instead of `Path.Combine`. The launcher's `parse_manifest` refuses the same shapes.
  - ⚠ **A poisoned BASELINE does not brick updating**: it takes the existing "no usable installed
    manifest — applying without removals" branch, so the app still updates and simply removes nothing.
  - **No action needed by an adopter** beyond upgrading, unless you generate manifests with absolute
    paths — which never worked correctly anyway.

- **`@shenora/react`: an async `fallback` no longer leaves a pending timer behind.** `invoke`'s
  development fallback raced the call against a timeout and never cleared the loser, so every call left
  a live timer for the full timeout (30 s by default) holding its closure. Dev-seam only — the real
  transport path always cleared correctly — and invisible to callers, but it kept timers pending in an
  app's test run.

- 🔴 **An EBML-laced Matroska block carrying a single frame is no longer misparsed.** The lacing header
  byte is *frames − 1* and EBML lacing codes exactly that many sizes — the last is always implied — so a
  one-frame block codes none. The reader read one anyway, consuming the frame's own first bytes as a
  length: usually the file was refused outright (the plan returned null and the computed-remux route
  declined, so the film simply did not play), occasionally the plan pointed at the wrong bytes. Xiph and
  fixed lacing were always correct; EBML was the one scheme with no test, and now has two.

- **Path containment is platform-correct, so it can no longer be wider than the filesystem.**
  `WebViewFiles.ResolveContained` (what authorises every file a PAGE can load) and `ZipExtraction`'s
  zip-slip fence compared case-insensitively on every OS. On Android, whose filesystem is
  case-SENSITIVE, an allowed root of `…/files/public` therefore also admitted `…/files/Public` — a
  different directory the app never allowed. Both now match `PathClaims.IsContained`, which had it right.

- **A notification batch is serialized once, not twice.** `NotificationPump` used to serialize every
  notification individually as a validity probe, discard the result, and then serialize the whole batch —
  `2N` serializations of app payloads on the IPC hot path in the case where nothing is wrong. It now
  serializes the batch and falls back to the per-notification pass only when that fails, so the isolation
  property (one bad payload never takes its batch down) is unchanged and only an actual offender pays.

- **A lifecycle hook that throws while stopping is logged instead of vanishing.** `ShenoraApplication.Stop`
  swallowed it with a bare `catch { }`, so "my cleanup did not run" had no diagnostic at all. It still
  never blocks shutdown.

- 🔴 **A Matroska track wrapped in Video-for-Windows now reports the codec its FourCC names, not `"vfw"`.**
  Matroska has native ids for h264, HEVC, MPEG-2, MPEG-4 Part 2, VP8/9 and AV1 — and for **everything else**
  it uses `V_MS/VFW/FOURCC`, with the real codec as a FourCC inside a `BITMAPINFOHEADER`. h263 has no native
  id at all, so a muxer has no other legal choice, and the kit named the WRAPPER.
  - ⚠ **Two adopter-visible consequences.** A `FAILED` event's `dropped` list changes: where it said
    `["vfw"]` — the name of a container CONVENTION, which no app can act on — it now says `["h263"]` or
    whichever codec is really there. And **a track that was declined may now CONVERT**, because the
    converter is finally asked about a codec it offers: an h263 clip that reported `dropped:["vfw"]` on an
    iPhone 17 Pro now converts to H.264 and plays.
  - ⚠ Families that arrive as a FourCC even though a native id exists (`DIVX`, `XVID`, `MP4V`, `FMP4`) all
    answer `mpeg4`, so one file does not report a different codec from another tool's output of the same
    content. A native id is never second-guessed by the private data, and an unknown FourCC still falls back
    to `vfw` — which is honest, since it is all the container said.
  - **Found by building a fixture rather than by reading code:** an h263 clip was made to prove iOS's
    picture conversion end to end, and it failed for a reason that had nothing to do with the converter under
    test.

- 🔴 **CONVERSION IS POLICY-BASED: what the kit CLAIMS is now separate from what the DEVICE can do.**
  `pipeline.Use(converter, claims)` declares the `(kind, codec)` pairs a converter offers — a list of the new
  `MediaStreamClaim` record struct — and `CanConvert` answers **claim ∩ device**: the declaration first,
  because it is free and a no there is final, then `IMediaCapability`. `pipeline.Claims` is readable without
  building a single codec, which is the cheap answer to *"what does this shell support?"* that nothing could
  ask before. ⚠ The claim-less `Use(converter)` overload is unchanged and still asked about anything, so no
  existing converter needs touching.
  - **Why:** `CanConvert` used to answer by CONSTRUCTING the converter's decoder and encoder on every ask,
    which fused two different questions and produced both failures in one evening — an over-claim (a promise
    made from an encoder alone, so the muxer failed *after* accepting a track and spending the walk) and an
    under-claim (a refusal of a codec that merely could not open a session without its file's ESDS).
  - **Overriding stays what it was:** later registrations are asked first, and declining in the converter
    still wins per stream. A claim only makes a NO cheap and the offer inspectable — deliberately not a
    second mechanism for the same job.
  - ⚠ **Nothing that worked stops working:** a converter registered with the claim-less overload is still
    asked about anything, *per registration* — absent is UNKNOWN, never NONE — and with no
    `IMediaCapability` the device half is answered exactly as before, by seeing whether a run starts.
  - **`IosMediaCapability` answers for VIDEO now**, having returned empty "rather than guessed". That gap was
    what forced the fused question: with no device answer for pictures, building codecs was the only way to
    ask. Probed once and cached, because a session costs a real codec instance and a device has few.

- 🔴 **`UseMediaConversion` remembers a source it CANNOT carry, instead of re-running the whole transcode
  once per retry, for ever.** Found on the iOS simulator: the sample's picture fixture failed six times in
  six seconds (missions m19–m24), because each poll of the `503` started a fresh conversion. The cost is
  not the retry, it is that `request.Dropped` is only populated AFTER the writer finishes — so discovering
  "this codec cannot be carried" costs a COMPLETE conversion every time: about a second on a fixture,
  minutes on a film, repeating for as long as the page is open.
  - ⚠ **Behaviour change adopters can see:** the second and later requests for such a source now answer
    **`404` rather than `503`**. A permanent "not ready" is what invites the retry loop, and the page has
    already been told what is wrong, by codec name, on the `FAILED` event the first attempt emitted.
  - ⚠ **Only DETERMINISTIC failures are remembered** — a dropped stream is a property of the file and
    re-running cannot change it. An IO error, an out-of-memory or a cancellation says nothing about the
    source and stays retryable, which is the same split `UseComputedRemux` already makes between
    `Unplannable` and `Failed`. The record is written BEFORE the `FAILED` event, so a page that re-requests
    the instant it hears cannot buy itself one more transcode.

- 🔴 **`Shenora.iOS` now SHIPS the whole Live Activity devkit — two of its four Swift files were missing
  from the package, which would have failed EVERY consuming iOS build.** The csproj packed
  `ShenoraLiveActivity.swift` and nothing else; that was correct at 0.10.0, when it was the only Swift
  file, and was never extended when the layout interpreter and the kit's generic views landed in this same
  band. `ShenoraBuildLiveActivityShim` compiles the layout for every app that references the package —
  unconditional since the 0.9.0 link defect — so a consumer would have hit
  `swiftc: error: no such file or directory` naming a path inside the nupkg, whether or not they used the
  feature. **Found before it shipped, and it is the 0.9.0 lesson exactly: only an app-shaped PACKAGE
  consumer resolves `buildTransitive/`, so this repo's own builds and every gate stayed green throughout.**
  The folder is globbed now rather than listed, and `LiveActivityPackagingTests` names the file if anyone
  lists them again.

- 🔴 **Reloading at a hash route now works on iOS as well as Android — an adopter writes nothing.**
  `location.reload()` at `/#/library` left iOS showing the PREVIOUS document forever (WKWebView keeps the
  page on screen when a provisional navigation fails, so the app looked perfectly healthy), and on Android
  a MAUI defect mapped the fragment into the asset name, 404'd, and produced the webview's error page.
  `Shenora.Mobile` serves the app's own document for a root-plus-fragment request on both shells, and
  DECLINES when the bundle cannot be read so an app serving its document another way is untouched.
  ⚠ The iOS half went unrepaired for three days because the Android repair read the bundle with a blocking
  `.GetAwaiter().GetResult()` inside the resource handler, which **deadlocks the iOS main thread** — the
  silence that followed was read as evidence the approach was wrong.

- 🔴 **`ServiceProvider.Dispose()` threw on any app holding a `MissionScheduler` — and D64 was about to
  make that every app.** Microsoft DI's SYNCHRONOUS dispose refuses a captured singleton implementing only
  `IAsyncDisposable`, so the documented `using var app = builder.Build(); app.Run();` threw once anything
  had resolved `IMissionScheduler`. `Dispose()` now cancels queued missions and signals shutdown **without
  awaiting in-flight bodies** — deliberately weaker than `DisposeAsync`, which still awaits them, because
  awaiting here would block whatever thread disposes, routinely the UI thread.
  **Prefer `await using var app = …` when a mission may be mid-write.**

- **Safe-area insets are re-read when the device ROTATES.** Rotation moves an inset to a different EDGE
  rather than resizing it, so a shell that reads once publishes the wrong SHAPE for the session.
  ⚠ **iOS was affected worse than the symptom suggested: it had never published a real inset at all** — its
  single read happened before layout and returned zeros, so an iOS app was laying out against its own
  default guess in every orientation. **If you set a `Default` that looked right, this is the fix that
  makes the real numbers arrive.**

- 🔴 **`Shenora.iOS`: the Live Activity shim was built once per APP instead of once per ARCHITECTURE**, so
  a DEVICE build linked the simulator's `x86_64` archive into an `arm64` app. The path used
  `IntermediateOutputPath`, which is not yet defined where a consumer imports the targets file, so the shim
  landed in the project ROOT; with no RID in the path, one architecture's `.a` satisfied another's
  incremental check. ⚠ **The symptom is a decoy** — `Undefined symbols for architecture arm64` is
  character-for-character the 0.9.0 packaging defect, and invites re-fixing something already correct.
  **If you hit this, delete any `shenora-liveactivity/` folder in your project root.**

- 🔴 **The iOS Live Activity devkit RENDERS ON A REAL DEVICE** (iPhone 17 Pro), both Island regions, with
  updates repainting. An earlier `### Known broken` block told adopters not to use it; its stated root
  cause did not survive checking, and `docs/guides/mobile.md` keeps the retraction visible.
    ⚠ **The lesson worth carrying, since it outlived its bug:** an update has three outcomes — accepted,
    applied and REPAINTED — and the app process can only observe the first two, so a repaint question can
    never be closed with a log line. `node devtools/dev.mjs mac island-watch` answers it by frame hash.
  - **The kit sets NO `staleDate`, so it makes no claim about an activity's content freshness.** A 60 s
    horizon lived on `update` (and not on `start`) for one day inside this band and is gone: `staleDate`
    tells the system when to mark content out of date for `context.isStale`, not when to repaint, so it
    declared every activity stale a minute after its last update — wrong for a status activity that
    legitimately does not change — while nothing in the kit read the flag. **Nothing to migrate:** 0.10.0
    shipped no horizon either, and an app wanting one writes its own SwiftUI views today.
  - 🔴 **An app-described layout reached the widget EMPTY, then WRONG.** The Swift mirror's
    `encode(to:)` was a stub — ActivityKit ENCODES the attributes to reach the widget PROCESS, so every
    region arrived as one unknown node. And the layout enums crossed as NUMBERS, so `Horizontal` fell back
    to `Vertical`: a plausible WRONG layout, which is worse than a blank one. Both halves are pinned by
    `LiveActivityMirrorTests`, sabotage-verified in both directions.

- **The Live Activity Swift shim no longer takes an `NSLock` inside an `async` context** — a warning in
  every adopter's build today and a hard ERROR in the Swift 6 language mode, in a file the adopter compiles
  but cannot fix.

- ⚠ **Adopter-visible: the runtime log tags follow the namespaces** — `[Shenora.Ipc]` →
  `[Shenora.Core.Ipc]`, `[Shenora.Media]` → `[Shenora.Modules.Media]`, `[Shenora.IO]` →
  `[Shenora.Modules.Update]`. Nothing asserts on them, but a log filter might.

- 🔴 **`UseFiles` no longer allocates a served file's whole window — on Android that was ALREADY every
  file, of any size.** `WebViewFiles.Read` did `new byte[count]` then `ReadExactly`, and under
  `WebViewRangeDelivery.Unsliced` (D44) the requested window IS the whole file, so every `UseFiles`
  response on that shell allocated the entire file whatever the `Range` header asked for — shipped since
  the middleware landed in 0.9.1, and independent of the media routes in front of it. The body is now
  `BoundedBodyStream`, a lazy window over a still-open `FileStream`: it closes the handle itself at its
  bound and tolerates a second close, because the two mobile shells disagree about who closes a response
  body (Android disposes `Content` at EOF, iOS never does). **No signature moved** — `ServeRange`'s seam
  already took a `Stream`.

  ⚠ **One adopter-visible consequence, shared with the computed-remux route: a mid-read IO failure now
  fails MID-RESPONSE instead of answering a clean 404**, because the status line and headers are already
  committed by the time the body is read. Measured on all three shells 2026-08-13, and they do not agree:

  | shell | what the page sees |
  |---|---|
  | Android | a failed load the page can observe. ⚠ That throw used to KILL THE PROCESS |
  | iOS | a committed `200` and a short body, silently |
  | Windows | the same as iOS — a silent short body |

  - ✅ **The kit now says so, which is the one thing it can do:** `BoundedBodyStream` reports a truncated
    body to the host log with the route and the byte count, so a silent short read is diagnosable from the
    host even where the page cannot see it.
  - 🔴 **So an adopter treats a mid-read failure as POSSIBLE on every shell** — verify a media load
    completed rather than assuming a `200` means a whole file arrived. Two of three shells give you
    nothing page-side.

## 0.10.0 — 2026-08-05

### Breaking

_Three shape fixes from a sweep of the whole public surface under **D47** (while one repo fully adopts the
kit, prefer the correct shape over the compatible one). All three are mechanical at the call site._

- **`FileDialogOptions` is split per method: `OpenFileOptions`, `OpenFolderOptions`, `SaveFileOptions`.**
  The base keeps the three fields every dialog takes (`Title`, `DefaultPath`, `RememberPathKey`); each
  derived type adds what only that dialog can honour. `IFileDialogs`' four methods take the matching type,
  so `new OpenFolderOptions { OverwritePrompt = true }` no longer compiles.

  It was one bag for all four methods with only XML tags saying which field applied where — survivable
  while the type was C#-only and a caller saw the tags in a tooltip, **not survivable now that a page names
  the same shape through `@shenora/react`**. The vocabulary stays unified, which was the point of a base
  rather than three unrelated types.
  - ⚠ `Filters` is on `OpenFolderOptions` too, honoured only when `AllowFileSelection` is set — the
    file-or-folder mode really is a file dialog underneath and really does filter. The split is what
    surfaced that; dropping it would have silently removed working behaviour. A field conditional on a
    SIBLING field is visible in one place, unlike one conditional on which method you called.

- **`IEventBus` subscriptions return `IDisposable` instead of a subscription-id string, and `Unsubscribe`
  is gone.** Assign the return value and dispose it; a subscription that was never released needs no
  change, because the id was already being discarded.

  ```csharp
  var id = bus.Subscribe("APP", "UPDATED", handler);   // before
  bus.Unsubscribe(id);

  var sub = bus.Subscribe("APP", "UPDATED", handler);  // after
  sub.Dispose();                                       // or `using`, or a field disposed in teardown
  ```

  **This was the kit disagreeing with itself.** `IWebViewInterceptor.Use` and
  `WebViewResourcePipeline.Use` already returned an `IDisposable` that removes the registration; the bus
  returned a string you had to remember to hand back, and `Unsubscribe`'s own contract *ignored* an id it
  did not recognise — so a typo or a double-release was a silent no-op. One library should have one answer
  to "how do I undo a registration".
  - It deleted a real leak rather than just tidying: `NotificationPump` had to hold BOTH the id AND a live
    reference to the bus in order to release, so a pump torn down after its bus had gone away leaked the
    subscription silently. That failure mode no longer exists to get wrong.
  - Double-dispose is safe and does not disturb other subscriptions — both pinned by tests, and both
    sabotage-verified.

- **`MissionSchedulerOptions.GlobalLaneCapacity` is now `int?`, where `null` means auto.** Previously `0`
  meant auto; now a value below 1 throws.

  ```csharp
  new MissionSchedulerOptions { GlobalLaneCapacity = 0 }   // before: auto
  new MissionSchedulerOptions { }                          // after: auto (or `= null`)
  ```

  It was **the last magic sentinel on the kit's surface** — every other option carries a real default and
  rejects nonsense (`LeaseTimeout` 30 s, `PollInterval` 50 ms, `MaxQueuedNotifications` 10 000; the IPC
  options throw rather than reinterpret). A sentinel makes one legal-looking value mean something else
  entirely, and what `0` actually describes is a scheduler that can never run anything.

- **`MissionSchedulerOptions.DefaultLaneCapacity` is renamed to `GlobalLaneCapacity`.** Rename the
  assignment; nothing else changes, and the value means exactly what it did.

  ```csharp
  new MissionSchedulerOptions { DefaultLaneCapacity = 8 }   // before
  new MissionSchedulerOptions { GlobalLaneCapacity = 8 }    // after
  ```

  **The old name is what caused the defect below.** It reads as "the default capacity a lane gets" and it is
  really the global CEILING over every lane, so the first adopter set it to 1 believing it was a per-lane
  default, gave a named lane 3, and got a lane that ran at 1 — with no way to discover that but to time the
  work. A doc paragraph can explain that; a name can stop it being written.

  **No compatibility alias was kept, deliberately.** A warning-level `[Obsolete]` leaves both names on the
  surface for years and the misleading one keeps getting written, which is the entire thing the rename
  exists to prevent. A compile error that names the new property is a better outcome than a warning nobody
  reads — the fix is one word, at every site, found by the compiler rather than by a measurement.

- **The file-operation engine LEFT `Shenora.Core` for a new package, `Shenora.IO` (D48).** Thirty public
  types change namespace `Shenora.Core` → `Shenora.IO`: `IFileUpdateQueue`/`FileUpdateQueue(+Options)`,
  `FileUpdate`/`FileChange`/`FileAtomicity`/`FileUpdateResult`, the journal set
  (`IFileUpdateJournal`/`FileUpdateJournal(+Options)`/`FileUpdateJournalEntry`/`FileUpdateStage`/
  `FileUndoStep`/`FileUndoKind`), the lease set (`IPathLocker`/`IPathLease`/`FilePathLocker(+Options)`),
  the manifest set (`UpdateManifest`/`ManifestFile`/`ManifestDiff`) and the updater
  (`UpdateStage(+Options,+Status)`/`UpdateOutcome`/`IUpdateSource`).

  ```xml
  <PackageReference Include="Shenora.IO" Version="…" />   <!-- add -->
  ```
  ```csharp
  using Shenora.IO;   // add to each file that names one of the types above
  ```

  **Nothing else changes** — no member was added, removed or resigned, which the API baselines show
  exactly: `Shenora.txt` lost 206 lines and gained none. An app that never mutates a file tree simply
  does not reference the package, which is the point: `Io/` was **34% of `Shenora.Core`** (2,244 lines) and
  `Shenora.Core` is what every other package references, so a phone app that hosts a page and plays a file
  was carrying a self-updater it will never call.
  - ⚠ **Three things deliberately did NOT move**, and the `using` you need depends on which you touch.
    `Files`/`FileReplacement` stay in `Shenora.Core` (`IFileDialogs.SaveAsync`'s default calls
    `Files.BeginReplace`, so moving them would invert the package edge); `PathClaims` stays (it is a claim
    SCOPE built on the mission types — scheduling vocabulary that happens to be about paths); and
    **`IFileLockInspector`/`FileLockHolder` stay**, because "who holds this file open?" is answered
    per-platform and is therefore a portable contract with a shell implementation, exactly like
    `IFileDialogs`. A shell must be able to implement a Core contract without referencing an optional
    feature package.
  - `Shenora.IO.Compression` now depends on `Shenora.IO` rather than on `Shenora.Core`, so
    `ZipUpdateSource`'s signatures name `Shenora.IO.UpdateManifest`/`ManifestFile`. A consumer that
    references `Shenora.IO.Compression` gets `Shenora.IO` transitively and needs no second reference.

### Added

- **Safe-area insets, published by the SHELL** — `SafeAreaOptions`/`SafeAreaInsets`/`SafeAreaScript` in
  `Shenora.Core`, `MobileSafeArea` in the mobile shells. Opt-in; an app that takes nothing keeps today's
  behaviour exactly.

  **The web platform's own answer is not sufficient on Android, measured on Android 16 / API 36:**
  `env(safe-area-inset-*)` reports the display CUTOUT only — never the system bars, so `bottom` came back
  0 on a device whose navigation bar is genuinely 24 CSS px tall — and reports **0 for the entire first
  page load**. Neither is fixable from the page: a re-read on `resize`/`visualViewport` was written and
  does nothing, because nothing changes within that document to observe.

  ```csharp
  new MobileSafeArea(webView, new SafeAreaOptions
  {
      Default = new SafeAreaInsets(24, 0, 24, 0),  // right at FIRST paint, not after the platform reports
      Color   = "#14161a",
      Settle  = TimeSpan.FromMilliseconds(180),
      Splash  = true,
  }, log);
  ```
  The page reads `var(--sa-top)` and friends, with `env()` as its fallback outside the shell.
  - **Every mechanism is individually declinable** (D21): the default, the colour, the settle animation,
    the splash and the variable prefix are each independent. The splash always carries a self-dismissing
    timeout — a page hidden forever is worse than the flash it hides.
  - **`SafeAreaScript.Build` is a pure function**, so the judgements — whether a zero measurement may
    overwrite a default, when the splash gives up — are unit-tested with no device (15 tests).
  - Verified on device at first paint: `top=48.762px bottom=24px color=#14161a` while `env()` still
    reported zero, including the bottom inset Android never exposes to CSS.

- **NEW PACKAGE `Shenora.Launcher`** — the prebuilt launcher that runs BEFORE your app and
  applies a staged update. It is the one part of staged updating that cannot be done in .NET: it runs
  when the runtime may be absent and must replace files the app holds open. **A self-contained app needs
  none of it** — `Shenora.IO`'s `UpdateStage.ApplyAsync` already applies updates in portable .NET.
  - Ships **prebuilt per-RID binaries** (`runtimes/win-x64|linux-x64/native/`) plus the **C++17 library
    sources and `main.cpp` template** under `launcher-src/`, so you can use the stock launcher — rename,
    re-icon and sign it — or build your own from the same library. What stays yours either way is small:
    the exe name, icon and version resources, the signature, four constants, and the failure-UI wording.
  - **Both binaries are built by the release itself**, on their own runners (MSVC and gcc), and each is
    conformance-tested against the real C# staging implementation before it can be packed. The publish
    job depends on that matrix, so a launcher that fails its tests stops the release rather than
    shipping.
  - **322 KB**, statically linked against the CRT so it needs no VC++ redistributable — a launcher that
    required one would have the bootstrap problem it exists to solve.
  - **It re-hashes nothing.** `ready.json` exists only when the staging side verified the whole stage,
    and the marker's meaning is that an applier need not re-check.
  - Gated by the release workflow's matrix on win-x64 AND linux-x64, running a conformance harness against the built
    binary, where every stage it applies is produced by the real C# implementation rather than a fixture.
    ⚠ `dev.mjs verify` does NOT compile it — this repo has no C++ toolchain and deliberately does not
    require one.

- **NEW PACKAGE `Shenora.IO.Compression`** — getting files into and out of an archive SAFELY. `net10.0`,
  no native engine, and the first member of the `Shenora.IO.*` family (D48) — file-operation work that does
  not belong in every consumer's `Shenora.Core`. It depends on `Shenora.IO`, which arrives with it.
  - **`ZipExtraction.ExtractTo` refuses any entry that would land outside the destination.** An archive is
    a list of paths chosen by whoever built it, and nothing stops one being `../../autoexec.bat` — the
    "zip slip" family. `ZipFile.ExtractToDirectory` has guarded this for years, but the hand-rolled
    `foreach` over `archive.Entries` that anyone writing progress or filtering ends up with does not, and
    neither does a native extractor unless it says so. **The donor this was harvested from has no check of
    its own** — it relies on its 7-Zip library's behaviour — which is exactly the gap
    `extraction-sources.md` says to fix during a port rather than carry.
    - A refused entry is SKIPPED and NAMED rather than throwing: one hostile entry is usually still an
      archive you want the rest of, and a caller who disagrees can treat a non-empty `Refused` as fatal in
      one line. Silently dropping it would hide an attack; throwing would deny the choice.
    - Size and entry-count LIMITS throw (default 1 GiB / 100k) — the zip-bomb bound. A partial extraction
      that stopped quietly would leave the caller believing it had everything.
    - Containment compares against the destination **plus a separator**, or `data-evil` passes as a child
      of `data` — the same prefix bug `WebViewFiles.ResolveContained` already documents. Sabotage-verified.
  - **Naming, recorded because the first attempt was wrong:** this shipped briefly as `Shenora.Archives`
    with `Archive…` type names, which over-claimed (everything in it is zip-only) and contradicted the
    kit's own lexicon note in the same file on the same day. Naming it after the framework's own area —
    `System.IO.Compression` — made the types SMALLER too (`ExtractionResult`, not
    `ArchiveExtractionResult`), because the namespace already says what they operate on. **A package name
    that has to be explained by its type names is the wrong package name.**

- **`ZipUpdateSource` — an `IUpdateSource` over one or more ZIP archives**, the release shape GitHub
  Releases encourages. The interface needed NO change to admit it: `OpenAsync(ManifestFile) → Task<Stream>`
  is exactly what a zip entry is, so this is a shipped implementation rather than a contract change. It
  turns "adoptable if you write the adapter" into "adoptable" — everything genuinely hard (staging, per-file
  SHA-256 verification, the journal, resume) was already on `UpdateStage`'s side, and the bridge is boring
  enough that several adopters would have written it identically.
  - **MULTIPLE archives, not one.** A release is commonly published as one zip PER PART with a single
    manifest spanning them, so a single-archive implementation would serve half a release. Entries are
    indexed across every archive at construction, and a path carried by TWO archives is refused rather than
    last-wins — which archive wins should never depend on the order they were passed.
  - **It does not download.** Where the archives come from stays the app's, for the same reason
    `IUpdateSource` ships no client: baking one in would drag an HTTP dependency into `Shenora.Core`.
  - ⚠ **A non-seekable stream is refused up front, naming the fix.** `ZipArchive` reads the central
    directory from the END of the file, so a live HTTP response fails with an unhelpful format error deep
    inside — download to a file or a `MemoryStream` first.
  - ⚠ **Not thread-safe**, because `ZipArchive` is not. Safe with `UpdateStage.FetchAsync` today because
    that opens files sequentially; parallelising that loop without a source per worker would corrupt reads
    rather than merely slow them, so it is stated on the type.
  - Paths normalise separators AND case, the same two rules `ManifestDiff` already learned: without the
    first a Windows-built manifest matches nothing in a zip forever, and without the second one letter's
    case turns a whole release into "not carried".

- **`interceptor.UseMediaConversion(scheduler, events, options)` (`Shenora.Media`) — serving media the
  platform cannot decode: convert once, cache the result, serve it with ranges.** It BUILDS nothing. Every
  hard part already shipped, and this is the composition: `IMissionScheduler` runs the long job without a
  thread of its own, `PathClaims.Exclusive` means one source converts once however many requests arrive,
  `MissionDefinition.Key` deduplicates the submissions, `Files.BeginReplace` makes the output atomic, and
  `DerivedCacheKey` keys on identity+length+mtime so replacing a source invalidates its conversion.
  - **The app supplies the engine** — `MediaConversionOptions.Convert` is a delegate. The kit ships no
    encoder and never vendors one (D42): the right one differs per app, and a bundled one is tens of
    megabytes every consumer pays for.
  - ⚠ **No probe and no codec policy in the options, deliberately.** Whether a source needs converting is
    the APP's decision, made before it builds the URL with the `MediaPlaybackPlanner` the kit already ships;
    a source that plays directly is pointed at `UseFiles` instead. Putting that decision here would mean
    launching a probe inside a webview callback, per request — the mobile interceptor resolves
    SYNCHRONOUSLY, so everything slow has to live in the mission.
  - **A cache miss answers `503` + `Retry-After` and starts the conversion.** The page is event-driven:
    `MediaConversionEvents.SourceProgress`/`Ready`/`Failed` ride the existing notification pipe, and the
    page sets its element's `src` on `READY`. Holding a webview callback open for a transcode is the
    alternative, and it is not one.
  - Failures cross as a TYPE name only, never exception text — the same boundary the IPC error contract
    enforces, because page script can read what it is told.
  - **`MediaConversionOptions.AllowRemoteSource` is the SSRF boundary (DM4), and it fails CLOSED twice
    over**: no policy refuses every remote source, and a policy that THROWS refuses too — a check that
    could not be completed is not a check that passed. The page picks the url and **the host can reach
    addresses the page cannot**, which is the whole asymmetry. Only `http`/`https` count as remote;
    anything else (`file:` above all) falls to the local branch and meets path containment instead of a
    policy written to think about web addresses.
    - **The kit authorises; it never fetches.** The app's engine reads the url — ffmpeg and friends open
      them natively — which keeps an HTTP client, and the credential/proxy/retry questions, out of the
      package. Synchronous unlike `NavigationGuard`'s async shape, because this runs on a resource path the
      mobile shells resolve synchronously: an async policy doing a lookup would block a webview callback on
      the network.
    - ⚠ A remote source is cached by its URL alone — nothing else is knowable without fetching it — so a
      url whose content changes at a fixed address will serve a stale conversion. Version your urls.

- **Native file dialogs are reachable FROM THE PAGE, on both sides of the wire.** The kit already had
  `ShellCapability.FilePicker`/`FolderPicker`/`SavePicker` in its vocabulary — three capabilities a shell
  advertises in the ready handshake — and shipped no way to use them, so every app wrote the same routes and
  then claimed the capability itself. This repo's own two samples had each done exactly that.
  - **`FileDialogFacade` + `services.AddShenoraFileDialogs()`** (`Shenora.Ipc`) — routes `OPEN_FILE`,
    `OPEN_FOLDER`, `SAVE_FILE`, `SAVE_TEXT` over whichever `IFileDialogs` the shell registered. Opt-in, like
    `AddShenoraOperations`.
  - **`FileDialogs` + `useFileDialogs()`** (`@shenora/react`) — the typed client, plus `canPickFile` /
    `canPickFolder` / `canPickSavePath` read from the handshake. **Use them to decide what to RENDER, not
    what to catch**: on a phone `canPickFolder` is false, so the button is never drawn.

    ```tsx
    const { dialogs, canPickFolder } = useFileDialogs();
    <button onClick={() => dialogs.openFile()}>Choose a file</button>
    {canPickFolder && <button onClick={() => dialogs.openFolder()}>Choose a folder</button>}
    ```
  - **`SAVE_TEXT` is the portable save** and works on every shell, because the HOST does the writing. It
    carries TEXT on purpose — the content crosses the IPC envelope, so anything large or binary should be
    produced host-side through `IFileDialogs.SaveAsync`, where it never enters a message.
  - **`IpcErrorCodes.CapabilityNotSupported` / `capabilityNotSupported`** — a refusal is not a fault, and a
    client must be able to tell the two apart. Built from the kit's own words plus the capability name,
    never from the caught exception's message.
  - Route names, the module name and all five wire shapes are pinned by `WireMirrorTests` against the TS
    source, both directions, sabotage-verified.

- **`useShellInfo()`** (`@shenora/react`) — what the host is and what it can do, from the ready handshake.
  ⚠ **This hook was referenced by two of this package's own doc examples for several releases and did not
  exist**; `bridge.shell` was the only way to read it, so anyone following the kit's own example wrote code
  that did not compile. It reads synchronously and does not re-render on a late handshake — the bridge's
  documented design, since a capability learned after layout is a visible flash — so await
  `bridge.notifyReady()` before rendering the tree that depends on it.

- **`FileDialogResult.Completed()` — success with NO addressable location, stated by name.** A dialog has
  THREE outcomes, not two, and the third is the one that surprises people: `SaveAsync` on both mobile shells
  returns `Success` with a null `FilePath`, because the bytes went to a content URI that is a revocable
  grant rather than something the app may reopen. **The contract did not say so** — `FilePath` was
  documented as "the picked location when `Success`", so an adopter writing `result.FilePath!` after
  checking `Success` had a null-reference waiting for them on a phone. The XML now states all three
  outcomes, and the mobile shells construct this outcome by name instead of open-coding
  `new() { Success = true }`, which read like a forgotten field rather than a decision.

- **`IMissionScheduler.GlobalLane` — the bound every mission draws from is now reachable, resizable and
  holdable.** It always bounded everything (design §3: "the default lane bounds total concurrency"), but it
  had no name and no accessor, so `MissionSchedulerOptions.DefaultLaneCapacity` was `init`-only and the
  bound could be chosen once at construction and never again. **That made a runtime capacity governor
  unbuildable in one direction:** it could throttle a named lane and could never restore it past the value
  picked at startup — a lane throttled once stayed throttled, as a permanent silent slowdown rather than a
  crash. Reported by the first adopter, whose governor throttles the gpu/cpu lanes under load and restores
  them when the machine goes idle.
  - Exposed as an `ILane` rather than as a bespoke setter, so `Hold()`/`Release()` work on it too — which is
    "pause the whole scheduler without cancelling anything", a capability the machinery already had and
    that could not be asked for.
  - `MissionScheduler.GlobalLaneName` (`"(global)"`) makes it addressable: `Lane(GlobalLaneName)` and a
    mission declaring that name both resolve to the **same instance**, never a decoy that would accept a
    capacity change and alter nothing. A mission naming it takes its permits **on top of** the implicit one,
    which is how a heavy mission counts double against the bound.
  - Additive; nothing breaks. Only an app that implements `IMissionScheduler` itself — which the kit does
    not expect — would need to add the member.

- **`ILane.EffectiveCapacity` — the width a lane can actually reach**, i.e. `min(Capacity, GlobalLane.Capacity)`.
  A lane set to 3 under a global bound of 1 runs at 1 while `Capacity` answers 3, and **nothing an app could
  ask distinguished that from a lane genuinely running at 3** — the only way to find out was to time the
  work. `Capacity` still reports what was REQUESTED rather than clamping, so a later widening of the global
  bound gives the caller the width they asked for instead of having silently discarded it.

### Fixed

- **`UpdateStage.CommitAsync` no longer publishes a marker for a stage `ApplyAsync` would refuse.** It now
  requires `staged/manifest.json` — the full release manifest — to be present, readable and non-empty,
  which is exactly what `ApplyAsync` requires to compute removals. `FetchAsync` writes that file; an app
  that stages by its own means had no way to know it must, and nothing checked.

  The marker's documented meaning is "an applier can act without re-checking", so it was promising more
  than it verified. **Where that failed is why it is now a guard:** `ApplyAsync` runs in the applier —
  typically a launcher, after the app has exited — so the refusal surfaced on next start with nothing
  running to report it. It is a CHECK and never a write: the manifest passed to `CommitAsync` is the staged
  *changeset*, while the file must be the *full release* manifest, so writing one into the other would tell
  the applier that every unchanged file had been removed from the release.
  - Found by `node devtools/dev.mjs update-probe`, new in this release: it drives the staged updater over a
    REAL directory tree (a `dotnet publish` output, or an adopter's own release) instead of a fixture. Six
    existing tests had asserted this stage was valid, each having built both sides of its own world.
  - Real-tree result, which is the other half of what the probe is for: **36 files, 0 would-be intrusions
    under the default policy** — `runtimes/*/native/` subtrees, `.pdb`s, `.xml` docs and `.deps.json`
    included. The default is not too strict.
- Twelve log messages in `Shenora.IO` still identified themselves as `[Shenora.Core]` after the package
  split.

_From a full review of the kit's non-code surface (2026-08-05). The correctness hot spots were clean; every
finding was in what a gate is structurally blind to — shipped package metadata, the npm barrel, and prose._

- **`@shenora/react` now exports `SubscribeOptions`**, the options type of all three
  `ShenoraEventBus.subscribe*` methods. It was reachable to CALL and impossible to NAME, so an app could
  not write a typed wrapper or a shared const around it. Identical to the `OperationProgress` gap the
  barrel already documents; the type-only pin in `index.test.ts` now covers it.
- **`Shenora.IO.Compression`'s NuGet description carried two errors and is rewritten.** It opened with
  "Shenora archives" — the retired name this package was renamed away from — and claimed "bounded
  recursion", which does not exist (zip entries are a flat list; the bounds are total bytes and entry
  count). A csproj `<Description>` ships to nuget.org and no gate reads it: the D22 domain-word audit
  sweeps the API baselines only.
- **Docs an adopter reads, corrected:** `README.md` said the three retired 0.5.0 package ids "carry a
  deprecation notice" — they do not, that action is still pending; `docs/RELEASING.md` told adopters
  `Shenora.Windows` pulls `WinForms`, a package that has not existed since 0.5.0, and framed pre-release
  consumption as being for "until the first public release".
- **`doc-drift` gained the retired PACKAGE IDS it had never watched** (every previous entry was a type
  name), which is what let the two items above survive. Two defects in the gate itself were fixed with
  them: its history heuristic could not match the repo's most common past-tense shape — ``was `X` `` — because
  `was ` was written with a trailing space followed by `\b`, requiring a word character after the space;
  and it scanned `devtools/_*` scratch directories, so its result depended on which throwaway consumers
  happened to exist locally. Six sabotage cases now pin both directions.

- **`OpenFolderAsync(AllowFileSelection: true)` returned the PARENT FOLDER for a real file named
  `Folder Selection.txt`.** Windows has no "file or folder" dialog mode — the Common Item Dialog picks
  folders or files, never both — so the kit types a placeholder name into an `OpenFileDialog` and reads it
  back. That read-back tested the NAME first (including `GetFileNameWithoutExtension`), so an existing file
  matching the placeholder was silently converted into its directory. A real file now wins: the placeholder
  can only mean "this folder" when nothing by that name exists.
  - Found by reading during the greenfield sweep, not reported — but it is the wrong-ANSWER class rather
    than a refusal, which is why it was worth fixing over a doc note.
  - The disambiguation is now a pure `internal static` with five tests, so the only decision in that dialog
    is reachable without opening one. Sabotage-verified: the old ordering fails both defect tests while the
    three must-stay-quiet cases keep passing.

- **Setting a lane's capacity above the global bound no longer does so silently.** It is still legal — a
  governor may widen a lane just before widening the bound, and neither order should be an error — but it
  now logs which value will actually apply and how to raise it. Nothing was wrong with the *behaviour*
  (`min(lane, global)` is what a global bound means); the defect was that it was undetectable.

- **Windows: `PlaybackInfo.Duration` was accepted and then DROPPED, so the OS never learned the track
  length.** Reported by the first adopter on the desktop adoption and reproduced exactly: title, artist,
  album, status, position and the whole control set read back correctly from Windows' own
  `GlobalSystemMediaTransportControlsSessionManager`, while `EndTime` was `00:00:00` for a track published
  with `Duration = 240s`. The flyout therefore had no total to draw its scrubber against — while
  `IsPlaybackPositionEnabled` was advertised, so the OS offered seeking on a timeline whose end it did not
  know.
  - `Publish` drove only the `DisplayUpdater` and `Report` built a timeline with no `EndTime`; nothing
    carried the duration between the two calls, so it could never be anything but zero. The session now
    remembers it, and `Clear` (and a `Publish` with no duration) resets it, so a new item cannot inherit the
    last one's length.
  - **`EndTime` AND `MaxSeekTime`**, not just the first: one is what the flyout draws against, the other is
    what bounds a drag, so setting only `EndTime` renders a length the user is not allowed to reach.
  - A position past the end is CLAMPED rather than passed through — SMTC rejects an out-of-order timeline
    wholesale, which would lose the duration as well as the position, and a position a tick past the end is
    ordinary at the moment a track finishes. Unknown and non-positive durations still leave the end at zero,
    which is what a live stream needs.
  - **The gate that should have caught it now exists.** The desktop sample's `PlaybackSessionProbe` had
    published a 240 s duration since the day it was written and never asserted the timeline; it now reads
    `EndTime` back out of the OS. Verified live: `pos=00:00:42|end=00:04:00`.

- **Android: a PAUSED session advertised `speed=1.0`, so the lock-screen scrubber drifted.**
  `MobilePlaybackSession` forwarded `PlaybackProgress.Rate` verbatim into
  `PlaybackState.setState(state, position, speed)` (measured on Android 12 via `dumpsys media_session`),
  while the iOS session already derived it from `State` — one portable contract producing two behaviours
  from identical input. A controller extrapolates the displayed position as `position + elapsed × speed`, so
  a paused session claiming 1.0 walks away from audio that is not moving. The speed is now derived from the
  state on Android too. **Apps do not need to zero `Rate` when pausing** — the adopter's workaround (lying
  about its own rate to satisfy one platform) can be deleted.

- **Android: every intercepted response carried a DUPLICATED `Content-Length`, whose first value was `0`.**
  A file served through `UseFiles` came back as `content-length: 0, 1102544` — an invalid HTTP message
  (RFC 9110 §8.6: two differing values), and a consumer taking the first reads the payload as empty.
  Reproduced on the sample and attributed on a device with a route that varied only which headers the kit
  supplied: **MAUI's Android intercept path always emits a `Content-Type` and a `Content-Length: 0` of its
  own AND passes our dictionary through as well** — a custom `X-` header arrived exactly once in every
  variant, so this is those two fields being re-derived, not blanket duplication. The kit no longer sends
  `Content-Length` on Android; the platform ignores both and delivered the complete body in every variant.
  - ⚠ **`Content-Type` still arrives twice on Android and that is deliberate.** MAUI reads it out of the
    dictionary to set the native mime type and then hands the dictionary over too, and there is no
    `SetResponse` overload taking a content type *alongside* headers — so the only way to avoid the repeat
    is to send none, which yields `application/octet-stream` and no `<video>` will touch that. Both values
    are identical, so nothing can be misled about the type.
  - **Android only**, deliberately: iOS builds an `NSHTTPURLResponse` through different platform code, has
    not been measured for this, and AVFoundation is the pickiest consumer the kit has (D44).
  - D44's behaviour is now a GATE rather than a human reading log lines: the MAUI sample loads both clips —
    including the one whose mp4 index sits at the END, which cannot open unless a tail range is answered
    correctly — and asserts each resolves a duration and seeks. Verified after the change:
    `duration=60.00|seeked=48.00` for both.

### Changed

- **`PlaybackProgress.Rate` now documents what each shell does with it**, because it was not discoverable
  from the types. An app never has to zero it when pausing (every shell derives the published speed from
  `State`), and ⚠ Windows cannot carry a rate at all — `SystemMediaTransportControls` has no speed field, so
  a 1.5× audiobook reads as normal speed there. This is the third finding of the shape "one shell silently
  ignores a field the contract offers", after the paused rate and the skip interval.

## 0.9.1 — 2026-08-04

### Fixed

- 🔴 **`Shenora.iOS` 0.9.0 could not be linked by an app that did not enable the Live Activity devkit.**
  Five undefined symbols at link time (`_shenora_activity_*`). If you are on 0.9.0 and hit this, the only
  workaround was to enable the devkit; rolling back does not help, because `IPlaybackSession` is new in
  0.9.0 and 0.8.0 has no iOS lock screen at all. **Reported by the first adopter and reproduced exactly.**
  - `[DllImport("__Internal")]` is resolved at STATIC LINK time, and the library carrying those symbols
    was only built when `ShenoraLiveActivityViews` was set. The shim is now built **unconditionally**;
    only the widget stays opt-in, because only it needs the app's own SwiftUI views.
  - Runtime lookup was tried first and does not work: removing the `DllImport` removes the only reference
    to the symbols, and nothing retains them. Measured — the archive held all five while the app binary
    held zero. Neither `ForceLoad` nor `-u` via the linker args changed that.
  - ⚠ `ILiveActivities.Unavailable` no longer claims it can report a missing shim. It never could: that
    was a link-time failure being described by a runtime property. It now reports what it can actually
    observe — the OS version, the user having switched activities off, or a failed call.
  - **The gate that should have caught this now exists.** `dev.mjs mac build` also builds the sample
    WITHOUT the opt-in, because the one iOS app this repo builds was the single configuration that
    worked. Sabotage-verified in both directions.

- **Android: the session token is exposed** (`MobilePlaybackSession.SessionToken`). The kit documented
  "the kit owns the session, the app owns the notification", but a `MediaStyle` notification binds to a
  session BY TOKEN and none was reachable — so the app's half of that split could not be written at all.
  Android-only on purpose: the type is `Android.Media.Session.MediaSession.Token`, and putting it on the
  portable contract would drag a platform type into `Shenora.Core`.

### Added

- **`IPlaybackSession` gains SKIP-BY-INTERVAL** — `PlaybackCommands.SkipForward`/`SkipBackward`,
  `IPlaybackSession.SkipInterval` (default 15 s) and `PlaybackCommandRequest.Interval`. Additive; nothing
  breaks.
  - **Filed by the first adopter the day 0.9.0 shipped.** An app with LONG-FORM audio — an audiobook, a
    podcast, a lecture — could not offer the one transport control that shape of content wants: `Next` is
    the wrong granularity when a track is fifty minutes long, and `Seek` is a scrubber rather than a
    button. They had it working and gave it up to adopt the kit, which is the trade the kit must not force.
  - **The interval is stated once, not per press**, because that is what the platforms take — and on iOS
    `PreferredIntervals` is also what makes the control DRAW the number rather than a bare arrow. Keep it
    to a value the platform UI is designed around; 15 s is the near-universal default.
  - It rides the request as well, because iOS sends its own interval with the event and honouring what
    arrived beats assuming what was asked for. Android and Windows send none, so the configured value is
    supplied — a handler can always just use it.
  - ⚠ Windows maps these onto SMTC fast-forward/rewind, which is the closest it offers and is an honest
    approximation rather than an exact match.
  - Verified against the OS registries: Android `actions=894` — exactly the previous `822` plus
    `ACTION_FAST_FORWARD` and `ACTION_REWIND` — and Windows reading back `ff=True|rw=True` from
    `GlobalSystemMediaTransportControlsSessionManager`.

### Changed

- **`ADOPTION.md` documents what a MAUI shell's page ORIGIN means for a server-backed app**, which cost
  the first adopter a day. `HybridWebView` serves the bundle from a synthetic SECURE origin —
  `https://0.0.0.1` on Android, `app://0.0.0.1` on iOS, both measured — so a plain-`http` backend is
  blocked as mixed content, and once that is relaxed the response is withheld by CORS instead. Both
  present as the same bare `TypeError: Failed to fetch`. Neither is a kit defect and neither needs an API;
  the doc states the origins (the iOS one is not otherwise discoverable), the Android relaxation and why
  it is the app's call, and the caveat that a non-standard scheme may present as `Origin: null`.

## 0.9.0 — 2026-08-04

### Added

- **Resource interception, in `Shenora.Core` and implemented by every shell (D45).** How a page gets bytes
  the platform will not hand it — and the answer is not media-shaped, which is why it is here. A page cannot
  reach a local file on ANY of the three shells (`file://` is refused from a virtual-host origin, and it
  would be the wrong answer anyway — it hands the page the whole filesystem), so serving local content is
  interception everywhere, and local files, generated images, exports and thumbnails are all the same
  problem. Building it around media would have meant breaking it to admit the second consumer.
  - **`IWebViewInterceptor`** — `RangeDelivery` plus `Use(middleware)` returning an `IDisposable` that
    removes the route. **A MIDDLEWARE pipeline, not a handler list**, because the cross-cutting concerns are
    the point: containment, a cache, a metric, a log of what a payload decoded to — each WRAPS the next
    rather than terminating, and expressing them separately is what stops every route re-implementing them.
    The kit already made this choice once, for messages: `IMessageDispatcher` is this shape over one
    transport.
  - **`WebViewResourcePipeline`** holds the registry and the composition, ONCE, for all three shells — the
    back-to-front chain build (so route 0 runs first), the copy-on-write array read on a platform event
    thread, and removal by reference identity so two registrations of the same method group are independent.
    All of it unit-testable with no webview, which is the reason it is not hand-rolled per shell.
  - **`WebViewRangeDelivery.Sliced`/`Unsliced`** names D44's measured asymmetry as a property of the
    INTERCEPTION rather than of the content: Android's webview applies the `Range` start to whatever body it
    receives, WebView2's and iOS's send it verbatim. It is read off the interceptor, never configured — a
    value copied from another shell would serve correct-looking bytes at the wrong offset, which plays every
    faststart file perfectly and fails every file whose index sits at the end.
  - **`interceptor.UseFiles(new WebViewFileOptions { AllowedRoots, Resolve })`** is the whole recipe for
    letting a page load local files, and the same three lines compile on all three shells. The app owns its
    route and its roots; the kit owns containment, ranges, content types and the platform's delivery rule.
    Fail-closed throughout: no roots means nothing is servable, `..` is refused *before* the filesystem is
    touched, roots are compared with a separator appended (without which `/media-evil` passes as a child of
    `/media`), and every refusal is the same 404 as a missing file so nothing can probe for existence.
  - **`WebViewContentTypes` is now public and answers media types.** It had none — an `.mp4` got
    `application/octet-stream`, which no `<video>` will touch. `.mkv` and `.avi` are named deliberately so
    the element decides rather than the map pre-refusing.
  - **`DerivedCacheKey`** (identity + length + mtime, never a path alone) keys anything derived from a
    source file. All three surveyed implementations reached that independently: a path-only key survives an
    overwrite, and then yesterday's conversion is served for a file the user has replaced.
- **`WebViewHost.Interceptor` — the desktop implements the same contract.** Available from construction, so
  routes are registered where an app composes everything else. Wired into the host's ONE
  `WebResourceRequested` subscription rather than a second one (two handlers assigning `args.Response` is
  last-writer-wins by subscription order), and it shares the page's own origin with the packaged bundle: a
  path the bundle *does* contain is still served synchronously and inline — the main document never reaches
  the deferred path — while a path it does not falls THROUGH to the pipeline instead of 404ing. In
  development an extra filter is registered for the dev-server origin, because that is where the page lives
  then; without it a route would work in a packaged build and 404 through every day of development.
  `DeferredSchemes` is unchanged and stays for what it is good at: a whole custom scheme of the app's own.
- **`Shenora.Media` (`net10.0`) is media LOGIC only, and is not needed to play a file.** It holds decisions,
  not plumbing, and depends on nothing: every type in it is a pure function over its own data. Its own
  package because a demuxer or an image codec is real shipped bytes and *everything* references
  `Shenora.Core`, so an app that never touches media should not pay for one (D40). What it adds on top of
  the interceptor is the DECISION about a file the platform cannot decode — probe it, remux it, transcode
  it — as a further middleware.
  - **`MediaPlaybackPlanner`** — container + codecs → `Direct` / `Remux` / `Transcode` / `Unsupported`,
    **per STREAM rather than per file** (D42). The frequent real failure is not "this will not play", it
    is *picture with no sound*: H.264 video that decodes perfectly beside AC-3 audio that does not,
    because licensed audio is absent from some platforms' mandatory sets. A `CanPlay(file) -> bool` is
    wrong in exactly that case, and throws away the cheap fix — copy the picture, re-encode only the
    sound. Pure and I/O-free, so it is unit-testable the way `ManifestDiff` is.
  - **`MediaProbeResult` / `MediaStreamInfo`** — the planner's input, best-effort and all-nullable. Both
    surveyed implementations admit the same thing in their own types; a probe is an external tool that may
    be absent, and code treating a null here as an error fails on files that play perfectly.
  - **`MediaPlaybackPolicy` carries the codec sets, and the kit ships NO default.** There is no correct
    universal list — a browser's differs from an engine's, and Android's differs per DEVICE because codec
    support is vendor-declared. A baked-in list would be one app's guess frozen into everyone's planner.
    The mechanism is the kit's; the policy is the application's.
- **`MobileWebViewInterceptor`, in `Shenora.Android` and `Shenora.iOS`** — one shared source is both
  shells' implementation, over MAUI's `HybridWebView.WebResourceRequested`. The only thing that differs is
  `RangeDelivery`, and it differs because the platforms genuinely do; a platform that declares neither fails
  **`#error` at compile time**, so a fourth shell cannot silently inherit a guess — the same fail-closed
  choice as the `partial` method that stopped a fourth shell shipping an undefined save.
- **Six package ids, not eight: `Shenora.Media.Android` and `Shenora.Media.iOS` were never released and no
  longer exist.** They were written, ran on a device, and then turned out to be the wrong layer:
  everything in them was interception rather than media, and the range-delivery rule they existed to carry
  is a property of the webview. Their content is now the shell interceptors plus `WebViewFiles`, so the
  capability shipped and the two packages did not.
  - ⚠ **The remote-source (SSRF) policy seam went with them and is deliberately NOT in this release.** It
    was a fail-closed guard for "may the host fetch this URL on the page's behalf" — real, and with no
    caller once serving moved: nothing in the kit fetches a remote resource for a page. It comes back with
    the middleware that does, rather than shipping as a public type with no consumer (D15). Its reasoning
    is worth keeping: the host can reach addresses the page cannot, so a *throwing* policy must deny.
- **`IPlaybackSession` — the OS's media transport surface, as a portable contract** (`Shenora.Core`), plus
  the desktop implementation (`WindowsPlaybackSession`, registered by `UseWinForms`). This is the lock
  screen, the media flyout, the headphone gesture and the car stereo: `Publish(PlaybackInfo)` /
  `Report(PlaybackProgress)` / `Clear()` go app → OS, and `CommandReceived` comes back the other way.
  - **`Shenora.Windows` now multi-targets `net10.0-windows` and `net10.0-windows10.0.17763.0`, and NOTHING
    BREAKS.** Existing consumers change nothing and keep their Windows 7-era floor. The second TFM exists for
    this one capability: `SystemMediaTransportControls` is WinRT, and the WinRT projections only exist when
    the TFM names a Windows SDK version — with a bare `net10.0-windows`, `Windows.Media` is not a namespace
    at all (measured: `CS0234`). An app that wants Now Playing on the desktop retargets to
    `net10.0-windows10.0.17763.0`; everyone else is unaffected.
    - On the plain TFM the type still EXISTS and **refuses by name at construction**, with the one-line fix
      in the message. Absent would have been worse: resolving a missing service names neither the shell nor
      the reason (`ShellCapability`).
    - 17763 is Windows 10 1809, the lowest ref pack .NET offers. **The SDK version in a Windows TFM is only
      the switch that turns the WinRT projections on — it is not a feature level you opt into**, so pick the
      lowest that compiles rather than the newest installed. This briefly shipped as 19041 purely because
      that was the oldest pack on the build machine.
    - ⚠ **The compile-against and run-on versions are separate, and only one is in the TFM.**
      `TargetPlatformVersion` (from the TFM) is what you may compile against;
      `SupportedOSPlatformVersion`/`TargetPlatformMinVersion` is the floor you run on — and **leaving the
      latter unset silently defaults it to the former**, which is how bumping a TFM for one API quietly
      raises the minimum Windows every consumer needs. This package had exactly that defect for one commit.
      It is now pinned (and matched on `-windows10.` rather than an exact TFM string, so a future bump cannot
      slip past it), with `CA1416` — a build error here — forcing any newer API to be guarded instead.
  - **It is two-way, and the return direction is the design.** Commands arrive from outside the app, so
    this is an event source as much as a publisher — and the kit deliberately ships no queue model behind
    it, because only the app knows what "next" means.
  - **The fields are `Title` / `Subtitle` / `GroupName`, not `Artist` / `Album`.** This contract lives in
    `Shenora.Core`, which every package references, so music vocabulary here would put those words on the
    surface of an app that has none — the same reasoning that keeps `Shenora.Media` separate and optional
    (D40/D45). The generic names are also honest: the same three fields carry a podcast's show and episode,
    an audiobook's book and chapter, a lecture's course.
  - **`Report` is for jumps, not for a timer.** All three platforms take a position plus a rate and
    extrapolate the displayed time themselves, so a host pushing the position every 250 ms is spending
    battery and IPC to tell the OS what it already worked out. Call it on seek, pause, resume, rate change
    and track change. A *delayed* report is worse than none, because the platform treats it as current.
  - **`Buffering` is its own state** — two of the three platforms have one, and folding it into `Playing`
    makes the OS extrapolate a position that is not moving.
  - ⚠ `CommandReceived` fires on a platform thread, **not** the UI thread on Windows. Marshal with
    `IUiDispatcher`. A throwing handler is caught and logged rather than escaping into a native callback.
  - Verified against the real OS, not asserted: the desktop sample's `PlaybackSessionProbe` publishes a
    known item and reads it back out of Windows' own `GlobalSystemMediaTransportControlsSessionManager`,
    asserting the title, subtitle, group and a `Playing` status. Sabotage-verified — dropping the
    `DisplayUpdater.Update()` call leaves our session visible with an *empty* title, which the probe
    distinguishes from having no session at all.
- **`IPlaybackSession` on the mobile shells too** (`MobilePlaybackSession` in `Shenora.Android` and
  `Shenora.iOS`) — one name, two entirely separate bodies: Android registers a platform `MediaSession`, iOS
  writes `MPNowPlayingInfoCenter` + `MPRemoteCommandCenter`. The same three calls now publish to the lock
  screen on all three platforms.
  - Verified against each OS's own view rather than the app's claim. iOS: Apple's `mediaremoted` logged
    `setting nowPlayingItem` for our bundle id with every field intact — title, artist, album,
    `Duration = 240`, `ElapsedTime = 42`, `PlaybackRate = 1`. Android: `dumpsys media_session` reported
    `active=true`, `state=3`, `position=42000`, `speed=1.0`, all three metadata fields, and
    **`actions=822`** — which decodes exactly to the requested set (512 `PLAY_PAUSE` + 256 `SEEK_TO` +
    32 `SKIP_TO_NEXT` + 16 `SKIP_TO_PREVIOUS` + 4 `PLAY` + 2 `PAUSE`, and no `STOP`, which was not asked
    for). That bitmask proves the whole flags mapping arithmetically.
  - ⚠ **A session makes an app CONTROLLABLE; being VISIBLE is separate, and it is the app's.** Android needs
    a MediaStyle notification and iOS an active `AVAudioSession`; both mean choosing icons, channels,
    categories and interruption behaviour, which are app design decisions rather than the kit's (D13).
    Everything else — metadata, state, offered actions, hardware button routing — works without them.
  - iOS has **no** playback-state property to set: `MPNowPlayingInfoCenter.playbackState` is macOS/tvOS only
    and absent from the iOS binding, so the RATE carries the state and Paused/Stopped/Buffering all report 0.
    All three shells agree that `TogglePlayPause` also lights the concrete play and pause controls, because
    hardware sends whichever it likes.
- **The Live Activity devkit (iOS)** — `ILiveActivities` + `LiveActivityState` in `Shenora.Core`, the
  ActivityKit implementation in `Shenora.iOS`, and **the whole adoption is one MSBuild property plus four
  SwiftUI view bodies**:

  ```xml
  <ShenoraLiveActivityViews>Platforms/iOS/IslandViews.swift</ShenoraLiveActivityViews>
  ```

  No lifecycle Swift, no extension `Info.plist`, no `.xcodeproj`, no codesigning. The package ships the
  ActivityKit shim, the state mirror and an MSBuild target that compiles the widget from its Swift plus
  yours, then hands it to the iOS SDK's own `AdditionalAppExtensions`/`NativeReference` to be embedded and
  re-signed. Recipe and traps in `ADOPTION.md`.
  - **You cannot avoid writing Swift and the docs say so.** A Live Activity's UI *is* a SwiftUI view in a
    widget extension — an OS requirement, not a .NET limitation — and it is your design system anyway,
    which the kit does not ship (D13). What the kit removes is everything around it.
  - **The Swift is shipped as SOURCE, and that is forced.** ActivityKit pairs an activity with a widget by
    its `ActivityAttributes` TYPE, and a Swift type's identity includes its MODULE — so the attributes must
    compile into the same module as your views. No prebuilt binary can satisfy that.
  - **A C#⇄Swift mirror tripwire**, because drift between the two state shapes fails completely silently: a
    renamed field decodes to nil, the activity does not appear, and no exception, log line or build warning
    is raised anywhere. It also catches the subtler half — a non-optional Swift property fails the WHOLE
    decode, since C# omits nulls. Sabotage-verified five ways.
  - **`Unavailable` returns a REASON, not a bool** (OS too old, switched off in Settings, shim not linked),
    and Android registers an implementation that answers with one rather than throwing — so portable logic
    asks and branches instead of catching. Android's own live surface is deliberately unbuilt: for media it
    is already `IPlaybackSession`, and a progress notification means choosing icons and channels (D15/D13).
  - Verified end to end on the simulator: `pluginkit` registered the extension, `liveactivitiesd` reported
    `Starting activity … state: active`, and `chronod` launched the widget through ExtensionKit to render
    it. ⚠ The Island itself stays blank on a simulator — an activity there reports only a lock-screen scene
    target — so seeing the pill needs a device. `dev.mjs mac activity` reports all three from the OS's own
    records.
- **A release now FAILS when `## Unreleased` is missing or has no entries** (`dev.mjs changelog`). Nothing
  in a package changes; this protects the *next* release. It used to warn and carry on, which is exactly
  how **v0.6.0 published 0.5.1's code**: the work was committed locally and never pushed, so the workflow
  released the remote's tree, bumped the version correctly, found nothing to stamp, and shipped with no
  changelog entry at all. The empty section was the signal, and it was there and unused. The message points
  at the likelier cause first — *check that the commits you mean to release are on the remote* — and there
  is no override flag, because the escape hatch is writing one bullet and any other one would get used.
  Also: `doctor` now rejects a tracked filename outside printable ASCII, and the stray 0-byte file with a
  Private-Use-Area name (a mangled shell redirect, committed in `11e3469`) is deleted. Both
  sabotage-verified in both directions, the quiet direction included.
- **`UpdateStageOptions.BaselinePath` — the baseline manifest no longer has to live inside the tree being
  updated.** Null (the default) is `{installRoot}/manifest.json`, so nothing changes for an app install,
  where the baseline genuinely belongs with the thing it describes. A relative path resolves against the
  install root; a rooted one is used as given.

  **Filed by the first adopter, and it was blocking the adoption outright.** Their targets are deploy
  INPUTS, not install trees: two directories whose aggregate content hash decides what gets re-uploaded,
  hashed with no exclusions on purpose so the figure agrees with the build's own manifest. A per-release
  `manifest.json` inside such a tree changes that hash on every release even when the payload is
  byte-identical — so *"did the backend actually change?"* answers yes forever, and a frontend-only change
  stops taking the seconds-long path and triggers a full cloud reconcile. That breaks a documented
  invariant there (a part's content is a pure function of SOURCE, never of build HISTORY), so nothing else
  about the kit's staging mattered until this moved.

  `ApplyAsync` now writes the baseline **explicitly** and always excludes it from the overlay, rather than
  letting it ride along because the stage happens to contain it and the destination happens to match.
  That keeps the configured and default cases on one code path — the alternative was a containment test
  that would have left a stray copy at the default location whenever the baseline was configured anywhere
  else, including *inside* the root under a different name. It appears in `UpdateOutcome.Written` only when
  it really landed in the tree, and a baseline that cannot be written logs loudly instead of throwing: the
  payload is already overlaid at that point, and a missing baseline degrades to "compute no removals next
  time", which is the safe direction.
- **`@shenora/react` gains `mediaUrl(payload, route?)`** — the page's half of a file route, and the reason
  it is shipped code rather than a documented convention. It returns a **relative** URL on the page's own
  origin (`media?<base64url>`), which D44 measured to be the ONE form intercepted on all three shells:
  `app://` is intercepted on both mobile shells but media-refused on Android, and an `https://<virtual-host>`
  URL works on Android and is not intercepted on iOS at all. `encodeMediaPayload`/`decodeMediaPayload` are
  exported for anything that needs the halves separately.
  - Sabotage found live: the MAUI sample page hand-rolled this encoding for one commit and immediately
    drifted from the host's route (`/video?` vs `/media?`), which surfaced only as a
    `MEDIA_ELEMENT_ERROR: Format error` on a device. The sample now imports the SHIPPED function, so the
    proof path is the published one.
- **A `localFiles` shell capability** joins the ready handshake, so ONE web bundle can tell whether the shell
  it is talking to can serve local files instead of sniffing the platform (A7's rule applied to D45).
- **The desktop interceptor is proven through a real WebView2, not asserted.**
  `samples/Shenora.Sample.Desktop`'s `InterceptorProbe` registers a file route and fetches through it from
  inside the page, asserting `206` + `Content-Range` + the body at a **non-periodic** offset (`bytes=3-7` →
  `DEFGH`), `Accept-Ranges: bytes`, a whole-file `200`, `416` for an unsatisfiable range, `404` for a
  traversal attempt at a file that really exists, and that the packaged bundle still wins on the origin it
  now shares. Sabotage-verified both ways: flipping `RangeDelivery` to `Unsliced` fails the probe naming
  what it read (1000 bytes starting at `A`), and that failure IS the measurement — WebView2 did not apply
  the offset itself, so it delivers sliced bodies.
- **The D41 media tripwire is ARMED rather than described.** `samples/Shenora.Sample.Logic` (a `net10.0`
  project) now references `Shenora.Media` and its facade uses the planner, so "app logic names
  `Shenora.Media` and never `Shenora.Media.{Platform}`" is enforced by the build. Sabotage-verified: a
  platform reference there fails `NU1201` by name, and cascades to the MAUI sample too, because the same
  portable logic feeds both mobile shells.

## 0.8.0 — 2026-08-03

### Breaking

- **`WebViewResourceRequest`, `WebViewResourceResponse` and `WebViewByteRange` moved from
  `Shenora.Windows` to `Shenora.Core`** (namespace `Shenora.Windows` → `Shenora.Core`).
  `WebViewDeferredScheme.Handler`'s signature now names the Core types; the member is otherwise unchanged.

  **Migration: add `using Shenora;` to files that name these types.** That is the whole fix, and it
  was measured rather than asserted — the move broke exactly three files in this repo (one sample, two test
  files) and each needed exactly that one line. Code that already has both usings does not change at all.

  **Why:** these three types describe a resource exchange between a host and a page — "URI plus headers in,
  status plus content-type plus a stream out" — and nothing about that is Windows-specific. They sat in the
  Windows package only because it was the one shell when they were written. MAUI's `HybridWebView` turns
  out to have a request-interception seam in .NET 10, so the mobile shells can serve dynamic, seekable
  content too, and `src/Shenora.Mobile/` cannot reference `Shenora.Windows`. Portable contracts live in
  Core (D19/D20) — this is that rule catching up with a capability the platform gained after the split.

  **No type-forward shim, deliberately.** Type forwarding preserves the full name *including* the
  namespace, so it would leave `Shenora.Windows.*` type names living inside the Core assembly — breaking
  the one-namespace-per-package convention the whole kit reads by, to save consumers a single `using`.

## 0.7.0 — 2026-08-02

### Breaking

- **`UpdateStage.CommitAsync` now REFUSES a stage containing files the manifest does not list.** No API
  changed, but the behaviour did: a stage that previously reported `Pending = true` now reports
  `Pending = false` if the staged tree holds anything unindexed. An app that fills `StagedDirectory` by
  extracting an archive whole — carrying entries the manifest never described — worked before and fails
  now.

  It is filed as breaking rather than as a fix because that is what a consumer experiences, even though
  the old behaviour was a hole: `ApplyAsync` overlays the staged TREE, so those unverified files were
  being copied into the install root. Verification now covers all three failure modes (truncation,
  tamper, intrusion) instead of two.

  **To restore the old outcome deliberately**, exempt what your release legitimately carries:
  `new UpdateStageOptions { Root = …, IsUnindexed = path => path.StartsWith("data/") }`. Exempting
  everything (`_ => true`) reproduces the previous behaviour exactly, and states in code that you meant
  to. The kit's own `manifest.json` is exempt unconditionally and needs no predicate.

### Added

- **An off-screen session can serve the app's OWN packaged bundle** —
  `SessionBrowserOptions.VirtualHost` + `ResourceProvider` + `FolderMappings`. Until now a session
  browser could only reach NETWORK-reachable URLs, so "co-browse my own UI" or "render my own page
  off-screen" simply did not work in a packaged desktop app: the session gets its own
  `CoreWebView2Environment` with none of the shell's serving set up, so navigating to
  `https://app.local/…` rendered WebView2's *"can't reach this page"* — and `SessionController`
  exposes no `CoreWebView2`, so it could not be bolted on from outside either.

  Pass the shell's own pair straight through; that is the whole recipe:

  ```csharp
  Browser = new SessionBrowserOptions
  {
      ProfileDirectory = …,
      KeepAliveInBackground = true,
      VirtualHost = hostOptions.VirtualHost,          // the SAME two values
      ResourceProvider = hostOptions.ResourceProvider, // the SAME provider instance (warm cache)
  }
  ```

  **Who this bit, and who never saw it:** a desktop-only app serving an embedded bundle. NOT a
  server-backed one — its pages sit on a real loopback origin, which is why the gap survived
  unnoticed: both sample demos work in dev mode and the e2e runs there.

  Three details are contracts rather than implementation:
  - **`VirtualHost` and `ResourceProvider` are both-or-neither**, refused at initialization naming the
    missing half. Either alone serves nothing, and its symptom is indistinguishable from the bug this
    closes.
  - **The app's `RequestFilter` is consulted BEFORE the bundle.** An app that blocks a request has
    stated a policy; serving it from the kit's own provider anyway would override that policy through a
    path the app cannot see. Both live in ONE `WebResourceRequested` handler for the same reason — two
    subscriptions each assigning `args.Response` is last-writer-wins by subscription order.
  - **`FolderMappings` ships alongside**, because the kit supports both bundle mechanisms
    (interception for embedded content, `SetVirtualHostNameToFolderMapping` for disk-backed) and
    shipping half would leave a disk-backed app with exactly this gap.

  Recorded as **D38**, which also states what is deliberately still NOT reachable in a session: a
  custom/deferred SCHEME (`app://`, `media://`). Those must be registered when the ENVIRONMENT is
  created, so it is a bigger surface than the bundle pair and no consumer has needed it — a known
  limit rather than a guess.

  Proven on the packaged sample in BOTH directions: with the seam the co-browse pane renders the
  sample's real React frontend (`frontend: packaged`) and the pooled `RENDER/PROBE` route reports
  `offscreen "Shenora Sample" rendered — 5749 chars of live DOM`; with the two options removed again,
  the same click reproduces the error page.

- **`IFileDialogs.SaveAsync(options, write)` — the PORTABLE save**, and the counterpart to
  `OpenReadAsync`: open became universal by letting the host do the reading, save becomes universal by
  letting the host do the writing. A default implementation over `SaveFileAsync`, so it breaks no
  existing implementor and any shell with a real save picker gets it free.

  ```csharp
  await dialogs.SaveAsync(options, async (stream, ct) => await Encode(source, stream, ct));
  ```

  **Why a callback and not a returned path.** "Give me somewhere to save to" is not expressible on
  mobile — the user grants access to one document, the app writes into it while the grant is live, and
  there is no path it can keep. The callback is the only shape that is honest on every shell, so
  portable logic should use it even on the desktop, where the weaker one also happens to work.
  `SaveFileAsync` is now documented as the DESKTOP-flavoured member, the same way `OpenFolderAsync` is
  (D35's shape).

  **The write is ATOMIC, and this is the case that motivated `Files.BeginReplace`.** The content is
  produced into a sibling temp and swapped in only once the callback completes, so a save that throws,
  is cancelled, or is interrupted half-way **leaves the user's existing file exactly as it was** — it
  costs the work, never the original. A save picker is usually pointed at a long operation (an encode,
  an export, a report), and the longer the operation the wider the window a naive write-over-the-target
  leaves open. Pinned by tests that assert the previous file's contents survive both a throw and a
  cancel, and sabotage-verified by writing straight at the destination instead.

- **`SaveAsync` is implemented on BOTH mobile shells**, so save is universal end to end rather than
  desktop-only with a documented gap. `ACTION_CREATE_DOCUMENT` on Android (through AndroidX's
  `CreateDocument` contract) and `UIDocumentPickerViewController` in its export-a-copy form on iOS —
  raw platform code in each package's `Platforms/` folder, because MAUI Essentials has no save picker
  and the obvious third-party one lives in CommunityToolkit.Maui, which D13 forbids.

  **Both produce the content into a cache temp and only then hand it over**, so the user's existing
  document is untouched until the content is complete — the desktop's `Files.BeginReplace` reasoning
  applied to a destination that is a system grant rather than a path. On Android that also avoids a real
  trap: opening a content URI in write mode truncates the target immediately, so a caller that threw
  half-way would have destroyed a file the user picked to overwrite.

  Three things are contracts, not implementation details:
  - **It is a `partial` method, not a virtual with a fallback.** A third platform joining the shared
    mobile source cannot compile until someone decides what save means there. Verified rather than
    asserted: before the iOS half existed, the iOS build failed with `CS8795`.
  - **⚠ The pick does not always come first.** Android asks, then produces (so a cancel costs nothing);
    iOS must produce first, because its export picker hands over a file that already exists — so a cancel
    there wastes the work. Callers must treat the write callback as "may run even if the user cancels".
  - **`FilePath` is null on success on mobile**, by contract: the destination is a revocable grant, not
    something the app could legitimately reopen. A page must not read the missing path as failure.

  `SaveFileAsync` (the path-returning one) still refuses loudly on mobile, and its message now names
  `SaveAsync` as the thing to call instead.

  **Proven on a device and a simulator, with matching bytes**: the same `SAVE_TEXT` route answered
  `{"success":true}` on both, and the file landed at the chosen destination at 160 bytes on each — the
  desktop, Android and iOS all running one portable write callback. The run also earned its keep by
  finding a defect no build could: iOS's export picker suggests the TEMP FILE's own name, so a
  GUID-prefixed temp surfaced in the user's "Save as" field. Uniqueness moved to a per-call directory.
  Android could never have shown it, because there the suggested name is passed separately — a reminder
  that two shells sharing one contract can hide each other's bugs.

- **`UpdateStageOptions.IsUnindexed` + the INTRUSION check in `UpdateStage.CommitAsync`.** Stage
  verification had two of the three failure modes a verifier needs: truncation (listed but missing) and
  tamper (present, wrong hash). It did not reject **intrusion** — a file present in the stage that the
  manifest does not list — and the gap was end-to-end rather than theoretical, because `ApplyAsync`
  overlays the staged TREE rather than the manifest. So a file nothing had verified was copied into the
  install root, while the marker's own documentation promised "complete and verified — an applier never
  has to re-check".

  Both halves were individually defensible, which is why it survived: enumerating in `ApplyAsync` is
  correct (a differential stage holds only the changeset, and `manifest.json` is in the tree but not in
  the manifest), and verifying the manifest is correct. It was the PAIR that left a hole.

  **Strict by default.** `IsUnindexed` is a predicate, not a list, because which paths a clean release
  legitimately carries unindexed is a property of whatever GENERATED the manifest — a bundled data
  folder, a seeded checksum stamp, a version file that changes every release. Baking that set in would
  freeze one app's packaging policy into everyone's verifier.

  ⚠ **Getting the exemption set wrong fails in the inverted direction**: too loose lets an injected file
  through, too strict rejects every honest download — and the second is worse, because it breaks for
  every user at once rather than for an attacker. The option says so, and says to validate against a
  real published release rather than fixtures, which agree by construction.

### Changed

- **The virtual-host serving path is now ONE implementation** (`WebViewBundleServing`, internal),
  shared by `WebViewHost` and `SessionBrowser` instead of copied. No behaviour change for the host.
  It also brought that logic under test for the first time — it used to live inline in a
  `WebResourceRequested` lambda over a live `CoreWebView2`, so nothing could reach it, and every part
  of it fails ONLY in a packaged build (dev serves the frontend from Vite and never comes through
  here). The pinned case worth naming: the query is stripped BEFORE the path is unescaped, so a
  filename containing `%3F` does not get truncated at the decoded `?`.

## 0.6.0 — 2026-08-02 — published, but it carries 0.5.1's code

**Nothing new shipped under this number.** `git diff v0.5.1 v0.6.0` touches no `src/` file except
`<VersionPrefix>` itself: the packages are 0.5.1's assemblies with a higher version on them. If you took
0.6.0 expecting anything below, you have 0.5.1 — upgrade to 0.7.0.

**What went wrong, and it was neither the workflow nor the version resolver.** A session's eight commits
were finished, verified and committed LOCALLY but never pushed, so the release ran against what the
remote actually had — the commit before that work started. The workflow bumped `0.5.1 → 0.6.0` exactly as
it was asked to. There was no bad input and no failed gate; the branch simply did not contain the work.

**The visible damage is that this section did not exist.** The workflow stamps `## Unreleased` with the
resolved version, and on the released commit there was no `## Unreleased` at all — that section was part
of the unpushed work. So 0.6.0 published with no changelog entry whatsoever, which is why this one is
written after the fact rather than stamped.

**The lesson is about release STATE, not release inputs**, and that makes it a different failure from
`## 0.2.0 — never released` above: that one was a hand-edit corrupting the version baseline, this one was
a correct release of a stale tree. Both were invisible at the moment of cutting. The signal that WAS
available and unused: a release whose changelog has nothing under `## Unreleased` is almost certainly
releasing nothing — worth a gate, tracked in `TASKS.md`.

Left published rather than unlisted, deliberately: it is a valid, working build of 0.5.1's code, and
0.7.0 landing immediately after means nothing resolves to it as "latest".

## 0.5.1 — 2026-08-02

### Added

- **`Files` + `FileReplacement` + `FileWriteMode` in `Shenora.Core`** — the kit's counterpart to
  `System.IO.File`, one letter away on purpose. **Every write is atomic by default**, so an
  interruption can never leave a file half-written or destroy the previous contents.

  ```csharp
  Files.WriteAllText(path, json);                              // atomic — the default
  Files.WriteAllText(path, json, mode: FileWriteMode.Direct);  // opt out, deliberately

  using var r = Files.BeginReplace(videoPath);
  await Encode(source, r.TempPath);                // the ORIGINAL is never touched
  if (await Probe(r.TempPath)) r.Commit();         // else dispose discards it
  ```

  **Atomicity is the default rather than an opt-in type, and that was the design's last correction.**
  An earlier draft called this `AtomicFile`, which framed correctness as a mode you remember to
  choose — and the call sites that forget an opt-in are precisely the ones that break. `Direct` exists
  for the two cases where atomic genuinely cannot pay (a very large file, where the temp doubles peak
  disk; a share that will not honour the rename) and is pinned by a test asserting it does NOT protect
  the previous file, so the trade is stated rather than implied.

  It cannot be called `File`: a consumer with both `using System.IO;` and `using Shenora;` would
  get an ambiguity error on every existing `File.` call.

  **The failure it prevents is a silent one.** `File.WriteAllText` truncates the target and then writes
  into it, and config stores typically load best-effort — so an interrupted write does not error, it
  resets the user's settings, and nobody notices until they wonder why their preferences reverted.

  `IFileUpdateQueue` already owned the concept via `FileChange.Replace`, but only through an async,
  queued, multi-change applier with rollback and cross-process partitioning. Most file writing is not
  that, and at least one caller saves from a window-closing path where awaiting a queue is actively
  worse.

  **The transform half is the general case and the write is its degenerate form** — the one where
  producing the output takes no time. Encoding, compiling, extracting and rendering share a shape:
  produce beside the target, verify, then swap. Verification is a SEAM rather than a feature, because
  only the app knows what valid means for its format — and "finished writing" is not "valid": a
  truncated encode is complete and worthless, and swapping it in destroys the original just as surely
  as writing over it would have.

  **Ported from the first adopter rather than designed here** (D8), keeping the four details that are
  easy to get wrong: a FIXED `.tmp` suffix, so a crash leaves one predictable leftover instead of
  debris nobody sweeps; flush-to-disk before the rename, or the rename lands while the data is still in
  the OS cache and a power loss leaves an intact rename pointing at an empty file;
  `File.Move(overwrite: true)` rather than `File.Replace`, which throws when the target does not exist
  yet; and the guarantee that on any failure the PREVIOUS file survives.

  **Two things from that port were then GENERALISED, because they were the adopter's policy rather
  than a mechanism** (`generic-library.md`: ship the mechanism, never the consumer's shape):
  - **The encoding is a parameter**, defaulting to UTF-8 without a BOM. Hard-coding no-BOM was one
    app's requirement — their native launcher substring-reads a JSON file — and would have locked out
    any app that needs the BOM for a legacy tool.
  - **It throws instead of returning `bool`.** Never-throw-and-return-false was a config store's
    best-effort policy; imposed on everyone it means a caller who ignores the result carries on with a
    stale file, which is the same silent failure this type exists to prevent, one level up. A
    best-effort caller writes `try/catch` and picks its own policy — and the previous file is intact
    either way.

  Sabotage-verified both ways, and one gap is stated rather than papered over: **deleting the flush
  leaves every test green.** Durability against power loss cannot be asserted from a process that is
  still running, so that line rests on reasoning and is marked load-bearing in the source.

## 0.5.0 — 2026-08-02

### Breaking

- **The package set is now one shell per PLATFORM.** Three published ids are superseded by one, and
  the mobile shell arrives as two:

  | Was (published at 0.4.0) | Is now |
  |---|---|
  | `Shenora.WinForms` | `Shenora.Windows` |
  | `Shenora.WebView2` | `Shenora.Windows` |
  | `Shenora.WebView2.Sessions` | `Shenora.Windows` |
  | — | `Shenora.Android`, `Shenora.iOS` |

  **Migration is a rename, not a rewrite.** Every type keeps its name and every member keeps its
  signature — the merged API surface was diffed against the three old baselines and is identical
  once namespaces are rewritten. Replace the three `PackageReference`s with one, and the three
  namespaces with `using Shenora.Windows;`.

  **Why Windows merged:** the split's only remaining justification was a consumer that took
  `Shenora.WinForms` without WebView2 — a tray or single-instance utility with no web frontend. This
  kit is React-in-a-webview by construction, so that consumer cannot exist; the boundary described an
  adoption STAGE, not a shipping configuration. `Sessions` folded in for free, adding no dependency of
  its own. D19's layer rule survives INSIDE the package: `Shell/` must not depend on `WebView/`.

  **Why mobile split:** Android and iOS ship separately, build on different hosts, and a consumer
  builds for one at a time. They share every line of source (`src/Shenora.Mobile/`, which is source
  and not a package), so the two can't drift.

  Naming is by platform rather than by framework throughout — the two mobile faces don't even share a
  web engine (Chromium's WebView vs WKWebView), so a framework name described the build system rather
  than the thing.

### Added

- **iOS — the third shell runs**, and the mobile shell now ships as **two platform packages**:
  `Shenora.Android` (`net10.0-android`) and `Shenora.iOS` (`net10.0-ios`). `node devtools/dev.mjs mac`
  drives a Mac over SSH to build, launch, screenshot and tap it (ported from the public sibling
  Sonora with its post-mortems kept; `devtools/README.md` has the traps).

  **The result worth reading is how little was needed.** The shell compiled for iOS with **no platform
  directive at all** — not one `#if` — and so did every line of `Shenora.Sample.Logic`. The sample
  needed exactly one, for the log sink, because a device log is the only way to see what a mobile host
  did and each platform has its own. The same page, the same envelope and the same portable facade
  produced `shell: maui · capabilities: [filePicker]`, `ECHO`, and `UI_STATE` returning
  `onUiThread: true` on an iPhone simulator.

  Two findings that outlive the port, both invisible on Android: a shared page must be written for
  the SUPERSET of shells (identical markup put the heading under the Dynamic Island, because an
  emulator has no safe-area insets to violate), and a sample that falls back to a hand-written
  transport when `dist/` is absent is a quietly weaker proof than one that does not.

- **Capability advertisement in the ready handshake** — `ShellInfo { Name, Capabilities }`
  (`Shenora.Ipc`), an `IpcHostBridgeOptions.Shell` forwarded by `WebViewIpcBridgeOptions` and
  `MobileIpcBridgeOptions`, the well-known names on `ShellCapability` (`Shenora.Core`), and their TS
  mirrors — `ShellInfo` / `ShellCapabilities`, with `notifyReady()` now resolving to
  `Promise<ShellInfo | undefined>` and the result cached on `bridge.shell`.

  This is what lets ONE web bundle ship to both shells. Before it, a page that wanted a title bar on
  desktop and none on mobile had to sniff the platform — a check the frontend cannot make correctly,
  because what a host can do depends on what the APP composed, not on the operating system. Now the
  host answers the handshake it was already answering with what it is and what it offers, and the
  page renders on data: `shell.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar/>`.

  Additive on the wire and in both languages: the reply previously carried no data, `Shell` is
  optional, and a host that leaves it null says nothing. **Absent means "assume nothing", never
  "assume desktop"** — a plain browser tab and a host predating this look identical to the client,
  and both are correctly capability-less. The names are pinned across languages by `WireMirrorTests`,
  which also grew a block-comment stripper: its TS interface parser truncated at the first
  `{@link …}` and dropped every field after it. Measured by disabling the stripper — it fails a
  CORRECT mirror rather than passing a wrong one, so the risk was the fix it invites (loosen the
  assertion) rather than a silent pass.

  Proven end-to-end on both shells, not just in tests. The same handshake, two honest answers: the
  desktop sample renders `shell: winforms · windowChrome, dropZones, filePicker, folderPicker,
  savePicker, secondaryWindows, tray` (every one of them something that composition actually mapped),
  and the MAUI sample on an Android device logs `shell: maui · capabilities: [filePicker]`.

- **`IpcJson.AddTypeInfoResolver`** — an app may now contribute an `IJsonTypeInfoResolver` (typically
  a source-generated `JsonSerializerContext`) to the one frozen wire-options instance, during startup
  and before anything serializes. Purely additive; the default path is byte-for-byte what
  `MakeReadOnly(populateMissingResolver: true)` produced before.

  Why it matters beyond convenience: the options were frozen with a **reflection** resolver, which is
  fine on desktop and Android and is exactly the metadata iOS strips (Mono AOT + trimming) — failing
  at runtime, on a device, rather than at build time. The same seam is what makes full AOT /
  NativeAOT reachable on Android, the strongest cold-start lever an on-device host has
  (`docs/2026-08-02-shenora-mobile-offline-plan.md` §4, §6).

  Contributed resolvers are consulted **before** the reflection fallback, so a generated context wins
  for the types it knows. Registering after `IpcJson.Options` has been built **throws** and names the
  fix rather than being silently dropped — a dropped resolver reappears as a stripped-metadata crash
  on a device, which looks nothing like its cause. What it does not yet buy: the kit ships no
  generated context for its own envelope types, so those still resolve through reflection unless an
  app includes them in its own context.

- **`IpcHostBridge` (+ `IpcHostBridgeOptions`) in `Shenora.Ipc`** — the transport-neutral INBOUND
  half of a host channel: parse → handshake-or-dispatch → response JSON, plus the dispatch lifetime
  token and the no-raw-exception-text error boundary. The mirror of the client's `ShenoraBridge`,
  which has owned correlation and batch unbundling since P3 while the host side had none — so
  `WebViewIpcBridge` was the only thing that knew this shape and it was welded to WinForms.

  Evidence rather than anticipation: the D3 transport spike needed no change to `Shenora.Ipc` at
  all, but did mean hand-writing this loop, which every non-WinForms host writes identically.

  Like `NotificationPump` it owns **no transport and no timer** — the base reads a message off its
  own wire, calls `HandleIncomingAsync`, and writes the result back if there is one. It optionally
  takes the pump, so "a handshake opens the outbound gate" lives in one place; CLOSING the gate
  stays the base's job, because only the base knows which of its events mean the client can no
  longer receive (P5.5 H3).

  `WebViewIpcBridge` is now a thinner adapter over it — the `Forms.Timer`, the WebView2 event
  wiring and `PostWebMessageAsString`. **Not a breaking change:** its public surface is
  byte-identical (`HandshakeModule`/`HandshakeType` are `const` forwards to the new home, so the
  literals every consumer compiled against are unchanged), and its API baseline did not move.

- **`UseHeadless` (+ `HeadlessRunnerOptions`) in `Shenora.Core`** — an `IShenoraRunner` for a host
  with no UI loop: lifecycle hooks, block until a stop signal, ordered shutdown. `Run()` used to
  throw unless a Windows package was referenced, so Core's application-host half was Windows-only in
  practice even though every type in it is portable — the D3 spike had to bypass the builder entirely
  and wire DI by hand.

  Stops on `HeadlessRunnerOptions.StopToken` and, by default, on SIGINT/SIGTERM. The signal handler
  sets `Cancel = true` deliberately: without it the runtime terminates the process and
  `IShenoraLifecycleHook.OnStopping` never runs, silently skipping everything the family relies on
  shutdown for. Hook ordering matches `WinFormsRunner` exactly — `OnStarting` in registration order
  and unguarded (a hook that cannot start is a startup failure the app must see), `OnStopping` in
  REVERSE order and guarded, running even when startup failed partway.

  **It is not the mobile answer**, and says so in its own XML: a host whose PLATFORM owns the loop
  (a mobile activity, a MAUI app) cannot honour `IShenoraRunner.Run`'s "blocks until shutdown"
  contract and needs its own runner.

- **`ShenoraApplication.Start()` / `Stop()`** — the lifecycle-hook sequence, now owned in ONE place
  instead of copied into every runner. `Run()` is `Start` → block → `Stop`; a host whose platform
  owns the loop calls the pair directly. Ordering and the start/stop asymmetry are unchanged
  (`OnStarting` in registration order, unguarded; `OnStopping` in reverse, guarded, running even
  when startup failed partway) — `WinFormsRunner` and the new headless runner both route through it,
  so a third shell cannot drift.

  **Both are idempotent.** A platform-owned loop offers several plausible places to start from and
  some of them re-enter (an activity's `OnCreate`/`OnResume` fire per activity instance), and
  re-running lifecycle hooks is the double-init bug class `WinFormsBootstrap.Initialize` already
  guards. A `Stop()` before any `Start()` deliberately does NOT latch, so a platform that signals
  "stopped" before it ever signalled "started" cannot disarm the real shutdown that follows.

  _Corrected after measuring on a device: an earlier revision justified this with "Android recreates
  the activity on a configuration change, so `Window.Created` fires again". That is not what happens
  in MAUI — its Window is process-scoped and the template's MainActivity declares
  `ConfigurationChanges`, so `Window.Created` fired exactly once across a home-and-return. The guard
  is cheap insurance for the wirings that do re-enter; it is not a fix for that one._

- **`UpdateStage` (+ `UpdateStageOptions`, `UpdateStageStatus`) in `Shenora.Core`** — the staging half
  of a two-phase update. An app downloads the changed files into `StagedDirectory` however it likes,
  then `CommitAsync(manifest)` verifies **every** file's SHA-256 and only then writes `ready.json`.

  **The ordering is the property, not an implementation detail.** The marker means "complete and
  verified", so an applier never re-checks; a crash mid-download leaves files but no marker and the
  next run restages. Sabotage-verified by publishing the marker first, which failed all three
  no-marker assertions (tampered, missing, cancelled).

  `Begin()` clears any previous attempt before downloading — leftovers from a stage that died after
  three of ten files would otherwise verify as part of the next one. `GetStatus()` reads only the
  marker and reports *not pending* for an unreadable one rather than throwing, because UI asks it on
  every settings screen. And it carries `ManifestDiff`'s deferred guard: **an empty manifest is
  refused**, since it would tell an applier to delete every tracked path — destroying the install as
  the successful outcome of an update.

- **`IUpdateSource` + `UpdateStage.FetchAsync`** — the release-source SEAM, and the kit ships **no
  implementation of it**. Both donor apps fetch from GitHub releases; that is one instance of
  "somewhere to get a manifest and some files from", not the shape, and baking a client in would drag
  an HTTP dependency into `Shenora.Core` and ship a consumer's decision. Two methods only —
  release notes, channels, signatures and rollout percentages are product decisions.

  `FetchAsync` is the whole download-and-stage phase: diff, fetch **only the changed files**, commit.
  A design point worth knowing: because a differential update stages only the changeset,
  `CommitAsync` takes the manifest of what is IN the stage, not the release manifest — verifying the
  full release against a partial stage would fail on every unchanged file. The full release manifest
  rides along as `manifest.json` inside the stage, because an applier needs it to compute REMOVALS
  and overlaying it makes it the new installed baseline. A fetch that throws is left to escape: a
  partial download must not be staged as though it were whole.

- **`UpdateStage.ApplyAsync` + `UpdateOutcome`** — the apply pass, and it is **portable .NET, not
  native**. Overlay the stage onto the install, delete only what the new manifest dropped, clear the
  stage. A self-contained app needs nothing else; a framework-dependent one still wants a native
  launcher, but that launcher's job shrinks to bootstrapping the runtime and calling this.

  **Run it from OUTSIDE the tree it overlays.** That is the topology the design chose: a launcher at
  `{root}/` overlaying `{root}/app/` can never overwrite or delete itself, which makes four
  self-exclusion guards *unreachable* rather than merely handled — the difference between a bug class
  fixed and a bug class that cannot occur.

  It carries the guard one donor has and the other does not, and this is the one that matters:
  removals are "installed minus release", so a staged manifest that fails to load would delete every
  tracked path — including the files just overlaid — turning a **successful copy into a corrupt
  install**. An unreadable or empty staged manifest therefore blocks the apply entirely rather than
  proceeding with no removals. Sabotage-verified. Removals are **tracked paths only**: untracked
  files (settings, databases, user data) are never swept, and a missing baseline means no removals
  at all rather than a guess.

  Still not shipped, deliberately: no downloader, no release source, no native launcher.

- **`UpdateManifest` / `ManifestFile` / `ManifestDiff` in `Shenora.Core`** — the staged-update
  changeset, and the first piece of `docs/2026-08-02-shenora-app-update-design.md` to ship. A running
  process cannot replace its own executable, so an update is two phases: the app downloads and
  verifies while alive, and something that runs before it applies the result. This is the contract
  the two phases share.

  `ManifestFile` is `{Path, Size, Sha256}` — the triple two sibling apps arrived at independently —
  and `ManifestDiff.Compute(installed, release)` yields `Added`/`Updated`/`Removed` plus
  `DownloadBytes`, so only changed files are fetched. Pure data and a pure function: **no downloader,
  no release source, no applier.** Where manifests come from is the app's, and the apply step is
  native by necessity.

  Two comparison rules are load-bearing rather than incidental, and both are sabotage-verified:
  paths normalize separators and case (otherwise the same file is "added" on every check and the
  update never converges) and hashes compare case-insensitively (otherwise a generator that emits
  upper-case hex reports EVERY file as changed — a full redownload that looks legitimate).
  `Removed` is **tracked paths only, never a directory sweep**, because user data lives in the same
  tree. ⚠ An empty release manifest legitimately removes everything, so one that failed to load must
  never reach `Compute` — validate before calling.

- **`@shenora/react` speaks both shells.** New `createHybridWebViewTransport()` (MAUI
  `HybridWebView`) and `createHostTransport()`, which picks whichever host the page is in.
  `ShenoraBridge`'s default transport is now the latter, so an app calls `invoke`/`post` and never
  learns which shell it is running in — the transport seam (D16) doing the job it was built for.

  Also widened: **`isShenoraAvailable()` now answers for the MAUI shell.** It tested `chrome.webview`
  alone, so on MAUI it returned FALSE — an app would have concluded it was in a plain browser tab
  while a perfectly good host sat on the other side of the channel. It answers "is there a host",
  which is the question callers actually ask. Widening only, so a WebView2 consumer sees no change.

- **Two new packages: `Shenora.Android` and `Shenora.iOS`** — the mobile shell, one package per
  platform. `MobileIpcBridge` over `HybridWebView`'s `RawMessageReceived`/`SendRawMessage`,
  `MobileUiDispatcher`, and the Essentials-backed implementations of the `Shenora.Core` contracts,
  registered by `UseMobile`.

  **Both compile from one shared source tree** (`src/Shenora.Mobile/`, which is source and NOT a
  published package) so the two faces cannot drift; the platform boundary is the package boundary
  because that is how they build, ship and get consumed — one platform at a time, on different hosts.
  Divergence goes in each project's `Platforms/` folder, which the MAUI SDK includes per TFM, so it
  needs no `#if`. There is none yet; the first is expected in the save picker (Android SAF vs
  `UIDocumentPickerViewController`).

  Named for the platform rather than the framework deliberately: the two faces run on entirely
  different engines — Chromium's WebView on Android, WKWebView on iOS — so a vendor name would have
  described the build system rather than the thing, and `Shenora.iOS` never touches WebView2 at all.

  **It registers no `IShenoraRunner` on purpose:** MAUI owns the loop, so the app drives
  `ShenoraApplication.Start`/`Stop` from its own lifecycle. It is a PEER of the Windows shell, not a
  layer on it — it references neither `Shenora.WinForms` nor `Shenora.WebView2`.

  Two limits stated rather than left to be found. `HybridWebView` has no request interception, so
  the packaged bundle is served by the platform from `Resources/Raw/wwwroot` and the kit's
  resource-provider layer does not apply on this shell. And it exposes no document-lifecycle event,
  so the notification ready gate can be opened but never closed — a reloaded page simply
  re-handshakes.

  **Its surface is gated more weakly than the other five**, because a `net10.0-windows` test project
  cannot reference an Android assembly: `MetadataSurfaceTests` reads the built DLL's IL metadata, so
  adds, removals and renames are caught but signature-only changes are not. Building the repo now
  needs the `maui-android` workload and a JDK — see `devtools/README.md`.

- **`samples/Shenora.Sample.Maui`** — an Android head hosting the SAME `Shenora.Sample.Logic` the
  desktop sample hosts. That shared reference is the point: D20's portability stops being a
  compile-time claim about a `net10.0` project and becomes two shells running one facade.

  **Proven on a device, not by construction.** Request/response (`ECHO` → `{"echoed":"HELLO FROM
  ANDROID","length":18}`), batched host→page notifications, the structured error boundary
  (`NO_HANDLER` with `{module,type}` and no exception text), the native file picker through the
  portable `IFileDialogs`, and the mission scheduler with its operations registry — the contended
  mission finished ~1.5 s after the disjoint one, which is the serialization the scheduler exists
  for, observed on a phone.

- **`IFileDialogs.OpenReadAsync`** — read the content behind a picked handle, so portable app logic
  never calls `File.OpenRead` on one itself. The contract has always said `FileDialogResult.FilePath`
  is "a path or URI the HOST can resolve"; this is how a caller *uses* it without knowing which.
  A **default interface member**, so it breaks no existing implementor.

  Measured on a device rather than assumed: MAUI's picker **copies** the chosen document into app
  cache and returns a real filesystem path, not a content URI — so the default path-based read is
  already correct on both shells today. That is a fact about today's two shells, not a property of
  the contract, which is exactly why it belongs on the interface: a shell whose picker returns a
  genuine content URI (raw SAF, iOS security-scoped URLs) overrides it and app logic never notices.

  ⚠ The copy has a semantic the desktop does not: the handle is a **snapshot**, not the live
  document. Writing to it does not write back to the user's file, and the cache can be evicted.

- **`ShellCapability.NotSupported` in `Shenora.Core`** — how a shell reports a contract it cannot
  honour, now that there is more than one shell. An absent capability **throws**, naming the platform
  and (where there is one) the alternative; it does not silently no-op, because a quiet nothing is the
  "mistyped resource prefix degrading to an all-404 provider" bug class this repo keeps paying for.

  It draws a line worth knowing: **absent is not the same as differently-satisfied.** Clipboard
  images have no expression in MAUI Essentials, so that refuses; `IUiInteraction`'s block/unblock is
  satisfied BY the platform (mobile pickers are modal), so on that shell it is an honest documented
  no-op. Refusing the second kind would break portable logic that is behaving correctly.

  Deliberately not a `DispatchProxy` — a reflection proxy is exactly what iOS trimming strips, which
  is what `IpcJson.AddTypeInfoResolver` exists to avoid depending on. Shells write small explicit
  stubs sharing this one message.

## 0.4.0 — 2026-08-02

_Do not stamp this heading by hand — the release workflow does it (`docs/RELEASING.md`). See the
0.2.0 note below for what hand-stamping cost._

### Breaking

- **The scheduler surface is renamed `Work*` → `Mission*`** (owner's call). `Work` is too common a
  word to own or to grep for, and the obvious alternative — `Task` — collides with
  `System.Threading.Tasks.Task`, with `TaskScheduler` ambiguous against the BCL type in every
  consumer that imports both namespaces. `Mission` names a unit of work with an objective and an
  outcome, is unique on this surface, and stays mechanism vocabulary rather than any app's domain
  noun. Namespace is unchanged (`Shenora.Core`); the folder moved to `src/Shenora.Core/Missions/`.

  | 0.3.0 | now |
  |---|---|
  | `IWorkScheduler` / `WorkScheduler` / `WorkSchedulerOptions` | `IMissionScheduler` / `MissionScheduler` / `MissionSchedulerOptions` |
  | `WorkRequest` / `WorkContext` / `WorkResult` / `WorkOutcome` | `MissionRequest` / `MissionContext` / `MissionResult` / `MissionOutcome` |
  | `WorkClaim` / `WorkLane` / `WorkKey` | `MissionClaim` / `MissionLane` / `MissionKey` |
  | `WorkView` / `WorkSnapshot` / `WorkSchedulerState` | `MissionView` / `MissionSnapshot` / `MissionSchedulerState` |
  | `IWorkPolicy` / `PriorityWorkPolicy` / `IWorkObserver` | `IMissionPolicy` / `PriorityMissionPolicy` / `IMissionObserver` |
  | `IWorkStore` / `WorkRecord` / `WorkState` | `IMissionStore` / `MissionRecord` / `MissionState` |
  | `WorkId` (property, and the `workId` parameter) | `MissionId` / `missionId` |
  | `MissionSnapshot.Work` | `MissionSnapshot.Mission` |

  `ILane`, `WorkLane`'s `Permits`, `IClaimScope`, `FlatClaimScope`, `NestedClaimScope`, `PathClaims`,
  `RetryPolicy` and `RecoveryPolicy` are unchanged — only the unit-of-work prefix moved. A rename is
  the whole change: no behaviour, no signature shapes, no defaults differ. Sed on the table above and
  you are done.

  It is a real break against a published surface (0.3.0 is on NuGet), taken deliberately while the
  layer is days old and the realistic consumer count is zero — not a free one.

- **The unit is split into a DEFINITION and an EXECUTION**, in the same window and for the same
  reason: introducing it later would be breaking, whereas doing it now is free of anything except this
  entry. `MissionRequest` → **`MissionDefinition`** (what should run), and `MissionContext` +
  `MissionView` + `MissionSnapshot` collapse into **`MissionExecution`** (one specific run) — four
  types for two concepts became two.

  ```csharp
  // before                                    // now
  Run = ctx => DoAsync(ctx.Cancellation)       Run = (mission, ct) => DoAsync(ct)
  IReadOnlyList<MissionSnapshot> Snapshot()    IReadOnlyList<MissionExecution> Snapshot()
  void OnStarted(in MissionView work)          void OnStarted(in MissionExecution mission)
  bool ShouldStart(in MissionView work, …)     bool ShouldStart(in MissionExecution mission, …)
  ```

  `MissionExecution` deliberately carries no `CancellationToken`: the body takes one as a second
  parameter, which matches every other callback seam in the kit and keeps an execution a pure value
  that is safe to hold, copy, and hand to a diagnostics view. `MissionSnapshot`'s `IsRunning` moved
  onto the execution itself, and `Attempt` is now visible on a running execution rather than only
  inside the body.

  One submit still produces exactly one execution. The split earns its keep the moment a mission
  recurs or is rebuilt from a `MissionRecord` — one definition, many executions — which is precisely
  the change that would otherwise have altered `SubmitAsync`, every body, all three observer callbacks
  and both policy methods on the same day.

- **`IMissionStore` → `IMissionQueueStore`**, and with it
  `MissionSchedulerOptions.Store` → `.QueueStore`, `SaveAsync` → `AppendAsync`, `LoadPendingAsync` →
  `LoadAsync`. Same three operations, same `MissionRecord`, same `RecoveryPolicy` — what changed is
  what the seam CLAIMS to be. It is not a "durable missions" service sitting beside the queue; it is
  where the queue's own entries live when they must survive a restart. Describing it as a separate
  concept is what made recovery read oddly, as though records arrived from somewhere other than the
  queue they were enqueued into.

  A fuller change was designed and rejected: making the whole queue a pluggable async seam. It would
  put an `await` in the dispatch path, which cannot run under the scheduler's lock, so admission would
  have to read candidates, take the lock, and then re-validate against a collection that may have
  changed underneath — a race in the one place where a race corrupts rather than delays, bought for a
  capability no consumer has asked for. Ordering was already the app's, through `IMissionPolicy`.

### Added

- **Crash-atomicity for `AllOrNothing` updates** — `IFileUpdateJournal`, the shipped
  `FileUpdateJournal`, `FileUpdateQueue.RecoverAsync()`, and the `FileUndoStep`/`FileUndoKind`/
  `FileUpdateStage` vocabulary the plan is written in. Supply a journal and an update survives the
  process DYING, not merely a change failing; without one, behaviour is exactly as before.

  The undo plan is written to disk BEFORE each change, which is the whole property: a plan written
  afterwards is missing precisely the change that got interrupted. That forced the one structural
  change — undo became DATA rather than closures, so every change is now planned (including the
  sidecar names it will use) and then applied.

  Recovery distinguishes two states, because they need opposite treatment: an update interrupted
  while APPLYING is rolled back, one interrupted while COMMITTING — every change landed, only staged
  deletions left — is FINISHED. Rolling that one back would undo a success. Recovery is safe to run
  twice; every undo step checks the world first, since after a crash it cannot assume the change it
  undoes ever happened.

  **The kit ships a journal implementation** despite shipping no other storage: a journal that is not
  crash-safe is pointless, and asking every adopter to write a crash-safe store for a mechanism whose
  purpose is surviving a crash is not reasonable. One `WriteThrough` JSON file per in-flight update,
  temp-then-replace, one file rather than an append log so a torn entry is skippable instead of a
  parsing failure at the worst moment.

- **Cross-process file locking, in two halves that answer different questions.** `IPathLocker`/
  `IPathLease` + `FilePathLocker` (`Shenora.Core`) give advisory leases; `IFileLockInspector` +
  `RestartManagerLockInspector` (`Shenora.WinForms`) name who is holding a file. Built on an
  adopter's evidence: a filesystem-heavy app whose managed tree it does not own, which both spawns
  its own tools AND competes with foreign processes.

  **Reaching for the wrong one is the mistake this split exists to prevent.** A lease excludes
  PARTICIPANTS — a second instance, or a child process the app spawns while the parent holds the
  lease — and does nothing whatsoever about a game, a mod loader, antivirus or another application
  editing the same folder. For those, exclusion is impossible and the useful thing is a NAME:
  `FileUpdateResult.Holders` turns "the process cannot access the file" into "held by X (pid)".
  `WhoHolds` returning empty means "cannot tell", never "nobody".

  Leases are lock FILES in a directory of the app's own — never the managed tree, since an app
  frequently does not own the folder it manages and sidecar locks there get synced, committed, and
  outlive the process. Opened `FileShare.Read` + `DeleteOnClose`, so the OS releases them on a crash
  rather than leaving a permanent wedge, and keyed by a hash of the canonical path so two spellings
  are one lease. `FileUpdateQueueOptions.Locker` makes the queue take them for every path an update
  touches, in sorted order so two overlapping updates cannot deadlock against each other.

  **Network shares are supported, correcting an earlier "not a target".** Leases work over SMB2+ —
  provided the lock directory is ON the share, since a lock in one machine's local storage is
  invisible to the other, and that is the setting that fails silently. A lease released by a crash
  returns when the SMB session times out rather than instantly: bounded and self-healing, but size the
  lease timeout for it.

- **A file-update queue** — `IFileUpdateQueue`/`FileUpdateQueue`, `FileUpdate`, `FileChange`
  (`Replace`/`Move`/`Delete`/`CreateDirectory`), `FileAtomicity`, `FileUpdateResult`, in
  `Shenora.Core`'s `Io`. Filesystem MUTATIONS land one at a time while the missions that produced
  them run in parallel.

  **Why it is not part of the scheduler:** a path claim excludes two missions for their whole
  duration, but the expensive phase usually touches only a temp file — so under claims alone a
  seven-second compress waits on another mission's three-millisecond rename. Compute in parallel,
  hand the finished change set to the queue, and only the landing is serialized. The failure modes
  do not overlap either: a scheduler's are starvation and deadlock, an applier's are partial writes
  and locked targets.

  **Atomicity is the app's choice per update.** `PerChange` applies in order and stops at the first
  failure, reporting the index it reached. `AllOrNothing` undoes what it applied, in reverse — which
  is why a delete under it is STAGED: moved aside and only really removed once the whole set lands,
  a delete being the one change that cannot be undone from nothing. Backups and aside-copies are
  siblings of their target so every move stays same-volume. **The limit is in the enum's own XML:
  this survives a failure, not a power cut** — crash-atomicity needs a durable intent journal, which
  is deliberately not built and additive when it comes.

  Cross-process path leases are designed but NOT shipped: claims exclude inside one process only, and
  whether anything needs more than that today is still an open question in the design doc.

- **Chained missions** — `MissionChain.Sequence(kind, params MissionStep[])`, `MissionStep`,
  `IMissionChainContext`. Steps run in order sharing one context, so a later step can use what an
  earlier one produced — the case claims cannot express, since they prevent overlap but say nothing
  about order or data flow. Before this, a chain lived in a stack frame: unresumable, invisible, and
  dead if the awaiting code went away.

  **A chain is ONE queue entry, not N.** `Sequence` returns an ordinary `MissionDefinition` the
  scheduler cannot tell apart from any other, so it gains no dependency edges and no "blocked on a
  predecessor" state — the alternative was a DAG engine by another name, which the kit declined on
  the evidence that no sibling has needed one. The cost is accepted and documented: a chain holds the
  UNION of its steps' claims for its whole life, taking the STRONGER mode where steps disagree, so a
  read-then-write chain holds that key exclusively throughout.

  A step's `RetryPolicy` retries that step only; there is no chain-level retry, because re-running
  completed steps is a judgement only the app can make. A failing step fails the chain, and cancelling
  cancels the chain — one mission, one token. `IMissionChainContext` is **in-memory only**: a durable
  chain carries state in `Payload` like every other durable mission, and that limit is stated rather
  than papered over, because a resume that silently lost the context is worse than one that never had
  it.

## 0.2.0 — never released

**This version number was consumed without ever shipping.** A session hand-edited
`<VersionPrefix>` from `0.1.2` to `0.2.0` and hand-stamped the changelog heading below to match.
Neither is a session's job. The release workflow RESOLVES the version — an empty `version` input
means "bump from whatever `VersionPrefix` currently says" — so the hand-bump silently moved the
baseline, the run bumped `0.2.0 → 0.3.0`, and 0.2.0 went straight from unreleased to skipped. Nothing
was ever published under it; the registries go 0.1.2 → 0.3.0.

The hand-stamp did the more visible damage. The workflow stamps `## Unreleased` with the resolved
version, and there was no `## Unreleased` left to stamp — so **0.3.0 shipped with its changelog
section titled "0.2.0"**, which is the exact failure `docs/RELEASING.md` says stamping was automated
to prevent. That is corrected below.

Kept as a stub rather than deleted: a gap in a changelog reads as an omission, and every design doc,
decision and task entry written while this work was in flight calls it "the 0.2.0 pass". Those names
are left alone — they refer to the work, not to a release that exists.

## 0.3.0 — 2026-08-01

_Released as 0.3.0; drafted under the working name 0.2.0 (see above). Heading corrected after the
fact — the content is unchanged and is what shipped._

The communication core (D23, `docs/2026-08-01-shenora-communication-core-design.md`): the module
contract now carries the EVENT path, the kit tracks long-running operations, and the host outbound
pipeline is base-agnostic. Triggered by the first adopter's IPC + drop-zone design review — the
verdict was that the client design already matched its own stated intent ("a stateful design with an
event hub … async from the UI, progress synced") while the HOST contract did not.

### Breaking

- **`BaseFacade.RouteMessageAsync` now takes an `IModuleContext` — the module contract's EVENT path
  is in the signature, not a side dependency every app wired by hand.**
  `(IpcRequest request, CancellationToken cancellationToken)` →
  `(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)`. Before this,
  `Shenora.Ipc` had **zero references to `IEventBus`** while the kit's own `DropZoneManager` took one
  as a REQUIRED option — the bus was already the spine, the contract just never admitted it.
  **Migration: add the parameter to every override; ignore it if your facade doesn't emit.**
  `context.Publish(type, payload?, scope?)` is the new default gesture for emitting — module-scoped,
  so it can never drift from `ModuleName` the way a hand-typed literal re-used at every call site
  can — and `context.Start`/`context.Run` are the tracked-operation primitive (see `### Added`).
  `BaseFacade`'s own constructor gained two optional parameters, `IEventBus?` and
  `IOperationRegistry?`, to back the context: `protected BaseFacade(ILogger? logger = null, IEventBus?
  events = null, IOperationRegistry? operations = null)`. Existing `base(logger)` calls compile
  unchanged; a facade that never publishes and never starts tracked work is completely unaffected,
  including every bus-less unit test in the suite. `Publish`/`Start`/`Run` fail LOUD at the call site
  — naming the exact fix (`pass an IEventBus to BaseFacade`, `call services.AddShenoraOperations()`)
  — rather than silently no-op-ing when the corresponding dependency was never supplied.
  `WebViewIpcBridge`'s internals also moved onto a new `Shenora.Ipc.NotificationPump` in this release
  (see `### Added`) with no public-surface break: `WebViewIpcBridgeOptions`' existing names
  (`NotificationInterval`, `MaxQueuedNotifications`) and behavior are preserved.
- **`OperationOptions.Resumable` / `OperationInfo.Resumable` (C#) and `resumable` (TS) are REMOVED**
  (generic-library audit finding 2, folded in before publish). The flag was consulted nowhere except
  `RegisterWaiting`'s own required-true gate — every caller had already forced it `true` to pass
  that gate, so it carried no information the method's existing non-empty-`ResumePayload` requirement
  didn't already express. **Migration:** drop the property from any `OperationOptions` initializer; a
  client testing "is this resumable" already used (and should keep using) `status === OperationStatuses.Waiting`.
- **The status collapse (owner direction, before publish — "structured like XHR"; see finding 7 under
  `### Added` for the full rationale).** `OperationStatus.Paused`/`.Interrupted` → one value,
  `OperationStatus.Waiting`; `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause` →
  `Wait(reason?, detail?)`; `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting`;
  `RequestPause` → `RequestWait`; `OperationEvents.PauseRequested`/`OPERATION_PAUSE_REQUESTED` →
  `WaitRequested`/`OPERATION_WAIT_REQUESTED`; facade route `PAUSE` → `WAIT`; client
  `OperationStatuses.Paused`/`.Interrupted` and the `paused`/`interrupted` getters REMOVED,
  `Waiting: 'waiting'` added (`waiting` is now the whole band). **Migration:** rename every occurrence
  1:1; a client testing "is this waiting" now reads `status === OperationStatuses.Waiting` instead of
  unioning `paused`/`interrupted`; a handler that branched on the removed values to guess whether
  `RequestResume` would drop the entry should instead just fold `OPERATION_REMOVED` — the host decides
  the drop-vs-keep asymmetry itself (see finding 8 under `### Added`) and always publishes it as a named
  removal, so a client-side guess at the signal (`resumePayload` or otherwise) is never needed.

### Added

- **The tracked-operation primitive** (D23; harvested mechanism-only from a private sibling's
  320-line process registry, per `generic-library`'s two-app bar): id, owning module, app-defined
  `Kind`/`Scope`, status, progress, idempotent finish, cancel-by-id, bounded history, and throttled
  progress emission — with NO queue, scheduler, retry, priority, phase model, `ProcessType`-style
  enum, i18n rendering, UI or persistence. What an operation IS stays the app's; the kit only tracks
  it. New in `Shenora.Ipc`: `OperationStatus` (`Running`/`Completed`/`Failed`/`Cancelled`/
  `Waiting`), `OperationLabel` (`{Text?, Key?, Parameters?}` — the same i18n shape as
  `IpcError`), `OperationProgress` (`{Value, Total?, Unit?}` — the app's own unit, not an assumed
  percent; see finding 6 below), `OperationOptions`, `OperationInfo` (the one snapshot type for every lifecycle
  transition — a client folds by `Id`, last-write-wins, no cross-type ordering hazard; carries
  `WaitReason`, an app-defined string like `Kind`), `IOperation`
  (`Report`/`Complete`/`Fail`×2/`Cancel`/`Wait`/`Resume`, all idempotent once terminal, with its OWN
  `CancellationToken` — never the request's, because work handed off outlives the request that
  started it), `IOperationRegistry`/`OperationRegistry(+OperationRegistryOptions)`,
  `OperationEvents` (`OPERATION_UPDATED`, `OPERATION_RESUME_REQUESTED`, `OPERATION_WAIT_REQUESTED`,
  `OPERATION_REMOVED`), `OperationsFacade`
  (`LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`/`DISMISS`/`WAIT` under module `OPERATIONS` by default —
  also exposed as the `ListType`/`CancelType`/`ClearFinishedType`/`ResumeType`/`DismissType`/
  `WaitType` constants, pinned against the client by the wire-mirror test), and
  `AddShenoraOperations(OperationRegistryOptions? options = null)` — opt-in DI wiring, so an app with
  no long-running work pays nothing; takes the options RECORD directly (not a configure callback) so
  a renamed `ModuleName` etc. can actually be set, matching every other options type in the kit.
  `GetAll(module?, scope?)` and `ClearFinished(module?, scope?)` share ONE scope rule with
  `IEventBus` — an unscoped operation matches any requested scope, not strict equality — and a
  removal (`MaxHistory` eviction, `ClearFinished`, a no-live-handle entry dropped by `RequestResume`)
  now publishes `OPERATION_REMOVED { operationIds }` so a client mirroring bounded host history
  actually hears about it (generic-library audit finding 4 — see below).
  Progress reports are throttled to `OperationRegistryOptions.ProgressInterval` (default 100 ms) with
  a TRAILING emit so the final value in a window is never dropped; every lifecycle transition emits
  immediately, never throttled. An operation failure obeys the same no-raw-exception-text boundary as
  a request/response failure: an unexpected exception crosses as `IpcErrorCodes.UnknownError` plus the
  exception type name, with the real detail logged host-side only. `Cancel` refuses an operation that
  never opted into `Cancellable`, rather than flipping its status while the body runs on underneath
  it — but the body's OWN end in `OperationCanceledException` (via `Run`, or a direct
  `IOperation.Cancel()` call by the operation's own owner) is always terminal regardless of
  `Cancellable`, because that is not the same permission question as an external by-id cancel
  request. `RequestWait`/`RequestResume` are the ASK half of the waiting band — a client asks, the
  owning module's own `IOperation.Wait`/`Resume` acts (see the design-pass note under `### Removed`
  for the crash-checkpoint half that was cut before publish).
  `IOperationRegistry.Find(id)` resolves a live handle for an already-started operation — reinstated
  after being sketched-then-dropped pre-0.2.0 as unearned surface; see the audit paragraph below for
  why that ruling changed.
  **The lifecycle is completed to THREE BANDS (§5A of the design doc, amendment before merge):** the
  first adopter found that a crash-checkpoint offer could only be removed by resuming it — `Validate`
  hard-coded `Status == Running` for every caller, `ClearFinished` only ever walked `_finishedOrder`
  (which the checkpoint-registration path deliberately never wrote to), and `PruneHistory` skipped
  offers on purpose — three individually-correct guards composing into a state with no exit at all, and
  that adopter had already shipped exactly this bug and stranded a real deployment on it (paused on DNS
  records, permanently offering Resume, permanently undeletable). **The rule this fixes generalises:
  every non-terminal status must have a sanctioned exit to a terminal one** — enforced by
  `OperationLifecycleInvariantTests`, which enumerates the live `OperationStatus` enum (not a
  hardcoded list) and fails BY NAME if a future non-terminal addition has no registered exit.
  `Validate` is reworked so each transition states what it accepts, instead of one hard-coded
  `Running` check: `Report`/`Wait` require `Running`; `Complete`/`Fail` accept `Running` OR `Waiting`
  (a waiting operation can still fail on a deadline); the public by-id `Cancel(id)` accepts `Running` OR
  `Waiting`, keeping its `Cancellable` permission check; the owner-path terminal cancel accepts ANY
  non-terminal status; `Resume`/`Dismiss` require the WAITING band (`Waiting`). The
  "ignored" diagnostic is also now honest about terminal vs. non-terminal — it used to say "has
  already reached a terminal state" for ANY refused status, which was simply false for a non-terminal
  one.
  New: `OperationStatus.Waiting` — a run that stops mid-flight WITHOUT crashing (expired cloud
  credentials, a throttling provider, DNS not yet propagated, a migration awaiting confirmation, or an
  app's own queue parking a just-started operation), reached via `IOperation.Wait(string? reason =
  null, OperationLabel? detail = null)` (`Running` →
  `Waiting`) and exited via `IOperation.Resume()` (`Waiting` → `Running`, clearing the reason) — both new
  members on `IOperation`. `reason` is an app-defined STRING, like `Kind`, never a kit enum, and
  OPTIONAL (generic-library audit finding 5) — a consumer whose wait is self-evident (the user
  clicked Pause) has nothing to name. `IOperationRegistry.Dismiss(string id)` declines a pending
  `Waiting` offer (`→ Cancelled`, terminal — enters bounded history, publishes an
  ordinary `OPERATION_UPDATED` snapshot like any other terminal transition, unlike `ClearFinished`/
  `RequestResume` which remove an entry and instead publish `OPERATION_REMOVED`, see finding 4 below)
  — it REFUSES `Running` on purpose, because declining an offer and cancelling LIVE work are different
  acts, and this branch's only Critical came from exactly that conflation inside `Cancel`; `Dismiss` is
  a separate member rather than `Cancel` accepting more states for the same reason. It signals the
  entry's own `CancellationToken` first when one exists, so a waiting body still parked on its token
  unwinds.
  `RequestResume`'s drop-vs-keep decision keys on how the entry reached `Waiting`, not on a second
  status (there is only one `Waiting` value — see findings 7 and 8 below) and not on the app-controlled
  `ResumePayload` field either (finding 8 closed that as a residual hole before publish), and the two
  cases are handled asymmetrically ON PURPOSE: an entry reached via an ordinary `Wait()` is LEFT IN
  PLACE (the app calls `IOperation.Resume()` on its own handle once it has actually resumed — the
  client asking is not the state changing) — even when the app also attached its own `ResumePayload` at
  `Start()` time, since the handle is still live either way — while one `RegisterWaiting` reconstructed
  from a checkpoint is still REMOVED (there is no live handle to flip — the process that owned it is
  gone, and this now also publishes `OPERATION_REMOVED { operationIds: [id] }`). The
  `OPERATION_RESUME_REQUESTED` payload also carries `status` (always `Waiting`), so a handler can keep
  branching on that field; a handler can no longer look the entry up afterward for the removed case,
  because it is gone.
  `GetAll` sorts by the three bands, not "Running vs. everything else": Active (oldest first) →
  Waiting (oldest first) → Terminal (newest FINISHED first, tiebroken by
  newest `Sequence` — `TimeProvider.System`'s ~15.6 ms granularity on Windows means two same-tick
  finishes would otherwise fall back to dictionary enumeration order, which reshuffles on unrelated
  churn). `IModuleContext.Run`/`IOperationRegistry.Run` only implicitly `Complete` a body when it is
  STILL `Running` once the work returns — a body that calls `op.Wait(reason)` and simply returns
  ("waiting by returning") is left `Waiting`, not silently stamped `Completed`; resuming it from there
  is the app's own job. `Dismiss` and the public by-id `Cancel(id)` now report exactly what the
  transition actually did rather than an assumed success, closing a narrow race where a concurrent
  `Resume()`/finish landing between the caller's own permission check and the terminal transition's
  own re-validation could otherwise answer a client `true` for a change that did not happen.
  `OperationInfo.WaitReason` is cleared by `Resume()` but RETAINED through a terminal transition
  reached directly from `Waiting` (useful history — "failed while waiting on credentials").

  **Generic-library audit (2026-08-01, before publish — every change below is free since 0.2.0 was
  never published):** the first release absorbed the shape of the ONE app it was
  harvested from on the removal and asking halves of the lifecycle, which that app's own host never
  had to solve. Fixed:
  1. **`ClearFinished` is now `ClearFinished(string? module = null, string? scope = null)`**, mirroring
     `GetAll` exactly, and the `CLEAR_FINISHED` route reads the same two payload keys `LIST` already
     did — it used to take/read nothing, so "clear completed" in one scoped window (a secondary
     window, a scoped container router) silently wiped every OTHER scope's finished history too.
  2. **`OperationOptions.Resumable`/`OperationInfo.Resumable` are REMOVED.** The flag was consulted
     nowhere except `RegisterWaiting`'s own required-true gate — every entry it ever produced had
     already forced it `true` to pass that gate, making it a tautology. `RegisterWaiting`'s
     existing non-empty-`ResumePayload` requirement already expresses "this is resumable" on its own.
  3. **`IOperationRegistry.RequestWait(string id)` is added** — an exact mirror of `RequestResume` for
     the direction the kit previously had no client route for at all. §5A.3 reasoned "pausing is the
     host's own knowledge" from one app's semantics (a host discovering its own blocker); that does not
     hold for the equally-common shape the kit itself already names as a consumer (a
     download-manager-style activity panel) — a human clicking Pause on visible work. `RequestWait`
     emits `OPERATION_WAIT_REQUESTED { operationId, module, kind, scope }` and changes nothing itself
     — the owner's own `IOperation.Wait` is what actually stops the work, same ASK/ACT split as
     `RequestResume` vs. `Resume`. The facade gains a matching `WAIT` route (`{ operationId }` →
     `{ requested }`).
     **`IOperationRegistry.Find(id)` is reinstated** for the same reason: `RESUME`/`WAIT` are both
     client-request routes carrying only an id, and whoever handles them (hearing
     `OPERATION_RESUME_REQUESTED`/`OPERATION_WAIT_REQUESTED`) must translate that id back into a
     handle to call `Resume`/`Wait` — a recurring shape every such consumer would otherwise re-solve
     with its own id→handle map. Safe to hold past the operation's life: every `IOperation` member
     re-validates current status before acting.
  4. **`OperationEvents.Removed` (`OPERATION_REMOVED`, payload `{ operationIds: string[] }`) is added**
     — emitted wherever an entry leaves the registry with no corresponding `OPERATION_UPDATED`:
     `MaxHistory` eviction, `ClearFinished`, and the no-live-handle entry drop inside `RequestResume`.
     The host bounds its own history; the client — the side actually rendering — never heard about it,
     so a status bar that never unmounts accumulated every terminal operation for the whole session.
     This also retires the two hand-written optimistic local prunes `@shenora/react`'s `clearFinished`/
     `resume` actions used to carry (below) — one authoritative event that cannot diverge from the
     host, replacing two guesses that already produced this release's only Critical (a `resume` prune
     that once dropped a live-`Wait()` row the host deliberately keeps).
  5. **Minors:** `Wait`'s `reason` is optional (above); doc comments that illustrated the API with "a
     paused deploy" now say "a waiting operation" (D22 permits domain words as examples, but the cost is
     the kit LOOKING like it ships that product); and a limit is recorded rather than solved —
     `MaxHistory` is one global cap with no per-module/scope bounding seam.
  6. **Progress is not percent (owner direction, before publish — "even its progress it might be
     different than 0-100%"), correcting finding 5's OWN fix above.** Stating "0–100 PERCENT" on the
     write side was the wrong fix to the right observation: percent is not the mechanism, it is one way
     an app happens to measure. `OperationOptions.Progress`/`OperationInfo.Progress` (C#) and
     `OperationInfo.progress` (TS) are now a new record, `OperationProgress(double Value, double? Total
     = null, string? Unit = null)` (TS: `{ value: number; total?: number; unit?: string }`), and
     `IOperation.Report(int? progress, …)` is now `Report(OperationProgress? progress, …)`. `Total`
     is the denominator when known and `null` when there is none (an absolute count with nothing to
     divide by — bytes off a chunked stream); `Unit` is app-defined and uninterpreted, exactly like
     `Kind`/`WaitReason`. **`ClampProgress` (`Math.Clamp(value, 0, 100)`) is REMOVED and nothing
     replaces it** — the registry passes `Progress` through completely unchanged; silently rewriting an
     app's own reported number is worse than passing it through, and a `Value` above its own `Total` is
     the app's bug to see, not the kit's to hide. No validation throw was added either: progress is
     reported from background work on a hot path, and throwing there would kill an operation over a
     cosmetic number. **`Complete()` no longer fabricates `Progress = 100`:** it now sets `Value =
     Total` only when the last report carried a known `Total` (the honest "all of it"), and otherwise
     leaves the last reported value exactly as it was — never inventing a figure the app never gave it.
     `@shenora/react` ships NO percent helper; the README documents the one-liner (`total ? (value /
     total) * 100 : undefined`) because that division is the consumer's own policy, not the kit's. The
     desktop sample and its web counterpart were updated to demonstrate the general shape
     (`new OperationProgress(step, steps, "steps")`, rendered as a ratio because `total` is set) instead
     of the percent special case. Caught before 0.2.0 was pushed or published, so free.
  7. **The status collapse (owner direction, before publish — "I don't even think we need any specific
     status than regular — think about this is going to be structured like XHR").** `Paused` and
     `Interrupted` — introduced above as two states — collapse into ONE, `OperationStatus.Waiting`:
     every transition already treated them as one band (`Dismiss`/`RequestResume` both accepted either,
     neither was ever pruned, the client's `waiting` getter already unioned them), and the one place
     they actually diverged (`RequestResume` dropping the crash-checkpoint case, keeping the live-`Wait()`
     case) was always about whether the entry had a live handle, which `ResumePayload` already told the
     registry on its own. Renamed throughout, mechanism not scenario (D22):
     `OperationInfo.PauseReason` → `WaitReason`; `IOperation.Pause` → `Wait(reason?, detail?)`;
     `IOperationRegistry.RegisterInterrupted` → `RegisterWaiting`; `RequestPause` → `RequestWait`;
     `OperationEvents.PauseRequested`/`OPERATION_PAUSE_REQUESTED` → `WaitRequested`/
     `OPERATION_WAIT_REQUESTED`; facade route `PAUSE` → `WAIT`; client `OperationStatuses.Paused`/
     `.Interrupted` and the `paused`/`interrupted` getters → `Waiting: 'waiting'` (the existing
     `waiting` getter is now the whole band; the two half-getters are DELETED, not deprecated).
     `IOperation.Resume`/`RequestResume`, `Dismiss`, `OPERATION_RESUME_REQUESTED`, `RESUME`, `DISMISS`
     keep their names — resuming and dismissing were already mechanism words. `RequestResume`'s
     drop-vs-keep read `ResumePayload` directly instead of a second status at this point (finding 4's
     asymmetry paragraph above was updated in place to describe this) — **closed further by finding 8
     below**, since that field turned out not to be a safe signal either. Also closes a known limit finding 5 above
     recorded rather than solved: "registered but not yet started" is now representable with no kit
     change — an app calls `Wait("queued")` on the handle immediately after `Start`, before real work
     begins. Full rationale: `docs/DECISIONS.md` D23's amendment. Caught before 0.2.0 was pushed or
     published, so free.
  8. **Keying `RequestResume`'s drop-vs-keep decision on `ResumePayload` (finding 7 above) was itself a
     residual hole, closed before publish, so also free.** `ResumePayload` is APP-controlled data — an
     app may attach one to `OperationOptions` at `Start()` — so it could not reliably answer "does this
     entry have a live handle": an app that did so and then called `Wait()` had a genuinely LIVE
     operation (handle intact, body parked) dropped exactly like a crash checkpoint, silently orphaning
     later `Report`/`Complete`/`Fail` calls on it. `RequestResume` now keys the decision on an internal
     `Entry.Reconstructed` flag instead, set only by `RegisterWaiting` (the one call site that
     legitimately reconstructs an entry with no live body) — never exposed on `OperationInfo`, since no
     consumer needs it and every public member is SemVer surface at 1.0. `ResumePayload`'s other roles
     are unchanged (`RegisterWaiting`'s non-empty requirement, the dedupe key, riding
     `OPERATION_RESUME_REQUESTED`). Full rationale: `docs/DECISIONS.md` D23's amendment.
- **`@shenora/react`: `useShenoraOperations` / `createOperationsStore`** — the client half of the
  primitive above, built the same way `createShenoraStore` already was: `OperationStatuses` (wire
  values, including `Waiting` — collapsed from the originally-shipped `Paused`/`Interrupted` pair, see
  finding 7 above) + `OperationInfo`/`OperationLabel` types (`OperationInfo.waitReason`
  mirrors the host's `WaitReason`), a `LIST` snapshot on first subscribe (so a progress strip that
  mounts mid-run isn't empty), folding `OPERATION_UPDATED` by id afterward, with `running`/
  `waiting`/`finished` DERIVED getters computed from `byId` on every read (`waiting` is now a
  single-status filter, exactly like `running` — the originally-shipped `paused`/`interrupted`
  half-getters and the internal status set that unioned them are DELETED, not deprecated, now that
  the host carries only one waiting value; `interrupted` had been added because it used to fall into
  NO getter at all: not `running`, not `paused` — matched only the literal `'paused'` — not `finished`,
  reachable only by hand-filtering `byId`) and `cancel`/`dismiss`/
  `wait`/`clearFinished`/`resume` actions. `wait` (generic-library audit finding 3; shipped at the
  time as `pause`) posts `WAIT`
  (`{ operationId }`) and touches no local state, mirroring `dismiss`'s shape — asking is not acting.
  **`clearFinished`/`resume` no longer carry an optimistic local prune (generic-library audit finding
  4, folded into 0.2.0 before publish):** they used to guess at what the host had removed, because
  removals had no wire event at all — `clearFinished` pruned every entry in the TERMINAL status set,
  and `resume` pruned only the `interrupted` case to mirror the host's own asymmetry (§5A.4). One of
  those guesses was this release's only Critical: `resume`'s prune once dropped a `paused` row the
  host deliberately keeps, making the still-parked entry unreachable until every subscriber unmounted
  and a fresh `LIST` ran. The host's new `OPERATION_REMOVED { operationIds }` (see finding 4 above) is
  now the ONE authoritative removal signal — folded by deleting exactly the named ids, regardless of
  status — so `clearFinished`/`resume` are now plain fire-and-forget posts (forwarding this store's own
  configured `scope`, generic-library audit finding 1) with no client-side guess left to diverge from
  the host. `dismiss` still mirrors `cancel`'s shape and needs no removal handling at all — the host's
  `Dismiss` publishes an ordinary terminal snapshot for the entry, same as a real cancel, since it
  transitions rather than removes.
  `createOperationsStore({ module?, scope? })` supports a renamed host module
  (avoiding a collision with an app's own module name) and a scope-filtered instance. **Known limit,
  deliberate:** no `byModule`/`byScope` selector — filtering by module or scope is a one-line consumer
  selector over `byId`, and shipping indexes for it would be duplicated derived state for no gain.
- **`Shenora.Ipc.NotificationPump`(+`NotificationPumpOptions`)** — the transport-neutral half of a
  host's outbound notification channel (bus subscribe from CONSTRUCTION → per-channel filter →
  bounded drop-oldest queue → batch → ready gate → guarded per-notification serialize), extracted out
  of `WebViewIpcBridge` so a second, non-WinForms base inherits these already-fixed bugs (P5.5 H2/H3)
  instead of re-earning them — D16's "the seam, not the package" applied to the HOST half of the
  outbound path (the client half, `ShenoraTransport`, has been base-agnostic since P3). The pump owns
  no timer and no transport: which thread may touch a base's client is a base-specific fact, so the
  base drives its own tick (a `Forms.Timer` on WinForms; a `PeriodicTimer` on a headless base) and
  calls `TryDrainBatch`. `WebViewIpcBridge` is now a thin adapter over it, keeping only what is
  WinForms/WebView2: the timer, `WebMessageReceived`, the `ContentLoading`/`READY`/`ProcessFailed`
  gate wiring, and `PostWebMessageAsString`.
- **Per-channel notification filtering** — `NotificationPumpOptions.Filter` /
  `WebViewIpcBridgeOptions.NotificationFilter`, applied at enqueue. Every bridge previously subscribed
  with `SubscribeToAll`, so with two windows every bus event reached both — an auxiliary session or a
  remote client would receive the whole app's traffic with no way to narrow it. Default: deliver
  everything, unchanged for an app that doesn't need the seam.
- **`@shenora/react` exports `OperationProgress`, `OperationEventTypes` and `OperationModuleName`**
  (whole-codebase review, before publish). `OperationInfo.progress` is typed as `OperationProgress`
  and `OperationInfo` was exported, so the field's own type was unnameable from outside the package —
  the tell is that the kit's OWN sample re-declared the shape inline (`{ value: number; total?:
  number; unit?: string }`) to write a one-line formatter. The other two close the same gap for the
  two events `createOperationsStore` deliberately does not subscribe to
  (`OPERATION_RESUME_REQUESTED`/`OPERATION_WAIT_REQUESTED`, which target the OWNING module's own
  service): the app writing that handler had to hard-code the literals the wire-mirror tests exist to
  stop it hard-coding. **The barrel gate could not have caught any of it** — `index.test.ts` compares
  `Object.keys(barrel)`, and a type has no runtime binding; the type half is now pinned by a
  type-only import in that same file, which `npm run typecheck` (the full tsconfig, which includes
  tests) compiles. Verified by sabotage: dropping `OperationProgress` from the barrel fails the
  typecheck naming it.

### Removed

- **The crash-checkpoint half of the operations cluster: `IOperationRegistry.RegisterWaiting`,
  `OperationOptions.ResumePayload` and `OperationInfo.ResumePayload` (and `resumePayload` on the TS
  mirror).** The 0.2.0 design pass, prompted by the owner asking a review to judge the DESIGN rather
  than only the code. The kit's own bar is "generalize what the survey shows at least TWO apps need"
  (`generic-library.md`), and the design doc's §4.2 provenance note had already admitted in writing
  that `Interrupted`/`ResumePayload`/`RegisterWaiting`/`RequestResume` "come from **one** app, not
  two". Shipping it anyway cost more than it carried: that cluster took roughly eight reshapes inside
  this single unpublished release and produced the release's only Critical.
  **The root cause was structural, not a sequence of unlucky bugs.** Accepting an entry the kit had
  never started meant every caller had to answer "does this one still have a live body?" — and each
  answer failed in its own way. A second status (`Interrupted`) turned out to have no terminal exit at
  all, stranding operations forever. Keying on `ResumePayload` read APP-controlled data, so an app that
  attached a token at `Start()` and then called `Wait()` had a genuinely live operation dropped out of
  the registry. An internal provenance flag finally worked, at the cost of a concept no consumer could
  see. Removing the question removes all three.
  **What stays, and why it is not the same thing:** `OperationStatus.Waiting`, `IOperation.Wait`/
  `Resume`, `Dismiss`, and the `RequestWait`/`RequestResume` ask-act pair. Those are the
  download-manager shape the kit itself names as a consumer — a human clicks Pause, then Resume — and
  cutting `RequestResume` too would have left a client able to pause but never resume. `RequestResume`
  is now an EXACT mirror of `RequestWait`: validate, emit, change nothing. Its payload drops
  `resumePayload` and `status` (the latter carried no information once there was one reach), so both
  ask-events are `{ operationId, module, kind, scope }` — pinned by a new test.
  **Migration:** crash recovery is the app's, which is where the checkpoint already lived — the kit
  only ever held an opaque token it could not interpret. Keep the token in your own store; on restart,
  begin the resumed run as an ordinary `Start()`/`Run()`. If you want the pending offer visible while
  the user decides, `Start()` it and immediately `Wait("interrupted")` — the same one-line shape that
  already covers "registered but not yet started".
- **`OPERATION_REMOVED` no longer fires from `RequestResume`** (it never removes an entry now). Its
  two remaining sources — `MaxHistory` eviction and `ClearFinished` — are unchanged, and the client
  folds it identically.

### Added

- **Work scheduling + filesystem claims in `Shenora.Core`** — `IWorkScheduler`/`WorkScheduler`,
  `WorkClaim`/`IClaimScope` (`FlatClaimScope`, `NestedClaimScope`), `ILane`/`WorkLane`,
  `IWorkPolicy`/`PriorityWorkPolicy`, `IWorkObserver`, `IWorkStore`/`WorkRecord`/`RecoveryPolicy`,
  and `PathClaims`. Design + evidence: `docs/2026-08-02-shenora-work-scheduling-design.md`.

  Harvested from all three donor apps, where the same two problems had been solved **five times and
  differently**: two file-operation planners (545 and 603 lines, one an event-driven path-overlap
  dispatcher, the other a two-plan single-worker model), two job queues (463 and 664 lines), a global
  GPU gate and a lane-holding capacity governor.

  **The design claim is that these are ONE mechanism.** A filesystem planner is a scheduler keyed by
  PATH, where two keys conflict if one contains the other; a job queue is a scheduler keyed by LANE,
  where a key admits N holders. Submission order, bounded parallelism, event-driven dispatch, dedup,
  retry and cancellation are identical — and each sibling rebuilt all of it. So the kit ships one
  engine plus two small key strategies, which is what makes adoption a deletion rather than a
  translation.

  Two behaviours are better than any source rather than equal to them, and both fall out of the model
  rather than being fixed by hand: the per-key semaphore **ref-count race** disappears (the scheduler
  owns claim lifetime, so there is no per-key lock object to remove), and the documented **lock-order
  rule** stops being a rule anyone must remember (claims are acquired as a set, so deadlock is
  structurally impossible). Shared claims — a reader/writer split none of the sources could express —
  are new.

  Scheduling POLICY is the app's: `IWorkPolicy` supplies *what* to pick up (`Compare`) and *when*
  (`ShouldStart`). It is consulted only about work already found safe to run, so a custom or buggy
  policy can delay work but never corrupt it. Durability is a seam (`IWorkStore`) with **no
  implementation shipped** — storage is the app's choice; recovery defaults to failing records found
  RUNNING after a crash, because re-running work that may have caused the crash produces a boot loop.

  33 tests. The concurrency ones assert parallelism **and** exclusion in the same run — correctness
  alone would pass a fully serial implementation — and were sabotage-verified both ways: forcing
  capacity 1 fails exactly the five parallelism assertions by name, and dropping the separator
  boundary check fails exactly the sibling-prefix case.

### Changed

- **`dev.mjs sample` now builds the packaged frontend before launching** (skip with `--no-build`;
  `--dev` is unaffected, vite serves source there). It was a bare `dotnet run`, and Production mode
  serves the EMBEDDED `wwwroot` — a gitignored local build output — so it silently ran whatever
  bundle was on disk. Found by hands-on testing: the sample's drop zone showed no hover feedback
  because the bundle predated the `.drop-hover` rule by three days. That makes the verification path
  itself unsound, since `phase-workflow.md` proves desktop behaviour against the sample. Full account
  in `docs/archive/fix-log.md`.
- **D25 — frameless chrome and native drop zones recorded as the kit's flagship pair**, settled after
  live testing; not open to redesign on symmetry or cohesion grounds without adopter evidence. See
  `docs/DECISIONS.md`.
- **`docs/ADOPTION.md`'s drop-zone entry now states the GAIN, not just the wiring.** It described
  accurately how to attach `DropZoneManager` and never said why an app would want it. It now leads
  with the capability an app cannot get any other way: an HTML5 drop hands the page a blob and
  withholds the path, so a page-side target cannot open, hash, watch or move the dropped file — the
  native overlays read the OS drag data and yield the real path, including drags from another app
  while the window is backgrounded. A callout under the Stage-1 table carries the dedup case (four
  independent ports of this one component across the family). Docs only.
- **The genericity rule finally has a tripwire — `SurfaceVocabularyTests`.** The owner's standing
  review criterion is *"make sure this is a library — we're not solving specific business logic;
  everything here has to be generic enough that any of our applications can adopt it"*, and it was
  the only load-bearing invariant in the repo with nothing watching it: `ApiSurfaceTests` is a SemVer
  gate that proves the surface CHANGED, and its documented workflow (copy `.actual` over the
  baseline) waves domain vocabulary straight through. Every public TYPE name is now checked against
  an allow-list of shell/platform words (`tests/Shenora.Tests/Api/surface-lexicon.txt`); an unknown
  word fails the build and the author either renames the type (D22) or argues the word onto the list.
  Allow-list rather than a blocklist of business nouns, because a blocklist only catches the domain
  words someone already imagined — and listing the private siblings' nouns in a tracked file would
  leak what those apps do. Derived from the 147 public types then shipping: 134 words, every one a
  mechanism, so the kit passed its own criterion on the day the gate was written. Sabotage-verified
  both ways, and a second test fails if the lexicon keeps words no type uses. No surface change.
- **`Shenora.AppCallback.Log(Action<string>? sink, Func<string> message)`** — the guarded, lazy
  diagnostic helper existed as FIVE byte-identical private copies (`WebViewHost`,
  `WebViewIpcBridge`, `EmbeddedResourceProvider`, `NotificationPump`, `OperationRegistry`), the same
  "N copies of the rule that must never be broken" shape `IpcErrorMapping` was collapsed for. One
  owner now, on the type that already owns the callback-guard policy. Additive; no behaviour change.
- **D16's host half is now EXECUTED rather than asserted — no code change was needed, which is the
  result.** `NotificationPump` was extracted in this release "so a second, non-WinForms base inherits
  these already-fixed bugs", and no second base existed, so nothing had ever run the kit's IPC stack
  without a Windows presentation layer. A throwaway spike (`devtools/_transport-spike/`, gitignored
  like `_dpi-probe` before it) did: a `net10.0` console app referencing ONLY `Shenora.Core` +
  `Shenora.Ipc`, with a pair of channels standing in for a socket, ran a typed request/response, the
  structured error boundary (`OperationException` → its code; unknown route → `NO_HANDLER`), the pump
  driven by a `PeriodicTimer` instead of a `Forms.Timer`, and a `ctx.Run` operation streamed back as
  batched notifications — all green. **The target framework is the proof**: a Windows type anywhere in
  that graph turns the project red, the same enforcement `samples/Shenora.Sample.Logic` already gives
  app logic, applied to the host half. Follow-ups it surfaced are recorded in `TASKS.md` rather than
  built, since one spike is one consumer and the kit's bar is two.
- **`dev.mjs verify`/`doctor` gained `doc-drift` — the gate the prose never had** (0.2.0 design pass,
  D4). Every code invariant in this repo has a test; no doc claim had anything, and the review that
  prompted this pass found 8 of its ~13 findings in comments and docs. Two PRECISE checks rather than
  one fuzzy sweep, because docs are full of BCL names, TS symbols and deliberately-historical
  references and a matcher that cries wolf gets switched off: **(1)** the dependency graph drawn in
  `README.md`/`docs/ADOPTION.md` is compared against the actual `ProjectReference`s — the check that
  would have caught both files documenting a `Shenora.WinForms → Shenora.Ipc` edge that has never
  existed; **(2)** names listed in `devtools/retired-names.txt` may not be stated as a CURRENT fact.
  Since this repo's docs are amendment stacks, (2) allows a retired name in the PAST tense (it looks
  for "used to / former / renamed / removed / superseded / …" around the mention) and takes an
  explicit `doc-drift:history` marker for a preserved design sketch or rename table.
  It found real drift on its first run: `webview2-hosting.md` still said `LoginWindow.ClearProfile`
  and `CoBrowseSession.StartAsync`, `generic-library.md` still cited `LoginWindow` as a current
  in-repo example, and `REVIEW-GUIDE.md` still told reviewers `CookieLoginFlow` "keeps its scenario
  name deliberately as the one reference driver" — which P7 reversed when it moved that driver out of
  the kit. All corrected. Both checks are sabotage-verified.
- **Frameless chrome stays a FIXED WinForms type, and the caption-button DRAWING moved out of
  `OptimizedForm` into an internal `CaptionButtonRenderer`** (0.2.0 design pass, D24). The review
  flagged `OptimizedForm` as the kit's one inheritance-only feature and proposed making the chrome
  attachable; that was rejected on the evidence — the window style belongs in `CreateParams` at handle
  creation, and attaching it later needs `SetWindowLong`+`SWP_FRAMECHANGED` as a second mechanism,
  doubling the verification surface in the one area where a green unit suite has twice been the wrong
  answer here (P5.6). The cohesion complaint was fair, though, so the part with NO message-loop
  responsibility was split out: palette fallback, glyph selection, the DPI-scaled icon font and the
  painting. `OptimizedForm` 998 → 905 lines. **No public surface change** — the renderer is internal
  and the form's behaviour is identical. The reusable rule (D24): extract what is pure input →
  pixels; leave anything that answers a window message where the OS can see it.
  New direct tests cover glyph choice, the fallback palette, DPI font scaling and its cache — none of
  which previously had any, since they were unreachable without a real window. One of them pins that
  every glyph is a single Private Use Area codepoint, guarding the documented CJK-locale mojibake trap
  that otherwise turns a caption button silently blank; sabotage-verified (a mangled glyph fails it
  reporting `Actual: 63`).

### Fixed

- **`OperationInfo` had no cross-language field mirror** — the single biggest shape on this wire (it
  is both the whole `OPERATION_UPDATED` payload and the `LIST` element) while the much smaller, newer
  `OperationProgress` had one. It was missed behind a plausible claim recorded in that test's own doc:
  "`OperationInfo`'s other fields are pinned by `[JsonPropertyName]` + the API baseline". Both halves
  are true and together they prove nothing about the MIRROR — they pin the host's names against the
  host's own baseline, and nothing compared them to the TS interface. Found when the cut above removed
  a field from both sides by hand and nothing verified that both hands had moved.
  `WireMirrorTests.OperationInfo_fields_match_the_host` now checks it in both directions, sabotage-
  verified (a client-only `resumePayload` fails naming it).
- **Docs on shipped surface still described `RequestResume`'s superseded rule** (whole-codebase
  review, before publish). Five XML/JSDoc sites and three docs said the drop-vs-keep decision is told
  apart by `ResumePayload`; the released behaviour keys on the registry's own internal provenance
  record (see the `### Breaking` note above and D23's closing amendment). An adopter following the
  shipped doc would attach its own `ResumePayload` at `Start()` and expect `RequestResume` to drop the
  entry — the kit now keeps it, which is the whole point of the fix. Corrected in
  `OperationStatus.Waiting`, `IOperationRegistry.RegisterWaiting`, the three TS mirrors in
  `operations.ts`, `docs/ARCHITECTURE.md` (which contradicted its own `RequestResume` paragraph 50
  lines earlier), `docs/ADOPTION.md`, and the design doc's §4.3/§5A.2/§5A.4.
- **`README.md`/`docs/ADOPTION.md` documented a dependency chain the packages do not have** — both
  drew `Shenora.WinForms → Shenora.Ipc`. The graph is a DIAMOND over `Shenora.Core`:
  `Shenora.Ipc` and `Shenora.WinForms` are siblings, and `Shenora.WebView2` is the first package that
  sees both. `Shenora.Ipc` targets `net10.0` and binds to no UI framework — that is what D16's
  transport story rests on, and why the two IPC-facing desktop facades live in `Shenora.WebView2`
  rather than either base. An adopter following ADOPTION Stage 0/1 for "a shell with no web frontend"
  would reference `Shenora.WinForms`, write a `BaseFacade`, and get an unresolved-namespace error the
  docs said could not happen. Both now show the real graph, the TFM per package, and the explicit
  "add `Shenora.Ipc` as a second reference" note.
- **`README.md` still said "Not yet published to NuGet/npm"** — stale since 0.1.0 and the first thing
  an evaluating reader saw, directly under the version headline (first-adopter finding, 2026-07-31).
  The package table also gained a target-framework column, so an adopter no longer has to download a
  nupkg to learn whether it fits (same finding).
- **`Shenora.WebView2.Sessions`' NuGet package description still shipped the scenario vocabulary D22
  removed from the types** — "login windows … (silent refresh, cookie capture)" and "co-browse
  streaming primitives", for types renamed `InteractiveSession`/`StreamingSession` in P5.5 H9.7/H9.8.
  D22's audit method is "sweep the API baselines for domain words", and a csproj `<Description>` is in
  no baseline — while being the single most public place that vocabulary appears (the nuget.org
  listing). Also renamed the off-screen window's caption and two log messages, which are externally
  readable for the same reason.
- **`InteractiveSession`'s loading-fallback timer invoked the app's `OnLoading` unguarded.** A
  WinForms timer tick has no caller on its stack, so a throwing splash toggle (`ObjectDisposedException`
  is the obvious way) was an unhandled UI-thread exception — the bootstrap's modal crash dialog. The
  same callback was already guarded on the two paths below it in the same method, with a comment
  recording what one unguarded `OnLoading` cost last time. Now routed through `AppCallback.Run`.
- **`EmbeddedResourceProvider` called the app's `Log` sink directly at seven sites**, two of them
  inside `BeginWarmup`'s fire-and-forget `Task.Run` where a throwing sink escapes the `catch` it is
  reporting from and becomes an unobserved task exception. All seven now go through the guarded, lazy
  `Log(Func<string>)` every other type in the kit uses.
- **`DropZoneManager` emitted with `_ = EventBus.EmitAsync(…)`** — the discard shape `IEventBus.Emit`
  was added in P6.4 to replace, and whose doc says a caller should not have to read the implementation
  to know the discard is safe. It was the kit's only in-repo emitter and it did not use its own member.
- **Stale/self-contradicting XML docs:** `DropZoneFacade` recommended mapping through
  `AddMessageDispatcher`'s configure callback — the advice `WindowCommandFacade`'s doc already records
  as impossible (that callback runs before any form exists, P5.5 H6); `SessionEnvironmentCache` said
  `WebViewEnvironment` "still has" the faulted-task-caching trap and cited a `TASKS.md H3` that no
  longer exists (H3 fixed it, and the two now share one shape); `ModuleContext` said it is built "at
  construction" while `BaseFacade` builds it lazily and says why; `docs/ARCHITECTURE.md` carried
  "known limit: a mapped module cannot be released" in the same sentence that lists
  `TryReleaseModule`.
- **Recorded a real known limit in its place: `IModuleRegistry` cannot see DI-registered facades.**
  `AddMessageDispatcher` maps them through `MapRegisteredModulesLazily` (one terminal middleware) and
  not through `TryClaimModule`, because claiming needs the module names and resolving facades inside
  the `IMessageDispatcher` singleton factory is the silent `StackOverflow` P5.5 H2 fixed. So
  `IsModuleMapped` answers `false` for a routed module, and a plug-in offering a name a DI facade owns
  gets `true` from `TryMapModule` and then never runs. Precedence is correct; the answer is not.
  Documented on `TryMapModule` and in `ARCHITECTURE.md` rather than guessed at — closing it needs a
  name-reservation seam or re-opening the deadlock, and no consumer has hit it.

## 0.1.2 — 2026-07-31

### Changed

- **`WindowStateManager.Apply(Form)` and `AttachTo(Form)` now resolve per-monitor DPI by default.**
  The parameterless overloads defer to `HandleCreated` when the form has no handle yet, then
  resolve `DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)` at that moment — still before `Show`,
  so the restored geometry lands on the initial paint with no resize flash. On a mixed-DPI setup
  the form is now sized against ITS monitor's DPI, not the primary. The 0.1.1 default used
  `DpiHelper.SystemScale()` (the PRIMARY monitor) synchronously; adopters had to know two
  kit-internal details — that `DeviceDpi` was the right source and that `OnHandleCreated` was
  the only valid moment — and call the explicit `AttachTo(form, DpiHelper.ScaleFromDeviceDpi(
  form.DeviceDpi))` overload themselves. The scale-explicit overloads are unchanged and remain
  as the escape hatch for callers who want to size against a scale they resolve themselves
  (a test harness, a preview against a different monitor). Reported by the first adopter after
  Stage 1 adoption on 0.1.1.

### Fixed

- **`WindowStateManager.Apply` now defers the maximize application to `Shown` for a plain
  `Form` too.** In 0.1.1 the `RestoreMaximizedTag` deferral was `IAppMaximizable`-only; for a
  plain `Form`, `Apply` set `form.WindowState = FormWindowState.Maximized` synchronously — which
  goes back to `Normal` by `OnLoad`, so a window opened restored-down however it was closed.
  The fix extends the existing marker mechanism to plain forms via a one-shot `Shown` handler
  that consumes the same tag. Same shape `IAppMaximizable` implementors already had, one owner
  for "apply maximize once realized". Not a kit regression — the hand-rolled predecessor code
  had the identical bug — but the kit is the right place for it to be fixed once. Reported by
  the first adopter.
- **`WindowStateManager.Apply(Form)` now pre-positions the handle to the saved location before
  resolving `DeviceDpi`, closing a cross-monitor mixed-DPI hole in the initial fix.** The first
  cut of the `HandleCreated` defer read `form.DeviceDpi` immediately — but the handle is
  created wherever WinForms/Windows initially places it (typically the primary monitor, since
  `Location` hasn't been set yet), so on a mixed-DPI setup with a saved position on a
  different-DPI secondary monitor, `DeviceDpi` returned the wrong value and the restored size
  was computed against the wrong scale. The fix moves the handle to the saved location first;
  the move triggers `WM_DPICHANGED` synchronously, updating `DeviceDpi` to the target monitor
  before the scale is resolved. There is no auto-heal to fall back on — the WinForms default
  `WM_DPICHANGED` handler does not rescale a Form's outer `Size` (verified live in
  `devtools/_dpi-probe/`: Windows' `SuggestedRectangle` came back unchanged after a 200% → 150%
  scale change). Caught by adversarial phase review of the first-cut commit.

## 0.1.1 — 2026-07-31

### Added

- **`WindowStateManager.Apply(Form, double scale)` and `AttachTo(Form, double scale)` overloads**
  for per-monitor DPI accuracy. The existing parameterless forms use `DpiHelper.SystemScale()` —
  the PRIMARY monitor — because that is usable before the form has a handle, not because it is
  the most accurate answer: a form opening on a secondary monitor with a different DPI would then
  be sized to the wrong physical size. Callers who can defer to `OnHandleCreated` (handle exists
  → `DeviceDpi` reflects the real monitor, still before `Show` → no resize flash) call
  `AttachTo(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi))` instead. The paired `AttachTo`
  overload was added so that adoption path does not lose the save-on-close ordering guarantee
  `AttachTo` exists to protect (P5.5 H4.5). Reported by the first adopter.
- **`WindowStateOptions.MaxToWorkArea` (default `true`)** — shrink the restored physical size to
  the target monitor's work area when a size saved on a bigger display would overflow a smaller
  one (moving to a laptop, unplugging an external monitor). The MinWidth/MinHeight floor still
  applies. **Behaviour change** for the default case: a saved size that would previously overhang
  now fits — which was the point. Set `MaxToWorkArea = false` for the pre-0.1.1 behaviour.
  Position is validated separately by `IsVisible`, unchanged.
- **`WindowStateManager.ToPhysical` overload taking `IEnumerable<Rectangle> workAreas`** — the
  work-area-aware pure conversion that powers the clamp above. The three-argument overload is
  unchanged and continues to skip the clamp (documented).

### Fixed

- **`docs/ADOPTION.md`: the "hand-rolled uses `Screen.WorkingArea`, kit uses `GetMonitorInfo`"
  fix claim moved from the `WindowStateManager` row to the `OptimizedForm` row**, where the P/Invoke
  actually lives (`TryGetCurrentWorkArea`). The `WindowStateManager` row previously overpromised:
  an adopter taking that primitive without also adopting `OptimizedForm` did not get the fix,
  which they only discovered by reading the source. Reported by the first adopter.
- **`docs/ADOPTION.md`: Stage 1's "highest payoff" heading rephrased** — payoff is proportional
  to what the adopter actually hand-rolled. The row-by-row wording is unchanged; the intro now
  says each row = a specific replacement rather than a claim that every app benefits from every
  row (an adopter that already had a C++ splash launcher, no single-instance mutex and injectable
  shell delegates only saw two rows apply).

## 0.1.0 — 2026-07-31

### Breaking

- **`MapModule(IModuleFacade)` now THROWS when the module is already mapped**, instead of accepting
  it silently. A facade answers every request for its module, so a second mapping was always dead
  code — it simply never ran, with no error and nothing to grep for. This matches the eager DI path
  (`MapRegisteredModules`), which has always guarded duplicates. **Migration:** if a taken name is a
  normal outcome for you rather than a composition bug — dynamically composed modules — call
  `TryMapModule`, which returns false instead. Nothing in a static composition is affected: every
  module is mapped once.
- **`LoginWindowController` is now `SessionController`** (P5.5 H4.6). It was never login-specific:
  `CoBrowseSession.Controller` is typed with it and exposes it publicly, so a co-browse consumer —
  streaming a page for remote viewing, nothing to do with signing in — had to program against a
  login-named type. Pure rename: same members, same behaviour, and the types that ARE
  login-specific keep their names (`LoginWindow`, `LoginResult`, `LoginErrorCodes`,
  `CookieLoginFlow`, `LoginCookie`). Update the type name where you name it explicitly —
  `LoginWindow.RunAsync`'s driver signature and `CookieLoginFlow.DriveAsync` both mention it.
  Deferred deliberately: extracting a genuinely shared base out of `RenderSession` and
  `SessionController`. The neutral NAME is what fixed the surface problem; what the shared core
  should actually be is better decided when the co-browse API is reshaped (D21 / H9) than guessed at
  now.
- **The two Windows packages are now one layer, and the portable contracts moved to
  `Shenora.Core`** (D19 + D20; design: `docs/2026-07-30-shenora-relayering-design.md`).
  `Shenora.WebView2` now depends on `Shenora.WinForms` — the boundary is Windows *primitives* and
  *web hosting on top of them*, not two peers. `WinForms` still carries no `Shenora.Ipc` dependency,
  and `WinForms → WebView2` remains forbidden.
  **What a consumer must change:** add `using Shenora;` where these types are referenced —
  `IFileDialogs`, `IFileDialogPathStore`, `FileDialogOptions`, `FileDialogFilter`,
  `FileDialogResult`, `IClipboardService` moved namespace (identical signatures otherwise). Nothing
  needs re-registering: `UseWinForms` registers the same implementations, now behind both the
  Windows and the portable interface.
  `IShellLauncher` and `IFormInteraction` were **split**, not changed: they now derive from
  `Shenora.IUrlLauncher` and `Shenora.IUiInteraction` respectively, so `OpenUrl`,
  `BlockInteraction` and `UnblockInteraction` are inherited rather than declared. Existing call
  sites compile unchanged; code that *implements* these interfaces still implements the same member
  set. Depend on the portable base where you only need the portable operation, and your logic
  compiles with no Windows reference — the point of the change (D16: mobile shells are a target).
- **`DpiHelper.ScalePixels`, `ScaleSize` and `ScalePoint` are removed** (P5.5 H6). They had no callers,
  and they were worse than unused: each baked in the PRIMARY monitor's scale, so any code that adopted
  them would silently mis-scale on a secondary monitor. Use `DpiHelper.Scale` with the DPI you mean —
  `ScaleFromDeviceDpi(control.DeviceDpi)` for anything attached to a control, `SystemScale()` only when no
  control exists yet.
- **`@shenora/react` no longer augments the global `Window` type** (P5.5 H6). The package shipped
  `declare global { interface Window { chrome?: … } }` in its `.d.ts`, which collides with `@types/chrome`
  in a consumer's program as an unfixable TS2717 in a file they do not own. A library must not claim
  global names; the transport now reads `window` through a local interface. No runtime change.
- **The dispatcher's composition helpers moved from `MessageDispatcher` onto `IMessageDispatcher`**
  (P5.5 H6). `Use(MessageMiddleware)` — the single primitive all of them already delegated to — is now an
  interface member, and `UseModule`/`UseRoute`/`UseLogging`/`UseErrorHandler`/`MapRoute`/`MapModule`/
  `UseScopedRouter`/`MapRegisteredModules`(`Lazily`) are extension methods over the interface
  (`MessageDispatcherExtensions`). **Why:** the interface exposed only dispatch/send, so a composition that
  maps a facade AFTER the container is built — the documented pattern for anything needing the live
  window — had to downcast. The reference composition did, and its `if (dispatcher is MessageDispatcher
  concrete)` had no `else`: registering a different `IMessageDispatcher`, or wrapping it in any decorator,
  silently dropped three whole modules and the frameless title bar just stopped working with no error.
  Adopters copy that branch.
  **What you must change:** almost certainly nothing — `dispatcher.MapModule(…)` etc. still compile
  through extension resolution. A fluent chain whose result you assign to a `MessageDispatcher`-typed
  variable now yields `IMessageDispatcher`; `AddMessageDispatcher`'s configure callback receives
  `IMessageDispatcher` instead of `MessageDispatcher`; and a custom `IMessageDispatcher` implementation
  must add `Use`. `UseLogging`/`UseErrorHandler` gained an optional `ILogger` and default to the
  dispatcher's own logger, so behaviour is unchanged.
- **`IpcResponse.CreateError`'s argument order now matches `OperationException`'s** (P5.5 H6):
  `(id, code, parameters, message)`, previously `(id, code, message, parameters)`. The two are siblings
  that build the same structured error from the same pieces, and they disagreed about the last two — so
  which one you were calling decided what a positional third argument meant. The shared order puts the
  wire-relevant piece first: `parameters` crosses to the client as i18n interpolation values, `message`
  is host-log only. Calls using `parameters:`/`message:` by name are unaffected; a positional third
  argument now fails to compile rather than silently landing in the wrong slot.
- **`BaseFacade` no longer calls `ConfigureAwait(false)` around your `RouteMessageAsync`** (P5.5 H6). It
  was the only such call in the dispatch path and it contradicted the documented context-preserving
  model — a facade routing a window command must be able to resume on the UI thread. If your facade
  relied on being resumed off the captured context, marshal explicitly.
- **`WebViewHost.AutoReloadCooldown` moved to `WebViewHostOptions.AutoReloadCooldown`** (P5.5 H3). It
  was a public static field, so it was neither per-host nor configurable. The new
  `WebViewHostOptions.MaxAutoReloads` joins it — see Fixed for why a cap was needed at all.
- **`OptimizedForm` is no longer a drop target.** It used to set `AllowDrop = true` with a `DragOver`
  handler, justified as letting a drop-zone manager see drags over the form — which is not how OLE drop
  works: targets are registered per HWND and `DropZoneOverlay` registers itself, so nothing in the kit
  ever used the form's drag events. All the flag did was force OLE (hence STA) on every consumer of the
  base class, and show a copy cursor for a drop it then silently discarded, since there was no
  `DragDrop` handler. If your app relies on form-level drops, set `AllowDrop = true` and wire your own
  handlers — plain WinForms, nothing needed from us. The IPC drop zones are unaffected.
- **The auxiliary-session surface is named for MECHANISM, not for scenarios** (P5.5 H9.7 + H9.8, D22).
  Two clusters of the public API were named after ONE use case each while containing no logic specific
  to it, which made the kit look like it shipped those products and forced unrelated consumers to
  program against their vocabulary. Renames only — no behaviour changed.

  | Was | Is |
  |---|---|
  | `LoginWindow` | `InteractiveSession` |
  | `LoginWindowOptions` | `InteractiveSessionOptions` |
  | `LoginResult` | `SessionResult` |
  | `LoginErrorCodes` | `SessionErrorCodes` |
  | `LOGIN_BUSY` / `LOGIN_CANCELLED` / `LOGIN_INCOMPLETE` / `LOGIN_ERROR` / `LOGIN_UNAVAILABLE` | `SESSION_BUSY` / `SESSION_CANCELLED` / `SESSION_INCOMPLETE` / `SESSION_ERROR` / `SESSION_UNAVAILABLE` |
  | `LoginCookie` | `SessionCookie` |
  | `CoBrowseSession` | `StreamingSession` |
  | `CoBrowseSessionOptions` | `StreamingSessionOptions` |
  | `CoBrowseInput` (+ `Pointer`/`Wheel`/`Text`/`Key`/`Viewport` variants, `CoBrowsePointerAction`) | `SessionInput` (+ `SessionPointerInput`/`SessionWheelInput`/`SessionTextInput`/`SessionKeyInput`/`SessionViewportInput`, `SessionPointerAction`) |
  | `CoBrowseFrame` | `SessionFrame` |
  | `CoBrowseEnded` / `CoBrowseEndReason` | `SessionEnded` / `SessionEndReason` |
  | `CoBrowseViewport` | `SessionViewport` |
  | `RunAsync`'s `driveLogin` parameter | `driver` |

  **`InteractiveSessionOptions.Title` now defaults to `"Session"`, not `"Sign in"`** — a default value,
  so this one is behavioural: set it explicitly if your window said "Sign in".
  **Why it mattered beyond tidiness:** `SessionController.GetCookiesAsync` returned
  `IReadOnlyList<LoginCookie>`, so a consumer streaming a page for remote viewing — nothing to do with
  signing in — had to name a login type. `LoginWindow` held no login logic at all: it is a busy-gated,
  profile-isolated browser window that runs an app-supplied driver until it captures a blob (a captcha,
  a terms acceptance, a checkout step). `CoBrowseSession` was an off-screen browser that streams frames
  and accepts input — co-browsing, remote support, visual capture or a preview pane, depending only on
  who wires it. **`CookieLoginFlow` deliberately keeps its name**: naming the scenario is the point of a
  reference driver (D21).
- **`StreamingSession` (was `CoBrowseSession`) takes TYPED input instead of an opaque JSON string**
  (P5.5 H9.1, D21). `DispatchInputAsync(string json)` → `DispatchAsync(SessionInput, CancellationToken)`.
  The old signature took the ORIGINATING APP'S wire protocol verbatim, so a consumer could not know what
  to pass without reading that app's client — the framework's contract was one application's message
  format. Construct `SessionPointerInput`/`SessionWheelInput`/`SessionTextInput`/`SessionKeyInput`/
  `SessionViewportInput`; coordinates stay FRACTIONS of the viewport, which is what keeps the protocol
  resolution-independent. **Migration is mechanical:** `SessionInput.TryParseLegacyJson(json, out var
  input)` parses the old shape, so an existing client keeps its frontend unchanged — it also now reports
  `false` on a malformed message instead of throwing it away silently.
- **`StreamingSession.Frames` is `ChannelReader<SessionFrame>`, not `ChannelReader<byte[]>`**
  (P5.5 H9.3). Each frame now carries the CSS viewport it depicts (`Jpeg`, `Width`, `Height`), read from
  that frame's own screencast metadata. Frames used to arrive as bare bytes with no geometry, so an app
  receiving fraction-coordinate input could not map a click back without inventing a side-channel —
  which is how a consumer ends up needing its own protocol anyway.
- **`StreamingSession.ReadHotspotsAsync()` is removed** (P5.5 H9.2). Returning a stringly-typed list of
  clickable-element rects is a co-browse UX decision, not a browser primitive — and it was
  `Task<string>`. Run it yourself through `session.Controller.ExecuteScriptAsync(...)`; the script that
  shipped is below verbatim, so nothing is lost:
  ```js
  (function(){try{
  var q='a[href],button,input[type=submit],input[type=button],input[type=image],[role=button],[onclick],label[for],select,summary';
  var els=document.querySelectorAll(q),W=innerWidth,H=innerHeight,o=[];
  for(var i=0;i<els.length&&o.length<80;i++){var e=els[i],r=e.getBoundingClientRect();
  if(r.width<8||r.height<8||r.right<0||r.bottom<0||r.left>W||r.top>H)continue;
  var s=getComputedStyle(e);if(s.visibility=='hidden'||s.display=='none'||s.pointerEvents=='none'||+s.opacity===0)continue;
  o.push([+(r.left/W).toFixed(4),+(r.top/H).toFixed(4),+(r.width/W).toFixed(4),+(r.height/H).toFixed(4)]);}
  return o;}catch(_){return [];}})()
  ```
- **`SessionBrowser.InitializeAsync` and `SessionBrowser.GetHtmlAsync` are now `internal`**
  (P5.5 H9.6). Both took a raw WinForms `WebView2` and had no consumer scenario — they mainly invited
  bypassing the render pool's accounting. Use `RenderSessionPool`, `InteractiveSession` or
  `StreamingSession`; `RenderSession.GetHtmlAsync()` is the supported way to read a rendered page.
- **The dispatch surface now carries a `CancellationToken`** (P6.4). The whole IPC pipeline was
  uncancellable: `DispatchAsync`, `SendAsync`, `MessageMiddleware`, `IModuleFacade.HandleMessageAsync`
  and `BaseFacade.RouteMessageAsync` took no token, so a handler could not observe one it was never
  given, and work still awaiting when the page navigated away or the host shut down had no way to
  learn that nobody was listening. `WebViewIpcBridge` now owns a lifetime CTS and cancels it in
  `Dispose`, so that signal reaches every handler.
  **What the token means, and what it does not:** it is the CALLER's lifetime, not per-request client
  cancellation. A one-way `post` has nobody waiting, so "the client changed its mind" remains an
  app-level CANCEL route carrying an operation id — what an operation IS belongs to the app (D21).
  Cancellation still surfaces as `OPERATION_CANCELLED`; `DispatchAsync`'s never-throws contract is
  unchanged, including for a token that is already cancelled on entry.
  **Migration.** Every parameter is optional (`= default`), so CALL sites compile untouched. What must
  change is anything that IMPLEMENTS or OVERRIDES:
  * `protected override Task<object?> RouteMessageAsync(IpcRequest request)` →
    `(IpcRequest request, CancellationToken cancellationToken)` — every facade. Ignore the parameter
    for quick synchronous work; observe it for anything that awaits.
  * a custom `IMessageDispatcher` or a decorator: add the parameter to `DispatchAsync` and both
    `SendAsync` overloads, and FORWARD it (a decorator that drops it silently disables cancellation
    for everything behind it).
  * a custom `IModuleFacade`: add it to `HandleMessageAsync`.
  * `Use(async (request, next) => …)` → `Use(async (request, next, ct) => …)`; `UseModule`/`UseRoute`
    handlers and `ModuleRouteBuilder.RouteAsync` take `(request, ct)`. `MapRoute`'s synchronous
    handler is unchanged.
  ⚠ **A lambda parameter named `_` shadows the discard.** Writing `async (request, _) =>` and then
  `_ = SomethingAsync();` inside it assigns to the token parameter instead of discarding — it is a
  compile error here, but only because the types happen to differ. Name it `ct`.
- **`IEventBus` gained `Emit`** (two overloads, fire-and-forget). Additive for CALLERS; **breaking for
  anyone who implements `IEventBus` themselves** — a test double or a substitute registered over the
  built-in one needs the two new members. See `### Added` for why it exists.
- **`IModuleRegistry.TrackMappedModule(string)` is now `TryClaimModule(IModuleFacade)`, and there is
  a matching `TryReleaseModule(string)`.** Claim and release have to be ONE owner's job: the registry
  can only take a route out again if it holds the routing it installed, and splitting "remember the
  name" from "install the route" is exactly what made release impossible. The claim is also ATOMIC
  now — check and install happen under one lock, so two threads offering the same plug-in name
  concurrently cannot both win, which the previous check-then-map could allow.
  **Migration:** apps never called `TrackMappedModule` (its own doc said so); use
  `MapModule`/`TryMapModule` as before. A DECORATOR that implements `IModuleRegistry` must forward
  the new members instead of the old one.
- **A deferred scheme's `Handler` now takes a `WebViewResourceRequest` and returns a
  `WebViewResourceResponse`**, instead of `Func<Uri, Task<(byte[], string)>>`. See `### Added` for
  what that unlocks and why it could not be done additively — the old signature had no room for a
  request header, a status code, or a stream.
  **Migration**, mechanically:
  `Handler = uri => Task.FromResult((bytes, "text/plain"))` becomes
  `Handler = request => Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.Bytes(bytes, "text/plain"))`.
  Returning null now means 404, and throwing still does (with the message kept host-side, as before).
- **`CookieLoginFlow` and `CookieLoginFlowOptions` are REMOVED from `Shenora.WebView2.Sessions`.**
  They were a product workflow shipping as library surface: `LoginUrl`, `CookieReadUrl`,
  `AuthCookiePatterns`, `RevealDelay` and `CaptureAllCookies` are one app's login recipe, and only an
  app doing cookie logins would use that API unchanged. Two decisions had talked each other into it —
  D21 blessed shipping "one opt-in reference driver", D22 then justified the scenario NAME because
  D21 had blessed shipping it — and neither ever applied D21's own test. Both are amended: **the kit
  ships no drivers**, and a type that needs a scenario name to make sense is telling you it does not
  belong in `src/`.
  **Migration:** the recipe now lives in the desktop sample as `CookieLoginDriver` — copy that file
  into your app and edit it; it is yours. Nothing else changes, because the driver only ever consumed
  public seam members (`InteractiveSession.RunAsync`, `SessionController.GetCookiesAsync`/
  `NavigateAsync`/`Reveal`/`SetLoading`). That it ports across as a plain consumer is the proof D21
  asks for. `SessionCookie` stays — a cookie is a browser primitive, not a login concept.
  A whole-surface audit went with it, by the documented method (sweep the API baselines for domain
  vocabulary): this was the ONLY product leak left. Everything the sweep flagged is genuine browser or
  platform vocabulary — `DownloadHit`/`OnDownloadStarting`, `SessionCookie`, `MuteAudio`,
  `ProfileDirectory`, `UserDataFolder`, `Module`.
- **Missing XML docs are now build ERRORS** (CS1591 unsuppressed, P7 docs sweep). Every public and
  protected member across all five packages is documented. Adding an undocumented public member no
  longer compiles — deliberate, because a public member is SemVer surface from 1.0 and "document it
  later" is how an API ends up with members nobody can explain. Turning it on immediately caught a
  broken `<see cref="..."/>` that had been invisible while warnings were non-fatal.

### Added

- **`IModuleRegistry` + `IMessageDispatcher.TryMapModule` — a dispatcher can say what it routes.**
  Module ownership used to be implicit: nothing recorded that a name was taken, so mapping the same
  module twice was silent (the second facade never ran, with no error). Any app composing its IPC
  surface DYNAMICALLY needs to know — plug-ins, features behind a licence or flag, per-tenant
  modules, lazily loaded areas — and for a module arriving from outside the app it is a boundary
  question: a late mapping that quietly shadowed an earlier one would take over that channel.
  `MessageDispatcher` now implements `IModuleRegistry` (`MappedModules`, `IsModuleMapped`,
  `TrackMappedModule`), kept OFF `IMessageDispatcher` so that interface stays the four things a
  dispatcher IS and a decorator still has four members to write. `TryMapModule` maps unless the name
  is taken; it **throws** rather than answering when the dispatcher does not implement the registry,
  because reporting a name as free is the dangerous wrong answer.
  KNOWN LIMIT, stated rather than papered over: a mapped module cannot be RELEASED — the pipeline
  only grows, so disabling a dynamic module needs a restart. No consumer has needed runtime removal
  yet, so the kit does not guess at that surface (`TASKS.md`).
- **`ShenoraBridge.post` — send without awaiting a reply**, and `createShenoraStore` — a store fed by
  one module's host event stream (P6.3a; design:
  `docs/2026-07-31-shenora-oneway-ipc-design.md`). Until now `invoke` was the ONLY outbound call, so
  every page→host message paid a correlation entry and a 30 s deadline, and — because the dispatch
  pipeline preserves the caller's synchronization context by design — ran its handler's synchronous
  segment on the UI THREAD. That made the wrong shape the only shape for a desktop app. `post` sends
  the same envelope with no pending entry and no timer (so no wire change: a transport and the host
  cannot tell the two apart), returns the request id so a caller can correlate, and reports a FAILED
  response through the new `onPostError` option instead of dropping it — an unmatched response was
  previously discarded silently. Reserve `invoke` for calls that are quick AND UI-thread-safe (the
  window commands are the model) and post everything else.
  `createShenoraStore(module, { initial, snapshot, on, actions })` returns one hook that declares a
  feature's sends, its event reducers and its shared state together. It opens ONE subscription per
  event type however many components read it, and takes a **snapshot on the first subscriber** so a
  component that mounts while work is already running sees current state — a stream cannot be
  replayed, which is the case a progress strip hits every time its tab is opened. Built on React's
  `useSyncExternalStore`, so the package still depends on nothing but React. Reducers are pure and a
  throwing one is reported rather than corrupting shared state. `useShenoraEvent` is unchanged and
  remains the counterpart: **shared or long-lived state → the store; a one-off reaction in one
  component → the hook.** Deliberately no job/queue/progress type — what an operation IS stays in the
  app.
- **Frameless caption buttons now behave like real ones — Snap Layouts, hover and press (P5.6).**
  New `OptimizedFormOptions.NativeCaptionButtons`: the cluster reported to
  `OptimizedForm.SetCaptionButtons` is cut out of the window region of **every direct child that
  covers it**, so those pixels become the form's own client area and the OS finally routes real mouse
  input there — which is the only way Windows 11 offers the Snap Layouts flyout on a maximize button
  a page drew. The window then paints the three buttons itself, with the standard Windows chrome
  glyphs and the maximize↔restore swap.
  New `CaptionButtonColors` (+ `OptimizedForm.CaptionButtonColors`) carries the palette: same split
  as `TrayMenuColors` — the kit owns the renderer (glyphs, hit states, DPI), your app owns every
  colour, because the kit ships no design (D13). Leave it null and a neutral palette is derived from
  the form's `BackColor`, so a half-wired app sees buttons rather than an empty rectangle.
  **Adopting it:** set the option, set the colours, and keep reporting the rectangles you already
  report through `SET_CAPTION_BUTTONS`; the union of those rectangles IS the hole, which is what
  makes it correct at every DPI (the cluster is ~250 physical px at 200% scaling, so any constant
  guessed at 100% cuts through the buttons). Your page should keep RESERVING that space — whatever it
  draws there is clipped away and invisible. Because the clip covers every child rather than one
  named control, the buttons also work while a splash panel is up, i.e. the window is closable before
  the frontend has loaded. `CaptionButtonStateChanged` is unchanged and still the right hook when the
  option is OFF and your app draws the buttons itself.
  This supersedes the previous release note that these types were NOT FUNCTIONAL over a WebView2.
- **The auxiliary session browser gained the three event policies it shipped without** (P5.5 H4.4):
  `NewWindowRequested` is suppressed (a pooled page calling `window.open()` used to get a real,
  visible popup in an app with no session UI), `PermissionRequested` is denied by default (an
  invisible page cannot meaningfully prompt, and an unanswered request stalls whatever asked), and
  `ProcessFailed` is now surfaced through a new `onProcessFailed` parameter on
  `SessionBrowser.InitializeAsync`. That last one closes a hang: a dead renderer was previously
  INVISIBLE, so the pool reset and re-leased the corpse forever, and a co-browse frame channel simply
  stopped with its reader waiting for a stream that could never resume. The pool now marks such an
  instance poisoned and discards it instead of re-pooling; co-browse completes its channel. Script
  dialogs are also disabled — an `alert()` in an off-screen page blocked its JS thread behind a dialog
  nobody could see or dismiss.
- `SessionBrowserOptions.IsDevelopment`, which re-appends `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` so
  a session browser is reachable over CDP. Setting `AdditionalBrowserArguments` at all makes WebView2
  ignore that variable; the sessions package had re-introduced that gotcha by hand-building its
  argument string.
- `BrowserArguments.Compose(preset, isDevelopment, devExtraArguments, additionalArguments)` — the one
  place that knows the two argument invariants, now shared by both presets: each features switch
  appears exactly ONCE (caller lists are MERGED, so an app appending its own `--disable-features=`
  can no longer silently discard the whole preset — the incident this class documents), and the dev
  CDP arguments are re-appended by hand.
- `Log` options on `SessionBrowserOptions`, `RenderSessionPoolOptions` and `CoBrowseSessionOptions`
  (P5.5 H4.7). The sessions package shipped with no logging of any kind against ~30 swallowed
  catches, so a wedged pool or a failing request filter was undiagnosable in production.
- **`IUiDispatcher` + `UiTargetState` (`Shenora.Core`) and `WinFormsUiDispatcher` (`Shenora.WinForms`)**
  — the single UI-thread marshalling seam the design contract specified from the start and P2 never
  built, which is how the pattern ended up hand-rolled 14 times across three packages with five
  mutually incompatible pre-handle policies. The target is deliberately **three-state**
  (`NotReady`/`Ready`/`Gone`) rather than one availability flag: "no handle yet" and "gone" require
  different caller behaviour, and three call sites in the kit have review-earned pre-handle policies
  that a bool would silently break. The dispatcher is per-CONTROL (sessions marshal to their anchor
  form; secondary windows run their own pumps), guards the body on both the posted and the inline
  path, and its awaitable overloads observe their cancellation token — an operation that accepts a
  token and ignores it cannot be cancelled when the UI thread is wedged.
- `LoginWindow.ComposeProfileDirectory(root, params segments)` — builds a per-account profile path
  from untrusted identifier segments, rejecting separators, `..`, drive qualifiers, invalid
  file-name characters and Windows reserved device names. Per-provider/per-account scoping is the
  session stack's isolation boundary, and the library previously documented that boundary while
  shipping no safe way to construct the path.
- **`Shenora.AppCallback`** (P5.5 H2) — the one guard for invoking APP-SUPPLIED code from a place
  where an escaping exception is fatal rather than catchable: a UI-thread event handler, a timer tick, a
  posted delegate, a dispose path. `Run` returns whether the callback completed; `RunOrDefault` returns
  its answer or an explicit policy fallback. Both swallow, deliberately — at these sites the
  alternative to losing the callback's exception is losing the operation, the window, or the process —
  and the optional error sink is itself guarded, because a failure reporter that throws must not become
  the crash it was reporting. Public because three packages consume it (D19/D20 placement law); apps can
  use it against their own extension points for the same reason. Every app callback and log sink in
  `Shenora.WebView2`, `Shenora.WebView2.Sessions` and `OptimizedForm.WndProcHook` now routes through it
  — see Fixed.
- **`RenderSessionPoolOptions.OpTimeout`, `NavigationTimeout` and `ResetTimeout`** (P5.5 H2) — the
  three budgets a leased session runs on, all validated at construction. `OpTimeout` (60 s) caps ONE
  marshalled operation (navigate / script / HTML read / CDP call) and is the piece that lets the pool
  recover from a wedged page: see Fixed. `NavigationTimeout` (30 s) is the document-load cap that used
  to be hardcoded — a SOFT cap, since the caller decides what "settled" means. `ResetTimeout` (5 s)
  bounds the return-to-pool reset. Keep `OpTimeout` above `NavigationTimeout`, or a legitimately slow
  load is reported as a wedge.
- **`StreamingSessionOptions.OnEnded` — the session lifecycle hook** (P5.5 H9.3, D21). Called exactly
  once with a `SessionEnded(SessionEndReason, string? Detail)` when the session ends. A dead renderer
  and a clean `DisposeAsync` both complete the frame channel, so a reader alone could never tell a
  crash from a shutdown; now it can. Fired through a shared latch because the two paths genuinely race,
  and invoked GUARDED — a throwing handler cannot take down the session or the UI thread.
- **`SessionResult.ThrowIfFailed()`** (P5.5 H9.4) — throws the outcome's failure as an
  `OperationException`, bridging `SessionErrorCodes` into the IPC error contract. The codes were always
  SCREAMING_SNAKE i18n keys in the shape `IpcErrorCodes` uses; what was missing was a typed path, so
  every app routing a session over IPC hand-wrote the same throw. Throwing (rather than returning an
  error object) is what plugs into the dispatcher's documented boundary — `BaseFacade` and
  `MessageDispatcher` already map an `OperationException` to the structured wire error.
- **`SessionBrowser` initialization observes a `CancellationToken`** (P5.5 H9.6), wired through the
  render pool and the streaming session. A cancelled lease used to wait out the full `InitTimeout`
  (up to 2×25 s) before anything noticed. The token gates the AWAIT only, never the creation — with the
  per-profile environment cache that task is SHARED across a pool's instances, so cancelling it for one
  caller would break the others.
- **Caption buttons the OS treats as real — the hit-test plumbing (P5.6).** This entry describes the
  MECHANISM; see `OptimizedFormOptions.NativeCaptionButtons` above for the finished feature and how to
  turn it on. (An earlier revision of this entry said "NOT YET FUNCTIONAL — do not adopt": that was
  true of the first attempt, which answered `WM_NCHITTEST` on a door the OS never knocked on, because
  WebView2 covers the client area with child windows owned by the BROWSER PROCESS and they cannot be
  subclassed to decline. Coverage turned out to be the only lever — the window now CLIPS those pixels
  out of every covering child — and the flyout has been confirmed by a human.)
  A frameless app draws its own minimize/maximize/close, and until now they were buttons the
  OS knew nothing about: no snap flyout, and no hover affordance the page could render faithfully.
  New in `Shenora.WinForms`: `CaptionButtonKind`, `CaptionButtonRegion`, `CaptionButtonState`,
  `OptimizedForm.SetCaptionButtons(...)` and `OptimizedForm.CaptionButtonStateChanged`. New in
  `Shenora.WebView2`: `WindowCommandOptions.SetCaptionButtons` + `CoordinateSpace`, enabling the
  `SET_CAPTION_BUTTONS` route (optional, same shape as `SET_THEME`). New in `@shenora/react`:
  `WindowCommands.setCaptionButtons` with `CaptionButtonKind`/`CaptionButtonRect`.
  **How it works, and the part worth knowing before adopting it:** Windows shows the Snap Layouts
  flyout only over a window that answers `WM_NCHITTEST` with `HTMAXBUTTON`, so the page reports where
  it drew its buttons and the window claims those rectangles. Claiming them COSTS the page every
  mouse event there — the OS treats them as non-client, so your `onClick` handlers and CSS `:hover`
  stop firing inside them. The kit therefore performs the click itself (through the same
  `ToggleMaximize`/`Close` the IPC commands use, so a frameless manual maximize keeps its
  bookkeeping) and pushes hover/pressed state out for you to render. Headless as ever (D13): the kit
  ships no CSS — what hot and pressed look like, including whether close goes red, stays yours.
  Re-send the rectangles whenever your layout changes; they are a snapshot, and a stale one moves the
  hit-test off the button the user can see. Opt-in throughout: register nothing and every message
  falls through exactly as before.
- **`ShenoraEventBus.subscribeToAll` / `.subscribeToModule`** — the two broad subscription breadths
  the client was missing (P6.4). The host's `IEventBus` had shipped `SubscribeToAll`/`SubscribeToModule`
  from the start and `WebViewIpcBridge` itself consumes the former, so the client was the asymmetric
  half of one concept: it could only subscribe to an exact `(module, type)`, which is unusable for any
  observer that cannot enumerate the event vocabulary up front — a plug-in-contributed event stream, a
  diagnostics or telemetry tap, a bridge folding the whole stream into another state library, or an
  adoption shim keeping a legacy "every host message" handler alive. Both return an unsubscribe
  function (React-effect friendly) and honour the same scope rule as `subscribe`.
  **Delivery is narrowest-first — exact pair, then module, then catch-all** — so a broad observer never
  runs ahead of the feature code it observes. Unlike the host, the breadths are NOT expressed as a `"*"`
  sentinel inside the key: separate collections mean a module or type an app legitimately names `*`
  can never silently become a catch-all (the `'\0'`-join lesson, applied before it could be earned
  twice — there is a test pinning it). `getSubscriptionCount(module, type)` now answers "how many
  listeners would receive this", counting the broad subscriptions that match; with no arguments it
  still counts everything.
  Found by building the two adoption adapters against the public surface and hitting the wall: the
  workaround — tunnelling every event through one reserved `(module, type)` pair — is expressible, but
  it makes adoption all-or-nothing per event, because tunnelled events are invisible to
  `useShenoraEvent` and `createShenoraStore`.
- **`IpcErrorMapping` is public** — `ToError(exception, …)` for a wire error and
  `ToErrorResponse(request, exception, …)` for a full response. It was internal, on the reasoning that
  a facade gets the error boundary free from `BaseFacade`. True, and beside the point for the case
  that found it (P6.4): an app whose IPC surface reports failures as EVENTS has no response to attach
  an error to, so it had to retype the policy — which is precisely the fifth copy this type was
  created to prevent, and its own doc says the copy that forgets `ex.GetType().Name` and passes
  `ex.Message` is how a path or a connection string reaches the page. Now it is surface rather than a
  rule people are told about.
  Note the sharp edge it documents and a test pins: an `OperationException`'s MESSAGE crosses the wire
  verbatim, because those are the app's own words for an expected failure — so never build one from an
  arbitrary `ex.Message`. That turns the one sanctioned channel into a bypass of the whole boundary.
- **`IEventBus.Emit(…)`** — emit without awaiting the handlers, for a caller that has no `await` to
  offer: a synchronous `Action`-shaped callback, a timer tick, a UI event handler. It is deliberately
  not "just" `_ = EmitAsync(…)` at the call site even though that is what it does. Discarding a task
  is normally a hazard, and whether it is safe here depends on an internal guarantee — every handler
  runs inside the bus's own guard, so the task cannot fault because of a subscriber. A caller could
  only learn that by reading the implementation, which is the actual finding: the guarantee is the
  API's to state, so it states it. Argument errors still throw synchronously — those are caller bugs.
- **`IMessageDispatcher.TryReleaseModule` — a dynamically composed module can now be turned OFF.**
  The pipeline only ever grew, so disabling a plug-in, dropping a per-tenant module when the tenant
  goes away, or unloading a lazily loaded area meant restarting the app. That was recorded as a known
  limit on the grounds that no consumer had needed it; "restart to disable a plug-in" is not something
  an adopter should have to design around, so it is closed. Releasing frees the name for a
  replacement, and `MappedModules` tells you what is releasable.
  **Two things it deliberately does not do.** Requests already executing inside the facade run to
  completion — this removes the ROUTE, it does not abort work in flight, and a caller mid-request
  still gets its answer. And the facade is NOT disposed: its lifetime belongs to whoever created it
  (usually the DI container), so disposing it here would kill a shared instance under another caller.
  Removal is surgical — the released module's entry comes out and the relative order of everything
  else (error handler, logging, app middleware, scoped router) is preserved exactly, which is the part
  that had to be right and has its own test.
- **A deferred scheme can answer any HTTP response, not just "200, here are all the bytes"** —
  `WebViewResourceRequest` (uri, method, headers) in, `WebViewResourceResponse` (status, reason,
  headers, content STREAM) out, plus `WebViewByteRange.TryParse` for the `Range` header.
  Two things were impossible before: a handler never saw a request header, so `Range` was invisible
  and **nothing it served could be sought** — a media element cannot seek a resource whose handler
  has no way to learn what offset was asked for; and it returned the complete `byte[]`, so a 4 GB file
  meant 4 GB of memory. One of the surveyed apps had to bypass the seam entirely and hook WebView2
  itself for exactly this, with an ADR explaining why (P6.6). It is not a media feature: conditional
  GETs, redirects, per-asset CORS and streaming-without-buffering were all equally unreachable.
  `WebViewByteRange.TryParse` ships because each of the three legal forms is its own chance to be
  wrong — `bytes=0-499`, `bytes=500-` (what a player actually sends when it seeks), and `bytes=-500`,
  a SUFFIX meaning the last 500 bytes, which hand-rolled parsers reliably read as "from 500". A start
  past the end is reported unsatisfiable rather than clamped, because clamping serves bytes nobody
  asked for with no error; `WebViewResourceResponse.RangeNotSatisfiable` carries the `Content-Range`
  the spec requires so a client can retry instead of looping on the same bad range.
  `Ok`/`Bytes` advertise `Accept-Ranges: bytes`, without which a media element will not even attempt
  a seek — which looks exactly like "seeking is broken" while the handler is perfectly capable.

### Changed

- **`DropZoneManager` clears its zones on DOCUMENT CHANGE instead of on the ready handshake.** It
  now subscribes to `ContentLoading` itself, so **apps should delete their `ClearAll()` call from
  `OnClientReady`** — leaving it in is harmless but pointless. This removes an ordering contract
  rather than documenting it: a `REGISTER` that arrived before `READY` was destroyed *after being
  acked*, leaving a zone the client believed was live and the host had forgotten, silent on both
  sides — and React's child-before-parent effect order made that the DEFAULT outcome for the obvious
  "call `notifyReady()` once at startup" composition. `useDropZone` therefore has no ordering
  constraint against `notifyReady()` any more. `ClearAll()` remains public for apps that want it.
- **`ShenoraEventBus.subscribe` takes an options object with `scope`, and `useShenoraEvent` passes it
  through** (P5.5 H6). Additive — existing calls compile unchanged. The wire has always carried a scope
  and the host has always keyed on it, but the client had no way to express one, so a component in one
  scope also woke for every other scope's events. The host's rule is mirrored exactly: no subscriber
  scope means every scope, and a global (scope-less) event still reaches scoped subscribers.
- **`BaseModuleService<TRequests>` is now constrained to `object`, not `Record<string, unknown>`**
  (P5.5 H6). The old bound was unsatisfiable by a plain `interface`, so the documented example and the
  README snippet failed with TS2344 — the first thing an adopter copies. Satisfying it the way the kit's
  own `windowCommands.ts` did widened `keyof TRequests & string` back to `string`, so a mistyped request
  type compiled and every payload collapsed to `unknown`: the typed-service feature checked nothing.
  Drop `extends Record<string, unknown>` from your request interfaces — with it, you keep the old
  no-checking behaviour.
- **The npm tarball now ships its LICENSE**, and `"./package.json"` is exported (P5.5 H6). The manifest
  declared MIT while shipping no license text; `dev.mjs doctor` now checks the package's copy byte-matches
  the repository root's, so the two cannot drift.
- `IpcErrorCodes.scopeRequired` (`SCOPE_REQUIRED`) is now exported from `@shenora/react`; it was emitted
  by the host but missing from the client, so a scoped app had to hard-code the string. A new
  `ClientOnlyIpcErrorCodes` export names the codes that exist only client-side (`TIMEOUT`,
  `NO_TRANSPORT`), which is what lets a test enforce the mirror instead of trusting care.
- **The verification gate now covers what it claimed to** (P5.5 H5): `Shenora.slnx` includes the
  sample projects and `Shenora.Core`, so `dev.mjs build|verify` compiles the reference composition
  and the e2e subject (the solution's `samples` folder was empty, so the sample could be red while
  `verify` reported green); `verify` also type-checks the sample web app and runs `doctor`;
  `dev.mjs test <unknown-target>` now fails instead of exiting 0 having run nothing; warnings are
  errors for `src/` (`TreatWarningsAsErrors`, `CS1591` still suppressed pending the P7 doc sweep)
  and are no longer hidden by `-clp:ErrorsOnly`; `vite` installs the sample's own dependencies and
  builds `@shenora/react` first.
- **The sensitive-info guard fails CLOSED** (P5.5 H5): a missing `local/sensitive-patterns.txt` used
  to print a notice and continue with only two structural patterns, so the private-name half of the
  scan silently did not run on a fresh clone or in CI. It now exits non-zero; pass
  `--allow-builtins-only` (or set `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1`, as the release workflow
  does) to opt in deliberately. It also scans file PATHS as well as contents, includes
  renamed/copied staged files (`git mv` stages as `R` and was skipped entirely), and a new
  `commit-msg` hook scans commit messages — which are history too.
- `create_tag: false` no longer produces a tag: the release step was always given `tag_name`, so it
  created the tag itself whenever the gated tag step was skipped — at the default-branch head,
  which need not be the published commit.
- A pool configured with a `NavigationGuard` now cancels unvetted CROSS-HOST navigation. See Fixed.
- **The `notifyReady()` → drop-zone-reset ordering contract is now documented on the surface**
  (P5.5 H7). No behaviour change; it was already sharp enough to bite and lived nowhere. A host clears
  the previous page's drop-zone overlays on the ready handshake, so a `REGISTER` that arrives BEFORE
  `READY` is discarded *after being acked* — the client believes its zone is live, the host has
  forgotten it, and nothing is logged on either side. In React this is the DEFAULT outcome rather than
  bad luck, because CHILD effects run before PARENT effects: the obvious reading of "call `notifyReady`
  once at startup" is a root-component effect, which runs after every child's `useDropZone` has already
  registered. Keep the handshake in the same component as, and declared above, anything that
  registers — or await it before rendering the subtree that does. Written on
  `ShenoraBridge.notifyReady`, `UseDropZoneOptions`, `DropZoneManager.ClearAll` and the npm README.
  `notifyReady()`'s promise REJECTS on a failed handshake, which is now stated too: `void`-ing it makes
  an unhandled rejection, and in a WebView2 page that is a silent console error.
- **The `@shenora/react` docs stopped using `'TODO'` as the example module name** (P5.5 H7). It was
  indistinguishable from an unfinished-work marker in published documentation — and it was the only
  `TODO` anywhere in `src/`. The example domain is now `NOTES` / `NoteService` / `Note`; nothing in the
  API changed.

### Fixed

- **Custom-scheme serving actually works now — `DeferredSchemes` had never served a request.** The
  host added a `WebResourceRequested` filter for `scheme://*`, but nothing registered the scheme with
  `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations`, and WebView2 accepts those only when the
  ENVIRONMENT is created — so every request was rejected by the network stack before the filter was
  consulted. Only `http`/`https` deferred schemes could work, and those were already `VirtualHost` /
  `FolderMappings`, so the feature as documented was empty. Found by an end-to-end probe; the unit
  tests, the API baseline and the docs all agreed it worked.
  **New:** `WebViewEnvironmentOptions.CustomSchemes` + `WebViewCustomScheme`
  (`Name`, `TreatAsSecure`, `HasAuthorityComponent`, `AllowedOrigins`). `WebViewHost` now THROWS at
  construction when `DeferredSchemes` names a non-http(s) scheme the environment does not register —
  the runtime symptom is otherwise a bare `TypeError: Failed to fetch` with nothing in the host log,
  which is undiagnosable from either side.
  **Also fixed, and needed before any of it worked in a page:** deferred-scheme responses now default
  `Access-Control-Allow-Origin: *` and `Access-Control-Expose-Headers: *` (both overridable per
  response). An app scheme is a different ORIGIN from the page that loads it, so without the first
  every fetch is refused; without the second a correct 206 arrives with the right bytes while
  `Content-Range` reads back as **null**. The bundle path already set the former; this path never did.
  **Migration:** add `CustomSchemes = [new WebViewCustomScheme { Name = "…", AllowedOrigins = […] }]`
  to your environment options for each app scheme. The constructor error names the exact fix.
  Note that changing a scheme registration on an existing app can wedge startup until its WebView2
  user-data folder is deleted — documented in `docs/ADOPTION.md`.
- **Maximizing and restoring a SNAPPED frameless window now exits the snap**, matching every other
  Windows app. `OptimizedForm.Maximize` captured the live window rect as its restore target, which
  for a snapped window is the docked half — so restore put the window straight back into the dock. It
  now captures `WINDOWPLACEMENT.rcNormalPosition`, which is Windows' own restore rectangle and which
  Aero Snap leaves at the pre-snap geometry.
- **A route mapped while requests were in flight could answer `NO_HANDLER`** (P5.5 H6). Late mapping is a
  supported, documented pattern — the WinForms host maps its window facades after the form exists — but
  `MessageDispatcher.Use` reassigned a `Lazy` field over an unsynchronized `List<T>` with no
  synchronization anywhere, so a concurrent dispatch could read the old cached pipeline and report no
  handler for a route that was by then registered, and a pipeline build enumerating the list while `Add`
  grew it was a plain data race. The middleware list is now copy-on-write, the built pipeline is volatile,
  and invalidate-then-rebuild happens under one lock.
- **Cancellation is no longer reported as `UNKNOWN_ERROR`** (P5.5 H6). New
  `IpcErrorCodes.OperationCancelled` (`OPERATION_CANCELLED`, mirrored on the client) means a UI can stay
  silent for the one failure it should not report as an error. Placed after `OperationException` in the
  mapping, so an app that models cancellation with its own code keeps its own words. The reference
  composition had already hand-rolled this arm — the tell that every adopting app would have had to.
- **A scope invalidated mid-request failed instead of using the rebuilt scope.**
  `ScopedContainerRouter.HandleAsync` now retries once on `ObjectDisposedException` (and not at all while
  the router itself is disposing, so shutdown cannot spin). `InvalidateScope` is a documented app-facing
  call that can fire while requests are in flight, so this race is normal, not exceptional.
- `EventBus.EmitAsync(module, type, …)` rejects an empty module or type instead of building an event that
  could never match any subscription; and `SubscribeCore` now publishes `_patterns` last — it is what
  `EmitAsync` enumerates, so a concurrent emit could previously see a subscription whose handler and
  match cache were not written yet, making its `continue` mean something other than the "concurrently
  unsubscribed" its comment claims.
- **An option added to `ShenoraPathsOptions` would have been silently dropped under `--app-root`.** The
  merge hand-copied all six properties into a new instance; the type is now a `record` and the merge uses
  `with`.
- **Notifications could stop for the rest of the process** (P5.5 H3). The ready gate closed on EVERY
  `NavigationStarting`, but the client sends `READY` only once per real page load — so a navigation that
  never replaced the document (one an app tap or a policy cancelled, one that failed before committing)
  closed the gate permanently on a page that was still alive: notifications buffered to the 10 000 cap
  and then silently dropped the oldest, forever. The gate now closes on `ContentLoading`, which is raised
  only when a new document actually begins loading. It also closes on `ProcessFailed` — a dead renderer
  left it OPEN, so the next tick drained a whole batch into a process that could not receive it, and
  since the queue was already emptied those notifications were simply gone.
- **Six unvalidated options that failed far from their cause** (P5.5 H3), now all rejected at
  construction: `MaxQueuedNotifications = 0` made `Enqueue` dequeue the item it had just enqueued, so
  every notification for the life of the process vanished with no error and no log line;
  `NotificationInterval` below 1 ms (or above the WinForms timer's int32 millisecond limit) threw from
  inside `Attach()`; `SessionBrowserOptions.InitTimeout = 0` failed init instantly with the
  profile-LOCK diagnosis, sending the caller hunting a zombie browser process that did not exist;
  `RenderSessionPoolOptions.OffscreenClientSize` of zero gave a 0×0 viewport in which pages "load" with
  every element sized zero; and `ScopedContainerRouterOptions.ConfigureScope` set to null surfaced as an
  NRE from inside scope creation, reported to the client as `UNKNOWN_ERROR` (`required` compels the
  caller to write the initializer, not to write a non-null value). `ConfigureScope` now also documents
  that each scope is a ROOT provider, so `AddScoped` there behaves as a per-scope singleton — the
  opposite of what it means elsewhere in Microsoft DI.
- **`WebViewHost.InitializeAsync` is idempotent, and its timeout covers the whole sequence** (P5.5 H3).
  The timeout message advises "start again", so a Retry button is the expected recovery — and a second
  call re-ran the event-policy wiring, double-subscribing every handler: from then on each external link
  opened TWICE, each download decision ran twice, and the renderer auto-reload raced itself. A failed
  initialization clears the cached task so a retry is still a real retry. Separately, each step used to
  get its own full `InitTimeout` — so the documented 25 s was really 50 s before the sequence even
  reached `ApplySettings`, and script injection was unbounded on top of that.
- **One transient WebView2 environment failure was terminal for the process.**
  `WebViewEnvironment.GetSharedAsync` cached its task with `??=`, faulted or not, so every later
  attempt — including the retry the init-timeout message asks for — got the original exception back
  without ever touching WebView2 again. A faulted or cancelled task is now evicted when observed.
- **A mistyped resource prefix opened a black window with no error.** The prefix depends on MSBuild's
  manifest-name mangling, so it matches nothing silently and every request 404s. `WebViewHost` now fails
  at `Navigate()` with an actionable message when the start document IS the packaged bundle and the
  provider has no `index.html`, and `EmbeddedResourceProvider` reports a can-serve-nothing configuration
  (new `CanServe` property) naming the bad prefix and the assembly's actual manifest prefixes. The check
  is deliberately not in the provider's constructor: a provider with nothing to serve is correct when
  the page loads from a dev URL, which is the normal state of a freshly cloned repo.
- **Exception text no longer reaches HTTP response bodies.** All three 404 paths served
  `$"Error: {ex.Message}"` under `Access-Control-Allow-Origin: *`, so page script could fetch and read
  it — routinely a full local filesystem path, and for a deferred-scheme handler potentially a remote
  URL. The body is now a constant and the diagnosis goes to the host log, matching the IPC error
  boundary's rule.
- **A crash-looping page reloaded forever.** The renderer auto-reload was rate-limited but had no
  terminal state, so a page that faults during load reloaded every cooldown for the process lifetime,
  spawning a renderer each time — while the option's own documentation promised that "a crash-looping
  page must not spin". New `MaxAutoReloads` (default 3) is that terminal state; the give-up is logged
  exactly once, and a successful navigation resets the budget so a long-running app is not rationed by
  unrelated crashes hours apart.
- **`@shenora/react`'s robustness tail** (P5.5 H2). A host message of literal `null` — valid JSON —
  survived the parse and then threw a `TypeError` out of the transport listener: an uncaught page error
  with no caller to catch it. `bridge.isAvailable` ignored `disposed`, so a stale reference to a bridge
  that `configureBridge` replaced reported itself available while every `invoke` on it rejected. The
  `fallback` path bypassed the timeout entirely, so an async fallback that never settled hung the caller
  forever. `BaseModuleService` captured the bridge in a constructor default, i.e. at construction — so a
  module-level service singleton (the normal way to write one) built before `configureBridge()` held the
  bridge that call then DISPOSED, and every request from it rejected with "Bridge disposed" for the rest
  of the session; the bridge is now resolved per call, and `this.bridge` still works in subclasses.
  `useDropZone` never registered a target that wasn't mounted on the first effect run — a `RefObject` is
  a stable object and a ref mutation triggers no render, so a conditionally-rendered target was silently
  dead for the component's whole life; the effect now keys on the element itself. `useWindowMaximized`
  fired one un-debounced IPC round-trip per `resize` event (~180 over a 3-second drag, each arming a
  30-second timer) and is now debounced, which is also the correct semantics since the state only
  changes when a resize ends. And `useShenoraQuery` no longer blanks good data when a REFETCH fails —
  one transient hiccup used to turn a recoverable error into an empty screen; both fields are now
  reported so the caller can render stale data with an error banner.
- **The WinForms shell's robustness tail** (P5.5 H2). `WinFormsBootstrap.Initialize` now fails fast on a
  non-STA thread with the fix in the message (a missing `[STAThread]` otherwise surfaced much later as a
  BLOCKING modal dialog inside window creation) and is idempotent (a second call re-registered all three
  exception channels, so every later exception was reported twice and raised two stacked dialogs). Its
  last-resort crash dialog is now one-at-a-time per thread: `MessageBox.Show` pumps, so a recurring
  UI-thread exception re-entered the handler and stacked dialogs unboundedly over a window nobody could
  reach — recurrences still reach the app's logger. `SecondaryWindows` removes its registry entry only
  after `Application.Run` returns (`FormClosed` fires while the form is still disposing its children, so
  a `Dispose` waiting for "no windows left" returned mid-teardown and let the process exit while a
  WebView2 child was still shutting down, leaving its user-data folder locked), removes the entry when
  `thread.Start()` fails (it was otherwise permanently "already open"), and replays an `Activate` that
  arrived before the window's handle existed (previously dropped — and that is the documented "`Open` on
  an existing name activates it" path). `SingleInstanceGuard.TryAcquire` is idempotent: an OS mutex is
  per-thread reentrant, so a second call took a second handle and reported success even when this
  process already owned it, after which `Dispose` could release only one and the mutex stayed held past
  shutdown. `OptimizedForm` re-applies its manual maximize on `WM_DPICHANGED` and display-settings
  changes (a monitor move or scale change left a "maximized" window at the old monitor's size) and
  validates its saved restore rect before using it, so a window whose monitor is gone no longer restores
  somewhere unreachable. `ClipboardService.SetTextAsync("")` clears the clipboard instead of throwing.
- `TrayIcon`'s close-to-tray documentation was factually wrong and is corrected: WinForms reports
  `CloseReason.UserClosing` for a programmatic `Form.Close()` too, so with `CloseToTray` on, an app whose
  startup-abort path calls `Close()` HIDES the window and leaves a resident process with a tray icon and
  a window that can never finish loading. Close from code with `ExitApplication()` or
  `Application.Exit()`. No behaviour changed — the reason code carries no way to tell the two apart.
- **An app callback that threw could take the host down, stall a browser event, or corrupt a tap list**
  (P5.5 H2). Every remaining unguarded app-supplied delegate now runs through `AppCallback`:
  `WebViewHostOptions.OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed` (all three run
  inside WebView2 events, where a throw has no caller and becomes an unhandled UI-thread exception —
  and a failed hook now falls back to the kit's built-in policy, because leaving the event unanswered
  is its own bug: an un-cancelled download proceeds, an unanswered permission request stalls its
  caller, a renderer crash goes unhandled exactly when things are already wrong);
  `OptimizedForm.WndProcHook`, where a throw inside `WndProc` surfaces as WinForms' own BLOCKING modal
  dialog mid-message-dispatch — a throwing hook now reads as "did not handle this message" and the
  window keeps working; `WebViewIpcBridgeOptions.OnClientReady`; and every `Log` sink in
  `Shenora.WebView2`, several of which sat inside a `catch` that exists to stop a failure escaping,
  where a throwing sink defeated the very thing it was reporting from. Log calls are also lazy now, so
  building a message can't throw outside the guard either.
- **`SessionController`'s driver taps were a data race.** The four tap collections were plain
  `List<T>`, appended from the driver's thread (a continuation resumes wherever the pool puts it) while
  the WebView2 event handlers read them on the UI thread. `List<T>.ToArray()` reads the count and then
  copies the backing store, so an `Add` in between throws or copies a torn view, and two concurrent
  `Add`s corrupt the list outright. They are now copy-on-write arrays published under a lock, so
  readers take no lock at all.
- **A wedged page permanently poisoned the render pool** (P5.5 H2, the second half of the
  unobserved-token fix). A page blocked in its own script thread never answers `ExecuteScriptAsync` or
  `GetHtmlAsync`. H4.2 already made the CALLER escape (the marshal observes its token), but that alone
  left the wedged instance going straight back into the pool, so every later lease inherited the
  corpse. Operations are now bounded by `OpTimeout`, an expiry surfaces as `TimeoutException`, and the
  instance is marked poisoned so returning the lease DISCARDS it and the next lease gets a fresh
  browser. A body that ran and merely threw (a rejected URL, a guard refusal) does not poison anything
  — completion is tracked, not inferred from the exception.
- **A returned session that could not be reset was re-pooled forever.** The reset-to-`about:blank`
  swallowed its own timeout and reported success unconditionally, so the documented "a failed reset
  DISCARDS the instance" rule was reachable only if the navigation THREW. An unresponsive renderer was
  therefore recycled indefinitely, each lease burning the full navigation cap before failing. The reset
  now reports its real outcome.
- **A cancelled session start left a live browser behind.** Both `RenderSessionPool` and
  `CoBrowseSession` checked cancellation only BEFORE the multi-second browser init, so a lease
  cancelled — or a pool disposed — during those seconds published nothing to the caller while leaving a
  realized off-screen window and a browser process holding the profile lock, with no owner left to
  dispose either. Both now re-check after init (co-browse also just before publishing) and tear down;
  `LeaseAsync` additionally passes the pool's own dispose token into instance creation.
- **Each retried lease against a locked profile orphaned another browser process.** `InitTimeout`
  abandons the *await* on `CoreWebView2Environment.CreateAsync`, never the creation itself, and every
  instance created its own environment — so a retry queued a second browser process onto the same
  locked profile folder, adding to the very lock the timeout's error message blames. A pool now shares
  ONE environment across its instances and a retry joins the creation already in flight. A failed
  creation is deliberately not cached, so one transient failure is not terminal for the process.
- **A co-browse frame stream could stop silently after a GC.** The CDP screencast receiver was held
  only in a local inside `StartAsync`, so nothing referenced it for the session's lifetime and the
  stream depended on the WebView2 SDK caching it internally. It is now rooted for the session and
  detached in `DisposeAsync`.
- **A late interceptor could read another lease's traffic.** `RenderSession.OnNetwork` and `OnMessage`
  were the only public members with no disposal check, and the only two that install a persistent tap
  — so a subscribe after `DisposeAsync` (a stale reference, a continuation outliving its `await using`)
  attached a live listener to a pooled instance the NEXT lease now owned, streaming its API responses
  and posted messages to the previous caller. Both now throw `ObjectDisposedException`, as every other
  member already did.
- **`AddMessageDispatcher` killed the process for an ordinary composition** (P5.5 H2). It resolved
  module facades INSIDE the `IMessageDispatcher` singleton factory, so any facade whose dependency
  graph reached `IMessageDispatcher` — the documented seam for cross-module `SendAsync` — re-entered
  that factory. Microsoft DI's cycle detection is call-site based and cannot see a factory delegate
  re-entering the provider, and the singleton is not cached yet, so it simply ran again: unbounded
  recursion, `StackOverflowException`, process death with no exception and no log line. Facades are
  now mapped through one terminal middleware that resolves them on the first dispatch, by which point
  the singleton is cached. Two facades claiming the same module name are also rejected instead of the
  second one's whole route table being silently unreachable.
- **`app.Dispose()` threw on a clean shutdown** whenever a singleton implemented only
  `IAsyncDisposable` — which Shenora's own `RenderSession` and `CoBrowseSession` do, so this was
  latent against the kit's own types. `ShenoraApplication` now implements `IAsyncDisposable`; prefer
  `await using var app = builder.Build();`.
- **A relative app root silently re-resolved mid-session.** `ShenoraPaths` returned the resolved root
  and data override verbatim, so a launcher passing `--app-root ..\install` left every derived path
  following the process working directory — and this kit MOVES that directory: the file dialogs set
  `RestoreDirectory = false` on purpose (per-key directory memory is ours), so the first Open/Save
  dialog relocated the CWD and the same `DataDir` string then pointed somewhere else, splitting the
  app's data. It also defeated `SingleInstanceGuard`'s channel hashing. Both paths are now absolute.
- **A throwing app `OnLoading` callback made the login window unclosable** (P5.5 H2). The completion
  block ran the app callback BEFORE `controller.Finish()`, inside an `async void` handler — so a
  throw (an already-disposed splash is the obvious case) meant `Finish()` never ran, and the
  foreground controller HOLDS the user's close until then, so its `FormClosing` handler cancelled
  every close including `Application.Exit`. `Finish()` + `Close()` now come first and the callback is
  guarded.
- **A maximized frameless window lost its state and became unrestorable.** `WindowStateManager` read
  `Form.WindowState`/`RestoreBounds`, but frameless chrome maximizes by hand and keeps
  `WindowState.Normal` — so closing while maximized persisted `Maximized: false` plus the WORK-AREA
  rect as the normal size. On the next launch the window filled the work area believing it was not
  maximized: the border gap the technique exists to remove came back, the chrome glyph was wrong, and
  clicking maximize captured the work-area rect as the restore bounds, making restore a PERMANENT
  no-op. New `IAppMaximizable` seam (implemented by `OptimizedForm`) is now preferred over the
  WinForms properties, and a saved maximized state is restored through the window's own mechanism.
  Live in the reference composition.
- `WindowStateManager.Apply` no longer overwrites a `MinimumSize` the form set for itself — the
  reference composition's own 640×420 minimum was dead code.
- **Arbitrary file read through file-mode frontend serving.** The resource provider applied no path
  containment, and the host unescapes the request path before calling it (it must, so bundle
  filenames with spaces or CJK characters resolve) — so `%2e%2e%2f…` arrived as `../` and walked out
  of the bundle, and a ROOTED path (`/C:%2f…`) escaped even more simply because `Path.Combine`
  discards its first argument when the second is rooted. Responses carry
  `Access-Control-Allow-Origin: *`, so page script could read what came back. Live wherever
  `PreferFiles` is on — which the sample derives from `IsDevelopment`. Both `GetResourceStream` and
  `Exists` now reject rooted and traversing paths and assert the resolved path stays under the root.
- **`NavigationGuard` was bypassed by redirects.** It was consulted only on the explicit
  `NavigateAsync` call, so a guard-approved URL answering `302 → http://127.0.0.1:8080/admin` was
  followed and its DOM handed to the caller. The pool now cancels unvetted cross-host navigation at
  `NavigationStarting`. Note the scope honestly: that event has no deferral in the WebView2 SDK, so
  an async guard cannot be awaited inside it — a synchronous cross-host rule is the most the event
  can enforce, and `SessionBrowserOptions.RequestFilter` (synchronous, `WebResourceContext.All`)
  remains the seam for full redirect/subresource policy. Documented on both options.
- **An unserializable notification payload crashed the UI thread and lost its whole batch.** The
  notification flush drained the queue and then serialized with no try/catch, on a 50 ms WinForms
  timer — so one app event carrying a cyclic object graph, a `Type`/delegate member or a throwing
  getter took down the UI thread (a modal crash dialog under the family bootstrap) and discarded the
  drained batch. Payloads are now serialized per notification so only the offender is dropped, with
  a catch-all around the flush. The incoming path had always been guarded; this asymmetry was the bug.
- **`LoginWindow.ClearProfile` is a recursive delete and accepted a traversing path.** Profile paths
  are normally composed from data-driven identifiers, so a stray `..` segment could aim the delete
  outside the sessions root — while the same options documented that scoping as a security boundary.
  It now refuses traversal segments; use `ComposeProfileDirectory` to build the path safely.
- A `Process` handle leaked on every external link click from the page: the WebView2 host's
  open-in-system-browser path did not dispose the started process, though the sibling implementation
  in `ShellLauncher.OpenUrl` already carried that Win11 fix.

- **`@shenora/react` was not importable under native Node ESM** (`0776f37`). The emitted relative
  imports carried no `.js` extension, which bundler resolution silently tolerated and plain Node
  rejected — so the published tarball would have failed for any consumer not behind a bundler. All
  relative specifiers now carry explicit extensions and `module`/`moduleResolution` are `NodeNext`,
  which makes a missing extension a build error rather than a publish-time surprise. Caught by the
  P1.1 local-feed consumption smoke; root cause in `docs/archive/fix-log.md`.

Bootstrap: repo, docs system, design contract, buildable package skeleton
(`Shenora.Core` / `Shenora.Ipc` / `Shenora.WebView2` / `Shenora.WinForms` / `@shenora/react`),
devtools loop (`build` / `test` / `verify` / `pack` / `doctor` + desktop verification tools),
manual OIDC release workflow. `@shenora/react` exposes only `isShenoraAvailable()`.

First extracted surface (P2 increments 1–5, gated by API-surface baseline tests):
`Shenora.Core` `ShenoraEnvironment` + `AppRootArgument` + `ShenoraPaths(+Options)` + the
application builder (`ShenoraApplication(+Options)`/`ShenoraApplicationBuilder`/`IShenoraModule`/
`IShenoraRunner`/`IShenoraLifecycleHook`);
`Shenora.WinForms` `DpiHelper` + window-state stack (`WindowState`/`WindowStateOptions`/
`IWindowStateStore`/`JsonFileWindowStateStore`/`WindowStateManager`) + `SingleInstanceGuard`
(incl. `TryAcquire(TimeSpan)` — the `--restarted` widened-wait relaunch handoff) +
`WinFormsBootstrap(+Options)`/`UnhandledExceptionReport` + the host composition
(`UseWinForms`, `WinFormsHostOptions`/`SingleInstanceHostOptions`/`WindowStateHostOptions`) +
`SplashPanel(+Options)`;
`Shenora.WebView2` `BrowserArguments` + `WebViewEnvironment(+Options)` (runtime probe, prewarm,
per-thread creation) + `PrewarmWebView2` builder extension + `WebViewHost(+Options)` (init
timeout guard, settings hardening, dev/prod navigation, new-window/download/permission/
process-failure policies, escaped `InjectedGlobals`, sync virtual-host + deferred app-scheme
serving, `WebViewFolderMapping`) + `IWebViewResourceProvider`/`EmbeddedResourceProvider(+Options)`
(lazy-with-warmup, file-fallback mode) + `WebViewDeferredScheme`.
Dependency note: `Shenora.Core` now depends on `Microsoft.Extensions.DependencyInjection`
(the implementation — the builder needs `BuildServiceProvider`), not only the abstractions (D17).

`Shenora.Ipc` first surface (P3.1 — the transport-neutral wire contract, design contract §5 +
D11/D16): `IpcRequest`/`IpcResponse`/`IpcError`/`IpcNotification`/`IpcNotificationBatch`
envelopes (names pinned with `JsonPropertyName`; optional app-defined `scope` field),
`IpcCategories` (lowercase `ipc`/`notification` discriminators), `OperationException`
(code + parameters, i18n-ready, `ToError()`), `IpcErrorCodes` (framework-reserved codes),
`PayloadHelper` (structured missing/invalid errors; JSON null == absent), and `IpcJson`
(frozen camelCase/camelCase-enums/null-omitting wire serializer defaults). Replaces the
assembly marker.

P3.2 — the dispatch pipeline and the in-process event bus. `Shenora.Ipc`:
`IMessageDispatcher`/`MessageDispatcher` (composable middleware pipeline —
`Use`/`UseModule`/`UseRoute`/`UseLogging`/`UseErrorHandler`/`MapRoute`/`MapModule`, incl.
facade-object mapping — plus `DispatchAsync` for transports, never throws/never null, and
programmatic `SendAsync`/`SendAsync<T>` sharing the same pipeline; failed typed sends rethrow
the structured `OperationException`; unknown exceptions cross the bridge as `UNKNOWN_ERROR`
only — details stay in the host log), `MessageMiddleware`, `ModuleRouteBuilder`,
`IModuleFacade`/`BaseFacade` (standardized error boundary), `IpcErrorCodes.NoHandler`.
`Shenora.Core`: `EventMessage`/`IEventBus`/`EventBus` (wildcard patterns + per-subscription
match cache; scoped subscribers also receive global events; handler failures isolated) —
auto-registered by `ShenoraApplicationBuilder.Build()` (`TryAdd`, replaceable).

P3.3 — `Shenora.WebView2` gains `WebViewIpcBridge(+Options)`: the postMessage transport —
incoming requests parsed and dispatched on the UI thread via async interleaving (never
`Task.Run`-per-message), responses/notifications posted with `IsHandleCreated`-guarded
non-blocking `BeginInvoke`, host→page pushes batched every ~50 ms through a bounded drop-oldest
queue (buffering starts at construction; delivery starts at the client's `SHENORA`/`READY`
handshake, which also fires `OnClientReady` per occurrence), optional `IEventBus`
wildcard-forwarding, `SendNotification` for direct pushes.

P4.1 — `Shenora.Ipc` gains the scoped-container router and the standard IPC composition:
`ScopedContainerRouter(+Options)` (per-scope child service containers, single-flight creation,
`MapModule<TFacade>` routing declarations, `GetScopeServices`/`InvalidateScope`/`ActiveScopes`,
structured `SCOPE_REQUIRED` for scoped modules called without a scope — `IpcErrorCodes.ScopeRequired`)
with `UseScopedRouter`, plus `AddModuleFacade<TFacade>`/`MapRegisteredModules`/
`AddMessageDispatcher` (the §5 pipeline order encoded: error handler → app middleware →
DI-registered facades).

P4.2 — the window manager: `Shenora.WinForms` `OptimizedForm(+Options)` (double-buffered base +
`WndProcHook` seam; optional frameless custom chrome — WM_NCCALCSIZE top-only caption removal,
manual work-area maximize with `IsAppMaximized`/`MaximizedChanged`, DWM dark border/rounded
corners, `ApplyChromeTheme` runtime resync — all colors parameterized); `Shenora.WebView2`
`WindowCommandFacade(+Options)` (module `WINDOW`: MINIMIZE/TOGGLE_MAXIMIZE/CLOSE/IS_MAXIMIZED/
START_DRAG/START_RESIZE + optional SET_THEME; delegate seams for the frameless paths);
`@shenora/react` `WindowCommands` service + `useWindowMaximized` hook.

P4.3 — the native desktop services in `Shenora.WinForms`, all TryAdd-registered by
`UseWinForms`: `IFormInteraction`/`FormInteraction` (main-window registry — the runner sets it —
plus nested modal blocking; handle read answers `Zero` before creation instead of creating it on
the wrong thread), `IFileDialogs`/`FileDialogs(+Options)` + `FileDialogOptions`/`Filter`/`Result`
+ the `IFileDialogPathStore` memory seam (STA-thread open/folder/save dialogs, owner-handle
z-order, per-key last-directory memory; failures throw instead of the source's wire-bound error
strings), `IShellLauncher`/`ShellLauncher` (reveal-in-Explorer, open directory, http/https-only
`OpenUrl`, `LaunchProcess` — the Windows 11 handle-leak/orphan-process fixes kept),
`IClipboardService`/`ClipboardService` (STA-marshalled text + image-file operations).

P4.4 — the drag-drop zone stack: `Shenora.WebView2` `DropZoneManager(+Options)` +
`DropZoneFacade` (module `DROP_ZONE`: transparent overlays synced to page elements capture real
OS file paths — including background drags; non-blocking UI marshalling, form-activation sync,
DOM occlusion checks; per-monitor `DeviceDpi` CSS→physical conversion + `DpiChanged` re-apply
from stored CSS rects — the P2.3b DPI tail; events emitted on `IEventBus`, forwarded by the
bridge); `@shenora/react` `useDropZone` (bounds auto-sync via observers, drag CSS feedback —
unstyled/headless, real-path `onDrop`, in-flight-REGISTER and fast-unmount teardown guards).

P4.5 — `Shenora.WinForms` gains `SecondaryWindows` + `SecondaryWindowOptions` (named windows,
each on its own STA thread with its own pump; geometry persistence reuses the window-state
stack per name via `IWindowStateStore`; open-on-existing activates; non-blocking close
discipline) and `TrayIcon(+Options)`/`TrayMenuColors` (NotifyIcon + Open/app-items/Exit menu,
double-click restore, close-to-tray, optional app-colored menu renderer — colors are the app's,
headless).

P3.4 — `@shenora/react` becomes the real client: wire-contract types mirroring `Shenora.Ipc`
(`IpcRequest`/`IpcResponse`/`IpcError`/`IpcNotification`/`IpcNotificationBatch`/`EventMessage`
+ `IpcCategories`/`IpcErrorCodes`/handshake constants), `OperationError` (structured
code + parameters, incl. client-side `TIMEOUT`/`NO_TRANSPORT`), the `ShenoraTransport` seam +
`createWebView2Transport` (transport-pluggable, D16), `ShenoraBridge` (correlated `invoke` with
per-call timeout, category routing, batch unbundling into the event bus, `notifyReady`
handshake, `fallback` seam for pure-UI browser dev) with lazy `getBridge`/`configureBridge`,
`ShenoraEventBus` + `eventBus`, `BaseModuleService<TRequests>` (typed per-module services),
hooks `useShenora`/`useShenoraEvent` (latest-ref, no resubscribe churn)/`useShenoraQuery`
(minimal fetch state, headless per D13), and `installDevInterceptor` (`window.__shenora` ring
buffers for CDP-driven testing). `react` is now a REQUIRED peer (hooks import it);
`isShenoraAvailable()` unchanged.

P5.1/P5.2 — new package `Shenora.WebView2.Sessions` (D14): auxiliary browser sessions — browser
work OUTSIDE the app's own UI, over the same WebView2 runtime. `SessionBrowser(+Options)` (the
ONE configuration path for auxiliary WebView2s: per-profile environment, quiet-start +
background-throttling-off arguments, settings hardening, `RequestFilter` request-blocking seam,
init-timeout guard, `GetHtmlAsync`) and the render pool — `RenderSessionPool(+Options)`/
`RenderSession`/`SessionApiCall` (bounded LIFO pool of off-screen sessions leased for
navigation/scripting/HTML-read/DevTools/network+message taps; capacity waits queue, a creation
failure releases the slot, a failed reset discards the instance instead of re-pooling it;
`NavigationGuard` SSRF policy seam; one shared hidden host in runtime mode or visible
per-session dev windows). The login stack — `LoginWindow(+Options)`/`LoginWindowController`/
`LoginResult`/`LoginErrorCodes`/`LoginCookie`/`DownloadHit`: interactive logins over
per-provider (and per-sub-account — a security boundary) persistent profiles, driven by a
caller-supplied driver over controller primitives (guarded navigate, script, origin-scoped
cookie read, message/download/new-window/navigation taps, `FitToBox` CSS→physical sizing,
`SetLoading`, idempotent `Reveal`); one login at a time with exactly-once completion, the
user's close HELD for a final cookie read, an optional silent-refresh shape (created
off-screen, revealed only if interaction is needed), and `ClearProfile` for real logout.
`CookieLoginFlow(+Options)` is the built-in driver: navigate then poll for a FRESHLY-SET auth
cookie (pattern-matched, judged against a pre-navigation baseline — a stale cookie never
captures, not even on close), cookies read from the separate `CookieReadUrl` origin, blob
round-trip via `ReadBlob`.

P5.3 — `Shenora.WebView2.Sessions` gains `CoBrowseSession(+Options)`/`CoBrowseViewport`:
co-browse an off-screen page in-app (countdowns/captchas stay human-solved, no native window) —
CDP `Page.startScreencast` JPEG frames flow into a bounded latest-wins `ChannelReader<byte[]>`
(`Frames`: a slow client drops the oldest frame, never backs up the compositor), the client's
input JSON is dispatched back via `DispatchInputAsync` (viewport messages mirror the client's
content box 1:1 through device metrics ALONE — never a physical resize; fraction-coordinate
mouse/wheel; `insertText` typing; special keys/shortcuts synthesized with the modifier bitmask +
Windows virtual-key map), `ReadHotspotsAsync` returns clickable-element rects as viewport
fractions (client-side hover/pressed affordances over pixels), and `Controller` exposes the
SAME `LoginWindowController` primitives over the streamed page. The wire protocol is identical
to the proven source for mechanical adoption; the transport (WebSocket, bridge, …) stays the
app's — frames out, input text back.
- **The npm tarball could have shipped test-support code** (P5.5 H7). `tsconfig.build.json` excluded
  only `src/**/*.test.ts(x)`, so the new shared `src/testing/fakeTransport.ts` — a non-test helper
  sitting beside the sources — compiled straight into `dist/`, which `files: ["dist"]` publishes
  wholesale. Caught while adding it, and confirmed by building without the exclusion: `dist/testing/`
  really was emitted. Fixed by excluding `src/testing/**`, and `dev.mjs doctor` now FAILS when
  `dist/testing/` exists so the exclusion cannot be dropped silently while editing an unrelated pattern.
- **The reference sample no longer swallows a failed ready handshake** (P5.5 H7). It called
  `void getBridge().notifyReady()`, so a rejection (no host, disposed bridge, timeout) became an
  unhandled promise rejection — a silent console error in a WebView2 page. It now catches and logs.
  Worth listing even though the sample is not shipped: it is the reference composition, and this is the
  snippet adopters copy. The sample also gained the CSS rule behind its `dropClassName`, which it had
  been passing with nothing to style it — so the e2e subject can finally demonstrate the drop zone's
  HOVER feedback and not only the drop.
- **`@shenora/react`'s shipped types no longer require `@types/react` to be in your global program.**
  `UseDropZoneOptions.targetRef` was declared as `React.RefObject<HTMLElement | null>` — the UMD global
  `React` — while the source imported only the three hooks it used. The emitted
  `dist/useDropZone.d.ts` therefore NAMED `React` with no import, so it resolved only when the
  consumer's program happened to pull `@types/react` in globally. A consumer with `"types": ["node"]`
  in their tsconfig — entirely reasonable, and the default for a non-React entry point — got
  **TS2503 "Cannot find namespace 'React'" out of a declaration file they cannot edit**. Fixed by
  importing `type RefObject`; the type is identical, so nothing source-breaking.
  Found by P6.4's client-adapter probe. P6.1's npm consumer missed it because its own tsconfig
  imports React in a `.tsx`, which loads the global — a consumer probe only ever tests the
  configuration it happens to have, which is the transferable lesson here rather than the one-liner.
