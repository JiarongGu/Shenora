# Changelog

All packages (NuGet `Shenora.*` + npm `@shenora/react`) version in lockstep from
`src/Directory.Build.props` (`VersionPrefix`). From 1.0, SemVer 2.0 applies, gated by the
API-surface baseline tests; while every consumer is one of the author's own applications, a
**documented** break may ship in a MINOR release — always called out under a `### Breaking`
heading here. Released versions are listed newest first; within `## Unreleased`, entries are in
landing order (oldest first) because they narrate one version being built.

**Each `###` heading appears AT MOST ONCE per version** — append to the existing group, never open a
second one. `## Unreleased` had grown two separate `### Breaking` lists (P5.5 H7), which is worse
here than untidy: that heading is the SemVer gate at 1.0, so a reader scanning it would have stopped
at the first list and missed five more breaking changes.

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
  exactly: `Shenora.Core.txt` lost 206 lines and gained none. An app that never mutates a file tree simply
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

  **Migration: add `using Shenora.Core;` to files that name these types.** That is the whole fix, and it
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

  It cannot be called `File`: a consumer with both `using System.IO;` and `using Shenora.Core;` would
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
- **`Shenora.Core.AppCallback.Log(Action<string>? sink, Func<string> message)`** — the guarded, lazy
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
  **What a consumer must change:** add `using Shenora.Core;` where these types are referenced —
  `IFileDialogs`, `IFileDialogPathStore`, `FileDialogOptions`, `FileDialogFilter`,
  `FileDialogResult`, `IClipboardService` moved namespace (identical signatures otherwise). Nothing
  needs re-registering: `UseWinForms` registers the same implementations, now behind both the
  Windows and the portable interface.
  `IShellLauncher` and `IFormInteraction` were **split**, not changed: they now derive from
  `Shenora.Core.IUrlLauncher` and `Shenora.Core.IUiInteraction` respectively, so `OpenUrl`,
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
- **`Shenora.Core.AppCallback`** (P5.5 H2) — the one guard for invoking APP-SUPPLIED code from a place
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
