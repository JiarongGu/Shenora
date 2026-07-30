---
name: phase-review
description: Run the standing end-of-phase review pass — subagent over the phase diff, fix real findings, sync docs/rules, prep the commit. Use after completing a development phase, before committing.
---

# Phase review

The standing rule: every phase gets an adversarial review before its commit.

## Steps

1. **Scope the diff**: `git diff <last-phase-commit>..HEAD --stat` (or working tree if
   uncommitted). Identify the phase's themes (new packages, public-surface changes, ported code).
2. **Spawn a review subagent** with the diff scope, and hand it `docs/REVIEW-GUIDE.md` as its brief
   (invariants by area, risk hotspots, and the already-settled list it must not re-raise). Prompt it
   to hunt for:
   - **the five classes five consecutive phase reviews MISSED** (earned 2026-07-30 — the first full
     review found them after this checklist had passed the same code five times): path/containment
     checks on anything that maps a request to a file; an async op that accepts a
     `CancellationToken` and never observes it; an app-supplied callback or payload running
     unguarded inside a UI-thread event handler or timer; a gate/scanner that fails OPEN; and a
     declared dependency edge that nothing crosses (duplication hiding behind a layering claim)
   - **library leaks**: app/domain vocabulary or consumer-shaped APIs in `src/` (see
     `generic-library`), sibling-project names or absolute paths in tracked files
   - **public-surface mistakes**: types public that should be internal, missing XML docs on
     shipped surface, breaking changes not flagged for the CHANGELOG
   - resource leaks (event handlers, timers, WebView2/controls, processes, streams — `IDisposable`
     honored end-to-end)
   - threading: UI-thread marshalling discipline (non-blocking `BeginInvoke`, `IsHandleCreated`
     checks, `ConfigureAwait(false)` off the UI thread — **except the IPC dispatch pipeline, which
     preserves context BY DESIGN**, see `ipc-contracts`; no sync-over-async), races
   - `async void` handlers without exhaustive try/catch; poll/retry loops without a terminal
     condition
   - invariants from `.claude/rules/` (core) or `.claude/knowledge/` (on-demand) being violated
   It must report file:line + why it's real — no style nits.
3. **Fix**: batch the real findings; delegating the whole fix batch to one subagent and
   re-verifying afterwards has worked well.
4. **Re-verify**: `node devtools/dev.mjs verify` — everything green.
5. **Sync docs**: README status, `docs/ARCHITECTURE.md` (projects/surface), `docs/ROADMAP.md`
   (done narrative) + prune `TASKS.md`, `CHANGELOG.md` (if surface changed), `docs/FIX-LOG.md` for
   any non-trivial fix the review produced (via `/fix-log`), `docs/REVIEW-GUIDE.md` if an invariant
   or settled-decision changed, and rules — a new
   invariant defaults to on-demand `.claude/knowledge/` via `node devtools/dev.mjs knowledge new
   <name>` (only universal ones go core with `--core`); then `… knowledge check`.
6. **Prep the commit message** (phase summary, verified-against notes — no private paths/names)
   and ask the user for commit approval.
