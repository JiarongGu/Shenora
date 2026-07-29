---
name: fix-log
description: After landing any non-trivial bug fix or regression fix, record it in docs/FIX-LOG.md (root cause, fix, verification, commit). Also use to review past fixes / trace when a behavior regressed.
---

# fix-log

Keep a durable, greppable history of **why** things broke and how they were fixed — so a future
regression's origin is traceable and the same bug isn't reintroduced.

## When to log (do this as part of "done", before moving on)
- **A regression** — something that used to work and broke. Always log it, and trace the
  introducing commit: `git log -S "<distinctive token>" -- <path>`.
- **A non-obvious bug** whose root cause would be easy to reintroduce (a threading/marshalling
  trap, an encoding gotcha, a WebView2 behavior, a packaging/versioning trap).
- **Skip:** trivial typos, pure refactors, or still-WIP work.

## How
Append to `docs/FIX-LOG.md`, newest entry first, under a `## YYYY-MM-DD` heading. Use this shape:

```
### <area>: <one-line symptom>
- **Symptom:** what was actually observed
- **Root cause:** the real mechanism — and the commit that introduced it if it's a regression
- **Fix:** what changed + the files
- **Verify:** the command/observation that confirmed it
- **Commit:** <hash>   (fill after committing; leave _pending_ until then)
```

## Cross-repo fixes
A Shenora fix often pairs with a change in a consuming sibling app. Log Shenora's part here and
reference the consumer generically ("paired adoption fix in the consuming app") — sibling names
stay out of tracked files; the specifics go in `local/PROJECT_NOTES.md`.

## Rule of thumb
Capture the **root cause**, not just the symptom. If you can't name the mechanism (or the commit
that introduced a regression), the entry isn't done yet. If the root cause is a reusable
invariant, ALSO add a knowledge rule (`dev.mjs knowledge new <name>`) — the fix log is history,
the rule is prevention.
