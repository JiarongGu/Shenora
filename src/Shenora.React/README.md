# @shenora/react

React client for [Shenora](https://github.com/JiarongGu/Shenora) desktop hosts (.NET + WinForms +
WebView2). The typed bridge between a React frontend and the Shenora host: correlated `invoke`
with timeouts and structured errors, the event hub host notifications stream into, typed module
services, React hooks, and a pluggable transport with a browser fallback so the UI can be
developed in a plain browser. Headless by design — no UI components, bring your own design
system. Versioned in lockstep with the `Shenora.*` NuGet packages.

```ts
import {
  getBridge, useShenoraEvent, useShenoraQuery, BaseModuleService, createShenoraStore,
} from '@shenora/react';

// Once at startup, after your listeners are attached: it starts notification delivery (anything the
// host buffered arrives in the first batch). Drop zones need no particular ordering against it — the
// host clears them when a new DOCUMENT loads, not on this handshake.
await getBridge().notifyReady();

// a typed service per backend module:
interface NoteRequests { GET_ALL: void; ADD: { title: string } }
class NoteService extends BaseModuleService<NoteRequests> {
  constructor() { super('NOTES'); }
  getAll() { return this.send<Note[]>('GET_ALL'); }
  add(title: string) { return this.send<Note>('ADD', { payload: { title } }); }
}

// in components:
const { data, loading, refetch } = useShenoraQuery<Note[]>('NOTES', 'GET_ALL');
useShenoraEvent<Note>('NOTES', 'ADDED', (note) => refetch());
```

Failed calls reject with `OperationError` — a structured `code` (an i18n key: translate
`errors.{code}`) plus interpolation `parameters`, never raw host exception text.

### Long-running work: post, then read a store

`invoke` awaits a correlated reply and carries a timeout, and its handler's synchronous segment runs
on the host's **UI thread** — so it is for calls that are quick and UI-thread-safe. Everything else
posts and streams results back as events:

```ts
const useDeploy = createShenoraStore('DEPLOY', {
  initial: { status: 'idle', lines: [] as string[] },
  // Loaded on the FIRST subscriber, so a component that mounts mid-run isn't empty:
  // events it missed cannot be replayed.
  snapshot: { type: 'GET_STATE', apply: (s, d) => ({ ...s, ...(d as object) }) },
  on: {
    PROGRESS: (s, p: { line: string }) => ({ ...s, lines: [...s.lines, p.line] }),
    ENDED: (s, p: { ok: boolean }) => ({ ...s, status: p.ok ? 'done' : 'failed' }),
  },
  actions: ({ post }) => ({ start: (cfg: unknown) => post('START', { payload: cfg }) }),
});

// any number of components share ONE subscription:
const status = useDeploy((s) => s.status);
useDeploy.actions.start({ env: 'prod' });
```

Use the **store for shared or long-lived state** and **`useShenoraEvent` for a one-off reaction in a
single component**. A failed `post` has no promise to reject, so it is reported through the bridge's
`onPostError` rather than vanishing — it is bridge-wide, so wire it once at startup
(`getBridge()` takes no options):

```ts
configureBridge({ onPostError: (failure) => log.error(failure.module, failure.type, failure.error) });
```

### Tracked operations: a ready-made progress store

For work the host tracks with `Shenora.Ipc`'s operation registry (an `IModuleContext.Run`/`Start`
route on the C# side), `useShenoraOperations()` is a `createShenoraStore` instance built the same
way as the example above — no per-feature event wiring needed:

```ts
import { useShenoraOperations } from '@shenora/react';

const running = useShenoraOperations((s) => s.running);         // every in-flight operation
const waiting = useShenoraOperations((s) => s.waiting);          // paused + interrupted — "needs you", one bucket
const paused = useShenoraOperations((s) => s.paused);            // stopped mid-flight, awaiting a decision
const interrupted = useShenoraOperations((s) => s.interrupted);  // crash-announced, pending resume offer
const importJob = useShenoraOperations((s) => s.byId[jobId]);   // one, by id

useShenoraOperations.actions.cancel(jobId);       // only does anything if the op opted into Cancellable
useShenoraOperations.actions.dismiss(jobId);      // decline a paused/interrupted offer — refuses a running one
useShenoraOperations.actions.pause(jobId);        // ASK the host to pause running work — refuses anything not running
useShenoraOperations.actions.clearFinished();
```

