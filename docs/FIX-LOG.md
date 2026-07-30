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

### Shenora.WebView2.Sessions: a throwing app logger could hang a lease and leak a capacity permit
- **Symptom:** found by the H2 sessions batch's own phase review, in code that batch had just written.
  No observed incident. An app `ILogger` that throws — a file sink whose handle went away, a
  scope-captured provider used after shutdown — could permanently hang a `LeaseAsync` caller, leak a
  capacity permit for the process lifetime, or crash the UI thread, depending on which log line hit.
- **Root cause:** an `ILogger` is APP CODE, so the package's own rule (no app-supplied callback runs
  unguarded inside a WebView2/WinForms event handler or a posted UI-thread body) applies to it — and the
  logging added in P5.5 H4.7 invoked it bare at all eight sites. Three of those turn a log line into a
  real failure: inside the instance-creation `catch` the throw escaped BEFORE `tcs.TrySetException`, so
  the lease's task never completed (a hung caller still holding its permit); inside the return-to-pool
  body it escaped before `_capacity.Release()`; and inside `NewWindowRequested`/`PermissionRequested`/
  `ProcessFailed` there is no caller on the stack at all, so it is an unhandled UI-thread exception.
  Note this is the same finding class — "an app-supplied callback running unguarded inside a UI-thread
  event handler or timer" — that the phase-review checklist was extended with after the first full
  review; it caught it here on the first pass.
- **Fix:** new internal `SessionLog.Try(ILogger?, Action<ILogger>)` — the one place that knows a lost
  log line must never become a lost session — used at all eight sites in `RenderSession`,
  `RenderSessionPool` and `SessionBrowser`. In `Return` the message's reason string is also computed
  before the call so the interpolation can't throw inside the guarded body either.
- **Verify:** `RenderSessionPoolTests.A_throwing_app_logger_cannot_hang_a_lease_or_leak_a_permit` — a
  logger that throws on every call, driven down the discard path (the one that logs); the lease
  completes, the instance is discarded, and the permit comes back. 382 dotnet + 39 vitest, `verify`
  PASSED.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a wedged page permanently poisoned the render pool
- **Symptom:** found by the first full P0–P5 review, then re-verified. One page blocked in its own
  script thread (a spin loop) made every later lease useless: with `Capacity=2`, two such pages
  answered `RENDER_BUSY` for the rest of the process lifetime.
- **Root cause:** TWO mechanisms, and fixing only the first (H4.2) was not enough.
  (1) `RenderSession.OnUiAsync` accepted a `CancellationToken`, checked it once inside the posted
  delegate, then awaited the body with no way to observe it again — so the caller could not escape, the
  lease never returned, and the capacity permit was gone. H4.2 closed this by routing the marshal
  through `WinFormsUiDispatcher`, whose `InvokeAsync` observes the token via `WaitAsync`.
  (2) But `WaitAsync` hands the CALLER back; it cannot kill the outstanding operation. The wedged
  instance was still returned to the pool by `DisposeAsync`, reset (see the next entry — the reset
  reported success even when it timed out), and re-leased. So the pool healed its accounting and kept
  handing out the same dead browser. Compounding both: no operation had a time cap at all, and every
  parameterless overload passes `CancellationToken.None`, so the default caller had no escape either.
- **Fix:** `RenderSession.RunBoundedAsync` wraps every marshalled op in a linked CTS with
  `CancelAfter(OpTimeout)` (new option, 60 s) and poisons `PoolInstance` when the body did not complete,
  which makes `RenderSessionPool.Return` discard it instead of re-pooling. Completion is TRACKED via a
  flag set in the body's `finally`, not inferred from the exception type: a body that ran and threw (a
  rejected URL, a guard refusal) leaves a reusable instance, and discarding it would cost a browser
  startup on every ordinary error. An expiry becomes `TimeoutException`; a caller's own
  `OperationCanceledException` is never rewritten, though it DOES poison — deliberately, since the
  caller walked away while the renderer may still be mid-script. `NavigateAsync`'s hardcoded 30 s cap
  became the `NavigationTimeout` option so the two budgets are coherent.
