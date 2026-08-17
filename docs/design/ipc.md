# The IPC stack — as built

**Maintainer-facing.** What the pieces are, how they compose, and what each one promises. For the strings
a PAGE types read [`../reference/wire.md`](../reference/wire.md); for the invariants you must not break
while editing read [`../../.claude/knowledge/ipc-contracts.md`](../../.claude/knowledge/ipc-contracts.md);
for WHY any of it is this way read the decisions linked below — **this doc states the design, never the
rationale** (D77).

## The two halves, and what is portable

Both sides ship from this repo and are kept in step by a tripwire, not by care (`WireMirrorTests`).

| Half | Lives in | Owns |
|---|---|---|
| host | `src/Shenora/Core/Ipc/` | the envelope, the pipeline, request tracking, the error boundary |
| transport (desktop) | `src/Shenora.Windows/WebView/WebViewIpcBridge.cs` | a WinForms timer, WebView2 events, `PostWebMessageAsString` |
| client | `src/Shenora.React/src/` | correlation, category demux, batch unbundling, the event bus mirror |

**Nothing transport-specific lives in `Core`, and nothing protocol-shaped lives in a transport.** The
split is enforced by two transport-neutral types: `IpcHostBridge` (inbound) and `NotificationPump`
(outbound). A second, non-WinForms base inherits every invariant they hold rather than re-earning it.

## Inbound: one pipeline, one boundary

```
transport → IpcHostBridge.HandleIncomingAsync
              ├─ SHENORA/READY  → handshake: open the pump's gate, answer with ShellInfo
              └─ everything else → IMessageDispatcher.DispatchAsync
                                     └─ error handler → logging → app middleware → scoped router → modules
```

The order is load-bearing (D11/D16): the error handler is registered FIRST so it wraps everything after
it. `MessageDispatcherExtensions` builds every mapping helper over the interface's ONE composition
primitive, `Use`, so a decorator has four members to write and every helper works on it for free.

**`DispatchAsync` never throws and never returns null.** Unhandled → `NO_HANDLER`; an escaped
`ShenoraException` → its structured error; anything else → `UNKNOWN_ERROR` plus the exception TYPE name.
The contract extends to every seam the boundary itself calls, not just to handlers — bookkeeping must
never decide a request's fate.

**Late mapping is supported**, because the WinForms host maps its window facades after the form exists.
So the pipeline is thread-safe by construction: copy-on-write middleware array, volatile pipeline field,
invalidate-then-rebuild under one lock.

## The error boundary is ONE implementation

`IpcErrorMapping` is the only place an exception becomes a wire error. Two things reach the client and
nothing else: an `ShenoraException`'s own structured error, or `UNKNOWN_ERROR` + the exception type name.
Message, stack and inner exception stay host-side.

⚠ **`ShenoraException`'s message crosses verbatim** — that is the one sanctioned channel, for failures an
app describes in its own words. Building one from `ex.Message` therefore bypasses the whole boundary.

Cancellation is a NORMAL outcome with its own code (`OPERATION_CANCELLED`), mapped AFTER
`ShenoraException` so an app that models cancellation in its own words keeps them.

## Outbound: always a batch, behind a gate

`NotificationPump` owns the bounded drop-oldest queue, the ready gate, coalescing and the guarded
serialize; it owns no timer and no transport, because which thread may touch a client is a base-specific
fact. The base drives the tick and calls `TryDrainBatch`.

- **Buffering starts at CONSTRUCTION**, not at `Open`, so an event emitted during a slow host init still
  reaches the queue.
- **A single event is a batch of one** — `category` alone discriminates, which is what lets the same
  envelope ride postMessage, a WebSocket or a mobile channel (D16).
- **Coalescing is opt-in by the EMITTER** (`CoalesceKey`). The pump cannot tell a snapshot from a delta,
  and coalescing deltas loses data.
- **The gate re-closes on the document, never on a navigation ATTEMPT.** A trigger that fires for
  navigations which never replace the document closes the gate forever, since the surviving page has
  already spent its one handshake.

## The request lifecycle (D66)

There is ONE identity: the request's own id. `IpcRequestStatus` carries the live state and nothing that
`IpcRequest` already has.

**The grace period is the whole design.** Every request is tracked automatically and NOTHING is published
until one outlives `GracePeriod` (50 ms) — so the fast path, which is nearly every request, never reaches
the wire. That is what removes the judgement call a module author would otherwise make about whether a
route is "long-running". It suppresses notifications only, never the response.

Tracking belongs to the **dispatch boundary**, which is the only place that sees every module and every
outcome; a route reaches its scope through an ambient matched BY REQUEST ID
(`IpcRequestScopeAccessor.For`), captured once by `IModuleContext` so background work keeps reporting
against the request that started it.

⚠ **A caller that reports an outcome must propagate the transition's own answer, never infer one.**
`Cancel` checks under one lock and transitions under another — `CancellationTokenSource.Cancel` runs
callbacks that re-enter the tracker, so it cannot run under the lock — and a finished entry can be GONE
rather than merely changed.

## Scoped routing

`ScopedContainerRouter` gives each app-defined scope its own `ServiceProvider`, built lazily and
single-flight. Three properties are easy to lose: a scoped module called without a scope answers a
structured `SCOPE_REQUIRED` rather than falling through; exceptions reach the pipeline's error mapping
instead of a local catch; and scope creation cannot build two providers under a first-request race.

Each scope is a ROOT provider, not a DI child scope — so `AddScoped` there behaves as a singleton for that
scope's lifetime, which is usually what an app wants and is the opposite of what `AddScoped` means
elsewhere.

## What is deliberately absent

- **No per-request client cancellation on the transport.** The dispatch token is a LIFETIME; "the client
  changed its mind" is an app-level CANCEL route carrying the request id (D23).
- **No `ConfigureAwait(false)` in the dispatch path.** The pipeline preserves the caller's synchronization
  context by design, because a facade routing a window command must resume on the UI thread. Work a route
  hands OFF to the background is not the dispatch path and must not capture it.
- **No `Task.Run` per inbound message** — a measured pool-starvation freeze.
- **No name reservation for DI-registered facades.** `UseMessageDispatcher` maps them through one terminal
  middleware resolved on first dispatch, because reading their names inside the `IMessageDispatcher`
  singleton factory is an unbounded recursion with no diagnostic. So `IsModuleMapped` answers `false` for
  a routed DI module; map anything a plug-in must collide with explicitly.
