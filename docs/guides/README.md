# Guides — one page per capability

Each of these is adoptable **on its own**. None requires another, none requires the staged migration in
[ADOPTION.md](../ADOPTION.md), and the first three need no shell, no IPC and no Windows.

| Guide | Take it when |
|---|---|
| [The mission scheduler](missions.md) | you have a job queue, a worker pool, or a "don't let these two touch the same path" rule |
| [The file-update queue](file-updates.md) | path claims are too coarse — you need staged writes, an undo journal, or another process holds your files |
| [Media playback](media.md) | a file your user picked will not play, or you want the playback lifecycle in .NET rather than in the page |
| [Running on a phone](mobile.md) | the same app logic should also run on Android or iOS |
| [Auxiliary browser sessions](sessions.md) | your app must drive OTHER pages — off-screen, in front of a human, or streamed into your own UI (desktop only) |

🔴 **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never restated.**
D57 retired five design docs precisely because a third copy of the reasoning goes stale while nobody
notices — and a per-feature guide is the ideal place for that to happen again. If you find yourself
explaining *why* the kit works this way, link the `D<n>` instead.

**New app?** Start at [getting-started.md](../getting-started.md).
**Existing desktop app?** Start at [ADOPTION.md](../ADOPTION.md).
