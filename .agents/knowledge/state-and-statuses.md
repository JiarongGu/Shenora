---
name: state-and-statuses
applies_when: a neighbouring app asks where Shenora writes on a user's machine, which folders it chooses and which the app must, or how a status it reads is derived — an update's pending state, which resource pack runs, a mission's lane and outcome, whether a launch is the single instance
enforces: the kit writes only under ShenoraPaths' data areas, the paths an app hands it, and CEF's cache; every status below is derived where named, and the kit never compares two version strings itself
---

# What the kit writes on a machine, and how its statuses are derived

**The kit chooses a few folders under `ShenoraPaths.DataDir`; everything else it writes is at a path the
app gives it. The statuses an app reads are computed in the files named below.** Design detail per area:
`docs/design/update.md`, `docs/design/missions-and-files.md`, `docs/design/shells.md`.

## Why

An app shares a machine and a data folder with the kit. A neighbour that guesses a path, or re-derives a
status from the files behind it, diverges the first time the kit's rule moves.

## Where it writes

- **The layout is `ShenoraPaths`** (`src/Shenora/ShenoraPaths.cs`, `ResolveFor`): `RootDir` from
  `--app-root`, then an environment variable, then the parent of an exe inside `libs/` or `lib/`, then the
  base directory; `DataDir` from an environment variable, then `ShenoraPathsOptions.DataDirectory`, then
  `~/Library/Application Support/<CFBundleIdentifier>` in a macOS bundle, then `<root>/data`.
  `DataArea(name)` is `<DataDir>/<name>`, created on first call.
- **Areas the kit chooses:** `journal` and `locks` (`src/Shenora/Engine/Files/FileSystemExtensions.cs`),
  `media` (the media player's cache, `MediaPlayerExtensions.cs`), and `chromium` — the CEF cache, session
  profiles (`default`) and `cef.log` — unless `UserDataFolder` overrides it
  (`src/Shenora.Chromium/ChromiumHostExtensions.cs`, `Host/CefStartup.cs`).
- **Paths the app must give:** the WebView2 user data folder (`WebViewEnvironmentOptions.UserDataFolder`
  is required — `src/Shenora.Windows/WebView/WebViewEnvironment.cs`), the window-state store
  (`WindowStateHostOptions.Store`; `JsonFileWindowStateStore` in `src/Shenora/Core/Shell/WindowState.cs`),
  the update stage root (`UpdateStageOptions.Root`: `staged/`, `ready.json`, `manifest.json` —
  `src/Shenora/Engine/Update/UpdateStage.cs`) and the resource-pack root (`<root>/<name>/<version>/`).
- **No log file of its own** except CEF's; the kit logs through `ILogger`.
- **Single instance** is a per-user mutex `"{app}.instance.{key}"` plus a named pipe
  (`src/Shenora/Core/Shell/SingleInstance.cs`).

## The statuses others read

- **An update's pending state** — `UpdateStage.GetStatus()` → `UpdateStageStatus { Pending, Version,
  StagedAt }`, read from `ready.json`; an unreadable marker is NOT pending. What changes between two
  manifests is `ManifestDiff.Compute` (`src/Shenora/Engine/Update/UpdateManifest.cs`): by path, sha256
  compared case-insensitively, giving added, updated, removed and `DownloadBytes`. **`UpdateManifest.Version`
  is opaque — the kit never parses or orders versions.** `ApplyAsync` reports `UpdateOutcome`.
- **Which resource pack runs** — `ResourcePackJournal.Open(packagedVersion)`
  (`src/Shenora/Engine/Compression/ResourcePackJournal.cs`) → `ResourcePackResult(Version, Kind:
  Packaged|Active|Pending, Attempt, RolledBackFrom)`. The ordering is the comparer the APP supplies; a
  newer packaged version beats pending and active; a pending pack gets one attempt (counted before it is
  served) until `Confirm()`; a throwing comparer falls back to the packaged pack.
- **A mission's lane and outcome** — `src/Shenora/Engine/Missions/`. The global lane `"(global)"` has
  capacity `GlobalLaneCapacity ?? clamp(ProcessorCount − 1, 1, 4)` (`MissionScheduler`); a named lane runs at
  the lower of its own permits and the global capacity. `MissionOutcome` is Completed, Failed, Cancelled or
  Deduplicated, and "succeeded" means Completed or Deduplicated. The default policy runs higher priority
  first, then FIFO (`PriorityMissionPolicy`).
- **Whether this launch is the instance** — `SingleInstanceResult`: `Acquired`, `AlreadyRunning`, or
  `Unverified` (the guard could not decide and the launch starts anyway — fail open). Scope defaults to
  `Paths.RootDir`.
- **What the page sees** — `ShellInfo.capabilities` at the handshake, the request tracker's states
  (`running`, `completed`, `failed`, `cancelled` on `SHENORA.REQUESTS`) and the media player's status; names
  in `docs/reference/wire.md`.

## Not confirmed, and what is absent

- None of the paths above is a fixed name the kit owns: the environment variables are the app's to name
  (`ShenoraPathsOptions.RootEnvironmentVariable` / `DataEnvironmentVariable`, none by default).
- **The Engine areas (Compression, Files, Missions, Update) expose no IPC route and publish no event**
  (searched `src/Shenora/Engine/`): a page sees them only through a module the app writes.
