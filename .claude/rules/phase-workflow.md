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
