---
name: development-documents
applies_when: setting a repository up for agents, adding, moving or splitting one of its documents, writing an entry into one, or when a session had to search for where something is written
enforces: a short brief every agent reads, detail on demand, records in places the always-read file names; a document read whole has a ceiling and a record read by lookup has none; an entry points to its detail; what may run unasked is a declaration a tool reads, never a sentence; where things are is a generated index the repository's own check keeps true
---
<!-- daoris: core/core/knowledge/development-documents.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# The development documents — what a repository keeps so a session works unasked

**A code-generating session starts with what it is handed and what it can find. Give every document
one job and one way it is read: a short brief always, detail on demand, records by lookup. Then say, in
the brief, where each one is.**

## Why

Each failure this prevents is silent.

- **A brief grown past a byte limit.** At least one widely used agent reads the root instruction file
  only up to a fixed size, 32,768 bytes when this was written, and drops the rest without a word. The
  shared doctrine sits at the end of that file. A brief that grows a paragraph at a time pushes the
  rules out of that agent's context, while every other agent still sees them, and a session that
  missed them looks exactly like one that read them.
- **A settled question reopened.** A decision recorded without what it rejected is re-derived by the
  next session, which reaches the rejected alternative, finds it reasonable, and builds it. The record
  that would have stopped it existed. It did not say *why not*.
- **A command allowed in prose and refused by the harness.** An instruction file shapes what an agent
  tries, not what its harness permits. "You may run the tests", written in a brief, is followed by a
  refusal and a stall. A sentence that forbids something stops nothing either.
- **The search before the work.** The doctrine names records by their job (the backlog, the decisions,
  the fix log) and never by their path, which is right for doctrine and costs every session in every
  repository a search. A session that guesses wrong writes to the wrong one. Measured in one
  repository, a third of each session's calls came before its first edit, spent finding its way; and a
  search names a file, not the line in it.
- **A record written twice, and paid for on every step.** A document's cost is its size times the steps
  that follow it: a session carries what it read into every step after, and a cache makes the repeat
  cheaper, never free. A retold entry costs every later reader and tells them nothing the first copy
  did not. A backlog trimmed without changing how its rows are written grows back.

The agents' makers reached the same answers independently: one short file every agent reads, holding
only what cannot be derived from the code; exact commands and one check that means done; detail moved
to a tier read on demand; a ceiling on what is read whole and none on what is read by lookup. Two
sources reaching one rule is the bar for believing it.

## How to apply

The `set-up-documents` skill carries the procedure and a template for each shape below.

### The roles

A **role** is a document's job. One document per role. Every role but the brief is optional, and the
names and paths are the repository's own.

| Role | Its one job | Read |
|---|---|---|
| **brief** | what this is, the constraint every change serves, the non-negotiables, the layout's non-obvious half, the command that means done, and where everything else is | always |
| **room** | one folder's conventions, traps and checks, in that folder's own instruction file | on demand, when working there |
| **knowledge** | a deep dive one area needs, saying when it applies and what it enforces | on demand, from the index |
| **skill** | a procedure, invoked by name | its description always, its body on use |
| **router** | each document, its kind (contract, method, study, evidence, record) and its standing (current, amended by, superseded by) | whole, at a task's start |
| **index** | where things are in the code and the records, by what a session looks for, each with its file and lines; generated, never edited by hand | by lookup, before any search of the code |
| **decisions** | numbered decisions, each with why, what it rejected, and what the checks do not cover | by lookup |
| **backlog** | open work only, each row naming its contract and its proof | whole, when picking work |
| **archive** | finished work, each with its date and outcome | by lookup |
| **fixes** | root cause, fix and verification per non-trivial defect | by lookup |
| **changelog** | what a user of a release sees changed | by lookup |
| **glossary** | the names people and code use for this repository's things | by lookup |
| **gates** | the checks, their exact commands, and the work a session may do without asking | by tools |

Design documents are listed by the router rather than given a role each: they come and go too often. A
roadmap is the backlog's sequence, and the router lists it. A glossary pays where a repository names
things to people. Elsewhere it is a document nobody opens, so no repository is required to keep one.

### Three ways a document is read

- **Always**: the brief, beside the always-loaded doctrine, in the one root instruction file every
  agent reads. It is the only file known to reach every agent at the start of a session. A nested file
  is not loaded by all of them, and not every agent reads a second file the first one imports.
- **On demand**: rooms, knowledge, skill bodies, the router, the design documents. Something always
  read names each one, or names the index that lists them, because telling is how an on-demand tier
  reaches an agent. A list that grows with the repository is read on demand too.
- **By lookup**: decisions, archive, fixes, changelog, glossary, and the index of where things are. Read
  for one entry and never whole, so they may grow without limit.

Name a knowledge document by its subject, and say when it applies in its first lines. A search finds
what a file's name and opening say, and that is how a document is found where no index is read.

### What a session pays for: one home per fact, and entries that point

