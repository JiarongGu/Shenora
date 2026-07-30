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
- **Serving something SEEKABLE or large** — video, audio, anything a page scrubs through — needs a
  `DeferredSchemes` handler, not a folder mapping: `SetVirtualHostNameToFolderMapping` cannot honour
  `Range`, which is the reason apps hand-roll this. The handler receives the request headers and
  returns a status, headers and a **stream**, so nothing is buffered whole:
  `WebViewByteRange.TryParse(request.GetHeader("Range"), length, out var range)` then
  `WebViewResourceResponse.PartialContent(...)`, or `RangeNotSatisfiable(length)` when
  `range.IsSatisfiable(length)` is false. Serve the whole file with `Ok(...)` when there is no range.
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

Both were written against this surface and run before this guide claimed they could be
(P6.4) — the shapes below are what that produced, not a sketch.

- **Host side.** Derive from `BaseFacade`, one instance per existing module, and let
  `RouteMessageAsync` call your module's handler. `request.Type` is your action; rebuild whatever
  document shape your handler expects from `request.Payload` (if your client spread the payload at
  the top level, nest/unnest here — it is a few lines). Return `null`: your modules answer with
  events, which is the `post` shape, and answering at all is what buys correlation. Emit through
  `IEventBus.EmitAsync(module, type, payload)`. Nothing about this needs Windows, so the adapter can
  live in a `net10.0` project (see Stage 4).
- **Client side.** `bridge.post(module, type, { payload })` for the send; `eventBus.subscribeToAll`
  for a legacy "every host message" handler. Emit real `(module, type)` pairs from the host adapter
  rather than tunnelling everything through one reserved pair — that is what lets a migrated
  component use `useShenoraEvent`/`createShenoraStore` while unmigrated stores still see the same
  event through the firehose. Tunnelled events are invisible to both, which makes migration
  all-or-nothing per event.

> ⚠ **The trap that quietly undoes the error boundary.** Do NOT wrap a caught exception as
> `throw new OperationException(code, message: ex.Message)`. An `OperationException`'s message crosses
> the wire VERBATIM by design — it is *your* words for an expected failure — so that one line puts raw
> exception text (paths, connection strings) back on the page. It is exactly the line you would port
> if your old dispatcher emitted `$"{action} failed: {ex.Message}"`. Let the exception escape instead:
> `BaseFacade` maps it to `UNKNOWN_ERROR` plus the exception's type name and logs the detail host-side.

Three things the adapters lean on that are worth knowing before you write yours:

- **The dispatch surface carries a `CancellationToken`**, and it is the CALLER's lifetime —
  `WebViewIpcBridge` ties it to its own and cancels on `Dispose`, so a handler still awaiting when the
  page goes away learns it. Pass it straight into your module's own `ct` parameter. It is *not*
  per-request client cancellation: a one-way `post` has nobody waiting, so "stop that operation" stays
  an app-level CANCEL route carrying an `operationId` (see the one-way design). Work you deliberately
  hand OFF to the background outlives the request — give that its own token.
- **`IEventBus.Emit`** is the fire-and-forget twin of `EmitAsync`, for bridging a synchronous
  `Action`-shaped emit callback without discarding a task.
- **`IpcErrorMapping.ToError`** gives you the kit's exception → wire-error policy where there is no
  response to attach one to — the case you hit if your client watches an event stream for failures.
  Use it rather than retyping the policy; that copy is how raw exception text eventually reaches a
  page.

What you gain immediately: correlated request/response where you want it, a structured error
boundary that never leaks exception text, batched notifications, and a ready gate that buffers events
until the page is listening.

- **Dynamically composed modules** (plug-ins, licence-gated features, per-tenant modules): map your
  own modules first, then offer the rest through `TryMapModule`, which returns false if the name is
  taken. `MapModule` throws on a duplicate. Ask what is claimed via `IModuleRegistry`.
  Turn one off again with `TryReleaseModule` — it stops answering and its name frees up for a
  replacement, with no restart. Requests already inside the facade finish, and the facade is not
  disposed (its lifetime is yours).
