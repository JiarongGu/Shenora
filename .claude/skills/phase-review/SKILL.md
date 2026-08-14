---
name: phase-review
description: Run the standing end-of-phase review pass — subagent over the phase diff, fix real findings, sync docs/rules, prep the commit. Use after completing a development phase, before committing.
---

# Phase review

The standing rule: every phase gets an adversarial review before its commit.

## Steps

1. **Scope the diff**: `git diff <last-phase-commit>..HEAD --stat` (or working tree if
   uncommitted). Identify the phase's themes (new packages, public-surface changes, ported code).
2. **Ask the DESIGN question before the correctness one** (owner direction, 2026-08-01 — a review that
   found real defects still missed the point): *"you should be getting the purpose of the project,
   rethinking if this is a good design, instead of just checking if the code itself works."* For each
   load-bearing piece of the diff ask **does this earn its place for the kit's PURPOSE** (`REVIEW-GUIDE.md`
   §1), not merely "is it consistent with the design doc that introduced it" — a doc asserting a design
   is the claim most worth attacking, not context. Cheap tells that something is wrong at the design
   level: a shape reworked several times inside ONE unpublished release, a feature whose own doc admits
   it comes from one consumer against the two-consumer bar, and a cluster that produced the release's
   worst defect. "The complaint is fair but the fix is worse" is an equally valid verdict — it just
   needs a `DECISIONS.md` entry so it stays rejected.
3. **Spawn a review subagent** with the diff scope, and hand it `docs/REVIEW-GUIDE.md` as its brief
   (invariants by area, risk hotspots, and the already-settled list it must not re-raise).
   **If the Agent tool is unavailable — it usually has been — run the SAME checklist yourself against
   the diff and say so in the notes.** Every review since P5.5 has been done that way and has still
   found real defects (the P5.6 pass caught a gate failing open; P6.3a's caught a vacuous tripwire),
   so a missing subagent is never a reason to skip the pass — only to do it by hand. Either way, hunt
   for:
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
4. **Fix**: batch the real findings; delegating the whole fix batch to one subagent and
   re-verifying afterwards has worked well.
5. **Re-verify**: `node devtools/dev.mjs verify` — everything green.
6. **Sync docs**: README status, `docs/ARCHITECTURE.md` (the MAP — projects, subsystem kinds,
   dependency rules; **never a surface listing**, which the API baselines and XML docs own),
   `CHANGELOG.md` (if surface
   changed), `docs/REVIEW-GUIDE.md` if an invariant or settled-decision changed, and rules — a new
   invariant defaults to on-demand `.claude/knowledge/` via `node devtools/dev.mjs knowledge new
   <name>` (only universal ones go core with `--core`); then `… knowledge check`. A non-trivial fix's
   root cause goes in the COMMIT MESSAGE — there is no fix log.
   **`TASKS.md` is pruned by DELETING finished entries, never by ticking them
   in place** (standing user rule; git is the archive since 2026-08-07) — the file's length is the size
   of the remaining work, so a DONE
   paragraph left under `## Open` defeats the only reason to open it. Leave behind at most a one-line
   pointer, and keep any follow-up the entry spawned.
7. **Run the EVIDENCE LEDGER before writing the commit message.** List every factual claim this change
   writes into a tracked file — a doc, an XML comment, a rule, a TASKS entry — and next to each, the
   command that established it. Anything with no command is DERIVED or ASSUMED and must SAY SO in the
   text, or come out.
   🔴 **This is not ceremony; it is the only step that has ever caught this class.** Asked for exactly
   this, a subagent's own report separated "verified by running something" from "written but NOT
   verified", and the unverified list contained real ones it then fixed — a rationale it had reasoned
   out and stated as fact, and an inference about warnings it replaced with a measurement. Nothing else
   in this loop looks at a claim's PROVENANCE: the gates check names, paths and tests, never how you know.
   The recurring shapes, each of which bit within one fortnight:
   - **Absence is not a defect until you know which environment you are in.** `local/` is gitignored and
     CANNOT exist in a worktree, a fresh clone or CI. A subagent read "MISSING", concluded the repo was
     broken, and copied private context across checkouts. Never copy `local/` anywhere.
   - **A tool's silence is evidence about your QUERY, not about the world.** `grep "record SessionFrame"`
     returned nothing and the type exists. Ask a second way before concluding "gone".
   - **"I fixed all of them" needs a definition of ALL.** A sweep fixed `process.cwd()` in three scanners
     and missed the fourth, because the population was "the files I happened to open".
   - **A measurement attributes to your variable only if nothing else moved.** `staleDate` became a 🔴
     rule from one device run on a day that also fixed two defects explaining the same symptom.
     Confounded is not wrong — it is UNATTRIBUTED, and must be labelled so rather than promoted.
   - **Your own edit invalidates prose about it, in the same commit** — including text you wrote minutes
     ago. One commit rewrote a comment and filed a task describing that comment's old wording.
   - **A documented invariant is not an enforced one** (D19: asserted in three documents, false in code).
8. **Prep the commit message** (summary, verified-against notes — no private paths/names)
   and ask the user for commit approval.
