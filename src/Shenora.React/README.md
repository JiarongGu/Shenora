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

### Requests in flight: a ready-made progress store

Every request the host handles is tracked automatically — there is nothing to declare on either side.
`useShenoraRequests()` is a `createShenoraStore` instance built the same way as the example above:

```ts
import { useShenoraRequests } from '@shenora/react';

const running = useShenoraRequests((s) => s.running);           // every request still in flight
const finished = useShenoraRequests((s) => s.finished);         // retained history, newest first
const importJob = useShenoraRequests((s) => s.byId[requestId]); // one, by id

useShenoraRequests.actions.cancel(requestId);   // XMLHttpRequest.abort() — the id you sent with
useShenoraRequests.actions.clearFinished();
```

🔴 **The id is the one you already have.** `requestId` is the `id` of the request you sent — there is no
second identity to correlate. Cancelling it targets the token the route is running under.

⚠ **Most requests never appear here, and that is the design.** The host stays SILENT for the first
50 ms (`IpcRequestTrackerOptions.GracePeriod`): a request that finishes inside that window emits no
event at all — no running snapshot, no completion, nothing retained. So this store is a list of work
that is actually *taking a while*, not a log of every call your page made. Nobody wants a spinner for
five milliseconds of work, and the clock decides that at run time rather than a module author guessing
at authoring time.

`request.progress` is an exported `IpcProgress` (`{ value: number; total?: number; unit?: string }`) —
import the type rather than re-declaring the shape. It is the APP's own unit (bytes of a known total,
items of a known total, an absolute count with no known total, or a genuine percent), never a
kit-assumed percentage: `total` is the denominator when one is known and `undefined` when there is
none, and `unit` is app-defined and uninterpreted. The kit ships no percent helper — render a ratio
only when you have a `total`:

```ts
import type { IpcProgress } from '@shenora/react';

function format(progress?: IpcProgress): string {
  if (!progress) return 'starting…';
  return progress.total
    ? `${Math.round((progress.value / progress.total) * 100)}%`
    : `${progress.value}${progress.unit ? ` ${progress.unit}` : ''}`;   // no known total
}
```

That division is your own policy, not the kit's, which is why it lives here instead of in `src/`.

The store snapshots via `LIST` on first subscribe (so a progress strip that mounts mid-run is not
empty), then folds `REQUEST_UPDATED` by id — one subscription however many components read it. Two
bands, both derived from `byId` on every read: `running` and `finished`. Filtering by your own
`module`/`type` is a plain `Array.filter` over either.

`clearFinished` does not touch local state itself: the host's `REQUEST_REMOVED { requestIds }` is the
one authoritative removal signal the store folds, deleting exactly the named ids. History eviction and
`clearFinished` both publish it, so a long-lived store's mirror of bounded host history cannot drift
from what the host actually did.

⚠ **Work nobody requested does not belong here.** A scheduled job, a background sync, anything the host
starts on its own — those have no request behind them and no response to wait for, so they report on
their own event stream via `useShenoraEvent`. Squeezing them in here is what the previous design did,
and it is why it needed a "waiting" state nothing else could explain.

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
