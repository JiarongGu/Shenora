# devtools — the dev loop, the gates, and the desktop and device drivers

## What this folder is

`dev.mjs` is the one entry point (`node devtools/dev.mjs` prints every verb); `scripts/` holds the gates
and prose scanners it runs; `win-input/`, `wgc-shot/` and `ui-responsiveness/` are native tools that drive
and capture the desktop sample; `hooks/` holds the git hooks `install-hooks` copies in. Repository paths
and names live in `project.config.mjs`. The verbs and their traps are in `devtools/README.md`.

## Conventions here

- `hygieneSteps()` in `dev.mjs` is ONE list with two callers, `verify` and `checks` — add a gate there,
  never to one caller.
- Set `process.exitCode`, never `process.exit()` with a `fetch` in flight (the abort replaces the exit
  code, so a failing script reports success). No `fs.cpSync` — it crashes Node 24 on this machine; copy
  with a manual loop (`windows-dev-gotchas`).
- A repo-wide sweep skips `local/` (and `CHANGELOG.md` where it reads prose): both are history by
  definition (`working-state-homes`).
- Correctness stops a release; style warns. A size or shape budget never fails (`work-loop`).
- `_*` here is gitignored scratch: probes, logs, a worktree to build from (not the session scratchpad,
  whose depth breaks `aapt2` on MAX_PATH).

## Traps

- A gate, scanner or tripwire is sabotage-verified in BOTH directions, and tested where it must stay
  QUIET, in the environment it runs in — three gates broke one release by being proven only on the path
  they should catch (`work-loop`).
- Never run a sabotage while a gate runs in the background: `verify` reads the working tree, so the
  sabotage becomes its subject.
- `check-sensitive` fails CLOSED without `local/sensitive-patterns.txt`; in CI or a worktree pass
  `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` (`sensitive-guard`).

## Checks

- `node devtools/dev.mjs checks` after a change here; `node devtools/dev.mjs verify` before calling it done.
