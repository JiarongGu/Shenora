# devtools/ — Shenora dev + test toolkit

One entry point: **`node devtools/dev.mjs <cmd>`** (family pattern — allow-listed once, no ad-hoc
shell). Project-specific paths/names live in **`project.config.mjs`** (the only file to edit to
reuse this toolkit on another repo). The library version is parsed there from
`src/Directory.Build.props` `<VersionPrefix>` — the one version source.

## Commands

| cmd | what |
|---|---|
| `build` | `dotnet build` the solution + `npm run build` in the react package |
| `test [dotnet\|npm]` | `dotnet test` + vitest (or one side) |
| `verify` | **the "am I done?" gate**: build · test · `check-sensitive --tree` · `knowledge check` + `footprint` (the always-loaded budget — gated since it drifted to its ceiling unwatched) · `doc-drift` · `doctor`, stop at first red |
| `pack` | doctor-fix, then nupkgs + npm tarball → `publish/packages/` (lockstep `-p:Version`, sha256 printed) |
| `doctor [--fix]` | version drift (npm `package.json` + README `## Status` headline vs `VersionPrefix`) **and doc drift** — `--fix` only applies to the version half; every doc-drift finding is a sentence a human has to rewrite |
| — `doc-drift` (run by `doctor`/`verify`; `scripts/doc-drift.mjs --list` to inspect) | **the gate the prose never had.** Every code invariant here has a test and no doc claim had anything; a whole-codebase review found 8 of its ~13 findings in comments and docs. Two PRECISE checks, deliberately not a fuzzy symbol sweep (which would drown the signal and get switched off): (1) the dependency graph drawn in `README.md`/`ADOPTION.md` vs the actual `ProjectReference`s — both files documented a `WinForms → Ipc` edge that has never existed; (2) names in `devtools/retired-names.txt` stated as CURRENT fact. Amendment stacks are the norm here, so a retired name is fine in the PAST tense — add `doc-drift:history` to mark a preserved sketch or rename table. **Add a name to `retired-names.txt` the moment you delete or rename one.** |
| `sample [--dev\|--no-build]` | run the sample desktop app. Builds the packaged frontend first (Production serves the EMBEDDED `wwwroot`, so skipping that ran a stale bundle — see `docs/archive/fix-log.md` 2026-08-02); `--no-build` skips it for a C#-only relaunch. `--dev` = vite URL + CDP port → `.cdp-port`, no bundle build needed |
| `vite` | the sample web dev server (Phase 2+) |
| `shot [name]` | PrintWindow capture of the sample window → `screenshots/` (auto-pruned, see below) |
| `wgc [name]` | occlusion-immune capture (Windows Graphics Capture) — works when the window is hidden/occluded |
| `click <fx> <fy>` | background click at client-rect **fractions** (0–1) — drives the WebView2 UI without CDP |
| `rclick <fx> <fy>` | as `click`, right button |
| `move <fx> <fy>` | background mouse-move to a client-rect fraction (hover states) |
| `drag <fx1> <fy1> <fx2> <fy2>` | background press-move-release between two client-rect fractions |
| `input <args…>` | raw `win-input` passthrough (`list`, `click x y`, `rclick x y`, `move x y`, `drag x1 y1 x2 y2`) |
| `responsiveness <fx> <fy> [--label n] [--duration\|--interval\|--timeout ms]` | click a control, then sample `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` sub-100ms to measure whether the UI thread keeps pumping — the probe behind the one-way-IPC UI-thread claim (see below) |
| `knowledge <check\|footprint\|new <name> [--core]>` | two-tier rule-base doctor: index↔files consistency, always-loaded byte budget, scaffold a rule |
| `clean [--all]` | drop `_*` scratch BUILD OUTPUT (bin/obj/node_modules/out/dist); `--all` also drops probe sources + `publish/` |
| `check-sensitive [--tree|--history]` | scan for dev paths / private names. `--tree` = checkout; `--history` = ONE-OFF audit of every blob, path and commit message |
| `install-hooks` | point `core.hooksPath` at `devtools/hooks` — ONCE per clone |

Releases are cut by the manual **Release** GitHub workflow — see `docs/RELEASING.md`.

## Screenshots are auto-pruned

`shot`/`wgc` keep the newest `shotRetention` captures (24, in `project.config.mjs`) and delete the
rest BEFORE capturing, so the new file is never the one evicted. Every deletion is printed — a
cleanup that removes work silently is worse than one that never runs. Override once with
`--keep N`, or raise the config value while mid-investigation.

Why: captures are gitignored, transient, and cost a keystroke, so the folder only grows — 53 files /
7.5 MB by v0.1.0, and no doc referred to any of them. Evidence in this repo is recorded as NUMBERS
and prose (ROADMAP, FIX-LOG), never as a PNG.

## Scratch folders (`devtools/_*`)

Gitignored probes — the P6 consumers, the adoption adapters, the P7 profile proofs. `docs/ROADMAP.md`
and `docs/archive/tasks.md` describe them as RE-RUNNABLE, so `clean` removes only their regenerable
build output and leaves the sources; `--all` is the opt-in destructive reading. (Verified: after a
`clean` that reclaimed ~60 MB, the profile probe still rebuilt from source and passed.)

## Desktop verification loop (no CDP needed)

