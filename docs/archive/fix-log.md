# FIX-LOG.md — notable fixes, newest first

Append via `/fix-log` after landing any non-trivial bug/regression fix. Grouped by `## <date>`;
entry template:

```
### <area>: <symptom>
- **Symptom:** what was observed
- **Root cause:** the actual mechanism
- **Fix:** what changed (files)
- **Verify:** how it was proven fixed
- **Commit:** <hash>
```

## 2026-08-06

### Android: reloading any hash-routed page died with `net::ERR_INVALID_RESPONSE`

- **Symptom:** filed by the first adopter against 0.9.1 — with a route registered, a page RELOAD showed
  the webview's error document while sub-resources and in-page hash routing stayed fine. **It would not
  reproduce here across three sessions**, on MuMu's Chromium 110 and on a purpose-built API 36 AVD running
  Chromium 133, which is what killed the repo's own version-gap hypothesis.
- **Root cause:** MAUI's request→asset mapping strips a QUERY and not a FRAGMENT, so a reload at
  `https://host/#/library` looks for an asset literally named `#/library`, answers 404 with no body and no
  MIME type, and Chromium turns that into `ERR_INVALID_RESPONSE`. **Not the kit's**: the adopter's A/B on
  one binary failed identically with no interceptor constructed at all. **Why every gate here was green:
  the reload probe reloaded at `/`** — it was aimed one character short of the defect. The misattribution
  had the same shape: `AbsolutePath` reads `/` for a fragment URL, so logging it *proves* nothing about
  whether a fragment is present, and it is also the reading everyone is told to use.
- **Fix:** `MobileWebViewInterceptor.RepairDocumentRequest` (Android only) answers a fragment-carrying root
  request with `HybridRoot/DefaultFile` — the bytes the platform serves for the fragment-free URL — so the
  page boots normally and its router reads the fragment off `location`. It runs AFTER app middleware
  decline and ALSO when the pipeline is empty (the defect is the platform's, not the pipeline's), and it
  DECLINES rather than 404s when the bundle cannot be read, so an app serving its own document is
  untouched. Predicate: `WebViewResourceRequest.IsRootWithFragment(Uri)`, public because a middleware
  answering the root needs the same shape. `PageProbe.CheckReloadAsync` now runs TWO arms (`/` as control,
  then `#/probe-route`) so a failure is attributable. iOS deliberately NOT repaired — same trigger,
  different machinery, and the adopter measured this repair making it worse there.
- **Verify:** sabotage-verified BOTH ways on a device (MuMu, WebView **110** — the adopter's exact
  version). Repair off → `RELOAD: FAIL`, `title=网页无法打开|nodes=11|text=… net::ERR_INVALID_RESPONSE`,
  **the reported symptom reproduced in this repo for the first time**. Repair on → `RELOAD: PASS` on both
  arms, `hash=#/probe-route|title=Shenora mobile sample|nodes=56`, with the shell's own
  `Served 'wwwroot/index.html' for a fragment document request`. Predicate unit-tested over 11 measured
  URL shapes, sabotage-verified in both directions. `verify` PASSED (1073 dotnet + 115 vitest).
- 🔴 **The sabotage caught a second bug — in the new gate.** Its first run reported `RELOAD: PASS` while
  staring at Chromium's error document, because the recovery check was a BLOCKLIST ("empty title, or
  `ERR_` in the body") and **both signals failed at once**: the error page's title is LOCALIZED and
  non-empty (`title=网页无法打开` on this CJK-locale device), and the body text was truncated to 60 chars
  ONE CHARACTER before the underscore (`net::ERR`). Fixed by inverting the shape — recognise OUR document
  by a title read from the live page, never the platform's error page — plus a 120-char slice, a `hash=`
  snapshot field and a `Misaimed` outcome so an arm must prove it was aimed. ⚠ **The hole was in the
  ORIGINAL probe too**; it never fired because the arm it guarded never failed. Rule: `mobile-shells.md`.
- **Commit:** _pending_

## 2026-08-05

### Launcher: the POSIX half had never been compiled by anything, and the first release build failed on it

- **Symptom:** the `launcher` matrix leg for `linux-x64` failed the moment a release was dispatched —
  `src/Shenora.Launcher/src/platform_posix.cpp:40:35: error: 'all_of' is not a member of 'std'`. The
  Windows leg was green, as it had been for every local run since the library was built.
- **Root cause:** two missing includes, `<algorithm>` (`std::all_of`) and `<chrono>`
  (`std::chrono::milliseconds`), in a file that only ever met MSVC — which supplies both transitively
  through other headers, where gcc does not. The deeper cause is the gap between two things that sound
  identical: `CMakeLists.txt` deliberately compiles BOTH platform `.cpp` files on every build so neither
  can rot, and that comment was read as "both platforms are proven". It is not — an `#ifdef`-guarded body
  is only checked by the compiler that takes the branch, so `platform_posix.cpp` was compiled constantly
  and never actually parsed past its `#ifndef _WIN32`. D5 (no push CI, one manual release) meant nothing
  else would ever compile it either.
- **Fix:** the two includes, with a note at the top of the file naming the trap
  (`src/Shenora.Launcher/src/platform_posix.cpp`). The gap itself closed with
  **`node devtools/dev.mjs launcher --posix`** — a `gcc:13` container build that a Windows box can run,
  driving the REAL `CMakeLists.txt` rather than its own g++ line so the check cannot drift from the build
  it checks, and running the binary afterwards to prove it links and starts. Docs: `devtools/README.md`,
  the `CMakeLists.txt` header, D50.
- **Verify:** sabotage-verified both ways — with `<algorithm>` removed the command exits non-zero and
  names `platform_posix.cpp:44` and the symbol; restored (via the same Edit tool, per
  `windows-dev-gotchas`) it compiles, links, runs `--apply-and-exit` → `applied=0 attempted=0`, exit 0.
  Windows unaffected: `dev.mjs launcher` still builds at 322 KB with 6/6 conformance cases, `verify`
  green. Also measured the figure D50 had only ever guessed for Linux: **46.8 KB** vs 322 KB on Windows,
  the whole gap being the statically linked CRT.
- **Commit:** `5e50674`

### Windows dialogs: `OpenFolderAsync(AllowFileSelection: true)` returned the PARENT FOLDER for a file named `Folder Selection.txt`

- **Symptom:** with `AllowFileSelection` set, picking an existing file whose name matches the internal
  placeholder — `Folder Selection` exactly, or `Folder Selection.<any extension>` — yielded that file's
  DIRECTORY instead of the file. Silent, and a wrong answer rather than a refusal. Found by reading during
  the public-surface sweep, not reported by an adopter.
- **Root cause:** Windows has no "file OR folder" dialog mode — the Common Item Dialog picks folders
  (`FOS_PICKFOLDERS`, what the plain branch uses) or files, never both — so `ShowFileOrFolderDialog` types a
  fake file name into an `OpenFileDialog` and recovers the user's intent by reading it back out. The
  read-back tested the NAME first, `Path.GetFileName(selected) == placeholder ||
  Path.GetFileNameWithoutExtension(selected) == placeholder`, and a real file satisfies both. The placeholder
  trick came in with `43f18ad` (P4, ported from the primary desktop sibling along with the rest of the STA
  dialogs); the collision was in the port from the start rather than introduced later.
- **Fix:** a REAL FILE now wins over the placeholder — `if (File.Exists(selected)) return selected;` before
  any name test, since a name nothing put on disk can only mean "this folder" when no file by it exists. The
  disambiguation moved out of the dialog into `FileDialogs.ResolveFileOrFolderSelection` (`internal static`,
  pure) with the placeholder as `FileDialogs.FolderPlaceholder`, so the only decision in that dialog is
  reachable without opening one — the reason it went untested for four months.
  `src/Shenora.Windows/Shell/FileDialogs.cs`, `tests/Shenora.Tests/WinForms/FileOrFolderSelectionTests.cs`.
- **Verify:** five tests, and sabotage-verified in both directions — restoring the old name-first ordering
  fails exactly the two defect cases (`A_REAL_file_named_like_the_placeholder_wins_over_it`,
  `A_REAL_extensionless_file_named_EXACTLY_the_placeholder_also_wins`) while the three that must stay QUIET
  keep passing, which is what proves the fix does not break the ordinary pick. `dev.mjs verify` green.
- **Reusable half, worth more than the fix:** *a workaround that encodes intent in a STRING must treat a
  real collision as the higher authority.* The placeholder is a sentinel in a namespace the user also
  controls, which is the same shape as `GlobalLaneCapacity`'s `0 = auto` retired the same day — a legal
  value in the caller's own space silently reinterpreted as a command. ⚠ Still locale-fragile in a second
  way (the placeholder is a hardcoded English string), which the fix makes harmless in practice but does not
  remove.
- **Commit:** `6b6cbe2`

## 2026-08-02

### release: three gates failed the 0.4.0 run, each never tested on the path it should ignore

- **Symptom.** The 0.4.0 release failed twice from the Actions tab. First in step 1 inside
  `doctor --fix`: *"`<VersionPrefix>` is 0.4.0 but the newest release tag is v0.3.0"*. After that was
  fixed, again at `knowledge footprint`: *"16.2 KB / 16.0 KB — ⚠ OVER"*. Both runs died before the
  verify gate finished, so nothing was published, committed or tagged — but nothing could ship either.
- **Root cause — one pattern, three instances.** Every check had been proven on the path where it
  should FIRE and never on the path where it should stay quiet:
  1. **Version-authorship guard.** It asserts `VersionPrefix == newest release tag`, true BETWEEN
     releases and deliberately false DURING one (the workflow bumps props in step 1, tags in step 6).
     It was sabotage-verified against the hand-bump it exists to stop, but no release had run since it
     was added, so the window where its invariant is meant to be false was never exercised.
  2. **Rule-size budget.** `fs.statSync().size` counts CRLF as two bytes. The dev box has an LF
     working tree, the CI runner an autocrlf checkout — identical files, 16.0 KB local vs 16.2 KB in
     CI. It had never run on a CRLF checkout. It was also FATAL, which it should never have been.
  3. **A build-output filter.** Local runs were filtered for `warning CS`, which silently excludes
     analyser warnings with other prefixes — so an `xUnit2031` warning was reported as "0 warnings"
     all session until CI printed it.
