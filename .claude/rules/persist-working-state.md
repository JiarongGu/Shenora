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
  **the COMMIT MESSAGE** (+ delete from `TASKS.md`); invariants/gotchas → **`.claude/rules|knowledge/`**
  via `node devtools/dev.mjs knowledge new`; as-built projects/surface → **`docs/ARCHITECTURE.md`**;
  a reusable procedure → a **`.claude/skills/`** skill. Work involving the sibling repos is
  tracked from `local/PROJECT_NOTES.md` too, so one place tells the whole story.
- **NEVER use the system temp / scratchpad for cross-turn state or progress** — it's
  session-isolated and ephemeral (the exact thing that gets lost). Throwaway probes go under
  `devtools/` (`_*` gitignored); durable state goes to the homes above.
- ⚠ **A repo-wide SCRIPT must exclude `local/` — it is an informal ARCHIVE, not just private.** Owner,
  2026-08-08: *"not for tracking but for historical reason … an informal archive for local purpose
  (things we don't want to publish but still good as referencing for development)"*. Its value is that it
  records what was true THEN, so a sweep that "helpfully" renames through it destroys the reference
  rather than updating it — a July session log now says `UseMessageDispatcher`, a name that did not exist
  in July. Caught live by a 28-file rename whose own accounting assertion passed, because the assertion
  checked the COUNT, not the SCOPE. **Skip `local/` the way `doc-drift` already skips `CHANGELOG.md`:
  both are history by definition.** (Harmless that once — nothing there ships — which is exactly why it
  would keep happening unrecorded.)
- **Read it back at session start** (per `CLAUDE.md`): `local/PROJECT_NOTES.md` + `TASKS.md` +
  routed docs. Stale notes ARE the bug this prevents — fix them as you go, keeping private
  specifics in `local/` (see `sensitive-info.md`), never a tracked doc.
