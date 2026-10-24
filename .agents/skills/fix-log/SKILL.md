---
name: fix-log
description: After landing a non-trivial bug or regression fix, record its root cause, fix, and verification in the repository's fix log. Also use to review past fixes or trace when a behaviour regressed. Use as part of "done", before moving on — not later.
---
<!-- daoris: core/core/skills/fix-log/SKILL.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# fix-log

Keep a durable, greppable history of **why** things broke and how they were fixed, so a future
regression's origin is traceable and the same bug is not reintroduced.

## When to log

- **A regression** — something that worked and stopped working. Always log it, and trace the commit that
  introduced it by searching history for the distinctive token that changed.
- **A non-obvious bug** whose root cause would be easy to reintroduce: an encoding trap, an ordering
  assumption, a silently-ignored field, a lifecycle or threading subtlety, a packaging trap.
- **Skip** trivial typos, pure refactors, and still-unfinished work. A log everything policy produces a
  file nobody reads.

## How

Append to the repository's fix log — newest entry first, under a dated heading — in this shape:

```
### <area>: <one-line symptom>
- **Symptom:** what was actually observed
- **Root cause:** the real mechanism, and the commit that introduced it if this is a regression
- **Fix:** what changed, and where
- **Verify:** the command or observation that confirmed it
- **Commit:** <hash>   (fill in after committing; leave pending until then)
```

## Rules

- **Capture the root cause, not the symptom.** If you cannot name the mechanism — or the commit that
  introduced a regression — the entry is not finished. "Fixed a null reference" records nothing; the
  mechanism that allowed the null is the whole value.
- **Log it as part of "done", before moving on.** Reconstructed later, the entry costs an investigation
  and is usually wrong about the cause.
- **A fix spanning several repositories is logged in each, and duplicated in none.** Record this
  repository's half here and refer to the other neutrally — cross-repository specifics, and any private
  name, stay out of tracked files.
- **If the root cause is a reusable invariant, also write the rule.** The log is history; the rule is
  prevention. A trap recorded only in the log will be rediscovered by whoever does not think to search it.

## Why

The value is not the record of the fix — version control already has that. It is the **mechanism**, which
version control does not: a diff shows a comparison changed from `<` to `<=` and never shows that the
boundary was off because the timestamp was inclusive. That sentence is what stops the next person
reintroducing it, and it exists nowhere unless someone writes it down while it is still fresh.
