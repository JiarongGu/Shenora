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

🔴 **THE GATE IS THREE SKILLS, NOT A RITUAL — run the ones whose ANSWER you do not already have.**
`/doc-loader` when you cannot name the docs and knowledge rules the task touches; `/pattern-finder` when
you are writing a unit shaped like an existing one and do not know which; `/skill-loader` when you are
unsure a skill covers the shape. **Already know the answer? You have run it.** Running all three on a task
whose area you just spent an hour in buys nothing and costs the budget the actual work needs.

**Skip it entirely for genuinely trivial edits** (a typo, a one-line fix, a doc tweak) — forcing it
there is noise. ⚠ **And skip it for a CONTINUATION**: a follow-up in the same area, in the same session,
is already inside the context the gate exists to load. The point stands: never hand-roll what a skill
covers, and never miss an invariant that an on-demand knowledge rule holds. When you finish something reusable, evolve the
system — but **reach for a GATE or a TEST first, and a rule only when neither can exist**
(`phase-workflow.md` has the ordering and the score that earned it). Prose competes for attention with
every other rule; a mechanism runs every time and names the file.
