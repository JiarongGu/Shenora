# Shenora file updates — design

**Status: THE QUEUE IS IMPLEMENTED 2026-08-02** (`IFileUpdateQueue`/`FileUpdateQueue`, `FileUpdate`,
`FileChange`, `FileAtomicity`, `FileUpdateResult` — 8 tests). **§4's cross-process LEASES are NOT
built**: they are additive, and open question 3 — does anything need them today, or is
single-instance the practical guarantee — is still unanswered. Nothing else here is pending.

Owner direction, verbatim, which is the whole brief:

> *"file system change isolation can be done by a filesystem update class (module) which allow for
> mission still able to run parallel but wait for file system completion one by one if needed"*
>
> *"we should support a Locking logic instead just say no"*
>
> *"it's more a different design rather than put them all into mission management"*

## §0 Why this is a separate module

`Shenora.Core`'s mission scheduler decides **which missions may run**. This decides **how a set of
file mutations lands**. Folding the second into the first grows the scheduler into the two-component
shape §1 of the mission design exists to collapse, and the failure modes do not even overlap: a
scheduler's are starvation and deadlock, a file applier's are partial writes, locked targets and
crash-time torn state.

They compose without knowing about each other, and either is usable alone:

```
mission scheduler   →  which missions run, and in parallel with what   (claims, lanes, policy)
file update queue   →  how their mutations land, and in what order     (serialization, atomicity)
```

## §1 The model — parallel compute, serialized apply

The insight in the owner's direction: **claims are too coarse for the compress-then-replace shape.**
A path claim excludes two missions for their WHOLE duration, including the expensive part. But the
expensive part usually does not touch the destination at all — it writes a temp file. Only the final
mutation needs exclusivity.

```
mission A   [ compress 8s ..................... ][ replace 3ms ]
mission B   [ compress 7s ..................... ]      ↑ waits  [ replace 3ms ]
                     ↑ both run in parallel            ↑ only the apply is serialized
```

Under path claims today, B's compress waits for A's replace: ~15s. Split, it is ~8s. The kit already
models the two phases (`Run`/`Commit`) but enforces exclusivity across both, because claims are the
only mechanism it has.

So: missions stay parallel, hand their finished work to the queue, and await **their own** update
landing. The queue is the only thing that mutates managed paths.

## §2 Sketch

```csharp
public interface IFileUpdateQueue
{
    /// Completes when THIS update has landed (or failed). Ordering across callers is the queue's.
    Task<FileUpdateResult> ApplyAsync(FileUpdate update, CancellationToken cancellationToken);
}

public sealed class FileUpdate
{
    public required IReadOnlyList<FileChange> Changes { get; init; }
    /// Partition key — updates with different keys may land concurrently. Null = the global partition.
    public string? Partition { get; init; }
    public RetryPolicy? Retry { get; init; }     // reuse the mission layer's, unchanged
}

public abstract record FileChange
{
    public sealed record Replace(string TempPath, string TargetPath, string? BackupPath = null);
    public sealed record Move(string From, string To, bool Overwrite = false);
    public sealed record Delete(string Path, bool Recursive = false);
    public sealed record CreateDirectory(string Path);
}
```

`Replace` is the load-bearing one: it is `File.Replace`/`File.Move(overwrite)` plus the retry the
family already knows it needs, and it is the commit half of the two-phase rule made real. The design
that shipped in 0.3.0 promised an atomic-replace helper (§6 of the mission design) and never
delivered one; this is where it belongs.

## §3 Decisions, with the defaults I would build

Each is a real fork. These are recommendations, not settled — that is what this document is for.

| Decision | Default proposed | Why, and what it costs |
|---|---|---|
| **Serialization granularity** | ONE writer, with `Partition` as the seam | Simplest thing that is obviously correct: one update lands at a time, so no two mutations can interleave. Apps with genuinely independent trees (per-drive, per-library) set a partition and get concurrency back. Starting per-root instead would mean inferring roots, and getting that wrong is silent corruption rather than slowness. |
| **Atomicity unit** | **The APP chooses, per update** — see §3.1 | Owner: *"it depends what the application need, it can be per file, or per change (so multiple file all taking count as a single action)."* Both modes are legitimate and an app needs different ones for different updates, so this is a property of the update, not a property of the queue. |
| **Temp ownership** | The MISSION owns temps | It already writes them, names them, and knows what to clean up. A queue that hands out staging directories would have to own their lifetime across a crash, which is the journal problem again by another route. |
| **Where it lives** | `Shenora.Core/Io/` | Portable (`net10.0`), no Windows reference, no scheduler reference. An app can use the queue with no mission scheduler at all. |

### §3.1 Atomicity is the update's choice, and the honest limit is the crash boundary

```csharp
public enum FileAtomicity
{
    /// Each change stands alone. Ordered, fail-fast: stop at the first failure and report its index,
    /// leaving earlier changes applied. Right when the changes are independent — N unrelated files.
    PerChange = 0,

    /// The whole change set is ONE action: either all of it is visible or none of it is. Right when a
    /// half-applied set is a broken product — a mod's files plus its manifest, a bundle plus its index.
    AllOrNothing = 1,
}
```

