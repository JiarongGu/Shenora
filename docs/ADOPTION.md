# Adopting Shenora into an existing desktop app

For an app that already has a WinForms + WebView2 shell and wants to stop maintaining it. It assumes
nothing about this repo's history: everything needed is here or linked.

**The order matters more than the pieces.** Stage 1 carries no IPC dependency, so it deletes the most
duplicated code for the least risk; the IPC substrate comes last because it is the only stage that
touches every module. Keep the app runnable and shipped at the end of every stage — none of this
requires a big-bang branch.

**What Shenora is not.** It is a library, not an application framework: it ships the desktop *body*
and no product decisions. It has no UI components and no design system (D13), no state library, and
no opinion about your domain. Anything in the "stays yours" column below stays yours permanently —
that is the design, not a gap.

---

## Stage 0 — consume the packages, change nothing

Reference the **leaf** package you need; the rest arrive transitively
(`Shenora.WebView2.Sessions` → `Shenora.WebView2` → `Shenora.WinForms` → `Shenora.Ipc` +
`Shenora.Core`). Reference `Shenora.WinForms` directly only for a shell with no web frontend. Pin
exact versions — see `docs/RELEASING.md` for the pre-release feed recipe.

> ⚠ **Pre-release trap that costs an afternoon.** NuGet's global folder (`~/.nuget/packages`) is
> keyed on id+**version** and beats every source, so re-consuming the same pre-release version can
> silently restore a package you cached weeks ago — no warning, no restore error, and `--no-cache`
> does not help (that is HTTP caching). The symptom is a type "not existing" that plainly does exist.
> Diagnose by comparing `obj/project.assets.json`'s recorded dependencies against the `.nuspec`
> inside the nupkg; fix with `dotnet nuget locals global-packages --clear`, or delete
> `~/.nuget/packages/<id>/<version>`. Packing from this repo evicts them for you.

Build and ship. Nothing has changed yet — this stage only proves the feed.

---

## Stage 1 — shell primitives (no IPC dependency, highest payoff)

These live in `Shenora.WinForms` and know nothing about IPC, so they can land one at a time.

| You probably hand-rolled | Use | Notes |
|---|---|---|
| Window bounds save/restore | `WindowStateManager` (+ `IWindowStateStore`, `JsonFileWindowStateStore`) | Stores LOGICAL px and restores physical, validates the rect is reachable, and prefers `IAppMaximizable` over `Form.WindowState`. **Fixes a bug you likely have:** hand-rolled versions reach for `Screen.WorkingArea`, which is DPI-mis-scaled on a HiDPI monitor (~12 px per edge); the kit uses `GetMonitorInfo`. |
| A double-buffered form base / frameless chrome | `OptimizedForm(+Options)` | Frameless is opt-in. Maximize is manual (work-area fill), so `IsAppMaximized` is the truth, **not** `Form.WindowState`. |
| Caption buttons drawn by the page | `OptimizedFormOptions.NativeCaptionButtons` + `CaptionButtonColors` | Report the rects via `SetCaptionButtons`; the window clips them out of every covering child and paints them, which is what buys Windows 11 **Snap Layouts**. Requires `FramelessChrome` — the combination throws at construction rather than doing nothing. |
| Tray icon + themed menu | `TrayIcon(+Options)`, `TrayMenuColors` | **`CloseReason.UserClosing` also means a programmatic `Close()`** — with close-to-tray on, a startup-abort path that calls `Close()` leaves a resident process. Close via `ExitApplication()`. |
| Single-instance mutex + activate-existing | `SingleInstanceGuard` | Idempotent by design (an OS mutex is per-thread reentrant, which broke the naive version). |
| File dialogs / clipboard / shell open / reveal | `IFileDialogs`, `IClipboardService`, `IUrlLauncher`(+`IShellLauncher`), `IUiInteraction`(+`IFormInteraction`) | Dialogs run on a dedicated STA thread with owner-handle z-order. The portable halves live in `Shenora.Core` — see Stage 4. |
| Extra windows on their own threads | `SecondaryWindows` | `FormClosed` is **not** the end of a window; cleanup happens after `Application.Run` returns, or a WebView2 child leaves a locked profile folder. |
| App root / data / resources paths, env overrides | `ShenoraPaths(+Options)` | Resolves and absolutizes; file dialogs move the process CWD, so a relative root must not be re-resolved later. |
| Startup splash | `SplashPanel(+Options)` | Colours are yours. |
| OS file drag-drop over page elements | `DropZoneManager` + `DropZoneFacade` (in `Shenora.WebView2`) | Needs IPC, so it belongs to Stage 3 — but it is usually the third copy of the same code in a family, so plan for it. Zones now clear on **document change**, not on the ready handshake, so there is no ordering contract against `notifyReady` any more. |

**Verify a stage-1 change:** the window still restores to the right monitor at the right size after a
DPI change, and closing while maximized reopens maximized.

---

## Stage 2 — the WebView2 host

Replace hand-rolled `EnsureCoreWebView2Async` + settings + event wiring with `WebViewHost` +
`WebViewHostOptions`.

