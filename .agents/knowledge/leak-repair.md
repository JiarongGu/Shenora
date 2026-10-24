---
name: leak-repair
applies_when: a credential, machine path, or private name has already been committed, or a repository is about to be made public
enforces: repair history rather than the working tree; scrub blobs, paths and messages together; prove the scrub with a scan you have seen fail
---
<!-- daoris: core/core/knowledge/leak-repair.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# Repairing a leak that is already committed

**Editing the file fixes the checkout and nothing else.** The value is still in every commit that
carried it, in every path that file ever had, and possibly in a commit message. A leak that has been
pushed is worse again: there are copies you no longer control, and the only complete response includes
**rotating the credential**.

This is the expensive path. The cheap one is a pre-commit scan, which catches a leak while it is still
staged — the only point where the fix is free.

## Before you start

- **Rotate first if it is a credential.** A scrubbed history does not un-disclose a token. Revoke it,
  then clean up; doing it in the other order leaves a live secret valid for however long the cleanup
  takes.
- **Take a backup you can restore from** — a full bundle of every ref, kept outside the working tree.
  History rewriting is the one routine operation with no undo.
- **Then remember the backup contains the leak.** It is a complete copy of the history you are about to
  clean, so it is the thing most easily forgotten and most exactly wrong to leave lying around. Delete it
  once the rewrite is verified, along with any scratch file holding the old value.
- **Rewriting shared history breaks every clone.** Everyone re-clones; anyone who merges instead will
  reintroduce the old objects. Agree that before you start, not after.
- **Decide whether you are removing a path or replacing content.** Dropping the file entirely and
  swapping its bytes everywhere are different operations with different commands, and the second is what
  you want when the file must still exist — a document with one bad image, say.

## Scrub all three surfaces, not just the obvious one

A rewrite that only replaces file contents leaves two of the three:

- **Blob contents** — the value itself.
- **Paths** — a file *named* after a private project leaks it whatever the bytes contain, and renaming
  it later leaves the old name in history.
- **Commit messages** — history too, and the half nobody reviews. Content and message replacement are
  usually separate options of the same tool; running one is easy to mistake for running both.

Then expire the reflog and garbage-collect. Until that happens the old objects are still reachable
locally, and a scan will keep finding them — which reads as a failed scrub.

## Prove it, then prove the proof

**A clean history scan deserves the same suspicion as a passing test.** Plant a pattern you *know* is in
history, confirm the scan reports it from a blob *and* from a commit message, then remove it. A scan
that has never produced a hit is indistinguishable from one that is searching nothing, and this is the
worst possible thing to be wrong about — the whole point of running it is to be trusted before a push.

Verify from the other direction too: search every reachable object and every message for the token and
require zero results. Two independent checks, because the one you wrote is the one you might have
written wrongly.

## After the rewrite, before the push

The tool leaves the repository in a state that does not look like the one you started in, and two of the
surprises will cost you a confusing hour:

- **It usually removes the remote.** Re-add it before pushing, and notice that this is a safety feature
  rather than a bug — it makes an accidental push impossible until you have looked at the result.
- **Tags need pushing too**, and were rewritten along with everything else. Tags pointing at commits older
  than the earliest rewritten one keep their identity; later ones moved.
- **The push is the outward, irreversible step.** Everything before it is local and recoverable — until
  you force-push, the remote still holds the original history and a fetch restores it. Confirm explicitly
  before taking that step; it is not one to take on someone's behalf.

## Traps

- **A dirty working tree blocks the rewrite.** Commit or discard first — and if you discard, remember
  that reverting a file to its last commit throws away uncommitted work it was also carrying.
- **Abbreviations survive a scrub of the full name.** A project scrubbed as `NorthwindTelemetry` lives
  on as `NWT` in a variable, a folder, or a commit subject. Sweep for the short forms and add each to
  the pattern list so the guard catches them next time.
- **Pushing all branches republishes what you just cleaned.** A local-only branch still holding the old
  objects goes up with a push-everything command. Push the one branch you cleaned.
- **A forge keeps its own copies.** Pull requests, forks and cached views can outlive a force-push, and
  some require asking the provider to drop them. Assume disclosure and rotate.
- **The audit is not a gate.** Scanning all history costs more as history grows, so it belongs at
  moments — before going public, and after a scrub to prove it worked — not on every commit, where it
  would re-check commits that were already checked when they were made.
