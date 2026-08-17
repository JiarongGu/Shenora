# Standing habits — the maintenance passes that are never "done"

Habits, not checkboxes: each one is a pass to re-run rather than a task to complete, which is exactly
why they do not belong in `TASKS.md` — a backlog whose entries can never be deleted stops tracking the
remaining work. MOVED here verbatim from that file on 2026-08-13 (it was 458 lines holding 7 open items,
the third occurrence of a drift its own header records twice).

## Standing habits — NOT checkboxes, deliberately

⚠ **These used to be `- [ ]` items and that was the bug.** A box that can never be ticked is permanent
noise in a file whose only signal is the box — the same defect the header complains about, committed by
the file itself. They are prose now, and they never "complete":

- **Re-verify a `DECISIONS.md` entry against the code WHEN YOU TOUCH ITS SUBJECT**, not in one linear
  pass — ⚠ **a linear read of 3,000 lines is exactly the sweep that produces confident nonsense.**
  🔴 **Run the instrument, then read where it points: `node devtools/dev.mjs decision-audit`.** It ranks
  every entry by how many of its claims fail against the tree, separates a live lie from correct past
  tense, and reports the source-citation count per entry (a crude proxy, and still the cheapest answer
  to *"how many readers does a wrong sentence have?"*).
  - 🔴 **TRUE is only half of it. Also ask whether the decision is still REASONABLE** for what the kit
    is now — D54's thesis (*can React already do this?*), D53's identity, `generic-library.md`, and
    above all: **is the PREMISE still there?** A decision taken when the kit was desktop-only, or had an
    optional feature tier, or one shell, can be perfectly accurate prose and an unreasonable decision
    today. No script answers this one.
  - 🔴 **Separate the DECISION from its EVIDENCE, because they fail independently.** A decision can stay
    right while the thing it was argued from has been fixed, disproved or deleted — and the entry is
    only dangerous when the stale part is what a reader will cite.
  - 🔴 **An entry recording a GAP ages in the opposite direction from one recording a DECISION.** When
    an entry argues from *"we do not have X yet"*, that sentence has a shelf life; the argument does
    not. Closing the to-do is what makes the entry lie.
  - 🔴 **A finding is closed when the CLAIM is gone from the REPO, not when the entry is amended.** An
    amendment corrects one reader; the copies keep their own dates.
  - **CORRECT IN PLACE; the WHY goes in the commit message** (`phase-workflow.md`). ⚠ **But never
    renumber** — a `D<n>` is a permanent address cited from shipped XML docs on nuget.org, so a
    superseded entry keeps its number as a one-line tombstone.
- 🔴 **Re-read code whose ASSUMPTIONS a later change invalidated — not just code that is new.** The
  standing advice was "read for missing coverage, not for complexity", and it missed a real leak:
  `WebViewHost`'s dispose path was unchanged and correct, until the response body went lazy and a
  dropped `MemoryStream` became a leaked OS file handle. Reading it a release earlier would have found
  nothing wrong. **The cheap query: what did this change make LAZY, SHARED, REMOTE or REUSED that used
  to be none of those?**
- 🔴 **Re-run the SEAM AUDIT after any pass that adds extension points — it is two greps, and the method
  is why it is a habit rather than a task.** Enumerate every public contract, then ask of each: *is there
  an implementation, and does anything CONSUME it?* Most unconsumed contracts are legitimate —
  options-supplied collaborators, per-webview objects, app-supplied seams — so the signal is narrow:
  **a kit-built implementation that nothing calls.** An extension point is done when something ASKS for
  it, not when the interface compiles (D63).
  - ⚠ **The second half of the question, added after a variant that was not silent:** *does anything
    IMPLEMENT what the kit PROMISES?* A capability query marked video encodable, so the planner answered
    `Transcode` while nothing could perform it and the track was dropped. The first three instances were
    silent; that one said the word — **a plan naming a conversion is read as a promise.**
- **Keep `docs/ARCHITECTURE.md` + `docs/README.md` in sync as pieces land.** Partly gated since
  2026-08-05: `doc-drift` fails if a packable project is named in neither. Everything below package
  granularity — a new type, a moved folder — is still yours to keep honest.
- **Run `dev.mjs test clipboard` after touching `ClipboardService`.** That suite is held OUT of the gate
  (this box refuses ~30 % of clipboard writes from a looping test process — an OS-level condition, see
  `TASKS.md`), so the gate will not do it for you. A held-out suite can rot, which is the price of the
  split and the reason the exclusion announces itself on every run.
- 🔴 **AFTER A RELEASE, AUDIT THE PROSE AGAINST WHAT SHIPPED — no gate can.** The gates check names,
  paths, versions and generated tables; none can ask "does the README describe this capability at
  all?". Measured after 0.11.0 by reading `CHANGELOG`'s section against README/ADOPTION/the guides
  and the API baselines: a guide was teaching the exact lifecycle bug the release FIXED, three docs
  named types that no longer compile, and the release's clearest showcase feature (the page-facing
  clipboard) appeared in no document anywhere. ⚠ **Read the changelog section as the checklist** —
  each `### Added` entry should be findable by someone who does not already know it exists.
- **Add a `.claude/knowledge/` rule the moment an invariant is EARNED**, via
  `node devtools/dev.mjs knowledge new <name>` — don't let it live only in a code comment. UI-thread
  marshalling, WebView2 gotchas, IPC batching numbers and the mobile header table all got here that way.
- **Re-measure the COMMENT RATIO in `src/` when a pass has been adding prose**, because it ships to
  adopters' IDEs and it has drifted upward before while a rule against it was already written: 45 %
  (2026-08-14) → 47.3 % (2026-08-15) → **40.9 % (2026-08-17: 17,186 comment / 42,035 total, 0.84 per
  code line)**. ⚠ There is no target, deliberately — a `///` on every public member is CORRECT for a
  library, so a low number is not the goal and a ratio alone cannot tell a doc from a narration. The
  number's job is to catch a REVERSAL. `doc-shape` sweeps `src/` for session-log prose, which is the
  floor under it.

- **Keep naming the concrete bug each ADOPTION stage removes.** From the first adopter's Stage-0
  feedback (2026-07-31): what made the decision easy was *"Stage 1 carries no IPC dependency, so it
  deletes the most duplicated code for the least risk; the IPC substrate comes last because it is the
  only stage that touches every module"* — and what justified adopting a kit at all was naming the
  specific bugs a hand-rolled shell tends to have (the DPI-mis-scaled `Screen.WorkingArea` restore;
  `CloseReason.UserClosing` firing for a programmatic `Close()`). Write new stages the same way.

---
