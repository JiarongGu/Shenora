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
