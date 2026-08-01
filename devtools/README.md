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
| `verify` | **the "am I done?" gate**: build · test · `check-sensitive --tree` · `knowledge check`, stop at first red |
| `pack` | doctor-fix, then nupkgs + npm tarball → `publish/packages/` (lockstep `-p:Version`, sha256 printed) |
| `doctor [--fix]` | version-drift check: npm `package.json` + README `## Status` headline vs `VersionPrefix` |
| `sample [--dev]` | run the sample desktop app (Phase 2+); `--dev` = vite URL + CDP port → `.cdp-port` |
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
and `docs/task-archive.md` describe them as RE-RUNNABLE, so `clean` removes only their regenerable
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
(`docs/2026-07-31-shenora-oneway-ipc-design.md` §7): work left in a route's synchronous segment
stalls the UI thread; work handed off (`ctx.Run`) and streamed back does not. It clicks a control
via `win-input`, then samples `SendMessageTimeout(hwnd, WM_NULL, …, SMTO_ABORTIFHUNG, timeoutMs)` —
which returns only once the target thread's message loop actually PUMPS, so a failed call means the
thread is genuinely busy, not just slow. Sampling is sub-100ms by default (both the interval and each
sample's own timeout), because ~1s sampling cannot resolve a multi-second freeze (a real v0.1.0
mistake).

**It refuses to print numbers unless the click actually landed** — the other real v0.1.0 mistake was
a run where the app never launched, the click never arrived, and the probe still reported "0 stalls"
as if that were a pass. Three guards, in order, any of which aborts with a nonzero exit and NO sample
stats: (1) a live process with a real main window is found (retries briefly — a GUI app can take a
moment to create its window), (2) one baseline `WM_NULL` sample succeeds BEFORE the click (the thread
pumps at all), (3) `win-input`'s own "click ok on hwnd=0x.." confirmation is present in its output.

```
node devtools/dev.mjs sample --dev &            # or a second terminal — leave it running
node devtools/dev.mjs input list                # find the SLOW buttons' fractions once
node devtools/dev.mjs responsiveness 0.30 0.85 --label block  --duration 4000
node devtools/dev.mjs responsiveness 0.62 0.85 --label stream --duration 4000
```

Prints `RESULT label=<name> samples=<n> unresponsive=<n> longestStallMs=<n>` — record the numbers in
`local/PROJECT_NOTES.md`, never only in a screenshot (this repo's evidence is numbers and prose).

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
