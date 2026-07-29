# Persist working state to the repo — context is ephemeral, the repo is the memory

The conversation gets **compacted mid-task**, dropping in-flight state (what we decided, what's
half-done, what's next). `phase-workflow.md` checkpoints at phase *end*; this rule adds the
missing habit — checkpoint **continuously, as you go** (earned live in the family: a long session
lost the thread of a multi-step rebuild because progress lived only in the chat).

## The rules

- **The repo is the memory, not the chat. Write state to its durable home BEFORE moving on** —
  don't batch it to the end. After each decision or milestone ask: *"if the context compacted now,
  would the next turn know where we are and why?"* If not, persist first, then continue.
- **Each kind of state has ONE home:** in-progress status / what's-next / live-run results →
  **`local/PROJECT_NOTES.md`** (private, untracked — the session-to-session file, kept current);
  load-bearing decisions + rationale → **`docs/DECISIONS.md`**; done milestones →
  **`docs/ROADMAP.md`** (+ remove from `TASKS.md`); invariants/gotchas → **`.claude/rules|knowledge/`**
  via `node devtools/dev.mjs knowledge new`; as-built projects/surface → **`docs/ARCHITECTURE.md`**;
  a reusable procedure → a **`.claude/skills/`** skill. Work involving the sibling repos is
  tracked from `local/PROJECT_NOTES.md` too, so one place tells the whole story.
- **NEVER use the system temp / scratchpad for cross-turn state or progress** — it's
  session-isolated and ephemeral (the exact thing that gets lost). Throwaway probes go under
  `devtools/` (`_*` gitignored); durable state goes to the homes above.
- **Read it back at session start** (per `CLAUDE.md`): `local/PROJECT_NOTES.md` + `TASKS.md` +
  routed docs. Stale notes ARE the bug this prevents — fix them as you go, keeping private
  specifics in `local/` (see `sensitive-info.md`), never a tracked doc.
