# Doc claims — a behavioural statement is verified against the SOURCE, never the design doc

Prose is the one surface with no compiler, and `doc-drift` deliberately checks only three exact things
(retired names, `docs/` links, the dependency graph). A remark that says *what the code does* sits
below that gate: it can be wrong for a whole release with every check green. This rule is what caught
three such claims in one pass on 2026-08-02 — two of them in **shipped XML** (`docs/FIX-LOG.md`,
commit `49bfc0c`).

## The rules

- **Write behavioural prose from the implementation, not from the design doc.** The docs for
  `Shenora.Core`'s work scheduler were written from `docs/2026-08-02-shenora-work-scheduling-design.md`
  and three claims did not survive a read of `src/Shenora.Core/Work/`: an unknown LANE was documented
  as throwing when `WorkScheduler.CreateEntry` creates it at the default capacity; `IWorkObserver` read
  as though the kit ships the operation-registry adapter, which nothing implements; and the design's
  `IFileSystem` + atomic-replace helper had never shipped at all. **A design doc states intent, and
  intent is what the last edit before merge changes.** Open the file, find the line, then write.
- **This is worst in the NEWEST code**, which is where to spend the check. Older areas have survived
  reviews; a component documented in one burst alongside its own design has had no second reader. An
  audit of the older shipped XML the same day (dispatch boundary, `NotificationPump.TryDrainBatch`,
  `IUiDispatcher.Post`, `IpcJson.Options`, the lifecycle-hook contract, the 404 path) found nothing.
- **A surprising behaviour gets a TEST, not just a corrected comment.** The comment that was wrong
  will be "fixed" back by the next reader who finds it surprising. `An_unseen_LANE_name_is_created_at
  _the_default_capacity_rather_than_throwing` pins the asymmetry with an unregistered claim scope,
  which DOES throw. Sabotage-verify it both ways like any other tripwire (`phase-workflow.md`).
- **Say which claims the gate did not check.** `dev.mjs verify` compiles and runs tests; on a
  docs-only change it proves nothing about the prose. Report that explicitly rather than letting a
  green gate imply the words were checked.

## Gotchas / traps

- **"Throws" claims are the expensive ones.** A wrong "this throws" costs an adopter silently — the
  lane case gives them a second lane at the default capacity and none of the exclusivity they
  configured, with no error and nothing in the log. Check every *throws / never throws / always /
  never / defaults to* statement against the code path, not against the neighbouring one.
- **"The kit ships X" ages badly.** X gets cut before publish (design §6's filesystem layer) or is
  planned and never written (`IWorkObserver`'s registry adapter). Grep for the type before saying it
  exists; if the app must write it, say so in the same sentence.
- **De-identify per `sensitive-info.md` while you are in there** — an adopter-facing doc describes
  roles ("the primary desktop sibling"), never a private project's name or file paths. The named
  per-sibling mapping lives in `local/`.
