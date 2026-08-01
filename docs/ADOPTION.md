# Adopting Shenora into an existing desktop app

For an app that already has a WinForms + WebView2 shell and wants to stop maintaining it. It assumes
nothing about this repo's history: everything needed is here or linked.

**The order matters more than the pieces.** Stage 1 carries no IPC dependency, so it deletes the most
duplicated code for the least risk; the IPC substrate comes last because it is the only stage that
touches every module. Keep the app runnable and shipped at the end of every stage — none of this
requires a big-bang branch.

**One section below is not a stage at all.** [The mission scheduler](#the-mission-scheduler--not-a-stage-adoptable-on-its-own)
lives in `Shenora.Core` and needs no shell, no IPC and no Windows, so it can be taken first, last, or
on its own by an app that wants nothing else here.

**What Shenora is not.** It is a library, not an application framework: it ships the desktop *body*
and no product decisions. It has no UI components and no design system (D13), no state library, and
no opinion about your domain. Anything in the "stays yours" column below stays yours permanently —
that is the design, not a gap.

---

## Stage 0 — consume the packages, change nothing

Reference the **leaf** package you need; the rest arrive transitively. The graph is a DIAMOND over
`Shenora.Core`, not a single chain:

```
                    Shenora.Core          net10.0        portable: no Windows reference
                      ↑          ↑
        Shenora.Ipc ──┘          └── Shenora.WinForms    net10.0-windows
          net10.0                          ↑
              ↑                            │
              └──── Shenora.WebView2 ──────┘             net10.0-windows
                            ↑
              Shenora.WebView2.Sessions                  net10.0-windows
```

Reference `Shenora.WinForms` directly only for a shell with no web frontend. Pin exact versions —
see `docs/RELEASING.md` for the pre-release feed recipe.

> ⚠ **`Shenora.WinForms` does NOT bring `Shenora.Ipc` with it** — they are SIBLINGS over
> `Shenora.Core`, not a chain. The edge is absent on purpose, in both directions: `Shenora.Ipc` binds
> to no UI framework (it targets `net10.0`, which is what lets the same envelopes ride a WebSocket or
> a mobile channel, D16), and `Shenora.WinForms` carries no IPC dependency — which is why the two
> IPC-facing desktop facades, `WindowCommandFacade` and `DropZoneFacade`, live in `Shenora.WebView2`,
> the first package that may see both halves. So a WinForms-only shell that wants typed messaging
> adds `Shenora.Ipc` as a second, explicit `PackageReference`. Without it `BaseFacade`/`IpcRequest`
> simply do not resolve, and the error names a missing namespace rather than a missing package.

> ⚠ **Pre-release trap that costs an afternoon.** NuGet's global folder (`~/.nuget/packages`) is
> keyed on id+**version** and beats every source, so re-consuming the same pre-release version can
> silently restore a package you cached weeks ago — no warning, no restore error, and `--no-cache`
> does not help (that is HTTP caching). The symptom is a type "not existing" that plainly does exist.
> Diagnose by comparing `obj/project.assets.json`'s recorded dependencies against the `.nuspec`
> inside the nupkg; fix with `dotnet nuget locals global-packages --clear`, or delete
> `~/.nuget/packages/<id>/<version>`. Packing from this repo evicts them for you.

Build and ship. Nothing has changed yet — this stage only proves the feed.

---

## Stage 1 — shell primitives (no IPC dependency, land one at a time)

These live in `Shenora.WinForms` and know nothing about IPC, so they can land one at a time. Payoff
is **proportional to what you actually hand-rolled** — an app that owns a splash launcher, wraps
`Process.Start` for tests and never bothered with a single-instance mutex will only see the window
and secondary-window rows apply. Table row = a specific handrolled piece it replaces, not a claim
that every app needs every row.

| You probably hand-rolled | Use | Notes |
|---|---|---|
| Window bounds save/restore | `WindowStateManager` (+ `IWindowStateStore`, `JsonFileWindowStateStore`) | Stores LOGICAL px and restores physical, validates the rect is reachable, prefers `IAppMaximizable` over `Form.WindowState`, and shrinks a size saved on a bigger display to fit a smaller one (`WindowStateOptions.MaxToWorkArea`, default on). Per-monitor DPI accuracy is the default — `AttachTo(form)` defers to `HandleCreated` when the form has no handle yet and resolves `DeviceDpi` at that moment (still before `Show`, so no resize flash). The explicit `AttachTo(form, scale)` overload is for the unusual case where you want to size against a scale you resolve yourself (a test harness, a preview against a different monitor). A saved `Maximized=true` is also deferred to `Shown` — setting `WindowState.Maximized` earlier does not survive `OnLoad` on a plain `Form`. |
| A double-buffered form base / frameless chrome | `OptimizedForm(+Options)` | Frameless is opt-in. Maximize is manual (work-area fill), so `IsAppMaximized` is the truth, **not** `Form.WindowState`. **Fixes a bug you likely have:** hand-rolled work-area code reaches for `Screen.WorkingArea`, which is DPI-mis-scaled on a HiDPI monitor (~12 px per edge — the visible gap the manual maximize path exists to remove); `OptimizedForm` uses `GetMonitorInfo`. |
| Caption buttons drawn by the page | `OptimizedFormOptions.NativeCaptionButtons` + `CaptionButtonColors` | Report the rects via `SetCaptionButtons`; the window clips them out of every covering child and paints them, which is what buys Windows 11 **Snap Layouts**. Requires `FramelessChrome` — the combination throws at construction rather than doing nothing. |
| Tray icon + themed menu | `TrayIcon(+Options)`, `TrayMenuColors` | **`CloseReason.UserClosing` also means a programmatic `Close()`** — with close-to-tray on, a startup-abort path that calls `Close()` leaves a resident process. Close via `ExitApplication()`. |
| Single-instance mutex + activate-existing | `SingleInstanceGuard` | Idempotent by design (an OS mutex is per-thread reentrant, which broke the naive version). |
| File dialogs / clipboard / shell open / reveal | `IFileDialogs`, `IClipboardService`, `IUrlLauncher`(+`IShellLauncher`), `IUiInteraction`(+`IFormInteraction`) | Dialogs run on a dedicated STA thread with owner-handle z-order. The portable halves live in `Shenora.Core` — see Stage 4. |
| Extra windows on their own threads | `SecondaryWindows` | `FormClosed` is **not** the end of a window; cleanup happens after `Application.Run` returns, or a WebView2 child leaves a locked profile folder. |
| App root / data / resources paths, env overrides | `ShenoraPaths(+Options)` | Resolves and absolutizes; file dialogs move the process CWD, so a relative root must not be re-resolved later. |
| Startup splash | `SplashPanel(+Options)` | Colours are yours. |
| OS file drag-drop over page elements | `DropZoneManager` (in `Shenora.WebView2`) + **`useDropZone`** | **Not optional sugar — the only workable file-drop path for a desktop shell, and the page's own drop event is what it replaces.** A page-side `onDrop` yields a `File` whose only accessor is its CONTENT, so with the page as UI and the host doing the file work, the bytes must be read into the renderer and pushed across IPC: a full copy of every dropped file, EAGERLY, at drop time, before the app knows whether it wants any of them. Drop 200 files to filter by extension and you pay for all 200; drop a multi-GB asset and you pay that, to reach a file the host could have opened off the same disk. `DropZoneManager` puts transparent native overlays over the page's zone elements, reads the OS drag data directly, and hands you `string[]` paths — open lazily, stream, hash incrementally, move or link without copying — including drags from Explorer or another app while your window is **backgrounded**. Wiring: **Stage-1-adoptable STANDALONE despite living in the WebView2 package** — it depends only on `Shenora.Core` (`IEventBus`), the WebView2 control and a `Form`, and references no `Ipc` type at all. `new` it, hand it your own bus, subscribe to its three events, and forward them over whatever transport you already have — no Stage 3 migration required. (An earlier revision of this table filed the whole thing under Stage 3 because `DropZoneFacade` does need IPC; that is true of the FACADE, not the manager, i.e. not the part that is actually hard — an adopter found this only by reading the source.) Zones clear on **document change**, not the ready handshake, so there is no ordering contract against `notifyReady`. The IPC-wired half — `DropZoneFacade` + `useDropZone` — formally belongs to Stage 3 because it rides the typed bridge, but treat it as the DESTINATION for this row rather than an optional extra: a React page should call `useDropZone` and never register a DOM drop handler for files. |

> **If you only take one thing from Stage 1, take the drop zones.** They are the clearest case in the
> kit for adopting anything at all. Native drag-drop over a web view is genuinely fiddly — transparent
> overlays tracked against page-reported rects, cleared on document change, surviving a backgrounded
> window — and it is the most-copied component in the family by a distance: the kit's own source header
> notes its THIRD copy was already annotated "ported from…", and the first app to adopt the kit turned
> out to be carrying a fourth (387 C# + 84 TS lines) whose header says the same thing. Four independent
> ports of one component that no app can do without and none of them wanted to write is the argument
> for a shared body, in one row.

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
  **Register the scheme on the ENVIRONMENT as well as declaring the handler**, with the origins your
  page is served from:
  ```csharp
  CustomSchemes = [new WebViewCustomScheme { Name = "media", AllowedOrigins = ["https://app.local"] }]
  ```
  WebView2 accepts scheme registrations only at environment-creation time, so the handler alone is
  never enough — the host throws at construction if you declare one without the other, because the
  runtime symptom is otherwise a bare `TypeError: Failed to fetch` with nothing in the host log. CORS
  response headers are defaulted for you (`Access-Control-Allow-Origin` and, so a ranged `fetch` can
  READ `Content-Range`, `Access-Control-Expose-Headers`); set either yourself to tighten it.
  A worked, self-checking example is in the desktop sample (`RangeSchemeProbe`), which serves a
  ranged resource and asserts the page really receives a 206 with the right bytes at the right offset.
  ⚠ **Changing a scheme registration on an existing app can wedge startup** until its WebView2
  user-data folder is deleted — worth knowing before you conclude your code is at fault.
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
See `docs/DECISIONS.md` D23.

**Write two adapters, not 200 edits.** A client shim mapping your `post`/`subscribe` pair onto the
bridge, and a host adapter presenting your module interface to `IMessageDispatcher`. Then your
existing modules and call sites keep working while the transport, error boundary, batching and ready
gate change underneath. **Those adapters belong in your repo, not in the kit** — the kit's envelope
stays uncontaminated by any one app's wire format (D21).

Both were written against this surface and run before this guide claimed they could be
(P6.4) — the shapes below are what that produced, not a sketch.

> ⚠ **`RouteMessageAsync` changed shape in 0.2.0 (D23) — every override needs the parameter added.**
> `protected override Task<object?> RouteMessageAsync(IpcRequest request, CancellationToken ct)` →
> `(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)`. The one-line
> migration: add the parameter; ignore it if your facade doesn't emit. If it does, this is also the
> moment to replace a hand-typed module string at your emit call sites with `context.Publish(type,
> payload, scope)` — it is stamped with the facade's own `ModuleName`, so it cannot drift the way a
> literal re-typed at every call site can.

- **Host side.** Derive from `BaseFacade`, one instance per existing module, and let
  `RouteMessageAsync` call your module's handler. `request.Type` is your action; rebuild whatever
  document shape your handler expects from `request.Payload` (if your client spread the payload at
  the top level, nest/unnest here — it is a few lines). Return `null`: your modules answer with
  events, which is the `post` shape, and answering at all is what buys correlation. Emit through
  `context.Publish(type, payload, scope)` (or `IEventBus.EmitAsync(module, type, payload)` directly,
  if you're not yet on `BaseFacade`'s context). Nothing about this needs Windows, so the adapter can
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
- **Long-running work** (a ten-minute deploy, a render, a model download — the case "always produces
  a response" leaves undefined, 0.2.0/D23): a route that starts one calls `context.Run(new
  OperationOptions { Kind = "DEPLOY", Cancellable = true }, async (op, ct) => { … op.Report(new
  OperationProgress(40, 100, "percent")); … })` — `Value`/`Total`/`Unit` in the APP's own terms (bytes
  transferred against a known total, items against a known total, an absolute count with no known
  total, or a genuine percent — the kit never assumes which), passed through unchanged (no clamp, no
  validation) — and returns `new { operationId = … }` immediately — that IS the response for the
  long case. `context.Start` is the lower-level primitive if your lifecycle doesn't fit one
  background body (a start outside the block, several failure branches, a resumable session).
  Register `services.AddShenoraOperations()` once (opt-in — nothing is added to the pipeline until
  you do) and it ships `OperationsFacade` (`LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`/`DISMISS`/`WAIT`
  under module `OPERATIONS`) for free — no hand-rolled `…PROGRESS`/`…DONE` event pair per feature, and
  no per-app re-agreement of what "cancel this operation" means. Client side, `useShenoraOperations()`
  is a ready-made `createShenoraStore` instance: snapshots via `LIST` on first subscribe (so a
  progress strip that mounts mid-run isn't empty), folds `OPERATION_UPDATED` by id afterward, and
  folds `OPERATION_REMOVED { operationIds }` by deleting those ids — the one authoritative signal for
  `MaxHistory` eviction, `ClearFinished`, and a dropped crash-resume offer. What an operation actually
  means — its phases, whether it queues, what a viewer looks like — stays yours; the kit tracks only
  id/status/progress/cancel.
  The lifecycle also covers a run that stops mid-flight WITHOUT crashing — expired credentials, a
  throttling provider, DNS not yet propagated, or an app's own queue parking a just-started operation
  (`op.Wait("queued")`, no kit change needed): `op.Wait("dns", …)` (`Running` → `Waiting`, an
  OPTIONAL app-defined reason string, like `Kind` — omit it when the wait is self-evident, e.g. the
  user clicked Pause) and `op.Resume()` (`Waiting` → `Running`) on the SAME handle `Start`/`Run` gave
  you, plus `IOperationRegistry.Dismiss(id)` for the human declining a waiting offer outright
  (→ `Cancelled`, terminal). A client can ASK for either direction —
  `IOperationRegistry.RequestWait(id)`/`RequestResume(id)`, the `WAIT`/`RESUME` routes — for the
  download-manager/activity-panel shape (the user clicking Pause, then Resume, on visible work). Both
  only emit (`OPERATION_WAIT_REQUESTED`/`OPERATION_RESUME_REQUESTED`, same
  `{ operationId, module, kind, scope }` payload) and change nothing themselves: **asking is not
  acting** — your module's own `op.Wait()`/`op.Resume()` is what moves the state. `Find(id)` resolves
  a live handle back from a bare id, which is exactly what those two handlers need.
  `OperationStatus` carries ONE waiting value reached ONE way (a live `op.Wait()`), so there is no
  sub-case to tell apart. **Crash recovery is deliberately yours.** The kit briefly carried a
  `RegisterWaiting` + `ResumePayload` pair for announcing an operation it had never started; it was
  cut before publish because it forced everything to answer "does this entry still have a body?", and
  every way of answering that produced a defect. Keep your checkpoint token in your own store, and on
  restart begin the resumed run as an ordinary `Start()`/`Run()`. Want the pending offer visible while
  the user decides? `Start()` it and immediately `op.Wait("interrupted")`. See
  `docs/2026-08-01-shenora-communication-core-design.md` §5A for the full shape.
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

## The mission scheduler — not a stage; adoptable on its own

`Shenora.Core` ships ONE scheduler for the two things the family built five separate times: a
**filesystem operation planner** (serialize work that touches overlapping paths, run disjoint work in
parallel) and a **job queue** (bounded concurrency, retry, cancel, durability). They are the same
engine with different key types — paths conflict when one CONTAINS the other, lanes admit N holders at
once — and putting only that difference behind a seam is what makes adoption a DELETION rather than a
translation. Evidence, rationale and the deliberately-not-built list:
`docs/2026-08-02-shenora-mission-scheduling-design.md`.

**It needs nothing else from the kit.** `IMissionScheduler` is in `Shenora.Core`: no shell, no IPC, no
Windows, and not even the host builder — `new MissionScheduler(options)`, registered as a singleton in
whatever container you already use. Nothing above is a prerequisite.

**The bugs it deletes.** Every one of these was live in a hand-rolled queue or planner in this family,
and none of them is exotic — they are what this problem costs when each app solves it alone:

- A ref-counted per-key semaphore, removed at zero holders, where a check-then-remove race handed two
  callers *different* semaphores for the same key — so the resource that looked serialized was not.
  There is no per-key lock object here; the scheduler owns claim lifetime, so the race has nowhere to
  live.
- A documented lock ORDER between two key spaces (entity, then category) that every call site had to
  remember. A request declares its whole claim SET and is admitted only when all of it is free, so
  there is no acquisition order to get wrong.
- Path overlap tested with a naive `StartsWith`, which makes `a/bc` a child of `a/b`: two unrelated
  resources then serialize against each other forever, and the symptom reads as "the queue is slow".
  Containment is tested at a separator boundary.
- Two spellings of one location (`data\mods\..\mods\x` and `data/mods/x`) treated as different keys,
  so two mutations ran on one directory at once. Claims are normalized once, at submit.
- A compress-then-replace that retried the WHOLE operation when only the replace hit a locked target —
  seconds of recompression, up to three times, to redo a file move that takes microseconds.
- Work found RUNNING after a crash and re-run on every boot, turning one crash into a loop the user
  cannot escape from inside the app.

### Setup

```csharp
var scheduler = new MissionScheduler(new MissionSchedulerOptions
{
    DefaultLaneCapacity = 0,   // 0 = clamp(cores-1, 1, 4), the value both hand-rolled planners chose
    Scopes = [PathClaims.Scope, new FlatClaimScope("entity"), new FlatClaimScope("category")],
    Log = message => logger.LogDebug("{Message}", message),
});
scheduler.Lane("gpu").Capacity = 1;   // a scarce shared resource — see the lane trap below
```

Register only the scopes you use. A claim naming an **unregistered scope throws at submit** rather
than being ignored — silently dropping an exclusion the caller asked for is the one failure mode a
scheduler must not have. Pass an explicit `DefaultLaneCapacity` in your own tests: a concurrency
assertion keyed off the host's core count passes or fails by machine, which is how a parallelism
regression hides on the one box with two cores.

### What replaces what

| You probably hand-rolled | Use | Notes |
|---|---|---|
| A planner that serializes operations touching the same file or directory | `PathClaims.Scope` + one `PathClaims.Exclusive(path)` per path an operation MUTATES (source, target and temp) | Hierarchical: `C:\a` conflicts with `C:\a\b`, because deleting a directory must not run while something writes inside it. |
| Reads serialized behind writes they did not actually conflict with | `PathClaims.Shared(path)` | No hand-rolled planner in the family could express a reader/writer split, so all of them over-serialized. Several shared holders run together; an exclusive one waits. |
| A per-entity mutex or per-key semaphore dictionary | `MissionClaim.Exclusive("entity", id)` over a `FlatClaimScope` | Flat keys conflict only when equal. |
| A second lock for a coarser key, plus a lock-order rule | both claims on ONE request — `Claims = [MissionClaim.Exclusive("entity", id), MissionClaim.Exclusive("category", group)]` | Acquired as a set, so deadlock is structurally impossible and the lock-order rule stops being something anyone must remember. Guarded by `MissionSchedulerAdoptionTests.Claims_acquired_as_a_set_cannot_deadlock_on_lock_order`, which drives crossing pairs under a timeout (a deadlock shows up as a hang, so the assertion has to be the timeout). |
| A mailbox actor that serializes one stream of items | one `Exclusive` claim on a single key | The actor falls out of the model; the kit ships no `Actor` type on purpose. |
| A `maxConcurrency` constructor argument | `MissionSchedulerOptions.DefaultLaneCapacity` | Every request draws one permit from the default lane. |
| A static gate/semaphore singleton over a scarce resource (one GPU, a rate-limited endpoint) | `scheduler.Lane("gpu").Capacity = 1` + `Lanes = [new MissionLane("gpu")]` on the request | Removes the singleton, so it is testable and there can be more than one. A lane that is a BUDGET rather than a slot count takes weighted permits: `new MissionLane("vram", 4)`. |
| A live "max active" slider | the `ILane.Capacity` setter | Lowering it never cancels running work — the surplus is swallowed as items finish. Proven in both directions: `Lowering_lane_capacity_throttles_new_work_without_killing_running_work` and `A_lowered_capacity_is_enforced_once_the_surplus_drains`. The setter enforces a floor of 1 and no ceiling, so clamp to your own maximum before assigning. |
| A capacity governor that suspends work under system load | `ILane.Hold()` / `Release()` (re-entrant) | The kit ships the mechanism and no policy: load probes, hysteresis and debounce stay yours. This is the difference between "yield the GPU while the user games" and "kill the user's transcode". |
| Dedup of an identical pending operation; a batch merge of work accumulated during a slow plan | `MissionDefinition.Key` (+ `IsActive(key)` so you can skip building an expensive request you know would only be deduplicated) | A matching submission completes eagerly against the existing item with `MissionOutcome.Deduplicated`, and the body runs once. |
| `MAX_RETRY_ATTEMPTS` / `RETRY_DELAY_MS` constants | `RetryPolicy` | Same defaults as the family's measured value: 3 attempts, 500 ms × attempt, `IOException` only. `RetryPolicy.None` opts out; `Retry = null` already means none. |
| A retry loop wrapped around an expensive operation to survive a cheap final step | `Run` (the expensive phase, runs ONCE) + `Commit` (cheap, retried) | Setting `Commit` is what makes `Run` exempt from the retry budget. |
| A `Channel` + worker pool + gate, or a plan-swap with a signal and a worker task | the scheduler | Dispatch is event-driven — on submit and on each completion. No worker thread, no polling latency. |
| Priority or "not now" rules baked into the queue's own loop | `IMissionPolicy` (`Compare` = what, `ShouldStart` = when); default `PriorityMissionPolicy` is priority-then-FIFO | Ordering is a PRODUCT decision, so it is yours. A policy is only consulted about items that already passed admission, so the worst a buggy one can do is DELAY work — it cannot make conflicting work overlap or bypass a lane. A throwing policy is treated as "not now" rather than wedging the scheduler. |
| `GetPendingOperationCount()`, a queue/diagnostics view | `PendingCount`, `RunningCount`, `Snapshot()` | `Snapshot()` is a copy: safe to hold, stale the moment it returns. |
| Durable jobs in SQLite (or JSON) + resume on startup | `IMissionStore` over your EXISTING repository, `Durable = true` per request, then `RecoverAsync(rehydrate)` at a moment you choose | The kit ships no store implementation, by design — see below. `Kind` and `Payload` are yours, never interpreted. |
| A "do not auto-resume this crash-prone type" flag | `RecoveryPolicyFor` → `RecoveryPolicy.Fail` | Already the default for records found `Running`; `Queued` records requeue. The safe default is the one that cannot loop. |
| Opening and closing a progress operation by hand in every mission body | `IMissionObserver` — see below | Every call is guarded, so an observer that throws cannot fail the work it was only watching. |
| A `candidate.StartsWith(root)` guard on anything that turns caller input into a path | `PathClaims.IsContained(root, candidate)` | Not scheduling, but it belongs to the same file: it resolves `..` and `.` FIRST and tests at a separator boundary, so neither an escaping segment nor `C:\data-old` passes as being inside `C:\data`. |

**Definition and execution are separate types, and the distinction is worth ten seconds up front.** A
`MissionDefinition` is WHAT should run — body, claims, lanes, retry, dedup key. A `MissionExecution` is
ONE specific run of it: id, attempt, position in the queue, whether it is running. You construct
definitions; the scheduler hands you executions (to the body, to observers, to a policy, and out of
`Snapshot()`). Today one submit produces one execution, so the split buys you consistent vocabulary
rather than new power — it is there because a recurring or re-hydrated mission is one definition with
many executions, and introducing that later would change every one of those signatures at once.

The two-phase shape, which is what the `Run`/`Commit` split was designed from:

```csharp
var result = await scheduler.SubmitAsync(new MissionDefinition
{
    Claims = [PathClaims.Exclusive(cachePath), PathClaims.Exclusive(archivePath)],
    Run    = (_, ct) => archive.CompressToTempAsync(cachePath, tempPath, ct),   // expensive, ONCE
    Commit = (_, ct) => files.ReplaceAsync(tempPath, archivePath, ct),          // cheap, retried
    Retry  = new RetryPolicy(),
    Key    = new MissionKey($"compress:{entityId}"),
});
if (!result.Succeeded)
    logger.LogWarning(result.Error, "compress failed after {Attempts} attempt(s)", result.Attempts);
```

> ⚠ **A failing body does not throw out of `SubmitAsync`** — the failure comes back as
> `MissionResult.Outcome`, because a submitter is usually a batch loop that must survive one bad item.
> Check `Succeeded`/`Outcome`, or call `ThrowIfFailed()` if you prefer exceptions. Caller bugs
> (unregistered claim scope, disposed scheduler) still throw at submit; those are not outcomes of the
> work. If you port a call site that assumed "it threw, so it failed", it will now look like it
> succeeded.

> ⚠ **A lane is created on first mention, at the default capacity.** A misspelled lane name therefore
> does NOT throw — it silently gives you a second lane whose capacity is not the one you configured,
> and the exclusivity you thought you had is gone. Set lane capacities once at startup and keep the
> names in constants.

> ⚠ **The parallelism change is the real risk in this adoption, and nothing will tell you.** If your
> current planner runs one operation at a time (a single worker, a global gate), disjoint work starts
> overlapping the moment you switch. That is the upgrade — it is why the newer of the family's two
> planners was rewritten — but anything that quietly depended on the old accidental global ordering
> breaks silently. Find those call sites before you move the second batch of operations across, not
> after.

> ⚠ **A scheduler only protects what goes through it.** If you keep an audit of which call sites route
> through your old planner, keep it: adopting the kit does not make an unrouted `Directory.Delete`
> safe. The rule becomes "never mutate a managed resource outside a scheduled mission".

> ⚠ **A policy that defers on an EXTERNAL condition needs a nudge.** Dispatch happens on submit and on
> completion, so a clock, load or battery rule must call `Reevaluate()` when its condition changes or
> the deferred item waits for unrelated traffic to wake it. The kit owns no timer: polling belongs to
> whoever knows what is being polled.

### Progress reporting composes — it is not merged in

The scheduler is the EXECUTION half of long-running work; `Shenora.Ipc`'s operation registry (Stage 3)
is the REPORTING half, and they stay separate because `Shenora.Ipc` may depend on `Shenora.Core` and
never the reverse (D19/D20). `IMissionObserver` is the seam: `OnQueued`/`OnStarted`/`OnFinished` for every
item, each call guarded so a throwing observer cannot fail the work it was only watching.

**The adapter is yours to write. It is about 35 lines** — measured, not estimated: the kit's own
sample now carries one (`samples/Shenora.Sample.Logic/MissionOperationObserver.cs`), a
`ConcurrentDictionary<string, IOperation>` plus the three methods. Copy it. No mission body opens an
operation by hand again, which is the boilerplate the family's apps repeated at every call site and
occasionally forgot, leaving operations stuck "running" forever. The same seam is where metrics and
tracing attach.

Two things that adapter learned the moment it ran, both of which you will hit:

- **Open the operation in `OnQueued`, then `op.Wait("queued")` immediately** — the shape
  `IOperationRegistry.Start` documents for an app whose own queue sits in front of the registry.
  Without it, an item waiting behind a claim is invisible until it starts, which is exactly when a
  user asks whether it is stuck. `OnStarted` then calls `op.Resume()` on the same handle.
- **Start those operations with `Cancellable = false` unless you wire cancellation yourself.** The
  scheduler cancels through the token you passed to `SubmitAsync`, and the registry's `Cancel` signals
  the OPERATION's own token — a different one, which no mission body observes. A cancel button wired
  straight through would flip the status while the work ran on underneath it, so the registry refuses
  to advertise it. To offer a real cancel, keep your own `CancellationTokenSource` per submission and
  expose your own route; the kit deliberately does not guess a link between the two lifetimes.

**Both halves are portable.** In the sample, the scheduler, the observer and the facade that submits
all live in the `net10.0` project that cannot reference Windows — so this composition is one of the
things Stage 4's guard keeps honest.

### What the kit does not ship here

- **No filesystem abstraction and no atomic-replace helper.** If you have an `IFileSystem` plus an
  in-memory implementation, keep it — it is the most valuable thing in that area, because an in-memory
  filesystem that injects latency and transient `IOException`s is how the concurrency invariants become
  provable in YOUR app. The write-to-temp-then-replace *shape* is what the kit models (`Run`/`Commit`);
  the replace itself is your `Commit` body. `PathClaims` is the only filesystem type here.
- **No archive, download or cleanup helpers.** Carve depth caps, leaked-handle retries, an
  extract-never-execute rule: business logic, and it stays yours.
- **No persistent `IMissionStore`, no handler registry by job type, no DAG/workflow engine, no per-item
  cooperative pause.** Each is deliberate, with reasons, in §10 of the design doc.

### Order to adopt

1. Add the scheduler alongside the existing queue and route ONE low-risk operation through it.
2. Move the rest of the raw-filesystem operations; delete the old planner.
3. Move the entity/category locks; delete the operation queue — the lock-order rule disappears here.
4. Lanes for scarce resources; delete the gate singleton.
5. Durability last: implement `IMissionStore` over your existing storage, wire `RecoverAsync`.

Steps 1, 3 and 4 are behaviour-preserving. **Step 2 is where the parallelism change lands**, so that
is the one to verify against real workloads rather than only against tests.

### Known gaps — worth knowing BEFORE you start

1. **Per-item cooperative pause is weaker than a hand-rolled one.** The kit offers lane hold (coarser
   — it suspends a lane, not an item) or cancel-and-resubmit. If you need to pause one specific
   in-flight item, say so: that is the first extension to build, and it should be built on your
   evidence rather than guessed at now.
2. **No handler-registry-by-type.** Deliberate: the `rehydrate` delegate already needs your
   record→body mapping, so the kit would be duplicating your composition.
3. **No persistent store.** Storage is the app's decision, and `Shenora.Core` takes no storage
   dependency.
4. **Content URIs are not paths.** `PathClaims` assumes a hierarchical filesystem with a platform
   separator — right for app-private storage, wrong for an Android MediaStore/SAF content URI, which
   needs its own `IClaimScope`. Nothing else in the scheduler cares.

**Verify:** one run must prove exclusion AND parallelism together — work on the same key never
overlaps *while* disjoint work does. Asserting only that results are correct passes a fully serial
implementation, which is the trap, and capacity alone can produce either half. The kit's own
`Parallel_and_serialized_hold_in_the_SAME_run` submits a contended key and disjoint keys in one mixed
workload and asserts peak concurrency twice — 1 for the contended key, more than 1 overall — and yours
should be shaped the same way. Then lower a lane's capacity mid-run and confirm in-flight work
survives while new work throttles.

---

## What stays yours, permanently

- **Your domain.** Modules, routes, payload schemas, business rules.
- **Every colour, size and pixel.** The kit takes a palette (`CaptionButtonColors`, `TrayMenuColors`,
  splash colours) and ships no design system (D13).
- **Transport-level product decisions** — what an "operation" is, its phases and progress shape,
  whether work queues, what a viewer looks like. The kit ships primitives and lifecycle hooks (D21).
- **Your queue's product decisions** — what runs next and when (`IMissionPolicy`), where durable work
  persists (`IMissionStore`), what a job record contains and which handler runs it. The kit owns the
  SAFETY rules (claim exclusion, lane capacity, no starvation) and hands the rest back.
- **Your state management.** `createShenoraStore` is built on React's `useSyncExternalStore`; if you
  already use a store library, keep it and subscribe through `useShenoraEvent`.
- **Your event/enum vocabulary.** Module and event names are app schema.

## If the kit almost fits

Say so rather than working around it — before 1.0 the surface is still cheap to change, and "the
framework almost fits, but…" is the most valuable feedback this phase can produce. A capability you
need and cannot express is a gap worth fixing; the reverse — the kit growing your product's shape —
is the failure mode the library discipline exists to prevent.
