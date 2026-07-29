---
name: phase-review
description: Run the standing end-of-phase review pass — subagent over the phase diff, fix real findings, sync docs/rules, prep the commit. Use after completing a development phase, before committing.
---

# Phase review

The standing rule: every phase gets an adversarial review before its commit.

## Steps

1. **Scope the diff**: `git diff <last-phase-commit>..HEAD --stat` (or working tree if
   uncommitted). Identify the phase's themes (new packages, public-surface changes, ported code).
2. **Spawn a review subagent** with the diff scope. Prompt it to hunt for:
   - **library leaks**: app/domain vocabulary or consumer-shaped APIs in `src/` (see
     `generic-library`), sibling-project names or absolute paths in tracked files
   - **public-surface mistakes**: types public that should be internal, missing XML docs on
     shipped surface, breaking changes not flagged for the CHANGELOG
   - resource leaks (event handlers, timers, WebView2/controls, processes, streams — `IDisposable`
     honored end-to-end)
   - threading: UI-thread marshalling discipline (non-blocking `BeginInvoke`, `IsHandleCreated`
     checks, `ConfigureAwait(false)` off the UI thread, no sync-over-async), races
   - `async void` handlers without exhaustive try/catch; poll/retry loops without a terminal
     condition
   - invariants from `.claude/rules/` (core) or `.claude/knowledge/` (on-demand) being violated
   It must report file:line + why it's real — no style nits.
3. **Fix**: batch the real findings; delegating the whole fix batch to one subagent and
   re-verifying afterwards has worked well.
4. **Re-verify**: `node devtools/dev.mjs verify` — everything green.
5. **Sync docs**: README status, `docs/ARCHITECTURE.md` (projects/surface), `docs/ROADMAP.md`
   (done narrative) + prune `TASKS.md`, `CHANGELOG.md` (if surface changed), and rules — a new
   invariant defaults to on-demand `.claude/knowledge/` via `node devtools/dev.mjs knowledge new
   <name>` (only universal ones go core with `--core`); then `… knowledge check`.
6. **Prep the commit message** (phase summary, verified-against notes — no private paths/names)
   and ask the user for commit approval.
