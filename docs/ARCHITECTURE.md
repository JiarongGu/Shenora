# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/DECISIONS.md`; this file records only what
EXISTS. There are no dated design docs any more — D57.)

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`, the same way README.md's
     headline is. Don't hand-edit it — and don't date this line either: the release workflow owns
     the version, so a hand-written one is stale the moment a release cuts. Everything ELSE in this
     file dates its claims instead of versioning them, for the same reason. -->
## Current state — **v0.10.0 published**; P1–P7 complete (v0.1.0 shipped 2026-07-31)

Six packable projects + `@shenora/react` on npm. **Five are the SHELL set, organised BY
PLATFORM since 0.5.0 (D37)**: `Core`, `Ipc`, `Windows` (the three old Windows ids merged), `Android`
and `iOS`. All five ship from ONE Windows runner — 0.5.0 published only the first four because `pack`
skipped iOS on the mistaken belief that it needed a Mac, and 0.5.1 corrected that. The sixth is the
native `Launcher` (D50). ⚠ **There is NO optional-feature tier, and this paragraph used to say there
was**: `Media`, `IO` and `IO.Compression` were packages of their own until 2026-08-07 and are now
FOLDERS inside `Core` (D53, D55) — a capability gets a namespace, never a package id, because a
nuget.org listing of single-domain libraries makes a claim about the product nobody meant. The
authoritative set is the header table of `docs/DECISIONS.md`, once. Since the summary below was written, P5.5
landed the
D19/D20 re-layer (`WebView2` → `WinForms`; portable contracts + `IUiDispatcher` in `Core`, enforced by
a `net10.0` sample that turns red if a Windows type reaches app logic), P5.6 added native caption
buttons, P6 readied adoption (`docs/ADOPTION.md`, and six capability gaps found and closed), and P7
stabilised: every public and protected member documented with CS1591 as an error, the login RECIPE
moved out of the library to the sample (D21/D22 amended), and the release pipeline hardened. The
narrative is `CHANGELOG.md`.

**2026-08-01 — the communication core** (D23,
implemented; drafted under the name "0.2.0" and released later that day as part of v0.3.0): the module
contract now carries the EVENT path — `IModuleContext` (`Publish`/`Start`/`Run`/`Logger`) is the
second parameter of `ModuleBase.RouteMessageAsync`, the one breaking change this release makes. A new
operations cluster in `Shenora.Ipc` tracks long-running work (id, status, progress, cancel-by-id,
throttled progress emission) as mechanism only — what an operation IS stays app-defined. The
transport-neutral half of the outbound notification pipeline moved out of `WebViewIpcBridge` into
`Shenora.Ipc`'s `NotificationPump`, so `WebViewIpcBridge` is now a thin WinForms/WebView2 adapter over
it (D16's "the seam, not the package" applied to the host half). `@shenora/react` gained
`useShenoraRequests`/`createRequestsStore`, a host-backed store mirroring the pattern
`createShenoraStore` already established. (No 0.2.0 release exists — `CHANGELOG.md`
`## 0.2.0 — never released` has the account. **Dates, not version numbers, are how this file marks
time**, precisely because that story exists: a version is assigned by the release workflow, so any
version written into prose is a guess about the future.)

**2026-08-02 — `Shenora`'s mission-scheduling + filesystem-claims layer**
(D27–D31): one scheduler whose key spaces are pluggable, so
a filesystem operation planner (paths conflict by containment) and a job queue (lanes admit N) are the
same engine — the EXECUTION half of long-running work, composing with `Shenora.Ipc`'s operations
cluster (the REPORTING half) rather than merging with it. Surface below; adopter-facing mapping in
`docs/ADOPTION.md`.

P2 delivered the core host (builder, WinForms runner, WebView2 hosting + serving, samples). P3
delivered the full IPC stack (wire contract, dispatcher + facades, event bus, postMessage
transport, `@shenora/react` client, live round-trip). P4 delivered the native desktop surface:
the scoped-container router + standard IPC composition, frameless chrome + frontend window
commands, STA dialogs/shell/clipboard/interaction services, drag-drop zones + `useDropZone`
(+ per-monitor DPI handling), secondary windows + tray. P5 added the `Shenora.Windows`
package: the one browser-configuration path, a bounded LIFO render-session pool, the login-window
stack (persistent per-account profiles, silent refresh, clear-on-logout), and co-browse streaming
— all proven live in the sample.

