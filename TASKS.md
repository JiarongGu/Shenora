# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/archive/tasks.md`](docs/archive/tasks.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.8.0 PUBLISHED (2026-08-03).** Five NuGet packages + `@shenora/react` on npm, verified live on
the feed. It carries the **D2a relocation** — `WebViewResourceRequest`/`Response`/`ByteRange` moved
`Shenora.Windows` → `Shenora.Core`, a documented break whose whole migration is one `using Shenora.Core;`.
That release exists because the move had shipped in the DOCS and not in the PACKAGES, which would have met
the first adopter as a compile error.

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

## Open

> **WORK ORDER (owner, 2026-08-03): ~~E1~~ → ~~C~~ → D (media).** **E1 and C are both DONE** — the session
> bundle seam (D38) and a portable `SaveAsync` proven on all three shells, entries in
> `docs/archive/tasks.md`. **Media is now the live work**, and its D1 harvest is done too: the design is
> settled in `docs/2026-08-03-shenora-media-design.md` and the open items are `DM2`–`DM5` below.
> **`DM1` — the critical path — is CLEARED (2026-08-03):** a real file plays AND seeks through the
> interception seam on both mobile shells. The capability everything else assumed now exists, and the rules
> it produced are **D44**. **DM2 is next**, and nothing below should be designed without reading D44 first —
> it contradicts two things the design doc used to assert.
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

- [ ] **`ApplyAsync` writes its baseline `manifest.json` INTO the install root, which rules out any
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
  Same theme, still open from the earlier round: `IUpdateSource.OpenAsync` assumes loose files. An
  archive-backed source PLUS a relocatable baseline together turn this from "adoptable if you fork the
  apply" into a straight adoption.

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
building DM2–DM5**: it carries the three rules the device runs produced, and two of them contradict what
this file and the design doc previously asserted. In one line: a reserved PATH on the page's own origin
(never a custom scheme) · the PORTABLE `SetResponse` with a header dictionary (`PlatformArgs` is not needed)
· 206 + `Content-Range` + `Accept-Ranges` · and a body that is **UNSLICED on Android but SLICED on iOS**,
because the two platforms apply the range start differently. That last asymmetry is the measured
justification for D40's per-platform media packages, and getting it wrong **looks correct on any faststart
file** — so the probe's control pair in `samples/Shenora.Sample.Maui/MediaRangeProbe.cs` is the regression
test to keep._
_**DM2 is DONE** — `MediaPlaybackPlanner` in the new `Shenora.Media`, per STREAM (D42), pure and I/O-free
with 14 tests. The codec sets are the app's (`MediaPlaybackPolicy`); the kit ships no list, because the
right one differs per player and per DEVICE on Android. `MediaProbeResult`/`MediaStreamInfo` (best-effort,
all-nullable) and `MediaCacheKey` (identity + length + mtime, 12 tests) landed with it — the latter is the
one piece DM3 said was missing. See `CHANGELOG.md` `## Unreleased`._

- [ ] **DM3 — the conversion, COMPOSED not built.** `IMissionScheduler` (the long run) +
  `PathClaims.Exclusive` (convert once, never twice) + `Files.BeginReplace` (atomic output) + a
  path+size+mtime cache key. Everything but the key helper already ships, and `Files.BeginReplace`'s own
  XML already names this composition. Progress rides the existing notification pipe as
  `SOURCE_PROGRESS`/`READY`/`FAILED`.
- [ ] **DM4 — the two authorization seams, because ONE interceptor serves local AND remote sources.** Remote
  is an SSRF surface → a fail-CLOSED guard, the shape `NavigationGuard` already has. Local is a
  path-containment surface, because the page supplies the path → the generic version of the
  `ResolveContained` fix, not a second hand-rolled one. Neither is optional, and both are classes the
  review checklist hunts for by name.
_**DM5 is DONE for the shipping platforms** — `Shenora.Media` + `Shenora.Media.Android` +
`Shenora.Media.iOS`, all three packing at 0.8.0, all three with the full checklist (inline `IsPackable`,
`packableProjects`, description, solution entry, API baseline, README row + graph, `ARCHITECTURE.md`,
lexicon). The mobile pair is ONE shared source (`src/Shenora.Media.Mobile/`) differing by one compile
symbol, and a package built with neither fails **`#error` at compile time** — sabotage-verified, so a third
platform cannot inherit a guess. They reference `Shenora.Media` and the MAUI SDK but **NOT the shell
packages**: D40 left that edge "to determine when building", and built, it does not exist. The D41 tripwire
is armed and sabotage-verified (`NU1201`, cascading to the MAUI sample)._

- [ ] **`Shenora.Media.Windows` — deliberately NOT created, and it may never need to be.** The desktop
  shell already serves ranges correctly through `WebViewDeferredScheme` + `WebViewResourceResponse`, which
  is where the 206/`Content-Range` logic was proven first. A `Media.Windows` package would hold a WebView2
  args adapter and the `Sliced` constant — i.e. it would duplicate what `Shenora.Windows` already does. Add
  it only when a desktop consumer shows something it genuinely cannot express today (a native surface, an
  engine binding), rather than for symmetry with the mobile pair. **Every public type earns its keep**
  (`generic-library.md`); three-package symmetry is not a reason.

- [ ] **⚠ A GATE WEAKNESS found while landing the mobile pair, and worth remembering beyond media.**
  `MetadataSurfaceTests.MetadataAssemblies()` is a HAND-MAINTAINED list, and the coverage test only checked
  that a baseline FILE existed — so two brand-new platform packages, seeded with EMPTY baselines and missing
  from that list, passed every gate with zero surface coverage. Closed by making the coverage test require a
  NON-EMPTY baseline. Left open here as the general question: **which other gates are satisfied by the
  presence of a file rather than its content?**

_Thumbnails and image resize are DEFERRED with the analysis already done — D43. They cost 0 MB on every
platform and need no engine, so they are cheap to add later, and the player does not depend on them._

### Release hygiene — two items earned by the 0.6.0 incident (2026-08-02)

- [ ] **Gate a release on `## Unreleased` having CONTENT.** v0.6.0 published 0.5.1's code because the
  work was committed locally and never pushed: the workflow released the remote's tree, bumped the
  version correctly, and had no `## Unreleased` to stamp — so it shipped with no changelog entry at all.
  **The empty section is the signal, and it was there and unused.** Cost of a false stop is one changelog
  line; cost of a miss is a burned version number, which this repo has now done twice (0.2.0 consumed,
  0.6.0 released stale). Make it FAIL rather than warn — releasing the wrong code is correctness, not
  style — and sabotage-verify that it stays QUIET on a legitimate release, which is the direction the
  0.4.0 gates all got wrong. Full account in `CHANGELOG.md` under `## 0.6.0`.
- [ ] **Remove the stray tracked file `\357\200\252\357\200\252This`** — 0 bytes, name is two
  Private-Use-Area characters then "This", almost certainly a mangled shell redirect. Added in
  `11e3469`, so it is in the public repo and in the 0.6.0 tree. Harmless (no csproj references it, so it
  reaches no package) but it is junk in a public repo. Worth asking the related question while there:
  **nothing looks for stray files** — neither `doctor` nor the sensitive scan — so a `git ls-files`
  sweep for names outside a sane charset may be worth adding rather than just deleting this one.

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
