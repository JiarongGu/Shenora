# CLAUDE.md — Shenora

Auto-loaded every session. **This is a STARTING PROMPT, not the rule base** — what the kit is, what must
run before development, and where everything else lives. 🔴 **A rule does not belong here.** Anything in
this file is applied to EVERY task whether or not it fits, which is the drift D77 names; a rule belongs in
`.claude/rules/` (core, always loaded) or `.claude/knowledge/` (loaded only when its area matches).

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

Shipped as NuGet packages (`Shenora` + ONE shell per platform, `Shenora.Windows|Android|iOS` — D37 —
plus the native `Shenora.Launcher`, D50) + npm (`@shenora/react`, plus the build-time `@shenora/cli` —
D67), all versioned in lockstep. **There is no optional feature tier** (D53/D55/D65): a capability gets
a FOLDER inside `Shenora`, never a package id. The layer is the namespace — `Shenora.Core.*` (Events ·
Ipc · Shell · WebView), `Shenora.Engine.*` (Files · Missions), `Shenora.Modules.*` (Media · FileDialog ·
Platform · Requests · Update). ⚠ **`Shenora.Ipc` is retired as BOTH a package id and a namespace.**
Code is **extracted from proven sibling apps**, not
invented — the framework's opinions are their measured lessons. Its sibling Lyntai is the AI
brain; **Shenora must never depend on Lyntai**. Two consumption profiles: desktop-only
(postMessage IPC) and server-backed (in-process HTTP for desktop+mobile; shell only).

**THREE HOMES (D77), and there is no archive** (D9): a WHY → `docs/DECISIONS.md`, a subsystem's DESIGN →
`docs/design/`, an INVARIANT → `.claude/knowledge/`; as-built map → `ARCHITECTURE.md`, what's left →
`TASKS.md`, what happened → `git log`.
🔴 **READ `DECISIONS.md` BY NUMBER, NEVER WHOLE — taking the file at once IS the drift** (D77): a
constraint earned in one context reads as universal and gets applied where it does not fit. Scan the
generated index at its top, open the `D<n>` your task actually touches, and leave the other seventy shut.

**Status → `README.md`** (no version number belongs in this file; `dev.mjs doctor` enforces it). The tree
normally runs AHEAD of the published version. Read `CHANGELOG.md` `## Unreleased` before touching the
surface: it carries **breaking changes**. **The package set lives in `docs/DECISIONS.md`'s header table,
once** — never reconstruct it from a chain of entries. Repo public, verified against the
FEED rather than the tree; the kit runs on all three shells, proven on a real iPhone and on Android.
`TASKS.md` has the open work. Growth is harvest-driven (D15) and adoption-driven. **Every public change
is SemVer surface**; 1.0 is a separate deliberate freeze, not yet cut.

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

**Where the constraints that shape development live**, so the loader can scope them to the task: library
discipline and C# naming → `generic-library.md` · extraction-first → `extraction-sources.md` · the version
and the release path → `release-discipline.md` · working files and cross-turn state →
`persist-working-state.md` · commit-only-on-approval → `phase-workflow.md`.
**Layering (D19/D20/D37) is enforced rather than remembered:** `samples/Shenora.Sample.Logic` is a
`net10.0` project that turns RED if a Windows type reaches app logic.

## Dev loop

`node devtools/dev.mjs` with no argument prints every verb — **read that, not a list here**, which is
how the last one went stale. Details: `devtools/README.md`. Verification gate before claiming done:
`dev.mjs verify` (builds, tests, type-checks the sample web app, and runs doctor). CI needs
`SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` — the sensitive guard fails CLOSED and `local/` can't exist there.
