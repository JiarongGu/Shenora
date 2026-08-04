# Shenora (神阙)

**The reusable desktop body for Windows applications built with .NET + WinForms + WebView2 +
React.** Shenora provides the hosting shell — application host, lifecycle, typed IPC, module
registration, window management, and native desktop integrations — so an application ships only
its domain logic and screens. The name (神阙, "vessel of the way") reflects the split: Shenora is
the developing body that hosts an application's logic and intelligence; its sibling library
[Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) is the reusable AI brain. The two never
depend on each other.

## Status

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`. Don't hand-edit the
     version here — bump VersionPrefix; the headline follows. -->
**v0.9.0 — pre-release, stabilising toward 1.0.** The application builder, WinForms host, WebView2
hosting, the full typed IPC stack (envelopes, middleware dispatcher, scoped-container router, event
bus, postMessage transport, `@shenora/react` client), the native desktop surface (frameless chrome +
native caption buttons, STA dialogs, shell/clipboard, drag-drop zones, secondary windows, tray) and
the auxiliary browser sessions (off-screen render pool, interactive sessions, streaming sessions) are
extracted from proven in-house applications and verified end-to-end against the sample app. Every
public and protected member is documented and gated by API-surface baselines. The kit also runs on
**Android and iOS**, proven on a device and a simulator.

⚠ **The package set was reorganised by platform in 0.5.0.** `Shenora.WinForms`, `Shenora.WebView2`
and `Shenora.WebView2.Sessions` are superseded by a single `Shenora.Windows`, and `Shenora.Android` /
`Shenora.iOS` are new. The old ids remain restorable at their last version (0.4.0) and carry a
deprecation notice; **migration is a rename, not a rewrite** — every type keeps its name and
signature. `CHANGELOG.md` under `### Breaking` has the mapping.

## Packages

Version in lockstep; reference the **leaf** you need and the rest arrive transitively.

| Package | Registry | Target framework | In one line |
|---|---|---|---|
| `Shenora.Core` | NuGet | `net10.0` | The application host, and the platform-neutral contracts your logic compiles against. |
| `Shenora.Ipc` | NuGet | `net10.0` | The transport-neutral IPC contract and middleware dispatcher. |
| `Shenora.Media` | NuGet | `net10.0` | Media LOGIC only: a per-stream playability planner and the probe-result shape it reads. Ships no codec list and no engine — and **is not needed to play a file** (see below). |
| `Shenora.Windows` | NuGet | `net10.0-windows` **or** `net10.0-windows10.0.17763.0` | The Windows shell, whole: bootstrap, windows, tray, dialogs, single-instance, WebView2 hosting + the postMessage bridge, and auxiliary browser sessions. Both TFMs carry all of it; the versioned one additionally implements `IPlaybackSession` (see below). |
| `Shenora.Android` | NuGet | `net10.0-android` | The Android shell: the same IPC envelope over MAUI's `HybridWebView`. |
| `Shenora.iOS` | NuGet | `net10.0-ios` | The iOS shell — same source as `Shenora.Android`, different platform. |
| `@shenora/react` | npm | ES2022 / ESM | The client half — bridge, event bus, store, hooks. |

The TFM column is here so an adopter can tell whether a package fits without downloading the nupkg to
inspect it. **One shell package per platform** — you reference the one you are building for, and the
two mobile ones are built from a single shared source tree so they cannot drift.

**`Shenora.Windows` offers two TFMs and you pick, which is the point** (D46). Everything in the shell is in
both; the versioned one additionally implements `IPlaybackSession` (Windows' media flyout and lock-screen
transport), because `SystemMediaTransportControls` is WinRT and the WinRT projections exist only when the
target framework names a Windows SDK version. Stay on plain `net10.0-windows` and that one capability refuses
by name with the one-line fix in the message; retarget and it works, on a Windows 10 1809 floor. **The kit
does not narrow your supported platforms for a feature you did not ask for.**

Dependencies — the graph is a **diamond, not a chain**:

```
                    Shenora.Core          net10.0        portable: no Windows reference
                      ↑          ↑
        Shenora.Ipc ──┘          │
          net10.0                │
              ↑                  │
              ├──── Shenora.Windows ──────┘             net10.0-windows
              ├──── Shenora.Android                     net10.0-android
              └──── Shenora.iOS                         net10.0-ios

        Shenora.Core
              ↑
        Shenora.Media                                   net10.0    optional, media LOGIC only
```

**`<video>`, `<audio>` and `<img>` over local files need NO media package.** Serving bytes to a page is
resource interception, and that is a SHELL capability (D45): `Shenora.Core` declares
`IWebViewInterceptor` plus a file middleware that does path containment, HTTP ranges and content types,
and each shell implements the contract over its own webview — `WebViewHost.Interceptor` on Windows,
`MobileWebViewInterceptor` on Android and iOS. One route, and the same three lines compile on all three:

```csharp
host.Interceptor.UseFiles(new WebViewFileOptions { AllowedRoots = [libraryDir], Resolve = MyRoute });
```

