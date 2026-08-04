# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.9.1 PUBLISHED (2026-08-04).** Six NuGet packages + `@shenora/react`, all confirmed at 0.9.1 on
the feed. **A patch release, and the reason matters more than the size: 0.9.0's `Shenora.iOS` could not be
LINKED by any iOS app that had not enabled the Live Activity devkit** — five undefined `_shenora_activity_*`
symbols — so the package was unusable for its intended default case. Found by the first adopter, not by us;
the gap was that every local check used a PROJECT reference, and the defect only exists for a PACKAGE
consumer. `dev.mjs mac build` now runs a link check in exactly the configuration the adopter had.

**0.9.1 is verified against the published package, not the tree** (2026-08-04): a scratch app-shaped iOS
consumer, cache purged so restore had to hit nuget.org, `PackageReference Shenora.iOS 0.9.1` and **no devkit
opt-in**, touching the API so the `DllImport`s are actually rooted. It builds; all five `@_cdecl` symbols are
defined (`T`) in the app binary with **zero undefined** — the exact inverse of 0.9.0 — and no `PlugIns/`, so
the widget extension stayed opt-in and the fix did not push the expensive half onto everyone. Recipe and its
traps: `.claude/knowledge/mobile-shells.md`.

0.9.0 carried the D45 re-layering (interception is a middleware pipeline in Core, implemented per shell),
`IPlaybackSession` on all three shells, and the iOS Live Activity devkit. **No `### Breaking` section in
either**: the WinRT requirement is opt-in via a second TFM on `Shenora.Windows` (**D46**), so existing
consumers change nothing. Post-publish verification — including a real `PackageReference` spike proving the
devkit's automatic `buildTransitive` import — is in `docs/archive/tasks.md`. The only item still unproven is
the Dynamic Island's visual rendering, which needs a device.

_0.8.0 (2026-08-03) carried the **D2a relocation** — `WebViewResourceRequest`/`Response`/`ByteRange` moved
`Shenora.Windows` → `Shenora.Core`, a documented break whose whole migration is one `using Shenora.Core;`.
That release existed because the move had shipped in the DOCS and not in the PACKAGES, which would have met
the first adopter as a compile error._

> **This library is the intended foundation for the author's apps** (owner, 2026-08-03), so the bar on the
> published surface is an adopter's, not a maintainer's: docs that match the artifact, breaks documented
> with their migration, and readiness claims verified against a restored package rather than the tree.

⚠ **0.6.0 shipped 0.5.1's CODE** — the work was committed locally and never pushed, so the release
ran against a stale tree; account under `CHANGELOG.md` `## 0.6.0`, and the gate that would have caught it is
in `### Release hygiene` below.

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
>
> DIRECTION (user, 2026-08-04): *"one thing you need to keep in mind, we are doing a library, for
> multiple platform, so if the library can provide powerful devtooling that will be even better so
> for example rely on less swift code for ios (dynamic island) and support platform logic like now
> playing"* — so **PLATFORM LOGIC is in scope, not just the shell**, and the measure of a platform
> capability is *how little native code an adopting app has to write*. See
> `### Platform integration` below for the read on the two named examples.

## Open

> **WORK ORDER (owner, 2026-08-03): ~~E1~~ → ~~C~~ → D (media).** **E1 and C are both DONE** — the session
> bundle seam (D38) and a portable `SaveAsync` proven on all three shells, entries in
> `docs/archive/tasks.md`. **Media is the live work, and most of it is now DONE:** `DM1` (a real file plays
> AND seeks through the interception seam — proven on both mobile shells, then on the desktop too), `DM2`
> (the per-stream playability planner), `DM5` (packaging) and the whole **D45 re-layering** are closed.
> **`DM3` (the conversion) and the remote half of `DM4` are what is left**, both below.
> ⚠ **Read `D44` AND `D45` before designing anything here.** D44 carries the three rules the device runs
> produced and contradicts two things the design doc asserts; D45 moves interception out of media entirely,
> which the design doc (`docs/2026-08-03-shenora-media-design.md`) predates and does not reflect.
>
> **The archive-backed `IUpdateSource` is DEFERRED, deliberately and not for lack of value** — the
> first adopter is building their own first. That is the better sequence and the one this kit is
> built around (D8/D15): the kit then ports a proven implementation instead of a guess at one, which
> is exactly how `Files`/`FileReplacement` arrived. Pick it up when theirs works, and read it before
> designing anything.

### Adoption readiness — CLEARED 2026-08-03, recorded so it is not re-checked

A server-backed sibling is adopting the kit. Readiness was checked against the **published artifacts**
rather than the tree, which is the only check that counts, and everything found is now resolved or
documented. Nothing here is open.

