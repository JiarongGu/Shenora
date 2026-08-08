# Work loop — build, verify, review, commit

**There are no phases any more.** P1–P7 are done and 0.1.2 shipped. Work now arrives as harvest (D15) or
adoption feedback. 🔴 **Finished work is DELETED, not archived** (2026-08-07) — git is the history, so a
closed task leaves `TASKS.md` and a shipped decision leaves only its `DECISIONS.md` entry. The loop below applies to any non-trivial change, and **every public change is
SemVer surface** — note breaks in `CHANGELOG.md` under `### Breaking`. This repo never edits another
repo: it readies the LIBRARY and writes `docs/ADOPTION.md`; the adopting app's own session adopts.

1. **Build** through the dev loop: `node devtools/dev.mjs <build|test|verify|pack|doctor|sample|…>` —
   don't invent ad-hoc shell for these.
2. **Verify with evidence.** `dev.mjs verify` is the "am I done?" gate (build · test · typechecks ·
   sensitive scan · knowledge check+footprint · doc-drift · doctor). Behavioural claims about the
   desktop shell are proven against the sample (`dev.mjs sample` + the capture/input tools), not
   asserted; performance claims need numbers. **When a claim is not covered by the gate, say so** —
   a green gate that wasn't looking at the samples is how the P0–P5 latent defects passed five
   reviews.

   🔴 **A/B THE HARNESS ITSELF — and count TRIALS, because an intermittent failure makes every
   single-run elimination a coin flip.** A renderer crash was chased through ~12 single-run A/B
   eliminations over two sessions (GPU, profile, page, scheme, IPC bridge, pool, .NET hosts). Every one
   "succeeded" and every one was meaningless: the crash fires ~50 % of the time, so half of those
   verdicts were noise, and the search kept moving because each answer looked clean. Treating the
   LAUNCHER as the variable and running 5 alternated trials per arm settled it in minutes —
   **0/12 without a new process group, 6/12 with one** (`dev.mjs sample` under `timeout`; see
   `windows-dev-gotchas.md`). The kit was never at fault.
   - **Measure WHEN before eliminating WHAT.** Timestamping was the cheapest experiment available and
     was run LAST, after everything it would have redirected. Two earlier attempts produced no output
     (shell pipe buffering) and were abandoned — **abandoning a broken instrument IS the error**,
     because every experiment after it answers a question nobody asked.
   - **The tells that the harness, not the code, is the author:** the failure appears only under
     instrumentation; it never reproduces when a human runs the app; no single cause survives
     elimination; and the diagnostics contradict each other (here: no Windows Error Reporting event
     despite an access violation, and an empty `FailureSourceModulePath` — no faulting module).
   - ⚠ **Do not stop at the first coherent story either.** "The `timeout` KILL orphans the app and its
     teardown writes the crash" fitted the evidence, was written into this rule, and was WRONG — the
     crash lands ~8 s in, long before any kill, and an immediate check found zero orphans. Reading the
     log IN ORDER killed it. A story that explains the facts is a hypothesis, not a finding.
3. **Review** with `/phase-review` — an adversarial pass over the whole diff → fix the real findings
   → sync docs (`ARCHITECTURE`/`TASKS`/`CHANGELOG`) and rules → then commit.
4. **Commit only on explicit user approval.** One commit per logical change; never fold a security
   fix into a structural refactor to honour "one commit".

**Tripwires are sabotage-verified in BOTH directions.** A green tripwire that cannot fail is worth
nothing: break what it watches, confirm the message names it, restore, confirm green again.

**Also test where it must stay QUIET, in the environment it really runs in.** Three gates broke the
0.4.0 release this way in one day — each proven on the path it should catch, never on the path it
should ignore (a version guard that had never run DURING a release; a size budget that had never run
on a CRLF checkout; a `warning CS` filter that had never met a non-CS analyser warning). Ask: **when
should this stay silent, and where does it execute?** Then try those.

🔴 **A RENAME OR REMOVAL IS THREE STEPS, AND THE THIRD IS THE ONE THAT GETS SKIPPED.** (1) change the
code, (2) add the old name to `devtools/retired-names.txt`, (3) **run `node devtools/dev.mjs stale-scan`
IN THE SAME COMMIT and triage its worklist.** Step 3 exists because the gate fed by step 2 *cannot* find
the prose you just invalidated: `doc-drift` suppresses any match within 6 lines of a history word, and
this repo's docs are amendment stacks by design — so a stale claim hides exactly where the suppression is
active (proven by sabotage 2026-08-08: a planted claim stayed green in `TASKS.md` AND `ARCHITECTURE.md`).
D66 did steps 1 and 2, and `docs/ADOPTION.md` told adopters to call a deleted API for three commits with
every gate green. `stale-scan` never fails a build — it is noisy on purpose, because most hits are correct
past tense and **only a human can tell those from a live lie**. That triage IS the deliverable.

**A fix records its ROOT CAUSE in the commit message** (there is no fix log — git is the history; trace
with `git log -S "<token>" -- <path>`). ⚠ **A reusable invariant is a RULE, not a message**:
`dev.mjs knowledge new <name>`.

🔴 **An extension point is done when something ASKS for it, not when the interface compiles (D63).** Three
times in one fortnight the kit declared a capability nothing consulted — and none threw, logged or failed
a test, because ABSENT is indistinguishable from working. A test must supply a fake and assert it was
USED. After any pass that adds seams, re-run the audit: which contracts have a kit implementation and no
consumer?

**Keep a gate proportionate.** Correctness stops a release; style warns. That budget was made fatal
and then blocked shipping over 0.2 KB.

**Extraction ports** keep the source's post-mortem comments and fix the known gaps
(`.claude/knowledge/extraction-sources.md`).