```
Shenora.slnx
├── src/
│   ├── Shenora        net10.0          — deps: M.E.DependencyInjection (impl, D17), M.E.Logging.Abstractions
│   │                                        ── Media/ (namespace Shenora.Media; a PACKAGE until D53,
│   │                                          2026-08-07, now shell work inside Core)
│   │                                          The TRANSLATION LAYER for the web (D52): the minimum
│   │                                          transformation that makes a file the user already has
│   │                                          playable in a webview, and never more. Not a media
│   │                                          toolkit, not a codec library, not ffmpeg. It is a
│   │                                          PIPELINE, and the folders are that pipeline:
│   │                                            Probe/  what is inside the file — MatroskaProbe reads
│   │                                                    an EBML header with no external tool, so
│   │                                                    "can this play?" costs a few hundred bytes
│   │                                                    rather than a shipped toolchain.
│   │                                            Plan/   the minimum move, per STREAM not per file —
│   │                                                    Direct | Remux | Transcode | Unsupported. A
│   │                                                    pure function, which is why it is the part a
│   │                                                    test pins exactly (D42).
│   │                                            Serve/  handing the result to the page:
│   │                                                    UseMediaConversion (one finished file) and
│   │                                                    UseSegmentStream (an HLS window, playable
│   │                                                    seconds in). Both are MIDDLEWARE registered on
│   │                                                    the shell's IWebViewInterceptor.
│   │                                            Engine/ the transform stage — Mp4Remuxer (Matroska → MP4,
│   │                                                    every frame copied untouched, no codec at all)
│   │                                                    plus the ISegmentEngine seam for what the kit
│   │                                                    does not do itself.
│   │                                            Play/   IMediaPlayer — the HOST plays, the page drives
│   │                                                    (D54). MediaPlayerBase holds the state machine the
│   │                                                    NATIVE players share — terminal states survive a
│   │                                                    platform transition, a paused rate is deferred, a
│   │                                                    cancelled open ends Empty, an abandoned open
│   │                                                    throws rather than hanging — so a shell writes
│   │                                                    ~40 lines, not ~150. Implementations:
│   │                                                      · MediaPlayer — the DEFAULT. Lifecycle in .NET,
│   │                                                        display and sound in a page element, driven
│   │                                                        over IEventBus (MediaPlayerEvents) with the
│   │                                                        page answering via Report(). It owns the
│   │                                                        decision the four stages above used to leave
│   │                                                        to each app:
│   │                                                        probe → plan → resolve the URL, which is how
│   │                                                        the interceptor's conversion route becomes
│   │                                                        this player's OUTPUT PIPE rather than a
│   │                                                        parallel feature (D58).
│   │                                                      · IosMediaPlayer (AVPlayer) — for the case a
│   │                                                        page element cannot serve: iOS pauses a
│   │                                                        <video> when backgrounded.
│   │                                                      · AndroidMediaPlayer (android.media.MediaPlayer,
│   │                                                        NOT ExoPlayer — D51 ships no engine).
│   │                                                      · WindowsMediaPlayer (Media Foundation, via
│   │                                                        Windows.Media.Playback) — the desktop native
│   │                                                        one: playback that survives the webview, and
│   │                                                        the platform's codec set rather than the
│   │                                                        webview's subset.
│   │                                                    ⚠ The natives are registered BY THEIR OWN TYPE,
│   │                                                    never as IMediaPlayer — that stays the page-backed
│   │                                                    MediaPlayer on every shell, so a page's
│   │                                                    PLAYER_REPORT cannot land on a native player that
│   │                                                    has no Report to take. Opt in by name.
│   │                                                    An Android native player is absent
│   │                                                    rather than stubbed.
│   │                                                    ⚠ This BOUNDS the four stages above rather than
│   │                                                    replacing them: they exist for apps serving bytes
│   │                                                    to a <video>, and stop being the answer to "the
│   │                                                    webview cannot play this". Ships no queue,
│   │                                                    playlist or effects — only the app knows what
│   │                                                    "next" means, as with IPlaybackSession.
│   │                                          ⚠ It still implements NO interception. Serving bytes to a
│   │                                          page configures a WEBVIEW and is a shell capability, so
│   │                                          IWebViewInterceptor lives in Core and each shell
│   │                                          implements it (D45); Serve/ COMPOSES on that contract.
│   │                                          <video>/<audio>/<img> over ordinary local files need none
│   │                                          of this — it is what answers a file the platform CANNOT
│   │                                          decode.
│   │                                          ⚠ In Core because that is where the thing it is "the same
│   │                                          category as" already lives (D53): serving a local file.
│   │                                          It was its own package until 2026-08-07, on a premise D51
│   │                                          made permanently false — that a demuxer means real shipped
│   │                                          BYTES. No engine byte ever ships, so what remained was
│   │                                          98 KB of the kit's own managed IL. Ships no codec LIST:
│   │                                          the mechanism is the kit's, the policy the app's (D42).
│   │                                    Files/ namespace Shenora.IO — file operations, whole:
│   │                                            Files/FileReplacement (atomic replace, the default
│   │                                                    behind IFileDialogs.SaveAsync)
│   │                                            PathClaims — a claim SCOPE over the mission types;
│   │                                                    scheduling vocabulary that is about paths
│   │                                            FileUpdateJournal/Queue — the journal is written BEFORE
│   │                                                    the mutation, so undo is DATA and recovery can
│   │                                                    roll back Applying and FINISH Committing
│   │                                            PathLocks — cross-process advisory leases, taken after
│   │                                                    the in-process gate, in sorted path order
│   │                                            UpdateManifest/UpdateStage — the staged self-updater
│   │                                                    with per-file verification
│   │                                            Compression/  namespace Shenora.IO.Compression —
│   │                                                    containment-checked ZIP extraction (any entry
│   │                                                    escaping its destination is refused) with
│   │                                                    size/count limits, plus ZipUpdateSource.
│   │                                                    No native engine: zip works on the framework
│   │                                                    alone, and any other format is a SEAM (D42).
│   │                                          ⚠ These were two packages (D48) until 2026-08-07 (D55).
│   │                                          The layering D48 established is INTACT and still visible
│   │                                          here — the edge runs Files/ → Core because every type logs
│   │                                          through AppCallback, which is also why merging them INTO
│   │                                          Core was the only mechanism available: a Core that packed
│   │                                          Shenora.IO.dll would have to reference it, and it already
│   │                                          references Core. What changed is the package COUNT, not
│   │                                          the structure.
│   ├── Shenora.Launcher/   (C++17, CMake — NOT a NuGet package yet)
│   │                                          The native APPLY step, which is the one part of staged
│   │                                          updates that cannot be done in .NET: it runs when the
│   │                                          runtime may be absent and must replace files the app
│   │                                          holds open. A LIBRARY (manifest parse, overlay, tracked
│   │                                          removals, the platform seam) plus a TEMPLATE `main.cpp`
│   │                                          an app copies and edits four constants in — the split
│   │                                          D50 took from §0's measurement of two donor launchers.
│   │                                          One tree for Windows + Linux: std::filesystem everywhere,
│   │                                          Win32/POSIX behind include/shenora/platform.hpp, and BOTH
│   │                                          compiled on every build so neither can rot.
│   │                                          It re-hashes NOTHING — `ready.json` exists only when the
│   │                                          C# side verified the whole stage, and re-verifying would
│   │                                          duplicate a rule that can drift.
│   │                                          Gated by the RELEASE workflow's launcher matrix (win-x64 +
│   │                                          linux-x64) running the conformance harness against the
│   │                                          built binary, NOT by `dev.mjs verify`, which has no C++
│   │                                          toolchain and deliberately does not grow one.
│   ├── Shenora.Launcher/    (C++17 + CMake, plus the packaging csproj)
│   │                                          B4b: puts the per-RID launcher binaries the `launcher`
│   │                                          release matrix builds (win-x64 + linux-x64) into one nupkg
│   │                                          under runtimes/{rid}/native/, alongside the C++ library
│   │                                          sources and template under launcher-src/ so an adopter
│   │                                          can either use the stock binary or build their own.
│   │                                          It consumes DOWNLOADED artifacts because the binaries
│   │                                          come from two different toolchains on two different
│   │                                          runners — no single `dotnet pack` can produce both — so
│   │                                          `dev.mjs pack` skips it unless they are staged, and the
│   │                                          csproj ERRORS rather than shipping an empty runtimes/.
│   │                                          ⚠ It is the one packable project with NO managed
│   │                                          surface, so it declares <NoManagedSurface>true</> and
│   │                                          MetadataSurfaceTests exempts it BY THAT DECLARATION —
│   │                                          delete the line and the baseline gate turns back on.
│   ├── Shenora.Windows     net10.0-windows  — deps: Shenora, Microsoft.Web.WebView2
│   │                                          The Windows shell, WHOLE (merged 2026-08-02 from
│   │                                          WinForms + WebView2 + WebView2.Sessions). Three folders
│   │                                          keep the areas legible: Shell/ (primitives — bootstrap,
│   │                                          frameless chrome, tray, secondary windows, window state,
│   │                                          STA dialogs/clipboard, the UI-thread dispatcher),
│   │                                          WebView/ (hosting, serving, IPC bridge, drop zones,
│   │                                          window commands), Sessions/ (render pool, interactive,
│   │                                          streaming). The old split protected a WinForms-without-
│   │                                          WebView2 consumer that cannot exist in a React-in-a-
│   │                                          webview kit; D19's package edge is now internal.
│   ├── Shenora.Mobile/     (SOURCE, no csproj) — the mobile shell's shared code; NOT a package, and
│   │                                          there is deliberately no Shenora.Mobile on nuget.org.
│   │                                          Ipc/ Threading/ Services/ Hosting/ WebView/, compiled
│   │                                          INTO both platform packages by Shenora.Mobile.props.
│   │                                          🔴 IT HOLDS WHAT IS GENUINELY SHARED, which is real
│   │                                          because both shells are MAUI: the HybridWebView IPC
│   │                                          transport, the UI dispatcher, the safe area, the
│   │                                          interceptor, the host composition. It is NOT a place
│   │                                          for two implementations behind an #if — five types
│   │                                          were exactly that until 2026-08-08 and moved out.
│   ├── Shenora.Android     net10.0-android  — deps: Shenora, Microsoft.Maui.Controls
│   ├── Shenora.iOS         net10.0-ios      — same deps, same source, and it builds on WINDOWS: a
│   │                                          net10.0-ios LIBRARY needs only the maui-ios workload,
│   │                                          never Xcode. Only an iOS APP needs a Mac.
│   │                                          The SECOND shell, one package per platform. Peers of
│   │                                          WinForms+WebView2, not layers on them: neither
│   │                                          references either. Thin by construction — the IPC
│   │                                          substrate is already portable, so this is the
│   │                                          HybridWebView adapter, a UI dispatcher and the
│   │                                          Essentials-backed Core contracts.
│   │                                          Both PROVEN on device/simulator. Divergence goes in
│   │                                          each project's OWN Services/ folder, named and
│   │                                          namespaced for its platform (AndroidMediaPlayer,
│   │                                          IosMediaPlayer) exactly as WindowsMediaPlayer is —
│   │                                          and needing no #if, because each shell is single-TFM
│   │                                          so the build already selects it.
│   │                                          Since 2026-08-03 there IS some: SaveAsync, because SAF
│   │                                          and UIDocumentPicker have nothing in common. It is a
│   │                                          `partial` method, so a THIRD platform cannot compile
│   │                                          until it decides what save means (CS8795), rather than
│   │                                          inheriting a stub that refuses at runtime.
│   │                                          BOTH are in the solution and gated on
│   │                                          every run — LIBRARY, not app, so no Mac is involved.
│   └── Shenora.React/      @shenora/react    — peer: react >=18; build tsc, test vitest
├── tests/
│   └── Shenora.Tests       net10.0-windows  — xunit; references the four leaf src projects (Core transitively)
└── samples/                                 — never packable; the e2e subject (dev.mjs sample/vite/shot/wgc/click)
    ├── Shenora.Sample.Desktop  net10.0-windows — the reference composition (builder → UseWindows →
    │                                            prewarm → WebViewHost + provider + SplashPanel +
    │                                            frameless OptimizedForm + WindowCommandModule +
    │                                            DropZoneManager/Facade + SecondaryWindows + TrayIcon +
    │                                            SampleModule → MessageDispatcher → WebViewIpcBridge,
    │                                            1 Hz IEventBus tick source); embeds wwwroot
    │                                            (built by the web sample, gitignored)
    ├── Shenora.Sample.Logic    net10.0         — the PORTABILITY PROOF (H4.3): one facade that picks
    │                                            a file, reads the clipboard and opens a URL through
    │                                            the Core contracts only (IUrlLauncher, NOT the
    │                                            Windows IShellLauncher). Plain net10.0 with no
    │                                            Windows reference, referenced by the desktop sample
    │                                            and in the solution — so a Windows type dragged into
    │                                            a portable contract turns the build RED instead of
    │                                            leaving D20's portability merely asserted. Also the
    │                                            SCHEDULER's worked example: SCHEDULE_DEMO submits
    │                                            four items (two contending for one path, two
    │                                            disjoint) under a capacity-2 lane, and
    │                                            MissionEventPublisher is the ~35-line IMissionObserver
    │                                            adapter that publishes them as an EVENT stream
    │                                            (D66: a mission is host-initiated work, not a
    │                                            request) — execution, reporting and the
    │                                            seam between them, all with no Windows reference.
    │                                            CHAIN_DEMO adds the composition an adopter builds:
    │                                            two MissionChains whose staging steps overlap and
    │                                            whose file landings go through one IFileUpdateQueue
    │                                            partition (proven live 2026-08-02 — both staged at
    │                                            the same millisecond, both landed through the queue)
    └── Shenora.Sample.Web      Vite + React    — consumes @shenora/react (file:), port 3900, builds
                                                 into the desktop sample's wwwroot; page-owned title
                                                 bar (WindowCommands + useWindowMaximized), notifyReady,
                                                 useShenoraQuery echo, useShenoraEvent tick, useDropZone
                                                 target, secondary-window controls, dev interceptor
                                                 (the e2e subject)
```

- Version: single `<VersionPrefix>` in `src/Directory.Build.props`; npm + README synced by
  `dev.mjs pack`/`doctor --fix`.
- Package metadata (authors, license MIT, repo URL, snupkg symbols, SourceLink, README-in-package)
  is shared in `src/Directory.Build.props`; each csproj adds only `PackageId` + `Description`.
- Central package management: `src/Directory.Packages.props` (root file is an import shim).

## The subsystems, and what KIND each one is

Written 2026-08-07 after the owner asked for the list — *"IPC, Queue/Mission, Media, FileSystem (include
dropzone?)"*. Naming them exposed that they are not four peers, and that the answer to the DropZone
question is "no, it is a different category". **Four kinds, not one list:**

| Kind | What it is | Members | How an adopter reaches it |
|---|---|---|---|
| **Host** | the application object, its lifecycle and the things every app needs | builder · runner · `ShenoraPaths` · `ShenoraEnvironment` · `IEventBus` · `AppCallback` | `ShenoraApplication.CreateBuilder(…)` |
| **Shell** | one per PLATFORM; picks who owns the UI loop | `UseWindows` · `UseAndroid`/`UseIOS` · `UseHeadless` | exactly one, at startup |
| **Engines** | portable logic with no platform code — the kit's own algorithms | **Missions** (`Missions/`) · **Media** (`Media/`) · **File system** (`Files/`) | one `Use…` call each |
| **Shell capabilities** | "can this platform do X?" — a portable contract, an implementation per shell | dialogs · clipboard · **drop zones** · tray/windows · `IPlaybackSession` · `ILiveActivities` · safe area · `IUrlLauncher` · `IUiDispatcher` · `IFileLockInspector` · `IMediaCapability` | injected; registered BY the shell |

