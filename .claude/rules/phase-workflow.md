# Work loop — build, verify, review, commit

**There are no phases.** Work arrives as harvest (D15) or adoption feedback. The current release is in
`README.md`'s status line and `src/Directory.Build.props`, **never in a rule** — a version written into
always-loaded context is read as the present state by every session that starts.
🔴 **Finished work is DELETED, not archived** — git is the history, so a closed task leaves `TASKS.md`
and a shipped decision leaves only its `DECISIONS.md` entry. The loop below applies to any non-trivial
change, and **every public change is SemVer surface** — note breaks in `CHANGELOG.md` under
`### Breaking`. This repo never edits another repo: it readies the LIBRARY and writes
`docs/ADOPTION.md`; the adopting app's own session adopts.

1. **Build** through the dev loop — `node devtools/dev.mjs` with no argument prints every verb. Don't
   invent ad-hoc shell for these.
2. **Verify with evidence.** `dev.mjs verify` is the "am I done?" gate (build · test · typechecks ·
   sensitive scan · knowledge check+footprint · doc-drift · doc-shape · doctor). Behavioural claims
   about the desktop shell are proven against the sample (`dev.mjs sample` + the capture/input tools),
   not asserted; performance claims need numbers. **When a claim is not covered by the gate, say so** —
   a green gate that wasn't looking at the samples is how five reviews passed over latent defects.
3. **Review** with `/phase-review` — an adversarial pass over the whole diff → fix the real findings
   → sync docs (`ARCHITECTURE`/`TASKS`/`CHANGELOG`) and rules → then commit.
4. **Commit only on explicit user approval.** One commit per logical change; never fold a security
   fix into a structural refactor to honour "one commit".

## Debugging: suspect the harness, and count trials

🔴 **A/B THE HARNESS ITSELF — and count TRIALS, because an intermittent failure makes every single-run
elimination a coin flip.** A renderer crash survived ~12 single-run eliminations over two sessions
(GPU, profile, page, scheme, IPC bridge, pool, .NET hosts); it fires ~50 % of the time, so half those
verdicts were noise and each clean-looking answer moved the search on. Five alternated trials per arm
settled it in minutes — **0/12 without a new process group, 6/12 with one**. The kit was never at fault.

- **Measure WHEN before eliminating WHAT.** Timestamping was the cheapest experiment available and was
  run LAST. Two earlier attempts produced no output (shell pipe buffering) and were abandoned —
  **abandoning a broken instrument IS the error**, because every experiment after it answers a question
  nobody asked.
- **The tells that the harness, not the code, is the author:** the failure appears only under
  instrumentation; it never reproduces when a human runs the app; no single cause survives elimination;
  and the diagnostics contradict each other (no Windows Error Reporting event despite an access
  violation, and an empty `FailureSourceModulePath` — no faulting module).
- ⚠ **Do not stop at the first coherent story.** "The `timeout` KILL orphans the app and its teardown
  writes the crash" fitted every fact and was WRONG — the crash lands ~8 s in, long before any kill.
  Reading the log IN ORDER killed it. **A story that explains the facts is a hypothesis, not a finding.**

## Gates and tripwires

**Tripwires are sabotage-verified in BOTH directions.** A green tripwire that cannot fail is worth
nothing: break what it watches, confirm the message names it, restore, confirm green again.

🔴 **NEVER RUN A SABOTAGE WHILE A GATE IS IN FLIGHT IN THE BACKGROUND.** `verify` reads the WORKING
TREE, so a sabotage started beside it becomes its subject: it reported **`VERIFY FAILED` on commits
that were fine**, and passed on the identical commits once the tree was restored. Same shape as the
`timeout` lesson one level up — **the instrument and the experiment shared a working tree.**
⚠ **And do not truncate a long gate's output.** That run was piped to `tail -8`, so the failure detail
had scrolled off and only `verify: test FAILED` survived — the failing test could not be named from the
evidence. **A gate whose output you truncate can report a failure it cannot explain**; pipe to a file.

**Also test where it must stay QUIET, in the environment it really runs in.** Three gates broke the
0.4.0 release in one day — each proven on the path it should catch, never on the path it should ignore
(a version guard that had never run DURING a release; a size budget that had never run on a CRLF
checkout; a `warning CS` filter that had never met a non-CS analyser warning). Ask: **when should this
stay silent, and where does it execute?** Then try those.

