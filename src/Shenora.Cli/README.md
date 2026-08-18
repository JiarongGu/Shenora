# @shenora/cli

Get a [Shenora](https://github.com/JiarongGu/Shenora) app onto a real iPhone — **without owning an Xcode
project**.

```bash
npm i -D @shenora/cli

npx shenora init            # write shenora.deploy.json
npx shenora ios doctor      # is this Mac able to build, sign and install?
npx shenora ios deploy      # build → sign → verify extensions → install → launch
npx shenora ios log         # the app's own output, off the device
npx shenora ios build       # a distributable: Release publish → .ipa
```

```json
{
  "project": "src/MyApp/MyApp.csproj",
  "iosTfm": "net10.0-ios",
  "androidTfm": "net10.0-android",
  "bundleId": "com.example.myapp",
  "team": "ABCDE12345"
}
```

⚠ **One TFM per platform.** `iosTfm` and `androidTfm` name the two heads a MAUI app has. The older
unqualified `tfm` still works and is read as the iOS one — which is exactly how it bites, so a value
naming the other platform is now refused by name instead of failing minutes later inside the SDK.

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

## The Mac can be somewhere else

Most .NET developers shipping to iOS work on Windows and have a Mac on the network rather than under the
desk. Every `ios` verb takes `--host`:

```bash
npx shenora ios doctor --host you@mac.local     # or: set SHENORA_IOS_HOST once
npx shenora ios push                            # send this tree over, uncommitted edits included
npx shenora ios deploy --simulator
```

`push` sends what git would list as source — tracked files plus anything not ignored — so `bin/`, `obj/`
and `node_modules` stay here. **Uncommitted edits travel**, deliberately: the obvious implementation is
`git push`, and it is the wrong one for a dev loop, because the Mac would build HEAD and the fix you just
made would never arrive. It adds and overwrites; it does not delete.

It needs Remote Login on (System Settings → General → Sharing) and your key in the Mac's
`~/.ssh/authorized_keys`. When it will not connect, `doctor` says **which** of the six unrelated causes it
is — an asleep Mac, Remote Login off, a key never authorised, an `.local` name that does not resolve, ssh's
auth-retry budget, or no ssh client here — because "cannot connect" sends you round all six.

⚠ **A device build needs the Mac logged in at its screen.** `codesign` cannot reach a login-keychain key
from an ssh session — an ssh login is a different *audit session*, so signing dies with
`errSecInternalComponent` no matter what you sign. The way through is to hand the build to the Mac's own
GUI session, which is what this does, and it needs a session to hand it to. Simulator builds sign ad-hoc
and never meet this.

## `shenora diag` — a device that answers back

Getting an app onto a phone is half the problem; the other half is that a phone tells you nothing. There
is no console, no devtools worth the name on iOS, and the failure you care about is usually "it launched
and the screen is blank".

```bash
npx shenora diag serve            # prints a LAN URL — open it on the phone
npx shenora diag devices          # who has checked in
npx shenora diag report           # what the device says about itself
npx shenora diag eval "location.href"
npx shenora diag host "xcodebuild -version"    # run something on the Mac
```

The device opens the page and **polls** — because a webview cannot be dialled into, there is no port to
open and no agent to install, so the only channel that exists is one the page itself opens. From then on
you queue work and it drains the queue.

**It is a devtool you start, and it ships inside nothing.** `@shenora/cli` is a devDependency; running
arbitrary JS in a page is not something that should be a flag in a product binary. A diagnostic hosted
inside the app would also die with it, exactly when you need it most.

The operator half — queueing work, reading results, running a command on the Mac — is **loopback only**,
tested. The device half is open to the LAN, because the device being diagnosed is routinely the one that
cannot authenticate.

## Requirements, and the two that cannot be automated

macOS with Xcode, the `maui-ios` workload, and an *Apple Development* signing identity — `shenora ios
doctor` checks all of it and names what is missing. The Mac may be this machine or one on your LAN.

**A device build needs a provisioning profile for your bundle id, and the .NET SDK cannot make one** — it
consumes profiles, it does not create them, so a bundle id nobody has provisioned fails with *"Could not
find any available provisioning profiles"*, an error about your app caused by a missing step the
toolchain does not offer. `shenora ios provision` is that step: it drives `xcodebuild
-allowProvisioningUpdates` against a throwaway project, once per bundle id.

```bash
npx shenora ios provision                          # your app
npx shenora ios provision com.example.app.widget   # …and every extension it embeds
```

⚠ **Extensions need their own profiles.** An extension is provisioned separately from its container, and
forgetting one fails at the very end of a device install with an error naming the *app*.

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
| `build` (a distributable) | `ios build` | ✅ — `dotnet publish`, **Release by default**, `.ipa` via `ArchiveOnBuild` |
| `add`, `open`, `ls`, `migrate` | — | **N/A by design**: they manage an Xcode/Android Studio project you edit. There isn't one |
| Android (`cap run android`) | `android deploy`, `android log`, `android build` | ✅ — and it runs on Windows |
| — | `ios … --host`, `diag` | **no counterpart**: a remote Mac, and a device that answers back |

**Android is here too** — `android doctor|devices|deploy|log|build`, and it runs on **Windows**, which is
where most .NET Android work happens. It does not exist to wrap `adb`; it exists for the four things that
are not `adb`:

- **finding a JDK.** The Android build needs one and reports its absence from deep inside an MSBuild
  target as a Java error, which reads as a broken SDK. Android Studio ships one in `jbr/` and sets no
  variable, so the common case is a machine that has one and cannot say where.
- **finding `adb`.** Visual Studio's SDK lands in `%LOCALAPPDATA%\Android\Sdk` and exports nothing.
- **refusing to guess between devices.** An emulator and a phone are routinely attached at once.
- **reading the log by PID rather than by tag.** A tag filter has to know how your app logs — `DOTNET`
  for `Console.WriteLine`, something else for `Android.Util.Log` — and gets it wrong silently. The PID
  is every line *your app* wrote under any tag, and it excludes a stale instance.

This CLI does not build your web bundle or run your tests — it is the last mile only.