- 🔴 **DropZone is NOT a peer of the other three — it is a shell capability**, and that is why it has no
  folder and no `Use…` call. It is a `ShellCapability` declaration + an IPC module + a React hook
  (`useDropZone`), with the real work in `Shenora.Windows`' overlay. Ask of anything proposed as a
  subsystem: **does it have portable LOGIC of its own, or is it a platform ability the kit is exposing?**
  Drop zones are the second. So are clipboard, dialogs and Now Playing.
- **The engines are the answer to "what does .NET do that React cannot" (D54)** — they are where the kit's
  own thinking lives, and each is a few thousand lines of portable code the page could not run.
  ⚠ **All three now register the same way** (`UseMissions` · `UseMediaPlayer` · `UseFileSystem`), which
  they did not until this pass: missions still made an adopter write `new MissionScheduler(options)` while
  the other two had one-call registration. Three engines, three ways in, was the inconsistency that naming
  them found.
- **There are exactly TWO middleware pipelines, and they share an idiom on purpose** —
  `IMessageDispatcher` for messages (`UseRoute`/`UseModule`/`UseLogging`/`UseErrorHandler`/`UseScopedRouter`)
  and `IWebViewInterceptor` for resources (`UseFiles`/`UseMediaConversion`/`UseSegmentStream`/`UseMediaPlayer`).
  ⚠ **The split between them is the D62 line: messages carry INTENT, resources carry BYTES.** A media file
  has never travelled through the message pipe, which is why a binary IPC envelope would not speed up media.
- **IPC is a CORE, and it is a FOLDER inside `Shenora` (D65, 2026-08-07).** It used to be its own
  package on the argument that a server-backed app might take it without a shell (D10) — which was a
  LAYERING answer to a question nobody was asking, the same shape D55 rejected for `Shenora.IO`. What
  decides a package boundary is what the package SET says the product is, and a separate `Shenora.Ipc`
  said "optional". It is the opposite: **IPC is one of the three cores** — the contract both sides agree
  on — so it ships with the framework or the framework does not work.
  ⚠ The fold was proven exact rather than asserted: `Shenora`'s baseline went 1172 → 1484 lines,
  `Shenora.Ipc.txt` was 312, and the set difference between the sum and the result is EMPTY in both
  directions. Namespace `Shenora.Ipc` is unchanged, so adopters delete a `PackageReference` and no code.

## Public surface

Gated by the API-surface baseline tests (`tests/Shenora.Tests/Api/Baselines/*.txt` — tracked;
drift writes a gitignored `.actual` and fails; copy over the baseline only for intentional
changes, noting them in `CHANGELOG.md`).

