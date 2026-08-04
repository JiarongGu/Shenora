# Shenora (神阙) — Completed Task Archive

> **This is the ARCHIVE of finished work — the closed backlog, kept for the record. The ACTIVE
> backlog lives in [`TASKS.md`](../../TASKS.md) (open tasks only).** An entry moves here once it is
> fully done (committed + verified), rather than being left checked off in place. `CHANGELOG.md`
> stays the release-facing log and `docs/ROADMAP.md` the narrative of what shipped and why; this file
> is the task-level record — the plans, the `file:line` anchors, and the judgement calls made while
> executing them.
>
> **Everything below is COMPLETE.** P5.5 (consolidation + the D19/D20 re-layer), P6 (adoption
> readiness) and P7 (stabilisation → v0.1.0 shipped 2026-07-31), plus the P1 skeleton tail.
>
> **Read this when you need the WHY behind a finished decision** — several entries carry warnings
> written for a future session ("DONE, but the fix had to be ADAPTED — read this before finishing
> it", the deliberate NOT-built list, the two `InternalsVisibleTo`/`Microsoft.Web.WebView2` keeps).
> Those are the entries most likely to be re-litigated by someone who only sees the code.

### The Live Activity devkit (2026-08-04) — DONE, iOS, verified end to end

**Outcome: the whole adoption is ONE MSBuild property plus four SwiftUI view bodies.** No lifecycle
Swift, no extension Info.plist, no .xcodeproj, no codesigning. Verified on the simulator from the OS's
own records: `pluginkit` registered the extension, `liveactivitiesd` reported
`Starting activity … state: active`, and `chronod` launched the widget through ExtensionKit to render it.

Owner scoped it iOS-first on this reasoning: Live Activities' portable analogue on Android is the
foreground-service media notification, which `IPlaybackSession` already covers for a music app — so the
distinct value here is NON-media progress (a download, a conversion, an export). Android therefore
registers an implementation that answers `Unavailable` with a reason rather than throwing.

**The four things worth carrying forward:**
1. **The Swift is shipped as SOURCE and that is forced.** ActivityKit pairs an activity with a widget by
   its ActivityAttributes TYPE; a Swift type's identity includes its MODULE; so the attributes must
   compile into the same module as the app's views. No prebuilt binary can satisfy that, which is why the
   package carries `buildTransitive/swift/` and an MSBuild target rather than a binary.
2. **The repo layout MIRRORS the package layout** (`buildTransitive/swift/`). The first Mac build failed
   because it did not, and a real consumer would have been the only one exercising the working path.
   Two paths where only one is tested is the shape of the bug, not the fix.
3. **The C#⇄Swift mirror tripwire is the piece to keep if only one could be kept.** Drift fails
   completely silently — a renamed field decodes to nil, nothing appears, nothing is logged. It also
   catches the subtle half: a non-optional Swift property fails the WHOLE decode because C# omits nulls.
   Sabotage-verified five ways.
4. **The devtooling had to be OBSERVE, not drive.** An activity is started by the app or by a push, so
   there is no simctl verb for it and a "drive" tool would really be driving the app's own IPC. What is
   actually missing is sight, so `dev.mjs mac activity` reports what the OS registered, started and
   launched — the third being the signature of a module-name mismatch, where every call succeeds and
   nothing renders.

⚠ **An empty Dynamic Island on a SIMULATOR is expected** — an activity there reports only a lock-screen
scene target. Seeing the pill needs a device, and that remains the one unverified visual.

The plan below is kept verbatim: the probe findings that de-risked it are part of the reasoning.

  Owner, 2026-08-04: *"yes I know Dynamic Island cannot be zero swift, but its better if we can support some
  kind of devkit for that?"* — so the goal is not to hide the Swift, it is to make everything *around* the
  Swift free. That framing is the right one, because the SwiftUI views are the SMALL part.

  **✅ THE PROBE RAN (2026-08-04) AND EVERY ANSWER CAME BACK IN THE GOOD DIRECTION.** Full mechanics in
  `.claude/knowledge/mobile-shells.md`; the short version, all measured rather than read:
  - **A Swift/SwiftUI app extension ships in a .NET iOS app, and `AdditionalAppExtensions` is FIRST-CLASS
    in-SDK support** — `_ExtendAppExtensionReferences` injects a prebuilt `.appex` into the embed and
    codesign lists and is reached from `_CompileToNativeDependsOn`, so it runs on every app build. Checked
    that the target is *reached*, not merely present, which is the distinction the presence-vs-content audit
    was about.
  - **`swiftc` alone builds the widget — no `.xcodeproj`, no second build system.** This was the answer that
    mattered most and it was not guaranteed: a devkit that had to own an Xcode project would be a different
    and much worse proposition than one that adds a file.
  - **iOS itself registered it:** `pluginkit` inside the simulator reported
    `com.shenora.sample.maui.islandprobe(1.0)` with `SDK = com.apple.widgetkit-extension`, the app
    installed and launched, and `codesign --verify --deep` confirmed the nested code did not break the
    container's signature.
  - **ActivityKit has no Objective-C surface** — `Headers/ActivityKit.h` is an empty include guard, the whole
    API is in the Swift module. So the earlier flagged assumption was RIGHT and is now verified: the
    lifecycle needs a Swift shim too, not just the view. Piece 2 below stands as written.
  - **Still unproven, so it is not claimed:** that a Live Activity actually STARTS and renders in the
    Island. That needs `NSSupportsLiveActivities` plus the C#→Swift call, and is the next probe.

  So the sketch below is no longer resting on an unknown, and the remaining work is ordinary. **What the
  probe changes about it:** piece 2 (the shim) is confirmed necessary rather than possibly-avoidable, and a
  new piece appears that is arguably more valuable than any of the four — **the kit can own the `.appex`
  BUILD**, since `swiftc` needs no project file. An app would supply four SwiftUI view bodies and the kit
  would compile, assemble, plist and hand them to `AdditionalAppExtensions`. That is the difference between
  "here is how to configure Xcode" and "add a file".

  **✅ SECOND PROBE ALSO DONE (2026-08-04) — a Live Activity STARTS from C# and the system launches the
  widget to render it.** So the entire chain is proven, not just the build: `Activity.request` returned an
  id, three updates were accepted, `liveactivitiesd` logged *"Starting activity … state: active"*, and
  `chronod` launched the extension through ExtensionKit. Mechanics and the four traps are in
  `.claude/knowledge/mobile-shells.md`; the two that change the DESIGN:
  - **The app-side shim is a static Swift library** (`swiftc -emit-library -static`, `@_cdecl`,
    `<NativeReference Kind="Static">`, `[DllImport("__Internal")]`), because activities are started BY THE
    APP. Verified with `nm` on the app executable. So piece 2 below is confirmed, and its shape is known.
  - **⚠ `-module-name` must MATCH between the appex and the shim**, or ActivityKit cannot pair the activity
    with the widget: a Swift type's identity includes its module, so one shared source compiled into two
    module names is two different types — and every API call still reports success while nothing renders.
    **That makes piece 1 (one definition, projected to both sides) strictly more valuable than it looked:
    the contract is not just the field list, it is the field list AND the module name.**
  - Not visually confirmed, with an informative reason: the activity's `sceneTargets` came back
    `[lockscreen: …]` only — no Island destination on this simulator — so an unlocked simulator shows an
    empty pill no matter what. A simulator presentation limit rather than a limit of the approach. Settle
    on a real device before claiming the Island renders.

  **The boundary falls exactly on the kit's existing headless law, which is what makes this clean rather
  than a compromise.** A Live Activity's UI *is* a SwiftUI view in a widget extension — an OS requirement,
  not a .NET limitation. But D13 already says the kit ships no design system and no UI component library, so
  "the views are the app's" is a rule this kit already had. Everything else is ours:

  | The app writes | The kit provides |
  |---|---|
  | the four SwiftUI presentations (lock screen, compact leading/trailing, minimal, expanded) | the state contract, both sides of it |
  | what its activity MEANS | the lifecycle (start / update / end), callable from portable C# |
  | | the C-callable Swift shim, so the app writes no lifecycle Swift |
  | | the tooling to drive and observe an activity without rebuilding the app |

  **The four pieces, strongest first — and the first one is the real prize.**

  1. **ONE definition of the activity's state, projected to BOTH languages.** A Live Activity's
     `ContentState` has to agree across Swift and C#, and when it drifts the on-device failure is SILENT:
     the activity simply does not appear, or appears stale. This repo has already solved this exact class
     twice — the C#⇄TS wire mirror kept by tripwires (`.claude/knowledge/ipc-contracts.md`), and `mediaUrl`
     shipped as CODE after the sample's hand-rolled copy drifted from the host route and cost a device run
     to find. A Swift peer is the same machine with a third target. **This is the highest-leverage part of
     the devkit and it is the one nobody builds for themselves**, because it only pays off after the second
     drift.
  2. **A thin C-callable Swift shim the KIT ships** — `@_cdecl` entry points (`start(json) -> id`,
     `update(id, json)`, `end(id, policy)`) with `[DllImport]` on the C# side. Mechanical, tiny, and
     written once instead of per app. ⚠ **This is the load-bearing unverified assumption** (see the probe
     below): if ActivityKit turns out to be reachable from C# directly, the shim disappears and this gets
     simpler, not harder — so the design is safe either way, but the plan must not ASSERT which.
  3. **The devtooling, which is the part that makes Live Activity work bearable at all.** Iterating on one
     today is miserable for three specific reasons, and each has an obvious answer in machinery this repo
     already has:
     - *You cannot see it without a device run, and the Island only exists on some models.* → **`dev.mjs
       activity start|update|end <json>`**, driving a real activity on the connected device or simulator
       from the dev loop. That turns "rebuild the app to try another state" into one line, exactly as
       `dev.mjs click|shot|input` did for the desktop.
     - *A malformed state fails silently.* → validate the payload against the declared contract BEFORE it
       leaves the host, so the failure is a message instead of an absence.
     - *There is no fast way to iterate the state MACHINE.* → a preview harness in the page, the same idea
       as the sibling's `installFakeBridge.ts`: render the activity's states in the browser during
       development, get the logic right with no device at all, then verify the real thing once.
     `dev.mjs mac` already has the capture half (`mirror`, screenshots, `cliclick`), so observing the Island
     is existing machinery pointed at a new target.
  4. **The portable half, which is what stops this being an iOS feature.** An activity almost always mirrors
     something the app ALREADY knows — playback position, a download's progress, a mission's status — and
     `IMissionScheduler` already produces exactly that shape. So the contract is *"project this state onto a
     platform live surface"*, with Android's foreground-service notification (`MediaStyle` /
     `setProgress`) as the second implementation. Two implementations is also what stops the contract being
     shaped around ActivityKit specifically, which is the mistake D45 had to undo for media.

  **✅ The de-risking probe is DONE** (see the top of this entry). The question it answered was not
  ActivityKit's API surface but whether the .NET for iOS build can carry a widget extension at all — it
  can, through in-SDK support, and `swiftc` needs no Xcode project. Nothing here rests on an unknown any
  more. **Both probes are answered.** The only thing still unverified is whether the ISLAND
  specifically renders, and that needs a real device — this simulator offers no Island scene target.

  **Not started, and it must not jump the queue** (D15): `DM3` is the live media work, and this wants a
  second consumer before it earns a package. The direction changes the SCOPE, not the order.

### D45 — the interceptor is a MIDDLEWARE pipeline, re-layered by DEPENDENCY (2026-08-04) — DONE

**Outcome: built on all three shells and verified on each — Android and iOS on devices, the desktop through a
real WebView2 (`InterceptorProbe`, sabotage-verified both ways). 8 package ids → 6.** The decision and the
as-built shape live in `docs/DECISIONS.md` D45; the release-facing account is `CHANGELOG.md` `## Unreleased`.
Commits: `cc49f8f` (contract) · `1fa4e06` (React + D45) · `8d7c271` (layers 2–3) · `2e31224` (shipped
`mediaUrl`) + the desktop half.

The plan below is kept verbatim because the ORDER of the owner's three corrections is the argument, and
because it records what was deliberately NOT built.


> **The framing that settles the shape** (owner): *"the interceptor today we focus on media, but in future
> this is going to be bigger than what we currently have — it's more like a middleware design if you think
> this way."*
>
> **And the kit already made this exact choice once.** `IMessageDispatcher` is a composable middleware
> pipeline over ONE transport, with the family's §5 order encoded (error boundary → app middleware →
> facades). This is that shape applied to RESOURCES instead of messages, which means the precedent, the
> vocabulary and the review instincts all transfer.
>
> **Why middleware and not a flat list of handlers:** the cross-cutting concerns are the point. Path
> containment, the SSRF guard, a cache, a log of what an opaque payload decoded to, a metric — each WRAPS
> the next rather than terminating, and expressing them as layers is what stops every route
> re-implementing them. A flat "first non-null wins" list cannot express any of them.
>
> **Media is today's only consumer and deliberately NOT the shape.** Local file access, generated images,
> exports and thumbnails are the same problem; a media-shaped seam would have to be broken to admit the
> second one. `Shenora.Core.WebViewResourceMiddleware` is written accordingly (done, uncommitted at time
> of writing).

Owner's correction, in three steps across one conversation, and each step widened it:
1. *"the interceptor interface should live in the core, and the implementation should live in
   mobile.ios/android, and media just taking care of media logic not really the interceptor logic"*
2. *"desktop will also have issue with access local folder/files so the interceptor is needed"* — so this
   is not a mobile workaround; a page cannot reach a local file on ANY shell.
3. *"even file access too"* — media is ONE CASE. The generic case is serving a local file to a page.

**So the split is by DEPENDENCY, not by feature — the same test D43 applied to thumbnails:**

| Layer | Holds | Why there |
|---|---|---|
| `Shenora.Core` | `IWebViewInterceptor` + `WebViewRangeDelivery` (**done**, uncommitted at time of writing) · the request/response/range types (**shipped**) · **the generic FILE server + path containment** | needs no dependencies, and every consumer wants local-file serving. An app serving a PDF or a generated image should not take a media package to get containment and ranges |
| `Shenora.Windows` · `.Android` · `.iOS` | one `IWebViewInterceptor` implementation each, declaring its own `RangeDelivery` | intercepting a request configures a WEBVIEW — a shell capability |
| `Shenora.Media` | **media logic only**: the playability planner, the probe-result shape, the cache key | needs a demuxer's vocabulary; nothing else does |
| `@shenora/react` | `mediaUrl(payload)` → a RELATIVE `\<route\>?\<base64url\>` · one new `ShellCapabilities` entry | ONE npm package for every shell, via the D36 handshake — capability, never platform |
| the app | the route name, the payload's contents, allowed roots, codec policy | all policy |

**What this MOVES from what already landed** (nothing is published — `shenora.media*` are all 404 on
nuget.org, verified — so it costs nothing but the edit):
- [x] `MediaRangeServer` → Core, renamed to drop "Media": it answers ranges for ANY file.
- [x] `MediaAccess` → Core, same reason: path containment and the SSRF guard are not media concerns.
- [x] `MediaBodyMode` → **already superseded** by `Core.WebViewRangeDelivery`; delete it from Media.
- [x] `MediaWebViewRoute` → dissolve. Its interception half becomes the shell implementations; its media
      half was only ever the body-rule constant, which is now the shell's.
- [x] **DELETE `Shenora.Media.Android` + `Shenora.Media.iOS`** (owner approved). With interception in the
      shells and serving in Core, they hold nothing. 8 package ids → 6. They come back only if genuinely
      platform-specific MEDIA work lands — the frame-grab / thumbnail pixels D43 deferred.
- [x] `Shenora.Media` keeps: `MediaPlaybackPlanner`, `MediaProbeResult`/`MediaStreamInfo`,
      `MediaPlaybackPolicy`, `MediaCacheKey`.

