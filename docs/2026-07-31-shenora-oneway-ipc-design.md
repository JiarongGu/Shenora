# One-way IPC + long-running operations — design (2026-07-31)

Status: **DESIGN, approved to write, not implemented.** Tracked as `TASKS.md` P6.3a, which blocks
P6.4. Public surface, so it must land before P7 freezes SemVer.

Read `.claude/knowledge/ipc-contracts.md` first — this design is constrained by it in three places
and contradicts it in none.

## 1. The problem

**Verified, not assumed** (`src/Shenora.React/src/bridge.ts` + the barrel): the client bridge has
exactly ONE outbound call, `invoke()`. It allocates a pending entry, starts a timer, awaits a
correlated response, and rejects with `TIMEOUT` after 30 s by default. There is no way to send a
message without expecting a reply.

That makes the wrong thing the only thing, for two reasons:

- **A correlated call carries a deadline; real work does not.** Anything longer than the timeout
  cannot use the only path the kit offers.
- **Request/response is UI-THREAD-COUPLED here by design.** The dispatch pipeline preserves the
  caller's synchronization context deliberately (`ipc-contracts`: "transports dispatch on the UI
  thread and every handler's synchronous segment stays there"), because a facade routing a window
  command must resume on the UI thread. The kit already pays that knowingly in one place:
  `WindowCommandFacade.Post` documents `START_DRAG` blocking for the entire OS modal loop, accepted
  and commented. Making request/response the default generalises that stall to the whole app.

**So the default for a desktop shell is: post a message, stream results back as events.**
Request/response is the special case — quick, UI-thread-safe calls, which is exactly what
`WindowCommandFacade` uses it for. (User direction, 2026-07-31; the first scoping pass had this
backwards and treated the event model as legacy.)

### 1.1 A precision the design must not overclaim

**A client-side `post` does not, by itself, free the UI thread.** The host receives
`WebMessageReceived` on the UI thread and dispatches there whether or not the client awaits, so a
handler that does heavy work synchronously stalls the window either way. What `post` removes is the
client-side deadline and bookkeeping, and the pretence that a reply is coming.

Freeing the UI thread is the HOST half: the handler returns immediately and streams results later.
Both halves are needed, and §3 is not optional garnish on §2.

## 2. Client: the missing outbound half

```ts
post<TPayload = unknown>(module: string, type: string, options?: PostOptions<TPayload>): void
```

- **No wire change.** It sends the same `IpcRequest` envelope, id included, so the C#⇄TS mirror and
  `WireMirrorTests` are untouched. Nothing new for a transport to learn (D16: the envelope is what
  lets the same messages ride postMessage, a WebSocket, or a mobile channel).
- **No pending entry and no timer**, so there is nothing to leak and no deadline to breach.
- Returns the id, so a caller that wants to correlate can (see §4). Returning `void` would force
  every correlating caller to generate its own id and pass it in the payload — the same information
  in two places, and a chance for them to disagree.

### 2.1 The part that is easy to get wrong: failures must not go silent

Today an inbound response whose id has no pending entry is **dropped without a trace**
(`bridge.ts`: `if (!entry) return;`). Add `post` naively and every failed one-way call becomes a
silent failure — precisely the class this repo keeps fixing (a mistyped resource prefix degrading to
an all-404 provider; a doctor check satisfied by the prose explaining it).

So `post` records its id in a bounded set of unawaited sends. When a response arrives for one:

- `success: true` → discard, nothing was expected.
- `success: false` → report through a bridge-level error sink (default `console.error`,
  overridable on `ShenoraBridgeOptions`) carrying module, type and the structured `IpcError`.

The set is CAPPED (drop-oldest) so a host that never answers cannot grow it without bound — the same
shape as the host's own notification queue. An id evicted before its response merely loses the error
report, which is strictly better than today's behaviour for every call.

## 3. Host: a route that answers with events

**No new host API is required**, and that is deliberate — a facade route can already return `Done()`
immediately and stream through `IEventBus`. What the kit owes is the CONVENTION and one boundary
clarification:

- A route that starts long work returns immediately. It does not block the dispatch, because that
  dispatch is on the UI thread.
- **Clarify the `ConfigureAwait(false)` rule, which currently reads as blanket.** It is banned in the
  DISPATCH PATH because facades must resume on the UI thread. A background operation body started by
  a route is not the dispatch path, and it should NOT capture the UI context. Today's wording would
  argue a future session into keeping long work on the UI thread — the exact failure this design
  exists to prevent. Fix the rule text as part of this work.
- The existing "never `Task.Run`-per-message" rule stands and does not conflict: that is about the
  TRANSPORT spawning per inbound message (a measured pool-starvation freeze), not about a handler
  deliberately handing off one long operation.

## 4. Correlating a streamed result — and the constraint that decides it

An operation started by one message reports progress and completion as notifications. The
correlation id **goes in the notification PAYLOAD. Never in `module`, `type` or `scope`.**

This is not a style preference. `EventBus` keeps a match cache keyed on module/type/scope, and
`ipc-contracts` already records the rule the hard way: those values "must be drawn from SMALL sets
(profiles, windows), never per-entity ids", because the cache lives per subscription × distinct event
key. An operation id is exactly a per-entity id — putting it in `scope` would grow the cache without
bound for the process lifetime. (A `'.'`-join in those keys once let two different events share and
permanently poison one entry; the fix was `'\0'`-joining, not a bigger cache.)

The shape, then:

1. **START** is a normal `invoke` — it is quick and UI-thread-safe by construction, because all it
   does is validate and hand off. It returns `{ operationId }`.
2. **Progress/result/failure** are notifications on a small, fixed set of types, each carrying
   `operationId` in the payload.
3. **CANCEL** is a `post` carrying the `operationId`.

**Naming (D22 — name for the mechanism, never a scenario):** `operationId`, not `jobId`/`taskId`.
It matches the vocabulary the kit already speaks — `OperationException`, `OPERATION_CANCELLED` — and
"job" is a scenario word that would invite a job-queue product to grow behind it.

`OPERATION_CANCELLED` already exists as a reserved code, so cancellation needs no new error surface.

## 5. What this deliberately does NOT ship

Per `generic-library` (primitives + hooks, not the product; every public type earns its keep):

- **No operation/job manager, registry, or progress type.** The kit ships the ability to post, the
  ability to stream, and the documented correlation convention. What an operation IS belongs to the
  app.
- **No client `useOperation` hook yet.** `useShenoraEvent` plus a payload filter already covers it.
  Harvest one later if the adoption shows two consumers writing the same thing — that is the
  standing harvest-driven rule, and the same trigger that finally justified the shared
  `debounce`/`randomUUID` helpers in H7.
- **No `oneWay` flag on the envelope.** Considered and rejected: it would let the host skip the
  response, which is the only channel a failure has. Silent failure costs more than a response
  nobody reads.

## 6. Verification plan (this is a wire + threading change — assert, don't assume)

- **Mirror:** `WireMirrorTests` must stay green untouched. If it needs editing, the design has
  drifted into a wire change and that is a signal to stop.
- **No leak:** posting N messages leaves the pending map empty and the unawaited-id set at its cap,
  not at N.
- **Not silent:** a failed one-way call reaches the error sink with its module, type and code. Verify
  by BREAKING it (the standing tripwire rule) — remove the reporting and confirm the test fails.
- **The UI-thread claim, measured not asserted:** drive a deliberately slow route in the sample both
  ways and show the window still repaints during the posted one. A claim about the UI thread that is
  only reasoned about is exactly the P5.6 mistake in a new costume.
- **Baseline:** any host-side surface change reviewed BY TYPE SECTION before promotion.
