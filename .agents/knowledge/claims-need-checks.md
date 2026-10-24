---
name: claims-need-checks
applies_when: writing any statement about what the code does or guarantees — a readme, a doc comment, a config field, a status line
enforces: verify behavioural prose against the implementation, not the design; ship the check in the same change; say which claims the gate did not cover
---
<!-- daoris: core/core/knowledge/claims-need-checks.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# A statement about behaviour is verified against the code, or it is not a statement

**When you write that something is guaranteed — always, never, throws, defaults to, verified, enforced —
check it against the implementation and ship the thing that enforces it in the same change. If you
cannot enforce it yet, say so in the same sentence.**

## Why

Prose is the one surface with no compiler. A remark saying what the code does can be wrong for a whole
release with every gate green, because no gate reads it.

An unenforced guarantee is worse than none. Silence invites a reader to check; a confident sentence
tells them not to bother. So the claim outlives the behaviour and is believed for exactly as long, and
nothing reports the divergence — the whole point of the sentence was that nobody needed to look.

Carelessness is not the cause. The claim and the enforcement are written at different moments and only
the claim is easy: documentation gets written while the behaviour is fresh and obviously true, which is
the one moment a check feels redundant and the moment it is cheapest to write.

Two repositories in this family derived this independently and from opposite ends — one auditing shipped
API documentation against its own source, one finding a configuration field parsed and never read.
Neither could have found the other by searching: they share almost no vocabulary.

## How to apply

### Write from the implementation, never from the design

**A design document states intent, and intent is what the last edit before merge changes.** Prose
written from the design describes the feature that was planned. Open the file, find the line, then
write.

One audit doing exactly that found three claims that had not survived contact with the code: a case
documented as throwing that quietly succeeded with a default instead, an interface described as though
an adapter shipped alongside it when nothing implemented one, and a helper from the design that was
never written at all.

**Spend the check on the newest code.** Older areas have survived reviews; a component documented in one
burst alongside its own design has had no second reader. In that same audit the long-shipped surfaces
were clean and everything wrong was recent.

### Enforce it, or say that you did not

- **Same change, or state the gap.** "Never touches the network" earns a check that fails when something
  does. A stated gap is honest and gets fixed; an implied guarantee is neither.
- **A check you have not watched fail proves nothing.** Break the behaviour, see it go red, restore it.
  Until then it is indistinguishable from a check that tests nothing. See below — the sabotage itself
  fails silently more often than people expect.
- **A surprising behaviour gets a test, not a corrected comment.** A comment that contradicts intuition
  gets "fixed" back by the next reader who finds it surprising. Only a test survives that.
- **Say which claims the gate did not cover.** A green build on a documentation-only change proves
  nothing about the words. Report that, rather than letting the green imply the prose was checked.
- **A parsed-and-unused input is a claim.** A field that exists says it does something.

### How a check passes without checking

Watching a check fail is the discipline. The trap is that **the sabotage can fail silently too**, and
then a green run is read as proof. These five shapes account for most of it, and each has been hit for
real:

- **The sabotage did not apply.** A scripted edit whose pattern no longer matched changed nothing, the
  check passed, and that was recorded as evidence. If you break something, *confirm the file changed*
  before believing the result — count the substitutions, or read the line back.
- **The sabotage used a form the check does not look for.** A guard that scanned one import syntax was
  "proven" by breaking it with another. The check was real and the proof was not. Sabotage in the shape
  a *realistic* regression would take, and preferably in more than one.
- **The thing under test was not the thing that ran.** A build emitted to an unexpected directory and
  the entry point silently fell back to sources: every command worked, the exit code was zero, and
  nothing built was being exercised. Assert on the artefact, not on the exit code of something that may
  have substituted for it.
- **The runner quietly saw fewer inputs.** A file-matching pattern behaved differently on one platform
  and dropped a test file; the suite stayed green and the count fell by one. Watch the *count*, not just
  the colour — a suite that shrinks is a suite that stopped asking something. The harder half is when
  the count never *rose*: a pattern written for a flat layout stops reaching once the code grows a
  subdirectory, and the files it now excludes have no earlier count to fall from. **Proving one
  pattern's reach proves nothing about the next one** — each is asserting its own scope, so each needs
  a real file placed where it is supposed to look.
- **Nothing runs the check.** A configuration file declared gates that no build step, hook or workflow
  ever invoked, so the declaration read as coverage for months while nothing executed it — and when it
  was finally run by hand it failed immediately. A check is only worth what invokes it, so before
  believing a new one, follow the path from the command people actually run to the code you just wrote.
  **Where you put a check matters as much as what it asserts**; a correct check in a tool nobody calls
  is indistinguishable from no check at all, and it is worse, because the declaration looks like one.

The common thread: **every one of them was green first.** Treat a green that arrives faster or more
easily than expected as a question rather than an answer.

### The expensive words

`always`, `never`, `throws`, `cannot`, `defaults to`, `guaranteed`, `enforced`, `verified`, `ensures`.
Reaching for one is the moment to ask what would catch it being false — and grepping for them is how you
audit documentation you inherited.

**"Throws" is the costliest to get wrong**, because it fails silently: a caller told to expect an error
gets a quiet default, with nothing in the log. **"Ships with X" ages badly** — X gets cut before release,
or is planned and never written. Grep for the thing before saying it exists, and if the consumer has to
supply it, say so in the same sentence.