Once the sample app exists (Phase 2), the desktop UI is driven natively: **`win-input`** posts
background mouse messages to the WebView2 render surface (fractions of the client rect — no cursor
move, no focus steal, works even occluded), and **`wgc`** / **`shot`** capture the result. Both
target the process from `project.config.mjs` (`processName`, passed via the `DEVTOOL_PROC` env
var — no project name is baked into the C# tools). Typical loop:

```
node devtools/dev.mjs build
node devtools/dev.mjs sample
node devtools/dev.mjs input list           # find the window + its client size
node devtools/dev.mjs click 0.21 0.30      # click a control by fraction
node devtools/dev.mjs shot after-click     # capture → devtools/screenshots/after-click.png
```

(Two lines, not `&&` — the primary shell here is PowerShell 5.1, which has no `&&`.)

## The UI-thread responsiveness probe

`responsiveness` re-proves the claim the one-way IPC design rests on
(`docs/DECISIONS.md` D23): work left in a route's synchronous segment
stalls the UI thread; work handed off (`ctx.Run`) and streamed back does not. It clicks a control
via `win-input`, then samples `SendMessageTimeout(hwnd, WM_NULL, …, SMTO_ABORTIFHUNG, timeoutMs)` —
which returns only once the target thread's message loop actually PUMPS, so a failed call means the
thread is genuinely busy, not just slow. Sampling is sub-100ms by default (both the interval and each
sample's own timeout), because ~1s sampling cannot resolve a multi-second freeze (a real v0.1.0
mistake).

**It refuses to print numbers unless the click actually landed AND the operation actually started** —
four guards, in order, any of which aborts with a nonzero exit and NO sample stats:

1. A live process with a real main window is found (retries briefly — a GUI app can take a moment to
   create its window). Fixes the v0.1.0 mistake where the app never launched, the click never
   arrived, and the probe still reported "0 stalls" as if that were a pass.
2. One baseline `WM_NULL` sample succeeds BEFORE the click (the thread pumps at all) — catches a
   process that exists but is already stuck for an unrelated reason.
3. `win-input`'s own "click ok on hwnd=0x.." confirmation is present in its captured output.
4. **The window TITLE (`GetWindowText`) shows a marker substring (`--title-contains`, default
   `"SLOW running"`) within a short grace period after the click.** Guard 3 alone is NOT sufficient:
   `win-input` reports "click ok" for any coordinate that resolves to *some* leaf window under the
   point, and for a WebView2 host that leaf is the render surface, which spans the whole client area
   — so stale fraction coordinates, a moved button, or a disabled control would all still "land" a
   click and produce a clean `unresponsive=0` result that measured nothing real. `SampleFacade` sets
   the title BEFORE either shape's slow work begins (see `RunningTitleMarker`'s doc comment there),
   deliberately, because in `block` mode the UI thread freezes for the rest of the route and a title
   set only during the freeze would not be observable in time — a window's title, unlike responses to
   most messages, is cached by Windows and readable cross-process even while the owning thread is
   hung (the same reason Task Manager / Alt-Tab still show a hung app's real title). A companion check
   (guard 1b) also refuses if the marker is ALREADY present *before* the click, so a stale "running"
   title left over from a prior, unfinished run cannot be mistaken for fresh evidence.

**Residual limit, named rather than hidden:** guard 4 proves *some* operation matching the title
marker started at roughly the right time — for a busier app with several concurrently-running,
same-marker operations it would not by itself say WHICH one. This sample has exactly one SLOW route
behind both buttons, so the ambiguity does not arise here; a consumer reusing this probe against a
busier app should give each operation kind its own marker. Guard 4 also requires the app under test
to cooperate with the title convention — unlike guards 1-3, it is not fully app-agnostic.

```
node devtools/dev.mjs sample --dev &            # or a second terminal — leave it running
node devtools/dev.mjs input list                # find the SLOW buttons' fractions once
node devtools/dev.mjs responsiveness 0.30 0.85 --label block  --duration 4000
node devtools/dev.mjs responsiveness 0.62 0.85 --label stream --duration 4000
```

Prints `RESULT label=<name> samples=<n> unresponsive=<n> longestStallMs=<n>` — record the numbers in
`local/PROJECT_NOTES.md`, never only in a screenshot (this repo's evidence is numbers and prose). A
wrong-coordinates run instead prints `REFUSING to report - ... the operation never appeared to start`
and exits nonzero, rather than a vacuous clean zero.

## Ground rules (keep the loop prompt-free)

- Drive everything through `dev.mjs` — don't invent ad-hoc shell for these steps.
- Don't prefix commands with `cd` (a compound `cd …` trips the sandbox permission prompt).
- Inspect code with the Read/Grep/Glob tools, not `cat`/`grep` in Bash.
- Throwaway/probe files go under `devtools/` and are prefixed `_` (gitignored); clean them up.
- Captures land in `devtools/screenshots/` (gitignored).

## Layout

- `dev.mjs` — the dispatcher (reads `project.config.mjs`).
- `project.config.mjs` — names, paths, ports + the `VersionPrefix` bridge. **Edit this to re-target.**
- `scripts/` — the standalone tools: `check-sensitive.mjs` (public-repo leak guard; private
  patterns load from gitignored `local/sensitive-patterns.txt`), `knowledge.mjs` (rule-base
  doctor), `shot-window.ps1` (PrintWindow + PW_RENDERFULLCONTENT capture).
- `hooks/pre-commit` — the tracked pre-commit guard (installed via `install-hooks`).
- `win-input/` · `wgc-shot/` · `ui-responsiveness/` — native C# desktop-verification tools
  (background input, WGC capture, the `SendMessageTimeout` stall probe), built on demand into their
  gitignored `bin/`; retargeted via `project.config.mjs`, not their source.
- `_*` / `screenshots/` / `.cdp-port` — gitignored scratch.

Windows gotchas (PS5 UTF-8/BOM, Node `fs.cpSync` crash, WebView2 CDP arg clobber) live in
`.claude/rules/windows-dev-gotchas.md`.
