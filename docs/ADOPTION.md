# Adopting Shenora into an existing desktop app

For an app that already has a WinForms + WebView2 shell and wants to stop maintaining it. It assumes
nothing about this repo's history: everything needed is here or linked.

**There are THREE shells now.** [Stage 5](#stage-5--a-maui-shell-if-stage-4s-logic-should-also-run-on-a-phone)
runs the same portable app logic on MAUI — **Android on a real device and iOS on a simulator, both
proven**, including a save picker with byte-identical output and seekable media playback. (An earlier
version of this line said "iOS not built"; it has been since 2026-08-02.) It is genuinely optional and
genuinely last — it only pays off if Stage 4 happened, because what it reuses is the assembly Stage 4
creates.

**⚠ If your app is SERVER-BACKED, Stage 5 is probably not your mobile story.** The kit has two
consumption profiles: desktop-only (postMessage IPC) and server-backed (an in-process HTTP origin that
also serves LAN clients). An app already serving its own pages over loopback reaches phones with a web
client against that origin, and does not need a MAUI shell at all — so for it Stage 5 is inapplicable
rather than deferred, and **Stage 4 is the last stage that pays**. Two consequences worth knowing before
you start:
- **Stage 2's embedded-bundle serving is mostly moot for you** — your pages are already on a real
  loopback origin, which is also why ranged media "just works" there and is a real design problem on a
  webview-only shell (D44).
- **Stage 3 is a partial fit.** The kit's event pipe (`IEventBus` → batched notifications) is the same
  shape a server-backed app already uses for one-way push, so that half is a straight swap. The
  request/response half is postMessage-shaped; if your requests are HTTP, the seam to adopt is
  `IpcHostBridge` (transport-neutral inbound) rather than `WebViewIpcBridge`.

**The order matters more than the pieces.** Stage 1 carries no IPC dependency, so it deletes the most
duplicated code for the least risk; the IPC substrate comes last because it is the only stage that
touches every module. Keep the app runnable and shipped at the end of every stage — none of this
requires a big-bang branch.

**Two sections below are not stages at all.** [The mission scheduler](#the-mission-scheduler--not-a-stage-adoptable-on-its-own)
and [the file-update queue](#the-file-update-queue--for-when-claims-are-too-coarse) both live in
`Shenora.Core` and need no shell, no IPC and no Windows, so either can be taken first, last, or on
its own by an app that wants nothing else here. They compose but neither requires the other.

**⚠ If your app was one of the SOURCES this was extracted from, read this differently.** You are not
replacing a shell you would rather not maintain — you are taking your own code back with its gaps closed,
so judge it on the DIFF, not on the concept. What the extraction deliberately fixed, none of which any
source had: global unhandled-exception handling, a WebView2 runtime presence check, real policies for
`NewWindowRequested`/`DownloadStarting`/`PermissionRequested`/`ProcessFailed`, options records instead of
magic numbers, JSON-escaped script injection, `ILogger` instead of `Console.WriteLine`, and no static
mutable registration state. Where two sources solved the same problem the kit MERGED them rather than
picking (window state is the clearest case), so you may be adopting the other app's fix for a bug you
still have. And your own post-mortem comments came along — if one is missing from the kit's version, that
is a porting bug worth reporting, not a decision.

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
        Shenora.Ipc ──┘          │
          net10.0                │
              ↑                  │
              ├──── Shenora.Windows ──────┘             net10.0-windows
              ├──── Shenora.Android                     net10.0-android
              └──── Shenora.iOS                         net10.0-ios
```

**One shell package per platform.** Reference the one you are building for. Pin exact versions — see
`docs/RELEASING.md` for the pre-release feed recipe.

**There are no optional feature packages** (D55). Media, file operations and archive extraction ship
inside `Shenora.Core` as the namespaces `Shenora.Media`, `Shenora.IO` and `Shenora.IO.Compression` — so a
shell reference brings all of them and there is nothing extra to add. Stage 4's file-landing section
below needs no new `PackageReference`.

> ⚠ **Pre-release trap that costs an afternoon.** NuGet's global folder (`~/.nuget/packages`) is
> keyed on id+**version** and beats every source, so re-consuming the same pre-release version can
> silently restore a package you cached weeks ago — no warning, no restore error, and `--no-cache`
> does not help (that is HTTP caching). The symptom is a type "not existing" that plainly does exist.
> Diagnose by comparing `obj/project.assets.json`'s recorded dependencies against the `.nuspec`
> inside the nupkg; fix with `dotnet nuget locals global-packages --clear`, or delete
> `~/.nuget/packages/<id>/<version>`. Packing from this repo evicts them for you.

> ⚠ **Your first build will emit `MSB3277`, and the kit's own gate does not show it to you.** Referencing
> `Shenora.Windows` pulls `Microsoft.Web.WebView2`, which ships a WPF flavour alongside the WinForms one,
> and its `Microsoft.Web.WebView2.Wpf.dll` unifies `WindowsBase` to a different version than the
> `net10.0` reference pack — so MSBuild reports a conflict for an assembly a WinForms app never loads.
> It is harmless and the kit demotes it in its own projects:
>
> ```xml
> <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);MSB3277</MSBuildWarningsAsMessages>
> ```
>
> Recorded here 2026-08-03 after a spike compiled a consumer against the PUBLISHED package and met it.
> Worth stating plainly rather than leaving you to judge it: this repo runs at zero warnings, and it gets
> there partly by silencing this one — so "the kit builds clean" was never a claim that your build would.
> ⚠ Note `TreatWarningsAsErrors` does **not** escalate it (that governs the C# compiler, not MSBuild
> tasks); a build that fails on it is one using `MSBuildTreatWarningsAsErrors`.

Build and ship. Nothing has changed yet — this stage only proves the feed.

**Verified 2026-08-03, so you can skip re-proving it:** `Shenora.Core` + `Shenora.Ipc` restore from
nuget.org into a bare `net10.0` project, and `Shenora.Windows` into `net10.0-windows` — from a throwaway
project with no local feed. And a **Stage 1 spike compiled clean against the published package**: an
app's two window-state call sites (`Apply` on load, `Save` on close, persisting width/height/x/y/maximized)
map onto `WindowStateManager` unchanged in shape, `WindowState` is the same five-field record, and the
off-screen guard an app would have kept private is here as a PURE function — `WindowStateManager.IsVisible`,
taking the monitor rectangles and options as arguments, so it is unit-testable in a way a private helper
reading `Screen.AllScreens` was not. `AttachTo(form)` collapses both call sites into one if you want it.

---

## Stage 1 — shell primitives (no IPC dependency, land one at a time)

These live in `Shenora.Windows` and know nothing about IPC, so they can land one at a time. Payoff
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
| OS file drag-drop over page elements | `DropZoneManager` (in `Shenora.Windows`) + **`useDropZone`** | **Not optional sugar — the only workable file-drop path for a desktop shell, and the page's own drop event is what it replaces.** A page-side `onDrop` yields a `File` whose only accessor is its CONTENT, so with the page as UI and the host doing the file work, the bytes must be read into the renderer and pushed across IPC: a full copy of every dropped file, EAGERLY, at drop time, before the app knows whether it wants any of them. Drop 200 files to filter by extension and you pay for all 200; drop a multi-GB asset and you pay that, to reach a file the host could have opened off the same disk. `DropZoneManager` puts transparent native overlays over the page's zone elements, reads the OS drag data directly, and hands you `string[]` paths — open lazily, stream, hash incrementally, move or link without copying — including drags from Explorer or another app while your window is **backgrounded**. Wiring: **Stage-1-adoptable STANDALONE despite living in the WebView2 package** — it depends only on `Shenora.Core` (`IEventBus`), the WebView2 control and a `Form`, and references no `Ipc` type at all. `new` it, hand it your own bus, subscribe to its three events, and forward them over whatever transport you already have — no Stage 3 migration required. (An earlier revision of this table filed the whole thing under Stage 3 because `DropZoneFacade` does need IPC; that is true of the FACADE, not the manager, i.e. not the part that is actually hard — an adopter found this only by reading the source.) Zones clear on **document change**, not the ready handshake, so there is no ordering contract against `notifyReady`. The IPC-wired half — `DropZoneFacade` + `useDropZone` — formally belongs to Stage 3 because it rides the typed bridge, but treat it as the DESTINATION for this row rather than an optional extra: a React page should call `useDropZone` and never register a DOM drop handler for files. |

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
- **Serving LOCAL FILES to the page — `<video>`, `<audio>`, `<img>`, a PDF — is the interceptor, and it is
  the same three lines on all three shells** (D45). Prefer this over a custom scheme for anything that is a
  file on disk: it is portable, and the containment check comes with it.
  ```csharp
  host.Interceptor.UseFiles(new WebViewFileOptions
  {
      AllowedRoots = [libraryDir],                    // EMPTY means nothing is servable — fail-closed
      Resolve = uri => uri.AbsolutePath.EndsWith("/media")   // your route; null = "not mine"
          ? Path.Combine(libraryDir, DecodeYourPayload(uri.Query))
          : null,
  });
  ```
  From the page, build the URL with the shipped helper so the two halves cannot drift:
  `import { mediaUrl } from '@shenora/react'` → `<video src={mediaUrl({ src: name }, 'media')} />`. It
  returns a **relative** URL on the page's own origin, which is the one form intercepted on every shell
  (D44) — do not "improve" it into `app://` or an absolute virtual-host URL, both of which fail on one
  platform each.
  Three things are handled for you and are the reason not to hand-roll it: **path containment** (`..`
  refused before the filesystem is touched, roots compared with a separator appended, every refusal the same
  404 as a missing file so nothing can probe for existence), **ranges** (200/206/416 with `Content-Range`,
  `Accept-Ranges` and a truthful `Content-Length`), and **the platform's range-delivery rule** — Android
  needs the body from offset 0 while WebView2 and iOS need it sliced, and `UseFiles` reads that off the
  interceptor so you cannot pass it in wrong. ⚠ **Keep your route off a path your bundle contains**; a
  collision resolves differently on desktop and mobile. `ShellCapability.LocalFiles` on the ready handshake
  tells the page whether the shell it is on can serve at all. A worked, self-checking example is
  `InterceptorProbe` in the desktop sample.
  - ⚠ **`request.Uri` CAN CARRY A `#fragment`, and the safe reading of it hides that.** A top-level
    navigation to `https://host/#/library` reaches your middleware as exactly that URL, with
    `Fragment = "#/library"` and `AbsolutePath = "/"`. So resolve against `AbsolutePath` (as the snippet
    above does) and never against `ToString()` or `PathAndQuery`, which mis-resolve. The trap is the other
    half: because `AbsolutePath` reads `/`, logging it is what convinced the first adopter their URLs were
    fragment-free and cost them an afternoon attributing a platform bug to this kit. **Log the whole `Uri`
    when a document request surprises you.**
- **If your page uses a HASH ROUTER, reloading it on Android was broken by the platform and the kit now
  repairs it — you write nothing.** MAUI's request→asset mapping strips a query string and not a fragment,
  so a reload at `https://host/#/library` looked for an asset named `#/library`, 404'd, and Chromium showed
  its `net::ERR_INVALID_RESPONSE` error page instead of your app. `MobileWebViewInterceptor` answers that
  request with `HybridRoot/DefaultFile` — the same bytes the platform serves for the fragment-free URL — so
  the page boots normally and your router reads the fragment off `location` as usual. It runs only after
  your own middleware decline, and it declines rather than 404s if the bundle is not there, so an app that
  serves its own document is untouched.
  - ⚠ **iOS is NOT repaired, and its version of this failure is SILENT.** The reload never produces a
    second document and WKWebView keeps the previous page on screen, so a screenshot shows a healthy app.
    Applying the Android repair there was measured to make it worse, so nothing speculative ships. If you
    depend on reload-at-a-hash-route on iOS, test it with a native-side witness (a pre-reload marker and a
    node count via `EvaluateJavaScriptAsync`) — "it rendered" is not evidence.
- **Serving something DYNAMIC and seekable that is not a file** — generated bytes, a fetch-and-cache proxy —
  is still a `DeferredSchemes` handler rather than a folder mapping: `SetVirtualHostNameToFolderMapping`
  cannot honour
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
- **Serving your own frontend into an OFF-SCREEN session** (co-browsing it, rendering it headlessly,
  capturing it) needs the same pair handed to the session, because a session browser builds its own
  environment and inherits none of the host's serving:
  ```csharp
  Browser = new SessionBrowserOptions
  {
      ProfileDirectory = …,
      VirtualHost = hostOptions.VirtualHost,           // the host's own two values,
      ResourceProvider = hostOptions.ResourceProvider, // the SAME provider instance
  }
  ```
  Set both or neither — either alone is refused at initialization, because on its own it serves nothing
  and looks exactly like the bug it would be. **A `DeferredSchemes` handler is NOT available inside a
  session** (D38): those need environment-level scheme registration, which sessions do not expose, so a
  page whose subresources come from `app://` will 404 them off-screen. **You only need any of this if
  you serve an embedded bundle** — a server-backed app's pages are on a real loopback origin, which a
  session can already reach.
  ⚠ **Only on a session that renders YOUR pages.** Bundle responses carry
  `Access-Control-Allow-Origin: *` — nearly moot in the shell, where the bundle is the document's own
  origin, and not moot here, where the page can be any origin: script in a third-party page you are
  co-browsing could `fetch` your whole bundle. Your shipped frontend is not a secret, so this is an
  unintended read channel rather than a breach, and the fix costs nothing because these options are
  per-session — give a third-party co-browse session its own `SessionBrowserOptions` without them, the
  way it already gets its own `ProfileDirectory`.
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
  the user decides? `Start()` it and immediately `op.Wait("interrupted")`. See `docs/DECISIONS.md` D23
  for the rationale.
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
   `Shenora.Core` and, if it holds facades, `Shenora.Ipc`. **Do not reference `Shenora.Windows` or
   `Shenora.Windows`**; adding either defeats the guard entirely, which is the one way this goes
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
(`WindowStateManager`, `IWindowStateStore`) stays in `Shenora.Windows`. Its signatures happen to look
platform-neutral, and that is not the bar — window geometry is a desktop concept, and the bar is "app
logic must be able to compile off Windows". Same for `OptimizedForm`, `TrayIcon`, `SplashPanel`,
`SecondaryWindows` and `SingleInstanceGuard`.

**If a contract does not fit**, say so — that is the feedback D20 wants. The portable set was derived
from what the surveyed apps actually needed, so a capability you cannot express through it is a real
finding, not a misuse.

---

## Stage 5 — a MAUI shell, if Stage 4's logic should also run on a phone

**This is Stage 4's payoff, and it only works if Stage 4 happened.** The MAUI shell hosts the same
portable assembly the desktop shell does; if your facades still reference `Shenora.Windows` there is
nothing to reuse. `samples/Shenora.Sample.Maui` is the worked example, and it references the very
same `Shenora.Sample.Logic` the desktop sample does — that shared reference is the whole demonstration.

**Status, stated plainly:** Android is built and was run on a device (request/response, batched
notifications, the error boundary, the native file picker, the mission scheduler). **iOS is not
built** — it needs the `ios` workload and a Mac build host.

### Setup

```csharp
// MauiProgram.CreateMauiApp, AFTER builder.Build()
var shenora = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
{
    ApplicationName = "YourApp",
    // Android's private data directory IS the app root; --app-root is desktop packaging vocabulary.
    Paths = new ShenoraPathsOptions { ExplicitRoot = FileSystem.AppDataDirectory },
});
shenora.UseMobile(Dispatcher.GetForCurrentThread()!, ex => Log(ex.ToString()));
shenora.Services.AddModuleFacade<YourPortableFacade>();
shenora.Services.AddMessageDispatcher();
var app = shenora.Build();
```

Then, on the page that owns the `HybridWebView`:

```csharp
var bridge = new MobileIpcBridge(webView, new MobileIpcBridgeOptions
{
    Dispatcher = app.Services.GetRequiredService<IMessageDispatcher>(),
    EventBus = app.Services.GetRequiredService<IEventBus>(),
});
bridge.Attach();          // construct early (buffering starts), attach before the page loads
```

**`UseMobile` registers no `IShenoraRunner`, deliberately.** MAUI owns the loop, so
`ShenoraApplication.Run` — contractually "blocks until shutdown" — has no honest implementation.
Drive the pair from the platform instead: `Start()` from `Window.Created`, `Stop()` from
`Window.Destroying`. Both are idempotent, so wiring them somewhere that fires more than once
(an activity's `OnCreate`/`OnResume`) is safe.

**The client needs no MAUI-specific code.** `@shenora/react`'s `ShenoraBridge` detects the host, so
`invoke`/`post` work unchanged from the desktop shell. ⚠ **The page MUST load MAUI's bridge script**
(`<script src="_framework/hybridwebview.js"></script>` on .NET 10). Without it `window.HybridWebView`
does not exist, and the failure is silent in the worst way: the page renders, the send throws a
`TypeError` nobody sees, and the host waits forever for a handshake.

### What transfers, and what does not

| | |
|---|---|
| **Transfers unchanged** | The whole IPC substrate — envelopes, `MessageDispatcher`, `BaseFacade`, `IModuleContext`, the operation registry, `IEventBus`, batched notifications. Every `Shenora.Core` contract. The mission scheduler and the file-update queue. |
| **Different implementation, same contract** | `IClipboardService`, `IUrlLauncher`, `IFileDialogs`, `IUiDispatcher` — MAUI Essentials behind the same interfaces. |
| **Transfers, INCLUDING seekable media — this row has been wrong twice** | **Resource serving.** `HybridWebView` has a request-interception seam in .NET 10 (`WebResourceRequested`, `e.Uri`, `e.Headers`, `e.Handled`), and the simple case needs none of it — put the built frontend in `Resources/Raw/wwwroot` and the platform serves it. What the seam buys is DYNAMIC content: a generated image, an exported file, **and seekable media**. ⚠ This row previously said seeking was impossible without `e.PlatformArgs`. **That is false** (corrected 2026-08-03 by device runs — **D44**): `SetResponse` has a SECOND overload taking a header DICTIONARY, on both mobile TFMs, and every header reaches the native response. Neither platform needed `PlatformArgs`. **But the two shells need OPPOSITE BODIES** for the same request — Android applies the `Range` start itself so you must NOT slice; iOS passes the body through so you MUST. **You do not write that yourself any more** (D45): `MobileWebViewInterceptor` implements the same `IWebViewInterceptor` the desktop does, so `interceptor.UseFiles(…)` and the page's `mediaUrl(…)` are literally the same code on all three shells and the delivery rule is read off the platform. Read D44 only if you are writing a middleware that answers ranges by hand; getting it wrong plays every faststart file perfectly and fails every other one. |
| **Absent, not different** | Native drop zones, tray, secondary windows, window state, frameless chrome. These are desktop CONCEPTS. You will not find them registered, and the mobile packages do not reference the packages that hold them — so portable logic cannot accidentally depend on one. |
| **The OS media transport** | `IPlaybackSession` — the lock screen, the media flyout, headphone and car-stereo buttons. One contract, three implementations, verified against each OS's own registry. `Publish` / `Report` / `Clear` go app → OS and `CommandReceived` comes back. ⚠ Two things to know. `Report` is for JUMPS, not a timer: all three platforms extrapolate the displayed time from a position plus a rate, so pushing it every 250 ms spends battery telling the OS what it already knows — and a *delayed* report lands as a jump backwards, because the platform treats it as current. And a session makes you CONTROLLABLE, not VISIBLE: Android needs a MediaStyle notification, iOS an active `AVAudioSession`, and both mean picking icons, channels, categories and interruption behaviour — your decisions, not the kit's. |
| **Live Activities / Dynamic Island (iOS)** | `ILiveActivities` — see the recipe below. Android registers an implementation that answers `Unavailable` with a reason rather than throwing, so portable logic asks and branches. |

**Where a contract is only partly honourable, it refuses LOUDLY** rather than doing nothing:
clipboard IMAGES (Essentials is text-only) and the folder picker throw
`ShellCapability.NotSupported` naming the platform and the alternative. `IUiInteraction`'s
block/unblock is the opposite case — a documented no-op, because mobile pickers are already modal, so
the capability is satisfied BY the platform rather than absent.

### ⚠ A server-backed app on a MAUI shell: the page's ORIGIN is not what you expect, and it costs a day

Filed by the first adopter (2026-08-04) after losing a day to it. Not a kit defect and it needs no kit API —
but nothing said it, and **both failures present as the same useless symptom: a bare
`TypeError: Failed to fetch`**, with the engine logging the real reason only as a `[warning security]` line
you cannot see without attaching devtools.

`HybridWebView` serves your bundle from a synthetic virtual host, so the page is on a **secure origin you did
not choose**:

| Shell | The page's origin |
|---|---|
| Android | `https://0.0.0.1` |
| iOS | `app://0.0.0.1` |

Both measured on real runs. **The iOS one especially is worth having from us** — the adopter could not measure
it at all (`ios-webkit-debug-proxy` would not install on their Mac), and it is not otherwise discoverable.

Two consequences, and they bite in sequence:

1. **Mixed content.** Every request from that secure origin to a plain-`http` backend is blocked outright.
   On Android the app can allow it, and that is where the decision belongs — it is a real security
   relaxation and the kit will not make it silently on your behalf:

   ```csharp
   Microsoft.Maui.Handlers.HybridWebViewHandler.Mapper.AppendToMapping("MixedContent", (handler, view) =>
   {
   #if ANDROID
       handler.PlatformView.Settings.MixedContentMode =
           Android.Webkit.MixedContentHandling.AlwaysAllow;
   #endif
   });
   ```

   Prefer `https` on the backend if you can; this is the escape hatch, not the recommendation.

2. **CORS, which only appears after you fix (1).** The request now leaves the device and the *response* is
   withheld instead, because your backend has never heard of that origin. **Allowlist the origins above**
   server-side. ⚠ A non-standard scheme may present as `Origin: null` rather than the literal string, so
   allowlist by what your server actually logs rather than by what this table says — check the header once
   and trust that.

**The related tooling gap, if you hit it:** WebKit does not forward the page's `console.*` to the device log,
so a page-side error can be genuinely invisible. Both this repo and the adopter independently ended up
routing page → host over IPC and logging host-side (`PageDiagFacade` in `samples/Shenora.Sample.Maui`). It is
a few lines; copy the pattern.

### The Dynamic Island for a PLAYER — what the kit gives you, and the four things you still write

**Use `IPlaybackSession`, not a Live Activity.** Two different iOS mechanisms reach the Island and they are
**mutually exclusive** — an app publishing a Now Playing session takes the Island, and a Live Activity
started beside it has nowhere to render. For playback, Now Playing is also the one Apple intends: it is
Apple's own presentation, it reaches CarPlay, the Watch, AirPods and car head units as well as the Island,
and a custom card duplicating it is the sort of duplication App Review pushes back on. Verified end to end
on an iPhone 17 Pro (2026-08-07).

**What the kit does:** `IPlaybackSession` on all three shells — `Publish`/`Report`/`Clear` out,
`CommandReceived` back, one contract, no platform code in your app logic.

**What you still write, and all four are small:**

1. **Artwork.** 🔴 *This is the one that decides whether the Island shows anything at all.* Set
   `PlaybackInfo.Artwork` (PNG/JPEG bytes). With a title and duration but no image, iOS knows something is
   playing, falls back to your app icon, and the Island is a wide bar with nothing in it — which reads
   exactly like "the feature is broken". It is the field most likely to be skipped, because it is the only
   one that is not a string.
2. **`UIBackgroundModes: [audio]`** in your `Platforms/iOS/Info.plist`. The kit cannot add it — no MSBuild
   item merges a key into your manifest. ⚠ Editing it does not reach an INCREMENTAL build either;
   `_WriteAppManifest` merges a stale `obj/**/AppManifest.plist`, so delete that or clean.
3. **An active `AVAudioSession`**, once at startup:
   ```csharp
   var session = AVFoundation.AVAudioSession.SharedInstance();
   session.SetCategory(AVFoundation.AVAudioSessionCategory.Playback,
                       AVFoundation.AVAudioSessionMode.Default, default);
   session.SetActive(true);
   ```
   The kit stays out of this deliberately: the category, whether you mix with other audio, and what happens
   on an interruption are product decisions. ⚠ **2 and 3 are a PAIR and neither does anything alone** —
   without the key iOS suspends your process; without the session it does not believe you are playing. The
   symptom of missing either is identical: plays in the foreground, silent after a swipe.
4. **A video→audio handoff, if you play VIDEO.** iOS pauses a `<video>` when the app backgrounds (the video
   track cannot render); an `<audio>` already playing continues. So on `visibilitychange` to hidden, copy
   the playhead onto an `<audio>` with the same source, start it, and pause the video — reversing it on the
   way back. ⚠ iOS restricts *starting* new playback once backgrounded, so if the handoff loses that race,
   keep the `<audio>` running muted alongside and just unmute it. `samples/Shenora.Sample.Maui`'s
   `wwwroot/index.html` has both the handoff and a standalone `♪` button, and the button matters as a
   DIAGNOSTIC: with only a `<video>`, a correctly-configured shell and a broken one look identical.

### Live Activities / the Dynamic Island — 🔴 BROKEN ON DEVICE, do not adopt yet

> ⚠ **The widget does not render on real hardware** (2026-08-07). ActivityKit starts the activity and
> returns an id, updates are accepted, the system reserves Island space — and the widget process never runs:
> the `.appex` the kit builds with `swiftc` starts at `main`, `WidgetBundle.main()` returns, and the process
> exits before serving. An app extension must be linked with `-e _NSExtensionMain` to enter the XPC run
> loop, which a bare `swiftc` does not do. It was only ever exercised on a simulator, which loads the bundle
> regardless. `TASKS.md` carries the decision. **`IPlaybackSession` is unaffected — use the section above.**

The OS requires the UI to be a SwiftUI view in a widget extension, so **you cannot avoid writing Swift** —
but you write only the views, which are your design system anyway and the one thing the kit does not ship
(D13). Everything else is the package's: the state contract, the ActivityKit shim, the extension's plist, the
build, and the codesigning.

**1.** Write the views. One file, four bodies — the lock-screen banner plus the Island's compact leading,
compact trailing, minimal and expanded regions. `ShenoraActivityAttributes` and its
`ShenoraActivityState` (`title` / `subtitle` / `progress`) come from the kit, compiled into the same module.
Copy `samples/Shenora.Sample.Maui/Platforms/iOS/IslandViews.swift` and restyle it.

**2.** Point one MSBuild property at it:

```xml
<PropertyGroup Condition="$(TargetFramework.Contains('ios'))">
  <ShenoraLiveActivityViews>Platforms/iOS/IslandViews.swift</ShenoraLiveActivityViews>
</PropertyGroup>
```

**3.** Declare `NSSupportsLiveActivities` in your app's `Platforms/iOS/Info.plist`. The kit cannot add this
for you — no MSBuild item merges a key into that file — and without it `Activity.request` fails for a reason
that is not obvious.

**4.** Use it from portable C#:

```csharp
var state = new LiveActivityState { Title = "Converting", Subtitle = "starting" };
var handle = activities.Start(state);              // null if it could not start
if (handle is not null)
    activities.Update(handle, state = state with { Progress = 0.6 });
activities.End(handle!);
```

**Ask `Unavailable` FIRST.** It returns null when activities can be started and otherwise a reason — the OS
being too old, the user having switched them off, or the shim not being linked. Android returns a reason
always, so portable logic branches instead of catching.

⚠ **Traps, all measured:**
- **A `null` `Progress` means INDETERMINATE**, not 0. Render a spinner; an empty bar claims "0% done".
- **Never change `LiveActivityState` without changing the Swift mirror in the same commit.** Drift fails
  SILENTLY — a renamed field decodes to nil and the activity just does not appear. A tripwire
  (`LiveActivityMirrorTests`) guards the kit's copy; if you widen the record in a fork, keep both sides.
- **An empty Dynamic Island on the SIMULATOR is expected.** An activity there reports only a lock-screen
  scene target, so the pill stays blank however long you wait. Use a device to see the Island itself.
  `node devtools/dev.mjs mac activity` shows what the OS actually registered, started and launched.
- **An active activity with the widget never launched** is the signature of a module-name mismatch between
  the shim and the extension — every call reports success and nothing renders. The kit sets
  `-module-name` on both sides for exactly this reason; do not override `ShenoraLiveActivityModule` on one.

**SAVING is universal, but only through `SaveAsync(options, write)`** — implemented natively on both
mobile shells since 2026-08-03 (`ACTION_CREATE_DOCUMENT`, `UIDocumentPickerViewController`). Call that,
not `SaveFileAsync`, which still refuses here because "give me a PATH to save to" has no mobile
expression: the user grants access to one document, the app writes into it, and there is nothing to hand
back. Three consequences an adopter should design around:

- **`FileDialogResult.FilePath` is null on SUCCESS.** Check `Success`, never the path — a page that
  treats the missing path as failure will report every mobile save as failed.
- **The write callback may run even if the user cancels.** Android asks first; iOS must produce the
  content first, because its export picker hands over a file that already exists. Do not put anything
  irreversible in the callback.
- **You get atomicity for free on every shell.** Both mobile implementations produce into a cache temp
  and only then hand it over, so an interrupted save leaves the user's previous document untouched — the
  same guarantee the desktop gets from `Files.BeginReplace`. That is the whole reason the shape is a
  callback rather than a path.

### One web bundle, every shell — advertise capabilities, don't sniff the platform

The table above is the host's view. The page needs the same answer, and **it cannot work it out for
itself**: what a shell offers depends on what the APP composed, not on the operating system — a
desktop host that never registers `TrayIcon` has no tray either. So the host states it, in the ready
handshake it already answers:

```csharp
// wherever you build the bridge options — WebView2 or MAUI, the option has the same name
Shell = new ShellInfo
{
    Name = "winforms",                              // diagnostics only; never branch on it
    Capabilities = [ShellCapability.WindowChrome, ShellCapability.DropZones,
                    ShellCapability.FilePicker, ShellCapability.Tray],
},
```

```tsx
const shell = await bridge.notifyReady();           // also cached on bridge.shell afterwards
return <>
  {shell?.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar />}
  {shell?.capabilities.includes(ShellCapabilities.dropZones) ? <DropTarget /> : <PickFileButton />}
</>;
```

Both sides use the same names (`ShellCapability` in C#, `ShellCapabilities` in TS), pinned to each
other by a test — and an app may advertise its own strings beyond them.

**Treat absent as "assume nothing", never as "assume desktop".** `Shell` is optional, so a plain
browser tab during frontend dev, and any host that does not set it, both arrive as `undefined` — and
both are correctly capability-less. Branching the other way makes the *browser* the one place your
title bar renders wrongly.

Advertise what you actually composed. A capability you claim but did not register turns a rendered
button into a `NotSupported` throw at the moment a user presses it.

Both samples do this for real and disagree honestly: `Shenora.Sample.Desktop` answers `winforms` with
all seven, `Shenora.Sample.Maui` answers `maui` with `[filePicker]`, and `Shenora.Sample.Web`'s
`App.tsx` reads the reply without knowing which one it is talking to.

**`FileDialogOptions` survived contact with mobile, which was an open question until now.**
`OpenFileAsync` needs no change: `FileDialogResult.FilePath` is specified as "a path or URI the HOST
can resolve", and Android's content URI is exactly that. The desktop-only options
(`CheckFileExists`, `OverwritePrompt`, `DefaultPath`, `RememberPathKey`, …) are ignored, and which
ones is listed on the implementation.

### iOS

Everything above applies unchanged — that is the finding, not a hedge. The shell compiles for
`net10.0-ios` with **no platform directive anywhere in the package**, the iOS head is three template
files (`AppDelegate`, `Program`, `Info.plist`), and the same page got the same `ShellInfo` back.
Build it with `node devtools/dev.mjs mac` (see `devtools/README.md`); iOS needs a Mac, so the TFM is
conditioned on the build host and a Windows `pack` is android-only.

Two things that only showed up here, and both are about your PAGE rather than the kit:

- **Write the page for the SUPERSET of shells.** Markup that looked right on an Android emulator for
  a whole session put its heading under the status bar and the Dynamic Island on the first iPhone
  run. Use `env(safe-area-inset-*)` with `viewport-fit=cover`; both collapse to nothing where there
  are no insets.
  - 🔴 **…and on Android that is NOT enough, so the kit ships the missing half.** Measured on Android 16:
    `env()` reports the display CUTOUT only — **never the system bars** (`bottom` came back 0 on a device
    whose navigation bar is genuinely 24 CSS px) — and reports **0 for the whole first page load**. No
    page-side code can work around either; a re-read on `resize`/`visualViewport` was written and does
    nothing, because nothing changes within that document to observe.
  - **`MobileSafeArea` publishes the platform's real insets as CSS variables**, at first paint, from the
    host. Opt-in, and every part of it is individually declinable:

    ```csharp
    _safeArea = new MobileSafeArea(webView, new SafeAreaOptions
    {
        Default = new SafeAreaInsets(24, 0, 24, 0), // published BEFORE the platform reports, so the
                                                    // first screen is right instead of laid out at 0
        Color   = "#14161a",                        // painted behind the inset strips
        Settle  = TimeSpan.FromMilliseconds(180),   // the correction eases instead of snapping
        Splash  = true,                             // covers the page until the real numbers land
    }, log);
    ```

    Your page then reads `var(--sa-top)` / `--sa-right` / `--sa-bottom` / `--sa-left` (rename the prefix
    with `VariablePrefix`), keeping `env()` as the fallback for anything that opens it outside the shell:

    ```css
    body { padding: max(12px, var(--sa-top, env(safe-area-inset-top))) /* …and the other three */ }
    ```
  - ⚠ **Two page-side rules the variables do not fix, both measured:** inset padding on a **scrolling**
    `<body>` scrolls away, so make body a non-scrolling flex column and scroll a child; and use
    `max(12px, inset)` rather than `calc(12px + inset)`, which stacks two paddings and reserved 61 CSS px
    where the platform asked for 49. `samples/Shenora.Sample.Maui/.../index.html` does both.
- **Strings leak the shell you developed on.** A shared bundle means "hello from android" eventually
  appears in an iPhone screenshot.

### Traps this repo already paid for

- **`Application.Current` is null inside `CreateMauiApp`** — `builder.Build()` makes the MauiApp, not
  the Application. Use `Dispatcher.GetForCurrentThread()`.
- **The envelope's `timestamp` is a `DateTimeOffset`.** A JS client sending `Date.now()` has its
  request dropped at the boundary — correctly logged host-side and correctly invisible to the page.
  Send `new Date().toISOString()`; `@shenora/react` already does.
- **Match the ABI when deploying to an emulator.** Most are x86_64 while a default build may produce
  arm64 only, and the install fails `INSTALL_FAILED_NO_MATCHING_ABIS`, which reads like a packaging
  fault rather than the wrong architecture.
- **The ready gate can be opened but never closed on this shell.** `HybridWebView` exposes no
  document-lifecycle event, so a page reload simply re-handshakes (`Open` is idempotent). Bounded,
  and worth knowing before you rely on buffering semantics the WebView2 bridge has.

---

## The mission scheduler — not a stage; adoptable on its own

`Shenora.Core` ships ONE scheduler for the two things the family built five separate times: a
**filesystem operation planner** (serialize work that touches overlapping paths, run disjoint work in
parallel) and a **job queue** (bounded concurrency, retry, cancel, durability). They are the same
engine with different key types — paths conflict when one CONTAINS the other, lanes admit N holders at
once — and putting only that difference behind a seam is what makes adoption a DELETION rather than a
translation. Evidence, rationale and the deliberately-not-built list: `docs/DECISIONS.md` D27–D31 + D57.

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
    // ⚠ A CEILING over every lane, not just their starting value — see "Known gaps" #5 before choosing
    // it. Omit it (or null) for auto = clamp(cores-1, 1, 4), the value both hand-rolled planners chose;
    // anything below 1 throws.
    GlobalLaneCapacity = null,
    Scopes = [PathClaims.Scope, new FlatClaimScope("entity"), new FlatClaimScope("category")],
    Log = message => logger.LogDebug("{Message}", message),
});
scheduler.Lane("gpu").Capacity = 1;   // a scarce shared resource — see "Known gaps" #5 (the lane trap)
```

Register only the scopes you use. A claim naming an **unregistered scope throws at submit** rather
than being ignored — silently dropping an exclusion the caller asked for is the one failure mode a
scheduler must not have. Pass an explicit `GlobalLaneCapacity` in your own tests: a concurrency
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
| A `maxConcurrency` constructor argument | `MissionSchedulerOptions.GlobalLaneCapacity` | Every request draws one permit from the global lane. ⚠ It is also a CEILING over every named lane — see "Known gaps" #5. (Renamed from `DefaultLaneCapacity`; rename the assignment, nothing else changes.) |
| A static gate/semaphore singleton over a scarce resource (one GPU, a rate-limited endpoint) | `scheduler.Lane("gpu").Capacity = 1` + `Lanes = [new MissionLane("gpu")]` on the request | Removes the singleton, so it is testable and there can be more than one. A lane that is a BUDGET rather than a slot count takes weighted permits: `new MissionLane("vram", 4)`. |
| A live "max active" slider | the `ILane.Capacity` setter | Lowering it never cancels running work — the surplus is swallowed as items finish. Proven in both directions: `Lowering_lane_capacity_throttles_new_work_without_killing_running_work` and `A_lowered_capacity_is_enforced_once_the_surplus_drains`. The setter enforces a floor of 1 and no ceiling, so clamp to your own maximum before assigning. |
| A hand-written IPC route that opens a file/folder/save dialog for the page | `services.AddShenoraFileDialogs()` + `useFileDialogs()` from `@shenora/react` | The kit ships the routes and the typed client. `canPickFile`/`canPickFolder`/`canPickSavePath` come from the ready handshake, so ONE bundle hides the controls a shell cannot honour rather than calling and catching. Keep your own route only when you have logic AROUND the dialog (a slow interruptible write, app validation) — not for a plain picker. |
| A capacity governor that suspends work under system load | `ILane.Hold()` / `Release()` (re-entrant), and `IMissionScheduler.GlobalLane` to move the total bound | The kit ships the mechanism and no policy: load probes, hysteresis and debounce stay yours. This is the difference between "yield the GPU while the user games" and "kill the user's transcode". ⚠ A governor that RESTORES as well as throttles must raise `GlobalLane.Capacity` too — see "Known gaps" #5. |
| Dedup of an identical pending operation; a batch merge of work accumulated during a slow plan | `MissionDefinition.Key` (+ `IsActive(key)` so you can skip building an expensive request you know would only be deduplicated) | A matching submission completes eagerly against the existing item with `MissionOutcome.Deduplicated`, and the body runs once. |
| `MAX_RETRY_ATTEMPTS` / `RETRY_DELAY_MS` constants | `RetryPolicy` | Same defaults as the family's measured value: 3 attempts, 500 ms × attempt, `IOException` only. `RetryPolicy.None` opts out; `Retry = null` already means none. |
| A retry loop wrapped around an expensive operation to survive a cheap final step | `Run` (the expensive phase, runs ONCE) + `Commit` (cheap, retried) | Setting `Commit` is what makes `Run` exempt from the retry budget. |
| A `Channel` + worker pool + gate, or a plan-swap with a signal and a worker task | the scheduler | Dispatch is event-driven — on submit and on each completion. No worker thread, no polling latency. |
| Priority or "not now" rules baked into the queue's own loop | `IMissionPolicy` (`Compare` = what, `ShouldStart` = when); default `PriorityMissionPolicy` is priority-then-FIFO | Ordering is a PRODUCT decision, so it is yours. A policy is only consulted about items that already passed admission, so the worst a buggy one can do is DELAY work — it cannot make conflicting work overlap or bypass a lane. A throwing policy is treated as "not now" rather than wedging the scheduler. |
| `GetPendingOperationCount()`, a queue/diagnostics view | `PendingCount`, `RunningCount`, `Snapshot()` | `Snapshot()` is a copy: safe to hold, stale the moment it returns. |
| Durable jobs in SQLite (or JSON) + resume on startup | `IMissionQueueStore` over your EXISTING repository, `Durable = true` per request, then `RecoverAsync(rehydrate)` at a moment you choose | The kit ships no store implementation, by design — see below. `Kind` and `Payload` are yours, never interpreted. |
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
- **No persistent `IMissionQueueStore`, no handler registry by job type, no DAG/workflow engine, no per-item
  cooperative pause.** Each is deliberate, with reasons, in §10 of the design doc.

### Order to adopt

1. Add the scheduler alongside the existing queue and route ONE low-risk operation through it.
2. Move the rest of the raw-filesystem operations; delete the old planner.
3. Move the entity/category locks; delete the operation queue — the lock-order rule disappears here.
4. Lanes for scarce resources; delete the gate singleton.
5. Durability last: implement `IMissionQueueStore` over your existing storage, wire `RecoverAsync`.

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
5. **⚠ `GlobalLaneCapacity` is a CEILING over every lane, not just their starting value** — the lane
   trap, and it cost the first adopter a measurement (2026-08-05). Every mission also draws one permit
   from the global lane, so a named lane runs at `min(its own capacity, the global bound)`. Set
   `GlobalLaneCapacity` to the widest any lane will ever need and narrow from there — a global bound of
   1 with `Lane("gpu").Capacity = 3` gives you a lane that runs at **1**.
   - **Read `ILane.EffectiveCapacity`, not `Capacity`,** to find out what a lane will actually reach.
     `Capacity` deliberately reports what you REQUESTED (so a later widening of the bound gives you the
     width you asked for rather than having discarded it), and setting it above the bound logs why it
     will not apply.
   - **A runtime governor must move the bound too.** `IMissionScheduler.GlobalLane` is that lane, and it
     is live-resizable like any other: `scheduler.GlobalLane.Capacity = 8`. Before it existed, a
     governor could throttle a lane and never restore it past the value chosen at startup. Its
     `Hold()`/`Release()` also give you "pause the whole scheduler without cancelling anything".

**Verify:** one run must prove exclusion AND parallelism together — work on the same key never
overlaps *while* disjoint work does. Asserting only that results are correct passes a fully serial
implementation, which is the trap, and capacity alone can produce either half. The kit's own
`Parallel_and_serialized_hold_in_the_SAME_run` submits a contended key and disjoint keys in one mixed
workload and asserts peak concurrency twice — 1 for the contended key, more than 1 overall — and yours
should be shaped the same way. Then lower a lane's capacity mid-run and confirm in-flight work
survives while new work throttles.

### Multi-step missions, when a later step needs what an earlier one produced

Claims stop two missions overlapping. They say nothing about ORDER, dependency, or data flow — so
"stage it, then commit it, then index it" has nowhere to live except a stack frame:

```csharp
var a = await scheduler.SubmitAsync(stage);            // hold the await, keep state in a local
if (a.Succeeded) await scheduler.SubmitAsync(Commit(a));
```

That works, and loses three things: the chain is invisible, it dies with the awaiting code, and it
cannot be resumed. `MissionChain.Sequence` gives you the same sequence as one mission:

```csharp
var chain = MissionChain.Sequence("IMPORT",
    new MissionStep("stage",  (m, ctx, ct) => { ctx.Set("temp", tempPath); return Stage(ct); },
                    Claims: [PathClaims.Exclusive(source)]),
    new MissionStep("commit", (m, ctx, ct) => Commit(ctx.Get<string>("temp")!, ct),
                    Claims: [PathClaims.Exclusive(target)],
                    Retry:  new RetryPolicy()));        // retries THIS step, never the ones before

await scheduler.SubmitAsync(chain);                     // an ordinary mission, as far as the scheduler knows
```

What to know before you reach for it:

- **A chain is ONE queue entry**, so it holds the UNION of its steps' claims for its whole life —
  taking the stronger mode where steps disagree, so a read-then-write chain holds that key
  exclusively throughout. For a long chain over many paths that is a real throughput cost, and it is
  the trade for the scheduler having no dependency graph. If it bites, that is the evidence for
  per-step claims and it wants its own design.
- **`IMissionChainContext` is in-memory only.** It exists to pass a temp path from step 1 to step 2
  inside one run. A DURABLE chain that resumes after a restart carries its state in `Payload`, like
  any other durable mission — the kit cannot serialize your object graph, and a resume that silently
  lost the context would be worse than one that never had it.
- A failing step fails the chain and later steps do not run. Cancelling cancels the chain. There is no
  chain-level retry: re-running completed steps is a judgement only you can make, and you make it by
  submitting again.

---

## The file-update queue — for when claims are too coarse

This is the **`Shenora.IO`** namespace, inside `Shenora.Core` — no extra package to reference. Still no
shell/IPC/Windows dependency, and **independent of the scheduler**: usable with it, without it, or before
you adopt it.

```csharp
using Shenora.IO;   // FileUpdate*, FileChange, IPathLocker, UpdateManifest, UpdateStage
```


**The problem it solves.** A path claim excludes two missions for their WHOLE duration. But the
expensive phase usually does not touch the destination at all — it writes a temp file. Only the final
mutation needs exclusivity:

```
mission A   [ compress 8s ..................... ][ replace 3ms ]
mission B   [ compress 7s ..................... ]      ↑ waits  [ replace 3ms ]
```

Under path claims, B's compress waits for A's replace: ~15s. Compute in parallel and serialize only
the landing: ~8s. So the queue is the destination for anything that currently claims a path just to
protect a rename.

```csharp
Run    = (mission, ct) => archive.CompressToTempAsync(source, temp, ct),   // parallel
Commit = (mission, ct) => updates.ApplyAsync(new FileUpdate
{
    Changes    = [new FileChange.Replace(temp, target)],
    Atomicity  = FileAtomicity.PerChange,
    Retry      = new RetryPolicy(),
}, ct),                                                                     // serialized
```

| You probably hand-rolled | Use | Notes |
|---|---|---|
| A "write temp, then `File.Replace` with retry" helper, copied per feature | `FileChange.Replace` inside a `FileUpdate` | The retry is `RetryPolicy`, the same type the scheduler uses, applied per change. |
| A lock or flag so two features never write the same tree at once | the queue itself — one writer per `Partition` | `Partition = null` is one global writer, the setting that cannot surprise you. Partition only by trees that genuinely never touch. |
| "Apply these five files together or not at all", hand-rolled with a backup folder | `FileAtomicity.AllOrNothing` | Undoes applied changes in reverse. A delete becomes STAGED — moved aside, really removed only once everything lands — because a delete cannot be undone from nothing. |
| Reporting which file broke a batch | `FileUpdateResult.FailedIndex` + `Applied` | The result reports rather than throws, like `MissionResult`; `ThrowIfFailed()` if you prefer exceptions. |

**Surviving a power cut is opt-in, and it is one line.** Without a journal, `AllOrNothing` rollback is
in-process: it covers a change that fails, not a process that dies. With one, the undo plan is on disk
before each change and recovery finishes the job at startup:

```csharp
var queue = new FileUpdateQueue(new FileUpdateQueueOptions
{
    Journal = new FileUpdateJournal(new FileUpdateJournalOptions { Directory = paths.DataArea("journal") }),
});
await queue.RecoverAsync();   // at startup, BEFORE submitting anything
```

An update interrupted while applying is rolled back; one interrupted after every change landed (only
staged deletions left) is finished instead — rolling that back would undo a success. Recovery is safe
to run twice, because every undo step checks the world before acting.

> ⚠ **A journal nobody replays is a directory that fills up.** Configuring one means calling
> `RecoverAsync()` at startup, before the first submit — an interrupted update's paths are exactly the
> ones your next update is likely to touch.

> ⚠ **Only `AllOrNothing` updates are journalled.** `PerChange` promises nothing about a crash, so
> paying a file write per update to guarantee something nobody asked for would be pure cost.

### Other processes touching your files — two different problems

The queue and mission claims both serialize work **inside your process**. If your app manages a folder
it does not own — a game's mod directory, a shared library on a NAS — that is not enough, and the two
remaining cases need different tools. Reaching for the wrong one is the mistake worth avoiding:

| Who is touching the file | Tool | Why the other one is useless here |
|---|---|---|
| **Your own second process** — another instance, or a tool you spawn (an `.exe`, a script) and wait on | `IPathLocker`/`FilePathLocker` — the parent takes the lease for the duration of the child's run | Both sides participate, so exclusion is real. Retrying would just mean two writers racing more politely. |
| **A foreign process** — the game itself, a mod loader, antivirus, Explorer's preview handler, another app editing the same folder | `RetryPolicy` (already there) + `IFileLockInspector` to NAME the holder | A lease is advisory. A process that never takes one is completely unaffected, and no lock design changes that. |

```csharp
// The queue takes leases for you, on every path an update touches:
new FileUpdateQueue(new FileUpdateQueueOptions
{
    Locker        = new FilePathLocker(new FilePathLockerOptions { LockDirectory = paths.DataArea("locks") }),
    LockInspector = new RestartManagerLockInspector(),   // Shenora.Windows
});

// Or hold one yourself around a tool that knows nothing about any of this:
await using var lease = await locker.TryAcquireAsync(modFolder, TimeSpan.FromSeconds(30), ct);
if (lease is null) return;            // someone else has it — defer, do not force
await RunExternalFixerAsync(modFolder, ct);
```

When a change fails, `FileUpdateResult.Holders` names who had it — so "the process cannot access the
file" becomes "held by 3DMigoto (12345)", which an app can retry against or show to a user.

> ⚠ **Put the lock directory where the contenders can both see it.** Several processes on one machine
> → your own local data folder (never the managed tree: an app that does not own that folder would be
> scattering lock files into something the user and other applications are also editing). Two MACHINES
> over a share → a directory ON the share. This is the setting that fails silently: everything works
> until two machines write the same file.

> ⚠ **`WhoHolds` returning empty means "cannot tell", not "nobody".** Restart Manager asks the local
> machine only, so a file held open from another machine over a share is invisible to it — that answer
> exists only on the server.

> ⚠ **Over a network share, a lease released by a CRASH comes back in tens of seconds, not instantly** —
> the server frees the handle when the session times out. Bounded and self-healing, but size your
> lease timeout for it, and expect more transient IO than a local disk (widening `RetryPolicy`'s
> `IsTransient` beyond `IOException` is reasonable over SMB).

**Verify:** the same partition never overlaps *while* a different partition does — both in one run,
for the same reason as the scheduler. Then fail a change mid-update under `AllOrNothing` and confirm
the earlier ones were undone in reverse, and that a staged delete came back.

### Adopting the STAGE without adopting the applier — the on-disk contract

**Staging and applying are separately adoptable, and for some apps only one of them ever will be.** If your
updates are applied by something the kit did not write — typically a native launcher that lives beside the
install and is never replaced by its own updates, so every copy in the field keeps the applier it shipped
with — you can still produce stages with `UpdateStage` and let your existing applier consume them. **The
layout is a supported contract, not an implementation detail:**

```
{UpdateStageOptions.Root}/
  ready.json          ← the MARKER. Written LAST, after every file has matched its hash.
  staged/
    manifest.json     ← the full release manifest, which is where an applier reads REMOVALS
    <every changed file, at its manifest-relative path>
```

`ready.json` is camelCase JSON: `{"pending":true,"version":"1.4.0","stagedAt":"2026-08-06T09:12:33.4+00:00"}`.
An applier that only asks "is an update waiting" may test for the file and read nothing.

**The marker is the only thing an applier may trust**, and that is the whole design: it appears only after
every staged file has been verified, so a crash mid-download leaves files and no marker, and the next run
restages. An applier that scans `staged/` for content instead will eventually act on a half-downloaded one.

> ⚠ **Take this half deliberately, not by default.** The strong half of the story is the journaled,
> recoverable APPLY (`FileUpdateQueue`, `RecoverAsync`, `AllOrNothing`) — and that is exactly the half a
> frozen applier already owns and cannot hand over. If your applier is already installed on your whole user
> base, adopting the stage is cheap and adopting the apply is a migration question the kit does not answer
> for you yet (`Shenora.Launcher` assumes it is the applier from day one, which is true only for a product
> that has not shipped). Naming that up front is more useful than a recipe that quietly assumes both.

---

## What stays yours, permanently

- **Your domain.** Modules, routes, payload schemas, business rules.
- **Every colour, size and pixel.** The kit takes a palette (`CaptionButtonColors`, `TrayMenuColors`,
  splash colours) and ships no design system (D13).
- **Transport-level product decisions** — what an "operation" is, its phases and progress shape,
  whether work queues, what a viewer looks like. The kit ships primitives and lifecycle hooks (D21).
- **Your queue's product decisions** — what runs next and when (`IMissionPolicy`), where the queue
  lives across restarts (`IMissionQueueStore`), what a job record contains and which handler runs it.
  The kit owns the SAFETY rules (claim exclusion, lane capacity, no starvation) and hands the rest back.
- **Your state management.** `createShenoraStore` is built on React's `useSyncExternalStore`; if you
  already use a store library, keep it and subscribe through `useShenoraEvent`.
- **Your event/enum vocabulary.** Module and event names are app schema.

## If the kit almost fits

Say so rather than working around it — before 1.0 the surface is still cheap to change, and "the
framework almost fits, but…" is the most valuable feedback this phase can produce. A capability you
need and cannot express is a gap worth fixing; the reverse — the kit growing your product's shape —
is the failure mode the library discipline exists to prevent.
