# ROADMAP.md — done + remaining

`## Done` is the durable record (narrative, newest first — what changed, why, how it was
verified). `## Remaining` is the phase plan; items graduate here from `TASKS.md` when finished.

## Done

### 2026-08-02 (later) — the mission layer: renamed, restructured, and given a queue, chains and a file writer

Five owner-directed changes to the layer that shipped hours earlier, all pre-1.0 and all landing before
any release takes them, so a consumer sees one hop rather than five.

**The naming.** `Work*` → `Mission*`. `Work` is too common a word to own or grep for; `Task` collides
with `System.Threading.Tasks`, where `TaskScheduler` would be ambiguous against the BCL type in every
consumer importing both namespaces. Of the owner's shortlist, `Quest` was rejected for reading as
domain vocabulary in a games family — which is what the genericity gate exists to keep out of `src/` —
and `Expedition` for putting ten characters in front of fifteen types.

**The definition/execution split, and the rule that was being misapplied.** `MissionRequest` became
`MissionDefinition` (what should run); `MissionContext` + `MissionView` + `MissionSnapshot` collapsed
into `MissionExecution` (one specific run). Four types for two concepts became two. The argument for
doing it now rather than waiting for a consumer was the owner's: *"bigger change does not mean a bad
thing, we need to think forward for future, change is allowed, this is still pre-1.0."* That exposed a
misapplication — the two-consumer bar governs CAPABILITY, while amendment A2 governs SHAPE and says to
pay now where later would be BREAKING. This change alters `SubmitAsync`, every body's parameter, all
three observer callbacks and both policy methods at once, so it is A2's case exactly.

**The queue's store**, where the first design was rejected by its own cost analysis. Making the whole
queue a pluggable async seam would put an `await` in the dispatch path, which cannot run under the
scheduler's lock, forcing admission to re-validate against a collection that may have changed — a race
where a race corrupts rather than delays, bought for a capability nobody asked for. Shipped instead as
`IMissionQueueStore`: the pending list stays internal and synchronous, and durability stops being a
concept parallel to the queue.

**Chains**, as ONE queue entry. `MissionChain.Sequence` returns an ordinary `MissionDefinition`, so the
scheduler gains no dependency edges and no blocked-on-predecessor state — the alternative was a DAG
engine by another name, declined again. Steps share an in-memory `IMissionChainContext`; a durable
chain carries state in `Payload`, and that limit is documented rather than papered over.

