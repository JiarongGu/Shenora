# Windows dev gotchas (this machine)

- **NEVER roundtrip source files through PowerShell 5 `Get-Content`/`Set-Content`** — it mangles
  UTF-8 (mojibake incident in the family; restored via git). Use the Edit/Write tools or Node
  scripts for text.
- **PS 5.1 collapses `@(@(a,b))` when there is exactly ONE nested pair** — `$pair` becomes a STRING, so
  `.Replace($pair[0],$pair[1])` is `Replace('E','v')`. Rewrote every capital E in a tracked doc
  (2026-08-05); the same script's five-pair target stayed nested and was correct, so more than one case
  hides it. **Bulk text edits go through the Edit tool**; if scripted, `git diff --numstat` before moving
  on — the tell is a diff far larger than the edit.
- 🔴 **`node -e "…"` in bash EATS every backtick — and it has now bitten FIVE times.** A JS template
  literal, or any `` `code span` `` inside a string being written to a doc, is command-substituted by the
  shell and vanishes. The damage is silent and reads as prose that merely lost a word: the 5th one wrote
  an adopter-facing CHANGELOG migration note with `NO_HANDLER`, `NO_ROUTE` and `@shenora/react` all
  deleted, plus two bullets run together. **Write the file with the Edit/Write tool.** If it must be
  scripted, put the script in a `devtools/_*.mjs` FILE (a quoted heredoc, `<<'EOF'`, is safe) and delete
  it after — never `node -e`.
  ⚠ **`git diff --numstat` is the SECOND line of defence, not the first.** It caught the 5th one, which
  is the only reason it did not ship — but by then the damage is written, and on a large diff the
  anomaly is easy to miss.
- **Undo a sabotage with the SAME tool that applied it (Edit/Write), then confirm GREEN.** Both
  restore shortcuts lie, and both were hit live in one session: `Move-Item`/`Copy-Item` preserve
  `LastWriteTime`, so the restored file can be older than the assembly built from the sabotage and
  the incremental build silently keeps running it (dangerous direction: a stale PASS); and
  `git checkout -- <path>` reverts to HEAD, discarding uncommitted edits the file already carried.
  A sabotage is only verified once both directions are — `dotnet build -t:Rebuild` if unsure.
- 🔴 **NEVER launch a WebView2 app under `timeout` — it manufactures a renderer CRASH.** GNU `timeout`
  puts the child in its OWN process group, and Chromium's renderer sandbox breaks inside one: the
  renderer dies with `0xC0000005` about 8 s in — **before any kill** — then the Storage Service, Network
  Service and GPU process follow, and the host's auto-reload makes the probes run twice. Measured
  2026-08-08: **0/9 crashes launching directly, 0/3 with `timeout --foreground`, 6/12 with plain
  `timeout`.** It is ~50 % per run, which is what made it survive ~12 single-run A/B eliminations
  (`phase-workflow.md` has the method lesson). It is also the likeliest source of the reported
  `0x800700AA` ("the requested resource is in use"). **To bound a sample run, spawn it from a
  `devtools/_*.mjs` script and `p.kill()` on a timer** — or pass `--foreground` if `timeout` is
  unavoidable. The redirect is innocent: `> file` alone never crashed it.
- **NEVER kill a SHARED runtime by name** — `Stop-Process -Name msedgewebview2` also kills Teams',
  Outlook's and Widgets'. Killed 38 live 2026-08-02 to clear one sample. Kill the app by its own name
  and let it take its children.
- 🔴 **AN UNRESPONSIVE EMULATOR HANGS THE ANDROID *APP* BUILD FOR EVER — `adb kill-server` fixes it in
  seconds.** The MAUI app build runs `GetPrimaryCpuAbi`, which does `adb shell getprop` against every device
  adb lists, with **no timeout**. A wedged emulator still listed as `device` never answers, so the build sits
  at 0 % CPU indefinitely. Measured 2026-08-13: **28 min, then 19, then 9 — and 11 SECONDS after
  `adb kill-server`.** `dev.mjs verify` builds the solution, so this takes the whole release gate down with
  it (it passed in 1 m 53 s immediately afterwards).
  - **The tell that it is THIS and not a slow machine:** `src/Shenora.Android` (the LIBRARY) builds in ~7 s
    beside the hang, because only an APP queries the device. Sample the build process's CPU — a 0 s delta
    over 3 s means blocked, not slow — then `dotnet build … -v diag` and read the last `Task "…"` line.
  - ⚠ **Three wrong causes were published before that one command was run** (the machine, a missing
    `-p:RuntimeIdentifier`, a "runaway" emulator burning CPU — its delta was zero on a 22-core box). Reach
    for the instrument, not the theory.
  - ⚠ A qemu in this state is **unkillable**: `Stop-Process -Force` and `taskkill /F /T` both refuse while
    `tasklist` still lists it. You do NOT need to kill it to build — clearing adb is enough — but you do to
    DEPLOY, and that needs a reboot.
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
- **WebView2 + CDP:** setting `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` makes
  WebView2 IGNORE the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` env var — a dev-mode host must
  re-append the env var's value itself or the devtools CDP loop silently gets no port. (Proven in
  two sibling apps; keep the fix in the browser-arguments builder.)
- Desktop verification without CDP: `dev.mjs input list` / `click|rclick|move|drag <fx> <fy>…` post
  background mouse messages to the WebView2 render surface (no focus steal, works occluded);
  `shot`/`wgc` capture the window (WGC works even when hidden). Target process comes from
  `devtools/project.config.mjs` (`processName` → `DEVTOOL_PROC`).
- **Tests creating WinForms handles with `AllowDrop = true` (or any OLE feature) must run on a
  dedicated STA thread** — xunit workers are MTA, and the failure is NOT a clean test failure:
  handle creation throws inside WndProc and WinForms pops a BLOCKING unhandled-exception dialog
  that stalls the whole suite (found live; wrap the body in `TestSupport/Sta.Run`, as
  `DropZoneManagerTests` does).
