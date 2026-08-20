# Missions and file updates — as built

**Maintainer-facing.** The Engine layer: what schedules work, and what mutates the filesystem safely. For
USING either read [`../guides/missions.md`](../guides/missions.md) and
[`../guides/file-updates.md`](../guides/file-updates.md); for WHY, the decisions linked below —
**this doc states the design, never the rationale** (D77).

## Two components, one reason they are separate (D30)

| | Answers | Key types |
|---|---|---|
| `Engine/Missions/` | **when** may this work run? | `MissionScheduler` · `MissionClaim` · `ILane` |
| `Engine/Files/` | **how** does this mutation survive failure? | `FileUpdateQueue` · `IFileUpdateJournal` · `IPathLocker` |

Scheduling decides admission; the file queue decides atomicity. An app that needs both composes them —
a mission body submits an update — and neither knows about the other.

## Admission: three conditions, evaluated on every event

A pending mission starts when all three hold:

1. **no IN-FLIGHT mission holds a conflicting claim** — the safety rule;
2. **no EARLIER PENDING mission holds a conflicting claim** — the fairness rule, without which a steady
   stream of newer disjoint work starves a queued item indefinitely;
3. **a permit is free in every lane it named**, and none of those lanes is held.

Dispatch is **event-driven** — there is no polling worker and no dedicated thread. It is re-evaluated on
**four** occasions, and the last two are easy to miss:

- a **submit**, and each **completion**;
- a **lane change** — capacity or hold state, which is what makes releasing a hold take effect at once;
- a **queued item's own cancellation**, so a cancelled entry frees the slot it was waiting for rather than
  holding it until something else happens;
- and whenever an app calls **`IMissionScheduler.Reevaluate()`**.

🔴 **That last one is not optional for a policy that reads the outside world.** `IMissionPolicy` decides on
whatever an app gives it — a network state, a battery level — and nothing here can observe those change, so
a policy that starts refusing or permitting work has no effect until something re-triggers admission.
`Reevaluate()` is that trigger, and an app owning such a policy has to call it.

🔴 **Work never runs under the lock.** The gate covers bookkeeping only; admitted bodies are collected
into a local list and started after it is released. Running a body inline deadlocks the moment that body
submits more work, which every real user of a scheduler eventually does.

### Claims are declared as a SET

A mission names every claim up front and the scheduler takes them together, so there is no lock ORDER to
get wrong — that is the deadlock the design removes (D27–D29). Two shared holders of one key coexist;
anything else on a conflicting key does not. A chain is ONE queue entry holding the claim UNION, stronger
mode winning.

### Safety first, then policy

`DispatchLocked` decides safety (claims, lanes, fairness) and offers only the SURVIVORS to
`IMissionPolicy`. **So a policy can delay work but never corrupt it** (D57). A throwing policy is treated
as "not now" and logged; it cannot wedge the scheduler.

### Lanes

One global lane bounds everything (`clamp(cores-1, 1, 4)` by default); a named lane narrows a subset.
Every mission takes a permit from the global lane as well as its own, which is what makes the global bound
real. ⚠ A lane whose capacity exceeds the global bound runs at the global bound and SAYS so in the log —
storing the request silently is what made that invisible.

### Teardown has two strengths, deliberately

`DisposeAsync` cancels queued work and **awaits** in-flight bodies. `Dispose` cannot await — that would be
a blocking wait on whatever thread disposes, routinely the UI thread — so it signals and returns. It
exists because the framework registers a scheduler in EVERY app (D64) and an async-only singleton makes
Microsoft DI's synchronous `ServiceProvider.Dispose()` throw. Prefer `await using` whenever a mission may
be mid-write.

⚠ The shutdown token is captured ONCE at construction. `RunEntryAsync` runs on a pool thread that
`StartAll` only QUEUED, so reading it off the source there races disposal — and anything that throws
before that method's try block strands the entry with its submitter awaiting forever.

## File updates: plan, then apply, then commit

Every change is **planned before it is applied**, because an undo plan can only be computed from the
current state and is only useful if it is durable BEFORE the mutation.

```
for each change:  plan (what will it do, how is it undone)
                  → journal the plan          ← WRITE-AHEAD
                  → apply
on failure:       roll back in REVERSE order
on success:       journal Committing → run staged deletions → forget
```

🔴 **A journal entry written AFTER the mutation is missing exactly the change that got interrupted**,
which is the only one recovery needs.

- **A delete is STAGED** — moved aside, really removed only once the whole set lands — because a delete is
  the one change that cannot be undone from nothing.
- **Recovery rolls back `Applying` and FINISHES `Committing`.** An update that reached the commit marker
  had already succeeded; rolling it back would undo a success.
- **Every undo step checks the world first**, so recovery is safe to run twice — after a crash a step may
  already have been done, or never have happened.
- **Only `AllOrNothing` is journalled.** `PerChange` promises nothing about a crash.

### Locking is two mechanisms, not one (D31)

| | Excludes | Limit |
|---|---|---|
| `MissionClaim` | missions inside ONE scheduler in ONE process | in-process only |
| `IPathLocker` lease | any process that also takes a lease | ADVISORY — a non-participant is unaffected |
| `IFileLockInspector` | nothing — it ANSWERS "who holds this?" | best-effort; empty means "cannot tell" |

Leases are taken **after** the in-process gate (taking a cross-process lock while another thread of this
process holds the partition is waiting on ourselves through the filesystem) and in **sorted path order**,
so two overlapping updates cannot deadlock. Lock files live in the app's own storage, never in the managed
tree, and are `DeleteOnClose` so a crash cannot leave a permanent lock.

⚠ **A refused lease must report the path that REFUSED**, not the first in the set — the lock inspector is
asked about that path, and asking about the wrong one returns empty while looking like it worked.

## Path containment and case

`PathClaims` supplies both the claim scope (hierarchical, so `C:\a` conflicts with `C:\a\b`) and
`IsContained`. **The platform case rule has ONE definition, `PathComparison`**, shared by every
cross-platform containment check: a case-insensitive comparison is WIDER than a case-sensitive filesystem,
and a path dropped from a set is a path that never gets a lease.

⚠ `PathClaims.IsContained` and `WebViewFiles.ResolveContained` are deliberately NOT interchangeable —
serving refuses a `..` segment outright where scheduling resolves it, and serving excludes the root itself
where scheduling includes it. `PathContainmentDifferenceTests` pins both differences.

## What is deliberately absent

- **No filesystem abstraction as public surface.** Apps have their own and should keep them; the internal
  `IFileOperations` exists only so the queue's invariants are testable.
- **No pluggable async queue** for the scheduler's pending list — it stays internal and synchronous (D28).
- **No cancellation past the point of application.** A half-applied set abandoned mid-way is the one
  outcome no caller can do anything with.
