# ARCHITECTURE.md — the as-built map

Keep in sync with reality: when a project, public type family, or dependency edge changes, update
this file in the same phase. (Design intent lives in `docs/DECISIONS.md`; this file records only what
EXISTS. There are no dated design docs any more — D57.)

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`, the same way README.md's
     headline is. Don't hand-edit it — and don't date this line either: the release workflow owns
     the version, so a hand-written one is stale the moment a release cuts. Everything ELSE in this
     file dates its claims instead of versioning them, for the same reason. -->
## Current state — **v0.16.0 published**

⚠ **That heading's WORDING is load-bearing** — `dev.mjs doctor` matches
`## Current state — **vX.Y.Z published**` to keep the version in step with `VersionPrefix`, and syncs it
on `--fix`. Never hand-edit the number; never reword the line.

There are five packable projects + two npm packages. **Four are the framework and its shells, organised
BY PLATFORM (D37)**: `Shenora` and one shell each for `Windows`, `Android` and `iOS`, all shipped from ONE
Windows runner. The fifth is the native `Launcher` (D50); the npm pair is `@shenora/react` and the
build-time `@shenora/cli` (D67).

⚠ **There is NO optional-feature tier**: media, files and compression are FOLDERS inside `Shenora`
(D53, D55) — a capability gets a namespace, never a package id, because a nuget.org listing of
single-domain libraries makes a claim about the product nobody meant. **The authoritative set is the
header table of `docs/DECISIONS.md`, once.**

**How this file marks time: with DATES, never version numbers.** A version is assigned by the release
workflow, so one written into prose is a guess about the future — and there is no 0.2.0 to prove it.
The release narrative is `CHANGELOG.md`; how the shape was arrived at is `git log` and `docs/DECISIONS.md`.

