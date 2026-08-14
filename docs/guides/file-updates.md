# The file-update queue

> **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never
> restated** — that is the rule D57 was written to keep (five design docs were retired precisely
> because a third copy of the reasoning goes stale while nobody notices).
> Migrating an existing app? Start at [ADOPTION.md](../ADOPTION.md).

## The file-update queue — for when claims are too coarse

**One call, the same as the media player:**

```csharp
builder.UseFileSystem();     // journalled queue + rollback + cross-process path locks
```

Inject `IFileUpdateQueue` and you are done. The journal and lock directories default under the app's own
data path — the kit can choose those because they change nothing the app is exposed to. ⚠ Contrast
`UseMediaPlayer`, where `AllowedRoots` is *not* defaulted because it is a containment boundary: the test
each time is **may the kit make this choice without changing what the app is exposed to?**

`builder.UseFileSystem(x => …)` overrides anything — the defaults are applied *after* your lambda, never
over it. The rest of this section is what the queue does and how to drive it.

This lives inside `Shenora` — no extra package to reference. Still no shell/IPC/Windows dependency, and
**independent of the scheduler**: usable with it, without it, or before you adopt it.

🔴 **It is TWO namespaces, not one.** The former `Shenora.IO` was removed by D65's relayering, so
`using Shenora.IO;` no longer compiles. The split is meaningful rather than bookkeeping: landing files
safely is an ENGINE concern, and updating the app is a MODULE built on it.

```csharp
using Shenora.Engine.Files;    // FileUpdate*, FileChange, IPathLocker
using Shenora.Modules.Update;  // UpdateManifest, UpdateStage
```


**The problem it solves.** A path claim excludes two missions for their WHOLE duration. But the
expensive phase usually does not touch the destination at all — it writes a temp file. Only the final
mutation needs exclusivity:

```
mission A   [ compress 8s ..................... ][ replace 3ms ]
mission B   [ compress 7s ..................... ]      ↑ waits  [ replace 3ms ]
```

Under path claims, B's compress waits for A's replace: ~15s. Compute in parallel and serialize only
the landing: ~8s. So the queue is the destination for anything that currently claims a path just to
protect a rename.

```csharp
Run    = (mission, ct) => archive.CompressToTempAsync(source, temp, ct),   // parallel
Commit = (mission, ct) => updates.ApplyAsync(new FileUpdate
{
    Changes    = [new FileChange.Replace(temp, target)],
    Atomicity  = FileAtomicity.PerChange,
    Retry      = new RetryPolicy(),
}, ct),                                                                     // serialized
```

| You probably hand-rolled | Use | Notes |
|---|---|---|
| A "write temp, then `File.Replace` with retry" helper, copied per feature | `FileChange.Replace` inside a `FileUpdate` | The retry is `RetryPolicy`, the same type the scheduler uses, applied per change. |
| A lock or flag so two features never write the same tree at once | the queue itself — one writer per `Partition` | `Partition = null` is one global writer, the setting that cannot surprise you. Partition only by trees that genuinely never touch. |
| "Apply these five files together or not at all", hand-rolled with a backup folder | `FileAtomicity.AllOrNothing` | Undoes applied changes in reverse. A delete becomes STAGED — moved aside, really removed only once everything lands — because a delete cannot be undone from nothing. |
| Reporting which file broke a batch | `FileUpdateResult.FailedIndex` + `Applied` | The result reports rather than throws, like `MissionResult`; `ThrowIfFailed()` if you prefer exceptions. |

**Surviving a power cut is opt-in, and it is one line.** Without a journal, `AllOrNothing` rollback is
in-process: it covers a change that fails, not a process that dies. With one, the undo plan is on disk
before each change and recovery finishes the job at startup:

```csharp
var queue = new FileUpdateQueue(new FileUpdateQueueOptions
{
    Journal = new FileUpdateJournal(new FileUpdateJournalOptions { Directory = paths.DataArea("journal") }),
});
await queue.RecoverAsync();   // at startup, BEFORE submitting anything
```

An update interrupted while applying is rolled back; one interrupted after every change landed (only
staged deletions left) is finished instead — rolling that back would undo a success. Recovery is safe
to run twice, because every undo step checks the world before acting.

> ⚠ **A journal nobody replays is a directory that fills up.** Configuring one means calling
> `RecoverAsync()` at startup, before the first submit — an interrupted update's paths are exactly the
> ones your next update is likely to touch.

> ⚠ **Only `AllOrNothing` updates are journalled.** `PerChange` promises nothing about a crash, so
> paying a file write per update to guarantee something nobody asked for would be pure cost.

