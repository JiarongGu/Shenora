# WebView2 hosting invariants — the measured rules the host encodes

The family's WebView2 lessons, earned live and now enforced by `src/Shenora.Windows/`
(`WebViewEnvironment`, `WebViewHost`, `EmbeddedResourceProvider`). Read before touching hosting,
serving, or session code (incl. the P5 sessions package) so a refactor doesn't undo a fix.

## The rules

- 🔴 **A RESPONSE BODY MUST CLOSE ITSELF AT ITS BOUND — WebView2 never disposes one, not even on the
  success path.** Measured 2026-08-15 (`BodyDisposalProbe`, desktop sample): a body read to its very end
  is still not disposed by the browser, so a handler returning a raw `FileStream` through
  `WebViewResourceResponse.Ok`/`PartialContent` leaks a handle per request — which on Windows also blocks
  moving or deleting the file being served. `BoundedBodyStream` exists for this and is applied in
  `WebViewFiles.Serve` and `ComputedRemuxRoute`; **anything else serving a stream has to do the same.**
  - ⚠ **Do not reach for an "abandoned mid-body" case on this platform: there isn't one.** WebView2
    buffers the WHOLE body before the page reads a byte — 32 MiB arrived in a SINGLE `read()` chunk — so
    every body is drained and the at-bound self-close is sufficient. Android differs (it disposes, and it
    does not pre-buffer), which is why this is a per-shell fact rather than a webview one.

- **A custom scheme takes THREE things, and missing any one fails identically: `TypeError: Failed to
  fetch`.** All three were missing or wrong at some point in P7.1, each producing the same
  indistinguishable page-side error, which is why this is a list and not a sentence.
  1. **Register the scheme with the ENVIRONMENT** (`WebViewEnvironmentOptions.CustomSchemes`).
     WebView2 accepts registrations only at environment creation, so declaring a `DeferredSchemes`
     handler alone is rejected by the network stack before the filter is consulted. `WebViewHost`
     now refuses at construction if the two disagree.
     ⚠ `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` is **null** on a default-constructed
     instance, so `.Add(...)` and `{ … }` initializers NullReference — and inside an async environment
     factory that surfaces as a startup that never completes, not a stack trace. Use the constructor
     overload.
  2. **`AllowedOrigins`** on the registration — the page is served from the virtual host, so a fetch
     to `scheme://` is CROSS-origin and the default (same-origin only) refuses it.
  3. **CORS headers on the RESPONSE.** (1) and (2) govern whether the request is made; this governs
     whether the browser hands the result to script. `WebViewHost` defaults
     `Access-Control-Allow-Origin: *` AND `Access-Control-Expose-Headers: *` for deferred schemes,
     both overridable per response. Without the second, a perfectly correct 206 arrives with the
     right bytes and `Content-Range` reads back as **null** — the metadata describing the bytes is
     invisible while the bytes are fine.
  **The diagnostic that cut through all of it: count handler hits.** Three page-side failures with a
  non-zero hit count says the browser refused our RESPONSE; a zero count says the request never
  reached us. Those are different bugs with one symptom, and guessing between them cost an afternoon.
- **A resource handler answers a REQUEST with a RESPONSE — never "here are all the bytes".** The
  deferred-scheme seam used to be `Func<Uri, Task<(byte[], string)>>`, which made two whole classes of
  response unwritable: anything depending on a request header (above all `Range`, so **nothing it
  served could be sought**), and anything larger than memory (the complete `byte[]` meant a 4 GB file
  was 4 GB of RAM). A surveyed app had bypassed the kit and hooked WebView2 itself for exactly this,
  with its own ADR (P6.6) — the definition of a capability someone needs and cannot express. Three
  things the implementation must keep right: **snapshot the request on the UI THREAD** before handing
  it to the handler's pool thread (the WebView2 args and their header collection are COM objects with
  thread affinity); the scheme's `CacheControl` is a DEFAULT for 2xx only, never stamped over a
  handler's own header or onto a 206/404; and `Accept-Ranges: bytes` must be advertised on the 200,
  or a media element will not even ATTEMPT a seek — indistinguishable from "seeking is broken".
  On the parser: a start past the end is **unsatisfiable (416), not clamped** — clamping serves bytes
  nobody asked for with no error — and the suffix form `bytes=-500` means the LAST 500 bytes, which
  hand-rolled parsers read as "from 500". Test it with an ASYMMETRIC case (`bytes=-1`): with a
  1000-byte resource, `bytes=-500` resolves to 500 either way, so the obvious test passes while the
  bug is live.

