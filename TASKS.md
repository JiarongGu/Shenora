# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/task-archive.md`](docs/task-archive.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.1.2 PUBLISHED (2026-07-31); 0.2.0 BUILT AND MERGED, NOT YET PUSHED OR PUBLISHED** — five
NuGet packages + `@shenora/react` on npm, from the manual Release workflow. P1–P7 are all complete;
nothing below is blocking. Growth from here is harvest-driven (D15) and adoption-driven: the next real
work arrives when a sibling app adopts the kit and hits something, or when a feature worth generalising
emerges while building one. **Because 0.2.0 is unpublished, its surface is still free to change** —
which is why several corrections landed in it rather than as a 0.2.1.

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

### 0.2.0 design pass — "make a proper 0.2.0" (owner direction, 2026-08-01)

> DIRECTION (user, 2026-08-01): *"usually if you do the code review, you should be getting the purpose
> of the project rethinking if this is a good design, instead just check if the code itself works or
> not"* — then: *"lets do all, make a proper 0.2.0."*

The whole-codebase review (`docs/task-archive.md`) audited the kit against its OWN stated intentions
and never asked whether those intentions were right. This is that second pass. **All four items are
free only while 0.2.0 is unpublished** — after publish, D1 and D2 are breaking changes.

**D1 (cut the crash-checkpoint half of the operations cluster) is DONE** — record and rationale in
`docs/task-archive.md` `### 0.2.0 design pass`. It landed narrower than first scoped: the
`RequestWait`/`RequestResume` ask-act pair stayed, because cutting it would have left a client able to
pause but never resume.

**D2 (frameless chrome) is DONE, and it landed as a REJECTION plus a narrower change** — see
`docs/DECISIONS.md` D24. Making the chrome attachable was rejected on evidence: the window style
belongs in `CreateParams` at handle creation, and attaching it later needs `SetWindowLong` +
`SWP_FRAMECHANGED` as a second mechanism, in the one area where a green unit suite has twice been the
wrong answer. What DID change: the pure input-to-pixels half moved to an internal
`CaptionButtonRenderer`, with direct tests it could never have had inside the form.

**D4 (gate the prose) is DONE** — `devtools/scripts/doc-drift.mjs`, run by `verify` and `doctor`. Two
PRECISE checks rather than a fuzzy symbol sweep: the dependency graph drawn in `README.md`/
`ADOPTION.md` against the real `ProjectReference`s, and names in `devtools/retired-names.txt` stated
as a CURRENT fact (past tense is fine — these docs are amendment stacks — and `doc-drift:history`
marks a preserved sketch). It found real drift on its first run; see `CHANGELOG.md` 0.2.0
`### Changed`. **Add a name to `devtools/retired-names.txt` the moment you delete or rename one.**

**D3 (validate D16 with a real second transport) is DONE, and it PASSED** — the spike lived in
`devtools/_transport-spike/` (gitignored, like `_dpi-probe` before it) and its findings are the
deliverable. A `net10.0` console app referencing ONLY `Shenora.Core` + `Shenora.Ipc` ran a full
request/response, the structured error boundary, a `NotificationPump` on a `PeriodicTimer`, and a
`ctx.Run` operation streamed to a client as batched notifications. **The TFM is the proof**: adding a
Windows type anywhere in that graph turns the project red, so "the IPC stack binds to no UI
framework" is now enforced rather than asserted. Three follow-ups it surfaced are listed below —
`Shenora.Ipc` needed no change at all.

- [ ] **A host-side transport helper — the D3 spike's one evidence-backed gap.** Standing up a second
  base (see the design-pass record in `docs/task-archive.md`) showed the IPC half needs NOTHING to run
  headless — but it made me hand-write ~40 lines every non-WinForms base will write identically: the
  transport read loop → deserialize → `DispatchAsync` → serialize → write, plus the pump tick. The
  CLIENT half has had this since P3 (`ShenoraBridge` owns correlation, category demux and the batch
  unbundle); the HOST half has no mirror, so `WebViewIpcBridge` is the only thing that knows the shape
  and it is welded to WinForms. **Not built yet on purpose:** the spike is ONE consumer, and the kit's
  bar is two (`generic-library.md`). It is recorded here so the next real non-WebView2 base is
  EVIDENCE rather than a re-argument from scratch — at which point the shape is already known.
  Candidate placement is `Shenora.Ipc` (no new package, D2).
- [ ] **`Shenora.Core` ships no headless `IShenoraRunner`.** Also from the D3 spike: `CreateBuilder`
  → `Build()` → `Run()` throws without a runner, and the only implementation lives in
  `Shenora.WinForms`. So Core's "application host" half is WinForms-only in practice even though every
  type in it is portable, and the spike had to bypass the builder entirely and wire DI by hand. An app
  CAN implement `IShenoraRunner` itself (it is a one-method interface), so this is a missing
  convenience rather than a missing capability — recorded, not guessed at.
- [ ] **D3's other half is still unvalidated: the desktop-FLAVOURED service contracts.** The spike
  proves the IPC/transport story and nothing else, because a transport needs no file dialogs.
  `FileDialogContracts.cs` still CONCEDES in writing that `FileDialogOptions` carries Win32 vocabulary
  and that a mobile picker would ignore half of it and return a content URI. That break is still
  waiting at the first real mobile adoption, and it is still a 1.0 break the kit says it will not
  take. Narrowing it needs a real mobile consumer, not another spike.

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

**IMPLEMENTED AS 0.2.0 (2026-08-01):** the missing event path, the undefined long-operation shape, and
the drop-zone Stage-1 finding all landed — `IModuleContext` (`Publish`/`Start`/`Run`) in
`BaseFacade.RouteMessageAsync`'s signature, the `Shenora.Ipc.Operations` cluster + `@shenora/react`'s
`useShenoraOperations`, `NotificationPump` extracted for a base-agnostic outbound channel, and
`docs/ADOPTION.md`'s drop-zone row corrected. Design + rationale:
`docs/2026-08-01-shenora-communication-core-design.md` + **D23**; what shipped task by task and how
it was verified: `docs/task-archive.md` `### 0.2.0 — the communication core`. One finding from this
review remains open, below.

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
- [ ] **Keep naming the concrete bug each ADOPTION stage removes.** The first adopter's Stage-0
  feedback (2026-07-31), recorded here as the habit it is rather than as work: what made the adoption
  decision easy was "Stage 1 carries no IPC dependency, so it deletes the most duplicated code for the
  least risk; the IPC substrate comes last because it is the only stage that touches every module" —
  and what justified adopting a kit at all was naming the specific bugs a hand-rolled shell tends to
  have (the DPI-mis-scaled `Screen.WorkingArea` restore; `CloseReason.UserClosing` firing for a
  programmatic `Close()`). Write new stages the same way.
