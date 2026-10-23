![Shenora](https://raw.githubusercontent.com/JiarongGu/Shenora/main/docs/assets/shenora-icon.png)

# Shenora (神阙)

[![NuGet](https://img.shields.io/nuget/v/Shenora.svg?label=nuget)](https://www.nuget.org/packages/Shenora)
[![npm](https://img.shields.io/npm/v/@shenora/react.svg?label=npm)](https://www.npmjs.com/package/@shenora/react)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/JiarongGu/Shenora/blob/main/LICENSE)

**A hybrid app framework for .NET + React, on Windows, macOS, Linux, Android and iOS.** Your interface is a React
page and your app's logic is .NET. Shenora is everything between them: the shell that hosts the page, typed IPC
across the seam, windows, native services and the modules that reach the platform. An app ships its domain logic
and its screens, and nothing else.

> 神阙 (shénquè), "the spirit's gate": the classical Chinese name for the navel, where the body was first joined
> to what fed it.

**.NET does the platform work, React does the interface, and the kit owns the seam between them.** It is a
framework, not a media library or a file library: those are capabilities it carries. Its sibling
[Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) is the AI brain, and the two never depend on each other.

## Why Shenora

- **Native .NET, not a JavaScript bridge.** Capacitor and Electron give you a webview and a bridge, so their
  ceiling is the web platform plus plugins. Here it is .NET's: real threads, real handles, the platform SDKs,
  background execution. The kit's job is the translation layer, which is to say what .NET can do and React
  cannot.
- **One typed IPC on every shell.** Correlated requests, batched events and structured errors, in the same
  envelope over WebView2, the kit's Chromium shell and MAUI's `HybridWebView`. The host tells the page what it
  offers in the handshake, so one web bundle serves every shell.
- **Your choice of engine on the desktop.** WebView2 in a WinForms shell for a small Windows app, or the kit's
  own Chromium shell (CEF) for one engine on Windows, macOS and Linux, with frameless windows, a native splash, a
  tray, file dialogs and one instance per install.
- **Media the webview cannot play.** Probe, plan, remux, or convert while it plays. On a phone the shell can draw
  the picture itself, under a transparent region the page leaves, so a film the webview refuses plays with no
  transcode and the page keeps every control; when the app goes to the background, a native player carries on.
- **The desktop services an app needs.** Window state that survives a DPI change, single instance that hands a
  later launch's arguments to the running app, secondary windows, drag and drop with real paths, the clipboard,
  and extra browsers: off-screen, interactive and streamed.
- **Safe file changes and updates.** A journalled file-update queue with path locks, a self-updater that applies
  only a release whose every file matched its hash, and a native launcher that applies it before your app starts
  and can show a startup screen until your first window.
- **Logic that stays portable.** App logic references only `Shenora` (`net10.0`) and injects the contracts it
  needs; a sample project turns red the moment a Windows type reaches it.
- **Headless.** No UI component library and no design system: you bring your own React.

## Quickstart

The kit's Chromium shell, which is one project on Windows, macOS and Linux. On Windows alone, `Shenora.Windows`
and `UseWindows` give you the same window in WinForms and WebView2
([getting started, step 2](https://github.com/JiarongGu/Shenora/blob/main/docs/getting-started.md#2-a-window)).

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <AssemblyName>MyApp.App</AssemblyName>
  <RootNamespace>MyApp</RootNamespace>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Shenora.Chromium" Version="0.23.2" />
  <EmbeddedResource Include="wwwroot\**" WithCulture="false" />
</ItemGroup>
```

The build lays the app out as `MyApp`, which starts `MyApp.App`; the runtime identifier is the OS you build for
(`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`); `wwwroot` is your built React page.

```csharp
using Shenora;
using Shenora.Chromium;
using Shenora.Core.Ipc;
using Shenora.Core.WebView;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { Args = args, ApplicationName = "My App" });
        builder.UseChromium(new ChromiumHostOptions
        {
            ResourceProvider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = typeof(Program).Assembly,
                ResourcePrefix = "MyApp.wwwroot",
            }),
            Window = new ChromiumWindowOptions
            {
                Title = "My App", Width = 1000, Height = 760, Path = "index.html",
                FramelessChrome = false,   // the OS's title bar; frameless, the default, has the page draw its own
            },
        });
        builder.Services.AddIpcModule<GreetingModule>();

        using var app = builder.Build();
        app.Run();
    }
}

sealed class GreetingModule : ModuleBase
{
    public override string ModuleName => "GREETING";

    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken ct) =>
        request.Type switch
        {
            "HELLO" => Task.FromResult<object?>($"Hello from .NET {Environment.Version}"),
            _ => throw UnknownType(request),
        };
}
```

```ts
// npm i @shenora/react
import { getBridge } from '@shenora/react';

await getBridge().notifyReady();                                   // the host's events flow from here
const greeting = await getBridge().invoke<string>('GREETING', 'HELLO');
```

The first build fetches the pinned CEF build once per machine and lays the app out. A failed call rejects with a
`ShenoraError`: a `ShenoraException`'s own code, parameters and message, or for any other exception
`UNKNOWN_ERROR` and its type name, never its text.
[Getting started](https://github.com/JiarongGu/Shenora/blob/main/docs/getting-started.md) goes on to the dev
server, a splash, window state, and onto a phone.

## What's in the box

| You want | Reach for | Read |
|---|---|---|
| A window around a React page, on Windows | `Shenora.Windows`, `UseWindows` (WinForms + WebView2) | [getting started, 2](https://github.com/JiarongGu/Shenora/blob/main/docs/getting-started.md#2-a-window) |
| One desktop app for Windows, macOS and Linux | `Shenora.Chromium`, `UseChromium` | [getting started, 2b](https://github.com/JiarongGu/Shenora/blob/main/docs/getting-started.md#2b-a-window-on-macos-or-linux) |
| The same app on a phone | `Shenora.Android`, `Shenora.iOS` over MAUI's `HybridWebView` | [mobile](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/mobile.md) |
| Typed calls and events between page and host | `ModuleBase`, `AddIpcModule<T>()`, `getBridge()`, `useShenoraEvent` | [the IPC stack](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md#shenoracoreipc--the-wire-and-the-dispatcher-a-namespace-inside-shenora-not-a-package) |
| Progress and cancellation for long work | `context.Report(…)`, `useShenoraRequests()` | [the IPC stack](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md#shenoracoreipc--the-wire-and-the-dispatcher-a-namespace-inside-shenora-not-a-package) |
| Windows, tray, dialogs, clipboard, single instance | `WindowsHostOptions`, `ChromiumHostOptions`, `IFileDialogs`, `IClipboardService` | [the Windows shell](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md#shenorawindows--the-shell-the-page-host-and-extra-browsers), [the Chromium shell](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md#shenorachromium--chromium-instead-of-webview2) |
| Local files in `<video>`, `<audio>` and `<img>` | `IWebViewInterceptor`, `UseFiles(…)` | [serving files](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md#how-the-set-is-shaped) |
| A file the webview cannot decode | `Shenora.Modules.Media`, `useMediaSurface`, `useMediaTransport` | [media](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/media.md) |
| Background work with claims, lanes and a durable queue | `IMissionScheduler` | [missions](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/missions.md) |
| Safe file changes and self-update | `Shenora.Engine.Files`, `UpdateStage`, `Shenora.Launcher` | [file updates](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/file-updates.md) |
| Extra browsers: off-screen, interactive, streamed | `RenderSessionPool`, `InteractiveSession`, `StreamingSession` | [sessions](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/sessions.md) |
| A build onto a simulator, an iPhone or an Android device | `npx shenora ios deploy`, `npx shenora android deploy` | [`@shenora/cli`](https://github.com/JiarongGu/Shenora/blob/main/src/Shenora.Cli/README.md) |

## Packages

Versioned in lockstep. Reference **one shell** for the platform you build for and it brings `Shenora`;
[the packages, one by one](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md) has how the set is
shaped and enough of each to get it working.

| Package | Registry | Target framework | In one line |
|---|---|---|---|
| `Shenora` | NuGet | `net10.0` | The application host and the platform-neutral contracts your logic compiles against, plus the capabilities that are shell work rather than extras: media (`Shenora.Modules.Media`), file operations and the self-updater (`Shenora.Engine.Files`, `Shenora.Engine.Update`), archive extraction (`Shenora.Engine.Compression`), the mission scheduler (`Shenora.Engine.Missions`) and the extra browsers (`Shenora.Core.Sessions`). |
| `Shenora.Windows` | NuGet | `net10.0-windows` **or** `net10.0-windows10.0.17763.0` | The Windows shell: bootstrap, windows, tray, dialogs, single instance, WebView2 hosting and its IPC bridge, and the extra browsers in WebView2. The versioned TFM adds Windows' media controls and a Media Foundation player. |
| `Shenora.Chromium` | NuGet | `net10.0` (Windows, macOS and Linux, x64 and arm64) | Chromium through CEF: a complete shell of its own (`UseChromium`) on every desktop OS, and the `ChromiumView` control `Shenora.Windows` hosts. **CEF is not in the package**: your app's build fetches the pinned CEF build, so reference it from the app's own project. |
| `Shenora.Android` | NuGet | `net10.0-android` | The Android shell: the same IPC envelope over MAUI's `HybridWebView`. |
| `Shenora.iOS` | NuGet | `net10.0-ios` | The iOS shell, sharing its MAUI half with `Shenora.Android`, plus AVPlayer, Now Playing and Live Activities. |
| `Shenora.Launcher` | NuGet | native (`win-x64`, `linux-x64`) | The native launcher that applies a staged update before your app starts, for a framework-dependent app, and can show a startup screen until its first window. A self-contained app needs none of it. |
| `@shenora/react` | npm | ES2022 / ESM · **React ≥ 18** | The page half: bridge, event bus, stores and hooks. |
| `@shenora/cli` | npm | Node 20+ | **Build-time only, a `devDependency`.** The `shenora` binary that takes a built app onto a simulator, an iPhone or an Android device, with no Xcode project of your own. |

**There are no optional feature packages.** Media, files, missions, updates and archives are namespaces inside
`Shenora`, so a reference to `Shenora` plus your platform's shell is all of it.

## Status

<!-- version-indicator: the **vX.Y.Z below is AUTO-SYNCED from src/Directory.Build.props
     <VersionPrefix> by `node devtools/dev.mjs pack` / `doctor --fix`. Don't hand-edit the
     version here — bump VersionPrefix; the headline follows. -->
**v0.23.2 — pre-release, stabilising toward 1.0.** Every public and protected member is documented and gated by
API-surface baselines. The WinForms shell, WebView2 hosting, the IPC stack, the native desktop services and the
extra browsers are extracted from in-house applications that had already shipped them; the Chromium shell, the
mobile shells and the segment media tier were built in this repository.

Where it has run: Windows; macOS (x64) and Linux (x64, under WSL and Xvfb) through the Chromium shell; an Android
device (IPC, events, the file picker, the mission scheduler); and an iPhone 17 Pro (also media playback, the save
picker, audio conversion and Live Activities). What has not yet run (the `arm64` desktop builds, the published
package's Linux helper, a real GNOME or KDE session) is listed in
[`TASKS.md`](https://github.com/JiarongGu/Shenora/blob/main/TASKS.md).

**This page describes the working tree, not only the newest package**: anything that has not shipped yet is
under `## Unreleased` in [`CHANGELOG.md`](https://github.com/JiarongGu/Shenora/blob/main/CHANGELOG.md), so check
there before assuming a member is in the version you installed.

> **Versioning.** Every public change is SemVer surface, and until 1.0 a minor release may break one: it is
> always listed under that release's `### Breaking` heading in `CHANGELOG.md`, with what to change. 1.0 is a
> deliberate freeze, not yet cut.

> **The segment (streaming) media tier had no application to prove it before it shipped.** It is checked against a
> corpus of sources muxed by other tools (MP4Box, Bento4, mkvmerge, Apple), by a decoder, a real browser
> `MediaSource` and a real iPhone. On Android it has run on the emulator, not yet on a phone.

## Documentation

- [**Getting started**](https://github.com/JiarongGu/Shenora/blob/main/docs/getting-started.md): a new app, from
  nothing to a window with a typed IPC round trip, then onto a phone.
- [**The packages, one by one**](https://github.com/JiarongGu/Shenora/blob/main/docs/packages.md): how the set is
  shaped, and enough of each package to get it working.
- [**Guides**](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/README.md), one capability each:
  [media](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/media.md),
  [mobile](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/mobile.md),
  [missions](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/missions.md),
  [file updates](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/file-updates.md) and
  [sessions](https://github.com/JiarongGu/Shenora/blob/main/docs/guides/sessions.md).
- [**Adoption**](https://github.com/JiarongGu/Shenora/blob/main/docs/ADOPTION.md): bringing an existing
  WinForms + WebView2 app across in stages, so it ships at every step.
- [The wire reference](https://github.com/JiarongGu/Shenora/blob/main/docs/reference/wire.md): the module names,
  routes, events, error codes and capabilities a page types, generated from the source.
- [`@shenora/react`](https://github.com/JiarongGu/Shenora/blob/main/src/Shenora.React/README.md) and
  [`@shenora/cli`](https://github.com/JiarongGu/Shenora/blob/main/src/Shenora.Cli/README.md), each package's own
  README.
- [The decisions](https://github.com/JiarongGu/Shenora/blob/main/docs/DECISIONS.md) behind the design, and
  [the changelog](https://github.com/JiarongGu/Shenora/blob/main/CHANGELOG.md), breaking changes called out.
- [The samples](https://github.com/JiarongGu/Shenora/tree/main/samples): a WinForms app, a Chromium app for all
  three desktop OSes, a MAUI app, and the portable logic all three run.

## For AI coding agents

Shenora publishes [`llms.txt`](https://raw.githubusercontent.com/JiarongGu/Shenora/main/llms.txt), an index of
these docs written for language models, and a
[`context7.json`](https://github.com/JiarongGu/Shenora/blob/main/context7.json) telling the
[Context7](https://context7.com) documentation index what to serve. The rules a coding agent most often gets wrong
when it writes an app on Shenora:

<!-- agent-rules:begin — llms.txt and context7.json's `rules` carry these same lines; doc-drift holds the three equal (D98) -->
- Reference ONE shell package and it brings `Shenora`: `Shenora.Windows` (WinForms + WebView2, Windows only), `Shenora.Chromium` (`UseChromium`, on Windows, macOS and Linux), `Shenora.Android` or `Shenora.iOS`. The page imports `@shenora/react`.
- Media, files, missions, updates and archives are namespaces inside `Shenora` (`Shenora.Modules.Media`, `Shenora.Engine.Files`, `.Missions`, `.Update`, `.Compression`), never packages of their own.
- Retired package ids, never to reference: `Shenora.Core`, `Shenora.Ipc`, `Shenora.Media`, `Shenora.IO`, `Shenora.IO.Compression`, `Shenora.WinForms`, `Shenora.WebView2`, `Shenora.WebView2.Sessions`. The IPC types are in namespace `Shenora.Core.Ipc`.
- Compose with `ShenoraApplication.CreateBuilder(…)`, one shell call (`UseWindows`, `UseChromium`, `UseAndroid` or `UseIOS`), then `Build()`. A desktop app calls `Run()` from an `[STAThread]` `Main`; a phone app calls `Start()`/`Stop()` from its lifecycle.
- `Build()` registers the framework itself (the dispatcher, the event bus, the kit's modules, the mission scheduler); an engine `Use…` call such as `UseMissions` changes what it registers rather than enabling it.
- A host route is a class derived from `ModuleBase` that names its `ModuleName` and switches on `request.Type`, registered with `services.AddIpcModule<T>()`.
- The page calls a route with `getBridge().invoke(module, type, { payload })`: the payload goes in the options object, and the module and type strings must match the host's exactly.
- For an error the page should show, throw `ShenoraException`: its code, parameters and message cross verbatim, so never build one from `ex.Message`. Any other arrives as `UNKNOWN_ERROR` with only its type name, a cancellation as `OPERATION_CANCELLED`.
- Keep `invoke` for quick calls: a route's synchronous part runs on the shell's UI thread. Start slow work with `post`, report with `context.Report(…)` and `context.Publish(…)`, and read it with `useShenoraEvent` or `useShenoraRequests()`.
- Call `getBridge().notifyReady()` once at page start: the host holds its events until then. It resolves to the `ShellInfo` the app declared (the `Shell` option of its shell or IPC bridge), or `undefined` when it declared none.
- Show a feature when `ShellInfo.capabilities` names it, never by sniffing the OS or the user agent.
- App logic for every shell goes in a plain `net10.0` project that references only `Shenora` and injects the contracts (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`); a WinForms, WebView2 or MAUI type there breaks the other shells.
<!-- agent-rules:end -->

## Contributing

Issues and pull requests are welcome. The working conventions are
[`CLAUDE.md`](https://github.com/JiarongGu/Shenora/blob/main/CLAUDE.md), the documentation map is
[`docs/README.md`](https://github.com/JiarongGu/Shenora/blob/main/docs/README.md), and
`node devtools/dev.mjs verify` is the "am I done?" check every change passes (build, tests, type checks and the
doc gates). Running `node devtools/dev.mjs` with no argument prints every verb. Requirements: the .NET 10 SDK,
Node 22+, and a JDK for the Android head. Outside the maintainer's machine, set
`SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1`: the leak scan fails closed without the private pattern list.

## License

[MIT](https://github.com/JiarongGu/Shenora/blob/main/LICENSE) © Jiarong Gu.