- **Fix.** `SHENORA_RELEASE=1` (the signal the pre-commit version guard already honoured) is now read
  by `doctor` and set job-wide in `release.yml`, so the guard stays quiet for the pipeline's own bump
  and loud for everything else. Sizes are measured after normalising line endings. The budget exits 0
  and prints ⚠ instead of failing — a style budget must not outrank shipping. The analyser warning is
  fixed; the build is genuinely at 0 warnings.
- **Verify.** Both guard directions were re-proven against a real 0.4.0-shaped tree (props bumped,
  derived files synced, tag still v0.3.0): with the env var, skipped and passing; without it, the full
  hand-bump failure. Note the first attempt at that check never reached the guard — bumping props
  alone trips the CONSISTENCY checks first, and the tag check only runs once those pass.
- **The rule this earned** is in `.claude/rules/phase-workflow.md`: verify the paths where a gate must
  stay QUIET, in the environment it will really run in — and keep gates proportionate, because
  correctness stops a release and style does not.
- **Commits:** `da68bf1` (release guard), `099af66` (budget + warning), `7c6f4eb` (the ARCHITECTURE
  sync that the same release path had also been missing).

### core: two XML comments on the mission scheduler described a kit that does not exist

- **Symptom.** Nothing failed. `IMissionScheduler.SubmitAsync` and `MissionResult` both listed "unknown
  lane" among the caller errors that throw at submit, and `IMissionObserver` read as though the kit ships
  the observer that binds execution to `Shenora.Ipc`'s operation registry. An adopter trusting the
  first writes `Lanes = [new MissionLane("gpu")]`, mistypes the name once, and gets no error and no
  exclusivity; an adopter trusting the second goes looking for a type that is not there.
- **Root cause.** `MissionScheduler.CreateEntry` resolves a lane exactly as `Lane(name)` does — creating
  it at the DEFAULT capacity when the name is new. Only an unregistered claim *scope* throws, and
  deliberately so (silently dropping an exclusion the caller asked for is the one failure a scheduler
  must not have). The XML was written from the design's intent rather than from the code, and prose is
  the one surface with no compiler — the same class of defect the `doc-drift` gate exists for, but
  below that gate's reach: it checks retired names, doc links and the dependency graph, not whether a
  remark about runtime behaviour is true. Nothing implements `IMissionObserver` either.
- **Found by.** Writing the adopter-facing scheduler section of `docs/ADOPTION.md` **against the
  source instead of against the design doc**. Three claims did not survive that check; the third —
  design §6's `IFileSystem` and atomic-replace helper, which never shipped — needed no code change and
  is recorded in `ARCHITECTURE.md`.
- **Fix.** The remarks in `IMissionScheduler.cs`, `MissionResult.cs` and `MissionObserver.cs` now say what the
  code does, and `MissionRequest.Lanes` carries the consequence at the property a caller is looking at
  when they get it wrong. Comments only — no behaviour change.
- **Verify.** The asymmetry is now pinned by
  `MissionSchedulerBehaviourTests.An_unseen_LANE_name_is_created_at_the_default_capacity_rather_than_throwing`,
  which also asserts the typo'd lane carries the default capacity rather than the configured gate's.
  Sabotage-verified both ways: making `CreateEntry` throw for an unknown lane failed the new test by
  name; restoring with the same tool left `MissionScheduler.cs` byte-identical to HEAD and the test green.
  Gate: 733 dotnet + 101 vitest, 0 warnings.
- **Commit:** `49bfc0c` (docs half: `d47d8c9`, `82b1380`).

### devtools: `dev.mjs sample` ran an arbitrarily STALE frontend, silently

- **Symptom.** A hands-on test of the drop zone (owner, on the running sample) found the drop itself
  working — real OS paths arriving — but **no hover feedback**: the zone never lit up as a drag
  entered it. Nothing in the source explained it. `.drop-hover` was defined in
  `samples/Shenora.Sample.Web/index.html`, the hook was called with `dropClassName: 'drop-hover'`,
  the class-toggle code was correct, and `verify` was fully green.
- **Root cause.** Not the React side at all. Production mode serves the EMBEDDED `wwwroot`, and
  `dev.mjs sample` was a bare `dotnet run` — it never built the bundle. `wwwroot/` is gitignored (a
  local build output emitted by `npm run build` in the web project), so the command ran whatever
  happened to be on disk: a bundle from 30 July, three days older than the `.drop-hover` rule. Its
  `index.html` was 552 bytes and carried only the base `html, body` rule. The comparison that makes
  the gap obvious: `dev.mjs vite` has always done the full chain (install deps → build
  `@shenora/react` → install sample deps → serve), and the production path had no equivalent.
- **Why it is more than cosmetic.** `.claude/rules/phase-workflow.md` says behavioural claims about
  the desktop shell are proven against the sample rather than asserted. A silently stale bundle means
  such a claim can be proven against arbitrarily old frontend code — the verification path itself was
  unsound. Sharper still: that CSS was added by P5.5 H7 *because* the hover feedback "is the part an
  adopter most wants to see working, was invisible" — the fix landed in source and never reached the
  thing anyone runs, so the defect it fixed persisted unnoticed for three days.
- **Fix.** `devtools/dev.mjs` `case 'sample'` builds the packaged frontend before launching whenever
  `--dev` is absent (in `--dev`, vite serves source, so a bundle build is pointless). `--no-build`
  skips it for a quick relaunch when only the C# side changed. The comment records the incident so
  the step is not "optimised away" later.
- **Verify.** Deleted `samples/Shenora.Sample.Desktop/wwwroot` entirely, ran `node devtools/dev.mjs
  sample`, and watched it rebuild (`index.html` 1.13 kB, hover rule present) and boot to
  `Client ready` — i.e. proven in the FAILING direction, not just observed green. Then re-tested by
  hand: hover feedback now fires. Both features re-confirmed live on the running sample — frameless
  chrome (Snap Layouts on maximize-hover, work-area maximize with no DPI gap, top-edge resize,
  rounded↔square corners) and drop zones (real OS paths, background drags, hover feedback).
- **Commit:** see the devtools commit dated 2026-08-02.

## 2026-08-01

### 0.2.0: the documented package dependency chain had an edge the packages do not have

- **Symptom.** `README.md` and `docs/ADOPTION.md` both drew the graph as a single chain,
  `Shenora.WebView2.Sessions → Shenora.WebView2 → Shenora.WinForms → Shenora.Ipc → Shenora.Core`,
  and immediately told the reader "a shell with no web frontend references `Shenora.WinForms`
  directly". Following that, an adopter's `BaseFacade`/`IpcRequest` code does not compile, and the
  error names a missing NAMESPACE rather than a missing package.
- **Root cause.** The `Shenora.WinForms → Shenora.Ipc` edge has never existed.
  `Shenora.WinForms.csproj` references only `Shenora.Core`; the real graph is a diamond, with
  `Shenora.Ipc` (`net10.0`, no UI-framework binding) and `Shenora.WinForms` (`net10.0-windows`) as
  SIBLINGS over `Shenora.Core`, joined for the first time by `Shenora.WebView2`. That absence is
  load-bearing in both directions — it is what keeps the IPC envelopes transport-neutral (D16) and
  the reason `WindowCommandFacade`/`DropZoneFacade` live in `Shenora.WebView2` rather than in
  `Shenora.WinForms`. Four code comments state the invariant (the WebView2 csproj, `ShellLauncher`,
  `DropZoneManager`, `WindowCommandFacade`); the two documents an adopter actually reads stated its
  opposite, and no test compares a doc to a csproj.
- **Fix.** Both documents now draw the real diamond with the TFM per package, lead with "`Shenora.Ipc`
  is platform-neutral and stays that way", and carry an explicit warning that `Shenora.WinForms` does
  not bring `Shenora.Ipc` with it. `README.md`, `docs/ADOPTION.md`.
- **Verify.** `dotnet list <proj> reference` on each of the five packables, plus the built output TFM
  (`src/Shenora.Ipc/bin/Debug/net10.0`), plus a grep of `Shenora.Ipc` for any Windows binding (the one
  hit is a doc comment naming WinForms as an example base). `dev.mjs verify` clean — 680 dotnet +
  101 vitest.
- **Commit:** the docs commit immediately following `6697697` (this fix is documentation-only — the
  packages themselves were always right).

### 0.2.0: `@shenora/react` did not export `OperationProgress`, and the barrel gate could not see it

- **Symptom.** `OperationInfo` is exported and its `progress` field is typed `OperationProgress`, but
  that type was not on the barrel — so the field's own type was unnameable from outside the package.
  The only visible symptom was in the kit's OWN sample: `samples/Shenora.Sample.Web/src/App.tsx`
  re-declared the shape inline (`{ value: number; total?: number; unit?: string }`) to write a
  one-line progress formatter, and the npm README's example did the same.
- **Root cause.** `OperationProgress` was added in commit `71e09f5` (progress-is-not-percent) and
  wired through the C# side, the TS mirror and its wire tripwire — but not added to `index.ts`.
  Nothing failed, because the npm surface gate (`index.test.ts`) pins the barrel by comparing
  `Object.keys(barrel)` against an explicit array. A `export type` has no runtime binding, so the
  gate is structurally blind to the entire type half of the public surface.
- **Fix.** Exported `OperationProgress` — plus `OperationEventTypes` and `OperationModuleName`, the
  same gap for the two events `createOperationsStore` deliberately does not subscribe to. Added the
  missing half of the gate: a type-only `import type { … } from './index.js'` in `index.test.ts`
  consumed by an `ExportedTypeSurface` tuple alias, which `npm run typecheck` (the full tsconfig,
  which includes test files) compiles. `src/Shenora.React/src/index.ts`, `index.test.ts`, plus the
  sample and npm README now importing the type instead of re-declaring it.
- **Verify.** Sabotage, per the standing tripwire rule: removing `type OperationProgress` from
  `index.ts` fails the typecheck with
  `src/index.test.ts(17,3): error TS2305: Module '"./index.js"' has no exported member 'OperationProgress'`,
  restored and green. `dev.mjs verify` clean — 680 dotnet + 101 vitest, both typechecks.
- **Commit:** `6697697`

### 0.1.2: WindowStateManager.Apply(Form) resolved DPI on the wrong monitor on cross-monitor mixed-DPI setups

