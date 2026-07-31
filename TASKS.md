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
Stage 1 adopter findings`. Two entries below are standing habits rather than work to pull.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
