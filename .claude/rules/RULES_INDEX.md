# Rules index — scan "Applies when", read the ones your task touches

**Core** (`.claude/rules/`) auto-loads every session. **Knowledge** (`.claude/knowledge/`) does not —
when your task matches a row's *Applies when*, `Read` that file before touching the area. Keeping a
body out of context until it is needed is the point.

Add one with `node devtools/dev.mjs knowledge new <kebab-name> [--core]`; check with `… knowledge
check|footprint`. **This INDEX is always loaded, so every rule costs core bytes for its row** — one
clause per row, and trim here before raising the cap.

## What a rule is, and what it costs to write one badly

🔴 **A rule is the INVARIANT, WHEN IT APPLIES, and WHAT BREAKS if it does not. Nothing else.** The
incident that earned it belongs to the commit, exactly as it does for a decision entry (D77) — and the
cost of getting this wrong is not merely length:

- 🔴 **A rule that cannot say when it does NOT apply gets applied everywhere.** That is the failure the
  owner named: *"some of them heavily influence the development (sometimes in a bad way)"*. Every rule
  here should be readable as *"in situation X, do Y, because Z breaks"* — if X is missing, X is
  "always", and a law earned from one incident starts taxing every task.
- 🔴 **THE RULE BASE MODELS A PROSE STYLE, AND THE CODE COPIES IT.** Measured 2026-08-14: **45 % of
  `src/` was comment**, carrying the same 🔴 banners, ALL-CAPS and incident narration these files use —
  in `///` docs that ship to an adopter's IDE. `doc-claims.md` holds the bar for those; this note is
  here because the habit starts in this directory. **Write the shortest thing that still names the
  invariant.**
- **Prefer a GATE or a TEST, and delete the rule when one lands.** A rule is read once per session and
  competes with every other; a mechanism runs every time and names the file.

⚠ **The 32 KB core budget is deliberately SLACK** (owner: *"lets do not care too much of the core
size"*) — it exists to notice a rule base that DOUBLES, not to make you argue for a paragraph. Keep
rows to one clause because it reads better, not to save bytes. **It WARNS, never fails**: a style
budget must not block a release. 🔴 **Prefer a GATE or a TEST to a rule** — `phase-workflow.md` carries
the scoring that earned it: every failure caught was caught by a mechanism, every failure that landed
walked past a rule already describing the correct behaviour.

## Core (always loaded)

| Rule | Applies when | Enforces |
|---|---|---|
| [skills-workflow](skills-workflow.md) | starting any non-trivial task | the gate: `/doc-loader` + `/pattern-finder` + `/skill-loader` first, then `/phase-review` before committing |
| [phase-workflow](phase-workflow.md) | any multi-step change (there are no phases any more) | build → verify (`dev.mjs verify`) → adversarial review → docs sync → commit only on approval; tripwires sabotage-verified both ways |
| [windows-dev-gotchas](windows-dev-gotchas.md) | editing text or running a script on THIS machine — most tasks | PS5 UTF-8/BOM mangling, `node -e` eating backticks, `fs.cpSync`, `MSYS_NO_PATHCONV`, MAX_PATH. ⚠ Area traps live with their area: WebView2 → `webview2-hosting`, emulator/device → `mobile-harness`, STA tests → `winforms-shell` |
| [sensitive-info](sensitive-info.md) | writing tracked files / commit messages, or rewriting history | no absolute local paths, no private sibling names, no personal data in the public repo; private context → `local/`; a committed leak is a history problem |
| [persist-working-state](persist-working-state.md) | any multi-step task; whenever you make a decision or hit a milestone | context is ephemeral (compaction) — checkpoint in-progress state to its durable repo home AS YOU GO; never the system temp |

## Knowledge (read on demand — `.claude/knowledge/`)

| Rule | Applies when | Enforces |
|---|---|---|
| [extraction-sources](../knowledge/extraction-sources.md) | extracting/porting host, IPC, window or native-service code from a sibling; deciding where a component should come from | which sibling proved which component, the gaps to FIX during the port, and the port discipline (keep the post-mortem comments) |
| [generic-library](../knowledge/generic-library.md) | designing or changing ANY public API, naming a type/package, **deciding which package a type belongs in**, or a consumer asks for a feature | generalize the request, never ship its shape; mechanism names not scenario names; seams over flags; every public type earns its keep; the D19/D20 placement law |
| [webview2-hosting](../knowledge/webview2-hosting.md) | changing WebView2 hosting/serving/session code, adding a resource scheme or injected script, **or touching UI-thread marshalling in ANY package** | the ONE marshalling owner and its invariants; environment thread-affinity; the sync-bundle vs deferred-scheme split |
| [ipc-contracts](../knowledge/ipc-contracts.md) | touching the IPC stack on either side (`src/Shenora/Core/Ipc/`, `WebViewIpcBridge`, `src/Shenora.React/src/`), adding a transport, or writing adoption shims | C#⇄TS wire mirror kept by tripwires; no raw exception text on any error path; never-throws dispatch; always-batched notifications; context-preserving pipeline |
| [winforms-shell](../knowledge/winforms-shell.md) | touching any desktop primitive in `src/Shenora.Windows/` — bootstrap, frameless chrome/maximize, tray, secondary windows, single-instance, window state, clipboard | STA-or-fail; idempotent init; `UserClosing` also means a PROGRAMMATIC close; `FormClosed` ≠ pump finished |
| [doc-claims](../knowledge/doc-claims.md) | writing prose about what the code DOES — XML comments, `ADOPTION.md`, a README | verify against the SOURCE, not the design doc; pin a surprise with a test |
| [mobile-shells](../knowledge/mobile-shells.md) | CHANGING the mobile shell (`src/Shenora.Mobile` → `Shenora.Android`/`.iOS`) or a page shipping to more than one shell | the C# ports for free, so the cost lands on the PAGE — MAUI re-deriving headers, `app://` being unregisterable on Android, an iOS eval that kills the app. ⚠ INVARIANTS only; measured numbers → `docs/design/mobile-shells.md`, running it → `mobile-harness` (D77) |
| [mobile-harness](../knowledge/mobile-harness.md) | RUNNING something on a device — `dev.mjs android\|mac`, a build host, or writing a device probe | the traps that make a device result mean nothing: a log tailed before it is filtered, an app that never relaunched, signing over ssh, a probe that cannot tell its own failure from the platform's |
| [probe-diagnostics](../knowledge/probe-diagnostics.md) | writing or debugging a sample probe, or a probe reports FAIL | a probe reading system-wide state survives other processes' failures; a diagnostic naming a bare exception reads as evidence and is worse than none |
| [standing-habits](../knowledge/standing-habits.md) | looking for maintenance work, or about to file a recurring pass as a TASKS entry | the passes that are never "done" (audits, sweeps, re-measurements) — habits to re-run, which is why they are not backlog |
| [debugging-method](../knowledge/debugging-method.md) | a failure is INTERMITTENT, only appears under instrumentation, or has survived several eliminations | A/B the harness and count trials; instrument before theorising; a coherent story is a hypothesis, not a finding |
| [renames-and-prose](../knowledge/renames-and-prose.md) | renaming or removing anything a doc, an XML comment or an adopter could name | the three steps and the skipped third (`stale-scan` triage); a rename damages the sentence that DEFINES it |
| [release-discipline](../knowledge/release-discipline.md) | about to touch a version number, `CHANGELOG.md`'s `## Unreleased` heading, or the release path | the version is the workflow's — a hand-bump moves the baseline and SKIPS a release (cost 0.2.0); `### Breaking` only for names that actually shipped |
