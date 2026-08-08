# @shenora/cli

Get a [Shenora](https://github.com/JiarongGu/Shenora) app onto a real iPhone — **without owning an Xcode
project**.

```bash
npm i -D @shenora/cli

npx shenora init            # write shenora.deploy.json
npx shenora ios doctor      # is this Mac able to build, sign and install?
npx shenora ios deploy      # build → sign → verify extensions → install → launch
npx shenora ios log         # the app's own output, off the device
```

```json
{
  "project": "src/MyApp/MyApp.csproj",
  "tfm": "net10.0-ios",
  "bundleId": "com.example.myapp",
  "team": "ABCDE12345"
}
```

## Why this exists

A hybrid framework's real measure is *how little native code an adopting app has to write* — and the
device loop is part of that. Reaching a phone normally means an Xcode project you did not want, plus a
handful of failures you only learn by hitting them. Each check here is one this kit hit on real hardware:

- **`set -o pipefail` on every pipeline.** Without it a piped command reports `tail`'s status, which is
  always 0 — so a REJECTED install reports success, the launch runs against an app that was never
  installed, and the tool finishes by printing "running on the device".
- **App extensions are verified BEFORE install.** An extension is provisioned separately from its
  container and will not launch without its own entitlements and embedded profile. One that cannot launch
  installs perfectly happily and then does nothing: a Live Activity shows as an empty capsule while every
  ActivityKit call reports success. **A simulator cannot catch this** — it does not enforce code signing.
- **It refuses to guess between two connected devices.** Silently picking the first one deploys to the
  wrong phone and you debug the wrong build.
- **The log filters before it tails.** A process-wide predicate is ~99% platform chatter, so tailing the
  raw stream shows a screen of noise with none of your app's lines — which looks exactly like a broken
  log sink.

## Requirements, and the two that cannot be automated

macOS with Xcode, the `maui-ios` workload, and an *Apple Development* signing identity — `shenora ios
doctor` checks all of it and names what is missing.

Two things are yours and no tool can do them for you:

1. **A free/personal team profile expires after 7 days.** Re-deploy to refresh it.
2. **A first install needs the certificate TRUSTED on the phone**: Settings → General → VPN & Device
   Management → your developer account → Trust.

## Scope, next to Capacitor

Shenora's premise is that you do **not** own a native project, so several of Capacitor's commands have no
counterpart here — that is a difference in design, not a missing feature.

| `cap` | `shenora` | |
|---|---|---|
| `init`, `doctor`, `copy`, `sync` | same names | ✅ |
| `run` (`--list`, `--target`) | `ios deploy --simulator/--device`, `ios devices`, `ios simulators` | ✅ |
| `build` (a distributable) | — | **not yet** — `dotnet publish` by hand for now |
| `add`, `open`, `ls`, `migrate` | — | **N/A by design**: they manage an Xcode/Android Studio project you edit. There isn't one |

Android is not here yet either: `adb` already does most of it, so the gap is smaller. It will land if
adopters ask (this kit grows by harvest, not by symmetry).

This CLI does not build your web bundle or run your tests — it is the last mile only.
