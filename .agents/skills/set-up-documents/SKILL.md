---
name: set-up-documents
description: Set a repository's development documents up to the standard, or repair ones that drifted — the brief, the rooms, the records and the declaration of safe work — from the templates beside this skill. Use when a repository is set up for agents, when a request asks for it, or when a session could not find where something is written.
---
<!-- daoris: core/core/skills/set-up-documents/SKILL.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# set-up-documents

The procedure behind the `development-documents` knowledge document, which says why each step is shaped
the way it is. Read that first. The templates are in `templates/`, beside this file.

## Before you start

- **The repository's own words are kept.** Every step moves, trims or declares what is already here.
  Nothing is deleted until each of its lines is somewhere else, and the test for a line you are about to
  drop is not "is this obvious" but "does anything else say it".
- **This repository only.** A document another repository should change is a request to whoever works
  there, never an edit made from here.
- **The result is for review.** It changes what every later session here reads, so it is left for a
  person to see whole, with what moved where.

## Steps

1. **Inventory by role.** For each role in `development-documents`, name the file that plays it, or
   none. Look under the usual names: a changelog, a decisions file or a folder of decision records, a
   fix log, a backlog or task list, a documentation index, a glossary. Note any file playing two roles,
   and any agent-specific instruction file that only some agents read. Measure the root instruction file
   in bytes, and each document read whole in words.

2. **Write or trim the brief** from `templates/brief.md`, in the root instruction file, outside any
   region a tool generates. Keep every heading; a section with nothing to say says *none*, so a reader
   can tell an empty section from a forgotten one. Move into it what every agent needs from an
   agent-specific file, and leave there only what that one agent needs. Hold every line to the content
   test, then move each line that fails to the tier that holds it:
   - a deep dive one area needs → a knowledge document (`templates/knowledge.md`)
   - one folder's conventions, traps or checks → that folder's room
   - history, and what was rejected → the decisions
   - status and counts → the backlog, or the one place that keeps them
   - a permission → the declaration (step 6)

3. **Write the rooms.** A folder whose conventions, traps or checks differ from the rest of the
   repository gets an instruction file of its own, from `templates/room.md`. Not every agent loads a
   nested file, so each room must be named in something always read: the tool's declaration of rooms
   where there is one, the brief's *Layout* where there is not.

4. **Place the records.** Give each record the repository keeps a line in the brief's *Where things
   are*, or declare it where a tool generates that section. Check its shape against its template (see
   the table below). New entries take the shape; old entries stand as they were written, because
   rewriting history into a new form loses what the old form said. A backlog row that holds more than
   its shape is open work, so its excess moves to the record that holds it (a log of sightings to the
   fix log, a history to the decisions, a design's text back to the design), and the row points there.

5. **Give it an index of where things are**, from `templates/index.md`. First list every index the
   repository already keeps, written by hand or generated (a router, a file named as an index, a routing
   skill, a code map), and keep each as and where it is. One that is generated and checked is the index:
   name it, and add nothing. Otherwise write a generator in the repository's own toolchain, beside its
   other tools, needing nothing the repository does not already use and writing the same files on every
   machine. Put its check in the command that means done. Mark its folder as generated, so a review
   folds it and a merge leaves it to be written again. The generated index lists the hand-kept ones
   first and restates none of their rows. The brief's *Where things are* names it.

6. **Declare the safe work** in the declaration beside the gates: the checks a session may run (not the
   ones that need the machine quiet or run for a long time), the build and test commands, the install
   from the lockfile. Exact, one command per entry, and none of the carve-outs. The brief's *Build,
   test, verify* names the one command that means done and says where the declaration is. It takes
   effect when a person accepts it, and the review says so.

7. **Measure.** Each document read whole against its ceiling in words; the root instruction file,
   brief and doctrine together, against the smallest byte limit among the agents the repository serves.
   Over is reported, never hidden. Relocate, then condense, then raise the ceiling with the reason.

8. **Check, and leave it for review.** Run the doctrine check where there is one and the repository's
   own build and tests. Then hand over: each line that left the brief and where it went, each record
   placed, each room written, each index the repository already kept and how the generated one differs,
   the declaration and what it allows, and every measure against its ceiling.

## The templates

| Role | Its shape |
|---|---|
| brief | `templates/brief.md` |
| room | `templates/room.md` |
| knowledge | `templates/knowledge.md` |
| router | `templates/router.md` |
| index | `templates/index.md`, the folder's README |
| decisions | `templates/decision.md`, one entry |
| backlog | `templates/backlog-row.md`, one row |
| archive | `templates/archive-entry.md`, one entry |
| fixes | the `fix-log` skill's entry |
| skill | a folder named for it, holding its entry file, whose frontmatter is `name` and `description` |
| changelog, glossary, gates | the repository's own shape, holding what `development-documents` says each holds |

**Copy a template's body, never its provenance line.** A template arrives stamped as shared doctrine.
On a copy, that line tells the next reader not to edit a file that is theirs. Replace every `<...>`;
a placeholder left in place reads as an instruction to whoever opens the file next.

## Why

Every session in the repository pays for what this sets up: the brief on every start, a missing record
on every search. Done once and with care, it is the cheapest improvement the repository's agents will
get. Done by accretion, the brief grows until an agent that cuts at a byte limit loses the doctrine at
its tail, and nobody sees it happen.

This skill pre-approves no tools, on purpose. What a session may run is the declaration's to say, and
a person accepts it.