- **Shared, host-fed state** (progress, status — the many-watchers case): `createShenoraStore` opens
  ONE subscription per event type however many components read it, and takes a `snapshot` on the
  first subscriber so a component that mounts mid-operation is not empty. Use `useShenoraEvent` for a
  one-off reaction in a single component, and `eventBus.subscribeToModule`/`subscribeToAll` when the
  event vocabulary isn't knowable up front — plug-in-contributed types, a diagnostics tap, the legacy
  firehose above.
- **Failures of a one-way send** have no promise to reject, so wire `configureBridge({ onPostError })`
  once at startup or they are invisible.

> ⚠ **Dev-loop trap.** A dev server pre-bundles `@shenora/react`. After upgrading the package, clear
> that cache (for Vite: delete `node_modules/.vite`) and restart it, or the page silently runs the
> OLD client — imports resolve to `undefined` and the app renders blank while the host looks healthy.

**Verify:** one round-trip works, an error arrives as a structured code rather than exception text,
and a page reload re-establishes events without duplicate subscriptions.

---

## Stage 4 — portability (optional, but cheap here)

**The point is enforcement, not tidiness.** A `net10.0` project cannot reference a Windows type, so
the compiler checks on every build what a document could only assert — and it stays checked when
someone later reaches for `Screen`, `Form` or `Application` inside app logic. It also makes the same
logic usable from a non-WinForms shell later, which is what D20 is for.

**The recipe**, as proven twice in this repo (`samples/Shenora.Sample.Logic` and the P6.4 host
adapter, which needed no Windows reference either):

1. **New project, plain `net10.0`** — no `-windows` suffix, no `UseWindowsForms`. Reference
   `Shenora.Core` and, if it holds facades, `Shenora.Ipc`. **Do not reference `Shenora.WinForms` or
   `Shenora.WebView2`**; adding either defeats the guard entirely, which is the one way this goes
   wrong quietly.
2. **Add it to your solution.** A guard project that nothing builds is not a guard. (This repo learned
   that the hard way: the samples were missing from the solution file, so the "am I done?" gate never
   compiled them.)
3. **Move the facades, then fix what goes red.** Each error is a genuine platform dependency; the
   fix is nearly always to inject a contract instead:

   | App logic reaches for | Inject instead (all in `Shenora.Core`) |
   |---|---|
   | `OpenFileDialog` / `SaveFileDialog` / `FolderBrowserDialog` | `IFileDialogs` (+ `FileDialogOptions`, `FileDialogFilter`, `FileDialogResult`) |
   | `Clipboard` | `IClipboardService` |
   | `Process.Start(url)` / `ShellExecute` | `IUrlLauncher` |
   | `Control.Invoke` / `BeginInvoke` / `InvokeRequired` | `IUiDispatcher` |
   | Enabling/disabling the window while busy | `IUiInteraction` |
   | App root, data and resource paths | `ShenoraPaths` |
4. **Leave the genuinely platform-bound routes behind** in the desktop project. Reveal-in-Explorer,
   secondary windows on their own STA threads, tray behaviour, window geometry — these are desktop
   concepts and forcing them through a portable contract only produces a contract nobody else can
   implement. `Shenora.Sample.Logic` and the desktop sample's own facade split exactly along that line.
5. **Register nothing extra.** `UseWinForms` registers both faces of each contract — the Windows one
   (`IShellLauncher`, `IFormInteraction`) and the portable one (`IUrlLauncher`, `IUiInteraction`) —
   against one implementation, so injecting the portable face just works.

**What is deliberately NOT portable, so you do not go looking:** the window-state stack
(`WindowStateManager`, `IWindowStateStore`) stays in `Shenora.WinForms`. Its signatures happen to look
platform-neutral, and that is not the bar — window geometry is a desktop concept, and the bar is "app
logic must be able to compile off Windows". Same for `OptimizedForm`, `TrayIcon`, `SplashPanel`,
`SecondaryWindows` and `SingleInstanceGuard`.

**If a contract does not fit**, say so — that is the feedback D20 wants. The portable set was derived
from what the surveyed apps actually needed, so a capability you cannot express through it is a real
finding, not a misuse.

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
