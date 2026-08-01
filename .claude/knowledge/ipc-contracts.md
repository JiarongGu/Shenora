# IPC contract invariants — the wire rules the P3 stack encodes

The envelope contract is FIXED (design §5, D11/D16) and both sides ship from this repo:
`src/Shenora.Ipc` + `src/Shenora.WebView2/WebViewIpcBridge.cs` (host) ⇄
`src/Shenora.React/src/types.ts|bridge.ts` (client). Read before touching any of them, adding a
transport, or building the P6 adoption shims.

## The rules

- **The C# and TS wire types are mirrors — and a TRIPWIRE, not care, keeps them so.**
  `WireMirrorTests` parses the TS source and asserts set equality for the error codes, the handshake
  route and the envelope categories. It exists because "both sides are tested" was FALSE comfort:
  each suite asserted its own hand-written literals and nothing compared the SETS, so `SCOPE_REQUIRED`
  lived in the host and was emitted for two phases while missing from `types.ts`. A code that is
  genuinely client-only goes in the exported `ClientOnlyIpcErrorCodes` — declare the exception on the
  client, never as a second list inside the test. Names are pinned with `[JsonPropertyName]` (host) —
  now inside the API baseline, so a rename is a gate failure — and interface fields (client).
- **A green tripwire that cannot fail is worth nothing.** After adding one, BREAK the thing it watches
  and confirm the message it prints (both mirror checks and the `@ts-expect-error` generic pins were
  verified that way). And make a parser self-check (`Assert.NotEmpty`) so a regex that silently matched
  nothing can't pass for the wrong reason.
- **`@ts-expect-error` assertions are INERT unless something type-checks the tests.** The npm build
  config excludes test files and vitest transpiles without checking, so the client's typed-service pins
  proved nothing until `npm run typecheck` (the full tsconfig) was wired into `dev.mjs verify`.
- **A RUNTIME export gate proves nothing about TYPE exports — the npm surface needs both halves.**
  `index.test.ts` pins the barrel by comparing `Object.keys(barrel)` against an explicit array, which
  is the right shape for values and structurally blind to `export type`: a type has no runtime
  binding, so deleting one from `index.ts` passes every assertion in that file while breaking every
  consumer that named it. Found live (whole-codebase review, 2026-08-01): `OperationInfo.progress` is
  typed `OperationProgress`, `OperationInfo` was exported and `OperationProgress` was not, so the
  field's own type was unnameable from outside the package for a whole release — and the only visible
  symptom was the kit's OWN sample re-declaring the shape inline rather than importing it. The fix is
  a second pin in the same file: a type-only `import type { … } from './index.js'` consumed by a tuple
  alias, which `npm run typecheck` compiles (per the bullet above — without that step it would be
  inert too). Verified the standing way, by sabotage: dropping the export fails the typecheck naming
  the type. **When a package exports types, pin the types.**
- **A typed request map is constrained to `object`, never `Record<string, unknown>`.** The stricter
  bound is unsatisfiable by a plain `interface` (no implicit index signature), so the documented example
  did not compile; and satisfying it widens `keyof TRequests & string` back to `string`, which makes
  typos compile and collapses every payload to `unknown` — the feature silently checking nothing.