- `Shenora` — `ShenoraEnvironment` (the ONE dev-mode detection: `DOTNET_ENVIRONMENT`/
  `ASPNETCORE_ENVIRONMENT` or the `.dev` marker; base directory); `AppRootArgument`
  (`--app-root` launcher-arg parsing); `ShenoraPaths`/`ShenoraPathsOptions` (the portable on-disk
  layout authority: explicit-root → root env var → libs-parent detection → base dir; data env
  var for child-process sharing; ensure-created `DataArea`s); the application builder —
  `ShenoraApplication(+Options)` (`CreateBuilder` resolves `--app-root` → paths → environment;
  `Run()` executes the registered runner; `Start()`/`Stop()` are the lifecycle-hook sequence itself —
  one owner, both idempotent, driven directly by a host whose platform owns the loop and used
  internally by every runner so a second shell cannot drift; `Dispose` owns the provider),
  `ShenoraApplicationBuilder` (`Services`, `AddModule`, `OnStarting`/`OnStopping`, build-once),
  `IShenoraModule` (per-feature service registration), `IShenoraRunner` (the host-loop seam),
  `UseHeadless`/`HeadlessRunnerOptions` (the no-UI runner: hooks → block on a stop token or
  SIGINT/SIGTERM → ordered shutdown, so `Run()` no longer needs a Windows package. NOT for a host
  whose platform owns the loop — a MAUI activity cannot honour "blocks until shutdown" and brings
  its own runner), `IShenoraLifecycleHook` (DI-registered start/stop participation; runners invoke post-gate);
  the in-process event bus — `EventMessage` (`{id, module, type, scope?, payload?, timestamp}`,
  host-side; the wire form is `Shenora.Ipc`'s notification envelope), `IEventBus`/`EventBus`
  (`"*"` wildcards + per-subscription match cache; unscoped subscriptions see every scope and
  global events reach scoped subscribers; handler failures logged + isolated; `EmitAsync` awaits
  every handler, `Emit` is the fire-and-forget twin for a synchronous caller; auto-registered
  by `Build()` via `TryAdd` — replaceable).
- **`Shenora`'s resource-interception layer (2026-08-04, D45)** — how a page gets bytes the platform will
  not give it, portable and identical on all three shells. `WebViewResourceRequest`/`Response`/
  `WebViewByteRange` are the exchange (relocated here in 0.8.0 by D2a); on top of them:
  `IWebViewInterceptor` (`RangeDelivery` + `Use(middleware)` → `IDisposable`), the
  `WebViewResourceMiddleware`/`WebViewResourceHandler` delegates, and `WebViewResourcePipeline` — the
  registry and composition itself, shared by every shell so the back-to-front chain build, the
  copy-on-write array and reference-identity removal exist ONCE and are unit-testable with no webview.
  **MIDDLEWARE, not a handler list,** because the cross-cutting concerns are the point (containment, a
  cache, a metric, a log of what a payload decoded to) — the same shape `IMessageDispatcher` already is
  for messages, applied to bytes.
  `WebViewRangeDelivery` (`Sliced`/`Unsliced`) is a property of the INTERCEPTION rather than of the
  content: Android's webview applies the `Range` start to whatever body it receives, WebView2's and iOS's
  send it verbatim (D44, measured on each).
  🔴 **`WebViewPipeline` is the APP-LEVEL half of the same seam** (D64, 2026-08-08): the app declares its
  routes ONCE on the built application — `app.UseFiles(…)`, `app.UseMediaPlayer()`, `app.Use(…)` — and every
  webview it hosts receives them. Registered by `ShenoraApplicationBuilder.Build`, reached as
  `ShenoraApplication.Pipeline`, and handed to each webview by the shell: the desktop travels through
  `WebViewHostOptions.Pipeline` (nullable, so a deliberately isolated host stays expressible), mobile through
  a REQUIRED `MobileWebViewInterceptor` constructor argument (the app calls that constructor directly, so
  only the compiler can stop the line being forgotten). It FREEZES on first application — a step declared
  after a window exists could not reach it, and serving some windows and not others is invisible from
  outside, so it throws instead. This replaced `interceptor.UseMediaPlayer(services)`, where the caller
  fetched an inner object and handed the provider back in; the two PHASES were always right, the receiver
  was not. The per-interceptor overloads remain for one webview that must differ.
  Serving files is `WebViewFileOptions` + `WebViewFiles.ResolveContained`/`Serve` +
  `interceptor.UseFiles(…)` — an extension over the interceptor so `RangeDelivery` is READ from the
  platform and cannot be passed in wrong. Fail-closed: no allowed roots means nothing is servable, `..`
  is refused before the filesystem is touched, roots are compared with a separator appended, and every
  refusal is the same 404 as a missing file so nothing probes for existence. `WebViewContentTypes`
  (public here since D45) answers the MIME type, and `DerivedCacheKey` keys anything derived from a
  source file by identity + length + mtime rather than by path.
  **This is what makes `<video>`, `<audio>` and `<img>` work with no media package at all** — a file the
  platform cannot decode simply errors in the element, and deciding what to do about that is
  `Shenora.Media`'s job as a further middleware.
- **`Shenora`'s mission-scheduling layer (0.3.0, `Missions/` + `Files/`)** — the EXECUTION half of
  long-running work, portable and with no DI, storage or reporting dependency of its own:
  `IMissionScheduler`/`MissionScheduler(+Options)` (`SubmitAsync`, `Lane(name)`, `GlobalLane`, `PendingCount`/
  `RunningCount`, `IsActive(MissionKey)`, `Snapshot()`, `Reevaluate()`, `RecoverAsync(rehydrate)`;
  `IAsyncDisposable` — dispose cancels what is queued and awaits what is running). **Admission** is
  event-driven, evaluated on submit and on each completion (no worker thread, no polling), and an item
  starts only when no in-flight AND no EARLIER-PENDING item holds a conflicting claim (rule 2 is
  fairness — it is what stops a queued item starving behind newer disjoint work) and every named lane
  has a permit; the lock covers bookkeeping only, never the body.
  **Claims** — mutual exclusion without the caller taking a lock: `MissionClaim` (`Scope`/`Key`/`Mode` +
  `Exclusive`/`Shared` factories), `ClaimMode`, and the `IClaimScope` seam supplying each key space's
  conflict rule — `FlatClaimScope` (equal only) and `NestedClaimScope` (equal or containment, tested at
  a SEPARATOR boundary so `a/b` contains `a/b/c` but not `a/bc`; `Normalize` collapses repeated
  separators and trims a trailing one, once, at submit). A request declares its whole claim SET, so
  there is no per-key lock object to leak and no acquisition ORDER to get wrong — the two bugs the
  family's hand-rolled versions had.
  **Lanes** — capacity, orthogonal to exclusion: `ILane` (`Capacity` settable LIVE, floor 1 and no
  ceiling; lowering swallows permits as items finish rather than killing in-flight work; `Hold`/`Release`
  re-entrant, the mechanism a load governor actuates with — the kit ships no probe, hysteresis or
  debounce policy), `MissionLane(Name, Permits = 1)` for a lane that is a BUDGET rather than a slot count.
  Every request also draws one permit from the GLOBAL lane (`GlobalLaneCapacity`, 0 = `clamp(cores-1,
  1, 4)`), which is the total concurrency bound — so a named lane runs at `min(its capacity, the bound)`,
  and `ILane.EffectiveCapacity` is what reports that (`Capacity` keeps the value you REQUESTED, and a
  request above the bound is logged rather than clamped or thrown). The bound is itself a lane —
  `IMissionScheduler.GlobalLane`, addressable as `MissionScheduler.GlobalLaneName` and resolving to the
  same instance from `Lane(name)` or from a mission declaring it — so it is live-resizable and holdable,
  which is what lets a load governor RESTORE and not only throttle (before it existed the bound was
  `init`-only and unreachable, so a throttled lane could never recover past its startup value).
  (`GlobalLaneCapacity` was renamed from `DefaultLaneCapacity`, a documented break with no alias kept.)
  **Definition vs execution — the split the rest of the layer is built on.** `MissionDefinition` is
  WHAT should run (`Run` + optional `Commit`: setting `Commit` makes `Run` run exactly ONCE and retries
  only the commit, so a failed cheap replace never recompresses; plus `Claims`, `Lanes`, `Priority`,
  `Key`, `Retry`, `Durable`, `Kind`, `Payload`). `MissionExecution` is ONE specific run of it
  (`MissionId`, `Kind`, `Priority`, `QueuedUtc`, `Sequence`, `Attempt`, `IsRunning`) — the single value
  handed to the body, to all three observer callbacks, to the policy, and back out of `Snapshot()`,
  where four differently-shaped types used to sit. It carries NO `CancellationToken`: the body takes
  its token as a second parameter (`Func<MissionExecution, CancellationToken, Task>`), matching the
  rest of the kit and keeping an execution a pure value that is safe to hold in a diagnostics view.
  Also `MissionKey` (dedup identity — a matching submission completes against the live item, body
  once), `RetryPolicy`(+`None`) (3 × 500 ms × attempt, `IOException` only), and `MissionResult`/
  `MissionOutcome` (`Completed`/`Failed`/`Cancelled`/`Deduplicated`; a failing body is REPORTED, not
  thrown — a batch submitter must survive one bad item — with `ThrowIfFailed()` for callers who prefer
  exceptions, while caller bugs still throw at submit).
  **The app's own scheduling rules** — `IMissionPolicy` (`Compare` = what next, `ShouldStart` = when) +
  `PriorityMissionPolicy` (priority, then FIFO — plain FIFO with no priorities set). Consulted ONLY about
  items that already passed admission, which is the structural reason a custom policy can delay work
  but never make conflicting work overlap or bypass a lane; a throwing policy is treated as "not now"
  rather than wedging the scheduler.
  **Observation + durability** — `IMissionObserver` (`OnQueued`/`OnStarted`/`OnFinished`, each guarded
  through `AppCallback`; the seam for metrics, tracing, or binding execution to a progress registry
  without `Core` learning what an operation is), `MissionSchedulerState` (what the scheduler is doing
  right now, for the policy);
  `IMissionQueueStore`/`MissionRecord`/`MissionState` + `RecoveryPolicy` (`Requeue`/`Fail`/`Discard`, defaulting to
  `Requeue` for `Queued` and **`Fail` for `Running`** — work found running after a crash may be what
  killed the process, and re-running it turns one crash into a boot loop) and `RecoveryPolicyFor`.
  Recovery is an explicit `RecoverAsync` with an app `rehydrate` delegate, never implicit: a delegate
  does not serialize, and that same delegate is why the kit ships no handler-registry-by-type.
  **Chains (multi-step missions)** — `MissionChain.Sequence(kind, params MissionStep[])` returns an
  ordinary `MissionDefinition`, so the scheduler gains NO concept of dependencies: a chain is ONE
  queue entry whose steps run in order, sharing an `IMissionChainContext`
  (`StepIndex`/`StepName`/`StepCount` + a `Get`/`Set` bag). `MissionStep` carries an optional
  per-step `Claims` and `RetryPolicy` — the claims are unioned onto the chain and held for its whole
  life (taking the STRONGER mode where steps disagree, so a read-then-write chain holds the key
  exclusively), and the retry repeats only that step, never the ones before it. A failing step fails
  the chain; cancelling cancels the chain. The context is IN-MEMORY only: a durable chain carries
  state in `Payload`, because an arbitrary object graph is what the kit cannot serialize for an app.
  **`Files/PathClaims`** (static) — `Scope` (a `NestedClaimScope` over `Path.DirectorySeparatorChar`,
  case-insensitive on Windows only), `Exclusive`/`Shared` (claims on the `"path"` scope, `ScopeName`),
  `Canonical` (absolute + separator-normalized, so two spellings of one location are one key) and
  `IsContained(root, candidate)` (the containment guard for anything mapping caller input to a file —
  resolves `..` first, boundary-tested, so `C:\data-old` is not inside `C:\data`).
  Naming is `Mission*` and deliberately not `Operation*`: `Shenora.Ipc` owns the reporting vocabulary,
  and reusing the word would blur the one distinction the design rests on. It was `Work*` until
  2026-08-02 — too common a word to own or grep, while `Task*` would collide with the BCL.
  **Three as-built facts worth recording, because a reader of the design doc or the XML would expect
  otherwise:** (1) an unknown LANE does NOT throw — it is created at the default capacity on first
  mention, so only an unregistered claim SCOPE is a submit-time error (the trap is a misspelled name
  silently costing the exclusivity that was configured; two XML remarks used to claim the lane threw,
  corrected and pinned by `An_unseen_LANE_name_is_created_at_the_default_capacity_rather_than_throwing`);
  (2) the design's `IFileSystem` and atomic-replace helper were never shipped — `PathClaims` is the
  whole of the scheduler's `Files/` half, and the write-to-temp-then-replace SHAPE is what `Run`/`Commit`
  models; (3) nothing
  nothing in the kit implements `IMissionObserver`, so wiring execution to reporting is the
  app's own ~35-line adapter — `samples/Shenora.Sample.Logic/MissionEventPublisher.cs` is the worked
  example — and `Shenora` stays free of any reporting dependency either way (D19/D20). ⚠ It publishes
  an EVENT STREAM rather than tracking requests (D66): a mission has no request behind it, so it has no
  request id, no response to await and nothing for the page to abort. That
  cancellation itself, because the registry's `Cancel` signals the OPERATION's own token while the
  work observes the one handed to `SubmitAsync`.
- **`Shenora`'s media layer — the translation layer for the web (`Shenora.Media` namespace, D52/D53),
  read as a PIPELINE.** The folders are the
  stages, and the surface is grouped the same way. The scope test the whole package answers to: *would a
  normal file the user already has fail to play, and is this the least we can do about it?*
  **`Probe/` — what is inside the file.** `MatroskaProbe.Read(path)` / `Read(stream, container)` walks an
  EBML header in managed code and returns a `MediaProbeResult`, or **null** for "I could not tell" — which
  is an ordinary answer, not a failure, and the planner already handles it. It reads the HEADER only, never
  a cluster, under an 8 MiB budget, because the file is one a PAGE can point at. Matroska CodecIDs are
  translated to the lowercase names every policy speaks (`V_MPEG4/ISO/AVC` → `h264`) so a policy needs one
  vocabulary, not two. `MediaProbeResult`/`MediaStreamInfo`/`MediaStreamKind` are the shape, and **every
  field but `Kind` is best-effort and may be null** — an app's own probe (ffprobe, a platform reader) fills
  the same record. ⚠ There is deliberately no `IMediaProbe` seam: `Plan` takes the record, so a probe is a
  helper an app may or may not call (D52).
  **`Plan/` — the minimum move, per STREAM.** `MediaPlaybackPlanner.Plan(probe, policy)` is a pure function
  → `MediaPlaybackPlan` (`MediaPlaybackAction` = `Direct`/`Remux`/`Transcode`/`Unsupported`, per-stream
  `MediaStreamPlan`, `ContainerOpens`, and a log-only `Reason`). The order is load-bearing: the CONTAINER is
  decided first and separately (an `.mkv` of ordinary AAC opens nowhere), an UNPROBED file gets the benefit
  of the doubt rather than a needless transcode, an unknown codec counts as decodable while the container
  opens, and subtitles never vote. `MediaPlaybackPolicy` (containers + `Codecs` and `Encodable`, both keyed
  by `MediaStreamKind` to match `IMediaCapability`) is the APP's — **the kit ships no default set**, because Android's
  codec support is vendor-declared per device and a baked-in list is one app's guess frozen into everyone's
  planner (D42).
  **`Serve/` — handing the result to the page.** Both are middleware over `Shenora`'s
  `IWebViewInterceptor`, returning an `IDisposable` registration, and neither implements interception
  itself (D45). `interceptor.UseMediaConversion(MediaConversionOptions)` converts a source to ONE finished
  file: `Resolve` maps a URL to a source, the app's `Convert` delegate does the work inside an
  `IMissionScheduler` mission (never on the request path), `PathClaims` makes a source convert once however
  many requests arrive, `Files.BeginReplace` means a failed or cancelled run can never leave a half-written
  file to be served as a cache hit, `DerivedCacheKey` invalidates on identity+length+mtime, and the page
  hears `MediaConversionEvents.SourceProgress`/`Ready`/`Failed` (`Failed` carries a TYPE name, never
  exception text). `interceptor.UseSegmentStream(SegmentStreamOptions)` is the other shape and the reason
  both exist: it publishes an HLS manifest computed from DURATION ALONE — so the scrub bar is the right
  length and a seek anywhere is expressible before a single segment exists — then keeps a rolling window of
  `seg{k}.ts` alive around the playhead. An hour-long source is an hour-long wait through conversion and a
  few seconds through this.
  **`Engine/` — the transform stage.** Two things: what the kit DOES, and the seam for what it does not.
  `Mp4Remuxer.Remux(source, destination)` → `Mp4RemuxerResult`/`Mp4RemuxerOutcome` rewrites Matroska as MP4
  with every frame copied untouched — tier 1 of D52's engine tiers, and the highest-value piece because it
  needs no codec at all: the picture is already H.264 or HEVC and the device already decodes both, so only
  the BOX is wrong. Carries H.264/HEVC video and AAC audio; anything MP4 cannot hold without re-encoding is
  reported (`NoCarriableStream`), never half-converted — though an unplayable soundtrack does not cost the
  picture. ⚠ Necessarily **TWO-PASS**: `moov` must precede `mdat` for seeking, and the sample table cannot
  be written until every frame's size and position are known. Two things it handles that a remuxer usually
  does not, each invisible until real content: **B-frames** (Matroska stores presentation time, MP4 stores
  decode time — identical without B-frames, which is why a remuxer can look correct and mangle most real
  H.264; `SampleTiming` derives the decode timeline, writes composition offsets and an edit list for the
  shift) and **lacing** (several AAC frames per block header — ignore it and most of the soundtrack silently
  disappears while every box validates).
  `ISegmentEngine` (+ `SegmentRunRequest`, `ISegmentRun`) is the seam the segment route produces through.
  🔴 Its most valuable member is `HasRenderedPicture`, and it is
  separate from `HasPicture` because **exit 0 is not evidence**: a hardware H.264 encoder advertised by both
  the tool's own list and the platform's codec list has been measured opening cleanly, taking every frame,
  writing `video:0KiB` and exiting 0. "Has a video stream" is the wrong test too — MPEG-TS names streams in
  the PMT, so a picture-less segment still declares one; what is missing is the SIZE. ⚠ `Dispose` on a run
  must KILL it: a rolling window that leaks a process leaks a CPU and a file handle, on a phone, invisibly.
- **`Shenora.IO` — the file-operation engine (the `Files/` folder inside `Shenora`; its OWN package
  only between 2026-08-05 and 2026-08-07, D48 → D55).** Portable `net10.0`, and it pairs with the scheduler above without
  depending on it: missions compute in parallel, this lands their results one at a time.
  **The file-update queue (2026-08-02), independent of the scheduler.**
  `IFileUpdateQueue`/`FileUpdateQueue(+Options)` serializes filesystem MUTATIONS so missions can
  compute in parallel and land one at a time: `ApplyAsync(FileUpdate)` completes when that update has
  landed. A `FileUpdate` is an ordered `FileChange` list (`Replace`, `Move`, `Delete`,
  `CreateDirectory` — a closed hierarchy), a `Partition` (null = one global writer; different
  partitions land concurrently), a `RetryPolicy` applied per change, and a `FileAtomicity`:
  `PerChange` stops at the first failure and reports its index, `AllOrNothing` undoes applied changes
  in REVERSE — which is why a delete under it is STAGED (moved aside, really removed only once the
  whole set lands), a delete being the one change that cannot be undone from nothing. Backups and
  aside-copies are siblings of the target, so every move is same-volume. `FileUpdateResult` reports
  rather than throws, like `MissionResult`. **Crash-atomicity is opt-in:** supply
  `FileUpdateQueueOptions.Journal` (`IFileUpdateJournal`, with the shipped `FileUpdateJournal` — one
  `WriteThrough` JSON file per in-flight update) and the undo plan is durable BEFORE each change, with
  `RecoverAsync()` resolving what a previous run left. `FileUpdateStage` decides which way: an update
  interrupted while `Applying` is rolled back, one interrupted while `Committing` is FINISHED, since
  rolling that back would undo a success. Undo is DATA (`FileUndoStep`/`FileUndoKind`) rather than
  closures — that is what a journal requires, and it is why each change is planned before it is
  applied. Without a journal the same `AllOrNothing` covers a failed change only. The internal `IFileOperations` seam exists so serialization and rollback
  ORDER are provable with a probe instead of with sleeps; the kit still ships no public filesystem
  abstraction.
  **Cross-process locking, the two halves of a problem claims cannot reach — and they live in
  DIFFERENT packages.** A `MissionClaim` excludes missions inside one process; these cover the rest.
  `IPathLocker`/`IPathLease`
  + `FilePathLocker(+Options)` are advisory leases as lock FILES in a directory of the app's own
  (never the managed tree — an app frequently does not own the folder it manages), opened
  `FileShare.Read` + `DeleteOnClose` so the OS releases them when a process dies, keyed by a hash of
  the canonical path so two spellings are one lease. `FileUpdateQueueOptions.Locker` makes the queue
  take them for every path an update touches, in sorted order so two overlapping updates cannot
  deadlock. That covers PARTICIPANTS — a second instance, or a child process the app spawns while
  holding leases. For a process that will never take one (a game, a mod loader, antivirus, another
  app editing the same folder), exclusion is impossible and the answer is `IFileLockInspector`:
  `WhoHolds(path)` → `FileLockHolder`s, surfaced on `FileUpdateResult.Holders`, so an opaque
  `IOException` becomes a name. Empty means "cannot tell", never "nobody". Over a share, leases work
  provided the lock directory is ON the share, and a crash-released lease returns when the SMB session
  times out rather than instantly.
  ⚠ **`IFileLockInspector` + `FileLockHolder` stayed in `Shenora`** when the rest moved out (D48),
  and the asymmetry is the layering rule rather than an oversight: "who holds this file open?" has a
  genuinely different answer per platform (Windows asks the Restart Manager), so it is a portable
  CONTRACT with a shell implementation — exactly like `IFileDialogs` — and a shell must be able to
  implement it without referencing an optional feature package. Advisory leases went the other way for
  the opposite reason: lock files are portable, so contract and implementation ship together.
  **`UpdateManifest`, `ManifestFile`, `ManifestDiff` (2026-08-02)** — the staged-update
  changeset, and the FIRST piece of the staged-update design (D57) to ship.
  `ManifestFile` is `{Path, Size, Sha256}` (the triple two sibling apps arrived at independently);
  `UpdateManifest` is `{Version, GeneratedAt?, Files}` with camelCase `Parse`/`ToJson` matching what
  they already emit; `ManifestDiff.Compute(installed, release)` yields `Added`/`Updated`/`Removed` +
  `DownloadBytes`. Pure data and a pure function — no downloader, no release source, no applier;
  those are the app's or the native step's. Two comparison rules are load-bearing and
  sabotage-verified: paths normalize separators and case (or the same file is "added" on every check
  and never converges) and hashes compare case-insensitively (or a generator's hex casing reports
  EVERY file changed). `Removed` is tracked paths only, never a directory sweep — user data lives in
  the same tree.
  ⚠ An empty RELEASE manifest legitimately removes everything, so a manifest that failed to load must
  never reach `Compute`; validating it is the caller's job — and that caller is `UpdateStage`, below.
  **`UpdateStage` (+`Options`, `+Status`)** — the staging half of the two-phase update. The app
  writes downloaded files into `StagedDirectory`; `CommitAsync` hashes every file the manifest lists,
  rejects anything staged that the manifest does NOT list, and writes `ready.json` **last**, so the
  marker's existence IS the promise that the stage is complete and verified and an applier need not
  re-check. Verification covers all three failure modes since 2026-08-03 — truncation (listed, missing),
  tamper (present, wrong hash) and **intrusion** (present, unlisted); the third was added because
  `ApplyAsync` overlays the staged TREE rather than the manifest, so an unlisted file was written into
  the install root having been verified by nothing. `UpdateStageOptions.IsUnindexed` exempts what a
  given release legitimately carries unindexed (a predicate, not a list — the answer belongs to whatever
  generated the manifest), and the kit's own `manifest.json` is always exempt because `FetchAsync` puts
  it there itself. `Begin()` clears a previous attempt (its
  leftovers would otherwise verify as part of the next one), `GetStatus()` reads only the marker and
  never throws, and an EMPTY manifest is refused here — the guard `ManifestDiff` defers. No
  downloader and no release source — those are the app's. `IUpdateSource` is the seam (two methods,
  no implementation shipped) and `FetchAsync` is the download-and-stage phase: diff, fetch only the
  CHANGED files, commit. Because only the changeset is staged, `CommitAsync` verifies the manifest of
  what is IN the stage; the full release manifest rides along as `manifest.json` so the applier can
  compute removals and so overlaying it makes the new installed baseline.
  **`UpdateStageOptions.BaselinePath` relocates that baseline** (2026-08-04, filed by the first adopter).
  Null = `{installRoot}/manifest.json`, unchanged for an install tree, where the baseline belongs with the
  thing it describes. It exists because that is only true of an install TREE: the adopter's targets are
  deploy INPUTS whose aggregate content hash decides what gets re-uploaded, so a per-release
  `manifest.json` inside one changed the hash on every release even when the payload was byte-identical —
  breaking their invariant that a part's content is a pure function of SOURCE, never of build HISTORY.
  `ApplyAsync` now writes the baseline EXPLICITLY and always excludes it from the overlay, so the
  configured and default cases are one code path rather than a containment test; it appears in
  `UpdateOutcome.Written` only when it really landed inside the tree.
  **`ApplyAsync` + `UpdateOutcome`** — the apply pass, portable .NET rather than native: overlay,
  remove what the new manifest dropped, clear. Run it from OUTSIDE the tree it overlays (a launcher
  at `{root}/` over `{root}/app/`), which is what makes self-exclusion guards unreachable rather than
  handled. An unreadable or empty staged manifest BLOCKS the apply, because removals are
  "installed minus release" and a manifest that failed to load would delete everything just written.
  A self-contained app needs no native code; a framework-dependent one still wants a launcher to
  bootstrap the runtime and call this.
