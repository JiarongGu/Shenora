# IPC contract invariants — the wire rules the P3 stack encodes

The envelope contract is FIXED (design §5, D11/D16) and both sides ship from this repo:
`src/Shenora/Core/Ipc/` + `src/Shenora.Windows/WebView/WebViewIpcBridge.cs` (host) ⇄
`src/Shenora.React/src/types.ts|bridge.ts` (client). Read before touching any of them, adding a
transport, or writing an adoption shim.

⚠ **Re-check these paths whenever a layer moves.** A dead path in a RULE is worse than in a doc, because
a rule is read as instructions — this line pointed at a folded package and a relayered file for days.

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
  `UseErrorHandler`, `ModuleBase`, `PayloadHelper` (the wire message carries only the key — the
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
  `UseMessageDispatcher` maps them through `MapRegisteredModulesLazily` — one terminal middleware
  resolving them on the first dispatch — precisely because claiming a name needs to READ the names,
  and resolving facades inside the `IMessageDispatcher` singleton factory is the silent
  `StackOverflow` the bullet further down describes. So `IsModuleMapped("OPERATIONS")` is `false`
  while `OPERATIONS` is routed, and `TryMapModule` answers `true` for a name a DI facade already owns
  — after which the plug-in never runs, because the lazy middleware is composed earlier and answers
  first. That is the silent-shadowing defect this whole seam exists to prevent, re-entering through
  the composition path rather than through the registry. The PRECEDENCE is right (the app's own
  modules win); only the answer is dishonest. Recorded rather than fixed: closing it needs a
  name-reservation seam the registry does not have, or re-opening the deadlock. Until a consumer hits
  it, map anything a plug-in must be able to collide with EXPLICITLY (`MapModule(module)` /
  `TryMapModule`), not through DI enumeration
  (`services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, TModule>())`).
- **The dispatch token is a LIFETIME, not a per-request cancel — and the boundary still never
  throws.** `DispatchAsync`/`SendAsync`/`MessageMiddleware`/`IIpcModule`/`ModuleBase.RouteMessageAsync`
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
- **An `ShenoraException`'s MESSAGE crosses the wire verbatim — so never build one from
  `ex.Message`.** The no-raw-exception-text rule above has exactly one sanctioned channel through it:
  `ShenoraException` is the app describing an EXPECTED failure in its own words, and
  `IpcErrorMapping` passes its code, parameters and message through untouched. That makes
  `catch (Exception ex) { throw new ShenoraException(code, message: ex.Message); }` a complete
  bypass of the boundary — and it is the natural line to write when porting a host whose dispatcher
  did `$"{action} failed: {ex.Message}"`, which is how the P6.4 adapter probe found it (sabotage:
  with the wrapper in place a planted connection string reached the client; without it, the response
  carried only `UNKNOWN_ERROR` + the exception type name). Let unexpected exceptions ESCAPE to the
  boundary; reserve `ShenoraException` for failures the app can name.
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
  in `UseMessageDispatcher`'s configure callback is wrong, because that runs before any form exists.
- **LATE MAPPING is supported, so the pipeline must be thread-safe** — "configure then serve" is not a
  safe assumption here (the WinForms host maps its window facades after the form exists). `Use` was a
  `Lazy` reassignment over a mutable `List<T>` with no synchronization: a dispatch could read the OLD
  cached pipeline and answer `NO_HANDLER` for an already-registered route, and a build enumerating the
  list while `Add` grew it was a data race. Copy-on-write list + volatile pipeline + one lock around
  invalidate-then-rebuild.
- **Cancellation is a NORMAL outcome and gets its own code** (`OPERATION_CANCELLED`), not
  `UNKNOWN_ERROR` — it is the one failure a UI should stay silent about, and a client could not tell it
  from a real fault. Map it AFTER `ShenoraException` so an app that models cancellation in its own
  words keeps them. Same shape for a scope invalidated mid-request: that is a race with a documented
  app-facing call, so retry once rather than reporting a fault.
- **`ConfigureAwait(false)` does NOT belong in the dispatch path — and "the dispatch path" is a
  BOUNDARY, not the whole handler.** The pipeline preserves the synchronization context BY DESIGN,
  because a facade routing a window command touches WinForms and must resume on the UI thread. One
  stray `ConfigureAwait(false)` in the retired `BaseFacade` contradicted that for two phases and survived only
  because every in-repo facade marshals internally anyway.
  **The other half, which the rule used to omit and which reads as a blanket ban without it:** work a
  route deliberately hands OFF — a long operation whose results stream back as notifications — is no
  longer the dispatch path and must NOT capture the UI context. Requiring it to would keep long work
  on the UI thread, which is the exact stall the one-way path exists to avoid
  (D23, measured: 2 027 ms stalled vs 0 ms). So: the route's own synchronous segment and its
  awaits stay context-preserving; the background body it starts does not. This does not conflict with
  the never-`Task.Run`-per-message rule below — that is about the TRANSPORT spawning per inbound
  message (a measured pool-starvation freeze), not a handler offloading one long operation.
- **The dispatch boundary never throws and never returns null** (`DispatchAsync`): unhandled →
  `NO_HANDLER` (+`{module,type}` params), `ShenoraException` → its structured error, else →
  `UNKNOWN_ERROR`. Transports rely on it — but `IMessageDispatcher` is a public seam, so
  `WebViewIpcBridge.HandleIncomingAsync` still wraps dispatch + serialize (an unserializable
  handler result once escaped through the async-void handler = process death; found in review).
  ⚠ **The contract extends to every SEAM the boundary itself calls, not just to handlers.** When
  tracking moved into `DispatchAsync` it called `IIpcRequestTracker.Begin`/`Fail`/`Dispose` bare — and
  that is a PUBLIC seam an app may implement, with `Fail` in the `catch` and `Dispose` in the `finally`,
  i.e. the two places an exception escapes or REPLACES the response. Sabotage-verified: a throwing
  `Dispose` propagated straight out of `DispatchAsync`. Guarded now, and the shape generalises —
  **bookkeeping must never decide a request's fate**, so a faulty tracker dispatches untracked and logs
  rather than turning a diagnostic loss into an outage. `IModuleContext.Report` is deliberately left
  unguarded by contrast: it runs inside the module's own error boundary, so it degrades to one failed
  request, and swallowing it would hide a broken tracker from the app that supplied it.
- **An app-supplied payload never serializes unguarded — including on the OUTGOING timer.** The
  rule above covers the incoming path; the notification flush is the twin and was NOT guarded:
  `WebViewIpcBridge.TryBuildBatchJson` DRAINS the queue and then serializes, on a 50 ms WinForms
  timer, so one event carrying a cyclic object graph (parent/child entities), a `Type`/delegate
  member, or a throwing getter is an unhandled UI-thread exception AND the whole drained batch is
  lost. Guard per-notification (one bad event must not kill its batch) plus a catch-all in `Flush`.
- **A DI singleton factory must never enumerate the provider it is building.** `UseMessageDispatcher`
  once resolved `IModuleFacade`s inside the `IMessageDispatcher` singleton factory, so any facade whose
  graph injects `IMessageDispatcher` — the documented cross-module `SendAsync` seam — re-enters the
  same factory. MS DI's cycle detection is call-site-based and cannot see a factory delegate
  re-entering the provider, and the cache entry isn't published yet: unbounded recursion, process
  death by StackOverflow, no exception and no log. Resolve lazily (a terminal middleware over a
  `Lazy<IIpcModule[]>`) so the singleton is cached before enumeration.
- **A batch COALESCES only what its EMITTER said may be coalesced.** `EventMessage.CoalesceKey` /
  `IpcNotification.CoalesceKey` declare that a notification supersedes an earlier undelivered one with
  the same module/type/scope/key; `NotificationPump` applies it at drain, last-write-wins, and the
  survivor keeps its own later position. ⚠ **Never derive the key inside the pump.** It cannot tell a
  full snapshot (safe to supersede) from a delta (`+3 bytes` — coalescing two of those loses one), and
  only the emitter can. The kit sets it on `REQUEST_UPDATED`, whose payload is a whole
  `IpcRequestStatus` the client already folds last-write-wins by id — that folding rule IS the licence
  to drop the intermediates — and deliberately NOT on `REQUEST_REMOVED`, whose payload is a batch of
  DIFFERENT ids, where superseding would silently lose removals. Host-side only (`[JsonIgnore]`, no TS
  mirror): the coalescing has already happened by the time a batch leaves, so a client has nothing to
  decide and shipping the key would invite it to re-implement a policy the host applied.
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

### The request lifecycle — tracking, progress, cancellation

The live types are `IpcRequestState` + `IpcRequestTracker`, pinned by `IpcRequestStateInvariantTests`.
⚠ **A predecessor "operations" mechanism was merged into `IpcRequest` by D66** — `OperationStatus.Waiting`,
`IOperationRegistry.Dismiss`, `RequestResume` and `CancelTokenThenFinish` are gone, along with the tests
named for them, so do not go looking. Three of its lessons outlived it and are why several rules below read as they
do: **check-then-act across two lock acquisitions is a race**; **a method that gates a mutation must report
whether it actually transitioned rather than let the caller assume**; and **a race test needs real threads,
not thread-pool tasks**.

- **`Publish` goes through `IModuleContext`, never a hand-typed module literal, so an emit cannot
  drift from the module's own `ModuleName`.** `ModuleContext.Publish` calls `events.Emit(Module, …)`
  with `Module` supplied by `ModuleBase` at construction — the same anti-drift reason
  `IpcRequestStatus.Module` is taken from the request itself rather than trusted from the app. The sample's pre-0.2.0 shape (a hardcoded `"SAMPLE"` string re-typed at every emit
  site) is exactly the class of bug this closes: one typo and an event silently claims the wrong
  module, with nothing to grep for.
