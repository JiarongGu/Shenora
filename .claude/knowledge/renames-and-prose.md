# Renaming or removing a public name — three steps, and the third gets skipped

**Applies when** you rename or delete anything a document, an XML doc or an adopter could name: a type, a
member, a package id, a verb, a file. **Not** for renaming a local or a private field — nothing outside the
compiler knows those, and the gates below have nothing to find.

🔴 **A RENAME OR REMOVAL IS THREE STEPS, AND THE THIRD IS THE ONE THAT GETS SKIPPED.** (1) change the code,
(2) add the old name to `devtools/retired-names.txt`, (3) **run `node devtools/dev.mjs stale-scan` IN THE
SAME COMMIT and triage its worklist.** Step 3 exists because the gate fed by step 2 *cannot* find the prose
you just invalidated: `doc-drift` suppresses any match within 6 lines of a history word, so a stale claim
hides exactly where the suppression is active (proven by sabotage — a planted claim stayed green in
`TASKS.md` AND `ARCHITECTURE.md`). D66 did steps 1 and 2, and `docs/ADOPTION.md` told adopters to call a
deleted API for three commits with every gate green. `stale-scan` never fails a build: most hits are correct
past tense and **only a human can tell those from a live lie**. That triage IS the deliverable.

🔴 **A RENAME ALSO DAMAGES THE ONE SENTENCE THAT NAMES IT.** A repo-wide sweep replaces the old name
everywhere — including the prose whose SUBJECT was that name — leaving "`X` depends on `X`", "`X` → `X`".
Grammatical, passes every gate, and nonsense exactly where a reader goes to learn what changed; five landed
across the docs in two days, three of them after two prose audits ran clean. `doc-drift` and `cite-scan` are
blind to it because both names exist and they are the same name. `node devtools/dev.mjs self-rename-scan`
lists them; **skip `local/`, read the diff, and when you rename, re-read the entry that DEFINES the rename.**

⚠ **A SHIPPED XML DOC IS PROSE TOO — and it is the prose this repo never re-reads.** It renders in an
adopter's IDE straight from the nupkg, so `self-rename-scan` reads `src/` and joins comment BLOCKS (an XML
doc wraps, and a per-line matcher sees neither half).

⚠ **A repo-wide SCRIPT must exclude `local/`** — it is an informal ARCHIVE, and its value is that it records
what was true THEN. A sweep that "helpfully" renames through it destroys the reference rather than updating
it. (`persist-working-state.md` carries the incident.)
