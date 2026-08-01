# On-device / offline mobile — a consumption profile the kit is not yet ready for

**Status: PLAN. Not scheduled, not in 0.2.0, nothing built.** Written so the analysis exists when a
consumer arrives, and so the gaps below are recognised as *already-identified* rather than
rediscovered.

This document is about the **kit**. The assessment of any particular app's readiness lives in
`local/` — this repo readies the LIBRARY and writes `docs/ADOPTION.md`; the adopting app's own
session adopts (`.claude/rules/phase-workflow.md`, `sensitive-info.md`).

## §1 The profile

`README.md` names two consumption profiles today: **desktop-only** (postMessage IPC) and
**server-backed** (in-process HTTP serving desktop and mobile; shell only). In the server-backed
profile a mobile client is a thin one — the .NET core runs on the server and the device holds a web
frontend. That works, and it already delivers "the same core, reused on mobile".

**Offline is a third profile: the core runs ON the device.** Nothing about it is hypothetical for the
kit — `Shenora.Core` and `Shenora.Ipc` are `net10.0` with no UI binding (D16), which the D3 spike
proved by running the whole IPC stack from a console app referencing only those two, and which the
`net10.0` `samples/Shenora.Sample.Logic` tripwire keeps true. Mobile TFMs reference `net10.0`
libraries. The portable half already travels.

## §2 The prerequisite is on the adopter's side, and it is not the shell

The instinct is that going on-device means picking a mobile UI framework. It does not. The blocking
question is **where the app's logic lives**:

> An offline app runs with **no HTTP in its process** — no server, no controllers, no localhost.
> Logic that lives inside a transport handler (an HTTP controller, a WebSocket handler) cannot make
> that move. Logic behind a transport-neutral seam can.

So for any adopter whose logic sits in controllers, the work that unlocks offline is factoring it out
into transport-neutral modules — **which needs no mobile tooling, can start at any time, and is
better design whether or not mobile ever happens.** That refactor belongs to the adopting app.

This is precisely what `Shenora.Ipc`'s facade/dispatcher model is for, and why D16 keeps that package
free of any UI binding. A module written against `IModuleContext` is served identically by an HTTP
endpoint, a WebSocket, a WebView bridge, or an in-process call.

**Worth adding to `docs/ADOPTION.md` when that section is written:** "can this logic run with no
transport at all?" is a better readiness test than any stage checklist, because it is the one that
decides whether a second profile is reachable later.

## §3 The shape: same modules, two transports, one frontend

```
            @shenora/react  (unchanged — transport-pluggable by design, D16)
                   │
      ┌────────────┴────────────┐
   ONLINE                    OFFLINE
   HTTP/WS  ──► a server      in-process bridge ──► on-device host
      │                                │
      └────────► the SAME modules ◄────┘
                 (Shenora.Core + Shenora.Ipc, net10.0)
```

The frontend does not fork. `@shenora/react` speaks one envelope over a swappable transport — the
seam exists because a donor app's event bridge and its WebSocket already shared one `{"__batch":…}`
envelope, which is where the kit learned that the envelope and the pipe are separable.

What a mobile shell must then supply is small: host a WebView, serve the bundle, carry the envelope.
.NET 9's `HybridWebView` does structurally what `Shenora.WebView2` does for the desktop, so a future
`Shenora.Maui` would be an existing package's mobile sibling rather than a new architecture.

## §4 Kit gaps this profile would hit

| Gap | Status |
|---|---|
| `Shenora.Core` ships no headless `IShenoraRunner` — `Build()`/`Run()` throws, and the only implementation is in `Shenora.WinForms` | In `TASKS.md`, held at the two-consumer bar. **An on-device host is consumer #2.** |
| No host-side transport helper — the ~40 lines every non-WinForms host writes identically (read loop → deserialize → dispatch → serialize → write, plus the pump tick) | In `TASKS.md`, same bar, **same consumer #2.** |
| `IFileDialogs`/`FileDialogOptions` carry Win32 vocabulary; the file concedes a mobile picker would ignore half and return a content URI | The known pre-1.0 break, explicitly waiting for a real mobile consumer. |
| `IpcJson.Options` is frozen with `MakeReadOnly(populateMissingResolver: true)` — a **reflection** resolver, with no way for an app to contribute a `JsonSerializerContext` | **Found while writing this.** Fine on desktop and Android; on iOS (Mono AOT + trimming) reflection-based `System.Text.Json` is the pattern whose metadata gets stripped, failing at runtime rather than build time. Fix is additive: accept an `IJsonTypeInfoResolver` to chain. Now in `TASKS.md`. |
| A mobile host package (WebView bridge + runner) | Does not exist. |