- **Raw exception text never crosses the bridge (design §5) — EVERY error path.** Wire errors are
  `{code, message?, parameters?}`; unknown exceptions cross as `UNKNOWN_ERROR` + the exception
  TYPE name only, details go to the host log. This holds in `MessageDispatcher.DispatchAsync`/
  `UseErrorHandler`, `BaseFacade`, `PayloadHelper` (the wire message carries only the key — the
  serializer's text lives in the inner exception), and the bridge's own fallback. New error
  paths get a `DoesNotContain` leak test (the suite has precedents).
- **Claim and release are ONE owner's job.** `IModuleRegistry` records the module AND holds the
  routing it installed (`TryClaimModule(facade)` / `TryReleaseModule(name)`), because a registry that
  only remembers a NAME can never take the route out again — which is precisely why release was
  impossible while `TrackMappedModule(string)` was the contract. Two properties the implementation
  owes: the claim is ATOMIC (check-then-map lets two threads offering the same plug-in name both
  win — the silent-shadowing defect reintroduced as a race), and release is SURGICAL — only the
  released module's entry leaves the pipeline, and the relative order of the error handler, logging,
  app middleware and the scoped router is preserved exactly, because that order is load-bearing
  (design §5) and reordering it fails in ways that do not look like an ordering bug. Release removes
  the ROUTE and nothing else: in-flight requests finish, and the facade is NOT disposed (its lifetime
  belongs to whoever built it — usually DI).
  **KNOWN LIMIT the registry does NOT cover, and it is the composition path most apps use:
  DI-registered facades are invisible to it** (whole-codebase review, 2026-08-01).
  `AddMessageDispatcher` maps them through `MapRegisteredModulesLazily` — one terminal middleware
  resolving them on the first dispatch — precisely because claiming a name needs to READ the names,
  and resolving facades inside the `IMessageDispatcher` singleton factory is the silent
  `StackOverflow` the bullet further down describes. So `IsModuleMapped("OPERATIONS")` is `false`
  while `OPERATIONS` is routed, and `TryMapModule` answers `true` for a name a DI facade already owns
  — after which the plug-in never runs, because the lazy middleware is composed earlier and answers
  first. That is the silent-shadowing defect this whole seam exists to prevent, re-entering through
  the composition path rather than through the registry. The PRECEDENCE is right (the app's own
  modules win); only the answer is dishonest. Recorded rather than fixed: closing it needs a
  name-reservation seam the registry does not have, or re-opening the deadlock. Until a consumer hits
  it, map anything a plug-in must be able to collide with EXPLICITLY (`MapModule(facade)` /
  `TryMapModule`), not through `AddModuleFacade`.
- **The dispatch token is a LIFETIME, not a per-request cancel — and the boundary still never
  throws.** `DispatchAsync`/`SendAsync`/`MessageMiddleware`/`IModuleFacade`/`BaseFacade.RouteMessageAsync`
  all carry a `CancellationToken` (P6.4; before that the whole pipeline was uncancellable, so a handler
  could not observe a token it was never given). The transport supplies it — `WebViewIpcBridge` owns a
  CTS and cancels it in `Dispose`, FIRST, before tearing anything else down. Three rules that follow:
  an already-cancelled token is thrown INSIDE the try so it maps to `OPERATION_CANCELLED` like any
  other cancel (one code for one outcome, and the never-throws contract holds); a decorator MUST
  forward the token, since dropping it silently disables cancellation for everything behind it; and
  work a route hands OFF to the background outlives the request, so it needs its own token — capturing
  this one kills long work the moment the page navigates. What the client "cancel this operation" case
  needs is an app-level CANCEL route carrying the operation id, never a transport concern: a one-way
  `post` has no caller waiting.
- **A test that awaits a cancellable operation must be BOUNDED (`WaitAsync`), not bare.** If the token
  ever stops flowing, `await Task.Delay(Timeout.Infinite, ct)` waits on something nobody can cancel and
  the test HANGS instead of failing — the worst outcome here, and the reason the dotnet suite runs
  serially at all (parallelism once masked a 17-second hang). Found by sabotage: swallowing the token
  in `BuildPipeline` hung the whole run; with the bound, five tests failed in five seconds.
- **An `OperationException`'s MESSAGE crosses the wire verbatim — so never build one from
  `ex.Message`.** The no-raw-exception-text rule above has exactly one sanctioned channel through it:
  `OperationException` is the app describing an EXPECTED failure in its own words, and
  `IpcErrorMapping` passes its code, parameters and message through untouched. That makes
  `catch (Exception ex) { throw new OperationException(code, message: ex.Message); }` a complete
  bypass of the boundary — and it is the natural line to write when porting a host whose dispatcher
  did `$"{action} failed: {ex.Message}"`, which is how the P6.4 adapter probe found it (sabotage:
  with the wrapper in place a planted connection string reached the client; without it, the response
  carried only `UNKNOWN_ERROR` + the exception type name). Let unexpected exceptions ESCAPE to the
  boundary; reserve `OperationException` for failures the app can name.
- **The client event bus mirrors the host's `IEventBus` in BREADTH, not just in the wire types.**
  Three levels — exact `(module, type)`, `subscribeToModule`, `subscribeToAll` — because an observer
  that cannot enumerate the vocabulary up front (plug-in-contributed events, a diagnostics tap, an
  adoption shim's legacy firehose) otherwise has no supported expression at all: the client shipped
  only the exact pair for five phases while the host had all three from the start, and
  `WebViewIpcBridge` itself consumes `SubscribeToAll`. Two rules that came with it: delivery is
  **narrowest-first** (exact → module → all), so a broad observer never runs ahead of the feature
  code it observes; and breadth is expressed as **separate collections, never a `"*"` sentinel in the
  key**, or a module an app legitimately names `*` silently becomes a catch-all — the same class as
  the `'\0'`-join collision below, pinned by a test before it could be earned twice.
- **A shipped `.d.ts` must not name a type it did not import.** `UseDropZoneOptions.targetRef` was
  written `React.RefObject<…>` — the UMD global — so the emitted declaration named `React` with no
  import and compiled only when the CONSUMER's program happened to contain `@types/react` globally;
  `"types": ["node"]` produced TS2503 out of a file the consumer cannot edit. Import the type
  (`import { type RefObject } from 'react'`). The reusable half: **a consumer probe only tests the
  configuration it happens to have** — P6.1's npm consumer missed this because its own tsconfig
  pulled the global in, so vary the probe's tsconfig, don't just add another probe.
- **The CLIENT's inbound handler must survive any valid JSON, not just any string.** A host message of
  literal `null` parses fine and then `parsed.category` throws a `TypeError` out of the transport
  listener — an uncaught page error with nothing above it to catch (P5.5 H2; the other primitives never
  threw, since property access on them just yields `undefined`). Narrow to a non-null object before
  reading the envelope, and treat every unknown shape as "not ours" for forward compatibility.
- **A `getBridge()` DEFAULT must be resolved per call, never captured at construction.**
  `configureBridge` DISPOSES the bridge it replaces, so anything that captured the previous default —
  a `BaseModuleService` singleton built at module scope, the normal way to write one — rejects every
  later request with "Bridge disposed" for the rest of the session. `isAvailable` must include
  `!disposed` too, or a stale reference reports itself usable while rejecting everything.
- **Every request path is bounded, including the browser `fallback`.** That branch bypassed the timeout
  entirely, so an async fallback (a scripted preview harness usually is) that never settled hung the
  caller with none of the real path's diagnostics. Race a THENABLE only — a plain value has already
  settled and must not be made async.
- **Composition helpers belong on `IMessageDispatcher`, via extensions over its ONE `Use` primitive.**
  They were instance methods on `MessageDispatcher`, so late mapping required a DOWNCAST — and the
  reference composition's `if (dispatcher is MessageDispatcher concrete)` had no `else`, so any decorator
  or alternative registration silently dropped three whole modules (symptom: the frameless title bar just
  stopped working). Keep the interface at the four things a dispatcher IS — dispatch, two sends,
  compose — so a decorator has four members to write and every helper works on it for free. Anything
  requiring the live window is mapped LATE, from wherever the window is created; a doc that says to do it
  in `AddMessageDispatcher`'s configure callback is wrong, because that runs before any form exists.
- **LATE MAPPING is supported, so the pipeline must be thread-safe** — "configure then serve" is not a
  safe assumption here (the WinForms host maps its window facades after the form exists). `Use` was a
  `Lazy` reassignment over a mutable `List<T>` with no synchronization: a dispatch could read the OLD
  cached pipeline and answer `NO_HANDLER` for an already-registered route, and a build enumerating the
  list while `Add` grew it was a data race. Copy-on-write list + volatile pipeline + one lock around
  invalidate-then-rebuild.
- **Cancellation is a NORMAL outcome and gets its own code** (`OPERATION_CANCELLED`), not
  `UNKNOWN_ERROR` — it is the one failure a UI should stay silent about, and a client could not tell it
  from a real fault. Map it AFTER `OperationException` so an app that models cancellation in its own
  words keeps them. Same shape for a scope invalidated mid-request: that is a race with a documented
  app-facing call, so retry once rather than reporting a fault.
- **`ConfigureAwait(false)` does NOT belong in the dispatch path — and "the dispatch path" is a
  BOUNDARY, not the whole handler.** The pipeline preserves the synchronization context BY DESIGN,
  because a facade routing a window command touches WinForms and must resume on the UI thread. One
  stray `ConfigureAwait(false)` in `BaseFacade` contradicted that for two phases and survived only
  because every in-repo facade marshals internally anyway.
  **The other half, which the rule used to omit and which reads as a blanket ban without it:** work a
  route deliberately hands OFF — a long operation whose results stream back as notifications — is no
  longer the dispatch path and must NOT capture the UI context. Requiring it to would keep long work
  on the UI thread, which is the exact stall the one-way path exists to avoid
  (`docs/2026-07-31-shenora-oneway-ipc-design.md`). So: the route's own synchronous segment and its
  awaits stay context-preserving; the background body it starts does not. This does not conflict with
  the never-`Task.Run`-per-message rule below — that is about the TRANSPORT spawning per inbound
  message (a measured pool-starvation freeze), not a handler offloading one long operation.
- **The dispatch boundary never throws and never returns null** (`DispatchAsync`): unhandled →
  `NO_HANDLER` (+`{module,type}` params), `OperationException` → its structured error, else →
  `UNKNOWN_ERROR`. Transports rely on it — but `IMessageDispatcher` is a public seam, so
  `WebViewIpcBridge.HandleIncomingAsync` still wraps dispatch + serialize (an unserializable
  handler result once escaped through the async-void handler = process death; found in review).
- **An app-supplied payload never serializes unguarded — including on the OUTGOING timer.** The
  rule above covers the incoming path; the notification flush is the twin and was NOT guarded:
  `WebViewIpcBridge.TryBuildBatchJson` DRAINS the queue and then serializes, on a 50 ms WinForms
  timer, so one event carrying a cyclic object graph (parent/child entities), a `Type`/delegate
  member, or a throwing getter is an unhandled UI-thread exception AND the whole drained batch is
  lost. Guard per-notification (one bad event must not kill its batch) plus a catch-all in `Flush`.
- **A DI singleton factory must never enumerate the provider it is building.** `AddMessageDispatcher`
  resolved `IModuleFacade`s inside the `IMessageDispatcher` singleton factory, so any facade whose
  graph injects `IMessageDispatcher` — the documented cross-module `SendAsync` seam — re-enters the
  same factory. MS DI's cycle detection is call-site-based and cannot see a factory delegate
  re-entering the provider, and the cache entry isn't published yet: unbounded recursion, process
  death by StackOverflow, no exception and no log. Resolve lazily (a terminal middleware over a
  `Lazy<IModuleFacade[]>`) so the singleton is cached before enumeration.
- **Notifications are ALWAYS a batch** (a single event is a batch of one) — `category` alone
  discriminates, which is what lets the same envelope ride postMessage, WebSocket, or a mobile
  channel (D16). Don't reintroduce a single-notification shape or a synthetic batch module/type.
- **The ready gate re-closes on `ContentLoading`, NOT on `NavigationStarting`** (+ on `ProcessFailed`,
  which the bridge subscribes to itself, since the host's auto-reload is optional). `WebViewIpcBridge`
  buffers notifications from construction and delivers only after the client's `READY`; the reset
  exists because a renderer-crash reload would otherwise drain events into a listener-less page.
  `NavigationStarting` was the wrong trigger and closed the gate FOREVER: it fires for navigations
  that never replace the document (one a tap or policy cancels, one that fails before committing),
  and the surviving page has already spent its single `READY` — so the buffer filled to 10 000 and
  then silently dropped oldest for the process lifetime (H3). The residual window between
  `NavigationStarting` and `ContentLoading`, where a flush reaches the OUTGOING page, is deliberate
  and documented at the site: those listeners are still attached.
- **Reset per-page host state on the DOCUMENT, never on the `READY` handshake.** A handshake-keyed
  reset races the page it is resetting for: a `REGISTER` arriving before `READY` is wiped *after being
  acked*, so the client believes its zone is live, the host has forgotten it, and nothing is logged on
  either side. In React that is the DEFAULT outcome rather than bad luck — CHILD effects run before
  PARENT effects, so the obvious reading of "call `notifyReady()` once at startup" (a root-component
  effect) runs after every child's `useDropZone` has registered. `DropZoneManager` therefore clears on
  `ContentLoading` (P5.6), which cannot race the client because it happens before the new page can
  send anything. **The fix was to remove the contract, not to document it** — it had needed warnings
  in four places (`notifyReady`, `UseDropZoneOptions`, `ClearAll`, the npm README) and a contract that
  sharp gets missed wherever it is not repeated. Two features needing the same reset was the signal
  that the kit should own it. `ContentLoading`, never `NavigationStarting`: the latter also fires for
  navigations that never replace the document, which would destroy the live page's state.
- **The dispatcher pipeline preserves the caller's synchronization context** (no
  `ConfigureAwait(false)` anywhere in `MessageDispatcher`) — that's the §5 threading model:
  transports dispatch on the UI thread and every handler's synchronous segment stays there, even
  after an async fall-through. The transport side interleaves async on the UI thread; never
  `Task.Run`-per-message (the measured pool-starvation freeze).

### 0.2.0 — the communication core (D23, `docs/2026-08-01-shenora-communication-core-design.md`)

- **`Publish` goes through `IModuleContext`, never a hand-typed module literal, so an emit cannot
  drift from the facade's own `ModuleName`.** `ModuleContext.Publish` calls `events.Emit(Module, …)`
  with `Module` supplied by `BaseFacade` at construction — the same anti-drift reason
  `OperationInfo.Module` is stamped by the registry from the caller's own module rather than trusted
  from the app. The sample's pre-0.2.0 shape (a hardcoded `"SAMPLE"` string re-typed at every emit
  site) is exactly the class of bug this closes: one typo and an event silently claims the wrong
  module, with nothing to grep for.
- **An operation's `CancellationToken` is its OWN, never the request's — work handed off outlives
  the request that started it.** `OperationRegistry.Start` allocates a fresh `CancellationTokenSource`
  per operation; `IModuleContext.Run`/`Start` never touch the dispatch token at all. Capturing the
  request's token instead would cancel a ten-minute deploy the moment the page that kicked it off
  navigates away — the same trap `BaseFacade.RouteMessageAsync`'s own doc already named for
  hand-rolled background work, now structurally impossible to get wrong through the primitive.
- **Progress emission is throttled to `OperationRegistryOptions.ProgressInterval` (default 100 ms)
  with a TRAILING emit, because the notification batcher queues without coalescing.** A tight
  `Report` loop would otherwise ship hundreds of updates a second — the exact defect the harvested
  source app had already fixed. At most one emission lands per window, but the LAST value in that
  window is never simply dropped: a trailing timer fires once the window closes. **The trailing
  flag must reset in a `finally`, covering every exit — success, cancellation, or a faulting
  `TimeProvider`** (`OperationRegistry.TrailingEmitAsync`) — resetting only on the happy path would
  leave `TrailingScheduled` stuck `true` forever after one fault, silently muting every later
  `Report` on that operation for its remaining lifetime (found in review: the first cut did exactly
  this). Lifecycle transitions (start, complete, fail, cancel, interrupt) are never throttled — they
  always emit immediately, because a terminal state arriving late or not at all is a different class
  of bug than a missed progress tick.
- **Progress is the app's own unit, never a kit-assumed percent — and the kit does not clamp, validate,
  or interpret it (owner direction, before 0.2.0 published — "even its progress it might be different
  than 0-100%").** `OperationOptions.Progress`/`OperationInfo.Progress`/`IOperation.Report`'s `progress`
  parameter are `OperationProgress?` (`Value`, `Total?`, `Unit?` — TS mirror `{ value, total?, unit? }`),
  not an `int?` percent: `Total = null` means an absolute count with no known denominator (bytes off a
  chunked stream), never zero, and `Unit` is app-defined and uninterpreted exactly like `Kind`. A
  previous pass fixed the wrong half of this — it patched the write-side XML doc to SAY "0–100 percent"
  instead of removing the assumption, which is the same mistake `Kind`-as-an-app-string already avoided
  for the app's taxonomy. `OperationRegistry`'s old `ClampProgress` (`Math.Clamp(value, 0, 100)`) is
  DELETED with nothing put in its place: silently rewriting an app's own reported number is worse than
  passing it through, so a `Value` above its own `Total` is the app's bug to see, not the kit's to hide
  — and no validation throw was added either, because `Report` runs on a hot path from background work
  and throwing there would kill an operation over a cosmetic number. `IOperation.Complete()` no longer
  fabricates `Progress = 100`: it sets `Value = Total` only when the last report carried a known `Total`
  (the honest "all of it"), otherwise it leaves the last reported value untouched. `OperationProgress` is
  a NEW wire shape both sides name, so it gets its own tripwire
  (`WireMirrorTests.OperationProgress_fields_match_the_host`) rather than trusting the two sides to stay
  in step by inspection, the same discipline every other shape on this wire already gets.
- **An operation failure obeys the same no-raw-exception-text boundary as a request/response
  failure.** `OperationRegistry.Run`'s guarded background body maps `OperationCanceledException` →
  the operation's own `Cancel()`, `OperationException` → `Fail(code, parameters, message)` (the app's
  own sanctioned words, same rule as `IpcErrorMapping`), and anything else → `Fail(IpcErrorCodes.
  UnknownError, {exceptionType})` with the real exception logged host-side only. One boundary, two
  entry points (a response and an `OperationInfo.Error`) — a second copy of the policy is exactly how
  the `ex.Message`-in-a-wrapper bypass gets re-earned. **That `Cancel()` is the handle's own
  (`IOperation.Cancel`), NOT the registry's public by-id `Cancel(string id)`** — see the next bullet
  for why conflating the two used to strand a non-`Cancellable` operation `Running` forever.
- **`NotificationPump` owns the gate, the cap and the batch; a base owns only the tick.** The pump
  subscribes to the bus at construction (buffering starts before any client could exist to receive
  anything), applies the per-channel `Filter` at enqueue, bounds the queue with drop-oldest, and
  serializes a batch guarded per-notification — all transport-neutral. It deliberately owns NO timer
  and NO transport: which thread may touch a base's client is a base-specific fact (WinForms must
  flush on the UI thread; a headless base can use a bare `PeriodicTimer`), so `WebViewIpcBridge`
  keeps only the `Forms.Timer`, the WebView2 event wiring (`ContentLoading`→`Close`,
  `READY`→`Open`, `ProcessFailed`→`Close`) and `PostWebMessageAsString`, and calls
  `TryDrainBatch` on its own schedule. A second, non-WinForms base gets every one of the pump's
  already-paid-for bug fixes (P5.5 H2/H3) by construction instead of re-earning them.
