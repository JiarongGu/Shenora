# One-way IPC, synchronized module state, and long-running operations — design (2026-07-31)

Status: **DESIGN, approved to write, not implemented.** Tracked as `TASKS.md` P6.3a, which blocks
P6.4. Public surface, so it must land before P7 freezes SemVer.

Read `.claude/knowledge/ipc-contracts.md` first — this design is constrained by it in three places
and contradicts it in none. It is **extraction-first**: §5 generalizes a pattern three sibling apps
each built for themselves, rather than an API invented here (`.claude/knowledge/extraction-sources.md`;
the named survey is in `local/`).

## 1. The problem

**Verified, not assumed** (`src/Shenora.React/src/bridge.ts` + the barrel): the client bridge has
exactly ONE outbound call, `invoke()`. It allocates a pending entry, starts a timer, awaits a
correlated response, and rejects with `TIMEOUT` after 30 s by default. There is no way to send a
message without expecting a reply, and no way to hold state fed by the host's event stream.

That makes the wrong thing the only thing, for two reasons:

- **A correlated call carries a deadline; real work does not.** Anything longer than the timeout
  cannot use the only path the kit offers.
- **Request/response is UI-THREAD-COUPLED here by design.** The dispatch pipeline preserves the
  caller's synchronization context deliberately (`ipc-contracts`: "transports dispatch on the UI
  thread and every handler's synchronous segment stays there"), because a facade routing a window
  command must resume on the UI thread. The kit already pays that knowingly in one place:
  `WindowCommandFacade.Post` documents `START_DRAG` blocking for the entire OS modal loop, accepted
  and commented. Making request/response the default generalises that stall to the whole app.

**So the default for a desktop shell is: post a message, stream results back as events, and keep the
resulting STATE somewhere many components can read.** Request/response is the special case — quick,
UI-thread-safe calls, which is exactly what `WindowCommandFacade` uses it for. (User direction,
2026-07-31; the first scoping pass had this backwards and treated the event model as legacy.)

### 1.1 A precision the design must not overclaim

**A client-side `post` does not, by itself, free the UI thread.** The host receives
`WebMessageReceived` on the UI thread and dispatches there whether or not the client awaits, so a
handler that does heavy work synchronously stalls the window either way. What `post` removes is the
client-side deadline and bookkeeping, and the pretence that a reply is coming.

Freeing the UI thread is the HOST half: the handler returns immediately and streams results later.
Both halves are needed, and §3 is not optional garnish on §2.

## 2. Client: the missing outbound half

```ts
post<TPayload = unknown>(module: string, type: string, options?: PostOptions<TPayload>): string
```

- **No wire change.** It sends the same `IpcRequest` envelope, id included, so the C#⇄TS mirror and
  `WireMirrorTests` are untouched. Nothing new for a transport to learn (D16: the envelope is what
  lets the same messages ride postMessage, a WebSocket, or a mobile channel).
- **No pending entry and no timer**, so there is nothing to leak and no deadline to breach.
- Returns the id, so a caller that wants to correlate can. Returning `void` would force every
  correlating caller to generate its own id and pass it in the payload — the same information in two
  places, and a chance for them to disagree.

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

The correlation id **goes in the notification PAYLOAD. Never in `module`, `type` or `scope`.**

This is not a style preference. `EventBus` keeps a match cache keyed on module/type/scope, and
`ipc-contracts` already records the rule the hard way: those values "must be drawn from SMALL sets
(profiles, windows), never per-entity ids", because the cache lives per subscription × distinct event
key. An operation id is exactly a per-entity id — putting it in `scope` would grow the cache without
bound for the process lifetime. **Confirmed by real usage, not only derived:** a sibling's progress
events carry `p.jobId` in the payload with a fixed event type.

**Naming (D22 — name for the mechanism, never a scenario):** `operationId`, not `jobId`/`taskId`.
It matches the vocabulary the kit already speaks — `OperationException`, `OPERATION_CANCELLED` — and
"job" is a scenario word that would invite a job-queue product to grow behind it.
`OPERATION_CANCELLED` already exists, so cancellation needs no new error surface.

## 5. The primitive this is really about: a synchronized module store

§2–§4 make one-way messaging *possible*. They do not stop every app rebuilding the same wiring, and
the survey says every app does.

### 5.1 What the reference apps actually built (three of them, independently)

- The **adoption target** has a `createIpcStore` helper whose own doc says it "replaces the
  `let wired; if (!wired) { onMessage(...) }` boilerplate that **every host-backed store repeated**",
  and ~12 feature stores built on it. Each is: subscribe once → switch on the event → fold into
  state → components select.
- It also has a component-level `useEventSubscription`, documented as the counterpart — "use the
  store pattern for shared/persistent state, this hook for a component that needs to react
  directly" — and noted as **mirroring a second sibling's** version of the same hook.
- That second sibling has the archetype in full: a `useJobsSync` whose doc reads "loads the job list
  once and subscribes to JOB_UPDATED / JOB_PROGRESS / JOBS_CHANGED … **shared by the full Tasks
  panel and the per-tab progress strip so each tab reflects current job state without
  re-implementing the wiring**".
- The **third sibling** ships the same component-level hook.

Two apps needing it is the generalization bar (`generic-library`); three built it, and one of them
factored it twice. This is a harvest, which is the standing direction for how the kit grows.

### 5.2 The thing the first draft of this design got wrong

`useJobsSync` **loads the list first, then subscribes**. That ordering is the whole point: a
component that mounts while an operation is already running has MISSED its events, and a stream
cannot be replayed. Deltas alone are not a design — **snapshot + deltas** is.

