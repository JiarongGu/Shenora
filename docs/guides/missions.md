# The mission scheduler

> **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never
> restated** — that is the rule D57 was written to keep (five design docs were retired precisely
> because a third copy of the reasoning goes stale while nobody notices).
> Migrating an existing app? Start at [ADOPTION.md](../ADOPTION.md).

## The mission scheduler — not a stage; adoptable on its own

`Shenora` ships ONE scheduler for the two things the family built five separate times: a
**filesystem operation planner** (serialize work that touches overlapping paths, run disjoint work in
parallel) and a **job queue** (bounded concurrency, retry, cancel, durability). They are the same
engine with different key types — paths conflict when one CONTAINS the other, lanes admit N holders at
once — and putting only that difference behind a seam is what makes adoption a DELETION rather than a
translation. Evidence, rationale and the deliberately-not-built list: `docs/DECISIONS.md` D27–D31 + D57.

**It needs nothing else from the kit.** `IMissionScheduler` is in `Shenora`: no shell, no IPC, no
Windows, and not even the host builder — `new MissionScheduler(options)`, registered as a singleton in
whatever container you already use. Nothing above is a prerequisite.

**The bugs it deletes.** Every one of these was live in a hand-rolled queue or planner in this family,
and none of them is exotic — they are what this problem costs when each app solves it alone:

- A ref-counted per-key semaphore, removed at zero holders, where a check-then-remove race handed two
  callers *different* semaphores for the same key — so the resource that looked serialized was not.
  There is no per-key lock object here; the scheduler owns claim lifetime, so the race has nowhere to
  live.
- A documented lock ORDER between two key spaces (entity, then category) that every call site had to
  remember. A request declares its whole claim SET and is admitted only when all of it is free, so
  there is no acquisition order to get wrong.
- Path overlap tested with a naive `StartsWith`, which makes `a/bc` a child of `a/b`: two unrelated
  resources then serialize against each other forever, and the symptom reads as "the queue is slow".
  Containment is tested at a separator boundary.
- Two spellings of one location (`data\mods\..\mods\x` and `data/mods/x`) treated as different keys,
  so two mutations ran on one directory at once. Claims are normalized once, at submit.
- A compress-then-replace that retried the WHOLE operation when only the replace hit a locked target —
  seconds of recompression, up to three times, to redo a file move that takes microseconds.
- Work found RUNNING after a crash and re-run on every boot, turning one crash into a loop the user
  cannot escape from inside the app.

### Setup

```csharp
var scheduler = new MissionScheduler(new MissionSchedulerOptions
{
    // ⚠ A CEILING over every lane, not just their starting value — see "Known gaps" #5 before choosing
    // it. Omit it (or null) for auto = clamp(cores-1, 1, 4), the value both hand-rolled planners chose;
    // anything below 1 throws.
    GlobalLaneCapacity = null,
    Scopes = [PathClaims.Scope, new FlatClaimScope("entity"), new FlatClaimScope("category")],
    Log = message => logger.LogDebug("{Message}", message),
});
scheduler.Lane("gpu").Capacity = 1;   // a scarce shared resource — see "Known gaps" #5 (the lane trap)
```

Register only the scopes you use. A claim naming an **unregistered scope throws at submit** rather
than being ignored — silently dropping an exclusion the caller asked for is the one failure mode a
scheduler must not have. Pass an explicit `GlobalLaneCapacity` in your own tests: a concurrency
assertion keyed off the host's core count passes or fails by machine, which is how a parallelism
regression hides on the one box with two cores.

### What replaces what