- **Serving.** `FolderMappings` maps one or more virtual hosts to folders — including a deliberately
  DIFFERENT origin when you need cross-origin ES-module imports (set `AccessKind`). Embedded-resource
  serving and app schemes are available too (`ResourceProvider`, `DeferredSchemes`). A `DevUrl` gives
  you the dev-server switch a hand-rolled host usually lacks, which is the stale-bundle footgun.
- **Policies you may not have.** `NewWindowRequested`, `PermissionRequested`, `ProcessFailed` and
  download handling are wired with safe defaults; app hooks fall back to the built-in policy if they
  throw, because leaving one of those events unanswered is its own bug.
- **Init is idempotent and bounded** (`InitTimeout` covers the whole sequence, not each step).
- HTML is served no-cache while hashed assets are cacheable; keep your bundle's asset hashing.

**Verify:** the app loads from the dev server and from the packaged bundle, and an external link
opens in the system browser exactly once.

---

## Stage 3 — the IPC substrate

This is the only stage that touches every module, so it goes last — and it is smaller than it looks
if both sides of your IPC funnel through one place, which they usually do.

**Keep your message shape.** If your app posts fire-and-forget messages and streams results back as
events, that is the RIGHT default for a desktop app and Shenora does not ask you to change it. Use
`bridge.post` for it; reserve the correlated `bridge.invoke` for calls that are **quick and
UI-thread-safe**, because the dispatch pipeline preserves the caller's synchronization context by
design, so a route's synchronous segment runs on the UI thread. Measured on the sample: the same 3 s
of work stalls the UI thread 2 027 ms when left in the route and 0 ms when handed off and streamed.
See `docs/2026-07-31-shenora-oneway-ipc-design.md`.

**Write two adapters, not 200 edits.** A client shim mapping your `post`/`subscribe` pair onto the
bridge, and a host adapter presenting your module interface to `IMessageDispatcher`. Then your
existing modules and call sites keep working while the transport, error boundary, batching and ready
gate change underneath. **Those adapters belong in your repo, not in the kit** — the kit's envelope
stays uncontaminated by any one app's wire format (D21).

What you gain immediately: correlated request/response where you want it, a structured error
boundary that never leaks exception text, batched notifications, and a ready gate that buffers events
until the page is listening.

- **Dynamically composed modules** (plug-ins, licence-gated features, per-tenant modules): map your
  own modules first, then offer the rest through `TryMapModule`, which returns false if the name is
  taken. `MapModule` throws on a duplicate. Ask what is claimed via `IModuleRegistry`.
  **Known limit:** a mapped module cannot be released — the pipeline only grows, so disabling a
  dynamic module needs a restart. Say so if you need otherwise; it is a recorded gap, not a refusal.
- **Shared, host-fed state** (progress, status — the many-watchers case): `createShenoraStore` opens
  ONE subscription per event type however many components read it, and takes a `snapshot` on the
  first subscriber so a component that mounts mid-operation is not empty. Use `useShenoraEvent` for a
  one-off reaction in a single component.
- **Failures of a one-way send** have no promise to reject, so wire `configureBridge({ onPostError })`
  once at startup or they are invisible.

> ⚠ **Dev-loop trap.** A dev server pre-bundles `@shenora/react`. After upgrading the package, clear
> that cache (for Vite: delete `node_modules/.vite`) and restart it, or the page silently runs the
> OLD client — imports resolve to `undefined` and the app renders blank while the host looks healthy.

**Verify:** one round-trip works, an error arrives as a structured code rather than exception text,
and a page reload re-establishes events without duplicate subscriptions.

---

## Stage 4 — portability (optional, but cheap here)

Put your facades in a `net10.0` project (no `-windows`) that references only `Shenora.Core`, and
inject the portable contracts — `IUrlLauncher`, `IClipboardService`, `IFileDialogs`,
`IUiDispatcher`. The Windows implementations are registered by `UseWinForms` in the desktop host.

The point is enforcement, not tidiness: if a Windows type ever creeps into your app logic, that
project turns red instead of the portability staying merely asserted. It also makes the same logic
usable from a future non-WinForms shell.

---

## What stays yours, permanently

- **Your domain.** Modules, routes, payload schemas, business rules.
- **Every colour, size and pixel.** The kit takes a palette (`CaptionButtonColors`, `TrayMenuColors`,
  splash colours) and ships no design system (D13).
- **Transport-level product decisions** — what an "operation" is, its phases and progress shape,
  whether work queues, what a viewer looks like. The kit ships primitives and lifecycle hooks (D21).
- **Your state management.** `createShenoraStore` is built on React's `useSyncExternalStore`; if you
  already use a store library, keep it and subscribe through `useShenoraEvent`.
- **Your event/enum vocabulary.** Module and event names are app schema.

## If the kit almost fits

Say so rather than working around it — before 1.0 the surface is still cheap to change, and "the
framework almost fits, but…" is the most valuable feedback this phase can produce. A capability you
need and cannot express is a gap worth fixing; the reverse — the kit growing your product's shape —
is the failure mode the library discipline exists to prevent.
