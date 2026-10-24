---
name: reaching-in
applies_when: you have written into another repository, or are about to, or are repairing one you already touched
enforces: stop and report rather than repair; treat a tree-state observation as stale the moment you look away; never revert a file you do not own
---
<!-- daoris: core/core/knowledge/reaching-in.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# What happens when you reach into another repository

The rule is `repository-owns-its-work`: never write into a repository that is not the one you are
working in. This is the incident it was written from, kept because the failure was not the first edit —
it was everything that followed from it.

## What happened

An agent published a task into a sibling repository's backlog. One file, one appended section,
uncommitted, clearly marked, with a note saying the receiving repository should decide. It looked like
the polite version of asking.

Correcting it went worse than the original mistake:

1. **The write itself.** A single file in a repository the agent did not own.
2. **The removal over-deleted.** A script meant to cut one section cut 171 lines, because it assumed the
   section ran to the end of the file.
3. **The repair destroyed someone's work.** Restoring that file from its last commit discarded an
   unstaged edit the sibling's own session was about to commit.

Three writes. One piece of work lost. Every step was well-intentioned, and each was worse than the one
before.

## What was actually wrong

**The reasoning, not the tooling.** The agent checked that the sibling's tree was clean, and then relied
on that an hour later — while another session was working in it. A tree-state observation is a fact
about a moment. If anyone else is working there it is stale immediately, and the entire point of this
arrangement is that someone else is.

**The repair was another outside edit**, made with less information than the first, against a tree that
had moved. That is the shape to watch for: the instinct to clean up after yourself is exactly what turns
one mistake into three.

**The rule already existed and was already known.** So did "reverting to the last commit is not an undo",
which is canonical elsewhere. Knowing a rule is not the same as noticing that it applies — and it
applies most where the action feels smallest.

## How to apply

- **Never write into another repository.** Publish the request — a quest where a request system exists,
  a message to that repository's owner where none does. There is no case where writing across is the
  answer, because the request is.
- **If you have already written, stop and report it.** Say what you touched and where. Do not undo it —
  the owner can, with context you do not have, and losing their work while tidying is the realistic
  outcome of trying.
- **Never revert a file you do not own.** Reverting to the last commit is not an undo; it silently
  discards whatever uncommitted work that file was carrying, and you cannot see what that was.
- **Treat "it was clean when I looked" as expired.** Re-check immediately before acting, and prefer not
  acting.
- **Assume concurrency.** In a family of repositories with their own agents, another session is probably
  working right now in the one you are about to touch.

## The general form

**A tool that enforces a rule is the most likely thing to break it**, because the person building it is
thinking about the mechanism rather than the principle. This edit was made *by the feature that shipped
the rule against it*, in the same change. When you build enforcement, check the enforcement against
itself first — and prefer a check that makes the violation impossible over one that describes it.
