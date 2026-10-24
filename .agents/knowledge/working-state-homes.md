---
name: working-state-homes
applies_when: any multi-step task here — at each decision or milestone, deciding where in-progress state, a decision, a gotcha or a done milestone is written; or writing a script that sweeps the repository
enforces: each kind of state has ONE home in this repository; local/PROJECT_NOTES.md is current state, not a session log; a repo-wide script excludes local/
---

# Where working state goes here — one home per kind, and local/ is history

**The shared `persist-working-state` rule (in `AGENTS.md`) says checkpoint as you go. This names the homes
in Shenora, and the two ways they rot.**

## Why

Context is compacted mid-task and drops in-flight state. Earned live in the family: a long session lost
the thread of a multi-step rebuild because progress lived only in the chat. After each decision or
milestone ask: *"if the context compacted now, would the next turn know where we are and why?"*

## The homes

- **In-progress status, what's next, live-run results → `local/PROJECT_NOTES.md`** (private, untracked —
  the session-to-session file, kept current). Work involving the sibling repos is tracked from there too.
- **Load-bearing decisions + rationale → `docs/DECISIONS.md`.**
- **Done milestones → the COMMIT MESSAGE** (+ delete from `TASKS.md`).
- **Invariants and gotchas → `.agents/knowledge/`.**
- **The as-built MAP → `docs/ARCHITECTURE.md`** (never a surface listing — the API baselines and the
  types' XML docs own that).
- **A reusable procedure → a skill in `.agents/skills/`.**
- **NEVER the system temp or scratchpad** for cross-turn state — throwaway probes go under `devtools/`
  (`_*` gitignored).
- **Read it back at session start:** `local/PROJECT_NOTES.md` + `TASKS.md` + routed docs. Stale notes ARE
  the bug this prevents — fix them as you go, keeping private specifics in `local/`.

## How each rots

- 🔴 **`PROJECT_NOTES.md` IS CURRENT STATE, NOT A SESSION LOG.** Where we are, what is next, what is
  blocking. The moment a section stops steering, **MOVE it to `local/archive/`** — a pure move, never a
  rewrite, because `local/` is not in git and the move is the only preservation there is. Left
  unattended it reached 1,678 lines across `SESSION 22`, `ROUND 5/4/2`, `HISTORY` and `SUPERSEDED`, with
  the live status buried at line 97. `dev.mjs doc-shape` checks this.
- **A decision entry is the decision, its why, and the constraint it imposes — nothing else.** A
  measurement, an audit table or a design essay goes in the COMMIT that landed it. Correct a wrong entry
  IN PLACE; never append a dated note narrating what it used to say (`work-loop.md`).
- ⚠ **A repo-wide SCRIPT must exclude `local/` — it is an informal ARCHIVE, not just private.** Its value
  is that it records what was true THEN, so a sweep that "helpfully" renames through it destroys the
  reference — a July session log now says `UseMessageDispatcher`, a name that did not exist in July.
  **Skip `local/` the way `doc-drift` already skips `CHANGELOG.md`: both are history by definition.**
  ⚠ The accounting assertion that let it through checked the COUNT, not the SCOPE — a 28-file rename
  whose own self-check passed. When a sweep asserts it did the right thing, assert on WHICH files.
