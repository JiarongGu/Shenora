# Rules index — scan "Applies when", read the ones your task touches

Two tiers, to keep the always-loaded base small as the rule set grows:

- **Core** (`.claude/rules/`) — auto-loaded every session; applies to nearly any task.
- **Knowledge** (`.claude/knowledge/`) — **not** auto-loaded; domain-specific. When your task matches
  a row's *Applies when*, `Read` that file before you touch the area. The body isn't in context until
  you read it — that's the point (it keeps sessions lean).

Add a rule with `node devtools/dev.mjs knowledge new <kebab-name> [--core]` (scaffolds from
`TEMPLATE.md` and appends a row here). Check the system stays consistent with
`node devtools/dev.mjs knowledge check`; see the always-loaded size with `… knowledge footprint`.

## Core (always loaded)

| Rule | Applies when | Enforces |
|---|---|---|
| [skills-workflow](skills-workflow.md) | starting any non-trivial task | the gate: `/doc-loader` + `/pattern-finder` + `/skill-loader` first, then `/phase-review` before committing |
| [phase-workflow](phase-workflow.md) | any multi-step feature or phase | build → verify (`dev.mjs verify`) → review subagent → docs sync → commit only on approval |
| [windows-dev-gotchas](windows-dev-gotchas.md) | running shell/PowerShell on this machine | PS5 UTF-8/BOM traps, Node `fs.cpSync` crash, `MSYS_NO_PATHCONV`, WebView2 CDP arg gotcha |
| [sensitive-info](sensitive-info.md) | writing tracked files / commit messages, or rewriting history | no absolute local paths, no private sibling names, no personal data in the public repo; private context → `local/`; a committed leak is a history problem |
| [persist-working-state](persist-working-state.md) | any multi-step task; whenever you make a decision or hit a milestone | context is ephemeral (compaction) — checkpoint in-progress state to its durable repo home AS YOU GO; never the system temp |

## Knowledge (read on demand — `.claude/knowledge/`)

| Rule | Applies when | Enforces |
|---|---|---|
| [extraction-sources](../knowledge/extraction-sources.md) | extracting/porting host, IPC, window, or native-service code from a sibling app; deciding where a component should come from | which sibling proved which component, the gaps to FIX during the port (exception handling, runtime check, WebView2 event hooks, options records), and the port discipline (keep post-mortem comments; named paths live in `local/EXTRACTION-MAP.md`) |
| [generic-library](../knowledge/generic-library.md) | designing or changing ANY public API, naming a type/package, or a consumer asks for a feature | generalize the consumer's request, never ship its shape: no app/domain vocabulary in `src/`, seams over flags, options records over magic values, every public type earns its keep |
| [webview2-hosting](../knowledge/webview2-hosting.md) | changing WebView2 hosting/serving code (`src/Shenora.WebView2/`), adding a resource scheme or injected script, or building the P5 sessions package | environment thread-affinity, BeginInvoke marshalling (IsHandleCreated, not InvokeRequired), the sync-bundle/deferred-scheme serving split, init-timeout guard, prewarm-behind-the-gate, no-cache-HTML caching, JSON-escaped injection, CDP re-append |
| [ipc-contracts](../knowledge/ipc-contracts.md) | touching the IPC stack on either side (`src/Shenora.Ipc/`, `WebViewIpcBridge`, `src/Shenora.React/src/`), adding a transport, or writing adoption shims | C#⇄TS wire mirror in lockstep, no raw exception text on any error path, never-throws dispatch boundary, always-batched notifications, ready-gate reset on navigation, context-preserving pipeline, collision-free event keys |
