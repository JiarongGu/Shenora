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
- `win-input/` · `wgc-shot/` — native C# desktop-verification tools (background input + WGC
  capture), built on demand into their gitignored `bin/`; retargeted via `project.config.mjs`,
  not their source.
- `_*` / `screenshots/` / `.cdp-port` — gitignored scratch.

Windows gotchas (PS5 UTF-8/BOM, Node `fs.cpSync` crash, WebView2 CDP arg clobber) live in
`.claude/rules/windows-dev-gotchas.md`.
