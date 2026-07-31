# TASKS.md — pending backlog only

Pull the next item when between tasks. When an item is DONE: record it in `docs/ROADMAP.md`
`## Done` and REMOVE it from here — this file holds only what's still pending.
`> DIRECTION (user):` blockquotes capture the user's steering verbatim.

> DIRECTION (user, 2026-07-30): Shenora is the shared infrastructure library for ALL sibling
> projects — a "UI kit for non-web applications" in the headless sense: it holds the desktop
> shell that different applications boot their own logic on, and it must NOT depend on any UI
> component library. Purpose is to stop re-solving the same problems per project. In-scope
> common work explicitly includes: multi-form/multi-window, co-browsing (auxiliary browser
> sessions), drag-drop zones, the IPC package design, the event hub, frontend display
> optimizations, and the React hooks layer.
>
> DIRECTION (user, 2026-07-30, later): growth is harvest-driven — when something nice emerges
> while developing another application, it gets generalized and promoted into Shenora (common
> design/library/tool sharing). And the kit must be able to adopt MOBILE application logic too:
> Capacitor (and similar) shells speaking the same IPC envelope through a pluggable transport.

## TODO

### P5.5 — Consolidation: cleanup, re-layer, roadmap revisit (2026-07-30) — DO BEFORE P6