- **`CoreWebView2Environment` is thread-affine.** Only the main UI thread uses the shared/
  prewarmed environment (`WebViewEnvironment.GetSharedAsync`); a window on its own STA thread
  MUST create its own on that thread (`CreateForCurrentThreadAsync`; same options + user-data
  folder ⇒ one shared browser process). Mixing threads throws — it broke every secondary window
  in the source app.
- **Cache an environment per PROFILE, scoped to the owner that opened it — never process-globally**
  (`SessionEnvironmentCache`, held by `RenderSessionPool`). Two forces meet here and only owner
  scoping satisfies both: a live environment keeps its profile's browser process AND the folder's OS
  lock alive, so a process-lifetime cache makes `InteractiveSession.ClearProfile` (what makes a logout a
  REAL logout) fail every time instead of only while a window is open; and thread affinity above
  means a global cache would need a thread key plus a lock, while an owner marshals to one anchor and
  is single-threaded by construction. Cache the IN-FLIGHT task, not just the result — `WaitAsync`
  abandons the *await*, never `CreateAsync`, so without that a retry against a locked profile
  spawns a SECOND browser process onto the lock the timeout's own message blames.
- **Never cache a FAULTED environment task.** `??=` caches a faulted `Task` as happily as a good one,
  so ONE transient failure (a profile lock, a runtime update mid-launch) became terminal for the whole
  process — including the retry the init-timeout's own message asks for. Reuse only "in flight or ran
  to completion"; evict a faulted/cancelled entry when you observe it. Both caches now do this
  (`WebViewEnvironment.GetSharedAsync`, `SessionEnvironmentCache`).
- **A rate limit is NOT a terminal state.** The renderer auto-reload was throttled to once per 10 s
  with no stopping condition, so a page that faults during load reload-crashed forever, spawning a
  renderer each time — while the option's own doc promised "a crash-looping page must not spin". Give a
  retry loop a CAP (`WebViewHostOptions.MaxAutoReloads`), log the give-up EXACTLY once (or the log
  becomes the new spin), and reset the budget on real success so a long-running app isn't rationed by
  unrelated failures hours apart.
- **The ready gate must close on `ContentLoading`, not `NavigationStarting`** — and on `ProcessFailed`.
  The client spends one `READY` per real page load, so closing the gate on a navigation that never
  replaces the document (cancelled by an app tap or a policy, or failed before committing) closed it
  FOREVER on a live page: buffer to the cap, then silently drop-oldest, for the process lifetime. A dead
  renderer is the mirror case — the gate stayed OPEN and the next tick DRAINED the queue into a process
  that could not receive it, and the drain empties before posting, so those notifications were gone.
  Accept the small `NavigationStarting`→`ContentLoading` window: a flush there reaches the outgoing
  page, whose listeners are still attached.
