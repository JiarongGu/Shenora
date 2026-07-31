# docs/ — the project memory map

The router: match your task below, read that doc (and the matched rules — scan
`.claude/rules/RULES_INDEX.md`'s *Applies when* column). When a doc is added or a system changes,
update the relevant entry HERE — this file is the durable index.

## Read this when…

| Task | Read |
|---|---|
| Getting oriented / new session | This file, then `docs/ARCHITECTURE.md` + `local/PROJECT_NOTES.md` (private status) |
| Understanding what Shenora is and why | `docs/2026-07-30-shenora-design.md` (the design contract), `docs/BRIEF.md` (originating requirements) |
| Package layering / where a contract belongs / mobile-shareable logic | `docs/2026-07-30-shenora-relayering-design.md` (D19+D20: one Windows shell layer, portable contracts in Core) |
| Sending IPC without awaiting / long-running work / correlating streamed results | `docs/2026-07-31-shenora-oneway-ipc-design.md` (why the event pipe is the default, not request/response) |
| Changing the module contract / tracking a long operation / hosting on a non-WinForms base | `docs/2026-08-01-shenora-communication-core-design.md` (0.2.0: `IModuleContext`, the operation registry, the portable notification channel + per-channel filtering) |
| "Why is it done this way?" | `docs/DECISIONS.md` (numbered rationale — don't relitigate, amend) |
| Picking the next piece of work | `TASKS.md` (root — OPEN only; v0.1.0 shipped, nothing queued) |
| Why a FINISHED decision was made that way | `docs/task-archive.md` (the closed backlog: plans, file:line anchors, judgement calls) |
| What shipped already / verifying history | `docs/ROADMAP.md` `## Done`, `CHANGELOG.md` |
| Reviewing the codebase (full/whole-tree review) | `docs/REVIEW-GUIDE.md` (orientation: invariants by area, risk hotspots, settled decisions, coverage map) |
| Extracting code from a sibling app | `.claude/knowledge/extraction-sources.md` (tracked, de-identified) + `local/EXTRACTION-MAP.md` (private, named) |
| Keeping the library generic | `.claude/knowledge/generic-library.md` |
| When did this break? | `docs/FIX-LOG.md` (use `/fix-log` to append) |
| Adopting Shenora into an existing desktop app | `docs/ADOPTION.md` (stage order, what replaces what, what stays the app's own) |
| Cutting or consuming a release | `docs/RELEASING.md` |
| Touching an invariant / gotcha | `.claude/rules/RULES_INDEX.md` — read the matched rule |
| Dev loop commands | `devtools/README.md` |

## Where things live (fast map)

- `src/` — the packable projects (`Shenora.Core|Ipc|WebView2|WebView2.Sessions|WinForms`) + `Shenora.React/`
  (the `@shenora/react` npm package) + `Directory.Build.props` (the ONLY version source).
- `tests/Shenora.Tests` — the single test project (folders mirror src).
- `samples/` — the sample desktop + web app (Phase 2+; doubles as the e2e subject).
- `devtools/` — one-entry dev loop (`node devtools/dev.mjs <cmd>`); config in `project.config.mjs`.
- `local/` — gitignored private context (real paths, sibling names, session notes).
- `.github/workflows/release.yml` — the manual release pipeline.

## Doc inventory

| Doc | Holds | Nature |
|---|---|---|
| `BRIEF.md` | The originating project brief (requirements + suggested API sketches) | Historical + intent |
| `2026-07-30-shenora-design.md` | The design contract: profiles, packages, IPC contract, gaps to fix, phasing | Keep in sync with reality (dated amendments) |
| `2026-07-30-shenora-relayering-design.md` | The approved re-layering: one Windows shell layer (`WebView2` → `WinForms`), portable contracts + `IUiDispatcher` in Core, sequencing | Design spec — retire into `ARCHITECTURE.md` once implemented |
| `2026-07-31-shenora-oneway-ipc-design.md` | One-way `post` + the operation-correlation convention: why the event pipe is the desktop default, and why the correlation id goes in the PAYLOAD | Design spec — retire into `ARCHITECTURE.md` once implemented (P6.3a) |
| `2026-08-01-shenora-communication-core-design.md` | The 0.2.0 communication core: the event path in the module contract (`IModuleContext`), tracked operations (registry + `OPERATION_UPDATED` + client store), and the base-agnostic `NotificationPump` with per-channel filtering | Design spec — retire into `ARCHITECTURE.md` once implemented |
| `2026-08-01-shenora-communication-core-plan.md` | The task-by-task implementation plan for the design above: 11 tasks in 3 stages, TDD steps, the tripwires to break on purpose | Working plan — delete once the work lands |
| `DECISIONS.md` | Numbered load-bearing choices + why | Living, append/amend |
| `ARCHITECTURE.md` | The as-built map: projects, dependencies, public surface | Keep in sync with reality |
| `ROADMAP.md` | Done (narrative, newest first) + Remaining (by phase) | Living |
| `FIX-LOG.md` | Notable fixes: symptom / root cause / fix / verify / commit | Append-only |
| `ADOPTION.md` | The staged adoption guide for an existing app: order, primitive-by-primitive mapping, migration traps, and the permanent "stays yours" list | Keep in sync with the public surface |
| `RELEASING.md` | How releases are cut and consumed pre-release | Keep in sync with reality |
| `REVIEW-GUIDE.md` | Orientation for a whole-codebase review: invariants by area, risk hotspots, settled decisions, coverage map | Keep in sync with reality |
| `../CHANGELOG.md` | Per-version release log (Breaking/Added/Changed/Fixed) | Append per release |
| `../TASKS.md` | OPEN backlog only — a done entry MOVES to `task-archive.md`, it is not checked off in place | Living |
| `task-archive.md` | The completed-task record (P5.5 · P6 · P7 · P1). Several entries carry warnings written for a future session — read before re-litigating a finished decision | Append on close |
| `../README.md` | The public front door + package table. **Ships inside every nupkg**; its `## Status` version headline is tool-synced (`dev.mjs pack`/`doctor --fix`) — never hand-edit that line | Keep in sync with reality |
| `../src/Shenora.React/README.md` | The npm package's own README (shipped to npmjs via `files`) | Keep in sync with the client API |