**A file-update queue**, deliberately outside mission management (owner: *"it's more a different design
rather than put them all into mission management"*). A path claim excludes two missions for their whole
duration when only the final rename needs exclusivity, so a seven-second compress waits on a
three-millisecond replace. Compute in parallel, hand the change set to the queue, serialize only the
landing. Atomicity is the app's choice per update — `PerChange`, or `AllOrNothing` via compensating
rollback, which forces STAGED deletes since a delete cannot be undone from nothing. The crash boundary
is out of scope and says so in the enum's own XML.

**Verification, and the two things it caught.** 751 dotnet tests. The file queue's serialization test
asserts both halves in one run and was sabotage-verified (a fresh semaphore per call fails it by name).
The chain claim-union test was sabotage-verified too — and **passed the sabotage**, because it had
ordered its steps shared-then-exclusive so that a "last wins" bug gave the same answer; it is now a
Theory over both orders. A worthless test found by running the sabotage rather than trusting the green
is the argument for the whole practice.

Earlier the same day: the scheduler was dogfooded in the sample (`SCHEDULE_DEMO` plus a ~35-line
`IMissionObserver` adapter, proven live — two contending items serialized while a disjoint one
overlapped), `.claude/knowledge/doc-claims.md` was written after three shipped doc claims turned out to
be false, and `dev.mjs verify` gained the always-loaded rule-budget gate that had been drifting
unwatched since the day it was built.

### 2026-08-02 — the mission scheduler: a filesystem planner and a job queue are ONE engine

Harvest-driven (D15), from owner direction that *"a common usecase is filesystem operations + parallel
tasks process… we need a library to support, since those 2 major features are complex themselves"* —
and against the bar *"allow sibling projects to USE the library instead of implementing their own, not
just say we did some mirroring."* A survey of the three donor apps found the same two problems solved
**five times and differently**: two file-operation planners (545 and 603 lines — one an event-driven
path-overlap dispatcher, the other a two-plan single-worker model it had been rewritten away from), two
job queues (463 and 664 lines), a global GPU gate and a lane-holding capacity governor. Design +
evidence: `docs/2026-08-02-shenora-mission-scheduling-design.md`; the surface is in `ARCHITECTURE.md`.

**It shipped in 0.3.0** — `CHANGELOG.md` dates that release 2026-08-01 while this entry is dated by the
day the work was built, so the two headings differ by a day for one release only.

**The claim that made it a merge rather than five ports:** a planner is a scheduler keyed by PATH
(two keys conflict when one contains the other) and a job queue is a scheduler keyed by LANE (a key
admits N holders); everything else — submission order, bounded parallelism, event-driven dispatch,
dedup, retry, cancellation — is identical. So `Shenora.Core` gained ONE engine plus an `IClaimScope`
seam, in `Missions/` + `Io/PathClaims.cs`, with **no new package** (D2) and no storage, DI or reporting
dependency. Two whole classes of prior bug disappear structurally rather than by being fixed: a
request declares its claim SET, so there is no per-key lock object to leak (the check-then-remove race
that handed two callers different semaphores for one key) and no acquisition ORDER to remember (the
documented entity→category rule every call site carried).

Two amendments landed DURING the build, both from *"think bigger — a new application with a new
requirement should also fit"*. **A1:** ordering and timing are PRODUCT decisions, so `IMissionPolicy`
owns what (`Compare`) and when (`ShouldStart`) with `PriorityMissionPolicy` as the default — safe to
expose because a policy only ever chooses among items that already passed admission, so the worst a
buggy one can do is delay work. **A2:** the surface was audited for changes that would be BREAKING
later rather than additive, which is how weighted lane permits (`MissionLane(Name, Permits)`) and
`Priority` shipped from the start, defaulted to today's behaviour. What that reasoning did NOT buy:
a DAG engine, a handler registry, per-item pause — "it might be needed" is not evidence (§10).

**Verified where a green suite could have lied.** The concurrency tests prove BOTH halves in the same
run — peak concurrency 1 for a contended key WHILE exceeding 1 overall — because asserting only that
results are correct passes a fully serial implementation, and every test passes an explicit lane
capacity so a parallelism regression cannot hide on a two-core box. Two adoption claims that had been
asserted with nothing behind them then got their own tests (`MissionSchedulerAdoptionTests`): crossing
two-claim pairs complete under a timeout (a deadlock manifests as a hang, so the timeout IS the
assertion), and lowering a lane's capacity mid-flight both spares in-flight work and really binds once
the surplus drains. Adopter-facing mapping: the mission-scheduler section of `docs/ADOPTION.md`.

### 2026-08-01 — 0.3.0: the module contract carries the event path, and the kit tracks operations

_Shipped as **0.3.0**, drafted throughout under the working name "0.2.0" — that number was consumed by
a hand-edit to `<VersionPrefix>` and never released (`CHANGELOG.md` `## 0.2.0 — never released`).
References to "the 0.2.0 pass" below and in the archive name the WORK, not a release._


The first harvest-and-adoption-driven work since v0.1.0 (D15), triggered by the first adopter's IPC +
drop-zone design review (`TASKS.md`, 2026-08-01). The verdict on that review: the CLIENT design
already matched its own stated intent — *"a stateful design with an event hub is the way to go —
async from the UI, progress synced"* — `createShenoraStore`'s snapshot-then-deltas model, the
per-subscription `EventBus`, and `invoke` correctly scoped to quick UI-thread-safe calls were all
already right. The HOST contract did not: `Shenora.Ipc` had **zero references to `IEventBus`** while
the kit's own `DropZoneManager` took one as a REQUIRED option, so the bus was already the spine and
the contract simply never admitted it. Design: `docs/2026-08-01-shenora-communication-core-design.md`
+ **D23** (11 tasks, 3 staged stages — the task-by-task plan they were implemented from is removed
per its own "delete once the work lands" lifecycle); what shipped and how it was verified, task by
task: `docs/archive/tasks.md` `### 0.2.0`.

**Stage 1 — contract.** `IModuleContext` (`Module`, `Logger`, `Publish(type, payload?, scope?)`,
`Start(OperationOptions)`, `Run(OperationOptions, work)`) is now the second parameter of
`BaseFacade.RouteMessageAsync` — the release's one breaking change, mechanical and accepted pre-1.0
(every override needs the parameter added; ignore it if unused). A mid-plan user steer mattered here:
*"we still allow for custom events so this is more like a context for every module/facade"* — the
context is the MODULE's context, not an operations entry point. `Publish` needs no registry and no
opt-in; `Start`/`Run` are the one opt-in thing the same context offers, present only once
`AddShenoraOperations()` is called. Both fail LOUD — naming the exact fix — when the corresponding
dependency was never supplied to `BaseFacade`'s two new optional constructor parameters
(`IEventBus?`, `IOperationRegistry?`), rather than silently no-op-ing, which would have been the
"mistyped resource prefix degrading to an all-404 provider" class of bug this repo keeps fixing.

**Stage 2 — operations.** A tracked-operation primitive in `Shenora.Ipc.Operations`, harvested
MECHANISM-ONLY from a private sibling's 320-line process registry (a second sibling's
`JOB_UPDATED`/`JOB_PROGRESS` archetype was the second data point the `generic-library` two-app bar
asks for): id, owning module, app-defined `Kind`/`Scope`, status, progress, idempotent finish, bounded
history, cancel-by-id, and progress emission throttled to `ProgressInterval` (default 100 ms) with a
TRAILING emit so the final value in a window is never dropped — lifecycle transitions (start,
complete, fail, cancel, interrupt) are never throttled. Two races surfaced and were fixed during the
build, not after: `Cancel` on a non-cancellable operation used to flip status while the body ran on
underneath it (now refused, the same honest shape as an unknown or already-terminal id), and the
trailing-emit flag was reset only on the success path, so a faulting `TimeProvider` left it stuck
`true` forever, silently muting every later `Report` on that operation — fixed by resetting it in a
`finally` covering every exit. An operation failure obeys the identical no-raw-exception-text
boundary as a response: unexpected exceptions cross as `UnknownError` + the exception type name only.
`OperationsFacade` (`LIST`/`CANCEL`/`CLEAR_FINISHED`/`RESUME`) and `AddShenoraOperations` (opt-in —
an app with no long-running work pays nothing) round out the host side; `@shenora/react`'s
`useShenoraOperations`/`createOperationsStore` is the client half, a host-backed `createShenoraStore`
instance with `running`/`finished` derived getters. Two things the design sketched and this stage
deliberately did NOT ship, recorded as known limits rather than oversights: `IOperationRegistry.Find(id)`
(no consumer resolves a handle from a bare id) and `byModule`/`byScope` client selectors (a one-line
consumer selector over `byId` — shipping indexes would be duplicated derived state). Also cut during
review, for the same reason: a protected `Events`/`Operations` accessor on `BaseFacade` that the
original plan sketched — no scenario needed it once `Context` existed.