`operation.progress` is `{ value: number; total?: number; unit?: string }` — the APP's own unit
(bytes-of-a-known-total, items-of-a-known-total, an absolute count with no known total, or a genuine
percent), never a kit-assumed percentage: `total` is the denominator when one is known and `undefined`
when there isn't one, and `unit` is app-defined and uninterpreted, exactly like `kind`. The kit ships
no percent helper — render a ratio only when you have a `total`:

```ts
const pct = importJob?.progress?.total
  ? (importJob.progress.value / importJob.progress.total) * 100
  : undefined;   // no known total — show the bare value/unit instead, or an indeterminate spinner
```

That division is your own policy, not the kit's, which is why it lives here instead of in `src/`.

It snapshots via `LIST` on first subscribe (so a progress strip that mounts mid-run isn't empty), then
folds `OPERATION_UPDATED` by id — one subscription however many components read it. The client
mirrors the host's three bands (design §5A.2), not five bare statuses: `running` (Active),
`paused`/`interrupted`/`waiting` (Waiting — `waiting` is `paused` ∪ `interrupted`, exactly what the
host's `Dismiss`/`RequestResume` both accept), and `finished` (Terminal). All five are derived from
`byId` on every read, never a second copy to keep in sync — `waiting` itself is derived from one
internal status set, not a hand-listed pair repeated across getters. Keep `paused` and `interrupted`
apart when your UI needs to (a resume prompt reads differently from a pause-reason display); read
`waiting` when you just want the one "needs attention" bucket for a status bar. Filtering by your own
`module`/`kind` is a plain `Array.filter` over any of them. A `paused` operation carries `pauseReason`
— an app-defined string, like `kind`, OPTIONAL on the host side — for your UI to branch on. An
`interrupted` entry is a pending RESUME **offer** re-registered from the app's own crash checkpoint:
the host never prunes it on its own — it stays offered until your UI calls `resume` or `dismiss`.
`resume`/`dismiss`/`pause` are all fire-and-forget client requests — the host's own
`IOperation.Pause`/`Resume` (called by whoever owns the operation, hearing
`OPERATION_PAUSE_REQUESTED`/`OPERATION_RESUME_REQUESTED`) is what actually changes the state; asking
is not acting. `clearFinished`/`resume`/`pause` do not touch local state themselves at all: the host's
`OPERATION_REMOVED { operationIds }` is the ONE authoritative removal signal the store folds, deleting
exactly the named ids — `MaxHistory` eviction, `clearFinished`, and a dropped crash-resume offer all
publish it, so a long-lived store's mirror of bounded host history cannot drift from what the host
actually did (this replaced two hand-written optimistic local prunes that a past release carried —
one of which was this project's only Critical, a `resume` prune that dropped a still-paused row).
`dismiss` never needed one, since the host's `Dismiss` publishes an ordinary terminal snapshot over
the wire, the same as a real cancel. Use `createOperationsStore({ module, scope })` instead of the
default export if your host renamed `OperationRegistryOptions.ModuleName` or
you need a scope-filtered instance (a secondary window, an auxiliary session) — `clearFinished`
forwards that scope so clearing history in one window cannot wipe another's.

### Observing the whole stream

`useShenoraEvent` and `createShenoraStore` listen for an exact `(module, type)`. When the vocabulary
isn't knowable up front — plug-in-contributed events, a diagnostics tap, or an adoption shim keeping
a legacy "every host message" handler alive while features migrate one at a time — subscribe broadly
instead. Both mirror the host's `IEventBus` and return an unsubscribe:

```ts
const off = eventBus.subscribeToAll((event) => log.debug(event.module, event.type, event.payload));
eventBus.subscribeToModule('DEPLOY', (event) => audit(event));   // every type from one module
```

Delivery is narrowest-first — exact pair, then module, then catch-all — so a broad observer never
runs ahead of the feature code it is observing. Prefer `subscribe` when you know the pair: a
catch-all wakes for every event on the bus.

Pure-UI development in a plain browser: pass a `fallback` to `configureBridge` (gated behind
`import.meta.env.DEV`) to answer requests with canned data. Other shells (WebSocket,
mobile/Capacitor) implement the small `ShenoraTransport` seam and speak the same envelopes.
For CDP-driven testing, `installDevInterceptor()` records IPC/event traffic into ring buffers
and exposes `window.__shenora.call()/waitEvent()`.

MIT © Jiarong Gu