- **`Shenora.IO.Compression` — archives, safely (2026-08-05).** `ZipExtraction.ExtractTo` +
  `ExtractionLimits`/`ExtractionResult`: every entry is containment-checked against the destination
  **plus a separator** (so `data-evil` is not a child of `data`), a refused entry is SKIPPED and NAMED
  rather than throwing, and total-bytes / entry-count limits (1 GiB / 100k) throw — the zip-bomb bound,
  fatal because a partial extraction that stopped quietly would leave the caller believing it had
  everything. `ZipUpdateSource` implements `Shenora.IO`'s `IUpdateSource` over one or MORE archives
  (a release is commonly one zip per part under a single manifest), indexed at construction, refusing a
  path carried by two archives rather than last-wins. ⚠ Non-seekable streams are refused up front
  (`ZipArchive` reads the central directory from the END), and it is not thread-safe because
  `ZipArchive` is not.
- `Shenora.Windows` — `DpiHelper` (BaseDpi, `SystemScale`, `ScaleFromDeviceDpi`, pure `Scale` +
  internal-element helpers); `WindowState`/`WindowStateOptions`/`IWindowStateStore`/
  `JsonFileWindowStateStore`/`WindowStateManager` (logical-px persistence, physical restore,
  off-screen recovery — pure `ToPhysical`/`ToLogical`/`IsVisible` cores); `SingleInstanceGuard`
  (per-scope FNV-1a mutex + activate broadcast, fail-open; `TryAcquire(TimeSpan)` = the
  `--restarted` widened-wait handoff with abandoned-mutex recovery); `WinFormsBootstrap(+Options)`
  + `UnhandledExceptionReport/Source` (one-call WinForms init + the three global exception
  channels with crash-log callback and last-resort dialog); the host composition —
  `UseWindows(WindowsHostOptions)` on `WindowsHostExtensions`, with `SingleInstanceHostOptions` (gate scope/restart
  argument/wait/losing-launch callback) and `WindowStateHostOptions` (store factory + options),
  backed by an internal runner (gate → bootstrap → starting hooks → form factory → window state →
  activate-message filter → loop → reverse-order stopping hooks → release); `SplashPanel(+Options)`
  (startup marquee overlay, app-chosen colors — headless per D13, debounced recenter);
  `OptimizedForm(+Options)` (double-buffered base + `WndProcHook` seam; optional frameless
  chrome: WM_NCCALCSIZE top-only caption removal, manual work-area maximize —
  `IsAppMaximized`/`MaximizedChanged` are the truth, not `WindowState` — DWM
  dark-mode/border/corner handling, top resize strip, `ApplyChromeTheme` runtime resync; all
  colors parameterized); native caption buttons (P5.6) — `NativeCaptionButtons` cuts the cluster
  reported to `SetCaptionButtons` out of the window region of every covering child so the OS routes
  real input to the form (Snap Layouts), and the form paints it with app-supplied
  `CaptionButtonColors`; `CaptionButtonStateChanged` remains for the un-clipped mode where the app
  draws them itself. **Frameless chrome is deliberately a FIXED TYPE rather than an attachable
  behaviour (D24)** — the window style belongs in `CreateParams` at handle creation, and attaching it
  later would need `SetWindowLong`+`SWP_FRAMECHANGED` as a second mechanism in the one area where a
  green unit suite has twice been wrong; the accepted limit (an app that cannot change its form base
  cannot take the chrome, though it can still drive the window commands) is recorded there. The
  drawing itself lives in the internal `CaptionButtonRenderer` (0.2.0 design pass): pure input →
  pixels — palette fallback, glyph selection, the DPI-scaled icon font — so it is unit-tested with no
  STA thread, no handle and no pump, while everything that answers a window message stayed in the
  form. The native services, TryAdd-registered by `UseWindows` —
  `IFormInteraction`/`FormInteraction` (main-window registry, runner-wired; nested modal
  blocking), `IFileDialogs`/`FileDialogs(+Options)` + `FileDialogOptions`/`Filter`/`Result` +
  `IFileDialogPathStore` seam (dedicated-STA open/folder/save dialogs, owner-handle z-order,
  per-key directory memory; failures throw). `IFileDialogs` carries two universal members with default
  implementations the desktop shell inherits unchanged — `OpenReadAsync` (the host resolves a picked
  handle) and, since 2026-08-03, `SaveAsync(options, write)` (the host picks AND writes, ATOMICALLY via
  `Files.BeginReplace`, so a failed or cancelled save leaves the previous file untouched). The
  path-returning `SaveFileAsync`/`OpenFolderAsync` are documented as the DESKTOP-flavoured pair (D35):
  they promise an addressable location, which mobile has no expression of.
  `IShellLauncher`/`ShellLauncher` (reveal/open-dir/
  http-https-`OpenUrl`/launch — Win11 handle-leak fixes), `IClipboardService`/`ClipboardService`
  (STA text + image-file ops); `SecondaryWindows(+SecondaryWindowOptions)` (named windows on
  own-STA-thread pumps, per-name `IWindowStateStore` geometry, activate-on-existing,
  non-blocking close); `TrayIcon(+Options)`/`TrayMenuColors` (NotifyIcon + composed menu,
  double-click restore, close-to-tray, optional app-colored renderer);
  `RestartManagerLockInspector` — the Windows implementation of `Shenora`'s
  `IFileLockInspector`, answering "who is holding this file?" through the Restart Manager API (the one
  an installer uses to say "close these applications"). Here rather than in `Core` because it is
  Win32; returns empty for a remote holder over a share, because that answer only exists on the server.