| You probably hand-rolled | Use | Notes |
|---|---|---|
| A planner that serializes operations touching the same file or directory | `PathClaims.Scope` + one `PathClaims.Exclusive(path)` per path an operation MUTATES (source, target and temp) | Hierarchical: `C:\a` conflicts with `C:\a\b`, because deleting a directory must not run while something writes inside it. |
| Reads serialized behind writes they did not actually conflict with | `PathClaims.Shared(path)` | No hand-rolled planner in the family could express a reader/writer split, so all of them over-serialized. Several shared holders run together; an exclusive one waits. |
| A per-entity mutex or per-key semaphore dictionary | `MissionClaim.Exclusive("entity", id)` over a `FlatClaimScope` | Flat keys conflict only when equal. |
| A second lock for a coarser key, plus a lock-order rule | both claims on ONE request — `Claims = [MissionClaim.Exclusive("entity", id), MissionClaim.Exclusive("category", group)]` | Acquired as a set, so deadlock is structurally impossible and the lock-order rule stops being something anyone must remember. Guarded by `MissionSchedulerAdoptionTests.Claims_acquired_as_a_set_cannot_deadlock_on_lock_order`, which drives crossing pairs under a timeout (a deadlock shows up as a hang, so the assertion has to be the timeout). |
| A mailbox actor that serializes one stream of items | one `Exclusive` claim on a single key | The actor falls out of the model; the kit ships no `Actor` type on purpose. |
| A `maxConcurrency` constructor argument | `MissionSchedulerOptions.GlobalLaneCapacity` | Every request draws one permit from the global lane. ⚠ It is also a CEILING over every named lane — see "Known gaps" #5. (Renamed from `DefaultLaneCapacity`; rename the assignment, nothing else changes.) |
| A static gate/semaphore singleton over a scarce resource (one GPU, a rate-limited endpoint) | `scheduler.Lane("gpu").Capacity = 1` + `Lanes = [new MissionLane("gpu")]` on the request | Removes the singleton, so it is testable and there can be more than one. A lane that is a BUDGET rather than a slot count takes weighted permits: `new MissionLane("vram", 4)`. |
| A live "max active" slider | the `ILane.Capacity` setter | Lowering it never cancels running work — the surplus is swallowed as items finish. Proven in both directions: `Lowering_lane_capacity_throttles_new_work_without_killing_running_work` and `A_lowered_capacity_is_enforced_once_the_surplus_drains`. The setter enforces a floor of 1 and no ceiling, so clamp to your own maximum before assigning. |
| A hand-written IPC route that opens a file/folder/save dialog for the page | `services.AddShenoraFileDialogs()` + `useFileDialogs()` from `@shenora/react` | The kit ships the routes and the typed client. `canPickFile`/`canPickFolder`/`canPickSavePath` come from the ready handshake, so ONE bundle hides the controls a shell cannot honour rather than calling and catching. Keep your own route only when you have logic AROUND the dialog (a slow interruptible write, app validation) — not for a plain picker. |
| A capacity governor that suspends work under system load | `ILane.Hold()` / `Release()` (re-entrant), and `IMissionScheduler.GlobalLane` to move the total bound | The kit ships the mechanism and no policy: load probes, hysteresis and debounce stay yours. This is the difference between "yield the GPU while the user games" and "kill the user's transcode". ⚠ A governor that RESTORES as well as throttles must raise `GlobalLane.Capacity` too — see "Known gaps" #5. |
| Dedup of an identical pending operation; a batch merge of work accumulated during a slow plan | `MissionDefinition.Key` (+ `IsActive(key)` so you can skip building an expensive request you know would only be deduplicated) | A matching submission completes eagerly against the existing item with `MissionOutcome.Deduplicated`, and the body runs once. |
| `MAX_RETRY_ATTEMPTS` / `RETRY_DELAY_MS` constants | `RetryPolicy` | Same defaults as the family's measured value: 3 attempts, 500 ms × attempt, `IOException` only. `RetryPolicy.None` opts out; `Retry = null` already means none. |
| A retry loop wrapped around an expensive operation to survive a cheap final step | `Run` (the expensive phase, runs ONCE) + `Commit` (cheap, retried) | Setting `Commit` is what makes `Run` exempt from the retry budget. |
| A `Channel` + worker pool + gate, or a plan-swap with a signal and a worker task | the scheduler | Dispatch is event-driven — on submit and on each completion. No worker thread, no polling latency. |
| Priority or "not now" rules baked into the queue's own loop | `IMissionPolicy` (`Compare` = what, `ShouldStart` = when); default `PriorityMissionPolicy` is priority-then-FIFO | Ordering is a PRODUCT decision, so it is yours. A policy is only consulted about items that already passed admission, so the worst a buggy one can do is DELAY work — it cannot make conflicting work overlap or bypass a lane. A throwing policy is treated as "not now" rather than wedging the scheduler. |
| `GetPendingOperationCount()`, a queue/diagnostics view | `PendingCount`, `RunningCount`, `Snapshot()` | `Snapshot()` is a copy: safe to hold, stale the moment it returns. |
| Durable jobs in SQLite (or JSON) + resume on startup | `IMissionQueueStore` over your EXISTING repository, `Durable = true` per request, then `RecoverAsync(rehydrate)` at a moment you choose | The kit ships no store implementation, by design — see below. `Kind` and `Payload` are yours, never interpreted. |
| A "do not auto-resume this crash-prone type" flag | `RecoveryPolicyFor` → `RecoveryPolicy.Fail` | Already the default for records found `Running`; `Queued` records requeue. The safe default is the one that cannot loop. |
| Opening and closing a progress operation by hand in every mission body | `IMissionObserver` — see below | Every call is guarded, so an observer that throws cannot fail the work it was only watching. |
| A `candidate.StartsWith(root)` guard on anything that turns caller input into a path | `PathClaims.IsContained(root, candidate)` | Not scheduling, but it belongs to the same file: it resolves `..` and `.` FIRST and tests at a separator boundary, so neither an escaping segment nor `C:\data-old` passes as being inside `C:\data`. |

