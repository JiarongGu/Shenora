---
name: skill-loader
description: Check whether a Shenora skill already covers the task (loading the right docs/knowledge, finding a code pattern or extraction source, running an end-of-phase review, or logging a fix) so you use the skill instead of hand-rolling. Use at the start of a task to pick the right tool.
---

# skill-loader

Prefer a skill over hand-rolling when one fits. Match the task, then invoke it.

## The skills

| Skill | Use when |
|---|---|
| `/doc-loader` | starting a non-trivial task — load the docs + on-demand knowledge rules it touches |
| `/pattern-finder` | adding a unit shaped like an existing one — find the exemplar (in this repo, a sibling source, or the family template) to mirror |
| `/phase-review` | finishing a development phase — adversarial review over the diff before committing |
| `/fix-log` | after landing a non-trivial bug/regression fix — record root cause + verification |
| `/skill-loader` | this table — pick the right skill for the task shape (you are here) |

## Steps

1. Match the task to a row. A non-trivial start usually pairs `/doc-loader` + `/pattern-finder`;
   wrapping a phase → `/phase-review`; after a fix lands → `/fix-log`.
2. If one fits, invoke it. If none does, proceed manually — don't force it.
3. This table is the one place that lists every skill — when you add or remove one under
   `.claude/skills/`, update this row set AND the `description` above, or the new skill never
   triggers. (Scaffolding skills — e.g. `new-ipc-module` — get added once Shenora's own patterns
   exist; see `docs/ROADMAP.md` Later.)