- **`Cancel` refuses an operation that never opted into `Cancellable` — flipping its status while
  the body runs on would be a lie to the UI.** `OperationRegistry.Start` allocates a
  `CancellationTokenSource` for every operation regardless of `Cancellable`, so a token is not what a
  non-cancellable operation lacks; what the flag actually gates is whether `Cancel()` is allowed to
  signal it at all. Honoring a cancel on an operation that opted out would report `Cancelled` to
  every subscriber while the background body kept running to its own `Complete()`/`Fail()` —
  observable state that no longer describes reality. Same honest-refusal shape as an unknown or
  already-terminal id: `Cancel` returns `false` and changes nothing, rather than pretending to
  succeed. **This refusal is ONLY on the public, by-id `IOperationRegistry.Cancel(string id)`** — the
  route an external CLIENT's `CANCEL` request goes through, where the permission question is real.
  `IOperation.Cancel()` (the handle held by the operation's own owner, and what `Run`'s catch calls
  when the body itself ends in `OperationCanceledException`) is deliberately unconditional: the work
  is over, and refusing to RECORD that — regardless of `Cancellable` — is data loss, not honesty. A
  whole-branch review found this conflated: `Run`'s catch used to call through the by-id route,
  refusing on the DEFAULT `Cancellable = false` and stranding the entry `Running` forever (no
  terminal transition, never evictable by `ClearFinished`, its CTS never disposed) — reachable any
  time a body's cancellation isn't a client's `CANCEL` request at all (an `HttpClient` timeout, a
  linked shutdown token: `TaskCanceledException` derives from `OperationCanceledException`).
