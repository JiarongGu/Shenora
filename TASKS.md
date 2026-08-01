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

### From the first adopter, second review of the communication core (2026-08-01)

Re-reviewed after the Paused/Dismiss lifecycle completion. **Both findings from the first review are
closed, and closed better than they were filed.** `Pause(reason, detail)` makes the reason REQUIRED
("a pause with no reason gives the app nothing to branch its UI on") — that adopter's four reasons are
the doc's own examples. `Dismiss` is a separate member rather than `Cancel` accepting more states, and
signals the entry's token first so a paused body parked on it unwinds like a cancelled one. `Run` now
completes only when still `Running`, so the spec's headline move (`op.Pause("dns"); return;`) is no
longer stamped `Completed` — the comment calling that "a THIRD lie, introduced by the very feature meant
to remove the other two" is the right instinct. The `RequestResume` asymmetry (Paused left in place, the
app flips its own handle; Interrupted removed because no handle survived) is deliberate, documented, and
carries `status` on the event so a handler can branch. `WireMirrorTests` derives from the host enum, so
the new status and route could not be added unmirrored. Nothing there needs changing.

One gap, in the client:

- [ ] **A crash-announced `interrupted` operation appears in NONE of the client's selectors, so the
  offer `RegisterInterrupted` exists to surface is invisible to a UI built on them.**
  `makeState` exposes `running` / `paused` / `finished`. `TERMINAL_STATUSES` is
  Completed/Failed/Cancelled and *deliberately* excludes `interrupted` (correctly — the comment says so).
  `paused` matches only `'paused'`. So an `interrupted` entry is in no band: not running, not paused, not
  finished. It is reachable only by hand-filtering `byId` — which the store's own docs discourage
  ("`running`/`finished` are DERIVED getters … never a second copy a fold has to remember to keep in
  sync"). The host models Paused and Interrupted as ONE waiting band (§5A.2), `Dismiss` and
  `RequestResume` both accept exactly that band, and the client's `paused` getter even documents "the
  WAITING band alongside `'interrupted'`" — so the concept is present on both sides; only the selector
  for the other half is missing.
  **Why it bites hardest at the worst moment:** a paused run is visible via `.paused` before a restart,
  and after the app relaunches and re-announces it from its own checkpoint it becomes `interrupted` — and
  vanishes from the UI. The one state that exists purely to say "your work did not finish, decide what to
  do" is the one a straightforward UI silently drops, precisely when the owner most needs to see it.
  Suggested: add an `interrupted` getter and a `waiting` one (paused + interrupted) — `waiting` being the
  band the two lifecycle verbs already operate on, so a UI can render "needs you" as one bucket and stop
  caring whether the process restarted in between.

### Standing (habits, not a queue)

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
