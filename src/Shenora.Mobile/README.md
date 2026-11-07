# `Shenora.Mobile` — shared source, not a package

**There is no `Shenora.Mobile` on nuget.org and there should not be.** This folder holds the code that
`Shenora.Android` and `Shenora.iOS` share, compiled into each of them by
`Shenora.Mobile.props`. It has no `.csproj` of its own, so it cannot be built, referenced or
published by accident.

```
Shenora.Mobile/          ← you are here. Source only.
  Ipc/                   MobileIpcBridge — the HybridWebView transport; MobileIpcCore, the MAUI-free half a test compiles
  Threading/             MobileUiDispatcher — the ONE UI-marshalling owner on this shell
  Services/              the Core shell contracts MAUI Essentials can honour
  WebView/               the interceptor, safe-area insets, the media surface, page transparency
  Hosting/               UseAndroid(...) / UseIOS(...) — the DI registration, and the lifecycle

Shenora.Android/         ← packs. net10.0-android. Services/ = Android-only code.
Shenora.iOS/             ← packs. net10.0-ios. Services/ = iOS-only code. Builds on Windows as a library;
                           a Mac is needed to produce and run an app.
```

## Why two packages and not one multi-targeted package

Owner's call (2026-08-02): they ship separately, they are built on different hosts, and a consumer
builds for one platform at a time — so the package boundary matching the platform boundary is what
makes the situation legible. The alternative (one package, two `lib/` faces) puts less in the
consumer's `PackageReference` but hides which platform is actually being served.

## Why the shared code is SOURCE and not a third package

A `Shenora.Mobile` assembly referenced by both would either be published — a package nobody asks for,
carrying its own SemVer surface — or need embedding tricks to hide it from the graph. Compiling the
source into each face costs nothing at runtime: a consumer resolves exactly one platform's assembly,
and the types are identical because the source is.

## Where divergence goes

**In `Shenora.Android/Services/` or `Shenora.iOS/Services/`, never behind an `#if`.** Each shell is a
single-TFM project, so a file in its own project is compiled for that platform alone and the same
contract can have a different implementation per platform with no preprocessor at all. There is no
`Platforms/<Platform>/` folder: that is the MAUI SDK's convention for a project that multi-targets, and
these do not.

Platform code is named for its platform (`AndroidFileDialogs`, `IosFileDialogs`: the save picker is raw
SAF `ACTION_CREATE_DOCUMENT` on Android and `UIDocumentPickerViewController` on iOS) and sits in that
platform's namespace, `Shenora.Android` or `Shenora.iOS`. The shared source here is in `Shenora.Mobile`,
the same in both assemblies, and `UseAndroid` and `UseIOS` register the platform types.
