# Phase workflow — build, verify, review, commit

The project moves in phases (`docs/ROADMAP.md`: P1 skeleton → P2 core host → P3 IPC → P4 modules
+ native services → P5 auxiliary browser sessions → **P5.5 consolidation/re-layer (CURRENT)** →
P6 sibling adoption → P7 stabilisation/1.0). Per phase:

1. **Build** through the dev loop: `node devtools/dev.mjs <build|test|verify|pack|doctor|sample|…>`
   — don't invent ad-hoc shell for these steps.
2. **Verify with evidence.** `node devtools/dev.mjs verify` is the "am I done?" gate (dotnet
   build + tests, npm build + tests, sensitive scan, knowledge check). Behavioral claims about the
   desktop shell are proven against the sample app (`dev.mjs sample` + the capture/input tools),
   not asserted. Performance claims need numbers.
   **The gate has known holes until P5.5 H5 lands** (`TASKS.md`) — and these are exactly how the
   P0–P5 latent defects passed five phase reviews: `verify` does NOT compile `samples/` (the
   solution's `/samples/` folder is empty, so the reference composition and e2e subject can be red
   while verify is green), `dev.mjs test <typo>` exits 0 having run nothing, and warnings are
   neither errors nor shown (`-clp:ErrorsOnly`). Compile the sample by hand before claiming done.
3. **Phase review (standing user rule):** run `/phase-review` — a review subagent over the full
   phase diff → fix its real findings (delegating the fix batch to a subagent works well) → sync
   docs (`ARCHITECTURE`/`ROADMAP`/`TASKS`/`CHANGELOG`) and rules/skills → then commit.
4. **Commit only on explicit user approval.** One commit per phase (+ review-fix commits) — OR one
   per fix batch when the phase's plan deliberately sequences them, as P5.5 does (`TASKS.md`
   `EXECUTION ORDER`: security fixes and gate holes first, the re-layer as its own commit, then the
   dedup on top). Never fold a security fix into a structural refactor to honour "one commit".

Library discipline: every public-surface change is deliberate (it becomes SemVer surface at 1.0 —
note breaking changes in `CHANGELOG.md` under `### Breaking`). Extraction ports keep the source's
post-mortem comments and fix the known gaps (`.claude/knowledge/extraction-sources.md`).

Working files: everything temporary goes under `devtools/` (`devtools/_*` is gitignored) — never
the system temp. **Public repo:** no private sibling names, no absolute local paths, no personal
data in tracked files or commit messages; private context lives in `local/`.
