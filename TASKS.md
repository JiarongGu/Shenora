# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.3.0 PUBLISHED (2026-08-01).** Five NuGet packages + `@shenora/react` on npm. It carries
everything through the mission scheduler — the design pass (D1–D4), the genericity gate, D25, and
`Shenora.Core`'s `Missions`/`Io` layer.

**0.2.0 does not exist and never will** — a session hand-bumped `<VersionPrefix>` to it, the release
workflow bumped from that baseline to 0.3.0, and the number was consumed without shipping. The
registries read 0.1.2 → 0.3.0. Full account in `CHANGELOG.md` under `## 0.2.0 — never released`; the
guard that stops a repeat is in `docs/RELEASING.md`. Work written while this was in flight calls it
"the 0.2.0 pass" — those names refer to the WORK, not to a release.

**The surface is now PUBLISHED, so the free-breaking-change window is closed.** D1 and D2 shipped.
Pre-1.0 still permits a documented break in a MINOR (`CHANGELOG.md`), but it is a real break against
real consumers now — no longer free, and it belongs under `### Breaking`. Growth from here is
harvest-driven (D15) and adoption-driven: the next real work arrives when a sibling app adopts the kit
and hits something, or when a feature worth generalising emerges while building one.

> DIRECTION (user, 2026-07-30): Shenora is the shared infrastructure library for ALL sibling
> projects — a "UI kit for non-web applications" in the headless sense: it holds the desktop
> shell that different applications boot their own logic on, and it must NOT depend on any UI
> component library. Purpose is to stop re-solving the same problems per project. In-scope
> common work explicitly includes: multi-form/multi-window, co-browsing (auxiliary browser
> sessions), drag-drop zones, the IPC package design, the event hub, frontend display
> optimizations, and the React hooks layer.
>
> DIRECTION (user, 2026-07-30, later): growth is harvest-driven — when something nice emerges
> while developing another application, it gets generalized and promoted into Shenora (common
> design/library/tool sharing). And the kit must be able to adopt MOBILE application logic too:
> Capacitor (and similar) shells speaking the same IPC envelope through a pluggable transport.

## Open

### A. The second shell — MAUI. The round trip is PROVEN; the surface around it is not.

**Where it stands (2026-08-02):** `Shenora.Mobile` ships, is in the solution and the gate, and was run
on a real Android device — request/response, batched notifications, the structured error boundary,
the native file picker through the portable `IFileDialogs`, and the mission scheduler serializing a
contended mission. `samples/Shenora.Sample.Maui` hosts the SAME `Shenora.Sample.Logic` as the desktop
sample. Commits `a85280e` · `31b9aaa` · `b87cf9c`; evidence in `docs/ROADMAP.md` `## Done`.

**What that does NOT mean.** The items below are ordered by what unblocks an adopter, not by size.

_A1 (the client transport) and A2 (the capability stubs) are CLOSED — `docs/archive/tasks.md`. A2
closed by ANALYSIS rather than code: the hole it described does not exist, because the layering
already prevents it. Read that entry before re-proposing stubs._

_A7 (capability advertisement) is CLOSED — `ShellInfo` rides the ready handshake, so ONE web bundle
renders correctly on both shells by reading `bridge.shell` instead of sniffing the platform. Owner
direction 2026-08-02: "the universal I mean is more about the interfaces also about the frontend code
itself". Proven on the device (`shell: maui · capabilities: [filePicker]`); `ADOPTION.md` has the
adopter recipe._

_A3 (the adopter guide) and A4 (the decisions) are CLOSED — `ADOPTION.md` Stage 5 and
`DECISIONS.md` D32–D34._

_A5 (`dev.mjs android`) is CLOSED — `devices|connect|deploy|run|log|shot`, with the four traps folded
in. See `devtools/README.md`._