A file the platform cannot decode simply errors in the element, which is the honest outcome. **Deciding
what to do about that** — probe it, remux it, transcode it — is what `Shenora.Media` adds, as a further
middleware. That is why it is optional: an app that serves ordinary files never references it.

⚠ **One measured platform fact rides on the interceptor, not on media.** Android's webview applies the
`Range` start to whatever body it is handed; WebView2's and iOS's send the body verbatim. So the same
request needs an *unsliced* body on one and a *sliced* body on the other two —
`IWebViewInterceptor.RangeDelivery` reports which, `UseFiles` reads it from the platform so it cannot be
passed in wrong, and a fourth shell cannot compile without declaring its own answer.

**`Shenora.Ipc` is platform-neutral and stays that way.** It targets `net10.0`, references only
`Shenora.Core`, and binds to no UI framework at all — the whole transport story (D16) rests on that:
the same envelopes ride WebView2 postMessage on Windows, `HybridWebView` on mobile, and a WebSocket
tomorrow, and a server-side or headless host can dispatch them with no shell anywhere in the graph.
Anything that genuinely needs a window lives one layer up: the UI-thread seam is the portable
`IUiDispatcher` in `Shenora.Core`, implemented once per shell.

App logic that must stay portable references only `Shenora.Core` — `samples/Shenora.Sample.Logic` is
a plain `net10.0` project that proves it, and the same facade runs on all three shells.

Two consumption profiles are supported: **desktop-only** (full postMessage IPC) and **server-backed**
(the app runs its own in-process HTTP server shared with mobile/LAN clients; Shenora provides the
shell and a one-way event fast-path). A `Shenora.Hosting.AspNetCore` package was considered and
**declined** — what it would have held turned out to be five lines of ASP.NET plus a security policy
that belongs to the app (D10).

Shenora is deliberately **headless**: it depends on no UI component library and ships no design
system — applications bring their own. It also ships no product recipes; the mechanisms are here and
the workflows are yours (D21).

---

## Using each package

Enough to get each one working, plus the trap that costs an afternoon. The sample app under
`samples/` is the full reference composition.

### `Shenora.Core` — the host, and portability

The builder, lifetime, module registration, environment and app paths — plus the **platform-neutral
contracts** (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`, `IUiInteraction`)
and the in-process `IEventBus`.

```csharp
var builder = ShenoraApplication.CreateBuilder(args);
builder.Services.AddSingleton<IMyService, MyService>();
using var app = builder.Build();
app.Run();                       // executes the registered runner (UseWinForms supplies one)
```

Put your facades in a plain `net10.0` project that references only this package and inject the
contracts. The compiler then enforces portability instead of a document asserting it — if a Windows
type ever creeps into app logic, that project turns red. See `docs/ADOPTION.md` Stage 4.

### `Shenora.Ipc` — the wire and the dispatcher

Typed request/response/notification envelopes, a composable middleware pipeline, module facades, and
a structured error contract (`code` + parameters, i18n-ready). Transport-neutral by design: the same
envelopes ride postMessage today and a WebSocket or mobile channel tomorrow.

```csharp
public sealed class SettingsFacade : BaseFacade
{
    public override string ModuleName => "SETTINGS";

    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context,
        CancellationToken ct) =>
        request.Type switch
        {
            "GET"  => Task.FromResult<object?>(_settings.Current),
            "SAVE" => Save(request, context, ct),
            _      => throw UnknownType(request),
        };
}

