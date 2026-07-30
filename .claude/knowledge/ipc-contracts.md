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
- **`ConfigureAwait(false)` does NOT belong in the dispatch path.** The pipeline preserves the
  synchronization context BY DESIGN, because a facade routing a window command touches WinForms and must
  resume on the UI thread. One stray `ConfigureAwait(false)` in `BaseFacade` contradicted that for two
  phases and survived only because every in-repo facade marshals internally anyway.
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
- **The ready gate re-closes on every main-document navigation.** `WebViewIpcBridge` buffers
  notifications from construction, delivers only after the client's `READY`, and
  `NavigationStarting` resets the gate — otherwise a renderer-crash reload silently drains
  events into a listener-less page (found in review). The client calls `notifyReady()` per page
  load, after its listeners subscribe.
- **The dispatcher pipeline preserves the caller's synchronization context** (no
  `ConfigureAwait(false)` anywhere in `MessageDispatcher`) — that's the §5 threading model:
  transports dispatch on the UI thread and every handler's synchronous segment stays there, even
  after an async fall-through. The transport side interleaves async on the UI thread; never
  `Task.Run`-per-message (the measured pool-starvation freeze).

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