The first draft of this document proposed "START returns an `operationId`, then events carry it",
which silently assumed the watcher was there from the beginning. In an app it usually is not: a
progress strip mounts when you open a tab, long after the work started.

### 5.3 The shape

One factory, returning one hook — so a feature's IPC, its event stream and its state are declared
together instead of hand-wired in three places:

```ts
const useDeploy = createShenoraStore('DEPLOY', {
  initial: { status: 'idle', lines: [] },
  snapshot: 'GET_STATE',              // invoke once on first subscriber → initial state
  on: {                               // module event type → pure reducer over state
    STARTED:  (s, p) => ({ ...s, status: 'running', operationId: p.operationId }),
    PROGRESS: (s, p) => ({ ...s, lines: [...s.lines, p.line] }),
    ENDED:    (s, p) => ({ ...s, status: p.ok ? 'done' : 'failed' }),
  },
  actions: ({ post, invoke }) => ({
    start: (cfg) => post('START', { payload: cfg }),
    cancel: (id) => post('CANCEL', { payload: { operationId: id } }),
  }),
});
```

Properties that make it worth being in the kit rather than in each app:

- **Subscribes ONCE per store, not per component.** N components mounting does not mean N
  subscriptions, and unmounting the last one tears it down.
- **A late mounter sees current state**, via `snapshot` on first subscription (§5.2) plus the shared
  state thereafter.
- **Built on React's `useSyncExternalStore`, so the kit imposes NO state library.** All three
  siblings reached for zustand; the kit must not (`@shenora/react`'s only peer is React, and D13's
  spirit is that apps bring their own). `useSyncExternalStore` exists for exactly this shape and is
  tearing-free under concurrent rendering.
- **Structural routing, which the siblings could not have.** Their events are flat strings they
  string-match and split on `'.'`; Shenora notifications already carry `module` and `type`
  separately, so `on` keys on the type within a module — no parsing, and typo-checkable.
- **Reducers are PURE**, so the store is testable with no bridge, and an app callback throwing
  cannot corrupt shared state (it is caught and reported, consistent with the kit's guarded-callback
  rule).

### 5.4 Relationship to what already exists

`useShenoraEvent` stays exactly as it is — it is the component-level counterpart both siblings
document alongside their store pattern, and the two are complementary, not competing. The rule to
write down: **shared or long-lived state → the store; a one-off reaction in one component → the
hook.** That sentence is lifted almost verbatim from a sibling's own doc comment, which is the best
evidence it is the right rule.

## 6. What this deliberately does NOT ship

Per `generic-library` (primitives + hooks, not the product; every public type earns its keep):

- **No operation/job manager, registry, queue, or progress TYPE.** The kit ships posting, streaming,
  the correlation convention and the store primitive. What an operation IS — its phases, its
  progress shape, whether they queue — belongs to the app. A `JobStore` in `src/` would be the D21
  failure exactly.
- **No state library, and no store devtools integration.** See §5.3.
- **No `oneWay` flag on the envelope.** Considered and rejected: it would let the host skip the
  response, which is the only channel a failure has. Silent failure costs more than a response
  nobody reads.
- **No automatic re-`snapshot` on reconnect/reload** in v1. The host's ready gate + the document
  reset already re-establish a page's world; adding a second recovery path before a consumer needs
  it is speculative. Revisit if the adoption shows it.

## 7. Verification plan (a wire + threading + shared-state change — assert, don't assume)

- **Mirror:** `WireMirrorTests` must stay green untouched. If it needs editing, the design has
  drifted into a wire change and that is a signal to stop.
- **No leak:** posting N messages leaves the pending map empty and the unawaited-id set at its cap,
  not at N. Mounting N components against one store yields ONE subscription; unmounting all of them
  yields zero.
- **The late-mounter case, which is the reason §5 exists:** start an operation, let events flow,
  THEN mount a component and assert it renders current state rather than empty. This is the test the
  first draft would have shipped without.
- **Not silent:** a failed one-way call reaches the error sink with its module, type and code, and a
  throwing reducer does not corrupt the store. Verify by BREAKING each (the standing tripwire rule).
- **The UI-thread claim — MEASURED 2026-07-31, and it holds.** A claim about the UI thread that is
  only reasoned about is exactly the P5.6 mistake in a new costume, so the sample gained a `SAMPLE.SLOW`
  route with both shapes (`mode: 'block' | 'stream'`, same 3 s of work) and responsiveness was sampled
  with `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` — which returns only when the target thread
  PUMPS, so a failure means the UI thread is busy:

  | shape | samples | unresponsive | longest stall |
  |---|---|---|---|
  | work left in the route's synchronous segment | 61 | 13 | **2 027 ms** |
  | work handed off, results streamed as events | 95 | **0** | **0 ms** |

  Identical work and duration; only the shape differs. The streamed run also reports
  `onUiThread: false` from its background body, so the handoff is confirmed rather than assumed, and
  the page renders "streaming 3/6 (off the UI thread)" while the 1 Hz tick keeps advancing.
  **Note what this does and does not prove** (§1.1): the dispatch is on the UI thread either way —
  what frees it is the HOST returning immediately, not the client declining to await.
  ⚠ Two vacuous readings were caught on the way, both by reading the output instead of the summary
  line: a first pass where `Start-Process` failed so the click never landed (0 stalls, i.e. a PASS for
  the wrong reason), and screenshots at ~1 s intervals being far too coarse to see a 3 s freeze. The
  probe now refuses to report unless the click confirms it landed.
- **Baseline:** any host-side surface change reviewed BY TYPE SECTION before promotion.