- **Symptom.** First cut of the 0.1.2 fix (commit `109654c`) made `Apply(Form)` defer to
  `HandleCreated` and resolve `ScaleFromDeviceDpi(form.DeviceDpi)` at that moment — claiming
  "per-monitor accuracy is now the default." Adversarial phase review flagged this as unproven
  for the actual multi-monitor scenario, and empirical probing confirmed the flaw.
- **Root cause.** At `HandleCreated`, the form's handle is created wherever
  WinForms/Windows initially places it — typically the primary monitor, since `Location` hasn't
  been set to the saved value yet. On a mixed-DPI setup with a saved position on a
  different-DPI secondary monitor, `form.DeviceDpi` returned the PRIMARY's DPI, not the
  target's, and the restored `Size` was computed against the wrong scale. The commit's live
  verification was on a single 200% display, which cannot exercise this path. WinForms' default
  `WM_DPICHANGED` handler does not rescale a Form's outer `Size` (verified live in
  `devtools/_dpi-probe/`: after a 200% → 150% change, `SuggestedRectangle` came back with the
  form's current width/height unchanged and the handler left `Size` alone), so there was no
  self-heal to fall back on.
- **Fix.** `WindowStateManager.PrePositionToTargetMonitor` moves the handle to the saved
  location BEFORE reading `DeviceDpi`. The `SetWindowPos` fires `WM_DPICHANGED` synchronously
  as the window crosses monitors, updating `DeviceDpi` to the target monitor before the scale
  is resolved. `src/Shenora.WinForms/WindowStateManager.cs`.
- **Verify.** Two new tests — `Apply_parameterless_pre_positions_before_reading_DeviceDpi` and
  `Apply_parameterless_skips_pre_position_for_an_off_screen_saved_position` — cover the code
  path on a single-monitor test machine (the DPI change itself cannot be observed without a
  second monitor, but pre-position happening / not happening is testable). Empirical DPI-event
  behaviour verified live via `devtools/_dpi-probe/` (a standalone .NET 10 WinForms PerMonitorV2
  probe that logs `DeviceDpi` + `Size` on every `DpiChanged` event). `dev.mjs verify` clean —
  566 dotnet + 85 vitest.
- **Commit:** _pending_

### 0.1.2: WindowStateManager.Apply on a plain Form opened restored-down from a saved Maximized=true

- **Symptom.** After adopting `WindowStateManager` in a private desktop sibling (Stage 1 on 0.1.1)
  a plain `Form` closed while maximized reopened as `Normal` however the saved state was set. Also
  present in the app's own hand-rolled predecessor — A/B'd against the pre-adoption build, which
  had the identical bug — so not a kit regression, but the kit was now the right place for the
  one-time fix.
- **Root cause.** `WindowStateManager.Apply` set `form.WindowState = FormWindowState.Maximized`
  synchronously for non-`IAppMaximizable` forms. Measured live: state is `Maximized` at the end of
  `OnHandleCreated` and back to `Normal` by `OnLoad` (WinForms' internal Load-time state
  reconciliation on a form whose window was created without the maximize style set). The
  `IAppMaximizable` path already used the `RestoreMaximizedTag` marker + `OnShown` consumption,
  which fires after `OnLoad` and so survives the reset.
- **Fix.** Extend the same marker mechanism to plain forms via a one-shot `Shown` handler
  (`DeferMaximizeToShown` at `src/Shenora.WinForms/WindowStateManager.cs:125-135`), consuming the
  same `RestoreMaximizedTag`. One owner for "apply maximize once realized."
- **Verify.** New test `Apply_defers_maximize_to_Shown_for_a_plain_form` runs the full show
  sequence under `Sta.Run` with `Application.Run(form)`, asserts `WindowState.Maximized` at
  `Shown` time and that the marker was consumed. `dev.mjs verify` clean (564 dotnet + 85 vitest).
- **Commit:** _pending_

## 2026-07-31

### P6.4: the npm package's shipped types needed `@types/react` in the CONSUMER's global program

- **Symptom.** A consumer type-checking against the published `.d.ts` with `"types": ["node"]` in its
  tsconfig failed with `error TS2503: Cannot find namespace 'React'` — pointing at
  `dist/useDropZone.d.ts`, a declaration file the consumer cannot edit. Nothing in the consumer
  mentioned drop zones; importing anything from the barrel was enough.
- **Root cause.** `src/Shenora.React/src/useDropZone.ts` declared `UseDropZoneOptions.targetRef` as
  `React.RefObject<HTMLElement | null>` — the **UMD global** `React` — while importing only
  `useEffect`/`useRef`/`useState` from `'react'`. TypeScript emits that annotation into the `.d.ts`
  verbatim, so the shipped declaration NAMES `React` with no import and resolves only when the
  consumer's program happens to contain `@types/react` globally. Introduced in `43f18ad` (P4) and
  invisible ever since, because every program that had looked at it also had React in it.
- **Fix.** `import { type RefObject } from 'react'` and annotate with `RefObject<…>`
  (`src/Shenora.React/src/useDropZone.ts`). The type is identical, so nothing source-breaking; the
  emitted `dist/useDropZone.d.ts` now carries its own import and is self-contained.
- **Verify.** Mechanism NAMED before the fix, not guessed: adding `"react"` to the probe's `types`
  array made the error vanish and removing it brought it back. After the fix the probe type-checks and
  RUNS under `"types": ["node"]` (`devtools/_p6-adapters/client`, 18 assertions), and
  `dev.mjs verify` passes 507 dotnet + 84 vitest.
- **Why five phases of gates missed it.** P6.1's npm consumer exists to catch exactly this class and
  did not, because its own `tsconfig.json` type-checks a `.tsx` that imports React — which loads the
  global. **A consumer probe only tests the configuration it happens to have.** The invariant is in
  `.claude/knowledge/ipc-contracts.md`: a shipped `.d.ts` must not name a type it did not import.
- **Commit:** _pending_

### P6.1: a consumer silently restored a months-old package from the NuGet global cache

- **Symptom.** A scratch consumer referencing only the leaf package failed to compile with "the type
  or namespace name 'WinForms' does not exist in the namespace 'Shenora'", while `Shenora.Core`,
  `Shenora.Ipc`, `Shenora.WebView2` and `Shenora.WebView2.Sessions` all resolved. Restore reported no
  error at all.
- **Root cause.** NuGet's global folder (`~/.nuget/packages`) is keyed on id+VERSION and wins over
  every source, including the local `publish/packages` feed. The cached `Shenora.WebView2 0.1.0` had
  been packed BEFORE the D19 re-layer added the `Shenora.WinForms` edge, so its recorded dependency
  graph simply had no WinForms in it. The freshly packed nupkg on disk was correct the whole time.
  `--no-cache` does not help — that flag governs HTTP caching, not the global folder.
- **How it was found, and the step that made it decisive.** The nuspec inside the packed nupkg listed
  `Shenora.WinForms`, so the package was right; `obj/project.assets.json` recorded only
  `Shenora.Ipc` + `Microsoft.Web.WebView2` for the same package id, which is what proved a DIFFERENT
  copy had been resolved. Comparing those two is the diagnosis for this whole class.
- **Fix.** `dev.mjs pack` now evicts this repo's ids at the packed version from the global folder
  after packing (`evictGlobalCache`) — a fresh pack makes any cached copy of those exact ids stale by
  definition, so the trap is removed rather than documented. `docs/RELEASING.md` carries the warning
  and the manual remedy for anyone who obtained packages another way.
- **Why it matters more than a scratch consumer.** This would have silently invalidated P6 itself: an
  adopting app would have compiled against a stale surface while believing it was testing the current
  one. It also means any re-run of the P1.1 smoke since the re-layer had been testing the old package.
- **Verified.** With the cache evicted, one PackageReference to the leaf resolves all five assemblies
  and the consumer runs. Re-running `pack` reports "evicted 5 cached package(s)".

### P6.1: two smaller findings from the same dry run

- **The transitivity promise was never tested.** `docs/RELEASING.md` says to reference the leaf and
  let the rest arrive, but P1.1's smoke referenced two packages BY HAND, so nothing exercised the
  chain. The P6.1 consumer references exactly one and touches a type from every package, so a missing
  link is a compile error. (It is what surfaced the cache bug.)
- **D20's portability claim now holds through a PACKAGE reference, not only a ProjectReference.** A
  `net10.0` (non-Windows) project consuming `Shenora.Core` compiles app logic against
  `IUrlLauncher`/`IClipboardService`/`IFileDialogs`/`IUiDispatcher`. The in-repo sample proves the
  same thing, but package resolution is a different path and had never been checked.

### Maximize+restore did not exit an Aero snap

- **Symptom.** Snap the window to a screen edge, then maximize and restore it: it went straight back
  into the dock. Every other Windows app leaves the snap. User-reported.
- **Root cause.** `OptimizedForm.Maximize` captured its restore target with `GetWindowRect`, i.e. the
  window's CURRENT rectangle — which, for a snapped window, is the docked half. Restoring therefore
  restored the dock.
- **Fix.** Capture `WINDOWPLACEMENT.rcNormalPosition` instead. That is Windows' own definition of
  "where this window goes when restored", and Aero Snap deliberately leaves it at the PRE-snap
  rectangle. `GetWindowRect` remains the fallback if the call fails.
