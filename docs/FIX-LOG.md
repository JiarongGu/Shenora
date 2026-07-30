# FIX-LOG.md — notable fixes, newest first

Append via `/fix-log` after landing any non-trivial bug/regression fix. Grouped by `## <date>`;
entry template:

```
### <area>: <symptom>
- **Symptom:** what was observed
- **Root cause:** the actual mechanism
- **Fix:** what changed (files)
- **Verify:** how it was proven fixed
- **Commit:** <hash>
```

## 2026-07-30

### Shenora.WebView2.Sessions: `SemaphoreSlim.Dispose()` wedged a just-cancelled waiter
- **Symptom:** a new P5 test (`RenderSessionPoolTests.Dispose_cancels_a_queued_lease…`) hung
  forever — `dotnet test` never printed a summary and hit the 10-minute harness timeout. The
  pool's `Dispose()` was supposed to cancel a lease queued on the capacity semaphore so a wedged
  wire request settles instead of hanging; the awaiting task never faulted.
- **Root cause:** `RenderSessionPool.Dispose()` cancelled the dispose `CancellationTokenSource`
  (which, linked into each `LeaseAsync`'s `WaitAsync`, should cancel a queued waiter) and then
  immediately called `_capacity.Dispose()`. Disposing a `SemaphoreSlim` while a waiter is still
  unwinding its just-fired cancellation races the waiter's internal queue-removal and can leave
  its task permanently incomplete. Introduced in this same P5 phase-review fix (not a regression
  of shipped code) — the cancel was correct, the adjacent `Dispose()` defeated it.
- **Fix:** stop disposing the semaphore (and the CTS) in `RenderSessionPool.Dispose()` — a
  `SemaphoreSlim` only needs disposal if `AvailableWaitHandle` was touched (it never is here), so
  skipping it is safe and removes the race; the cancel alone wakes queued waiters cleanly. The
  regression test now also bounds its wait with `Task.WaitAsync(5s)` so a future re-break FAILS
  fast instead of stalling the suite. File: `src/Shenora.WebView2.Sessions/RenderSessionPool.cs`.
- **Verify:** the isolated test went from a >10-min hang to passing in ~0.3 s; full `verify`
  green (318 dotnet + 39 vitest).
- **Commit:** _pending_

### @shenora/react packaging: the published tarball was unusable under native Node ESM
- **Symptom:** `npm install <tarball>` then `import('@shenora/react')` in plain Node failed with
  `ERR_MODULE_NOT_FOUND … dist/types` — the package worked in every bundler (Vite, vitest) but
  not under Node's own ESM loader. Found by the P1.1 local-feed consumption smoke, which exists
  exactly to catch what the bundler-based dev loop can't.
- **Root cause:** the sources used extensionless relative imports (`from './types'`), and the
  tsconfig's `moduleResolution: "bundler"` neither requires nor emits extensions — so the
  compiled `dist/*.js` carried extensionless specifiers, which bundlers resolve but native Node
  ESM (and any strict ESM tooling) rejects. Not a regression — the gap existed since the first
  real source files; the sample app masked it because Vite bundles the package.
- **Fix:** explicit `.js` extensions on every relative import/export specifier in
  `src/Shenora.React/src/*.ts` (TS resolves `.js` → `.ts` at build time), and
  `module`/`moduleResolution` switched to `NodeNext` in `tsconfig.json` so a missing extension
  is now a BUILD error — prevention, not just history. Consumption recipe recorded in
  `docs/RELEASING.md`.
- **Verify:** rebuilt + re-packed; the scratch npm consumer (`devtools/_p11-consumer/npm`)
  imports the tarball under plain Node and resolves every export; full `verify` green
  (273 dotnet + 39 vitest); the NuGet side of the same smoke pins `[0.1.0]` from the local feed
  and runs a live dispatch round-trip.
- **Commit:** `0776f37`
