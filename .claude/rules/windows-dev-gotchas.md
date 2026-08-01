# Windows dev gotchas (this machine)

- **NEVER roundtrip source files through PowerShell 5 `Get-Content`/`Set-Content`** — it mangles
  UTF-8 (mojibake incident in the family; restored via git). Use the Edit/Write tools or Node
  scripts for text.
- **Restoring a sabotage with `Move-Item`/`Copy-Item` can leave MSBuild running the SABOTAGED
  binary.** Both cmdlets preserve `LastWriteTime`, so a file restored from a `.bak` can be OLDER
  than the assembly built from the sabotaged version — the incremental build then reports success,
  changes nothing, and the suite keeps failing (or, in the dangerous direction, keeps PASSING against
  a stale binary while you conclude a tripwire fires when it does not). Hit live during the 0.2.0
  design pass. The sabotage-verification discipline this repo runs on depends on the restore actually
  taking effect, so: restore by rewriting the CONTENT (Edit/Write) rather than moving a file back, or
  force it with `dotnet build -t:Rebuild`. Re-run the test after restoring and confirm it is GREEN —
  a sabotage is only verified once both directions are.
  ⚠ **`git checkout -- <path>` is NOT a safe restore either** — it reverts to HEAD, silently
  discarding any UNCOMMITTED edits that file already carried. Also hit live: a sabotage restore threw
  away a redirect made minutes earlier in the same file. Prefer sabotaging a file you have no pending
  work in, or undo the sabotage with the same tool that applied it.
- **Node 24 `fs.cpSync` crashes on this box** (fail-fast 0xC0000409, silent). Use manual
  recursive copy loops in .mjs scripts.
- PS 5.1 quirks in scripts: no `&&`/`||` chains; `-Encoding utf8` writes a BOM (fine for
  PowerShell, poison for JSONL/BOM-sensitive consumers). BOM-less UTF-8 C# sources on this
  CJK-locale machine need `<CodePage>65001</CodePage>` or csc reads them as ANSI (set in
  `src/Directory.Build.props`).
- Git Bash path mangling: set `MSYS_NO_PATHCONV=1` when an argument like `/p:...` or a
  device-style path must reach a native tool untouched.
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
