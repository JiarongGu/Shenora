# Shenora missions — the queue, and chained missions — design

**Status: BOTH PARTS IMPLEMENTED 2026-08-02** — Part 1 as `IMissionQueueStore`, Part 2 as
`MissionChain`/`MissionStep`/`IMissionChainContext` (10 tests). One claim below was NOT delivered and
is corrected in place rather than left standing: see §5's note on `Snapshot()`. Two owner-directed additions to the mission layer
that shipped in 0.3.0 (`docs/2026-08-02-shenora-mission-scheduling-design.md`, renamed and split by
its A3). Written to be argued with; retired into `DECISIONS.md` + `ARCHITECTURE.md` once built.

> *"a proper queue design for mission is properly needed, you can use the store as its queue location
> or just rename the store to queue"*
>
> *"a proper context might be needed for a multi step mission (chain mission which the later one
> depends on the one before)"*

---

# Part 1 — The queue

## §1 What exists, and why it is not enough

The pending collection is a **private `LinkedList<Entry>` inside `MissionScheduler`**. Ordering comes
from `IMissionPolicy`, capacity from lanes, and persistence used to come from a seam called
`IMissionStore`. So there was no *queue* in the surface at all: an app could not inspect it beyond
`Snapshot()`, could not supply its own, and could not have the pending set survive a restart except
through that separate store.

The store was the awkward half. It formerly described itself as persisting "durable work", read back
by `RecoverAsync(rehydrate)` — which meant the queue's contents lived in two places with two shapes:
an in-memory list of live entries, and a store of `MissionRecord`s the app must rebuild definitions
from. That split is why recovery needs a rehydrate delegate at all.

## §2 The proposal — the queue is a first-class concept; the store is where it LIVES

The owner offered two forms: *"use the store as its queue location, or just rename the store to
queue"*. A bare rename would make things worse — "store" at least says *persistence*, whereas an
`IMissionQueue` that is only persistence, while the real queue stays a private field, is a name that
lies.

The first draft of this section went the other way and made the whole queue a pluggable async seam.
That is rejected here, by its own cost analysis: **it puts an `await` in the dispatch path.** A
pluggable queue cannot be read under the scheduler's lock, so admission would have to read candidates
first, take the lock, and then RE-VALIDATE everything against a queue that may have changed
underneath — a new class of race, in the one piece of this component where a race is a corruption
rather than a slowdown. The capability it buys (a distributed or app-supplied queue) has no consumer,
and the part apps actually vary — ordering — is already `IMissionPolicy`'s job.

So, taking the owner's first option literally:

```csharp
/// Where the pending queue lives when it must survive a restart. In-memory by default: supply this
/// and the queue is durable. Replaces IMissionStore, which described the same storage as though it
/// were a separate "durable missions" concept alongside the queue rather than the queue's own backing.
public interface IMissionQueueStore
{
    Task AppendAsync(MissionRecord record, CancellationToken cancellationToken);
    Task RemoveAsync(string missionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken cancellationToken);
}
```

- The pending collection stays **internal and synchronous** — the linked list, the lock, and the
  dispatch pass are unchanged, which is why this is a small change rather than a rewrite.
- Durability stops being a parallel notion: a mission is durable because **the queue it went into is
  backed by a store**. `MissionDefinition.Durable` still marks which missions are worth persisting.
- `MissionRecord` is unchanged and keeps `Kind` + `Payload` for the same reason as before (a delegate
  does not serialize). `RecoverAsync(rehydrate)` reads as what it is: the queue had entries when we
  started; turn each back into a definition.

## §3 Cost, honestly

- `IMissionStore` → `IMissionQueueStore`, and `SaveAsync` → `AppendAsync`/`LoadPendingAsync` →
  `LoadAsync`. Another break, in the same pre-1.0 window as A3 and for the same reason: it changes
  `MissionSchedulerOptions`, so later is breaking and now is a changelog entry.
- `MissionState` (`Queued`/`Running`) survives on the record — recovery still has to distinguish
  "never started" from "may have caused the crash" (`RecoveryPolicy`).
- **This is now a genuinely small change**, which is the point of rejecting the async seam. The
  behaviour it must not alter is what the concurrency suite already pins.

---

# Part 2 — Chained missions

## §4 What a chain needs that claims cannot give

