# docs/ — the project memory map

The router: match your task below, read that doc (and the matched rules — scan
`.claude/rules/RULES_INDEX.md`'s *Applies when* column). When a doc is added or a system changes,
update the relevant entry HERE — this file is the durable index.

> **Retired in the 0.2.0 cleanup** (git history has them): `BRIEF.md` (the originating brief — its
> API sketches were superseded, see D11), `2026-07-30-shenora-relayering-design.md` (→ D19/D20 +
> `ARCHITECTURE.md`), `2026-07-31-shenora-oneway-ipc-design.md` (→ D23). A pre-implementation design
> doc earns its keep until the thing is built; after that it competes with `ARCHITECTURE.md` for
> "what is true now" and loses. **`DECISIONS.md` is the permanent home for a WHY** — cite a `D<n>`
> from code, not a dated doc's `§`, or the doc can never be retired (that coupling is what kept these
> three alive).
>
> **Retired 2026-08-02, same rule, same day they were built:**
> `2026-08-02-shenora-mission-queue-and-chains-design.md` (→ **D28**, **D29**) and
> `2026-08-02-shenora-file-updates-design.md` (→ **D30**, **D31**). Both were plans, both were built
> within hours, and code/tests now cite the `D<n>` rather than the path — which is what made retiring
> them free. The mission-scheduling design was KEPT, deliberately: it carries the harvest evidence
> (§0) and the amendment history (A1–A3) that no other file holds.

## Read this when…

