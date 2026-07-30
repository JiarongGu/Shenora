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

- [ ] **`RenderSession` must observe the tokens it accepts.** `OnUiAsync:213-235` checks the token
  once inside the posted delegate then awaits with no `WaitAsync(ct)` and no cap (zero `WaitAsync`
  in the file). Its sibling `LoginWindowController.cs:166-169,184` does it right AND documents it as
  the source's known gap. A page blocked in JS (`alert()`, spin loop) makes
  `GetHtmlAsync`/`ExecuteScriptAsync` never complete; the sample's RENDER route awaits those inside
  `await using`, so the lease never returns and the permit never releases — with `Capacity=2`, two
  such pages answer `RENDER_BUSY` for the process lifetime. FIX: `WaitAsync(ct)` + an `OpTimeout`
  option; the pool discards an instance whose op was abandoned.
- [ ] **Suppress script dialogs on session browsers.** `SessionBrowser.cs:112-120` leaves
  `AreDefaultScriptDialogsEnabled` true while `OffscreenWindow` parks the host off-screen at
  opacity 0 — an `alert()` blocks the renderer behind a dialog nobody can see or dismiss, which
  compounds the item above.
- [ ] **Unclosable login modal.** `LoginWindow.cs:274` finally order is
  `fallback.Dispose(); OnLoading?.Invoke(false); controller?.Finish(); form.Close();` — `OnLoading`
  is app code, so a throw (splash already disposed) escapes the `async void` handler, `Finish()`
  never runs, and the foreground `FormClosing` handler (`LoginWindowController.cs:67-72`) then
  cancels EVERY close including the user's and `Application.Exit`; `ShowDialog` never returns and
  the busy gate stays set. FIX: try/catch the callback; `Finish()`+`Close()` FIRST. Same for
  `:234` and the posted body behind `SetLoading` (`LoginWindowController.cs:239`).
- [ ] **The frameless-maximize ⇄ window-state seam (live in the reference composition).**
  `WindowStateManager.Save:60-61` reads `form.WindowState`, but frameless `OptimizedForm.Maximize()`
  (`:142-157`) only sets `_maximized` (pinned: `OptimizedFormTests:91` asserts `Normal`). Closing
  maximized persists `maximized:false` WITH the work-area rect as normal bounds → next launch fills
  the work area believing it is not maximized: `WM_NCCALCSIZE` takes the normal-inset branch (the
  border gap the whole technique removes), the page's glyph is wrong, and clicking maximize captures
  the work-area rect as `_restoreBounds` so RESTORE IS A PERMANENT NO-OP. FIX: an app-maximized
  seam (`IsAppMaximized` + app restore bounds) that `Save`/`Apply` prefer over
  `Form.WindowState`/`RestoreBounds`.
- [ ] **`AddMessageDispatcher` DI recursion → StackOverflow, no diagnostic.**
  `IpcServiceCollectionExtensions.cs:49-55` enumerates facades (`sp.GetServices<IModuleFacade>()`)
  INSIDE the `IMessageDispatcher` singleton factory. Any facade whose graph injects
  `IMessageDispatcher` — the documented cross-module `SendAsync` seam — re-enters the same factory;
  MS DI's cycle detection is call-site-based and cannot see a factory delegate re-entering the
  provider, and the cache entry isn't published yet → unbounded recursion, process death. FIX: map
  facades lazily (terminal middleware over a `Lazy<IModuleFacade[]>`) so the singleton is cached
  before enumeration; test the exact composition (`class F(IMessageDispatcher) : BaseFacade`).
- [ ] **`app.Dispose()` throws on a clean quit** when any singleton is `IAsyncDisposable`-only
  (`ShenoraApplication.cs:46,132`; MS DI throws for async-only captured disposables). Latent against
  Shenora's OWN `RenderSession`/`CoBrowseSession`. FIX: add `IAsyncDisposable` → `_provider.DisposeAsync()`.