`AllOrNothing` is implemented by **compensating rollback**: `Replace` keeps the displaced original
(that is what `File.Replace`'s backup parameter is for), `Move` remembers where it came from,
`CreateDirectory` remembers it created, and a failure walks the applied changes backwards undoing
them. `Delete` is the one that cannot be undone from nothing, so under `AllOrNothing` a delete is
staged — moved to a temp location and only really removed once the whole set has landed.

**The limit, stated up front rather than discovered:** this covers a change that FAILS. It does not
cover the process DYING mid-apply, because nothing is written down. Crash-atomicity needs a durable
intent journal replayed at startup — additive later (it is a store behind the same API, and the
rollback bookkeeping is already the journal's content), so it is not built now, but the API must not
promise what it does not do. `FileAtomicity.AllOrNothing` means "no partial result from a failure",
not "no partial result from a power cut", and the XML will say exactly that.

### §3.2 What the build changed from this plan

Two things worth recording, because the plan is what a reader trusts:

- **The temp-ownership question answered itself.** Missions own their temps, as proposed — but
  `AllOrNothing` forces the queue to own the ones IT creates: the backup of a replaced file, and the
  aside-copy of a staged delete. Both are siblings of the target (`<path>.shenora-bak-<n>`,
  `.shenora-del-<n>`) so the move is same-volume and therefore atomic; a staging directory elsewhere
  would silently turn every replace into a cross-volume copy of the file being replaced.
- **A cancellation token is accepted but stops mattering once an update starts applying.** It cancels
  the WAIT for the partition. Abandoning a half-applied set on a cancel is the one outcome no caller
  can do anything with, so it is not offered.

## §4 Locking — cross-process, because claims are not (NOT BUILT — see the status note)

This is the gap the owner named, and it is real. A `MissionClaim` excludes work **inside one
scheduler in one process**. It does nothing about a second instance of the app, an installer, a
sync client, or the user's own tooling touching the same tree.

```csharp
public interface IPathLease : IAsyncDisposable
{
    string Path { get; }
    bool IsHeld { get; }
}

public interface IPathLocker
{
    /// Null when the lease cannot be taken within the timeout — a caller DEFERS, it does not force.
    Task<IPathLease?> TryAcquireAsync(string path, LockMode mode, TimeSpan timeout, CancellationToken ct);
}
```

Proposed implementation: a sidecar lock file opened with `FileShare.None` and
`FileOptions.DeleteOnClose`, so the OS drops the hold when the process dies — which is what makes
stale locks a non-problem on Windows rather than a cleanup chore. Points that need stating plainly in
the implementation, because each is a trap:

- **Advisory, not mandatory.** It excludes *participants*. Nothing stops a process that does not take
  leases. The rule an adopting app inherits is the one their audit already states: never mutate a
  managed path outside the sanctioned path.
- **POSIX differs.** `DeleteOnClose` semantics and file locking are not the same on Linux/macOS. The
  first implementation targets Windows honestly and the seam allows another.
- **Network shares are not a target.** SMB lock semantics vary by server; claiming support we cannot
  test is worse than declining it.
- **No ordering guarantee.** A lease is not a queue: two waiters may acquire in any order. In-process
  fairness stays the scheduler's job (admission rule 2).

**Composition, not a third mechanism.** In-process exclusion = mission claims. Cross-process
exclusion = a lease taken by the update queue before it applies. An app that wants both declares a
claim and lets the queue take the lease; the queue is the only place that needs to know.

## §5 How a mission uses it

```csharp
Run    = (mission, ct) => archive.CompressToTempAsync(source, temp, ct),   // parallel, expensive
Commit = (mission, ct) => updates.ApplyAsync(new FileUpdate
{
    Changes = [new FileChange.Replace(temp, target)],
}, ct),                                                                     // serialized, cheap
```

Note what is NOT needed here: a path claim on `target`. The queue is the only writer, so exclusivity
comes from it. Claims remain the right tool when two missions must not even *compute* concurrently
(they read a shared cache, they would duplicate a download) — which is a different question from who
writes last.

## §6 Deliberately not built

Recorded so the next session does not re-argue them, each with the trigger that would change the
answer:

- **A durable intent JOURNAL (crash-atomicity).** In-process rollback is in scope (§3.1); surviving a
  power cut mid-apply is not. Trigger: an app that cannot tolerate a torn set after a hard kill.
  Additive when it comes — the rollback bookkeeping §3.1 already builds IS the journal's content, so
  it becomes a store behind the same API rather than a reshape.
- **Transactions across volumes.** No OS gives us this; simulating it is the journal, twice.
- **A general `IFileSystem` abstraction.** The apps have their own and should keep them
  (`docs/ADOPTION.md` says so). The kit needs a *writer*, not a filesystem.
- **Archive, download, cleanup helpers.** Business logic. Unchanged from the mission design's §6.
- **Watching for external changes.** A different component again (`FileSystemWatcher` semantics,
  debouncing, rename storms). Not this.

## §7 Verification — what would make it believable

The mission scheduler's concurrency suite is the model: prove BOTH halves in one run, because a
serial implementation passes any test that only checks results.

- Two updates on the same partition **never overlap** (an applier probe records enter/exit), while
  updates on different partitions **do** — asserted in the same run.
- A failing change stops its update at that index and reports it, leaving earlier changes applied.
- `Replace` survives a target locked by another handle for the first N attempts (the retry path),
  and gives up honestly after.
- A lease held by a *second process* (a real child process in the test, not a mock) blocks the queue
  until released — the only way to prove a cross-process lock is with a second process.
- Cancellation mid-queue leaves no partial change from the cancelled update, and does not disturb the
  one already applying.

## §8 Open questions

1. ~~Per-change or all-or-nothing?~~ **Answered (owner, 2026-08-02): both, chosen per update** — §3.1.
   The crash boundary stays out of scope and stays documented.
2. Should the queue own **deletes of temp files** on failure, or is that the mission's cleanup? Note
   `AllOrNothing` forces a partial answer: staged deletes are the queue's, because it created them.
3. Does anything need cross-process leases *today*, or is single-instance the practical guarantee?
   Additive either way, so it can follow.
