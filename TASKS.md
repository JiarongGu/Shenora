# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/task-archive.md`](docs/task-archive.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.3.0 PUBLISHED (2026-08-01).** Five NuGet packages + `@shenora/react` on npm. It carries
everything through the mission scheduler — the design pass (D1–D4), the genericity gate, D25, and
`Shenora.Core`'s `Missions`/`Io` layer.

**0.2.0 does not exist and never will** — a session hand-bumped `<VersionPrefix>` to it, the release
workflow bumped from that baseline to 0.3.0, and the number was consumed without shipping. The
registries read 0.1.2 → 0.3.0. Full account in `CHANGELOG.md` under `## 0.2.0 — never released`; the
guard that stops a repeat is in `docs/RELEASING.md`. Work written while this was in flight calls it
"the 0.2.0 pass" — those names refer to the WORK, not to a release.

**The surface is now PUBLISHED, so the free-breaking-change window is closed.** D1 and D2 shipped.
Pre-1.0 still permits a documented break in a MINOR (`CHANGELOG.md`), but it is a real break against
real consumers now — no longer free, and it belongs under `### Breaking`. Growth from here is
harvest-driven (D15) and adoption-driven: the next real work arrives when a sibling app adopts the kit
and hits something, or when a feature worth generalising emerges while building one.

> DIRECTION (user, 2026-07-30): Shenora is the shared infrastructure library for ALL sibling
> projects — a "UI kit for non-web applications" in the headless sense: it holds the desktop
> shell that different applications boot their own logic on, and it must NOT depend on any UI
> component library. Purpose is to stop re-solving the same problems per project. In-scope
> common work explicitly includes: multi-form/multi-window, co-browsing (auxiliary browser
> sessions), drag-drop zones, the IPC package design, the event hub, frontend display
> optimizations, and the React hooks layer.
>
> DIRECTION (user, 2026-07-30, later): growth is harvest-driven — when something nice emerges
> while developing another application, it gets generalized and promoted into Shenora (common
> design/library/tool sharing). And the kit must be able to adopt MOBILE application logic too:
> Capacitor (and similar) shells speaking the same IPC envelope through a pluggable transport.

## Open

### Designed, awaiting an answer

The mission queue, chains and the file-update queue were built on 2026-08-02 and are recorded in
`docs/ROADMAP.md` `## Done` + `docs/task-archive.md`. What is left of that group is one item, and it
is waiting on a question rather than on effort:

- [x] ~~**Cross-process path leases**~~ — DONE 2026-08-02. The open question was answered by the
  owner with a real consumer: a filesystem-heavy sibling that does not own its working folder, spawns
  its own fixing tools, and competes with a mod loader and other applications — including over a NAS.
  That evidence also SPLIT the feature: leases for participants, `IFileLockInspector` for the foreign
  processes a lease cannot touch. The plan's "network shares are not a target" was corrected rather
  than kept (§4.1).

### The rest — held at the two-consumer bar

**Nothing below is blocking.** The 0.2.0 design pass (D1–D4) and the two whole-codebase reviews are
finished — record, rationale and verification in `docs/task-archive.md`. What survives below is what
those passes deliberately did **not** build, each held back by a named evidence bar rather than by
effort. That distinction is the point: none of these should be started because the list looks short.

### Held at the two-consumer bar (`generic-library.md`)

Surfaced by the D3 transport spike, which PASSED — `Shenora.Ipc` needed no change at all. These are
recorded so the next real non-WebView2 base arrives as EVIDENCE rather than a re-argument from
scratch; at that point the shape is already known.

> **The anticipated consumer #2 for the first two is an on-device (offline) mobile host** — see
> `docs/2026-08-02-shenora-mobile-offline-plan.md`. Its finding: the prerequisite sits with the
> ADOPTING app, not the kit — logic living inside transport handlers cannot move on-device, so
> factoring it behind a transport-neutral seam comes first. Do NOT build these because a plan exists;
> build them when the consumer does.

- [ ] **A host-side transport helper — the D3 spike's one evidence-backed gap.** Standing up a second
  base (see the design-pass record in `docs/task-archive.md`) showed the IPC half needs NOTHING to run
  headless — but it made me hand-write ~40 lines every non-WinForms base will write identically: the
  transport read loop → deserialize → `DispatchAsync` → serialize → write, plus the pump tick. The
  CLIENT half has had this since P3 (`ShenoraBridge` owns correlation, category demux and the batch
  unbundle); the HOST half has no mirror, so `WebViewIpcBridge` is the only thing that knows the shape
  and it is welded to WinForms. **Not built yet on purpose:** the spike is ONE consumer, the bar is
  two. Candidate placement is `Shenora.Ipc` (no new package, D2).
- [ ] **`Shenora.Core` ships no headless `IShenoraRunner`.** Also from the D3 spike: `CreateBuilder`
  → `Build()` → `Run()` throws without a runner, and the only implementation lives in
  `Shenora.WinForms`. So Core's "application host" half is WinForms-only in practice even though every
  type in it is portable, and the spike had to bypass the builder entirely and wire DI by hand. An app
  CAN implement `IShenoraRunner` itself (it is a one-method interface), so this is a missing
  convenience rather than a missing capability — recorded, not guessed at.
- [ ] **`IpcJson.Options` cannot take an app-supplied `JsonSerializerContext`.** Found 2026-08-02
  while assessing on-device mobile. The instance is frozen with
  `MakeReadOnly(populateMissingResolver: true)` — i.e. a REFLECTION resolver — which is fine on
  desktop and Android but is exactly the pattern iOS (Mono AOT + trimming) strips the metadata for,
  failing at runtime rather than build time. The fix is additive (let an app contribute an
  `IJsonTypeInfoResolver` to chain) and must not reintroduce the drifting-copies problem the frozen
  single instance was created to solve. No consumer yet; do not pre-build. Worth knowing it pays
  twice: the same change is what unlocks full/NativeAOT on Android, which is the strongest cold-start
  lever an on-device host has.
- [ ] **D3's other half is still unvalidated: the desktop-FLAVOURED service contracts.** The spike
  proves the IPC/transport story and nothing else, because a transport needs no file dialogs.
  `FileDialogContracts.cs` still CONCEDES in writing that `FileDialogOptions` carries Win32 vocabulary
  and that a mobile picker would ignore half of it and return a content URI. That break is still
  waiting at the first real mobile adoption, and it is still a 1.0 break the kit says it will not
  take. Narrowing it needs a real mobile consumer, not another spike.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
- [ ] **Keep naming the concrete bug each ADOPTION stage removes.** The first adopter's Stage-0
  feedback (2026-07-31), recorded here as the habit it is rather than as work: what made the adoption
  decision easy was "Stage 1 carries no IPC dependency, so it deletes the most duplicated code for the
  least risk; the IPC substrate comes last because it is the only stage that touches every module" —
  and what justified adopting a kit at all was naming the specific bugs a hand-rolled shell tends to
  have (the DPI-mis-scaled `Screen.WorkingArea` restore; `CloseReason.UserClosing` firing for a
  programmatic `Close()`). Write new stages the same way.
