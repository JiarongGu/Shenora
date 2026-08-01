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
- **A host-side removal (`ClearFinished`, `RequestResume`) is NOT mirrored by a wire event** —
  `OPERATION_UPDATED` only ever adds/updates an id, never removes one. `@shenora/react`'s
  `clearFinished`/`resume` actions prune their own rows from LOCAL state as an optimistic update (no
  round trip needed — the action already knows the answer), but the host's own `MaxHistory` eviction
  has no equivalent: a long-lived store keeps every terminal entry it has ever seen until
  `clearFinished` is actually called client-side too.
