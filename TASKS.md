# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/task-archive.md`](docs/task-archive.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: v0.1.0 SHIPPED (2026-07-31)** — five NuGet packages + `@shenora/react` on npm, from the
manual Release workflow. P1–P7 are all complete; nothing below is blocking. Growth from here is
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

### From the first adopter (2026-07-31)

Filed by the adoption loop this file describes ("the next real work arrives when a sibling app adopts
the kit"). A private desktop sibling is at **Stage 0**: `Shenora.WebView2.Sessions` 0.1.0 referenced,
all five packages resolving transitively from the leaf, host building 0 errors against
`net10.0-windows`. Nothing consumes the kit yet, so these are packaging/docs findings, not API
findings — API feedback arrives with Stages 1-3.

- [ ] **`README.md` still says "Not yet published to NuGet/npm" — stale, and actively misleading.**
  All six packages are live (`Shenora.Core`, `.Ipc`, `.WinForms`, `.WebView2`, `.WebView2.Sessions` on
  NuGet, `@shenora/react` on npm, all 0.1.0 — verified against the registries). The line sits directly
  under the "**v0.1.0 - pre-release**" status heading, so an evaluating reader's first conclusion is
  that they cannot consume it yet. `TASKS.md` says shipped, the README says otherwise, and the README
  is what a newcomer reads first.
- [ ] **Consider stating the lib TFM in the package table.** `Shenora.WinForms` ships
  `net10.0-windows7.0`. An adopter currently has to download the nupkg and inspect `lib/` to learn
  whether it fits - which is what the first adopter did before referencing anything. One column, or a
  line under the table, removes that step.
- [ ] **`docs/ADOPTION.md` is genuinely good - the staging rationale is the valuable part.** Recorded as
  positive feedback rather than a request: "Stage 1 carries no IPC dependency, so it deletes the most
  duplicated code for the least risk; the IPC substrate comes last because it is the only stage that
  touches every module" is what made the adoption decision easy. Naming the concrete bugs a hand-rolled
  shell tends to have - the DPI-mis-scaled `Screen.WorkingArea` restore, `CloseReason.UserClosing`
  firing for a programmatic `Close()` - is what justifies adopting a kit at all. Worth keeping that
  bug-naming habit as more stages get written.

The Stage 1 findings from the same adopter (over-promised DPI claim, no work-area clamp, primary-scale
`Apply`, conditional "highest payoff") landed in 0.1.1 — see `docs/task-archive.md` `### 0.1.1 —
Stage 1 adopter findings`. The Stage 1 ADOPTION findings (DPI resolution ownership, plain-form
maximize deferral) landed in 0.1.2 — see `docs/task-archive.md` `### 0.1.2 — Stage 1 adopted:
kit-owns-DPI + plain-form maximize deferral`. Two entries below are standing habits rather than work
to pull.

### From the first adopter, IPC + drop-zone design review (2026-08-01)

Requested review of whether the CURRENT implementation matches the stated design intent — *"not a sync
request pattern, which does not fit desktop or mobile: the backend layer here is mostly attached to its
frontend layer, so a stateful design with an event hub is the way to go — async from the UI, progress
synced"* — plus a proper Windows-native drop experience. Read against `Shenora.Ipc`, `Shenora.Core`'s
`EventBus`, `@shenora/react`, and `DropZoneManager`.

**The verdict first: the client design already matches the intent; the HOST contract does not.**
`createShenoraStore` is exactly the described model — `snapshot` loads current state on first subscribe,
`on` maps events to PURE reducers, `actions` are fire-and-forget `post`. Its own doc gets the hard part
right ("a component that mounts while work is already in flight has MISSED the events, and a stream cannot
be replayed. Snapshot-then-deltas is the contract"), and `invoke` is correctly scoped to "calls that are
quick AND UI-thread-safe" rather than being the default. `EventBus` (module/type/scope patterns, isolated
concurrent handlers, per-subscription match cache) is a real hub, not an afterthought. No change asked for
in any of that — recorded because the next reviewer should not "fix" it toward request/response.

**DESIGNED 2026-08-01 (not implemented):** the first two findings below — the missing event path and
the undefined long-operation shape — are answered by
**`docs/2026-08-01-shenora-communication-core-design.md`** (+ **D23**), which also takes the host
outbound pipeline base-agnostic per the standing mobile direction. Ships as **0.2.0** in three staged
stages: contract (`IModuleContext`) → operations (registry + client store) → channel
(`NotificationPump` + per-channel filtering). Task-by-task plan (11 tasks, TDD steps, the tripwires to
break on purpose): **`docs/2026-08-01-shenora-communication-core-plan.md`**. The two drop-zone findings
below are docs work; the ADOPTION.md note they ask for is folded into the plan's final task.

- [ ] **The module contract carries the REQUEST path but not the EVENT path — which inverts the stated
  priority.** `Shenora.Ipc` has **zero references to `IEventBus`**. `IModuleFacade.HandleMessageAsync`
  "always produces a response", and `BaseFacade` hands a module an `ILogger` plus
  `RouteMessageAsync → Task<object?>`. So the request/response half is first-class and typed, while the
  progress/state half — the half the design is supposed to be built on — is a side dependency every app
  wires by hand, with the module/type/scope conventions re-agreed per app. The tell that this is real
  rather than theoretical: **the kit's own `DropZoneManager` takes `IEventBus` as a REQUIRED option**,
  because the bus is the actual spine. The contract should admit what the kit already does.
  Suggested: give facades a module-scoped publish handle (e.g. `protected void Publish(string type,
  object? payload = null, string? scope = null)` on `BaseFacade`, backed by the injected bus), so emitting
  progress is the default gesture and not a wiring exercise. For comparison, the adopter's hand-rolled
  contract is `HandleAsync(action, payload, emit, ct)` — `emit` is IN the signature, so every module
  streams progress by construction. That is the one place the thing being replaced is closer to the stated
  intent than the kit, which seems worth closing before Stage 3 asks apps to migrate onto it.
- [ ] **Long-running operations have no first-class shape, and on desktop they are the normal case.**
  "Always produces a response" leaves the important case undefined: what does a facade do when a `post`
  starts a ten-minute deploy, a render, a model download, a DB migration? Return immediately and stream
  (then what is the response?), or hold the request open for ten minutes (then the response is meaningless
  and the caller's timeout is wrong)? Both readings are available today. Suggested: name the pattern in
  the contract and the docs — return an operation id immediately, then publish `…PROGRESS` / terminal
  `…COMPLETED` / `…FAILED` events on the bus under that id, with the client store folding them via `on`.
  This is the exact shape "async from the UI, progress synced" implies, and it is what every adopter will
  otherwise invent slightly differently.
- [ ] **`DropZoneManager` is already consumable WITHOUT the IPC migration — say so, loudly.** It depends
  only on `Shenora.Core` (`IEventBus`), the WebView2 control and a `Form`; it does **not** reference the
  bridge, dispatcher or any `Ipc` type, and its surface (`RegisterZone` / `UpdateZoneBounds` /
  `UnregisterZone` / `ShowOverlay` / `ClearAll`) is drivable directly. An app can therefore `new` it, hand
  it a bus, subscribe, and forward the three events over its own existing transport — no Stage 3 required.
  That matters because ADOPTION.md currently files drop zones under Stage 3 ("Needs IPC, so it belongs to
  Stage 3"), which is true of `DropZoneFacade`/`useDropZone` but **not** of the manager, i.e. not of the
  part that is actually hard. This adopter only discovered it by reading the source — the same failure
  mode as the 0.1.1 DPI-claim finding. Suggested: an ADOPTION.md note that the native manager is
  Stage-1-adoptable standalone, and the facade + hook are the Stage 3 half.
- [ ] **Drop zones are the strongest dedup case in the kit, and worth stating as such.** The native
  approach is right for the goal (transparent overlays over the page's zone elements capturing REAL OS
  file paths, which the page can never see because HTML5 drop gives it blob URLs — including drags from
  other apps while backgrounded). It is also demonstrably the most-copied component in the family: the
  kit's own header says "its third copy was already annotated 'ported from…' — this ends that", and this
  adopter independently carries a fourth (387 C# + 84 TS lines) whose header reads "Ported from
  <the same sibling>'s DropZoneOverlay". Four copies of one genuinely fiddly native component is the
  clearest possible argument for the kit existing; ADOPTION.md undersells it as one row in a table.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
