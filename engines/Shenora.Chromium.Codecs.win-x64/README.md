# Shenora.Chromium.Codecs.win-x64

CEF 154.0.32 (Chromium 154.0.8037.58) for Windows x64, built with Chromium's proprietary codecs: **H.264, AAC and HEVC**,
which CEF's standard build leaves out. It is the engine's bytes for
[`Shenora.Chromium`](https://www.nuget.org/packages/Shenora.Chromium), the Chromium shell of
[Shenora](https://github.com/JiarongGu/Shenora), the hybrid app framework for .NET + React.

```xml
<ItemGroup>
  <PackageReference Include="Shenora.Chromium" Version="..." />
  <PackageReference Include="Shenora.Chromium.Codecs.win-x64" Version="154.0.32" />
</ItemGroup>
```

In an app with `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, the layout (the launcher, the shim and CEF beside the
app) then comes from this package, with no download at build time. Nothing else changes: the same `ChromiumBrowserProcess`,
the same layout. An app that does not reference it gets CEF's standard engine, which plays none of these codecs.

- **Its version is CEF's**, not the kit's: pair it with a `Shenora.Chromium` that pins the same CEF (0.25.0 pins 154.0.32).
  A rebuild of the same CEF with other flags is a fourth number.
- **Windows x64 only.** An app built for another RuntimeIdentifier is refused with a build error naming the mismatch.
- **HEVC plays through the machine's own decoder** (a GPU, or Windows' HEVC Video Extensions), and a GPU's decoder refuses a
  very small picture: a 128x96 clip failed on a GeForce where 320x240 played.
- **Patents.** H.264, AAC and HEVC are covered by patents licensed through patent pools. **This package comes with no patent
  licence of any kind**; whether an app that ships it needs one is for that app to decide. See `NOTICE.md`.
- `tools/engine/LICENSE.txt` and `tools/engine/CREDITS.html` are CEF's licence and Chromium's third-party notices, which an
  app that ships the engine ships with it.

## Bringing it after the app is installed

The app need not carry it. The Windows shim loads CEF from an `engine\` folder beside the app's exe when that holds a
`libcef.dll`, and otherwise from beside the exe, so anything that runs **before the app starts** can bring the codecs: an
installer, a native starter, the kit's staged updater (a manifest entry is a path relative to the install root, such as
`engine/libcef.dll`). Merge this package's `tools/engine/Release/*` and `tools/engine/Resources/*` into `<app>\engine\`
(keep `locales\`), and the next launch plays them; delete `engine\` to go back. Nothing in the app or the kit changes.

- It takes effect at the **next launch**: a running app has `libcef.dll` loaded and cannot swap it.
- The install folder must be writable by whatever brings it, and the app's own `bootstrap.exe` and `chrome_elf.dll` stay: use
  an engine of the **same CEF version** the app was laid out with (154.0.32).
- `node devtools/dev.mjs chromium-codecs --later <the package's tools/engine folder>` is that, end to end, in the Shenora
  repository: a sample laid out on CEF's standard engine fails the codecs, and plays them after the merge.

The check that an engine plays them is `node devtools/dev.mjs chromium-codecs` in the Shenora repository.
