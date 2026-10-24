# samples — the apps that prove the kit's behaviour

## What this folder is

The sample apps every behavioural claim is proven against: `Shenora.Sample.Desktop` (WinForms + WebView2),
`Shenora.Sample.Chromium`, `Shenora.Sample.Maui` (the Android and iOS head), `Shenora.Sample.Web` (the React
page they host) and `Shenora.Sample.Logic`. All of them are in `Shenora.slnx`, so `verify` builds them.
They consume the kit as an adopter would; nothing in `src/` may depend on them.

## Conventions here

- `Shenora.Sample.Logic` is `net10.0` on purpose: it is the D19/D20 portability guard and turns RED the
  moment a Windows type reaches app logic. Never retarget it, and never take it out of the solution.
- A `*Probe.cs` is code that accuses the kit when it is wrong: guard each iteration over system-wide state
  separately, and print the HRESULT and the failing step, never a bare exception name (`probe-diagnostics`).
- A probe that asserts an absence proves nothing until it has been seen to FAIL; a seam is proven by a
  fake that is asserted USED (D63).

## Traps

- "The probe says FAIL" is a hypothesis about the probe as much as the kit — both recorded incidents ended
  with the kit innocent (`probe-diagnostics`).
- On a device, a log tailed before it is filtered or an app that never relaunched makes a result mean
  nothing (`mobile-harness`); the page's cost on each mobile shell is `mobile-shells`.
- A sample warning is a warning: `TreatWarningsAsErrors` covers `src/` only, so `build --strict` (inside
  `verify`) is what fails one here.

## Checks

- `node devtools/dev.mjs verify` builds every sample and type-checks `Shenora.Sample.Web`.
- `node devtools/dev.mjs sample` runs the desktop sample; `dev.mjs android|mac` drive the devices. They
  need a display or a device, so they are not in the declared safe work.
