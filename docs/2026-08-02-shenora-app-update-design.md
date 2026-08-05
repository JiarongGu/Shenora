# Staged application updates — design

**Status: DESIGN, nothing built.** Written 2026-08-02 after owner direction to take the launcher
question properly and to draw on the primary desktop sibling's implementation rather than reason
about it from the outside. Retire this doc once the work lands (`docs/README.md`'s rule); the WHYs
move to `docs/DECISIONS.md`, the surface to `docs/ARCHITECTURE.md`.

**This supersedes the "C++ launcher template" line in `ROADMAP.md`'s `### Later / candidates`,** and
it corrects an assessment made earlier the same day that never opened the source. That assessment
claimed one sibling had a launcher and that the capability was three shallow jobs. Both are wrong,
and §0 is why.

## §0 Evidence — two apps solved this independently, and arrived at the same architecture

| | Primary desktop sibling | Sonora (public) |
|---|---|---|
| Native launcher | `main.cpp` 142 · `dotnet_runtime.cpp` 170 · `updater.cpp` 234 | `main.cpp` 76 · `dotnet_runtime.cpp` 116 · `updater.cpp` 145 |
| Model | app downloads + stages; launcher applies on next start | identical |
| Staging | `{root}/.update/staged` + `ready.json` marker | `{root}/.update/staged` + `ready.json` marker |
| Manifest entry | `{path, size, sha256}` | `ManifestFile(Path, Sha256, Size)` |
| Apply | robocopy overlay + manifest-diff removal | robocopy overlay + manifest-diff removal |
| Install root reaches the app via | `--app-root` argument | `SONORA_ROOT` environment variable |
| Runtime bootstrap | detect .NET, silent install if absent | same |

Two independent implementations, the same three-file launcher shape, the same two-phase protocol,
the same manifest triple, the same `.update`/`ready.json` contract. The two-consumer bar
(`generic-library.md`) is met **on evidence**, not on direction — this is the "solved N times and
differently" pattern that justified the mission scheduler.

**Why two phases at all, in one sentence:** a running process cannot replace its own executable on
Windows, so the app downloads and verifies while it is alive, and something that runs *before* it
applies the result.

## §1 The claim — the capability is three parts, and only ONE of them is native

The earlier assessment treated this as "a C++ launcher", which is why it reached the wrong answer.
Split by what each part actually needs:

1. **Stage (portable .NET).** Check a release source, diff manifests, download changed files,
   extract, **sha256-verify every staged file**, write `ready.json`. No native code, no Windows API.
   This is the bulk of the logic and all of the risk.
2. **The manifest contract (pure data + a pure function).** `{path, size, sha256}` per file, and a
   diff producing added / updated / removed + total download size. Both apps already have this; both
   wrote it twice (a C# model and a C++ parser) because the two phases are in different languages.
3. **Apply (native, small).** Overlay the staged tree, delete what the new manifest dropped, clear
   staging, start the app. Must be native because it runs when .NET may be absent and must replace
   files the app holds open.

Parts 1 and 2 are **portable `Shenora.Core` material** and fully covered by this repo's existing
gate. Part 3 is ~150 lines of C++ that no gate here compiles — see §5.

## §2 The topology decision — Sonora's separation is the one to take

The two apps diverge in one structural way, and it decides how much can go wrong:

- **Primary sibling:** launcher at `{install}/App.exe`, runtime at `{install}/libs/App.App.exe`, and
  the update overlays **the whole install root**. The launcher is therefore inside its own update
  target, which costs four separate guards: robocopy `/XF` self-excluding the running image
  (resolved dynamically via `GetModuleFileNameW`, never hardcoded), a launcher-basename guard on the
  removal step, a rule that the build manifest MUST list the launcher, and an orphan-cleanup pass for
  a renamed predecessor. That rule was earned in production: omitting the launcher from the new
  manifest made the *old* launcher delete the freshly-copied new one — "the launcher removed itself".
- **Sonora:** launcher at `{root}/Sonora.exe`, app in `{root}/app/`, and the update overlays
  **`app/` only**. The launcher is structurally outside the update target, so none of those four
  guards is needed. They are not fixed — they are *unreachable*.

`extraction-sources.md` says merge, don't pick blindly. Merged answer: **take Sonora's topology**,
because it deletes a whole bug class rather than guarding it, and keep the primary sibling's guards
that topology does **not** cover (§4).

Shenora already owns the app-facing half of this: `AppRootArgument` + `ShenoraPaths` is the
`--app-root` contract. Sonora's `SONORA_ROOT` environment variable is the same idea spelled
differently; the kit keeps the argument form it already ships, and an adopter's launcher passes it.

## §3 What Shenora ships

In `Shenora.Core` (portable, no new package — D2) — **⚠ all of the below shipped in `Shenora.IO`
instead, once D48 (2026-08-05) split the file-operation engine out of Core. Portable either way; the
namespace is `Shenora.IO`:**

- **`UpdateManifest` / `ManifestFile`** — `{path, size, sha256}`, the on-disk contract, plus read/write.
- **`ManifestDiff.Compute(installed, release)`** → added / updated / removed + total download bytes.
  A pure function; the single most testable piece and the one both apps hand-rolled.
- **A staging area** — write staged files under `{root}/.update/staged`, verify **every** file's
  sha256 against the staged manifest, then write `ready.json` **last**. The ordering is the property:
  the marker is the app's promise that the stage is complete and verified, so a launcher that sees it
  never has to re-verify. A crash mid-download leaves no marker and the next run restages.
- **`IUpdateStagingArea`-style state** — `GetState()` → pending + version, so a UI can say "restart
  to apply" without knowing the layout.
- **A release-source SEAM**, not an implementation. Both apps use GitHub releases; that is one
  instance of "somewhere to fetch a manifest and files from", and baking it in would ship a
  consumer's shape (`generic-library.md`).

**Deliberately NOT a new package and NOT a service with a policy.** The kit ships the mechanism; when
to check, whether to auto-check at startup, and what the dialog looks like are the app's.

## §4 The guards that must survive the port

Each of these is an incident, not a preference. A port that drops one re-earns it.

- **Verify the whole stage before writing the marker.** The launcher applies without re-checking.
- **Never let an unreadable new manifest drive removals** (Sonora's guard, absent in the other): an
  empty parse would delete every previously-tracked path — including the files just overlaid —
  producing a corrupt install out of a *successful* copy.
- **Close-all before overlay**, skipping the applier's own PID: a hung instance holds a lock the
  overlay needs. Topology does not cover this one.
- **One stage at a time.** Two concurrent downloads race on `.update`.
- **The restart must exit the old process before relaunching**, or the single-instance guard
  (`SingleInstanceGuard`, already in the kit) makes the new instance bounce off the old one.
- **Removals apply to TRACKED paths only** — never a directory sweep. User data lives in the same
  tree.

## §5 The verification problem, stated rather than waved past

**This repo has no C++ toolchain and `dev.mjs verify` cannot build one.** That objection was the
only correct part of the earlier assessment, and it applies to part 3 alone — parts 1 and 2 are
ordinary C# with ordinary tests.

Two things make it tractable, and both come from the sibling:

- The primary sibling drives its launcher end-to-end from Node with `--apply-and-exit` over sandbox
  directories (`dev.mjs test-update-apply`), asserting a self-update and a topology migration. That
  is a *behavioural* test of a prebuilt binary — it needs no compiler, only the exe.
- So the split is: ship the C++ as a **template** (a repo folder an app copies, not a package),
  document that building it needs MSVC, and make the kit's own gate cover the part it can — the
  manifest, the diff, the staging and the verification.

**A template the kit cannot compile must not pretend otherwise.** If the launcher template ships,
`README`/`ADOPTION` say plainly that it is unverified by this repo's gate and that the adopter's CI
builds it — the same honesty `doc-claims` requires everywhere else.

## §5a AMENDMENT 2026-08-05 — it is a LIBRARY plus a template, and the language question is settled

Owner direction, refining §5's "ship it as a template". **Decision and full reasoning: D50.** This
section records the shape; that entry records why.

**The requirements, as stated:** compatible with **Linux and Windows** (Linux for future need, not
today), **small**, and **one binary per platform is fine** — explicitly modelled on how the mobile
shells work: one shared source tree, N platform artifacts.

**The split is library + template, and §0 already drew the line twice.** Both siblings independently
wrote the same three files, and the boundary between them is not a judgement call:

| file | sibling A | sibling B | which half |
|---|---|---|---|
| `updater.cpp` | 234 | 145 | generic → the **library** |
| `dotnet_runtime.cpp` | 170 | 116 | generic → the **library** |
| `main.cpp` | 142 | 76 | per-app → the **template** |

Two teams, no contact, same seam — and the library is the larger half. What stays per-app is smaller
than "a launcher": the exe name, icon and version resources, the code signature, the topology
constants, and the wording of failure UI. On Windows those are embedded in the binary, so an adopter
applies them as a post-build step before signing; on Linux the same information lives in a `.desktop`
file and touches no binary. **That asymmetry is a build step, not a source fork.**

**Language: C++.** Rust was evaluated properly and lost on the criterion the owner named — it brings
**zero** NuGet-packing benefit (a `.nupkg` is a zip; `runtimes/{rid}/native/` is a folder convention;
MSBuild's RID copy cannot tell what compiler produced the bytes), and once each target builds on its
own CI runner the cross-compilation advantage disappears too. D8 then decides it: two proven C++
implementations exist, one carrying an incident that is expensive to re-earn (§4). See D50.

**Shape:**

- ONE source tree, `std::filesystem` (C++17) for everything portable, Win32 specifics (the
  `GetModuleFileNameW` self-exclusion of §4) behind a thin platform header. **CMake**, so the same
  tree builds under MSVC and gcc/clang.
- **Per-RID binaries from a CI matrix** (`windows-latest` + `ubuntu-latest`), shipped as
  `runtimes/win-x64/native/` + `runtimes/linux-x64/native/` — the mobile pattern, and no
  cross-compilation anywhere.
- **The JSON decision is a conformance question, not a taste one.** The siblings hand-rolled a parser;
  vendoring single-header `nlohmann/json` is the likely better trade. Either way the requirement is
  that it agrees with `UpdateManifest.Parse` — including the two comparison rules that are already
  sabotage-verified on the C# side (separator AND case normalisation on paths, case-insensitive hash
  compare). A second implementation of a parser is a second place to get those wrong.
- **⚠ Sizes below are BANDS, not measurements** — roughly 150–300 KB for C++ against 300 KB–1 MB for
  Rust with size optimisation. Nobody has built either. If this is ever revisited on size, build a
  hello-world of each first and put real numbers here, the way D40 and D46 were decided.

**§5's verification problem is UNCHANGED and gets no free pass from being called a library.** A
library implies the kit owns its correctness while `dev.mjs verify` still compiles none of it. The
answer stays the sibling's: ship the Node harness that drives a PREBUILT launcher with
`--apply-and-exit` over sandbox directories, so the adopter's CI builds once and runs **the kit's**
conformance suite against **their** binary. That is what makes "library" honest rather than a
promotion in name only.

## §6 Not building

- **No release-source implementation** (no GitHub client). Seam only.
- **No update UI**, no auto-check policy, no scheduling.
- **No launcher self-update.** Both siblings decline it; the running image cannot replace itself, and
  Sonora's topology means it never needs to.
- **No delta/binary patching.** Per-file sha256 replacement is what both apps do and it is enough.
- **No installer.** Out of scope; this is in-place update of an existing install.
