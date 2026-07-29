# @shenora/react

React client for [Shenora](https://github.com/JiarongGu/Shenora) desktop hosts (.NET + WinForms +
WebView2). Provides the typed bridge between a React frontend and the Shenora host: correlated
`invoke`/`send`/`subscribe`, module services, React hooks, and a browser fallback so the UI can
be developed in a plain browser.

**Status: pre-release skeleton** — the bridge lands with the framework's Phase 3 extraction; the
package currently exposes only `isShenoraAvailable()`. Versioned in lockstep with the `Shenora.*`
NuGet packages.

```ts
import { isShenoraAvailable } from '@shenora/react';

if (isShenoraAvailable()) {
  // running inside a Shenora WebView2 host
}
```

MIT © Jiarong Gu
