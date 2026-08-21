# Shenora (神阙)

**A hybrid app development framework — .NET + React — for Windows, Android and iOS.** Shenora is the
"body" an app boots its logic on: shell hosting, typed IPC, modules, window management and native
services, so an application ships only its domain logic and screens. **.NET does the platform work,
React does the interface, and the kit owns the seam between them** — which is the whole differentiator
against a webview-plus-plugins stack: the ceiling here is .NET's (real threads, real handles, the
platform SDKs, background execution), not the web platform's.

⚠ **It is not a media library, a file library, or any other single-domain library** — those are
capabilities it happens to carry (D53). The name (神阙, "vessel of the way") reflects the split: Shenora is
the developing body that hosts an application's logic and intelligence; its sibling library
[Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) is the reusable AI brain. The two never
depend on each other.

## Status

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`. Don't hand-edit the
     version here — bump VersionPrefix; the headline follows. -->
**v0.12.0 — pre-release, stabilising toward 1.0.** The page-facing clipboard (`useClipboard()` over an
opt-in `SHENORA.CLIPBOARD` module — reading with no user gesture, and an app's own format carried
verbatim, neither of which the browser's Clipboard API can do), the segment/streaming media tier, and
the browser sessions' hook + event catalogue are the newest arrivals.

> ⚠ **The segment/streaming media tier is EXPERIMENTAL.** It works, and it is the one part of the kit
> not extracted from an application that had already proven it in production. A defect review in
> August 2026 found several faults concentrated there, all of them now fixed — but the reason they
> concentrated has not changed, and it is worth knowing before you rely on it: **it had only ever been
> tested against media this kit itself produced.** Sources muxed by MP4Box, Bento4, mkvmerge or Apple
> exercise shapes ours never emits — lacing, a track that starts late, unusual descriptor encodings — and
> that is where every fault was. Fixed: a foreign AAC track that could not play at all, a track starting
> late being dropped for a whole film, an unbounded buffer, a stream that could wedge permanently, a
> rotated url keeping its expired opener, and laced audio losing its frame durations. The label stays
> until the tier has coverage against media the kit did NOT produce. The rest of the kit carries no such
> caveat.

The application builder, WinForms host, WebView2 hosting, the full typed IPC stack (envelopes,
middleware dispatcher, scoped-container router, event bus, postMessage transport, `@shenora/react` client), the native desktop surface (frameless chrome +
native caption buttons, STA dialogs, shell/clipboard, drag-drop zones, secondary windows, tray) and
the auxiliary browser sessions (off-screen render pool, interactive sessions, streaming sessions) are
extracted from proven in-house applications and verified end-to-end against the sample app. Every
public and protected member is documented and gated by API-surface baselines. The kit also runs on
**Android and iOS**, both proven on real hardware — an Android device and an iPhone 17 Pro, including
media playback, the native save picker, audio conversion and Live Activities.

⚠ **The package set was reorganised by platform in 0.5.0.** `Shenora.WinForms`, `Shenora.WebView2`
and `Shenora.WebView2.Sessions` are superseded by a single `Shenora.Windows`, and `Shenora.Android` /
`Shenora.iOS` are new. The old ids remain restorable at their last version (0.4.0);
**migration is a rename, not a rewrite** — every type keeps its name and
signature. `CHANGELOG.md` under `### Breaking` has the mapping.

## Packages

Version in lockstep; reference the **leaf** you need and the rest arrive transitively.

| Package | Registry | Target framework | In one line |
|---|---|---|---|
| `Shenora` | NuGet | `net10.0` | The application host and the platform-neutral contracts your logic compiles against — plus the capabilities that are shell work rather than optional extras: media (`Shenora.Modules.Media` — probe, plan, serve, remux), file operations (`Shenora.Engine.Files` — journalled update queue, path locks, staged self-updater) and safe archive extraction (`Shenora.Engine.Compression`). |
| `Shenora.Launcher` | NuGet | native (`win-x64`, `linux-x64`) | The prebuilt launcher that runs **before** your app and applies a staged update — for framework-dependent apps, where the runtime may be absent and files may be held open. Carries per-RID binaries plus the C++17 library sources and `main.cpp` template, so you can use the stock launcher or build your own. **A self-contained app needs none of it** — `Shenora.Engine.Update`'s `UpdateStage.ApplyAsync` already applies updates in portable .NET. |
| `Shenora.Windows` | NuGet | `net10.0-windows` **or** `net10.0-windows10.0.17763.0` | The Windows shell, whole: bootstrap, windows, tray, dialogs, single-instance, WebView2 hosting + the postMessage bridge, and auxiliary browser sessions. Both TFMs carry all of it; the versioned one additionally implements `IPlaybackSession` (see below). |
| `Shenora.Android` | NuGet | `net10.0-android` | The Android shell: the same IPC envelope over MAUI's `HybridWebView`. |
| `Shenora.iOS` | NuGet | `net10.0-ios` | The iOS shell. It SHARES the MAUI-shaped half with `Shenora.Android` (`src/Shenora.Mobile/`: transport, dispatcher, safe area, interception) and owns what is genuinely per-platform — AVPlayer, `MPNowPlayingInfoCenter`, ActivityKit — in its own `Services/`. |
| `@shenora/react` | npm | ES2022 / ESM · **React ≥ 18** | The client half — bridge, event bus, store, hooks. Built and tested against the LATEST React (19); 18 is supported and the floor is enforced rather than assumed — `verify` type-checks the shipped sources against React 18's types, so an API that does not exist there fails here instead of in your build. 18 is the floor because `useSyncExternalStore` is, and the store is built on it. |
| `@shenora/cli` | npm | Node 20+ | **Build-time only, a `devDependency`** (D67). The `shenora` binary: take a built app onto a simulator, a real iPhone or an Android device, with no Xcode project of your own. The Android half runs on Windows. Ships inside nothing you deploy. |

