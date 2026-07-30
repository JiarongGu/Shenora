# FIX-LOG.md — notable fixes, newest first

Append via `/fix-log` after landing any non-trivial bug/regression fix. Grouped by `## <date>`;
entry template:

```
### <area>: <symptom>
- **Symptom:** what was observed
- **Root cause:** the actual mechanism
- **Fix:** what changed (files)
- **Verify:** how it was proven fixed
- **Commit:** <hash>
```

## 2026-07-30

### @shenora/react packaging: the published tarball was unusable under native Node ESM
- **Symptom:** `npm install <tarball>` then `import('@shenora/react')` in plain Node failed with
  `ERR_MODULE_NOT_FOUND … dist/types` — the package worked in every bundler (Vite, vitest) but
  not under Node's own ESM loader. Found by the P1.1 local-feed consumption smoke, which exists
  exactly to catch what the bundler-based dev loop can't.
- **Root cause:** the sources used extensionless relative imports (`from './types'`), and the
  tsconfig's `moduleResolution: "bundler"` neither requires nor emits extensions — so the
  compiled `dist/*.js` carried extensionless specifiers, which bundlers resolve but native Node
  ESM (and any strict ESM tooling) rejects. Not a regression — the gap existed since the first
  real source files; the sample app masked it because Vite bundles the package.
- **Fix:** explicit `.js` extensions on every relative import/export specifier in
  `src/Shenora.React/src/*.ts` (TS resolves `.js` → `.ts` at build time), and
  `module`/`moduleResolution` switched to `NodeNext` in `tsconfig.json` so a missing extension
  is now a BUILD error — prevention, not just history. Consumption recipe recorded in
  `docs/RELEASING.md`.
- **Verify:** rebuilt + re-packed; the scratch npm consumer (`devtools/_p11-consumer/npm`)
  imports the tarball under plain Node and resolves every export; full `verify` green
  (273 dotnet + 39 vitest); the NuGet side of the same smoke pins `[0.1.0]` from the local feed
  and runs a live dispatch round-trip.
- **Commit:** _pending_
