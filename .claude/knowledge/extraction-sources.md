# Extraction sources — which sibling proved which component, and what to fix while porting

Shenora is extracted, not invented. This rule maps each framework area to the app that proved it
(de-identified — the real names + file paths live in the private `local/EXTRACTION-MAP.md`;
read BOTH before porting anything). Foundation: 2026-07-30 survey of all five family repos.

## The rules

- **Port the proven file, keep its post-mortem comments.** In the sources, the comments carry the
  measured incidents (why not `Task.Run`-per-message, why `BeginInvoke` not `Invoke`, which
  Chromium flags were rejected). They are the product — carry them forward, updating names only.
- **Source map (who proved what):**
  - *Primary desktop sibling* — correlated postMessage IPC (envelopes + category wrapper + 50 ms
    notification batching), middleware dispatcher + facade base, WebView2 initializer/prewarmer +
    embedded-resource serving over a virtual host, single-instance guard (tested), drop-zone
    overlays, DPI-correct window-state service, secondary windows on own STA threads, STA file
    dialogs, structured i18n errors, the TS bridge/module-service/event-bus trio, dev interceptor.
  - *Second desktop sibling (conformance reference)* — same framework layer co-evolved; adds
    frameless window chrome (WM_NCCALCSIZE, manual work-area maximize, DWM dark border) + the
    frontend window-command routes (minimize/maximize/close/drag/resize) and a browser fake-bridge
    preview harness.
  - *Third desktop sibling (first adoption target)* — the minimal-seam proof (its tiny Core IPC
    assembly) and the gap list that is Shenora's value: no dev-server switch (stale-bundle
    footgun), uncorrelated fire-and-forget IPC, window-state code duplicated per window, portable
    app-paths layout with env overrides for child processes.
  - *Sonora (public; server-backed profile)* — best-in-family window-state store (logical-px
    store / physical restore / never-block-close), singleton mutex + `--restarted` widened-wait
    relaunch, WebView2 host with `.dev`-marker dev switch + settings hardening +
    NewWindowRequested→system browser, bounded drop-oldest event queue + UI-timer batch flush
    (`{"__batch":[…]}` — the same envelope its WebSocket uses: the transport-pluggable seam),
    tray/close-to-tray pattern, 25 s WebView2-init timeout guard, UI-thread anchor pattern.
    ALSO the P5 auxiliary-browser-sessions stack (D14): the one-place WebView2 configurator,
    offscreen render service + bounded LIFO session pool, driveable session primitives,
    per-provider/per-account login-window profiles with clear-on-logout, and co-browse
    streaming (CDP screencast out / input dispatch back). The primary sibling's external-login
    window is the second proof of the login-window shape.
  - *Lyntai (public; repo template, no code)* — packaging/versioning/release/devtools/docs model.
- **Fix the known gaps DURING the port, not after** (absent in every source): global
  unhandled-exception handlers + crash dialog; WebView2 runtime presence check;
  `NewWindowRequested`/`DownloadStarting`/`PermissionRequested`/`ProcessFailed` handling; options
  records replacing magic numbers (dev port, colors, timeouts, batch intervals); escaped JS
  injection; no `Console.WriteLine` logging (use `ILogger<T>`); no `as dynamic` payloads; no
  static mutable registration state; make eager embedded-resource preload lazy-with-warmup.
- **Merge, don't pick blindly, where two sources solved the same problem** (window-state: merge
  the DPI-pure-function testability of one with the RestoreBounds/never-block-close discipline of
  the other).

- **A declared dependency edge that nothing crosses is a duplication smell.** Found live:
  `Shenora.Windows` declared its `ProjectReference` to `Shenora.Windows` and then imported
  nothing from it — so the port re-implemented browser-argument building (re-introducing the CDP
  env-var gotcha from `windows-dev-gotchas`), environment creation, the init-timeout guard, and
  settings hardening, and shipped WITHOUT the `NewWindowRequested`/`PermissionRequested`/
  `ProcessFailed` policies this file lists as must-fix. Before porting a helper a second time, grep
  the packages you already reference for an owner. **After D19/D20 a ported helper's home is decided
  by LAYER, not by which sibling proved it** (portable contract → `Shenora`; Windows
  implementation → `Shenora.Windows`; web hosting on top).

## Gotchas / traps

- The sources disagree on virtual-host mechanics (`SetVirtualHostNameToFolderMapping` vs
  `WebResourceRequested` + embedded resources). Both are legitimate: folder mapping for
  disk-backed bundles, resource interception for single-file embedded bundles. Shenora's frontend
  options must support both — don't "unify" one away.
- Keep sibling names out of tracked code/docs while porting (see `sensitive-info`) — attribution
  comments say "ported from a family app", nothing more.