**Keep a gate proportionate.** Correctness stops a release; style warns. A size budget was made fatal
and then blocked shipping over 0.2 KB.

🔴 **A REUSABLE INVARIANT IS A MECHANISM. A RULE IS WHAT YOU WRITE WHEN NO MECHANISM CAN EXIST — and
that is rarer than it feels.** Scored over one fortnight: **every failure caught was caught by a GATE OR
A TEST; every failure that landed walked past a rule already describing the correct behaviour.** A rule
is read once per session and competes with every other rule; a gate runs every time and names the file.

So, in order: **(1) can a gate or a test catch it?** Then write that and nothing else — the commit
message carries the root cause. **(2) Can the code make it unrepresentable?** Closing a type, deleting
the seam. **(3) Only if neither** — a rule, and prefer on-demand `.claude/knowledge/` over core, because
core is paid on every session forever. `dev.mjs knowledge new <name> [--core]`.

## Renames, removals and prose

🔴 **A RENAME ALSO DAMAGES THE ONE SENTENCE THAT NAMES IT.** A repo-wide sweep replaces the old name
everywhere — including the prose whose SUBJECT was that name — leaving "`X` depends on `X`", "`X` → `X`".
Grammatical, passes every gate, and nonsense exactly where a reader goes to learn what changed; five
landed across the docs in two days, three of them after two prose audits ran clean. `doc-drift` and
`cite-scan` are blind to it because both names exist and they are the same name.
`node devtools/dev.mjs self-rename-scan` lists them; **skip `local/`, read the diff, and when you
rename, re-read the entry that DEFINES the rename.**

⚠ **A SHIPPED XML DOC IS PROSE TOO — and it is the prose this repo never re-reads.** It renders in an
adopter's IDE straight from the nupkg, so `self-rename-scan` reads `src/` and joins comment BLOCKS (an
XML doc wraps, and a per-line matcher sees neither half).

🔴 **A RENAME OR REMOVAL IS THREE STEPS, AND THE THIRD IS THE ONE THAT GETS SKIPPED.** (1) change the
code, (2) add the old name to `devtools/retired-names.txt`, (3) **run `node devtools/dev.mjs stale-scan`
IN THE SAME COMMIT and triage its worklist.** Step 3 exists because the gate fed by step 2 *cannot* find
the prose you just invalidated: `doc-drift` suppresses any match within 6 lines of a history word, so a
stale claim hides exactly where the suppression is active (proven by sabotage — a planted claim stayed
green in `TASKS.md` AND `ARCHITECTURE.md`). D66 did steps 1 and 2, and `docs/ADOPTION.md` told adopters
to call a deleted API for three commits with every gate green. `stale-scan` never fails a build: most
hits are correct past tense and **only a human can tell those from a live lie**. That triage IS the
deliverable.

🔴 **CORRECT PROSE IN PLACE — never append a dated note narrating what it used to say.** Replace the
wrong sentence with the right one; the WHY goes in the commit message. Appending is what let
`DECISIONS.md` reach 3,207 lines with 47 % of its entries still stating something untrue, and it
*blinds* `doc-drift`, whose history suppression an amendment stack keeps permanently on.
`dev.mjs doc-shape` enforces this. The test: **a fact about the SYSTEM stays, a fact about the
DOCUMENTATION goes.** A superseded `DECISIONS.md` entry keeps its number as a one-line tombstone —
those numbers ship in XML docs on nuget.org and must always land somewhere.

**A fix records its ROOT CAUSE in the commit message** (there is no fix log — git is the history; trace
with `git log -S "<token>" -- <path>`).

## Seams

🔴 **An extension point is done when something ASKS for it, not when the interface compiles (D63).**
Three times in one fortnight the kit declared a capability nothing consulted — and none threw, logged or
failed a test, because ABSENT is indistinguishable from working. **A test must supply a fake and assert
it was USED.** After any pass that adds seams, re-run the audit: which contracts have a kit
implementation and no consumer?

**Extraction ports** keep the source's post-mortem comments and fix the known gaps
(`.claude/knowledge/extraction-sources.md`).
