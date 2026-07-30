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
`onPostError` (default `console.error`) rather than vanishing.

Pure-UI development in a plain browser: pass a `fallback` to `configureBridge` (gated behind
`import.meta.env.DEV`) to answer requests with canned data. Other shells (WebSocket,
mobile/Capacitor) implement the small `ShenoraTransport` seam and speak the same envelopes.
For CDP-driven testing, `installDevInterceptor()` records IPC/event traffic into ring buffers
and exposes `window.__shenora.call()/waitEvent()`.

MIT © Jiarong Gu