- **Why this beats the planned approach.** `TASKS.md` had budgeted for an is-this-window-snapped
  heuristic (compare the rect against the work area's halves and quadrants) because Win32 has no clean
  API for it. Probing first showed the question never has to be asked: Windows is already tracking the
  answer. Measured before writing any code — Win+Left moved the rect from (783,413) 2560x1600 to
  (-10,0) 1940x2314 while `rcNormalPosition` stayed byte-identical.
- **Verified.** Live, with real input, because Aero Snap is an OS shell gesture no message can fake:
  Win+Left, then SC_MAXIMIZE, then SC_RESTORE. Rects went pre-snap 2560x1600 → snapped 1940x2314 →
  maximized 3840x2304 → **restored to the pre-snap 2560x1600**. The unit suite pins the ordinary
  maximize/restore round-trip; the snapped case is e2e by construction (`rcNormalPosition` only
  diverges from the live rect once the OS has actually docked the window), which `docs/REVIEW-GUIDE.md`
  now records so it is not re-filed as a coverage gap.
- **A probe trap on the way, worth its own line.** The first verification script named its helper
  `R`, which collides with PowerShell's `Invoke-History` alias: every rect read failed, all four rects
  came back empty, and the comparison then reported **PASS** — a vacuous pass, the exact thing the
  standing rule warns about ("treat a passing check whose mechanism you cannot name as unverified").
  The script now refuses to judge when any read returns nothing.

### Drop zones were cleared on the ready handshake, which raced the page

- **Symptom.** A drop zone registered before the client's `READY` was silently dead: the client had a
  successful ack and believed the zone was live, the host had already destroyed the overlay, and
  nothing was logged on either side.
- **Root cause.** The reset was keyed on the HANDSHAKE, so it ran after anything the page sent first.
  React runs CHILD effects before PARENT effects, so the obvious composition — `notifyReady()` in a
  root-component effect — put the handshake AFTER every child's `useDropZone` registration, making the
  bug the default outcome rather than bad luck.
- **Fix.** `DropZoneManager` subscribes to `CoreWebView2.ContentLoading` itself and clears there, so
  overlay lifetime follows the DOCUMENT. It hooks either immediately or via
  `CoreWebView2InitializationCompleted`, because apps construct the manager before the (slow) WebView2
  init. `ContentLoading`, never `NavigationStarting` — the same choice the IPC ready gate made in H3,
  because `NavigationStarting` also fires for navigations that never replace the document.
- **Why it is a fix and not a doc change.** P5.5 H7 had documented this contract in FOUR places
  (`notifyReady`, `UseDropZoneOptions`, `ClearAll`, the npm README) precisely because it was sharp
  enough to be missed anywhere it was not repeated. Removing the contract deleted all four warnings and
  the app's `ClearAll()` call. The trigger to finally do it was a second feature needing the same
  reset: the sample had to hand-roll it for its streaming session, right next to `_dropZones.ClearAll()`.
- **Verified.** Live: the drop-zone overlay is a real child window, so it is countable. After startup
  (two document loads) there is EXACTLY ONE overlay at the zone's position — zero would mean the clear
  ate the registration, more than one would mean nothing cleared and overlays were accumulating.

### P5.6 hybrid: the hover state changed but nothing ever repainted

- **Symptom.** With the window owning the caption-button pixels, the buttons rendered correctly but
  never reacted to the pointer — no hover, no pressed. Reported by the user as "the functionalities
  kind working, but the hover style is not".
- **Root cause.** `OptimizedForm.SetCaptionButtonState` updated `_hotCaptionButton`/
  `_pressedCaptionButton` and raised `CaptionButtonStateChanged`, but never called `Invalidate`. That
  was harmless while the PAGE drew the buttons (the callback was the whole delivery mechanism) and
  became the entire bug the moment the kit started painting them.
- **Why it survived everything.** Every other link in the chain was already correct — the clip, the
  hit-test, `WM_NCMOUSEMOVE` arriving, the state flipping, the callback firing. Instrumenting proved
  the message arrived (`hot=Close`) and that `OnPaint` ran with the right clip rectangle and the right
  state, which is what narrowed it to "the repaint was never requested".
- **Fix.** `SetCaptionButtonState` invalidates the cluster before raising the callback. It is a no-op
  in the un-clipped mode, where the callback is the point.
- **Verified.** Screen-pixel sample over each button: `#252525` idle → `#2F2F2F` hovered, close →
  `#C42B1C`, and back to `#252525` on leave. Pinned by `Hovering_a_button_repaints_it` and
  `Releasing_a_press_repaints_the_button_it_left`, which watch `Control.Invalidated` — no message
  pumping, so they cannot stall the suite the way a modal loop once did.

### P5.6 hybrid: a child added after the rects were reported was never clipped

- **Symptom.** A control added to the form AFTER `SetCaptionButtons` had run kept covering the caption
  buttons, killing the feature for it. Latent in the sample (a drop-zone overlay is exactly this
  shape); found by a new test, not by running.
- **Root cause.** `ControlAdded` fires BEFORE the child's handle exists, and a window region cannot be
  applied to an unrealized control — so the hole was skipped once and nothing ever came back to it.
- **Fix.** Track every direct child's `HandleCreated` as well as `SizeChanged`/`LocationChanged`, and
  compute the hole when the handle appears. Tracking is now unconditional and separate from the set of
  children we actually gave a region to, so the kit only ever nulls a region IT set — an app is free
  to give its own control one.
- **Verified.** `A_child_added_AFTER_the_rects_were_reported_is_cut_too`, which failed before the fix.

### P5.6 review: NativeCaptionButtons without FramelessChrome did nothing, silently

- **Symptom.** None — which is the problem. A framed window never reaches the custom hit-test, so
  the option could only ever be a no-op; an app that set it would see "the caption buttons just
  don't work", with no error, no log and nothing to grep for.
- **Root cause.** The clip and paint paths were gated on `FramelessChrome && NativeCaptionButtons`
  and simply returned when the combination was wrong.
- **Fix.** `OptimizedForm`'s constructor throws `ArgumentException` naming both options. Fail at
  composition, not at the pixel — the same call the P5.5 H3 option-validation batch made.
- **Verified.** `Asking_for_native_buttons_on_a_FRAMED_window_fails_loudly`. Found by the phase
  review, under the "a gate that fails OPEN" heading of the standing checklist.

### Probe traps that made correct code look broken (P5.6, three of them)

Not product bugs — measurement bugs, each of which produced a confident wrong reading. Recorded
because the P5.6 feature was already shipped once on the strength of a probe that lied.

- **`WindowFromPoint` returns the TOPMOST window, so the target must be foregrounded first.** The
  first routing probe answered `CASCADIA_HOSTING_WINDOW_CLASS` — Windows Terminal, the console the
  probe itself ran in — which reads exactly like "the clip failed". Same for `GetPixel` on the screen
  DC. **WGC capture works while occluded and these do not**, which is why a screenshot can look right
  while the probe reports nonsense.
- **A region's `GetBounds` still spans the full rectangle when only a corner is excluded.** Probing
  bounds "confirmed" a hole whose existence had not been shown at all; `Region.IsVisible(point)` is
  the real check.
- **WGC capture is far too slow to catch a ~1 s splash.** Racing it wasted several runs; asserting the
  mechanism instead (`IsVisible` at the hole while the splash is still in `Controls`) is
  deterministic. The DPI rule from the earlier spike still stands: a probe process must call
  `SetProcessDPIAware()` first or every coordinate is the wrong pixel.
### Sample stream viewer: three defects that only RUNNING the app could find
- **Symptom:** the H9.5 seam test compiled, passed `verify`, and was wrong in three ways the moment it
  ran. (1) The stream reported `streaming 320x240` no matter how large the pane was. (2) After any page
  reload, every later START answered `STREAM_ALREADY_RUNNING` for the rest of the process. (3) The
  viewer pane collapsed to a single line of alt text before the first frame arrived.
- **Root cause:** (3) is the origin of (1). The `<img>` is an inline replaced element with no `src`
  until a frame lands, and its container had only `max-width` inside a centered, shrink-to-fit column —
  so the box collapsed, `start()` measured ~0 via `getBoundingClientRect()`, and the viewport it sent
  was clamped UP to `ClampViewport`'s 320x240 minimum. The stream then genuinely ran at the floor.
  (2) is more interesting: the first fix attempt was a React unmount cleanup calling STOP, and it does
  not work, because **effect cleanups do not run on a full page reload** — the page is simply gone.
  Only the HOST can observe a reload, and it already has the signal: the ready handshake.
- **Fix:** the container takes an explicit `width: 32rem` (+ `maxWidth: 100%`) and the image
  `display: block`, so the first measurement is real. The host disposes any live session in
  `OnClientReady` — the same place, and for the same reason, that it already calls
  `_dropZones.ClearAll()`: per-page host state belongs to the page that created it, and a handshake
  means a new page. The React unmount cleanup is kept as well, since it does cover an in-page unmount.
  Files: `samples/Shenora.Sample.Web/src/StreamViewer.tsx`,
  `samples/Shenora.Sample.Desktop/MainForm.cs`.
- **Verify:** LIVE, via `dev.mjs sample --dev` + `wgc` captures. Before: `streaming 320x240`; after:
  `streaming 514x240` (the pane's real width; 240 is the legitimate clamp floor for a 14rem box).
  Reload case: an HMR reload mid-stream followed by START previously produced
  `stream error: OperationError: STREAM_ALREADY_RUNNING` on screen, and now starts cleanly. `verify`
  PASSED (476 dotnet + 63 vitest).
- **Commit:** _pending_

### Sample STREAM route: three lifecycle bugs in the new co-browse composition
- **Symptom:** found by this batch's own phase review, in code the batch had just written. None had
  shipped; all three are the kind an adopter would copy, since the sample is the reference composition.
- **Root cause:** (1) `OnEnded` did not clear the sample's `_stream` handle, so a renderer death — which
  ends a session with nobody calling STOP — left it non-null and every later START answered
  `STREAM_ALREADY_RUNNING` for the rest of the process. (2) `NavigateAsync` was awaited AFTER `_stream`
  was assigned but with no try/catch, so a URL the navigation guard refused left a live off-screen
  window and a browser process holding the profile lock, with the handle already published and no path
  that would dispose it. (3) the frame pump's `Task.Run` body was unguarded, so any fault inside it
  (an `EmitAsync` throw, a malformed frame) became an UNOBSERVED task exception — surfacing late,
  through the bootstrap's global handler, with no route back to the page.
- **Fix:** `OnEnded` nulls the handle before emitting; the session is created into a LOCAL, navigated
  inside a try/catch that disposes and reports a structured `STREAM_REFUSED`, and only then published
  to `_stream`; the pump body is wrapped and logs. File: `samples/Shenora.Sample.Desktop/MainForm.cs`.
- **Verify:** compile + review only — like the rest of the sample's session code, exercising these
  paths needs a live browser. `verify` PASSED (476 dotnet + 63 vitest). Stated rather than implied:
  the sample has NOT been run this session.
- **Commit:** _pending (P5.5 H9 batch)_

## 2026-07-30

### Shenora.Tests: one test entered the OS modal size loop and hung, hidden by test parallelism
- **Symptom:** invisible for four batches. `WindowCommandFacadeTests.Drag_and_resize_routes_answer_success(START_RESIZE)`
  took **16.9 s of the suite's 26.8 s** once tests ran serially, and **hung indefinitely** (killed at
  200 s) when run alone with `--filter`. Under the previous collection-level parallelism the wall clock
  stayed at 6 s, so nothing looked wrong and five phase reviews passed it.
- **Root cause:** P5.5 H4.2 made `WinFormsUiDispatcher.Post` run a body **INLINE** when the caller is
  already on the UI thread — correct and deliberate, because `START_DRAG`/`START_RESIZE` hand off to the
  OS move/size loop, which must start while the mouse button is still down. The test creates its form on
  the test thread, so the test thread IS that form's UI thread: `SendMessage(WM_NCLBUTTONDOWN, HTTOPLEFT, …)`
  ran synchronously and entered the modal size loop inside the test, escaping only when unrelated
  concurrent activity happened to interrupt it. The test's own comment — "Deliberately NOT pumped: the
  posted handoff enters the OS move/size loop" — had silently become false at H4.2: the body was no
  longer posted.
- **Fix:** test-only, because the production behaviour is right. The route is now dispatched from a
  worker thread (`Task.Run`), so `InvokeRequired` is true, the body is `BeginInvoke`'d to a queue the
  test never pumps, and the comment is true again. `WindowCommandFacade.Post`'s doc gained the
  consequence this pins down — a transport dispatches on the UI thread, so those two routes' `Done()`
  reaches the page only after the user releases the mouse, and forcing a post to "fix" that would
  reintroduce the H4.2 regression. Files: `tests/…/WebView2/WindowCommandFacadeTests.cs`,
  `src/Shenora.WebView2/WindowCommandFacade.cs` (doc), plus `tests/…/xunit.runner.json`
  (`parallelizeTestCollections: false`) so a future hang cannot hide the same way.
- **Verify:** suite went from 26.8 s (one test 16.9 s) to a steady **9–10 s across three runs**; the
  isolated `--filter` run no longer hangs. Full `verify` PASSED (442 dotnet + 63 vitest).
- **Commit:** _pending (P5.5 H7 batch)_

### Shenora.Tests: four path-containment cases were passing with containment deleted
- **Symptom:** found while reworking the fixture. `EmbeddedResourceProviderTests.File_mode_refuses_paths_that_escape_the_root`
  looked like seven cases guarding the H1 traversal fix; four of them proved nothing.
- **Root cause:** the fixture created exactly one file outside the provider root and named it
  `shenora-outside-marker.txt`, while the theory requested `../secret.txt`, `..\secret.txt`,
  `assets/../../secret.txt` and `../../Windows/win.ini`. Nothing named `secret.txt` existed anywhere, so
  those four resolved to a path that merely did not exist and the provider returned null **whether or
  not containment ran**. Only the three ROOTED cases (`C:/Windows/win.ini` and friends), which land on
  the real `win.ini`, exercised the check. The marker file was decorative — and it was written into
  `%TEMP%` itself, cleaned up in a `finally`.
- **Fix:** the fixture now places the escape target where the requested paths actually point
  (`<temp>/secret.txt` with the provider root at `<temp>/bundle`), and the test asserts the target
  EXISTS as an explicit precondition, so a refusal can only mean containment worked.
  `../../Windows/win.ini` was dropped (unreachable as a real file; `assets/../../secret.txt` already
  covers multi-segment traversal), hence 428 → 427 before the batch's additions. File:
  `tests/…/WebView2/EmbeddedResourceProviderTests.cs`.
- **Verify:** sabotage — with all three containment checks in `WebViewResourceProvider.ResolveContained`
  stubbed out, all 6 cases plus `A_sibling_directory_sharing_the_root_prefix_is_not_inside_the_root`
  FAIL (7 of 18 in that class). Before the rework, four of them would have stayed green. Production file
  restored and confirmed diff-free.
- **Commit:** _pending (P5.5 H7 batch)_

### devtools: the new "test code must not ship" gate failed OPEN twice before it worked
- **Symptom:** found by this batch's own phase review, in code the batch had just added. The new
  `doctor` check meant to stop `src/testing/` (the shared `FakeTransport`) from being published
  reported `ok` in both situations that matter.
- **Root cause:** two independent fail-open bugs. (1) It inspected `dist/testing/`, but `pack` calls
  `doctor` FIRST and only then runs `npm run build`, whose `clean` step deletes `dist/` and rebuilds
  it — so the artifact doctor examined was never the artifact that ships, and on a fresh clone there is
  no `dist/` at all, which skipped the check entirely. This is the same shape as the H5 finding where
  `verify` scanned pre-sync files because `pack` had already run `doctor --fix`. (2) Rewritten to check
  the source instead, it did `tsconfigText.includes('src/testing')` over the WHOLE file — and passed
  because the explanatory COMMENT above the `exclude` array also names that path. The guard was
  satisfied by the prose explaining it.
- **Fix:** the check is source-based and scoped to the `"exclude"` ARRAY via
  `/"exclude"\s*:\s*\[([^\]]*)\]/` (the file is JSONC, so it cannot be `JSON.parse`d), keyed on
  `src/testing/` actually existing on disk so it adapts rather than hardcoding a folder that may be
  renamed. The `dist/testing/` check is retained as belt-and-braces for when a build does precede
  doctor. File: `devtools/dev.mjs`.
- **Verify:** with the `exclude` entry removed AND `dist/` deleted — the exact fresh-clone/pack
  condition both earlier versions missed — `doctor` now exits 1 with the remediation text. Restored,
  and full `verify` PASSED.
- **Commit:** _pending (P5.5 H7 batch)_

### Shenora.WebView2: notifications could stop for the rest of the process
- **Symptom:** found by review. Host→page notifications silently stopped arriving, permanently, with the
  app otherwise working normally. Or, after a renderer crash, one whole batch vanished.
- **Root cause:** the ready gate had exactly one close path and one open path, and they were not
  symmetric. `WebViewIpcBridge` closed it on EVERY `NavigationStarting`, while the client sends `READY`
  once per real page load — so a navigation that never replaced the document (cancelled by an app tap or
  by the navigation policy, or failed before committing) closed the gate on a page that then had no
  second `READY` to give: `TryBuildBatchJson` returned null forever, the queue grew to
  `MaxQueuedNotifications` and started dropping its oldest entries silently. The mirror case: a dead
  renderer left the gate OPEN, so the next 50 ms tick DRAINED the queue into a process that could not
  receive it — and because the drain empties the queue before posting, those notifications were gone.
- **Fix:** close on `ContentLoading` (raised only when a new document actually begins loading, so a
  cancelled or non-committing navigation no longer counts) and on `ProcessFailed`, which the bridge now
  subscribes to itself rather than depending on the host's auto-reload policy being enabled. The residual
  window between `NavigationStarting` and `ContentLoading` is documented at the site as a deliberate
  trade: a flush there reaches the outgoing page, whose listeners are still attached.
- **Verify:** `WebViewIpcBridgeTests` — a closed gate buffers with the queue INTACT and the buffered
  event survives to the next page's handshake; the gate is re-armable repeatedly (reload, crash-reload,
  hot reload) and closing it twice is harmless, since `ContentLoading` and `ProcessFailed` can both fire.