- **Verify:** `RenderSessionPoolTests` — a new `StalledAnchor` (a handle realized on its own thread that
  NEVER pumps) makes "the operation never completes" deterministic; note this detail, because an anchor
  on the test thread runs bodies INLINE via the dispatcher's correct fast path and would have proven
  nothing. Tests: an abandoned op throws `TimeoutException` and poisons; a cancelled caller gets
  `OperationCanceledException` (not a timeout) and also poisons; an ordinary body failure does NOT
  poison and is re-pooled; a poisoned instance is discarded without even attempting a reset. 381 dotnet
  + 39 vitest, `verify` PASSED.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a session that could not be reset was re-pooled forever
- **Symptom:** found by review. A pooled instance whose renderer stopped answering was recycled
  indefinitely; every lease that drew it burned the full navigation cap before failing.
- **Root cause:** `RenderSessionPool.ResetToBlankAsync` awaited the blank navigation with
  `WaitAsync(5s)` inside a `try`/`catch` that swallowed the outcome and then `return true`
  unconditionally. Its own comment defended this — "slow blank nav — re-pool anyway; the next lease
  navigates away regardless" — which is the actual error: a renderer that cannot complete a navigation
  to `about:blank` cannot complete the next lease's navigation either. So the documented "a failed
  reset DISCARDS the instance" invariant was only reachable if the navigation THREW. The test pinning
  that invariant drove `ResetOverride`, never the real path, which is why it passed five phase reviews.
- **Fix:** the wait's decision moved to `internal static AwaitResetNavigationAsync(Task, TimeSpan)`,
  which returns false on timeout OR fault; `ResetToBlankAsync` returns it. The 5 s budget became the
  validated `ResetTimeout` option. `Return`'s discard log now names WHICH invariant fired (a dead
  renderer vs a reset the renderer never answered) — lumping them together is what made a wedged pool
  opaque.
- **Verify:** `RenderSessionPoolTests` — a theory over the REAL helper (a never-completing navigation →
  false, a completed one → true) plus a faulted navigation → false, and the existing discard test still
  pins the consequence.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a cancelled session start left a live browser holding the profile lock
- **Symptom:** found by review. A cancelled `LeaseAsync`/`StartAsync`, or a pool disposed while an
  instance was being created, returned/threw to the caller while a realized off-screen window and its
  browser process stayed alive — holding the profile's folder lock with no owner left to dispose it. For
  co-browse a screencast could additionally start writing frames into a channel no reader would ever be
  handed.
- **Root cause:** both call sites checked `cancellationToken.IsCancellationRequested` exactly once, at
  the TOP of the marshalled body — before the multi-second `SessionBrowser.InitializeAsync` (browser
  process spawn + profile attach + settings). Nothing re-checked afterwards, so anything cancelled
  during the expensive part still published a fully live instance. `LeaseAsync` also built a linked
  token (caller + pool dispose) for the capacity wait but then passed the RAW caller token to the
  instance factory, so pool disposal could not cancel a creation at all.
- **Fix:** `RenderSessionPool.CreateInstanceAsync` re-checks after init and runs the same cleanup as the
  failure path — extracted to a shared `TearDown()` local, which also stopped being silent (a leaked
  control keeps the profile locked, the exact symptom the init-timeout message tries to explain).
  `CoBrowseSession.StartAsync` re-checks twice: after init, and again before publishing, since past that
  line the caller owns teardown. `LeaseAsync` now passes `linked.Token` to the factory.
