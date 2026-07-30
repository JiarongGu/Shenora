# WebView2 hosting invariants — the measured rules the host encodes

The family's WebView2 lessons, earned live and now enforced by `src/Shenora.WebView2/`
(`WebViewEnvironment`, `WebViewHost`, `EmbeddedResourceProvider`). Read before touching hosting,
serving, or session code (incl. the P5 sessions package) so a refactor doesn't undo a fix.

## The rules

- **`CoreWebView2Environment` is thread-affine.** Only the main UI thread uses the shared/
  prewarmed environment (`WebViewEnvironment.GetSharedAsync`); a window on its own STA thread
  MUST create its own on that thread (`CreateForCurrentThreadAsync`; same options + user-data
  folder ⇒ one shared browser process). Mixing threads throws — it broke every secondary window
  in the source app.
- **Cache an environment per PROFILE, scoped to the owner that opened it — never process-globally**
  (`SessionEnvironmentCache`, held by `RenderSessionPool`). Two forces meet here and only owner
  scoping satisfies both: a live environment keeps its profile's browser process AND the folder's OS
  lock alive, so a process-lifetime cache makes `LoginWindow.ClearProfile` (what makes a logout a
  REAL logout) fail every time instead of only while a window is open; and thread affinity above
  means a global cache would need a thread key plus a lock, while an owner marshals to one anchor and
  is single-threaded by construction. Cache the IN-FLIGHT task, not just the result — `WaitAsync`
  abandons the *await*, never `CreateAsync`, so without that a retry against a locked profile
  spawns a SECOND browser process onto the lock the timeout's own message blames.
- **Never cache a FAULTED environment task.** One transient failure becomes terminal for the process
  (still open in `WebViewEnvironment`, `TASKS.md` H3; deliberately not copied into the sessions
  cache). Evict on observing a faulted/cancelled entry so the next attempt genuinely retries.
- **Everything on `CoreWebView2` is UI-affine — marshal through the ONE owner, never hand-roll a
  `BeginInvoke`.** Post-D19/D20 the seam is `IUiDispatcher` (`Shenora.Core`) implemented once as
  `WinFormsUiDispatcher(Control)` (`Shenora.WinForms`); `Shenora.WebView2` and
  `Shenora.WebView2.Sessions` consume it through the sanctioned downward edge. This rule exists
  because hand-rolling produced **14 copies with 5 incompatible pre-handle policies** and real
  defects. Why the owner is shaped as it is — all four are invariants, not preferences:
  - **`IsHandleCreated` BEFORE `InvokeRequired`.** Pre-handle, `InvokeRequired` lies (false on a
    pool thread), so "no handle" must never be mistaken for "already on the UI thread" and run the
    WebView2 call off-thread. A deferred `WebResourceRequested` response must be BUILT on the UI
    thread; no handle ⇒ complete the deferral empty (`WebViewHost.ServeDeferred`).
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
  - **There is ONE guard and it is `Shenora.Core.AppCallback`** (`Run`/`RunOrDefault`) — not a
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
  Do not "fix" this back into an awaited guard. Not applied to `LoginWindow`: interactive OAuth
  legitimately redirects across hosts.
- **Guard init with a timeout** (`WebViewHostOptions.InitTimeout`, 25 s family default): an
  orphaned user-data-folder lock (zombie browser process) hangs `EnsureCoreWebView2Async`
  forever with no error. Fail loudly with the fix in the message.
- **Re-check cancellation AFTER a multi-second acquire, before publishing.** Browser init is seconds
  long (process spawn + profile attach), so a single check at the top of the marshalled body means a
  start cancelled during the expensive part still publishes a live off-screen window and a browser
  process holding the profile lock — with no owner left to dispose either, because the caller got a
  cancellation instead of a handle. Re-check and tear down (`RenderSessionPool.CreateInstanceAsync`,
  `CoBrowseSession.StartAsync`), reusing the failure path's cleanup so the two can't drift. And pass
  the LINKED token (caller + owner-dispose) into creation, not the caller's alone.
- **A health probe must FAIL CLOSED.** `ResetToBlankAsync` swallowed its own timeout and returned
  `true` unconditionally, which made the documented "a failed reset DISCARDS the instance" rule
  reachable only via a throw — an unresponsive renderer recycled forever. A renderer that can't
  answer `about:blank` can't answer the next caller either; report the real outcome, name WHICH
  invariant discarded the resource in the log, and put the decision in a seam the unit tests can
  reach (`AwaitResetNavigationAsync`) — the old test could only drive the override, which is exactly
  how this passed five phase reviews.
- **A subscribe API on a POOLED object needs a disposal check as much as an operation does.**
  `RenderSession.OnNetwork`/`OnMessage` install a persistent tap, and after dispose the instance
  belongs to the next lease — so a late subscribe streamed another lease's API responses and posted
  messages to the previous caller. Throw `ObjectDisposedException` (loudly, not a silent no-op) AND
  re-check inside the marshalled body, or the check-then-post race reopens it.
- **Root a CDP event receiver in a field for the subscription's whole life.**
  `GetDevToolsProtocolEventReceiver(...)` left in a local means nothing references the receiver once
  the method returns, so the subscription's survival relies on the SDK caching it internally —
  unspecified, and a stream that stops after an arbitrary GC reports NO error. Detach on dispose too.
- **Prewarm stays BEHIND the single-instance gate.** Environment creation takes the user-data
  OS lock; a losing second launch must never touch it (`PrewarmWebView2` registers a lifecycle
  hook, not an immediate call — keep it that way).
- **Caching policy: no-cache HTML, immutable hashed assets** (`WebViewContentTypes`). The source
  served `index.html` immutable — stale bundle after every update.
- **Injected script values are JSON-serialized, never interpolated** (`WebViewScripts.
  BuildGlobalScript` — STJ's default encoder escapes `</script>` breakouts). New injection points
  must go through it.
- **Dev CDP args must be re-appended manually** — setting `AdditionalBrowserArguments` makes
  WebView2 ignore `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` (also in `windows-dev-gotchas`; the
  fix lives in `BrowserArguments.Build`).

## Gotchas / traps

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