- **Commit:** _pending (P5.5 H3)_

### Shenora.WebView2: one transient environment failure was terminal for the process
- **Symptom:** found by review. After a single failed WebView2 environment creation — a profile lock
  from a zombie process, a runtime update mid-launch — every subsequent attempt failed identically and
  instantly, including the retry the init-timeout's own message tells the user to make. Only restarting
  the app helped.
- **Root cause:** `WebViewEnvironment.GetSharedAsync` cached with `_shared ??= CreateAsync(options)`. A
  faulted `Task` is non-null, so the failure was cached permanently and every later caller awaited the
  same faulted task without WebView2 ever being touched again.
- **Fix:** reuse the cached task only while it is in flight or ran to completion; evict a faulted or
  cancelled one when it is observed, so the next call genuinely retries. This is the shape
  `Shenora.WebView2.Sessions.SessionEnvironmentCache` was deliberately written to in H2 while explicitly
  NOT copying this one — the original is now brought in line, and the cross-reference points both ways.
- **Verify:** compile-and-review; a live browser is needed to fault environment creation, and the
  decision logic is the same one `SessionEnvironmentCacheTests` covers directly.
- **Commit:** _pending (P5.5 H3)_

### Shenora.WebView2: a mistyped resource prefix opened a black window with no error
- **Symptom:** found by review. The app starts, the window is blank, nothing is logged as an error, and
  every resource request 404s. The cause is a `ResourcePrefix` that matches no embedded resource.
- **Root cause:** the prefix is a manifest name, so it depends on MSBuild's name mangling (directory
  separators AND filename dots collapse to `.`), which makes it easy to get wrong and impossible to
  eyeball. `EmbeddedResourceProvider` computed an empty manifest, reported "FILE-BASED" at info level,
  and then answered every request with null. `ResolveStartUrl` throws actionably for the neighbouring
  mistake (missing URL configuration), so the asymmetry was the bug.
- **Fix:** and the PLACEMENT is the interesting part. Throwing from the provider's constructor — the
  obvious fix, and what the review asked for — is wrong: a provider with nothing to serve is legitimate
  when the page loads from a dev URL, which is the normal state of a fresh clone whose bundle has not
  been built (the sample's csproj documents exactly that). So the provider REPORTS the condition (new
  `CanServe`, plus a log notice naming the bad prefix, the assembly, and the manifest prefixes that DO
  exist) and `WebViewHost.AssertBundleServable` throws — it is the only place that knows the bundle is
  the start document. The probe is `IWebViewResourceProvider.Exists("index.html")`, which also catches a
  present-but-incomplete bundle and gives that member the consumer H6 was going to delete it for.
- **Verify:** `WebViewHostTests` — a bundle start document with no index throws actionably; a servable
  one passes; and a dev URL or a `ProductionUrl` pointing elsewhere never consults the provider (the
  three cases that make constructor-throwing wrong). `EmbeddedResourceProviderTests` asserts the notice
  names the prefix and the available ones, and that a file directory alone is enough. Note the old test
  `No_matching_resources_and_no_directory_serves_nothing` asserted the DEFECT and was rewritten.
- **Commit:** _pending (P5.5 H3)_