- **The one blocker is FIXED by 0.8.0.** D2a had shipped in the docs and not in the packages, so
  `using Shenora.Core;` — what every doc says — would not have compiled. Confirmed live: `Shenora.Core`
  0.8.0 restored *from nuget.org* contains all three types under `Shenora.Core.*`, and `Shenora.Windows`
  0.8.0 carries none of the old names. ⚠ The cache trap in `ADOPTION.md` Stage 0 is real and applies to
  verifying a release too — clear any locally-cached copy of that version first, or you validate your own
  build instead of the feed.
- **No TFM blocker** — the adopter targets `net10.0` / `net10.0-windows`, exactly what the kit ships.
- **Stage 0 works from a clean feed**: `Core` + `Ipc` on a bare `net10.0` project, `Windows` on
  `net10.0-windows`.
- **Stage 1 spike compiled clean against the PUBLISHED package** (a `PackageReference`, not a project
  reference — that is what surfaced the next point). An app's real window-state usage maps onto
  `WindowStateManager` unchanged in shape, and the off-screen guard it would have kept private is here as
  a pure, unit-testable function.
- **`MSB3277` on a consumer's first build is documented in `ADOPTION.md` Stage 0.** Harmless, and the kit
  already demoted it in its own projects while telling adopters nothing — so "the kit builds clean" was
  never a claim that theirs would. Worth remembering as a pattern: **spike against the published artifact,
  because a project reference hides packaging.**

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

_A8 (iOS published) is CLOSED — 0.5.1 shipped all five packages from one Windows runner,
`Shenora.iOS` included. The macOS pack job was retired unbuilt: only an iOS APP needs a Mac. See
`docs/archive/tasks.md`._

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

### From the first adopter, `UpdateStage` second attempt (2026-08-03)

`Files` (0.5.1) is **adopted and the adopter's stopgap is deleted** — that loop closed cleanly. Went back
for `UpdateStage` intending to write the archive bridge locally rather than wait, and hit a second,
sharper constraint than the per-file source. Filing it because it is about a CLASS of target, not one app.

- [x] **DONE 2026-08-04 — `UpdateStageOptions.BaselinePath`.** Exactly the suggested fix, with one
  improvement found while writing it: `ApplyAsync` now writes the baseline EXPLICITLY and **always**
  excludes it from the overlay, rather than skipping it only when it points outside the root. The
  conditional version would have left a stray copy at `{installRoot}/manifest.json` whenever the baseline
  was configured anywhere else — including inside the root under a different name — so the unconditional
  rule is both simpler and more correct. Default behaviour is pinned by a test that did not exist before
  (nothing asserted `UpdateOutcome.Written` at all), and the two new rules are sabotage-verified in both
  directions: un-skip the overlay and the "pure function of the payload" test fails; read the old hardcoded
  path and the "still READ for removals" test fails. **The archive source below is still owed**, so this
  unblocks the baseline half only — which was the sharper of the two constraints.

  <details><summary>the original filing, kept for the reasoning</summary>

  **`ApplyAsync` writes its baseline `manifest.json` INTO the install root, which rules out any
  target whose bytes are themselves hashed or shipped.** `FetchAsync` stages the release manifest and
  `ApplyAsync` overlays it, so after an apply the tree contains a kit bookkeeping file. For an app
  install tree that is exactly right — the baseline belongs with the thing it describes, and both donor
  implementations put it there.
  But this adopter's targets are **deploy inputs**, not an install tree: two directories whose aggregate
  content hash decides what gets re-uploaded to a cloud account. Its hash walks every file with no
  exclusions — deliberately, because it mirrors the build's own manifest aggregate so the two agree. A
  per-release `manifest.json` inside that tree changes the hash on every release even when the payload is
  byte-identical, so "did the backend actually change?" answers YES always, and a frontend-only change
  stops taking the seconds-long path and takes a full cloud reconcile instead. That breaks a documented
  invariant there ("a part's content must be a pure function of SOURCE, never of build HISTORY"), so the
  adoption cannot proceed on those terms.
  Worth separating what is NOT the problem: staging, per-file SHA verification, the diff, the
  marker-written-LAST ordering and resume are all exactly what was wanted, and better than the
  hand-rolled version. The blocker is purely WHERE the baseline lives.
  Suggested: make the baseline location a parameter — `UpdateStageOptions.BaselinePath`, defaulting to
  `{installRoot}/manifest.json` so nothing changes for the install-tree case, with `ApplyAsync` skipping
  it during the overlay when it points outside the root. That also serves any target the app does not
  want the kit writing into at all.

  </details>

