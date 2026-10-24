---
name: post-feature
description: Audit a finished feature or fix before proposing a commit — every layer the change touched has its counterpart, the records it made stale are updated, and any reusable pattern it revealed is written down. Use when the implementation looks done. Skip for typos and one-line tweaks.
---
<!-- daoris: core/core/skills/post-feature/SKILL.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# post-feature

The gap this closes is between **"it compiles"** and **"it is finished"**. Those differ by a set of links
that are invisible from the code you just wrote, because each one lives somewhere else.

## Steps

1. **Scope the change.** Read the actual diff, staged and unstaged. Audit what changed, not what you
   remember changing — the two diverge exactly when a session ran long, which is when this matters most.

2. **Close the wiring chain.** For each new unit the diff introduces, ask what else must exist for it to
   work end to end, and check each: registration or wiring, the schema or data change, the counterpart in
   every parallel set the codebase maintains, configuration, and a test that exercises it. **The
   counterpart sets are the ones that rot** — anywhere the codebase requires two things to stay in step,
   a change that updates one and not the other compiles perfectly and is wrong.

3. **Refresh what the change made stale.** Any record that described the old behaviour now misdescribes
   the new one: the status or roadmap entry, the user-facing log, and the document a newcomer would read
   to understand the area.

4. **Read the prose the change added, against the list below.** Documentation is the one surface with
   no compiler, so a pass over it is the only check it gets.

5. **Ask whether this revealed a reusable pattern.** If the work turned out to require a specific
   sequence of edits across several files in a particular order, that sequence is knowledge, and right
   now is the only moment anyone knows it. Write it down as a rule or a knowledge document. **This is how
   the system compounds** — the next task starts where this one ended instead of rediscovering it.

6. **Report before committing.** List each item as done or missing, and give the concrete follow-up for
   every missing one. Do not propose a commit while anything is outstanding unless it has been explicitly
   deferred — and say which, so the deferral is a decision rather than an omission.

## The prose pass

Each of these is writing that looked useful while it was being typed and costs a reader something
every time afterwards. Hunt them in what the change added:

- **The same rule in two places.** Search a distinctive phrase from anything newly written. Where it
  already exists, keep one home and link the other — two copies of a rule become two different rules.
- **History outside the record that holds history.** A document describing current behaviour should say
  what is true now; what changed and when belongs to the decisions record, the changelog, or the commit.
- **Status annotations in prose** — "implemented", "not yet wired", "future". Status rots in place while
  the code moves, and a stale claim is read as current by everyone who was not there.
- **A catalogue restated by hand** where a generated index or the source already answers it. The copy is
  wrong from the first change nobody remembered to mirror.
- **The path taken to the answer, rather than the answer.** Step-by-step narration of how it was
  built, proof of the obvious branch, and a walkthrough of the tests. Keep the resulting contract and
  the durable reason; delete the derivation.
- **Rationale repeated beside each sibling** instead of stated once where the capability lives.
- **A paragraph carrying four rules and three asides.** Split it, or move the detail to the tier that
  holds detail.
- **Emphasis everywhere.** When several sentences are bold, none of them is; keep it for the clause
  that changes what a reader does.
- **Intent where the record should state fact.** "Should", a migration plan and an acceptance checklist
  belong to work that is proposed; once it has shipped, the same document says what is.

## Why

Every item here is something that fails *silently*. A missing registration, a translation added on one
side only, a schema change with no migration, a record still describing last week's behaviour — none of
them break a build, and all of them surface later as a defect that looks unrelated to the change that
caused it.

The pattern-capture step earns its place for the opposite reason: nothing fails at all if it is skipped.
The cost is paid by the next person, who spends the same hours working out the same sequence, and who has
no way to know it was ever worked out before.

The prose pass is there because documentation has no compiler and therefore no other check. A wrong
sentence can outlive the behaviour it describes for a whole release with every gate green — and it is
believed for exactly that long, because the confident sentence is what tells a reader not to go and
look. The list is a checklist rather than a rule for the same reason a checklist beats a principle at
the end of a long session: by then the writing all looks necessary to the person who wrote it.
