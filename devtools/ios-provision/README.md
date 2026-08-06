# `devtools/ios-provision` — the kit's own provisioning stub

A minimal Xcode project whose **only** job is to make Xcode mint a provisioning profile for a bundle id.
It is never run, never shipped, and never installed on anything.

Driven by `node devtools/dev.mjs mac provision <bundle-id> [<bundle-id>…]`.

## Why this exists at all

**`.NET` cannot mint a provisioning profile.** It CONSUMES profiles — `-p:CodesignProvision=Automatic`
selects one that already exists and fails with *"Could not find any available provisioning profiles"* when
none does. The only thing that creates one is `xcodebuild -allowProvisioningUpdates`, and that needs *an*
Xcode project to point at.

So a kit whose whole measure is *how little native code an adopting app writes* had a hole in exactly the
place that matters most: you could not reach a device at all without owning an Xcode project.

⚠ **The wrong fix, tried and rejected (2026-08-06):** borrowing a sibling app's Capacitor/Xcode project to
mint the profile. It works, and it is wrong three ways — it is slow, it drags that app's SPM checkouts into
an unrelated build, and it makes this kit depend on a consumer having Capacitor installed. The owner's
verdict was direct: *"why you rely on capacitor instead create your own one"*. This is that.

## What is deliberately NOT in it

- **No bundle id.** It is passed on the `xcodebuild` command line as `PRODUCT_BUNDLE_IDENTIFIER=…`, so the
  project file is generic and never edited per invocation. One less thing to leave dirty in a tree.
- **No team id.** Same reason, plus `sensitive-info.md`: a team id is a personal identifier and belongs in
  the gitignored `local/mac.json`, never in a tracked file. `mac provision` reads it from there, or derives
  it from the signing certificate's `OU` when it is not configured.
- **No Info.plist.** `GENERATE_INFOPLIST_FILE = YES` — a file that exists only to be minimal is a file that
  can drift.
- **No app.** `main.swift` is one line. Nothing here is meant to run; the profile is the artefact.

## The two things that cannot be automated, ever

They belong in any recipe that ships this, and being honest about them is the point:

1. **A free / personal-team profile expires after 7 DAYS.** Re-run `mac provision` when a device build
   starts failing to sign; that is the expected maintenance, not a fault.
2. **A first install needs the certificate TRUSTED ON THE PHONE** — Settings → General → VPN & Device
   Management → the developer account → Trust. There is no command-line form of that, on purpose.