_A6 (iOS) is CLOSED — the THIRD shell runs. `dev.mjs mac` drives a Mac over SSH, `Shenora.Mobile` and
the sample multi-target `net10.0-ios`, and the simulator shows the same page answering the same
handshake with `maui · [filePicker]`, plus `ECHO` and `UI_STATE` (`onUiThread: true`) round trips.
`Shenora.Mobile` needed **no platform directive at all**; the sample needed one, for the log sink.
Five traps folded into `devtools/README.md`. See `docs/archive/tasks.md`._

- [ ] **A8 — `Shenora.iOS` 0.5.0 is BUILT but not PUBLISHED; the pipeline problem turned out not to
  exist.** 0.5.0 shipped four packages — Core, Ipc, Windows, Android — and silently omitted iOS,
  because `pack` skipped it and the release runs on Windows.
  - **The whole "iOS needs a macOS pack job" premise was WRONG** (owner, 2026-08-03: *"I dont think
    the ios package has any dependency to build on mac"*). A `net10.0-ios` LIBRARY builds anywhere the
    `maui-ios` workload is installed; only an APP needs Xcode, and the target that blocked the sample
    (`_ValidateXcodeVersion`) is conditioned on `_CanOutputAppBundle`. Verified by packing on Windows
    with no Mac and no override flags: identical `lib/` layout and nuspec to the Mac-built package.
    **The three-job release design was retired unbuilt** — it solved a problem that was not there.
  - Done as a result: `Shenora.iOS` is in the solution and gated on every run (its own metadata
    baseline, no surrogate), `macOnlyPackableProjects` is empty, and one `dev.mjs pack` on Windows
    produces all five packages plus the npm tarball. **No release-workflow change is needed.**
  - **What is left is only to publish it.** The next release cuts 0.5.1+ and carries iOS
    automatically; alternatively `Shenora.iOS.0.5.0.nupkg` can be pushed on its own to complete the
    version, since `src/` is unchanged since the `v0.5.0` tag and the package is byte-equivalent.
  - ⚠ **New machine prerequisite: the `maui-ios` workload**, alongside `maui-android`. CI has not been
    checked for it — 0.5.0 proved the runner has maui-android, nothing more. If a release fails on the
    iOS TFM, add `dotnet workload restore` to `release.yml`; that is the one-line fix and the reason
    to look there first.
  - Separately, RUNNING the sample on a simulator still rides two override flags
    (`ValidateXcodeVersion=false` + `MtouchLink=SdkOnly`) because that Mac's Xcode 26.3 is older than
    the workload's 26.6. That is an APP concern only, gitignored machine config, simulator-debug only
    — it never touches the packages. Device and Release iOS remain UNPROVEN.

### B. Staged application updates — DESIGNED 2026-08-02, nothing built

Design + evidence: `docs/2026-08-02-shenora-app-update-design.md` (two independent sibling
implementations, same two-phase model, same `{path, size, sha256}` manifest). The claim to build
against: **only the apply step is native.**

_B1 (the manifest + diff) is DONE — `Shenora.Core`'s `UpdateManifest`/`ManifestFile`/`ManifestDiff`,
10 tests, the two comparison rules sabotage-verified. `docs/archive/tasks.md`._

_B2 (the staging area) is DONE — `UpdateStage`, 9 tests, the write-the-marker-LAST ordering
sabotage-verified. It also carries B1's deferred empty-manifest guard._
_B3 (the release-source seam) is DONE — `IUpdateSource` + `UpdateStage.FetchAsync`. The kit ships no
implementation of the seam, and the differential-vs-full manifest distinction is documented on
`FetchAsync`._
- [ ] **B4 — the NATIVE launcher, and it is now much smaller than the design assumed.** Owner's call
  (2026-08-02): ship the apply logic as portable .NET first. Done — `UpdateStage.ApplyAsync` overlays,
  removes and clears, gate-covered and sabotage-verified, so **a self-contained app needs no native
  code at all.** What is left for the launcher is only what genuinely cannot be done in .NET: detect
  and install the runtime when it may be absent, then invoke the applier. Take Sonora's topology
  (app in `{root}/app/`, overlay only that) — the applier already documents and tests that layout.
  Still the one artifact this repo's gate cannot compile, so it ships as a TEMPLATE with that said
  plainly, and the sibling's Node harness (drive a PREBUILT exe over sandbox dirs) is the model for
  testing it on demand rather than in `verify`.

### From the first adopter, `Shenora.Core/Io` adoption attempt (2026-08-03)

Tried to adopt the Io layer and **declined both halves — but on SHAPE, not on quality**, which is the
kind of reason that should become kit work rather than a workaround. Both gaps are generalizable; neither
is that app's domain leaking in. (Its owner pushed back on the decline with "it's better we not reinvent
the wheel", which is why these are filed rather than quietly worked around.)

> DIRECTION (owner, 2026-08-03): *"compress is just one single case, think about any processing to
> file logic, video encoding, code building"* — *"that's why other projects when adopting suggest for
> atomic design"*.
>
> **This is the framing that makes the two items below one requirement rather than two conveniences.**
> Every long file operation has the same shape: read an input, spend real time producing an output,
> put the output where the input was. Encoding, thumbnailing, compiling, archive extraction, report
> generation, log rotation. Done naively — write over the target as you go — an interruption destroys
> the original AND leaves no usable output, and the longer the operation the wider the window. An app
> that gets this wrong does not find out in testing; it finds out when a user closes the lid.
>
> So the kit owes an ATOMIC FILE-TRANSFORM primitive, not just an atomic write. The write is the
> degenerate case where producing the output takes no time.

- [ ] **There is no SYNCHRONOUS, single-file atomic write, and it is the most common file operation a
  desktop app has.** `FileChange.Replace(TempPath, TargetPath)` is exactly the primitive — but the only
  way in is `IFileUpdateQueue.ApplyAsync`, an async queued multi-change applier with rollback and
  cross-process partitioning. Every config store in a desktop app is single-file, synchronous and
  best-effort, and at least one of them saves from a window-closing path where awaiting a queue is
  actively worse. So the adopter wrote ~30 lines (sibling temp → flush through to disk → rename over
  target) that the kit conceptually already owns.
  **Why it is worth having:** that app had FOUR stores using `File.WriteAllText`, which truncates before
  it writes. All four load best-effort (corrupt ⇒ defaults), so an interrupted write does not fail
  loudly — it silently resets the user's settings, chat history and UI language. That failure is
  invisible until someone notices their configuration reverted, and every sibling app has the same
  stores. Suggested: expose the primitive directly — `FileUpdates.WriteAtomic(path, contents)` or a
  synchronous `Replace(temp, target)` — with the flush-before-rename included, since a rename that lands
  while the content is still in the OS write cache is the same failure wearing a different hat.

  **VERIFIED against the source 2026-08-03 — the complaint is accurate on both halves.**
  `FileChange.Replace` is a *record describing* a change (`FileUpdates.cs:39`); the only code that
  executes one is `FileUpdateQueue` (`:378`), reachable only through `ApplyAsync`. There is no public
  synchronous path, and `FileUpdates` contributes no methods at all — only record types. Five
  constraints for whoever builds it, four already solved inside the queue and one NOT:
  - **Target-absent is a different operation.** `File.Replace` throws when the target does not exist,
    so a first write must `Move` instead. The queue branches on exactly this (`:380-389`); an API that
    forgets it passes every test on a machine that already has the file and fails on a fresh install.
  - **The temp must be a SIBLING of the target.** A rename is atomic only within a volume, so a temp
    in `%TEMP%` silently degrades to copy-then-delete across volumes. The adopter got this right by
    hand; the API should pick the temp path itself so a caller cannot get it wrong.
  - **Flush-to-disk before the rename — and NOTHING in `Io` does this today.** No `Flush`,
    `FlushToDisk` or `WriteThrough` anywhere in the layer: the queue renames a temp file the CALLER
    wrote, so durability is currently the caller's problem and quietly unmet. This is the one thing
    the queue does not already answer, and it is what makes "atomic" a fact rather than a claim.
  - **It bypasses `PathLocks` and partitioning deliberately — say so ON the API.** The queue exists
    for multi-change, cross-process, rollback-able work; this is last-writer-wins on one file. Right
    for a config store, wrong for what the queue was built for, and an undocumented fast path is
    exactly how someone reaches for it in the wrong place.
  - Open design question: `string contents` covers the common case but forces binary callers to
    buffer; a `Stream` or `Action<Stream>` overload avoids that. Pick deliberately rather than
    shipping the string one and bolting the other on later.

  **THE IMPLEMENTATION ALREADY EXISTS IN THE ADOPTER — port it, do not redesign it** (D8: lift the
  proven code and keep its post-mortem comments). It is ~30 lines plus six tests, written as an
  explicit STOPGAP with a comment saying to delete it when the kit ships the primitive. Its real path
  is in `local/EXTRACTION-MAP.md`. What to keep verbatim:
  - **A FIXED `.tmp` suffix, not a random name.** A crash before the move leaves ONE predictable
    leftover that the next successful write overwrites, instead of accumulating debris — which is a
    better answer than sweeping at startup, because there is nothing to sweep.
  - **`stream.Flush(flushToDisk: true)` before the move**, with the reason recorded: without it the
    rename lands while the content is still in the OS write cache, so a power loss leaves an intact
    rename pointing at an EMPTY file — the exact failure the rename was supposed to prevent.
  - **`File.Move(temp, path, overwrite: true)`, not `File.Replace`.** Simpler, needs no backup path,
    and a config store does not need the target's ACLs and timestamps preserved. (The queue uses
    `File.Replace` because it needs the backup for rollback — different job, different primitive.)
  - **Best-effort: returns `bool`, never throws, deletes the temp on failure.** The guarantee is that
    the PREVIOUS file survives — losing one edit is recoverable, silently reverting to defaults is
    not.
  - UTF-8 **without** a BOM; create missing directories; `FileShare.None` while writing.
  - **The six tests come too**, and one is worth stealing outright: *a failed write leaves the
    PREVIOUS file intact*, simulated by creating a DIRECTORY at the temp path so the write fails at
    exactly the point a crash would. Also pinned: a shorter value must REPLACE rather than leave a
    tail, no temp survives success, and the BOM check.
  - **Where the kit's version must differ:** the fixed suffix is safe for config precisely because
    those writes are short and last-writer-wins. A long TRANSFORM (the item below) cannot share it —
    two encodes of one file would collide on `x.tmp` — so that path needs a distinct temp or a
    `PathClaim`. Same primitive, different concurrency story; do not paper over the difference.
