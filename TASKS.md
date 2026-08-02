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

**Where it stands (2026-08-02):** `Shenora.Maui` ships, is in the solution and the gate, and was run
on a real Android device — request/response, batched notifications, the structured error boundary,
the native file picker through the portable `IFileDialogs`, and the mission scheduler serializing a
contended mission. `samples/Shenora.Sample.Maui` hosts the SAME `Shenora.Sample.Logic` as the desktop
sample. Commits `a85280e` · `31b9aaa` · `b87cf9c`; evidence in `docs/ROADMAP.md` `## Done`.

**What that does NOT mean.** An adopter cannot use this yet: the sample speaks the envelope by hand
because the client half does not exist, and the capability rule is a decision with no code behind it.
The items below are ordered by what unblocks an adopter, not by size.

_A1 (the client transport) and A2 (the capability stubs) are CLOSED — `docs/archive/tasks.md`. A2
closed by ANALYSIS rather than code: the hole it described does not exist, because the layering
already prevents it. Read that entry before re-proposing stubs._

_A3 (the adopter guide) and A4 (the decisions) are CLOSED — `ADOPTION.md` Stage 5 and
`DECISIONS.md` D32–D34._

_A5 (`dev.mjs android`) is CLOSED — `devices|connect|deploy|run|log|shot`, with the four traps folded
in. See `devtools/README.md`._

- [ ] **A6 — iOS, and it is genuinely blocked, not deferred.** Needs the `ios` workload AND a Mac
  build host. The Mac EXISTS and is already driven over SSH by a public sibling
  (`devtools/scripts/mac.mjs`: push → xcodebuild → simulator → screenshot back → tap/type). Porting
  that harness is the prerequisite; do it when iOS is actually wanted, and keep its post-mortems
  (codesign fails over ssh because an ssh login is a different AUDIT SESSION; Windows OpenSSH has no
  ControlMaster; `-o pipefail` or `xcodebuild | tail` reports success on a failed build).

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
  and this was held for a real mobile consumer rather than another spike. `MauiFileDialogs` is that
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
  - Folder picking is the same story with a tree URI, and additionally needs a directory abstraction
    (list/read within a granted tree) before it means anything portable.

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