### @shenora/react: the client robustness tail (seven defects, two silent by construction)
- **Symptom:** found by review. An uncaught page error from one host message; a bridge reporting itself
  available while rejecting everything; a hung caller with no diagnostics; every request from a service
  singleton rejecting "Bridge disposed" for the rest of the session; a drop zone that silently never
  existed; ~180 IPC round-trips per window drag; and a recoverable refetch error blanking the screen.
- **Root cause:** seven independent mechanisms. The two that were silent by construction:
  `JSON.parse('null')` returns `null` — valid JSON — so `parsed.category` threw a `TypeError` out of the
  transport listener, where nothing catches it (the other primitives never threw, since property access
  on them just yields `undefined`; null and only null did). And `BaseModuleService`'s
  `bridge: ShenoraBridge = getBridge()` is a constructor default, evaluated at CONSTRUCTION, while
  `configureBridge` disposes the bridge it replaces — so any service constructed before startup
  configuration held a corpse. `useDropZone`'s effect keyed on `targetRef`, a stable object: the effect
  ran once, and a null `current` on that run meant it bailed out and never re-ran. `isAvailable` checked
  only the transport. The `fallback` branch returned `Promise.resolve(...)` with no timeout race.
  `useWindowMaximized` bound `resize` straight to a full IPC query. `useShenoraQuery`'s error handler set
  `data: undefined` unconditionally.
- **Fix:** guard the parse to non-null objects; `isAvailable` includes `!disposed`; race the fallback
  against the timeout but ONLY when it returns a thenable (a plain value has already settled and must not
  be made async); `BaseModuleService` resolves through a `protected get bridge` so subclasses keep using
  `this.bridge` and an explicit bridge is still honoured; `useDropZone` mirrors the ref into `useState`
  via a deliberately dep-array-less effect and keys its effects on the ELEMENT (a ref mutation triggers
  no render, so there is nothing a dep array could observe — this is why it isn't just a deps fix); the
  resize query is debounced with a `cancel` on teardown; the query hook keeps previous data alongside the
  error. New non-exported `internal.ts` holds the now twice-needed `debounce`/`randomId`.
- **Verify:** +10 vitest (49 total), 404 dotnet, `verify` PASSED. Including: a bare-JSON-value message of
  each primitive shape; `isAvailable` after dispose; a never-settling fallback timing out and a
  synchronous one still resolving; a service constructed BEFORE `configureBridge` reaching the new
  default, and an explicit bridge still winning; a target that mounts after the first effect registering,
  and its unmount unregistering; 50 resize events coalescing to one query; a failed refetch keeping data.
  One test-only trap worth noting: the new `afterEach` bridge reset rejects still-pending requests, so
  fire-and-forget calls in those tests need a `.catch` or vitest fails the run on an unhandled rejection.
- **Commit:** _pending (P5.5 H2 client tail)_

### Shenora.WinForms: the shell robustness tail (nine defects under everything else)
- **Symptom:** found by review. All of them present as something other than a window bug: a resident
  process with a tray icon and a window that can never load; a stale WebView2 profile lock that hangs the
  NEXT launch; a "maximized" window at the wrong size after a monitor move, or restoring somewhere the
  user cannot reach it; a secondary-window name permanently "already open"; a single-instance mutex still
  held after shutdown; two stacked crash dialogs, or unboundedly many; `SetTextAsync("")` throwing.
- **Root cause:** nine independent mechanisms; the ones worth naming here —
  `MessageBox.Show` runs its own modal loop, so it PUMPS: a recurring UI-thread exception is dispatched
  again while the dialog is up, re-entering the handler. `FormClosed` fires while `Application.Run` has
  NOT returned (the form is still disposing children), so removing the registry entry there let a
  `Dispose` waiting on "no windows left" return mid-teardown. An OS mutex is per-thread REENTRANT, so a
  second `TryAcquire` on the same thread took a second handle and reported success even though this
  process was the owner — leaving `Dispose` able to release only one. A manual maximize is a one-shot
  `SetWindowPos` in physical px, so nothing kept it true across a DPI/resolution change, and the saved
  restore rect could point at a monitor that no longer exists. And a missing `[STAThread]` surfaced not
  as an error but as WinForms' own BLOCKING modal dialog inside handle creation.
- **Fix:** see the CHANGELOG entry for the full list. Two were judgement calls rather than mechanical
  fixes. The form-level `AllowDrop`/`DragOver` on `OptimizedForm` was REMOVED, not option-gated, because
  its justification was false: OLE registers drop targets per HWND and `DropZoneOverlay` registers
  itself, so nothing ever consumed the form's drag events — the flag only forced OLE/STA on every
  consumer of the base class and showed a copy cursor for a drop it then discarded (no `DragDrop`
  handler). `TrayIcon`'s wrong comment was fixed as DOCUMENTATION: `CloseReason` cannot distinguish the
  user's X from a programmatic `Close()`, so the fix is telling adopters to use
  `ExitApplication()`/`Application.Exit()`, stated on the `CloseToTray` option where the choice is made.
- **Verify:** 10 new tests, 404 dotnet + 39 vitest, `verify` PASSED. Notably
  `WinFormsBootstrapTests.Only_one_crash_dialog_is_shown_at_a_time` drives the re-entrancy through a new
  internal `ShowDialogOverride` seam (a real MessageBox would hang the suite, and the re-entrancy is the
  whole invariant), `A_recurring_exception_still_reaches_the_app_logger` pins that suppression applies to
  the DIALOG only, `SecondaryWindowsTests.The_entry_survives_until_the_pump_has_finished_tearing_down`
  blocks inside `FormClosed` to stand in for a slow WebView2 child, and
  `OptimizedFormTests.Restoring_from_an_unreachable_saved_rect_lands_somewhere_visible` maximizes from
  an off-virtual-desktop rect. NOT tested, deliberately: the clipboard fix — a test would clobber the
  developer's real clipboard, and the change is `Clipboard.Clear()` instead of a throwing `SetText("")`.
- **Commit:** _pending (P5.5 H2 WinForms tail)_

### Kit-wide: the last unguarded app callbacks, and a data race in the session controller's taps
- **Symptom:** found by review; the closing half of H2's "no app callback runs unguarded" item. Three
  distinct failures, all from app-supplied delegates invoked where nothing can catch them: a throwing
  `OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed` crashed the UI thread AND left the
  WebView2 event unanswered; a throwing `WndProcHook` surfaced as WinForms' own BLOCKING modal dialog
  mid-message-dispatch, on a window that may not be visible yet; and `SessionController`'s driver taps
  could throw or deliver a torn handler list under concurrent registration.
- **Root cause:** the pattern was guarded per-site, by memory, so each new site re-opened it. For the
  taps specifically: the four collections were plain `List<T>`, appended from the driver's thread (a
  driver continuation resumes wherever the thread pool puts it) while the WebView2 handlers read them
  on the UI thread. `List<T>.ToArray()` reads `_size` and then `Array.Copy`s the backing store, so an
  `Add` that grows the array in between throws or copies a torn view, and two concurrent `Add`s corrupt
  the list. The `.ToArray()` at the read site looked like the fix for exactly this and is not one.
- **Fix:** one owner — `Shenora.Core.AppCallback` (`Run`/`RunOrDefault`), public because three packages
  consume it and a `ProjectReference` grants no internal access (D19/D20 placement law). Routed through
  it: the three `WebViewHost` policy hooks, which now also FALL BACK to the kit's built-in policy rather
  than leaving the event unanswered; `WndProcHook`, where a throw reads as "did not handle this
  message"; `OnClientReady`; `SessionController.Fan`; and `SessionLog`, which stopped carrying its own
  copy of the policy. Every `Action<string>? Log` site in `WebViewHost` and `WebViewIpcBridge` became a
  guarded, LAZY `Log(Func<string>)` — lazy so the guard covers BUILDING the message too, since several
  read WebView2/COM properties that throw once the underlying object is gone and that read would
  otherwise happen at the call site, outside the guard. The taps became copy-on-write `volatile` arrays
  published under a lock, so readers need no lock and every reader sees one immutable snapshot.
- **Verify:** `AppCallbackTests` (7 cases incl. a throwing error sink, and that a null callback is a
  CALLER bug that still throws — the guard covers the app's mistakes, not the kit's) and
  `OptimizedFormTests.A_throwing_WndProcHook_does_not_take_the_window_down`, which realizes a real
  window through a hook that throws on every message and asserts the window still responds. 394 dotnet
  + 39 vitest, `verify` PASSED. `WebViewHost`'s hooks and `SessionController`'s taps need a live browser
  core to construct, so those sites are compile-and-review verified; the sample e2e is their subject.
- **Commit:** _pending (P5.5 H2 callback sweep)_

### Shenora.WebView2.Sessions: a throwing app logger could hang a lease and leak a capacity permit
- **Symptom:** found by the H2 sessions batch's own phase review, in code that batch had just written.
  No observed incident. An app `ILogger` that throws — a file sink whose handle went away, a
  scope-captured provider used after shutdown — could permanently hang a `LeaseAsync` caller, leak a
  capacity permit for the process lifetime, or crash the UI thread, depending on which log line hit.
- **Root cause:** an `ILogger` is APP CODE, so the package's own rule (no app-supplied callback runs
  unguarded inside a WebView2/WinForms event handler or a posted UI-thread body) applies to it — and the
  logging added in P5.5 H4.7 invoked it bare at all eight sites. Three of those turn a log line into a
  real failure: inside the instance-creation `catch` the throw escaped BEFORE `tcs.TrySetException`, so
  the lease's task never completed (a hung caller still holding its permit); inside the return-to-pool
  body it escaped before `_capacity.Release()`; and inside `NewWindowRequested`/`PermissionRequested`/
  `ProcessFailed` there is no caller on the stack at all, so it is an unhandled UI-thread exception.
  Note this is the same finding class — "an app-supplied callback running unguarded inside a UI-thread
  event handler or timer" — that the phase-review checklist was extended with after the first full
  review; it caught it here on the first pass.
- **Fix:** new internal `SessionLog.Try(ILogger?, Action<ILogger>)` — the one place that knows a lost
  log line must never become a lost session — used at all eight sites in `RenderSession`,
  `RenderSessionPool` and `SessionBrowser`. In `Return` the message's reason string is also computed
  before the call so the interpolation can't throw inside the guarded body either.