- [ ] **Absolutize the resolved root/data paths** in `ShenoraPaths.Resolve`/`ResolveRoot:90-101`
  (returned verbatim today). `FileDialogs` sets `RestoreDirectory = false` on all three dialogs
  (`:146,174,218`, deliberate), so the process CWD moves after the first dialog and a relative
  `--app-root` re-resolves `DataDir` mid-session; it also defeats `SingleInstanceGuard.ChannelKey`
  hashing (two spellings of one install → two instances over the single-writer WebView2 folder).
- [ ] **No app callback runs unguarded inside a WebView2/WinForms event handler.** Unguarded today:
  `WebViewHost.cs:335-374` (`OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed` + every
  `_log?.Invoke`), the posted bodies in `DropZoneManager.cs:201-211` and
  `WindowCommandFacade.cs:147-158` (a throwing `ApplyTheme` or `form.Close()` takes the app down),
  `OptimizedForm.cs:242` (`WndProcHook` inside `WndProc` — before bootstrap that is the blocking-
  dialog failure mode), and the tap lists at `LoginWindowController.cs:32-38,58,241-247` (plain
  `List<T>` mutated off the UI thread while the UI thread `.ToArray()`s them).
- [ ] **Pool reset must fail closed.** `RenderSessionPool.ResetToBlankAsync:240-259` swallows the
  5 s `WaitAsync` outcome and returns `true` unconditionally, so the "a failed reset DISCARDS the
  instance" invariant is only reachable via a throw — a dead renderer is re-pooled forever, each
  later lease burning the 30 s nav cap. The test that pins the invariant only drives `ResetOverride`.
- [ ] **Re-check cancellation after the multi-second init** (`RenderSessionPool.cs:131`,
  `CoBrowseSession.cs:135`) and tear down instead of publishing — today a cancelled start still
  yields a live off-screen window, a browser process holding the profile lock, and (co-browse) a
  screencast writing into a channel nobody reads. Pass the linked token into the pool factory.
- [ ] **Root the CDP screencast receiver.** `CoBrowseSession.cs:146-158` keeps
  `GetDevToolsProtocolEventReceiver(...)` in a local; nothing holds it for the session's lifetime and
  `DisposeAsync` never detaches it — a stream that freezes after an arbitrary GC, with no error.
- [ ] **`RenderSession.OnNetwork`/`OnMessage` (`:121,158`) don't check `_disposed`,** so a
  post-return call attaches a tap to an instance another lease now owns — cross-lease disclosure in
  a package whose story is profile isolation.
- [ ] WinForms robustness tail: STA assertion + idempotence in `WinFormsBootstrap.Initialize:65-88`
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
- [ ] Client-side robustness tail (`@shenora/react`): a host message of literal `null` throws an
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

**H3 — The notification/ready gate and validation**

- [ ] **The ready gate has exactly one re-arm path,** so any spurious close stops notifications
  forever. `WebViewIpcBridge` `OnNavigationStarting` → `ResetClientReady()` unconditionally, while
  the client sends `READY` once per real page load (`bridge.ts:168`) — a same-document navigation
  the page survives leaves `_clientReady=false` permanently: buffer to 10 000, then drop-oldest,
  silently. Conversely the gate stays OPEN after a renderer crash until `Reload()`, so a tick in
  between drains a batch into a dead renderer. FIX: ignore same-document navigations; reset in the
  `ProcessFailed` path before reloading.
- [ ] **Validate the numeric options nobody validates** (ctor-time `ArgumentOutOfRangeException`,
  the `RenderSessionPool.cs:76` convention): `MaxQueuedNotifications = 0` makes `Enqueue` dequeue
  what it just enqueued — silently dropping EVERY notification for the process lifetime;
  `NotificationInterval < 1 ms` throws out of `Attach()` instead of construction;
  `SessionBrowserOptions.InitTimeout = 0` reports the misleading profile-lock diagnosis;
  `RenderSessionPoolOptions.OffscreenClientSize` default/zero gives a 0×0 viewport. Also null-check
  `ScopedContainerRouterOptions.ConfigureScope` (`required` but unvalidated → NRE as
  `UNKNOWN_ERROR`) and document that a scope container is a ROOT provider (so `AddScoped` inside
  `ConfigureScope` silently becomes a singleton).