- `Shenora.Windows` — `BrowserArguments` (the measured Chromium display-optimization preset;
  single-occurrence feature lists; dev CDP-args append); `WebViewEnvironment(+Options)`
  (runtime presence probe, idempotent prewarm, thread-affine shared environment +
  per-STA-thread creation for secondary windows); `PrewarmWebView2` on `WebView2BuilderExtensions`
  (prewarm as a deferred starting hook — stays behind the single-instance gate);
  `WebViewHost(+Options)` (the ONE place a WebView2 is configured: env + ensure under a 25 s
  init-timeout guard, settings-hardening preset + `ConfigureSettings` escape hatch, dev/prod
  `Navigate` with actionable errors, sync virtual-host serving of the packaged bundle vs
  deferred off-UI-thread app schemes (`WebViewDeferredScheme` — a full request/response seam:
  `WebViewResourceRequest` (uri/method/headers) in, `WebViewResourceResponse` (status/headers/
  content STREAM) out, with `WebViewByteRange.TryParse` + `PartialContent`/`RangeNotSatisfiable`
  so a served resource can be SOUGHT and a large one is never buffered whole), disk-folder hosts
  (`WebViewFolderMapping`), escaped `InjectedGlobals` + family scripts, and the four default
  event policies: new-window→system browser, downloads canceled, permissions denied except
  allowlist, guarded renderer-crash reload); `IWebViewResourceProvider` seam +
  `EmbeddedResourceProvider(+Options)` (assembly+prefix, lazy-with-warmup, file-fallback mode,
  path→name lookups) — the no-cache-HTML / immutable-hashed-asset header policy lives in
  `Shenora`'s `WebViewContentTypes` (public since D45, because every shell's interceptor needs a
  MIME map) and is applied by `WebViewHost` when it serves; **`WebViewHost.Interceptor`** — the desktop
  half of the D45 contract (`WebView2Interceptor`, `RangeDelivery = Sliced`, measured by the sample's
  `InterceptorProbe`), wired into the host's ONE `WebResourceRequested` subscription rather than a second
  one, sharing the page's own origin with the bundle: a path the bundle does not contain falls through to
  the pipeline (`WebViewBundleServing.TryServe`) instead of 404ing, and in DEV an extra filter is
  registered for the dev-server origin because that is where the page lives then; `WebViewIpcBridge(+Options)`
  (the postMessage transport: UI-thread async-interleaved request dispatch into an
  `IMessageDispatcher`, `IsHandleCreated`-guarded `BeginInvoke` posts, bounded drop-oldest
  notification queue buffering from construction + ~50 ms batch flush after the reserved
  `SHENORA`/`READY` client handshake, optional `IEventBus` wildcard forwarding,
  `SendNotification`, `OnClientReady` per-handshake callback); `WindowCommandModule` + `WindowCommandOptions`
  (module `SHENORA.WINDOW`: MINIMIZE/TOGGLE_MAXIMIZE/CLOSE/IS_MAXIMIZED/START_DRAG/START_RESIZE +
  optional SET_THEME; `ToggleMaximize`/`IsMaximized` delegate seams for frameless apps — here
  because the commands arrive over the bridge and need Ipc, which WinForms doesn't reference);
  the drop-zone stack — `DropZoneManager(+Options)` (transparent overlays over page elements
  capture real OS paths incl. background drags; non-blocking UI marshalling, activation sync,
  DOM occlusion checks, per-monitor `DeviceDpi` conversion + `DpiChanged` re-apply; zones cleared on
  `ContentLoading` so overlay lifetime follows the DOCUMENT, never the ready handshake, which used to
  race the page that was registering; events on `IEventBus`) + `DropZoneModule` (module `SHENORA.DROPZONE`:
  REGISTER/UPDATE/UNREGISTER/SHOW).
- `Shenora` also owns `AppCallback` — the ONE guard for invoking app-supplied code from a place
  where an escaping exception is fatal rather than catchable (a UI-thread event handler, a timer tick, a
  posted body, a dispose path). Public because `Shenora.Windows` and the two mobile shells (via the
  shared `Shenora.Mobile` source) all consume it and a `ProjectReference` grants no `internal` access
  (D19/D20). ⚠ This sentence named `Shenora.Windows` THREE TIMES until 2026-08-07 — the D37 merge
  rewrote all three old Windows ids to the new one and nobody read the result. `doc-drift` is blind to
  it by construction (the retired names are gone, so nothing is left to match), which is why the rule
  after a rename sweep is to READ THE DIFF. Four instances of this shape have now been found: this one,
  `AppCallback`'s XML, `WindowCommandModule`'s, and `REVIEW-GUIDE.md`'s own (fixed 2026-08-05).
- `Shenora.Windows` — auxiliary browser sessions (D14: browser work outside the
  app's own UI, kept out of the core hosting package): `SessionBrowser(+Options)` (the ONE
  auxiliary-WebView2 configuration path — per-profile environment, quiet-start +
  background-throttling-off arguments, settings hardening, `RequestFilter` block seam,
  init-timeout guard, `GetHtmlAsync`, and — since 2026-08-03 — `VirtualHost` + `ResourceProvider` +
  `FolderMappings`, so a session can serve the app's OWN packaged bundle and "co-browse / off-screen
  render MY UI" works in a packaged build (E1/D38; before it, a session reached network-reachable URLs
  only). The two halves are both-or-neither, refused at initialization; the app's `RequestFilter` is
  consulted BEFORE the bundle, from ONE `WebResourceRequested` handler; the serving itself is
  `WebViewBundleServing`, the same internal implementation `WebViewHost` uses);
  `RenderSessionPool(+Options)`/`RenderSession`/
  `SessionApiCall` (bounded LIFO-pooled off-screen sessions: lease → navigate (http/https-only
  + `NavigationGuard` SSRF seam + `NavigationTimeout`)/execute/read/DevTools/network+message taps →
  dispose returns to the pool; capacity waits queue, a creation failure or a cancelled-during-init
  creation releases the slot and tears down, every operation is capped by `OpTimeout` and an
  abandoned one POISONS its instance, a poisoned instance or a `ResetTimeout`-expired about:blank
  reset DISCARDS it rather than re-pooling; one shared hidden host in runtime mode,
  visible cascaded windows in dev mode; internal `SessionEnvironmentCache` gives the pool ONE
  `CoreWebView2Environment` for its profile — owner-scoped, not static, because a live environment
  holds the profile's folder lock and would defeat `ClearProfile`); internal `SessionLog` (the
  package's one guarded-diagnostic path — an app `ILogger` is an app callback); the human-in-the-loop
  stack — `InteractiveSession(+Options)` (a modal, driver-run browser window over
  per-provider/per-sub-account persistent profiles — the sub scoping is a security boundary;
  busy-serialized with exactly-once completion incl. the token fallback, the user's close HELD so the
  driver gets a final read, silent-refresh off-screen shape, static `ClearProfile` so discarding a
  session is real), `SessionController` (guarded `NavigateAsync`,
  `ExecuteScriptAsync`, origin-scoped `GetCookiesAsync`, `OnMessage`/`OnDownload`/
  `OnNewWindow`/`OnNavigation` taps, `FitToBox` CSS→physical, `SetLoading`, idempotent
  `Reveal`, `WindowClosed`), `SessionResult` (+ `ThrowIfFailed` bridging into the IPC error
  contract)/`SessionErrorCodes`, and `CookieLoginFlow(+Options)`/
  `SessionCookie`/`DownloadHit` (the one opt-in REFERENCE DRIVER, which keeps its scenario name on
  purpose — D22: fresh-set auth-cookie detection against a
  pre-navigation baseline, so a stale cookie never captures, not even on close; separate
  `CookieReadUrl` origin; `ReadBlob`); and `StreamingSession(+Options)`/`SessionViewport`
  (an off-screen browser that STREAMS what it renders and ACCEPTS synthetic input: screencast JPEGs
  into a bounded latest-wins `ChannelReader<SessionFrame>` — each frame carrying the CSS viewport it
  depicts — `DispatchAsync(SessionInput, …)` for typed input (`SessionPointerInput`/`SessionWheelInput`/
  `SessionTextInput`/`SessionKeyInput`/`SessionViewportInput` + `SessionPointerAction`, plus
  `SessionInput.TryParseLegacyJson` as the adoption shim), 1:1 device-metrics viewport mirroring,
  fraction coordinates, and `OnEnded`/`SessionEnded`/`SessionEndReason` as the exactly-once lifecycle
  hook. The LIFECYCLE is the contract — started / navigated / frames / ended-or-faulted; the transport,
  viewer UI, hover affordances and what any of it is FOR belong to the app (D21/D22), which is what the
  sample's `STREAM` route + `StreamViewer` demonstrate).
- `Shenora.Ipc` — the transport-neutral wire contract (design §5, D11/D16; names pinned with
  `JsonPropertyName` so envelopes hold under any serializer options): `IpcRequest`
  (`{id, module, type, scope?, payload?, timestamp}` — `scope` is the app-defined routing
  field), `IpcResponse` (`{category:"ipc", id, success, data?, error?}` + `CreateSuccess`/
  `CreateError`), `IpcError` (`{code, message?, parameters?}` — code is the client-side i18n
  key), `IpcNotification`/`IpcNotificationBatch` (`{category:"notification", id, payload:[…],
  timestamp}` — always-batched host→client push; the same envelope any transport carries),
  `IpcCategories`, `OperationException` (the one exception whose details cross the bridge;
  `ToError()`), `IpcErrorCodes` (framework-reserved codes), `PayloadHelper`
  (`GetRequiredValue`/`GetOptionalValue` with structured errors; JSON null == absent), `IpcJson`
  (frozen camelCase/camelCase-enum/null-omitting wire serializer defaults, plus
  `AddTypeInfoResolver` — a startup-only seam for an app's source-generated `JsonSerializerContext`,
  chained AHEAD of the reflection fallback so an AOT/trimmed host can supply the metadata reflection
  cannot; it adds metadata rather than reopening the one frozen instance, and registering after
  `Options` is built throws); `IpcHostBridge`/`IpcHostBridgeOptions` (the transport-neutral INBOUND
  half — parse → handshake-or-dispatch → response JSON, the dispatch lifetime token and the
  no-raw-exception-text boundary; owns no transport and no timer, the mirror of `NotificationPump`
  on the other direction, and the host-side mirror of the client's `ShenoraBridge`. Takes the pump
  optionally so the handshake opens the outbound gate in one place; CLOSING it stays the base's
  call. `HandshakeModule`/`HandshakeType` live here — `WebViewIpcBridge` forwards the consts);
  `ShellInfo` (`Name` + `Capabilities`, returned as the handshake's response data via
  `IpcHostBridgeOptions.Shell` — how one web bundle targets every shell: the page renders on the
  advertised capabilities instead of sniffing the platform, since what a host offers depends on what
  the app composed. Optional; null says nothing, which the client reads as "assume nothing");
  the dispatch
  pipeline — `IMessageDispatcher`/`MessageDispatcher` (`Use`/`UseModule`/`UseRoute`/`UseLogging`/
  `UseErrorHandler` + `MapRoute`/`MapModule(name, routes)`/`MapModule(facade)`; `DispatchAsync`
  transport entry: never throws, never null — `NO_HANDLER`/structured/`UNKNOWN_ERROR` mapping
  with details kept host-side; programmatic `SendAsync`/`SendAsync<T>` over the same pipeline,
  typed failures rethrow `OperationException`), `MessageMiddleware` delegate,
  `ModuleRouteBuilder`, `IIpcModule` (carries `ModuleName` — facade objects route via DI +
  `MapModule`, no static registry) / `ModuleBase` (standardized error boundary) /
  `IpcErrorMapping` (that boundary as public surface: `ToError`/`ToErrorResponse`, for an app whose
  failures travel as events and so has no response to attach one to); a `CancellationToken` flows
  the whole pipeline — the CALLER's lifetime, supplied by the transport and cancelled on its dispose,
  not a per-request client cancel;
  `ScopedContainerRouter(+Options)` (per-scope child containers: app `ConfigureScope` +
  `OnScopeCreated`, single-flight creation, `MapModule<TFacade>` declarations, structured
  `SCOPE_REQUIRED`, `GetScopeServices`/`InvalidateScope`/`ActiveScopes`) + `UseScopedRouter`
  (on `ScopedContainerRouterExtensions`); composition helpers
  `UseIpcModule<TFacade>`/`MapRegisteredModules`/`UseMessageDispatcher` on
  `IpcServiceCollectionExtensions` (error handler → app middleware → DI-registered facades, mapped
  LAZILY so the singleton is cached before the provider is enumerated); and
  `MessageDispatcherExtensions`, which carries the composition helpers as extensions over the
  interface's ONE `Use(MessageMiddleware)` primitive — so they work on any `IMessageDispatcher`,
  including a decorator, without the downcast the reference composition used to need (H6);
  and `IModuleRegistry` (`MappedModules`/`IsModuleMapped`/`TryClaimModule`/`TryReleaseModule` — claim, ask, release; implemented by
  `MessageDispatcher`) + `TryMapModule` — the seam for a DYNAMICALLY composed surface (plug-ins,
  licence-gated or per-tenant modules), kept OFF `IMessageDispatcher` so that interface stays the
  four things a dispatcher IS. `MapModule(facade)` throws on a duplicate; `TryMapModule` returns
  false instead, and throws rather than answering when the dispatcher cannot know. (The line that
  used to sit here — "known limit: a mapped module cannot be released, the pipeline only grows" —
  was stale from the release that added `TryReleaseModule`, and contradicted this same sentence's
  own member list.)
  **Known limit, recorded rather than solved: the registry does not see DI-registered facades.**
  `UseMessageDispatcher` maps them through `MapRegisteredModulesLazily` — ONE terminal middleware
  resolving them on first dispatch — not through `TryClaimModule`, because claiming needs the module
  NAMES and reading those means resolving the facades, which inside the `IMessageDispatcher` singleton
  factory is the silent `StackOverflow` P5.5 H2 fixed. Two consequences: `IsModuleMapped("OPERATIONS")`
  is `false` while `SHENORA.OPERATIONS` is routed, and a plug-in offering a name a DI facade already owns gets
  `true` from `TryMapModule` and then never runs, because the lazy middleware is composed earlier and
  answers first. Precedence is the one you want (the app's own modules win); the honesty is not.
  Closing it needs either a name-reservation seam the registry does not have or re-opening the
  deadlock — so until a consumer actually hits it, map anything that must be checkable through
  `MapModule(facade)`/`TryMapModule` explicitly rather than through DI registration.
  **The module contract's event half (D23, merged into the request in D66):** `IModuleContext`
  (`Module`, `RequestId`, `Logger`, `Publish(type, payload?, scope?)`, `Report(IpcProgress?, IpcLabel?)`)
  is the second parameter of `ModuleBase.RouteMessageAsync`, and is built PER REQUEST — which is what
  lets `Report` take no id. `Publish` throws a loud, self-naming `InvalidOperationException` when no
  `IEventBus` was supplied rather than silently no-op-ing; `Report` is a silent no-op without a tracker,
  because publishing is the route's own output (losing it loses app data) while progress is the kit's
  bookkeeping about a request it is tracking.
  **Request tracking** (`Core/Ipc/Requests/`): `IpcRequestState`
  (`Running`/`Completed`/`Failed`/`Cancelled` — a request is IN FLIGHT or DONE, crossing the wire
  camelCase for free via `IpcJson`'s enum converter), `IpcLabel` (`{Text?, Key?, Parameters?}`, the same
  i18n shape as `IpcError`), `IpcProgress` (`{Value, Total?, Unit?}` — the app's own unit, e.g. bytes or
  items of a known total, an absolute count with no known total (`Total = null`), or a genuine percent;
  `Unit` is app-defined and uninterpreted), and `IpcRequestStatus` (the full snapshot — both the
  `REQUEST_UPDATED` payload and the `LIST` response element; one type for every transition, so a client
  folds by `Id` with no cross-type ordering hazard).
  🔴 **There is NO separate operation entity, and that is D66.** `IpcRequestStatus.Id` IS
  `IpcRequest.Id`; `Type` is the request's own action rather than a declared "kind"; `StartedAt` is its
  `Timestamp`. The predecessor minted a fresh `Guid` unrelated to the request that caused it and carried
  a parallel `Kind`/`Scope`/`StartedAt`, leaving the page to correlate two identities for one thing.
  `IIpcRequestTracker`/`IpcRequestTracker(+Options)` holds one lock over in-memory state: `Begin`
  (returning an `IIpcRequestScope` whose disposal completes the request), `GetAll` (in flight
  oldest-first, then finished newest-first, filtered by module/scope with `IEventBus`'s scope rule),
  `Cancel(requestId)` (`XMLHttpRequest.abort()` — signals the token first, then records `Cancelled`)
  and `ClearFinished`.
  🔴 **`Begin` is called by `MessageDispatcher.DispatchAsync`, and the dispatch boundary is the only
  honest place for it** (2026-08-08). Every request passes there however its module was written — a
  `ModuleBase`, a bare `IIpcModule`, an ad-hoc `MapRoute` lambda — and the OUTCOME is known there and
  nowhere else, since one `IpcResponse` carries success, an app's structured failure, a cancellation
  and `NO_HANDLER` alike. The dispatcher also hands the scope's token down the pipeline, which is what
  makes `Cancel` reach the token a route is actually observing. ⚠ It started in `ModuleBase` until
  then, from a tracker each facade had to inject and forward — which no kit module did, so `Begin` was
  never called in a composed app and `Failed` was unreachable (that catch returned an error response
  while the scope disposed as `Completed`). A route reaches its scope through `IModuleContext.Report`,
  which resolves it from the ambient scope by matching the request id.
  🔴 **The GRACE PERIOD is what makes tracking everything affordable, and it replaced the declaration.**
  `Begin` publishes NOTHING; a request is announced only if it outlives
  `IpcRequestTrackerOptions.GracePeriod` (50 ms — `NotificationPumpOptions.FlushInterval`'s own default,
  and roughly the threshold below which a human does not want a spinner). One that finishes inside the
  window emits no event, retains no history and never reaches the wire, so the fast path costs one
  dictionary insert and one removal. There is nothing for a module author to declare, because whether a
  route is "long-running" is something only the clock knows at run time. ⚠ It suppresses NOTIFICATIONS
  only — the response is never delayed. `ProgressInterval` then throttles progress per request once
  announced; terminal transitions are never throttled. `MaxHistory` bounds retained history, each
  eviction announced through `REQUEST_REMOVED` so a long-lived client store mirrors a bounded list.

  **`NotificationPump`(+`Options`)** — the transport-neutral half of the outbound notification
  channel (design §5, D16 applied to the host side): bus subscription (from CONSTRUCTION, not
  `Open`), the per-channel `Filter` (applied at enqueue, fail-CLOSED on a throwing predicate — the
  filter exists so a channel gets only its own slice of traffic, and delivering a notification the
  app meant to keep off this channel is the more dangerous failure), the bounded drop-oldest queue,
  the ready gate (`Open`/`Close`), batch building, and the guarded per-notification serialize (one
  bad payload must not sink its batch). **It also COALESCES a batch** (2026-08-08): a notification
  carrying an `IpcNotification.CoalesceKey` supersedes an earlier undelivered one with the same
  module/type/scope/key, last-write-wins, the survivor keeping its own later position. The key comes
  from `EventMessage.CoalesceKey` and is host-side only (`[JsonIgnore]`, absent from the TS mirror) —
  by the time a batch leaves, the coalescing has happened. ⚠ Strictly opt-in: the pump cannot tell a
  snapshot from a delta, so only the emitter may declare it; the kit sets it on `REQUEST_UPDATED` and
  deliberately not on `REQUEST_REMOVED`, whose payload is a batch of different ids. Owns NO timer and
  NO transport — `TryDrainBatch` is called by
  whatever the base drives its own tick with (a `Forms.Timer` on WinForms; a `PeriodicTimer` on a
  headless base), because which thread may touch a base's client is a base-specific fact.
  `WebViewIpcBridge` is now a thin adapter over it: it keeps only what is WinForms/WebView2 — the
  timer, `WebMessageReceived`, `ContentLoading`→`Close()`, `READY`→`Open()`,
  `ProcessFailed`→`Close()`, and `PostWebMessageAsString` — while `WebViewIpcBridgeOptions` keeps its
  existing option names (`NotificationInterval`, `MaxQueuedNotifications`, forwarded to the pump's
  `FlushInterval`/`MaxQueued`) and gains `NotificationFilter`.
- `@shenora/react` — the client side of the contract: wire types mirroring `Shenora.Ipc`
  name-for-name (+ `IpcCategories`/`IpcErrorCodes`/handshake constants), `OperationError`
  (structured code + parameters; client-side `TIMEOUT`/`NO_TRANSPORT` reject the same way),
  `ShenoraTransport` seam + `createWebView2Transport` (D16 pluggability) +
  `isShenoraAvailable`, `ShenoraBridge` (correlated `invoke` + timeout, one-way `post` +
  `onPostError` — no pending entry, no deadline, and a failed response reported rather than dropped;
  category routing, batch unbundling, `notifyReady` handshake — which RESOLVES to the host's
  `ShellInfo | undefined` and caches it on `bridge.shell`, the client half of "one bundle, every
  shell"; `ShellInfo`/`ShellCapabilities` mirror the host names — `fallback` seam for pure-UI browser
  dev; lazy default via `getBridge`/`configureBridge`), `ShenoraEventBus`/`eventBus` (three
  subscription breadths mirroring the host's `IEventBus` — exact `(module, type)`,
  `subscribeToModule`, `subscribeToAll` — delivered narrowest-first),
  `createShenoraStore` (a store fed by one module's event stream: ONE subscription however many
  components read it, `snapshot` on the first subscriber so a late mounter is not empty, built on
  React's `useSyncExternalStore` so the package imposes no state library),
  `BaseModuleService<TRequests>`, hooks (`useShenora`/`useShenoraEvent`/`useShenoraQuery`),
  `WindowCommands` typed service + `useWindowMaximized` (resize-triggered resync), `useDropZone`
  (native drop zones synced to elements — real OS paths, unstyled drag feedback),
  `installDevInterceptor` (`window.__shenora` CDP-testing global); **`useShenoraRequests`/
  `createRequestsStore`** — mirrors the host's request tracking: `IpcRequestStates` (the wire values),
  `IpcRequestEventTypes` + `IpcRequestsModuleName`, the
  `IpcRequestStatus`/`IpcLabel`/`IpcProgress` types, and a `createShenoraStore` instance
  (`snapshot: LIST`, `on: { REQUEST_UPDATED: fold-by-id, REQUEST_REMOVED: delete-named-ids }`,
  `actions: { cancel, clearFinished }`) with `running`/`finished` DERIVED getters computed from `byId`
  on every read — never a second copy a reducer has to keep in sync.
  **TWO bands, and `cancel` takes the id you already have.** The store is a list of work that is
  actually taking a while rather than a log of every call the page made, because the host stays silent
  for the first 50 ms. `clearFinished` does not touch local state: the host's
  `REQUEST_REMOVED { requestIds }` is the ONE authoritative removal signal, folded by deleting exactly
  the named ids — history eviction and `clearFinished` both publish it, so the client cannot drift from
  what the host actually did.
  `createRequestsStore(options)` takes an optional renamed module (for an app that changed
  `IpcRequestTrackerOptions.ModuleName` to avoid a collision) and an optional `scope`, threaded into the
  snapshot payload, the bus subscription AND the action envelopes so a scoped store stays internally
  consistent; `useShenoraRequests` is the ready-made default instance. Known limit, deliberate: no
  `byModule`/`byType` selector — filtering is a one-line consumer selector over `byId`
  (`Object.values(state.byId).filter(r => r.module === 'X')`), and shipping indexes for it would be
  duplicated derived state for no gain. react ≥18 required peer.


## Dependency rules (enforced by review)

- `Core` depends only on Microsoft.Extensions DI (implementation — the builder needs
  `BuildServiceProvider`, D17) + logging abstractions. Everything else depends downward on `Core`.
- **Execution and reporting compose; they do not merge.** `Core`'s `Work/` layer must never learn what
  an operation is — a mission body reports into `Shenora.Ipc`'s operation registry, and the seam pointing
  that way is `IMissionObserver`. `Shenora.Ipc` may depend on `Shenora`, never the reverse (D19/D20),
  which is also why the scheduler ships no storage dependency: `IMissionQueueStore` is a seam, not an
  implementation.
- **Windows is ONE package, and D19's edge is now internal (2026-08-02).** D19 established that the
  Windows primitives and the web hosting on top of them are one layer rather than two peers — decided
  on evidence, after the UI-thread marshal pattern had been hand-rolled 14 times with five
  incompatible pre-handle policies, two of them buggy. Three packages then expressed that one layer,
  and the split's remaining justification was a WinForms-without-WebView2 consumer: a tray or
  single-instance utility with no web frontend. **That consumer cannot exist in this kit** — the
  premise is React in a webview — so the boundary described an adoption STAGE, not a shipping
  configuration, and the three merged. `Sessions` folded in for free: it added no dependency of its
  own, already sharing `Microsoft.Web.WebView2`.
  The internal direction still matters and the folders carry it: `Shell/` must never depend on
  `WebView/`, which would be the cycle D19 forbade.
- **Portable contracts live in `Shenora` (D20):** `IUiDispatcher`/`UiTargetState`,
  `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`Filter`/`Result`, `IClipboardService`,
  and the portable bases `IUrlLauncher`/`IUiInteraction`, plus `ShellCapability` — the shared
  capability vocabulary (`windowChrome`, `dropZones`, `filePicker`, `folderPicker`, `savePicker`,
  `secondaryWindows`, `tray`) and the `NotSupported` factory a shell throws from when it lacks one
  (D33). The names are what a host advertises through `ShellInfo` and what a page branches on.
  Their Windows implementations stay in
  `Shenora.Windows`, which registers BOTH faces of each split service so app logic can depend on the
  neutral contract and compile with no Windows reference. The bar for moving a contract to `Core` is
  "app logic must compile off Windows", NOT "the signature happens to be platform-neutral" — which is
  why the window-state stack deliberately stays in `Shenora.Windows`. `Shenora.Windows` layers
  on `Shenora.Windows` (the one deliberate package-on-package edge above `Core` — D14 keeps
  the session stack out of the core hosting package).
- `src/*` never references `tests/`, `samples/`, or anything app-specific.
- No Lyntai reference, ever (docs/DECISIONS.md D1).
