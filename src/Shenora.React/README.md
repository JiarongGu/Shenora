# @shenora/react

React client for [Shenora](https://github.com/JiarongGu/Shenora) desktop hosts (.NET + WinForms +
WebView2). The typed bridge between a React frontend and the Shenora host: correlated `invoke`
with timeouts and structured errors, the event hub host notifications stream into, typed module
services, React hooks, and a pluggable transport with a browser fallback so the UI can be
developed in a plain browser. Headless by design — no UI components, bring your own design
system. Versioned in lockstep with the `Shenora.*` NuGet packages.

```ts
import { getBridge, useShenoraEvent, useShenoraQuery, BaseModuleService } from '@shenora/react';

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

Pure-UI development in a plain browser: pass a `fallback` to `configureBridge` (gated behind
`import.meta.env.DEV`) to answer requests with canned data. Other shells (WebSocket,
mobile/Capacitor) implement the small `ShenoraTransport` seam and speak the same envelopes.
For CDP-driven testing, `installDevInterceptor()` records IPC/event traffic into ring buffers
and exposes `window.__shenora.call()/waitEvent()`.

MIT © Jiarong Gu
