# @shenora/react

React client for [Shenora](https://github.com/JiarongGu/Shenora) desktop hosts (.NET + WinForms +
WebView2). The typed bridge between a React frontend and the Shenora host: correlated `invoke`
with timeouts and structured errors, the event hub host notifications stream into, typed module
services, React hooks, and a pluggable transport with a browser fallback so the UI can be
developed in a plain browser. Headless by design — no UI components, bring your own design
system. Versioned in lockstep with the `Shenora.*` NuGet packages.

```ts
import { getBridge, useShenoraEvent, useShenoraQuery, BaseModuleService } from '@shenora/react';

// once, at startup, after your listeners are attached:
await getBridge().notifyReady();

// a typed service per backend module:
interface TodoRequests { GET_ALL: void; ADD: { title: string } }
class TodoService extends BaseModuleService<TodoRequests> {
  constructor() { super('TODO'); }
  getAll() { return this.send<TodoItem[]>('GET_ALL'); }
  add(title: string) { return this.send<TodoItem>('ADD', { payload: { title } }); }
}

// in components:
const { data, loading, refetch } = useShenoraQuery<TodoItem[]>('TODO', 'GET_ALL');
useShenoraEvent<TodoItem>('TODO', 'ADDED', (todo) => refetch());
```

Failed calls reject with `OperationError` — a structured `code` (an i18n key: translate
`errors.{code}`) plus interpolation `parameters`, never raw host exception text.

Pure-UI development in a plain browser: pass a `fallback` to `configureBridge` (gated behind
`import.meta.env.DEV`) to answer requests with canned data. Other shells (WebSocket,
mobile/Capacitor) implement the small `ShenoraTransport` seam and speak the same envelopes.
For CDP-driven testing, `installDevInterceptor()` records IPC/event traffic into ring buffers
and exposes `window.__shenora.call()/waitEvent()`.

MIT © Jiarong Gu
