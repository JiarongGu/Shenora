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
| "Why is it done this way?" | `docs/DECISIONS.md` (numbered rationale — don't relitigate, amend) |
| Picking the next piece of work | `TASKS.md` (root — pending only), `docs/ROADMAP.md` `## Remaining` |
| What shipped already / verifying history | `docs/ROADMAP.md` `## Done`, `CHANGELOG.md` |
| Reviewing the codebase (full/whole-tree review) | `docs/REVIEW-GUIDE.md` (orientation: invariants by area, risk hotspots, settled decisions, coverage map) |
| Extracting code from a sibling app | `.claude/knowledge/extraction-sources.md` (tracked, de-identified) + `local/EXTRACTION-MAP.md` (private, named) |
| Keeping the library generic | `.claude/knowledge/generic-library.md` |
| When did this break? | `docs/FIX-LOG.md` (use `/fix-log` to append) |
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
| `DECISIONS.md` | Numbered load-bearing choices + why | Living, append/amend |
| `ARCHITECTURE.md` | The as-built map: projects, dependencies, public surface | Keep in sync with reality |
| `ROADMAP.md` | Done (narrative, newest first) + Remaining (by phase) | Living |
| `FIX-LOG.md` | Notable fixes: symptom / root cause / fix / verify / commit | Append-only |
| `RELEASING.md` | How releases are cut and consumed pre-release | Keep in sync with reality |
| `REVIEW-GUIDE.md` | Orientation for a whole-codebase review: invariants by area, risk hotspots, settled decisions, coverage map | Keep in sync with reality |
| `../CHANGELOG.md` | Per-version release log (Breaking/Added/Changed/Fixed) | Append per release |
| `../TASKS.md` | Pending backlog only (done ⇒ ROADMAP, removed here) | Living |
| `../README.md` | The public front door + package table. **Ships inside every nupkg**; its `## Status` version headline is tool-synced (`dev.mjs pack`/`doctor --fix`) — never hand-edit that line | Keep in sync with reality |
| `../src/Shenora.React/README.md` | The npm package's own README (shipped to npmjs via `files`) | Keep in sync with the client API |