**Definition and execution are separate types, and the distinction is worth ten seconds up front.** A
`MissionDefinition` is WHAT should run — body, claims, lanes, retry, dedup key. A `MissionExecution` is
ONE specific run of it: id, attempt, position in the queue, whether it is running. You construct
definitions; the scheduler hands you executions (to the body, to observers, to a policy, and out of
`Snapshot()`). Today one submit produces one execution, so the split buys you consistent vocabulary
rather than new power — it is there because a recurring or re-hydrated mission is one definition with
many executions, and introducing that later would change every one of those signatures at once.

The two-phase shape, which is what the `Run`/`Commit` split was designed from:

```csharp
var result = await scheduler.SubmitAsync(new MissionDefinition
{
    Claims = [PathClaims.Exclusive(cachePath), PathClaims.Exclusive(archivePath)],
    Run    = (_, ct) => archive.CompressToTempAsync(cachePath, tempPath, ct),   // expensive, ONCE
    Commit = (_, ct) => files.ReplaceAsync(tempPath, archivePath, ct),          // cheap, retried
    Retry  = new RetryPolicy(),
    Key    = new MissionKey($"compress:{entityId}"),
});
if (!result.Succeeded)
    logger.LogWarning(result.Error, "compress failed after {Attempts} attempt(s)", result.Attempts);
```

> ⚠ **A failing body does not throw out of `SubmitAsync`** — the failure comes back as
> `MissionResult.Outcome`, because a submitter is usually a batch loop that must survive one bad item.
> Check `Succeeded`/`Outcome`, or call `ThrowIfFailed()` if you prefer exceptions. Caller bugs
> (unregistered claim scope, disposed scheduler) still throw at submit; those are not outcomes of the
> work. If you port a call site that assumed "it threw, so it failed", it will now look like it
> succeeded.

> ⚠ **A lane is created on first mention, at the default capacity.** A misspelled lane name therefore
> does NOT throw — it silently gives you a second lane whose capacity is not the one you configured,
> and the exclusivity you thought you had is gone. Set lane capacities once at startup and keep the
> names in constants.

> ⚠ **The parallelism change is the real risk in this adoption, and nothing will tell you.** If your
> current planner runs one operation at a time (a single worker, a global gate), disjoint work starts
> overlapping the moment you switch. That is the upgrade — it is why the newer of the family's two
> planners was rewritten — but anything that quietly depended on the old accidental global ordering
> breaks silently. Find those call sites before you move the second batch of operations across, not
> after.

> ⚠ **A scheduler only protects what goes through it.** If you keep an audit of which call sites route
> through your old planner, keep it: adopting the kit does not make an unrouted `Directory.Delete`
> safe. The rule becomes "never mutate a managed resource outside a scheduled mission".

> ⚠ **A policy that defers on an EXTERNAL condition needs a nudge.** Dispatch happens on submit and on
> completion, so a clock, load or battery rule must call `Reevaluate()` when its condition changes or
> the deferred item waits for unrelated traffic to wake it. The kit owns no timer: polling belongs to
> whoever knows what is being polled.

