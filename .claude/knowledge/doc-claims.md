# Doc claims — a behavioural statement is verified against the SOURCE, never the design doc

Prose is the one surface with no compiler, and `doc-drift` deliberately checks only three exact things
(retired names, `docs/` links, the dependency graph). A remark that says *what the code does* sits
below that gate: it can be wrong for a whole release with every check green. This rule is what caught
three such claims in one pass on 2026-08-02 — two of them in **shipped XML** (commit `49bfc0c`).

🔴 **AND THE CORRECTION IS AS DANGEROUS AS THE CLAIM — trace the call chain to its END before rewriting
one.** Earned three times in one audit (2026-08-09), the third being the worst because it created a NEW
false claim while removing an old one:

- `MediaPlayerModule`'s remark said *"Registered by default with `UseMessageDispatcher()`"*. The core
  registers no feature, so that mechanism is wrong — and I rewrote it to *"registered by
  `UseMediaPlayer()`, **not** by `Build()`… an app that never calls it has no player routes at all"*, then
  propagated that into the CHANGELOG and `TASKS.md`.
- **`Build()` calls `UseMediaPlayer()` itself.** The original's CONCLUSION was right and only its
  mechanism was wrong; my correction inverted a true statement about adopter-visible behaviour.
- **The tell I ignored:** I verified the claim's *named mechanism* (grep `UseMessageDispatcher`) and
  stopped, instead of asking the question the sentence was actually about — *does an app get this without
  asking?* One more file (`ShenoraApplicationBuilder.Build`) had the answer.
- **So: a claim has a SUBJECT and a MECHANISM, and they fail independently.** Disproving the mechanism
  disproves nothing about the subject. Check both, and when only the mechanism is wrong, fix only the
  mechanism.

## The rules

- **Write behavioural prose from the implementation, not from the design doc.** The docs for
  `Shenora`'s mission scheduler were written from `docs/2026-08-02-shenora-mission-scheduling-design.md`
  and three claims did not survive a read of `src/Shenora/Engine/Missions/`: an unknown LANE was documented
  as throwing when `MissionScheduler.CreateEntry` creates it at the default capacity; `IMissionObserver` read
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
- 🔴 **A claim about the WORLD decays with nothing in the repo changing, and NO gate can catch it** —
  a registry's configuration, a machine's state, a deadline. `decision-audit` checks names against the
  tree and finds these untouched, so the file keeps asserting them and a reader takes the backlog for
  the present. **Ask the world, not the doc**: `npm view <pkg> --json` (`_npmUser` names the publisher),
  `Get-Process` / `adb devices`. Twice on 2026-08-20: "npm needs a token until the publisher is
  configured" (it had been configured for days — 0.11.0 published over OIDC) and "the wedged emulator
  needs a reboot" (already dead, `adb` saw nothing). ⚠ **`TASKS.md` is the worst offender**, because an
  entry describes a world that moved on while the entry sat still.

- 🔴 **AUDIT THE SENTENCES, NOT THE CODE FENCES — measured, and it settles where the effort goes.** The
  snippets are fine and always have been: a checker that reads every `.Method(…)` in a `csharp` fence and
  compares its ARITY against the tracked API baseline found **0 mismatches at every release tag** (v0.3.0 →
  HEAD, 7 → 40 call sites), while proving it fires — planted calls were caught with the right allowed sets.
  So it was NOT kept; a gate that has never fired only costs attention. **Every doc defect this repo has
  actually found was in the PROSE AROUND the snippet** — the `required` claim (D70), the mission-scheduler
  three, the `MediaPlayerModule` mechanism. A snippet is copied from working code; a sentence is written
  from memory of what the code used to do.
- **A MODIFIER is a claim too, and it is the one no gate can see.** `MediaConversionOptions.Convert` stopped
  being `required` and two docs said it still was, with the API baseline and the CHANGELOG both correct —
  nothing was renamed, so every name gate had nothing to match. `dev.mjs retired-audit` now prints
  `required` deltas for exactly this; **read its `NO LONGER required` list as a list of paragraphs to
  re-read.**

## A shipped XML doc is adopter-facing API documentation, not a record of how a bug was found

🔴 **`///` is extracted into the nupkg and renders in an adopter's IDE.** The bar it has to clear is
*would a reader who never saw the incident act differently for having read this?* — and when 45 % of
`src/` was comment, most of what failed that bar was session log: dated measurements, "found in review",
"the tell was", "it cost us a day". `doc-shape` sweeps for those and keeps the count at zero.

- **Keep a post-mortem comment when it states what must not be done again.** *"Both platform encoders are
  configured not to reorder, so this is a fail-closed guard on an assumption"* is actionable. *"Found by
  review 2026-08-10"* is not.
- 🔴 **The TENSE is the tell, and moving it is usually the whole fix.** *"Produced an over-claim"*
  describes an incident that happened to us; *"produces an over-claim"* describes the design. An adopter
  needs the second, and the first is what the commit message is for.
- ⚠ **A measurement usually survives — its DATE usually does not.** *"An iPhone decodes AC-3 via
  AudioToolbox"* is a fact a reader can use; the day it was measured belongs with the measurement, in
  `docs/design/`. ⚠ Except where the date IS the subject (*"printed the raw integer until `<date>`"*),
  which is why this cannot be scripted — a blind date-strip over the tree was tried and reverted.

## Gotchas / traps

- **"Throws" claims are the expensive ones.** A wrong "this throws" costs an adopter silently — the
  lane case gives them a second lane at the default capacity and none of the exclusivity they
  configured, with no error and nothing in the log. Check every *throws / never throws / always /
  never / defaults to* statement against the code path, not against the neighbouring one.
- **"The kit ships X" ages badly.** X gets cut before publish (design §6's filesystem layer) or is
  planned and never written (`IMissionObserver`'s registry adapter). Grep for the type before saying it
  exists; if the app must write it, say so in the same sentence.
- **De-identify per `sensitive-info.md` while you are in there** — an adopter-facing doc describes
  roles ("the primary desktop sibling"), never a private project's name or file paths. The named
  per-sibling mapping lives in `local/`.
