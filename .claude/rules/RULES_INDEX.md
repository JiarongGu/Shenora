# Rules index — scan "Applies when", read the ones your task touches

**Core** (`.claude/rules/`) auto-loads every session. **Knowledge** (`.claude/knowledge/`) does not —
when your task matches a row's *Applies when*, `Read` that file before touching the area. Keeping a
body out of context until it is needed is the point.

Add one with `node devtools/dev.mjs knowledge new <kebab-name> [--core]`; check with `… knowledge
check|footprint`. **This INDEX is always loaded, so every rule costs core bytes for its row** — one
clause per row, and trim here before raising the cap — raise it only when the growth is earned, with
the reason written next to the constant (once so far, 16→17 KB). The budget WARNS, never fails: a
style budget must not block a release.

## Core (always loaded)

| Rule | Applies when | Enforces |
|---|---|---|
| [skills-workflow](skills-workflow.md) | starting any non-trivial task | the gate: `/doc-loader` + `/pattern-finder` + `/skill-loader` first, then `/phase-review` before committing |
| [phase-workflow](phase-workflow.md) | any multi-step change (there are no phases any more) | build → verify (`dev.mjs verify`) → adversarial review → docs sync → commit only on approval; tripwires sabotage-verified both ways |
| [windows-dev-gotchas](windows-dev-gotchas.md) | running shell/PowerShell on this machine | PS5 UTF-8/BOM traps, Node `fs.cpSync` crash, `MSYS_NO_PATHCONV`, WebView2 CDP arg gotcha |
| [sensitive-info](sensitive-info.md) | writing tracked files / commit messages, or rewriting history | no absolute local paths, no private sibling names, no personal data in the public repo; private context → `local/`; a committed leak is a history problem |
| [persist-working-state](persist-working-state.md) | any multi-step task; whenever you make a decision or hit a milestone | context is ephemeral (compaction) — checkpoint in-progress state to its durable repo home AS YOU GO; never the system temp |

## Knowledge (read on demand — `.claude/knowledge/`)

| Rule | Applies when | Enforces |
|---|---|---|
| [extraction-sources](../knowledge/extraction-sources.md) | extracting/porting host, IPC, window or native-service code from a sibling; deciding where a component should come from | which sibling proved which component, the gaps to FIX during the port, and the port discipline (keep the post-mortem comments) |
| [generic-library](../knowledge/generic-library.md) | designing or changing ANY public API, naming a type/package, **deciding which package a type belongs in**, or a consumer asks for a feature | generalize the request, never ship its shape; mechanism names not scenario names; seams over flags; every public type earns its keep; the D19/D20 placement law |
| [webview2-hosting](../knowledge/webview2-hosting.md) | changing WebView2 hosting/serving/session code, adding a resource scheme or injected script, **or touching UI-thread marshalling in ANY package** | environment thread-affinity; the ONE marshalling owner and its four invariants; the sync-bundle vs deferred-scheme split; init timeout; guarded app callbacks; CDP re-append |
| [ipc-contracts](../knowledge/ipc-contracts.md) | touching the IPC stack on either side (`src/Shenora.Ipc/`, `WebViewIpcBridge`, `src/Shenora.React/src/`), adding a transport, or writing adoption shims | C#⇄TS wire mirror kept by tripwires; no raw exception text on any error path; never-throws dispatch; always-batched notifications; context-preserving pipeline |
| [winforms-shell](../knowledge/winforms-shell.md) | touching any desktop primitive in `src/Shenora.WinForms/` — bootstrap, frameless chrome/maximize, tray, secondary windows, single-instance, window state, clipboard | STA-or-fail; idempotent init; `UserClosing` also means a PROGRAMMATIC close; manual maximize via `IAppMaximizable`; `FormClosed` ≠ pump finished; pre-handle intent in a flag |
| [doc-claims](../knowledge/doc-claims.md) | writing prose about what the code DOES — XML comments, `ADOPTION.md`, a README | verify against the SOURCE, not the design doc; pin a surprise with a test |
| [mobile-shells](../knowledge/mobile-shells.md) | touching `Shenora.Maui`, a page shipping to more than one shell, or a device harness (`dev.mjs android\|mac`) | the C# ports for free, so the cost lands elsewhere — page written for the SUPERSET, RID matched to the target, device log filtered BEFORE tailing, the iOS Xcode/workload pairing |