**Stage 3 — channel.** The transport-neutral half of the outbound notification pipeline — bus
subscribe, per-channel filter, bounded drop-oldest queue, batch building, guarded per-notification
serialize, the ready gate — moved out of `WebViewIpcBridge` into `Shenora.Ipc`'s new
`NotificationPump`, so a second, non-WinForms base inherits the already-fixed P5.5 H2/H3 bugs instead
of re-earning them (D16's "the seam, not the package" finally applied to the HOST half of the outbound
path; the client half, `ShenoraTransport`, has been base-agnostic since P3). The pump owns no timer
and no transport — which thread may touch a base's client is a base-specific fact — so
`WebViewIpcBridge` is now a thin adapter keeping only the `Forms.Timer`, the WebView2 event wiring and
`PostWebMessageAsString`; option names (`NotificationInterval`, `MaxQueuedNotifications`) and public
behaviour are unchanged. New: per-channel `Filter`/`NotificationFilter` — every bridge previously
subscribed with `SubscribeToAll`, so two windows meant every bus event reached both.

**Verified against the real sample, not just unit tests.** `SampleFacade`'s `SLOW` route was rewritten
onto `ctx.Run`, and `node devtools/dev.mjs responsiveness` — a newly tracked probe, rebuilt from the
v0.1.0 one-off shell session so the numbers stay re-runnable — measured 0/65 unresponsive samples
(0 ms longest stall) for the streamed shape across repeat runs, matching the v0.1.0 baseline (0/95);
the unchanged `block` anti-example still stalls ~2978–2989 ms of a 4000 ms window. The probe itself
was hardened mid-task after review found it proved only that a click LANDED, not that the operation
STARTED (a WebView2 render surface swallows a stale-coordinate click and still reports "click ok"): a
fourth guard now polls the window title for a marker `SampleFacade` sets synchronously before the
slow work begins, and the guard's own refusal path was proven live before trusting it.