The consolidation checkpoint after P0–P5 put the whole body of the kit down fast (see
`docs/ROADMAP.md` `### P5.5` for the phase's framing and the P6/P7 revisit that came with it). Three
strands: **cleanup** (this list), **re-layer** (H4.1, D19+D20), **roadmap revisit** (done — in
ROADMAP).

The cleanup list came from the first full P0–P5 review (six parallel reviewers over all five
packages + the npm client + the tree; `docs/REVIEW-GUIDE.md` was the brief). Baseline was green
(`verify` PASSED at `130d4cd`), so every item below is a latent defect, not a regression — the
product of velocity, not of breakage. Findings are grouped as fix batches, ordered
by leverage; `file:line` anchors are from `130d4cd`. H1–H3 are things a consuming app cannot work
around, H4 removes the duplication that CAUSED several of them, H5 closes the gate that let them
through, H6–H7 are pre-1.0 surface and consistency.

**EXECUTION ORDER (decided 2026-07-30 with the re-layering design — do NOT just run H1→H8):**
1. **H1 + H5** on the CURRENT layering — security fixes + gate holes. Surgical, no structural churn;
   a path-traversal fix must not wait behind a refactor.
2. **The re-layer** (H4.1, its own commit) — `docs/2026-07-30-shenora-relayering-design.md`, D19+D20.
3. **H4.2–H4.7** dedup on top — mechanical once the single owner exists.
4. **H4.6 + H9** — the neutral session controller and the co-browse primitives/hooks redesign (D21).
   Together, because H9.4 needs H4.6's base, and both are pre-1.0 breaking changes to the same types.
5. **H2 / H3 / H6 / H7** — several H2 items are marshal-related and DISSOLVE into step 2; re-check
   them against the new code rather than fixing them twice. Note H9.3 subsumes H2's co-browse
   "root the screencast receiver / complete the channel" items — do them once, there.

Standing rule for this phase: each batch ends with `dev.mjs verify` + a regression test per fixed
defect, and every earned invariant lands in `.claude/knowledge/` (see H8) rather than only in a
code comment.

**H1 — Security / data-integrity (do first; two are reachable by content the app doesn't control)**

- [x] **Path containment in file-mode serving.** `WebViewHost.cs:199` unescapes the request path,
  then `WebViewResourceProvider.Normalize:180` only does `\`→`/` + `TrimStart('/')` — no `..`
  rejection, no containment — before `Path.Combine(root, …)` at `:125` (and `:161` `Exists`). Two
  live vectors: `%2e%2e%2f…` → `../`, and a ROOTED path (`/C:%2f…`) which `Path.Combine` returns
  outright. Responses carry `Access-Control-Allow-Origin: *`. Active whenever `PreferFiles` is on —
  the sample derives it from `IsDevelopment`, so every dev session + any file-mode deployment. FIX:
  reject rooted/`..` paths and assert `Path.GetFullPath(combined).StartsWith(fullRoot + sep)` in
  BOTH methods; tests for `%2e%2e%2f`, `C:%2f`, and a legitimate CJK/spaced filename (the unescape
  exists for those — don't regress it).
- [x] **Enforce `NavigationGuard` in `NavigationStarting`, not only on the explicit call.** Checked
  only at `RenderSession.cs:59` / `LoginWindowController.cs:142`; the package's sole
  `NavigationStarting` subscription (`LoginWindowController.cs:94`) just fans out to app taps and
  never cancels. So a guard-approved URL answering `302 → http://127.0.0.1:8080/admin` is followed
  and its DOM handed to the caller — the documented SSRF boundary doesn't hold for redirects,
  `location.href`, `<meta refresh>`, or iframes. FIX: cancel in `NavigationStarting` (covers
  `e.IsRedirected`); keep the per-call check as a fast pre-check. Same wiring for pool instances.
  **DONE, but the fix had to be ADAPTED — read this before "finishing" it:**
  `CoreWebView2NavigationStartingEventArgs` has **no deferral** (proven by compiler error while
  implementing the obvious version), so the async guard CANNOT be awaited in that event and blocking
  on it would deadlock the UI thread it runs on. What shipped: the pool records the host the guard
  approved (`PoolInstance.ApprovedHost`, cleared on return-to-pool) and cancels unvetted CROSS-HOST
  navigation synchronously — which closes the documented `302 → 127.0.0.1` vector while leaving
  same-host hops working. Full redirect/subresource policy remains `SessionBrowserOptions.RequestFilter`
  (already synchronous and wired with `WebResourceContext.All`); both options now document the
  division of labour. Deliberately NOT applied to `LoginWindow` — interactive OAuth legitimately
  redirects across hosts, so cancelling unvetted hops there would break real logins.
- [x] **Guard the outgoing notification serialize.** `WebViewIpcBridge.TryBuildBatchJson:278-293`
  DRAINS the queue then calls `IpcJson.Serialize` with no try/catch, reached from `Flush` ← the
  50 ms timer. An app event carrying a cyclic graph, a `Type`/delegate member, or a throwing getter
  → unhandled UI-thread exception (crash dialog under the family bootstrap) AND the whole drained
  batch is lost. The INCOMING path already guards this exact case with a comment — copy it
  (per-notification, so one bad event can't kill its batch) + a catch-all in `Flush`.
- [x] **Contain the profile path that `ClearProfile` deletes.** `LoginWindow.cs:295-306` is an
  unbounded `Directory.Delete(recursive: true)` on a caller-composed path, while the same options
  doc calls per-(provider, sub) scoping a security boundary and describes provider definitions as
  data-driven. A `..` segment merges two accounts onto one cookie jar or aims the delete outside the
  sessions root. FIX: a compose helper that rejects separators/`..`/reserved names + resolve-and-
  contain before deleting.
- [x] **Dispose the leaked process handle** at `WebViewHost.cs:324` — `ShellLauncher.cs:69-72` has
  the Win11 `?.Dispose()` lesson; the WebView2 copy of the same open-in-shell code omits it, so
  every external link click from the page leaks a `Process`.

**H2 — Hangs, crashes and lifetime (a consuming app cannot work around these)**

- [x] **`RenderSession` must observe the tokens it accepts.** DONE across two batches. H4.2 routed the
  marshal through `WinFormsUiDispatcher`, whose `InvokeAsync` observes the token via `WaitAsync`, so the
  CALLER always escapes. This batch added the half that actually frees the pool:
  `RenderSession.RunBoundedAsync` caps every marshalled op at the new
  `RenderSessionPoolOptions.OpTimeout` (60 s) and POISONS the instance when the body never completed,
  so `Return` discards it instead of re-pooling a wedged page. Two judgement calls worth reading:
  (a) "never completed" is TRACKED (a flag set in the body's `finally`), not inferred from the
  exception — a body that ran and threw (a rejected URL, a guard refusal) leaves a perfectly reusable
  instance, and discarding it would cost a browser startup on every ordinary error; (b) a CALLER
  cancellation also poisons, deliberately — the caller walked away while the op was outstanding, so
  the renderer may still be mid-script and handing that page to the next lease is the real risk. The
  expiry surfaces as `TimeoutException`, but a caller's own `OperationCanceledException` is never
  rewritten. `NavigateAsync`'s hardcoded 30 s cap became `NavigationTimeout` so the two budgets are
  coherent (`OpTimeout` must exceed it, documented on the option).
- [x] **Suppress script dialogs on session browsers.** DONE in H4.4 (`AreDefaultScriptDialogsEnabled = false`). `SessionBrowser.cs:112-120` leaves
  `AreDefaultScriptDialogsEnabled` true while `OffscreenWindow` parks the host off-screen at
  opacity 0 — an `alert()` blocks the renderer behind a dialog nobody can see or dismiss, which
  compounds the item above.
- [x] **Unclosable login modal.** DONE — `Finish()`+`Close()` moved first, app callback guarded. `LoginWindow.cs:274` finally order is
  `fallback.Dispose(); OnLoading?.Invoke(false); controller?.Finish(); form.Close();` — `OnLoading`
  is app code, so a throw (splash already disposed) escapes the `async void` handler, `Finish()`
  never runs, and the foreground `FormClosing` handler (`LoginWindowController.cs:67-72`) then
  cancels EVERY close including the user's and `Application.Exit`; `ShowDialog` never returns and
  the busy gate stays set. FIX: try/catch the callback; `Finish()`+`Close()` FIRST. Same for
  `:234` and the posted body behind `SetLoading` (`LoginWindowController.cs:239`).
- [x] **The frameless-maximize ⇄ window-state seam (live in the reference composition).** DONE via the
  new `IAppMaximizable` seam (`OptimizedForm` implements it; `WindowStateManager.Save`/`Apply` prefer
  it over `Form.WindowState`/`RestoreBounds`), + 4 regression tests. The `MinimumSize` clobber below
  was fixed in the same pass.
  `WindowStateManager.Save:60-61` reads `form.WindowState`, but frameless `OptimizedForm.Maximize()`
  (`:142-157`) only sets `_maximized` (pinned: `OptimizedFormTests:91` asserts `Normal`). Closing
  maximized persists `maximized:false` WITH the work-area rect as normal bounds → next launch fills
  the work area believing it is not maximized: `WM_NCCALCSIZE` takes the normal-inset branch (the
  border gap the whole technique removes), the page's glyph is wrong, and clicking maximize captures
  the work-area rect as `_restoreBounds` so RESTORE IS A PERMANENT NO-OP. FIX: an app-maximized
  seam (`IsAppMaximized` + app restore bounds) that `Save`/`Apply` prefer over
  `Form.WindowState`/`RestoreBounds`.
- [x] **`AddMessageDispatcher` DI recursion → StackOverflow, no diagnostic.** DONE —
  `MapRegisteredModulesLazily` + a duplicate-module guard. NOTE the honest asymmetry, documented at the
  site: the eager `MapRegisteredModules` throws at composition, but the lazy path can't detect a
  duplicate until the first dispatch, and `DispatchAsync` never throws by contract — so there it is a
  logged error response. "Diagnosable", not "fails at startup".
  `IpcServiceCollectionExtensions.cs:49-55` enumerates facades (`sp.GetServices<IModuleFacade>()`)
  INSIDE the `IMessageDispatcher` singleton factory. Any facade whose graph injects
  `IMessageDispatcher` — the documented cross-module `SendAsync` seam — re-enters the same factory;
  MS DI's cycle detection is call-site-based and cannot see a factory delegate re-entering the
  provider, and the cache entry isn't published yet → unbounded recursion, process death. FIX: map
  facades lazily (terminal middleware over a `Lazy<IModuleFacade[]>`) so the singleton is cached
  before enumeration; test the exact composition (`class F(IMessageDispatcher) : BaseFacade`).
- [x] **`app.Dispose()` throws on a clean quit** when any singleton is `IAsyncDisposable`-only
  (`ShenoraApplication.cs:46,132`; MS DI throws for async-only captured disposables). Latent against
  Shenora's OWN `RenderSession`/`CoBrowseSession`. FIX: add `IAsyncDisposable` → `_provider.DisposeAsync()`.
- [x] **Absolutize the resolved root/data paths** in `ShenoraPaths.Resolve`/`ResolveRoot:90-101`
  (returned verbatim today). `FileDialogs` sets `RestoreDirectory = false` on all three dialogs
  (`:146,174,218`, deliberate), so the process CWD moves after the first dialog and a relative
  `--app-root` re-resolves `DataDir` mid-session; it also defeats `SingleInstanceGuard.ChannelKey`
  hashing (two spellings of one install → two instances over the single-writer WebView2 folder).
- [x] **A cancelled lease cannot escape DURING browser init — DONE in H9.6 (2026-07-31)**, exactly as
  planned: bundled with making the statics internal, since it is the same signature.
  `SessionBrowser.InitializeAsync` now takes a `CancellationToken` passed to BOTH `WaitAsync` calls and
  wired from the render pool and the streaming session, so a cancelled lease escapes during init instead
  of waiting out `InitTimeout` twice. The token gates the AWAIT only — never the creation — because the
  environment task is shared across the pool's instances. Original note follows.
  (the H2 sessions batch
  closed the "publishes a live browser" half; this is the promptness half). `SessionBrowser.InitializeAsync`
  takes no `CancellationToken` at all, so a cancelled `LeaseAsync` waits out `InitTimeout` (up to 2×25 s)
  before the new post-init check fires. Deliberately NOT expanded into that batch: adding the parameter
  is a public-surface change, and H6 proposes making these statics internal anyway — do both in one
  move there. Note the token must gate the AWAIT only, never cancel the creation itself: the
  environment task is now SHARED across a pool's instances (`SessionEnvironmentCache`), so cancelling it
  for one caller would break the others.
- [x] **No app callback runs unguarded inside a WebView2/WinForms event handler.** DONE. The structural
  answer is **one owner** — `Shenora.Core.AppCallback` (`Run` / `RunOrDefault`, public per the D19/D20
  placement law because three packages consume it) — rather than a try/catch remembered per site. What
  it closed, in landing order:
  - H4.2: `WindowCommandFacade` (SET_THEME's `ApplyTheme`, CLOSE's `FormClosing`) and `DropZoneManager`
    post through the guarded dispatcher; `LoginWindow`'s `OnLoading` guarded (see the modal item).
  - The H2 sessions batch: an **`ILogger` is app code too**, and H4.7's logging invoked it bare at all
    eight sites in that package — one throw escaped before `tcs.TrySetException` (hung lease, permit
    held), one before `_capacity.Release()`. Found by that batch's own phase review.
  - This batch: `WebViewHost`'s three app policy hooks
    (`OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed`) — and note the fix is not just
    "don't crash": a failed hook now **falls back to the kit's built-in policy**, because leaving the
    event unanswered is its own bug (an un-cancelled download proceeds, an unanswered permission
    request stalls its caller, a renderer crash goes unhandled exactly when things are already wrong).
    `OptimizedForm.WndProcHook` reads a throwing hook as "did not handle this message", so the window
    keeps working. And every `Action<string>? Log` site in `WebViewHost` + `WebViewIpcBridge` became a
    guarded, LAZY `Log(Func<string>)` — lazy because the guard must cover BUILDING the message too
    (several read WebView2/COM properties that throw once the object is gone), and because several sit
    inside a `catch` that exists to stop a failure escaping, where a throwing sink defeats the very
    thing it reports from.
  - `SessionController`'s four tap lists are now COPY-ON-WRITE arrays published under a lock. This was
    a genuine data race, not a style point: taps are registered from the driver's thread while the
    WebView2 handlers read them on the UI thread, and `List<T>.ToArray()` reads `_size` then copies the
    backing store — an `Add` in between throws or copies a torn view, and two concurrent `Add`s corrupt
    the list. Readers now need no lock at all.
- [x] **Pool reset must fail closed.** DONE — `AwaitResetNavigationAsync` (internal, so the REAL path
  is unit-testable; the old test could only drive `ResetOverride`, which is exactly why this survived
  five reviews) returns the navigation's actual outcome, and the 5 s budget became the validated
  `ResetTimeout` option. It swallowed the `WaitAsync` outcome and returned `true` unconditionally,
  reasoning in a comment that "the next lease navigates away regardless" — it does not: a renderer that
  can't answer a navigation to `about:blank` can't answer the next lease's either. So the documented
  "a failed reset DISCARDS the instance" invariant was reachable only via a THROW.
- [x] **Re-check cancellation after the multi-second init** DONE in `RenderSessionPool.CreateInstanceAsync`
  (its failure cleanup became a shared `TearDown()` local, now used by the cancelled path too and no
  longer silent) and at TWO points in `CoBrowseSession.StartAsync` — after init and again before
  publishing, since past that line the caller owns teardown. `LeaseAsync` now passes `linked.Token`
  (caller + pool-dispose) to the factory instead of the raw caller token, so disposing the pool
  mid-creation cancels the creation rather than letting it publish a live off-screen window whose
  browser process then holds the profile lock with nothing left to dispose it.
- [x] **Root the CDP screencast receiver.** DONE — the receiver AND its handler are fields
  (`_frameReceiver`/`_onFrame`), and `DisposeAsync` detaches before stopping the screencast. It lived
  only in a local, so the frame stream depended on the WebView2 SDK caching the receiver internally —
  unspecified behaviour, and a stream that stops after an arbitrary GC reports no error at all.
- [x] **`RenderSession.OnNetwork`/`OnMessage` don't check `_disposed`.** DONE — both throw
  `ObjectDisposedException` (matching every other member, via `OnUiAsync`) and the posted subscribe
  body re-checks, closing the check-then-post race. They were the only public members without a
  disposal check and the only two that install a PERSISTENT tap, so a late subscribe streamed the next
  lease's API responses and posted messages to the previous caller's handler.
- [x] **WinForms robustness tail — DONE**, all nine items, with `winforms-shell.md` (H8) capturing the
  invariants. Two judgement calls worth reading: (a) the form-level `AllowDrop`/`DragOver` was
  **removed outright** rather than option-gated, because the premise behind it was false — a drop target
  is registered PER HWND and `DropZoneOverlay` registers itself, so nothing ever needed the form's drag
  events; all it did was force OLE/STA on every consumer of the base class and show a copy cursor for a
  drop it then silently discarded (the existing test asserting `AllowDrop == true` carried that false
  premise in its comment). (b) `TrayIcon`'s wrong comment was fixed as DOCUMENTATION, not code:
  `CloseReason` genuinely cannot distinguish the user's X from a programmatic `Close()`, so the fix is
  telling adopters to close via `ExitApplication()`/`Application.Exit()` — now on the `CloseToTray`
  option itself, where the decision is made. Also landed: `OptimizedForm` re-fills on `WM_DPICHANGED` +
  `DisplaySettingsChanged` (with the `SystemEvents` unsubscribe that a static publisher demands) and
  validates its restore rect through `WindowStateManager.IsVisible` rather than a second opinion on
  "off-screen"; `WinFormsBootstrap` asserts STA with the fix in the message, is idempotent, and its
  crash dialog is one-at-a-time per thread (new internal `ShowDialogOverride` seam, since a real
  MessageBox would hang the suite and the re-entrancy IS the invariant); `SecondaryWindows` cleans up
  only after `Application.Run` returns, removes the entry on a failed `thread.Start()`, and replays a
  pre-handle `Activate`; `SingleInstanceGuard.TryAcquire` is idempotent; `SetTextAsync("")` clears.
  NOT tested, deliberately: the clipboard fix — a test would clobber the developer's real clipboard.
  Original list: STA assertion + idempotence in `WinFormsBootstrap.Initialize:65-88`
  (a missing `[STAThread]` currently surfaces as a blocking dialog inside handle creation; a second
  call double-registers all three exception channels); re-entrancy guard on the last-resort crash
  dialog (`:103-121` — `MessageBox.Show` pumps, so a repeating UI-thread exception stacks dialogs
  unboundedly); drop the unused form-level `AllowDrop`/`DragOver` (`OptimizedForm.cs:99-103` — no
  `src/` code subscribes to the form's drag events, it makes handle creation OLE/STA-dependent, and
  with no `DragDrop` handler it shows a copy cursor then silently discards the drop) or put it
  behind an option; `TrayIcon.cs:150-156`'s comment is factually wrong — WinForms reports
  `UserClosing` for a programmatic `Form.Close()` too (the repo's own `TrayIconTests:73` asserts the
  cancellation), so with `CloseToTray=true` a startup-abort `Close()` hides the window and leaves a
  resident process; `SecondaryWindows` registry/wait fixes (`:104` removes the entry on `FormClosed`
  so `Dispose` returns before `Application.Run` finishes tearing down a WebView2 window → stale
  profile lock; `:78-85` a failing `thread.Start()` leaves a permanent phantom entry; `:148-158`
  `Activate` is silently dropped pre-handle, which is the documented "`Open` activates the existing
  one" path); `WindowStateManager.Apply:29` unconditionally overwrites an app-set `MinimumSize`
  (the sample's `MainForm.cs:43` is already dead code); `SingleInstanceGuard.TryAcquire:90-118` is
  not idempotent (a second call leaks handle 1 and breaks the fast `--restarted` handoff);
  `ClipboardService.SetTextAsync("")` throws (`:32-40`) where a no-op/clear is meant;
  `OptimizedForm` manual maximize has no DPI/display-change handling, so `_restoreBounds` (raw
  physical px) goes stale across a monitor move and the fill is never refreshed.
- [x] **Client-side robustness tail (`@shenora/react`) — DONE**, all seven items, +10 vitest tests.
  Notes worth keeping: (a) `useDropZone`'s dead-zone bug needed the REF'S CONTENT made reactive, not a
  dep-array tweak — a `RefObject` is a stable object and a ref mutation triggers no render, so the fix
  is a `useState` element mirrored by a deliberately dep-array-less effect (`setElement` with an
  unchanged value is a React no-op, so it can't loop); the API stayed as it was. (b) `BaseModuleService`
  now resolves the bridge through a `protected get bridge` rather than a constructor default —
  subclasses keep using `this.bridge` unchanged, and an explicitly-passed bridge is still honoured
  (tested, because lazy resolution silently ignoring it would break the multi-transport case).
  (c) The fallback timeout only races a THENABLE — a plain value has already settled and must not be
  made async. (d) `useShenoraQuery` now keeps previous data alongside the error, so the caller chooses
  between stale-with-banner and hiding it. (e) The `debounce`/`randomUUID` helpers H4.5 deferred moved
  into a new non-exported `internal.ts` — a second consumer finally justified the shared home
  (`useWindowMaximized` needed the same debounce), which is the trigger H4.5 said to wait for.
  Original list: a host message of literal `null` throws an
  uncaught page error (`bridge.ts:186-192` — `JSON.parse('null')` then `parsed.category`);
  `BaseModuleService` captures the bridge eagerly (`moduleService.ts:26`), so a later
  `configureBridge` permanently kills every service built before it — resolve inside `send` the way
  `useDropZone` already does; `useDropZone` never registers a zone whose element isn't mounted on
  the first effect (`:139-141,201` — deps are `[enabled, targetRef]`, a stable ref object), so any
  conditionally-rendered target is silently dead — key the effect on the element;
  `useWindowMaximized` fires one un-debounced IPC round-trip per `resize` event (`:76-93` ≈ 180
  calls per 3 s drag, each with a 30 s timer) — reuse the debounce helper; `useShenoraQuery` blanks
  good data on a failed refetch (`hooks.ts:86`); `bridge.isAvailable` ignores `disposed` (`:87-89`);
  the `fallback` path bypasses the timeout entirely (`:120-127`).

**H3 — The notification/ready gate and validation — DONE (2026-07-30)**

- [x] **The ready gate has exactly one re-arm path.** DONE — the gate now closes on **`ContentLoading`**
  (a new document really is loading) and on **`ProcessFailed`**, instead of on every
  `NavigationStarting`. That event fires for navigations that never replace the document — one an app
  tap or a policy CANCELS, one that fails before committing — and the surviving page has already spent
  its single `READY`, so the gate closed FOREVER: buffer to 10 000, then silently drop-oldest, for the
  process lifetime. The bridge watches `ProcessFailed` ITSELF rather than relying on the host's
  auto-reload policy, which is optional. **The trade is stated at the site:** between
  `NavigationStarting` and `ContentLoading` the gate is still open, so a flush tick there delivers to
  the OUTGOING page rather than buffering for the incoming one — which is the better outcome, since
  those listeners are still attached and these are progress/status notifications.
- [x] **Validate the numeric options nobody validates.** DONE, all six:
  `MaxQueuedNotifications` (< 1 rejected — 0 made `Enqueue` dequeue what it had just enqueued, so
  EVERY notification vanished for the process lifetime with no error), `NotificationInterval`
  (< 1 ms, and > int32 ms — the WinForms timer's real limit), `SessionBrowserOptions.InitTimeout`
  (non-positive made init fail instantly with the profile-LOCK diagnosis, sending the caller after a
  zombie process that does not exist), `RenderSessionPoolOptions.OffscreenClientSize` (0×0 viewport),
  and `ScopedContainerRouterOptions.ConfigureScope` (`required` forces the caller to WRITE the
  initializer, not to write a non-null value — an explicit null surfaced as an NRE from inside scope
  creation, reported to the client as `UNKNOWN_ERROR`). The ROOT-provider caveat is documented on
  `ConfigureScope` itself: `AddScoped` there behaves as a per-scope SINGLETON, which is the opposite of
  what it means everywhere else in MS DI.
- [x] `WebViewHost.InitializeAsync` idempotence + one whole-sequence budget. DONE — the first call does
  the work and later calls await the same task; a FAILED init clears the cache so a retry is a real
  retry. It used to re-run `WireEventPolicies` on every call, double-subscribing every handler: each
  external link then opened TWICE and the auto-reload raced itself. The `InitTimeout` now covers the
  whole sequence through one linked CTS (each step used to get its own full budget, so "25 s" was
  really 50 s before `ApplySettings`, and script injection — a real browser round-trip — was
  unbounded). `WebViewEnvironment.GetSharedAsync` no longer caches a FAULTED task: `??=` made one
  transient failure terminal for the process, so the retry its own timeout message advises got the
  original exception back without touching WebView2 again.
- [x] A mistyped `ResourcePrefix` degrades to a silent all-404 provider. DONE, **but NOT where the
  review said** — read this before "improving" it. Throwing from the provider's constructor was the
  obvious fix and is wrong: a provider with nothing to serve is legitimate when the page loads from a
  dev URL, which is the normal state of a fresh clone whose bundle has not been built (the sample's own
  csproj documents exactly that). So the provider REPORTS it (`CanServe` + a log notice naming the bad
  prefix and the assembly's actual manifest prefixes), and the loud failure lives in
  `WebViewHost.AssertBundleServable`, which is the only place that knows the bundle IS the start
  document. The probe is `IWebViewResourceProvider.Exists("index.html")` — which also gives that member
  the consumer H6 was going to delete it for.
- [x] Don't put exception text in HTTP response bodies readable by page script. DONE — one constant
  `NotFoundBody` for every 404 and the diagnosis to the host log. Applies to all three sites (bundle
  miss, bundle failure, deferred-scheme handler failure); the last is the worst, since an app scheme
  handler's message is the most likely to carry a real path or a remote URL.
- [x] Cap the renderer auto-reload. DONE — new `MaxAutoReloads` (3) is the TERMINAL state the option's
  own doc already promised ("a crash-looping page must not spin"); rate-limiting alone is not a
  stopping condition, so a page that faults during load reloaded every 10 s forever, burning a browser
  process each time. The give-up is logged EXACTLY once, or the log becomes the new spin. A successful
  navigation resets the count, so a long-running app isn't rationed by unrelated crashes hours apart.
  `AutoReloadCooldown` moved from a public static field on `WebViewHost` to an option (**breaking**).

**H4 — The re-layer, then the dedup collapse (this duplication CAUSED several H1–H3 items)**

DECIDED 2026-07-30 (supersedes the "two internal owners + `InternalsVisibleTo`" idea the review
proposed): the shared owner problem is solved structurally by re-layering. Design:
`docs/2026-07-30-shenora-relayering-design.md`; rationale: **D19** (`Shenora.WebView2` →
`Shenora.WinForms`; the two Windows packages are one layer, boundary = primitives →
hosting-on-primitives) + **D20** (portable contracts + `IUiDispatcher` in `Shenora.Core`, so app
logic compiles with no Windows reference and a future mobile shell can implement the same
contracts). The design-contract §4 rule authorised this revision on exactly this evidence.

- [x] **H4.1 — Land the re-layer (own commit, before the dedup items below).** Take the
  `WebView2 → WinForms` project reference; move the portable contracts to `Shenora.Core`
  (`IClipboardService`, `IFileDialogs`/`IFileDialogPathStore` + `FileDialogOptions`/`FileDialogFilter`/
  `FileDialogResult` — platform-neutral in signature, but this is a file **SPLIT**: every one of them
  is declared inside its implementation's file, and `FileDialogs.cs` holds six of them plus the
  `FileDialogsOptions` that must stay behind); split the two mixed interfaces into a portable base +
  Windows extension
  (`IUrlLauncher` ← `IShellLauncher`, `IUiInteraction` ← `IFormInteraction`) so one implementation
  still satisfies both; add `IUiDispatcher` to Core plus TWO implementations in `Shenora.WinForms` —
  `WinFormsUiDispatcher(Control)` (explicit/per-control, what WebView2 + Sessions construct) and
  `MainFormUiDispatcher(IFormInteraction)` (the DI singleton, resolving the main form lazily because
  the runner registers it only after the form factory); register the portable faces alongside the
  Windows ones in `UseWinForms`.
  `FileDialogsOptions` (the impl's options, which references `IFormInteraction`) stays put. DO NOT
  move the window-state stack — portable-in-signature is not the bar (see the design's §4.4 guard).
  Namespaces stay flat per package. **DELETE the moved members from the derived interfaces** —
  re-declaring `OpenUrl` on `IShellLauncher` or `BlockInteraction`/`UnblockInteraction` on
  `IFormInteraction` is CS0108, which H5's `TreatWarningsAsErrors` turns into a build error. Then
  review + promote **exactly two** baselines (`Shenora.Core.txt`, `Shenora.WinForms.txt`) — a diff in
  the other three is a SIGNAL, not noise — and add a `### Breaking` CHANGELOG entry.
  **Doc sync in the SAME commit** (four tracked docs assert the old layering and would argue a future
  session back to it): `docs/ARCHITECTURE.md` "Dependency rules … never sideways";
  `docs/REVIEW-GUIDE.md` §5's "the ONE deliberate package-on-package edge"; `README.md`'s package
  table (it ships inside every nupkg; WinForms stops owning the dialog/clipboard/shell contracts);
  `docs/RELEASING.md`'s "the two leaf packages" (WinForms stops being a leaf); plus the design
  contract's §4 table rows. Then the `Shenora.Core`/`Shenora.WinForms` csproj `<Description>`s — the
  "UI-dispatcher seam" claim becomes TRUE here, and WinForms gains the dispatcher implementation.
- [x] **H4.2 — Retire the marshal copies onto `WinFormsUiDispatcher`.** COMPLETE. The sessions copies
  landed with H4.4 as planned (`RenderSession.OnUiAsync`/`OnUiFireAndForget`,
  `SessionController.OnUiAsync`/`PostUi`, `CoBrowseSession.RunOnUiAsync`×2/`RunOnUiFireAndForget`),
  and each closed something real: `RenderSession` now OBSERVES the cancellation tokens it accepts
  (H2's pool-starvation P0 — the dispatcher's `WaitAsync` means the caller escapes even when the UI
  thread never runs the body), `SessionController`'s inverted pre-handle guard is gone (its own
  comment described the trap and the next line committed it), and `CoBrowseSession` uses the
  never-faulting `InvokeOrDefaultAsync` so its "one bad input message must not fault the session"
  contract survives the collapse. Earlier: the six
  outside the sessions package are converted (`FormInteraction.SetEnabled`, `SecondaryWindows.Post`,
  `WebViewIpcBridge.PostJson`, `WebViewHost`'s deferral marshal, `WindowCommandFacade.Post`,
  `DropZoneManager.MarshalToUi`). **The sessions copies land with H4.4**, which rewrites the same
  files anyway — doing them twice would be churn. Two outcomes worth carrying forward:
  (a) **`SplashPanel`'s two self-marshals are deliberately NOT converted** and say so in the code: a
  control marshalling to ITSELF is idiomatic and its pre-handle apply-directly is correct, so the
  honest count is "collapse the service-to-foreign-control copies", not "14 → 1";
  (b) `FormInteraction` keeps applying `Enabled` directly when NotReady — `Control.Enabled` on an
  unrealized control is a stored value, and dropping it would lose the block for a not-yet-shown
  window. Conversion also fixed two live defects: `WindowCommandFacade` used to defer even when
  already on the UI thread (losing `START_DRAG`'s mouse-down timing) and left the posted body
  unguarded (a throwing `ApplyTheme`/`FormClosing` crashed the app); `DropZoneManager` used to run
  `PointToScreen`/`Controls.Add` inline ON A WORKER THREAD pre-handle, which is now a drop-and-log. 14 hand-rolled copies
  across 3 packages with 5 incompatible pre-handle policies — 7 of them (all in Sessions) have no
  guard at all, and `LoginWindowController.cs:250-254` carries a comment explaining the pre-handle
  trap and then commits it on the next line (`if (!_form.IsHandleCreated || !_form.InvokeRequired)
  return work();` runs the WebView2 call INLINE on the calling thread — reachable via the co-browse
  background controller while a driver continuation is off the UI thread). The single owner's
  semantics: `IsDisposed`/`IsHandleCreated` pre-check BEFORE `InvokeRequired` → non-blocking
  `BeginInvoke` + TCS → token observed via `WaitAsync` → guarded body → explicit throw/swallow
  policy → inline only when already on the UI thread. Per-CONTROL, never per-application (Sessions
  marshal to their anchor form; `SecondaryWindows` run their own STA pumps). Note
  `WindowCommandFacade.Post` always defers even when already on the UI thread, which loses
  `START_DRAG`'s mouse-down timing. It makes H2's `RenderSession` unobservable-token P0 MECHANICAL but
  does not close it: `WaitAsync` returns the awaiter, it does not kill the wedged op or release the
  pool's accounting — H2 still owes `OpTimeout` + discard-the-abandoned-instance.
  **THREE SITES KEEP THEIR OWN POLICY** — each was earned in a previous review, and a single bool
  would silently re-break two of them (see the design's §5.4 table): `DropZoneManager.MarshalToUi`
  returns false so the CALLER proceeds inline ("recursed without end" if it re-invokes);
  `SecondaryWindows.Post` must be a pre-handle no-op carrying intent in a flag (posting there "would
  create the handle on the wrong thread and kill the pump"); `SplashPanel` applies directly
  pre-handle. `CoBrowseSession`'s input/hotspot paths must not fault the session — they use the
  never-faulting `InvokeOrDefaultAsync` overload. This is why the contract is three-state
  (`NotReady`/`Ready`/`Gone`) plus `IsOnUiThread`, not one bool.
- [x] **H4.3 — The portability proof.** A `net10.0` project `samples/Shenora.Sample.Logic` with one
  facade that picks a file, reads the clipboard and opens a URL, referenced by the desktop sample.
  Compiles with no Windows reference = the seam is real; a Windows type later dragged into a contract
  turns it red. Without this, portability is asserted rather than enforced. (~30 lines.) TWO
  conditions or it proves nothing: it must inject **`IUrlLauncher`**, not `IShellLauncher` (today's
  `SampleFacade` injects the Windows extension, so the facade gets SPLIT — portable routes out,
  reveal-in-Explorer and secondary windows stay in the desktop sample); and it must be added to
  `Shenora.slnx` — a SECOND solution edit after H5's, or `verify` never compiles the proof.
- [x] **H4.4 — Make the declared `Sessions → Shenora.WebView2` edge actually carry something.** DONE,
  with a scoping judgement worth reading: what crossed the edge is the **invariant**
  (`BrowserArguments.Compose` — single-occurrence feature switches + the dev CDP re-append), not the
  whole app-shell preset. The session ARGUMENT preset, the EVENT policies and the environment caching
  legitimately differ from `WebViewHost`'s: an app shell opens external links in the system browser
  while an unattended session must open nothing, and one shared app environment is not the same thing
  as one environment per profile. Sharing those would have been coupling, not dedup. Also landed here:
  the three missing policies (`NewWindowRequested` suppressed, `PermissionRequested` denied,
  `ProcessFailed` surfaced → the pool poisons the instance, co-browse completes its frame channel),
  script dialogs disabled, and the `Log` options (H4.7). The last piece — one cached environment per
  profile (H2's "each retry orphans another browser process") — LANDED with the H2 sessions pass as the
  internal `SessionEnvironmentCache`, and the shape it took is the interesting part: **owner-scoped
  (the pool holds one), never static/profile-keyed.** A live environment keeps its profile's browser
  process and therefore the folder's OS lock alive, so a process-lifetime cache would have made
  `LoginWindow.ClearProfile` — the call that makes a logout a REAL logout — fail every time instead of
  only while a window is open. A login window opens one profile once and gains nothing from caching; a
  pool creates N instances on ONE profile, which is the case that does. Owner scoping also makes it
  single-threaded by construction, which matters because `CoreWebView2Environment` is thread-affine. It
  reuses an IN-FLIGHT creation (that is the anti-orphan half: `InitTimeout` abandons the await, never
  the `CreateAsync`) and deliberately does NOT cache a faulted/cancelled task — the trap
  `WebViewEnvironment` still has, still listed under H3. With
  D19 the answer is settled: route Sessions through the edge (the alternative — dropping the
  reference — is off the table now that the layering is deliberate). VERIFIED:
  the `ProjectReference` exists (`Shenora.WebView2.Sessions.csproj:15`) and NO file in the package
  imports `Shenora.WebView2` or uses one of its types. Consequences: `SessionBrowser`
  re-implements browser-argument building (`:66-93` vs `BrowserArguments.Build` — and the rewrite
  re-introduces the CDP gotcha from `windows-dev-gotchas.md`: setting `AdditionalBrowserArguments`
  makes WebView2 ignore `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, so a dev session browser silently
  gets no debug port; it also appends caller `--disable-features` raw, reproducing the exact
  last-occurrence-wins bug `BrowserArguments` documents in capitals), environment creation (hence
  one env per instance instead of one cached per profile — and the init timeout abandons
  `CreateAsync`, so each retry against a locked profile orphans another browser process, adding to
  the lock its own error message blames), the init-timeout guard + message (vs
  `WebViewHost.InitializeAsync:51-75`), and settings hardening (vs `ApplySettings:108-131`, whose
  list is MORE complete). Above all: the three policies `extraction-sources.md` lists as "fix during
  the port" — `NewWindowRequested` / `PermissionRequested` / `ProcessFailed` — exist in
  `WireEventPolicies` and are ENTIRELY ABSENT for pooled/co-browse instances, so `window.open()` on
  a pooled page opens a real visible popup, a permission prompt stalls an invisible page, and a
  renderer crash is invisible to the pool (and leaves `CoBrowseSession.Frames` waiting forever).
  Wiring `ProcessFailed` is also what lets the pool poison an instance and co-browse complete its
  channel. Either route Sessions through the edge or drop the reference and stop claiming it.
- [x] **H4.5 —** DONE. Collapsed: the IPC error boundary → one `IpcErrorMapping.ToErrorResponse` (it
  was four byte-identical copies of the kit's most load-bearing invariant — the fifth copy is how a
  raw `ex.Message` eventually reaches a client); `Done()` + `UnknownType(request)` → `BaseFacade`
  (three private copies, one of them in the SAMPLE, which is the tell it was consumer-facing);
  `WebViewHost`'s open-a-URL → `IUrlLauncher` (a drifted copy of `ShellLauncher.OpenUrl` that was
  missing the Win11 handle `Dispose`); four bring-to-front variants → one internal
  `WindowActivation.BringToFront` (the tray copy omitted `SetForegroundWindow`, so restoring from the
  tray behind another app could leave the window hidden); three `DeviceDpi / 96` sites → `DpiHelper`
  (one used integer division, none guarded a non-positive DPI); the off-screen park coordinate → one
  const + `OffscreenWindow.IsParked` (a THIRD site inferred on-screen-ness from a DIFFERENT threshold,
  so moving the park position would have silently broken reveal detection); the window-state
  apply/save pair → `WindowStateManager.AttachTo` (the ordering IS the contract); `CookieLoginFlow`'s
  private `JsonSerializerOptions` → `IpcJson.Options`. NOT collapsed, deliberately: `WebViewScripts`'
  own options (it must NOT omit nulls, unlike the wire serializer — noted at the site), and the npm
  `randomUUID`/debounce helpers (trivial, and the npm package has no shared-internals home yet —
  folded into H7). Original list —
  Collapse the remaining duplicates, each to a named owner. **Visibility rule (from
  D19/D20): a helper whose consumer is ANOTHER PACKAGE is public, not `internal`** — a
  `ProjectReference` does not grant internal access, and `InternalsVisibleTo` is granted only to
  `Shenora.Tests`. That corrects three prescriptions below whose `internal` owner could not serve the
  copy it was meant to retire (`MapException` — one copy is in `Shenora.WebView2`; the
  bring-to-front helper — a fourth copy is outside `Shenora.WinForms`), and it makes the "the two
  cross-package `DeviceDpi / 96` sites can't reach `DpiHelper`" premise FALSE once H4.1 lands — they
  can, so collapse them too. Also add here: `WebViewHost`'s copy of open-a-URL-in-the-shell should
  delegate to `IUrlLauncher` rather than keep the handle-leak fix H1 applies to it. The duplicates: the IPC error-boundary
  `catch (OperationException)/catch (Exception)` pair — 4 copies (`MessageDispatcher.cs:62-73,215-226`,
  `BaseFacade.cs:39-50`, partial at `WebViewIpcBridge.cs:239-248`) of the single most load-bearing
  invariant in the kit → one `internal static MapException(...)` in `Shenora.Ipc` (two of the four
  are deliberate belt-and-braces; keep that, share the body); facade boilerplate `Done()` +
  the unknown-type terminator → two `protected` helpers on `BaseFacade` (the sample retypes both at
  `SampleFacade.cs:61-62`, which is the tell that it is consumer-facing); 4 copies of
  `DeviceDpi / 96` → `DpiHelper.ScaleFromDeviceDpi` (the copy at `OptimizedForm.cs:313` is in
  `DpiHelper`'s OWN package and uses integer division; the two cross-package ones can't reach it —
  add the `> 0` guard there); 4 "bring a window to the front" variants, 3 of them in one package
  (`WinFormsHost.cs:228-238`, `SecondaryWindows.cs:151-157`, `TrayIcon.cs:129-137` — the tray one
  omits `SetForegroundWindow`, which is why restoring from the tray can leave the window behind
  everything) → one internal `Activate(Form)`; the off-screen park coordinate triplicated with a
  MISMATCHED threshold (`OffscreenWindow.cs:19` and `LoginWindow.cs:218` use `-32000`;
  `LoginWindowController.cs:51` infers on-screen-ness from `> -30000`) → an internal const +
  `IsParked(Form)`; private `JsonSerializerOptions` copies (`WebViewScripts.cs:16-19`,
  `CookieLoginFlow.cs:65`) — the exact drift `IpcJson`'s own doc says it exists to prevent (note
  `IpcJson.Options` omits nulls, so `WebViewScripts` may need to stay separate WITH a comment saying
  why); the window-state attach triple (`WinFormsHost.cs:175-177` = `SecondaryWindows.cs:99-101`) →
  `WindowStateManager.AttachTo(form)`; `randomUUID`-with-fallback and the debounce helper in
  `@shenora/react` (`bridge.ts:54-58`, `useDropZone.ts:38-41,48-56`) → internal utils.
- [x] **H4.6 —** DONE as a RENAME, not a base extraction: `LoginWindowController` → `SessionController`
  (21 occurrences across 5 source files + the living docs; historical `ROADMAP`/`FIX-LOG` entries left
  intact because they were true when written, with the mapping noted in the new entry). That is what
  actually fixed the reported surface problem — `CoBrowseSession.Controller` is public and was typed
  with a login-named type, so a co-browse consumer had to program against "Login…". The login-specific
  types keep their names. The base extraction below is DEFERRED to H9 on purpose: the neutral name was
  the surface fix, and what the shared core should be is better decided while reshaping the co-browse
  API (D21) than guessed now. Original note kept for context —
  Consider one honest shared base for the three session types (`RenderSession` /
  `LoginWindowController` / `CoBrowseSession` share browser + host window + guarded navigate +
  script + taps + marshal). This is also the clean route to the deferred session-neutral rename: a
  neutral base with the login-flavoured type as the foreground subclass. Judgement call — only do it
  if the shared core is real after H4's earlier items land.
- [x] **H4.7 —** Add the missing `ILogger<T>?` + `NullLogger` convention to `Shenora.WebView2.Sessions` — it
  has ZERO logging of any kind against ~30 silent `catch { }` blocks, so a wedged pool or a failing
  request filter is undiagnosable in production. (`ILogger` is reachable transitively.) While there,
  reconcile the two logging conventions in `Shenora.WebView2` (11 sites use `ILogger`, 4 use an
  `Action<string>? Log` option, so one package uses both).

**H5 — Close the gate holes (near-zero churn, highest payoff per edit)**

- [x] **`Shenora.slnx` has an EMPTY `<Folder Name="/samples/" />`** and doesn't list
  `Shenora.Core` either. `dev.mjs build` builds only `config.solution`, so **`verify` — the
  documented "am I done?" gate — never compiles the reference composition or the e2e subject**; the
  sample can be red while `verify` and the release workflow pass green. `samples/Shenora.Sample.Web`
  is likewise never type-checked (its `typecheck`/`build` scripts are called by nothing). FIX: add
  the sample projects + `Shenora.Core` to the solution; add the web typecheck to `verify`.
- [x] **`dev.mjs test <typo>` exits 0 having run nothing** (`:93-101` — no else branch, `ok` stays
  `true`). Add the else and fail loudly.
- [x] **`check-sensitive.mjs` fails OPEN on a fresh clone and in CI.** `:33-42` prints a notice and
  continues with only the two structural built-ins when `local/sensitive-patterns.txt` is absent —
  and `local/` is gitignored, so **the brand/sibling-name half of the guard never runs in the
  release gate**. Three further misses: renamed/copied staged files are skipped entirely
  (`--diff-filter=ACM`, but `git mv` stages as `R`); file PATHS are never matched, only content (a
  file *named* after a banned token passes); `--tree` reads the working tree, so an
  already-committed leak edited away locally reports clean. There is also no `commit-msg` hook, while
  the release workflow pipes commit subjects into public release notes. FIX: exit non-zero when the
  pattern file is missing (or require an explicit `--allow-builtins-only`), match paths too, include
  `R`/`C` status, and add a `commit-msg` hook.
- [x] Turn on `TreatWarningsAsErrors` in `src/Directory.Build.props` and drop `-clp:ErrorsOnly`
  from `dev.mjs:86,128-129` — nothing in the repo makes warnings errors and the gate hides them
  (CS1591 is additionally silenced, so missing XML docs ship to the nupkg unnoticed).
- [x] Make `verify` run `doctor` (today version/README drift doesn't fail the gate; it self-heals
  only because `pack` runs `doctor --fix` first, meaning verify scans the PRE-sync files). Also add
  a `prepublishOnly` guard for the npm package — the release workflow re-packs from source and a
  stale/missing `dist/` would ship a README-only package with no error.
- [x] `release.yml` creates the GitHub release — and therefore the TAG — even when
  `create_tag: false` (only the tag STEP is gated), so the tag can be created at the default-branch
  head, pointing at a different commit than the one published.
- [x] Set `ManagePackageVersionsCentrally` in the root `Directory.Packages.props` shim (it's
  hand-set 3× today and missing from both devtools csprojs); add `CodePage=65001` to the two
  devtools csprojs (both contain non-ASCII string literals and `src/Directory.Build.props:20-21`
  documents that exact mojibake failure on this machine); drop the unused
  `M.E.DependencyInjection.Abstractions` pin and the redundant `Microsoft.Web.WebView2` re-declaration
  in `Shenora.WebView2.Sessions.csproj:16`; reconsider shipping `InternalsVisibleTo Shenora.Tests`
  in all five nupkgs on unsigned assemblies.
  **DONE except two DELIBERATE keeps:** (a) the `Microsoft.Web.WebView2` reference in
  `Shenora.WebView2.Sessions.csproj` STAYS — the package uses WebView2 types directly, and an
  explicit direct reference is better practice than relying on a transitive one arriving through
  `Shenora.WebView2`; the duplicate nuspec entry is harmless. (b) `InternalsVisibleTo Shenora.Tests`
  stays for now: removing it needs the test project to stop using internal seams (a large change),
  and the exposure is bounded to an assembly deliberately named `Shenora.Tests`. Revisit at P7 with
  signing. Also done here: `prepublishOnly` on the npm package, so a stale/missing `dist/` can't ship.

**H6 — Public surface + cross-language lockstep (cheapest window is BEFORE 1.0)**

- [x] **Extend the API baseline to `protected` members.** DONE, and much further: the one-line
  `GetMembers(BindingFlags.Public)` dump became `ApiSurfaceDump`, which renders protected members
  (`BaseFacade.RouteMessageAsync` — the member EVERY consumer overrides — was entirely ungated),
  default parameter values (dropping a `= null` is a source break for every caller and showed NO diff),
  `init` vs `set`, `required`, `static`, `virtual`/`abstract`/`override`, parameter names (named
  arguments are a source contract), generic constraints, nullability, base types + directly-implemented
  interfaces, const VALUES (a wire code is what consumers compare against), and attributes — all 22
  `[JsonPropertyName]` wire names are now pinned, so renaming one can no longer break the C#⇄TS mirror
  silently. Accessors render as `{ get; init; }` rather than separate `get_X()` lines: shorter AND
  strictly more informative. Three rendering decisions are documented in the file because they were
  wrong on the first attempt: an unconstrained `T` reads as Nullable at runtime and must NOT be
  annotated; the compiler's `[Obsolete]` parameterless-ctor stub on a `required` type is filtered (its
  message is SDK-version-dependent and would churn); C# aliases (`void`, `string`) are used because a
  human reviews this file on every change. The assembly list now comes from the baseline DIRECTORY, plus
  a new `Every_shipped_assembly_has_a_baseline` walking transitive references to close the other
  direction (deriving from the directory alone would leave a new package silently ungated).
- [x] **Add the cross-language mirror tripwire and the missing code.** DONE — `scopeRequired` added to
  `types.ts`, plus `WireMirrorTests`, which parses the TS SOURCE (what an adopter imports; a generated
  artifact would add another place to diverge) and asserts set equality of the error codes, the
  handshake route, and the envelope categories. Client-ONLY codes are excluded via a new exported
  `ClientOnlyIpcErrorCodes` so the client DECLARES its own exceptions rather than the test carrying a
  second hard-coded list. The parser self-checks (`Assert.NotEmpty`) so a regex that silently matched
  nothing can't make it pass, and I verified the tripwire FAILS by temporarily removing the code —
  message: "The host emits these codes but the client cannot name them: SCOPE_REQUIRED".
- [x] **`'\0'`-join the client event-bus keys and add a scope filter.** DONE, with the host's exact
  scope rule mirrored — including the half that is easy to miss: a global (scope-less) event still
  reaches a SCOPED subscriber, so an app-wide announcement isn't swallowed by a per-scope listener.
  `useShenoraEvent` takes `scope` through. Three tests pin the scope semantics and one pins the
  collision (`("APP","TASK.DONE")` vs `("APP.TASK","DONE")` were the same key).
- [x] **Fix `BaseModuleService`'s generic constraint.** DONE — `TRequests extends object`, and the
  `extends Record<…>` dropped from both callers (`windowCommands.ts` was demonstrating the very
  anti-pattern the base class exists to prevent). **This uncovered a bigger hole:** the tests were not
  type-checked by ANYTHING — `build` uses `tsconfig.build.json` which excludes them, vitest transpiles
  without checking, and the `tsconfig.json` written to do the job had never been run (it was red on an
  ES2020 `lib` vs `.at()`). So `@ts-expect-error` assertions were inert. Fixed the lib, added a
  `typecheck` script, and wired it into `dev.mjs verify` — then proved it works by reintroducing the
  anti-pattern and watching TS2578 fire.
- [x] **Give form-dependent facades a first-class registration seam.** DONE — and NOT by either option
  the review listed. The recommendation (facades resolve the form lazily via `IFormInteraction` and
  register through `AddModuleFacade`) does not actually work for two of the three modules: `DropZoneFacade`
  needs the live `DropZoneManager`, which needs the WebView2 control, and the RENDER route closes over the
  form's session pool — neither is resolvable from DI before the form exists. And widening
  `IMessageDispatcher` with the whole `Map*`/`Use*` family was rejected as too large.
  What shipped is smaller than either: **`Use(MessageMiddleware)` — the ONE primitive every helper
  already delegated to — moved onto the interface, and the six helpers became extension methods over it**
  (`MessageDispatcherExtensions`). So the interface stays at the four things a dispatcher genuinely is
  (dispatch, two sends, compose), a decorator has four members to write instead of ten, and every helper
  works on any implementation. The sample's `if (dispatcher is MessageDispatcher concrete)` — which had no
  `else` — is gone. `AddMessageDispatcher`'s configure callback now receives the INTERFACE, since taking
  the concrete type would have kept propagating the downcast. Two tests pin it: late mapping through the
  interface, and a pass-through decorator (the exact shape that used to make three modules vanish).
  `MessageDispatcher.Use` is declared twice on purpose — C# forbids a covariant return when implementing
  an interface, so the explicit impl returns `IMessageDispatcher` while the public one keeps the concrete
  type for existing fluent chains. Also fixed the `WindowCommandFacade` doc, which pointed at
  `AddMessageDispatcher`'s callback — a path that CANNOT work, since it runs before any form exists.
  Original recommendation kept for context: The reference composition has
  to downcast — `MainForm.cs:85` `if (dispatcher is MessageDispatcher concrete)` — because
  `IMessageDispatcher` exposes only `DispatchAsync`/`SendAsync`, and `WindowCommandFacade.cs:41-43`
  documents a path (`AddMessageDispatcher`'s configure callback) that CANNOT work: that callback runs
  at provider-build time, before the form exists. The `if` has no `else`, so a different
  `IMessageDispatcher` registration (or a future decorator) silently drops WINDOW + DROP_ZONE +
  RENDER and the frameless title bar just stops working. RECOMMENDED: have those facades resolve the
  main form lazily via the existing `IFormInteraction` so they register as ordinary DI facades
  through `AddModuleFacade` (smaller surface change than widening the interface). Fix the
  `WindowCommandFacade` doc either way.
- [~] Trim surface that doesn't earn its keep, and add what's missing. **The CORRECTNESS half is DONE
  (2026-07-30)** — these were bugs behind surface items, so they went first:
  - `MessageDispatcher.Use()`/`_middlewares`: the `Lazy` + `List<T>` swap was unsynchronized, so a
    concurrent dispatch could read the OLD cached pipeline and answer `NO_HANDLER` for a route that was
    already registered, and a build enumerating the list while `Add` grew it was a plain data race. Now
    copy-on-write array + volatile pipeline + invalidate-and-rebuild under one lock. Regression test
    hammers 200 late `UseRoute` calls against continuous dispatch.
  - `IpcErrorCodes.OperationCancelled` + the `catch (OperationCanceledException)` arm in
    `IpcErrorMapping`, placed AFTER `OperationException` so an app that models cancellation in its own
    words keeps them. Mirrored to `types.ts` (the H6.2 tripwire enforced that automatically — it works).
  - `IpcResponse.CreateError`'s argument order now matches `OperationException`'s: `code`,
    `parameters`, `message`. The shared order puts the WIRE-relevant piece first (`parameters` crosses;
    `message` is host-log only). Every in-repo call site already used `parameters:` named, and a
    positional third argument now fails to compile rather than binding to the wrong thing.
  - `EventBus`: the convenience `EmitAsync` overload guards module/type (it used to build a message that
    could never match any subscription — a silently undeliverable event); and `SubscribeCore` publishes
    `_patterns` LAST, since that is what `EmitAsync` enumerates — so its `continue` can now only mean
    "concurrently unsubscribed", as its comment always claimed.
  - `ScopedContainerRouter.HandleAsync` retries ONCE on `ObjectDisposedException` (guarded on
    `!_disposed` so a router shutting down can't spin): `InvalidateScope` is a documented app-facing call
    that can fire mid-request, and the race used to surface as `UNKNOWN_ERROR` instead of just using the
    rebuilt scope.
  - `ShenoraPathsOptions` is a `record`, and the `--app-root` merge uses `with` — it hand-copied six
    properties, so a seventh option would have been silently dropped whenever that flag was passed.
  - `BaseFacade`'s lone `ConfigureAwait(false)` REMOVED: it was the only one in the dispatch path and it
    contradicted the documented context-preserving model, discarding the very context a WINDOW facade
    needs. It survived only because every in-repo facade marshals internally anyway.
  **The TRIM half is now done too (2026-07-30):**
  - `DpiHelper.ScalePixels`/`ScaleSize`/`ScalePoint` REMOVED (zero callers). Worth noting WHY they were
    worse than merely unused: each hardcoded the PRIMARY monitor's scale, so anything that adopted them
    would silently mis-scale on a secondary monitor. `Scale` + the DPI you actually mean replaces them,
    and the consumer their own docs named (the drop-zone overlay) already converts from the control's
    `DeviceDpi`, which is the correct source.
  - npm: the `declare global { Window.chrome }` augmentation is GONE (it collided with `@types/chrome` as
    an unfixable TS2717 in a `.d.ts` the consumer doesn't own — a library must not claim global names; a
    local interface + one cast replaces it); `"./package.json"` added to `exports`; and the tarball now
    ships the LICENSE, with `doctor` checking it byte-matches the root one (verified by breaking it).
  **STILL OPEN, all deliberately deferred:** `SessionBrowser`'s public statics → internal WITH the
  H2-deferred `CancellationToken`; `CoBrowseSession`'s two token-less async members (H9 reshapes both
  signatures — doing it here would mean changing them twice); bridging Sessions' `LoginErrorCodes` into
  the IPC contract (same reason — H9 owns that vocabulary); duplicate `ModuleName` rejection in the EAGER
  `MapRegisteredModules` (the lazy path, which is what `AddMessageDispatcher` uses, already rejects them);
  and `EventMessage<T>` as an alias of `IpcNotification<T>`.
  **NOTE `IWebViewResourceProvider.Exists` must NOT be removed** — H3 gave it a real consumer (the
  startup bundle sanity check), which is the option the review offered as the alternative.
  Original list: `DpiHelper.ScalePixels`/
  `ScaleSize`/`ScalePoint` have ZERO callers and their documented consumer (the drop-zone overlay)
  architecturally cannot reach them; `IWebViewResourceProvider.Exists` is never called in `src/` or
  `samples/` (every implementor pays for it) — remove it or use it for a startup sanity check on
  `index.html`, which would also catch the wrong-prefix case in H3; `SessionBrowser`'s public statics
  take a raw WinForms `WebView2` and have no consumer scenario (they also invite bypassing the
  pool's accounting) — internal until an adopter proves otherwise; `CoBrowseSession.DispatchInputAsync`
  and `ReadHotspotsAsync` are the only async members in the package with NO `CancellationToken` and
  both can block indefinitely on a wedged renderer (adding the parameter after 1.0 is binary-breaking);
  align the argument order of the two sibling error constructors — `IpcResponse.CreateError(code,
  message, parameters)` vs `OperationException(code, parameters, message)` (breaking after 1.0);
  add `IpcErrorCodes.OperationCancelled` + a `catch (OperationCanceledException)` arm so cancellation
  stops surfacing as `UNKNOWN_ERROR` (the sample already hand-rolls the workaround at
  `MainForm.cs:107`); bridge Sessions' parallel error vocabulary (`LoginErrorCodes` strings on a DTO
  with no `ToError()`) into the IPC contract so every adopting app stops rewriting
  `MainForm.cs:104-119`; guard `Use()`/`_middlewares` (`MessageDispatcher.cs:138-143`) — late
  mapping is a SUPPORTED, documented pattern and the `List<T>` + `Lazy` swap is unsynchronized, so a
  concurrent dispatch can see the old pipeline and answer `NO_HANDLER` for a registered route;
  reject duplicate `ModuleName`s in `MapRegisteredModules` (today the second facade's whole route
  table is silently unreachable); mirror `EventBus`'s null/empty guards into the convenience
  `EmitAsync` overload; publish `_patterns` LAST in `SubscribeCore` (`:63-65`) so `EmitAsync`'s
  `continue` can only mean "concurrently unsubscribed", as its comment claims; retry once on
  `ObjectDisposedException` in `ScopedContainerRouter.HandleAsync` (a scope invalidated while a
  request is in flight currently fails as `UNKNOWN_ERROR` instead of rebuilding); drop the
  `declare global { Window.chrome }` augmentation the npm package ships (`transport.ts:8-12` — it
  collides with `@types/chrome` in a consumer's program, an unfixable TS2717 in a `.d.ts` they don't
  own); make `EventMessage<T>` an alias of the structurally identical `IpcNotification<T>`; add
  `"./package.json"` to `exports` and a `LICENSE` to the published tarball (the manifest declares
  MIT while shipping no license text); make `ShenoraPathsOptions` a `record` so the `--app-root`
  merge stops hand-copying six fields (`ShenoraApplication.cs:94-102` — a seventh option would be
  silently dropped); document or remove `BaseFacade`'s lone `ConfigureAwait(false)` (`:36`), which
  contradicts the dispatcher's documented context-preserving model.

**H7 — Tests, docs and dead weight**

- [x] **Test-suite health — DONE (2026-07-30).** The suite is **442 dotnet + 63 vitest**, and the
  parallelization item turned out to be hiding a real defect rather than only a flake risk.
  - **`xunit.runner.json` with `parallelizeTestCollections: false`**, whole suite, NOT per-class
    `[Collection]` — decided on measurement, not taste: parallel 6 s but masking the hang below;
    serial-with-hang 28 s then 1 m 6 s (wildly variable); serial once fixed a steady **9–10 s**. Serial
    is also self-maintaining (a new pump test needs no attribute) and it is what SURFACED the defect.
    Declared explicitly as `<None CopyToOutputDirectory>` in the csproj: xunit's auto-include glob did
    NOT copy the file, and a runner config the runner ignores is worse than none.
  - **THE FIND: `WindowCommandFacadeTests`' `START_RESIZE` case entered the OS modal size loop ON THE
    TEST THREAD** — 16.9 s of the suite's 26.8 s, and an indefinite hang when run alone. H4.2 made
    `WinFormsUiDispatcher.Post` run a body INLINE when already on the UI thread (correct: the loop must
    start while the mouse button is down), and the test creates its form on the test thread — so
    `SendMessage(WM_NCLBUTTONDOWN)` ran synchronously. Its own "deliberately NOT pumped" comment had
    been false since H4.2, and collection parallelism kept the wall clock at 6 s so nobody saw it.
    Test-only fix (production behaviour is right): dispatch via `Task.Run` so `InvokeRequired` is true
    and the body is queued to something the test never pumps. `WindowCommandFacade.Post`'s doc now
    records the accepted consequence — those two routes answer only after the user releases the mouse.
  - **Doubles collapsed, each to a SUPERSET of what it replaced** (which is why nothing regressed):
    `TestSupport/Sta.cs` (3 remaining `RunSta` copies; one spelling everywhere now — the copies had
    `ExceptionDispatchInfo` but a bare unbounded `Join()`, the shared one has both that and the 30 s
    bound); `TestSupport/FakeWindowStateStore.cs` (3 fakes — seed and assertion target are deliberately
    SEPARATE members, since `MemoryStore` used one field for both and read as a round-trip guarantee it
    never made); `TestSupport/IpcRequests.cs` (5 factories, 4 signatures — the part worth one owner is
    the `Payload` null-means-absent ternary); `TestSupport/TempDir.cs` (all 7 create/delete pairs —
    cleanup is BEST-EFFORT because four copies had a bare `Directory.Delete` in `finally`, so a locked
    file threw FROM the finally and replaced the test's real failure with an unrelated IO error).
    Two `SetApartmentState` sites REMAIN deliberately: the long-lived never-pumped anchor threads in
    `RenderSessionPoolTests` and `WinFormsUiDispatcherTests` are not this shape.
  - **npm:** `vitest.config.ts` + `vitest.setup.ts` — `globals` stays FALSE (the tests import
    `describe/it/expect` explicitly, the better habit), so RTL's `cleanup` is registered EXPLICITLY in
    `setupFiles` rather than bought as a side effect of turning globals on; the setup guards on
    `typeof document` and dynamically imports, because the environment is per test FILE and four suites
    run in node. Evidence it took effect: vitest's `setup` went `0 ms` → `1.26 s`. One shared
    `src/testing/fakeTransport.ts` replaced 4 classes + 2 inline literals and builds replies from the
    exported `IpcCategories` (all four hand-wrote `{ category: 'ipc' }`, so they could have drifted from
    the wire contract together and stayed green); remaining literals converted too.
    **`src/testing/` is EXCLUDED in `tsconfig.build.json`** or it compiles into `dist/` and
    `files: ["dist"]` publishes it — the old exclude covered only `*.test.ts`. Backed by a new `doctor`
    check that fails when `dist/testing/` exists, proven by breaking the exclusion (the build really did
    emit `dist/testing/fakeTransport.js`).
  - **Barrel gated** (`index.test.ts`, 21 runtime exports as an explicit SORTED ARRAY, not a snapshot —
    a snapshot self-updates under `-u` and a reviewer never sees the removal) + a no-undefined-bindings
    check. **`createWebView2Transport` covered** (5 tests: null with no host / no `chrome.webview`,
    verbatim post, the `typeof event.data === 'string'` filter, unsubscribe detaching) — it had ZERO
    references while being the transport every real consumer runs on.
  - **Untested seams — one filled, the rest bounded HONESTLY.** `SessionBrowserOptions.RequestFilter`
    (the item with the `about:blank` bug on record) is now covered by 15 tests: its decision was lifted
    out of the `WebResourceRequested` lambda into `internal SessionBrowser.ShouldBlockRequest`, the same
    "make the REAL path testable" move as the pool's reset probe. Sabotage-verified. **The rest are
    e2e/manual BY CONSTRUCTION, not by neglect, and `docs/REVIEW-GUIDE.md` §6 now says so:**
    `SessionController`'s constructor subscribes to `_web.CoreWebView2.WebMessageReceived`, so the type
    cannot be INSTANTIATED without a live browser — which covers its public members (bar
    `ComputeFitSize`, tested), `CoBrowseSession.DispatchInputAsync`/`ReadHotspotsAsync`/`Frames`/
    `DisposeAsync`, `RenderSession`'s tap bookkeeping (its disposal checks ARE tested), and
    `CookieLoginFlow`'s 4-line controller→`Hooks` mapping (the poll/capture logic is covered through the
    internal `Hooks` overload, 8 cases).
  - **Implementation-detail assertions relaxed to their actual invariants**, all four: the exact
    exception-message sentence in `PayloadHelperTests` → contains the key AND leaks neither the raw
    value, the CLR type nor the JSON path; `TrayIconTests`' internal type NAME → the renderer's
    `ColorTable` really carries the app's colours (the old test would have passed a renderer that
    ignored every colour it was handed); `SplashPanelTests`' `Controls[0].Controls[0]` → named
    `internal ContentPanel`/`Bar` accessors, with layout expectations DERIVED from
    `SplashPanelOptions` instead of retyping its defaults; the exact STJ digit padding
    (`"deviceScaleFactor":1.50`) → no comma-decimal plus a parsed value, so changing the format string
    no longer fails a *culture* test. Both loosened assertions were sabotage-verified to still catch a
    real break.
- [x] **Docs drift — DONE (2026-07-30), and the list was ~80% STALE.** Earlier batches had already
  fixed: `README.md` + `Shenora.Core.csproj`'s Microsoft.Extensions claim (now "DI (implementation) +
  logging abstractions", matching the actual references) and its UI-dispatcher seam (H4.1 made it TRUE);
  `Shenora.WinForms.csproj`'s "drag-drop overlays" (gone) and "UI-thread dispatcher" (now true);
  `README.md`'s bridge-API row; `ROADMAP` `## Remaining` P1; `CHANGELOG`'s missing `0776f37` and missing
  `### Fixed` and the "newest first" contradiction; both packable-project counts; `CLAUDE.md`'s D-range;
  `rclick`/`move`/`drag` (documented in dev.mjs's header AND its usage line); ARCHITECTURE's
  `WindowCommandOptions` naming, its cache-header attribution, and its test-project reference count
  (it already said "the four leaf src projects (Core transitively)", which is correct).
  **The four GENUINE items, now fixed:** (a) `docs/ARCHITECTURE.md` never listed
  **`Shenora.Sample.Logic`** — the H4.3 portability proof — now in the tree with why it exists; (b) it
  named **none of the FIVE public extension classes** (the list said four; H6's
  `MessageDispatcherExtensions` made it five) — all five now named at their methods; (c) `CHANGELOG.md`
  had **TWO separate `### Breaking` groups** under one `## Unreleased`, merged in landing order, with
  the header now stating each `###` heading appears at most once per version — worse than untidy, since
  that heading is the SemVer gate and a reader would have missed five entries; (d) **not on the list at
  all** — `.claude/knowledge/ipc-contracts.md` still said the ready gate re-closes on
  `NavigationStarting`, which H3 changed to `ContentLoading` + `ProcessFailed`. `docs/REVIEW-GUIDE.md`
  §6 was stale too (it claimed protected members were ungated, which H6 fixed, and cited 318/39).
- [x] **Dead weight — DONE (2026-07-30).** `grep TODO src/` is now EMPTY: `'TODO'` was the example
  module name in SHIPPED npm docs (`moduleService.ts`, `devInterceptor.ts`, the npm README), which reads
  as an unfinished-work marker, so the whole example domain was renamed `Todo*` → `Note*` / `'NOTES'`
  across the README, both source docs and two test files. Stale comments fixed: `IShenoraModule` now
  explains that facades register HERE and the dispatcher maps them (so its one member is deliberate, not
  a placeholder) instead of promising later phases; `SessionBrowserOptions` lost "once it ships" about
  `LoginWindow`. The sample's `dropClassName: 'drop-hover'` finally HAS a rule (in `index.html`'s
  existing `<style>` — the sample has no CSS file), so the e2e subject can demonstrate the HOVER half of
  the drop contract; and `void getBridge().notifyReady()` became a real `.catch`, because an unhandled
  rejection in a WebView2 page is a silent console error and this is the snippet adopters copy.
- [x] **Documented the `notifyReady` → `ClearAll` ordering contract** (2026-07-30). Verified on the
  tree first: `ClearAll()` really is called from the sample's `OnClientReady`, and the method's own doc
  already said the handshake calls it — so a `REGISTER` arriving before `READY` is wiped AFTER BEING
  ACKED, leaving the client believing its zone is live with nothing logged on either side. Written at
  FOUR sites, because a contract this sharp gets missed when it lives in one doc comment:
  `ShenoraBridge.notifyReady` (+ the "don't `void` this promise" note),
  `UseDropZoneOptions`, `DropZoneManager.ClearAll`, and the npm README's copy-paste snippet — plus a
  bullet in `.claude/knowledge/ipc-contracts.md`. The sample's `useEffect` now says it must stay ABOVE
  the `useDropZone` call, since effects inside one component run in declaration order.
  **The "or make it order-independent" half landed in P5.6:** `DropZoneManager` clears on
  `ContentLoading` now, which removed this contract and all four documentation sites with it.

**H8 — Capture the earned invariants (do as the batches land, not after)**

**EXTEND existing rules; do NOT add a file per invariant** (the rule set must not sprawl — mapping
verified against every rule file). Several were landed EARLY, ahead of the code, because a stale rule
would have argued a future session back to the pre-D19 position:

- [x] DONE ahead of the work: the ONE marshal owner + token observance + guarded body + per-control
  (`webview2-hosting.md`); the D19/D20 placement law + "cross-package kit consumption justifies
  public" (`generic-library.md`); "a declared edge nothing crosses is a duplication smell" +
  layer-decides-the-home (`extraction-sources.md`); the unguarded OUTGOING serialize + "a DI
  singleton factory must never enumerate the provider it is building" (`ipc-contracts.md`); the
  known gate holes (`phase-workflow.md` + `CLAUDE.md`) and the guard's real coverage
  (`sensitive-info.md`); the five missed hunt classes + `FIX-LOG`/`REVIEW-GUIDE` doc-sync
  (`phase-review` skill); the router's two blind spots (`RULES_INDEX.md`).
- [x] DONE with the H2 sessions batch, all in `webview2-hosting.md` (on-demand tier, which has room —
  the CORE tier is at 15.7/16.0 KB and must not grow): containment-checked static serving (H1) and
  "an async navigation policy CANNOT be enforced in `NavigationStarting`" (H1, with the three-way
  division of labour so nobody re-litigates it); plus this batch's own — owner-scoped per-profile
  environment caching and never caching a faulted one, "escaping a wedged op is only HALF the fix"
  (added under the marshalling rule it completes), re-check cancellation after a multi-second acquire,
  a health probe must fail closed, a subscribe API on a pooled object needs a disposal check, and root
  a CDP event receiver in a field.
- [x] **The one genuinely new file: `winforms-shell.md`** — DONE with the WinForms tail, covering all
  four named traps plus the ones that batch earned (pumping re-entrancy, `SystemEvents` leaking a
  static reference, per-HWND drop targets, `FormClosed` ≠ pump finished, pre-handle intent in a flag).
  **The core tier was OVER budget when the `RULES_INDEX` row landed (16.4/16.0)** — paid for by a real
  trim, not a cosmetic one: the "known gate holes until H5 lands" text in `CLAUDE.md` +
  `phase-workflow.md` and the guard's "current limits" list in `sensitive-info.md` were all STALE (H5
  closed them) and were actively telling future sessions to distrust a working gate. Now 15.6/16.0.
- [x] **DONE (2026-07-30, with H7):** the `SemaphoreSlim.Dispose()`-wedges-a-cancelled-waiter root
  cause is now a bullet in `webview2-hosting.md` (on-demand tier) — cancelling waiters and then
  disposing the semaphore races its internal queue-removal and can leave a waiter's task PERMANENTLY
  incomplete; a `SemaphoreSlim` only needs disposing if `AvailableWaitHandle` was touched, so the fix
  is not to dispose it. The rule carries the "bound such a regression test with `Task.WaitAsync`"
  half too, since the original symptom was a 10-minute harness timeout with no summary.
- [x] **DONE (2026-07-30, with H7):** `knowledge check` passes (rows resolve, every rule indexed) and
  `knowledge footprint` reports **core 15.6 / 16.0 KB — ok** (on-demand 43.5 KB across 5 files). H7
  only grew the on-demand tier — the two rule edits (`ipc-contracts` handshake ordering + gate-trigger
  correction, `webview2-hosting` semaphore bullet) are both there, so the always-loaded budget is
  untouched. **The next `.claude/rules/` (core) addition still needs a trim, not an append.**

**H9 — Auxiliary sessions: primitives + lifecycle hooks, not the product (D21/D22) — COMPLETE (2026-07-31)**

Suite **476 dotnet + 63 vitest**, `verify` PASSED. Only the `Shenora.WebView2.Sessions` baseline moved
across the whole batch — the other four stayed byte-identical, which is the evidence that this
reshaped one package and nothing else.

- [x] **H9.1 — typed input seam.** `DispatchInputAsync(string json)` →
  `DispatchAsync(SessionInput, CancellationToken)`, with `SessionPointerInput`/`SessionWheelInput`/
  `SessionTextInput`/`SessionKeyInput`/`SessionViewportInput` + a `SessionPointerAction` enum, and
  `SessionInput.TryParseLegacyJson` as the explicitly-named adoption shim (D21's accepted cost —
  an existing client keeps its frontend). Fraction coordinates kept: that is what makes the protocol
  resolution-independent. `BuildMouseEventJson` takes the enum now, so there is ONE vocabulary.
  **One correction worth keeping:** the record hierarchy is NOT airtight — a record's compiler-generated
  COPY constructor is `protected`, so `private protected` on the base does not seal it. `DispatchAsync`
  therefore keeps an explicit default arm that LOGS rather than assuming exhaustiveness; without it an
  unknown input vanishes silently, which on a watched stream looks like the page hung.
- [x] **H9.2 — `ReadHotspotsAsync()` removed.** A stringly-typed list of clickable rects is a co-browse
  UX decision, not a browser primitive. Apps run their own script through `Controller` — the proven
  script ships verbatim in the CHANGELOG's breaking entry so nothing is lost.
- [x] **H9.3 — the lifecycle hooks. RE-VERIFIED FIRST, and half this item was already stale:** H4.4 had
  wired `onProcessFailed` to complete the frame channel, so the "reader waits forever" bug was gone.
  Genuinely missing and now shipped: `SessionEnded`/`SessionEndReason` + `StreamingSessionOptions.OnEnded`
  (guarded, fired EXACTLY ONCE through a shared latch — dispose and a renderer crash genuinely race),
  and frame GEOMETRY — `Frames` is `ChannelReader<SessionFrame>` carrying the viewport read from THAT
  FRAME'S own metadata, not the session's current viewport (a resize in flight would otherwise mislabel
  the frame, which is exactly when a mis-mapped click hurts).
- [x] **H9.4 — the error-vocabulary bridge.** `SessionResult.ThrowIfFailed()` → `OperationException`,
  so the codes cross as wire codes verbatim and plug into the dispatcher's documented boundary. NOTE
  the "neutral session controller" half of this item was ALREADY DONE by H4.6's rename.
- [x] **H9.5 — the seam is PROVEN, compile-wise.** The sample composes the product over the primitives
  exactly as its RENDER route composes the pool: a `STREAM` facade (START/INPUT/STOP) pumping `Frames`
  out as base64 IPC notifications, plus `StreamViewer.tsx` sending pointer/wheel input back. **Every
  call is public API — no internals — which is the seam test passing.** The transport being the
  interesting part is the point: frames are BINARY and the bridge is JSON, so the sample base64s them;
  a server-backed profile would push the same bytes down a WebSocket and the session would not know.
  **Compile-verified only — the sample has NOT been run** (see the note under P1).
- [x] **H9.6 — `SessionBrowser` statics internal + a `CancellationToken`** (the H2/H6 deferral, bundled
  here because it is the same signatures). `InitializeAsync`/`GetHtmlAsync` are `internal`; the token
  gates the AWAIT ONLY and is wired from the pool and the streaming session. Cancelling the CREATION
  would break other callers — the environment task is SHARED via `SessionEnvironmentCache`.
- [x] **H9.7 + H9.8 — the naming, on user direction (2026-07-31) → D22.** The kit had passed D21 on
  SHAPE while failing it on NAME, twice. `LoginWindow` contained no login logic; `CoBrowseSession` was
  named for one product built on generic mechanics. Renamed to `InteractiveSession` and
  `StreamingSession` with their whole type families (see the CHANGELOG table), `driveLogin` → `driver`
  (parameter names are a source contract the baseline pins), and
  `InteractiveSessionOptions.Title`'s `"Sign in"` default → `"Session"`. `CookieLoginFlow` KEEPS its
  name on purpose — naming the scenario is the point of a reference driver.
  **A whole-library audit ran** by sweeping the API baselines for domain vocabulary: the Login cluster
  was the ONLY genuine leak across all five packages, and the npm barrel is clean. The false positives
  are listed in D22 so nobody re-raises them (`ProfileDirectory` is a Chromium user-data folder,
  `Module` is the kit's composition unit, `ImmersiveDarkMode`/`UserDataFolder` are platform SDK terms).
  The rule now lives in `.claude/knowledge/generic-library.md` so the next session catches this class
  unprompted.

### P6 — Sibling adoption (brief Phase 5) — SCOPED 2026-07-31, not started

The first adoption target is the **business-manager sibling** (`local/EXTRACTION-MAP.md` names it).
Survey done 2026-07-31; the increments below come from that survey, not from the original brief.

> ⚠ **The roadmap's premise for this phase is STALE — do not plan against it.** P6 was written around
> "adopt in the newest desktop sibling first (smallest host, gaps already documented)". That app has
> since grown an API tier, a plugin system with its own IPC-namespace guarding, an MCP server and a
> deployment stack. Its desktop host now has **28 IPC modules** and its web client **~148 send
> call-sites**. It is still the right first target — its gaps are exactly Shenora's value proposition,
> and it already consumes the family's other library from a pinned feed — but "smallest host" is no
> longer why.

**The finding that makes this tractable.** Both sides of its IPC funnel through ONE seam each: the
client has a single `post()` + `onMessage()` pair (~60 lines total) that all ~148 call-sites go
through, and the host has a single dispatcher (`DispatchAsync` + `Emit`) behind a one-method module
interface. So swapping the IPC substrate is **two adapters, not 28 module rewrites and 148 edits**.
Verify that both chokepoints still hold before committing to the plan — it is the whole basis of the
sizing.

**The two models, and which one is the DEFAULT (user direction, 2026-07-31 — corrects the first
scoping pass).** The target's IPC is FLAT and UNCORRELATED — `{ type: "module.action", …payload }`
posted fire-and-forget, with everything coming back on a pushed event stream discriminated by
`type`. The first pass scoped that as legacy to be bridged away from. **That was backwards.** For a
desktop shell the event pipe is the correct default and request/response is the special case, for two
reasons the kit's own docs already establish:

- **It frees the UI thread.** The dispatch pipeline preserves the caller's synchronization context
  BY DESIGN (`.claude/knowledge/ipc-contracts.md`: "transports dispatch on the UI thread and every
  handler's synchronous segment stays there"), so a request/response handler's synchronous segment
  runs ON the UI thread. This repo already pays that knowingly in one place —
  `WindowCommandFacade.Post` documents `START_DRAG` blocking for the whole OS modal loop for exactly
  this reason. Making request/response the default generalises that stall to the whole app. Posting
  and answering with events lets the host move the work off the UI thread and keeps the window live.
- **A correlated call has a deadline; real work does not.** The client's `invoke` defaults to a 30 s
  timeout, which is meaningless for anything substantial.

So: **request/response for quick, UI-thread-safe calls** (read a bit of state, toggle a window — what
`WindowCommandFacade` uses it for); **post + event stream for everything else**, which is most of an
app. The adapters in P6.4 must PRESERVE the target's model, not migrate it.

What is genuinely wrong in the target is narrower than "it doesn't use request/response": it is the
missing CORRELATION. With no id, a result or an error cannot be attributed to the invocation that
caused it — its dispatcher emits a generic `error` event and the client cannot tell which action
failed. That is worth fixing; the event-stream shape is not.

**And this exposes a kit gap, found before the adoption rather than by it — fix it before 1.0:**
`@shenora/react`'s bridge has exactly ONE outbound call, `invoke()`, which allocates a correlation
entry, awaits, and times out. **There is no fire-and-forget send.** So the kit currently pushes every
page→host call down the UI-thread-coupled, deadline-bearing path — i.e. it makes the wrong thing the
default, which is precisely the complaint above. Design the missing half deliberately (a `post`/`send`
that does not await, plus a documented convention for correlating a streamed result back to the
invocation that started it — a handle returned by a quick request/response START is the obvious
shape, and it also gives cancellation and progress somewhere to live). Per D21 the ADOPTER's shim
still owns any wire-format compat; this item is about the kit lacking a first-class path, not about
carrying someone's envelope.

#### How this phase works (user direction, 2026-07-31 — supersedes the increment framing below)

**This repo does NOT edit the sibling.** Shenora readies the LIBRARY; the sibling's own session does
the adoption once it is ready. So every P6 item here is library work plus the guide an adopter needs.

**And a sibling is a CHECKPOINT, not the spec** (`.claude/knowledge/generic-library.md`): read it to
answer *"is this capability present and safe?"*, never *"what method did they write?"*. Shenora is
generic and must serve apps that do not exist yet; the surveyed apps only tell you which capabilities
are REAL and which are speculation. Copying their method is how a consumer's shape gets shipped.

#### Capability findings from the survey (2026-07-31)

Already covered — no work needed, and the earlier plan was wrong to call these open questions:
- **Multi-origin static serving.** `WebViewHostOptions.FolderMappings` + `WebViewFolderMapping`
  (with `AccessKind`) already covers several virtual hosts, including a deliberately DIFFERENT origin
  for cross-origin ES-module imports. P6.3 does not need a serving-model decision.
- **Portable app paths.** `ShenoraPaths` (root/data/resources + `DataArea` + env override) matches the
  portable-layout shape an adopter hand-rolls.
- **Window state.** `WindowStateManager` covers logical-px persistence, DPI scaling, on-screen
  validation and restore-bounds-when-maximized — and fixes a latent bug on the way, since a hand-rolled
  version reaches for `Screen.WorkingArea`, which is DPI-mis-scaled (use `GetMonitorInfo`).
- **Dynamic module composition.** CLOSED 2026-07-31: `IModuleRegistry` + `TryMapModule`.

Known capability LIMITS:
- [x] **A mapped module cannot be RELEASED — CLOSED 2026-07-31.** `TryReleaseModule`, with
  `IModuleRegistry` reshaped to `TryClaimModule`/`TryReleaseModule` so claim and release have one
  owner (a registry that only remembers a NAME can never take the route out again). The original
  reasoning — "no consumer has needed it, so do not guess at the surface" — was sound as a default and
  wrong as a final answer once P7's SemVer freeze was the alternative: "restart to disable a plug-in"
  is not something an adopter should design around.

#### Still to do for adoption readiness

- [x] **P6.2 — DONE (2026-07-31): `docs/ADOPTION.md`.** Four stages ordered by risk (consume ->
  shell primitives, which carry no IPC dependency -> the WebView2 host -> the IPC substrate), a
  primitive-by-primitive mapping table, the migration traps that cost real debugging here, and a
  permanent "stays yours" list. Every one of the 48 kit names it promises was checked against the API
  baselines and the client barrel — a guide that names a member the library lacks is worse than none.
  Writing it exposed NO further capability gap: the three the earlier plan called open questions were
  already covered (serving via `FolderMappings`, paths via `ShenoraPaths`, window state via
  `WindowStateManager`), and the one real gap (dynamic module claim/query) was closed first. Original
  note follows.
- [x] ~~P6.2 original~~ Write the adoption guide (`docs/`): which kit primitive replaces which hand-rolled
  piece, in the order an app should adopt them (shell primitives first — they carry no IPC
  dependency), what stays the app's own, and the migration notes for each. This is the artefact the
  sibling's session works from, so it must stand alone without this conversation.
- [x] **P6.3 — DONE (2026-07-31): close whatever the guide exposes as missing.** Writing the guide
  (P6.2) exposed nothing; writing the ADAPTERS (P6.4) exposed two things and both are closed — see
  below. That asymmetry is the finding worth carrying: a mapping table can be written from the API
  list, so it only catches names that do not exist. Only code that must actually *express* something
  finds a capability that is missing.
- [x] **P6.4 — DONE (2026-07-31): both adapters written, RUN, and sabotage-verified.** Throwaways in
  `devtools/_p6-adapters/{host,client}` (gitignored, never shipped — D21): a `BaseFacade` adapter over
  a foreign one-method module contract (17 assertions) and a `post`/`onMessage` shim over the bridge
  (18 assertions). The host adapter needs no Windows reference, so it re-proves D20 for the adapter
  layer. **Two real findings, both fixed:** the shipped `.d.ts` named the UMD global `React` and so
  required `@types/react` in the CONSUMER's global program (`FIX-LOG`), and the client event bus could
  not express a catch-all subscription while the host's `IEventBus` had shipped `SubscribeToAll`/
  `SubscribeToModule` all along — closed by adding both breadths (`CHANGELOG` `### Added`).
  **The three "almost fits" it recorded are now CLOSED too** (user direction, 2026-07-31: *"you really
  need to close those gaps"* — my triage had deferred them as workaroundable, and workaroundable is not
  the bar before a SemVer freeze). A `CancellationToken` flows the whole dispatch surface, supplied by
  the transport as a LIFETIME and cancelled on its dispose (**breaking** for implementers/overriders);
  `IEventBus.Emit` is the fire-and-forget twin so a synchronous caller need not discard a task and read
  kit source to know it is safe; `IpcErrorMapping` is public so an app whose failures travel as EVENTS
  can reuse the leak policy instead of retyping it. All three were re-verified from the ADAPTER's side,
  not just by unit tests — the throwaway probe now uses each and its 22 checks pass.
- [x] **P6.5 — DONE (2026-07-31): portability guidance (D20).** `docs/ADOPTION.md` Stage 4 is now the
  real recipe — the project shape, the contract-substitution table (dialogs/clipboard/URL launcher/UI
  dispatcher/interaction/paths), the "add it to the solution or the guard never runs" step, and an
  explicit NOT-portable list (the window-state stack, `OptimizedForm`, tray, splash, secondary
  windows, single-instance) so nobody goes looking for a contract that deliberately does not exist.
  Proven twice in-tree: `samples/Shenora.Sample.Logic` and P6.4's host adapter, which needed no
  Windows reference either. No D20 amendment needed — the portable contract set covered every case
  both exercises hit.
- [x] **P6.6 — DONE (2026-07-31): the remaining targets evaluated.** Read as capability CHECKPOINTS,
  never as specs. Findings:
  - **The video-library sibling — ONE REAL GAP, closed.** It serves local media to its page over a
    custom virtual host with HTTP `Range`/206, with an ADR recording that
    `SetVirtualHostNameToFolderMapping` cannot honour `Range`. Shenora's deferred-scheme handler was
    `Func<Uri, Task<(byte[], string)>>` — no request headers, no status, no response headers, whole
    file in memory — so it could not express that at all. Closed: `WebViewResourceRequest`/
    `WebViewResourceResponse` + `WebViewByteRange` (**breaking**, see CHANGELOG).
  - **Its native-player host is RECORDED, not built.** It composites a native surface with the web
    view; P5.6's caption-button clipping is the same mechanism, but the sibling solves this in its own
    leaf library and has not asked the kit for it. A capability nobody has asked for is speculation.
  - **The skin-manager sibling — no gap.** Its plug-in SDK (`IPlugin`/`IPluginContext`/
    `IPluginProgress`) is the APP's contract per D21; what it needs from the kit is dynamic module
    composition with claim/release (now present) and progress-as-notifications (present).
  - **The server-backed app — no gap, and it needs the least.** It serves over in-process Kestrel, so
    `Range` is ASP.NET's problem, not the kit's; its profile is shell-only (`Shenora.WinForms` plus
    optionally the WebView2 host with no resource provider). Its host-side IPC seam is already
    `IMessageDispatcher.DispatchAsync` — an HTTP endpoint calls it directly, so D16's transport
    pluggability holds without new surface.
  - **Feed-back status:** every API change P6 argued for has landed, so nothing is left owing before
    P7 freezes SemVer.

#### Increments (keep it runnable at every step — that is the phase's standing rule)

- [x] **P6.1 — DONE (2026-07-31): the consumption path is proven, and it was BROKEN.**
  Three consumers under `devtools/_p6-consumer/` (gitignored): a leaf one with ONE PackageReference
  that touches a type from every package, a `net10.0` portable one proving D20 through a PACKAGE
  reference for the first time, and an npm one type-checking the packed tarball under NodeNext plus a
  native-ESM import. **It found a real defect: the NuGet global cache beats every source, so a
  consumer silently restored a `Shenora.WebView2` packed before the D19 re-layer and `Shenora.WinForms`
  was absent from its graph — with no restore error.** `dev.mjs pack` now evicts this repo's ids at
  the packed version, closing it; `docs/RELEASING.md` + `docs/FIX-LOG.md` carry the detail. Also fixed:
  the npm README did not say `onPostError` is set via `configureBridge`. Original note follows.
- [x] ~~P6.1 original~~ `dev.mjs pack` → local feed + exact-version pinning
  per `docs/RELEASING.md`, npm tarball for `@shenora/react`. Nothing adopted yet; this proves the
  consumption path end-to-end from outside this repo. **This is also P1.2's blocker in disguise** —
  a real external consumer is the dry run.
> ⚠ The staged-adoption increments that used to sit here — "shell primitives INTO the app", "the
> WebView2 host INTO the app" — were **deleted on 2026-07-31**, not left as pending work. They
> instructed this repo to edit the sibling, which the user direction above supersedes: Shenora readies
> the LIBRARY and the adopting app's own session does the adoption, working from `docs/ADOPTION.md`
> (whose Stages 1 and 2 are exactly those two increments, written for the adopter). A stale item that
> contradicts a standing direction is worse than no item — the next session acts on it.

- [x] **P6.3a — DONE (2026-07-31): the client can send one-way, and shares module state.**
  Landed `ShenoraBridge.post` + `onPostError`/`maxTrackedPosts` and `createShenoraStore`, with 13
  new vitest cases; ALL FIVE new tripwires sabotage-verified (one was vacuous first time — a
  primitive-returning selector cannot prove the getSnapshot cache). The host side needed no new API,
  as designed. The two open items are now DONE too: the `ConfigureAwait(false)` rule text says which
  half is the dispatch path, and **the UI-thread claim is MEASURED** — a `SAMPLE.SLOW` route in both
  shapes, sampled with `SendMessageTimeout`: work left in the route stalls the UI thread 2 027 ms,
  the same work handed off stalls it 0 ms. Original note follows.
  **DESIGNED 2026-07-31 → `docs/2026-07-31-shenora-oneway-ipc-design.md`** (read it before
  implementing; it carries the three constraints that decide the shape, the two things it
  deliberately does NOT ship, and a verification plan). Summary of what it lands: a `post` that sends
  the SAME envelope with no pending entry and no timer (so no wire change and the mirror test stays
  untouched), reporting a FAILED response through a bridge-level error sink because an unmatched
  response is silently dropped today; the documented convention that a long operation is START via
  `invoke` returning `{ operationId }` + notifications carrying that id **in the PAYLOAD, never in
  `module`/`type`/`scope`** (the EventBus match cache keys on those and would grow unbounded); and a
  fix to the `ConfigureAwait(false)` rule text, which currently reads as blanket when it only ever
  applied to the dispatch path — as written it would argue a future session into keeping long work on
  the UI thread. **AND the part that matters most (user direction, second pass): a
  `createShenoraStore(module, …)` factory returning ONE hook that declares a feature's send, its
  event reducers and its shared state together.** That is a HARVEST, not an invention — three sibling
  apps each built it, one of them factored it out twice after "every host-backed store repeated" the
  same wiring. Two things it must get right that the first design draft missed: **snapshot THEN
  deltas** (a component mounting mid-operation has missed the events and a stream cannot be replayed
  — a progress strip mounts when you open a tab, long after the work started), and **one subscription
  per store no matter how many components read it**, since status/progress UI in an app is inherently
  many-watchers. Build it on React's `useSyncExternalStore` so the kit imposes NO state library (all
  three siblings reached for zustand; the npm package's only peer stays React). Original note
  follows. Today
  `@shenora/react`'s bridge has exactly one outbound call, `invoke()`, which allocates a correlation
  entry, awaits a response and times out at 30 s — so the kit makes the UI-thread-coupled,
  deadline-bearing path the ONLY path, i.e. the wrong default (see the section above). Add the
  missing half: a send that does not await, plus a documented convention for correlating a streamed
  result back to the invocation that started it — a handle returned by a quick request/response START
  is the obvious shape, and it gives progress and cancellation somewhere to live. Public surface, so
  it must land before P7 freezes SemVer. Mirror it on the host side (a route that answers with
  events rather than a response) and check `BaseFacade`/`Done()` still read correctly for it. A client shim
  mapping `post`/`onMessage` onto the bridge, and a host adapter presenting its module interface to
  `MessageDispatcher` — so all 28 modules and all ~148 call-sites keep working while the transport,
  the error boundary, the batching and the ready gate change underneath. **Not a migration to
  request/response:** per the section above, posting and answering with events is the right default
  here, so the adapters preserve it and request/response is adopted only where a call is quick and
  UI-thread-safe. What SHOULD change is the missing correlation, so a result or an error can be
  attributed to the invocation that caused it. **This is the increment that tests D21 for real**;
  write down every "the framework almost fits, but…" as it happens — that list is the phase's most
  valuable output, and the item below is the first entry, found before the adoption even started.
*(P6.5 and P6.6 are listed once, under “Still to do for adoption readiness” above.)*

#### Standing constraints for the phase

- **The adoption is the real test of P5.5's fixes.** Several P0s were latent-only — nothing in this
  repo triggered them — so a real consumer is what proves them fixed rather than merely patched:
  the DI composition (a facade injecting `IMessageDispatcher`), async disposal of singletons, and a
  relative `--app-root`. Exercise all three deliberately rather than hoping they come up.
- **Adopt against the CURRENT layering.** D19/D20 landed in P5.5, so referencing the leaf package
  pulls the rest transitively; nothing here should reference `Shenora.Core` directly.
- **Private specifics stay in `local/`.** Real names, paths and file-level findings from the survey
  live in `local/PROJECT_NOTES.md` and `local/EXTRACTION-MAP.md` — this file stays generic.

### P7 — Stabilisation + 1.0 (CURRENT)

- [x] **The API-surface gate is complete** (P5.5 H6 closed the hole: protected members, default
  values, `required`/`init`, attributes, parameter names, const values). 1.0 must not freeze behind a
  gate with a hole in it, and no longer would.
- [x] **XML-doc sweep — DONE 2026-07-31.** CS1591 is unsuppressed and, like every other warning, an
  ERROR. All five packages document every public and protected member. Adding an undocumented public
  member no longer compiles. Turning it on immediately caught a broken `<see cref/>` that had been
  invisible for as long as warnings were non-fatal.
- [x] **The last product leak is out of the library — DONE 2026-07-31 (user direction).**
  `CookieLoginFlow` moved to the desktop sample as `CookieLoginDriver`; D21 and D22 amended, since
  they had been justifying it to each other rather than testing it. A whole-surface audit by the
  documented method (sweep the API baselines for domain vocabulary) found no others: everything else
  it flagged is genuine browser or platform vocabulary.
- [x] **Per-package README sections + frontend build guidance — DONE 2026-07-31.** The README ships
  INSIDE every nupkg, so a `Shenora.Ipc` consumer reads the whole file: it now has a "Using each
  package" section per package — the smallest working snippet plus the one trap that costs an
  afternoon — rather than a single table addressed to nobody in particular. The P2/P3 carry-over
  landed with it: hash the assets, keep the HTML unhashed (the host serves it no-cache), split vendor
  code into stable chunks so a one-line app change does not invalidate everyone's bundle, and clear
  the dev server's pre-bundle cache after upgrading the client. Every C# name was checked against the
  API baselines and every TS name against the barrel — a README naming a member the library lacks is
  worse than none.
- [x] **`Shenora.Hosting.AspNetCore` go/no-go (D10) — DECIDED: NO-GO (2026-07-31).** Decided on
  evidence rather than reasoning: in the server-backed sibling the "SPA static-file policy" is five
  lines of ASP.NET, and the "loopback gate" is a two-line host check embedded in that app's own threat
  model — app security policy, not a reusable helper. Its host→page channel is the one-way event push
  the kit already provides, and its host-side IPC seam is already `IMessageDispatcher.DispatchAsync`.
  Recorded as an amendment on D10; the two-profile split stands, only the extra package is dropped.
- [ ] **First publish + repo public.** Blocked with P1.2 on a GitHub remote existing.

### P1 — Skeleton tail

- [ ] **P1.2 — Release workflow dry-run readiness.** Once a GitHub remote exists: run the Release
  workflow with `draft=true` against test feeds (or `--skip-duplicate` into a throwaway version)
  to validate OIDC config; document the nuget.org/npmjs.com trusted-publisher setup steps taken.

### Standing

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
