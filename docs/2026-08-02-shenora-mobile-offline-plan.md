# On-device / offline mobile — plan, not a project

**Status: PLAN. Not scheduled, not in 0.2.0, nothing built.** Written because Sonora will need an
offline mode later and the owner asked to plan for it. The point of writing it now is that the
sequencing is counter-intuitive: the expensive part is not the mobile shell, and the cheap part is
worth doing whether or not mobile ever happens.

## §1 The question that prompted this

*"Sonora currently uses Capacitor. Could we build our own framework on .NET (say MAUI) so we reuse
the same core library?"* — and, on being told Sonora is currently a thin client: *"this is the
current stage; in future it will need an offline mode, so it might be worth planning out."*

**Today MAUI would be a downgrade for Sonora, and that is not a tuning problem.** Its mobile app
ships the React bundle in a WebView and pairs to the PC server over LAN; every real workload —
transcode, scan, SMB proxy, the whole C# core — runs server-side. Swapping the shell changes neither
the rendering path (same bundle, same system WebView) nor the data path (same HTTP), while adding
runtime init to cold start and several times the APK size (both accepted and tunable once offline is
the goal — see §7; they are decisive only while the swap buys nothing). The .NET core is *already*
reused on mobile; it just runs on the server. That is the kit's documented server-backed profile
working as designed.

Offline changes the premise, because then the core has to run **on the device**.

## §2 The real blocker is transport coupling, not the shell

Measured 2026-08-02: `Sonora.Server` has **28 ASP.NET controllers**, and Sonora references no
Shenora package at all (it is a donor to the kit, not an adopter).

That is where the cost is. An offline app runs the same logic with **no HTTP in the process** — no
Kestrel, no controllers, no localhost. Logic that lives in a controller cannot make that move; logic
that lives behind a transport-neutral seam can. So:

> **The work that unlocks offline mode is factoring logic out of controllers into transport-neutral
> modules — and that work is worth doing regardless of whether mobile ever happens, can start today,
> and involves no mobile tooling at all.**

This is exactly what `Shenora.Ipc`'s facade/dispatcher model is for, and why D16 keeps that package
`net10.0` with no UI binding. A module written against `IModuleContext` is served identically by an
HTTP endpoint, a WebSocket, a WebView bridge, or an in-process call — proven by the D3 spike, which
ran the whole stack from a console app referencing only `Shenora.Core` + `Shenora.Ipc`.

## §3 The shape offline takes

The same modules, two transports, one frontend:

```
            @shenora/react  (unchanged — transport-pluggable by design, D16)
                   │
      ┌────────────┴────────────┐
   ONLINE                    OFFLINE
   HTTP/WS  ──► LAN server    in-process bridge ──► on-device host
      │                                │
      └────────► the SAME modules ◄────┘
                 (Shenora.Core + Shenora.Ipc, net10.0)
```

The frontend does not fork. `@shenora/react` already speaks one envelope over a swappable transport
— the seam that exists because Sonora's own event bridge and WebSocket used the same `{"__batch":…}`
envelope, which is where the kit learned the lesson.

What the mobile shell then has to provide is small: host a WebView, serve the bundle, carry the
envelope. .NET 9's `HybridWebView` is structurally the same thing `Shenora.WebView2` does for the
desktop — so a future `Shenora.Maui` is the mobile sibling of an existing package, not a new
architecture.

## §4 Kit gaps this would hit, and which already have an owner

| Gap | Status |
|---|---|
| `Shenora.Core` ships no headless `IShenoraRunner` — `Build()`/`Run()` throws, and the only implementation is in `Shenora.WinForms` | Already in `TASKS.md`, held at the two-consumer bar. **A mobile host is consumer #2.** |
| No host-side transport helper — the ~40 lines every non-WinForms host writes identically (read loop → deserialize → dispatch → serialize → write, plus the pump tick) | Already in `TASKS.md`, same bar, **same consumer #2.** |
| `IFileDialogs`/`FileDialogOptions` carry Win32 vocabulary; the file concedes a mobile picker would ignore half and return a content URI | The known pre-1.0 break, explicitly waiting for a real mobile consumer. This is it. |
| `IpcJson.Options` is frozen with `MakeReadOnly(populateMissingResolver: true)` — a **reflection** resolver, with no way for an app to contribute a `JsonSerializerContext` | **New, found while assessing this.** Fine on Android; on iOS (Mono AOT + trimming) reflection-based `System.Text.Json` is the pattern whose metadata gets trimmed and fails at runtime rather than build time. Fix is additive: let the app supply an `IJsonTypeInfoResolver` to chain. |
| A `Shenora.Maui` package (HybridWebView bridge + runner) | Does not exist. The mobile analogue of `Shenora.WebView2`. |