### Progress reporting composes — it is not merged in

The scheduler is the EXECUTION half of long-running work; `Shenora.Core.Ipc`'s request tracking
is the REPORTING half, and they stay separate because the engines may depend on the cores and never
the reverse (D19/D20). ⚠ The two halves are LAYERS inside `Shenora`, not separate packages, which
changes where the rule is enforced but not the rule. `IMissionObserver` is the seam: `OnQueued`/`OnStarted`/`OnFinished` for every
item, each call guarded so a throwing observer cannot fail the work it was only watching.

**The adapter is yours to write. It is about 35 lines** — measured, not estimated: the kit's own
sample carries one (`samples/Shenora.Sample.Logic/MissionEventPublisher.cs`). Copy it. No mission body
reports progress by hand again, which is the boilerplate the family's apps repeated at every call site
and occasionally forgot. The same seam is where metrics and tracing attach.
> ⚠ **A mission is NOT a request, and that is why this is an adapter rather than a kit feature** (D66).
> Host-initiated work — a scheduled or recovered mission — has no request behind it and nobody waiting on
> a reply, so it reports on its OWN event stream. Squeezing it into a request-shaped hole is what gave the
> old design two unrelated things in one bucket.

Two things that adapter learned the moment it ran, both of which you will hit:

- **Publish mission state as EVENTS, not as requests.** A mission is host-initiated: nobody sent a
  request for it, so it has no request id, no response to await and nothing for the page to abort.
  `samples/Shenora.Sample.Logic/MissionEventPublisher.cs` emits one `MISSION_UPDATED` event per
  transition (`queued` / `running` / `completed` / `cancelled` / `failed`) and the page folds it with
  `useShenoraEvent`. Queue depth belongs to the scheduler, which is where the answer actually lives.
  ⚠ This replaced an adapter that reported missions as tracked "operations" — it was the only code
  anywhere that needed a parked state, which is exactly why that state existed and why removing it was
  safe (D66).
- **Cancellation stays yours.** The scheduler cancels through the token you passed to `SubmitAsync`.
  The kit deliberately does not guess a link between that lifetime and anything on the page: to offer a
  real cancel, keep your own `CancellationTokenSource` per submission and expose your own route.


**Both halves are portable.** In the sample, the scheduler, the observer and the facade that submits
all live in the `net10.0` project that cannot reference Windows — so this composition is one of the
things that tripwire keeps honest (`samples/Shenora.Sample.Logic` turns RED if a Windows type creeps in).

### What the kit does not ship here

- **No filesystem abstraction and no atomic-replace helper.** If you have an `IFileSystem` plus an
  in-memory implementation, keep it — it is the most valuable thing in that area, because an in-memory
  filesystem that injects latency and transient `IOException`s is how the concurrency invariants become
  provable in YOUR app. The write-to-temp-then-replace *shape* is what the kit models (`Run`/`Commit`);
  the replace itself is your `Commit` body. `PathClaims` is the only filesystem type here.
- **No archive, download or cleanup helpers.** Carve depth caps, leaked-handle retries, an
  extract-never-execute rule: business logic, and it stays yours.
- **No persistent `IMissionQueueStore`, no handler registry by job type, no DAG/workflow engine, no per-item
  cooperative pause.** Each is deliberate, with reasons, in §10 of the design doc.

### Order to adopt

1. Add the scheduler alongside the existing queue and route ONE low-risk operation through it.
2. Move the rest of the raw-filesystem operations; delete the old planner.
3. Move the entity/category locks; delete the operation queue — the lock-order rule disappears here.
4. Lanes for scarce resources; delete the gate singleton.
5. Durability last: implement `IMissionQueueStore` over your existing storage, wire `RecoverAsync`.

Steps 1, 3 and 4 are behaviour-preserving. **Step 2 is where the parallelism change lands**, so that
is the one to verify against real workloads rather than only against tests.

### Known gaps — worth knowing BEFORE you start

1. **Per-item cooperative pause is weaker than a hand-rolled one.** The kit offers lane hold (coarser
   — it suspends a lane, not an item) or cancel-and-resubmit. If you need to pause one specific
   in-flight item, say so: that is the first extension to build, and it should be built on your
   evidence rather than guessed at now.
