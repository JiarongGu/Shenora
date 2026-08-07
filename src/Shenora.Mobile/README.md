# `Shenora.Mobile` — shared source, not a package

**There is no `Shenora.Mobile` on nuget.org and there should not be.** This folder holds the code that
`Shenora.Android` and `Shenora.iOS` share, compiled into each of them by
`Shenora.Mobile.props`. It has no `.csproj` of its own, so it cannot be built, referenced or
published by accident.

```
Shenora.Mobile/          ← you are here. Source only.
  Ipc/                   MobileIpcBridge — the HybridWebView transport
  Threading/             MobileUiDispatcher — the ONE UI-marshalling owner on this shell
  Services/              the Core shell contracts MAUI Essentials can honour
  Hosting/               UseAndroid(...) / UseIOS(...) — the DI registration

Shenora.Android/         ← packs. net10.0-android. Platforms/ = Android-only code.
Shenora.iOS/             ← packs. net10.0-ios. Platforms/ = iOS-only code. macOS build host required.
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

**In `Platforms/`, never behind an `#if`.** The MAUI SDK includes `Platforms/<Platform>/**` only for
the matching TFM, so the same type can have a different implementation per platform with no
preprocessor at all. Proven the hard way: an iOS build compiled cleanly while
`Platforms/Android/MainActivity.cs` — which references `Android.App` and cannot compile for iOS — sat
in the same project.

Today there is **zero** divergent code. The first is expected to be the save picker: Android needs raw
SAF (`ACTION_CREATE_DOCUMENT`), iOS needs `UIDocumentPickerViewController`, and the portable shape
`SaveAsync(options, write)` is designed in `TASKS.md`.

Namespace stays `Shenora.Mobile` in both assemblies, including for platform-specific types, so app
code writes one `using` and compiles on either. Two assemblies sharing a namespace is safe here
because a consumer only ever resolves one of them.
