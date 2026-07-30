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
**v0.1.0 — pre-release, core host + IPC + native services + auxiliary browser sessions extracted.** The application builder,
WinForms host, WebView2 hosting, the full typed IPC stack (envelopes, middleware dispatcher,
scoped-container router, event bus, postMessage transport, `@shenora/react` client), and the
native desktop surface (frameless chrome + frontend window commands, STA dialogs, shell/clipboard,
drag-drop zones, secondary windows, tray), and the auxiliary browser sessions (offscreen render
pool, login windows, co-browse streaming) are extracted from the proven in-house applications and
verified end-to-end against the sample app (see `docs/ROADMAP.md`). Not yet published to NuGet/npm.

## Packages

| Package | Registry | What it gives you |
|---|---|---|
| `Shenora.Core` | NuGet | Application host + builder + lifetime, module registration (`IShenoraModule`), environment (dev/prod), app paths, options, event bus. Depends on Microsoft.Extensions DI (implementation — the builder needs `BuildServiceProvider`, D17) + logging abstractions. |
| `Shenora.Ipc` | NuGet | Typed request/response/notification envelopes, composable middleware dispatcher, structured error contract (`code` + parameters, i18n-ready), `System.Text.Json` defaults. Transport-neutral. |
| `Shenora.WebView2` | NuGet | WebView2 hosting: environment prewarm, dev-server vs packaged-frontend loading (embedded resources / virtual host), navigation + new-window + download + permission + process-failure handling, postMessage bridge with batched event push. |
| `Shenora.WebView2.Sessions` | NuGet | Auxiliary off-screen browser sessions over the same runtime: the one browser-configuration path, a bounded LIFO render-session pool, per-provider/per-account login windows (clear-on-logout), and co-browse streaming (CDP screencast out / input dispatch back). Layers on `Shenora.WebView2`. |
| `Shenora.WinForms` | NuGet | The native shell: bootstrapper with global exception handling, window-state persistence (DPI-correct), single-instance guard, secondary windows + tray, frameless-chrome form, splash panel, STA file dialogs, clipboard/shell services. (Drag-drop overlays live in `Shenora.WebView2` — they need the WebView2 control.) |
| `@shenora/react` | npm | The frontend bridge: correlated `invoke` + `notifyReady`/`dispose` (`ShenoraBridge`), an event bus you `subscribe` to, typed module services (`send` on `BaseModuleService`), React hooks (`useShenora`, `useShenoraEvent`, `useShenoraQuery`), drop-zone hook, window commands, a browser fallback for pure-UI development. |

Two consumption profiles are supported: **desktop-only** (full postMessage IPC) and
**server-backed** (the app runs its own in-process HTTP server shared with mobile/LAN clients;
Shenora provides the shell and an optional one-way event fast-path).

Shenora is deliberately **headless**: it depends on no UI component library — applications bring
their own design system on top of the shell, bridge, hooks, and behaviors. The IPC contract is
transport-neutral, so the same application logic can later run in mobile shells (Capacitor-style)
speaking the same envelope. Planned areas beyond the current packages: server-hosting helpers
(`Shenora.Hosting.AspNetCore`) and a mobile transport adapter — see `docs/ROADMAP.md`.

## Ergonomics (as built — the sample app is the full reference)

```csharp
var builder = ShenoraApplication.CreateBuilder(args);
builder.Services.AddModuleFacade<SettingsFacade>();               // your typed IPC module
builder.Services.AddMessageDispatcher();                          // error handler → middleware → facades
builder.PrewarmWebView2(app => new WebViewEnvironmentOptions { /* … */ });
builder.UseWinForms(new WinFormsHostOptions { MainForm = sp => new MainForm(sp) });
using var app = builder.Build();
app.Run();
```

```tsx
const settings = await getBridge().invoke<Settings>('SETTINGS', 'GET');
useShenoraEvent<JobProgress>('JOBS', 'PROGRESS', p => setProgress(p.percent));
```

## Dev loop

```
node devtools/dev.mjs build     # dotnet build + npm build (react package)
node devtools/dev.mjs test      # dotnet test + vitest
node devtools/dev.mjs verify    # build · test · leak scan · knowledge check — the "am I done?" gate
node devtools/dev.mjs pack      # nupkgs + npm tarball into publish/packages (lockstep version)
node devtools/dev.mjs doctor    # version/readme drift check (--fix to sync)
```

Requirements: .NET 10 SDK, Node 22+. See `devtools/README.md` for the full command set and
`docs/README.md` for the documentation map.

## License

MIT — see [LICENSE](LICENSE).
