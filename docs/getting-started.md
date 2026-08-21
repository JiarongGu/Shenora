# Getting started

A **new** app, from nothing to a window with a typed IPC round trip, then onto a phone.

> Bringing an **existing** WinForms + WebView2 app onto the kit instead? That is a different path and it
> is staged so your app ships at every step: **[ADOPTION.md](ADOPTION.md)**.
>
> **This page says HOW.** Every *why* lives in [DECISIONS.md](DECISIONS.md) and is linked, never restated
> (D57 — a third copy of the reasoning goes stale while nobody notices).

Every snippet below is lifted from `samples/Shenora.Sample.Desktop` and `samples/Shenora.Sample.Maui`,
which the gate compiles and runs. If one stops matching, the sample is right and this page is wrong.

---

## 1. Reference the packages

Reference the **leaf** you need; the rest arrive transitively. The full table with target frameworks is
in the [root README](../README.md#packages).

```xml
<PackageReference Include="Shenora.Windows" Version="0.13.0" />   <!-- desktop: pulls in Shenora -->
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
