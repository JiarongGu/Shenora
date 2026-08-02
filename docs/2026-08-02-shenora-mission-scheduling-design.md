# Shenora mission scheduling + filesystem layer — design

**Status: IMPLEMENTED 2026-08-02, and RENAMED the same day** (`Work*` → `Mission*`, plus the
definition/execution split — A3 below, and `DECISIONS.md` D27). Surface: `src/Shenora.Core/Missions/`
+ `src/Shenora.Core/Io/`.

**This document dates its claims and does not name release versions** (owner, 2026-08-02). Versions
are assigned by the release workflow, so a version written into a design doc is a guess that goes
stale the moment a release cuts — and this file previously said "Target 0.2.0", a release that turned
out never to exist. `CHANGELOG.md` is where work meets version numbers; the one place a doc carries a
current version is `ARCHITECTURE.md`'s status line, which `doctor --fix` syncs.

**Why this document survived the cleanup when its two younger siblings did not.** The plans for the
queue/chains (D28/D29) and file updates (D30/D31) were retired the moment they were built —
`docs/README.md`'s rule: a pre-implementation doc competes with `ARCHITECTURE.md` for "what is true
now" and loses. This one is kept for what only it holds: **§0's evidence** (the same two problems
solved five times across the donor apps, which is what justifies the component existing at all) and
the **amendment history** A1–A3, which records decisions taken DURING the build. The as-built surface
is `ARCHITECTURE.md`; the load-bearing WHYs are `DECISIONS.md` D27–D31. Where this document and those
disagree, they win.

Owner direction: *"a common usecase is filesystem operations +
parallel tasks process… it's not implementing the business features but we need to implement a
library to support, since those 2 major features are complex themselves"*, and — the bar this
document is written against — *"our goal is allow sibling projects to use the library instead of
implementing their own, not just say we did some mirroring of the code, so this needs to be
designed/redesigned properly."*

## §0 Evidence — what the family already built, five times

