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

- **An automated pre-commit guard enforces this** — `devtools/scripts/check-sensitive.mjs` (run by
  `devtools/hooks/pre-commit`) scans staged changes and blocks the commit on any dev path or
  private name. Install once per clone: `node devtools/dev.mjs install-hooks`. The real tokens
  live in the gitignored `local/sensitive-patterns.txt` (add new ones there — never in a tracked
  file); the tracked scanner carries only generic path shapes. Scan the whole tree any time with
  `node devtools/dev.mjs check-sensitive --tree`. Bypass deliberately (rare) with
  `git commit --no-verify`.
- If the guard blocks you: move the value to `local/` and reference it generically in the tracked file.
- **A leak already committed is a history problem, not a working-tree problem.** Editing the
  current file leaves it in every past commit + message. Fix with a scoped `git filter-repo` pass
  (backup bundle first, dry-run on a `--mirror` clone, verify
  `git grep <token> $(git rev-list --all)` = 0 AND messages, then apply).

## Gotchas / traps

- **Abbreviations slip through.** A name scrubbed in full survives as a shorthand — sweep for the
  short forms too and add each to `local/sensitive-patterns.txt` so the guard catches it.
- **Commit messages are history too.** `--replace-text` scrubs blobs; you also need
  `--replace-message` plus `reflog expire --expire=now --all` + `gc --prune=now`.
- **`git push --all` can leak local-only branches** — push `main` only.