The 0.3.0 design says sequential composition is expressible: hold the same claim across steps and
they cannot interleave. True — and insufficient. Claims prevent OVERLAP; they do not express
**order** ("B after A"), **dependency** ("B only if A succeeded"), or **data flow** ("B needs the temp
path A produced").

Today an app writes:

```csharp
var a = await scheduler.SubmitAsync(stepA);          // hold the await, keep state in a local
if (a.Succeeded) await scheduler.SubmitAsync(StepB(a));
```

which works, and loses three things: the chain is invisible to `Snapshot()` (only the current step
exists), it dies with the awaiting code, and a durable queue cannot resume it — the "what comes next"
lives in a stack frame.

## §5 The proposal — a chain is ONE mission whose steps share a context

Per §6.1 this adds no scheduling concept. A chain is a `MissionDefinition` whose body happens to run
steps in order, so the scheduler needs no change at all:

```csharp
public sealed record MissionStep(
    string Name,
    Func<MissionExecution, IMissionChainContext, CancellationToken, Task> Run,
    IReadOnlyList<MissionClaim>? Claims = null,   // folded into the chain's union
    RetryPolicy? Retry = null);                   // retries THIS step

/// Shared, mutable state flowing along ONE chain, in memory only (§6.3).
public interface IMissionChainContext
{
    /// The step now running, 0-based — also what Snapshot() labels the mission with.
    int StepIndex { get; }
    string StepName { get; }
    T? Get<T>(string key);
    void Set<T>(string key, T value);
}

public static class MissionChain
{
    /// Builds a definition that runs `steps` in order, claiming the UNION of their claims up front.
    public static MissionDefinition Sequence(string kind, params MissionStep[] steps);
}
```

`MissionChain.Sequence` is a helper, not a new scheduler input — which is exactly why this is small:
it returns an ordinary `MissionDefinition` that `SubmitAsync` cannot tell apart from any other.

> **Corrected after building it:** §6.1 below claims `Snapshot()` shows "one mission, labelled with the
> step it is on". **It does not, and no attempt was made to make it.** A `MissionExecution` is an
> immutable value produced at submit time; giving it a mutable step label would mean either a mutable
> execution or the scheduler learning what a chain is — and the entire point of one-entry was that the
> scheduler learns nothing. The step is reported through `IMissionChainContext` instead
> (`StepIndex`/`StepName`/`StepCount`), which an app forwards to its own progress surface. Left
> uncorrected, that sentence would have been the exact class of defect `.claude/knowledge/doc-claims.md`
> was written about, in the document that argued for the feature.

**Why a bag and not typed results:** step B needs "the temp path A wrote", and the kit cannot know
that type. A typed chain (`Chain<A, B>` threading a result type) is elegant for two steps and
collapses at three with branching. The bag is what the family's actual sequences need, and it keeps
the kit out of the app's data model — the same reason `Payload` is a string.

**Ordering and claims compose, not conflict.** A chain runs its steps in order, each step admitted
normally: a step still waits for its claims and lane permits, so a chain cannot jump the queue or
bypass exclusion. A chain step and an unrelated mission may still run concurrently — the chain
constrains only itself.

## §6 The fork, and the decision

1. **Is a chain one entry in the queue, or N?** **DECIDED (owner, 2026-08-02): ONE entry.**
   - Rejected — *N entries with dependency edges*: each step would get its own claims and lanes, so
     unrelated steps could interleave for maximum concurrency. But the queue must then model "blocked
     on a predecessor", which is the dependency edge a DAG engine is built from, and §10 declined that
     on the evidence that no sibling has one.
   - Chosen — *one entry, steps sequenced inside it*: no new scheduling concept at all. A chain is a
     mission whose body runs its steps in order. Claims are the UNION of the steps', acquired as a set
     up front, so the deadlock-freedom property is unchanged. `Snapshot()` shows one mission, labelled
     with the step it is on. The cost is real and accepted: a chain holds the union of its claims for
     its whole life, so a five-step chain touching five paths blocks all five throughout.
   - Escalation path, if per-step claims ever matter: that IS design (a), and it wants its own pass.
2. **What does cancelling step 2 of 5 mean?** Cancel the chain. One entry, one token, one cancel —
   "skip to step 3" would need a second verb and a reason to believe an app wants it.
3. **Does the context persist? NO, and the limit is documented rather than papered over.** The
   in-memory bag is for passing a temp path from step 1 to step 2 inside one run. A durable chain that
   resumes after a restart carries its state in `Payload`, like every other durable mission, because
   an arbitrary object graph is exactly what the kit cannot serialize for the app. A resume that
   silently lost the context would be worse than one that never had it.
4. **Retry semantics.** A step's `RetryPolicy` retries THAT step. There is no chain-level retry:
   re-running completed steps is a decision only the app can make, and it can make it by submitting
   the chain again.

## §7 Recommended order of work

1. **Part 1 (queue) first.** It is the smaller change, it is breaking (so it wants the pre-1.0 window),
   and Part 2's durability question is unanswerable until the queue is the thing that persists.
2. **Then Part 2**, and only after §6.1 is decided — one entry or N is not an implementation detail,
   it is the design.
3. The file-update module (`docs/2026-08-02-shenora-file-updates-design.md`) is independent of both
   and can go in any order.

## §8 Deliberately still not built

Unchanged from the mission design's §10, and this plan does not reopen them: a general DAG engine
(a linear chain is not one — §6.1 is where that line gets tested), a handler registry, per-item
cooperative pause.