- [ ] **Still open from the earlier round: `IUpdateSource.OpenAsync` assumes LOOSE FILES.** An archive-backed
  source was half of what turned this adoption from "adoptable if you fork the apply" into a straight one;
  the relocatable baseline (above) is now done, so this is the remaining half. **Deferred by owner direction
  — the adopter builds theirs first** (see below), which is the sequence this kit is designed around: port a
  proven implementation rather than guess at one.

**Meanwhile that adopter keeps its own** (owner's call: *"do our own first … in the meantime you should
have your own"*). Its version already does download → extract → verify every file against the release
manifest → swap, is verified against a real published release, and self-heals an interrupted run because
the installed-version stamp is written only after a successful swap. So this is a deferral with a working
alternative, not a hole.

#### Notes for whoever ports it (written 2026-08-03, while it is fresh)

_The INTRUSION half is DONE (2026-08-03) — `UpdateStage.CommitAsync` now rejects a staged file the
manifest does not list, exempted by `UpdateStageOptions.IsUnindexed`. Verified END TO END and it was
worse than this note assumed; the trap that nearly broke it is recorded in `docs/archive/tasks.md`.
The two bullets below are kept because the third one (validate against a REAL release) is still owed by
whoever ports the archive source._

Since the sequence is "port a proven implementation", here is what that implementation learned — the
parts worth taking and the one that will bite.

- **A verifier needs THREE failure modes, not two: tamper (hash mismatch), intrusion (present but
  unlisted), truncation (listed but missing).** `UpdateStage.CommitAsync` currently does tamper and
  truncation — it walks `manifest.Files` and checks each is present and matches — but nothing rejects a
  file in the staged tree that the manifest does not list. That adopter shipped the same asymmetry: its
  native launcher rejected all three from the start and its managed verifier only two, so the identical
  threat model was enforced two ways with one weaker. Worth closing here BEFORE the port, because the
  kit is where "everyone gets the strong version" is decided.
- **⚠ The exclusion list is the part that will bite, and it must be CALLER-SUPPLIED.** An intrusion check
  needs to know which paths a clean archive legitimately carries that the manifest deliberately does not
  index — for that adopter: a bundled `data/` folder, a seeded `<kind>.sha` stamp (indexing it would be
  circular), `app-version.txt` (changes every release), and `manifest.json` itself. That set is a
  property of **whatever generated the manifest**, not of the kit, and `generic-library.md`'s own rule
  applies: ship the mechanism, never the consumer's shape. A baked-in list would be one app's policy
  frozen into everyone's verifier. Suggest a predicate on the options —
  `Func<string, bool>? IsUnindexed` defaulting to "nothing is exempt", so the strict behaviour is the
  default and exemptions are opted into deliberately.
- **The failure mode of getting that list wrong is inverted, which is why it needs real-release
  validation rather than fixtures.** Too LOOSE lets an injected file through; too STRICT rejects every
  honest download — the second is worse, because it breaks for every user at once rather than for an
  attacker, and synthetic fixtures pass it happily since the tester writes both sides. That adopter
  validated against its actual published release and got management 106 files/104 indexed, backend
  103/102, frontend 21/21 with zero would-be-intrusions; the extras were exactly the exempt ones. Worth
  making that a documented step for the kit's own verifier, not a habit one adopter happened to have.

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

_The atomic write and the general TRANSFORM are DONE — `Files` + `FileReplacement` in
`Shenora.Core`, ported from the adopter with its six tests plus seven more for the transform half.
Sabotage-verified both ways; the flush-to-disk gap is stated in the source because no test can cover
it. See `docs/archive/tasks.md`._

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

### From the first adopter, `IPlaybackSession` + MAUI shell adoption (2026-08-04)

The server-backed sibling adopted 0.9.0's `IPlaybackSession` the day it shipped, replacing a
hand-written equivalent it had built the day before. **The contract matched almost exactly** — its
`Set`/`SetState`/`Clear`/`CommandRequested` map one-for-one onto `Publish`/`Report`/`Clear`/
`CommandReceived`, and it had independently arrived at artwork-as-BYTES with the fetch and cache kept
portable above the platform. That convergence is the useful signal; the two gaps below are the whole
delta, and only the first blocks anything.

- [x] **DONE 2026-08-04 — shipped as `SkipForward`/`SkipBackward` + `IPlaybackSession.SkipInterval`
  (default 15 s) + `PlaybackCommandRequest.Interval`.** Additive, no break. Both suggestions were taken:
  the interval is stated once (and IS what makes iOS draw the number), and it rides the request too,
  because iOS sends its own with the event and honouring what arrived beats assuming what was asked for.
  Verified against the OS registries — Android `actions=894` (= 822 + 64 FAST_FORWARD + 8 REWIND, the
  previous number plus exactly the two new bits) and Windows reading back `ff=True|rw=True`; iOS is
  compile-verified, its interval rendering being a device concern like the Island. The note about a FIXED
  15 s is in the XML docs so nobody widens it casually.

  <details><summary>the original filing, kept for the reasoning</summary>

  **`PlaybackCommands` needs SKIP-BY-INTERVAL (±15 s), and it is a functional loss without it.** The
  shipped set is Play/Pause/TogglePlayPause/Stop/Next/Previous/Seek, so an adopter with **long-form
  audio** — an audiobook, a podcast, a lecture, an hour-plus spoken-word track — cannot offer the one
  transport control that shape of content actually wants. Next/Previous are the wrong granularity when a
  "track" is fifty minutes long, and `Seek` is a scrubber rather than a button. The adopter had this
  working and gave it up to adopt the kit, which is exactly the trade the kit should not force.
  - Both platforms already express it and the code exists to port: iOS
    `MPRemoteCommandCenter.SkipForwardCommand`/`SkipBackwardCommand`, where **`PreferredIntervals` is
    also what makes iOS draw the "15" ON the button** — without it the control renders as a bare arrow
    with no interval, which reads as a different feature. Android is
    `PlaybackState.ActionFastForward`/`ActionRewind` + `OnFastForward`/`OnRewind`. Windows' SMTC has
    `FastForward`/`Rewind` too, so all three can answer.
  - Shape suggestion, deliberately small: two more `PlaybackCommands` flags plus an interval the app
    states once (the platforms take a preferred interval, not a per-press value), and `Seconds` on
    `PlaybackCommandRequest` alongside the existing `Position` — mirroring how `Seek` already carries one.
  - ⚠ The adopter's own note, worth keeping: a *fixed* 15 s is what both platforms' UI is designed
    around, so an arbitrary interval is not obviously better than a small allowed set.

  </details>

- [ ] **🔴 BLOCKER: `Shenora.iOS` 0.9.0 cannot be consumed unless the app opts into the Live Activity
  devkit.** An iOS app that references the package and does NOT set `ShenoraLiveActivityViews` fails to
  LINK. Measured on the adopter's first 0.9.0 build:

  ```
  clang++ exited with code 1: Undefined symbols for architecture x86_64:
    "_shenora_activity_end",  referenced from: <initial-undefines>
    "_shenora_activity_free", referenced from: <initial-undefines>
    …
  ```

  - **Mechanism.** `MobileLiveActivities.iOS` declares `[DllImport("__Internal")]` for the five
    `shenora_activity_*` entry points. On iOS `__Internal` is resolved by the STATIC LINKER, not at
    runtime — so the symbols must exist in the final binary. They are produced by
    `ShenoraBuildLiveActivity`, whose condition is `'$(ShenoraLiveActivityViews)' != ''`. No opt-in ⇒ no
    library ⇒ no symbols ⇒ the app does not link. The type is always registered, so nothing can be
    dropped by trimming either.
  - **Why the gate missed it: `samples/Shenora.Sample.Maui` OPTS IN** (its csproj sets the property), so
    the only iOS app the repo builds is the one configuration that works. This is the repo's own
    "a package no gate compiles" objection, one level in — the package compiles, but the package as a
    NON-Live-Activity adopter consumes it does not. A second sample, or the existing one built twice, is
    what would have caught it.
  - **It contradicts the shipped design.** The CHANGELOG says `Unavailable` returns a reason including
    *"shim not linked"*, and the class XML says the type "does nothing useful unless the app's build
    includes the widget extension" — both describe graceful degradation that a link-time `__Internal`
    import makes impossible. The intent is right; the binding mechanism defeats it.
  - **Fix options, cheapest first.** (a) Resolve the five entry points with `dlsym` and report
    `Unavailable("shim not linked")` when absent — this is what the docs already promise, keeps the
    opt-in property meaningful, and makes the type honest on every configuration. (b) Build and link the
    SHIM unconditionally (it does not need the app's views; only the `.appex` does) and keep only the
    widget extension behind the property. (c) Leave it link-time but make the package's presence imply
    the opt-in, which is the worst of the three — every adopter then pays for a devkit they may not want.
  - **Adopter impact right now:** iOS is stuck. Rolling back is not available either, because
    `IPlaybackSession` — the thing being adopted — is new in 0.9.0, so 0.8.0 has no iOS lock screen at
    all. The Android half is unaffected and is verified.

- [ ] **Android: the session's `Token` has to cross the kit/app boundary, and today nothing does.** This
  one BLOCKS the Android half of the adoption, and it is the kit's own documented split that cannot be
  built: *"the kit owns the session and the app owns the notification"* — but a `Notification.MediaStyle`
  is attached to a session by `SetMediaSession(session.SessionToken)`, and `MobilePlaybackSession` exposes
  no token (nothing in `src/` mentions one). So the app can post a notification with buttons, and it
  cannot post the MEDIA notification the split describes.
  - What is lost without it is the visible half the boundary was drawn around: the system media player in
    the shade and on the lock screen is built from the SESSION and adopts a notification only through that
    token. Buttons in a plain notification are not the same surface.
  - Smallest fix is a read-only platform-typed property on the Android implementation (the class is
    already `public` and platform-specific, so this adds no portable surface and no `Shenora.Core`
    vocabulary). A portable `object? PlatformSessionHandle` on the interface would also work but puts a
    weakly-typed member on every shell to serve one.
  - ⚠ Worth stating in the same breath as the split in the class remarks, since the remarks currently
    describe a division of labour the adopter then discovers is not connected.
  - Interim on the adopter's side: keep an app-owned `IPlaybackSession` implementation on Android (the
    kit's `TryAddSingleton` registration makes overriding it a one-liner, which is the right shape and
    was noticed and appreciated) and adopt the kit's on iOS, where the app's half — an `AVAudioSession`
    category — needs nothing from the session.

- [x] **DONE 2026-08-04 — `docs/ADOPTION.md` now has it**, as its own section before the Live Activity
  recipe: both origins in a table (`https://0.0.0.1` Android, `app://0.0.0.1` iOS — **including the iOS one
  the adopter could not measure**, taken from this repo's own device runs), the mixed-content relaxation
  with the code and the reason it is the app's call, the CORS consequence that only appears after fixing
  the first, and the caveat that a non-standard scheme may present as `Origin: null` so an allowlist should
  follow what the server actually logs. The page-diagnostic gap is noted there too.

  <details><summary>the original filing, kept for the reasoning</summary>

  **Document what a MAUI shell's page ORIGIN means for a server-backed adopter — it cost a day.**
  `HybridWebView` serves the bundle from a synthetic virtual host (`https://0.0.0.1` on Android,
  measured), which is a SECURE origin. Two separate consequences follow, and both present as the same
  useless symptom — a bare `TypeError: Failed to fetch`:
  1. **Mixed content.** Every request to a plain-`http` backend is blocked outright. On Android the app
     can fix this itself (`MixedContentHandling.AlwaysAllow` appended to `HybridWebViewHandler.Mapper`),
     and that is arguably where it belongs — it is a real security relaxation and the kit should not
     make it silently. But nothing SAYS so, and the engine only logs it as a `[warning security]` line
     that is invisible without a devtools attach.
  2. **CORS.** After the relaxation the request leaves the device and the *response* is then withheld,
     because the backend has never heard of that origin. This one the app must fix server-side.
  - Neither is a kit defect, and neither needs a kit API — **a paragraph in `ADOPTION.md` would have
    saved the day**: "a server-backed adopter must allowlist the hybrid origin, and relax mixed content
    if the backend is http". Suggest stating the origins per platform, since an adopter cannot guess
    them and they are not obviously discoverable. ⚠ iOS's was NOT measurable from the adopter's machine
    (no `ios-webkit-debug-proxy`; see below), so the kit stating it is worth more than it sounds.
  - The adopter's workaround for the measurement gap was to port this repo's own `PageDiagFacade`
    pattern — page → host over IPC, host writes to the device log. That it was needed twice, in two
    repos, for the same reason (WebKit does not forward page `console.*`) is the two-consumer signal for
    **shipping a tiny page-diagnostic facade in the kit** rather than leaving every adopter to rebuild
    it. Filed as an observation, not a request — it is three lines to write and possibly not worth a
    public type.

  </details>

- [ ] **STILL OPEN from that round: should the kit SHIP a page-diagnostic facade?** Two repos have now
  built the same three lines for the same reason (WebKit does not forward page `console.*`), which is the
  two-consumer signal — but the adopter filed it as an observation and explicitly doubted it earns a public
  type. `generic-library.md`'s bar is that every public type earns its keep, so the honest question is
  whether a documented PATTERN (now in `ADOPTION.md`) is the right shape instead of an API.

- Noted for `DM3`, not a request: that adopter is a **second consumer for the conversion**, and its case
  is the interesting one — it converts SERVER-side today and would rather the phone decided for itself,
  because Android codec support is vendor-declared per DEVICE and the server is therefore guessing on
  the client's behalf. The planner already moves that decision to the right machine; the conversion is
  what it cannot yet act on.

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

  **SAVE IS DONE (2026-08-03) — `SaveAsync(options, write)` on all three shells**, proven on a device
  and a simulator with matching bytes. `ACTION_CREATE_DOCUMENT` on Android through AndroidX's
  activity-result REGISTRY (not `RegisterForActivityResult`, which cannot be reached from a DI-resolved
  service), `UIDocumentPickerViewController` on iOS, both in `Platforms/` as a `partial` method so a
  fourth shell cannot compile until it decides what save means. `SaveFileAsync` keeps refusing on
  mobile, and that is the correct answer, not a gap: "give me a PATH" has no mobile expression. Record
  in `docs/archive/tasks.md`; adopter guidance in `ADOPTION.md` Stage 5.

  **What is still open is narrower again: only `OpenFolderAsync`** — and see the D35 note below, which
  argues it should stay closed. `FolderPicker` lives in **CommunityToolkit.Maui** (checked by
  compiling), a UI-component package D13 forbids, so Android would need raw
  `ACTION_OPEN_DOCUMENT_TREE`.
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

_D1 (harvest before designing) is DONE — `docs/2026-08-03-shenora-media-design.md`. All three read;
two of them credit each other in their own XML, so the bar is met on evidence. The disagreements were
indeed the interesting part, and the harvest **changed the scope twice**: playback is the real driver
(not thumbnails), and a "transcode layer" turned out to be a composition of `IMissionScheduler` +
`PathClaims` + `Files.BeginReplace` plus ONE new pure function. Read §0d–§0f before designing anything._

> **OWNER DIRECTION (2026-08-03): the driver is NATIVE video/audio playback** — *"web does not have full
> support on video/audio types"* — and the mobile gap is the reason the item exists, not a deferrable
> half: *"mostly no mobile thats an issue, so thats why we here"*.
>
> DIRECTION (owner, 2026-08-03, the shape): *"in the end our plan is to make a url that can play in video
> element … so we not even making a video player yet, thats the react part, or depends on how the adopters
> will design"* — routed as `app://video?src=…`. And on the engine: *"I prefer to use engine, because mobile
> library is not stable to support different type of media but if we use engine we have the control"*, with
> a platform player failing on roughly a third of a real collection.

_D2–D5 as originally written are SUPERSEDED. The harvest settled placement (D40), consumption and
versioning (D41), engine strategy (D42) and the contract split (D43), and the owner settled the shape:
**the deliverable is a URL a `<video>` element can play**, not a player. **Read the design doc's
"THE DESIGN, in one place" section before anything else** — everything below it there is the trail, and it
contains intermediate positions that were corrected._

_D2a is DONE — the exchange contracts now live in `Shenora.Core` (commit `8d23dd1`, a documented break)._

_DM1 (the critical path — answer a `Range` with real headers on each mobile shell, and prove a real file
**plays AND seeks**) is **CLOSED 2026-08-03, on both shells** — `docs/archive/tasks.md`. **Read `D44` before
building anything else here**: it carries the three rules the device runs produced, and two of them
contradict what this file and the design doc previously asserted. In one line: a reserved PATH on the page's
own origin (never a custom scheme) · the PORTABLE `SetResponse` with a header dictionary (`PlatformArgs` is
not needed) · 206 + `Content-Range` + `Accept-Ranges` · and a body that is **UNSLICED on Android but SLICED
on iOS and on WebView2**, because the platforms apply the range start differently. That asymmetry was read
as the justification for D40's per-platform media packages; **D45 corrected that** — it is a property of the
INTERCEPTION, so it became `Core.WebViewRangeDelivery` and the packages had nothing left. Getting it wrong
**looks correct on any faststart file**, so two probes are the regression tests to keep: the control pair in
`samples/Shenora.Sample.Maui/MediaRangeProbe.cs` and `InterceptorProbe` in the desktop sample._
_**DM2 is DONE** — `MediaPlaybackPlanner` in the new `Shenora.Media`, per STREAM (D42), pure and I/O-free
with 14 tests. The codec sets are the app's (`MediaPlaybackPolicy`); the kit ships no list, because the
right one differs per player and per DEVICE on Android. `MediaProbeResult`/`MediaStreamInfo` (best-effort,
all-nullable) and `MediaCacheKey` (identity + length + mtime, 12 tests) landed with it — the latter is the
one piece DM3 said was missing. See `CHANGELOG.md` `## Unreleased`._

### D — media: the interceptor is DONE; the conversion is not

_**D45 is CLOSED and archived** (`docs/archive/tasks.md`): interception is a middleware pipeline, the contract
lives in `Shenora.Core` with a working file middleware, and all three shells implement it —
`WebViewHost.Interceptor` on Windows, `MobileWebViewInterceptor` on Android and iOS. `Shenora.Media` is media
LOGIC only; `Shenora.Media.Android`/`.iOS` were deleted before ever being published (8 ids → 6). So
`<video>`/`<audio>`/`<img>` over local files need NO media package, which is what makes the content family
opt-in. Verified on both devices AND through a real WebView2 on the desktop. Read D45 before touching any of
it — it overturns the layering the media design doc assumed._

_`MediaPlaybackPlanner` (D42, per-STREAM) + `MediaProbeResult`/`MediaStreamInfo` +
`Core.DerivedCacheKey` shipped with it; the planner's codec sets are the APP's policy — the right one differs
per player and per DEVICE on Android._

- [ ] **DM3 — the conversion, COMPOSED not built.** `IMissionScheduler` (the long run) +
  `PathClaims.Exclusive` (convert once, never twice) + `Files.BeginReplace` (atomic output) +
  `Core.DerivedCacheKey` (identity + length + mtime — the piece DM3 used to say was missing, and it now
  ships). Everything it needs already exists, and `Files.BeginReplace`'s own XML already names this
  composition. Progress rides the existing notification pipe as `SOURCE_PROGRESS`/`READY`/`FAILED`.
  It arrives as a MIDDLEWARE on the D45 pipeline, in `Shenora.Media`, layered over the file middleware Core
  already serves with — not as a second serving path.
- [ ] **DM4 — the REMOTE authorization seam only; the local half is DONE.** Path containment shipped as
  `Core.WebViewFiles.ResolveContained` (generic, fail-closed, tested) because the page supplies the path.
  What is left is the SSRF surface: a fail-CLOSED guard for *may the host FETCH this url on the page's
  behalf*, the shape `NavigationGuard` already has — denying with no policy AND when the policy throws,
  because the host can reach addresses the page cannot. ⚠ It was written once as
  `MediaAccess.IsRemoteAllowed` and deliberately dropped in the D45 re-layer: nothing in the kit fetches a
  remote resource for a page, so it had no caller. **Land it WITH the middleware that needs it, not before**
  (D15) — a public seam with no consumer is exactly what the last one was.

- [ ] **`Shenora.Media.Windows` — NOT created (owner: "no need … for now"), and D45 makes it even less likely.**
  The desktop shell serves ranges correctly two ways now: `WebViewDeferredScheme` (where the
  206/`Content-Range` logic was proven first) and `WebViewHost.Interceptor` + `UseFiles`, which is the
  portable one. A `Media.Windows` package would hold a WebView2 args adapter and the `Sliced` constant —
  both of which `Shenora.Windows` now owns outright. Add it only when a desktop consumer shows something it
  genuinely cannot express today (a native surface, an engine binding), rather than for symmetry with the
  mobile pair — which no longer exists either. **Every public type earns its keep**
  (`generic-library.md`); package symmetry is not a reason.

_**The gate weakness and its general question are both CLOSED (2026-08-04)** — full account in
`docs/archive/tasks.md`. The audit found two more real holes: `check-sensitive` failed closed on a MISSING
patterns file and OPEN on an empty one (and logged-but-ignored a pattern that would not compile — partial,
permanent and invisible), and **nothing compared the two hand-maintained definitions of "shipped"**
(`packableProjects` vs `<IsPackable>true</IsPackable>`), whose dangerous direction is a new package that
gates its surface correctly and then silently never ships._

_The reusable smell, worth more than either fix: **a presence-only coverage check is safe only when the
same set that drives it also drives the content check.** The runtime API baselines were never vulnerable
because their case source IS the baseline files; the metadata ones were, because that case source is a
hand-maintained list. So the question to ask a coverage gate is not "does it check content" but "is the
coverage set the same set as the content set?"_

_Thumbnails and image resize are DEFERRED with the analysis already done — D43. They cost 0 MB on every
platform and need no engine, so they are cheap to add later, and the player does not depend on them._

### Platform integration — OS-level logic, measured by how little native code an app writes

> DIRECTION (user, 2026-08-04): *"we are doing a library, for multiple platform, so if the library can
> provide powerful devtooling that will be even better so for example rely on less swift code for ios
> (dynamic island) and support platform logic like now playing"*

This widens the kit's scope in a specific way and it is worth stating precisely, because it is easy to
read as "add features". The shell packages so far hold what an app needs to *host a page* — windows, IPC,
dialogs, interception. This direction says they should also hold what an app needs to *be a citizen of the
OS*: the lock-screen transport, the system media controls, the live-activity surface. The stated measure is
the useful part — **not "does the kit expose the API" but "how much native code does the adopting app still
have to write"** — which is the same test D45 passed by moving interception into the shells (an adopting app
writes `interceptor.UseFiles(...)` and no platform code at all).

Two named examples, and they are NOT the same difficulty. Being honest about which is which matters more
than being encouraging about both.

- [ ] **NOW PLAYING — the textbook fit, and the natural successor to D45.** Once a page can play a local
  file, the OS needs to know what is playing: iOS `MPNowPlayingInfoCenter` + `MPRemoteCommandCenter`,
  Android `MediaSession`, Windows `SystemMediaTransportControls`. One portable contract in `Shenora.Core`
  (metadata + playback state + position + which transport commands are supported), three shell
  implementations — exactly the `IWebViewInterceptor`/`IFileDialogs`/`IUiDispatcher` shape the kit already
  has three times over, so the precedent, the layering law and the review instincts all transfer.
  - **The interesting design half is the DIRECTION of travel.** Metadata flows app → OS, but commands flow
    OS → app: a lock-screen pause, a headphone double-tap, a car stereo's next-track. That makes it an
    EVENT SOURCE, so it rides `IEventBus` / the batched notification pipe rather than request/response —
    and it must be a *seam the app answers*, because only the app knows what "next" means. The kit must not
    ship a queue model; that is the app's.
  - **Position reporting is the trap to design for up front.** These APIs want an elapsed time and a rate,
    not a tick stream, and a naive "push the current time every 250 ms" both burns battery and drifts
    against the OS's own extrapolation. The contract should take *(position, rate, timestamp)* and let the
    platform extrapolate — which is what all three actually want.
  - It clears the two-consumer bar the moment a second sibling wants it; media was the first thing three of
    them all needed, so ask before assuming this is only one app's.

_**The Live Activity devkit is DONE and archived** (`docs/archive/tasks.md`): the whole adoption is one
MSBuild property plus four SwiftUI view bodies, verified end to end on the simulator from the OS's own
records. iOS only, deliberately — Android's analogue for media is already `IPlaybackSession`, so its live
surface waits for a real non-media consumer (D15). Recipe and traps in `docs/ADOPTION.md`; mechanics in
`.claude/knowledge/mobile-shells.md`. The one thing still unverified is the ISLAND rendering, which needs
a device: a simulator gives an activity only a lock-screen scene target._

- [ ] **"Powerful devtooling" is the other half of the direction, and it is the part this repo has already
  proved works.** Every platform capability so far became trustworthy the moment it had a HARNESS: `dev.mjs
  android`/`mac` for the device loops, `InterceptorProbe` and `MediaRangeProbe` for the serving seam, and
  the D44 body-rule asymmetry was only ever *found* because a probe could run on both devices. So the rule
  to carry into any of the above: **a platform capability ships with the tooling that drives and observes
  it**, or it ships as an assertion. For Now Playing that means being able to see the OS's own view of the
  session from the dev loop (Android `dumpsys media_session`, and the simulator/device equivalent on iOS)
  rather than trusting that a call succeeded.

**⚠ Nothing here is started, and none of it should jump the queue on its own.** `DM3` (the conversion) is
the live media work, and the harvest rule (D15) still applies: these arrive when a sibling app needs them,
which is what stops the kit growing features nobody adopts. The direction above changes what is IN SCOPE
when that happens, not the order.

### Release hygiene — ✅ both items the 0.6.0 incident earned are CLOSED (2026-08-04)

_**Both DONE 2026-08-04**, and both sabotage-verified in both directions — details in
`docs/archive/tasks.md`. In one line each: `dev.mjs changelog` now FAILS a release whose `## Unreleased`
is missing or has no bullets, with the message pointing first at the likelier cause (*the commits you
mean to ship are not on the remote*); and `doctor` now sweeps `git ls-files` for names outside printable
ASCII, which is what nothing was doing when a mangled shell redirect became a tracked file._

_⚠ One lesson from doing them that generalises: **my first sabotage harness was wrong, not the gate.**
It spliced at `indexOf('## Unreleased')`, which found the phrase in the intro PROSE rather than the
heading, so five cases "failed correctly" for the wrong reason and one that should have stayed quiet
also failed. A gate that reports the right verdict via the wrong path is indistinguishable from a
working one until you check WHICH message it printed. Read the message, not just the exit code._

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