- 🔴 **A request's token IS the one a route observes, and CANCEL targets the request id.** Since D66
  there is no separate operation with a separate token: `IpcRequestTracker.Begin` links the caller's
  lifetime into a scope token, `MessageDispatcher.DispatchAsync` hands THAT down the pipeline, and
  `Cancel(requestId)` signals it.
  ⚠ **A fresh token PER OPERATION is the wrong answer and was the predecessor's**, taken because a
  request's token used to die with its response. A request now outlives its own send, so its token is the
  one to observe — do not reintroduce a second lifetime to work around a gap that is closed.
- 🔴 **TRACKING BELONGS TO THE DISPATCH BOUNDARY, and putting it in `ModuleBase` made the whole feature
  silently absent for a release** (2026-08-08). `ModuleBase` took an optional `IIpcRequestTracker` that
  each facade had to inject and forward through `base(logger, events, requests)` — and not one module in
  the kit did, so the only `Begin` call site in the repo never fired in a composed app. `LIST` answered
  empty, `CANCEL` answered false, `REQUEST_UPDATED` never went out, nothing threw or logged, and the
  tracker's own tests stayed green because they called it directly. **D63's class, and its fifth
  instance.** Two independent reasons the boundary is the honest place, and the second is the one that
  was missed: the dispatcher sees EVERY module (a bare `IIpcModule` and an ad-hoc `MapRoute` lambda could
  never have wired anything), and it sees the OUTCOME — one `IpcResponse` carries success, an app's
  structured failure, a cancellation and `NO_HANDLER` alike. `ModuleBase`'s `catch` could not: it
  returned an error response and then let the scope dispose as `Completed`, so `IpcRequestState.Failed`
  was unreachable and `IIpcRequestScope.Fail` had no caller anywhere.
  **Generalize it:** when a capability needs every implementer to remember one line, the capability is in
  the wrong place — put it where nothing can decline to opt in, and delete the parameter that asked.
  ⚠ A route reaches its scope through an AMBIENT (`IpcRequestScopeAccessor`), matched BY REQUEST ID.
  The id match is not paranoia: a route calling another module's `HandleMessageAsync` directly leaves the
  outer scope genuinely ambient, and an unguarded read attributes the inner module's progress to the
  outer request. The ambient does NOT leak upward out of an async method — `AsyncTaskMethodBuilder.Start`
  restores the caller's ExecutionContext — so the explicit restore is belt-and-braces, not the guarantee.
  That was measured by sabotage after being asserted the other way round from memory.