- [ ] `WebViewHost.InitializeAsync` is public, non-idempotent and unguarded (`:51-75`) — a Retry
  button (its own timeout message says "start again") double-subscribes every policy handler, so
  every external link opens twice. Add an `_initialized` guard. Also make the timeout cover the
  WHOLE sequence (today each await gets its own full budget → 2× 25 s, and `ApplySettings`/
  `InjectScriptsAsync` are unbounded), and stop `WebViewEnvironment.cs:84-90` caching a FAULTED
  shared task permanently (one transient failure is currently terminal for the process).
- [ ] A mistyped `ResourcePrefix` degrades to a silent all-404 provider
  (`WebViewResourceProvider.cs:79-86`) — a black window with no error, while `ResolveStartUrl`
  throws actionably for the same class of mistake. Fail loudly.
- [ ] Don't put exception text in HTTP response bodies readable by page script
  (`WebViewHost.cs:219,245` — `NotFound($"Error: {ex.Message}")` with `Access-Control-Allow-Origin: *`).
- [ ] Cap the renderer auto-reload (`WebViewHost.cs:358-374`): rate-limited but with no terminal
  state, so a deterministically-crashing page reloads every 10 s forever, contradicting the option's
  own doc. Also make `AutoReloadCooldown` an option rather than a public static field (`:27`).

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
- [ ] **H4.2 — Retire the 14 marshal copies onto `WinFormsUiDispatcher`.** 14 hand-rolled copies
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
- [ ] **H4.3 — The portability proof.** A `net10.0` project `samples/Shenora.Sample.Logic` with one
  facade that picks a file, reads the clipboard and opens a URL, referenced by the desktop sample.
  Compiles with no Windows reference = the seam is real; a Windows type later dragged into a contract
  turns it red. Without this, portability is asserted rather than enforced. (~30 lines.) TWO
  conditions or it proves nothing: it must inject **`IUrlLauncher`**, not `IShellLauncher` (today's
  `SampleFacade` injects the Windows extension, so the facade gets SPLIT — portable routes out,
  reveal-in-Explorer and secondary windows stay in the desktop sample); and it must be added to
  `Shenora.slnx` — a SECOND solution edit after H5's, or `verify` never compiles the proof.
