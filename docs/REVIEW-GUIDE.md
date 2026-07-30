# REVIEW-GUIDE.md — orientation for a full code review of Shenora

Written to hand a whole-codebase code review the context it needs without re-deriving it. Shenora
has been built in phases (P0–P5) across five commits. Read this, then review `src/` against the
invariants it points at. Nothing here overrides the design contract or the rules — it routes you to
them and flags what's already settled.

> **The first full review already ran, at `130d4cd`.** Its ~60 open findings are `TASKS.md`
> `### P5.5` (batches H1–H8), summarised in `docs/ROADMAP.md` `### P5.5`. **Verify and extend that
> list — do not re-derive it.** A second reviewer's value is in what H1–H8 missed.

## 1. What Shenora is (the review lens)

Shenora (神阙) is a **reusable library, not an app**: the desktop "body" (WinForms + WebView2 +
React hosting, typed IPC, modules, native services, auxiliary browser sessions) for a family of
Windows apps, shipped as NuGet (`Shenora.Core|Ipc|WebView2|WebView2.Sessions|WinForms`) + npm
(`@shenora/react`), versioned in lockstep. Two properties shape every judgement:

- **It is EXTRACTED, not invented.** Code is lifted from proven in-house sibling apps, keeping
  their post-mortem comments and fixing a listed set of gaps. So "why is this shaped like the
  source?" usually has a measured answer — check `.claude/knowledge/extraction-sources.md` and the
  per-port comments before calling a shape wrong. New abstractions with no sibling precedent are
  the ones to scrutinise hardest.
- **It is a LIBRARY with a public surface.** Every exported type becomes SemVer surface at 1.0.
  Two hard constraints: (a) **generalize, don't ship the consumer's shape** — no app/domain
  vocabulary in `src/`, seams over flags, options records over magic values
  (`.claude/knowledge/generic-library.md`); (b) **headless (D13)** — NO dependency on any UI
  component library, anywhere.