- 🔴 **NOTHING is published until a request outlives the grace period, and that is the whole reason
  every request can be tracked without asking anyone to declare anything.**
  `IpcRequestTrackerOptions.GracePeriod` (50 ms, the notification pump's own flush interval) suppresses
  the running snapshot, the progress and the completion alike: a request that finishes inside the window
  leaves NO event and NO history. ⚠ It suppresses NOTIFICATIONS only — never the response, which returns
  before the tracking scope disposes. Anyone implementing this by parking the response has inverted it
  and added latency to every fast call in the app to save a notification nobody would have seen.
  Progress is then throttled per request by `ProgressInterval` (default 100 ms) once announced;
  terminal transitions are never throttled, because a terminal state arriving late is a different class
  of bug from a missed progress tick.
- **Progress is the app's own unit, never a kit-assumed percent — and the kit does not clamp, validate
  or interpret it** (owner direction: *"even its progress it might be different than 0-100%"*).
  `IpcProgress` is `{ Value, Total?, Unit? }` (TS mirror `{ value, total?, unit? }`), not an `int?`
  percent: `Total = null` means an absolute count with no known denominator (bytes off a chunked
  stream), never zero, and `Unit` is app-defined and uninterpreted. Silently rewriting an app's own
  reported number is worse than passing it through, so a `Value` above its own `Total` is the app's bug
  to see — and no validation throw was added either, because `Report` runs on a hot path from background
  work and throwing there would kill a request over a cosmetic number. It is a wire shape both sides
  name, so it has its own tripwire (`WireMirrorTests.IpcProgress_fields_match_the_host`) rather than
  trusting the two sides to stay in step by inspection.
- **A failure obeys the same no-raw-exception-text boundary as any request/response failure.**
  `ModuleBase` maps an `ShenoraException` to its structured error and anything else to
  `IpcErrorCodes.UnknownError` plus the exception type name, with the detail logged host-side only —
  one boundary, and `IpcRequestStatus.Error` carries the same shape. A second copy of the policy is
  exactly how the `ex.Message`-in-a-wrapper bypass gets re-earned.

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
- **`Cancel` refuses an unknown or already-finished id, rather than pretending to succeed.**
  `IIpcRequestTracker.Cancel(requestId)` signals the request's token FIRST — so a body observing it
  unwinds instead of racing a finished-then-cancelled flip — then records `Cancelled`, and returns
  whether it actually transitioned. There is no opt-in: **every request is cancellable**, because the
  scope's token is the one the whole pipeline runs under.
  ⚠ **There USED to be a `Cancellable` flag that `Cancel` refused without**, and the reasoning behind it
  is worth knowing only as the shape of a bug: a by-id cancel that honoured the flag stranded entries
  `Running` forever whenever a body's cancellation was not a client's `CANCEL` at all (an `HttpClient`
  timeout, a linked shutdown token — `TaskCanceledException` derives from `OperationCanceledException`).
  D66 removed the flag along with the whole second entity. **The surviving rule is the general one: a
  refusal must be honest, and a terminal transition must never be refused as a permission question.**
- **`GetAll`'s `scope` filter follows the SAME rule as `IEventBus`, not strict equality** — no
  requested scope matches every scope, AND a request with no `Scope` of its own (a global one) matches
  ANY requested scope. Both event buses already apply exactly this (a scope-less event still reaches
  scoped subscribers), so a `GetAll` that instead required strict equality would disagree with the
  deltas a scoped store folds afterward: it would never SEE an unscoped request in a scoped `LIST`
  snapshot but WOULD receive its `REQUEST_UPDATED` deltas, so a scoped store's contents would silently
  depend on whether it mounted before or after the work started.
- 🔴 **Every non-terminal state needs a sanctioned exit to a terminal one, and a TEST must enforce it —
  not reviewer attention.** `IpcRequestState` is `Running` plus three terminals, so this holds by
  construction today; it is written down for the next state machine here (a session lifecycle, a
  connection state), where it will not.
  **Why a reviewer cannot catch it:** the way this fails is several guards that are each individually
  correct — one requiring `Running`, one walking only the finished list, one skipping a case on purpose —
  which together leave a state nothing can leave. **It is invisible in any single guard's diff**, because
  each is reviewed in isolation from the others it composes with.
  **The test shape** (`IpcRequestStateInvariantTests`): enumerate the LIVE enum by reflection
  (`Enum.GetValues<IpcRequestState>()`), never a hardcoded list, so a new state is swept in
  automatically; classify every value and assert the unclassified set is EMPTY, naming the offender —
  so a state with no exit fails loudly instead of silently checking nothing; and prove the exit lands on
  a terminal state through the real object, never a static claim about what should transition where.
  ⚠ **A test shape is only reusable if something re-uses it after a rename** — this one died with its
  subsystem and went unreplaced for two versions.

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
- 🔴 **An options type's property accessors must match HOW it is handed over, and the mismatch is a
  compile error that reads as a mystery.** The kit has both shapes on purpose:
  | handed over as | accessors | example |
  |---|---|---|
  | a **configure callback**, `Use…(Action<TOptions>)` | `{ get; set; }` | `IpcRequestTrackerOptions` — `builder.UseRequests(x => x.GracePeriod = …)` |
  | a **built object** passed in | `{ get; init; }` | `WebViewIpcBridgeOptions` |
  ⚠ **Give an `init`-only type a configure-callback shape and `o => o.X = v` is CS8852** — the callback
  can then only READ a freshly-defaulted instance, never configure one. Decide which shape a new
  `*Options` is before choosing its accessors.
- 🔴 **A removal needs its OWN authoritative wire event; a client must never GUESS what the host
  dropped.** `REQUEST_UPDATED` only ever adds or updates an id, so anything that makes an entry LEAVE
  (history eviction, `CLEAR_FINISHED`) has no snapshot to fold — which is what tempts a client into an
  optimistic local prune. `IpcRequestEvents.Removed` closes it; its contract (batch payload, global
  scope, fold by deleting the named ids) lives on the constant's own XML doc, not here.
  **The failure it prevents:** a client-side prune encodes a *guess* about the host's rule, and diverges
  the moment that rule changes — including in ways no test covers, because both sides still pass their
  own. A row vanishes locally while the host still holds the entry, and it is then unreachable until
  every subscriber unmounts and a fresh list runs. **Folding a named-id event cannot drift that way.**
  ⚠ Generalizes past IPC: whenever a host-side transition is ASYMMETRIC across two input states, an
  optimistic client mirror must encode the same asymmetry — a single branch that reads "prune on click"
  is a desync waiting on the next change to the host's rule.
- 🔴 **An implicit terminal transition must check the CURRENT state, not assume it.** For any
  "finish implicitly unless something else already happened" tail: peek the live state and only
  transition if it is still the one you assumed. The trap is that the thing which may already have
  happened is often a legitimate transition on the SAME state the unconditional call also accepts — so
  it does not throw, it silently stamps the wrong terminal state on a run that was fine.
  ⚠ **Its TEST has a scheduling trap** (`debugging-method.md`): once an awaited `Task` is already
  complete it does not yield, so the whole sequence runs in one synchronous burst. A test polling for
  the FIRST non-initial observation can catch an intermediate state and pass by scheduling luck. **Poll
  for the SETTLED state**, never the first change.
- 🔴 **A permission check and the transition it authorizes must not straddle two lock acquisitions
  without the SECOND one's outcome being the one reported.** `IpcRequestTracker.Cancel` validates and
  reads the token under one `lock`, releases it, then calls `Finish`, which re-validates under its own
  freshly acquired `lock`. The gap is deliberate: `CancellationTokenSource.Cancel()` must run outside
  any lock, because its callbacks can re-enter the tracker.
  **So report `Finish`'s answer, never the first check's.** A caller that returns `true` once its OWN
  check passed is trusting that the gap cannot change the outcome — but a concurrent transition landing
  inside it makes `Finish` correctly refuse while the caller still reports success to whoever asked.
  ⚠ **A race test for a window this narrow needs REAL THREADS** — thread-pool tasks do not reliably hit
  it, so the test passes without ever entering the gap.
  **The general rule:** when a permission check and the mutation it gates are split across two lock
  acquisitions for a documented reason, the SECOND acquisition's outcome is the only one still true by
  the time the caller returns.
- **A client-side derived-getter set covering a host state machine must be checked against the FULL
  status enum, not eyeballed against the getters that already exist.** A getter set that reads complete
  against the statuses its author had in mind leaves any other status in NO band — not running, not
  finished, reachable only by hand-filtering the raw map, which is the workaround such a store's own
  docs warn against. It fails silently, because "belongs to no band" looks identical to "none right now".
  **Same test shape as the host side above, applied to the client**: enumerate the LIVE status object,
  never a hardcoded list, and assert every value lands in exactly one getter-backed band.
  ⚠ **Define the band set ONCE and derive every getter from it** — a hand-maintained parallel list
  inside a second getter is how this re-earns itself.