**Docs pass, closing the loop the review opened.** `docs/ADOPTION.md`'s drop-zone finding closed
alongside the code: `DropZoneManager` is Stage-1-adoptable STANDALONE (it depends only on
`Shenora.Core`'s `IEventBus`, the WebView2 control and a `Form`, referencing no `Ipc` type at all) —
only `DropZoneFacade`/`useDropZone` are the Stage 3 IPC half, which the guide had previously filed the
whole component under. `ARCHITECTURE.md`, `.claude/knowledge/ipc-contracts.md` (the invariants earned
here, each with its reason), `CHANGELOG.md`, both READMEs, and the design/plan docs' status lines were
brought current in the same pass, and `<VersionPrefix>` moved to `0.2.0` — the only version source.
One finding from the same review remains open in `TASKS.md` (drop zones as the kit's strongest dedup
case, worth stating more plainly than one table row).

## Remaining

> **NOTHING IS REMAINING. v0.1.0 shipped on 2026-07-31** — five NuGet packages + `@shenora/react` on
> npm, through the manual Release workflow. P1–P7 are all complete, and every section below is kept
> for the record of what each phase set out to do and which carry-overs it decided against. The open
> backlog is `TASKS.md` (two standing habits); the task-level record is `docs/archive/tasks.md`.
>
> **What "next" looks like from here is not a phase.** Growth is harvest-driven (D15) and
> adoption-driven: work arrives when a sibling app adopts the kit and hits something, or when a
> feature worth generalising emerges while building one. The candidates that were deliberately NOT
> built are under `### Later / candidates` at the end.

### P1 — Skeleton hardening (short tail) — COMPLETE

Both original bullets were done in P2/P1.1 (`0776f37`), and **P1.2 closed with the real release**:
OIDC trusted publishing is validated by v0.1.0 having shipped through it. Its own premise was wrong
and the workflow was changed rather than the plan — `draft=true` is not a dry run (it only affects
the GitHub Release, while both registry pushes precede it and are permanent), so a genuine `dry_run`
input was added.

### P2 — Core host extraction (brief Phase 2) — COMPLETE except deliberate carry-overs

Everything above landed (increments 1–6, see Done). Carried forward on purpose:
- **DPI tail → P4** (`OnDpiChanged` handling + CSS-px→physical conversion) — lands with the
  overlay components that need it (drop zones, login windows).
- **Optimized form / frameless chrome → P4** — lands with the window manager + frontend window
  commands.
- **Stable-chunk frontend build guidance** (docs) → written with the P3 `@shenora/react` docs,
  where frontend build advice naturally lives.

### P3 — IPC extraction (brief Phase 3) — COMPLETE

Everything landed (increments 1–5, see Done): envelopes/errors/serializer defaults, dispatcher +
facade base + event bus, the WebView2 postMessage transport, the `@shenora/react` client, and
the live round-trip e2e. Carried forward on purpose:
- **Stable-chunk frontend build guidance** (docs for consuming apps: vite `manualChunks`, hashed
  assets vs the no-cache HTML policy) → lands with the P6 adoption docs, where a real consumer
  exercises it. Drop-zone hook + window-command helpers were always P4 surface.

### P4 — Modules + native services (brief Phase 4) — COMPLETE

Everything landed (increments 1–6 + phase review, see Done): scoped-container router + the
standard IPC composition, frameless chrome + frontend window commands, the native services
(dialogs/shell/clipboard/interaction), drag-drop zones + `useDropZone` (+ the P2.3b DPI tail),
secondary windows + tray, and the live sample/e2e proof.

### P5 — Auxiliary browser sessions (`Shenora.WebView2.Sessions`, D14) — COMPLETE

Everything landed (increments 1–4 + phase review, see Done): the one browser-configuration path
(`SessionBrowser` + init-timeout guard), the bounded LIFO render-session pool, the login-window
stack (`LoginWindow`/`LoginWindowController`/`CookieLoginFlow` — per-provider/per-account
profiles, silent refresh, clear-on-logout) and co-browse streaming (`CoBrowseSession` — CDP
screencast frames out, input dispatched back, human-solved by design), in its own package with a
live sample demo.

### P5.5 — Consolidation: cleanup, re-layer, roadmap revisit — **COMPLETE 2026-07-31**

> **STATUS (2026-07-30, end of the second consolidation session): H1 · H2 · H3 · H4 · H5 · H6 · H8 are
> DONE. Only H7 and H9 remain.** Fourteen commits across two sessions; `dev.mjs verify` PASSES at
> **428 dotnet + 54 vitest**. See `## Done` above for the per-batch narratives (newest first) and
> `TASKS.md` `### P5.5` for the itemised remainder. Two notes for whoever picks this up:
> **(a)** several of H7's docs-drift items were fixed opportunistically while other batches landed, so
> re-check each against the tree instead of working the list as written;
> **(b)** four surface items were deliberately deferred OUT of H6 and INTO H9 or the H2 tail, with the
> reasons recorded next to them in `TASKS.md` — they are not oversights.

**What this phase is.** P0–P5 put the whole body of the kit down in a short span — five commits,
~8.7k lines of `src/` plus ~4.7k of tests, five packages and an npm client — extraction-first and
phase-gated, but moving fast, and with holes in the verification gate itself (see H5). P5.5 is the
deliberate **consolidation checkpoint**: clean up what that velocity left behind (duplication,
missing guards, convention drift), take the structural correction while it is still free (pre-1.0),
close the gate, and revisit the rest of the roadmap in light of what the pass taught. It is a
planned settling pass, not an emergency — the tree was green throughout.

Consolidation has three strands:

1. **Cleanup** — the first review spanning all of P0–P5 (2026-07-30): six parallel reviewers over
   the five packages, the npm client, the samples and the tree, briefed by `docs/REVIEW-GUIDE.md`.
   The baseline was green (`verify` PASSED at `130d4cd`), so everything found is a LATENT defect
   rather than a regression — which is exactly why it lands before a real app depends on the surface
   (P6) and before the 1.0 SemVer freeze (P7). Full itemised plan with `file:line` anchors:
   `TASKS.md` `### P5.5`, batches H1–H8.
2. **Re-layer** — the structural change below (D19 + D20), which the cleanup's own findings argued
   for and which is only cheap while nothing is published.
3. **Roadmap revisit** — this section, plus the amendments to P6/P7/Later that follow from both.

**And an API-shape correction** (user direction, 2026-07-30 — D21): for a whole application *feature*
the kit ships **primitives + lifecycle hooks, not the product**. `CoBrowseSession` had it backwards —
`DispatchInputAsync(string)` takes the source app's wire protocol as an opaque JSON string and
`ReadHotspotsAsync` encodes a co-browse UX decision, while the hooks that make a feature extensible
are missing (nothing signals the session ending or faulting, so a renderer crash leaves the frame
channel never completed and the app's reader waiting forever). The kit's other two session families
already got this right — the render pool ships the pool and the sample writes its own flow; the login
window keeps policy in a driver seam. Tracked as `TASKS.md` H9, after the re-layer.

**The phase also carries a structural change** (user direction after reading the review, approved
2026-07-30): the two Windows shell packages become one layer — `Shenora.WebView2` depends on
`Shenora.WinForms` — and the portable contracts plus the long-specified-never-built `IUiDispatcher`
move to `Shenora.Core`, so an app's own logic compiles with no Windows reference and a future mobile
shell can implement the same contracts. Design:
`docs/2026-07-30-shenora-relayering-design.md`; decisions: D19 + D20. This replaces the review's
proposed `InternalsVisibleTo`/linked-file workaround — the deduplication fix and the portability
seam turn out to be the same object, so one change buys both. Execution order matters: security
fixes first (H1 + H5), then the re-layer, then the dedup on top — see `TASKS.md`.

The review's own verdict was that the per-package internals are disciplined — the extraction
comments are load-bearing and accurate, the dependency graph holds exactly as documented, the IPC
error boundary leaks no exception text on any traced path, and the wire mirror is correct
field-for-field bar one missing constant. The weaknesses are **at the seams between packages, and
in the gate around them**:

- **Six confirmed P0s** (each re-verified against the code before being recorded): no path
  containment in file-mode serving (arbitrary file read, live in every dev session); the
  frameless-maximize ⇄ window-state seam (a maximized close makes restore a permanent no-op — live
  in the reference composition); `RenderSession` accepting cancellation tokens it never observes
  (one JS-blocked page starves the pool for the process lifetime); `NavigationGuard` — the
  documented SSRF boundary — bypassed by redirects and in-page navigation; `AddMessageDispatcher`
  enumerating facades inside its own singleton factory (StackOverflow, no diagnostic, on the
  documented cross-module composition); and a throwing app `OnLoading` callback leaving an
  unclosable login modal that then vetoes `Application.Exit`.
- **The duplication is causal, not cosmetic.** The UI-marshal pattern is hand-rolled 14 times with
  5 incompatible pre-handle policies — 7 unguarded, and one site carries a comment explaining the
  pre-handle trap then commits it on the next line. And the `Sessions → Shenora.WebView2` edge that
  D14 documents as deliberate is **declared but entirely unused**, which is why `SessionBrowser`
  re-implements browser arguments (re-introducing the CDP env-var gotcha), environment creation, the
  init-timeout guard and settings hardening — and why pooled/co-browse instances have none of the
  `NewWindowRequested`/`PermissionRequested`/`ProcessFailed` policies the host package already
  implements.
- **The gate had holes.** `Shenora.slnx` carries an empty `/samples/` folder (and omits
  `Shenora.Core`), so `verify` never compiled the reference composition or the e2e subject;
  `dev.mjs test <typo>` exited 0 having run nothing; and `check-sensitive` fails OPEN when the
  gitignored pattern file is absent — i.e. the private-name half of the guard never ran in CI.
- **Pre-1.0 surface work** that is far cheaper now than after the freeze: the API baseline doesn't
  gate `protected` members (so `BaseFacade.RouteMessageAsync`, the member every consumer overrides,
  is outside the SemVer gate) or default parameter values; `BaseModuleService`'s typed-payload
  feature type-checks nothing and its documented example doesn't compile; and the reference
  composition has to downcast `IMessageDispatcher` because form-dependent facades have no
  registration seam.

### P6 — Sibling adoption (brief Phase 5) — **COMPLETE 2026-07-31**

> ✅ **P6.1–P6.6 are all done; nothing here is pending.** The narrative entries are under `## Done`
> (newest first). What the phase actually delivered, against the framing below: the library is READY
> and `docs/ADOPTION.md` is the artefact an adopting app's own session works from — this repo never
> edited a sibling, on user direction. Six gaps were found and closed rather than recorded (the npm
> `.d.ts` UMD-global defect, the client's missing catch-all subscription, the absent dispatch
> `CancellationToken`, no synchronous `IEventBus.Emit`, an internal-only `IpcErrorMapping`, and a
> resource seam that could not answer anything but "200, here are all the bytes"), plus module
> release. Everything below is the ORIGINAL framing, kept for the record — its "adopt in the sibling
> first" premise was superseded, and its "smallest host" premise was stale before the phase started.

- Adopt in the newest desktop sibling first (smallest host, gaps already documented), via local
  feed + pinning; keep it runnable at every step. Then evaluate the other two desktop siblings
  and the server-backed app (shell-only profile).
- Feed every "the framework almost fits, but…" back into the API before 1.0.

**Revisited 2026-07-30 (post-consolidation):**
- **Do not start P6 before P5.5's H1–H5.** Adopting against a surface that is about to be re-layered
  (D19/D20) means doing the integration twice, and adopting against the pre-H5 gate means the
  adoption itself isn't verified — `verify` did not even compile the sample until H5.
- **Adoption gains a second dimension: portability.** With D20's contracts in `Shenora.Core`, put the
  adopting app's own facades in a `net10.0` project from day one (H4.3 proves the pattern on the
  sample). That makes the app's logic mobile-shareable as a side effect of adopting, and it turns
  the abstract question "are these the right portable contracts?" into a concrete one answered by a
  real app — feed the answer back as a D20 amendment.
- **The adoption is the real test of the review's fixes.** Several P5.5 P0s were latent-only
  (nothing in-repo triggered them); a real consumer is what proves them fixed rather than merely
  patched — notably the DI composition (facades injecting `IMessageDispatcher`), async disposal of
  singletons, and a relative `--app-root`.


**Scoped 2026-07-31 (survey done, nothing adopted yet) — and the premise above is now STALE.**
The first target is no longer a small host: it has grown an API tier, a plugin system, an MCP server
and a deployment stack, and its desktop side now carries 28 IPC modules against ~148 client
call-sites. It is still the right first target, but not for the reason originally given. What makes
it tractable is that both sides funnel through ONE seam each — a single client post/subscribe pair
and a single host dispatcher behind a one-method module interface — so swapping the IPC substrate is
two ADAPTERS rather than 28 rewrites — **both since written and run against the public surface**
(P6.4, above): expressible, and the exercise found two real defects that the guide alone had not.

**Reframed 2026-07-31 (user direction): this repo readies the LIBRARY and never edits the sibling.**
The adopting app's own session does the adoption, working from `docs/ADOPTION.md`; a sibling is a
CHECKPOINT that answers "is this capability present and safe?", never a spec to mirror. The staged
increments that used to be listed here as work for THIS repo are now that guide's Stages 1–4.
**P6.1/6.2/6.3/6.3a/6.4 are done; P6.5 (portability guidance) and P6.6 (feed back before P7 freezes
SemVer) remain** — see `TASKS.md` `### P6`.

On the model mismatch they bridge: the target speaks flat, uncorrelated, fire-and-forget IPC with an
event stream back. That is **not** a legacy shape to migrate away from — for a desktop shell the event
pipe is the correct DEFAULT and correlated request/response is the special case, because the dispatch
pipeline preserves the caller's synchronization context by design (measured: the same 3 s of work
stalls the UI thread 2 027 ms in-route, 0 ms handed off). So the adapters PRESERVE the model; what
they add is the missing correlation. Per D21 any wire-format compat lives in the ADOPTER's shim and
never in the kit's envelope — a question the 2026-07-30 extraction survey had deliberately left open
until adoption time, now decided.

