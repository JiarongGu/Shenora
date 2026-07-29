# Skills workflow — start non-trivial tasks through the skills (the gate)

The knowledge base is on-demand and the design is standardized, so don't dive into code cold. At
the start of a **non-trivial** task (a new package/component, an extraction port, a cross-cutting
change), run the discovery skills FIRST — in parallel — then act:

1. **`/doc-loader`** — route the task to the docs + the matched on-demand `.claude/knowledge/`
   rules, and read them.
2. **`/pattern-finder`** — find the exemplar to mirror: in this repo once one exists, else the
   proven sibling source (via `extraction-sources` + `local/EXTRACTION-MAP.md`) or the family
   library template.
3. **`/skill-loader`** — check whether a skill already covers the task shape.

Then implement in the loaded patterns, and close the phase with **`/phase-review`** before
committing.

**Skip the gate only for genuinely trivial edits** (a typo, a one-line fix, a doc tweak) — forcing
it there is noise. The point stands: never hand-roll what a skill covers, and never miss an
invariant that an on-demand knowledge rule holds. When you finish something reusable, evolve the
system — add a rule (`node devtools/dev.mjs knowledge new <name>`) or a skill so the next task
starts ahead.