| Task | Read |
|---|---|
| Getting oriented / new session | This file, then `docs/ARCHITECTURE.md` + `local/PROJECT_NOTES.md` (private status) |
| Understanding what Shenora is and why | `CLAUDE.md`'s opening (the identity + the thesis that decides what is worth building) + `docs/DECISIONS.md` **D53**–**D56** |
| Package layering / where a contract belongs / mobile-shareable logic | `docs/DECISIONS.md` D19+D20 (one Windows shell layer, portable contracts in Core) + `docs/ARCHITECTURE.md` for the as-built graph |
| Sending IPC without awaiting / long-running work / correlating streamed results | `docs/DECISIONS.md` D23 (why the event pipe is the default, not request/response) |
| Changing the module contract / tracking a long operation / hosting on a non-WinForms base | `docs/DECISIONS.md` **D23** (`IModuleContext`, the operation registry, `NotificationPump`, the lifecycle bands) + `.claude/knowledge/ipc-contracts.md` |
| "Why is it done this way?" | `docs/DECISIONS.md` (numbered rationale — don't relitigate, amend) |
| Picking the next piece of work | `TASKS.md` (root — OPEN only, in the owner's work order) |
| Why a FINISHED decision was made that way | `docs/DECISIONS.md` — and if it is not there, `git log`. There is no closed-backlog file (deleted 2026-08-07) |
| What shipped already / verifying history | `CHANGELOG.md`, then `git log` |
| Reviewing the codebase (full/whole-tree review) | `docs/REVIEW-GUIDE.md` (orientation: invariants by area, risk hotspots, settled decisions, coverage map) |
| Extracting code from a sibling app | `.claude/knowledge/extraction-sources.md` (tracked, de-identified) + `local/EXTRACTION-MAP.md` (private, named) |
| Keeping the library generic | `.claude/knowledge/generic-library.md` |
| When did this break? | `git log -S "<distinctive token>" -- <path>` — there is no fix log; commit messages carry root causes |
| Adopting Shenora into an existing desktop app | `docs/ADOPTION.md` (stage order, what replaces what, what stays the app's own) |
| Running the same app logic on MOBILE (a MAUI shell) | `docs/ADOPTION.md` Stage 5 (what transfers, what does not, and the traps already paid for) + `docs/DECISIONS.md` **D32**–**D34** (a second shell is a PEER; absent vs differently-satisfied capabilities; why its API baseline is weaker) + **D36** (the host advertises capabilities in the handshake, so ONE web bundle serves both shells) + **D39** (why the auxiliary-session stack does NOT port, even though both shells host a webview) |
| Replacing a hand-rolled file-operation planner, job queue or resource gate | `docs/DECISIONS.md` **D27**–**D31** (the one-scheduler-two-key-kinds claim) + **D57** (why a policy is safe to expose: it chooses among LEGAL moves) + the mission-scheduler section of `docs/ADOPTION.md` (adopter-facing mapping) |
| Serializing filesystem MUTATIONS, atomic replace, crash-atomicity, cross-process file locks | `docs/DECISIONS.md` **D30**+**D31** (why the file queue is separate from scheduling; why locking is two mechanisms) + **D48**+**D55** (the `Shenora.IO` namespace, inside `Shenora` — a package until 2026-08-07) + `docs/ARCHITECTURE.md` for the surface + the file-queue section of `docs/ADOPTION.md` |
| Multi-step missions, or where the pending queue lives | `docs/DECISIONS.md` **D28**+**D29** (a chain is ONE queue entry; the queue's store, and the pluggable async queue that was rejected) |
| Shipping app updates: a staged/two-phase updater, an update manifest, or a native launcher | `docs/DECISIONS.md` **D50** (the launcher is a library + a template; the topology that deletes a bug class) + **D57** (why two phases at all, and the two-sibling evidence) + **D56** (this is PRODUCT, not devtools) |
| Probing, planning or **playing a file the webview cannot decode** | `docs/DECISIONS.md` **D52** (what the translation layer IS and the scope test it gives) + **D51** (why no engine byte ever ships) + **D53** (why it lives in `Shenora` and not its own package), then `docs/ARCHITECTURE.md`'s `Shenora.Media` pipeline bullet for the as-built surface |
| Cutting or consuming a release | `docs/RELEASING.md` |
| Touching an invariant / gotcha | `.claude/rules/RULES_INDEX.md` — read the matched rule |
| Dev loop commands | `devtools/README.md` |
| Anything FINISHED — closed tasks, shipped phases, past fixes | **`git log`.** There is no archive tier (D9's 2026-08-07 amendment): finished work is deleted, not filed. A doc nobody reads and that grows fastest is an archive wearing another name |

## Where things live (fast map)

- `src/` — the packable projects, ONE shell per platform since 0.5.0 (D37):
  `Shenora|Ipc|Windows|Android|iOS` plus the optional `Media|IO|IO.Compression` hanging off Core
  (D48), with `Shenora.Mobile/` as the SOURCE (no csproj) compiled into
  both mobile packages — plus `Shenora.React/` (the `@shenora/react` npm package) and
  `Directory.Build.props` (the ONLY version source).
- `tests/Shenora.Tests` — the single test project (folders mirror src).
- `samples/` — the sample desktop + web app (Phase 2+; doubles as the e2e subject).
- `devtools/` — one-entry dev loop (`node devtools/dev.mjs <cmd>`); config in `project.config.mjs`.
- `local/` — gitignored private context (real paths, sibling names, session notes).
- `.github/workflows/release.yml` — the manual release pipeline.

## Doc inventory

| Doc | Holds | Nature |
|---|---|---|
| `DECISIONS.md` | Numbered load-bearing choices + why | Living, append/amend |
| `ARCHITECTURE.md` | The as-built map: projects, dependencies, public surface | Keep in sync with reality |
| `ADOPTION.md` | The staged adoption guide for an existing app: order, primitive-by-primitive mapping, migration traps, and the permanent "stays yours" list | Keep in sync with the public surface |
| `RELEASING.md` | How releases are cut and consumed pre-release | Keep in sync with reality |
| `REVIEW-GUIDE.md` | Orientation for a whole-codebase review: invariants by area, risk hotspots, settled decisions, coverage map | Keep in sync with reality |
| `../CHANGELOG.md` | Per-version release log (Breaking/Added/Changed/Fixed) | Append per release |
| `../TASKS.md` | OPEN backlog only — a done entry is DELETED, not checked off in place | Living |
| `../README.md` | The public front door + package table. **Ships inside every nupkg**; its `## Status` version headline is tool-synced (`dev.mjs pack`/`doctor --fix`) — never hand-edit that line | Keep in sync with reality |
| `../src/Shenora.React/README.md` | The npm package's own README (shipped to npmjs via `files`) | Keep in sync with the client API |