- **Verify:** `RenderSessionPoolTests.Dispose_cancels_an_in_flight_instance_creation` — a factory parked
  on `Task.Delay(Infinite, ct)` proves the token it receives is the linked one, the lease throws
  `OperationCanceledException`, and the capacity permit comes back. The post-init re-checks need a real
  browser to exercise and are covered by the sample e2e, not a unit test.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: every retry against a locked profile orphaned another browser process
- **Symptom:** found by review (carried over from H4.4). Repeated leases against a profile folder held
  by a zombie `msedgewebview2` each added another browser process queued on that same lock — growing
  the very lock the init-timeout's error message blames. Separately, a pool of N instances paid for N
  environments on one profile.
- **Root cause:** `SessionBrowser.InitializeAsync` called `CoreWebView2Environment.CreateAsync` per
  instance, guarded by `.WaitAsync(InitTimeout)`. `WaitAsync` abandons the AWAIT, not the underlying
  operation — so the timed-out creation kept running and the next attempt started an additional one.
- **Fix:** new internal `SessionEnvironmentCache`, held by `RenderSessionPool` and passed to an internal
  `InitializeAsync` overload (the public signature is unchanged). It reuses an IN-FLIGHT creation, which
  is the anti-orphan half, and a completed one, which is the one-per-profile half. Two shape decisions
  are load-bearing: (a) it is **owner-scoped, not static/profile-keyed** — a live environment keeps its
  profile's browser process and therefore the folder lock alive, so a process-lifetime cache would have
  made `LoginWindow.ClearProfile` fail every time rather than only while a window is open; a login
  window opens one profile once and gains nothing from caching. Owner scoping also makes it
  single-threaded by construction, which matters because `CoreWebView2Environment` is thread-affine.
  (b) A faulted or cancelled creation is **not** cached — the trap `Shenora.WebView2`'s own
  `WebViewEnvironment` still has (`TASKS.md` H3), where one transient failure is terminal for the
  process. `RenderSessionPool.Dispose` clears the cache.
- **Verify:** `SessionEnvironmentCacheTests` — in-flight reuse (creation delegate called once),
  completed reuse, faulted and cancelled both retried, and `Clear` releasing. Real environment creation
  needs a browser process, so the cache's DECISIONS are tested through the creation delegate.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a co-browse frame stream could stop silently, and a late tap could read another lease
- **Symptom:** two review findings in the same area, both silent by construction. (1) A co-browse
  stream that freezes after an arbitrary GC, with no error anywhere — the consumer just sees a page that
  went still. (2) A `RenderSession` interceptor installed after the lease returned received the NEXT
  lease's JSON API responses and posted messages.
- **Root cause:** (1) `CoBrowseSession.StartAsync` kept `GetDevToolsProtocolEventReceiver(...)` in a
  local and subscribed to it there. Nothing referenced the receiver once the method returned, so the
  subscription's survival depended on the WebView2 SDK caching it internally — unspecified behaviour —
  and `DisposeAsync` never detached it either. (2) `OnNetwork`/`OnMessage` were the only public
  `RenderSession` members with no `_disposed` check, and the only two that install a PERSISTENT tap;
  after `DisposeAsync` the instance is back in the pool and handed to another lease, so a stale
  reference or a continuation outliving its `await using` produced cross-lease disclosure — in a package
  whose whole story is profile isolation.