- **Fail loudly, but from the layer that knows the requirement.** A mistyped `ResourcePrefix` (a
  manifest name, so MSBuild's mangling makes it easy to get wrong and impossible to eyeball) matched
  nothing and opened a BLACK WINDOW with no error. The fix is NOT to throw in
  `EmbeddedResourceProvider`'s constructor: a provider that can serve nothing is correct when the page
  loads from a dev URL — the normal state of a fresh clone. The provider reports (`CanServe` + a notice
  listing the manifest prefixes that DO exist); `WebViewHost.AssertBundleServable` throws, because only
  it knows the bundle is the start document. Probe with `Exists("index.html")`.
- **No exception text in an HTTP response body.** Every response here carries
  `Access-Control-Allow-Origin: *`, so page script can fetch and read it — and `ex.Message` routinely
  means a full local path, or a remote URL from an app scheme handler. One constant body; the diagnosis
  goes to the host log. Same rule as the IPC error boundary (`ipc-contracts`).
- **Everything on `CoreWebView2` is UI-affine — marshal through the ONE owner, never hand-roll a
  `BeginInvoke`.** Post-D19/D20 the seam is `IUiDispatcher` (`Shenora`) implemented once as
  `WinFormsUiDispatcher(Control)` (`Shenora.Windows`); the `WebView/` and `Sessions/` folders inside that
  package consume it through the sanctioned downward edge.
  This rule exists because hand-rolling produced **14 copies with 5 incompatible pre-handle policies**
  and real defects. Why the owner is shaped as it is — all four are invariants, not preferences:
  - **`IsHandleCreated` BEFORE `InvokeRequired`.** Pre-handle, `InvokeRequired` lies (false on a
    pool thread), so "no handle" must never be mistaken for "already on the UI thread" and run the
    WebView2 call off-thread. A deferred `WebResourceRequested` response must be BUILT on the UI
    thread; no handle ⇒ complete the deferral empty (`WebViewHost.ServeAsync`).
  - **Non-blocking `BeginInvoke`, never a blocking `Invoke` off the UI thread** (a measured AppHang).
  - **The marshal OBSERVES the token it accepts.** An op that takes a `CancellationToken` and
    ignores it after posting cannot be cancelled when the page's JS thread is blocked — that is a
    permanent pool-permit leak, not a slow call.
  - **Escaping a wedged op is only HALF the fix — the resource must also be DISCARDED.**
    `WaitAsync` hands the caller back; it cannot kill the outstanding call. Fixing only the escape
    left `RenderSessionPool` re-pooling the dead page, so every later lease inherited it. So a
    pooled resource needs a per-op CAP (`OpTimeout` — every parameterless overload passes
    `CancellationToken.None`, so the default caller has no token at all) plus a poison flag the
    return path honours. Track "the body completed" with a flag set in its `finally`; do NOT infer
    it from the exception type — a body that ran and threw (a rejected URL, a guard refusal) leaves
    a perfectly reusable instance, and discarding it costs a browser startup on every ordinary error.
  - **The posted body is GUARDED.** An exception in a posted delegate is an unhandled UI-thread
    exception (crash dialog), because there is no caller on that stack to catch it.
  - **There is ONE guard and it is `Shenora.AppCallback`** (`Run`/`RunOrDefault`) — not a
    try/catch remembered per site, because "remembered per site" is exactly how this reopened three
    times. It covers anything app-supplied reached from a place with no caller on the stack: event
    handlers, timer ticks, posted bodies, dispose paths.
    - **An `ILogger` or a `Log` action counts as an app callback.** Missed once already: a throwing
      sink (dead file handle, provider used after shutdown) landed before `tcs.TrySetException`
      (lease hangs forever, permit held), before `_capacity.Release()` (permit leaked for the process
      lifetime), and inside three WebView2 event handlers. Worst, several sit inside a `catch` that
      exists to stop a failure escaping, so a throwing sink defeats the thing it reports from.
    - **Make log calls LAZY (`Log(Func<string>)`).** The guard must cover BUILDING the message, not
      just writing it — several messages read WebView2/COM properties that throw once the underlying
      object is gone, and interpolation at the call site happens outside the guard.
    - **Guarding is not enough where the kit still owes the event an answer.** A failed
      `OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed` must FALL BACK to the built-in
      policy: an un-cancelled download proceeds, an unanswered permission request stalls its caller,
      and a renderer crash goes unhandled exactly when things are already wrong. `WndProcHook` falls
      back to "did not handle this message" so the window keeps working.
    - A lost callback must never become a lost operation, so the guard swallows — and the error sink
      is guarded too, or the fatal throw just moves one frame outward.
  - **Handler/tap collections read on the UI thread must be COPY-ON-WRITE, not `List<T>`**
    (`SessionController`'s four tap arrays). Taps get registered from a driver's thread while the
    WebView2 handlers read them on the UI thread, and `List<T>.ToArray()` reads `_size` then copies
    the backing store — an `Add` in between throws or copies a torn view, and two concurrent `Add`s
    corrupt the list. A `.ToArray()` at the read site LOOKS like the fix for this and is not one:
    publish a fresh array under a lock (`volatile` field) so readers take no lock at all.
  - **Per-CONTROL, never per-application.** Sessions marshal to their anchor form and
    `SecondaryWindows` run their own STA pumps, so one app-wide dispatcher is wrong for both.
- **Serve the packaged bundle synchronously; defer dynamic schemes.** The virtual-host bundle is
  in-memory and includes the MAIN DOCUMENT — deferring it stalls the initial navigation ("stuck
  on start", production-only). Dynamic content (disk reads, remote fetch) served inline blocks
  the UI thread under request bursts (thumbnail grids) — those go through `GetDeferral` +
  `Task.Run`. Both paths exist in `WebViewHost` on purpose; don't unify them.
- **Both virtual-host mechanics stay supported** (`SetVirtualHostNameToFolderMapping` for
  disk-backed content, `WebResourceRequested` + provider for embedded bundles) — different
  sources proved each (see `extraction-sources`).
- **Static serving is CONTAINMENT-checked, in every method that touches the filesystem**
  (`EmbeddedResourceProvider.ResolveContained`, used by `GetResourceStream` AND `Exists` — `Exists`
  alone leaks existence). The host must unescape the request path (bundle filenames carry spaces and
  CJK characters), so `%2e%2e%2f` arrives as `../`; and `Path.Combine(root, "C:\…")` DISCARDS its
  first argument when the second is rooted. Reject rooted paths and `..` segments, then assert
  `Path.GetFullPath(combined)` still sits under `GetFullPath(root)` + separator — the separator
  matters or a sibling directory sharing the root's prefix passes. Responses carry
  `Access-Control-Allow-Origin: *`, so page script reads whatever comes back.
- **An async navigation policy CANNOT be enforced in `NavigationStarting`** —
  `CoreWebView2NavigationStartingEventArgs` has NO deferral (compiler-proven), and blocking on the
  policy would deadlock the UI thread the event runs on. So the division of labour is fixed: the async
  guard is a PRE-CHECK on the explicit navigate; what the event can add is a SYNCHRONOUS rule
  (`RenderSessionPool.WireNavigationPolicy` records the host the guard approved and cancels an
  unvetted CROSS-HOST hop, which closes `302 → 127.0.0.1`); and full redirect/subresource policy is
  `SessionBrowserOptions.RequestFilter`, already synchronous and wired with `WebResourceContext.All`.
  Do not "fix" this back into an awaited guard. Not applied to `InteractiveSession`: interactive OAuth
  legitimately redirects across hosts.
- **Guard init with a timeout** (`WebViewHostOptions.InitTimeout`, 25 s family default): an
  orphaned user-data-folder lock (zombie browser process) hangs `EnsureCoreWebView2Async`
  forever with no error. Fail loudly with the fix in the message.
- **Re-check cancellation AFTER a multi-second acquire, before publishing.** Browser init is seconds
  long (process spawn + profile attach), so a single check at the top of the marshalled body means a
  start cancelled during the expensive part still publishes a live off-screen window and a browser
  process holding the profile lock — with no owner left to dispose either, because the caller got a
  cancellation instead of a handle. Re-check and tear down (`RenderSessionPool.CreateInstanceAsync`,
  `StreamingSession.StartAsync`), reusing the failure path's cleanup so the two can't drift. And pass
  the LINKED token (caller + owner-dispose) into creation, not the caller's alone.
- **A health probe must FAIL CLOSED.** `ResetToBlankAsync` swallowed its own timeout and returned
  `true` unconditionally, which made the documented "a failed reset DISCARDS the instance" rule
  reachable only via a throw — an unresponsive renderer recycled forever. A renderer that can't
  answer `about:blank` can't answer the next caller either; report the real outcome, name WHICH
  invariant discarded the resource in the log, and put the decision in a seam the unit tests can
  reach (`AwaitResetNavigationAsync`) — the old test could only drive the override, which is exactly
  how this passed five phase reviews.
- **IF a POOLED object ever exposes a SUBSCRIBE api, it needs a disposal check as much as an operation
  does** — no leased type has one today, which is why this is a trap rather than a rule about live code.
  A subscribe installs a PERSISTENT tap, and after dispose the instance belongs to the next lease, so a
  late subscribe streams one caller's traffic to the previous one. Throw `ObjectDisposedException`
  (loudly, not a silent no-op) AND re-check inside the marshalled body, or the check-then-post race
  reopens it.
- **NEVER dispose a `SemaphoreSlim` (or its linked CTS) right after cancelling waiters on it.**
  `RenderSessionPool.Dispose()` cancelled the dispose CTS — correct, it wakes a lease queued on the
  capacity semaphore — and then immediately called `_capacity.Dispose()`. Disposing while a waiter is
  still unwinding its just-fired cancellation races the semaphore's internal queue-removal and can
  leave that waiter's task PERMANENTLY INCOMPLETE: the test hung for the full 10-minute harness
  timeout with no summary. A `SemaphoreSlim` only needs disposing if `AvailableWaitHandle` was touched
  (it never is here), so the fix is to not dispose it at all — the cancel alone wakes waiters cleanly.
  Bound such a regression test with `Task.WaitAsync(5s)` so a re-break FAILS instead of stalling the
  suite. (Earned in the P5 review; promoted from `FIX-LOG` to a rule in P5.5 H8.)
- **Root a CDP event receiver in a field for the subscription's whole life.**
  `GetDevToolsProtocolEventReceiver(...)` left in a local means nothing references the receiver once
  the method returns, so the subscription's survival relies on the SDK caching it internally —
  unspecified, and a stream that stops after an arbitrary GC reports NO error. Detach on dispose too.
- **Prewarm stays BEHIND the single-instance gate.** Environment creation takes the user-data
  OS lock; a losing second launch must never touch it (`PrewarmWebView2` registers a lifecycle
  hook, not an immediate call — keep it that way).
- **Caching policy: no-cache HTML, immutable hashed assets** (`WebViewContentTypes`, in `Shenora`
  since D45). The source served `index.html` immutable — stale bundle after every update.
- **The interceptor (D45) shares the page's origin with the bundle, and the ORDER is the invariant.**
  `WebViewHost` asks the bundle first (`WebViewBundleServing.TryServe`) and serves a hit SYNCHRONOUSLY —
  that is the main-document rule above, unchanged. A MISS falls through to the middleware pipeline
  instead of 404ing, which is the only reason a relative route (`https://app.local/media?…`) works at
  all. So: **keep interception routes off bundle paths.** A colliding route resolves differently on
  desktop (bundle wins) and mobile (middleware wins), because there the PLATFORM serves the bundle and
  only sees what the pipeline declined.
  ⚠ **In dev, the page is on the Vite origin and nothing filters it** unless you register it —
  `WebView2Interceptor.ExtraFilters`. Forget that and a route works packaged and 404s all through
  development. `"*"` is not the fix (it raises the event for every request the page makes, the open
  internet included), and a `ProductionUrl` origin is left alone on purpose: a real in-process server is
  behind it, and shadowing its routes means two servers for one origin.
- **Injected script values are JSON-serialized, never interpolated** (`WebViewScripts.
  BuildGlobalScript` — STJ's default encoder escapes `</script>` breakouts). New injection points
  must go through it.
- **Dev CDP args must be re-appended manually** — setting `AdditionalBrowserArguments` makes
  WebView2 ignore `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` (also in `windows-dev-gotchas`; the
  fix lives in `BrowserArguments.Build`).
- 🔴 **KEEP INTERCEPTION PATHS OFF BUNDLE PATHS**, on every shell. Bundle-versus-pipeline order is
  OPPOSITE between them — the desktop asks the bundle FIRST (deferring the main document stalls the
  initial navigation) and falls through on a miss, while on mobile the platform serves the bundle and the
  pipeline sees only what it declined. **A route that collides with a bundle path is relying on a
  difference between shells**, so it works on one and 404s on the other. ⚠ In DEV the page lives on the
  Vite server, so that origin must be filtered too, or a route works packaged and 404s all through
  development. (D45.)

## Gotchas / traps

- 🔴 **NEVER launch a WebView2 app under `timeout` — it manufactures a renderer CRASH.** GNU `timeout`
  puts the child in its OWN process group, and Chromium's renderer sandbox breaks inside one: the
  renderer dies with `0xC0000005` about 8 s in — **before any kill** — then the Storage Service, Network
  Service and GPU process follow, and the host's auto-reload makes the probes run twice. ⚠ **It fires
  ~50 % of the time** (6/12 under plain `timeout`, 0/9 launching directly, 0/3 with `--foreground`),
  which is why single-run elimination cannot find it — `phase-workflow.md` has that method lesson. It is
  also the likeliest source of a reported `0x800700AA`. **To bound a sample run, spawn it from a
  `devtools/_*.mjs` script and `p.kill()` on a timer**, or pass `--foreground`. The redirect is innocent.
- **WebView2 + CDP:** setting `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` makes
  WebView2 IGNORE the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` env var — a dev-mode host must
  re-append the env var's value itself or the devtools CDP loop silently gets no port. (Proven in
  two sibling apps; keep the fix in the browser-arguments builder.)
- Desktop verification without CDP: `dev.mjs input list` / `click|rclick|move|drag <fx> <fy>…` post
  background mouse messages to the WebView2 render surface (no focus steal, works occluded);
  `shot`/`wgc` capture the window (WGC works even when hidden). Target process comes from
  `devtools/project.config.mjs` (`processName` → `DEVTOOL_PROC`).

- **Custom schemes do NOT need `CoreWebView2CustomSchemeRegistration` for the deferred-serving
  path.** The primary source app registers none, yet serves `app://`/`proxy://` subresource
  loads (`<img src>` thumbnails) through `AddWebResourceRequestedFilter` + `WebResourceRequested`
  in production daily — interception fires before the network stack rejects the unknown scheme.
  Registration only matters for full web-platform semantics on the scheme (fetch/CORS/service
  workers), which no family app uses; add a registration option only when a consumer proves the
  need (a phase-review subagent once flagged this as broken from the docs alone — the live app
  is the counter-evidence).

- MSBuild manifest names collapse directory separators AND filename dots to `.` — an embedded
  path can't be reconstructed from its name. `EmbeddedResourceProvider` therefore maps
  path→name (deterministic), never name→path (the source's direction mis-served any dotted
  filename). Directory names with invalid identifier chars (hyphens) get mangled by MSBuild —
  use `LogicalName` metadata if a bundle ever needs them.
- No default dev port ships (`WebViewHostOptions.DevUrl` is required in dev): every family app
  picks a unique Vite port so parallel dev sessions of siblings can't collide. Don't "helpfully"
  default it.
