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
**v0.2.0 — pre-release, stabilising toward 1.0.** The application builder, WinForms host, WebView2
hosting, the full typed IPC stack (envelopes, middleware dispatcher, scoped-container router, event
bus, postMessage transport, `@shenora/react` client), the native desktop surface (frameless chrome +
native caption buttons, STA dialogs, shell/clipboard, drag-drop zones, secondary windows, tray) and
the auxiliary browser sessions (off-screen render pool, interactive sessions, streaming sessions) are
extracted from proven in-house applications and verified end-to-end against the sample app. Every
public and protected member is documented and gated by API-surface baselines. **All six packages are
published** — see `docs/RELEASING.md` for how a version is cut and `CHANGELOG.md` for what each one
carries.

## Packages

Version in lockstep; reference the **leaf** you need and the rest arrive transitively.

| Package | Registry | Target framework | In one line |
|---|---|---|---|
| `Shenora.Core` | NuGet | `net10.0` | The application host, and the platform-neutral contracts your logic compiles against. |
| `Shenora.Ipc` | NuGet | `net10.0` | The transport-neutral IPC contract and middleware dispatcher. |
| `Shenora.WinForms` | NuGet | `net10.0-windows` | The native Windows shell: bootstrap, windows, tray, dialogs, single-instance. |
| `Shenora.WebView2` | NuGet | `net10.0-windows` | Hosting a WebView2: serving, policies, and the postMessage bridge. |
| `Shenora.WebView2.Sessions` | NuGet | `net10.0-windows` | Extra browser sessions: a render pool, interactive windows, frame streaming. |
| `@shenora/react` | npm | ES2022 / ESM | The client half — bridge, event bus, store, hooks. |

The Windows packages ship as `net10.0-windows7.0` in `lib/` — the TFM column is here so an adopter
can tell whether a package fits without downloading the nupkg to inspect it.

Dependencies — the graph is a **diamond, not a chain**:

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

**`Shenora.Ipc` is platform-neutral and stays that way.** It targets `net10.0`, references only
`Shenora.Core`, and binds to no UI framework at all — the whole transport story (D16) rests on that:
the same envelopes ride WebView2 postMessage today and a WebSocket or a mobile shell's channel
tomorrow, and a server-side or headless host can dispatch them with no WinForms anywhere in the
graph. Anything that genuinely needs a window lives one layer up: the UI-thread seam is the portable
`IUiDispatcher` in `Shenora.Core`, implemented once in `Shenora.WinForms`, and the two IPC-facing
desktop facades (`WindowCommandFacade`, `DropZoneFacade`) live in `Shenora.WebView2` — which is the
first package that may see both halves — rather than in either base.

The corollary for adopters: `Shenora.WinForms` does **not** bring `Shenora.Ipc` with it. They are
siblings over `Shenora.Core`, so a WinForms-only shell that wants typed messaging adds
`Shenora.Ipc` as a second, explicit reference. App logic that must stay portable references only
`Shenora.Core`.

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

### `Shenora.WinForms` — the native shell

Bootstrap with global exception handling, window-state persistence (DPI-correct, via `GetMonitorInfo`
rather than the mis-scaled `Screen.WorkingArea`), single-instance guard, secondary windows on their
own STA pumps, tray icon, frameless `OptimizedForm` with optional native caption buttons, splash
panel, and the Windows implementations of the Core contracts.

```csharp
builder.UseWinForms(new WinFormsHostOptions { MainForm = sp => new MainForm(sp) });
```

**`CloseReason.UserClosing` also means a programmatic `Close()`.** With close-to-tray on, a
startup-abort path that calls `Close()` leaves a resident process — exit via `ExitApplication()`.

### `Shenora.WebView2` — hosting the page

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

### `Shenora.WebView2.Sessions` — extra browsers

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
