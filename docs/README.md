# docs/ — the index

**Two audiences, and the split is the point.** Using the library is a different job from maintaining it.
A migration story ordered end to end serves the maintainer and leaves someone who wants ONE capability
with no way in — hence the guides, and `ADOPTION.md` for the whole adoption.

## Using Shenora

| You want | Read |
|---|---|
| To start a NEW app | **[getting-started.md](getting-started.md)** — packages, a window, a typed IPC round trip, onto a device |
| One capability, on its own | **[guides/](guides/)** — [missions](guides/missions.md) · [file updates](guides/file-updates.md) · [media](guides/media.md) · [mobile](guides/mobile.md) |
| To move an EXISTING desktop app across | **[ADOPTION.md](ADOPTION.md)** — staged, so your app ships at every step |
| The package table and per-package basics | [the root README](../README.md) |
| The strings your PAGE types — routes, events, error codes, capabilities | **[reference/wire.md](reference/wire.md)** — generated from the source constants |
| The device loop (`shenora ios deploy`, `android deploy`) | [`@shenora/cli`'s README](../src/Shenora.Cli/README.md) |
| Why any of it is the way it is | [DECISIONS.md](DECISIONS.md) — numbered, cited from shipped XML docs |

🔴 **A guide says HOW; `DECISIONS.md` says WHY, and a guide LINKS it rather than restating it.** That is
the rule D57 exists to keep — five design docs were retired precisely because a third copy of the
reasoning goes stale while nobody notices, and a per-feature guide is the ideal place for that to happen
again.

---

## Maintaining Shenora

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
> them free.
>
> ⚠ **This ended "the mission-scheduling design was KEPT, deliberately" — and it is GONE.** D57 retired
> all five design docs on 2026-08-07 and `docs/archive/` went with them, so the one exception this note
> carved out no longer exists. Its harvest evidence and amendment history are in `git log`; the reasoning
> it held is D27–D31. **An exception outlives the rule that granted it** — the sweep that retired the
> others had no reason to look at the sentence protecting this one.

## Read this when…

| Task | Read |
|---|---|
| Getting oriented / new session | This file, then `docs/ARCHITECTURE.md` + `local/PROJECT_NOTES.md` (private status) |
| Understanding what Shenora is and why | `CLAUDE.md`'s opening (the identity + the thesis that decides what is worth building) + `docs/DECISIONS.md` **D53**–**D56** |
| Package layering / where a contract belongs / mobile-shareable logic | `docs/DECISIONS.md` D19+D20 (one Windows shell layer, portable contracts in Core) + `docs/ARCHITECTURE.md` for the as-built graph |
| Sending IPC without awaiting / long-running work / correlating streamed results | `docs/DECISIONS.md` D23 (why the event pipe is the default, not request/response) |
| Changing the module contract / tracking a long operation / hosting on a non-WinForms base | `docs/DECISIONS.md` **D23** + **D66** (`IModuleContext`, request tracking, `NotificationPump`; D66 merged the old operations cluster into `IpcRequest`) + `.claude/knowledge/ipc-contracts.md` |
| "Why is it done this way?" | `docs/DECISIONS.md` (numbered rationale — don't relitigate, amend) |
| **About to CHANGE a subsystem — how does it work as built?** | **`docs/design/<feature>.md`** (D77) — the stages, the seams, what each promises. Maintainer-facing, written FROM the code. ⚠ It states the design and LINKS a `D<n>` for anything that needs defending; the moment it argues instead of describes it becomes the stale third copy D57 deleted five of. Today: [ipc](design/ipc.md) · [media](design/media.md) · [missions-and-files](design/missions-and-files.md) · [mobile-shells](design/mobile-shells.md) · [shells](design/shells.md) · [update](design/update.md) |
| What did a DEVICE actually do — a codec table, a range pattern, a background window? | [`docs/design/mobile-shells.md`](design/mobile-shells.md) — every figure cost a device run. ⚠ True of the device and DATE in its heading, never a promise the platform makes |
| Picking the next piece of work | `TASKS.md` (root — OPEN only, in the owner's work order) |
| **About to propose something the kit deliberately does NOT do** | `docs/DECISIONS.md` → "Anti-goals" (moved out of `TASKS.md` 2026-08-13 — a list of what is NOT being built read as a backlog while it lived there) |
| Looking for maintenance work, or filing a RECURRING pass | `.claude/knowledge/standing-habits.md` — habits to re-run, never backlog entries, because an entry that can never be deleted stops the length tracking the work |
| Why a FINISHED decision was made that way | `docs/DECISIONS.md` — and if it is not there, `git log`. There is no closed-backlog file (deleted 2026-08-07) |
| What shipped already / verifying history | `CHANGELOG.md`, then `git log` |
| Reviewing the codebase (full/whole-tree review) | `docs/REVIEW-GUIDE.md` (orientation: invariants by area, risk hotspots, settled decisions, coverage map) |
| Extracting code from a sibling app | `.claude/knowledge/extraction-sources.md` (tracked, de-identified) + `local/EXTRACTION-MAP.md` (private, named) |
| Keeping the library generic | `.claude/knowledge/generic-library.md` |
| When did this break? | `git log -S "<distinctive token>" -- <path>` — there is no fix log; commit messages carry root causes |
| Adopting Shenora into an existing desktop app | `docs/ADOPTION.md` (stage order, what replaces what, what stays the app's own) |
| Changing the DESKTOP shell — the run sequence, the window, native services, WebView2 hosting | [`docs/design/shells.md`](design/shells.md) — the order the runner depends on, the STA rule, the DPI rule, and what is deliberately absent. INVARIANTS: `.claude/knowledge/winforms-shell.md` + `.claude/knowledge/webview2-hosting.md`. WHY: `docs/DECISIONS.md` **D19**+**D20** (the layer and its direction) + **D37** (one shell package per platform) |
| Running the same app logic on MOBILE (a MAUI shell) | `docs/guides/mobile.md` (what transfers, what does not, and the traps already paid for) + `docs/DECISIONS.md` **D32**–**D34** (a second shell is a PEER; absent vs differently-satisfied capabilities; why its API baseline is weaker) + **D36** (the host advertises capabilities in the handshake, so ONE web bundle serves both shells) + **D39** (why the auxiliary-session stack does NOT port, even though both shells host a webview) |
| Replacing a hand-rolled file-operation planner, job queue or resource gate | `docs/DECISIONS.md` **D27**–**D31** (the one-scheduler-two-key-kinds claim) + **D57** (why a policy is safe to expose: it chooses among LEGAL moves) + `docs/guides/missions.md` (adopter-facing mapping) |
| Serializing filesystem MUTATIONS, atomic replace, crash-atomicity, cross-process file locks | `docs/DECISIONS.md` **D30**+**D31** (why the file queue is separate from scheduling; why locking is two mechanisms) + **D48**+**D55**+**D65** (the layering, and why it is the `Shenora.Engine.Files` namespace inside `Shenora` rather than a package) + `docs/guides/file-updates.md` |
| Multi-step missions, or where the pending queue lives | `docs/DECISIONS.md` **D28**+**D29** (a chain is ONE queue entry; the queue's store, and the pluggable async queue that was rejected) |
| Shipping app updates: a staged/two-phase updater, an update manifest, or a native launcher | [`docs/design/update.md`](design/update.md) — the staging pipeline, the on-disk contract, the four things a commit verifies, and the path rule the manifest must pass. WHY: `docs/DECISIONS.md` **D50** (the launcher is a library + a template; the topology that deletes a bug class) + **D57** (why two phases at all, and the two-sibling evidence) + **D56** (this is PRODUCT, not devtools) |
| Probing, planning or **playing a file the webview cannot decode** | `docs/DECISIONS.md` **D52** (what the translation layer IS and the scope test it gives) + **D51** (why no engine byte ever ships) + **D53** (why it lives in `Shenora` and not its own package), then `docs/guides/media.md` for the adopter-facing HOW and `docs/ARCHITECTURE.md`'s `src/` tree for where each pipeline stage lives |
| Cutting or consuming a release | `docs/RELEASING.md` |
| Touching an invariant / gotcha | `.claude/rules/RULES_INDEX.md` — read the matched rule |
| Dev loop commands | `devtools/README.md` |
| Anything FINISHED — closed tasks, shipped phases, past fixes | **`git log`.** There is no archive tier (D9's 2026-08-07 amendment): finished work is deleted, not filed. A doc nobody reads and that grows fastest is an archive wearing another name |

## Where things live (fast map)

- `src/` — the packable projects, ONE shell per platform since 0.5.0 (D37). ⚠ **The authoritative set is
  the table at the top of `DECISIONS.md` and nowhere else** — a second copy of it here is how a folded
  feature tier goes on being listed. Alongside them: `Shenora.Mobile/` as the SOURCE (no csproj) compiled into both mobile packages,
  `Shenora.React/` and `Shenora.Cli/` (the two npm packages), and `Directory.Build.props` (the ONLY
  version source).
- `tests/Shenora.Tests` — the single test project (folders mirror src).
- `samples/` — four: `Sample.Logic` (portable `net10.0`, the D20 tripwire), `Sample.Desktop`,
  `Sample.Web` and `Sample.Maui`. They double as the e2e subject on all three shells.
- `devtools/` — one-entry dev loop (`node devtools/dev.mjs <cmd>`); config in `project.config.mjs`.
- `local/` — gitignored private context (real paths, sibling names, session notes).
- `.github/workflows/release.yml` — the manual release pipeline.

## Doc inventory

| Doc | Holds | Nature |
|---|---|---|
| `DECISIONS.md` | Numbered load-bearing choices + why | Living, append/amend |
| `ARCHITECTURE.md` | The as-built map: projects, subsystem kinds, dependency rules. **Not the public surface** — that is the API baselines and the XML docs | Keep in sync with reality |
| `getting-started.md` | The GREENFIELD path: packages, a window, a typed IPC round trip, onto a device. Every snippet is lifted from a sample the gate compiles | Keep in sync with the samples |
| `reference/wire.md` | GENERATED from the source constants (`dev.mjs verify` fails when it drifts): the module names, route types, event types, error codes and capability names a PAGE types by hand | Never edit — regenerate |
| `guides/` | One page per capability, moved VERBATIM out of `ADOPTION.md` (they said "not a stage" while living inside a staged migration). Says HOW; links `DECISIONS.md` for WHY | Keep in sync with the public surface |
| `ADOPTION.md` | The staged adoption guide for an existing app: order, primitive-by-primitive mapping, migration traps, and the permanent "stays yours" list | Keep in sync with the public surface |
| `RELEASING.md` | How releases are cut and consumed pre-release | Keep in sync with reality |
| `REVIEW-GUIDE.md` | Orientation for a whole-codebase review: invariants by area, risk hotspots, settled decisions, coverage map | Keep in sync with reality |
| `../CHANGELOG.md` | Per-version release log (Breaking/Added/Changed/Fixed) | Append per release |
| `../TASKS.md` | OPEN backlog only — a done entry is DELETED, not checked off in place | Living |
| `../README.md` | The public front door + package table. **Ships inside every nupkg**; its `## Status` version headline is tool-synced (`dev.mjs pack`/`doctor --fix`) — never hand-edit that line | Keep in sync with reality |
| `../src/Shenora.React/README.md` | The npm package's own README (shipped to npmjs via `files`) | Keep in sync with the client API |