A session pays for what it reads on every step after it reads it. So what is read whole stays short by
how each entry is written, and what is long is read by lookup. Each fact has one home, and every other
place names that home in a line.

- **A backlog row**: an identifier; what and why in two sentences; its contract, by name and section;
  its proof. Never the design's own text, a log of sightings or a history: those are records, and the
  row points to them.
- **An archive entry**: the row as it stood, the date, and the outcome in a line or three: what
  changed, and where its detail lives.
- **A router row**: the document, its kind, what it is for, and where it stands in a line, naming the
  decision that changed it. What was built under it is that decision's.
- **A decision's amendment**: under the entry it amends, headed by the work that made it.
- **Look up by identifier.** A record is searched for the identifier a row or a decision names and read
  at that entry; a design is read at the section a row names. Reading a record whole to find one entry
  pays for every other entry in it.

### What goes in the brief

**A fact goes in the brief only if nearly every task needs it and a reader could not derive it from the
code.** The exact command that means done is in. A tour of the folders is out. Of the layout, only what
the tree does not say: which folders are generated, which are copies of another, which have their own
instructions, and what must never be edited by hand. Of the conventions, only those that differ from
the language's and the tools' defaults.

A brief never holds status (*built*, *not yet*), which rots in place; history, which belongs to the
decisions; counts, which belong to the one place that keeps them; a quotation of a person, which
belongs to the decision it motivated; or a permission, which belongs to the declaration.

### Ceilings

A document read whole has a ceiling. A record read by lookup has none. Two units, because two things
are paid for:

- **Bytes, for the root instruction file as a whole**: the brief and the doctrine together, under the
  smallest limit among the agents the repository serves. What is lost is the tail, and the tail is the
  doctrine.
- **Words and lines, for attention.** Starting ceilings, each with headroom over what a document
  measures when written: the brief, 1,500 words and 200 lines; a room, 600 words; the router, 2,500
  words; the backlog, 5,000 words.

An entry has a shape beside them, in words, whether its record is read whole or by lookup: a backlog
row or a router row, 60 words; an archive entry's outcome, 60 words. They report, like every ceiling.

**A ceiling reports and never fails**, because whether a document says too much is a judgement. When one
goes over, relocate what belongs in another tier, then condense, and only then raise the number,
deliberately and with the reason. What fails is a fact: a named document missing, or a list of places
that no longer matches the files.

### Where the records are

The brief ends with **where things are**: one line per record the repository keeps, its role and its
path. Where a tool generates that list from a declaration, declare the records and let the tool keep
the list true. Without one, write it by hand. A record with no line there is a record no session finds.

### An index of where things are

A session that knows what it needs but not where it is will search, and the search is its largest cost
before its first change. A search names a file, not a line, so the read that follows is the whole file;
and a long record is read whole to find one amendment in it.

- **Generate it and commit it.** A tool in the repository's own toolchain writes it from the files, and
  nobody edits it by hand. Committed, because a file is read by every agent on every machine with
  nothing installed and nothing running. A hand-kept list is wrong the moment the code moves.
- **By what is sought, each row a place.** Group it by what sessions look for here (an entry point, a
  command, a key, a fixture), each row naming a file and a line range, so the read that follows is a
  range.
- **Outline what is too long to read whole**: its declarations or headings, each with its lines.
- **Digest the decisions**, where there is a decisions record: each decision's title and lines, and each
  dated amendment's label and lines.
- **The repository's own check keeps it true**, failing on a stale index and naming the command that
  writes it again. A conflict in it is resolved by writing it again, never by hand, and it is marked as
  generated so a review folds it.
- **The brief names it in a line**, and says to open it before searching.
- **An index the repository already keeps stays**, the knowledge's among them. The generated one links
  it and restates none of its rows.

### What may run unasked is a declaration

What a session may run without asking goes in a declaration a tool reads, beside the gates, and a
person accepts it once: the checks that are a session's to run, the build and test commands, the
install from the lockfile. The brief says only where the declaration is.

- **Exact commands, one per entry.** A pattern that admits any argument to a runner or an interpreter
  admits anything at all.
- **The carve-outs never enter it**: a push, a publish, a release, a history rewrite, a discard, a
  recursive delete, a path outside the repository (`autonomous-development`). They stay the person's.
- **A declaration takes effect once it has landed and a person has accepted it.** One that a session
  can edit and have obeyed in the same run is an agent granting itself permission.
- **Not every check is a session's to run.** One that needs the machine quiet, opens windows or runs for
  a long time belongs to whoever runs the full set of gates, and the declaration says which.
- **No side door.** A procedure whose own metadata pre-approves tools, honoured without review, is the
  same widening in a smaller form. No shared skill carries one.

### Without the tool

Every part of this is a committed file. A repository with no doctrine tool writes its list of places by
hand, keeps its ceilings by judgement, and its declaration is read by whatever reads it. A tool adds
two things: the list kept true, and the facts checked. The index is the repository's own: its
generator, its check and its files need no doctrine tool, and neither does reading it.
