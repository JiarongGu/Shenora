---
name: what-shenora-ships
applies_when: a neighbouring app asks which Shenora package, namespace or entry point it needs, what Build() gives it without asking, which version to reference, or whether a change belongs in Shenora rather than in the app
enforces: one Shenora package plus one shell per platform (and the Chromium engine), versioned in lockstep from VersionPrefix; capabilities are folders, never packages; the public surface is pinned by the API baselines
---

# What Shenora ships, and where each part is

**An app references `Shenora`, the ONE shell package for its platform, and optionally the `Shenora.Chromium`
engine and the native `Shenora.Launcher`; its page uses `@shenora/react`. Every package carries the same
version. Everything below is confirmed in the code at the path named, unless marked otherwise.**

## Why

A neighbour adopting the kit needs to know what to reference and what it gets for free, and it cannot see
that from its own tree. Usage, with code, is in `docs/packages.md` and `docs/getting-started.md`; bringing an
existing WinForms + WebView2 app over is `docs/ADOPTION.md`. This document is the map of ownership behind them.

## The packages

| Package | Targets | Holds | Project |
|---|---|---|---|
| `Shenora` | `net10.0` | `Shenora.Core.*` (Events · Ipc · Sessions · Shell · WebView), `Shenora.Engine.*` (Compression · Files · Missions · Update), `Shenora.Modules.*` (Clipboard · FileDialog · Media · Platform · Requests), the application builder | `src/Shenora/Shenora.csproj` |
| `Shenora.Windows` | `net10.0-windows`, `net10.0-windows10.0.17763.0` | the WinForms + WebView2 shell | `src/Shenora.Windows/Shenora.Windows.csproj` (references `Shenora` and `Shenora.Chromium`) |
| `Shenora.Chromium` | `net10.0`, RIDs win/osx/linux × x64/arm64 | the CEF engine (macOS, Linux, and beside WinForms); CEF is fetched at the app's build, pinned in `cef.json` | `src/Shenora.Chromium/Shenora.Chromium.csproj` |
| `Shenora.Android` / `Shenora.iOS` | `net10.0-android` (min 21) / `net10.0-ios` (min 15) | the MAUI shells, one shared source tree `src/Shenora.Mobile/` (not a package) | `src/Shenora.Android/`, `src/Shenora.iOS/`, `src/Shenora.Mobile/Shenora.Mobile.props` |
| `Shenora.Launcher` | native, per RID | the C++ launcher; no managed surface | `src/Shenora.Launcher/Shenora.Launcher.csproj` |
| `@shenora/react` (npm) | ESM, peer `react >=18` | the page's bridge, typed clients and hooks | `src/Shenora.React/package.json` |
| `@shenora/cli` (npm) | node ≥20, bin `shenora` | build-time deploy to devices, reading `shenora.deploy.json` | `src/Shenora.Cli/package.json`, `src/Shenora.Cli/src/config.ts` |

- **No feature package exists** (D53/D55/D65): a capability is a folder in `Shenora`. An engine is the one
  package boundary (D81). `Shenora.Ipc` is retired as a package id and as a namespace.
- **Shenora depends on no repository of this workspace** — no package reference, client or address names
  one (searched every csproj and `package.json`). Its runtime dependencies are Microsoft's:
  `Microsoft.Extensions.DependencyInjection`, `…Logging.Abstractions`, `Microsoft.Web.WebView2`,
  `Microsoft.Maui.Controls` (`src/Directory.Packages.props`).

## The version, and what counts as a break

- **One version for every package**: `<VersionPrefix>` in `src/Directory.Build.props`, read by
  `readVersion()` in `devtools/project.config.mjs`; `dev.mjs doctor` fails when an npm `package.json` or
  the README disagrees. Only the release workflow (`.github/workflows/release.yml`) moves it.
- **The public surface is pinned**: `tests/Shenora.Tests/Api/Baselines/*.txt` (reflected, gated by
  `ApiSurfaceTests`) and `tests/Shenora.Tests/Api/MetadataBaselines/*.txt` (Android, iOS, Windows; gated by
  `MetadataSurfaceTests`). A change to either file is a surface change.
- **Breaks are announced** under `### Breaking` in `CHANGELOG.md`; a type's old and new full names across a
  namespace move are in `docs/reference/namespace-moves.md`. Before 1.0 a documented break may ship in a
  MINOR release (`CHANGELOG.md`'s preamble; 0.22.0 and 0.24.0 both carry one).

## Entry points an app calls

- `ShenoraApplication.CreateBuilder(args)` → `ShenoraApplicationBuilder` (`Services`, `Paths`, `OnStarting`,
  `OnStopping`, `Build()` once) → `Run()`, or `Start()`/`Stop()` (`src/Shenora/ShenoraApplication.cs`,
  `ShenoraApplicationBuilder.cs`).
- **`Build()` registers by default**, after the app's own registrations and only where absent: the event
  bus, `UseMissions()`, `UseFileSystem()`, `UseMediaPlayer()` (`SHENORA.MEDIA`), `UseRequests()`
  (`SHENORA.REQUESTS`) and the message dispatcher over every registered `IIpcModule`
  (`ShenoraApplicationBuilder.Build`).
- **The shell**: `UseWindows(WindowsHostOptions)` (`src/Shenora.Windows/Shell/WindowsHostExtensions.cs`),
  `UseChromium(ChromiumHostOptions)` (`src/Shenora.Chromium/ChromiumHostExtensions.cs`), `UseAndroid` /
  `UseIOS` (`src/Shenora.Mobile/Hosting/MobileHostExtensions.cs` — no runner; the app calls `Start`/`Stop`),
  `UseHeadless` (`src/Shenora/HeadlessRunner.cs`).
- **Opt-in routes**: `AddShenoraClipboard`, `AddShenoraColorScheme`, `AddShenoraWindowOrientation`,
  `AddShenoraBackNavigation`, `AddShenoraAppLifecycle`, `AddShenoraMedia` (under `src/Shenora/Modules/`);
  an app's own module: `AddIpcModule<T>()` (`src/Shenora/Core/Ipc/IpcServiceCollectionExtensions.cs`).

## Not confirmed

- **A "server-backed" consumption profile** (an app serving its page and IPC over its own in-process HTTP,
  desktop and mobile alike) is described in `docs/DECISIONS.md` (around D10) and in the old brief. **The kit
  ships no HTTP server for IPC**: its only listeners are the CEF debugging relay
  (`src/Shenora.Chromium/Host/CdpRelay.cs`) and the CEF browser process. Whether that profile is still a
  supported way to consume the kit is settled in `docs/DECISIONS.md`, not in the code.