```
Shenora.slnx
├── src/
│   ├── Shenora        net10.0          — deps: M.E.DependencyInjection (impl, D17), M.E.Logging.Abstractions
│   │                                        Modules/Media/ (namespace Shenora.Modules.Media; a PACKAGE until D53,
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
│   │                                            Deliver/ handing the result to the page — THREE routes:
│   │                                                    UseComputedRemux (an MP4 answered over ranges
│   │                                                    that was never produced), UseMediaConversion
│   │                                                    (one finished file) and UseSegmentStream (an
│   │                                                    HLS window, playable seconds in). All three
│   │                                                    are MIDDLEWARE on the shell's
│   │                                                    IWebViewInterceptor, and REGISTRATION ORDER
│   │                                                    IS THE ROUTING: computed remux first, since a
│   │                                                    source it declines must reach the next one.
│   │                                            Engine/ the transform stage — Mp4Remuxer (Matroska → MP4,
│   │                                                    every frame copied untouched, no codec at all;
│   │                                                    Plan() states the output's length and every
│   │                                                    byte's provenance BEFORE writing it) and
│   │                                                    DefaultSegmentEngine (fMP4 fragments, mobile
│   │                                                    only), behind the ISegmentEngine seam an app
│   │                                                    replaces to go past the platform's reach.
│   │                                                    BOTH copy what MP4 can carry and re-encode only
│   │                                                    what it cannot — one predicate, Mp4Carriage,
│   │                                                    because a second spelling of it is how a plan
│   │                                                    and a write disagree about one file (D76). A
│   │                                                    copied track lands on the SOURCE's keyframes, so
│   │                                                    the cuts are a SegmentPlan the manifest and the
│   │                                                    run share rather than a fixed grid.
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
│   │                                          implements it (D45); Deliver/ COMPOSES on that contract.
│   │                                          <video>/<audio>/<img> over ordinary local files need none
│   │                                          of this — it is what answers a file the platform CANNOT
│   │                                          decode.
│   │                                          ⚠ In Core because that is where the thing it is "the same
│   │                                          category as" already lives (D53): serving a local file.
│   │                                          A package of its own rested on a premise D51 made
│   │                                          permanently false — that a demuxer means real shipped
│   │                                          BYTES. No engine byte ever ships, so what remained was
│   │                                          98 KB of the kit's own managed IL. Ships no codec LIST:
│   │                                          the mechanism is the kit's, the policy the app's (D42).
│   │                                    Engine/Files/ namespace Shenora.Engine.Files — file operations:
│   │                                            Files/FileReplacement (atomic replace, the default
│   │                                                    behind IFileDialogs.SaveAsync)
│   │                                            PathClaims — a claim SCOPE over the mission types;
│   │                                                    scheduling vocabulary that is about paths
│   │                                            FileUpdateJournal/Queue — the journal is written BEFORE
│   │                                                    the mutation, so undo is DATA and recovery can
│   │                                                    roll back Applying and FINISH Committing
│   │                                            PathLocks — cross-process advisory leases, taken after
│   │                                                    the in-process gate, in sorted path order
│   │                                    Engine/Update/ namespace Shenora.Engine.Update — the release
│   │                                            PROTOCOL, beside Files/ rather than under Modules/:
│   │                                            it carries no capability to the PAGE, which is what
│   │                                            Modules/ means (D65), and needs no platform half —
│   │                                            ApplyAsync is portable by design:
│   │                                            UpdateManifest/UpdateStage — the staged self-updater
│   │                                                    with per-file verification
│   │                                            ZipUpdateSource — the one IUpdateSource the kit ships
│   │                                    Engine/Compression/ namespace Shenora.Engine.Compression —
│   │                                            getting bytes out of an archive onto disk, SAFELY;
│   │                                            not update-specific, which is why it is its own area:
│   │                                            ZipExtraction — containment-checked (any entry
│   │                                                    escaping its destination is refused) with
│   │                                                    size/count limits. No native engine: zip works
│   │                                                    on the framework alone, any other format is a
│   │                                                    SEAM (D42)
│   │                                            ResourcePack — a named, VERSIONED set of files on disk,
│   │                                                    marker-written-last like UpdateStage
│   │                                          ⚠ D48 made these two packages and D55 folded them back in.
│   │                                          The layering D48 established is INTACT and still visible
│   │                                          here — the edge runs Files/ → Core because every type logs
│   │                                          through AppCallback, which is also why merging them INTO
│   │                                          Core was the only mechanism available: a Core that packed
│   │                                          Shenora.IO.dll would have to reference it, and it already
│   │                                          references Core. What changed is the package COUNT, not
│   │                                          the structure.
│   │                                    Modules/Platform/ namespace Shenora.Modules.Platform — the
│   │                                            contracts a SHELL implements and app logic calls:
│   │                                            ILiveActivities + LiveActivityState — a long-running job
│   │                                                    on the OS-rendered strip (iOS Dynamic Island and
│   │                                                    lock screen). Android answers with a REASON.
│   │                                            Activities/ namespace Shenora.Modules.Platform.Activities
│   │                                                    what the kit's generic widget DRAWS, described
│   │                                                    in C# and interpreted by SwiftUI at runtime
│   │                                                    (D69), so an adopting app writes NO Swift.
│   │                                                    — Presentation (the per-surface set), Layout
│   │                                                    (the div), Text/Icon/ProgressBar/Spacer/Cutout,
│   │                                                    and Components for the ready-made ones. SHORT
│   │                                                    names, which is what the namespace buys.
│   │                                            IPlaybackSession + PlaybackInfo — the OS media transport
│   │                                                    (lock screen, media notification, SMTC).
│   │                                            SafeArea — what the platform reserves, relayed to the page.
│   │                                    Modules/FileDialog/ namespace Shenora.Modules.FileDialog —
│   │                                            IFileDialogs and its IPC module. Every write it performs
│   │                                            is atomic (see Engine/Files above), and SaveAsync is the
│   │                                            only universal shape: mobile grants a DOCUMENT, not a path.
│   │                                    Modules/Clipboard/ namespace Shenora.Modules.Clipboard —
│   │                                            ClipboardModule over the shell's IClipboardService,
│   │                                            OPT-IN via AddShenoraClipboard() because a page reaching
│   │                                            the clipboard is a decision, not a default. What it buys
│   │                                            over the browser's own API: no user gesture, and an app's
│   │                                            private format carried verbatim.
│   │                                    Modules/Requests/ namespace Shenora.Modules.Requests —
│   │                                            IpcRequestsModule (LIST / CANCEL / CLEAR_FINISHED) over
│   │                                            IIpcRequestTracker. ON BY DEFAULT (D64): Build() adds it
│   │                                            for every app, which is why its registration is internal
│   │                                            and UseRequests only configures it.
│   │                                    Core/Events/ namespace Shenora.Core.Events — IEventBus and
│   │                                            EventMessage, one of the three cores Build() composes
│   │                                            unconditionally (D64). In-process pub/sub; a transport
│   │                                            bridge is what forwards an event to a client.
│   │                                    ⚠ A RESTRUCTURE UPDATES THE MAP FOR THE FOLDERS ITS OWN COMMITS
│   │                                    TOUCHED, which is not the same set as the folders it MOVED —
│   │                                    D65 moved every one and three went missing from this tree.
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
│   │                                          streaming — and a PRODUCER on Core's IEventBus: a session
│   │                                          publishes what its browser does, scoped by a per-session
│   │                                          id, rather than owning bespoke subscription taps).
│   │                                          The old split protected a WinForms-without-
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
│   │                                          for two implementations behind an #if — a type that
│   │                                          differs per platform belongs in that platform's package.
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
│   ├── Shenora.React/      @shenora/react    — peer: react >=18; build tsc, test vitest
│   └── Shenora.Cli/        @shenora/cli      — build-time only (D67), a devDependency that ships inside
│                                               nothing: the `shenora` binary takes a built app onto a
│                                               simulator, an iPhone or an Android device. Node, no build
│                                               step, tested with vitest. The Android half runs on Windows.
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
    ├── Shenora.Sample.Web      Vite + React    — consumes @shenora/react (file:), port 3900, builds
    │                                            into the desktop sample's wwwroot; page-owned title
    │                                            bar (WindowCommands + useWindowMaximized), notifyReady,
    │                                            useShenoraQuery echo, useShenoraEvent tick, useDropZone
    │                                            target, secondary-window controls, dev interceptor
    │                                            (the e2e subject)
    └── Shenora.Sample.Maui     net10.0-android — the MOBILE e2e subject, and the only one a device claim
                                +net10.0-ios     may cite: it hosts the same portable logic over the MAUI
                                                 shells and carries the on-device probes (media, codecs,
                                                 safe area, save picker, background playback, Live
                                                 Activity). `dev.mjs android`/`mac` drive it.
                                                 ⚠ Its net10.0-ios TFM is macOS-only, so the `#if IOS`
                                                 half cannot be compiled or verified on Windows.
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
  directions. ⚠ **The fold alone cost adopters no code, but D65 then made the LAYER the namespace**, so it
  is `Shenora.Core.Ipc` and a `using` has to move as well. **A migration-cost claim is the first thing a
  later restructure invalidates, and it is the one an adopter acts on.**

