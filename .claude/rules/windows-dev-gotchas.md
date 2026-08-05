# Windows dev gotchas (this machine)

- **NEVER roundtrip source files through PowerShell 5 `Get-Content`/`Set-Content`** — it mangles
  UTF-8 (mojibake incident in the family; restored via git). Use the Edit/Write tools or Node
  scripts for text.
- **PS 5.1 collapses `@(@(a,b))` when there is exactly ONE nested pair** — `$pair` becomes a STRING, so
  `.Replace($pair[0],$pair[1])` is `Replace('E','v')`. Rewrote every capital E in a tracked doc
  (2026-08-05); the same script's five-pair target stayed nested and was correct, so more than one case
  hides it. **Bulk text edits go through the Edit tool**; if scripted, `git diff --numstat` before moving
  on — the tell is a diff far larger than the edit.
- **Undo a sabotage with the SAME tool that applied it (Edit/Write), then confirm GREEN.** Both
  restore shortcuts lie, and both were hit live in one session: `Move-Item`/`Copy-Item` preserve
  `LastWriteTime`, so the restored file can be older than the assembly built from the sabotage and
  the incremental build silently keeps running it (dangerous direction: a stale PASS); and
  `git checkout -- <path>` reverts to HEAD, discarding uncommitted edits the file already carried.
  A sabotage is only verified once both directions are — `dotnet build -t:Rebuild` if unsure.
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
  `src/Directory.Build.props`).
- Git Bash path mangling: set `MSYS_NO_PATHCONV=1` when an argument like `/p:...` or a
  device-style path must reach a native tool untouched.
- **Never build this repo from the session scratchpad path** — it is deep enough that `aapt2` fails
  with `APT2098 …flat: error: failed to open file`, which reads as a corrupt Android resource and is
  really MAX_PATH. It cost a false "this commit is broken" verdict against a `git worktree` there.
  Build a worktree under `devtools/_*` (gitignored) instead. The tell that it is the PATH and not the
  code: check out a tree you KNOW is green and confirm it fails identically.
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
  that stalls the whole suite (found live; see `OptimizedFormTests.RunSta`).