- **Fix:** (1) the receiver and its handler are now fields (`_frameReceiver`/`_onFrame`), passed into
  the constructor, and `DisposeAsync` detaches before stopping the screencast. (2) both members call
  `ThrowIfDisposed()` (the same `ObjectDisposedException` every other member already throws via
  `OnUiAsync` — failing loudly, not silently no-op'ing) and the marshalled subscribe body re-checks
  `_disposed`, closing the check-then-post race.
- **Verify:** `RenderSessionPoolTests.Interceptors_cannot_be_installed_after_the_lease_is_returned`.
  The receiver rooting is a lifetime fix with no unit-testable seam — it is compile-and-review verified,
  and the co-browse sample seam (H9.5) is where it gets exercised live.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2: file-mode frontend serving read any file the process could
- **Symptom:** no observed incident — found by the first full P0–P5 review. A page (or any script in
  it) could request `https://<virtualHost>/%2e%2e%2f%2e%2e%2fWindows%2fwin.ini`, or a rooted
  `/C:%2fUsers%2f…`, and receive the file's contents. Responses carry
  `Access-Control-Allow-Origin: *`, so the body was readable by page script.
- **Root cause:** `WebViewHost.ServeVirtualHost` unescapes the request path before calling the
  provider — deliberately, so bundle filenames with spaces or CJK characters resolve — and
  `EmbeddedResourceProvider`'s `Normalize` only replaced backslashes and trimmed leading slashes. No
  `..` rejection and no containment assertion. Worse for the rooted case:
  `Path.Combine(root, "C:\…")` DISCARDS its first argument when the second is rooted, returning the
  attacker's absolute path verbatim. Embedded mode was safe only incidentally (`../` maps to a
  manifest name that doesn't exist). Live wherever `PreferFiles` is set — the sample derives it from
  `IsDevelopment`, so every dev session.
- **Fix:** `EmbeddedResourceProvider.ResolveContained` rejects rooted paths and `..` segments, then
  asserts `Path.GetFullPath(combined)` still sits under `Path.GetFullPath(root)` + separator (so a
  sibling directory sharing the root's prefix can't pass either). Applied in BOTH `GetResourceStream`
  and `Exists` — `Exists` alone would have leaked existence.
- **Verify:** `EmbeddedResourceProviderTests` — 7 escaping paths (traversal in both separator
  spellings, nested, and three rooted forms) return null/false while a legitimate file still serves;
  3 legitimate paths with spaces, CJK characters and nesting still serve (the unescape exists for
  those); plus the sibling-prefix case. 346 dotnet + 39 vitest green, `verify` PASSED.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2: an unserializable notification crashed the UI thread and lost its batch
- **Symptom:** found by review. One app event whose payload can't serialize — a cyclic object graph
  (ORM parent/child), a `Type`/delegate member, a throwing getter — produced an unhandled UI-thread
  exception (a modal crash dialog under the family bootstrap, recurring on the 50 ms timer) and the
  whole pending notification batch vanished.
- **Root cause:** `WebViewIpcBridge.TryBuildBatchJson` dequeues every pending notification and THEN
  calls `IpcJson.Serialize` on the batch, with no try/catch anywhere on the path from `Timer.Tick` →
  `Flush` → here. Because the queue was already drained, the throw lost the good notifications too.
  The INCOMING path guards this exact case with an explicit comment; the outgoing twin never did.
- **Fix:** serialize per notification and keep only the ones that succeed (so a single bad event
  can't take its batch down), logging the offender's module/type but never its payload; plus a
  catch-all around `Flush` since it runs on a timer with no caller to observe it.
- **Verify:** `WebViewIpcBridgeTests` — a batch mixing two good notifications with a cyclic payload
  and a throwing getter yields a 2-item batch in order and drains the queue; an all-bad batch yields
  no batch rather than throwing.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2.Sessions: `NavigationGuard` did not survive a redirect
- **Symptom:** found by review. A data-driven URL that passed the app's SSRF guard could answer
  `302 → http://127.0.0.1:8080/admin`; WebView2 followed it and `GetHtmlAsync` handed the caller the
  loopback page's DOM. The guard's own XML doc sold it as the app's SSRF/allowlist policy.
- **Root cause:** the guard was consulted only inside the explicit `NavigateAsync` call. The
  package's single `NavigationStarting` subscription (in `LoginWindowController`) only fanned out to
  app taps — it never consulted the guard or set `e.Cancel`.
- **Fix:** the pool records the host the guard approved (`PoolInstance.ApprovedHost`, cleared on
  return-to-pool so a recycled instance can't inherit it) and cancels unvetted CROSS-HOST navigation
  at `NavigationStarting`. Scope stated honestly in the option's docs rather than over-promised:
  `CoreWebView2NavigationStartingEventArgs` exposes NO deferral (confirmed by compiler error while
  implementing the first attempt), so an async guard cannot be awaited in that event and blocking on
  it would deadlock the UI thread — a synchronous cross-host rule is the most the event can enforce.
  `SessionBrowserOptions.RequestFilter` is synchronous and already wired with
  `WebResourceContext.All`, so it remains the seam for full redirect/subresource policy; both options
  now document the division. Deliberately NOT applied to `LoginWindow`: interactive OAuth sign-in
  legitimately redirects across hosts, and a human-driven login window is not a data-driven SSRF
  surface.
- **Verify:** builds + full suite green; the live redirect path needs a real server and stays e2e/
  manual per `docs/REVIEW-GUIDE.md` §6.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2.Sessions: `ClearProfile` was a recursive delete on an unvalidated path
- **Symptom:** found by review. `LoginWindow.ClearProfile` runs
  `Directory.Delete(recursive: true)` on a caller-composed path, and profile paths are built from
  data-driven provider/account identifiers — so a `..` segment could aim the delete outside the
  sessions root, or collapse two accounts onto one cookie jar, defeating the isolation the same
  options doc calls a security boundary.
- **Root cause:** no validation, and no supported way to build the path safely — the library
  documented the boundary but left composition to the caller.
- **Fix:** `ClearProfile` refuses paths containing `..` segments, and a new public
  `LoginWindow.ComposeProfileDirectory(root, params segments)` validates each segment (no separators,
  no `.`/`..`, no drive qualifier, no invalid file-name characters, no Windows reserved device names)
  and asserts the composed path stays under the root.
- **Verify:** `LoginWindowTests` — 4 traversing paths throw; composition builds a contained path
  usable by `ClearProfile`; 9 unsafe segments throw; two accounts get distinct directories.
- **Commit:** _pending (P5.5 H1 batch)_

### devtools: the sensitive guard failed OPEN and the verify gate never compiled the samples
- **Symptom:** found by review. Two gates reported success while covering less than documented:
  `dev.mjs verify` never compiled `samples/` (so the reference composition and e2e subject could be
  red while verify was green), and `check-sensitive` degraded to two structural patterns whenever
  `local/sensitive-patterns.txt` was absent — which is every fresh clone and every CI run, i.e. the
  private-name half of the guard never ran in the release gate. `dev.mjs test <typo>` exited 0 having
  run nothing.
- **Root cause:** `Shenora.slnx` carried an EMPTY `<Folder Name="/samples/" />` (and omitted
  `Shenora.Core`) while `dev.mjs build` builds only the solution; the scanner's missing-pattern-file
  branch printed a notice and continued; the `test` dispatcher compared its argument against three
  values with no else branch. Compounding: `-clp:ErrorsOnly` plus no `TreatWarningsAsErrors` made
  warnings both non-fatal and invisible.
- **Fix:** samples + Core added to the solution; scanner exits non-zero without the pattern file
  (`--allow-builtins-only` / `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` to opt in, which the release
  workflow now does explicitly), also scans file paths, includes renamed/copied staged files, and a
  new `commit-msg` hook scans commit messages; `test` fails on an unknown target; warnings are errors
  for `src/`; `verify` additionally type-checks the sample web app and runs `doctor`.
- **Verify:** `dotnet build Shenora.slnx` succeeded with 0 warnings / 0 errors WITH the sample newly
  compiled and warnings-as-errors on; the four scanner behaviours exercised by hand (clean message →
  0, leaking message → 1, missing pattern file → 1, `--allow-builtins-only` → 0); `verify` PASSED
  showing the two new steps.
- **Commit:** _pending (P5.5 H5 batch)_

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
- **Commit:** 4ebb8e0

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