- **Verify:** `RenderSessionPoolTests.A_throwing_app_logger_cannot_hang_a_lease_or_leak_a_permit` — a
  logger that throws on every call, driven down the discard path (the one that logs); the lease
  completes, the instance is discarded, and the permit comes back. 382 dotnet + 39 vitest, `verify`
  PASSED.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a wedged page permanently poisoned the render pool
- **Symptom:** found by the first full P0–P5 review, then re-verified. One page blocked in its own
  script thread (a spin loop) made every later lease useless: with `Capacity=2`, two such pages
  answered `RENDER_BUSY` for the rest of the process lifetime.
- **Root cause:** TWO mechanisms, and fixing only the first (H4.2) was not enough.
  (1) `RenderSession.OnUiAsync` accepted a `CancellationToken`, checked it once inside the posted
  delegate, then awaited the body with no way to observe it again — so the caller could not escape, the
  lease never returned, and the capacity permit was gone. H4.2 closed this by routing the marshal
  through `WinFormsUiDispatcher`, whose `InvokeAsync` observes the token via `WaitAsync`.
  (2) But `WaitAsync` hands the CALLER back; it cannot kill the outstanding operation. The wedged
  instance was still returned to the pool by `DisposeAsync`, reset (see the next entry — the reset
  reported success even when it timed out), and re-leased. So the pool healed its accounting and kept
  handing out the same dead browser. Compounding both: no operation had a time cap at all, and every
  parameterless overload passes `CancellationToken.None`, so the default caller had no escape either.
- **Fix:** `RenderSession.RunBoundedAsync` wraps every marshalled op in a linked CTS with
  `CancelAfter(OpTimeout)` (new option, 60 s) and poisons `PoolInstance` when the body did not complete,
  which makes `RenderSessionPool.Return` discard it instead of re-pooling. Completion is TRACKED via a
  flag set in the body's `finally`, not inferred from the exception type: a body that ran and threw (a
  rejected URL, a guard refusal) leaves a reusable instance, and discarding it would cost a browser
  startup on every ordinary error. An expiry becomes `TimeoutException`; a caller's own
  `OperationCanceledException` is never rewritten, though it DOES poison — deliberately, since the
  caller walked away while the renderer may still be mid-script. `NavigateAsync`'s hardcoded 30 s cap
  became the `NavigationTimeout` option so the two budgets are coherent.
- **Verify:** `RenderSessionPoolTests` — a new `StalledAnchor` (a handle realized on its own thread that
  NEVER pumps) makes "the operation never completes" deterministic; note this detail, because an anchor
  on the test thread runs bodies INLINE via the dispatcher's correct fast path and would have proven
  nothing. Tests: an abandoned op throws `TimeoutException` and poisons; a cancelled caller gets
  `OperationCanceledException` (not a timeout) and also poisons; an ordinary body failure does NOT
  poison and is re-pooled; a poisoned instance is discarded without even attempting a reset. 381 dotnet
  + 39 vitest, `verify` PASSED.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a session that could not be reset was re-pooled forever
- **Symptom:** found by review. A pooled instance whose renderer stopped answering was recycled
  indefinitely; every lease that drew it burned the full navigation cap before failing.
- **Root cause:** `RenderSessionPool.ResetToBlankAsync` awaited the blank navigation with
  `WaitAsync(5s)` inside a `try`/`catch` that swallowed the outcome and then `return true`
  unconditionally. Its own comment defended this — "slow blank nav — re-pool anyway; the next lease
  navigates away regardless" — which is the actual error: a renderer that cannot complete a navigation
  to `about:blank` cannot complete the next lease's navigation either. So the documented "a failed
  reset DISCARDS the instance" invariant was only reachable if the navigation THREW. The test pinning
  that invariant drove `ResetOverride`, never the real path, which is why it passed five phase reviews.
- **Fix:** the wait's decision moved to `internal static AwaitResetNavigationAsync(Task, TimeSpan)`,
  which returns false on timeout OR fault; `ResetToBlankAsync` returns it. The 5 s budget became the
  validated `ResetTimeout` option. `Return`'s discard log now names WHICH invariant fired (a dead
  renderer vs a reset the renderer never answered) — lumping them together is what made a wedged pool
  opaque.
- **Verify:** `RenderSessionPoolTests` — a theory over the REAL helper (a never-completing navigation →
  false, a completed one → true) plus a faulted navigation → false, and the existing discard test still
  pins the consequence.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a cancelled session start left a live browser holding the profile lock
- **Symptom:** found by review. A cancelled `LeaseAsync`/`StartAsync`, or a pool disposed while an
  instance was being created, returned/threw to the caller while a realized off-screen window and its
  browser process stayed alive — holding the profile's folder lock with no owner left to dispose it. For
  co-browse a screencast could additionally start writing frames into a channel no reader would ever be
  handed.
- **Root cause:** both call sites checked `cancellationToken.IsCancellationRequested` exactly once, at
  the TOP of the marshalled body — before the multi-second `SessionBrowser.InitializeAsync` (browser
  process spawn + profile attach + settings). Nothing re-checked afterwards, so anything cancelled
  during the expensive part still published a fully live instance. `LeaseAsync` also built a linked
  token (caller + pool dispose) for the capacity wait but then passed the RAW caller token to the
  instance factory, so pool disposal could not cancel a creation at all.
- **Fix:** `RenderSessionPool.CreateInstanceAsync` re-checks after init and runs the same cleanup as the
  failure path — extracted to a shared `TearDown()` local, which also stopped being silent (a leaked
  control keeps the profile locked, the exact symptom the init-timeout message tries to explain).
  `CoBrowseSession.StartAsync` re-checks twice: after init, and again before publishing, since past that
  line the caller owns teardown. `LeaseAsync` now passes `linked.Token` to the factory.
- **Verify:** `RenderSessionPoolTests.Dispose_cancels_an_in_flight_instance_creation` — a factory parked
  on `Task.Delay(Infinite, ct)` proves the token it receives is the linked one, the lease throws
  `OperationCanceledException`, and the capacity permit comes back. The post-init re-checks need a real
  browser to exercise and are covered by the sample e2e, not a unit test.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: every retry against a locked profile orphaned another browser process
- **Symptom:** found by review (carried over from H4.4). Repeated leases against a profile folder held
  by a zombie `msedgewebview2` each added another browser process queued on that same lock — growing
  the very lock the init-timeout's error message blames. Separately, a pool of N instances paid for N
  environments on one profile.
- **Root cause:** `SessionBrowser.InitializeAsync` called `CoreWebView2Environment.CreateAsync` per
  instance, guarded by `.WaitAsync(InitTimeout)`. `WaitAsync` abandons the AWAIT, not the underlying
  operation — so the timed-out creation kept running and the next attempt started an additional one.
- **Fix:** new internal `SessionEnvironmentCache`, held by `RenderSessionPool` and passed to an internal
  `InitializeAsync` overload (the public signature is unchanged). It reuses an IN-FLIGHT creation, which
  is the anti-orphan half, and a completed one, which is the one-per-profile half. Two shape decisions
  are load-bearing: (a) it is **owner-scoped, not static/profile-keyed** — a live environment keeps its
  profile's browser process and therefore the folder lock alive, so a process-lifetime cache would have
  made `LoginWindow.ClearProfile` fail every time rather than only while a window is open; a login
  window opens one profile once and gains nothing from caching. Owner scoping also makes it
  single-threaded by construction, which matters because `CoreWebView2Environment` is thread-affine.
  (b) A faulted or cancelled creation is **not** cached — the trap `Shenora.WebView2`'s own
  `WebViewEnvironment` still has (`TASKS.md` H3), where one transient failure is terminal for the
  process. `RenderSessionPool.Dispose` clears the cache.
- **Verify:** `SessionEnvironmentCacheTests` — in-flight reuse (creation delegate called once),
  completed reuse, faulted and cancelled both retried, and `Clear` releasing. Real environment creation
  needs a browser process, so the cache's DECISIONS are tested through the creation delegate.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2.Sessions: a co-browse frame stream could stop silently, and a late tap could read another lease
- **Symptom:** two review findings in the same area, both silent by construction. (1) A co-browse
  stream that freezes after an arbitrary GC, with no error anywhere — the consumer just sees a page that
  went still. (2) A `RenderSession` interceptor installed after the lease returned received the NEXT
  lease's JSON API responses and posted messages.
- **Root cause:** (1) `CoBrowseSession.StartAsync` kept `GetDevToolsProtocolEventReceiver(...)` in a
  local and subscribed to it there. Nothing referenced the receiver once the method returned, so the
  subscription's survival depended on the WebView2 SDK caching it internally — unspecified behaviour —
  and `DisposeAsync` never detached it either. (2) `OnNetwork`/`OnMessage` were the only public
  `RenderSession` members with no `_disposed` check, and the only two that install a PERSISTENT tap;
  after `DisposeAsync` the instance is back in the pool and handed to another lease, so a stale
  reference or a continuation outliving its `await using` produced cross-lease disclosure — in a package
  whose whole story is profile isolation.
