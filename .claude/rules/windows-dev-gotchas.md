# Windows dev gotchas (this machine)

- **NEVER roundtrip source files through PowerShell 5 `Get-Content`/`Set-Content`** — it mangles
  UTF-8 (mojibake incident in the family; restored via git). Use the Edit/Write tools or Node
  scripts for text.
- **PS 5.1 collapses `@(@(a,b))` when there is exactly ONE nested pair** — `$pair` becomes a STRING, so
  `.Replace($pair[0],$pair[1])` is `Replace('E','v')`. Rewrote every capital E in a tracked doc
  (2026-08-05); the same script's five-pair target stayed nested and was correct, so more than one case
  hides it. **Bulk text edits go through the Edit tool**; if scripted, `git diff --numstat` before moving
  on — the tell is a diff far larger than the edit.
- 🔴 **`node -e "…"` in bash EATS every backtick.** A JS template literal, or any `` `code span` `` in a
  string being written to a doc, is command-substituted by the shell and vanishes — silently, reading as
  prose that merely lost a word. **Write the file with the Edit/Write tool**; if it must be scripted, put
  it in a `devtools/_*.mjs` FILE (a quoted heredoc, `<<'EOF'`, is safe) and delete it after.
  ⚠ **`git diff --numstat` is the SECOND line of defence, not the first** — by the time it shows, the
  damage is written, and on a large diff the anomaly is easy to miss.
- **Undo a sabotage with the SAME tool that applied it (Edit/Write), then confirm GREEN.** Both restore
  shortcuts lie: `Move-Item`/`Copy-Item` preserve `LastWriteTime`, so an incremental build silently keeps
  running the sabotaged assembly (a stale PASS — the dangerous direction), and `git checkout -- <path>`
  discards uncommitted edits the file already carried. `dotnet build -t:Rebuild` if unsure.
- **NEVER kill a SHARED runtime by name** — `Stop-Process -Name msedgewebview2` also kills Teams',
  Outlook's and Widgets'. Killed 38 live 2026-08-02 to clear one sample. Kill the app by its own name
  and let it take its children.
- **Node 24 `fs.cpSync` crashes on this box** (fail-fast 0xC0000409, silent). Use manual
  recursive copy loops in .mjs scripts.
- **`process.exit()` with a `fetch` in flight aborts Node** (libuv `UV_HANDLE_CLOSING` assertion) and
  the abort REPLACES the exit code, so a script that meant to fail reports SUCCESS. Set
  `process.exitCode` instead.
- PS 5.1 quirks in scripts: no `&&`/`||` chains; `-Encoding utf8` writes a BOM (fine for
  PowerShell, poison for JSONL/BOM-sensitive consumers). BOM-less UTF-8 C# sources on this
  CJK-locale machine need `<CodePage>65001</CodePage>` or csc reads them as ANSI (set in
  `src/Directory.Build.props`). **The C++ half is `/utf-8`** — MSVC otherwise reads the same sources
  as codepage 936 and every file with an em-dash in a comment fails C4819, fatal under `/WX`. Set in
  `src/Shenora.Launcher/CMakeLists.txt`; hit on that library's first build.
- Git Bash path mangling: set `MSYS_NO_PATHCONV=1` when an argument like `/p:...` or a
  device-style path must reach a native tool untouched.
- **Never build this repo from the session scratchpad path** — it is deep enough that `aapt2` fails
  with `APT2098 …flat: error: failed to open file`, which reads as a corrupt Android resource and is
  really MAX_PATH. It cost a false "this commit is broken" verdict against a `git worktree` there.
  Build a worktree under `devtools/_*` (gitignored) instead. The tell that it is the PATH and not the
  code: check out a tree you KNOW is green and confirm it fails identically.
  ⚠ **A SUBAGENT worktree lands in `.claude/worktrees/agent-<id>/` and is deep enough that even DELETING
  it fails** — `git worktree remove` reports *"Filename too long"* and leaves the tree behind, still
  registered. Clear it with PowerShell and the long-path prefix:
  `Remove-Item -LiteralPath "\\?\<abs-path>" -Recurse -Force`, then `git worktree prune`.
- 🔴 **`> nul` / `2>nul` in Git Bash CREATES A FILE named `nul`** — it is cmd's null device, not the
  shell's, so the redirect silently lands in the repo. Use `/dev/null`. **Committing one is worse than
  the mess it looks like**: a Windows reserved name breaks checkout for every future clone.
  ⚠ **Both the obvious cleanup AND its verification lie.** `Remove-Item -LiteralPath "\\?\…\nul"`
  reports success and deletes NOTHING, and `Test-Path` on a reserved name answers false whether or not
  the file is there — so the delete looks confirmed. What works is
  `[System.IO.File]::Delete("\\?\<abs-path>")`, **verified from bash** (`ls ./nul`), which is the only
  side that can see it.