Surveyed 2026-08-02 across the three donor apps. Every mechanism below clears the kit's
two-consumer bar (`generic-library.md`), and the implementations **differ**, which is what makes
this a merge rather than a port (`extraction-sources.md`: *"merge, don't pick blindly, where two
sources solved the same problem"*).

| Mechanism | Primary desktop sibling | Video sibling | Sonora |
|---|---|---|---|
| FS operation planner | 545 ln — path-overlap dispatcher, event-driven, **parallel on disjoint paths** | 603 ln — **two-plan model** (processing + queued), single worker, batch merge, fully serialized | — |
| Work/job queue | import-queue actor 266 ln + per-key operation queue 132 ln | 463 ln — `Channel` + worker pool, **live-resizable** limit, durable, pause/resume/cancel/retry | 664 ln + repository, checkpoints, handler registry |
| Resource gate | per-key semaphore, ref-counted | 44 ln — one global exclusive lane, idempotent releaser | 177 ln — named **lanes** (gpu/cpu) held/released by policy with hysteresis + debounce |

Two facts shape everything below.

**The two planners diverged, and one is the evolved form.** The primary sibling's was rewritten
(2026-07-11) *from* the single-worker model the video sibling still runs, to stop batch operations
across unrelated resources serializing behind each other. So its concurrency model is the one with
measured justification; the video sibling's contribution is the *merge/dedup of work accumulated
during a slow batch*, which the rewrite kept.

**The kit already owns half of this.** `Shenora.Ipc`'s `OperationRegistry` **is** the siblings'
process registry — Start/Report/Complete/Fail, idempotent finish, notifications to the page. So the
REPORTING half exists and must not be rebuilt. What is missing is the EXECUTION half. This document
is only about execution; the two compose (§7).

## §1 The unifying model — one scheduler, two key kinds

The central claim of this design, and the reason it is not five ports glued together:

> **A filesystem planner and a job queue are the same scheduler with different key types.** A planner
> schedules work keyed by PATH, where two keys conflict if they are equal or one contains the other.
> A job queue schedules work keyed by LANE, where a key admits N holders at once. Everything else —
> submission order, bounded parallelism, event-driven dispatch, dedup, retry, cancellation, progress
> — is identical, and each sibling reimplemented all of it.

So the kit ships ONE engine and two small key strategies, rather than a `FileOperationPlanner` and a
`JobService` that share nothing. This is what makes adoption a deletion for the siblings instead of a
translation.

Two orthogonal concepts, deliberately separated (the siblings conflated them, which is why the video
sibling's GPU gate had to be a static singleton reachable from unrelated features):

- **Claims** — *mutual exclusion between missions*. "I need exclusive use of this path."
- **Lanes** — *capacity-limited pools*. "I need a permit from the `gpu` lane, which admits one."

An item declares both. It runs when its claims are free AND its lane permits are available.

## §2 Claims and claim scopes

```csharp
public enum ClaimMode { Exclusive, Shared }

public readonly record struct MissionClaim(string Scope, string Key, ClaimMode Mode);
```

A **scope** names a key space and supplies its conflict rule:

```csharp
public interface IClaimScope
{
    string Name { get; }
    string Normalize(string key);
    bool Conflicts(string a, string b);   // called on NORMALIZED keys
}
```

The kit ships exactly two, because the siblings between them needed exactly two:

- `FlatClaimScope` — keys conflict iff equal. Entities, categories, lanes-as-locks, job ids.
- `NestedClaimScope` — keys conflict iff equal **or one is a prefix of the other** at a separator
  boundary. This is the primary sibling's path-overlap rule generalized: it is not about
  filesystems, it is about any hierarchical namespace (paths, tree nodes, registry keys, URL
  prefixes). `PathClaims` (§6) is this scope pre-configured for the platform's path rules.

Two `Shared` claims on the same key do not conflict; `Exclusive` conflicts with everything on a
conflicting key. Shared is what makes a reader/writer split expressible — none of the siblings had
it, and all three worked around its absence by over-serializing reads.

**Why a seam and not an enum of built-in rules:** an app with a genuinely different namespace (a
content-addressed store, a database schema) supplies its own scope without the kit growing a case.
Seams over flags (`generic-library.md`).

## §3 Lanes

```csharp
public interface ILane
{
    string Name { get; }
    int Capacity { get; }        // live-resizable
    bool IsHeld { get; }         // externally suspended
}
```

- **Capacity is live-resizable.** Raising it releases permits; lowering it *swallows* permits as
  running work finishes rather than killing in-flight work — the video sibling's rule, and the only
  correct behaviour for a user turning a concurrency slider down mid-run.
- **A lane can be HELD** — suspended without cancelling anything in flight, and released later.
  This is Sonora's capacity governor seam, and it is the difference between "yield the GPU while the
  user games" and "kill the user's transcode". The kit ships the *hold* mechanism and no policy: the
  governor itself (load probes, hysteresis, debounce) is app territory.
- The **default lane** bounds total concurrency. Default `clamp(cores-1, 1, 4)` for IO-bound
  schedulers, which is the value both planners independently arrived at.

A lane with capacity 1 is the video sibling's GPU gate, expressed without a static singleton.

## §4 The scheduler

```csharp
// As SKETCHED here. What shipped differs in two places, and ARCHITECTURE.md has the real surface:
// TryFind(key, out status) became the simpler IsActive(key) — no consumer wanted the status — and
// the parameter is a MissionDefinition, since a definition and an execution are now distinct (D27).
public interface IMissionScheduler : IAsyncDisposable
{
    Task<MissionResult> SubmitAsync(MissionDefinition definition, CancellationToken ct = default);
    bool IsActive(MissionKey key);              // sketched as TryFind, the video sibling's HasPendingOperation
    int PendingCount { get; }
    ILane Lane(string name);
}
```

**Admission rule** — an item may start when all hold:
1. No **in-flight** item holds a conflicting claim.
2. No **earlier pending** item holds a conflicting claim — per-resource FIFO. This is a fairness
   rule, not a safety one, and it is why the primary sibling's dispatcher cannot starve a queued
   item behind a stream of newer disjoint work.
3. A permit is available in every lane the item names.

**Dispatch is event-driven** — evaluated on submit and on each completion. No polling worker; both
sibling planners that used one paid for it in latency or in a dedicated thread.

**Work never runs under the scheduler lock.** The lock covers bookkeeping only; the body is started
on the thread pool. This is stated because it is the single easiest thing to get wrong here, and
both planners carry a comment about it.

**Deduplication** is by an app-supplied `MissionKey`: an identical request already pending or in flight
completes eagerly against the existing one instead of queueing a second. Both planners have this;
the video sibling additionally *merged* redundant operations accumulated during a slow batch, which
this expresses as dedup against the pending set.

**Retry** is a policy on the request, not a hardcoded loop:
`RetryPolicy(int Attempts, TimeSpan Delay, Func<Exception,bool> IsTransient)`. Defaults match the
family's measured value (3 attempts, 500 ms × attempt) for transient IO locks.

> **The two-phase rule, earned and worth carrying:** the primary sibling learned that retrying a
> whole expensive operation is wrong — a compress that fails to *replace* a locked target must not
> recompress. So a request may be split into `PrepareAsync` (runs ONCE, does the expensive work into
> a temp location) and `CommitAsync` (cheap, retried under the policy). The kit models this because
> the lesson does not survive being left as advice.

## §5 Durability — a seam, no implementation

Owner direction: *"durability we must have, but configurable (say where the state saves to or
persists to — we don't handle persistence for now)."*

```csharp
// Sketched here as IMissionStore with SaveAsync/LoadPendingAsync; shipped, and later renamed to say
// what it actually is — the queue's storage rather than a durability service beside it (A3, Part 1 of
// DECISIONS.md D28/D29).
public interface IMissionQueueStore
{
    Task AppendAsync(MissionRecord record, CancellationToken ct);
    Task RemoveAsync(string missionId, CancellationToken ct);
    Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken ct);
}
```

- The kit ships **no** persistent implementation — no SQLite, no JSON file. An app supplies one.
  This keeps `Shenora.Core` free of a storage dependency and matches the direction above.
- Durability is **per request** (`MissionDefinition.Durable`), not global: a scheduler may mix cheap
  in-memory work with durable work.
- `RecoverAsync()` is an explicit startup call, never implicit — the app decides when recovery is
  safe relative to its own initialization.
- **Recovery policy per mission type**, because of a genuinely earned lesson from the video sibling:
  work found in the RUNNING state after a crash *may have caused the crash*, and blindly re-running
  it produces an unrecoverable boot loop. So:
  `RecoveryPolicy { Requeue, Fail, Discard }`, defaulting to **`Fail`** for RUNNING records and
  `Requeue` for QUEUED ones. The safe default is the one that cannot loop.

## §6 The filesystem layer

With scheduling factored out, what remains genuinely filesystem-specific is small — which is the
point, and is why this is not a port of two 500-line planners.

- **`IFileSystem`** — the seam all three siblings needed and two of them added late. Its real value
  is not abstraction, it is that an in-memory implementation can inject latency and transient
  `IOException`s and *assert the concurrency invariants* (§8).
- **`PathClaims`** — `NestedClaimScope` configured for platform path semantics (case rules,
  separator normalization, trailing separators). Turns "these ops touch overlapping paths" into a
  claim set.
- **Atomic replace** — write to temp, then replace; the commit half of the two-phase rule (§4).
- **Path containment** — `IsContained(root, candidate)` after full normalization. This is on the
  review guide's list of latent-defect classes ("path/containment checks on anything that maps a
  request to a file"), the kit already needs it in the resource providers, and every app that maps
  user input to a path needs it. It belongs here once.

The kit does **not** ship archive handling, downloads, or cleanup scans. Those are the *business*
side of the siblings' planners and stay in the apps.

## §7 Composition — actors, workflows, and what the kit refuses to invent

Owner raised the actor pattern and workflow logic. Assessed honestly:

- **An actor falls out of the model already.** The primary sibling's import-queue actor is "work
  submitted with an `Exclusive` claim on one key" — a serialized mailbox, in one line, with no new
  type. Shipping a separate `Actor` abstraction would add surface that the claim model already
  covers, so the kit ships a **documented pattern**, not a class. (`generic-library.md`: every
  public type earns its keep.)
- **Sequential composition** — chaining steps that must not interleave — is likewise the same
  claim held across steps. Expressible.
- **A general workflow/DAG engine is NOT proposed.** No sibling has one; there is no evidence, and
  the two-consumer bar exists precisely to stop this kind of speculative build. Recorded as
  deliberately-not-built so the next session does not re-argue it.
- **Progress reporting composes, it does not merge.** `Shenora.Ipc`'s `OperationRegistry` stays the
  reporting owner; a mission body reports into it. Core must not learn about operations — Ipc → Core is
  the legal direction (D19/D20), never the reverse.

## §8 Adoption proof — can each sibling actually delete its code?

This is the section the design lives or dies on. *Mirroring* the siblings is the failure mode the
owner named; the test is whether each existing implementation is expressible **without loss**.

| Existing behaviour | Expressed as | Lost? |
|---|---|---|
| Path-overlap serialization (equal/ancestor across source+target+temp) | `PathClaims` + one `Exclusive` claim per touched path | no |
| Disjoint paths run parallel under a cap | default lane, capacity `clamp(cores-1,1,4)` | no |
| Per-resource FIFO / no starvation | admission rule 2 | no |
| Dedup identical pending op → eager Ok | `MissionKey` dedup | no |
| Retry 3 × 500 ms on transient lock | `RetryPolicy` default | no |
| Compress once, retry only the replace | `PrepareAsync`/`CommitAsync` | no |
| Per-entity mutex, ref-counted cleanup | `FlatClaimScope` + `Exclusive`; **the ref-count race disappears** — the scheduler owns claim lifetime, so there is no per-key semaphore to remove | improved |
| Category lock + documented lock ORDER | two claims on one request, acquired as a set — **deadlock becomes structurally impossible**, so the lock-order rule stops being a rule anyone must remember | improved, **proven** |
| Global GPU exclusivity | lane `gpu`, capacity 1 | no — and no static singleton |
| Live max-active slider | `Lane.Capacity` setter, permit-swallowing on decrease | no, **proven** |
| Externally hold a lane under load | `Lane.Hold()/Release()`; probes+hysteresis stay in the app | no |
| Durable jobs, resume on start | `IMissionQueueStore` + `RecoverAsync` | no — app owns the store |
| No-auto-resume for crash-prone types | `RecoveryPolicy.Fail` (the default for RUNNING) | no |
| Pause / cancel / retry a job | cancellation token per item; pause = lane hold or claim release | partial — see below |
| Handler registry by job type | app-side; the scheduler takes a delegate | **out of scope, deliberately** |
| Two-plan batch merge | dedup against the pending set | behaviourally, yes |

**The two rows marked "proven" were the gap in this table.** Every other row is either a
straightforward mapping or already covered by the concurrency suite, but those two claim a whole
class of bug is now IMPOSSIBLE — the strongest kind of claim here, and the two that were asserted
with nothing behind them. `MissionSchedulerAdoptionTests` now drives crossing two-claim pairs under a
timeout (a deadlock manifests as a hang, so the assertion must be a timeout, not a result check) and
lowers a lane's capacity mid-flight to show in-flight work survives AND that the new limit really
binds once the surplus drains. The capacity pair is sabotage-verified; the deadlock test is a
regression guard that cannot be meaningfully sabotaged without reintroducing locks — said plainly
rather than counted as proof it does not provide.

Two honest gaps, stated rather than hidden:

1. **Pause/resume of an individual in-flight item** is weaker than the video sibling's, which
   cooperatively pauses a specific job. Lane hold is coarser (it suspends a lane, not an item). If
   the adopter needs per-item pause, that is the first extension — and it should be built when they
   ask, not guessed at now.
2. **Handler-registry-by-type** is app composition, not scheduling. Both job services have one; both
   are ~30 lines of dictionary. Shipping it would pull job *modelling* into the kit.

## §9 Placement, naming, verification

- **Package: `Shenora.Core`.** Both halves are portable, and the Core bar — *"app logic must compile
  off Windows"* — is met and mechanically enforced by the `samples/Shenora.Sample.Logic` `net10.0`
  tripwire. **No new package** (D2).
- **Naming:** `Work*` throughout, deliberately NOT `Operation*` — `Shenora.Ipc` already owns
  `IOperation`/`OperationRegistry` for *reporting*, and reusing the word would blur the one
  distinction this design depends on. Every new type name must pass `SurfaceVocabularyTests`; new
  lexicon words (Work, Claim, Lane, Scope, Permit, Retry, Recovery, Store, Nested) are shell/
  concurrency vocabulary, not domain nouns.
- **Verification is the deliverable, not an afterthought.** The primary sibling's concurrency tests
  are the model: an in-memory filesystem that injects latency and transient failures and records
  **`MaxConcurrentSamePath` (must stay 1)** alongside **`MaxConcurrentTotal` (must exceed 1)**. A
  scheduler that claims parallelism and exclusion must *prove both in the same run* — asserting only
  that results are correct would pass a fully serial implementation.

## §10 Deliberately not built

Recorded so the next session does not re-argue them: a workflow/DAG engine (§7); a persistent
`IMissionQueueStore` implementation (§5, owner direction); handler-registry-by-type (§8); per-item
cooperative pause (§8); any archive, download or cleanup helper (§6).

## Amendments

### A1 — the app supplies the scheduling logic (2026-08-02, during implementation)

Owner: *"we do need to allow for those applications to supply logic how this schedules — what to
pick up, when to pick up, how to pick up."* The design as first written hardcoded priority-then-FIFO,
which is the mistake that gets a kit forked: ordering is a PRODUCT decision. "User-initiated before
background", "smallest first", "nothing heavy before 9am", "pause on battery" are all legitimate and
mutually exclusive.

So `IMissionPolicy` now owns **what** (`Compare`) and **when** (`ShouldStart`), with
`PriorityMissionPolicy` as the default that reproduces the original behaviour.

**The safety boundary is the part that makes this safe to expose, and it is structural.** A policy is
consulted ONLY about items that have already passed admission — claims free, lane permits available,
fairness satisfied. It chooses among legal moves. It cannot make conflicting work run concurrently,
cannot bypass a lane, and cannot reorder work that conflicts with an earlier item. The worst a buggy
policy can do is DELAY work; it can never corrupt it. A throwing policy is caught and treated as
"not now" rather than wedging the scheduler (tested).

Consequence: a policy that defers on an EXTERNAL condition (clock, load, battery) needs a nudge when
that condition changes, because dispatch is event-driven. Hence `IMissionScheduler.Reevaluate()`. The
kit deliberately owns no timer — polling belongs to whoever knows what is being polled.

### A2 — designed for requirements that do not exist yet (2026-08-02, during implementation)

Owner: *"you have to always think bigger than we currently have, so this needs to be durable for the
long term — a new application with a new requirement should also fit."* The design was audited for
changes that would be BREAKING to make later, as opposed to merely additive, and two were found and
fixed before anything shipped:

- **Weighted lane permits.** `MissionDefinition.Lanes` was `IReadOnlyList<string>`, one permit each. A lane
  is often a BUDGET (memory, VRAM, bandwidth) where items cost different amounts. Adding a cost later
  changes the property's type and breaks every caller, so `MissionLane(string Name, int Permits = 1)` is
  there from the start. Cost of carrying it: one defaulted parameter.
- **Priority.** Adding an ordering input to a strictly-FIFO scheduler changes admission semantics for
  existing callers. Present from the start, defaulted to 0 — which IS plain FIFO.

Two further extension points were added for fit rather than for a current consumer:
`IMissionObserver` (metrics, tracing, and the seam by which `Shenora.Ipc`'s operation registry attaches
without `Shenora.Core` learning about it) and `Snapshot()` (a queue view; both sibling job services
have one).

**What was NOT added on this reasoning:** a DAG engine, a handler registry, per-item pause. "It might
be needed" is not evidence, and §10 stands. The line drawn here is narrow and deliberate — pay the
cost now only where the later change would be BREAKING rather than additive.

### A3 — `Work*` becomes `Mission*`, and the unit splits in two (2026-08-02, after 0.3.0)

Two owner decisions, applied together because they touch the same signatures.

**The rename.** `Work` is too common a word to own or to grep for; `Task` was the obvious alternative
and collides with `System.Threading.Tasks` — `TaskScheduler` would be ambiguous against the BCL type
in every consumer importing both namespaces. `Quest` reads as domain vocabulary in a games family,
which is what `SurfaceVocabularyTests` exists to keep out of `src/`; `Expedition` puts ten characters
in front of fifteen types. `Mission` is unique here, mechanism vocabulary, and short. The full mapping
is in `CHANGELOG.md`; the old names are in `devtools/retired-names.txt` so prose cannot quietly state
one as current again.

**The split, and the reasoning that flipped it.** The owner proposed a fuller structure —
definition / schedule / execution / result, with typed handlers, a queue and a runner. Most of it was
declined on the evidence already recorded here (§7, §8, §10): a handler registry is app composition
and would make the kit own serialization of app types; a separate queue and runner rebuild the
two-component shape §1 exists to collapse; an `IMission` interface pushes toward class-per-mission.
`MissionStatus` beside `MissionState`, and `MissionOptions` beside `MissionSchedulerOptions`, are
second words for one axis.

But the **definition/execution** half was accepted, and the argument for it is A2's own test rather
than "a consumer asked": introducing it later would be BREAKING — it changes `SubmitAsync`, every
mission body's parameter, all three `IMissionObserver` methods and both `IMissionPolicy` methods at
once — while doing it now costs a changelog entry. The owner's steer was the corrective: *"bigger
change does not mean a bad thing, we need to think forward for future, change is allowed, this is
still pre-1.0."* Holding a breaking-later change behind a two-consumer bar meant for CAPABILITY was a
misapplication of the bar.

It also removes a type rather than adding one. `MissionContext` (what the body got),
`MissionView` (what the policy got) and `MissionSnapshot` (what a diagnostics view got) were three
shapes of one thing; they are now `MissionExecution`, carrying `Attempt` and `IsRunning` so a running
execution reports both. It carries no `CancellationToken` — the body takes one as a second parameter,
matching every other callback seam in the kit and keeping an execution a pure value.

**What this unlocks additively**, and is therefore NOT built yet: a `MissionSchedule` — a time or
recurrence trigger that submits a definition repeatedly, one definition to many executions. That is a
real capability gap (the kit owns no timer at all, by A1), but adding it changes no existing signature
now that the split exists, so it waits for a consumer rather than being guessed at.
