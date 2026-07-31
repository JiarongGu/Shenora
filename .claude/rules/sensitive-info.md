# Sensitive info — keep dev-machine + private data out of tracked files

This repo will be **public** (GitHub + NuGet/npm). Anything that identifies the author's machine
or their private projects must never land in a tracked file, a commit message, or git history.
Private context lives in the gitignored `local/` (`local/CLAUDE.local.md` = dev instructions +
real paths; `local/PROJECT_NOTES.md` = status). This rule was earned in the family by a leak that
needed a full `git filter-repo` rewrite — the working tree being clean is NOT enough.

## The rules (never in a tracked file or commit message)

- **No absolute local paths.** A real project root, a user home (`C:\Users\<name>\…`), or
  mapped-drive roots. Use a repo-relative path or a neutral placeholder. (Generic examples like
  `C:\MyApp\…` in docs are fine.)
- **No private sibling-project names.** The three private desktop apps Shenora is extracted from
  are referred to generically ("the primary desktop sibling", "the consuming app"). Lyntai and
  Sonora are public repos by the same author and MAY be named (docs/DECISIONS.md D12). The real
  name→path map lives only in `local/CLAUDE.local.md` / `local/EXTRACTION-MAP.md`.
- **No personal / network specifics.** Real LAN IPs, machine names, the user's name/email in file
  *content* (authorship metadata in git/LICENSE/`Authors`/GitHub URLs is fine).
- **Working files stay in this repo** — `devtools/` for temp/probes (`_*` gitignored), `local/`
  for private. Never create a sibling/backup folder elsewhere.

## How to apply

- **An automated guard enforces this — but only if you INSTALL IT.** `node devtools/dev.mjs
  install-hooks` once per clone; a fresh clone has no hooks, so the guard silently does nothing (hit
  live — two commits nearly landed unguarded). `devtools/scripts/check-sensitive.mjs` then scans
  staged content AND paths on pre-commit, plus the commit MESSAGE (history too) on commit-msg, and
  fails CLOSED when `local/sensitive-patterns.txt` is missing — CI must pass
  `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` deliberately. The real tokens live in that gitignored file
  (add new ones there — never in a tracked file); the tracked scanner carries only generic path
  shapes. Whole tree: `node devtools/dev.mjs check-sensitive --tree`. Bypass (rare):
  `git commit --no-verify`.
- If the guard blocks you: move the value to `local/` and reference it generically in the tracked file.
- **A leak already committed is a history problem, not a working-tree problem.** Editing the
  current file leaves it in every past commit + message.
  **CHECK IT, don't assume it:** `node devtools/dev.mjs check-sensitive --history` scans every blob
  reachable from every ref, every PATH those blobs ever had, and every commit MESSAGE. That mode
  exists because this rule demanded a history check for five phases while the scanner only offered
  `--tree`, which reads the CURRENT checkout — so the one question the rule cares most about was the
  one it could not answer. Run it before making a repo public, and after any scrub.
  If it finds something, fix with a scoped `git filter-repo` pass (backup bundle first, dry-run on a
  `--mirror` clone, verify `git grep <token> $(git rev-list --all)` = 0 AND messages, then apply).
  ⚠ **A history scan that reports clean deserves the same suspicion as a test that passes.** Prove
  the pipeline is live before believing it: append a pattern matching a string you KNOW is in
  history, confirm it reports hits from both a blob and a `commit-message`, then remove it. Verified
  that way when the mode was added (2026-07-31): 929 blobs + all messages, clean on the real
  patterns, and correctly noisy on planted ones.

## Gotchas / traps

- **Abbreviations slip through.** A name scrubbed in full survives as a shorthand — sweep for the
  short forms too and add each to `local/sensitive-patterns.txt` so the guard catches it.
- **Commit messages are history too.** `--replace-text` scrubs blobs; you also need
  `--replace-message` plus `reflog expire --expire=now --all` + `gc --prune=now`.
- **`git push --all` can leak local-only branches** — push `main` only.