Note the shape of that table: three of five are already-identified items whose stated blocker is
"needs a second consumer". Offline mobile supplies it. Nothing here argues for building them now.

## §5 One thing that needs care: Android storage is not paths

`PathClaims` and the work scheduler assume a filesystem with hierarchical paths. That holds for
**app-private storage**, which is where synced/downloaded media for an offline library would live —
so the offline use case is served. It does **not** hold for user-visible media on modern Android,
which is reached through MediaStore/SAF content URIs, not raw paths.

So: `PathClaims` is right for the offline cache and wrong for a general "browse the user's files"
feature. That distinction should be made deliberately when the time comes, not discovered. A content
URI namespace would be its own `IClaimScope` — which is precisely why claim scopes are a seam.

## §6 Sequencing

**Now (cheap, useful regardless, no mobile tooling):**
1. Nothing in this repo. The kit is ready; the gaps above are correctly parked.
2. In Sonora, when convenient: move logic out of controllers into transport-neutral services, so a
   controller becomes a thin adapter over a module rather than the place the logic lives. This is
   good design on its own merits and is the entire prerequisite for offline.

**When offline becomes real:**
3. Sonora adopts `Shenora.Core` + `Shenora.Ipc` (`docs/ADOPTION.md`; the IPC substrate is the last
   stage because it touches every module).
4. Close the five gaps in §4 — now evidence-backed, with a named consumer.
5. Build `Shenora.Maui`, and only then benchmark.

**Benchmark what matters, when there is something to measure.** Not "MAUI vs Capacitor as a shell" —
that comparison is lost on cold start and app size for no gain. The comparison that could justify the
switch is **C# on-device vs JS-plus-network for real work**: library scan, hashing, sync,
transcode-or-not decisions, with the network unavailable. Measure on a physical device; emulator
cold-start figures are worthless here.

## §7 Bundle size and cold start: accepted, and not fixed constants

**Owner decision, 2026-08-02:** *"bundle size, cold startup on first run is all acceptable downsides,
and we can try to improve those."* Recorded because §1 framed them as verdicts, which was too
passive — Capacitor's own startup is the product of years of optimization, not a natural advantage,
and .NET has its own levers.

**First, the comparison changes once offline is the goal.** Shell-versus-shell, a slower cold start
buys nothing. Against "the app does not work without the server", a few hundred milliseconds is
obviously worth paying. So this stops being a reason not to do it and becomes a thing to tune.

**Levers, strongest first** — to be tried in this order when the time comes, not now:

- **Profiled AOT on Android** (`AndroidEnableProfiledAot`) — AOT-compiles the recorded startup path
  only, rather than everything. The largest single win available, and well supported.
- **Full AOT / NativeAOT for Android** — bigger startup and size wins again, but it constrains
  reflection. **This is the same constraint as the `IpcJson` gap in §4**, which is the useful link:
  giving `IpcJson` an app-suppliable `IJsonTypeInfoResolver` is not just an iOS-trimming fix, it is
  what unlocks the strongest startup option on Android too. One change, two payoffs.
- **Trimming** (`PublishTrimmed`, `TrimMode=full`) plus R8 on the Java side — the main size lever.
- **Keep the startup path lazy.** A kit discipline that already exists: the embedded resource
  provider was deliberately changed from an eager `Parallel.ForEach` in its constructor to
  lazy-with-warmup during extraction. An on-device host must not construct its whole module graph
  before the first frame; warm the rest behind the splash.
- **Perceived startup.** The kit already carries the no-white-flash colour contract end to end, and
  a splash primitive on desktop. Runtime init hidden behind a splash that is already painted is a
  different user experience from the same milliseconds spent on a white screen.

**What must not happen: claiming any of this without numbers.** `phase-workflow.md` requires
measurements for performance claims, and this document deliberately contains none — everything above
is a lever to try, not a result. Measure on a physical device, both sides, same device.

## §8 Not being built, and why

No `Shenora.Maui`, no transport helper, no headless runner, no `IpcJson` resolver seam, no content-URI
claim scope. Every one of them has a plausible future consumer and no present one, and the kit's bar
is two consumers with evidence (`generic-library.md`). Recorded here so the next session finds the
analysis rather than redoing it — and so nobody builds a mobile package because a plan mentioned one.
