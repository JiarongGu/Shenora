# CLAUDE.md — Shenora

Auto-loaded every session. Keep short — details live in `docs/` and `.claude/rules/`.

## What this is

Shenora (神阙) is a **hybrid app development framework — .NET + React**, not an app: the "body"
(shell hosting, typed IPC, modules, window management, native services) that React apps boot their
logic on, across Windows, Android and iOS. ⚠ **It is not a media library, a file library, or any other
single-domain library** — those are capabilities it happens to carry, and a package boundary that
suggests otherwise is making a claim about the product (D53).

🔴 **The thesis, which decides what is worth building (D54):** the differentiator against Capacitor and
Electron is **native .NET capability**. They give you a webview and a JS bridge, so their ceiling is the
web platform plus plugins; this kit's ceiling is .NET's — real threads, real handles, the platform SDKs,
background execution — with React for the interface. **The kit's job is the translation layer between
them: what .NET can do and React cannot.** So the question for any feature is not *"is this useful?"* but
*"can React already do this?"* — if it can, the kit is competing with the web platform and loses.
`.NET does the platform work · React does the interface · the kit owns the seam and the IPC.`

Shipped as NuGet packages (`Shenora.Core|Ipc|Windows|Android|iOS` — ONE shell per platform, D37 —
plus the optional `IO|IO.Compression` hanging off Core, D48; media is IN Core since D53) + npm
(`@shenora/react`), all versioned in lockstep. Code is **extracted from proven sibling apps**, not
invented — the framework's opinions are their measured lessons. Its sibling Lyntai is the AI
brain; **Shenora must never depend on Lyntai**. Two consumption profiles: desktop-only
(postMessage IPC) and server-backed (in-process HTTP for desktop+mobile; shell only). See
`docs/2026-07-30-shenora-design.md` (+ its `## Amendments`) + `docs/DECISIONS.md` (D1–D49 and growing —
check the file, not this range; its header carries the current package set) before relitigating anything. The 0.2.0 cleanup RETIRED the pre-implementation design docs once implemented
(re-layering → D19/D20, one-way IPC → D23, plus the originating brief): decisions live in
`DECISIONS.md`, as-built shape in `ARCHITECTURE.md`. One design doc survives —
`docs/2026-08-01-shenora-communication-core-design.md`, rewritten to the current shape — because the
code cites its `§` numbers.

**Status: v0.10.0 published (2026-08-05)**; the tree is ahead of it. Read `CHANGELOG.md` `## Unreleased`
before touching the surface — it carries **breaking changes**, including `Shenora.Media` ceasing to be a
package (D53). **The package set lives in `docs/DECISIONS.md`'s header table, once** — do not reconstruct
it from a chain of entries, which is how three of them came to state a set that no longer existed. Repo
public, verified against the FEED rather than the tree; the kit runs on all three shells, proven on a real
iPhone and on Android. P1–P7 are complete. `TASKS.md` has the open work. Growth is harvest-driven (D15) and
adoption-driven. **Every public change is SemVer surface**; 1.0 is a separate deliberate freeze, not yet cut.

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
- **Layering (D19/D20/D37):** ONE shell package per PLATFORM. Windows primitives and web hosting
  are one layer — since 2026-08-02 literally one package, `Shenora.Windows`, with the direction kept
  internally (`Shell/` must never depend on `WebView/`). Portable contracts + the `IUiDispatcher`
  marshalling seam live in `Shenora.Core` so app logic compiles with no Windows reference —
  enforced, not asserted: `samples/Shenora.Sample.Logic` is a `net10.0` project that turns RED if a
  Windows type creeps into app logic. `docs/ARCHITECTURE.md` describes this as-built; don't
  "fix" the layering back toward the pre-P5.5 shape or re-split the packages (D37 has the reasoning
  and the measurements that killed the counter-arguments).
- **Extraction-first:** prefer lifting proven sibling code — including its post-mortem comments —
  over new abstractions (`.claude/knowledge/extraction-sources.md` + `local/EXTRACTION-MAP.md`).
- **NEVER touch the version — the release workflow owns it.** One `<VersionPrefix>`
  (src/Directory.Build.props) is the only version source; npm/README are synced by `dev.mjs
  pack`/`doctor --fix`, never hand-edited. **`VersionPrefix` itself is not yours to bump either**, nor
  is the CHANGELOG's `## Unreleased` heading (the workflow stamps it). An empty `version` input means
  "bump from whatever VersionPrefix says", so a hand-bump moves that baseline and SKIPS a release —
  it cost 0.2.0 outright on 2026-08-01. Between releases `VersionPrefix` == the newest `v*` tag;
  `doctor` and a pre-commit guard both enforce it. Cut releases from the Actions tab (`docs/RELEASING.md`).
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