### Other processes touching your files — two different problems

The queue and mission claims both serialize work **inside your process**. If your app manages a folder
it does not own — a game's mod directory, a shared library on a NAS — that is not enough, and the two
remaining cases need different tools. Reaching for the wrong one is the mistake worth avoiding:

| Who is touching the file | Tool | Why the other one is useless here |
|---|---|---|
| **Your own second process** — another instance, or a tool you spawn (an `.exe`, a script) and wait on | `IPathLocker`/`FilePathLocker` — the parent takes the lease for the duration of the child's run | Both sides participate, so exclusion is real. Retrying would just mean two writers racing more politely. |
| **A foreign process** — the game itself, a mod loader, antivirus, Explorer's preview handler, another app editing the same folder | `RetryPolicy` (already there) + `IFileLockInspector` to NAME the holder | A lease is advisory. A process that never takes one is completely unaffected, and no lock design changes that. |

```csharp
// The queue takes leases for you, on every path an update touches:
new FileUpdateQueue(new FileUpdateQueueOptions
{
    Locker        = new FilePathLocker(new FilePathLockerOptions { LockDirectory = paths.DataArea("locks") }),
    LockInspector = new RestartManagerLockInspector(),   // Shenora.Windows
});

// Or hold one yourself around a tool that knows nothing about any of this:
await using var lease = await locker.TryAcquireAsync(modFolder, TimeSpan.FromSeconds(30), ct);
if (lease is null) return;            // someone else has it — defer, do not force
await RunExternalFixerAsync(modFolder, ct);
```

When a change fails, `FileUpdateResult.Holders` names who had it — so "the process cannot access the
file" becomes "held by 3DMigoto (12345)", which an app can retry against or show to a user.

> ⚠ **Put the lock directory where the contenders can both see it.** Several processes on one machine
> → your own local data folder (never the managed tree: an app that does not own that folder would be
> scattering lock files into something the user and other applications are also editing). Two MACHINES
> over a share → a directory ON the share. This is the setting that fails silently: everything works
> until two machines write the same file.

> ⚠ **`WhoHolds` returning empty means "cannot tell", not "nobody".** Restart Manager asks the local
> machine only, so a file held open from another machine over a share is invisible to it — that answer
> exists only on the server.

> ⚠ **Over a network share, a lease released by a CRASH comes back in tens of seconds, not instantly** —
> the server frees the handle when the session times out. Bounded and self-healing, but size your
> lease timeout for it, and expect more transient IO than a local disk (widening `RetryPolicy`'s
> `IsTransient` beyond `IOException` is reasonable over SMB).

**Verify:** the same partition never overlaps *while* a different partition does — both in one run,
for the same reason as the scheduler. Then fail a change mid-update under `AllOrNothing` and confirm
the earlier ones were undone in reverse, and that a staged delete came back.

### Adopting the STAGE without adopting the applier — the on-disk contract

**Staging and applying are separately adoptable, and for some apps only one of them ever will be.** If your
updates are applied by something the kit did not write — typically a native launcher that lives beside the
install and is never replaced by its own updates, so every copy in the field keeps the applier it shipped
with — you can still produce stages with `UpdateStage` and let your existing applier consume them. **The
layout is a supported contract, not an implementation detail:**

```
{UpdateStageOptions.Root}/
  ready.json          ← the MARKER. Written LAST, after every file has matched its hash.
  staged/
    manifest.json     ← the full release manifest, which is where an applier reads REMOVALS
    <every changed file, at its manifest-relative path>
```

`ready.json` is camelCase JSON: `{"pending":true,"version":"1.4.0","stagedAt":"2026-08-06T09:12:33.4+00:00"}`.
An applier that only asks "is an update waiting" may test for the file and read nothing.

**The marker is the only thing an applier may trust**, and that is the whole design: it appears only after
every staged file has been verified, so a crash mid-download leaves files and no marker, and the next run
restages. An applier that scans `staged/` for content instead will eventually act on a half-downloaded one.

> ⚠ **Take this half deliberately, not by default.** The strong half of the story is the journaled,
> recoverable APPLY (`FileUpdateQueue`, `RecoverAsync`, `AllOrNothing`) — and that is exactly the half a
> frozen applier already owns and cannot hand over. If your applier is already installed on your whole user
> base, adopting the stage is cheap and adopting the apply is a migration question the kit does not answer
> for you yet (`Shenora.Launcher` assumes it is the applier from day one, which is true only for a product
> that has not shipped). Naming that up front is more useful than a recipe that quietly assumes both.

---
