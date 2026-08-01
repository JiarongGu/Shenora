# TASKS.md — open backlog only

**This file holds OPEN tasks only.** Once a task is fully done (committed + verified) its entry is
**moved to [`docs/task-archive.md`](docs/task-archive.md)**, not checked off in place — so the length
of this file is the size of the remaining work, which is the whole point of looking at it.
`docs/ROADMAP.md` `## Done` is the narrative of what shipped and why; `CHANGELOG.md` is the
release-facing log. `> DIRECTION (user):` blockquotes capture the user's steering verbatim and stay
here as long as they still steer.

**Status: 0.1.2 PUBLISHED (2026-07-31); 0.2.0 IS ON `main` AND ON GITHUB, BUT UNPUBLISHED** — no
`v0.2.0` tag exists and the registries still serve 0.1.2, because publishing is the MANUAL Release
workflow. Keep the two apart: pushing costs nothing, **publishing is what freezes the surface**. Five
NuGet packages + `@shenora/react` on npm, from that workflow. Growth from here is
harvest-driven (D15) and adoption-driven: the next real work arrives when a sibling app adopts the kit
and hits something, or when a feature worth generalising emerges while building one. **Because 0.2.0 is
unpublished, its surface is still free to change** — which is why several corrections landed in it
rather than as a 0.2.1, and why its two breaking changes (D1, D2) cost nothing yet. That freedom ends
at the Release workflow, not at `git push`.

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

**Nothing here is blocking.** The 0.2.0 design pass (D1–D4) and the two whole-codebase reviews are
finished — record, rationale and verification in `docs/task-archive.md`. What survives below is what
those passes deliberately did **not** build, each held back by a named evidence bar rather than by
effort. That distinction is the point: none of these should be started because the list looks short.

### Held at the two-consumer bar (`generic-library.md`)

Surfaced by the D3 transport spike, which PASSED — `Shenora.Ipc` needed no change at all. These are
recorded so the next real non-WebView2 base arrives as EVIDENCE rather than a re-argument from
scratch; at that point the shape is already known.

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
- [ ] **D3's other half is still unvalidated: the desktop-FLAVOURED service contracts.** The spike
  proves the IPC/transport story and nothing else, because a transport needs no file dialogs.
  `FileDialogContracts.cs` still CONCEDES in writing that `FileDialogOptions` carries Win32 vocabulary
  and that a mobile picker would ignore half of it and return a content URI. That break is still
  waiting at the first real mobile adoption, and it is still a 1.0 break the kit says it will not
  take. Narrowing it needs a real mobile consumer, not another spike.

### Worth saying better (documentation, not code)

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