Three of five are already-parked items whose stated blocker is "needs a second consumer". This
profile supplies it. None of that is an argument for building them now.

## §5 One platform fact to get right rather than discover

`PathClaims` and the work scheduler assume a filesystem with hierarchical paths. That holds for
**app-private storage**, which is where a synced offline cache lives — so the offline case is served.
It does **not** hold for user-visible media on modern Android, reached through MediaStore/SAF content
URIs rather than raw paths.

So `PathClaims` is right for an offline cache and wrong for "browse the user's files". A content-URI
namespace would be its own `IClaimScope` — which is exactly why claim scopes are a seam and not an
enum of built-in rules.

## §6 Bundle size and cold start: accepted, and not fixed constants

**Owner decision, 2026-08-02:** *"bundle size, cold startup on first run is all acceptable downsides,
and we can try to improve those."* Recorded because an earlier draft framed them as verdicts, which
was too passive — a mature hybrid toolchain's startup is the product of years of optimization, not a
natural advantage, and .NET has its own levers.

The comparison also changes with the goal. Shell-versus-shell, a slower cold start buys nothing.
Against "the app does not work without a server", it is obviously worth paying.

Levers, strongest first, to try when there is something to measure:

- **Profiled AOT on Android** (`AndroidEnableProfiledAot`) — AOT-compiles the recorded startup path
  rather than everything. The largest single win, well supported.
- **Full AOT / NativeAOT** — larger startup and size wins, but constrains reflection. **This is the
  same constraint as the `IpcJson` gap in §4**, which is the useful connection: an app-suppliable
  `IJsonTypeInfoResolver` is not only an iOS-trimming fix, it is what unlocks the strongest
  cold-start option. One change, two payoffs.
- **Trimming** (`PublishTrimmed`, `TrimMode=full`) plus R8 — the main size lever.
- **Keep the startup path lazy.** A discipline the kit already has: the embedded resource provider
  was deliberately moved off an eager `Parallel.ForEach` in its constructor to lazy-with-warmup. An
  on-device host must not construct its whole module graph before the first frame.
- **Perceived startup.** The kit already carries the no-white-flash colour contract end to end.
  Runtime init behind an already-painted splash is a different experience from the same milliseconds
  on a white screen.

**No performance numbers appear in this document, deliberately.** `phase-workflow.md` requires
measurements for performance claims; everything above is a lever to try, not a result. Measure on a
physical device, both sides, same hardware — emulator cold-start figures would be misleading.

## §6a A related question, already settled: desktop Linux

Asked in the same session and answered in **D26**: the kit stays Windows-desktop-scoped, and Linux is
served by the server-backed profile rather than a native shell. The criterion that decided it is
worth carrying into any mobile-host choice too — **a candidate shell must expose the NATIVE WINDOW,
not merely host a WebView**, because the kit's differentiators (native drop overlays, frameless
chrome) live in the window, not the page. A shell that only wraps a WebView cannot carry this kit.

That test applies directly here: whatever hosts a WebView on a mobile device must still let the host
reach platform capability the page cannot, or the on-device profile buys nothing the server-backed
one does not already give.

## §7 Not being built, and why

No mobile host package, no transport helper, no headless runner, no `IpcJson` resolver seam, no
content-URI claim scope. Each has a plausible future consumer and no present one, and the kit's bar
is two consumers with evidence (`generic-library.md`). Recorded here so the next session finds the
analysis instead of redoing it — and so nobody ships a mobile package because a plan mentioned one.