## Public surface

**This file does not list it, deliberately — D57's rule applied to itself.** A hand-kept listing here
would be a THIRD copy beside the API baselines and the shipped XML docs, and the only one of the three
that no mechanism checks.

| What you want | Where it actually lives | What keeps it true |
|---|---|---|
| **The enumeration** — every public type and member, exactly | `tests/Shenora.Tests/Api/Baselines/*.txt` (portable + Windows) and `Api/MetadataBaselines/*.txt` (Android · iOS · Windows) | a test writes a gitignored `.actual` and FAILS on any drift; an intentional change means copying it over and noting it in `CHANGELOG.md` |
| **What each member means, and why** | the XML docs, in your IDE, straight from the nupkg | `CS1591` is an ERROR, so every public and protected member carries one |
| **The wire strings a page types by hand** — module names, route and event types, error codes, capability names | `docs/reference/wire.md` | GENERATED from the source constants; `verify` fails on drift |
| **Why a thing is shaped the way it is** | `docs/DECISIONS.md` | `dev.mjs decision-audit` re-checks every entry's claims against the tree |
| **How to USE a capability** | `docs/ADOPTION.md` and `docs/guides/` | — |

🔴 **The XML docs are the RICHER copy, and that is what settles it.** Checked across 32 claims spanning
every type family: not one lived only in the map, and several were better in the source —
`ShenoraApplication.Start`'s idempotence remark carries a device measurement *correcting its own first
justification*. **A hand-maintained third copy does not add detail; it subtracts accuracy.**

