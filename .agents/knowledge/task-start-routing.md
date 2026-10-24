---
name: task-start-routing
applies_when: starting a non-trivial task here, or running /doc-loader or /pattern-finder — which discovery skills to run, which documents route a task in Shenora, and where an exemplar is found when this repository has none of the shape
enforces: run the discovery skills whose answer you do not already have; DECISIONS.md is read by number, never whole; an extracted component is mirrored from the sibling that proved it, with its post-mortem comments kept
---

# Starting a task here — the gate, the routers, and where an exemplar comes from

**The shared `skills-workflow` rule and the shared `doc-loader` and `pattern-finder` skills say what to do;
this says how much of it, and where it is in Shenora. Route through `docs/README.md` and
`.agents/INDEX.md`; mirror this repository first, then the sibling that proved the component, then
Lyntai. Close the work with `/phase-review`.**

## Why

The knowledge tier is on-demand and the design is standardized, so a task started cold misses an
invariant a knowledge document already holds; read `docs/DECISIONS.md` whole and a constraint earned in
one context is applied where it does not fit (D77). Both skills were this repository's own before it took
up the shared doctrine; their Shenora-specific lines live here so the shared versions could replace them.

## How much of the gate

A **non-trivial** task is a new package or component, an extraction port, a cross-cutting change. Run
`/doc-loader` + `/pattern-finder` + `/skill-loader` FIRST, in parallel, then implement in the loaded
patterns and close with `/phase-review` before committing.

🔴 **The gate is three skills, not a ritual — run the ones whose ANSWER you do not already have.**
`/doc-loader` when you cannot name the docs and knowledge documents the task touches; `/pattern-finder`
when you are writing a unit shaped like an existing one and do not know which; `/skill-loader` when you
are unsure a skill covers the shape. Running all three on a task whose area you just spent an hour in
buys nothing and costs the budget the actual work needs.

**Skip it entirely for genuinely trivial edits** (a typo, a one-line fix, a doc tweak). ⚠ **And for a
CONTINUATION**: a follow-up in the same area, in the same session, is already inside the context the gate
exists to load. Never hand-roll what a skill covers. When you finish something reusable, reach for a GATE
or a TEST first and a rule only when neither can exist (`work-loop.md`).

## Loading documents (`/doc-loader`)

- **The router is `docs/README.md`'s "Read this when…" table** — it gains rows as documents land, so it
  outranks any list here. Usually one or two of: `docs/ARCHITECTURE.md` (as-built map),
  `docs/design/<subsystem>.md` (how a subsystem works, what was measured), `docs/DECISIONS.md` (why, and
  what the kit IS — D53–D56), `TASKS.md` (open work), `docs/ADOPTION.md` (bringing an app onto the kit),
  `docs/REVIEW-GUIDE.md` (reviewing anything).
- 🔴 **`docs/DECISIONS.md` is read by number, never whole.** Scan its generated index at the top, open
  the `D<n>` the task touches, leave the rest shut.
- **The knowledge rows are `.agents/INDEX.md`'s Knowledge table** (generated from each document's
  frontmatter). Match the task against *Applies when* and read every matched `.agents/knowledge/*.md`.
- **Private context:** a task touching a sibling app, a real path or session continuity also reads
  `local/CLAUDE.local.md` and `local/PROJECT_NOTES.md` (untracked).

## Finding an exemplar (`/pattern-finder`)

The usual shapes here are a packable csproj, an options record, an IPC envelope or middleware piece, a
WinForms service, a React bridge hook, a unit test, a `devtools` verb, a knowledge document. Search in
this order:

1. **This repository** — once Shenora has one of the shape, mirror Shenora's own.
2. **The sibling that proved it** — `extraction-sources.md` says which app proved which component;
   `local/EXTRACTION-MAP.md` (private) holds the real paths. Port the proven file, keep its post-mortem
   comments, fix only the gaps listed there.
3. **Lyntai, the family library template** — packaging, csproj/props shapes, release and devtools
   scripts, test patterns (API-surface baselines, contract-test bases). Real path in
   `local/CLAUDE.local.md`.

For an IPC module or a native service, `/new-ipc-module` and `/new-native-service` walk the whole chain.