2. **No handler-registry-by-type.** Deliberate: the `rehydrate` delegate already needs your
   record→body mapping, so the kit would be duplicating your composition.
3. **No persistent store.** Storage is the app's decision, and `Shenora` takes no storage
   dependency.
4. **Content URIs are not paths.** `PathClaims` assumes a hierarchical filesystem with a platform
   separator — right for app-private storage, wrong for an Android MediaStore/SAF content URI, which
   needs its own `IClaimScope`. Nothing else in the scheduler cares.
5. **⚠ `GlobalLaneCapacity` is a CEILING over every lane, not just their starting value** — the lane
   trap, and it cost the first adopter a measurement (2026-08-05). Every mission also draws one permit
   from the global lane, so a named lane runs at `min(its own capacity, the global bound)`. Set
   `GlobalLaneCapacity` to the widest any lane will ever need and narrow from there — a global bound of
   1 with `Lane("gpu").Capacity = 3` gives you a lane that runs at **1**.
   - **Read `ILane.EffectiveCapacity`, not `Capacity`,** to find out what a lane will actually reach.
     `Capacity` deliberately reports what you REQUESTED (so a later widening of the bound gives you the
     width you asked for rather than having discarded it), and setting it above the bound logs why it
     will not apply.
   - **A runtime governor must move the bound too.** `IMissionScheduler.GlobalLane` is that lane, and it
     is live-resizable like any other: `scheduler.GlobalLane.Capacity = 8`. Before it existed, a
     governor could throttle a lane and never restore it past the value chosen at startup. Its
     `Hold()`/`Release()` also give you "pause the whole scheduler without cancelling anything".

**Verify:** one run must prove exclusion AND parallelism together — work on the same key never
overlaps *while* disjoint work does. Asserting only that results are correct passes a fully serial
implementation, which is the trap, and capacity alone can produce either half. The kit's own
`Parallel_and_serialized_hold_in_the_SAME_run` submits a contended key and disjoint keys in one mixed
workload and asserts peak concurrency twice — 1 for the contended key, more than 1 overall — and yours
should be shaped the same way. Then lower a lane's capacity mid-run and confirm in-flight work
survives while new work throttles.

### Multi-step missions, when a later step needs what an earlier one produced

Claims stop two missions overlapping. They say nothing about ORDER, dependency, or data flow — so
"stage it, then commit it, then index it" has nowhere to live except a stack frame:

```csharp
var a = await scheduler.SubmitAsync(stage);            // hold the await, keep state in a local
if (a.Succeeded) await scheduler.SubmitAsync(Commit(a));
```

That works, and loses three things: the chain is invisible, it dies with the awaiting code, and it
cannot be resumed. `MissionChain.Sequence` gives you the same sequence as one mission:

```csharp
var chain = MissionChain.Sequence("IMPORT",
    new MissionStep("stage",  (m, ctx, ct) => { ctx.Set("temp", tempPath); return Stage(ct); },
                    Claims: [PathClaims.Exclusive(source)]),
    new MissionStep("commit", (m, ctx, ct) => Commit(ctx.Get<string>("temp")!, ct),
                    Claims: [PathClaims.Exclusive(target)],
                    Retry:  new RetryPolicy()));        // retries THIS step, never the ones before

await scheduler.SubmitAsync(chain);                     // an ordinary mission, as far as the scheduler knows
```

What to know before you reach for it:

- **A chain is ONE queue entry**, so it holds the UNION of its steps' claims for its whole life —
  taking the stronger mode where steps disagree, so a read-then-write chain holds that key
  exclusively throughout. For a long chain over many paths that is a real throughput cost, and it is
  the trade for the scheduler having no dependency graph. If it bites, that is the evidence for
  per-step claims and it wants its own design.
- **`IMissionChainContext` is in-memory only.** It exists to pass a temp path from step 1 to step 2
  inside one run. A DURABLE chain that resumes after a restart carries its state in `Payload`, like
  any other durable mission — the kit cannot serialize your object graph, and a resume that silently
  lost the context would be worse than one that never had it.
- A failing step fails the chain and later steps do not run. Cancelling cancels the chain. There is no
  chain-level retry: re-running completed steps is a judgement only you can make, and you make it by
  submitting again.

---
