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
  - **The posted body is GUARDED.** An exception in a posted delegate is an unhandled UI-thread
    exception (crash dialog), because there is no caller on that stack to catch it.
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
- **Guard init with a timeout** (`WebViewHostOptions.InitTimeout`, 25 s family default): an
  orphaned user-data-folder lock (zombie browser process) hangs `EnsureCoreWebView2Async`
  forever with no error. Fail loudly with the fix in the message.
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
