# Adopting Shenora into an existing desktop app

For an app that already has a WinForms + WebView2 shell and wants to stop maintaining it. It assumes
nothing about this repo's history: everything needed is here or linked.

**There are THREE shells now.** [Stage 5](#stage-5--a-maui-shell-if-stage-4s-logic-should-also-run-on-a-phone)
runs the same portable app logic on MAUI — **both proven on real hardware**: Android on a device, iOS on
an iPhone 17 Pro, including a save picker with byte-identical output, seekable media playback, the
audio-conversion tier and Live Activities. It is genuinely optional and
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

**Three capabilities here are not stages at all** and now live in [guides/](guides/):
[the mission scheduler](guides/missions.md), [the file-update queue](guides/file-updates.md) and
[media playback](guides/media.md). All three live in `Shenora` and need no shell, no IPC and no Windows,
so any can be taken first, last, or on its own by an app that wants nothing else here. They compose but
none requires another.

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

Reference the **leaf** package you need; the rest arrive transitively. The graph is a **fan, one level
deep** — each shell references `Shenora` and nothing else of the kit's, so referencing two shells is
impossible by construction rather than by convention. (It was a *diamond* while `Shenora.Ipc` sat in the
middle; D65 removed that level.)

```
                      Shenora           net10.0    portable: no Windows reference
                         ↑              (the IPC stack is the Shenora.Core.Ipc
            ┌────────────┼────────────┐  NAMESPACE — it stopped being a package in D65)
            │            │            │
   Shenora.Windows  Shenora.Android  Shenora.iOS
   net10.0-windows  net10.0-android  net10.0-ios
```

**One shell package per platform.** Reference the one you are building for. Pin exact versions — see
`docs/RELEASING.md` for the pre-release feed recipe.

**There are no optional feature packages** (D55). Media, file operations and archive extraction ship
inside `Shenora` as the namespaces `Shenora.Modules.Media`, `Shenora.Engine.Files` and
`Shenora.Engine.Compression` — so a
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

**Verified 2026-08-03, so you can skip re-proving it:** the portable packages restore from nuget.org into
a bare `net10.0` project, and `Shenora.Windows` into `net10.0-windows` — from a throwaway project with no
local feed. ⚠ That run named `Shenora` + `Shenora.Ipc`, which was the set at the time; D65 has since
folded IPC in, so today it is `Shenora` alone. And a **Stage 1 spike compiled clean against the published package**: an
app's two window-state call sites (`Apply` on load, `Save` on close, persisting width/height/x/y/`Placement`)
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
| Window bounds save/restore | `WindowStateManager` (+ `IWindowStateStore`, `JsonFileWindowStateStore`) | Stores LOGICAL px and restores physical, validates the rect is reachable, prefers `IAppMaximizable` over `Form.WindowState`, and shrinks a size saved on a bigger display to fit a smaller one (`WindowStateOptions.MaxToWorkArea`, default on). Per-monitor DPI accuracy is the default — `AttachTo(form)` defers to `HandleCreated` when the form has no handle yet and resolves `DeviceDpi` at that moment (still before `Show`, so no resize flash). The explicit `AttachTo(form, scale)` overload is for the unusual case where you want to size against a scale you resolve yourself (a test harness, a preview against a different monitor). A saved `Placement = WindowPlacement.Maximized` is also deferred to `Shown` — maximizing earlier does not survive `OnLoad` on a plain `Form`. ⚠ `WindowState.Placement` is an enum, not the old `Maximized` bool: an existing store's rows need the one-field migration `CHANGELOG.md`'s `### Breaking` describes. |
| A double-buffered form base / frameless chrome | `OptimizedForm(+Options)` | Frameless is opt-in. Maximize is manual (work-area fill), so `AppPlacement` is the truth, **not** `Form.WindowState`. **Fixes a bug you likely have:** hand-rolled work-area code reaches for `Screen.WorkingArea`, which is DPI-mis-scaled on a HiDPI monitor (~12 px per edge — the visible gap the manual maximize path exists to remove); `OptimizedForm` uses `GetMonitorInfo`. |
| Caption buttons drawn by the page | `OptimizedFormOptions.NativeCaptionButtons` + `CaptionButtonColors` | Report the rects via `SetCaptionButtons`; the window clips them out of every covering child and paints them, which is what buys Windows 11 **Snap Layouts**. Requires `FramelessChrome` — the combination throws at construction rather than doing nothing. |
| Tray icon + themed menu | `TrayIcon(+Options)`, `TrayMenuColors` | **`CloseReason.UserClosing` also means a programmatic `Close()`** — with close-to-tray on, a startup-abort path that calls `Close()` leaves a resident process. Close via `ExitApplication()`. |
| Single-instance mutex + activate-existing | `SingleInstanceGuard` | Idempotent by design (an OS mutex is per-thread reentrant, which broke the naive version). |
| File dialogs / clipboard / shell open / reveal | `IFileDialogs`, `IClipboardService`, `IUrlLauncher`(+`IShellLauncher`), `IUiInteraction`(+`IFormInteraction`) | Dialogs run on a dedicated STA thread with owner-handle z-order. The portable halves live in `Shenora` — see Stage 4. **Clipboard: one `SetAsync(ClipboardContent)` carries every representation at once** — hand-rolled code that sets text and then an image is silently keeping only the image. Put your own format in `Formats["application/x-yourapp-…"]`; the kit carries it verbatim. ⚠ **On ANDROID a picture is refused, deliberately.** An image reaches another app as a `content://` URI served by a `ContentProvider` **your app** declares in its manifest — the kit cannot declare one on your behalf, and inventing a private scheme would produce a copy no other app can open, which is worse than the refusal. Text, HTML and your own formats work everywhere; gate an image control on the capability rather than assuming it. iOS and Windows carry pictures. **To reach it from the PAGE rather than from C#**, register
`AddShenoraClipboard()` — an opt-in IPC module (`SHENORA.CLIPBOARD`) whose client half is
`useClipboard()` in `@shenora/react`. It exists because the browser's own Clipboard API cannot do
what this can: read without a user gesture, and carry an app's private format alongside the text.
The hook reports what THIS shell will honour, so a page gates its paste control instead of
discovering the refusal by exception. |
| Extra windows on their own threads | `SecondaryWindows` | `FormClosed` is **not** the end of a window; cleanup happens after `Application.Run` returns, or a WebView2 child leaves a locked profile folder. |
| App root / data / resources paths, env overrides | `ShenoraPaths(+Options)` | Resolves and absolutizes; file dialogs move the process CWD, so a relative root must not be re-resolved later. |
| Startup splash | `SplashPanel(+Options)` | Colours are yours. |
| OS file drag-drop over page elements | `DropZoneManager` (in `Shenora.Windows`) + **`useDropZone`** | **Not optional sugar — the only workable file-drop path for a desktop shell, and the page's own drop event is what it replaces.** A page-side `onDrop` yields a `File` whose only accessor is its CONTENT, so with the page as UI and the host doing the file work, the bytes must be read into the renderer and pushed across IPC: a full copy of every dropped file, EAGERLY, at drop time, before the app knows whether it wants any of them. Drop 200 files to filter by extension and you pay for all 200; drop a multi-GB asset and you pay that, to reach a file the host could have opened off the same disk. `DropZoneManager` puts transparent native overlays over the page's zone elements, reads the OS drag data directly, and hands you `string[]` paths — open lazily, stream, hash incrementally, move or link without copying — including drags from Explorer or another app while your window is **backgrounded**. Wiring: **Stage-1-adoptable STANDALONE despite living in the WebView2 package** — it depends only on `Shenora` (`IEventBus`), the WebView2 control and a `Form`, and references no `Ipc` type at all. `new` it, hand it your own bus, subscribe to its three events, and forward them over whatever transport you already have — no Stage 3 migration required. (An earlier revision of this table filed the whole thing under Stage 3 because `DropZoneModule` does need IPC; that is true of the FACADE, not the manager, i.e. not the part that is actually hard — an adopter found this only by reading the source.) Zones clear on **document change**, not the ready handshake, so there is no ordering contract against `notifyReady`. The IPC-wired half — `DropZoneModule` + `useDropZone` — formally belongs to Stage 3 because it rides the typed bridge, but treat it as the DESTINATION for this row rather than an optional extra: a React page should call `useDropZone` and never register a DOM drop handler for files. |

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

- 🔴 **Which WebView2 RUNTIME you ship is YOUR call, and the kit will not make it for you (D68).** The
  default is **Evergreen** — the machine's shared runtime, kept patched by Microsoft — because the kit
  ships no browser bytes. To pin a fixed-version runtime instead, point at its folder:
  ```csharp
  new WebViewEnvironmentOptions { BrowserExecutableFolder = @"C:\MyApp\WebView2Runtime" }  // null = Evergreen
  ```
  It buys determinism — the browser you tested against, and no surprise update — and costs ~150 MB per
  install plus **ownership of the security updates Evergreen was handling for you**. That trade depends on
  facts the kit cannot see (managed machines? does install size matter? can you accept an untested browser
  update?), which is exactly why it is a default and a seam rather than a feature.
  - **Your USER-DATA folder is already app-local either way** — `paths.DataArea("webview2")`, never shared
    between apps. If you arrived here worried about sharing, that half is already handled.

- **Serving.** `FolderMappings` maps one or more virtual hosts to folders — including a deliberately
  DIFFERENT origin when you need cross-origin ES-module imports (set `AccessKind`). Embedded-resource
  serving and app schemes are available too (`ResourceProvider`, `DeferredSchemes`). A `DevUrl` gives
  you the dev-server switch a hand-rolled host usually lacks, which is the stale-bundle footgun.
- **Serving LOCAL FILES to the page — `<video>`, `<audio>`, `<img>`, a PDF — is the interceptor, and it is
  the same three lines on all three shells** (D45). Prefer this over a custom scheme for anything that is a
  file on disk: it is portable, and the containment check comes with it.
  ```csharp
  using var app = builder.Build();
  app.UseFiles(new WebViewFileOptions
  {
      AllowedRoots = [libraryDir],                    // EMPTY means nothing is servable — fail-closed
      Resolve = uri => uri.AbsolutePath.EndsWith("/media")   // your route; null = "not mine"
          ? Path.Combine(libraryDir, DecodeYourPayload(uri.Query))
          : null,
  });
  app.Run();
  ```
  Declared on the BUILT app, before the first window exists, so **every `WebViewHost` the app builds** serves
  it, a secondary window's included (D64). ⚠ **A SESSION BROWSER DOES NOT** — it is not a `WebViewHost`, it
  builds its own environment and interceptor, and there is no way to hand it the pipeline. A page that
  renders `mediaUrl(…)` in the main window 404s inside a `RenderSession`; see "Serving your own frontend
  into an OFF-SCREEN session" below for the pair you hand it instead.
  `host.Interceptor.UseFiles(…)` still exists for the
  one webview that must genuinely differ; it serves that interceptor only. ⚠ Declaring a step after a window
  already exists THROWS rather than half-applying, because a route that reached some windows and not others
  is invisible from the outside.
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
- **If your page uses a HASH ROUTER, reloading it was broken by the platform on BOTH mobile shells and the
  kit now repairs it — you write nothing.** MAUI's request→asset mapping strips a query string and not a fragment,
  so a reload at `https://host/#/library` looked for an asset named `#/library`, 404'd, and Chromium showed
  its `net::ERR_INVALID_RESPONSE` error page instead of your app. `MobileWebViewInterceptor` answers that
  request with `HybridRoot/DefaultFile` — the same bytes the platform serves for the fragment-free URL — so
  the page boots normally and your router reads the fragment off `location` as usual. It runs only after
  your own middleware decline, and it declines rather than 404s if the bundle is not there, so an app that
  serves its own document is untouched.
  - ✅ **iOS is repaired too, by the same code** — the guard is pure URL shape with no platform test, and
    the repair runs in shared, unguarded code, so there is nothing to opt into on either shell.
    ⚠ **When you verify a reload on iOS, "it rendered" is not evidence** — WKWebView keeps the previous
    page on screen. Use a pre-reload marker and a node count via `EvaluateJavaScriptAsync`. On that
    non-evidence the repair was once written off as iOS-impossible; what had actually broken it was a
    blocking bundle read that deadlocks the iOS main thread, not the idea.
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
  environment and inherits none of the host's serving. **You only need this if you serve an EMBEDDED
  bundle** — a server-backed app's pages are on a real loopback origin, which a session already reaches.
  The wiring, the both-or-neither rule and the two limits that decide whether it fits (a deferred SCHEME
  cannot work inside a session; the bundle's CORS header makes it safe only on a session rendering YOUR
  pages) are `guides/sessions.md` and D38 — one owner rather than a second copy that drifts.
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

- **Host side.** Derive from `ModuleBase`, one instance per existing module, and let
  `RouteMessageAsync` call your module's handler. `request.Type` is your action; rebuild whatever
  document shape your handler expects from `request.Payload` (if your client spread the payload at
  the top level, nest/unnest here — it is a few lines). Return `null`: your modules answer with
  events, which is the `post` shape, and answering at all is what buys correlation. Emit through
  `context.Publish(type, payload, scope)` (or `IEventBus.EmitAsync(module, type, payload)` directly,
  if you're not yet on `ModuleBase`'s context). Nothing about this needs Windows, so the adapter can
  live in a `net10.0` project (see Stage 4).
- **Client side.** `bridge.post(module, type, { payload })` for the send; `eventBus.subscribeToAll`
  for a legacy "every host message" handler. Emit real `(module, type)` pairs from the host adapter
  rather than tunnelling everything through one reserved pair — that is what lets a migrated
  component use `useShenoraEvent`/`createShenoraStore` while unmigrated stores still see the same
  event through the firehose. Tunnelled events are invisible to both, which makes migration
  all-or-nothing per event.

> ⚠ **The trap that quietly undoes the error boundary.** Do NOT wrap a caught exception as
> `throw new ShenoraException(code, message: ex.Message)`. A `ShenoraException`'s message crosses
> the wire VERBATIM by design — it is *your* words for an expected failure — so that one line puts raw
> exception text (paths, connection strings) back on the page. It is exactly the line you would port
> if your old dispatcher emitted `$"{action} failed: {ex.Message}"`. Let the exception escape instead:
> `ModuleBase` maps it to `UNKNOWN_ERROR` plus the exception's type name and logs the detail host-side.

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
  a response" leaves undefined, 0.2.0/D23): **you declare nothing.** Every request is tracked from the
  moment it is dispatched (D66), so a slow route is just a route:
  ```csharp
  protected override async Task<object?> RouteMessageAsync(
      IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
  {
      context.Report(new IpcProgress(40, 100, "percent"));   // as often as you like
      await DoTheLongThingAsync(cancellationToken);           // ct is cancellable by the page
      return new { ok = true };                               // the ordinary response
  }
  ```
  `Value`/`Total`/`Unit` are in the APP's own terms (bytes against a known total, items, an absolute
  count with no denominator, or a genuine percent — the kit never assumes which) and are passed through
  unchanged: no clamp, no validation.
  **Nothing to register and nothing to enable** — request tracking is part of the application
  (`Build()` sets it up), and `builder.UseRequests(x => …)` only CONFIGURES it. It ships
  `IpcRequestsModule` (`LIST`/`CANCEL`/`CLEAR_FINISHED` under module `SHENORA.REQUESTS`), so there is no
  hand-rolled `…PROGRESS`/`…DONE` event pair per feature and no per-app re-agreement of what "cancel
  this" means. Client side, `useShenoraRequests()`
  is a ready-made `createShenoraStore` instance: snapshots via `LIST` on first subscribe (so a
  progress strip that mounts mid-run isn't empty), folds `REQUEST_UPDATED` by id afterward, and
  folds `REQUEST_REMOVED { requestIds }` by deleting those ids — the one authoritative signal for
  history eviction and `CLEAR_FINISHED`. ⚠ Most requests never appear at all: the host stays silent for
  the first 50 ms, so this is a list of work that is actually TAKING A WHILE, not a log of every call.
  🔴 **There is no "waiting" state, and nothing to declare.** A request is IN FLIGHT or DONE, exactly as
  `XMLHttpRequest` has it. Work that parks awaiting a human is not a request — nobody is waiting on the
  reply — so it belongs on an event stream of its own; see the mission adapter below for the worked
  example. **Crash recovery is deliberately yours**: keep your checkpoint token in your own store, and
  on restart begin the resumed run as an ordinary request.

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
   `Shenora` — one package, which carries the IPC contracts as the `Shenora.Core.Ipc` namespace.
   **Do not reference `Shenora.Windows`, `Shenora.Android` or `Shenora.iOS`**; adding any of them
   defeats the guard entirely, which is the one way this goes wrong quietly.
2. **Add it to your solution.** A guard project that nothing builds is not a guard. (This repo learned
   that the hard way: the samples were missing from the solution file, so the "am I done?" gate never
   compiled them.)
3. **Move the facades, then fix what goes red.** Each error is a genuine platform dependency; the
   fix is nearly always to inject a contract instead:

   | App logic reaches for | Inject instead (all in `Shenora`) |
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
5. **Register nothing extra.** `UseWindows` registers both faces of each contract — the Windows one
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

Setup, what transfers, the page ORIGIN trap that costs a day, Live Activities, one bundle for every
shell, and the iOS deploy loop: **[guides/mobile.md](guides/mobile.md)**.

---

## Not stages — capabilities you can adopt on their own

These three said *"not a stage"* in their own opening lines while living inside a staged migration, which
is what made this file 1,400 lines and gave a reader who only wanted one of them no way in. They are
**[guides](guides/)** now, moved verbatim:

| Guide | Adopt it when |
|---|---|
| [The mission scheduler](guides/missions.md) | you have a job queue, a worker pool, or a "don't let these two touch the same path" rule |
| [The file-update queue](guides/file-updates.md) | path claims are too coarse — you need staged writes, an undo journal, or another process holds your files |
| [Media playback](guides/media.md) | a file your user picked will not play, or you want the lifecycle in .NET rather than in the page |

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
