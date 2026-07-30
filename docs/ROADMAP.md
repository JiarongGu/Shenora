# ROADMAP.md — done + remaining

`## Done` is the durable record (narrative, newest first — what changed, why, how it was
verified). `## Remaining` is the phase plan; items graduate here from `TASKS.md` when finished.

## Done

### 2026-07-30 — P5.5 batches H1 + H5: the security fixes and the gate that was supposed to catch them

First half of the consolidation phase, deliberately sequenced ahead of the re-layer so a
path-traversal fix wasn't waiting behind a refactor.

**H5 — the gate holes closed first**, because until they were, "verified" meant less than it
claimed: `Shenora.slnx` carried an EMPTY `samples` folder (and omitted `Shenora.Core`), so
`verify` never compiled the reference composition or the e2e subject; `dev.mjs test <typo>` exited 0
having run nothing; and `check-sensitive` silently degraded to two structural patterns whenever the
gitignored `local/sensitive-patterns.txt` was absent — every fresh clone and every CI run, i.e. the
private-name half never ran in the release gate. Now: samples + Core are in the solution, `verify`
additionally type-checks the sample web app and runs `doctor`, unknown test targets fail, warnings
are errors for `src/` and no longer hidden by `-clp:ErrorsOnly`, the scanner fails CLOSED (with an
explicit `--allow-builtins-only` opt-in that the release workflow now uses) and also scans file
paths and renamed/copied files, a new `commit-msg` hook scans commit messages, `create_tag: false`
no longer produces a tag, CPM is enforced from the root shim, and the npm package gained
`prepublishOnly`. **The first build with the samples compiled and warnings-as-errors on came back
0 warnings / 0 errors** — the sample was not, in fact, broken; it simply wasn't being checked.

**H1 — five fixes, four of them reachable by content the app doesn't control.** Arbitrary file read
through file-mode serving (no path containment, and `Path.Combine` returns a rooted second argument
verbatim); `NavigationGuard` bypassed by redirects; an unserializable notification payload crashing
the UI thread and losing its whole batch; `ClearProfile`'s recursive delete accepting a traversing
path; and a leaked `Process` handle per external link click. Root causes and verification per fix in
`docs/FIX-LOG.md`; new public API (`LoginWindow.ComposeProfileDirectory`) and the behaviour changes
in `CHANGELOG.md`.

One fix had to be **adapted rather than implemented as specified**, and the adaptation is the
interesting part: `CoreWebView2NavigationStartingEventArgs` has no deferral, so an async guard cannot
be awaited in that event at all. What shipped is a synchronous cross-host rule (the pool records the
host the guard approved and cancels unvetted hops), which closes the documented
`302 → 127.0.0.1` vector, while `SessionBrowserOptions.RequestFilter` — synchronous by design and
already wired with `WebResourceContext.All` — remains the seam for full redirect/subresource policy.
Both options now say so instead of over-promising. Not applied to `LoginWindow`: interactive OAuth
legitimately redirects across hosts.

Verified: `dev.mjs verify` PASSED — 346 dotnet + 39 vitest, sample web typecheck, sensitive scan,
knowledge check, doctor. 20 new tests (7 escaping paths + 3 legitimate CJK/spaced paths + a
sibling-prefix case; 2 notification-serialize cases; 4 traversal + 9 unsafe-segment + 2 composition
cases). The `Shenora.WebView2.Sessions` API baseline drifted by exactly one intentional line and was
reviewed before promotion.

### 2026-07-30 — P5 increment 4 + phase review: sessions proven live — P5 COMPLETE

The sample gains the sessions demo: a `RenderSessionPool` (capacity 2, own `sessions/render`
profile, a loopback-only navigation guard) and a `RENDER`/`PROBE` route that leases a pooled
off-screen session, navigates the requested page, and returns its LIVE-DOM title + HTML length.
The web page adds a "render this page off-screen" button. PROVEN LIVE (dev mode, CDP through
`window.__shenora`; screenshot `p54-dev-render.png`): first PROBE created the instance and
returned `"Shenora Sample"` + ~3.8 KB of live DOM (its JS ran off-screen), a second PROBE reused
the warm instance in ~250 ms, a non-loopback URL came back as structured `RENDER_REFUSED` (the
guard seam), and the page button showed the success line. Graceful close exits code 0 (the pool
disposes with the window).