### P7 — Stabilisation + 1.0 (brief Phase 6) — **COMPLETE 2026-07-31 (shipped as v0.1.0)**

> Every item below is done. **1.0 was NOT cut**, deliberately: five breaking changes landed while
> readying the release, so v0.1.0 ships the stabilised surface and 1.0 stays a separate, deliberate
> freeze once adoption has exercised it. `Shenora.Hosting.AspNetCore` was answered **NO** (D10) — on
> evidence, not deferral. See `## Done` for the narrative and `docs/archive/tasks.md` for the tasks.

- API-surface baseline tests on; docs pass (XML docs, README per package section); CHANGELOG
  discipline from first publish; `Shenora.Hosting.AspNetCore` go/no-go (D10); first NuGet/npm
  publish via the release workflow; GitHub repo goes public.

**Revisited 2026-07-30 (post-consolidation):**
- **"API-surface baseline tests on" is not yet the SemVer gate it is assumed to be.** They dump
  `BindingFlags.Public` only, so `protected` members — including `BaseFacade.RouteMessageAsync`, the
  one member every consumer overrides — are ungated, along with default parameter values, `init` vs
  `set`, `required`, and attributes. P5.5 H6 closes this; 1.0 must not freeze behind a gate with a
  hole in it.
- **Part of the docs pass moves earlier.** P5.5 H7 already corrects the shipped-in-nupkg inaccuracies
  (package descriptions, README claims). What remains for P7 is genuinely new writing: per-package
  README sections, the XML-doc sweep enabled by turning CS1591 back on (H5), and the stable-chunk
  frontend build guidance carried over from P2/P3.