⚠ **If you are about to describe the surface here again, that is the signal to write an XML doc instead.**
The one exception already has a mechanism: the wire, because those strings cross a language boundary
where no IDE can help, and it is generated rather than typed.

What this file keeps is the MAP: which project holds what (the tree above), what KIND each subsystem is,
and the dependency rules a reviewer checks.

## Dependency rules (enforced by review)

- `Shenora` depends only on Microsoft.Extensions DI (implementation — the builder needs
  `BuildServiceProvider`, D17) + logging abstractions. Every shell depends downward on it.
- **Execution and reporting compose; they do not merge.** The `Engine/` layer must never learn what a
  long-running request is — a mission body reports through `IMissionObserver`, and that seam is the only
  thing pointing at the IPC side. ⚠ **This is now an INTERNAL direction, not a package edge** (D65 folded
  `Shenora.Ipc` into `Shenora` and relayered the namespace to `Shenora.Core.Ipc` — it is retired as BOTH,
  which is what `CLAUDE.md` says). It survived the fold because the compiler was never
  what enforced it: the rule is *feature → logic → wire*, discovered by reading the edges rather than
  imposed, and a reviewer checks it by asking which namespace names which. It is also why the scheduler
  ships no storage dependency: `IMissionQueueStore` is a seam, not an implementation.
  🔴 **A FOLDED PACKAGE CANNOT BE CITED AS A BOUNDARY** — an edge stated between two ids that are one
  package reads as enforceable and is not, so a reviewer would check it against a boundary the build no
  longer has.
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
- **`UiDispatcherBase` holds the shell-independent half of `IUiDispatcher`** (`Core/Shell/`, added
  2026-08-14). A shell supplies three hooks — `State`, `IsOnUiThread`, `TryPost(work, out failure)` —
  and inherits everything a caller can observe: the guarded inline and posted paths, the load-bearing
  `(Action)` cast that stops `Post(Func<Task>)` recursing into an uncatchable `StackOverflowException`,
  the state-shaped failures, and the cancellation-observing awaits. The two shell dispatchers were
  member-for-member mirrors before this — ~36 significant lines shared out of 80 and 132 — and those
  invariants belong to the CONTRACT rather than to either platform, which is the argument for stating
  them once. ⚠ `TryPost` hands the platform's own exception
  back rather than swallowing it, which is what let this preserve both shells' failure behaviour exactly:
  WinForms still faults an awaited call with what `BeginInvoke` threw, MAUI still reports
  `ObjectDisposedException` when `Dispatch` answers false.
- **Portable contracts live in `Shenora` (D20):** `IUiDispatcher`/`UiTargetState`,
  `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`Filter`/`Result`, `IClipboardService`,
  `IFileLockInspector`/`FileLockHolder` (0.11.0 moved these out of `Engine/Files` under exactly this
  rule — a SHELL implements it, so the contract lives here),
  and the portable bases `IUrlLauncher`/`IUiInteraction`, plus `ShellCapability` — the shared
  capability vocabulary (`windowChrome`, `dropZones`, `filePicker`, `folderPicker`, `savePicker`,
  `secondaryWindows`, `tray`) and the `NotSupported` factory a shell throws from when it lacks one
  (D33). The names are what a host advertises through `ShellInfo` and what a page branches on.
  Their Windows implementations stay in
  `Shenora.Windows`, which registers BOTH faces of each split service so app logic can depend on the
  neutral contract and compile with no Windows reference. The bar for moving a contract to `Core` is
  "app logic must compile off Windows", NOT "the signature happens to be platform-neutral" — which is
  why the window-state stack deliberately stays in `Shenora.Windows`.
  🔴 **There is no package-on-package edge above `Shenora` any more.** D37 merged the session stack into
  `Shenora.Windows`, where it is the `Sessions/` FOLDER, so D14's separation survives as an internal
  direction rather than an edge a reviewer can check against the csproj files.
- `src/*` never references `tests/`, `samples/`, or anything app-specific.
- No Lyntai reference, ever (docs/DECISIONS.md D1).
