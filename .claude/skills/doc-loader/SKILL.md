---
name: doc-loader
description: Route a task to the docs and knowledge rules it needs and load them. Use at the START of a non-trivial task to pull the right context (the docs/README router + .claude/rules/RULES_INDEX.md) before exploring code — the knowledge rules are on-demand, so you must load what applies.
---

# doc-loader

The two-tier rule base keeps sessions lean by NOT auto-loading domain rules. Before writing code
for a non-trivial task, load what the task touches — otherwise you'll miss a hard-won invariant.

## Steps

1. **Docs.** Open `docs/README.md`'s "Read this when…" table — it is the SOURCE OF TRUTH and gains
   rows as docs are added, so trust it over this list. Read the 1–2 that match:
   `docs/ARCHITECTURE.md` (**as-built**), `docs/DECISIONS.md` (why it's done this way — and what the kit
   IS, D53–D56), `docs/ROADMAP.md`/`TASKS.md` (status/next),
   `docs/ADOPTION.md` (bringing an existing app onto the kit),
   `docs/REVIEW-GUIDE.md` (reviewing anything). Don't read all of them.
2. **Rules.** Open `.claude/rules/RULES_INDEX.md`. The **core** rules are already auto-loaded.
   Scan the **Knowledge** table's *Applies when* column against the task and `Read` every matched
   `.claude/knowledge/*.md`. Examples: extracting/porting code from a sibling app →
   `extraction-sources` (+ private `local/EXTRACTION-MAP.md`); designing/changing any public API →
   `generic-library`.
3. **Private context.** If the task touches sibling apps, real paths, or session continuity, also
   read `local/CLAUDE.local.md` / `local/PROJECT_NOTES.md`.
4. **Report.** Print a 2–4 line summary: which docs + knowledge rules you loaded and the key
   constraints they impose here. If nothing matched, say so and proceed.

Load only what the task touches — bulk-loading defeats the point.