**⚠ CORE MUST SERVE MEDIA ON ITS OWN — the media package is an ADDITION, not a prerequisite** (owner,
2026-08-04: *"the interceptor without media bundle should still allow to load video/image/audio just by
default; if the platform does not support it just go error"* … *"and media bundle adds a middleware to
this"*).

So `Shenora.Core` ships a **working file middleware**, not only the contract: route + allowed roots →
containment → ranges → content type. With that alone, `<video src=…>`, `<audio>` and `<img>` all work for
anything the platform can already decode, and a file it cannot decode simply errors in the element — which
is the honest outcome and needs no kit code to produce. `Shenora.Media` then adds a middleware for exactly
the cases that default cannot serve: deciding playability, and later converting. **That ordering is what
makes the family opt-in rather than load-bearing** — an app serving a PDF or a JPEG never takes a media
dependency.

- [x] Core needs a content-type map for this. ⚠ One already exists as `WebViewContentTypes` in
  `Shenora.Windows` — check before writing a second (`extraction-sources.md`'s "grep for an owner before
  porting a helper a second time", which this repo has already been bitten by).

**Order to build, because layer 1 sets the shape of everything under it** (owner: *"the first thing to do
is setup the react"*): ~~React helper + capability~~ **(DONE)** → Core file middleware → three shell
interceptors → trim Media → rewire the sample → re-verify on both devices.

⚠ **The handshake must NOT carry the scheme or the range delivery.** A page told "you are on iOS, use
`app://`" is branching on platform again, and it is unnecessary: a relative url already resolves to
`app://…` on iOS and `https://…` on Android by itself. `RangeDelivery` is a host-side fact a page must never
see. The handshake answers only *"can you serve local files?"*.

⚠ **The route is `media`, not `video`.** Owner: *"this is going for audio/video/image"* — and with "even
file access too", serving is kind-agnostic. Only the PLANNER is playable-media-specific; an image needs
serving and no plan. The sample currently says `/video?…` and must be renamed.

_**DM5 is DONE for the shipping platforms** — `Shenora.Media` + `Shenora.Media.Android` +
`Shenora.Media.iOS`, all three packing at 0.8.0, all three with the full checklist (inline `IsPackable`,
`packableProjects`, description, solution entry, API baseline, README row + graph, `ARCHITECTURE.md`,
lexicon). The mobile pair is ONE shared source (`src/Shenora.Media.Mobile/`) differing by one compile
symbol, and a package built with neither fails **`#error` at compile time** — sabotage-verified, so a third
platform cannot inherit a guess. They reference `Shenora.Media` and the MAUI SDK but **NOT the shell
packages**: D40 left that edge "to determine when building", and built, it does not exist. The D41 tripwire
is armed and sabotage-verified (`NU1201`, cascading to the MAUI sample)._

_**The mobile media packages were VERIFIED WORKING on both devices** (2026-08-04, owner asked): served
through `MediaWebViewRoute.TryServe`, `[Unsliced]` on Android and `[Sliced]` on iOS chosen by the PACKAGE —
there is no `mode=` in either URL, so the app does not choose. Android: 3 requests, no loop,
`duration=60.00s`, `seeked -> 48.00s`. iOS: many small exact windows, `seeked -> 48.04s`. The gap that check
found was real — the packages packed while their entry point had never run._

⚠ **THE TWO PARAGRAPHS ABOVE ARE SUPERSEDED — read them as history, not as the shipped shape.** They
describe the intermediate 8-package layout, which was **deleted the same day and never published**.
`Shenora.Media.Android`, `Shenora.Media.iOS`, `MediaWebViewRoute`, `MediaRangeServer`, `MediaBodyMode`,
`MediaAccess` and `MediaCacheKey` **do not exist**. What shipped instead:

| Was | Is |
|---|---|
| `MediaWebViewRoute.TryServe` (per-platform package) | `MobileWebViewInterceptor` in `Shenora.Android`/`.iOS`, + `WebViewHost.Interceptor` on Windows |
| `MediaRangeServer.Serve` | `Core.WebViewFiles.Serve` + `interceptor.UseFiles(…)` |
| `MediaBodyMode` | `Core.WebViewRangeDelivery`, read off the interceptor |
| `MediaAccess.ResolveLocal` | `Core.WebViewFiles.ResolveContained` |
| `MediaAccess.IsRemoteAllowed` | dropped — no caller (`TASKS.md` DM4) |
| `MediaCacheKey` | `Core.DerivedCacheKey` |

The device evidence still stands, because the same clips play through the same seam — it was re-run after
the move (Android `Unsliced`, iOS `Sliced`, `seeked -> 48.00s`/`48.03s`), and the desktop was added
(`InterceptorProbe`, sabotage-verified). Current shape: `docs/DECISIONS.md` D45 + `docs/ARCHITECTURE.md`.

### The presence-vs-content gate audit (2026-08-04) — DONE, two real holes closed

The general question the empty-baseline incident left open: **which other gates are satisfied by the
presence of a file rather than its content?** Answered by walking every gate `verify` runs. Two were open,
one was already safe for a reason worth writing down, and the mechanism turns out to be predictable.

**The mechanism.** A presence-only coverage check is safe *only when the same set that drives it also
drives the content check*. `ApiSurfaceTests` is safe: `ShenoraAssemblies()` is derived from the baseline
FILES, so an empty baseline is still a test case and the drift assertion fails on it. The metadata path was
vulnerable precisely because its case source is a **hand-maintained list** — an empty baseline there had no
test comparing it to anything. So the smell to look for is not "does this check content" but **"is the
coverage set the same set as the content set?"**

**Hole 1 — `check-sensitive` failed closed on a MISSING patterns file and open on an EMPTY one.** The
guard's own comment says silently degrading to the two structural patterns "is indistinguishable from
clean" — and that reasoning was attached to one of the two ways of getting there. A file truncated by a
crashed editor or a mangled redirect (this repo has already had a file *created* that way), or created and
not yet filled in, ran two patterns and reported clean with no message at all. Now:

- Zero private patterns loaded → fail closed, same as missing.
- **A pattern that does not compile now FAILS instead of logging.** This was the worse half: partial,
  permanent, and invisible after the first scroll of output — the author believes a token is banned and it
  is not. The file is gitignored and the author's own, so "fix your line" is the only useful outcome.
- The "running built-ins ONLY" notice moved to where it belongs: it now prints whenever the private half
  did not load, not only when the file is absent. Under the CI opt-in an empty file used to run degraded
  and say only "clean".
- Sabotage-verified six ways including the two that must stay QUIET, with the patterns file restored
  byte-identical afterwards (asserted, not assumed).

**Hole 2 — nothing compared the two hand-maintained definitions of "shipped".**
`project.config.mjs`'s `packableProjects` is what `pack` iterates; `<IsPackable>true</IsPackable>` in the
csproj is what the surface gate means by shipped. That config file's own comment already states the
invariant — a project "claiming it while the tooling skips it is the two halves disagreeing" — and nothing
enforced it. The dangerous direction is silent and release-affecting: a new package with
`IsPackable=true` and no entry in the list has its surface gated correctly, is never packed, and the
release ships without it with every gate green. `doctor` now fails on either direction, and
sabotage-verified both.

**Already safe, and why:** the runtime API baselines (case source derived from the files), the surface
lexicon (an empty one fails every type name — loudly, not silently), `knowledge check`/`footprint` (they
read content, and the footprint budget warns by design), doc-drift (resolves pointers, i.e. content).

⚠ **Both fixes are in gates that must stay quiet in normal use**, which is the direction this repo's 0.4.0
incident got wrong three times in one day. Both were therefore verified on the path they should IGNORE as
well as the path they should catch — and both harnesses restore what they sabotage and assert the restore.

### Release hygiene — the two items the 0.6.0 incident earned (2026-08-04) — BOTH DONE

**1. A release now FAILS when `## Unreleased` is missing or empty.** `dev.mjs changelog` used to warn and
carry on, which is exactly how v0.6.0 published **0.5.1's code**: the work was committed locally and never
pushed, so the workflow released the remote's tree, bumped the version correctly, found nothing to stamp,
and shipped a version with no changelog entry at all. *The empty section was the signal, and it was there
and unused.*

- It FAILS rather than warns, which is a different judgement from the size/style budgets this repo keeps
  non-fatal. The rule is `RULES_INDEX.md`'s: correctness stops a release, style warns — and publishing the
  wrong code is correctness. The asymmetry settles it: a false stop costs one bullet and always has an
  obvious fix; a miss burns a version number, and this repo has burned two.
- **No override flag, on purpose.** The escape hatch is writing the line. Any other one gets used.
- The failure message points at the LIKELIER cause first — *check that the commits you mean to release are
  actually on the remote* — because an empty changelog is far more often a symptom of a stale tree than of
  forgotten prose. That is the whole 0.6.0 lesson, and it belongs in the message rather than only in a doc.
- "Entries" means at least one **bullet**, not merely a non-blank line: a `### Added` with nothing under it
  is precisely the artefact a half-finished release leaves behind, and it satisfies any looser test.
- It cannot turn ordinary commits red, which was checked rather than assumed: `changelogDoctor` is called
  only from the `changelog` command, which only the release workflow runs. `verify` never touches it.
- **Sabotage-verified across seven cases**, including the three that must stay QUIET (the real section, a
  one-bullet minimum, and the titled `## Unreleased — <title>` form) and on a **CRLF** checkout — "a gate
  that had never run on CRLF" is how one of the 0.4.0 gates broke.

⚠ **The harness was wrong before the gate was.** The first version spliced at
`indexOf('## Unreleased')`, which finds that phrase in the file's INTRO PROSE rather than at the heading.
So five sabotage cases "failed correctly" for entirely the wrong reason, and the one case that should have
stayed quiet failed too. **A gate that reaches the right verdict by the wrong path is indistinguishable
from a working one until you read WHICH message it printed.** An exit code is not enough when a check has
more than one failure branch.

**2. The stray tracked file is gone, and `doctor` now sweeps for the next one.** A 0-byte file whose name
was two Private-Use-Area characters then "This" — a mangled shell redirect — was committed in `11e3469`,
reached the public repo and rode in the 0.6.0 tree. Harmless (no csproj referenced it, so it never entered
a package) but junk in a public repo, and **nothing was looking**: not `doctor`, not the sensitive scan,
not CI. Deleting the one file would have left the next one just as invisible, so `doctor` now fails on any
tracked PATH outside printable ASCII, printing the offending name `\uXXXX`-escaped so the message itself
does not carry unprintable characters into whatever reads the log.

- Deliberately narrow — tracked **paths**, printable ASCII — because that is what this repo uses and a
  narrow check cannot cry wolf. It is **not** a ban on non-ASCII CONTENT: sources here are UTF-8 with CJK
  in comments and strings. A legitimate non-ASCII filename would be a real decision, so failing and making
  someone widen the check deliberately is the right cost.
- Skipped with a stated reason outside a git checkout, rather than reporting a clean sweep — the same
  convention `doctor` already applies to the tag check it skips during a release.
- Sabotage-verified both ways: planting a PUA-named tracked file fails naming the escaped name; removing
  it returns green.

### `IpcJson` takes an app-supplied type-info resolver (2026-08-02) — DONE

Parked at the two-consumer bar since it was found while assessing on-device mobile; **owner direction
supplied the consumer** ("there should be a MAUI adaptation in the roadmap you can take too"). It is
the first of the three prerequisites `docs/2026-08-02-shenora-mobile-offline-plan.md` §4 names, and
the cheapest.

`IpcJson.AddTypeInfoResolver(IJsonTypeInfoResolver)` chains an app's resolver AHEAD of the reflection
fallback, before `Options` is first read. Three judgement calls worth keeping:

1. **It adds metadata; it does not reopen the options.** The single frozen instance exists because the
   source app grew three private copies that drifted. There is still exactly one instance, still
   read-only by the time anything can serialize with it — pinned by
   `The_options_are_still_one_frozen_instance`.
2. **Registering late THROWS, naming the fix.** Silently ignoring it would surface as a
   stripped-metadata crash on an iOS device, which looks nothing like its cause — the same reasoning
   that makes `ModuleContext` fail loud rather than no-op.
3. **Order is the whole feature, so the test is orientation-sensitive and was sabotage-verified.**
   Swapping the chain to default-first failed exactly
   `A_contributed_resolver_answers_before_the_reflection_fallback` and nothing else; restored green.

**What it deliberately does NOT do:** ship a generated `JsonSerializerContext` for the kit's own
envelope types, so `IpcRequest` and friends still resolve through reflection. Full NativeAOT with no
reflection at all needs that too — additive, and a separate change. Said so in the XML and the
CHANGELOG rather than leaving the hole implied.

### B1 — the update manifest and its diff (2026-08-02) — DONE

The first piece of the staged-update design to ship, and deliberately the piece with no I/O in it:
`UpdateManifest`/`ManifestFile`/`ManifestDiff` in `Shenora.Core`. Both donor apps hand-rolled this
TWICE — once in C#, once again in their native applier — which is what made it the obvious start.

Judgement calls worth keeping:

1. **Two comparison rules decide whether an updater converges, and both are sabotage-verified.**
   Paths normalize separators AND case: without it a manifest written with backslashes never matches
   one written with forward slashes, so the same file is "added" on every check forever. Hashes
   compare case-insensitively: without it a generator emitting upper-case hex reports every file
   changed — a full redownload indistinguishable from a legitimate one. Dropping either failed the
   test named for it and nothing else.
2. **A duplicate path throws instead of last-wins.** Last-wins makes the changeset depend on list
   order, which reproduces only on some inputs; the message names the path and which manifest carried
   it.
3. **`Removed` is tracked paths only** — never a directory sweep, because user data lives in the same
   tree and the manifest is the only thing that knows which files the app owns.
4. **The empty-release case is PINNED rather than defended against.** An empty release legitimately
   means "everything went away", so `Compute` cannot tell that from a manifest that failed to load —
   and that mistake deletes the whole install as the *successful* outcome of a copy. Validation
   belongs to the caller (B2), the XML says so, and a test records the behaviour as a decision rather
   than a surprise. One donor's applier carries exactly this guard; the other does not.
5. **`Diff` and `Manifest` entered the surface lexicon** with the reason beside them, and the note of
   what did NOT: no Release, Download, Install or Patch noun. The kit ships the changeset, not the
   updater.

### A1 — the client speaks both shells (2026-08-02) — DONE (`81c5232`)

`createHybridWebViewTransport()` (MAUI) + `createHostTransport()` (picks whichever host is present),
with the latter now `ShenoraBridge`'s default. An app calls `invoke`/`post` and never learns which
shell it is in — the transport seam (D16) doing exactly what it was built for.

Worth keeping: **the platform's two directions are asymmetric**, and the code says so rather than
smoothing it over — send via `window.HybridWebView.SendRawMessage`, receive a
`HybridWebViewMessageReceived` CustomEvent on `window`. Also a genuine bug fix fell out of it:
`isShenoraAvailable()` tested `chrome.webview` alone, so on the MAUI shell it answered FALSE and an
app would have concluded it was in a plain browser tab with a live host on the other side.
Sabotage-verified by reverting exactly that.

The MAUI sample page stays hand-written on purpose: it is plain HTML out of `Resources/Raw` with no
bundler, so it cannot import an npm package, and what it demonstrates is the WIRE. Its comment says
so now, replacing one that had become false.

### A2 — the capability stubs (2026-08-02) — CLOSED BY ANALYSIS, nothing to build

**Read this before proposing shell capability stubs again.** The plan item said `UseMaui` leaves
capabilities unregistered, so portable logic "gets a null instead of the named refusal D33
promises". That hole does not exist:

1. Everything genuinely absent — drop zones, tray, secondary windows, window state, frameless
   chrome — lives in `Shenora.WinForms`/`Shenora.WebView2`, and `Shenora.Maui` references NEITHER.
   Portable logic cannot name those types at all. **D19/D20's layering already prevents the class of
   bug the stub rule was invented for**, which is a nice result: the older decision is doing the work.
2. Every `Shenora.Core` contract an app actually resolves IS registered by `UseMaui`.
3. The two Core seams with no MAUI implementation — `IPathLocker`, `IFileLockInspector` — are not DI
   contracts at all. They are nullable options on `FileUpdateQueueOptions` and `UseWinForms` does not
   register them either. And `IFileLockInspector.WhoHolds` is contractually *"never throws; empty
   means the platform cannot tell"*, so a throwing stub would VIOLATE the contract it implements.

D33 is not weakened by this — `ShellCapability` is used exactly where a shell implements a contract
it cannot fully honour: clipboard IMAGES (Essentials is text-only) and the folder/save pickers. Those
stubs shipped with the package in `a85280e`. The rule's scope is narrower than the plan assumed, and
that is the finding.

### The headless `IShenoraRunner` (2026-08-02) — DONE

The third and last of the mobile plan's §4 prerequisites. `ShenoraApplication.Run` threw without a
runner and the only implementation was in `Shenora.WinForms`, so Core's application-host half was
Windows-only IN PRACTICE despite every type in it being portable — the D3 spike bypassed the builder
entirely and wired DI by hand rather than fight it.

`UseHeadless` + `HeadlessRunnerOptions` (`HeadlessRunner` itself stays `internal`, like
`WinFormsRunner`). Judgement calls:

1. **The hook asymmetry is copied deliberately, not reinvented.** `OnStarting` unguarded (a hook that
   cannot start is a startup failure the app must see), `OnStopping` reverse-order and guarded,
   running even when startup failed partway. Sabotage-verified: forward-order shutdown fails
   `Run_starts_hooks_in_order_and_stops_them_in_REVERSE_order` — with two hooks, the right and wrong
   implementations give different answers, which is the property that test needed.
2. **`Cancel = true` in the signal handler is the load-bearing line.** Without it the runtime
   terminates the process on SIGINT/SIGTERM and the ordered shutdown never runs at all — which would
   have made the runner look correct in every test while skipping the whole reason hooks exist.
3. **It is NOT the mobile answer, and the XML says so where someone would look.** A platform that
   owns its own loop (a MAUI activity) cannot honour "blocks until shutdown" and needs its own
   runner. "Headless" reads like it covers that case; it does not, and that is the kind of claim
   `doc-claims` exists to stop shipping.
4. **The lexicon gained `Headless`** with the reason written next to it — the gate's own instruction
   is that adding a word IS the review.

**Every test in the file is bounded.** The wait is real, so an unobserved token would HANG the suite
rather than fail it.

### The host-side transport helper — `IpcHostBridge` (2026-08-02) — DONE

The second of the mobile plan's §4 prerequisites, and the one the D3 spike actually measured: the
spike needed **no change** to `Shenora.Ipc`, but it had to hand-write the read → deserialize →
dispatch → serialize → write loop that every non-WinForms host writes identically. That loop's
middle is now `Shenora.Ipc.IpcHostBridge`, and `WebViewIpcBridge` is a thinner adapter over it.

Judgement calls worth keeping:

1. **The split follows `NotificationPump`'s, deliberately.** No transport, no timer — which thread
   may touch a base's client is base-specific. What moved is protocol; what stayed is WebView2
   vocabulary (`ContentLoading`/`ProcessFailed`, the `Forms.Timer`, `PostWebMessageAsString`, the
   int32 timer-interval bound).
2. **The handshake→open-the-gate pairing MOVED; closing did not.** Opening is protocol, so every
   base gets it. Closing needs to know which of a base's own events mean "the client can no longer
   receive", and choosing that wrongly is P5.5 H3 (`NavigationStarting` closed the gate forever).
3. **`HandshakeModule`/`HandshakeType` moved to `Shenora.Ipc` with `const` forwards left behind.**
   A headless host cannot reference `Shenora.WebView2`, and the constants are wire contract pinned
   by `WireMirrorTests`. Because consts inline, the literals every consumer compiled against are
   unchanged — proven by `Shenora.WebView2`'s API baseline not moving at all.
4. **The old suite is the regression proof.** `WebViewIpcBridge.HandleIncomingAsync` was kept as an
   internal forwarder so `WebViewIpcBridgeTests` still drives the full WebView2 composition rather
   than the neutral piece in isolation. Sabotage confirmed it is not decorative: deleting
   `Pump?.Open()` failed **12** tests — the new `The_handshake_opens_the_pumps_gate` plus eleven
   pre-existing WebView2 notification tests. The error boundary was sabotage-verified separately
   (leaking `ex.Message` failed the `DoesNotContain` leak test by name).

### The mission layer's three owner-directed additions (2026-08-02) — all DONE

Planned in two docs, built in the recommended order the same day, each with its own commit and its own
`### Breaking`/`### Added` entry. The narrative is `docs/ROADMAP.md`; what is worth keeping HERE is the
judgement in each, because two of the three shipped as something other than what was first proposed.

1. **The queue's store** (`66d8d1f`) — `IMissionStore` → `IMissionQueueStore`. **The first design was
   rejected by its own cost analysis**, and that is the part to read before proposing it again: a
   pluggable async queue puts an `await` in the dispatch path, which cannot run under the scheduler's
   lock, so admission would read candidates, take the lock, then RE-VALIDATE against a collection that
   may have changed underneath. A race in the one place where a race corrupts rather than delays, in
   exchange for a distributed-queue capability no consumer has asked for — while the part apps do vary,
   ordering, was already theirs through `IMissionPolicy`. So the pending list stayed internal and
   synchronous and only the STORE changed, which is why 733 tests passed unchanged.

2. **Chained missions** (`3a89d38`) — `MissionChain.Sequence`, `MissionStep`, `IMissionChainContext`.
   The fork was ONE queue entry versus N with dependency edges; the owner chose one, so the scheduler
   learns nothing about chains and §10's "no DAG engine" survives. The accepted cost is written into
   the XML: a chain holds the UNION of its steps' claims for its whole life, stronger mode winning, so
   a read-then-write chain holds that key exclusively throughout. Escalating to per-step claims is
   design (a) and wants its own pass. `RunStepAsync` deliberately duplicates the retry rule rather than
   delegating to the scheduler's, because the scheduler retries a MISSION and a chain is one mission —
   delegating would re-run steps 1–3 when step 4 failed.

3. **The file-update queue** (`c395448`) — deliberately NOT part of mission management. Atomicity is
   the app's choice per update, which was the owner correcting a fixed default; `AllOrNothing` then
   forced staged deletes, since a delete is the one change that cannot be undone from nothing. Backups
   and aside-copies are siblings of their target so every move stays same-volume — a staging directory
   elsewhere would silently turn each replace into a cross-volume copy of the file being replaced.

**Two warnings for a future session.** `.claude/knowledge/doc-claims.md` was written this day and
immediately paid for itself twice: `doc-drift` caught a retired symbol stated as current inside the very
plan that introduced the rename, and again inside the `TASKS.md` entry for the task doing the renaming.
And the chain claim-union test **passed its sabotage** — it had ordered its steps shared-then-exclusive,
so a "last wins" bug produced the same answer. It is now a `Theory` over both orders; if someone
simplifies it back to one case, that test stops testing anything.

4. **Cross-process locking** (`25f32ad`) — the open question ("does anything need leases today?") was
   answered with a real consumer: a filesystem-heavy sibling that does not own its working folder,
   spawns its own fixing tools, and competes with a mod loader and other applications, over a NAS as
   well as locally. That evidence SPLIT the feature rather than just unblocking it: `IPathLocker` for
   participants, `IFileLockInspector` for the foreign processes a lease can never touch. It also
   corrected the plan's own "network shares are not a target" (§4.1) — caution written as guidance.

5. **Crash-atomicity** (`a2b1c9f`-ish, the journal commit) — `IFileUpdateJournal` +
   `FileUpdateQueue.RecoverAsync`. The design predicted this would be additive "because the rollback
   bookkeeping is already the journal's content", and that held — but it missed the structural
   consequence: undo had to become DATA rather than closures, which forced every change to be planned
   before it is applied. **Read that before adding a change kind**: the plan/apply split is not
   stylistic, it is what makes a write-ahead record possible at all.

**Sabotage-verification earned its keep three times in this group**, twice by exposing tests that
could not fail:
- The chain claim-union test ordered its steps shared-then-exclusive, so a "last wins" bug gave the
  same answer. Now a `Theory` over both orders.
- The first crash tests could not distinguish a write-ahead journal from a write-after one, because
  the crash always fired BEFORE the mutation. `A_change_that_LANDED_before_the_crash_is_still_undone`
  is the one that fails when journalling moves after the apply — the others do not.
- The file queue's serialization test does fail when the partition gate is removed, as intended.

**Nothing from this group is open.**

### Drop zones: state the gain, not just the wiring (2026-08-02)

The last open finding from the first adopter's IPC + drop-zone review, and a direct application of the
owner's second review criterion — *"we focus on making things work… a better UI"* — which asks what the
ADOPTER GAINS, not merely what code they delete. `ADOPTION.md`'s drop-zone row was a dense, accurate
description of how to WIRE the manager that never said why anyone would want it. It now leads with the
capability: an HTML5 drop hands the page a blob and withholds the path, so a page-side drop target
cannot open, hash, watch or move the file — the native overlays read the OS drag data directly and
yield the real path, including drags from another app while the window is backgrounded. A callout under
the Stage-1 table carries the dedup argument the review asked for: four independent ports of one fiddly
component (the kit's own header notes its third copy was already annotated "ported from…"; the first
adopter was carrying a fourth at 387 C# + 84 TS lines) is the case for a shared body in one row.

No code changed — the component was always right, only the argument for it was missing.

### 0.2.0 design pass — judging the DESIGN, not the code (2026-08-01, before publish)

Owner direction after the whole-codebase review below: *"usually if you do the code review, you
should be getting the purpose of the project rethinking if this is a good design, instead just check
if the code itself works or not"* — then *"lets do all, make a proper 0.2.0"*. The review had audited
the kit against its OWN stated intentions and never asked whether those intentions were right. Four
items (D1–D4), all free only because 0.2.0 was built but unpublished. Commits `351313f`, `8214f89`,
`fe0a10a` + the D3 spike.

**D1 — cut the crash-checkpoint half of the operations cluster.** Three facts pointed the same way:
the design doc's own §4.2 note already admitted `RegisterWaiting`/`ResumePayload`/`RequestResume`
"come from ONE app, not two" against a two-app bar; the cluster took ~8 reshapes inside one
unpublished release; and it produced the release's only Critical. The root cause was structural —
accepting entries the kit never started forced every caller to answer "does this one still have a
live body?", and the three answers tried (a second status with no terminal exit, an app-controlled
field that dropped live operations, an internal provenance flag) are the amendment stack. Removing
the question removed all three. `OperationRegistry.cs` 992 → 826 lines. **The cut landed NARROWER
than first scoped**, and that correction matters: `RequestWait`/`RequestResume` stayed, because they
are the ask-act pair for the download-manager shape the kit itself names as a consumer, and cutting
`RequestResume` would have left a client able to pause but never resume.

**D2 — frameless chrome: a REJECTION plus a narrower change.** The review flagged `OptimizedForm` as
the kit's one inheritance-only feature and proposed making the chrome attachable. Rejected on
evidence (D24): the window style belongs in `CreateParams` at handle creation, so attaching it later
needs `SetWindowLong`+`SWP_FRAMECHANGED` as a second mechanism — doubling the verification surface in
the one area where a green unit suite has twice been wrong — and `WindowCommandOptions` already lets
a non-`OptimizedForm` window drive the commands, so only the chrome itself needs the type. Owner
direction settled the placement ("a style of our winform design" / "a fixed winform type"). The
cohesion complaint was still fair, so the part with NO message-loop responsibility moved to an
internal `CaptionButtonRenderer` (998 → 905 lines). **The split line is the reusable rule:** extract
what is pure input → pixels; leave anything that answers a window message where the OS can see it.

**D3 — validate D16 with a real second transport. PASSED.** `NotificationPump` had been extracted
"so a second, non-WinForms base inherits these already-fixed bugs" and no second base existed, so the
claim had never been executed. A throwaway `net10.0` console app (`devtools/_transport-spike/`,
gitignored) referencing ONLY `Shenora.Core` + `Shenora.Ipc` ran request/response, the structured
error boundary, the pump on a `PeriodicTimer`, and a `ctx.Run` operation streamed as batched
notifications — all green, with no change to `Shenora.Ipc`. The TFM is the proof: a Windows type
anywhere in that graph turns the project red. Three follow-ups are in `TASKS.md` — a host-side
transport helper (~40 lines every non-WinForms base will write identically; NOT built, because the
spike is one consumer and the bar is two), no headless `IShenoraRunner` in Core, and the honest limit
that a transport spike says nothing about the desktop-flavoured SERVICE contracts (`IFileDialogs`),
which still await a real mobile consumer.

**D4 — gate the prose.** See `CHANGELOG.md` 0.2.0 `### Changed`. Two precise checks, sabotage-verified
both ways; it found real drift on its first run (three knowledge/guide files still naming types
renamed in P5.5/P7, including `REVIEW-GUIDE.md` telling reviewers to stand down on a finding P7 had
already accepted).

### 0.2.0 — whole-codebase review (2026-08-01, before 0.2.0 was pushed or published)

A full read of `src/` (~13 k lines across the five packages + `@shenora/react`), the samples and the
docs, run against `docs/REVIEW-GUIDE.md`'s invariant map and the five classes five consecutive phase
reviews once missed. Baseline was green before and after (`verify` PASSED; 680 dotnet + 101 vitest),
so **every finding below is a latent defect, not a regression** — and the shape of the list is worth
noting for the next reviewer: nothing was found in the threading, marshalling, resource-ownership or
error-boundary hot spots the guide points at hardest. Those areas have been reviewed repeatedly and it
shows. What was left was the surface NO existing gate looks at:

- **Docs that ship.** Three defects lived in XML/JSDoc and the README — the places a consumer reads
  and no test compiles. The `RequestResume`/`ResumePayload` drift is the cleanest example: the code fix
  (commit `7c4313c`) was correct and its DECISIONS/design-doc amendment stacks were complete, but five
  shipped doc sites and three docs still described the superseded rule, and `docs/ARCHITECTURE.md`
  contradicted its own paragraph 50 lines earlier.
- **The dependency graph, which no doc had ever drawn correctly.** `README.md` and `docs/ADOPTION.md`
  both claimed `Shenora.WinForms → Shenora.Ipc`. It has never existed: the graph is a DIAMOND over
  `Shenora.Core`, and the absence of that edge is load-bearing in both directions — it is why
  `Shenora.Ipc` is `net10.0` and binds to no UI framework (D16's transport story), and why
  `WindowCommandFacade`/`DropZoneFacade` live in `Shenora.WebView2`. Four separate code comments state
  the invariant; the two documents an adopter actually reads stated its opposite.
- **Gates with a blind half.** `index.test.ts` pins the npm barrel by comparing `Object.keys(barrel)`
  — structurally blind to `export type`, so `OperationProgress` was missing from the public surface for
  a release with nothing failing. The only symptom was the kit's own sample re-declaring the shape
  inline. Now pinned by a type-only import in the same file (sabotage-verified). Same class:
  D22's audit method is "sweep the API baselines for domain words", and a csproj `<Description>` is in
  no baseline — so the `Shenora.WebView2.Sessions` package description still advertised "login
  windows … (silent refresh, cookie capture)" and "co-browse streaming primitives" on nuget.org, for
  types renamed in P5.5 H9.7/H9.8.
- **One live operational gap:** the git hooks were not installed in this clone, so the sensitive
  guard's pre-commit and commit-msg halves were inert on a public repo. `sensitive-info.md` names this
  exact failure ("a fresh clone has no hooks, so the guard silently does nothing — hit live, two
  commits nearly landed unguarded"). Fixed with `dev.mjs install-hooks`; the rule was right and the
  clone had simply never run it.
- **Small guard/consistency fixes** where the kit did not follow its own earned rules:
  `InteractiveSession`'s loading-fallback timer tick ran the app's `OnLoading` unguarded (a timer tick
  is the AppCallback rule's own named shape, and the same callback is guarded twice below it in the
  same method); `EmbeddedResourceProvider` called the app's `Log` sink directly at seven sites, two of
  them in a fire-and-forget `Task.Run`; `DropZoneManager` — the kit's only in-repo emitter — still used
  `_ = EmitAsync(…)` rather than the `IEventBus.Emit` added in P6.4 to replace exactly that.
- **One known limit recorded rather than guessed at:** `IModuleRegistry` cannot see DI-registered
  facades, because `AddMessageDispatcher` maps them through `MapRegisteredModulesLazily` rather than
  `TryClaimModule` — which is not an oversight but the P5.5 H2 `StackOverflow` fix. So
  `IsModuleMapped` answers `false` for a routed module and `TryMapModule` answers `true` for a taken
  name. Precedence is correct (the app's own modules win); only the answer is dishonest, no consumer
  has hit it, and closing it needs a name-reservation seam or re-opening the deadlock. Documented on
  `TryMapModule`, in `ARCHITECTURE.md` and in `ipc-contracts.md`.

This also closed the last of the **first adopter's Stage-0 findings (2026-07-31)** — the stale "not
yet published to NuGet/npm" line and the missing TFM column — which had stayed open through 0.1.1 and
0.1.2 because they were docs, not API. Their context, retired from `TASKS.md` with them: a private
desktop sibling reached Stage 0 with `Shenora.WebView2.Sessions` 0.1.0 referenced, every package
resolving transitively from the leaf, and the host building 0 errors against `net10.0-windows` —
nothing consuming the kit yet, so the whole batch was packaging/docs rather than API. Worth noting
against the dependency-graph defect above: that adopter took the leaf, so the transitive close was
correct for them and the wrong chain in the docs never bit. It would have bitten the FIRST adopter who
followed the README's own "a shell with no web frontend references `Shenora.WinForms` directly" advice.

Full list with rationale: `CHANGELOG.md` 0.2.0 `### Added`/`### Fixed`. Reusable lessons promoted to
`.claude/knowledge/ipc-contracts.md`: a runtime export gate proves nothing about type exports, and the
DI-registry limit.

### P5.5 — Consolidation: cleanup, re-layer, roadmap revisit (2026-07-30) — DO BEFORE P6

The consolidation checkpoint after P0–P5 put the whole body of the kit down fast (see
`docs/ROADMAP.md` `### P5.5` for the phase's framing and the P6/P7 revisit that came with it). Three
strands: **cleanup** (this list), **re-layer** (H4.1, D19+D20), **roadmap revisit** (done — in
ROADMAP).

The cleanup list came from the first full P0–P5 review (six parallel reviewers over all five
packages + the npm client + the tree; `docs/REVIEW-GUIDE.md` was the brief). Baseline was green
(`verify` PASSED at `130d4cd`), so every item below is a latent defect, not a regression — the
product of velocity, not of breakage. Findings are grouped as fix batches, ordered
by leverage; `file:line` anchors are from `130d4cd`. H1–H3 are things a consuming app cannot work
around, H4 removes the duplication that CAUSED several of them, H5 closes the gate that let them
through, H6–H7 are pre-1.0 surface and consistency.

**EXECUTION ORDER (decided 2026-07-30 with the re-layering design — do NOT just run H1→H8):**
1. **H1 + H5** on the CURRENT layering — security fixes + gate holes. Surgical, no structural churn;
   a path-traversal fix must not wait behind a refactor.
2. **The re-layer** (H4.1, its own commit) — `docs/2026-07-30-shenora-relayering-design.md`, D19+D20.
3. **H4.2–H4.7** dedup on top — mechanical once the single owner exists.
4. **H4.6 + H9** — the neutral session controller and the co-browse primitives/hooks redesign (D21).
   Together, because H9.4 needs H4.6's base, and both are pre-1.0 breaking changes to the same types.
5. **H2 / H3 / H6 / H7** — several H2 items are marshal-related and DISSOLVE into step 2; re-check
   them against the new code rather than fixing them twice. Note H9.3 subsumes H2's co-browse
   "root the screencast receiver / complete the channel" items — do them once, there.

Standing rule for this phase: each batch ends with `dev.mjs verify` + a regression test per fixed
defect, and every earned invariant lands in `.claude/knowledge/` (see H8) rather than only in a
code comment.

**H1 — Security / data-integrity (do first; two are reachable by content the app doesn't control)**

- [x] **Path containment in file-mode serving.** `WebViewHost.cs:199` unescapes the request path,
  then `WebViewResourceProvider.Normalize:180` only does `\`→`/` + `TrimStart('/')` — no `..`
  rejection, no containment — before `Path.Combine(root, …)` at `:125` (and `:161` `Exists`). Two
  live vectors: `%2e%2e%2f…` → `../`, and a ROOTED path (`/C:%2f…`) which `Path.Combine` returns
  outright. Responses carry `Access-Control-Allow-Origin: *`. Active whenever `PreferFiles` is on —
  the sample derives it from `IsDevelopment`, so every dev session + any file-mode deployment. FIX:
  reject rooted/`..` paths and assert `Path.GetFullPath(combined).StartsWith(fullRoot + sep)` in
  BOTH methods; tests for `%2e%2e%2f`, `C:%2f`, and a legitimate CJK/spaced filename (the unescape
  exists for those — don't regress it).
- [x] **Enforce `NavigationGuard` in `NavigationStarting`, not only on the explicit call.** Checked
  only at `RenderSession.cs:59` / `LoginWindowController.cs:142`; the package's sole
  `NavigationStarting` subscription (`LoginWindowController.cs:94`) just fans out to app taps and
  never cancels. So a guard-approved URL answering `302 → http://127.0.0.1:8080/admin` is followed
  and its DOM handed to the caller — the documented SSRF boundary doesn't hold for redirects,
  `location.href`, `<meta refresh>`, or iframes. FIX: cancel in `NavigationStarting` (covers
  `e.IsRedirected`); keep the per-call check as a fast pre-check. Same wiring for pool instances.
  **DONE, but the fix had to be ADAPTED — read this before "finishing" it:**
  `CoreWebView2NavigationStartingEventArgs` has **no deferral** (proven by compiler error while
  implementing the obvious version), so the async guard CANNOT be awaited in that event and blocking
  on it would deadlock the UI thread it runs on. What shipped: the pool records the host the guard
  approved (`PoolInstance.ApprovedHost`, cleared on return-to-pool) and cancels unvetted CROSS-HOST
  navigation synchronously — which closes the documented `302 → 127.0.0.1` vector while leaving
  same-host hops working. Full redirect/subresource policy remains `SessionBrowserOptions.RequestFilter`
  (already synchronous and wired with `WebResourceContext.All`); both options now document the
  division of labour. Deliberately NOT applied to `LoginWindow` — interactive OAuth legitimately
  redirects across hosts, so cancelling unvetted hops there would break real logins.
- [x] **Guard the outgoing notification serialize.** `WebViewIpcBridge.TryBuildBatchJson:278-293`
  DRAINS the queue then calls `IpcJson.Serialize` with no try/catch, reached from `Flush` ← the
  50 ms timer. An app event carrying a cyclic graph, a `Type`/delegate member, or a throwing getter
  → unhandled UI-thread exception (crash dialog under the family bootstrap) AND the whole drained
  batch is lost. The INCOMING path already guards this exact case with a comment — copy it
  (per-notification, so one bad event can't kill its batch) + a catch-all in `Flush`.
- [x] **Contain the profile path that `ClearProfile` deletes.** `LoginWindow.cs:295-306` is an
  unbounded `Directory.Delete(recursive: true)` on a caller-composed path, while the same options
  doc calls per-(provider, sub) scoping a security boundary and describes provider definitions as
  data-driven. A `..` segment merges two accounts onto one cookie jar or aims the delete outside the
  sessions root. FIX: a compose helper that rejects separators/`..`/reserved names + resolve-and-
  contain before deleting.
- [x] **Dispose the leaked process handle** at `WebViewHost.cs:324` — `ShellLauncher.cs:69-72` has
  the Win11 `?.Dispose()` lesson; the WebView2 copy of the same open-in-shell code omits it, so
  every external link click from the page leaks a `Process`.

**H2 — Hangs, crashes and lifetime (a consuming app cannot work around these)**

- [x] **`RenderSession` must observe the tokens it accepts.** DONE across two batches. H4.2 routed the
  marshal through `WinFormsUiDispatcher`, whose `InvokeAsync` observes the token via `WaitAsync`, so the
  CALLER always escapes. This batch added the half that actually frees the pool:
  `RenderSession.RunBoundedAsync` caps every marshalled op at the new
  `RenderSessionPoolOptions.OpTimeout` (60 s) and POISONS the instance when the body never completed,
  so `Return` discards it instead of re-pooling a wedged page. Two judgement calls worth reading:
  (a) "never completed" is TRACKED (a flag set in the body's `finally`), not inferred from the
  exception — a body that ran and threw (a rejected URL, a guard refusal) leaves a perfectly reusable
  instance, and discarding it would cost a browser startup on every ordinary error; (b) a CALLER
  cancellation also poisons, deliberately — the caller walked away while the op was outstanding, so
  the renderer may still be mid-script and handing that page to the next lease is the real risk. The
  expiry surfaces as `TimeoutException`, but a caller's own `OperationCanceledException` is never
  rewritten. `NavigateAsync`'s hardcoded 30 s cap became `NavigationTimeout` so the two budgets are
  coherent (`OpTimeout` must exceed it, documented on the option).
- [x] **Suppress script dialogs on session browsers.** DONE in H4.4 (`AreDefaultScriptDialogsEnabled = false`). `SessionBrowser.cs:112-120` leaves
  `AreDefaultScriptDialogsEnabled` true while `OffscreenWindow` parks the host off-screen at
  opacity 0 — an `alert()` blocks the renderer behind a dialog nobody can see or dismiss, which
  compounds the item above.
- [x] **Unclosable login modal.** DONE — `Finish()`+`Close()` moved first, app callback guarded. `LoginWindow.cs:274` finally order is
  `fallback.Dispose(); OnLoading?.Invoke(false); controller?.Finish(); form.Close();` — `OnLoading`
  is app code, so a throw (splash already disposed) escapes the `async void` handler, `Finish()`
  never runs, and the foreground `FormClosing` handler (`LoginWindowController.cs:67-72`) then
  cancels EVERY close including the user's and `Application.Exit`; `ShowDialog` never returns and
  the busy gate stays set. FIX: try/catch the callback; `Finish()`+`Close()` FIRST. Same for
  `:234` and the posted body behind `SetLoading` (`LoginWindowController.cs:239`).
- [x] **The frameless-maximize ⇄ window-state seam (live in the reference composition).** DONE via the
  new `IAppMaximizable` seam (`OptimizedForm` implements it; `WindowStateManager.Save`/`Apply` prefer
  it over `Form.WindowState`/`RestoreBounds`), + 4 regression tests. The `MinimumSize` clobber below
  was fixed in the same pass.
  `WindowStateManager.Save:60-61` reads `form.WindowState`, but frameless `OptimizedForm.Maximize()`
  (`:142-157`) only sets `_maximized` (pinned: `OptimizedFormTests:91` asserts `Normal`). Closing
  maximized persists `maximized:false` WITH the work-area rect as normal bounds → next launch fills
  the work area believing it is not maximized: `WM_NCCALCSIZE` takes the normal-inset branch (the
  border gap the whole technique removes), the page's glyph is wrong, and clicking maximize captures
  the work-area rect as `_restoreBounds` so RESTORE IS A PERMANENT NO-OP. FIX: an app-maximized
  seam (`IsAppMaximized` + app restore bounds) that `Save`/`Apply` prefer over
  `Form.WindowState`/`RestoreBounds`.
- [x] **`AddMessageDispatcher` DI recursion → StackOverflow, no diagnostic.** DONE —
  `MapRegisteredModulesLazily` + a duplicate-module guard. NOTE the honest asymmetry, documented at the
  site: the eager `MapRegisteredModules` throws at composition, but the lazy path can't detect a
  duplicate until the first dispatch, and `DispatchAsync` never throws by contract — so there it is a
  logged error response. "Diagnosable", not "fails at startup".
  `IpcServiceCollectionExtensions.cs:49-55` enumerates facades (`sp.GetServices<IModuleFacade>()`)
  INSIDE the `IMessageDispatcher` singleton factory. Any facade whose graph injects
  `IMessageDispatcher` — the documented cross-module `SendAsync` seam — re-enters the same factory;
  MS DI's cycle detection is call-site-based and cannot see a factory delegate re-entering the
  provider, and the cache entry isn't published yet → unbounded recursion, process death. FIX: map
  facades lazily (terminal middleware over a `Lazy<IModuleFacade[]>`) so the singleton is cached
  before enumeration; test the exact composition (`class F(IMessageDispatcher) : BaseFacade`).
- [x] **`app.Dispose()` throws on a clean quit** when any singleton is `IAsyncDisposable`-only
  (`ShenoraApplication.cs:46,132`; MS DI throws for async-only captured disposables). Latent against
  Shenora's OWN `RenderSession`/`CoBrowseSession`. FIX: add `IAsyncDisposable` → `_provider.DisposeAsync()`.
- [x] **Absolutize the resolved root/data paths** in `ShenoraPaths.Resolve`/`ResolveRoot:90-101`
  (returned verbatim today). `FileDialogs` sets `RestoreDirectory = false` on all three dialogs
  (`:146,174,218`, deliberate), so the process CWD moves after the first dialog and a relative
  `--app-root` re-resolves `DataDir` mid-session; it also defeats `SingleInstanceGuard.ChannelKey`
  hashing (two spellings of one install → two instances over the single-writer WebView2 folder).
- [x] **A cancelled lease cannot escape DURING browser init — DONE in H9.6 (2026-07-31)**, exactly as
  planned: bundled with making the statics internal, since it is the same signature.
  `SessionBrowser.InitializeAsync` now takes a `CancellationToken` passed to BOTH `WaitAsync` calls and
  wired from the render pool and the streaming session, so a cancelled lease escapes during init instead
  of waiting out `InitTimeout` twice. The token gates the AWAIT only — never the creation — because the
  environment task is shared across the pool's instances. Original note follows.
  (the H2 sessions batch
  closed the "publishes a live browser" half; this is the promptness half). `SessionBrowser.InitializeAsync`
  takes no `CancellationToken` at all, so a cancelled `LeaseAsync` waits out `InitTimeout` (up to 2×25 s)
  before the new post-init check fires. Deliberately NOT expanded into that batch: adding the parameter
  is a public-surface change, and H6 proposes making these statics internal anyway — do both in one
  move there. Note the token must gate the AWAIT only, never cancel the creation itself: the
  environment task is now SHARED across a pool's instances (`SessionEnvironmentCache`), so cancelling it
  for one caller would break the others.
- [x] **No app callback runs unguarded inside a WebView2/WinForms event handler.** DONE. The structural
  answer is **one owner** — `Shenora.Core.AppCallback` (`Run` / `RunOrDefault`, public per the D19/D20
  placement law because three packages consume it) — rather than a try/catch remembered per site. What
  it closed, in landing order:
  - H4.2: `WindowCommandFacade` (SET_THEME's `ApplyTheme`, CLOSE's `FormClosing`) and `DropZoneManager`
    post through the guarded dispatcher; `LoginWindow`'s `OnLoading` guarded (see the modal item).
  - The H2 sessions batch: an **`ILogger` is app code too**, and H4.7's logging invoked it bare at all
    eight sites in that package — one throw escaped before `tcs.TrySetException` (hung lease, permit
    held), one before `_capacity.Release()`. Found by that batch's own phase review.
  - This batch: `WebViewHost`'s three app policy hooks
    (`OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed`) — and note the fix is not just
    "don't crash": a failed hook now **falls back to the kit's built-in policy**, because leaving the
    event unanswered is its own bug (an un-cancelled download proceeds, an unanswered permission
    request stalls its caller, a renderer crash goes unhandled exactly when things are already wrong).
    `OptimizedForm.WndProcHook` reads a throwing hook as "did not handle this message", so the window
    keeps working. And every `Action<string>? Log` site in `WebViewHost` + `WebViewIpcBridge` became a
    guarded, LAZY `Log(Func<string>)` — lazy because the guard must cover BUILDING the message too
    (several read WebView2/COM properties that throw once the object is gone), and because several sit
    inside a `catch` that exists to stop a failure escaping, where a throwing sink defeats the very
    thing it reports from.
  - `SessionController`'s four tap lists are now COPY-ON-WRITE arrays published under a lock. This was
    a genuine data race, not a style point: taps are registered from the driver's thread while the
    WebView2 handlers read them on the UI thread, and `List<T>.ToArray()` reads `_size` then copies the
    backing store — an `Add` in between throws or copies a torn view, and two concurrent `Add`s corrupt
    the list. Readers now need no lock at all.
- [x] **Pool reset must fail closed.** DONE — `AwaitResetNavigationAsync` (internal, so the REAL path
  is unit-testable; the old test could only drive `ResetOverride`, which is exactly why this survived
  five reviews) returns the navigation's actual outcome, and the 5 s budget became the validated
  `ResetTimeout` option. It swallowed the `WaitAsync` outcome and returned `true` unconditionally,
  reasoning in a comment that "the next lease navigates away regardless" — it does not: a renderer that
  can't answer a navigation to `about:blank` can't answer the next lease's either. So the documented
  "a failed reset DISCARDS the instance" invariant was reachable only via a THROW.
- [x] **Re-check cancellation after the multi-second init** DONE in `RenderSessionPool.CreateInstanceAsync`
  (its failure cleanup became a shared `TearDown()` local, now used by the cancelled path too and no
  longer silent) and at TWO points in `CoBrowseSession.StartAsync` — after init and again before
  publishing, since past that line the caller owns teardown. `LeaseAsync` now passes `linked.Token`
  (caller + pool-dispose) to the factory instead of the raw caller token, so disposing the pool
  mid-creation cancels the creation rather than letting it publish a live off-screen window whose
  browser process then holds the profile lock with nothing left to dispose it.
- [x] **Root the CDP screencast receiver.** DONE — the receiver AND its handler are fields
  (`_frameReceiver`/`_onFrame`), and `DisposeAsync` detaches before stopping the screencast. It lived
  only in a local, so the frame stream depended on the WebView2 SDK caching the receiver internally —
  unspecified behaviour, and a stream that stops after an arbitrary GC reports no error at all.
- [x] **`RenderSession.OnNetwork`/`OnMessage` don't check `_disposed`.** DONE — both throw
  `ObjectDisposedException` (matching every other member, via `OnUiAsync`) and the posted subscribe
  body re-checks, closing the check-then-post race. They were the only public members without a
  disposal check and the only two that install a PERSISTENT tap, so a late subscribe streamed the next
  lease's API responses and posted messages to the previous caller's handler.
- [x] **WinForms robustness tail — DONE**, all nine items, with `winforms-shell.md` (H8) capturing the
  invariants. Two judgement calls worth reading: (a) the form-level `AllowDrop`/`DragOver` was
  **removed outright** rather than option-gated, because the premise behind it was false — a drop target
  is registered PER HWND and `DropZoneOverlay` registers itself, so nothing ever needed the form's drag
  events; all it did was force OLE/STA on every consumer of the base class and show a copy cursor for a
  drop it then silently discarded (the existing test asserting `AllowDrop == true` carried that false
  premise in its comment). (b) `TrayIcon`'s wrong comment was fixed as DOCUMENTATION, not code:
  `CloseReason` genuinely cannot distinguish the user's X from a programmatic `Close()`, so the fix is
  telling adopters to close via `ExitApplication()`/`Application.Exit()` — now on the `CloseToTray`
  option itself, where the decision is made. Also landed: `OptimizedForm` re-fills on `WM_DPICHANGED` +
  `DisplaySettingsChanged` (with the `SystemEvents` unsubscribe that a static publisher demands) and
  validates its restore rect through `WindowStateManager.IsVisible` rather than a second opinion on
  "off-screen"; `WinFormsBootstrap` asserts STA with the fix in the message, is idempotent, and its
  crash dialog is one-at-a-time per thread (new internal `ShowDialogOverride` seam, since a real
  MessageBox would hang the suite and the re-entrancy IS the invariant); `SecondaryWindows` cleans up
  only after `Application.Run` returns, removes the entry on a failed `thread.Start()`, and replays a
  pre-handle `Activate`; `SingleInstanceGuard.TryAcquire` is idempotent; `SetTextAsync("")` clears.
  NOT tested, deliberately: the clipboard fix — a test would clobber the developer's real clipboard.
  Original list: STA assertion + idempotence in `WinFormsBootstrap.Initialize:65-88`
  (a missing `[STAThread]` currently surfaces as a blocking dialog inside handle creation; a second
  call double-registers all three exception channels); re-entrancy guard on the last-resort crash
  dialog (`:103-121` — `MessageBox.Show` pumps, so a repeating UI-thread exception stacks dialogs
  unboundedly); drop the unused form-level `AllowDrop`/`DragOver` (`OptimizedForm.cs:99-103` — no
  `src/` code subscribes to the form's drag events, it makes handle creation OLE/STA-dependent, and
  with no `DragDrop` handler it shows a copy cursor then silently discards the drop) or put it
  behind an option; `TrayIcon.cs:150-156`'s comment is factually wrong — WinForms reports
  `UserClosing` for a programmatic `Form.Close()` too (the repo's own `TrayIconTests:73` asserts the
  cancellation), so with `CloseToTray=true` a startup-abort `Close()` hides the window and leaves a
  resident process; `SecondaryWindows` registry/wait fixes (`:104` removes the entry on `FormClosed`
  so `Dispose` returns before `Application.Run` finishes tearing down a WebView2 window → stale
  profile lock; `:78-85` a failing `thread.Start()` leaves a permanent phantom entry; `:148-158`
  `Activate` is silently dropped pre-handle, which is the documented "`Open` activates the existing
  one" path); `WindowStateManager.Apply:29` unconditionally overwrites an app-set `MinimumSize`
  (the sample's `MainForm.cs:43` is already dead code); `SingleInstanceGuard.TryAcquire:90-118` is
  not idempotent (a second call leaks handle 1 and breaks the fast `--restarted` handoff);
  `ClipboardService.SetTextAsync("")` throws (`:32-40`) where a no-op/clear is meant;
  `OptimizedForm` manual maximize has no DPI/display-change handling, so `_restoreBounds` (raw
  physical px) goes stale across a monitor move and the fill is never refreshed.
- [x] **Client-side robustness tail (`@shenora/react`) — DONE**, all seven items, +10 vitest tests.
  Notes worth keeping: (a) `useDropZone`'s dead-zone bug needed the REF'S CONTENT made reactive, not a
  dep-array tweak — a `RefObject` is a stable object and a ref mutation triggers no render, so the fix
  is a `useState` element mirrored by a deliberately dep-array-less effect (`setElement` with an
  unchanged value is a React no-op, so it can't loop); the API stayed as it was. (b) `BaseModuleService`
  now resolves the bridge through a `protected get bridge` rather than a constructor default —
  subclasses keep using `this.bridge` unchanged, and an explicitly-passed bridge is still honoured
  (tested, because lazy resolution silently ignoring it would break the multi-transport case).
  (c) The fallback timeout only races a THENABLE — a plain value has already settled and must not be
  made async. (d) `useShenoraQuery` now keeps previous data alongside the error, so the caller chooses
  between stale-with-banner and hiding it. (e) The `debounce`/`randomUUID` helpers H4.5 deferred moved
  into a new non-exported `internal.ts` — a second consumer finally justified the shared home
  (`useWindowMaximized` needed the same debounce), which is the trigger H4.5 said to wait for.
  Original list: a host message of literal `null` throws an
  uncaught page error (`bridge.ts:186-192` — `JSON.parse('null')` then `parsed.category`);
  `BaseModuleService` captures the bridge eagerly (`moduleService.ts:26`), so a later
  `configureBridge` permanently kills every service built before it — resolve inside `send` the way
  `useDropZone` already does; `useDropZone` never registers a zone whose element isn't mounted on
  the first effect (`:139-141,201` — deps are `[enabled, targetRef]`, a stable ref object), so any
  conditionally-rendered target is silently dead — key the effect on the element;
  `useWindowMaximized` fires one un-debounced IPC round-trip per `resize` event (`:76-93` ≈ 180
  calls per 3 s drag, each with a 30 s timer) — reuse the debounce helper; `useShenoraQuery` blanks
  good data on a failed refetch (`hooks.ts:86`); `bridge.isAvailable` ignores `disposed` (`:87-89`);
  the `fallback` path bypasses the timeout entirely (`:120-127`).

**H3 — The notification/ready gate and validation — DONE (2026-07-30)**

- [x] **The ready gate has exactly one re-arm path.** DONE — the gate now closes on **`ContentLoading`**
  (a new document really is loading) and on **`ProcessFailed`**, instead of on every
  `NavigationStarting`. That event fires for navigations that never replace the document — one an app
  tap or a policy CANCELS, one that fails before committing — and the surviving page has already spent
  its single `READY`, so the gate closed FOREVER: buffer to 10 000, then silently drop-oldest, for the
  process lifetime. The bridge watches `ProcessFailed` ITSELF rather than relying on the host's
  auto-reload policy, which is optional. **The trade is stated at the site:** between
  `NavigationStarting` and `ContentLoading` the gate is still open, so a flush tick there delivers to
  the OUTGOING page rather than buffering for the incoming one — which is the better outcome, since
  those listeners are still attached and these are progress/status notifications.
- [x] **Validate the numeric options nobody validates.** DONE, all six:
  `MaxQueuedNotifications` (< 1 rejected — 0 made `Enqueue` dequeue what it had just enqueued, so
  EVERY notification vanished for the process lifetime with no error), `NotificationInterval`
  (< 1 ms, and > int32 ms — the WinForms timer's real limit), `SessionBrowserOptions.InitTimeout`
  (non-positive made init fail instantly with the profile-LOCK diagnosis, sending the caller after a
  zombie process that does not exist), `RenderSessionPoolOptions.OffscreenClientSize` (0×0 viewport),
  and `ScopedContainerRouterOptions.ConfigureScope` (`required` forces the caller to WRITE the
  initializer, not to write a non-null value — an explicit null surfaced as an NRE from inside scope
  creation, reported to the client as `UNKNOWN_ERROR`). The ROOT-provider caveat is documented on
  `ConfigureScope` itself: `AddScoped` there behaves as a per-scope SINGLETON, which is the opposite of
  what it means everywhere else in MS DI.
- [x] `WebViewHost.InitializeAsync` idempotence + one whole-sequence budget. DONE — the first call does
  the work and later calls await the same task; a FAILED init clears the cache so a retry is a real
  retry. It used to re-run `WireEventPolicies` on every call, double-subscribing every handler: each
  external link then opened TWICE and the auto-reload raced itself. The `InitTimeout` now covers the
  whole sequence through one linked CTS (each step used to get its own full budget, so "25 s" was
  really 50 s before `ApplySettings`, and script injection — a real browser round-trip — was
  unbounded). `WebViewEnvironment.GetSharedAsync` no longer caches a FAULTED task: `??=` made one
  transient failure terminal for the process, so the retry its own timeout message advises got the
  original exception back without touching WebView2 again.
- [x] A mistyped `ResourcePrefix` degrades to a silent all-404 provider. DONE, **but NOT where the
  review said** — read this before "improving" it. Throwing from the provider's constructor was the
  obvious fix and is wrong: a provider with nothing to serve is legitimate when the page loads from a
  dev URL, which is the normal state of a fresh clone whose bundle has not been built (the sample's own
  csproj documents exactly that). So the provider REPORTS it (`CanServe` + a log notice naming the bad
  prefix and the assembly's actual manifest prefixes), and the loud failure lives in
  `WebViewHost.AssertBundleServable`, which is the only place that knows the bundle IS the start
  document. The probe is `IWebViewResourceProvider.Exists("index.html")` — which also gives that member
  the consumer H6 was going to delete it for.
- [x] Don't put exception text in HTTP response bodies readable by page script. DONE — one constant
  `NotFoundBody` for every 404 and the diagnosis to the host log. Applies to all three sites (bundle
  miss, bundle failure, deferred-scheme handler failure); the last is the worst, since an app scheme
  handler's message is the most likely to carry a real path or a remote URL.
- [x] Cap the renderer auto-reload. DONE — new `MaxAutoReloads` (3) is the TERMINAL state the option's
  own doc already promised ("a crash-looping page must not spin"); rate-limiting alone is not a
  stopping condition, so a page that faults during load reloaded every 10 s forever, burning a browser
  process each time. The give-up is logged EXACTLY once, or the log becomes the new spin. A successful
  navigation resets the count, so a long-running app isn't rationed by unrelated crashes hours apart.
  `AutoReloadCooldown` moved from a public static field on `WebViewHost` to an option (**breaking**).

**H4 — The re-layer, then the dedup collapse (this duplication CAUSED several H1–H3 items)**

DECIDED 2026-07-30 (supersedes the "two internal owners + `InternalsVisibleTo`" idea the review
proposed): the shared owner problem is solved structurally by re-layering. Design:
`docs/2026-07-30-shenora-relayering-design.md`; rationale: **D19** (`Shenora.WebView2` →
`Shenora.WinForms`; the two Windows packages are one layer, boundary = primitives →
hosting-on-primitives) + **D20** (portable contracts + `IUiDispatcher` in `Shenora.Core`, so app
logic compiles with no Windows reference and a future mobile shell can implement the same
contracts). The design-contract §4 rule authorised this revision on exactly this evidence.

- [x] **H4.1 — Land the re-layer (own commit, before the dedup items below).** Take the
  `WebView2 → WinForms` project reference; move the portable contracts to `Shenora.Core`
  (`IClipboardService`, `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`FileDialogFilter`/
  `FileDialogResult` — platform-neutral in signature, but this is a file **SPLIT**: every one of them
  is declared inside its implementation's file, and `FileDialogs.cs` holds six of them plus the
  `FileDialogsOptions` that must stay behind); split the two mixed interfaces into a portable base +
  Windows extension
  (`IUrlLauncher` ← `IShellLauncher`, `IUiInteraction` ← `IFormInteraction`) so one implementation
  still satisfies both; add `IUiDispatcher` to Core plus TWO implementations in `Shenora.WinForms` —
  `WinFormsUiDispatcher(Control)` (explicit/per-control, what WebView2 + Sessions construct) and
  `MainFormUiDispatcher(IFormInteraction)` (the DI singleton, resolving the main form lazily because
  the runner registers it only after the form factory); register the portable faces alongside the
  Windows ones in `UseWinForms`.
  `FileDialogsOptions` (the impl's options, which references `IFormInteraction`) stays put. DO NOT
  move the window-state stack — portable-in-signature is not the bar (see the design's §4.4 guard).
  Namespaces stay flat per package. **DELETE the moved members from the derived interfaces** —
  re-declaring `OpenUrl` on `IShellLauncher` or `BlockInteraction`/`UnblockInteraction` on
  `IFormInteraction` is CS0108, which H5's `TreatWarningsAsErrors` turns into a build error. Then
  review + promote **exactly two** baselines (`Shenora.Core.txt`, `Shenora.WinForms.txt`) — a diff in
  the other three is a SIGNAL, not noise — and add a `### Breaking` CHANGELOG entry.
  **Doc sync in the SAME commit** (four tracked docs assert the old layering and would argue a future
  session back to it): `docs/ARCHITECTURE.md` "Dependency rules … never sideways";
  `docs/REVIEW-GUIDE.md` §5's "the ONE deliberate package-on-package edge"; `README.md`'s package
  table (it ships inside every nupkg; WinForms stops owning the dialog/clipboard/shell contracts);
  `docs/RELEASING.md`'s "the two leaf packages" (WinForms stops being a leaf); plus the design
  contract's §4 table rows. Then the `Shenora.Core`/`Shenora.WinForms` csproj `<Description>`s — the
  "UI-dispatcher seam" claim becomes TRUE here, and WinForms gains the dispatcher implementation.
- [x] **H4.2 — Retire the marshal copies onto `WinFormsUiDispatcher`.** COMPLETE. The sessions copies
  landed with H4.4 as planned (`RenderSession.OnUiAsync`/`OnUiFireAndForget`,
  `SessionController.OnUiAsync`/`PostUi`, `CoBrowseSession.RunOnUiAsync`×2/`RunOnUiFireAndForget`),
  and each closed something real: `RenderSession` now OBSERVES the cancellation tokens it accepts
  (H2's pool-starvation P0 — the dispatcher's `WaitAsync` means the caller escapes even when the UI
  thread never runs the body), `SessionController`'s inverted pre-handle guard is gone (its own
  comment described the trap and the next line committed it), and `CoBrowseSession` uses the
  never-faulting `InvokeOrDefaultAsync` so its "one bad input message must not fault the session"
  contract survives the collapse. Earlier: the six
  outside the sessions package are converted (`FormInteraction.SetEnabled`, `SecondaryWindows.Post`,
  `WebViewIpcBridge.PostJson`, `WebViewHost`'s deferral marshal, `WindowCommandFacade.Post`,
  `DropZoneManager.MarshalToUi`). **The sessions copies land with H4.4**, which rewrites the same
  files anyway — doing them twice would be churn. Two outcomes worth carrying forward:
  (a) **`SplashPanel`'s two self-marshals are deliberately NOT converted** and say so in the code: a
  control marshalling to ITSELF is idiomatic and its pre-handle apply-directly is correct, so the
  honest count is "collapse the service-to-foreign-control copies", not "14 → 1";
  (b) `FormInteraction` keeps applying `Enabled` directly when NotReady — `Control.Enabled` on an
  unrealized control is a stored value, and dropping it would lose the block for a not-yet-shown
  window. Conversion also fixed two live defects: `WindowCommandFacade` used to defer even when
  already on the UI thread (losing `START_DRAG`'s mouse-down timing) and left the posted body
  unguarded (a throwing `ApplyTheme`/`FormClosing` crashed the app); `DropZoneManager` used to run
  `PointToScreen`/`Controls.Add` inline ON A WORKER THREAD pre-handle, which is now a drop-and-log. 14 hand-rolled copies
  across 3 packages with 5 incompatible pre-handle policies — 7 of them (all in Sessions) have no
  guard at all, and `LoginWindowController.cs:250-254` carries a comment explaining the pre-handle
  trap and then commits it on the next line (`if (!_form.IsHandleCreated || !_form.InvokeRequired)
  return work();` runs the WebView2 call INLINE on the calling thread — reachable via the co-browse
  background controller while a driver continuation is off the UI thread). The single owner's
  semantics: `IsDisposed`/`IsHandleCreated` pre-check BEFORE `InvokeRequired` → non-blocking
  `BeginInvoke` + TCS → token observed via `WaitAsync` → guarded body → explicit throw/swallow
  policy → inline only when already on the UI thread. Per-CONTROL, never per-application (Sessions
  marshal to their anchor form; `SecondaryWindows` run their own STA pumps). Note
  `WindowCommandFacade.Post` always defers even when already on the UI thread, which loses
  `START_DRAG`'s mouse-down timing. It makes H2's `RenderSession` unobservable-token P0 MECHANICAL but
  does not close it: `WaitAsync` returns the awaiter, it does not kill the wedged op or release the
  pool's accounting — H2 still owes `OpTimeout` + discard-the-abandoned-instance.
  **THREE SITES KEEP THEIR OWN POLICY** — each was earned in a previous review, and a single bool
  would silently re-break two of them (see the design's §5.4 table): `DropZoneManager.MarshalToUi`
  returns false so the CALLER proceeds inline ("recursed without end" if it re-invokes);
  `SecondaryWindows.Post` must be a pre-handle no-op carrying intent in a flag (posting there "would
  create the handle on the wrong thread and kill the pump"); `SplashPanel` applies directly
  pre-handle. `CoBrowseSession`'s input/hotspot paths must not fault the session — they use the
  never-faulting `InvokeOrDefaultAsync` overload. This is why the contract is three-state
  (`NotReady`/`Ready`/`Gone`) plus `IsOnUiThread`, not one bool.
- [x] **H4.3 — The portability proof.** A `net10.0` project `samples/Shenora.Sample.Logic` with one
  facade that picks a file, reads the clipboard and opens a URL, referenced by the desktop sample.
  Compiles with no Windows reference = the seam is real; a Windows type later dragged into a contract
  turns it red. Without this, portability is asserted rather than enforced. (~30 lines.) TWO
  conditions or it proves nothing: it must inject **`IUrlLauncher`**, not `IShellLauncher` (today's
  `SampleFacade` injects the Windows extension, so the facade gets SPLIT — portable routes out,
  reveal-in-Explorer and secondary windows stay in the desktop sample); and it must be added to
  `Shenora.slnx` — a SECOND solution edit after H5's, or `verify` never compiles the proof.
- [x] **H4.4 — Make the declared `Sessions → Shenora.WebView2` edge actually carry something.** DONE,
  with a scoping judgement worth reading: what crossed the edge is the **invariant**
  (`BrowserArguments.Compose` — single-occurrence feature switches + the dev CDP re-append), not the
  whole app-shell preset. The session ARGUMENT preset, the EVENT policies and the environment caching
  legitimately differ from `WebViewHost`'s: an app shell opens external links in the system browser
  while an unattended session must open nothing, and one shared app environment is not the same thing
  as one environment per profile. Sharing those would have been coupling, not dedup. Also landed here:
  the three missing policies (`NewWindowRequested` suppressed, `PermissionRequested` denied,
  `ProcessFailed` surfaced → the pool poisons the instance, co-browse completes its frame channel),
  script dialogs disabled, and the `Log` options (H4.7). The last piece — one cached environment per
  profile (H2's "each retry orphans another browser process") — LANDED with the H2 sessions pass as the
  internal `SessionEnvironmentCache`, and the shape it took is the interesting part: **owner-scoped
  (the pool holds one), never static/profile-keyed.** A live environment keeps its profile's browser
  process and therefore the folder's OS lock alive, so a process-lifetime cache would have made
  `LoginWindow.ClearProfile` — the call that makes a logout a REAL logout — fail every time instead of
  only while a window is open. A login window opens one profile once and gains nothing from caching; a
  pool creates N instances on ONE profile, which is the case that does. Owner scoping also makes it
  single-threaded by construction, which matters because `CoreWebView2Environment` is thread-affine. It
  reuses an IN-FLIGHT creation (that is the anti-orphan half: `InitTimeout` abandons the await, never
  the `CreateAsync`) and deliberately does NOT cache a faulted/cancelled task — the trap
  `WebViewEnvironment` still has, still listed under H3. With
  D19 the answer is settled: route Sessions through the edge (the alternative — dropping the
  reference — is off the table now that the layering is deliberate). VERIFIED:
  the `ProjectReference` exists (`Shenora.WebView2.Sessions.csproj:15`) and NO file in the package
  imports `Shenora.WebView2` or uses one of its types. Consequences: `SessionBrowser`
  re-implements browser-argument building (`:66-93` vs `BrowserArguments.Build` — and the rewrite
  re-introduces the CDP gotcha from `windows-dev-gotchas.md`: setting `AdditionalBrowserArguments`
  makes WebView2 ignore `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, so a dev session browser silently
  gets no debug port; it also appends caller `--disable-features` raw, reproducing the exact
  last-occurrence-wins bug `BrowserArguments` documents in capitals), environment creation (hence
  one env per instance instead of one cached per profile — and the init timeout abandons
  `CreateAsync`, so each retry against a locked profile orphans another browser process, adding to
  the lock its own error message blames), the init-timeout guard + message (vs
  `WebViewHost.InitializeAsync:51-75`), and settings hardening (vs `ApplySettings:108-131`, whose
  list is MORE complete). Above all: the three policies `extraction-sources.md` lists as "fix during
  the port" — `NewWindowRequested` / `PermissionRequested` / `ProcessFailed` — exist in
  `WireEventPolicies` and are ENTIRELY ABSENT for pooled/co-browse instances, so `window.open()` on
  a pooled page opens a real visible popup, a permission prompt stalls an invisible page, and a
  renderer crash is invisible to the pool (and leaves `CoBrowseSession.Frames` waiting forever).
  Wiring `ProcessFailed` is also what lets the pool poison an instance and co-browse complete its
  channel. Either route Sessions through the edge or drop the reference and stop claiming it.
- [x] **H4.5 —** DONE. Collapsed: the IPC error boundary → one `IpcErrorMapping.ToErrorResponse` (it
  was four byte-identical copies of the kit's most load-bearing invariant — the fifth copy is how a
  raw `ex.Message` eventually reaches a client); `Done()` + `UnknownType(request)` → `BaseFacade`
  (three private copies, one of them in the SAMPLE, which is the tell it was consumer-facing);
  `WebViewHost`'s open-a-URL → `IUrlLauncher` (a drifted copy of `ShellLauncher.OpenUrl` that was
  missing the Win11 handle `Dispose`); four bring-to-front variants → one internal
  `WindowActivation.BringToFront` (the tray copy omitted `SetForegroundWindow`, so restoring from the
  tray behind another app could leave the window hidden); three `DeviceDpi / 96` sites → `DpiHelper`
  (one used integer division, none guarded a non-positive DPI); the off-screen park coordinate → one
  const + `OffscreenWindow.IsParked` (a THIRD site inferred on-screen-ness from a DIFFERENT threshold,
  so moving the park position would have silently broken reveal detection); the window-state
  apply/save pair → `WindowStateManager.AttachTo` (the ordering IS the contract); `CookieLoginFlow`'s
  private `JsonSerializerOptions` → `IpcJson.Options`. NOT collapsed, deliberately: `WebViewScripts`'
  own options (it must NOT omit nulls, unlike the wire serializer — noted at the site), and the npm
  `randomUUID`/debounce helpers (trivial, and the npm package has no shared-internals home yet —
  folded into H7). Original list —
  Collapse the remaining duplicates, each to a named owner. **Visibility rule (from
  D19/D20): a helper whose consumer is ANOTHER PACKAGE is public, not `internal`** — a
  `ProjectReference` does not grant internal access, and `InternalsVisibleTo` is granted only to
  `Shenora.Tests`. That corrects three prescriptions below whose `internal` owner could not serve the
  copy it was meant to retire (`MapException` — one copy is in `Shenora.WebView2`; the
  bring-to-front helper — a fourth copy is outside `Shenora.WinForms`), and it makes the "the two
  cross-package `DeviceDpi / 96` sites can't reach `DpiHelper`" premise FALSE once H4.1 lands — they
  can, so collapse them too. Also add here: `WebViewHost`'s copy of open-a-URL-in-the-shell should
  delegate to `IUrlLauncher` rather than keep the handle-leak fix H1 applies to it. The duplicates: the IPC error-boundary
  `catch (OperationException)/catch (Exception)` pair — 4 copies (`MessageDispatcher.cs:62-73,215-226`,
  `BaseFacade.cs:39-50`, partial at `WebViewIpcBridge.cs:239-248`) of the single most load-bearing
  invariant in the kit → one `internal static MapException(...)` in `Shenora.Ipc` (two of the four
  are deliberate belt-and-braces; keep that, share the body); facade boilerplate `Done()` +
  the unknown-type terminator → two `protected` helpers on `BaseFacade` (the sample retypes both at
  `SampleFacade.cs:61-62`, which is the tell that it is consumer-facing); 4 copies of
  `DeviceDpi / 96` → `DpiHelper.ScaleFromDeviceDpi` (the copy at `OptimizedForm.cs:313` is in
  `DpiHelper`'s OWN package and uses integer division; the two cross-package ones can't reach it —
  add the `> 0` guard there); 4 "bring a window to the front" variants, 3 of them in one package
  (`WinFormsHost.cs:228-238`, `SecondaryWindows.cs:151-157`, `TrayIcon.cs:129-137` — the tray one
  omits `SetForegroundWindow`, which is why restoring from the tray can leave the window behind
  everything) → one internal `Activate(Form)`; the off-screen park coordinate triplicated with a
  MISMATCHED threshold (`OffscreenWindow.cs:19` and `LoginWindow.cs:218` use `-32000`;
  `LoginWindowController.cs:51` infers on-screen-ness from `> -30000`) → an internal const +
  `IsParked(Form)`; private `JsonSerializerOptions` copies (`WebViewScripts.cs:16-19`,
  `CookieLoginFlow.cs:65`) — the exact drift `IpcJson`'s own doc says it exists to prevent (note
  `IpcJson.Options` omits nulls, so `WebViewScripts` may need to stay separate WITH a comment saying
  why); the window-state attach triple (`WinFormsHost.cs:175-177` = `SecondaryWindows.cs:99-101`) →
  `WindowStateManager.AttachTo(form)`; `randomUUID`-with-fallback and the debounce helper in
  `@shenora/react` (`bridge.ts:54-58`, `useDropZone.ts:38-41,48-56`) → internal utils.
- [x] **H4.6 —** DONE as a RENAME, not a base extraction: `LoginWindowController` → `SessionController`
  (21 occurrences across 5 source files + the living docs; historical `ROADMAP`/`FIX-LOG` entries left
  intact because they were true when written, with the mapping noted in the new entry). That is what
  actually fixed the reported surface problem — `CoBrowseSession.Controller` is public and was typed
  with a login-named type, so a co-browse consumer had to program against "Login…". The login-specific
  types keep their names. The base extraction below is DEFERRED to H9 on purpose: the neutral name was
  the surface fix, and what the shared core should be is better decided while reshaping the co-browse
  API (D21) than guessed now. Original note kept for context —
  Consider one honest shared base for the three session types (`RenderSession` /
  `LoginWindowController` / `CoBrowseSession` share browser + host window + guarded navigate +
  script + taps + marshal). This is also the clean route to the deferred session-neutral rename: a
  neutral base with the login-flavoured type as the foreground subclass. Judgement call — only do it
  if the shared core is real after H4's earlier items land.
- [x] **H4.7 —** Add the missing `ILogger<T>?` + `NullLogger` convention to `Shenora.WebView2.Sessions` — it
  has ZERO logging of any kind against ~30 silent `catch { }` blocks, so a wedged pool or a failing
  request filter is undiagnosable in production. (`ILogger` is reachable transitively.) While there,
  reconcile the two logging conventions in `Shenora.WebView2` (11 sites use `ILogger`, 4 use an
  `Action<string>? Log` option, so one package uses both).

**H5 — Close the gate holes (near-zero churn, highest payoff per edit)**

- [x] **`Shenora.slnx` has an EMPTY `<Folder Name="/samples/" />`** and doesn't list
  `Shenora.Core` either. `dev.mjs build` builds only `config.solution`, so **`verify` — the
  documented "am I done?" gate — never compiles the reference composition or the e2e subject**; the
  sample can be red while `verify` and the release workflow pass green. `samples/Shenora.Sample.Web`
  is likewise never type-checked (its `typecheck`/`build` scripts are called by nothing). FIX: add
  the sample projects + `Shenora.Core` to the solution; add the web typecheck to `verify`.
- [x] **`dev.mjs test <typo>` exits 0 having run nothing** (`:93-101` — no else branch, `ok` stays
  `true`). Add the else and fail loudly.
- [x] **`check-sensitive.mjs` fails OPEN on a fresh clone and in CI.** `:33-42` prints a notice and
  continues with only the two structural built-ins when `local/sensitive-patterns.txt` is absent —
  and `local/` is gitignored, so **the brand/sibling-name half of the guard never runs in the
  release gate**. Three further misses: renamed/copied staged files are skipped entirely
  (`--diff-filter=ACM`, but `git mv` stages as `R`); file PATHS are never matched, only content (a
  file *named* after a banned token passes); `--tree` reads the working tree, so an
  already-committed leak edited away locally reports clean. There is also no `commit-msg` hook, while
  the release workflow pipes commit subjects into public release notes. FIX: exit non-zero when the
  pattern file is missing (or require an explicit `--allow-builtins-only`), match paths too, include
  `R`/`C` status, and add a `commit-msg` hook.
- [x] Turn on `TreatWarningsAsErrors` in `src/Directory.Build.props` and drop `-clp:ErrorsOnly`
  from `dev.mjs:86,128-129` — nothing in the repo makes warnings errors and the gate hides them
  (CS1591 is additionally silenced, so missing XML docs ship to the nupkg unnoticed).
- [x] Make `verify` run `doctor` (today version/README drift doesn't fail the gate; it self-heals
  only because `pack` runs `doctor --fix` first, meaning verify scans the PRE-sync files). Also add
  a `prepublishOnly` guard for the npm package — the release workflow re-packs from source and a
  stale/missing `dist/` would ship a README-only package with no error.
- [x] `release.yml` creates the GitHub release — and therefore the TAG — even when
  `create_tag: false` (only the tag STEP is gated), so the tag can be created at the default-branch
  head, pointing at a different commit than the one published.
- [x] Set `ManagePackageVersionsCentrally` in the root `Directory.Packages.props` shim (it's
  hand-set 3× today and missing from both devtools csprojs); add `CodePage=65001` to the two
  devtools csprojs (both contain non-ASCII string literals and `src/Directory.Build.props:20-21`
  documents that exact mojibake failure on this machine); drop the unused
  `M.E.DependencyInjection.Abstractions` pin and the redundant `Microsoft.Web.WebView2` re-declaration
  in `Shenora.WebView2.Sessions.csproj:16`; reconsider shipping `InternalsVisibleTo Shenora.Tests`
  in all five nupkgs on unsigned assemblies.
  **DONE except two DELIBERATE keeps:** (a) the `Microsoft.Web.WebView2` reference in
  `Shenora.WebView2.Sessions.csproj` STAYS — the package uses WebView2 types directly, and an
  explicit direct reference is better practice than relying on a transitive one arriving through
  `Shenora.WebView2`; the duplicate nuspec entry is harmless. (b) `InternalsVisibleTo Shenora.Tests`
  stays for now: removing it needs the test project to stop using internal seams (a large change),
  and the exposure is bounded to an assembly deliberately named `Shenora.Tests`. Revisit at P7 with
  signing. Also done here: `prepublishOnly` on the npm package, so a stale/missing `dist/` can't ship.

**H6 — Public surface + cross-language lockstep (cheapest window is BEFORE 1.0)**

- [x] **Extend the API baseline to `protected` members.** DONE, and much further: the one-line
  `GetMembers(BindingFlags.Public)` dump became `ApiSurfaceDump`, which renders protected members
  (`BaseFacade.RouteMessageAsync` — the member EVERY consumer overrides — was entirely ungated),
  default parameter values (dropping a `= null` is a source break for every caller and showed NO diff),
  `init` vs `set`, `required`, `static`, `virtual`/`abstract`/`override`, parameter names (named
  arguments are a source contract), generic constraints, nullability, base types + directly-implemented
  interfaces, const VALUES (a wire code is what consumers compare against), and attributes — all 22
  `[JsonPropertyName]` wire names are now pinned, so renaming one can no longer break the C#⇄TS mirror
  silently. Accessors render as `{ get; init; }` rather than separate `get_X()` lines: shorter AND
  strictly more informative. Three rendering decisions are documented in the file because they were
  wrong on the first attempt: an unconstrained `T` reads as Nullable at runtime and must NOT be
  annotated; the compiler's `[Obsolete]` parameterless-ctor stub on a `required` type is filtered (its
  message is SDK-version-dependent and would churn); C# aliases (`void`, `string`) are used because a
  human reviews this file on every change. The assembly list now comes from the baseline DIRECTORY, plus
  a new `Every_shipped_assembly_has_a_baseline` walking transitive references to close the other
  direction (deriving from the directory alone would leave a new package silently ungated).
- [x] **Add the cross-language mirror tripwire and the missing code.** DONE — `scopeRequired` added to
  `types.ts`, plus `WireMirrorTests`, which parses the TS SOURCE (what an adopter imports; a generated
  artifact would add another place to diverge) and asserts set equality of the error codes, the
  handshake route, and the envelope categories. Client-ONLY codes are excluded via a new exported
  `ClientOnlyIpcErrorCodes` so the client DECLARES its own exceptions rather than the test carrying a
  second hard-coded list. The parser self-checks (`Assert.NotEmpty`) so a regex that silently matched
  nothing can't make it pass, and I verified the tripwire FAILS by temporarily removing the code —
  message: "The host emits these codes but the client cannot name them: SCOPE_REQUIRED".
- [x] **`'\0'`-join the client event-bus keys and add a scope filter.** DONE, with the host's exact
  scope rule mirrored — including the half that is easy to miss: a global (scope-less) event still
  reaches a SCOPED subscriber, so an app-wide announcement isn't swallowed by a per-scope listener.
  `useShenoraEvent` takes `scope` through. Three tests pin the scope semantics and one pins the
  collision (`("APP","TASK.DONE")` vs `("APP.TASK","DONE")` were the same key).
- [x] **Fix `BaseModuleService`'s generic constraint.** DONE — `TRequests extends object`, and the
  `extends Record<…>` dropped from both callers (`windowCommands.ts` was demonstrating the very
  anti-pattern the base class exists to prevent). **This uncovered a bigger hole:** the tests were not
  type-checked by ANYTHING — `build` uses `tsconfig.build.json` which excludes them, vitest transpiles
  without checking, and the `tsconfig.json` written to do the job had never been run (it was red on an
  ES2020 `lib` vs `.at()`). So `@ts-expect-error` assertions were inert. Fixed the lib, added a
  `typecheck` script, and wired it into `dev.mjs verify` — then proved it works by reintroducing the
  anti-pattern and watching TS2578 fire.
- [x] **Give form-dependent facades a first-class registration seam.** DONE — and NOT by either option
  the review listed. The recommendation (facades resolve the form lazily via `IFormInteraction` and
  register through `AddModuleFacade`) does not actually work for two of the three modules: `DropZoneFacade`
  needs the live `DropZoneManager`, which needs the WebView2 control, and the RENDER route closes over the
  form's session pool — neither is resolvable from DI before the form exists. And widening
  `IMessageDispatcher` with the whole `Map*`/`Use*` family was rejected as too large.
  What shipped is smaller than either: **`Use(MessageMiddleware)` — the ONE primitive every helper
  already delegated to — moved onto the interface, and the six helpers became extension methods over it**
  (`MessageDispatcherExtensions`). So the interface stays at the four things a dispatcher genuinely is
  (dispatch, two sends, compose), a decorator has four members to write instead of ten, and every helper
  works on any implementation. The sample's `if (dispatcher is MessageDispatcher concrete)` — which had no
  `else` — is gone. `AddMessageDispatcher`'s configure callback now receives the INTERFACE, since taking
  the concrete type would have kept propagating the downcast. Two tests pin it: late mapping through the
  interface, and a pass-through decorator (the exact shape that used to make three modules vanish).
  `MessageDispatcher.Use` is declared twice on purpose — C# forbids a covariant return when implementing
  an interface, so the explicit impl returns `IMessageDispatcher` while the public one keeps the concrete
  type for existing fluent chains. Also fixed the `WindowCommandFacade` doc, which pointed at
  `AddMessageDispatcher`'s callback — a path that CANNOT work, since it runs before any form exists.
  Original recommendation kept for context: The reference composition has
  to downcast — `MainForm.cs:85` `if (dispatcher is MessageDispatcher concrete)` — because
  `IMessageDispatcher` exposes only `DispatchAsync`/`SendAsync`, and `WindowCommandFacade.cs:41-43`
  documents a path (`AddMessageDispatcher`'s configure callback) that CANNOT work: that callback runs
  at provider-build time, before the form exists. The `if` has no `else`, so a different
  `IMessageDispatcher` registration (or a future decorator) silently drops WINDOW + DROP_ZONE +
  RENDER and the frameless title bar just stops working. RECOMMENDED: have those facades resolve the
  main form lazily via the existing `IFormInteraction` so they register as ordinary DI facades
  through `AddModuleFacade` (smaller surface change than widening the interface). Fix the
  `WindowCommandFacade` doc either way.
- [~] Trim surface that doesn't earn its keep, and add what's missing. **The CORRECTNESS half is DONE
  (2026-07-30)** — these were bugs behind surface items, so they went first:
  - `MessageDispatcher.Use()`/`_middlewares`: the `Lazy` + `List<T>` swap was unsynchronized, so a
    concurrent dispatch could read the OLD cached pipeline and answer `NO_HANDLER` for a route that was
    already registered, and a build enumerating the list while `Add` grew it was a plain data race. Now
    copy-on-write array + volatile pipeline + invalidate-and-rebuild under one lock. Regression test
    hammers 200 late `UseRoute` calls against continuous dispatch.
  - `IpcErrorCodes.OperationCancelled` + the `catch (OperationCanceledException)` arm in
    `IpcErrorMapping`, placed AFTER `OperationException` so an app that models cancellation in its own
    words keeps them. Mirrored to `types.ts` (the H6.2 tripwire enforced that automatically — it works).
  - `IpcResponse.CreateError`'s argument order now matches `OperationException`'s: `code`,
    `parameters`, `message`. The shared order puts the WIRE-relevant piece first (`parameters` crosses;
    `message` is host-log only). Every in-repo call site already used `parameters:` named, and a
    positional third argument now fails to compile rather than binding to the wrong thing.
  - `EventBus`: the convenience `EmitAsync` overload guards module/type (it used to build a message that
    could never match any subscription — a silently undeliverable event); and `SubscribeCore` publishes
    `_patterns` LAST, since that is what `EmitAsync` enumerates — so its `continue` can now only mean
    "concurrently unsubscribed", as its comment always claimed.
  - `ScopedContainerRouter.HandleAsync` retries ONCE on `ObjectDisposedException` (guarded on
    `!_disposed` so a router shutting down can't spin): `InvalidateScope` is a documented app-facing call
    that can fire mid-request, and the race used to surface as `UNKNOWN_ERROR` instead of just using the
    rebuilt scope.
  - `ShenoraPathsOptions` is a `record`, and the `--app-root` merge uses `with` — it hand-copied six
    properties, so a seventh option would have been silently dropped whenever that flag was passed.
  - `BaseFacade`'s lone `ConfigureAwait(false)` REMOVED: it was the only one in the dispatch path and it
    contradicted the documented context-preserving model, discarding the very context a WINDOW facade
    needs. It survived only because every in-repo facade marshals internally anyway.
  **The TRIM half is now done too (2026-07-30):**
  - `DpiHelper.ScalePixels`/`ScaleSize`/`ScalePoint` REMOVED (zero callers). Worth noting WHY they were
    worse than merely unused: each hardcoded the PRIMARY monitor's scale, so anything that adopted them
    would silently mis-scale on a secondary monitor. `Scale` + the DPI you actually mean replaces them,
    and the consumer their own docs named (the drop-zone overlay) already converts from the control's
    `DeviceDpi`, which is the correct source.
  - npm: the `declare global { Window.chrome }` augmentation is GONE (it collided with `@types/chrome` as
    an unfixable TS2717 in a `.d.ts` the consumer doesn't own — a library must not claim global names; a
    local interface + one cast replaces it); `"./package.json"` added to `exports`; and the tarball now
    ships the LICENSE, with `doctor` checking it byte-matches the root one (verified by breaking it).
  **STILL OPEN, all deliberately deferred:** `SessionBrowser`'s public statics → internal WITH the
  H2-deferred `CancellationToken`; `CoBrowseSession`'s two token-less async members (H9 reshapes both
  signatures — doing it here would mean changing them twice); bridging Sessions' `LoginErrorCodes` into
  the IPC contract (same reason — H9 owns that vocabulary); duplicate `ModuleName` rejection in the EAGER
  `MapRegisteredModules` (the lazy path, which is what `AddMessageDispatcher` uses, already rejects them);
  and `EventMessage<T>` as an alias of `IpcNotification<T>`.
  **NOTE `IWebViewResourceProvider.Exists` must NOT be removed** — H3 gave it a real consumer (the
  startup bundle sanity check), which is the option the review offered as the alternative.
  Original list: `DpiHelper.ScalePixels`/
  `ScaleSize`/`ScalePoint` have ZERO callers and their documented consumer (the drop-zone overlay)
  architecturally cannot reach them; `IWebViewResourceProvider.Exists` is never called in `src/` or
  `samples/` (every implementor pays for it) — remove it or use it for a startup sanity check on
  `index.html`, which would also catch the wrong-prefix case in H3; `SessionBrowser`'s public statics
  take a raw WinForms `WebView2` and have no consumer scenario (they also invite bypassing the
  pool's accounting) — internal until an adopter proves otherwise; `CoBrowseSession.DispatchInputAsync`
  and `ReadHotspotsAsync` are the only async members in the package with NO `CancellationToken` and
  both can block indefinitely on a wedged renderer (adding the parameter after 1.0 is binary-breaking);
  align the argument order of the two sibling error constructors — `IpcResponse.CreateError(code,
  message, parameters)` vs `OperationException(code, parameters, message)` (breaking after 1.0);
  add `IpcErrorCodes.OperationCancelled` + a `catch (OperationCanceledException)` arm so cancellation
  stops surfacing as `UNKNOWN_ERROR` (the sample already hand-rolls the workaround at
  `MainForm.cs:107`); bridge Sessions' parallel error vocabulary (`LoginErrorCodes` strings on a DTO
  with no `ToError()`) into the IPC contract so every adopting app stops rewriting
  `MainForm.cs:104-119`; guard `Use()`/`_middlewares` (`MessageDispatcher.cs:138-143`) — late
  mapping is a SUPPORTED, documented pattern and the `List<T>` + `Lazy` swap is unsynchronized, so a
  concurrent dispatch can see the old pipeline and answer `NO_HANDLER` for a registered route;
  reject duplicate `ModuleName`s in `MapRegisteredModules` (today the second facade's whole route
  table is silently unreachable); mirror `EventBus`'s null/empty guards into the convenience
  `EmitAsync` overload; publish `_patterns` LAST in `SubscribeCore` (`:63-65`) so `EmitAsync`'s
  `continue` can only mean "concurrently unsubscribed", as its comment claims; retry once on
  `ObjectDisposedException` in `ScopedContainerRouter.HandleAsync` (a scope invalidated while a
  request is in flight currently fails as `UNKNOWN_ERROR` instead of rebuilding); drop the
  `declare global { Window.chrome }` augmentation the npm package ships (`transport.ts:8-12` — it
  collides with `@types/chrome` in a consumer's program, an unfixable TS2717 in a `.d.ts` they don't
  own); make `EventMessage<T>` an alias of the structurally identical `IpcNotification<T>`; add
  `"./package.json"` to `exports` and a `LICENSE` to the published tarball (the manifest declares
  MIT while shipping no license text); make `ShenoraPathsOptions` a `record` so the `--app-root`
  merge stops hand-copying six fields (`ShenoraApplication.cs:94-102` — a seventh option would be
  silently dropped); document or remove `BaseFacade`'s lone `ConfigureAwait(false)` (`:36`), which
  contradicts the dispatcher's documented context-preserving model.

**H7 — Tests, docs and dead weight**

- [x] **Test-suite health — DONE (2026-07-30).** The suite is **442 dotnet + 63 vitest**, and the
  parallelization item turned out to be hiding a real defect rather than only a flake risk.
  - **`xunit.runner.json` with `parallelizeTestCollections: false`**, whole suite, NOT per-class
    `[Collection]` — decided on measurement, not taste: parallel 6 s but masking the hang below;
    serial-with-hang 28 s then 1 m 6 s (wildly variable); serial once fixed a steady **9–10 s**. Serial
    is also self-maintaining (a new pump test needs no attribute) and it is what SURFACED the defect.
    Declared explicitly as `<None CopyToOutputDirectory>` in the csproj: xunit's auto-include glob did
    NOT copy the file, and a runner config the runner ignores is worse than none.
  - **THE FIND: `WindowCommandFacadeTests`' `START_RESIZE` case entered the OS modal size loop ON THE
    TEST THREAD** — 16.9 s of the suite's 26.8 s, and an indefinite hang when run alone. H4.2 made
    `WinFormsUiDispatcher.Post` run a body INLINE when already on the UI thread (correct: the loop must
    start while the mouse button is down), and the test creates its form on the test thread — so
    `SendMessage(WM_NCLBUTTONDOWN)` ran synchronously. Its own "deliberately NOT pumped" comment had
    been false since H4.2, and collection parallelism kept the wall clock at 6 s so nobody saw it.
    Test-only fix (production behaviour is right): dispatch via `Task.Run` so `InvokeRequired` is true
    and the body is queued to something the test never pumps. `WindowCommandFacade.Post`'s doc now
    records the accepted consequence — those two routes answer only after the user releases the mouse.
  - **Doubles collapsed, each to a SUPERSET of what it replaced** (which is why nothing regressed):
    `TestSupport/Sta.cs` (3 remaining `RunSta` copies; one spelling everywhere now — the copies had
    `ExceptionDispatchInfo` but a bare unbounded `Join()`, the shared one has both that and the 30 s
    bound); `TestSupport/FakeWindowStateStore.cs` (3 fakes — seed and assertion target are deliberately
    SEPARATE members, since `MemoryStore` used one field for both and read as a round-trip guarantee it
    never made); `TestSupport/IpcRequests.cs` (5 factories, 4 signatures — the part worth one owner is
    the `Payload` null-means-absent ternary); `TestSupport/TempDir.cs` (all 7 create/delete pairs —
    cleanup is BEST-EFFORT because four copies had a bare `Directory.Delete` in `finally`, so a locked
    file threw FROM the finally and replaced the test's real failure with an unrelated IO error).
    Two `SetApartmentState` sites REMAIN deliberately: the long-lived never-pumped anchor threads in
    `RenderSessionPoolTests` and `WinFormsUiDispatcherTests` are not this shape.
  - **npm:** `vitest.config.ts` + `vitest.setup.ts` — `globals` stays FALSE (the tests import
    `describe/it/expect` explicitly, the better habit), so RTL's `cleanup` is registered EXPLICITLY in
    `setupFiles` rather than bought as a side effect of turning globals on; the setup guards on
    `typeof document` and dynamically imports, because the environment is per test FILE and four suites
    run in node. Evidence it took effect: vitest's `setup` went `0 ms` → `1.26 s`. One shared
    `src/testing/fakeTransport.ts` replaced 4 classes + 2 inline literals and builds replies from the
    exported `IpcCategories` (all four hand-wrote `{ category: 'ipc' }`, so they could have drifted from
    the wire contract together and stayed green); remaining literals converted too.
    **`src/testing/` is EXCLUDED in `tsconfig.build.json`** or it compiles into `dist/` and
    `files: ["dist"]` publishes it — the old exclude covered only `*.test.ts`. Backed by a new `doctor`
    check that fails when `dist/testing/` exists, proven by breaking the exclusion (the build really did
    emit `dist/testing/fakeTransport.js`).
  - **Barrel gated** (`index.test.ts`, 21 runtime exports as an explicit SORTED ARRAY, not a snapshot —
    a snapshot self-updates under `-u` and a reviewer never sees the removal) + a no-undefined-bindings
    check. **`createWebView2Transport` covered** (5 tests: null with no host / no `chrome.webview`,
    verbatim post, the `typeof event.data === 'string'` filter, unsubscribe detaching) — it had ZERO
    references while being the transport every real consumer runs on.
  - **Untested seams — one filled, the rest bounded HONESTLY.** `SessionBrowserOptions.RequestFilter`
    (the item with the `about:blank` bug on record) is now covered by 15 tests: its decision was lifted
    out of the `WebResourceRequested` lambda into `internal SessionBrowser.ShouldBlockRequest`, the same
    "make the REAL path testable" move as the pool's reset probe. Sabotage-verified. **The rest are
    e2e/manual BY CONSTRUCTION, not by neglect, and `docs/REVIEW-GUIDE.md` §6 now says so:**
    `SessionController`'s constructor subscribes to `_web.CoreWebView2.WebMessageReceived`, so the type
    cannot be INSTANTIATED without a live browser — which covers its public members (bar
    `ComputeFitSize`, tested), `CoBrowseSession.DispatchInputAsync`/`ReadHotspotsAsync`/`Frames`/
    `DisposeAsync`, `RenderSession`'s tap bookkeeping (its disposal checks ARE tested), and
    `CookieLoginFlow`'s 4-line controller→`Hooks` mapping (the poll/capture logic is covered through the
    internal `Hooks` overload, 8 cases).
  - **Implementation-detail assertions relaxed to their actual invariants**, all four: the exact
    exception-message sentence in `PayloadHelperTests` → contains the key AND leaks neither the raw
    value, the CLR type nor the JSON path; `TrayIconTests`' internal type NAME → the renderer's
    `ColorTable` really carries the app's colours (the old test would have passed a renderer that
    ignored every colour it was handed); `SplashPanelTests`' `Controls[0].Controls[0]` → named
    `internal ContentPanel`/`Bar` accessors, with layout expectations DERIVED from
    `SplashPanelOptions` instead of retyping its defaults; the exact STJ digit padding
    (`"deviceScaleFactor":1.50`) → no comma-decimal plus a parsed value, so changing the format string
    no longer fails a *culture* test. Both loosened assertions were sabotage-verified to still catch a
    real break.
- [x] **Docs drift — DONE (2026-07-30), and the list was ~80% STALE.** Earlier batches had already
  fixed: `README.md` + `Shenora.Core.csproj`'s Microsoft.Extensions claim (now "DI (implementation) +
  logging abstractions", matching the actual references) and its UI-dispatcher seam (H4.1 made it TRUE);
  `Shenora.WinForms.csproj`'s "drag-drop overlays" (gone) and "UI-thread dispatcher" (now true);
  `README.md`'s bridge-API row; `ROADMAP` `## Remaining` P1; `CHANGELOG`'s missing `0776f37` and missing
  `### Fixed` and the "newest first" contradiction; both packable-project counts; `CLAUDE.md`'s D-range;
  `rclick`/`move`/`drag` (documented in dev.mjs's header AND its usage line); ARCHITECTURE's
  `WindowCommandOptions` naming, its cache-header attribution, and its test-project reference count
  (it already said "the four leaf src projects (Core transitively)", which is correct).
  **The four GENUINE items, now fixed:** (a) `docs/ARCHITECTURE.md` never listed
  **`Shenora.Sample.Logic`** — the H4.3 portability proof — now in the tree with why it exists; (b) it
  named **none of the FIVE public extension classes** (the list said four; H6's
  `MessageDispatcherExtensions` made it five) — all five now named at their methods; (c) `CHANGELOG.md`
  had **TWO separate `### Breaking` groups** under one `## Unreleased`, merged in landing order, with
  the header now stating each `###` heading appears at most once per version — worse than untidy, since
  that heading is the SemVer gate and a reader would have missed five entries; (d) **not on the list at
  all** — `.claude/knowledge/ipc-contracts.md` still said the ready gate re-closes on
  `NavigationStarting`, which H3 changed to `ContentLoading` + `ProcessFailed`. `docs/REVIEW-GUIDE.md`
  §6 was stale too (it claimed protected members were ungated, which H6 fixed, and cited 318/39).
- [x] **Dead weight — DONE (2026-07-30).** `grep TODO src/` is now EMPTY: `'TODO'` was the example
  module name in SHIPPED npm docs (`moduleService.ts`, `devInterceptor.ts`, the npm README), which reads
  as an unfinished-work marker, so the whole example domain was renamed `Todo*` → `Note*` / `'NOTES'`
  across the README, both source docs and two test files. Stale comments fixed: `IShenoraModule` now
  explains that facades register HERE and the dispatcher maps them (so its one member is deliberate, not
  a placeholder) instead of promising later phases; `SessionBrowserOptions` lost "once it ships" about
  `LoginWindow`. The sample's `dropClassName: 'drop-hover'` finally HAS a rule (in `index.html`'s
  existing `<style>` — the sample has no CSS file), so the e2e subject can demonstrate the HOVER half of
  the drop contract; and `void getBridge().notifyReady()` became a real `.catch`, because an unhandled
  rejection in a WebView2 page is a silent console error and this is the snippet adopters copy.
- [x] **Documented the `notifyReady` → `ClearAll` ordering contract** (2026-07-30). Verified on the
  tree first: `ClearAll()` really is called from the sample's `OnClientReady`, and the method's own doc
  already said the handshake calls it — so a `REGISTER` arriving before `READY` is wiped AFTER BEING
  ACKED, leaving the client believing its zone is live with nothing logged on either side. Written at
  FOUR sites, because a contract this sharp gets missed when it lives in one doc comment:
  `ShenoraBridge.notifyReady` (+ the "don't `void` this promise" note),
  `UseDropZoneOptions`, `DropZoneManager.ClearAll`, and the npm README's copy-paste snippet — plus a
  bullet in `.claude/knowledge/ipc-contracts.md`. The sample's `useEffect` now says it must stay ABOVE
  the `useDropZone` call, since effects inside one component run in declaration order.
  **The "or make it order-independent" half landed in P5.6:** `DropZoneManager` clears on
  `ContentLoading` now, which removed this contract and all four documentation sites with it.

**H8 — Capture the earned invariants (do as the batches land, not after)**

**EXTEND existing rules; do NOT add a file per invariant** (the rule set must not sprawl — mapping
verified against every rule file). Several were landed EARLY, ahead of the code, because a stale rule
would have argued a future session back to the pre-D19 position:

- [x] DONE ahead of the work: the ONE marshal owner + token observance + guarded body + per-control
  (`webview2-hosting.md`); the D19/D20 placement law + "cross-package kit consumption justifies
  public" (`generic-library.md`); "a declared edge nothing crosses is a duplication smell" +
  layer-decides-the-home (`extraction-sources.md`); the unguarded OUTGOING serialize + "a DI
  singleton factory must never enumerate the provider it is building" (`ipc-contracts.md`); the
  known gate holes (`phase-workflow.md` + `CLAUDE.md`) and the guard's real coverage
  (`sensitive-info.md`); the five missed hunt classes + `FIX-LOG`/`REVIEW-GUIDE` doc-sync
  (`phase-review` skill); the router's two blind spots (`RULES_INDEX.md`).
- [x] DONE with the H2 sessions batch, all in `webview2-hosting.md` (on-demand tier, which has room —
  the CORE tier is at 15.7/16.0 KB and must not grow): containment-checked static serving (H1) and
  "an async navigation policy CANNOT be enforced in `NavigationStarting`" (H1, with the three-way
  division of labour so nobody re-litigates it); plus this batch's own — owner-scoped per-profile
  environment caching and never caching a faulted one, "escaping a wedged op is only HALF the fix"
  (added under the marshalling rule it completes), re-check cancellation after a multi-second acquire,
  a health probe must fail closed, a subscribe API on a pooled object needs a disposal check, and root
  a CDP event receiver in a field.
- [x] **The one genuinely new file: `winforms-shell.md`** — DONE with the WinForms tail, covering all
  four named traps plus the ones that batch earned (pumping re-entrancy, `SystemEvents` leaking a
  static reference, per-HWND drop targets, `FormClosed` ≠ pump finished, pre-handle intent in a flag).
  **The core tier was OVER budget when the `RULES_INDEX` row landed (16.4/16.0)** — paid for by a real
  trim, not a cosmetic one: the "known gate holes until H5 lands" text in `CLAUDE.md` +
  `phase-workflow.md` and the guard's "current limits" list in `sensitive-info.md` were all STALE (H5
  closed them) and were actively telling future sessions to distrust a working gate. Now 15.6/16.0.
- [x] **DONE (2026-07-30, with H7):** the `SemaphoreSlim.Dispose()`-wedges-a-cancelled-waiter root
  cause is now a bullet in `webview2-hosting.md` (on-demand tier) — cancelling waiters and then
  disposing the semaphore races its internal queue-removal and can leave a waiter's task PERMANENTLY
  incomplete; a `SemaphoreSlim` only needs disposing if `AvailableWaitHandle` was touched, so the fix
  is not to dispose it. The rule carries the "bound such a regression test with `Task.WaitAsync`"
  half too, since the original symptom was a 10-minute harness timeout with no summary.
- [x] **DONE (2026-07-30, with H7):** `knowledge check` passes (rows resolve, every rule indexed) and
  `knowledge footprint` reports **core 15.6 / 16.0 KB — ok** (on-demand 43.5 KB across 5 files). H7
  only grew the on-demand tier — the two rule edits (`ipc-contracts` handshake ordering + gate-trigger
  correction, `webview2-hosting` semaphore bullet) are both there, so the always-loaded budget is
  untouched. **The next `.claude/rules/` (core) addition still needs a trim, not an append.**

**H9 — Auxiliary sessions: primitives + lifecycle hooks, not the product (D21/D22) — COMPLETE (2026-07-31)**

Suite **476 dotnet + 63 vitest**, `verify` PASSED. Only the `Shenora.WebView2.Sessions` baseline moved
across the whole batch — the other four stayed byte-identical, which is the evidence that this
reshaped one package and nothing else.

- [x] **H9.1 — typed input seam.** `DispatchInputAsync(string json)` →
  `DispatchAsync(SessionInput, CancellationToken)`, with `SessionPointerInput`/`SessionWheelInput`/
  `SessionTextInput`/`SessionKeyInput`/`SessionViewportInput` + a `SessionPointerAction` enum, and
  `SessionInput.TryParseLegacyJson` as the explicitly-named adoption shim (D21's accepted cost —
  an existing client keeps its frontend). Fraction coordinates kept: that is what makes the protocol
  resolution-independent. `BuildMouseEventJson` takes the enum now, so there is ONE vocabulary.
  **One correction worth keeping:** the record hierarchy is NOT airtight — a record's compiler-generated
  COPY constructor is `protected`, so `private protected` on the base does not seal it. `DispatchAsync`
  therefore keeps an explicit default arm that LOGS rather than assuming exhaustiveness; without it an
  unknown input vanishes silently, which on a watched stream looks like the page hung.
- [x] **H9.2 — `ReadHotspotsAsync()` removed.** A stringly-typed list of clickable rects is a co-browse
  UX decision, not a browser primitive. Apps run their own script through `Controller` — the proven
  script ships verbatim in the CHANGELOG's breaking entry so nothing is lost.
- [x] **H9.3 — the lifecycle hooks. RE-VERIFIED FIRST, and half this item was already stale:** H4.4 had
  wired `onProcessFailed` to complete the frame channel, so the "reader waits forever" bug was gone.
  Genuinely missing and now shipped: `SessionEnded`/`SessionEndReason` + `StreamingSessionOptions.OnEnded`
  (guarded, fired EXACTLY ONCE through a shared latch — dispose and a renderer crash genuinely race),
  and frame GEOMETRY — `Frames` is `ChannelReader<SessionFrame>` carrying the viewport read from THAT
  FRAME'S own metadata, not the session's current viewport (a resize in flight would otherwise mislabel
  the frame, which is exactly when a mis-mapped click hurts).
- [x] **H9.4 — the error-vocabulary bridge.** `SessionResult.ThrowIfFailed()` → `OperationException`,
  so the codes cross as wire codes verbatim and plug into the dispatcher's documented boundary. NOTE
  the "neutral session controller" half of this item was ALREADY DONE by H4.6's rename.
- [x] **H9.5 — the seam is PROVEN, compile-wise.** The sample composes the product over the primitives
  exactly as its RENDER route composes the pool: a `STREAM` facade (START/INPUT/STOP) pumping `Frames`
  out as base64 IPC notifications, plus `StreamViewer.tsx` sending pointer/wheel input back. **Every
  call is public API — no internals — which is the seam test passing.** The transport being the
  interesting part is the point: frames are BINARY and the bridge is JSON, so the sample base64s them;
  a server-backed profile would push the same bytes down a WebSocket and the session would not know.
  **Compile-verified only — the sample has NOT been run** (see the note under P1).
- [x] **H9.6 — `SessionBrowser` statics internal + a `CancellationToken`** (the H2/H6 deferral, bundled
  here because it is the same signatures). `InitializeAsync`/`GetHtmlAsync` are `internal`; the token
  gates the AWAIT ONLY and is wired from the pool and the streaming session. Cancelling the CREATION
  would break other callers — the environment task is SHARED via `SessionEnvironmentCache`.
- [x] **H9.7 + H9.8 — the naming, on user direction (2026-07-31) → D22.** The kit had passed D21 on
  SHAPE while failing it on NAME, twice. `LoginWindow` contained no login logic; `CoBrowseSession` was
  named for one product built on generic mechanics. Renamed to `InteractiveSession` and
  `StreamingSession` with their whole type families (see the CHANGELOG table), `driveLogin` → `driver`
  (parameter names are a source contract the baseline pins), and
  `InteractiveSessionOptions.Title`'s `"Sign in"` default → `"Session"`. `CookieLoginFlow` KEEPS its
  name on purpose — naming the scenario is the point of a reference driver.
  **A whole-library audit ran** by sweeping the API baselines for domain vocabulary: the Login cluster
  was the ONLY genuine leak across all five packages, and the npm barrel is clean. The false positives
  are listed in D22 so nobody re-raises them (`ProfileDirectory` is a Chromium user-data folder,
  `Module` is the kit's composition unit, `ImmersiveDarkMode`/`UserDataFolder` are platform SDK terms).
  The rule now lives in `.claude/knowledge/generic-library.md` so the next session catches this class
  unprompted.

### P6 — Sibling adoption (brief Phase 5) — SCOPED 2026-07-31, not started

The first adoption target is the **business-manager sibling** (`local/EXTRACTION-MAP.md` names it).
Survey done 2026-07-31; the increments below come from that survey, not from the original brief.

> ⚠ **The roadmap's premise for this phase is STALE — do not plan against it.** P6 was written around
> "adopt in the newest desktop sibling first (smallest host, gaps already documented)". That app has
> since grown an API tier, a plugin system with its own IPC-namespace guarding, an MCP server and a
> deployment stack. Its desktop host now has **28 IPC modules** and its web client **~148 send
> call-sites**. It is still the right first target — its gaps are exactly Shenora's value proposition,
> and it already consumes the family's other library from a pinned feed — but "smallest host" is no
> longer why.

**The finding that makes this tractable.** Both sides of its IPC funnel through ONE seam each: the
client has a single `post()` + `onMessage()` pair (~60 lines total) that all ~148 call-sites go
through, and the host has a single dispatcher (`DispatchAsync` + `Emit`) behind a one-method module
interface. So swapping the IPC substrate is **two adapters, not 28 module rewrites and 148 edits**.
Verify that both chokepoints still hold before committing to the plan — it is the whole basis of the
sizing.

**The two models, and which one is the DEFAULT (user direction, 2026-07-31 — corrects the first
scoping pass).** The target's IPC is FLAT and UNCORRELATED — `{ type: "module.action", …payload }`
posted fire-and-forget, with everything coming back on a pushed event stream discriminated by
`type`. The first pass scoped that as legacy to be bridged away from. **That was backwards.** For a
desktop shell the event pipe is the correct default and request/response is the special case, for two
reasons the kit's own docs already establish:

- **It frees the UI thread.** The dispatch pipeline preserves the caller's synchronization context
  BY DESIGN (`.claude/knowledge/ipc-contracts.md`: "transports dispatch on the UI thread and every
  handler's synchronous segment stays there"), so a request/response handler's synchronous segment
  runs ON the UI thread. This repo already pays that knowingly in one place —
  `WindowCommandFacade.Post` documents `START_DRAG` blocking for the whole OS modal loop for exactly
  this reason. Making request/response the default generalises that stall to the whole app. Posting
  and answering with events lets the host move the work off the UI thread and keeps the window live.
- **A correlated call has a deadline; real work does not.** The client's `invoke` defaults to a 30 s
  timeout, which is meaningless for anything substantial.

So: **request/response for quick, UI-thread-safe calls** (read a bit of state, toggle a window — what
`WindowCommandFacade` uses it for); **post + event stream for everything else**, which is most of an
app. The adapters in P6.4 must PRESERVE the target's model, not migrate it.

What is genuinely wrong in the target is narrower than "it doesn't use request/response": it is the
missing CORRELATION. With no id, a result or an error cannot be attributed to the invocation that
caused it — its dispatcher emits a generic `error` event and the client cannot tell which action
failed. That is worth fixing; the event-stream shape is not.

**And this exposes a kit gap, found before the adoption rather than by it — fix it before 1.0:**
`@shenora/react`'s bridge has exactly ONE outbound call, `invoke()`, which allocates a correlation
entry, awaits, and times out. **There is no fire-and-forget send.** So the kit currently pushes every
page→host call down the UI-thread-coupled, deadline-bearing path — i.e. it makes the wrong thing the
default, which is precisely the complaint above. Design the missing half deliberately (a `post`/`send`
that does not await, plus a documented convention for correlating a streamed result back to the
invocation that started it — a handle returned by a quick request/response START is the obvious
shape, and it also gives cancellation and progress somewhere to live). Per D21 the ADOPTER's shim
still owns any wire-format compat; this item is about the kit lacking a first-class path, not about
carrying someone's envelope.

#### How this phase works (user direction, 2026-07-31 — supersedes the increment framing below)

**This repo does NOT edit the sibling.** Shenora readies the LIBRARY; the sibling's own session does
the adoption once it is ready. So every P6 item here is library work plus the guide an adopter needs.

**And a sibling is a CHECKPOINT, not the spec** (`.claude/knowledge/generic-library.md`): read it to
answer *"is this capability present and safe?"*, never *"what method did they write?"*. Shenora is
generic and must serve apps that do not exist yet; the surveyed apps only tell you which capabilities
are REAL and which are speculation. Copying their method is how a consumer's shape gets shipped.

#### Capability findings from the survey (2026-07-31)

Already covered — no work needed, and the earlier plan was wrong to call these open questions:
- **Multi-origin static serving.** `WebViewHostOptions.FolderMappings` + `WebViewFolderMapping`
  (with `AccessKind`) already covers several virtual hosts, including a deliberately DIFFERENT origin
  for cross-origin ES-module imports. P6.3 does not need a serving-model decision.
- **Portable app paths.** `ShenoraPaths` (root/data/resources + `DataArea` + env override) matches the
  portable-layout shape an adopter hand-rolls.
- **Window state.** `WindowStateManager` covers logical-px persistence, DPI scaling, on-screen
  validation and restore-bounds-when-maximized — and fixes a latent bug on the way, since a hand-rolled
  version reaches for `Screen.WorkingArea`, which is DPI-mis-scaled (use `GetMonitorInfo`).
- **Dynamic module composition.** CLOSED 2026-07-31: `IModuleRegistry` + `TryMapModule`.

Known capability LIMITS:
- [x] **A mapped module cannot be RELEASED — CLOSED 2026-07-31.** `TryReleaseModule`, with
  `IModuleRegistry` reshaped to `TryClaimModule`/`TryReleaseModule` so claim and release have one
  owner (a registry that only remembers a NAME can never take the route out again). The original
  reasoning — "no consumer has needed it, so do not guess at the surface" — was sound as a default and
  wrong as a final answer once P7's SemVer freeze was the alternative: "restart to disable a plug-in"
  is not something an adopter should design around.

#### Still to do for adoption readiness

- [x] **P6.2 — DONE (2026-07-31): `docs/ADOPTION.md`.** Four stages ordered by risk (consume ->
  shell primitives, which carry no IPC dependency -> the WebView2 host -> the IPC substrate), a
  primitive-by-primitive mapping table, the migration traps that cost real debugging here, and a
  permanent "stays yours" list. Every one of the 48 kit names it promises was checked against the API
  baselines and the client barrel — a guide that names a member the library lacks is worse than none.
  Writing it exposed NO further capability gap: the three the earlier plan called open questions were
  already covered (serving via `FolderMappings`, paths via `ShenoraPaths`, window state via
  `WindowStateManager`), and the one real gap (dynamic module claim/query) was closed first. Original
  note follows.
- [x] ~~P6.2 original~~ Write the adoption guide (`docs/`): which kit primitive replaces which hand-rolled
  piece, in the order an app should adopt them (shell primitives first — they carry no IPC
  dependency), what stays the app's own, and the migration notes for each. This is the artefact the
  sibling's session works from, so it must stand alone without this conversation.
- [x] **P6.3 — DONE (2026-07-31): close whatever the guide exposes as missing.** Writing the guide
  (P6.2) exposed nothing; writing the ADAPTERS (P6.4) exposed two things and both are closed — see
  below. That asymmetry is the finding worth carrying: a mapping table can be written from the API
  list, so it only catches names that do not exist. Only code that must actually *express* something
  finds a capability that is missing.
- [x] **P6.4 — DONE (2026-07-31): both adapters written, RUN, and sabotage-verified.** Throwaways in
  `devtools/_p6-adapters/{host,client}` (gitignored, never shipped — D21): a `BaseFacade` adapter over
  a foreign one-method module contract (17 assertions) and a `post`/`onMessage` shim over the bridge
  (18 assertions). The host adapter needs no Windows reference, so it re-proves D20 for the adapter
  layer. **Two real findings, both fixed:** the shipped `.d.ts` named the UMD global `React` and so
  required `@types/react` in the CONSUMER's global program (`FIX-LOG`), and the client event bus could
  not express a catch-all subscription while the host's `IEventBus` had shipped `SubscribeToAll`/
  `SubscribeToModule` all along — closed by adding both breadths (`CHANGELOG` `### Added`).
  **The three "almost fits" it recorded are now CLOSED too** (user direction, 2026-07-31: *"you really
  need to close those gaps"* — my triage had deferred them as workaroundable, and workaroundable is not
  the bar before a SemVer freeze). A `CancellationToken` flows the whole dispatch surface, supplied by
  the transport as a LIFETIME and cancelled on its dispose (**breaking** for implementers/overriders);
  `IEventBus.Emit` is the fire-and-forget twin so a synchronous caller need not discard a task and read
  kit source to know it is safe; `IpcErrorMapping` is public so an app whose failures travel as EVENTS
  can reuse the leak policy instead of retyping it. All three were re-verified from the ADAPTER's side,
  not just by unit tests — the throwaway probe now uses each and its 22 checks pass.
- [x] **P6.5 — DONE (2026-07-31): portability guidance (D20).** `docs/ADOPTION.md` Stage 4 is now the
  real recipe — the project shape, the contract-substitution table (dialogs/clipboard/URL launcher/UI
  dispatcher/interaction/paths), the "add it to the solution or the guard never runs" step, and an
  explicit NOT-portable list (the window-state stack, `OptimizedForm`, tray, splash, secondary
  windows, single-instance) so nobody goes looking for a contract that deliberately does not exist.
  Proven twice in-tree: `samples/Shenora.Sample.Logic` and P6.4's host adapter, which needed no
  Windows reference either. No D20 amendment needed — the portable contract set covered every case
  both exercises hit.
- [x] **P6.6 — DONE (2026-07-31): the remaining targets evaluated.** Read as capability CHECKPOINTS,
  never as specs. Findings:
  - **The video-library sibling — ONE REAL GAP, closed.** It serves local media to its page over a
    custom virtual host with HTTP `Range`/206, with an ADR recording that
    `SetVirtualHostNameToFolderMapping` cannot honour `Range`. Shenora's deferred-scheme handler was
    `Func<Uri, Task<(byte[], string)>>` — no request headers, no status, no response headers, whole
    file in memory — so it could not express that at all. Closed: `WebViewResourceRequest`/
    `WebViewResourceResponse` + `WebViewByteRange` (**breaking**, see CHANGELOG).
  - **Its native-player host is RECORDED, not built.** It composites a native surface with the web
    view; P5.6's caption-button clipping is the same mechanism, but the sibling solves this in its own
    leaf library and has not asked the kit for it. A capability nobody has asked for is speculation.
  - **The skin-manager sibling — no gap.** Its plug-in SDK (`IPlugin`/`IPluginContext`/
    `IPluginProgress`) is the APP's contract per D21; what it needs from the kit is dynamic module
    composition with claim/release (now present) and progress-as-notifications (present).
  - **The server-backed app — no gap, and it needs the least.** It serves over in-process Kestrel, so
    `Range` is ASP.NET's problem, not the kit's; its profile is shell-only (`Shenora.WinForms` plus
    optionally the WebView2 host with no resource provider). Its host-side IPC seam is already
    `IMessageDispatcher.DispatchAsync` — an HTTP endpoint calls it directly, so D16's transport
    pluggability holds without new surface.
  - **Feed-back status:** every API change P6 argued for has landed, so nothing is left owing before
    P7 freezes SemVer.

#### Increments (keep it runnable at every step — that is the phase's standing rule)

- [x] **P6.1 — DONE (2026-07-31): the consumption path is proven, and it was BROKEN.**
  Three consumers under `devtools/_p6-consumer/` (gitignored): a leaf one with ONE PackageReference
  that touches a type from every package, a `net10.0` portable one proving D20 through a PACKAGE
  reference for the first time, and an npm one type-checking the packed tarball under NodeNext plus a
  native-ESM import. **It found a real defect: the NuGet global cache beats every source, so a
  consumer silently restored a `Shenora.WebView2` packed before the D19 re-layer and `Shenora.WinForms`
  was absent from its graph — with no restore error.** `dev.mjs pack` now evicts this repo's ids at
  the packed version, closing it; `docs/RELEASING.md` + `docs/FIX-LOG.md` carry the detail. Also fixed:
  the npm README did not say `onPostError` is set via `configureBridge`. Original note follows.
- [x] ~~P6.1 original~~ `dev.mjs pack` → local feed + exact-version pinning
  per `docs/RELEASING.md`, npm tarball for `@shenora/react`. Nothing adopted yet; this proves the
  consumption path end-to-end from outside this repo. **This is also P1.2's blocker in disguise** —
  a real external consumer is the dry run.
> ⚠ The staged-adoption increments that used to sit here — "shell primitives INTO the app", "the
> WebView2 host INTO the app" — were **deleted on 2026-07-31**, not left as pending work. They
> instructed this repo to edit the sibling, which the user direction above supersedes: Shenora readies
> the LIBRARY and the adopting app's own session does the adoption, working from `docs/ADOPTION.md`
> (whose Stages 1 and 2 are exactly those two increments, written for the adopter). A stale item that
> contradicts a standing direction is worse than no item — the next session acts on it.

- [x] **P6.3a — DONE (2026-07-31): the client can send one-way, and shares module state.**
  Landed `ShenoraBridge.post` + `onPostError`/`maxTrackedPosts` and `createShenoraStore`, with 13
  new vitest cases; ALL FIVE new tripwires sabotage-verified (one was vacuous first time — a
  primitive-returning selector cannot prove the getSnapshot cache). The host side needed no new API,
  as designed. The two open items are now DONE too: the `ConfigureAwait(false)` rule text says which
  half is the dispatch path, and **the UI-thread claim is MEASURED** — a `SAMPLE.SLOW` route in both
  shapes, sampled with `SendMessageTimeout`: work left in the route stalls the UI thread 2 027 ms,
  the same work handed off stalls it 0 ms. Original note follows.
  **DESIGNED 2026-07-31 → `docs/2026-07-31-shenora-oneway-ipc-design.md`** (read it before
  implementing; it carries the three constraints that decide the shape, the two things it
  deliberately does NOT ship, and a verification plan). Summary of what it lands: a `post` that sends
  the SAME envelope with no pending entry and no timer (so no wire change and the mirror test stays
  untouched), reporting a FAILED response through a bridge-level error sink because an unmatched
  response is silently dropped today; the documented convention that a long operation is START via
  `invoke` returning `{ operationId }` + notifications carrying that id **in the PAYLOAD, never in
  `module`/`type`/`scope`** (the EventBus match cache keys on those and would grow unbounded); and a
  fix to the `ConfigureAwait(false)` rule text, which currently reads as blanket when it only ever
  applied to the dispatch path — as written it would argue a future session into keeping long work on
  the UI thread. **AND the part that matters most (user direction, second pass): a
  `createShenoraStore(module, …)` factory returning ONE hook that declares a feature's send, its
  event reducers and its shared state together.** That is a HARVEST, not an invention — three sibling
  apps each built it, one of them factored it out twice after "every host-backed store repeated" the
  same wiring. Two things it must get right that the first design draft missed: **snapshot THEN
  deltas** (a component mounting mid-operation has missed the events and a stream cannot be replayed
  — a progress strip mounts when you open a tab, long after the work started), and **one subscription
  per store no matter how many components read it**, since status/progress UI in an app is inherently
  many-watchers. Build it on React's `useSyncExternalStore` so the kit imposes NO state library (all
  three siblings reached for zustand; the npm package's only peer stays React). Original note
  follows. Today
  `@shenora/react`'s bridge has exactly one outbound call, `invoke()`, which allocates a correlation
  entry, awaits a response and times out at 30 s — so the kit makes the UI-thread-coupled,
  deadline-bearing path the ONLY path, i.e. the wrong default (see the section above). Add the
  missing half: a send that does not await, plus a documented convention for correlating a streamed
  result back to the invocation that started it — a handle returned by a quick request/response START
  is the obvious shape, and it gives progress and cancellation somewhere to live. Public surface, so
  it must land before P7 freezes SemVer. Mirror it on the host side (a route that answers with
  events rather than a response) and check `BaseFacade`/`Done()` still read correctly for it. A client shim
  mapping `post`/`onMessage` onto the bridge, and a host adapter presenting its module interface to
  `MessageDispatcher` — so all 28 modules and all ~148 call-sites keep working while the transport,
  the error boundary, the batching and the ready gate change underneath. **Not a migration to
  request/response:** per the section above, posting and answering with events is the right default
  here, so the adapters preserve it and request/response is adopted only where a call is quick and
  UI-thread-safe. What SHOULD change is the missing correlation, so a result or an error can be
  attributed to the invocation that caused it. **This is the increment that tests D21 for real**;
  write down every "the framework almost fits, but…" as it happens — that list is the phase's most
  valuable output, and the item below is the first entry, found before the adoption even started.
*(P6.5 and P6.6 are listed once, under “Still to do for adoption readiness” above.)*

#### Standing constraints for the phase

- **The adoption is the real test of P5.5's fixes.** Several P0s were latent-only — nothing in this
  repo triggered them — so a real consumer is what proves them fixed rather than merely patched:
  the DI composition (a facade injecting `IMessageDispatcher`), async disposal of singletons, and a
  relative `--app-root`. Exercise all three deliberately rather than hoping they come up.
- **Adopt against the CURRENT layering.** D19/D20 landed in P5.5, so referencing the leaf package
  pulls the rest transitively; nothing here should reference `Shenora.Core` directly.
- **Private specifics stay in `local/`.** Real names, paths and file-level findings from the survey
  live in `local/PROJECT_NOTES.md` and `local/EXTRACTION-MAP.md` — this file stays generic.

- [x] **P7.1 — custom-scheme serving works end to end. RESOLVED 2026-07-31.** The
  `DeferredSchemes` feature had never served a request: `WebViewHost` added a `WebResourceRequested`
  filter for `scheme://*` but nothing registered the scheme with the environment, so requests were
  rejected by the network stack before the filter was consulted. Found by the P7 e2e adoption pass —
  the unit tests, the API baseline and the docs all agreed the feature worked.
  **Three things were missing, each producing the identical page-side error** (`TypeError: Failed to
  fetch`), which is why it took four rounds: the environment registration
  (`WebViewEnvironmentOptions.CustomSchemes` + `WebViewCustomScheme`, now guarded at construction);
  `AllowedOrigins`, because the page is served from the virtual host so the fetch is cross-origin;
  and CORS headers on the RESPONSE — `Access-Control-Allow-Origin` plus
  `Access-Control-Expose-Headers`, without which a correct 206 arrives with the right bytes while
  `Content-Range` reads back as null.
  **Proven, not asserted:** the sample now carries `RangeSchemeProbe`, which fetches a ranged
  resource through the real browser on every run and asserts status 206, `Content-Range`
  `bytes 10-19/1000`, the correct ten bytes AT THE CORRECT OFFSET (content, not length — a wrong
  offset is still ten bytes), 200 for the whole resource, 416 for an unsatisfiable range, and a
  working XHR. `RANGE SEAM: PASS`.
  Two method notes worth keeping: **counting handler hits** is what separated "the browser refused
  our response" from "the request never reached us" — same symptom, different bugs; and a 40-line
  isolation probe with no kit code answered in one run what three attempts against the full sample
  could not.

### P7 — Stabilisation + 1.0 (CURRENT)

- [x] **The API-surface gate is complete** (P5.5 H6 closed the hole: protected members, default
  values, `required`/`init`, attributes, parameter names, const values). 1.0 must not freeze behind a
  gate with a hole in it, and no longer would.
- [x] **XML-doc sweep — DONE 2026-07-31.** CS1591 is unsuppressed and, like every other warning, an
  ERROR. All five packages document every public and protected member. Adding an undocumented public
  member no longer compiles. Turning it on immediately caught a broken `<see cref/>` that had been
  invisible for as long as warnings were non-fatal.
- [x] **The last product leak is out of the library — DONE 2026-07-31 (user direction).**
  `CookieLoginFlow` moved to the desktop sample as `CookieLoginDriver`; D21 and D22 amended, since
  they had been justifying it to each other rather than testing it. A whole-surface audit by the
  documented method (sweep the API baselines for domain vocabulary) found no others: everything else
  it flagged is genuine browser or platform vocabulary.
- [x] **Per-package README sections + frontend build guidance — DONE 2026-07-31.** The README ships
  INSIDE every nupkg, so a `Shenora.Ipc` consumer reads the whole file: it now has a "Using each
  package" section per package — the smallest working snippet plus the one trap that costs an
  afternoon — rather than a single table addressed to nobody in particular. The P2/P3 carry-over
  landed with it: hash the assets, keep the HTML unhashed (the host serves it no-cache), split vendor
  code into stable chunks so a one-line app change does not invalidate everyone's bundle, and clear
  the dev server's pre-bundle cache after upgrading the client. Every C# name was checked against the
  API baselines and every TS name against the barrel — a README naming a member the library lacks is
  worse than none.
- [x] **`Shenora.Hosting.AspNetCore` go/no-go (D10) — DECIDED: NO-GO (2026-07-31).** Decided on
  evidence rather than reasoning: in the server-backed sibling the "SPA static-file policy" is five
  lines of ASP.NET, and the "loopback gate" is a two-line host check embedded in that app's own threat
  model — app security policy, not a reusable helper. Its host→page channel is the one-way event push
  the kit already provides, and its host-side IPC seam is already `IMessageDispatcher.DispatchAsync`.
  Recorded as an amendment on D10; the two-profile split stands, only the extra package is dropped.
- [x] **FIRST RELEASE SHIPPED — v0.1.0, 2026-07-31.** All five NuGet packages + @shenora/react on
  npm, tag v0.1.0, draft GitHub release. The order that actually worked, since it is not the one
  the docs originally implied:
  1. Create the npm ORG (@shenora is an org scope, not a user scope). npm checks AUTH before SCOPE,
     so a missing org first surfaced as a 2FA 403 and only afterwards as the real scope error.
  2. Hand-publish npm ONCE - trusted publishing cannot be configured for a package that does not
     exist. Locally that means NO --provenance, which requires a CI with OIDC.
  3. Run the workflow. The npm step SKIPPED because that version was already live, so the first run
     needed no npm auth at all.
  **Two workflow defects the first runs found, both fixed:** there was no real dry run (draft only
  affects the GitHub Release; both registry pushes precede it), and the NuGet push used a quoted
  glob copied from the sibling library - which works there only because that job runs on ubuntu and
  the SHELL expands it. On windows-latest dotnet took the pattern literally and pushed nothing.
  **And the release commit validated a design choice in production:** it changed ONLY CHANGELOG.md,
  because the version was already 0.1.0. Detecting "did anything change" by asking GIT rather than
  comparing version strings is the only reason that commit happened at all - a string compare would
  have seen no version change, skipped the commit, and silently lost the CHANGELOG stamp.
- [x] **Every reference profile proven to adopt, end to end — DONE 2026-07-31.** The third
  pre-release gate (user direction: *"verify all reference project they can adopt this seamlessly
  (you might setup some usecases and test them e2e)"*). Throwaways in `devtools/_p7-profiles/`
  (gitignored), resolving Shenora.* from the **packed packages** through a local feed — the real
  restore graph an adopter gets, not ProjectReferences.

  | profile | what it stands for | proof |
  |---|---|---|
  | desktop IPC adapters | the business-manager sibling: flat event-stream IPC behind two adapters | host adapter, **28 checks**, on `net10.0` with no Windows reference |
  | client shim | the same app's ~148 call sites behind `post`/`onMessage` | client probe, **18 checks**, incl. legacy firehose and migrated `(module,type)` seeing the same event |
  | plug-in hosting | the skin-manager sibling's plug-in SDK | claim / answer / **release** / re-claim with no restart, inside the host adapter's run |
  | media serving | the video sibling's ranged local media | `RANGE SEAM: PASS` through a real browser (P7.1) |
  | shell-only | the server-backed sibling: Kestrel serves its own UI, it wants the shell only | **11 checks** against `Shenora.WinForms` ALONE |

  **The shell-only profile is proven in BOTH directions**, which is the half that would otherwise rot:
  a runtime check shows the primitives work (paths, DPI-correct window-state conversions and
  off-screen recovery, an idempotent single-instance mutex, the portable contracts resolving to their
  Windows implementations), and a **compile-time negative** shows `Shenora.WinForms` does not drag in
  `Shenora.Ipc` or `Shenora.WebView2` — `NotReachable.cs` names one type from each behind an
  undefined `#if`, and building with the symbol defined FAILS with CS0246 on both. Verified by doing
  exactly that. "It works without them" is easy to say and easy to be wrong about later.

  Findings: none. Every profile adopted against the current surface without a workaround — which is
  the first time in this phase that a verification pass has produced no gap, and is the actual
  release signal.
- [x] **P7.1 is RESOLVED**, so nothing blocks the release except the GitHub remote itself.

### P1 — Skeleton tail

- [x] **P1.2 — DONE 2026-07-31, superseded by the real release.** OIDC trusted publishing is
  validated by v0.1.0 having shipped through it. Note this item's own premise was WRONG and the
  workflow was changed rather than the plan: `draft=true` is not a dry run — it only affects the
  GitHub Release, while both registry pushes precede it and are effectively permanent. A genuine
  `dry_run` input now exists (gate + pack + OIDC login, publishing nothing, touching no git). The
  trusted-publisher setup steps are in `docs/RELEASING.md`, and the npm ordering — org first,
  hand-publish once, then configure trusted publishing — is recorded above with the release.

### 0.2.0 — the communication core: event path, tracked operations, base-agnostic channel (2026-08-01)

Closes the first two findings from "the first adopter, IPC + drop-zone design review (2026-08-01)"
(`TASKS.md`) plus the drop-zone Stage-1 finding from the same review — three of that section's four
findings; the fourth ("drop zones are the strongest dedup case, worth stating as such") remains open
below. Design + rationale: `docs/2026-08-01-shenora-communication-core-design.md` + **D23**.
Implemented over 11 tasks in 3 staged stages (contract → operations → channel) from a plan doc now
removed per its own "delete once the work lands" lifecycle (`docs/README.md`'s doc inventory) — this
entry is its durable replacement. Ships as **0.2.0**, the first deliberate break since v0.1.0.

**AMENDED again (owner direction, before publish — "I don't even think we need any specific status
than regular — think about this is going to be structured like XHR"): `OperationStatus.Paused` and
`.Interrupted`, introduced below as two statuses, later collapsed into ONE, `Waiting`.** Every
transition already treated them as one band (`Dismiss`/`RequestResume` accepted either, neither was
ever pruned, the client's `waiting` getter already unioned them); `RequestResume`'s drop-vs-keep now
keys on `ResumePayload` instead of a second status. Renamed throughout (mechanism, not scenario, D22):
`PauseReason`→`WaitReason`, `IOperation.Pause`→`Wait`, `RegisterInterrupted`→`RegisterWaiting`,
`RequestPause`→`RequestWait`, `PauseRequested`/`OPERATION_PAUSE_REQUESTED`→`WaitRequested`/
`OPERATION_WAIT_REQUESTED`, facade route `PAUSE`→`WAIT`, client `paused`/`interrupted`
getters DELETED (`waiting` is now the whole band). The bullets below describe the branch as it shipped
AT THE TIME (`Paused`/`Interrupted` as two statuses) and are kept that way as the historical record;
the CURRENT shape, full rationale, and the complete rename table live in `docs/DECISIONS.md` D23's
amendment and `CHANGELOG.md`'s 0.2.0 entry. Caught before 0.2.0 was pushed or published, so free.

- [x] **The module contract carries the REQUEST path but not the EVENT path.** Closed by
  `IModuleContext` (`Module`, `Logger`, `Publish`, `Start`, `Run`) as the second parameter of
  `BaseFacade.RouteMessageAsync` — the one breaking change (`016bb9c`). `Publish` needs no registry
  and is always available; `Start`/`Run` are the one opt-in thing the same context offers, decided by
  a mid-plan user steer (*"we still allow for custom events so this is more like a context for every
  module/facade"*) that kept `IModuleContext` from narrowing into an operations-only entry point.
  Both fail LOUD, naming the fix, when the corresponding dependency (`IEventBus`/
  `IOperationRegistry`) was never supplied to `BaseFacade` — never a silent no-op.
- [x] **Long-running operations have no first-class shape.** Closed by the operations cluster in
  `Shenora.Ipc.Operations` (`a7dd661`, `3a2c035`, `99cbb02`, `373e579`, `5c35457`, `da79be8`) —
  harvested MECHANISM-ONLY from a private sibling's 320-line process registry, per `generic-library`'s
  two-app bar (a second sibling's `JOB_UPDATED`/`JOB_PROGRESS` archetype was the second data point).
  `OperationRegistry`/`IOperationRegistry`, `IOperation`, `OperationOptions`/`OperationInfo`/
  `OperationLabel`, `OperationEvents`, `OperationsFacade` (`LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`),
  `AddShenoraOperations` (opt-in). Two review findings fixed during the build, both about a
  cancel/throttle race rather than the shape: (1) `Cancel` on a non-`Cancellable` operation used to
  flip status while the body kept running — now refused, the same honest-refusal shape as an unknown
  or already-terminal id (`3a2c035`); (2) the trailing-progress-emit flag was reset only on the
  SUCCESS path, so a faulting `TimeProvider` left `TrailingScheduled` stuck `true` forever, silently
  muting every later `Report` on that operation — fixed by resetting it in a `finally` covering every
  exit (`7326984`, caught by review, corrected the plan doc's own snippet too — `018f4e0`).
  `RegisterInterrupted`/`RequestResume` (crash-resumable checkpoint offers, deduped on
  `(module, kind, resumePayload)`) came from ONE sibling only, flagged in the design as the first
  candidate for removal if a 1.0 audit wants surface trimmed (`da79be8`).
  **Deliberately dropped from the original interface sketch, recorded as a known limit, not an
  oversight:** `IOperationRegistry.Find(id)` — no consumer resolves a handle from a bare id, and
  every public member is SemVer surface at 1.0; an app needing one keeps its own id→handle map.
  **AMENDED (generic-library audit, 2026-08-01, before publish): reinstated.** The ruling above did
  not survive contact with `RequestPause`/`RequestResume` — both are client-request routes carrying
  only an id, and whoever handles them must translate that id back into a handle to call
  `Pause`/`Resume`, a recurring shape every such consumer would otherwise re-solve with its own
  id→handle map. See `CHANGELOG.md`'s 0.2.0 entry for the full audit list.
  Also dropped during review: a protected `Events`/`Operations` accessor on `BaseFacade` that the
  Task 1 plan snippet had sketched — no consumer scenario existed for reaching the raw dependencies
  once `Context` (`Publish`/`Start`/`Run`) exists, so it would have been unwanted SemVer surface
  (`0d3af6e`, plan corrected in `ba74c79`).
  **Client side:** `@shenora/react`'s `useShenoraOperations`/`createOperationsStore` (`586a1e3`,
  fixed to thread `module`/`scope` into the `LIST` snapshot payload in `8d499d4`) — a host-backed
  `createShenoraStore` instance with `running`/`finished` DERIVED getters over `byId`. Design §4.6
  sketched `byModule`/`byScope` selectors too; **deliberately not shipped** — filtering by module or
  scope is a one-line consumer selector over `byId`, and shipping indexes for it would be duplicated
  derived state with no gain. The design doc's prose promising them was trimmed in this task (11)
  rather than left to over-promise a surface that does not exist.
  **Verified against the real sample, not just unit tests** (`129bf10` rewrites `SampleFacade`'s
  SLOW route onto `ctx.Run`; `d6243cd` hardens the probe itself after review found it proved only
  that a click landed, not that the operation started — a 4th guard now polls the window TITLE for a
  marker set synchronously before the slow work begins). `node devtools/dev.mjs responsiveness`
  measured 0/65 unresponsive samples for the streamed shape across repeat runs (0 ms longest stall),
  matching the v0.1.0 baseline (0/95); the unchanged `block` anti-example still stalls ~2978–2989 ms
  of a 4000 ms window — the refactor did not quietly move work back onto the UI thread. Full numbers:
  `local/PROJECT_NOTES.md` 2026-08-01.
- [x] **`DropZoneManager` is already consumable WITHOUT the IPC migration — say so, loudly.**
  `docs/ADOPTION.md` Stage 1's drop-zone row previously listed `DropZoneManager` bundled with
  `DropZoneFacade` and noted "needs IPC, so it belongs to Stage 3" — true of the facade, not the
  manager, which depends only on `Shenora.Core` (`IEventBus`), the WebView2 control and a `Form`, and
  references no `Ipc` type. Fixed in this task (11): the row now names `DropZoneManager` alone as
  Stage-1-adoptable standalone, with `DropZoneFacade`/`useDropZone` pointed at Stage 3 as the IPC
  half. An adopter previously found this only by reading the source — the same failure mode as the
  0.1.1 DPI-claim finding.

**Cross-base channel (Stage 3, no public behaviour change — D16's "the seam, not the package"
applied to the host outbound half):** `NotificationPump`(+`Options`) extracted from
`WebViewIpcBridge` into `Shenora.Ipc` (`93563da`), which is now a thin WinForms/WebView2 adapter over
it (`84556a5`) — option names (`NotificationInterval`, `MaxQueuedNotifications`) and behaviour
preserved, `NotificationFilter` added. Every bridge previously subscribed with `SubscribeToAll`, so
two windows meant every event reached both; the filter is the seam that lets a channel receive only
its own slice.

**Docs pass (this task, 11):** `ARCHITECTURE.md` (the `IModuleContext`/operations/`NotificationPump`
inventory + the bridge's new shape), `.claude/knowledge/ipc-contracts.md` (the invariants earned
here, each with its reason), `CHANGELOG.md` (`### Breaking` for the `RouteMessageAsync` signature,
`### Added` for the rest), `README.md` + `src/Shenora.React/README.md` (the `IModuleContext`/
operations surface), `docs/ROADMAP.md` `## Done`, and the design + plan docs marked implemented per
`docs/README.md`'s doc inventory. `<VersionPrefix>` bumped to `0.2.0` — the only version source.

#### Amendment: the lifecycle completed to three bands (2026-08-01, before merge)

Closes both findings from "the first adopter, review of the unreleased communication core
(2026-08-01)" (`TASKS.md`) — the other two of that section's four findings from the design review
(`docs/2026-08-01-shenora-communication-core-design.md` §5A + **D23**'s 2026-08-01 amendment to
`docs/DECISIONS.md`). The fourth finding ("drop zones are the strongest dedup case, worth stating as
such") remains open in `TASKS.md`.

- [x] **An `Interrupted` offer could only be removed by resuming it — there was no way to decline
  one.** The adopter's own review named the exact shape: `Validate` hard-coded `Status == Running`
  for every caller so `Cancel`/`Complete`/`Fail` all refused an interrupted entry, `ClearFinished`
  only ever walked `_finishedOrder` (which `RegisterInterrupted` deliberately never wrote to), and
  `PruneHistory` skipped offers on purpose — three individually-correct, individually-commented
  guards that together left the state with no exit. That same adopter had already shipped the
  identical bug and stranded a real deployment on it (paused on DNS records it could not complete,
  permanently offering Resume, permanently undeletable). Closed by `IOperationRegistry.Dismiss(id)`
  (`b0f2dde`):
  `Paused`/`Interrupted` → `Cancelled` (terminal — enters bounded history, publishes an ordinary
  `OPERATION_UPDATED` snapshot, unlike `ClearFinished`/`RequestResume` which remove an entry with no
  wire event). Refuses `Running` on purpose — a separate member from `Cancel`, not `Cancel` accepting
  more states, because declining a pending offer and cancelling LIVE work are different acts and
  conflating them inside `Cancel` was this branch's only Critical (Finding 1 of the earlier
  whole-branch review). Signals the entry's own `CancellationToken` first when one exists, so a
  paused body still parked on it unwinds the same way a running one does under `Cancel`.
- [x] **No recoverable-but-stopped state existed for a run that halts mid-flight WITHOUT a crash.**
  Closed by `OperationStatus.Paused` (`b0f2dde`) — the WAITING band alongside `Interrupted` (§5A.2): a run that
  stops without crashing (expired cloud credentials, a throttling provider, DNS not yet propagated, a
  migration awaiting confirmation), which the surveyed app reports as its most common non-success
  deploy outcome, more common than failure. `IOperation.Pause(string reason, OperationLabel? detail =
  null)` (`Running` → `Paused`) and `IOperation.Resume()` (`Paused` → `Running`, clearing the reason)
  on the SAME handle `Start`/`Run` already return — `reason` is an app-defined STRING, like `Kind`,
  never a kit enum (the app's own taxonomy for what its UI offers). `IOperationRegistry.RequestResume`
  now also accepts `Paused`, and the two waiting-band cases are handled ASYMMETRICALLY on purpose
  (§5A.4): a `Paused` entry is left in place for the app to flip via its own `Resume()` handle (the
  client asking is not the state changing), while an `Interrupted` entry is still removed (no live
  handle to flip — the body died with the process); the `OPERATION_RESUME_REQUESTED` payload gained a
  `status` field so a handler can tell the two cases apart, since it cannot look an `Interrupted`
  entry up afterward. **Deliberately no `PAUSE` client route** — pausing is the host's own knowledge;
  `RESUME`/`DISMISS` are client routes because resuming and declining are the human's decisions.
  Client (`@shenora/react`): `'paused'` in `OperationStatuses`, `pauseReason` on `OperationInfo`, a
  `paused` derived getter alongside `running`/`finished`, and a `dismiss` action mirroring `cancel`'s
  shape (no optimistic local prune needed — `Dismiss` publishes an ordinary wire snapshot, unlike
  `clearFinished`/`resume`).

**The rule enforced, not just the fix (§5A.1, `b0f2dde`):** *every non-terminal status must have a
sanctioned exit to a terminal one.* `OperationLifecycleInvariantTests` enumerates the LIVE `OperationStatus` enum
via reflection — never a hardcoded list — and requires (and exercises through the real registry) a
registered exit for every non-terminal value; a future status added with no exit fails that test BY
NAME instead of stranding an operation the way `Interrupted` used to. Verified by sabotage: making
`Dismiss` temporarily refuse `Interrupted` failed the test citing `OperationStatus.Interrupted`
specifically, before the fix was restored — the standing rule that a tripwire proves nothing until it
has been seen to fail. `WireMirrorTests` extended for the new `DISMISS` route (also verified by
sabotage: a mismatched client literal fails naming the two differing strings) and the new `Paused`
status (`Every_operation_status_exists_on_both_sides` caught the gap for free, since it already
compares the live enum against the client's `OperationStatuses` object rather than a second hardcoded
list — it failed the moment the host gained `Paused` and the client had not, exactly as designed).

**Docs pass (this amendment):** `CHANGELOG.md` (folded into 0.2.0's still-unreleased `### Added`,
not a new version — 0.2.0 had not merged), `ARCHITECTURE.md` (the three-band lifecycle + the
invariant test named), `docs/ADOPTION.md` (the Pause/Resume/Dismiss half of the long-running-work
row), `src/Shenora.React/README.md` (the client-side `paused`/`dismiss` usage), and
`.claude/knowledge/ipc-contracts.md` (the §5A.1 rule — "every non-terminal state needs an exit" — as
the reusable half, generalised past operations to any future state machine in this codebase).

#### Review pass: `GetAll`'s sort, and a layer above the state machine (2026-08-01, before merge, `6b0ffad`/`af29884`)

The invariant test's own review (§5A.1's structural fix above) surfaced one real defect and one
documentation gap, both fixed in `6b0ffad`, ruled on the same day:

- [x] **`GetAll` sorted `Running` vs. everything else, not the three §5A.2 bands** — a `Paused` entry
  (`FinishedAt == null`) fell into the "everything else" bucket right alongside completed history,
  burying the exact row a user needs to find in order to resume or dismiss it. Reordered into
  Active (oldest first) → Waiting (`Paused`/`Interrupted`, oldest first) → Terminal (newest finished
  first). Pinned by two tests, both confirmed RED before the fix.
- [x] **`OperationOptions.Resumable` and `OperationInfo.PauseReason` had undocumented lifetimes** —
  `Resumable` governs ONLY the crash-checkpoint path (`RegisterInterrupted`), never `Pause`/`Resume` (a
  `Paused` operation is resumable by construction); `PauseReason` is cleared by `Resume()` but
  RETAINED through a terminal transition reached directly from `Paused`. Both stated on the properties
  themselves so a future reader does not "fix" either as an oversight.
  **AMENDED (generic-library audit, 2026-08-01, before publish): `Resumable` itself was REMOVED**, not
  just documented — the lifetime note above proved it was consulted nowhere except
  `RegisterInterrupted`'s own required-true gate, which every caller had already satisfied. See
  `CHANGELOG.md`'s 0.2.0 entry.

A second review pass then found that the client store and `Run` were never RE-DERIVED against the new
`Paused`/`Interrupted` asymmetry §5A.4 introduced — four findings plus hardening, one batch:

- [x] **CRITICAL — the client's `resume` pruned a row the host now deliberately KEEPS.** `resume`'s
  local prune predated the asymmetry (written when `RequestResume` always removed the entry
  host-side) and still deleted unconditionally. Since a kept `Paused` entry publishes NOTHING (nothing
  changed), the row vanished locally while the host still held it — unreachable until every subscriber
  unmounted and a fresh `LIST` ran: §5A.1's original bug, rebuilt one layer up, in the exact place this
  feature exists to eliminate. Fixed by gating the prune on `status === OperationStatuses.Interrupted`
  specifically, mirroring the host's own branch; pinned beside the existing `resume` test, RED
  confirmed first (`expected undefined to be defined`).
- [x] **IMPORTANT — `Run`'s tail marked a paused operation `Completed`.** `Complete` legitimately
  accepts `Running` OR `Paused`, so `Run`'s unconditional `operation.Complete()` after the awaited body
  returned stamped `Completed` on a body doing the design's own headline move
  (`op.Pause("dns"); return;`) — a third lie alongside the two §5A.2 exists to remove. Fixed by only
  completing implicitly when the entry is STILL `Running`; documented as "pausing by returning" on
  both `IModuleContext.Run` and `IOperationRegistry.Run`. The first version of the pinning test PASSED
  by accident (an already-completed awaited `Task` does not yield, so `Pause`+`Complete` ran in one
  synchronous burst faster than the test's first poll could reliably distinguish them) — rewritten to
  wait for the settled state, then RED confirmed 3/3 runs (`Expected: Paused, Actual: Completed`)
  before the fix, GREEN 3/3 after.
- [x] **IMPORTANT — the terminal band had no deterministic tiebreak.** Sorting on `FinishedAt` alone
  ties under `TimeProvider.System`'s ~15.6 ms Windows granularity, falling back to dictionary
  enumeration order (which reshuffles on unrelated churn). Added `.ThenByDescending(Sequence)` — a
  strictly monotonic counter that never repeats. RED confirmed first with a frozen `FakeTimeProvider`
  (both operations finishing at the identical instant).
- [x] **IMPORTANT — two shipped docs asserted a guard only `clearFinished` has.** `README.md`/
  `CHANGELOG.md` both said `clearFinished`/`resume` share ONE terminal-set pin; only `clearFinished`
  does, and the claim was self-contradicting (removing an interrupted row is `resume`'s own job).
  Reworded to attribute the terminal-set pin to `clearFinished` alone and describe `resume`'s prune as
  the interrupted-case mirror of the host's own asymmetry.
- Hardening (cheap, each closing a next-status trap): `NonTerminal` derived from
  `Enum.GetValues<OperationStatus>().Where(s => !IsTerminal(s))` instead of hand-maintained, in the one
  file whose thesis is "don't hand-maintain a status set"; `OperationRegistry.IsTerminal` made
  `internal` (`InternalsVisibleTo("Shenora.Tests")` already existed) so
  `OperationLifecycleInvariantTests` calls it directly instead of keeping its own copy; the invariant
  sweep now also calls `ClearFinished()` and asserts the entry is gone, covering the SECOND half of the
  original bug (never entering `_finishedOrder`), not just the first (no terminal exit); `Dismiss`/the
  public `Cancel(id)` now report what `Finish`'s own re-validation actually decided rather than an
  assumed `true`, closing a race where a concurrent `Resume()` landing between the two lock
  acquisitions could report a live operation as successfully dismissed — verified by a many-real-thread
  race test, sabotage-confirmed (reverting the honest return reproduced `dismissed=true but ended
  Running` reliably 3/3 runs).

**Docs pass (this review):** `CHANGELOG.md` (folded into 0.2.0's still-unreleased `### Added`),
`ARCHITECTURE.md`, `src/Shenora.React/README.md`, and
`.claude/knowledge/ipc-contracts.md` (two new reusable lessons: a client-side optimistic prune must
mirror the host's own asymmetry exactly rather than applying one rule to both branches of a wire
action, and a permission check split across two lock acquisitions must report the SECOND
acquisition's outcome, never the first).

#### Second adopter review: the interrupted entry with no selector (2026-08-01, before merge)

Re-review of the completed lifecycle above, from "the first adopter, second review of the
communication core (2026-08-01)" (`TASKS.md`). **Both findings from the first review confirmed
closed, and closed better than filed — recorded as positive confirmation, nothing to fix:**
`Pause(reason, detail)` makes the reason required; `Dismiss` is a separate member from `Cancel` and
signals the entry's token first; `Run` completes a body only while still `Running`, so
`op.Pause("dns"); return;` is no longer silently stamped `Completed`; the `RequestResume` asymmetry
(`Paused` left in place, `Interrupted` removed) is deliberate, documented, and carries `status` on the
event; `WireMirrorTests` derives from the host enum so neither the status nor the route could have
been added unmirrored.

One real gap, in the client:

- [x] **A crash-announced `interrupted` operation appeared in NONE of the client's selectors, so the
  offer `RegisterInterrupted` exists to surface was invisible to a UI built on them.** `makeState`
  exposed `running`/`paused`/`finished`; `TERMINAL_STATUSES` deliberately excludes `interrupted`
  (correctly) and `paused` matched only the literal `'paused'`, so an `interrupted` entry belonged to
  NO band — reachable only by hand-filtering `byId`, which the store's own docs discourage. The host
  models `Paused`+`Interrupted` as ONE waiting band (§5A.2) that `Dismiss`/`RequestResume` both accept;
  only the client-side selector for the other half was missing. Worst-case timing: a paused run is
  visible via `.paused` right up to a restart, then reappears as `interrupted` from the app's own
  checkpoint and vanishes from any UI built on `running`/`paused`/`finished` alone — the one state that
  exists purely to say "your work did not finish, decide what to do" silently dropped at exactly the
  moment its owner needs to see it.
  Closed by two DERIVED getters on `OperationsState` (`src/Shenora.React/src/operations.ts`) — never
  stored state, computed in the same `makeState` the existing three already live in: `interrupted`
  (that status alone) and `waiting` (`paused` ∪ `interrupted`). `waiting` is derived from one
  `WAITING_STATUSES` set defined ONCE, the same discipline `TERMINAL_STATUSES` already used, rather
  than a hand-listed pair repeated across two getters — a second, independently-maintained copy is
  exactly the class of bug this branch's earlier findings (the `resume`/`clearFinished` asymmetry
  bugs above) were also made of. `paused` itself is unchanged: a resume prompt and a pause-reason
  display are different UI uses and both still need their own half.
  Pinned by a client-side mirror of the host's `OperationLifecycleInvariantTests`: a test enumerates
  the LIVE `OperationStatuses` object (never a hardcoded list) and asserts every status lands in
  exactly one of {running, waiting, finished}, plus a direct `waiting == paused ∪ interrupted` check
  and a dedicated `interrupted` getter test. RED confirmed first against the pre-fix code — both new
  assertions threw `TypeError: Cannot read properties of undefined (reading 'map'/'some')` since
  `state.waiting`/`state.interrupted` did not exist yet; GREEN after the getters landed
  (`src/Shenora.React/src/operations.test.ts`).

**Docs pass:** `CHANGELOG.md` (folded into 0.2.0's still-unreleased `### Added`),
`src/Shenora.React/README.md` (the `interrupted`/`waiting` getters and when to reach for which),
`ARCHITECTURE.md` (the client surface enumeration), `.claude/knowledge/ipc-contracts.md` (one new
gotcha: a derived-getter set covering a host state machine's bands must be checked against the FULL
enum, not eyeballed against the getters that already exist), and `operations.ts`'s own doc comments
(the `OperationsState` doc now states the host's three-band table directly, and notes that an
`interrupted` entry is a pending OFFER the host never prunes on its own — only `Resume`/`Dismiss`
remove it).

### 0.1.2 — Stage 1 adopted: kit-owns-DPI + plain-form maximize deferral (2026-08-01)

Second round of adopter feedback, this time from the same private desktop sibling **after**
adopting Stage 1 on 0.1.1 (roughly 45 hand-rolled lines replaced by `WindowStateManager` +
`IWindowStateStore` over the app's settings file — verified live on a 200% display: position and
size restore at the correct physical scale, an unreachable saved position re-centres, save/restore
symmetric with no per-launch drift). Both findings are behaviour-only fixes on 0.1.1's surface — no
new members, no baseline delta.

- [x] **The kit owns DPI resolution entirely; the adopter supplies only LOGICAL state.**
  `AttachTo(form)` / `Apply(form)` now defer to `HandleCreated` when the handle doesn't exist yet
  and resolve `DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)` at that moment (still before `Show`, so
  the restored geometry lands on the initial paint with no resize flash). The 0.1.1 default used
  `DpiHelper.SystemScale()` (the PRIMARY monitor) synchronously, which made adopters responsible for
  two pieces of kit-internal knowledge — that `DeviceDpi` is the right source and that
  `OnHandleCreated` is the only moment it is valid — for a concern that is entirely the kit's. The
  scale-explicit overloads stay as the escape hatch (test harness, preview against a different
  monitor). `WindowStateManager.cs` L40-58 (parameterless defer) + `AttachTo` delegating to it.
  **Cross-monitor DPI hole in the first cut — caught by adversarial phase review, then fixed:**
  the initial commit read `form.DeviceDpi` immediately at `HandleCreated`, but the handle is
  created wherever WinForms/Windows initially places it (typically the primary monitor, since
  `Location` hasn't been set yet). On a mixed-DPI setup with a saved position on a
  different-DPI secondary, that returned the wrong DPI and the restored size was wrong. Fixed
  by pre-positioning the handle to the saved location BEFORE reading `DeviceDpi` — the move
  triggers `WM_DPICHANGED` synchronously (verified live in `devtools/_dpi-probe/` on the dev
  machine: DeviceDpi updated from 192 → 144 on a scale change, but SuggestedRectangle stayed
  unchanged and WinForms did NOT auto-rescale outer Size, so pre-position is load-bearing not
  belt-and-braces). `WindowStateManager.PrePositionToTargetMonitor`.
- [x] **`Apply` defers the maximize application to `Shown` for plain forms too.** In 0.1.1 only
  `IAppMaximizable` implementors got the `RestoreMaximizedTag` deferral; a plain `Form` had
  `WindowState.Maximized` set synchronously and — measured live by the adopter — the state reset to
  `Normal` by `OnLoad`, so a window opened restored-down however it was closed. Fix reuses the same
  marker via a one-shot `Shown` handler for plain forms (`DeferMaximizeToShown` at
  `WindowStateManager.cs` L125-135). Not a kit regression — the app's hand-rolled predecessor had
  the identical bug — but the kit is the right place for it to be fixed once.

Two tests updated (`Apply_places_the_saved_position_even_when_maximized` now asserts the marker
instead of the synchronous `WindowState`; `Window_state_applies_before_the_loop_and_saves_on_close`
forces the form's handle before reading `Size`, matching how `Application.Run` would trigger the
deferred apply) and three added (`Apply_parameterless_defers_to_HandleCreated_when_the_handle_does_not_exist_yet`,
`Apply_parameterless_applies_synchronously_when_the_handle_already_exists`,
`Apply_defers_maximize_to_Shown_for_a_plain_form`). API baseline unchanged — Apply/AttachTo
signatures identical to 0.1.1.

Deferred deliberately: `Apply(Form, double)` still applies synchronously — a caller supplying a
scale has said "size against this scale now", and inserting a HandleCreated defer would be
surprising. If the finding recurs there — an adopter passing a stale scale from before a handle
existed — the fix is documentation, not another defer.

### 0.1.1 — Stage 1 adopter findings (2026-08-01)

First real API feedback from the adoption loop — a private desktop sibling evaluated Stage 1 against
its own hand-rolled shell and did NOT adopt `WindowStateManager` because for that app the swap would
have been a net downgrade. The findings named specific gaps rather than a general dislike, which made
them cheap to close. Non-breaking: two new overloads + one new option (default-on) + docs.

- [x] **`Apply(Form, double scale)` overload.** The existing `Apply(Form)` uses
  `DpiHelper.SystemScale()` (the PRIMARY monitor), which is what makes it usable before the form has a
  handle — not what makes it accurate on a mixed-DPI setup. An adopter calling from `OnHandleCreated`
  with `DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)` was measurably MORE accurate than the kit. The
  overload lets them ship that pattern; the parameterless one keeps working. `WindowStateManager.cs`
  L32/L42.
- [x] **`WindowStateOptions.MaxToWorkArea` (default true) + `ToPhysical` overload taking
  `IEnumerable<Rectangle> workAreas`.** A size saved on a bigger display used to restore larger than
  a smaller display could show — the hand-rolled code being replaced clamped to the work area, so
  adopting as-is would have lost shrink-to-fit. Default-on because a window bigger than its monitor
  cannot be resized back down, which is a worse state than one slightly smaller than saved. Position
  stays owned by `IsVisible` + the caller's centre fallback. `WindowState.cs` L37, `WindowStateManager.cs`
  L182.
- [x] **`ADOPTION.md`'s DPI-fix claim moved from the `WindowStateManager` row to the `OptimizedForm`
  row.** Where the P/Invoke `GetMonitorInfo` actually lives (`OptimizedForm.TryGetCurrentWorkArea`).
  Previously the row overpromised: an adopter taking `WindowStateManager` without also taking
  `OptimizedForm` did not get the fix and only discovered so by reading kit source — precisely what a
  published surface is meant to make unnecessary. `ADOPTION.md` L43-45.
- [x] **`ADOPTION.md` Stage 1's "highest payoff" reworded as conditional.** Payoff is proportional to
  what the adopter actually hand-rolled — the same shell that produced these findings only had two rows
  apply (no single-instance mutex, no clipboard use, no splash, no frameless chrome, three shell-open
  sites already injected). Row-by-row wording unchanged; the intro is honest about it. `ADOPTION.md` L37.

Why the fixes are conservative on API shape: this is 0.1.1, and every public change is now SemVer
surface. Two new overloads on `Apply`/`AttachTo` + one new `ToPhysical` overload + one new option
are additive; the only behaviour change is `MaxToWorkArea` defaulting on, called out under
`### Added` because the old behaviour is one option away (`MaxToWorkArea = false`). Deferred
deliberately: the `ToPhysical` clamp could also clamp POSITION to keep the window fully within the
work area — the finding said "position is already handled well by `IsVisible`", so this stays a
size-only clamp until an adopter reports otherwise.

**Failure mode to watch for**, so the next session that sees it does not rediscover it: an odd
multi-monitor arrangement whose saved position falls in the GAP between monitors will hit
`PickTarget`'s "largest overlap" or "first" fallback, and if primary has a small work area the
window shrinks even though the user has room on the other monitor. `MaxToWorkArea = false` is the
escape hatch that turns the clamp off entirely; a smarter fix would clamp POSITION to keep the
window on the largest-overlap monitor, which is the deliberate deferral above.

Baselines re-promoted (additions only — one property on `WindowStateOptions`, one `ToPhysical`
overload, one `Apply` overload, one `AttachTo` overload — the last added during phase review to
close a symmetry gap the reviewer caught).

## Atomic file write + transform — DONE 2026-08-03 (`Files`, `FileReplacement`)

Filed by the first adopter's `Shenora.Core/Io` adoption attempt and built the same day.
Shipped as `Files.WriteAllText/WriteAllBytes/Write/BeginReplace` + `FileReplacement` in
`Shenora.Core`, ported from the adopter's stopgap with its six tests plus seven for the transform
half (13 total). Baseline additive, 0 removals. Sabotage-verified: swapping `File.Move` for
`File.Replace` fails four tests including the fresh-install case, and removing the temp discard
fails the abandoned-transform test.

⚠ **One guarantee is NOT test-covered and says so in the source**: deleting the flush-to-disk
leaves all 13 green, because durability against power loss cannot be asserted from a running
process. It rests on reasoning and is marked load-bearing.

The original entries, kept for the reasoning rather than the outcome:

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

## A8 — iOS published, and the pipeline problem never existed — DONE 2026-08-03

0.5.1 shipped all five packages from ONE Windows runner, `Shenora.iOS` included — its first published
version, since 0.5.0 has none and never will (versions are immutable, so that package's history
begins at 0.5.1). Confirmed against nuget.org's flat container after the registration index proved to
lag: the same package flipped between visible and not across polls, which is edge-cache
inconsistency, not a missing push.

The three-job macOS release design was retired UNBUILT — a `net10.0-ios` LIBRARY needs only the
`maui-ios` workload, never Xcode; only an iOS APP needs a Mac. `release.yml` installs both mobile
workloads explicitly, because the runner image publishes a 9.0 `maui.*` list and cannot be assumed to
carry the .NET 10 ones.

The original entry, for the reasoning:

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
  - **CI now installs both mobile workloads explicitly** (`release.yml`), because the runner image
    publishes a 9.0 `maui.*` list and cannot be assumed to carry the .NET 10 ones. 0.5.0 proved
    net10.0-android resolved there and proved nothing about iOS. The failure mode was safe either way —
    the Verify gate runs before Pack, Push, Commit and Tag, so a missing workload burns no version —
    but a release is the wrong place to find out.
  - Separately, RUNNING the sample on a simulator still rides two override flags
    (`ValidateXcodeVersion=false` + `MtouchLink=SdkOnly`) because that Mac's Xcode 26.3 is older than
    the workload's 26.6. That is an APP concern only, gitignored machine config, simulator-debug only
    — it never touches the packages. Device and Release iOS remain UNPROVEN.


## E1 — off-screen sessions could not reach the app's OWN bundle — CLOSED 2026-08-03

**The gap.** `SessionBrowserOptions` had no resource seam, so an off-screen session reached only
NETWORK-reachable URLs. Found 2026-08-02 while chasing the sample's broken stream: with the navigation
guard fixed, `StreamingSession` navigated happily to the packaged app's virtual host and rendered
**WebView2's "can't reach this page"** — a session browser builds its own `CoreWebView2Environment`
with none of the shell's serving on it. `SessionController` exposes no `CoreWebView2`, so an app could
not bolt it on from outside either.

**What shipped.** `SessionBrowserOptions.VirtualHost` + `ResourceProvider` + `FolderMappings` — the
SAME option names `WebViewHostOptions` already uses, so the adopter recipe is to pass the host's own
values through. Rationale and the deliberate omissions are **D38**; surface in `ARCHITECTURE.md`,
adopter recipe in `ADOPTION.md` Stage 2.

- **`WebViewBundleServing` (internal) is now the ONE serving implementation**, shared by `WebViewHost`
  and `SessionBrowser`. Refactor, not a copy — that logic is where this path gets subtly wrong, and it
  had never been under test because it lived inline in a `WebResourceRequested` lambda over a live
  `CoreWebView2`.
- **Both-or-neither, refused at initialization** (`AssertBundleConfigured`). Either half alone serves
  nothing, and its symptom is indistinguishable from the bug being closed.
- **The app's `RequestFilter` runs BEFORE the bundle**, from ONE handler (`DecideRequest`). A blocked
  request is a stated policy; and two `WebResourceRequested` subscriptions both assigning
  `args.Response` is last-writer-wins by subscription order.
- **`FolderMappings` came along** so a disk-backed app is not left with exactly the gap this closes for
  an embedded one.

**Deliberately NOT built, and this is the part to read before re-proposing it:** a custom/deferred
SCHEME inside a session. WebView2 accepts scheme registrations only at ENVIRONMENT creation, so it is a
materially bigger surface (env options, `AllowedOrigins`, CORS) than the bundle pair, and no consumer
has needed it — `generic-library.md`'s bar as written. Likewise `SessionController` still exposes no
`CoreWebView2`: the finding named that absence as evidence the gap was unworkaroundable, not as the fix.

**Evidence — proven on the PACKAGED sample in both directions, which is the only build that shows it**
(a server-backed app never saw the bug; both sample demos work in dev mode and the e2e runs there):

| | Streamed pane | Pooled `RENDER/PROBE` |
|---|---|---|
| With the seam | the sample's real React frontend, `frontend: packaged` (`devtools/screenshots/e1-stream-own-bundle.png`) | `offscreen "Shenora Sample" rendered — 5749 chars of live DOM` |
| Options removed again | WebView2's error page — cloud glyph + Refresh button (`devtools/screenshots/e1-BEFORE-no-seam.png`) | — |

Three tripwires sabotage-verified in BOTH directions, each failing by name and restored with Edit:
`AssertBundleConfigured` (each direction of the both-or-neither check, separately — the mirror sabotage
fails a DIFFERENT test), the filter-before-bundle order, and the strip-query-before-unescape order. That
last one is why the asymmetric `%3F` case exists: every symmetric path test passes with the order
reversed.

**Incidental finding worth keeping:** the streamed page reports `host: no injected metadata` /
`shell: nothing advertised — assume nothing`. That is D36's "absent = assume nothing" default working
in a real second context — a session browser has no injected globals and no host handshake — rather
than the page mistakenly assuming desktop. Corroboration for D36 that no test could give.

**A doc claim that was FALSE and is now moot:** the old TASKS entry said "`docs/ADOPTION.md` says
plainly that off-screen sessions reach network URLs only". It did not — ADOPTION.md had no session
content at all. The `doc-claims` rule, catching a claim about a doc rather than about code.

## The stage verifier's THIRD failure mode — intrusion — CLOSED 2026-08-03

Owner filed it mid-session as a note for whoever ports the archive-backed `IUpdateSource`: a verifier
needs tamper, truncation AND intrusion, and `CommitAsync` had only the first two. Closed immediately
rather than with the port, because the note's own argument is right — the kit is where "everyone gets
the strong version" is decided.

**Verified before building, and it was worse than the note assumed: the hole was END TO END.**
- `CommitAsync` walked `manifest.Files` only — presence and hash. Nothing enumerated the staged tree.
- **`ApplyAsync` overlays `Directory.EnumerateFiles(StagedDirectory, "*", AllDirectories)`** — it copies
  EVERYTHING staged, not just listed files. So an unlisted staged file was verified by nobody and
  written into the install root, while the marker's own XML promised "complete and verified — an applier
  never has to re-check".
- **Both halves were individually defensible, which is why five reviews missed it.** Enumerating in
  `ApplyAsync` is RIGHT (a differential stage holds only the changeset, and `manifest.json` is in the
  tree but not in `manifest.Files`, so a manifest-driven overlay would fail to copy it), and verifying
  the manifest is RIGHT. It was the PAIR that left the gap — the class of defect worth naming, because
  neither file looks wrong on its own.

**⚠ THE TRAP, found by reading `FetchAsync` before writing the check: the kit stages an unindexed file
ITSELF.** `FetchAsync` writes the release `manifest.json` into the stage for the applier's removals and
deliberately keeps it out of the staged manifest. A literal "nothing is exempt" default would therefore
**reject every stage the kit's own flow produces** — exactly the inverted failure the owner's note warns
about (too strict breaks for every user at once, an attacker not required), arriving from the kit's own
design rather than any consumer's packaging. So `manifest.json` is exempt unconditionally, named once as
a constant because three things must agree on it (`FetchAsync` writes, `ApplyAsync` reads, the check
exempts).

**Shipped:** `UpdateStageOptions.IsUnindexed` — a PREDICATE, not a list, because which paths a clean
release legitimately carries unindexed belongs to whatever generated the manifest (a bundled data
folder, a seeded checksum stamp that would be circular to index, a per-release version file). A baked-in
list would freeze one app's packaging policy into everyone's verifier. Strict by default; the predicate
receives a manifest-relative forward-slashed path, which is pinned by a test because every
`StartsWith("data/")` exemption an adopter writes depends on it.

Comparison reuses `ManifestDiff.Normalize` (promoted private→internal) rather than a third copy: a disk
path and a manifest path must agree on separators and case here for the same reasons that rule already
exists, and those comparison rules are sabotage-verified in one place.

**Sabotage-verified in BOTH directions, and the second direction is the one that mattered:**
- Check not run → 5 intrusion tests fail by name.
- `manifest.json` exemption removed → `THE_REAL_FetchAsync_FLOW_STILL_COMMITS` and
  `The_kit_s_OWN_manifest_json_is_exempt_without_any_predicate` fail **and so does the PRE-EXISTING
  `UpdateStageTests.FetchAsync_downloads_only_the_CHANGED_files_and_commits`** — independent
  confirmation from the old suite that the trap is real and not invented. Note what that turns on: the
  pre-existing test catches it only because it drives the REAL `FetchAsync`. A fixture-built stage would
  have sailed past, because the test author writes both sides and they agree by construction. That is
  the owner's "synthetic fixtures will not catch it" warning proving itself inside the kit.

**Still owed by whoever ports the archive source:** validating an exemption set against a REAL published
release rather than fixtures. Kept in `TASKS.md` for that reason.

## C — the SAVE picker: universal on all three shells — CLOSED 2026-08-03

Owner's work order put this after E1, and owner chose "do it PROPERLY on both platforms" over a share
sheet when asked. The share sheet was rejected for a specific reason worth keeping: `Share.RequestAsync`
completes when the sheet is PRESENTED, not when the user picks, so `Success` would have meant "handed to
the platform" — the same member promising something weaker on mobile than on the desktop, which is
exactly what D35 exists to prevent.

**The shape: `IFileDialogs.SaveAsync(options, write)`.** The counterpart to `OpenReadAsync` — open became
universal by letting the host do the READING, save becomes universal by letting the host do the WRITING.
A callback rather than a returned path because "give me somewhere to save to" has no mobile expression at
all: the user grants access to one document, the app writes into it while the grant is live, and there is
nothing to hand back. `SaveFileAsync` is now documented as the DESKTOP-flavoured member and keeps
refusing on mobile, which is the correct answer rather than a gap.

| Shell | Mechanism | Evidence |
|---|---|---|
| Windows | default over `SaveFileDialog` + `Files.BeginReplace` | 8 tests; atomicity sabotage-verified |
| Android | `ACTION_CREATE_DOCUMENT` via AndroidX's activity-result registry | emulator; 160 B at the chosen path |
| iOS | `UIDocumentPickerViewController(asCopy: true)` | simulator; 160 B, byte-identical |

**All three produce into a temp and only then hand over**, so an interrupted save never damages the
user's previous file. That is the case `Files.BeginReplace` was built for — a save picker usually fronts a
long operation, and on Android it also dodges a real trap: opening a content URI in write mode truncates
the target immediately.

**Established by COMPILING rather than guessing** (the technique this repo already uses):
- **`Microsoft.Maui.ApplicationModel.Platform.OnActivityResult` does not exist in .NET 10.** The route is
  `((ComponentActivity)Platform.CurrentActivity).ActivityResultRegistry.Register(key, contract, callback)`
  — the REGISTRY specifically, because `RegisterForActivityResult` must be called before the activity
  reaches STARTED and a DI-resolved service cannot. It also needs NO app-side wiring, which is what makes
  it adoptable: no `OnActivityResult` override for an adopter to remember.
- **`Platforms/<Platform>/**` globbing works for a SINGLE-TFM library project**, which had never been
  exercised in these projects (`ARCHITECTURE.md` said "there is none yet"). Sabotage-verified in BOTH
  directions: a deliberate error in `Platforms/iOS/` fails the iOS build, so the file really is compiled;
  and an Android-only file in `Platforms/Android/` inside the iOS project builds fine, so the exclusion
  really works. A "Build succeeded" over a file that was never compiled would have proven nothing.
- ⚠ `= default` on the IMPLEMENTING half of a partial method is **CS1066** — the default belongs only to
  the defining declaration.

**A `partial` method rather than a virtual with a fallback, and it proved itself:** before the iOS half
existed, the iOS build failed **CS8795**. A fourth shell joining the shared mobile source cannot compile
until someone decides what save means there, instead of silently inheriting a stub that refuses at
runtime. Fail-closed at compile time beats fail-loud at runtime.

**THE DEVICE RUN EARNED ITS KEEP — it found a defect no build could.** iOS's export picker suggests the
TEMP FILE's own name to the user, so `NewTempPath`'s `{guid}-shenora-sample.txt` appeared in the "Save as"
field as `89c9bdcc7248436…`. **Android could not have shown it**: there the suggested name is passed
separately to `Launch()` and the temp's name never surfaces. Fixed by moving uniqueness from the file NAME
to a per-call DIRECTORY (plus `DiscardTemp`, or every save leaks an empty folder for the life of the
install), then re-verified on the simulator: "Save as **shenora-sample**", and the file landed as
`shenora-sample.txt`.
- **The lesson as a class: two shells sharing one contract can hide each other's bugs**, for reasons that
  live entirely in the platform's UI rather than in the shared code. Proving one shell is not proving the
  contract.
- Both runs also showed TICK notifications flowing throughout, so neither an open picker nor the write
  blocked either UI thread.

**Process note for the next mobile task: each iOS round trip costs ONE COMMIT**, because `mac push`
refuses a dirty tree by design. Finding the naming defect and re-verifying took two. That is the guard
working — it exists because a warning alone once cost two rounds of concluding things about code the Mac
never saw — not friction to route around.

**Still open under C, and narrower than before:** only `OpenFolderAsync`, which D35 argues should stay
closed as a portable capability. Kept in `TASKS.md`.

## DM1 — a `Range` answered with real headers on BOTH mobile shells — CLOSED 2026-08-03

The media backlog's critical path, and the item everything else was blocked behind: *serve one real media
file through the mobile interception seam on each platform and confirm it **plays AND seeks***. Done on an
Android emulator and an iOS simulator. Rationale and the rules it produced are **D44**; the measurements are
`docs/2026-08-03-shenora-media-design.md` §0j (Android) and §0k (iOS). The probe ships in the sample:
`samples/Shenora.Sample.Maui/MediaRangeProbe.cs` + the media row in the sample page.

**The recipe that works, and it is nearly the same on both:** a reserved PATH on the page's own origin
reached RELATIVELY · the PORTABLE `SetResponse(code, reason, headers, stream)` · 206 with `Content-Range`
and `Accept-Ranges` · and a body that is **unsliced on Android, sliced on iOS** (D44 — the platforms apply
the range start differently, and that one row is the measured case for per-platform media packages).

**Evidence.** On the `moov`-at-end clip, which cannot open at all unless a tail range is answered correctly:
Android loaded it in exactly three requests with no retry loop, in mdat-first order (`bytes=0-` → the tail →
back into the media), resolved its true `1:00` duration and ran to `1:00 / 1:00`; iOS reported
`loadedmetadata — duration=60.00s 480x270`, `play() resolved`, then `seeked -> currentTime=48.04s`.
Screenshots: `devtools/_android/dm1-*.png`, `devtools/_mac/ios-dm1-*.png`.

### Three claims this repo had ALREADY WRITTEN DOWN and that the runs falsified

Worth listing together, because they share one shape — each was an inference presented as a finding.

1. **"The portable seam cannot set response headers."** It can; there is a second `SetResponse` overload
   taking a header dictionary, on both mobile TFMs. The original read one overload as the whole set, and it
   had made a per-platform `PlatformArgs` implementation look MANDATORY before any contract existed. One
   build to check.
2. **"The URL must be the app scheme."** True of iOS interception, false of Android playback — Android
   intercepts `app://` and its media pipeline then refuses it outright.
3. **"Media reaches the seam, so the design reaches all three shells."** Interception and playability are
   different questions; the session-4 probe deliberately answered nothing, so it could only ever prove the
   first.

### What actually made it tractable, kept because the next device investigation will want it

- **A CONTROL PAIR, not a test file.** Two clips of identical content differing only in whether the mp4
  `moov` atom sits at the front or the end. The faststart one plays even from a server that ignores `Range`
  entirely, so "the video played" proves nothing on its own; the tail one cannot start without a correct
  range answer. **The whole Android defect is invisible without this pair**, because a faststart file only
  ever asks for `bytes=0-`, where the double-skip is a no-op.
- **An explicit `fetch` with a `Range`, asserting the returned BYTES.** A `<video>` element can only ever
  say `MEDIA_ERR_SRC_NOT_SUPPORTED`, which made three wrong hypotheses look equally likely and cost several
  deploys. The instrument settled it in one. **Assert on an OFFSET-revealing slice** — `bytes=4-11` is
  `"ftypisom"` in any mp4 — because a length-only check passes just as happily on the wrong bytes, which is
  exactly how a seam that silently serves from 0 looks correct.
- **A bundle control** (the same file served by the platform's own static serving, touching none of our
  code) to establish that the file, the codec and the device's media stack were fine before blaming them.
- **Toggles in the page for response mode and URL form**, so a platform question costs one deploy instead of
  one deploy per hypothesis — which matters most on iOS, where `mac push` refuses a dirty tree and every
  round trip costs a commit.
- **Reading the platform response BACK.** After calling the portable `SetResponse`, Android's
  `PlatformArgs.Response` is the ground truth about what MAUI built (`mime=video/mp4 enc=UTF-8 status=206
  reason=Partial Content …`). That one log line eliminated the entire "MAUI drops the headers" branch.