- [ ] **The general primitive: an atomic file TRANSFORM, of which the atomic write is a special
  case.** Per the direction above, this is the actual requirement — the write is just the transform
  whose "produce" step is instantaneous. Four steps, and the kit owns three of them:
  1. **Hand the caller a temp path beside the target** — sibling, so the final rename stays within
     the volume and stays atomic. The caller never chooses it, so it cannot be got wrong.
  2. **The caller produces into it** (encode, compile, extract, render). The kit does not care how
     long that takes or what tool does it, and **the input is never touched** — which is the whole
     point: an interruption here costs the work, never the original.
  3. **The caller VERIFIES it** — a predicate the app supplies, because only the app knows what valid
     means for its format. Seams over flags (`generic-library.md`): the kit must not grow a list of
     file types it knows how to validate. "Finished writing" is not "valid", and swapping in a
     truncated output destroys the original just as surely as writing over it would have.
  4. **The kit commits it** with the rename-over from the item above, or discards the temp.
  - **Composition, not duplication:** `UpdateStage` is this exact shape at release scale (stage →
    verify every file's hash → publish the marker last → apply). The primitive is that pattern for
    ONE file with a caller-supplied check instead of a hash, and the two should visibly share a
    vocabulary even if they do not share code.
  - It must also compose with `IMissionScheduler` for the long cases — an encode is precisely the
    "long-running work with a claim on a path" the scheduler exists for — and with `PathClaims` so
    two transforms of the same file cannot interleave. Getting that wiring right is most of the
    design work; the file mechanics are the easy part.
  - Interrupted transforms must leave no junk: name temps predictably and sweep them at startup.
- [ ] **`UpdateStage` assumes a PER-FILE source, so an archive-based release cannot use it without
  writing the bridge itself.** `IUpdateSource.OpenAsync(ManifestFile)` fits a release that publishes
  loose files. The adopter's releases publish one ZIP per part with a manifest listing per-file hashes —
  a shape at least as common, since it is what GitHub Releases encourages. Everything else fits
  perfectly: `UpdateStage` verifies every staged file's SHA-256 before the stage counts as pending,
  which is precisely the load-bearing step that app hand-rolled, with the same anti-truncation reasoning.
  Two consequences worth separating: the per-file DELTA buys nothing when the whole archive arrives
  anyway (fine — it just does not help), but the bridge itself is glue several adopters would write
  identically. Suggested: ship an archive-backed `IUpdateSource` (open a `ZipArchive` once, serve entries
  by manifest path). Small, and it turns "adoptable if you write an adapter" into "adoptable".
  Recorded honestly: the adopter declined partly on a BAD metric — adapter lines ≈ deleted lines — which
  misses that the tricky, worth-inheriting logic (staging, verification, journal, resume) is all on the
  kit's side and the bridge is boring. With the source shipped, this becomes a straight adoption.

  **VERIFIED 2026-08-03 — and the news is better than the entry assumes: the INTERFACE already fits.**
  `OpenAsync(ManifestFile) -> Task<Stream>` is exactly what a ZIP-backed source needs; it can hold one
  `ZipArchive` and return `entry.Open()` per manifest path. **No contract change, purely a shipped
  implementation** — which makes this cheaper than "ship an archive-backed source" sounds. Four notes
  for whoever writes it:
  - `FetchAsync` opens files **sequentially** (`UpdateStage.cs:275`, a plain `foreach` with `await`),
    so one shared `ZipArchive` is safe today. `ZipArchive` is NOT thread-safe, so that is a coupling
    to state in the XML rather than leave the next person to parallelise the loop and find out.
  - The archive stream must be **seekable**. Over a live HTTP response `ZipArchive` is forward-only
    and random access by entry fails; download to a file (or a `MemoryStream`) first.
  - The adopter publishes **one ZIP per part**, not one per release, so the source must map
    manifest path → (archive, entry). A single-archive implementation would only serve half of them.
  - It belongs where `IUpdateSource` already lives (`Shenora.Core`, no new package per D2) and needs
    no dependency: `System.IO.Compression` is in the shared framework.

### C. Held at the two-consumer bar

**Nothing below is blocking.** The 0.2.0 design pass (D1–D4) and the two whole-codebase reviews are
finished — record, rationale and verification in `docs/archive/tasks.md`. What survives below is what
those passes deliberately did **not** build, each held back by a named evidence bar rather than by
effort. That distinction is the point: none of these should be started because the list looks short.

### Held at the two-consumer bar (`generic-library.md`)

Surfaced by the D3 transport spike, which PASSED — `Shenora.Ipc` needed no change at all. These are
recorded so the next real non-WebView2 base arrives as EVIDENCE rather than a re-argument from
scratch; at that point the shape is already known.

> **The anticipated consumer #2 for the first two is an on-device (offline) mobile host** — see
> `docs/2026-08-02-shenora-mobile-offline-plan.md`. Its finding: the prerequisite sits with the
> ADOPTING app, not the kit — logic living inside transport handlers cannot move on-device, so
> factoring it behind a transport-neutral seam comes first.
>
> **UNBLOCKED 2026-08-02 by owner direction** (*"there should be a MAUI adaptation in the roadmap you
> can take too"*): the on-device host is being built, so it IS consumer #2 and these stopped being
> speculative. **All three of the plan's §4 prerequisites are now DONE** — the `IpcJson` resolver
> seam, `IpcHostBridge`, and the headless `IShenoraRunner` (`docs/archive/tasks.md` for each). What
> remains below is the ONE item that direction did not unblock, because no spike can: it needs a real
> mobile consumer, not a plan. The bar still applies to everything not on that list.

- [ ] **The desktop-FLAVOURED service contracts — EVIDENCE HAS NOW ARRIVED, and it is better than
  expected.** `FileDialogContracts.cs` concedes in writing that `FileDialogOptions` carries Win32
  vocabulary and that "a mobile picker would ignore the validation hints and return a content URI",
  and this was held for a real mobile consumer rather than another spike. `MobileFileDialogs` is that
  consumer, and the finding is: **`OpenFileAsync` needs NO break.** `FileDialogResult.FilePath` is
  already specified as "a path or URI the HOST can resolve", which is exactly what Android returns;
  the desktop-only options are simply ignored, and which ones is now written in the implementation's
  XML rather than left to be discovered.
  **OPEN is now universal end to end** (owner direction, 2026-08-02: *"make the frontend and
  interface as universal as we can"* — the C# layer is device-dependent, the JS is not).
  `IFileDialogs.OpenReadAsync` is the missing half: portable logic reads a picked handle through the
  contract instead of calling `File.OpenRead` on it. Measured on a device: MAUI's picker COPIES the
  document into app cache and returns a real path, so the default works on both shells — but the
  method exists so a shell returning a genuine content URI can override it invisibly.

  **What is still open, and it is narrower than before:** `OpenFolderAsync` and `SaveFileAsync`.
  Neither has a MAUI Essentials equivalent — checked by compiling: `FolderPicker` and `FileSaver`
  live in **CommunityToolkit.Maui**, a UI-component package D13 forbids the kit from taking. So
  Android needs raw Storage Access Framework (`ACTION_OPEN_DOCUMENT_TREE` /
  `ACTION_CREATE_DOCUMENT`), which returns URIs and needs Activity-result plumbing.
  - **Save is the harder one and the more interesting**: a picked cache path cannot be written back
    to the user's document, so "give me a path to save to" is not expressible on this platform at
    all. The universal shape is `SaveAsync(options, write)` — pick and write in one call, host-side —
    mirroring `OpenReadAsync`. That is the design; it is unbuilt because the SAF plumbing is real
    work and no consumer has needed it yet.
  - **Folder picking is CLOSED as a portable capability — see D35.** Owner's framing: on mobile
    "open folder" means the camera roll, or the app's own space, or a system-authorized grant; on
    desktop it is free access to any path. Same word, different guarantee. So it is documented as a
    DESKTOP capability and the mobile refusal points at the three intents that ARE portable —
    `ShenoraPaths` (app-owned space, no picker), a media picker (camera roll), and
    `OpenFileAsync` + `OpenReadAsync` (one document).
  - _The media PICKER is folded into the media library below — it was held at the two-consumer bar,
    and that bar is now cleared three times over._

### D. Media — the first thing THREE consumers all need

> DIRECTION (owner, 2026-08-02): *"we also need to add Media library into roadmap (this also why I
> push for interface library merge, because 3 of my application will need this)"* — the video-library
> sibling, Sonora, and the business-manager sibling.

**This is the first item to clear the two-consumer bar outright rather than argue its way past it**
(`generic-library.md`), and the bar exists precisely so that when three real consumers DO show up the
answer is yes without further debate. It also retroactively justifies the packaging work: a media
surface that three apps share is exactly what "one shell package per platform" is for — each platform
implements it differently, none of them leaks into app logic.

- [ ] **D1 — Harvest before designing.** Three consumers means three existing implementations, and
  the extraction rule (D8/D15) says the design is IN them, not ahead of them. Read all three first
  and write down what they actually share, the way `docs/2026-08-02-shenora-app-update-design.md` was
  written from two sibling updaters rather than from first principles. Expect thumbnails, duration
  and dimension probing, format/codec detection, and a cache keyed on content — but expect the
  disagreements to be the interesting part.
- [ ] **D2 — Where it goes, before any code.** Media is a mix of genuinely portable logic (a cache
  index, a thumbnail request/result contract, a probe result shape) and per-platform decoding. So
  `Shenora.Core` holds the CONTRACTS and each shell package the implementation — the D19/D20/D37
  placement law, applied to a case where the platform split is unusually sharp: Windows has Media
  Foundation / WIC, Android has `MediaMetadataRetriever` and `ThumbnailUtils`, iOS has AVFoundation
  and `QLThumbnailGenerator`. **Nothing here justifies a new package (D2)**; if it seems to, that is
  the signal to re-read why.
- [ ] **D3 — The picker rides along.** `MediaPicker.PickPhotosAsync` exists in MAUI Essentials
  (verified by compiling) and the desktop equivalent is a multi-select image dialog. D35 already
  identified "let the user hand me some media" as one of the three portable intents behind the
  desktop-only "open a folder", so this closes that gap rather than opening a new one.
- [ ] **D4 — Say what is NOT in scope.** No playback, no transcoding, no editing, no codec bundling —
  those are products, and D21 keeps products out of the kit. If a consumer needs playback it composes
  its own over the contracts, the way the sample builds a co-browse pane over `StreamingSession`.
- [ ] **D5 — Media supplies the VERIFY, the Io layer supplies the mechanism.** The atomic-transform
  primitive is general and lives in `Io` (see the adopter section above); what is media-specific is
  what "valid" means — does it decode, is the duration within tolerance, are the expected streams
  present. Note this cannot reuse `UpdateStage`'s answer: that verifies a SHA-256, and a re-encode is
  not byte-predictable, so the media check is semantic rather than exact.

### E. Off-screen sessions cannot see the app's OWN content

- [ ] **E1 — `SessionBrowserOptions` has no resource seam, so an off-screen session can only reach
  network-reachable URLs.** Found 2026-08-02 while chasing the sample's broken stream: with the
  navigation guard fixed, `StreamingSession` navigates happily to the packaged app's virtual host
  (`https://sample.local`) and renders **WebView2's "can't reach this page"**, because the session
  browser has its own environment with none of the main host's serving set up.
  `SessionController` exposes no `CoreWebView2`, so an app cannot even bolt it on from outside.
  - The main host solves this with `IWebViewResourceProvider` + `VirtualHost`; sessions have neither.
    The likely shape is to let `SessionBrowserOptions` take the same provider the host already uses,
    which would make "co-browse / render MY OWN UI off-screen" work in packaged mode.
  - **Who this bites:** a desktop-only app serving an embedded bundle. NOT a server-backed one —
    Sonora's pages are on a real loopback origin, so it is unaffected. That asymmetry is why this
    survived unnoticed: both sample demos work in dev mode, and the e2e runs there.
  - Until then, `docs/ADOPTION.md` says plainly that off-screen sessions reach network URLs only.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
- [ ] **Keep naming the concrete bug each ADOPTION stage removes.** The first adopter's Stage-0
  feedback (2026-07-31), recorded here as the habit it is rather than as work: what made the adoption
  decision easy was "Stage 1 carries no IPC dependency, so it deletes the most duplicated code for the
  least risk; the IPC substrate comes last because it is the only stage that touches every module" —
  and what justified adopting a kit at all was naming the specific bugs a hand-rolled shell tends to
  have (the DPI-mis-scaled `Screen.WorkingArea` restore; `CloseReason.UserClosing` firing for a
  programmatic `Close()`). Write new stages the same way.