- **CHANGELOG discipline starts now, not at first publish** — the log is already missing the one fix
  that changed a published artifact's importability (`0776f37`), which is exactly the class of entry
  the discipline exists for.

### Later / candidates

- `Shenora.Hosting.AspNetCore` (SPA static policy, loopback-gated endpoint helpers) — D10.
- Mobile transport adapter (Capacitor or similar speaking the same IPC envelope) — D16; packaged
  at first mobile adoption (`@shenora/capacitor` vs an adapter in `@shenora/react`). **Revisited
  2026-07-30:** the decision point is unchanged (first real mobile adoption), but the .NET-side
  surface such a shell would implement is now enumerated rather than hypothetical — D20's portable
  contracts in `Shenora.Core` (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`,
  `IUiInteraction`). D16 covers the transport seam; D20 covers the feature seams. Neither ships an
  implementation until there is a consumer.
- Harvest-promotions from ongoing app development (D15) — any proven-nice feature gets
  generalized and lands here as a task before shipping in a minor.
- C++ launcher template (runtime check/install, staged self-update) as a repo template, not a package.
- Scaffolding skills once patterns exist (`new-ipc-module`, `new-native-service`).
- Contract codegen (C# ⇄ TS) — explicitly out of initial scope; revisit after adoption feedback.