services.AddModuleFacade<SettingsFacade>();
services.AddMessageDispatcher();          // error handler → logging → your middleware → facades
```

**Raw exception text never crosses the wire.** An `OperationException` carries the app's own code,
parameters and message; anything else becomes `UNKNOWN_ERROR` plus the exception's type name, with
the detail in the host log. The one sharp edge: an `OperationException`'s message crosses **verbatim**
— so never build one from `ex.Message`, which would turn the sanctioned channel into a bypass.

**`IModuleContext` is the route's world: who it is, how it emits, how it starts long work.**
`context.Publish(type, payload?, scope?)` emits on the host bus under the facade's own module — the
default gesture for progress and state, not a wiring exercise, and it can never drift from
`ModuleName` the way a hand-typed literal at every call site can. For work too long to answer inline,
`context.Run(new OperationOptions { Kind = "IMPORT", Cancellable = true }, async (op, ct) => {
op.Report(progress: 40); … })` hands it to the background, tracks it (id, status, progress,
cancel-by-id), and returns the operation id immediately — pair it with `services
.AddShenoraOperations()` and `@shenora/react`'s `useShenoraOperations()` for a host-backed progress
store with no per-feature event wiring. Both are opt-in: a facade that never publishes and never
starts tracked work pays nothing.

### `Shenora.Windows` — the shell, the page host, and extra browsers

One package, three areas — `Shell/`, `WebView/` and `Sessions/` in the source. They merged because
this kit is React-in-a-webview by construction: there is no Windows composition that wants the
WinForms primitives and not WebView2.

#### The native shell

Bootstrap with global exception handling, window-state persistence (DPI-correct, via `GetMonitorInfo`
rather than the mis-scaled `Screen.WorkingArea`), single-instance guard, secondary windows on their
own STA pumps, tray icon, frameless `OptimizedForm` with optional native caption buttons, splash
panel, and the Windows implementations of the Core contracts.

```csharp
builder.UseWinForms(new WinFormsHostOptions { MainForm = sp => new MainForm(sp) });
```

**`CloseReason.UserClosing` also means a programmatic `Close()`.** With close-to-tray on, a
startup-abort path that calls `Close()` leaves a resident process — exit via `ExitApplication()`.

#### Hosting the page

One place a WebView2 is configured: environment prewarm, dev-server vs packaged-bundle loading,
serving (embedded resources, virtual hosts, app schemes), safe defaults for new windows, downloads,
permissions and renderer crashes, plus the postMessage IPC bridge with batched event push.

```csharp
var host = new WebViewHost(webView, options);
await host.InitializeAsync();            // idempotent, and bounded by one InitTimeout
var bridge = new WebViewIpcBridge(webView, new WebViewIpcBridgeOptions { Dispatcher = dispatcher });
host.Navigate();
bridge.Attach();
```

Construct the bridge **before** `InitializeAsync` — event buffering starts at construction, so
anything emitted during the slow WebView2 init survives. Serving something seekable or large (video,
audio) needs a deferred scheme rather than a folder mapping: a folder mapping cannot honour `Range`.
The handler receives request headers and returns a status, headers and a stream, so nothing is
buffered whole.

#### Extra browsers

> ⚠ **A session can only reach URLs the network can reach.** Each one runs its own browser
> environment with its own profile, and none of the main host's serving — the resource provider, the
> virtual host — is set up in it. So a session can load `http://localhost:…` or the internet, but NOT
> your app's embedded bundle: navigating an off-screen session to your packaged origin renders
> WebView2's "can't reach this page". Affects desktop-only apps serving embedded resources; a
> server-backed app whose pages are already on a loopback origin is unaffected. Tracked as `TASKS.md`
> E1.

Off-screen and auxiliary browser sessions over the same runtime: a bounded LIFO `RenderSessionPool`,
`InteractiveSession` (a human-in-the-loop window over an isolated persistent profile, driven by
**your** driver), and `StreamingSession` (frames out, input in). The kit ships the mechanics and no
scenario — a worked driver example lives in the sample, to copy and edit.

### `@shenora/react` — the client half

```ts
import { getBridge, configureBridge, useShenoraEvent, createShenoraStore } from '@shenora/react';

configureBridge({ onPostError: (f) => log.error(f.module, f.type, f.error) });
await getBridge().notifyReady();          // starts notification delivery

const settings = await getBridge().invoke<Settings>('SETTINGS', 'GET');
useShenoraEvent<Progress>('JOBS', 'PROGRESS', (p) => setProgress(p.percent));
```

**Reserve `invoke` for calls that are quick and UI-thread-safe**, and `post` for everything else.
The host's dispatch pipeline preserves the caller's synchronization context by design, so a route's
synchronous segment runs on the UI thread: measured on the sample, the same 3 s of work stalls the
window 2 027 ms when left in the route and 0 ms when handed off and streamed back as events.

---

## Building the frontend bundle

The host serves **HTML no-cache and hashed assets immutable**. Two things follow for the app's build:

- **Content-hash your asset filenames** and let the HTML stay unhashed. Vite does this by default
  (`assets/index-<hash>.js`); the point is that a released HTML file must never be cached, or a user
  keeps loading an old document that references assets you have replaced.
- **Split vendor code into stable chunks** so a one-line app change does not invalidate the whole
  bundle for everyone. With Vite, `build.rollupOptions.output.manualChunks` — pulling React and any
  large third-party library into their own chunks — keeps those hashes steady across releases.

Embed the built output and point the provider at it:

```xml
<EmbeddedResource Include="wwwroot\**" />
```
```csharp
ResourceProvider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
{
    Assembly = typeof(Program).Assembly,
    ResourcePrefix = "MyApp.wwwroot",     // includes the folder segment
})
```

> ⚠ **Dev-loop trap.** A dev server pre-bundles `@shenora/react`. After upgrading the package, clear
> that cache (for Vite, delete `node_modules/.vite`) and restart it — otherwise the page silently runs
> the OLD client: imports resolve to `undefined` and the app renders blank while the host looks
> perfectly healthy.

## Dev loop

```
node devtools/dev.mjs build     # dotnet build + npm build (react package)
node devtools/dev.mjs test      # dotnet test + vitest
node devtools/dev.mjs verify    # build · test · typecheck · leak scan · knowledge check · doctor
node devtools/dev.mjs pack      # nupkgs + npm tarball into publish/packages (lockstep version)
node devtools/dev.mjs doctor    # version/readme drift check (--fix to sync)
```

Requirements: .NET 10 SDK, Node 22+. See `devtools/README.md` for the full command set,
`docs/README.md` for the documentation map, and `docs/ADOPTION.md` to bring an existing app onto the
kit.

## License

MIT — see [LICENSE](LICENSE).
