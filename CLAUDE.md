# CLAUDE.md — Shenora

Auto-loaded every session. Keep short — details live in `docs/` and `.claude/rules/`.

## What this is

Shenora (神阙) is a **reusable library**, not an app: the desktop "body" (WinForms + WebView2 +
React hosting, typed IPC, modules, window management, native services) for the family's Windows
applications, shipped as NuGet packages (`Shenora.Core|Ipc|WebView2|WebView2.Sessions|WinForms`) + npm
(`@shenora/react`), all versioned in lockstep. Code is **extracted from proven sibling apps**, not
invented — the framework's opinions are their measured lessons. Its sibling Lyntai is the AI
brain; **Shenora must never depend on Lyntai**. Two consumption profiles: desktop-only
(postMessage IPC) and server-backed (in-process HTTP for desktop+mobile; shell only). See
`docs/2026-07-30-shenora-design.md` (+ its `## Amendments`) + `docs/DECISIONS.md` (D1–D22) before
relitigating anything. The 0.2.0 cleanup RETIRED the pre-implementation design docs once implemented
(re-layering → D19/D20, one-way IPC → D23, plus the originating brief): decisions live in
`DECISIONS.md`, as-built shape in `ARCHITECTURE.md`. One design doc survives —
`docs/2026-08-01-shenora-communication-core-design.md`, rewritten to the current shape — because the
code cites its `§` numbers.

**Status: v0.1.0 SHIPPED (2026-07-31)** — five NuGet packages + `@shenora/react` on npm, repo public.
P1–P7 are complete and **nothing is queued**: `TASKS.md` holds two standing habits, the finished work
lives in `docs/task-archive.md`. Growth from here is harvest-driven (D15) and adoption-driven — real
work arrives when a sibling app adopts the kit and hits something. **Every public change is now SemVer
surface**; 1.0 is a separate deliberate freeze, not yet cut.

**Read first:** `docs/README.md` — the memory map that routes any task to the right doc or rule.
**Private companion:** also read `local/CLAUDE.local.md` + `local/PROJECT_NOTES.md` at session
start. Absolute paths and private sibling names stay OUT of tracked files (→ `local/`).

## The gate (§0)

Start any non-trivial task through the discovery skills, in parallel: `/doc-loader` +
`/pattern-finder` + `/skill-loader`. Close a phase with `/phase-review` before committing.
Skip only for genuinely trivial edits.

## Rules — two tiers

Core (auto-loaded): `skills-workflow` · `phase-workflow` · `windows-dev-gotchas` ·
`sensitive-info` · `persist-working-state`. On-demand knowledge rules live in
`.claude/knowledge/` — don't trust any list here to stay current; scan
`.claude/rules/RULES_INDEX.md`'s *Applies when* column.

## Hard rules (family carry-overs)

- **Library discipline:** generalize the consumer's request, never ship its shape
  (`.claude/knowledge/generic-library.md`). No app/domain vocabulary in `src/`. **Headless
  (D13):** no UI component library dependency anywhere — apps bring their own design system.
- **Layering (D19/D20 — IMPLEMENTED in P5.5, shipped in v0.1.0):** `Shenora.WebView2` →
  `Shenora.WinForms` is a **sanctioned downward edge** (the two are one Windows presentation layer;
  the old "never sideways" rule was retired on evidence). Portable contracts + the `IUiDispatcher`
  marshalling seam live in `Shenora.Core` so app logic compiles with no Windows reference —
  enforced, not asserted: `samples/Shenora.Sample.Logic` is a `net10.0` project that turns RED if a
  Windows type creeps into app logic. `docs/ARCHITECTURE.md` now describes this as-built; don't
  "fix" the layering back toward the pre-P5.5 shape.
- **Extraction-first:** prefer lifting proven sibling code — including its post-mortem comments —
  over new abstractions (`.claude/knowledge/extraction-sources.md` + `local/EXTRACTION-MAP.md`).
- One `<VersionPrefix>` (src/Directory.Build.props) is the only version source; npm/README are
  synced by `dev.mjs pack`/`doctor --fix`, never hand-edited.
- C# naming: no `Dto` suffix; contract names mirror the TS names exactly.
- Working files: temp/probes under `devtools/` (`_*` gitignored), private under `local/` —
  never the system temp, never sibling folders elsewhere.
- Knowledge lives in the repo (docs/rules/skills), not assistant memory.
- **Never commit without explicit user approval.** Public repo: run the sensitive guard.

## Dev loop

`node devtools/dev.mjs <build|test|verify|pack|doctor|sample|vite|shot|wgc|click|rclick|move|drag|input|knowledge|check-sensitive|install-hooks>`
— see `devtools/README.md`. Verification gate before claiming done: `dev.mjs verify` (it compiles the
samples, type-checks the sample web app, and runs doctor since P5.5 H5). CI needs
`SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` — the sensitive guard fails CLOSED and `local/` can't exist there.