The TFM column is here so an adopter can tell whether a package fits without downloading the nupkg to
inspect it. **One shell package per platform** — you reference the one you are building for, and the
two mobile ones are built from a single shared source tree so they cannot drift.

**`Shenora.Windows` offers two TFMs and you pick, which is the point** (D46). Everything in the shell is in
both; the versioned one additionally implements `IPlaybackSession` (Windows' media flyout and lock-screen
transport) and `WindowsMediaPlayer` (the Media Foundation `IMediaPlayer`), because both rest on WinRT and
the WinRT projections exist only when the target framework names a Windows SDK version. Stay on plain
`net10.0-windows` and those two capabilities refuse
by name with the one-line fix in the message; retarget and they work, on a Windows 10 1809 floor. **The kit
does not narrow your supported platforms for a feature you did not ask for.**

Dependencies — the graph is a **fan, one level deep**: each shell references `Shenora` and nothing else
of the kit's, so referencing two shells is impossible by construction rather than by convention.
(It was a *diamond* while `Shenora.Ipc` sat in the middle; D65 removed that level.)

```
                      Shenora           net10.0    portable: no Windows reference
                         ↑              (Core · Engine · Modules — the IPC stack is
            ┌────────────┼────────────┐  Shenora.Core.Ipc, a NAMESPACE, not a package)
            │            │            │
   Shenora.Windows  Shenora.Android  Shenora.iOS
   net10.0-windows  net10.0-android  net10.0-ios

   Shenora.Launcher — native, referenced by nothing; a build-time artifact (D50)
```

**There are no optional feature packages — the framework ships as one whole** (D55). `Shenora`
carries media, file operations and archive extraction as namespaces (`Shenora.Modules.Media`,
`Shenora.Engine.Files`, `Shenora.Engine.Compression`) rather than as separate NuGet ids. ⚠ Those
are the names D65 relayered to — a retired PACKAGE id written where a namespace belongs is a `using` an
adopter cannot compile. The
reason they stopped being packages is not size: **a package set is a public statement about what a product IS**, and
`Shenora.Media` + `Shenora.IO` + `Shenora.IO.Compression` on nuget.org read as a shelf of single-domain
libraries. This is a hybrid app framework; those are capabilities it carries. Reference `Shenora`
plus your platform's shell and you have all of it.

**The line is what a CONSUMER experiences, not size** (D53): *making the page host, serve and play what it
was handed* is shell work and lives in `Shenora`; *something only some apps do* earns its own package.
That is why media is in Core and file operations are not — every app that hosts a page can be handed a file
it cannot play; not every app rewrites a directory tree.

**`<video>`, `<audio>` and `<img>` over local files need nothing beyond the shell.** Serving bytes to a
page is resource interception, and that is a SHELL capability (D45): `Shenora` declares
`IWebViewInterceptor` plus a file middleware that does path containment, HTTP ranges and content types,
and each shell implements the contract over its own webview — `WebViewHost.Interceptor` on Windows,
`MobileWebViewInterceptor` on Android and iOS. One route, and the same three lines compile on all three:

```csharp
host.Interceptor.UseFiles(new WebViewFileOptions { AllowedRoots = [libraryDir], Resolve = MyRoute });
```

A file the platform cannot decode simply errors in the element, which is the honest outcome. **Deciding
what to do about that** — probe it, plan it, remux it — is the `Shenora.Modules.Media` namespace, also in
`Shenora`, composed on the same interceptor as a further middleware. An app that only ever serves
ordinary files simply never calls it.