Design contract: `docs/2026-07-30-shenora-design.md`. Load-bearing choices: `docs/DECISIONS.md`
(D1–D20 — numbered; don't relitigate, they record *why*). **D19 + D20 are the newest and the most
likely to look like violations:** the package layering was deliberately changed
(`docs/2026-07-30-shenora-relayering-design.md`), and the tree still predates it. As-built map + full public surface:
`docs/ARCHITECTURE.md`. Phase-by-phase narrative of what changed and how it was verified:
`docs/ROADMAP.md` `## Done`.

## 2. What exists (commit-by-commit)

| Commit | Phase | Adds |
|---|---|---|
| `34add37` | P0–P2 bootstrap | Repo/docs/devtools skeleton; `Shenora.Core` host + builder + paths + env; `Shenora.WinForms` bootstrap/window-state/single-instance; `Shenora.WebView2` hosting + serving; the sample app |
| `eeb23f7` | P3 | The full typed IPC stack: `Shenora.Ipc` contracts + dispatcher + facades, `Shenora.Core` event bus, `Shenora.WebView2` postMessage bridge, the `@shenora/react` client, live round-trip |
| `43f18ad` | P4 | Scoped-container router + IPC composition; frameless chrome + window commands; STA dialogs/shell/clipboard/interaction; drag-drop zones + `useDropZone`; secondary windows + tray |
| `0776f37` | P1.1 | Local-feed consumption smoke — caught + fixed a real npm ESM packaging bug (extensionless imports) |
| `4ebb8e0` | P5 | `Shenora.WebView2.Sessions`: session browser, render-session pool, login windows, co-browse streaming |

Layout: `src/` (5 packable projects + `Shenora.React/`), `tests/Shenora.Tests` (one project, folders
mirror src), `samples/` (desktop + web; the e2e subject), `devtools/` (one-entry dev loop). Detail
per package is in `docs/ARCHITECTURE.md` — this guide does not duplicate it.

## 3. Invariants by area — where "correct" is DEFINED

The knowledge rules encode the earned invariants. Read the matching rule before reviewing an area;
a finding that contradicts one of these is either a real regression or a rule that needs updating.

| Area (files) | Invariant source | What to check |
|---|---|---|
| IPC stack (`src/Shenora.Ipc/`, `WebViewIpcBridge`, `Shenora.React/src/`) | `.claude/knowledge/ipc-contracts.md` | C#⇄TS wire mirror in lockstep; **no raw exception text on ANY error path** (only `OperationException`/error codes cross the bridge); `DispatchAsync` never throws / never returns null; notifications always batched; ready-gate resets on navigation; camelCase wire via the frozen `IpcJson` options |
| WebView2 hosting/serving (`src/Shenora.WebView2/`) | `.claude/knowledge/webview2-hosting.md` | environment thread-affinity; `IsHandleCreated` checked BEFORE `InvokeRequired`; non-blocking `BeginInvoke` (never blocking `Invoke` off the UI thread); init-timeout guard; sync-bundle vs deferred-scheme serving split; JSON-escaped script injection; CDP arg re-append (the env-var-ignored gotcha) |
| Any public API / naming / new type | `.claude/knowledge/generic-library.md` + D13 | generalized shape (no consumer vocabulary), options records, seams; every public type earns its keep; no UI-component-library dependency |
| Extraction ports (all of `src/`) | `.claude/knowledge/extraction-sources.md` | post-mortem comments kept; the listed gaps actually fixed (no `as dynamic`, no static mutable registry, `ILogger` not console, async-interleaved dispatch not `Task.Run`-per-message) |
| Windows/build/shell | `.claude/rules/windows-dev-gotchas.md` | PS5 UTF-8/BOM traps; `fs.cpSync` avoided; WinForms `AllowDrop`/OLE handle-creation must be on an STA thread (xunit workers are MTA) |
| Any tracked file / commit message | `.claude/rules/sensitive-info.md` | NO absolute local paths, NO private sibling names, NO personal/network data (this repo goes public) |

## 4. Highest-risk areas — where to spend the review budget

1. **UI-thread marshalling (the whole stack, acute in `Shenora.WebView2.Sessions`).** Every
   WebView2 touch marshals to a WinForms UI thread via `BeginInvoke` + `TaskCompletionSource`.
   Look for: blocking `Invoke` off the UI thread (a measured AppHang in the family);
   `InvokeRequired` checked without `IsHandleCreated` first (pre-handle it lies); `async void` /
   `BeginInvoke(async …)` bodies without exhaustive try/catch (unobservable UI-thread crashes);
   TCS double-completion; `ConfigureAwait` polarity (true inside UI bodies, false off them);
   dispose racing in-flight ops; semaphore permit leaks. (P5's phase review already fixed a batch
   here — see §5 — but this is the standing hotspot.)
2. **The IPC error boundary.** The single most important contract: raw exception text must never
   reach the client. Trace every `catch` in the dispatcher, facades, the bridge, and the sample
   routes — expected failures become `OperationException`/structured codes; unknowns become
   `UNKNOWN_ERROR` with details kept host-side only.
3. **WebView2 lifecycle & resource ownership.** Controls, forms, CTS, `SemaphoreSlim`, channels,
   and event subscriptions (`WebMessageReceived`, `NavigationCompleted`, DevTools receivers,
   `DownloadStarting`, `NewWindowRequested`) — is every subscription detached and every
   `IDisposable` honored on every path incl. exceptions? The session pool's create/return/discard/
   dispose paths and `CoBrowseSession.DisposeAsync` are the densest.
4. **Packaging & versioning.** One `<VersionPrefix>` in `src/Directory.Build.props` is the only
   version source (npm/README synced by tooling, never hand-edited); `devtools/project.config.mjs`
   `packableProjects` must list every packable project; the npm package must resolve under **native
   Node ESM** (the P1.1 bug — bundler resolution hides it). `node devtools/dev.mjs pack` must
   produce all five nupkgs + the tarball.
5. **Sensitive-info.** Public repo. The pre-commit guard scans staged changes, but review tracked
   content too — any dev path, private sibling name, or personal data is a history problem once
   committed.

## 5. Already settled — do NOT re-raise these

**Accepted design deviations (with rationale in `docs/DECISIONS.md` / port comments):**
- `Shenora.Core` depends on the Microsoft DI *implementation* package, not just abstractions (D17 —
  the builder needs `BuildServiceProvider`).
- `WindowCommandFacade` / the drop-zone stack live in `Shenora.WebView2` (not `WinForms`) because
  they need `Shenora.Ipc`, which `WinForms` deliberately does not reference.
- `Shenora.WebView2.Sessions` depends on `Shenora.WebView2` (D14 keeps the session stack out of the
  core hosting package), and `Shenora.WebView2` depends on `Shenora.WinForms` (**D19** — the two
  Windows packages are one layer: primitives, then web hosting on top). Both edges are deliberate, so
  neither is a violation; the old "never sideways" rule was retired on evidence. `WinForms` →
  `WebView2` is still forbidden, and `WinForms` still carries no `Ipc` dependency. Note the Sessions
  edge is currently **declared but unused**: nothing in the package imports a `Shenora.WebView2`
  type, which is `TASKS.md` H4.4.
- The portable contracts (`IUiDispatcher`, `IFileDialogs` + models, `IClipboardService`,
  `IUrlLauncher`, `IUiInteraction`) live in `Shenora.Core` with their implementations in
  `Shenora.WinForms` (**D20**), and `WinFormsUiDispatcher` is public deliberately — a
  `ProjectReference` does not grant `internal` access, so the alternative was `InternalsVisibleTo` for
  two packages.
- `CoBrowseSession` reuses `SessionController` as a **background** controller (the source's own
  pattern); its window-managing calls are gated inert by a `foreground` flag.
- `PayloadHelper` is static; `IpcResponse.category` is lowercase; notifications are always batched —
  all documented deviations from the source shapes.

**P5 phase-review findings already fixed** (full list in `docs/ROADMAP.md` P5-close entry): the
foreground/background controller split (hold-close no longer vetoes `Application.Exit`); pool
init-failure/dispose leaks; a `SemaphoreSlim.Dispose()`-races-cancelled-waiter hang
(`docs/FIX-LOG.md`); silent-refresh ownerless modal; loading-splash fallback; drag button state;
cached co-browse viewport; request-filter `about:blank` page-source; init-timeout on env creation;
sample lease timeout; the pack/README packaging gap; controller taps accumulate.

**Deliberately deferred** (recorded in the private notes; not defects to file):
- Renaming the login-named types (`SessionController`/`LoginCookie`/`DownloadHit`) to
  session-neutral names — a documented pre-1.0 option, revisit only if a pure co-browse consumer
  finds it awkward.
- STA-wrapping the new pool/login tests — the earned STA rule's trigger (`AllowDrop`/OLE) does not
  apply to those forms, and the tests are deterministically green.
- `LoginWindow`'s busy gate can be released by the cancellation fallback a beat before `ShowDialog`
  returns — the driver's linked token closes the window promptly, so the stacked-dialog race window
  is tiny.

## 6. What's verified vs what's not (so thin coverage isn't mistaken for a gap)

- **Gated by tests:** the public surface is pinned by API-surface baseline tests
  (`tests/Shenora.Tests/Api/Baselines/*.txt` — drift fails the build) — but **public members only**:
  `protected` members (including `BaseFacade.RouteMessageAsync`, the one every consumer overrides),
  default parameter values, `init`-vs-`set` and `required` are NOT gated yet (`TASKS.md` H6). Unit tests cover the pure/
  seam-testable logic: Core env/paths/builder/event-bus, Ipc envelopes/dispatcher/facade/router/
  payload, WinForms dpi/window-state/single-instance/dialog seams, WebView2 bridge + drop-zone
  seams, React bridge/hooks/services, Sessions pool accounting (via factory/reset seams), login
  gate mechanics, cookie-flow freshness logic, and the co-browse protocol builders.
- **Proven live (e2e), not unit-tested:** real WebView2 behavior is driven against the sample via
  CDP (`window.__shenora`) + `win-input` + `wgc` screenshots — hosting, IPC round-trip,
  notifications, window commands, drop-zone registration, secondary windows, and the P5 render-
  session pool round-trip. This split is the family precedent: real browser processes are the
  sample's job, not the unit suite's.
- **Manual / not yet exercised:** real interactive login, real co-browse screencast+input streaming,
  real file dialogs/clipboard, and frameless-chrome visuals — these need a human or a real provider
  and are validated by hand. Judge their code by inspection against the source + the invariants,
  not by expecting a test.

Current totals: **318 dotnet + 39 vitest** green; `verify` PASSED; `doctor` consistent.

## 7. Reproduce / verify locally

```
node devtools/dev.mjs verify   # build · test · sensitive scan · knowledge check — the "am I done?" gate
node devtools/dev.mjs pack     # all five nupkgs + the npm tarball into publish/packages
node devtools/dev.mjs sample --dev   # run the sample against Vite with CDP (e2e subject)
node devtools/dev.mjs vite      # the sample's Vite dev server (port 3900)
node devtools/dev.mjs shot|wgc|click|input   # desktop capture/input without stealing focus
```

Report findings as file:line + why it's real (a concrete failure scenario), ranked by severity —
no style nits. If a finding contradicts a rule in `.claude/knowledge/`, say which, so the invariant
can be corrected rather than silently worked around.