- **`GetAll`'s `scope` filter follows the SAME rule as `IEventBus`, not strict equality** — no
  requested scope matches every scope, AND an operation started with no `Scope` of its own (a global
  operation) matches ANY requested scope. Both event buses already apply exactly this (a scope-less
  event still reaches scoped subscribers), so a `GetAll` that instead required strict equality
  disagreed with the deltas a scoped store folds afterward: it never SAW an unscoped operation in a
  scoped `LIST` snapshot but DID receive its `OPERATION_UPDATED` deltas, so a scoped store's contents
  silently depended on whether it mounted before or after the work started.
- **Every non-terminal state must have a sanctioned exit to a terminal one — this generalises past
  operations, and it is enforced by a test, not by reviewer attention** (§5A.1, the D23 amendment
  before 0.2.0 merged). The bug that named the rule: a crash-checkpoint offer (its own status,
  `Interrupted`, at the time — since collapsed into `OperationStatus.Waiting`, see the amendment
  below) could only be removed by *resuming* it — `Validate` hard-coded `Status == Running` for every
  caller so `Cancel`/`Complete`/`Fail` all refused it, `ClearFinished` only ever walked
  `_finishedOrder` (which the checkpoint-registration path deliberately never wrote to, since an offer
  is not finished history), and `PruneHistory` skipped offers on purpose. **Three guards, each
  individually correct and each with a comment explaining why — and together they left a state with no
  exit at all.** That is what makes this class of bug dangerous: it is invisible in any single guard's
  diff, because each guard is reviewed (and passes review) in isolation from the others it composes
  with. The same app that reviewed this branch had already shipped the identical bug and stranded a
  real production deployment on it hours earlier (paused waiting on DNS records it could not complete
  — permanently offering Resume, permanently undeletable, because a waiting run *is* the live state) —
  the kit's own review had flagged the gap as a Minor and deferred it; the adopter's production
  incident was the sharper evidence.
  The fix (`OperationStatus.Waiting`, `IOperationRegistry.Dismiss`) is the specific instance; the
  REUSABLE half is the test shape: `OperationLifecycleInvariantTests` enumerates the LIVE status enum
  via reflection (`Enum.GetValues<OperationStatus>()`), never a hardcoded list, so a future status is
  swept in automatically — and for each non-terminal value it looks up a registered `(reach, exit)`
  pair, asserting `ContainsKey` explicitly (by name) rather than only iterating the dictionary's own
  keys, which is what makes it fail LOUDLY when a new status has no exit registered, instead of
  silently checking nothing. Verified by sabotage (the standing rule for every tripwire here): making
  `Dismiss` temporarily refuse the crash-checkpoint status failed the test citing it by name before the
  fix was restored — re-verified the same way after the later status collapse (see below), citing
  `OperationStatus.Waiting`. Any future state machine in this codebase (a session lifecycle, a
  connection state) should get the same shape: enumerate the enum, require a registered exit per
  non-terminal value, prove the exit actually lands on a terminal one through the real object — not a
  static claim about what "should" transition where.
  **AMENDED (owner direction, before publish — "structured like XHR"): `Paused` and `Interrupted`
  collapsed into the single `OperationStatus.Waiting` shown above.** Both were already one band
  everywhere that mattered (`Dismiss`/`RequestResume` accepted either, neither was pruned, the
  client's `waiting` getter already unioned them); the one place they diverged — `RequestResume`
  dropping the checkpoint case, keeping the live-`Wait()` case — moved to keying on `ResumePayload`
  instead of a second status. With one fewer non-terminal status, `OperationLifecycleInvariantTests`'
  sweep is simpler, not weaker — it still enumerates the live enum rather than a hardcoded list. Full
  rename table and rationale: `docs/DECISIONS.md` D23's amendment.
  **CLOSED (2026-08-01, before 0.2.0 pushed/published): keying on `ResumePayload` was a residual
  hole, not the final shape — that field is APP-controlled (an app may set it on `OperationOptions` at
  `Start()`), so it could not reliably answer "does this entry have a live handle".** An app that
  attached its own `ResumePayload` at `Start()` and then called `Wait()` had a genuinely live operation
  (handle intact, body parked) dropped by `RequestResume` anyway — silently orphaning later
  `Report`/`Complete`/`Fail` calls on it, the same defect class `IModuleContext` closed for module
  drift (a decision keyed on a value the caller also controls, not on the fact the kit itself knows for
  certain). `RequestResume` now keys the drop-vs-keep decision on an internal `Entry.Reconstructed`
  flag instead — set `true` only by `RegisterWaiting` (the one call site that legitimately reconstructs
  an entry with no live body), left `false` by `Start` (which always allocates one) — never exposed on
  `OperationInfo`, since no consumer needs it and every public member is SemVer surface at 1.0.
  `ResumePayload`'s other roles are unchanged: `RegisterWaiting` still requires it non-empty, the
  dedupe key still uses it, and it still rides `OPERATION_RESUME_REQUESTED`.

## Gotchas / traps

- **EventBus match-cache keys must be collision-free**: module/type/scope are arbitrary app
  strings, so keys are `'\0'`-joined (`EventBus.EmitAsync`) — a `'.'`-join let
  `("APP","TASK","s1")` and `("APP","TASK.s1")` share (and permanently poison) one cache entry.
  The cache also lives per subscription × distinct event key: scope/type must be drawn from
  SMALL sets (profiles, windows), never per-entity ids.
- JSON `null` == absent on this wire (`IpcJson` omits nulls; the client convention is
  `undefined`) — `PayloadHelper` treats an explicit null as missing on purpose.
- The client bridge fails fast after `dispose()` (`NO_TRANSPORT`) — stale instances captured
  before `configureBridge` replaced the default otherwise burn the full 30 s timeout per call.
- **`AddShenoraOperations` takes the `OperationRegistryOptions` RECORD, not a configure callback** —
  every property on it is `{ get; init; }` (the kit's one immutability convention), so an
  `Action<OperationRegistryOptions>` callback shape made `o => o.ModuleName = "X"` a compile error
  (CS8852): the callback could only ever read a freshly-defaulted instance, never configure one. Pass
  a built `new OperationRegistryOptions { ModuleName = "X" }` instead, same as
  `WebViewIpcBridgeOptions`/`NotificationPumpOptions`.
- **A host-side removal now publishes `OperationEvents.Removed` (`OPERATION_REMOVED`,
  `{ operationIds: string[] }`) — generic-library audit finding 4, closing a gap that used to require
  client-side guessing.** `OPERATION_UPDATED` only ever adds/updates an id, so a client mirroring
  bounded host history (`MaxHistory`) or a `ClearFinished`/`RequestResume` removal had NO wire event to
  fold — the ONLY reason `@shenora/react`'s `clearFinished`/`resume` actions used to carry a
  hand-written optimistic local prune apiece. `Removed` is emitted wherever an entry actually leaves
  the registry (`MaxHistory` eviction inside `Finish`, `ClearFinished`, the no-live-handle drop inside
  `RequestResume`) and is scope-`null` (global) on purpose — a batch can span several scopes at once,
  and deleting an id a subscriber never had is a harmless no-op, so every store hears it regardless of
  its own scope filter. The client folds it by deleting exactly the named ids, unconditionally — no
  status check, because the HOST already decided what left. `clearFinished`/`resume` are now plain
  fire-and-forget posts with no local mutation of their own.
  **`Dismiss` was never in this category, and still isn't** — it does not remove anything, it
  transitions the entry to `Cancelled` through the same `Finish` path as `Complete`/`Fail`/`Cancel`, so
  it publishes an ordinary `OPERATION_UPDATED` snapshot the wire already carries. Don't wire `Removed`
  handling onto it out of habit; there is no delta to compensate for.
- **The retired lesson, worth keeping even though the fix is now structural: a CLIENT-side optimistic
  prune must mirror the HOST's own asymmetry exactly, never a uniform rule applied to both branches of
  one wire action** (found in review, lifecycle-completion batch, before `Removed` existed):
  `resume`'s local prune used to delete the id unconditionally, written back when `RequestResume`
  always removed the entry host-side. §5A.4 then made that conditional — the no-live-handle case is
  still removed, an entry reached via a live `Wait()` is deliberately LEFT IN PLACE for the app's own
  `Resume()` handle to flip (this asymmetry originally keyed on a second status, `Interrupted` vs.
  `Paused`; after the status collapse it briefly keyed on `ResumePayload`, and now keys on the host's
  own internal provenance record — the client-side lesson below holds regardless of which host-side
  signal decides it, since the client only ever folds the named-id `OPERATION_REMOVED` event, never
  the field itself) — and the client's prune did not get re-derived alongside it. The
  consequence rebuilt §5A.1's original bug ONE LAYER UP: a user clicking Resume on a still-waiting
  entry made the row vanish locally (nothing published host-side, since nothing changed), so the
  still-parked operation became unreachable — no visible row
  to click Dismiss on — until every subscriber unmounted and a fresh `LIST` ran. This was the release's
  only Critical, and it is exactly the class of bug an authoritative removal event structurally
  prevents: a client-side guess about "what the host must have removed" can diverge from the host's own
  asymmetric rule the moment that rule changes, while folding a named-id event never can. Generalizes:
  whenever a host-side transition is asymmetric across
  two input states, an optimistic client mirror of it must encode that same asymmetry — a single
  branch that reads "prune on click" is a category of client/host desync waiting on the NEXT design
  amendment to the host's asymmetry, not just this one.
- **`Run`'s implicit terminal transition must check the CURRENT status, not assume it** — `Run`'s tail
  used to call `operation.Complete()` unconditionally once the awaited body returned, and `Complete`
  itself legitimately accepts `Running` OR `Waiting` (a waiting operation can still complete once
  unblocked — see the `Cancel`/`Complete`/`Fail` band table above). So a body doing the exact shape the
  design itself advertises — `op.Wait(reason); return;` — got silently stamped `Completed` by the very
  primitive whose job is not to lie about a waiting-but-not-crashed run. Reproduced this way: `Task.Run`
  dispatches to a thread-pool thread, but once that thread starts, an already-completed awaited `Task`
  does not yield — the whole `Wait()` → `Complete()` sequence runs in one synchronous burst, so a
  test polling for "first non-`Running` observation" can transiently see `Waiting` and pass BY ACCIDENT
  depending on scheduling luck (found live: the first version of this test passed, then failed
  reliably once it waited for the settled state instead of the first observation — see
  `ModuleOperationTests.Run_does_not_complete_a_body_that_waited_and_returned`'s own comment). The
  fix — peek the entry's live status and only call `Complete()` when it is still `Running` — is the
  general rule for any "finish implicitly unless something else already happened" tail: check, don't
  assume, especially when the thing that might have happened is itself a legitimate, newly-added
  transition on the SAME status the unconditional call also accepts.
- **A permission check and the transition it authorizes must not straddle two separate lock
  acquisitions without the SECOND one's outcome being the one reported** — `Dismiss`/`Cancel(id)` each
  validate + read a token under one `lock`, release it, then call `Finish` (which re-validates under
  its OWN freshly re-acquired `lock`) — a deliberate gap, because `CancellationTokenSource.Cancel()`
  must run outside any lock (its callbacks can re-enter the registry). Both callers used to return
  `true` unconditionally once THEIR OWN check passed, trusting that gap could never change the
  outcome — but a concurrent transition landing exactly in that gap (e.g. `Resume()` flipping a
  `Waiting` entry back to `Running` between `Dismiss`'s check and `Finish`'s re-check) makes `Finish`
  correctly refuse while the caller still reports success to whoever asked. `Finish` (and its shared
  tail, `CancelTokenThenFinish`) now returns whether it actually transitioned, and both callers
  propagate that instead of assuming — verified by a many-real-threads race test in
  `OperationDismissTests` (thread-pool tasks alone do not reliably hit a window this narrow, same
  finding as the pre-existing `Concurrent_Cancel_and_Complete_…` test). The general rule: when a
  method's own permission check and the mutation it gates are split across two lock acquisitions for a
  documented reason, the SECOND acquisition's outcome is the only one that is actually true by the
  time the caller returns — report that one, never the first.
- **A client-side derived-getter set covering a host state machine's bands must be checked against
  the FULL status enum, not eyeballed against the getters that already exist** (`operations.ts`,
  second adopter review). `makeState` shipped `running`/`paused`/`finished` when `Paused` was added,
  which reads complete against three statuses — but the host already had FIVE (`Interrupted` predates
  `Paused`), so `interrupted` fell into no getter at all: not `running`, not `paused` (matched only the
  literal string), not `finished` (`TERMINAL_STATUSES` deliberately excludes it). It was reachable only
  by hand-filtering `byId`, exactly the workaround the store's own docs warn against. The fix at the
  time (`interrupted`, `waiting` = `paused` ∪ `interrupted`, both derived from one `WAITING_STATUSES`
  set — the same one-definition discipline `TERMINAL_STATUSES` already used) is the specific instance;
  the REUSABLE half is the same shape as the host's own `OperationLifecycleInvariantTests`: a test that
  enumerates the LIVE status object (`Object.values(OperationStatuses)`, never a hardcoded list) and
  asserts every value lands in exactly one getter-backed band, so a status added later with no band
  fails that test instead of silently belonging nowhere. A hand-maintained parallel status set (a
  second `paused`/`interrupted` list living inside a different getter) is precisely how this class of
  gap re-earns itself — define the set once, derive every getter that needs it from that one set.
  **AMENDED (owner direction, before publish — the status collapse): `Paused` and `Interrupted`
  folded into the single `OperationStatus.Waiting`, so `paused`/`interrupted` and `WAITING_STATUSES`
  were DELETED rather than kept as aliases — `waiting` is now a single-status filter, exactly like
  `running`, with no second internal set to derive it from.** The reusable half of this bullet is
  unaffected — the same "enumerate the live status object" test still pins that every status lands in
  exactly one band, now with one fewer non-terminal value to sweep.
