# Getting started

A **new** app, from nothing to a window with a typed IPC round trip, then onto a phone.

> Bringing an **existing** WinForms + WebView2 app onto the kit instead? That is a different path and it
> is staged so your app ships at every step: **[ADOPTION.md](ADOPTION.md)**.
>
> **This page says HOW.** Every *why* lives in [DECISIONS.md](DECISIONS.md) and is linked, never restated
> (D57 — a third copy of the reasoning goes stale while nobody notices).

Every snippet below is lifted from `samples/Shenora.Sample.Desktop`, `samples/Shenora.Sample.Chromium` and
`samples/Shenora.Sample.Maui`, which the gate compiles. If one stops matching, the sample is right and this page is
wrong.

**On macOS or Linux?** Step 1's `PackageReference` and step 2 are Windows' (WinForms and WebView2);
[2b](#2b-a-window-on-macos-or-linux) is the same window on the kit's Chromium shell. The npm packages of step 1, and
steps 3 and 4, apply unchanged.

---

## 1. Reference the packages

Reference the **leaf** you need; the rest arrive transitively. The full table with target frameworks is
in the [root README](../README.md#packages).

```xml
<PackageReference Include="Shenora.Windows" Version="0.18.0" />   <!-- desktop: pulls in Shenora -->
```

```bash
npm i @shenora/react
npm i -D @shenora/cli      # build-time only, for the device loop (step 4)
```

**There is no optional feature tier.** Media, IO and compression are *namespaces inside* `Shenora`, not
packages you add — the framework ships as one whole (D53/D55). If you find yourself looking for the
former `Shenora.Media` package, it is already referenced — as the `Shenora.Modules.Media` namespace.

---

## 2. A window

`ShenoraApplication.CreateBuilder` → configure → `Build()` → `Run()`. The shape is deliberately ASP.NET's,
including the split between **configuring** services and **using** the built app (D64).

```csharp
[STAThread]
private static void Main(string[] args)
{
    var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
    {
        Args = args,
        ApplicationName = "My App",
    });

    builder.UseWindows(new WindowsHostOptions
    {
        MainForm = sp => sp.GetRequiredService<MainForm>(),
    });

    using var app = builder.Build();
    app.Run();
}
```

- **`[STAThread]` is required, not decorative** — WinForms and every OLE feature (drag-drop, dialogs)
  fail without it, and the failure is a blocking modal rather than a clean exception.
- **`UseWindows` is the only per-platform call.** The framework itself is ON by default: modules,
  dispatcher, event bus and the rest are registered by `Build()` — `Use…` *configures*, it does not
  enable (D64).
- The window's own capabilities — frameless chrome, tray, window-state restore, single instance,
  secondary windows — are `WindowsHostOptions` and are covered in
  [the root README](../README.md#shenorawindows--the-shell-the-page-host-and-extra-browsers).

---

## 2b. A window on macOS or Linux

WinForms and WebView2 are Windows'. On macOS and Linux the window is the kit's own Chromium shell, `UseChromium`
(D81–D84), which runs on Windows too, so one project serves all three. `samples/Shenora.Sample.Chromium` is that
project, and its page is `chromium.html` in `samples/Shenora.Sample.Web`.

**The project.** `Shenora.Chromium` in place of `Shenora.Windows` (it brings `Shenora`), a runtime identifier, and
an assembly named `<App>.App`:

```xml
<PropertyGroup>
  <!-- WinExe: on Windows no console window beside the app; on macOS and Linux the same as Exe. -->
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <!-- The build lays the app out as MyApp, which starts this assembly. -->
  <AssemblyName>MyApp.App</AssemblyName>
  <!-- osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64 or win-arm64: NuGet picks the OS's build of the engine by it. -->
  <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Shenora.Chromium" Version="0.18.0" />
  <!-- The built page, served from beside the app. -->
  <None Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

The sample's project picks the identifier of the machine that builds it, which suits a project built on more than
one OS. The first build fetches the pinned CEF build once per machine and lays the app out. ⚠ The three `arm64`
identifiers are built and packaged but have not yet run on that hardware.

**The window:**

```csharp
builder.UseChromium(new ChromiumHostOptions
{
    ContentRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot"),   // packaged
    DevUrl = "http://localhost:3901",                                    // development
    Window = new ChromiumWindowOptions { Title = "My App", Width = 1000, Height = 760, Path = "index.html" },
    Shell = new ShellInfo
    {
        Name = "chromium",
        Capabilities = [ShellCapability.WindowChrome, ShellCapability.FilePicker],
    },
});
```

- **`UseChromium` in place of `UseWindows`, on the thread that then calls `Run`.** Run from its layout, it starts
  Chromium at once, while the rest of the app is composed (D87).
- **The window is frameless** (`FramelessChrome`), so the page draws its title bar: an element with
  `-webkit-app-region: drag`, and `WindowCommands` from `@shenora/react` behind its buttons. On macOS,
  `NativeCaptionButtons` gives it the system's own buttons instead.
- **`Shell` is what the page is told**: `getBridge().notifyReady()` answers with it, so the page shows what the app
  composed rather than guessing from the OS.

**Run it.** `dotnet build`, then start the app the build laid out:

| | Start | Needs |
|---|---|---|
| macOS | `bin/Debug/net10.0/osx-arm64/bundle/MyApp.app`, or its `Contents/MacOS/MyApp` from a terminal | |
| Linux | `bin/Debug/net10.0/linux-x64/MyApp`, beside `MyApp-helper` | `libnss3` and `libasound2`; to build, `bzip2`, and binutils' `strip`, without which `libcef.so` keeps 1.2 GB of debug information |
| Windows | `bin\Debug\net10.0\win-x64\MyApp.exe` | |

A .NET installed under your home folder, rather than by Microsoft's installer, is found only through `DOTNET_ROOT`,
which Finder and `open` do not pass: `open --env DOTNET_ROOT=$HOME/.dotnet MyApp.app`. On macOS and Linux,
`dotnet publish --self-contained` makes a bundle or folder that needs no .NET at all.

**Development.** With `DOTNET_ENVIRONMENT=Development` in the app's environment, or a `.dev` file beside its
assemblies, the page comes from `DevUrl`, your dev server, and Chromium takes command-line switches such as
`--remote-debugging-port=9333`. The sample's page: `npm run dev:chromium` in `samples/Shenora.Sample.Web` for the
dev server, and `npm run build:chromium` writes the packaged page into the sample's `wwwroot`.

**What differs by OS.** Linux: the tray needs a panel that shows StatusNotifierItems, which GNOME has only with its
AppIndicator extension, and without one closing the main window ends the app; the clipboard is X11's, where what
the app copied lasts while it runs unless a clipboard manager keeps a copy; the window's `WM_CLASS` is `MyApp`,
which a `.desktop` file's `StartupWMClass` names. macOS: the bundle's icon is `ShenoraChromiumBundleIcon`, an
`.icns`, and the build neither signs nor notarizes it, which Gatekeeper needs of a download. ⚠ Both: the app's
data, Chromium's profile among it, lives in `data/` beside the app as it does on Windows, which on macOS is inside
the bundle. Fine while you develop; a signed or installed app moves it (`ChromiumHostOptions.UserDataFolder` for
the profile, `ShenoraApplicationOptions.Paths` for the rest), and a default of each OS's own is open in `TASKS.md`.

**What ships with the engine**, on every OS: the app now redistributes Chromium, whose license and notices are the
app's to ship; its pages play no H.264, AAC or HEVC; and `ShenoraChromiumLocales` trims the locales it lays out.
[ADOPTION's Stage 2 on Chromium](ADOPTION.md#stage-2-on-chromium--when-the-app-ships-its-own-engine) has the
sizes and the files. The rest of `Shenora.Chromium` is in
[the root README](../README.md#shenorachromium--chromium-instead-of-webview2).

---

## 3. A typed IPC round trip

**Host side.** A module owns a name and routes by type:

```csharp
public sealed class SettingsModule : ModuleBase
{
    public override string ModuleName => "SETTINGS";

    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context,
        CancellationToken ct) =>
        request.Type switch
        {
            "GET"  => Task.FromResult<object?>(_settings.Current),
            _      => throw UnknownType(request),
        };
}

services.AddIpcModule<SettingsModule>();
```

**Page side**, through `@shenora/react`. The contract names mirror the C# names exactly — that is a rule,
not a coincidence, and tripwires keep the two halves in step.

**Two things worth knowing before your first route:**

- **Raw exception text never crosses the wire.** A `ShenoraException` carries your own code and
  parameters; anything else becomes `UNKNOWN_ERROR` plus the type name, with the detail in the host log.
  ⚠ Never build one from `ex.Message` — that turns the sanctioned channel into a bypass.
- **Long work needs no extra plumbing.** `context.Report(new IpcProgress(40, 100, "steps"))` reports on
  the *current* request. The host stays silent for the first 50 ms, so a fast request emits nothing at
  all. Pair it with `useShenoraRequests()` on the page.

Adding a route the client will name touches a chain with gates that fail late — the walkthrough is
`/new-ipc-module` in this repo, and [ADOPTION.md Stage 3](ADOPTION.md) for an existing app.

---

## 4. Onto a device

Mobile is the same app logic behind a MAUI shell — [guides/mobile.md](guides/mobile.md) has setup, what
transfers, and the traps (the page ORIGIN one costs a day). The last mile is the CLI:

```bash
npx shenora init                 # writes shenora.deploy.json
npx shenora ios doctor           # can this Mac build, sign and install?
npx shenora ios deploy           # build → sign → verify extensions → install → launch
npx shenora ios log              # your app's own output, off the device
```

**Android is the same four verbs, and they run on WINDOWS** — which is where most .NET Android work
happens, so this half is not a Mac story at all:

```bash
npx shenora android doctor       # dotnet, the android workload, adb, a JDK, devices ready
npx shenora android devices      # including the ones adb calls unauthorized
npx shenora android deploy       # build → install → launch  [--device <serial>]
npx shenora android log          # your app's lines, filtered by PID  [-n <lines>] [--all]
npx shenora android build        # a distributable: .apk, or --aab for Play
```

It exists for the four things that are not `adb`: finding a JDK (Android Studio ships one in `jbr/` and
sets no variable), finding `adb` (Visual Studio's SDK lands in `%LOCALAPPDATA%\Android\Sdk` and exports
nothing), **refusing to guess** between an attached emulator and phone, and reading the log by PID
rather than by tag — which is every line YOUR app wrote, under any tag, excluding a stale instance.

You do **not** own an Xcode project, which is why several `cap` commands have no counterpart here — see
`@shenora/cli`'s own README for the parity table.

- **A free/personal team profile expires after 7 days.** Re-deploy to refresh it.
- **A first install needs the certificate TRUSTED on the phone**: Settings → General → VPN & Device
  Management → your developer account → Trust.
- Network (LAN) pairing works for the whole cycle; reach for USB when a long operation keeps dropping.

---

## Where to go next

| You want | Read |
|---|---|
| One capability, on its own | [guides/](guides/) — missions, file updates, media, mobile |
| To move an existing app across | [ADOPTION.md](ADOPTION.md) |
| What the pieces are, as built | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Why any of it is this way | [DECISIONS.md](DECISIONS.md) |