**Phase review (adversarial subagent over the full P5 diff) — real findings fixed:** the
`LoginWindowController` assumed a foreground login window, so an off-screen co-browse host (which
reuses it) would (1) veto `Application.Exit` via its hold-close handler and (2) pop an invisible
window on screen if a driver called `Reveal` — both now gated behind a `foreground` flag (the
background co-browse controller's window-managing calls are inert); (3) a failed session init
leaked the WebView2 control (and could finish attaching a browser process holding the profile
lock) — the pool now disposes control + fresh host on the failure path; (4) a silent-refresh
login showed an OWNED modal, disabling the app's main window while invisible — now ownerless;
(5) the loading-splash fallback never fired `onLoading(false)` if the driver threw before
signalling — now dropped unconditionally in the finally; (6) `RenderSessionPool.Dispose` hung a
queued lease forever and could re-pool an instance into a dead pool — Dispose now cancels queued
waiters via a dispose token and `Return` discards once disposed; (7) the controller's UI marshal
checked `InvokeRequired` without `IsHandleCreated` (the family pre-handle trap) — fixed;
(8) `CoBrowseSession.StartAsync`'s `BeginInvoke` was unguarded — now faults the task + completes
the frame channel; (9) `CoBrowseSession.DisposeAsync` could hang on a stopped message loop —
completes the frame reader first, then fires UI cleanup without awaiting; (10) drag was
impossible because mouse-move always sent `buttons:0` — a held button now carries through moves;
(11) every mouse event round-tripped a script call to read the viewport (and its fallback
disagreed with the initial viewport, misplacing clicks) — the emulated viewport is now cached;
(12) the request filter passed `about:blank`/pre-commit sources as the page host, so a same-host
filter could 403 the page's own document — non-http(s) sources are nulled; (13) the init-timeout
guidance only wrapped the core attach, not environment creation — now both; (14) the sample
`RENDER` lease could hang forever behind a wedged pool — bounded with a 60 s `RENDER_BUSY`; plus
the packaging gap (the new package was missing from `dev.mjs pack`'s list and the README) and
the controller's raw-event taps silently replaced each other (now accumulate like `OnMessage`).
A live-caught hang: `SemaphoreSlim.Dispose()` racing a just-cancelled waiter wedged it (fix-log);
resolved by not disposing the semaphore (it never allocated a wait handle). Re-verified 318
dotnet + 39 vitest green; sample re-proven live. Deferred deliberately (recorded in the private
notes): renaming the login-named types to session-neutral names (pre-1.0, revisit if a pure
co-browse consumer finds it awkward), and STA-wrapping the new pool/login tests (the earned rule's
trigger — `AllowDrop`/OLE — doesn't apply here; the tests are deterministically green).

### 2026-07-30 — P5 increment 3: co-browse streaming

`CoBrowseSession` ports the server-backed sibling's co-browse core with the transport cut away
as the seam: the generic package owns the off-screen browser (fixed generous physical surface —
the CSS viewport is driven purely by `Emulation.setDeviceMetricsOverride`, DPI-independent),
the screencast (`Page.startScreencast` JPEGs → a bounded latest-frame-wins channel, frame-acked;
`everyNthFrame:1` because CDP only emits on visual change, so idle bandwidth is ~0), the input
dispatch (the source's wire protocol VERBATIM for mechanical adoption — 1:1 viewport mirroring
via device metrics alone with the measured clamps, fraction→CSS-px mouse/wheel, `insertText`
typing, special keys/shortcuts synthesized with the modifier bitmask + the Windows virtual-key
map), the hotspot extraction script (clickable rects as viewport fractions — the client only
has pixels), and the SAME `LoginWindowController` primitives over the streamed page (the
source's deliberate reuse, kept). The app keeps the WebSocket pumps, the send lock, and the
polling cadence — its transport, its schema. Formatting is invariant-culture throughout (the
source's live "1,50-is-broken-JSON" locale fix, pinned by a de-DE test). Verified: 16 new tests
over the pure protocol builders (clamps, VK map matrix, modifier bitmask, down/up pairing,
fraction scaling, locale pinning, option validation) — 315 dotnet + 39 vitest green; baseline
promoted (additions only). Live streaming is the P5.4 e2e's subject.

### 2026-07-30 — P5 increments 1–2: the sessions package — offscreen render pool + login windows

New package `Shenora.WebView2.Sessions` (D14), extracted from the server-backed sibling's
render/session/login stack merged with the primary sibling's external-login service. P5.1: the
one auxiliary-browser configuration path (`SessionBrowser` — per-profile environment,
quiet-start + background-throttling-off arguments, hardening, `RequestFilter` seam, the 25 s
init-timeout guard) and the bounded LIFO render pool (`RenderSessionPool`/`RenderSession` —
capacity waits queue rather than fail, a creation failure releases its slot, a failed
about:blank reset DISCARDS the poisoned instance, `NavigationGuard` is the generalized SSRF
policy seam; one shared hidden off-screen host in runtime mode, visible cascaded windows in dev
mode). P5.2: the login stack — `LoginWindow` runs a caller-supplied driver over
`LoginWindowController` primitives inside a modal nested loop, with the sibling-proven
mechanics ported: busy serialization with EXACTLY-ONCE completion (the dropped-post wedge fixed
via the cancellation-token fallback — and the source's unused-token gap fixed with observed
tokens throughout), the user's close HELD open for a final cookie read, the silent-refresh
off-screen shape (`RevealImmediately=false` + idempotent `Reveal()` — "no interaction ⇒ no
window"), desktop-width default sizing (narrow windows reflow providers to mobile layouts with
NO login UI — measured), `FitToBox` CSS→physical DPI math, per-provider AND per-sub-account
profile scoping documented as the security boundary it is, and `ClearProfile` as real logout.
`CookieLoginFlow` is the built-in driver: poll for a FRESHLY-SET auth cookie judged against a
pre-navigation baseline (a stale profile cookie never captures — the dead-session incident),
reading from the SEPARATE `CookieReadUrl` origin (the parent-domain capture bug), with the
no-anonymous-blob gate held even on the final close read. Verified: 26 new tests over internal
seams (pool accounting/LIFO/discard/cancellation with a fake factory; flow freshness/reveal
timing/close capture/gating via the hooks seam; busy-gate + token-fallback mechanics with a
deliberately unpumped anchor; `ComputeFitSize` DPI cases; `ClearProfile`) — 299 dotnet + 39
vitest green; Sessions API baseline reviewed and promoted (additions only). Real browser
behavior is the P5.4 sample/e2e's subject, per the family precedent.

### 2026-07-30 — P1.1: local-feed consumption smoke — and the real bug it caught

The pack output was consumed like an external app would (the rerunnable scratch consumer lives
untracked in `devtools/_p11-consumer/`): NuGet side — a standalone `net10.0-windows` console
project with a `nuget.config` pointing at `publish/packages` + nuget.org, exact-pinned
`[0.1.0]` references to the two leaf packages (Core/Ipc resolve transitively), CPM opted out —
restored, built, and ran a live dispatch round-trip printing all four assembly versions at
0.1.0. npm side — the packed tarball installed with `react` into a scratch project and imported
under PLAIN NODE ESM… which FAILED, catching a real packaging bug the bundler-based dev loop
structurally cannot see: the emitted `dist/*.js` carried extensionless relative imports
(`from './types'`) because `moduleResolution: bundler` never requires extensions — fine in
Vite/vitest, rejected by Node's own loader. Fixed with explicit `.js` extensions on every
relative specifier and the package tsconfig moved to `NodeNext`, which makes a missing
extension a build error (prevention; full entry in `docs/FIX-LOG.md`). Re-packed, the npm smoke
now resolves every export under plain Node; full `verify` green (273 dotnet + 39 vitest). The
consumption recipe is recorded in `docs/RELEASING.md`.

### 2026-07-30 — P4 increment 6: the P4 surface proven live (sample + e2e) — P4 feature-complete

The samples become the full P4 reference composition. Desktop: `MainForm` is now a FRAMELESS
`OptimizedForm` (chrome colors = the app background, DWM border matched — no visible frame), the
window-facing facades map late in the form's constructor (`WindowCommandFacade` wired to the
manual maximize path, `DropZoneFacade` over a `DropZoneManager`), the ready handshake clears
stale drop zones before starting the tick source, a launcher-style `TrayIcon` (no close-to-tray,
so the e2e's graceful close still exits), `SecondaryWindows` + `SampleFacade` routes
(OPEN/HAS/CLOSE_PANEL + PICK_FILE/REVEAL for the manual dialog/shell demos). Web: the page
renders its own title bar (drag via `startDrag`, min/max/close buttons, a top resize strip,
`useWindowMaximized` glyph), a `useDropZone` target, and the secondary-window controls.
PROVEN LIVE (screenshots gitignored in `devtools/screenshots/`): dev (`p46-dev-frameless.png`)
and packaged (`p46-packaged.png`) both show the frameless window with page-owned chrome and
every status line green. CDP drive (`window.__shenora`): `WINDOW IS_MAXIMIZED` false →
`TOGGLE_MAXIMIZE` → true → restored (the manual work-area maximize end-to-end);
`SAMPLE OPEN_PANEL` → `HAS_PANEL` true → `CLOSE_PANEL` → false; the page's drop zone
auto-registered (`DROP_ZONE REGISTER:ok` + bounds UPDATEs + SHOW traffic — StrictMode's
mount-unmount-remount sequence handled exactly as the ported fix comments promise). Native
input drive: `dev.mjs click` on the page's panel button fired `OPEN_PANEL` (win-input works
against the new UI), and `input list` showed BOTH top-level windows — the frameless main window
and `Shenora Sample — panel` on its own STA thread. Graceful closes exit code 0 in both modes.

**Phase review (adversarial subagent over the full diff) — 10 real findings, all fixed:**
(1) the drop-zone manager's pre-handle marshal re-invoked its caller → unbounded recursion →
uncatchable StackOverflow (reachable via startup-failure disposal) — pre-handle now proceeds
inline; (2) `FormInteraction` held its lock across a blocking `Invoke` — the classic pool↔UI
deadlock the family already documented — now `BeginInvoke`; (3) frameless `SC_RESTORE` was
swallowed while minimized+maximized, stranding the window in the taskbar — the intercept now
defers to `DefWindowProc` when minimized and `RestoreFromMax` un-minimizes first; (4)
`SecondaryWindows.Post` ran inline on the CALLER's thread pre-handle — an `Activate` racing
creation would create the handle on the wrong thread and kill the pump — pre-handle is now a
no-op with flag-carried intent (`HandleCreated` re-checks `CloseRequested`); (5) `useDropZone`'s
in-flight REGISTER ack could land after teardown and mark the destroyed zone registered
(StrictMode's default sequence!) — epoch-guarded now; (6) `SecondaryWindows.Dispose` didn't
wait for the pumps, losing geometry saves at exit — bounded drain added; (7) `TrayIcon`'s
`_exiting` wedged after a canceled close (next user close would EXIT) and the icon hid before
the close was certain — reset-on-cancel + hide moved to `FormClosed` (+ a Font handle leak);
(8) `ScopedContainerRouter` invalidate/dispose racing an in-flight creation leaked the built
provider — `DisposeScope` now observes the `Lazy` (waiting out in-flight builds) and `Dispose`
drains; (9) the occlusion check interpolated the app-supplied zone id raw into a script —
JSON-injected + `CSS.escape`d per the injection rule; (10) `START_DRAG` while manually
maximized dragged a work-area-sized window with stale restore bounds — the facade refuses it.
Regression tests cover 1, 3, 5, 6, 7, 8. Re-verified: 273 dotnet + 39 vitest green; `verify`
PASSED. **P4 (modules + native services) is complete.**

### 2026-07-30 — P4 increment 5: secondary windows + tray

`Shenora.WinForms` gains `SecondaryWindows` — the primary sibling's ~630-line secondary-window
service decomposed to its generic core: named windows, each opened on its OWN STA thread with
its own message pump (the source's preload/sync-create split existed only because callers ran
the thread; the registry now owns it), with the app's `CreateForm` factory holding everything
the source hardcoded (content, sessions, theme). Geometry persistence reuses the P2
window-state stack per name (`IWindowStateStore` per window — the extraction map's
"IWindowGeometryStore seam" realized; logical store / physical restore / off-screen recovery
come along free). Kept post-mortems: the non-blocking close discipline (a blocking `Invoke`
from the IPC thread deadlocked the source during scope switches). Deviations: opening an
existing name ACTIVATES it (the source's close-and-recreate churned; its login-window sibling
proof focuses), and a close racing window creation is caught by a flag instead of being lost.
`TrayIcon(+Options)` generalizes the server-backed sibling's tray: NotifyIcon lifecycle,
Open/app-items/Exit menu composition (`ConfigureMenu` gives the app the raw
`ContextMenuStrip` — no DSL), double-click restore, the close-to-tray FormClosing dance, and
`TrayMenuColors` — the parameterized port of its dark menu renderer (disabled-text legibility
on dark surfaces was its measured reason to exist); null colors = stock renderer, the palette
is the app's (D13). Verified: 268 dotnet + 38 vitest green (+10: own-STA-thread pumps with
polling, activate-on-existing, raced close, state-store save-on-close, failing-factory cleanup,
close-all; tray menu composition/order, close-to-tray → hide then real exit, opt-out, dispose
detach); WinForms baseline promoted (additions only); `verify` PASSED.

### 2026-07-30 — P4 increment 4: drag-drop zones + `useDropZone` (+ the P2.3b DPI tail)

The third-most-copied component in the family (one sibling's copy was literally annotated
"ported from…" another) lands once: `Shenora.WebView2` gains `DropZoneManager(+Options)` —
transparent `WS_EX_TRANSPARENT` overlays positioned over page elements to capture REAL OS file
paths (the DOM only ever sees blob URLs), including drags from other apps while the window is in
the background (an inactive form always shows its overlays). Ported with the measured
discipline: non-blocking `MarshalToUi` (a blocking `Invoke` off the UI thread caused an AppHang
in the source), form-activation visibility sync, the DOM occlusion check (a covered zone must
not light up), the disposed-during-async `Dead` guard, and event-handler detach on dispose.
Events emit on `IEventBus` (`DROP_ZONE`: DRAG_ENTER/DRAG_LEAVE/FILE_DROP) — the bridge's
wildcard forwarding ships them to the page, decoupling the manager from the transport.
`DropZoneFacade` provides the REGISTER/UPDATE/UNREGISTER/SHOW routes. The P2.3b DPI tail lands
here: CSS→physical conversion now uses the CONTROL's per-monitor `DeviceDpi` (the source used a
process-global scale — wrong on mixed-DPI setups), and the manager stores each zone's CSS rect
and re-applies all bounds on `Form.DpiChanged`. Placed in the WebView2 package (the design
sketch said WinForms) because it drives the WebView and needs Ipc — same dependency reality as
the window commands. `@shenora/react` gains `useDropZone` with the source's fix-history kept
(unregister-on-attempted so a fast unmount tears down an in-flight REGISTER; duplicate-REGISTER
guard; teardown on `enabled` flip) and generalized: zero dependencies (local debounce, no uuid
lib) and NO CSS shipped (headless D13 — the drop class is applied, the app styles it).
Verified: 258 dotnet + 38 vitest green (+12: overlay lifecycle/parenting/bounds on STA threads,
DPI re-apply from stored rects, bus wire shapes, facade route matrix incl. structured missing
payload, hook register/unregister/drop-routing/class-toggle/SHOW/disabled/flip); real drags +
occlusion are the P4.6 e2e's subject; WebView2 baseline promoted (additions only); `verify`
PASSED.

### 2026-07-30 — P4 increment 3: the native desktop services

`Shenora.WinForms` gains the service layer the source apps hand-rolled, all TryAdd-registered by
`UseWinForms` so every app gets them and any registration can be replaced:
`IFormInteraction`/`FormInteraction` (the main-window registry — the runner registers the form
automatically — plus nested modal blocking via the native `Enabled` property; the handle read is
fixed to answer `Zero` before creation, where the source's `Invoke` dance would have CREATED the
handle on the wrong thread), `IFileDialogs`/`FileDialogs(+Options)` with the wire-friendly
`FileDialogOptions`/`Filter`/`Result` models and the `IFileDialogPathStore` seam (generalizing
the source's settings-service coupling): every dialog on a DEDICATED STA thread (the measured
WebView2 conflict), owned by the main window for z-order, main window blocked while up, per-key
last-directory memory with stale-entry fallthrough, the folder-or-file `OpenFileDialog` trick
kept, and a NEW `SaveFileAsync` in the same pattern — failures now THROW (the source flattened
exception text into a wire-bound string, the exact leak shape §5 forbids);
`IShellLauncher`/`ShellLauncher` (reveal-in-Explorer with the Windows 11 handle-leak fix, shell
"open"-verb directories — not `explorer.exe`, which orphaned processes — http/https-only
`OpenUrl` matching the new-window policy, `LaunchProcess`);
`IClipboardService`/`ClipboardService` (STA-marshalled text get/set + the family's two
image-file operations, centralizing its ad-hoc clipboard threads). A shared internal
`StaThread.RunAsync` carries the STA post-mortem once. Verified: 252 dotnet + 32 vitest green
(+20: nested blocking, handle states, filter strings, initial-path chain incl. stale cleanup and
a throwing store, remember-path guards, shell validation throws, registration + runner wiring);
real dialogs/shell launches are e2e/manual territory; WinForms baseline promoted (additions
only); `verify` PASSED.

### 2026-07-30 — P4 increment 2: the window manager — frameless chrome + frontend window commands

`Shenora.WinForms` gains `OptimizedForm(+Options)`, merged from both desktop siblings with the
measured lessons kept: the double-buffered base + `WndProcHook` seam (first sibling) and the
optional frameless custom chrome (second sibling) — WM_NCCALCSIZE removes ONLY the top caption
(native invisible side/bottom resize borders stay; returning 0 for all sides needs a visible
inset), no `ControlStyles.UserPaint` (an unpainted WHITE frame otherwise), MANUAL work-area
maximize via `MonitorFromWindow`+`GetMonitorInfo` (never `Screen.WorkingArea` — DPI-mis-scaled
~12 px short; `WindowState.Maximized` left a ~6 px gap and squared the corners) with
`SC_MAXIMIZE`/`SC_RESTORE` routed through it, `WM_NCACTIVATE` lParam −1 (the grey caption
strip), DWM dark-mode/border-color/corner preference (rounded windowed, square maximized — the
clipping report), a DPI-scaled top resize strip re-added via WM_NCHITTEST, and
`ApplyChromeTheme` for runtime light↔dark resync. All colors are options (headless, D13).
`Shenora.WebView2` gains `WindowCommandFacade(+Options)` — module `WINDOW` (generalized from the
siblings' `APP`): MINIMIZE / TOGGLE_MAXIMIZE / CLOSE / IS_MAXIMIZED / START_DRAG (ReleaseCapture
+ WM_NCLBUTTONDOWN/HTCAPTION — the reliable WebView2 drag) / START_RESIZE (top edges only by
design; lParam MUST be the cursor screen pos or the size loop tracks from (0,0)) / optional
SET_THEME, with delegate seams (`ToggleMaximize`/`IsMaximized`) so frameless apps wire the
manual path — placed in the WebView2 package because the commands arrive over the bridge and
need Ipc, which WinForms deliberately doesn't reference. `@shenora/react` gains the
`WindowCommands` typed service + `useWindowMaximized` (the max-glyph resync pattern: re-query on
window resize). Verified: 232 dotnet + 32 vitest green; a live test-harness incident became a
rule — OptimizedForm's OLE drag-drop registration requires STA, and on xunit's MTA workers the
failure is a BLOCKING WinForms exception dialog, not a red test (tests now run bodies on a
dedicated STA thread; recorded in `windows-dev-gotchas`). WinForms + WebView2 baselines promoted
(additions only); `verify` PASSED. The frameless visuals + native drag/resize loops are the
P4.6 sample e2e's subject.

### 2026-07-30 — P4 increment 1: scoped-container router + the standard IPC composition

`Shenora.Ipc` gains `ScopedContainerRouter(+Options)` — the generalization of the primary
desktop sibling's per-profile service router (generic-library: an app-defined scope +
scoped-container router, no domain id). Each scope id lazily gets its own child
`ServiceProvider` from the app's `ConfigureScope` callback (validation throws structured
`OperationException`s), with `OnScopeCreated` for post-build init (the migrations/plugin-loading
the source hardcoded), `MapModule<TFacade>` routing declarations, `GetScopeServices`/
`InvalidateScope`/`ActiveScopes` (the sweep seam replacing the source's hardcoded
close-all-windows walk), and full disposal. Deliberate fixes over the source: single-flight
creation (`Lazy` per id — the source's bare `GetOrAdd` could build two providers under a
first-request race and leak one undisposed; failed creations don't poison the cache),
exceptions flow to the pipeline's error mapping instead of a leaking local catch, and a scoped
module called without a scope answers a structured `SCOPE_REQUIRED` (the source's equivalent
check was unreachable through its own wiring — why its client grew a hand-rolled guard).
Composition helpers formalize the sample's proven loop: `AddModuleFacade<TFacade>` +
`MapRegisteredModules` + `AddMessageDispatcher` (the §5 order encoded: error handler → app
middleware → DI-registered facades); the sample now composes through them. Verified: 216 tests
green (+15: routing matrix, `SCOPE_REQUIRED`, caching + single-flight under concurrency,
failed-creation retry, half-built-scope disposal, invalidate/dispose, structured validation
errors end-to-end, composition ordering); Ipc baseline promoted (additions only); `verify`
PASSED.

### 2026-07-30 — P3 increment 5: the IPC round-trip proven live (sample + e2e) — P3 closed

The sample apps become the IPC reference composition and the phase's proof. Desktop:
`SampleFacade` (`BaseFacade`, module `SAMPLE`: `ECHO` reads its payload through `PayloadHelper`
and returns a typed object; `FAIL` throws a structured `OperationException`), facades registered
in DI and mapped onto a `MessageDispatcher` (`UseErrorHandler` first) at composition time,
`WebViewIpcBridge` wired in its intended order (constructed before `InitializeAsync` so bus
buffering covers init; attached after init, before `Navigate`; disposed with the form) with
`OnClientReady` starting a 1 Hz `SAMPLE.TICK` emitter on the app's `IEventBus`. Web: the page
calls `notifyReady()` from an effect, runs `useShenoraQuery('SAMPLE','ECHO')` and renders the
typed response, streams `SAMPLE.TICK` via `useShenoraEvent`, and installs the dev interceptor in
dev builds. PROVEN LIVE with the devtools loop (screenshots in `devtools/screenshots/`,
gitignored): packaged mode shows `SAMPLE.ECHO("shenora") → SHENORA (7)` and `SAMPLE.TICK`
advancing #19→#23 across two captures 4 s apart (`p35-packaged-a/b.png`); dev mode the same over
Vite (`p35-dev.png`, TICK #38). CDP-driven assert (dev, via `window.__shenora` + the `.cdp-port`
loop): `call('SAMPLE','ECHO',{text:'cdp drive'})` returned `{echoed:"CDP DRIVE", length:9}`,
`call('SAMPLE','FAIL')` rejected as `OperationError` `{code:"SAMPLE_FAILURE",
parameters:{reason}}` (raw exception text never crossed), `waitEvent('SAMPLE','TICK')` resolved
with a live tick, and the ring buffer showed the full exchange. **P3 (IPC extraction) is
complete**: contracts → dispatcher/event bus → WebView2 transport → React client → live
round-trip, all verified (`verify` PASSED at every increment).

**Phase review (adversarial subagent over the full diff) — 9 real findings, all addressed:**
(1) an unserializable handler result (or a throwing app dispatcher) escaped the transport's
async-void handler → process death; the bridge now wraps dispatch + serialize and always answers
`UNKNOWN_ERROR`; (2) the ready gate never re-closed, so a renderer-crash reload drained
notifications into a listener-less page → `NavigationStarting` now resets it; (3) the event
bus's `'.'`-joined match-cache key let arbitrary app names collide and permanently poison
results → `'\0'`-joined; (4) `useShenoraQuery` left `loading: true` forever when `enabled`
flipped false mid-flight; (5) `PayloadHelper` put raw serializer text on the wire (design §5) —
now only the key crosses, details stay in the inner exception; (6) a disposed TS bridge burned
the full timeout per call → fails fast with `NO_TRANSPORT`; (7) the match cache's unbounded key
space is now a documented cardinality contract; (8) `ConfigureAwait(false)` inside the
dispatcher pipeline broke the §5 stay-on-caller-context model after async fall-throughs —
removed, documented; (9) the sample's `NO_HANDLER` was missing its documented `module`
parameter. New tests cover 1–6; the earned invariants became `.claude/knowledge/ipc-contracts.md`.
Re-verified: 201 dotnet + 28 vitest green, `verify` PASSED.

### 2026-07-30 — P3 increment 4: `@shenora/react` becomes the real client

The placeholder package becomes the client side of the contract, ported from the primary desktop
sibling's bridge/event-bus/module-service trio and generalized where the source carried app
schema. `types.ts` mirrors the `Shenora.Ipc` envelopes name-for-name; `OperationError` carries
the structured code + parameters (client-side failures — `TIMEOUT`, `NO_TRANSPORT` — reject
through the same shape, so error handling is uniform). The transport is a two-method seam
(`ShenoraTransport`) with `createWebView2Transport` as the desktop default — the D16
pluggability point a WebSocket or Capacitor shell implements later. `ShenoraBridge`: correlated
`invoke` (uuid ids, per-call timeout over a 30 s default), category routing, batch unbundling
into `ShenoraEventBus`, `notifyReady()` (the `SHENORA`/`READY` handshake that starts host
notification delivery), and a `fallback` option generalizing the source's hardcoded dev mocks —
the app supplies canned answers for pure-UI browser development; the library ships none (no app
schema in the kit). The default instance is LAZY (`getBridge`/`configureBridge` — no import-time
side effects, honest `sideEffects: false`). `BaseModuleService<TRequests>` keeps the typed-send
core and drops the source's boolean/array/optional wrappers (pure casts). Hooks: `useShenora`,
`useShenoraEvent` (latest-ref pattern replaces the source's deps param — no resubscribe churn,
no stale closures), `useShenoraQuery` (deliberately minimal fetch state — headless, D13).
`installDevInterceptor` ports the CDP-testing global (`window.__shenora`: `call`/`waitEvent`/
ring buffers), idempotent across HMR. `react` becomes a required peer (hooks import it
statically). Verified: 26 vitest tests green (wire shape, resolve/structured-reject/timeout,
batch order, malformed-message tolerance, handshake, fallback + `NO_TRANSPORT`, dispose,
event-bus semantics, typed service, hook lifecycle via renderHook incl. the latest-ref
guarantee, interceptor recording/idempotence); `doctor` consistent; full `verify` PASSED.

### 2026-07-30 — P3 increment 3: the WebView2 postMessage transport

`Shenora.WebView2` gains `WebViewIpcBridge(+Options)` — the transport tying a WebView2 window to
the dispatch pipeline and the event bus, merged from the two family transports with their
post-mortem comments kept. Incoming: `WebMessageReceived` requests parse (`IpcJson`) and
dispatch async ON the UI thread — each await yields the message pump so concurrent IPC
interleaves without a pool thread per call (the measured incident: `Task.Run`-per-message under
heavy backend load starved the pool and froze the app; heavy work belongs in the backend's own
bounded queues). Outgoing: responses and ~50 ms-batched `IpcNotificationBatch` pushes via
`PostWebMessageAsString`, guarded by the family marshalling discipline (`IsHandleCreated`
checked before `InvokeRequired` — the pre-handle lie — then non-blocking `BeginInvoke`).
Notifications flow through a bounded drop-oldest queue (cap 10k — telemetry-like events; OOM is
worse than losing stale progress ticks) that buffers from CONSTRUCTION (events emitted during
the slow WebView2 init survive) and delivers only after the client's ready handshake (reserved
`SHENORA`/`READY` route, intercepted before the dispatcher; `OnClientReady` fires per occurrence
— reloads included — as the cue to reset per-page state). Optional `IEventBus` wildcard
forwarding; `SendNotification` for direct pushes; `Dispose` stops the flush timer (the source's
timer once outlived its window, posting into a torn-down WebView). Verified: 197 tests green
(+12 protocol tests over internal seams — handshake semantics, dispatcher pass-through +
interception, malformed-input drops, ready-gated batching, wire shape/order, drop-oldest cap,
bus forwarding/unsubscribe); the live transport is the P3.5 sample e2e's subject; WebView2
baseline promoted (additions only); `verify` PASSED.

### 2026-07-30 — P3 increment 2: dispatch pipeline + facade base + in-process event bus

`Shenora.Ipc` gains the middleware dispatcher ported from the primary desktop sibling:
`MessageDispatcher` behind the `IMessageDispatcher` seam — `Use`/`UseModule`/`UseRoute`/
`UseLogging`/`UseErrorHandler` middleware composition (family order: error handler → logging →
app middleware → facades), `MapRoute`/`MapModule` route tables, a lazily rebuilt pipeline, and
`DispatchAsync` as the transport entry point that never throws and never returns null (unhandled
→ structured `NO_HANDLER`; escaped `OperationException` → its structured error; anything else →
`UNKNOWN_ERROR` with details kept host-side — the source leaked `ex.Message` across the bridge,
design §5 forbids it). Programmatic `SendAsync`/`SendAsync<T>` share that exact pipeline; failed
typed sends rethrow the structured `OperationException` (the source flattened to
`InvalidOperationException`), and data conversion uses the wire options (the source's default
options would have broken camelCase round-trips). `IModuleFacade` (now carrying `ModuleName`, so
facade objects route without the source's static mutable registry — DI + `MapModule(facade)`
replace it) + `BaseFacade` with the standardized error boundary. `Shenora.Core` gains the
in-process event bus per the design's package split (§4): `EventMessage`/`IEventBus`/`EventBus`
(scope generalizes the per-profile field) with `"*"` wildcards, the per-subscription match
cache, isolated handler failures, concurrent fan-out — auto-registered by
`ShenoraApplicationBuilder.Build()` (`TryAdd` last, so app/module registrations win). All
logging is `ILogger<T>`, optional so composition works without `AddLogging`. Verified: 184 tests
green (+30: matching semantics incl. the scoped/global rules, middleware ordering,
post-dispatch registration, error mapping incl. no-leak assertions, all three typed-data
conversion paths, facade routing); Core + Ipc baselines promoted (reviewed, additions only);
`verify` PASSED.

### 2026-07-30 — P3 increment 1: the IPC wire contract (`Shenora.Ipc` first surface)

The envelope contract two family apps already speak (D11), shipped transport-neutral (D16) and
pinned with `JsonPropertyName` so the wire shape survives any serializer options: `IpcRequest`
(`{id, module, type, scope?, payload?, timestamp}` — `scope` generalizes the source's per-profile
routing field), category-wrapped `IpcResponse` with a structured `IpcError` (`{code, message?,
parameters?}` — the source's JSON-string error + duplicated error data collapsed into one i18n-ready
object), and the always-batched `IpcNotification(Batch)` push envelope (~50 ms flush upstream;
`category` alone discriminates, so the same envelope rides postMessage, WebSocket, or a mobile
channel — the source's synthetic batch module/type wrapper is gone). `OperationException`
(code + parameters, `ToError()`), framework-reserved `IpcErrorCodes`, static `PayloadHelper`
(structured missing/invalid failures instead of `ArgumentException`; JSON null == absent per the
family wire convention), and `IpcJson` — ONE frozen camelCase/camelCase-enums/null-omitting
options instance, ending the source's three drifting private copies. Replaces the Ipc assembly
marker. Verified: 152 tests green (25 new: wire shapes incl. attribute pinning under foreign
options, exception mapping, payload reads, serializer defaults); Ipc API baseline promoted
(reviewed); `verify` PASSED.

### 2026-07-30 — P2 increment 6: samples + the desktop e2e loop, both frontend modes proven live

`samples/Shenora.Sample.Desktop` + `samples/Shenora.Sample.Web` — the reference composition and,
from here on, the e2e subject. The desktop app is the full stack in its intended shape:
`ShenoraApplication.CreateBuilder` → DI-registered `WebViewEnvironmentOptions` (ONE instance
shared by prewarm and the window's host) + `EmbeddedResourceProvider` (embedded
`wwwroot` bundle, file-fallback in dev) + `WebViewHostOptions` (dev URL 3900, virtual host,
injected metadata global, no-white-flash background) → `PrewarmWebView2` + provider warmup as
starting hooks → `UseWinForms` (single instance, `JsonFileWindowStateStore` window state) →
`MainForm` (WebView2 + `SplashPanel` until first navigation, runtime-presence prompt, actionable
init errors). The web sample is a minimal Vite React app consuming `@shenora/react` that displays
its serving mode, `isShenoraAvailable()`, and the injected host metadata — so one screenshot
proves the whole stack. Verified live with the devtools loop (`wgc` capture): PACKAGED mode
(embedded bundle over the virtual host — "frontend: packaged / bridge: WebView2 host detected /
host: Shenora.Sample.Desktop v1.0.0") and DEV mode (live Vite — "frontend: dev (Vite)", same
bridge + metadata), window state persisted DPI-logically (physical ~2538 px stored as 1280
logical at 200 %) and restored on relaunch, and the CDP devtools port reachable in dev — the
`AdditionalBrowserArguments`-clobbers-the-env-var fix working end-to-end. `dev.mjs
sample/vite/shot/wgc/click` now have their target. 126 tests green; `verify` PASSED.

### 2026-07-30 — P2 increment 5: WebView2 host, packaged-frontend serving, event policies, splash

`Shenora.WebView2` gains the "one place a WebView2 gets configured": `WebViewHost(+Options)` —
environment acquisition (shared/prewarmed or per-STA-thread) and `EnsureCoreWebView2Async` under
the family's 25 s init-timeout guard (an orphaned user-data-folder lock otherwise hangs init
forever, silently), the settings-hardening preset (dev-gated devtools/context menus, everything
unused off, web messages on) with a `ConfigureSettings` escape hatch, dev/prod navigation with
actionable errors (`ResolveStartUrl`: DevUrl in dev — deliberately no default port; explicit
`ProductionUrl` or the virtual host's index in prod), and the four event policies every source
lacked: new-window → system browser (scheme-checked), downloads canceled by default, permissions
silently denied except an allowlist (clipboard-read), renderer-crash auto-reload with a cooldown
— each replaceable by a callback. Resource serving keeps the source's measured sync/deferred
split with its post-mortem comments: the virtual-host bundle serves synchronously in-memory (the
main document must be prompt), app schemes (`WebViewDeferredScheme`) defer off the UI thread and
marshal responses back via `BeginInvoke`; disk-folder virtual hosts (`WebViewFolderMapping`)
are supported alongside interception (both family mechanics, deliberately). Fixed during the
port: the caching policy is now no-cache HTML / immutable hashed assets (the source served
`index.html` immutable — a stale-update trap), and injected globals are real JSON with escaping
(`InjectedGlobals`) instead of raw string interpolation. `EmbeddedResourceProvider(+Options)`
behind the `IWebViewResourceProvider` seam is parameterized by assembly + prefix, lazy-with-warmup
(the source preloaded everything in a blocking parallel ctor loop), file-fallback mode for dev,
and resolves lookups path→name so dotted filenames work. `Shenora.WinForms` gains
`SplashPanel(+Options)` — the startup marquee overlay with app-chosen colors (headless, D13) and
a debounced recenter; the source's dead status labels were dropped. Verified: 126 tests green
(provider modes/warmup/dotted names, script escaping, URL resolution, content-type + cache
policies, splash layout); the live host path is proven by the P2.6 sample e2e; baselines
promoted (additions only).

### 2026-07-30 — P2 increment 4: application builder + lifetime, `--restarted` relaunch handoff

`Shenora.Core` gains the composition root the design's goal statement names:
`ShenoraApplication.CreateBuilder(args)` resolves the launcher contract up front (`--app-root` →
`ShenoraPaths` → `ShenoraEnvironment` anchored at the resolved root), exposes
`Services`/`AddModule(IShenoraModule)`/`OnStarting`/`OnStopping`, and `Build()` produces a
`ShenoraApplication` whose `Run()` executes a host-package-registered `IShenoraRunner` (actionable
error when none). Lifecycle participation is DI-based (`IShenoraLifecycleHook`), so composed
packages hook startup/shutdown without Core referencing them — the mechanic that keeps package
dependencies strictly downward (design §4 amendment; Core's dependency moved to the DI
implementation package, D17). `Shenora.WinForms` gains `UseWinForms(WinFormsHostOptions)` — an
internal runner executing the family's measured order: single-instance gate FIRST (now with the
`--restarted` widened-wait handoff: `SingleInstanceGuard.TryAcquire(TimeSpan)`, abandoned-mutex
recovery, explicit release-before-teardown), `WinFormsBootstrap.Initialize`, starting hooks,
main-form factory (+ optional window-state apply/save and an activate-on-second-launch message
filter that works with ANY `Form` — no base-class requirement), the message loop, then
reverse-order guarded stopping hooks. `Shenora.WebView2` gains `PrewarmWebView2` (a deferred
starting hook — the prewarm's user-data lock must stay behind the gate). Verified: 93 tests
green (builder composition, documented run order, losing-launch path, widened-wait/timeout/
abandonment handoffs, window-state wiring through internal seams); the real message-pump path is
proven by the P2.6 sample e2e; API baselines promoted (additions only).

### 2026-07-30 — P2 increment 3: WebView2 environment factory + runtime presence check

`Shenora.WebView2` gains `WebViewEnvironment(+Options)`: the prewarm pattern (browser-process
spawn overlapping the rest of startup — ~1–2 s measured in the source), the shared environment
with its thread-affinity contract (main UI thread) plus `CreateForCurrentThreadAsync` for
secondary windows on their own STA threads (same options/user-data folder ⇒ one shared browser
process), the dev CDP-args re-append, an injectable log sink instead of the source's
`Console.WriteLine`, and — NEW, the gap every source shipped with — a never-throwing runtime
presence probe (`GetAvailableRuntimeVersion`/`IsRuntimeAvailable`) so apps can show an
actionable install prompt instead of failing inside `EnsureCoreWebView2Async`. 70 tests green.

### 2026-07-30 — P2 increment 2: paths authority, app-root arg, bootstrap + global exception handling

`Shenora.Core` gains `AppRootArgument` (the launcher's `--app-root` contract, both arg forms) and
`ShenoraPaths(+Options)` — the portable layout authority generalized from two sources: explicit
root → root env var → libs-parent detection → base dir, a data env var so child processes share
the host's data dir (a live divergence incident in a source app), configurable folder names, and
ensure-created purpose areas with NO framework-defined area vocabulary. `Shenora.WinForms` gains
`WinFormsBootstrap` — the proven one-call WinForms init (visual styles, GDI+ text, PerMonitorV2,
catch-mode) PLUS the audit's #1 gap fixed: `Application.ThreadException`,
`AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` all routed to a
crash-log callback with a guarded last-resort dialog (a known no-op reflection hack from the
source was deliberately dropped). Verified: 67 tests green; baselines promoted (additions only).

### 2026-07-30 — P2 increment 1: the pure seams (environment, DPI, window-state, single-instance, browser args)

First real extraction, targeting the fully-unit-testable seams. `Shenora.Core.ShenoraEnvironment`
unifies dev-mode detection that one source app duplicated across four files. `Shenora.WinForms`
gains `DpiHelper` (primary-monitor + per-device-DPI scales, pure `Scale` core), the merged
window-state stack (`WindowStateManager` with pure `ToPhysical`/`ToLogical`/`IsVisible`, an
`IWindowStateStore` seam covering both family storage styles plus `JsonFileWindowStateStore`),
and `SingleInstanceGuard` (per-scope FNV-1a key, activate broadcast, fail-open). `Shenora.WebView2`
gains `BrowserArguments` — the measured display-optimization preset with the
single-feature-switch rule and the CDP env-var-clobber fix. The placeholder no-public-types test
was replaced by real API-surface baseline tests (tracked baselines, `.actual` drift dumps).
Verified: 43 tests green (DPI math, state roundtrips, visibility strips, mutex acquire/release
across guards, argument composition), `verify` gate green.

### 2026-07-30 — P0: repo bootstrap

The repo was created from the family preset and turned into a library devkit workspace. All five
sibling repos were surveyed in parallel (the library-template repo, the org-system/host donor, and
the three desktop apps) to produce the extraction map — the brief's Phase 1 audit — recorded in
`local/EXTRACTION-MAP.md` (named) and `.claude/knowledge/extraction-sources.md` (tracked,
de-identified). The design contract (`docs/2026-07-30-shenora-design.md`) and the decision log
(`docs/DECISIONS.md` D1–D12) were written: two consumption profiles, four NuGet packages + one
npm package, net10.0, lockstep versioning, manual OIDC release, no push CI. The Sonora-preset
devtools/rules/skills were culled to the generic core and re-targeted; the docs system
(router/ARCHITECTURE/ROADMAP/TASKS/FIX-LOG/DECISIONS/CHANGELOG), the buildable solution skeleton,
the rewritten devtools (`build`/`test`/`verify`/`pack`/`doctor` + the desktop verification loop),
the release workflow, and the git repo + pre-commit guard were set up. Verified: `dev.mjs verify`
green (dotnet build + tests, npm build + tests, sensitive scan, knowledge check).

## Remaining

### P1 — Skeleton hardening (short tail)

Both original bullets are DONE — the placeholder types were pinned in P2 (see Done) and the
local-feed consumption smoke landed as P1.1 (`0776f37`). Only one item remains:
- **P1.2 — release-workflow dry run**, blocked until a GitHub remote exists (`TASKS.md`).

### P2 — Core host extraction (brief Phase 2) — COMPLETE except deliberate carry-overs

Everything above landed (increments 1–6, see Done). Carried forward on purpose:
- **DPI tail → P4** (`OnDpiChanged` handling + CSS-px→physical conversion) — lands with the
  overlay components that need it (drop zones, login windows).
- **Optimized form / frameless chrome → P4** — lands with the window manager + frontend window
  commands.
- **Stable-chunk frontend build guidance** (docs) → written with the P3 `@shenora/react` docs,
  where frontend build advice naturally lives.

### P3 — IPC extraction (brief Phase 3) — COMPLETE

Everything landed (increments 1–5, see Done): envelopes/errors/serializer defaults, dispatcher +
facade base + event bus, the WebView2 postMessage transport, the `@shenora/react` client, and
the live round-trip e2e. Carried forward on purpose:
- **Stable-chunk frontend build guidance** (docs for consuming apps: vite `manualChunks`, hashed
  assets vs the no-cache HTML policy) → lands with the P6 adoption docs, where a real consumer
  exercises it. Drop-zone hook + window-command helpers were always P4 surface.

### P4 — Modules + native services (brief Phase 4) — COMPLETE

Everything landed (increments 1–6 + phase review, see Done): scoped-container router + the
standard IPC composition, frameless chrome + frontend window commands, the native services
(dialogs/shell/clipboard/interaction), drag-drop zones + `useDropZone` (+ the P2.3b DPI tail),
secondary windows + tray, and the live sample/e2e proof.

### P5 — Auxiliary browser sessions (`Shenora.WebView2.Sessions`, D14) — COMPLETE

Everything landed (increments 1–4 + phase review, see Done): the one browser-configuration path
(`SessionBrowser` + init-timeout guard), the bounded LIFO render-session pool, the login-window
stack (`LoginWindow`/`LoginWindowController`/`CookieLoginFlow` — per-provider/per-account
profiles, silent refresh, clear-on-logout) and co-browse streaming (`CoBrowseSession` — CDP
screencast frames out, input dispatched back, human-solved by design), in its own package with a
live sample demo.

### P5.5 — Consolidation: cleanup, re-layer, roadmap revisit — NEXT, before P6

**What this phase is.** P0–P5 put the whole body of the kit down in a short span — five commits,
~8.7k lines of `src/` plus ~4.7k of tests, five packages and an npm client — extraction-first and
phase-gated, but moving fast, and with holes in the verification gate itself (see H5). P5.5 is the
deliberate **consolidation checkpoint**: clean up what that velocity left behind (duplication,
missing guards, convention drift), take the structural correction while it is still free (pre-1.0),
close the gate, and revisit the rest of the roadmap in light of what the pass taught. It is a
planned settling pass, not an emergency — the tree was green throughout.

Consolidation has three strands:

1. **Cleanup** — the first review spanning all of P0–P5 (2026-07-30): six parallel reviewers over
   the five packages, the npm client, the samples and the tree, briefed by `docs/REVIEW-GUIDE.md`.
   The baseline was green (`verify` PASSED at `130d4cd`), so everything found is a LATENT defect
   rather than a regression — which is exactly why it lands before a real app depends on the surface
   (P6) and before the 1.0 SemVer freeze (P7). Full itemised plan with `file:line` anchors:
   `TASKS.md` `### P5.5`, batches H1–H8.
2. **Re-layer** — the structural change below (D19 + D20), which the cleanup's own findings argued
   for and which is only cheap while nothing is published.
3. **Roadmap revisit** — this section, plus the amendments to P6/P7/Later that follow from both.

**And an API-shape correction** (user direction, 2026-07-30 — D21): for a whole application *feature*
the kit ships **primitives + lifecycle hooks, not the product**. `CoBrowseSession` had it backwards —
`DispatchInputAsync(string)` takes the source app's wire protocol as an opaque JSON string and
`ReadHotspotsAsync` encodes a co-browse UX decision, while the hooks that make a feature extensible
are missing (nothing signals the session ending or faulting, so a renderer crash leaves the frame
channel never completed and the app's reader waiting forever). The kit's other two session families
already got this right — the render pool ships the pool and the sample writes its own flow; the login
window keeps policy in a driver seam. Tracked as `TASKS.md` H9, after the re-layer.

**The phase also carries a structural change** (user direction after reading the review, approved
2026-07-30): the two Windows shell packages become one layer — `Shenora.WebView2` depends on
`Shenora.WinForms` — and the portable contracts plus the long-specified-never-built `IUiDispatcher`
move to `Shenora.Core`, so an app's own logic compiles with no Windows reference and a future mobile
shell can implement the same contracts. Design:
`docs/2026-07-30-shenora-relayering-design.md`; decisions: D19 + D20. This replaces the review's
proposed `InternalsVisibleTo`/linked-file workaround — the deduplication fix and the portability
seam turn out to be the same object, so one change buys both. Execution order matters: security
fixes first (H1 + H5), then the re-layer, then the dedup on top — see `TASKS.md`.

The review's own verdict was that the per-package internals are disciplined — the extraction
comments are load-bearing and accurate, the dependency graph holds exactly as documented, the IPC
error boundary leaks no exception text on any traced path, and the wire mirror is correct
field-for-field bar one missing constant. The weaknesses are **at the seams between packages, and
in the gate around them**:

- **Six confirmed P0s** (each re-verified against the code before being recorded): no path
  containment in file-mode serving (arbitrary file read, live in every dev session); the
  frameless-maximize ⇄ window-state seam (a maximized close makes restore a permanent no-op — live
  in the reference composition); `RenderSession` accepting cancellation tokens it never observes
  (one JS-blocked page starves the pool for the process lifetime); `NavigationGuard` — the
  documented SSRF boundary — bypassed by redirects and in-page navigation; `AddMessageDispatcher`
  enumerating facades inside its own singleton factory (StackOverflow, no diagnostic, on the
  documented cross-module composition); and a throwing app `OnLoading` callback leaving an
  unclosable login modal that then vetoes `Application.Exit`.
- **The duplication is causal, not cosmetic.** The UI-marshal pattern is hand-rolled 14 times with
  5 incompatible pre-handle policies — 7 unguarded, and one site carries a comment explaining the
  pre-handle trap then commits it on the next line. And the `Sessions → Shenora.WebView2` edge that
  D14 documents as deliberate is **declared but entirely unused**, which is why `SessionBrowser`
  re-implements browser arguments (re-introducing the CDP env-var gotcha), environment creation, the
  init-timeout guard and settings hardening — and why pooled/co-browse instances have none of the
  `NewWindowRequested`/`PermissionRequested`/`ProcessFailed` policies the host package already
  implements.
- **The gate had holes.** `Shenora.slnx` carries an empty `/samples/` folder (and omits
  `Shenora.Core`), so `verify` never compiled the reference composition or the e2e subject;
  `dev.mjs test <typo>` exited 0 having run nothing; and `check-sensitive` fails OPEN when the
  gitignored pattern file is absent — i.e. the private-name half of the guard never ran in CI.
- **Pre-1.0 surface work** that is far cheaper now than after the freeze: the API baseline doesn't
  gate `protected` members (so `BaseFacade.RouteMessageAsync`, the member every consumer overrides,
  is outside the SemVer gate) or default parameter values; `BaseModuleService`'s typed-payload
  feature type-checks nothing and its documented example doesn't compile; and the reference
  composition has to downcast `IMessageDispatcher` because form-dependent facades have no
  registration seam.

### P6 — Sibling adoption (brief Phase 5)

- Adopt in the newest desktop sibling first (smallest host, gaps already documented), via local
  feed + pinning; keep it runnable at every step. Then evaluate the other two desktop siblings
  and the server-backed app (shell-only profile).
- Feed every "the framework almost fits, but…" back into the API before 1.0.

**Revisited 2026-07-30 (post-consolidation):**
- **Do not start P6 before P5.5's H1–H5.** Adopting against a surface that is about to be re-layered
  (D19/D20) means doing the integration twice, and adopting against the pre-H5 gate means the
  adoption itself isn't verified — `verify` did not even compile the sample until H5.
- **Adoption gains a second dimension: portability.** With D20's contracts in `Shenora.Core`, put the
  adopting app's own facades in a `net10.0` project from day one (H4.3 proves the pattern on the
  sample). That makes the app's logic mobile-shareable as a side effect of adopting, and it turns
  the abstract question "are these the right portable contracts?" into a concrete one answered by a
  real app — feed the answer back as a D20 amendment.
- **The adoption is the real test of the review's fixes.** Several P5.5 P0s were latent-only
  (nothing in-repo triggered them); a real consumer is what proves them fixed rather than merely
  patched — notably the DI composition (facades injecting `IMessageDispatcher`), async disposal of
  singletons, and a relative `--app-root`.

### P7 — Stabilisation + 1.0 (brief Phase 6)

- API-surface baseline tests on; docs pass (XML docs, README per package section); CHANGELOG
  discipline from first publish; `Shenora.Hosting.AspNetCore` go/no-go (D10); first NuGet/npm
  publish via the release workflow; GitHub repo goes public.

**Revisited 2026-07-30 (post-consolidation):**
- **"API-surface baseline tests on" is not yet the SemVer gate it is assumed to be.** They dump
  `BindingFlags.Public` only, so `protected` members — including `BaseFacade.RouteMessageAsync`, the
  one member every consumer overrides — are ungated, along with default parameter values, `init` vs
  `set`, `required`, and attributes. P5.5 H6 closes this; 1.0 must not freeze behind a gate with a
  hole in it.
- **Part of the docs pass moves earlier.** P5.5 H7 already corrects the shipped-in-nupkg inaccuracies
  (package descriptions, README claims). What remains for P7 is genuinely new writing: per-package
  README sections, the XML-doc sweep enabled by turning CS1591 back on (H5), and the stable-chunk
  frontend build guidance carried over from P2/P3.
- **CHANGELOG discipline starts now, not at first publish** — the log is already missing the one fix
  that changed a published artifact's importability (`0776f37`), which is exactly the class of entry
  the discipline exists for.

### Later / candidates

- `Shenora.Hosting.AspNetCore` (SPA static policy, loopback-gated endpoint helpers) — D10.
- Mobile transport adapter (Capacitor or similar speaking the same IPC envelope) — D16; packaged
  at first mobile adoption (`@shenora/capacitor` vs an adapter in `@shenora/react`). **Revisited
  2026-07-30:** the decision point is unchanged (first real mobile adoption), but the .NET-side
  surface such a shell would implement is now enumerated rather than hypothetical — D20's portable
  contracts in `Shenora.Core` (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`,
  `IUiInteraction`). D16 covers the transport seam; D20 covers the feature seams. Neither ships an
  implementation until there is a consumer.
- Harvest-promotions from ongoing app development (D15) — any proven-nice feature gets
  generalized and lands here as a task before shipping in a minor.
- C++ launcher template (runtime check/install, staged self-update) as a repo template, not a package.
- Scaffolding skills once patterns exist (`new-ipc-module`, `new-native-service`).
- Contract codegen (C# ⇄ TS) — explicitly out of initial scope; revisit after adoption feedback.
