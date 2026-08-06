# docs/archive/ — finished records, kept out of the living docs

**Entry criterion: this is done and will not change.** A file belongs here when it is a RECORD rather
than a statement about the present — closed tasks, shipped phases, fixed bugs. The living docs
(`../ARCHITECTURE.md`, `../DECISIONS.md`, `../ADOPTION.md`, `../ROADMAP.md`, `../TASKS.md`) describe
what is true now and stay small enough to read.

**Why this folder exists (2026-08-02).** Append-only history had grown to dominate the docs:
`ROADMAP.md` was 1,873 lines of which ~1,450 were a closed era, and the task record and fix log
another 2,700 between them. The current cycle was buried under finished ones, which is the opposite
of what a memory map is for — and the fix is a folder, not deletion, because the WHY behind a
finished decision is exactly what a future session needs before re-litigating it.

| File | Holds |
|---|---|
| `tasks.md` | The completed-task record: plans, `file:line` anchors, and the judgement calls made while executing them. Several entries carry warnings written FOR a future session |
| `fix-log.md` | Notable fixes — symptom / root cause / fix / verification / commit. Appended via `/fix-log` |
| `roadmap-v0.1.0.md` | The P1–P7 narrative (2026-07-30 → 2026-07-31) that produced v0.1.0 |

## Rules for this folder

- **Append; do not rewrite — a record of something that SHIPPED is never edited.** Correcting it erases
  the trail that makes it useful. If it later turned out wrong, say so in the LIVING doc and link back.
- 🔴 **But DELETE a record whose subject never existed** (owner, 2026-08-07: *"we should do a cleanup,
  remove everything thats irrelevant anymore which is clearer than keep adding"*). The two rules answer
  different questions, and conflating them is what let this folder grow noise:

  | | |
  |---|---|
  | a decision or feature that shipped and later CHANGED | **keep it**, and say what replaced it — that is the trail |
  | a plan, package or type that **never shipped at all** | **delete it**, leaving one line for what happened instead |

  A build log for packages nobody can install teaches nothing and makes the surrounding record harder to
  read. Applied 2026-08-07 to the DM5 entry (the 8-package media layout, deleted the same day it was built
  and never published) and, in the living docs, to D40/D41 and the 923-line media design doc. **Git history
  holds every word**, which is what makes deletion cheap; the test is *did a consumer ever see this?*
- ⚠ **Check the reason, not just the conclusion.** D40 justified a package with "a demuxer is real shipped
  bytes"; D51 later guaranteed no engine byte would ever ship, and the premise sat dead for two days while
  the entry still read as current. A record whose REASONING has been invalidated is the hardest kind of
  stale to see, because every sentence in it is still grammatical.
- **`doc-drift` exempts `docs/archive/` wholesale** — a retired name or a since-deleted doc path in a
  record is ACCURATE, not stale. That exemption is by path prefix, so a new file here is exempt the
  moment it exists.
- **Nothing here is the source of truth for the present.** As-built surface → `../ARCHITECTURE.md`.
  Load-bearing WHYs → `../DECISIONS.md`. Release-facing log → `../../CHANGELOG.md`.
- **Moving something here is a signal, not filing.** It says the work is closed. If a record still
  needs editing to stay true, it is not finished and does not belong here yet.