⚠ **One measured platform fact rides on the interceptor, not on media.** Android's webview applies the
`Range` start to whatever body it is handed; WebView2's and iOS's send the body verbatim. So the same
request needs an *unsliced* body on one and a *sliced* body on the other two —
`IWebViewInterceptor.RangeDelivery` reports which, `UseFiles` reads it from the platform so it cannot be
passed in wrong, and a fourth shell cannot compile without declaring its own answer.

**`Shenora.Core.Ipc` is platform-neutral and stays that way.** It is a namespace inside `Shenora`,
which targets `net10.0` and binds to no UI framework at all — the whole transport story (D16) rests on
that: the same envelopes ride WebView2 postMessage on Windows, `HybridWebView` on mobile, and a WebSocket
tomorrow, and a server-side or headless host can dispatch them with no shell anywhere in the graph.
Anything that genuinely needs a window lives one layer up: the UI-thread seam is the portable
`IUiDispatcher` in `Shenora`, implemented once per shell.

App logic that must stay portable references only `Shenora` — `samples/Shenora.Sample.Logic` is
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

### `Shenora` — the host, and portability

The builder, lifetime, module registration, environment and app paths — plus the **platform-neutral
contracts** (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`, `IUiInteraction`)
and the in-process `IEventBus`.

```csharp
var builder = ShenoraApplication.CreateBuilder(args);
builder.Services.AddSingleton<IMyService, MyService>();
using var app = builder.Build();
app.Run();                       // executes the registered runner (UseWindows supplies one)
```

Put your facades in a plain `net10.0` project that references only this package and inject the
contracts. The compiler then enforces portability instead of a document asserting it — if a Windows
type ever creeps into app logic, that project turns red. See `docs/ADOPTION.md` Stage 4.

### `Shenora.Core.Ipc` — the wire and the dispatcher *(a namespace inside `Shenora`, not a package)*

Typed request/response/notification envelopes, a composable middleware pipeline, IPC modules, and
a structured error contract (`code` + parameters, i18n-ready). Transport-neutral by design: the same
envelopes ride postMessage today and a WebSocket or mobile channel tomorrow.

```csharp
public sealed class SettingsModule : ModuleBase
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

services.AddIpcModule<SettingsModule>();
services.UseMessageDispatcher();          // error handler → logging → your middleware → modules
```

**Raw exception text never crosses the wire.** A `ShenoraException` carries the app's own code,
parameters and message; anything else becomes `UNKNOWN_ERROR` plus the exception's type name, with
the detail in the host log. The one sharp edge: a `ShenoraException`'s message crosses **verbatim**
— so never build one from `ex.Message`, which would turn the sanctioned channel into a bypass.

**`IModuleContext` is the route's world: who it is, how it emits, how it starts long work.**
`context.Publish(type, payload?, scope?)` emits on the host bus under the facade's own module — the
default gesture for progress and state, not a wiring exercise, and it can never drift from
`ModuleName` the way a hand-typed literal at every call site can. For work too long to answer inline,
`context.Report(new IpcProgress(40, 100, "steps"))` reports progress on the CURRENT request — no
options record, no second id, nothing to declare. Every request is tracked automatically and the host
stays SILENT for the first 50 ms, so a request that finishes quickly emits nothing at all and only work
that is actually taking a while reaches the page. Pair it with `@shenora/react`'s
`useShenoraRequests()` for a host-backed progress store with no per-feature event wiring, and cancel
with the id you already have.

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
builder.UseWindows(new WindowsHostOptions { MainForm = sp => new MainForm(sp) });
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
> server-backed app whose pages are already on a loopback origin is unaffected. To serve your bundle
> into a session deliberately, set `VirtualHost` + `ResourceProvider` on its `SessionBrowserOptions` —
> `docs/guides/sessions.md` has the both-or-neither rule and the CORS caveat.

Off-screen and auxiliary browser sessions over the same runtime: a bounded LIFO `RenderSessionPool`,
`InteractiveSession` (a human-in-the-loop window over an isolated persistent profile, driven by
**your** driver), and `StreamingSession` (frames out, input in). The kit ships the mechanics and no
scenario — a worked driver example lives in the sample, to copy and edit. **What a session DOES is
published on the app's own `IEventBus`** (`SessionEvents`, scoped by session id — navigation, DOM,
downloads, web messages, renderer failures), and five hooks decide what happens when a page raises a
dialog, an auth challenge, a certificate request, a popup or a permission prompt. Three of those
prevent a wedge that would otherwise stop an off-screen page for good.
**`docs/guides/sessions.md`** is the guide.

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
