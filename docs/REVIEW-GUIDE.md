# REVIEW-GUIDE.md — orientation for a full code review of Shenora

Written to hand a whole-codebase code review the context it needs without re-deriving it. Read this,
then review `src/` against the invariants it points at. Nothing here overrides the design contract or
the rules — it routes you to them and flags what's already settled.

> **A review that only asks "does this work?" has done HALF the job.** Owner direction, 2026-08-01,
> after a review that found real defects and still missed the point: *"usually if you do the code
> review, you should be getting the purpose of the project rethinking if this is a good design,
> instead just check if the code itself works or not."* The failure mode is specific and easy to
> repeat — that review audited the kit against **its own stated intentions** and never asked whether
> those intentions were right, so a doc asserting a design was treated as context rather than as the
> claim most worth attacking. So on every pass, spend budget on **§1's lens** as well as §4's hot
> spots, and ask of anything load-bearing: does this earn its place for the PURPOSE (§1), or only for
> the design it already committed to? The four findings that came out of asking that (D1–D4,
> `docs/archive/tasks.md` `### 0.2.0 design pass`) were each bigger than anything the correctness pass
> found — one cut a whole feature half, one was a REJECTION with a narrower change in its place. Note
> both directions: "this design is wrong" and "this complaint is fair but the fix is worse" are
> equally valid outcomes, and only the second needs a `DECISIONS.md` entry so it stays rejected.
>
> **Three full reviews have already run. Verify and EXTEND them — do not re-derive them.**
>
> 1. **The P0–P5 review** (at `130d4cd`) — ~60 findings, executed as batches H1–H8 and now closed;
>    the record is `docs/archive/tasks.md` `### P5.5`, summarised in `docs/ROADMAP.md` `### P5.5`.
> 2. **The whole-codebase review** (2026-08-01, before 0.2.0 was published) —
>    `docs/archive/tasks.md` `### 0.2.0 — whole-codebase review`.
> 3. **The design pass** (2026-08-01, same day, prompted by the direction above) —
>    `docs/archive/tasks.md` `### 0.2.0 design pass`. Its four verdicts are settled; D24 records the
>    rejection. Don't re-open them without new evidence.
>
> **What the second one found is the more useful hint about where to spend YOUR budget.** It found
> nothing in the threading, UI-thread marshalling, resource-ownership or IPC-error-boundary hot spots
> §4 below points at hardest — those have been reviewed repeatedly and it shows. Everything real was
> in the surface no gate looks at: **docs that SHIP** (XML/JSDoc, the README — read by consumers,
> compiled by nothing), **the parts of the public surface a gate is structurally blind to** (the npm
> barrel test compares `Object.keys`, so it cannot see a missing `export type`; the D22 domain-word
> audit sweeps the API baselines, so it cannot see a csproj `<Description>`), and **the kit failing to
> follow its own earned rules at one site out of N** (one unguarded app callback in a timer tick; one
> emitter still using the discard shape a member was added to replace).
>
> So: treat a claim in a doc as a claim to CHECK against the code, not as context. Where an invariant
> has a gate, ask what that gate cannot see. And when the kit states a rule in a knowledge file, grep
> for the sites that don't follow it — the exceptions are where the defects were.

## 1. What Shenora is (the review lens)

Shenora (神阙) is a **reusable library, not an app**: the desktop "body" (WinForms + WebView2 +
React hosting, typed IPC, modules, native services, auxiliary browser sessions) for a family of
Windows apps, shipped as NuGet (`Shenora.Core|Ipc|WebView2|WebView2.Sessions|WinForms`) + npm
(`@shenora/react`), versioned in lockstep.

> **The owner's standing criterion, in their words (2026-08-01) — this is the review, everything
> below is detail:** *"make sure this is a library — we're not solving specific business logic.
> Everything we have here needs to be generic enough that our application can adopt it. And we focus
> on making things work — like the frameless form: it's a better visual design using the tech we
> have, and gives a better UI."*
>
> Two tests, and a piece of work has to pass **both**. **Generic enough to adopt** is the veto: any
> sibling app must be able to take it as-is, so an application's concept leaking into `src/` is a
> defect no matter how well written. **Better than what the app would have built** is the positive
> test, and it is the one reviews forget — the kit does not exist to deduplicate code, it exists so
> every app gets a *better shell* than it would have hand-rolled. `OptimizedForm` is the reference
> case: frameless chrome is not a feature any app asked for, it is the kit using the tech available
> to raise the visual bar for all of them at once. So "this is generic" is not sufficient — ask what
> the adopting app actually GAINS, and if the honest answer is "nothing, it's just shared", that is
> a finding.

