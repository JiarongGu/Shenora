---
name: sensitive-guard
applies_when: writing a tracked file or a commit message here, the sensitive guard blocked a commit, a commit is made from a worktree or CI, or history is about to be scanned or rewritten
enforces: the shared sensitive-info rule, through this repository's guard — installed per clone, fed by the gitignored local/sensitive-patterns.txt, failing closed without it
---

# The sensitive-info guard — how this repository enforces the shared rule

**The principle is the shared `sensitive-info` rule (in `AGENTS.md`); repairing a committed leak is the
shared `leak-repair` knowledge. This is the mechanism here: `devtools/scripts/check-sensitive.mjs`, run
from git hooks you must install, fed by patterns that live only in `local/`.**

## Why

This repository is public (GitHub, nuget.org, npm). The rule was earned in the family by a leak that
needed a full `git filter-repo` rewrite — a clean working tree is NOT enough.

## What may and may not be named

- **No absolute local paths** — a real project root, a user home (`C:\Users\<name>\…`), a mapped-drive
  root. Generic examples like `C:\MyApp\…` in docs are fine.
- **No private sibling-project names.** The private desktop apps Shenora is extracted from are named by
  role ("the primary desktop sibling", "the consuming app"). Lyntai and Sonora are public repositories by
  the same author and MAY be named (`docs/DECISIONS.md` D12). The real name→path map lives only in
  `local/CLAUDE.local.md` / `local/EXTRACTION-MAP.md`.
- **No personal or network specifics** — real LAN IPs, machine names, the user's name or email in file
  *content* (authorship metadata in git, `LICENSE`, `Authors` and GitHub URLs is fine).
- **Working files stay in this repo** — `devtools/` for temp and probes (`_*` gitignored), `local/` for
  private. Never a sibling or backup folder elsewhere.

## How to apply

- **Install the guard: `node devtools/dev.mjs install-hooks`, once per clone.** A fresh clone has no
  hooks, so the guard silently does nothing (hit live — two commits nearly landed unguarded).
  `check-sensitive.mjs` then scans staged content AND paths on pre-commit, plus the commit MESSAGE on
  commit-msg.
- **It fails CLOSED when `local/sensitive-patterns.txt` is missing.** The real tokens live in that
  gitignored file (add new ones there — never in a tracked file); the tracked scanner carries only
  generic path shapes. In CI, a fresh clone or a git worktree, `local/` cannot exist: pass
  `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` deliberately — and then the brand/sibling-name half is NOT
  running, so check names by hand. Never copy `local/` across checkouts.
- Whole tree: `node devtools/dev.mjs check-sensitive --tree`. Bypass (rare): `git commit --no-verify`.
- If the guard blocks you: move the value to `local/` and reference it generically in the tracked file.
- **History: `dev.mjs check-sensitive --history`** scans every reachable blob, every path those blobs
  ever had, and every commit message. It is a **ONE-OFF AUDIT** (before going public, after a scrub) —
  never in `verify` or the hook, whose cost would grow with the history. **The pre-commit hook is the
  ongoing protection**: it catches a leak while it is still staged, the only point where the fix is free.
- Fix a real hit with a scoped `git filter-repo` pass (backup bundle, dry-run on a `--mirror` clone,
  verify `git grep <token> $(git rev-list --all)` = 0 AND messages, then apply).
  ⚠ **A clean history scan deserves the same suspicion as a passing test** — plant a pattern you KNOW is
  in history, confirm hits from both a blob and a `commit-message`, remove it. Done that way when the mode
  was added (929 blobs + all messages).

## Gotchas / traps

- **Abbreviations slip through.** A name scrubbed in full survives as a shorthand — sweep for the short
  forms too and add each to `local/sensitive-patterns.txt` so the guard catches it.
- **Commit messages are history too.** `--replace-text` scrubs blobs; you also need `--replace-message`
  plus `reflog expire --expire=now --all` + `gc --prune=now`.
- **`git push --all` can leak local-only branches** — push `main` only.