- [ ] **H4.4 — Make the declared `Sessions → Shenora.WebView2` edge actually carry something.** With
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
- [ ] **H4.5 —** Collapse the remaining duplicates, each to a named owner. **Visibility rule (from
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
- [ ] **H4.6 —** Consider one honest shared base for the three session types (`RenderSession` /
  `LoginWindowController` / `CoBrowseSession` share browser + host window + guarded navigate +
  script + taps + marshal). This is also the clean route to the deferred session-neutral rename: a
  neutral base with the login-flavoured type as the foreground subclass. Judgement call — only do it
  if the shared core is real after H4's earlier items land.
- [ ] **H4.7 —** Add the missing `ILogger<T>?` + `NullLogger` convention to `Shenora.WebView2.Sessions` — it
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

- [ ] **Extend the API baseline to `protected` members.** `ApiSurfaceTests.cs:55` uses
  `BindingFlags.Public` only, so `BaseFacade.RouteMessageAsync` — the one member EVERY consumer
  overrides — is outside the SemVer gate. Also invisible to the dump: default parameter values
  (`new EventBus()` works but the baseline renders the ctor as non-optional — dropping `= null` is a
  silent break, same shape at 8 sites), `init` vs `set`, `required` add/remove, `static` vs
  instance, attributes incl. `[JsonPropertyName]`, generic constraints, nullability, parameter names.
  And derive the assembly list from the baseline directory — `:15-22` hardcodes five
  `typeof(...).Assembly` literals, a second hand-maintained copy of `devtools/project.config.mjs`'s
  `packableProjects`; delete a line and the theory silently runs 4 cases with an orphaned baseline.
- [ ] **Add the cross-language mirror tripwire and the missing code.** `SCOPE_REQUIRED` exists in
  `IpcErrorCodes.cs:20` and is emitted by `ScopedContainerRouter.cs:164`, but is absent from
  `types.ts:25-34` — so a scoped app cannot match it by constant, while `ARCHITECTURE.md` claims the
  mirror is "name-for-name". Root cause is structural: the two suites each assert their own
  hand-written literals and nothing compares the sets. FIX: a check that parses the C# consts and
  asserts set equality with `types.ts`.
- [ ] **`'\0'`-join the client event-bus keys and add a scope filter.** `eventBus.ts:19,37` uses
  `` `${module}.${type}` `` — the exact '.'-collision `ipc-contracts.md` forbids and that
  `EventBus.cs:85` fixed with `'\0'` (its comment spells the collision out). The TS key also omits
  `scope` while the host keys on it and the wire carries it, so a scoped app's `useShenoraEvent`
  fires for every scope with no filter available.
- [ ] **Fix `BaseModuleService`'s generic constraint** (`moduleService.ts:21`). `TRequests extends
  Record<string, unknown>` is not satisfiable by a plain `interface` (no implicit index signature),
  so the TSDoc example at `:10` and the README snippet DON'T COMPILE (TS2344) — the first thing an
  adopter copies. Satisfying it the way `windowCommands.ts:9` does widens `keyof TRequests & string`
  to `string`, so typos compile and payloads collapse to `unknown`: the flagship typed-service
  feature currently checks nothing. FIX: `TRequests extends object`, drop `extends Record<…>` from
  the callers and both doc snippets.
- [ ] **Give form-dependent facades a first-class registration seam.** The reference composition has
  to downcast — `MainForm.cs:85` `if (dispatcher is MessageDispatcher concrete)` — because
  `IMessageDispatcher` exposes only `DispatchAsync`/`SendAsync`, and `WindowCommandFacade.cs:41-43`
  documents a path (`AddMessageDispatcher`'s configure callback) that CANNOT work: that callback runs
  at provider-build time, before the form exists. The `if` has no `else`, so a different
  `IMessageDispatcher` registration (or a future decorator) silently drops WINDOW + DROP_ZONE +
  RENDER and the frameless title bar just stops working. RECOMMENDED: have those facades resolve the
  main form lazily via the existing `IFormInteraction` so they register as ordinary DI facades
  through `AddModuleFacade` (smaller surface change than widening the interface). Fix the
  `WindowCommandFacade` doc either way.
- [ ] Trim surface that doesn't earn its keep, and add what's missing: `DpiHelper.ScalePixels`/
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

- [ ] Test-suite health: add `[Collection]`/`xunit.runner.json` — there is NO parallelization
  control anywhere while the suite creates real message pumps and asserts wall-clock budgets
  (a 150 ms `TryAcquire` assertion, a `Thread.Sleep(150)` mutex handoff, a 10 s poll deadline over
  six real window threads), which is the flake vector on a loaded box; de-duplicate the doubles
  (`RunSta` ×3 byte-identical + a 4th spelling → `tests/…/TestSupport/Sta.cs`; `FakeTransport` ×4
  full classes + 2 inline literals in the npm suite; three `IWindowStateStore` fakes where one is a
  superset; 5 `IpcRequest` factories; 7 copies of the temp-dir create/delete pair); make the npm
  suite use the exported `IpcCategories` constant instead of hand-built `{category:'ipc'}` literals
  in 6 files (the C# side already asserts against the constant — today both halves could drift
  green); add a `vitest.config.ts` (no config today ⇒ `globals:false` ⇒ RTL's `afterEach(cleanup)`
  never registers, so every `renderHook` stays mounted for the rest of the file with live
  subscriptions and listeners — green only because each test builds a private transport); gate the
  npm barrel (deleting any `index.ts` export fails no test) and cover `createWebView2Transport`
  (zero references, two regression-prone behaviors); fill the untested seams —
  `CookieLoginFlow.DriveAsync`'s hook mapping (swapping two entries keeps all 8 cases green), every
  public member of `LoginWindowController`, `CoBrowseSession.DispatchInputAsync`/`ReadHotspotsAsync`/
  `Frames`/`DisposeAsync`, `RenderSession.OnMessage`/`OnNetwork` bookkeeping, and
  `SessionBrowserOptions.RequestFilter` (the SSRF-shaped predicate whose sibling has a seam test and
  in which a live `about:blank` bug is already on record); relax the implementation-detail assertions
  (exact exception-message equality, an internal type's NAME, `Controls[0].Controls[0]` + re-typed
  production defaults, exact STJ number formatting).
- [ ] Docs drift (all verified, both sides cited in the reviewer reports): `README.md:28` +
  `Shenora.Core.csproj:6` say Core depends on Microsoft.Extensions **abstractions**, contradicting
  D17 and the actual reference — and this text ships in every nupkg; the same csproj advertises a
  "UI-dispatcher seam" that does not exist — H4.1 makes that claim TRUE (D20's `IUiDispatcher`)
  rather than deleting it, so fix this line only after H4.1 lands; and `Shenora.WinForms.csproj:8` advertises "drag-drop
  overlays" (they live in `Shenora.WebView2`) and "the UI-thread dispatcher" (no such type);
  `README.md:33` describes a bridge API of `invoke`/`send`/`subscribe` where the real surface is
  `isAvailable`/`invoke`/`notifyReady`/`dispose`; `docs/ROADMAP.md` `## Remaining` P1 lists only
  already-DONE items and omits the one genuinely pending task (P1.2) — fixed in this pass, keep it
  that way; `CHANGELOG.md` has no record of `0776f37` (the npm ESM fix that made the PUBLISHED
  artifact importable) and no `### Fixed` section at all, and its "Newest first" header is
  contradicted by ascending entries; `docs/ARCHITECTURE.md:29` says the test project references all
  five src projects (it references four), its tree shows projects the solution doesn't list, it
  writes `WindowCommandFacade(+Options)` for a type named `WindowCommandOptions`, it attributes the
  cache-header policy to the wrong type, and it never names four public extension classes;
  `docs/README.md:26` + `CLAUDE.md:9` still list four packable projects (omitting
  `Shenora.WebView2.Sessions`) and `CLAUDE.md:14` still says "D1–D12" against a log running to D18;
  three implemented `dev.mjs` commands (`rclick`, `move`, `drag`) are undocumented everywhere
  including the tool's own help.
- [ ] Dead weight: stale comments describing shipped work (`IShenoraModule.cs:15` "land on top of
  this in later phases" — both shipped in P4; `SessionBrowser.cs:13-14` "see `LoginWindow` once it
  ships" — it shipped in the same commit); `'TODO'` as the example module name in shipped public
  docs (`moduleService.ts:12,23`, `devInterceptor.ts:11-12` — indistinguishable from an
  unfinished-work marker in the published npm docs, and the only `TODO` hits in `src/`); the sample
  sets `dropClassName: 'drop-hover'` but ships no rule for it, so the e2e subject cannot demonstrate
  the hover half of the drop-zone contract; `void getBridge().notifyReady()` in the sample produces
  an unhandled rejection on a failed handshake — and it is the pattern consumers copy.
- [ ] Document the `notifyReady` → `ClearAll` ordering contract (or make it order-independent).
  The host wipes all drop zones on every handshake; React runs CHILD effects before PARENT effects,
  so the natural reading of "call `notifyReady` once at startup" (a root-component effect) posts
  `REGISTER` before `READY` → `ClearAll` destroys the overlay while the client still believes it is
  registered, leaving a silently dead zone. The sample only works because both calls live in one
  component in the right order.

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
- [ ] Still owed, as the batches land: containment-checked static serving (H1) and
  navigation-policy-enforced-at-`NavigationStarting` (H1) → extend `webview2-hosting.md` (rename its
  scope line to cover WebView2 **and** WinForms hosting).
- [ ] **The one genuinely new file: `winforms-shell.md`** — `src/Shenora.WinForms/` has NO knowledge
  row at all, while its earned traps are real and expensive: the frameless-maximize ⇄ window-state
  seam, `TrayIcon` cancelling a programmatic `Close()` because WinForms reports `UserClosing`,
  `SingleInstanceGuard.TryAcquire` not being idempotent, and `SecondaryWindows`' registry/wait
  ordering. Add it with `dev.mjs knowledge new winforms-shell` + a `RULES_INDEX` row when H2 lands.
- [ ] Unclaimed from the fix log: the `SemaphoreSlim.Dispose()`-wedges-a-cancelled-waiter root cause
  never became a rule, though `/fix-log`'s own closing step requires it → one bullet somewhere in
  the WinForms/sessions rule.
- [ ] Then `node devtools/dev.mjs knowledge check` + `… knowledge footprint` (the always-loaded tier
  grew this pass — confirm the budget is still sane).

**H9 — Co-browse: ship primitives + lifecycle hooks, not the product (D21) — AFTER the re-layer**

User direction 2026-07-30: *"co-browse itself is a whole feature — you just need to provide enough
interface for other systems to plug/hook onto its cycle; you don't really need to implement the entire
business feature."* Rationale + the full surface audit: **D21**. Sequenced after H4.1/H4.6 on purpose —
this is an API redesign and must not ride inside a package-boundary move. It is a **pre-1.0 breaking
change** to `CoBrowseSession`, so it belongs in this phase, not after.

Keep as primitives (the earned mechanics — do NOT churn these): `StartAsync`/`DisposeAsync`, `Frames`
as the bounded latest-wins channel, the screencast ack protocol, and 1:1 viewport mirroring via
`Emulation.setDeviceMetricsOverride` (NOT a physical resize).

- [ ] **H9.1 — Replace `DispatchInputAsync(string)` with a typed input seam.** Today it takes the
  source app's wire protocol as an opaque JSON string, so a consumer cannot know what to pass without
  reading that app's client. Ship typed primitives instead (pointer move/down/up/wheel at FRACTION
  coordinates, text insert, VK-mapped key) — the fraction-coordinate choice stays, it is what makes
  the protocol resolution-independent. Keep the verbatim JSON mapping as an explicitly-named
  **adoption shim** (e.g. a `CoBrowseInput.FromLegacyJson` parser) so the existing sibling migrates
  mechanically, but the framework CONTRACT stops being one app's wire format. Also closes H6's
  "`DispatchInputAsync` has no `CancellationToken`" item — add it while the signature changes anyway,
  since adding it after 1.0 is binary-breaking.
- [ ] **H9.2 — Move `ReadHotspotsAsync()` out of the core surface.** Returning a stringly-typed list
  of "clickable rect fractions" is a co-browse UX decision, not a browser primitive. The app can run
  its own script through the session controller. If a helper is still wanted, it ships as an
  explicitly optional, TYPED extra — never as core surface, and never as `Task<string>`.
- [ ] **H9.3 — Add the lifecycle hooks that are actually missing (this half is a live bug).** Nothing
  signals the session ending or faulting: `ProcessFailed` is unwired (H4.4 wires it for pool
  instances — do the same here), so a renderer crash leaves the frame channel never completed and the
  app's reader **waiting forever**. Complete the channel on death and surface an ended/faulted hook
  with a reason. Add frame geometry too (size/viewport, ideally per frame or via a viewport-changed
  hook): today the app receives raw bytes with no geometry, so it cannot map input coordinates back —
  which is why a caller ends up needing H9.1's protocol anyway.
- [ ] **H9.4 — Land it on the neutral session controller from H4.6**, so `CoBrowseSession.Controller`
  stops being typed `LoginWindowController`. That public member is the sharpest evidence for H4.6: a
  co-browse consumer — nothing to do with signing in — must program against a login-named type, and
  its busy-gate errors speak `LOGIN_BUSY`/`LOGIN_CANCELLED`. Free to fix now, breaking after 1.0.
- [ ] **H9.5 — Prove the seam the way the render pool is proven:** the sample composes the *product*
  (transport + a viewer) over the primitives, exactly as its `RENDER` route composes the pool. If the
  sample cannot build a minimal co-browse product without reaching into internals, the seam is wrong.
  Keep it small — this is a seam test, not a co-browse demo app.

### P1 — Skeleton tail

- [ ] **P1.2 — Release workflow dry-run readiness.** Once a GitHub remote exists: run the Release
  workflow with `draft=true` against test feeds (or `--skip-duplicate` into a throwaway version)
  to validate OIDC config; document the nuget.org/npmjs.com trusted-publisher setup steps taken.

### Standing

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