Two properties follow from that and shape every judgement:

- **It is EXTRACTED, not invented.** Code is lifted from proven in-house sibling apps, keeping
  their post-mortem comments and fixing a listed set of gaps. So "why is this shaped like the
  source?" usually has a measured answer — check `.claude/knowledge/extraction-sources.md` and the
  per-port comments before calling a shape wrong. New abstractions with no sibling precedent are
  the ones to scrutinise hardest.
- **It is a LIBRARY with a public surface.** Every exported type becomes SemVer surface at 1.0.
  Two hard constraints: (a) **generalize, don't ship the consumer's shape** — no app/domain
  vocabulary in `src/`, seams over flags, options records over magic values
  (`.claude/knowledge/generic-library.md`); (b) **headless (D13)** — NO dependency on any UI
  component library, anywhere.
  **(a) now has a tripwire** — `SurfaceVocabularyTests` checks every public TYPE name against an
  allow-list of shell/platform words (`tests/Shenora.Tests/Api/surface-lexicon.txt`), so a domain
  noun fails the build instead of waiting for a reviewer to notice. Note what it does NOT cover:
  member names, parameter names, csproj `<Description>`s, and the docs — and it says nothing at all
  about the second test above. **A green suite is not evidence that a component earns its place.**

Design contract: `docs/2026-07-30-shenora-design.md`. Load-bearing choices: `docs/DECISIONS.md`
(D1–D22 — numbered; don't relitigate, they record *why*). **D21 + D22 are the newest and the most
likely to look like violations:** a feature ships as primitives + lifecycle hooks rather than the
product, and every public type is named for its MECHANISM (so `InteractiveSession`/`StreamingSession`
are deliberately not named after logging in or co-browsing). D19 + D20 changed the package layering
(D19 + D20 — implemented in P5.5, so the tree matches them). As-built map + full public surface:
`docs/ARCHITECTURE.md`. Phase-by-phase narrative of what changed and how it was verified:
`docs/ROADMAP.md` `## Done`.

## 2. What exists (commit-by-commit)

| Commit | Phase | Adds |
|---|---|---|
| `34add37` | P0–P2 bootstrap | Repo/docs/devtools skeleton; `Shenora.Core` host + builder + paths + env; `Shenora.Windows` bootstrap/window-state/single-instance; `Shenora.Windows` hosting + serving; the sample app |
| `eeb23f7` | P3 | The full typed IPC stack: `Shenora.Ipc` contracts + dispatcher + facades, `Shenora.Core` event bus, `Shenora.Windows` postMessage bridge, the `@shenora/react` client, live round-trip |
| `43f18ad` | P4 | Scoped-container router + IPC composition; frameless chrome + window commands; STA dialogs/shell/clipboard/interaction; drag-drop zones + `useDropZone`; secondary windows + tray |
| `0776f37` | P1.1 | Local-feed consumption smoke — caught + fixed a real npm ESM packaging bug (extensionless imports) |
| `4ebb8e0` | P5 | `Shenora.Windows`: session browser, render-session pool, login windows, co-browse streaming |

Layout: `src/` (5 packable projects + `Shenora.React/`), `tests/Shenora.Tests` (one project, folders
mirror src), `samples/` (desktop + web; the e2e subject), `devtools/` (one-entry dev loop). Detail
per package is in `docs/ARCHITECTURE.md` — this guide does not duplicate it.

## 3. Invariants by area — where "correct" is DEFINED

The knowledge rules encode the earned invariants. Read the matching rule before reviewing an area;
a finding that contradicts one of these is either a real regression or a rule that needs updating.

| Area (files) | Invariant source | What to check |
|---|---|---|
| IPC stack (`src/Shenora.Ipc/`, `WebViewIpcBridge`, `Shenora.React/src/`) | `.claude/knowledge/ipc-contracts.md` | C#⇄TS wire mirror in lockstep; **no raw exception text on ANY error path** (only `OperationException`/error codes cross the bridge); `DispatchAsync` never throws / never returns null; notifications always batched; ready-gate resets on navigation; camelCase wire via the frozen `IpcJson` options |
| WebView2 hosting/serving (`src/Shenora.Windows/`) | `.claude/knowledge/webview2-hosting.md` | environment thread-affinity; `IsHandleCreated` checked BEFORE `InvokeRequired`; non-blocking `BeginInvoke` (never blocking `Invoke` off the UI thread); init-timeout guard; sync-bundle vs deferred-scheme serving split; JSON-escaped script injection; CDP arg re-append (the env-var-ignored gotcha) |
| Any public API / naming / new type | `.claude/knowledge/generic-library.md` + D13 | generalized shape (no consumer vocabulary), options records, seams; every public type earns its keep; no UI-component-library dependency |
| Extraction ports (all of `src/`) | `.claude/knowledge/extraction-sources.md` | post-mortem comments kept; the listed gaps actually fixed (no `as dynamic`, no static mutable registry, `ILogger` not console, async-interleaved dispatch not `Task.Run`-per-message) |
| Missions (`src/Shenora.Core/Missions/`) | `docs/DECISIONS.md` D27–D29 | claims declared as a SET (never acquired one at a time — that is the deadlock the design removed); work never runs under the scheduler lock; a policy is consulted only AFTER admission, so it can delay but never corrupt; a chain is one entry holding its claim UNION, stronger mode winning; the queue's pending list stays internal and synchronous (D28 records why a pluggable async queue was rejected) |
| File updates + locking (`src/Shenora.Core/Io/`) | `docs/DECISIONS.md` D30–D31 | **the journal is written BEFORE the mutation** — a plan written after is missing exactly the interrupted change, which is why undo is DATA and every change is planned then applied; recovery rolls back `Applying` and FINISHES `Committing`; undo steps check the world first (safe to run twice); leases are taken after the in-process gate, in sorted path order; lock files never land in the managed tree; `WhoHolds` empty means "cannot tell", not "nobody" |
| Windows/build/shell | `.claude/rules/windows-dev-gotchas.md` | PS5 UTF-8/BOM traps; `fs.cpSync` avoided; WinForms `AllowDrop`/OLE handle-creation must be on an STA thread (xunit workers are MTA) |
| Any tracked file / commit message | `.claude/rules/sensitive-info.md` | NO absolute local paths, NO private sibling names, NO personal/network data (this repo goes public) |

## 4. Highest-risk areas — where to spend the review budget

1. **UI-thread marshalling (the whole stack, acute in `Shenora.Windows`).** Every
   WebView2 touch marshals to a WinForms UI thread via `BeginInvoke` + `TaskCompletionSource`.
   Look for: blocking `Invoke` off the UI thread (a measured AppHang in the family);
   `InvokeRequired` checked without `IsHandleCreated` first (pre-handle it lies); `async void` /
   `BeginInvoke(async …)` bodies without exhaustive try/catch (unobservable UI-thread crashes);
   TCS double-completion; `ConfigureAwait` polarity (true inside UI bodies, false off them);
   dispose racing in-flight ops; semaphore permit leaks. (P5's phase review already fixed a batch
   here — see §5 — but this is the standing hotspot.)
2. **The IPC error boundary.** The single most important contract: raw exception text must never
   reach the client. Trace every `catch` in the dispatcher, facades, the bridge, and the sample
   routes — expected failures become `OperationException`/structured codes; unknowns become
   `UNKNOWN_ERROR` with details kept host-side only.
3. **WebView2 lifecycle & resource ownership.** Controls, forms, CTS, `SemaphoreSlim`, channels,
   and event subscriptions (`WebMessageReceived`, `NavigationCompleted`, DevTools receivers,
   `DownloadStarting`, `NewWindowRequested`) — is every subscription detached and every
   `IDisposable` honored on every path incl. exceptions? The session pool's create/return/discard/
   dispose paths and `StreamingSession.DisposeAsync` are the densest.
4. **Packaging & versioning.** One `<VersionPrefix>` in `src/Directory.Build.props` is the only
   version source (npm/README synced by tooling, never hand-edited); `devtools/project.config.mjs`
   `packableProjects` must list every packable project; the npm package must resolve under **native
   Node ESM** (the P1.1 bug — bundler resolution hides it). `node devtools/dev.mjs pack` must
   produce all five nupkgs + the tarball.
5. **Sensitive-info.** Public repo. The pre-commit guard scans staged changes, but review tracked
   content too — any dev path, private sibling name, or personal data is a history problem once
   committed.

## 5. Already settled — do NOT re-raise these

**Accepted design deviations (with rationale in `docs/DECISIONS.md` / port comments):**
- `Shenora.Core` depends on the Microsoft DI *implementation* package, not just abstractions (D17 —
  the builder needs `BuildServiceProvider`).
- `WindowCommandFacade` / the drop-zone stack live in `Shenora.Windows` (not `WinForms`) because
  they need `Shenora.Ipc`, which `WinForms` deliberately does not reference.
- `Shenora.Windows` depends on `Shenora.Windows` (D14 keeps the session stack out of the
  core hosting package), and `Shenora.Windows` depends on `Shenora.Windows` (**D19** — the two
  Windows packages are one layer: primitives, then web hosting on top). Both edges are deliberate, so
  neither is a violation; the old "never sideways" rule was retired on evidence. `WinForms` →
  `WebView2` is still forbidden, and `WinForms` still carries no `Ipc` dependency. Note the Sessions
  edge is currently **declared but unused**: nothing in the package imports a `Shenora.Windows`
  type, which is `TASKS.md` H4.4.
- The portable contracts (`IUiDispatcher`, `IFileDialogs` + models, `IClipboardService`,
  `IUrlLauncher`, `IUiInteraction`) live in `Shenora.Core` with their implementations in
  `Shenora.Windows` (**D20**), and `WinFormsUiDispatcher` is public deliberately — a
  `ProjectReference` does not grant `internal` access, so the alternative was `InternalsVisibleTo` for
  two packages.
- `StreamingSession` reuses `SessionController` as a **background** controller (the source's own
  pattern); its window-managing calls are gated inert by a `foreground` flag.
- `PayloadHelper` is static; `IpcResponse.category` is lowercase; notifications are always batched —
  all documented deviations from the source shapes.

**P5 phase-review findings already fixed** (full list in `docs/ROADMAP.md` P5-close entry): the
foreground/background controller split (hold-close no longer vetoes `Application.Exit`); pool
init-failure/dispose leaks; a `SemaphoreSlim.Dispose()`-races-cancelled-waiter hang
(`docs/archive/fix-log.md`); silent-refresh ownerless modal; loading-splash fallback; drag button state;
cached co-browse viewport; request-filter `about:blank` page-source; init-timeout on env creation;
sample lease timeout; the pack/README packaging gap; controller taps accumulate.

**Deliberately deferred** (recorded in the private notes; not defects to file):
- ~~Renaming the login-named types to session-neutral names~~ — **DONE, and it was not optional after
  all** (P5.5 H4.6 then H9.7/H9.8, now **D22**). A reader asked why the library had login-specific
  business logic; it did not — `LoginWindow` held no login logic — but the NAMES made it look like it
  shipped that product, and `SessionController.GetCookiesAsync` returned `IReadOnlyList<LoginCookie>`,
  forcing a streaming consumer to name a login type. See D22 for the rule and the audit method.
  (This bullet used to end "do NOT re-raise `CookieLoginFlow`, which keeps its scenario name
  deliberately as the one reference driver" — P7 reversed exactly that: a scenario name in `src/` is a
  PLACEMENT smell, so the driver moved to the sample and the kit ships none. `generic-library.md`
  carries the amended rule.)
- STA-wrapping the new pool/session tests — the earned STA rule's trigger (`AllowDrop`/OLE) does not
  apply to those forms, and the tests are deterministically green.
- `InteractiveSession`'s busy gate can be released by the cancellation fallback a beat before `ShowDialog`
  returns — the driver's linked token closes the window promptly, so the stacked-dialog race window
  is tiny.

## 6. What's verified vs what's not (so thin coverage isn't mistaken for a gap)

- **Gated by tests:** the public surface is pinned by API-surface baseline tests
  (`tests/Shenora.Tests/Api/Baselines/*.txt` — drift fails the build). Since H6 that gate is
  thorough, not nominal: `protected` members (including `BaseFacade.RouteMessageAsync`, the one every
  consumer overrides), default parameter values, `init`-vs-`set`, `required`, `static`, virtuality,
  parameter NAMES, generic constraints, nullability, base types, const VALUES and attributes — all 22
  `[JsonPropertyName]` wire names included, so a rename cannot break the C#⇄TS mirror silently. A
  companion test walks transitive references so a NEW package cannot ship without a baseline. The npm
  half has its own gate since H7 (`index.test.ts` pins the barrel's 21 runtime exports as an explicit
  array — not a snapshot, which would self-update under `-u`). Unit tests cover the pure/
  seam-testable logic: Core env/paths/builder/event-bus, Ipc envelopes/dispatcher/facade/router/
  payload, WinForms dpi/window-state/single-instance/dialog seams, WebView2 bridge + drop-zone
  seams, React bridge/hooks/services/transport, Sessions pool accounting (via factory/reset seams),
  login gate mechanics, cookie-flow freshness logic, the co-browse protocol builders, and the
  session request-filter decision (`SessionBrowser.ShouldBlockRequest`).
- **The missions + `Io` layer is unusually well pinned, and three of its tests exist because a
  sabotage exposed a worthless one** (2026-08-02). The concurrency suite proves exclusion AND
  parallelism in the SAME run, because either alone passes a broken implementation. The chain
  claim-union test is a `Theory` over BOTH step orders — as a single case it passed a deliberate
  "last wins" bug. The journal's `A_change_that_LANDED_before_the_crash_is_still_undone` is the only
  test that distinguishes a write-ahead journal from a write-after one; the other seven pass either
  way. **If any of those is ever "simplified", it stops testing the thing it names.** Crash recovery
  is tested by freezing the filesystem + journal at the moment of death and recovering with a fresh
  queue — not by catching an exception, which is the opposite of a crash.
- **Not covered by tests, by nature:** the Restart Manager inspector's answer for a REMOTE holder
  (needs a second machine), lease behaviour over SMB after a hard power loss (needs the hardware),
  and whether `DeleteOnClose` semantics hold on POSIX (the implementation targets Windows and says
  so). Each is stated as a limit in the XML rather than assumed.
- **The sessions live-browser boundary — read this before filing "untested public member".**
  `SessionController`'s constructor subscribes to `_web.CoreWebView2.WebMessageReceived`, so the type
  cannot be INSTANTIATED without a real browser core. Everything reachable only through an instance is
  therefore e2e/manual territory by construction, not by neglect: `SessionController`'s public members
  (bar `ComputeFitSize`, which is tested), `StreamingSession.DispatchAsync`/`Frames`/`DisposeAsync`, `RenderSession`'s tap BOOKKEEPING (its disposal checks *are* tested), and
  and — until P7 moved it out of the kit — a reference driver's four-line `SessionController` →
  `Hooks` mapping. The lesson H7 applied where it
  COULD: pure decisions get lifted out of live-object lambdas so the real rule is testable — that is
  what `ShouldBlockRequest` and the pool's `AwaitResetNavigationAsync` are. Prefer that over a mock of
  `CoreWebView2`.
- **The chrome that only the OS can exercise — also by construction, not neglect (P5.6).** Two things
  about `OptimizedForm` cannot be built in-process and are verified live instead. **Input ROUTING:**
  which window receives a click is decided by `WindowFromPoint`, so a `SendMessage` test proves the
  window's DECISION and nothing about whether the OS ever asks — that gap is what let P5.6 ship
  broken once with a green suite. It is closed by asserting the covering child's `Region` really has
  the cluster cut out (unit-testable) plus a live `WindowFromPoint` probe and a human pass for the
  Snap Layouts flyout. **Aero Snap:** `WINDOWPLACEMENT.rcNormalPosition` only diverges from the live
  window rect once the OS has actually docked the window, and snapping is a shell gesture, so
  "maximize+restore exits the snap" is proven live (Win+Left → SC_MAXIMIZE → SC_RESTORE, asserting
  the pre-snap rectangle). The unit tests pin the ordinary case. Don't file either as a coverage gap.
- **Proven live (e2e), not unit-tested:** real WebView2 behavior is driven against the sample via
  CDP (`window.__shenora`) + `win-input` + `wgc` screenshots — hosting, IPC round-trip,
  notifications, window commands, drop-zone registration, secondary windows, and the P5 render-
  session pool round-trip. This split is the family precedent: real browser processes are the
  sample's job, not the unit suite's.
- **Manual / not yet exercised:** real interactive login, real co-browse screencast+input streaming,
  real file dialogs/clipboard, and frameless-chrome visuals — these need a human or a real provider
  and are validated by hand. Judge their code by inspection against the source + the invariants,
  not by expecting a test.

Current totals: **442 dotnet + 63 vitest** green; `verify` PASSED; `doctor` consistent. The dotnet
suite runs SERIALLY (`tests/Shenora.Tests/xunit.runner.json`, P5.5 H7) — it creates real STA message
pumps, real OS mutexes and real window threads, and collection parallelism was both a flake vector and
an active mask: it hid a test that entered the OS modal size loop for ~17 s.

## 7. Reproduce / verify locally

```
node devtools/dev.mjs verify   # build · test · sensitive scan · knowledge check — the "am I done?" gate
node devtools/dev.mjs pack     # all five nupkgs + the npm tarball into publish/packages
node devtools/dev.mjs sample --dev   # run the sample against Vite with CDP (e2e subject)
node devtools/dev.mjs vite      # the sample's Vite dev server (port 3900)
node devtools/dev.mjs shot|wgc|click|input   # desktop capture/input without stealing focus
```

Report findings as file:line + why it's real (a concrete failure scenario), ranked by severity —
no style nits. If a finding contradicts a rule in `.claude/knowledge/`, say which, so the invariant
can be corrected rather than silently worked around.