- **Fix:** (1) the receiver and its handler are now fields (`_frameReceiver`/`_onFrame`), passed into
  the constructor, and `DisposeAsync` detaches before stopping the screencast. (2) both members call
  `ThrowIfDisposed()` (the same `ObjectDisposedException` every other member already throws via
  `OnUiAsync` — failing loudly, not silently no-op'ing) and the marshalled subscribe body re-checks
  `_disposed`, closing the check-then-post race.
- **Verify:** `RenderSessionPoolTests.Interceptors_cannot_be_installed_after_the_lease_is_returned`.
  The receiver rooting is a lifetime fix with no unit-testable seam — it is compile-and-review verified,
  and the co-browse sample seam (H9.5) is where it gets exercised live.
- **Commit:** _pending (P5.5 H2 sessions batch)_

### Shenora.WebView2: file-mode frontend serving read any file the process could
- **Symptom:** no observed incident — found by the first full P0–P5 review. A page (or any script in
  it) could request `https://<virtualHost>/%2e%2e%2f%2e%2e%2fWindows%2fwin.ini`, or a rooted
  `/C:%2fUsers%2f…`, and receive the file's contents. Responses carry
  `Access-Control-Allow-Origin: *`, so the body was readable by page script.
- **Root cause:** `WebViewHost.ServeVirtualHost` unescapes the request path before calling the
  provider — deliberately, so bundle filenames with spaces or CJK characters resolve — and
  `EmbeddedResourceProvider`'s `Normalize` only replaced backslashes and trimmed leading slashes. No
  `..` rejection and no containment assertion. Worse for the rooted case:
  `Path.Combine(root, "C:\…")` DISCARDS its first argument when the second is rooted, returning the
  attacker's absolute path verbatim. Embedded mode was safe only incidentally (`../` maps to a
  manifest name that doesn't exist). Live wherever `PreferFiles` is set — the sample derives it from
  `IsDevelopment`, so every dev session.
- **Fix:** `EmbeddedResourceProvider.ResolveContained` rejects rooted paths and `..` segments, then
  asserts `Path.GetFullPath(combined)` still sits under `Path.GetFullPath(root)` + separator (so a
  sibling directory sharing the root's prefix can't pass either). Applied in BOTH `GetResourceStream`
  and `Exists` — `Exists` alone would have leaked existence.
- **Verify:** `EmbeddedResourceProviderTests` — 7 escaping paths (traversal in both separator
  spellings, nested, and three rooted forms) return null/false while a legitimate file still serves;
  3 legitimate paths with spaces, CJK characters and nesting still serve (the unescape exists for
  those); plus the sibling-prefix case. 346 dotnet + 39 vitest green, `verify` PASSED.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2: an unserializable notification crashed the UI thread and lost its batch
- **Symptom:** found by review. One app event whose payload can't serialize — a cyclic object graph
  (ORM parent/child), a `Type`/delegate member, a throwing getter — produced an unhandled UI-thread
  exception (a modal crash dialog under the family bootstrap, recurring on the 50 ms timer) and the
  whole pending notification batch vanished.
- **Root cause:** `WebViewIpcBridge.TryBuildBatchJson` dequeues every pending notification and THEN
  calls `IpcJson.Serialize` on the batch, with no try/catch anywhere on the path from `Timer.Tick` →
  `Flush` → here. Because the queue was already drained, the throw lost the good notifications too.
  The INCOMING path guards this exact case with an explicit comment; the outgoing twin never did.
- **Fix:** serialize per notification and keep only the ones that succeed (so a single bad event
  can't take its batch down), logging the offender's module/type but never its payload; plus a
  catch-all around `Flush` since it runs on a timer with no caller to observe it.
- **Verify:** `WebViewIpcBridgeTests` — a batch mixing two good notifications with a cyclic payload
  and a throwing getter yields a 2-item batch in order and drains the queue; an all-bad batch yields
  no batch rather than throwing.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2.Sessions: `NavigationGuard` did not survive a redirect
- **Symptom:** found by review. A data-driven URL that passed the app's SSRF guard could answer
  `302 → http://127.0.0.1:8080/admin`; WebView2 followed it and `GetHtmlAsync` handed the caller the
  loopback page's DOM. The guard's own XML doc sold it as the app's SSRF/allowlist policy.
- **Root cause:** the guard was consulted only inside the explicit `NavigateAsync` call. The
  package's single `NavigationStarting` subscription (in `LoginWindowController`) only fanned out to
  app taps — it never consulted the guard or set `e.Cancel`.
- **Fix:** the pool records the host the guard approved (`PoolInstance.ApprovedHost`, cleared on
  return-to-pool so a recycled instance can't inherit it) and cancels unvetted CROSS-HOST navigation
  at `NavigationStarting`. Scope stated honestly in the option's docs rather than over-promised:
  `CoreWebView2NavigationStartingEventArgs` exposes NO deferral (confirmed by compiler error while
  implementing the first attempt), so an async guard cannot be awaited in that event and blocking on
  it would deadlock the UI thread — a synchronous cross-host rule is the most the event can enforce.
  `SessionBrowserOptions.RequestFilter` is synchronous and already wired with
  `WebResourceContext.All`, so it remains the seam for full redirect/subresource policy; both options
  now document the division. Deliberately NOT applied to `LoginWindow`: interactive OAuth sign-in
  legitimately redirects across hosts, and a human-driven login window is not a data-driven SSRF
  surface.
- **Verify:** builds + full suite green; the live redirect path needs a real server and stays e2e/
  manual per `docs/REVIEW-GUIDE.md` §6.
- **Commit:** _pending (P5.5 H1 batch)_

### Shenora.WebView2.Sessions: `ClearProfile` was a recursive delete on an unvalidated path
- **Symptom:** found by review. `LoginWindow.ClearProfile` runs
  `Directory.Delete(recursive: true)` on a caller-composed path, and profile paths are built from
  data-driven provider/account identifiers — so a `..` segment could aim the delete outside the
  sessions root, or collapse two accounts onto one cookie jar, defeating the isolation the same
  options doc calls a security boundary.
- **Root cause:** no validation, and no supported way to build the path safely — the library
  documented the boundary but left composition to the caller.
- **Fix:** `ClearProfile` refuses paths containing `..` segments, and a new public
  `LoginWindow.ComposeProfileDirectory(root, params segments)` validates each segment (no separators,
  no `.`/`..`, no drive qualifier, no invalid file-name characters, no Windows reserved device names)
  and asserts the composed path stays under the root.
- **Verify:** `LoginWindowTests` — 4 traversing paths throw; composition builds a contained path
  usable by `ClearProfile`; 9 unsafe segments throw; two accounts get distinct directories.
- **Commit:** _pending (P5.5 H1 batch)_

### devtools: the sensitive guard failed OPEN and the verify gate never compiled the samples
- **Symptom:** found by review. Two gates reported success while covering less than documented:
  `dev.mjs verify` never compiled `samples/` (so the reference composition and e2e subject could be
  red while verify was green), and `check-sensitive` degraded to two structural patterns whenever
  `local/sensitive-patterns.txt` was absent — which is every fresh clone and every CI run, i.e. the
  private-name half of the guard never ran in the release gate. `dev.mjs test <typo>` exited 0 having
  run nothing.
- **Root cause:** `Shenora.slnx` carried an EMPTY `<Folder Name="/samples/" />` (and omitted
  `Shenora.Core`) while `dev.mjs build` builds only the solution; the scanner's missing-pattern-file
  branch printed a notice and continued; the `test` dispatcher compared its argument against three
  values with no else branch. Compounding: `-clp:ErrorsOnly` plus no `TreatWarningsAsErrors` made
  warnings both non-fatal and invisible.
- **Fix:** samples + Core added to the solution; scanner exits non-zero without the pattern file
  (`--allow-builtins-only` / `SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1` to opt in, which the release
  workflow now does explicitly), also scans file paths, includes renamed/copied staged files, and a
  new `commit-msg` hook scans commit messages; `test` fails on an unknown target; warnings are errors
  for `src/`; `verify` additionally type-checks the sample web app and runs `doctor`.
- **Verify:** `dotnet build Shenora.slnx` succeeded with 0 warnings / 0 errors WITH the sample newly
  compiled and warnings-as-errors on; the four scanner behaviours exercised by hand (clean message →
  0, leaking message → 1, missing pattern file → 1, `--allow-builtins-only` → 0); `verify` PASSED
  showing the two new steps.
- **Commit:** _pending (P5.5 H5 batch)_

### Shenora.WebView2.Sessions: `SemaphoreSlim.Dispose()` wedged a just-cancelled waiter
- **Symptom:** a new P5 test (`RenderSessionPoolTests.Dispose_cancels_a_queued_lease…`) hung
  forever — `dotnet test` never printed a summary and hit the 10-minute harness timeout. The
  pool's `Dispose()` was supposed to cancel a lease queued on the capacity semaphore so a wedged
  wire request settles instead of hanging; the awaiting task never faulted.
- **Root cause:** `RenderSessionPool.Dispose()` cancelled the dispose `CancellationTokenSource`
  (which, linked into each `LeaseAsync`'s `WaitAsync`, should cancel a queued waiter) and then
  immediately called `_capacity.Dispose()`. Disposing a `SemaphoreSlim` while a waiter is still
  unwinding its just-fired cancellation races the waiter's internal queue-removal and can leave
  its task permanently incomplete. Introduced in this same P5 phase-review fix (not a regression
  of shipped code) — the cancel was correct, the adjacent `Dispose()` defeated it.
- **Fix:** stop disposing the semaphore (and the CTS) in `RenderSessionPool.Dispose()` — a
  `SemaphoreSlim` only needs disposal if `AvailableWaitHandle` was touched (it never is here), so
  skipping it is safe and removes the race; the cancel alone wakes queued waiters cleanly. The
  regression test now also bounds its wait with `Task.WaitAsync(5s)` so a future re-break FAILS
  fast instead of stalling the suite. File: `src/Shenora.WebView2.Sessions/RenderSessionPool.cs`.
- **Verify:** the isolated test went from a >10-min hang to passing in ~0.3 s; full `verify`
  green (318 dotnet + 39 vitest).
- **Commit:** 4ebb8e0

### @shenora/react packaging: the published tarball was unusable under native Node ESM
- **Symptom:** `npm install <tarball>` then `import('@shenora/react')` in plain Node failed with
  `ERR_MODULE_NOT_FOUND … dist/types` — the package worked in every bundler (Vite, vitest) but
  not under Node's own ESM loader. Found by the P1.1 local-feed consumption smoke, which exists
  exactly to catch what the bundler-based dev loop can't.
- **Root cause:** the sources used extensionless relative imports (`from './types'`), and the
  tsconfig's `moduleResolution: "bundler"` neither requires nor emits extensions — so the
  compiled `dist/*.js` carried extensionless specifiers, which bundlers resolve but native Node
  ESM (and any strict ESM tooling) rejects. Not a regression — the gap existed since the first
  real source files; the sample app masked it because Vite bundles the package.
- **Fix:** explicit `.js` extensions on every relative import/export specifier in
  `src/Shenora.React/src/*.ts` (TS resolves `.js` → `.ts` at build time), and
  `module`/`moduleResolution` switched to `NodeNext` in `tsconfig.json` so a missing extension
  is now a BUILD error — prevention, not just history. Consumption recipe recorded in
  `docs/RELEASING.md`.
- **Verify:** rebuilt + re-packed; the scratch npm consumer (`devtools/_p11-consumer/npm`)
  imports the tarball under plain Node and resolves every export; full `verify` green
  (273 dotnet + 39 vitest); the NuGet side of the same smoke pins `[0.1.0]` from the local feed
  and runs a live dispatch round-trip.
- **Commit:** `0776f37`
