# Publishing `Shenora.iOS` — release-pipeline proposal

**Status: DRAFT FOR REVIEW. Nothing in `.github/workflows/` has been changed.** The release pipeline
is the one part of this repo that has already cost a version (0.2.0), and `docs/RELEASING.md` treats
cutting a release as a deliberate manual act. Rewiring publish on an assistant's judgement is the
wrong call; rewiring it on a reviewed diff is fine.

Retire this file once the change lands, the way the 0.2.0 cleanup retired its implemented design docs.

## What changed since the first draft — the job got much smaller

The first version of this document assumed one multi-targeted `Shenora.Maui` package, which forced a
macOS job that could build **both** mobile platforms and a step that swapped out a half-built package
produced on Windows. Owner's call (2026-08-02) replaced that with **one package per platform**, and
that decision deleted most of the pipeline problem:

| | one multi-TFM package | two platform packages |
|---|---|---|
| Windows job | packs a HALF-complete `Shenora.Maui`, which must then be discarded | packs `Shenora.Android` completely |
| macOS job | needs `maui-android` **and** `maui-ios`, plus a JDK and the Android SDK | needs `maui-ios` only |
| publish | must REPLACE a package with the same id and version | additive — nothing to overwrite |
| failure mode | shipping a package missing a face, silently | a missing package, which is loud |

The dangerous case — an artifact that looks finished and is not — no longer exists. Every package
now either builds completely on a given host or cannot build there at all.

## Measured, not assumed

| Face | Packed on | `lib/` folder |
|---|---|---|
| Android | Windows | `lib/net10.0-android36.0/…` (+ `.xml`) |
| iOS | macOS | `lib/net10.0-ios26.0/…` (+ `.xml`) |

Both were packed and their layouts read out of a real `.nupkg`. The macOS nuspec is complete on its
own — dependencies on `Shenora.Core`/`Shenora.Ipc` at the matching version, `Microsoft.Maui.Controls`,
README, licence, repository metadata. A plain `dotnet pack` on macOS needs no help.

⚠ Those folder names carry the workload's **TargetPlatformVersion** (`android36.0`, `ios26.0`), not
`SupportedOSPlatformVersion` (21.0 / 15.0). That is what a consuming project must be compatible with,
and it belongs in `ADOPTION.md` when this ships.

## Already landed, so the pipeline diff is small

`dev.mjs pack` selects by host instead of pretending:

- `project.config.mjs` has `macOnlyPackableProjects: ['src/Shenora.iOS']` — note `Shenora.Android` is
  **not** in it, because Windows packs it completely.
- A default `pack` produces the five desktop packages + `Shenora.Android` + the npm tarball, and
  prints what it skipped and why.
- `pack --mac` packs exactly the macOS-only set and refuses to run elsewhere. It skips the npm
  tarball, so two passes cannot both emit one and leave publish guessing which is current.

## Shape — two packs, one publish

```
version  (ubuntu)  ── computes the version string. No writes, no side effects.
   │
   ├─► ios-pack (macOS)    ── dev.mjs pack --mac  → uploads Shenora.iOS.*
   │
   └─► publish  (windows)  ── dev.mjs pack        → downloads the iOS artifact into
                               publish/packages, pushes the union
```

### 1. New job — `version`

Moves the bump arithmetic out of `publish` so two jobs cannot disagree about what is being released.

```yaml
  version:
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.v.outputs.version }}
      tag: ${{ steps.v.outputs.tag }}
    defaults:
      run:
        shell: pwsh
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - id: v
        run: |
          $current = node -e "import('./devtools/project.config.mjs').then(m => console.log(m.default.version))"
          $req = '${{ inputs.version }}'.Trim()
          if ($req) { $final = $req }
          else {
            $parts = $current.Split('.')
            switch ('${{ inputs.bump }}') {
              'none'  { $final = $current }
              'patch' { $parts[2] = [int]$parts[2] + 1; $final = $parts -join '.' }
              'minor' { $parts[1] = [int]$parts[1] + 1; $parts[2] = 0; $final = $parts -join '.' }
              'major' { $parts[0] = [int]$parts[0] + 1; $parts[1] = 0; $parts[2] = 0; $final = $parts -join '.' }
            }
          }
          if ($final -notmatch '^\d+\.\d+\.\d+$') { throw "bad version: $final" }
          "version=$final" >> $env:GITHUB_OUTPUT
          "tag=v$final" >> $env:GITHUB_OUTPUT
```

### 2. New job — `ios-pack`

No JDK, no Android SDK, no `maui-android` — the split removed all of it.

```yaml
  ios-pack:
    needs: version
    runs-on: macos-15
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - uses: actions/setup-node@v4
        with:
          node-version: 22          # project.config.mjs / dev.mjs are node
      - run: dotnet workload install maui-ios
      - run: node devtools/dev.mjs pack --mac
      - uses: actions/upload-artifact@v4
        with:
          name: shenora-ios-nupkg
          path: publish/packages/Shenora.iOS.*
```

> **Known gap, not hidden:** `pack --mac` takes its version from `project.config.mjs` (i.e.
> `VersionPrefix`), so this job would pack the CURRENT version rather than the one being released.
> It needs either a `-p:Version` passthrough in `dev.mjs pack` (a two-line change) or a `VersionPrefix`
> rewrite in this job before the pack step. Flagging it rather than pretending the job is drop-in.

### 3. Changes to `publish`

```yaml
  publish:
    needs: [version, ios-pack]
    runs-on: windows-latest
```

In **Determine version**, replace the bump arithmetic with the passed value — everything after it
(the `VersionPrefix` rewrite, the CHANGELOG stamp, `doctor --fix`, the `git status` check) is untouched:

```pwsh
          $final = '${{ needs.version.outputs.version }}'
          if ($final -notmatch '^\d+\.\d+\.\d+$') { throw "bad version: $final" }
```

Then one new step, **after `Pack` and before `Push to NuGet`**. Purely additive — `pack` never
produces a `Shenora.iOS` on Windows, so there is nothing to overwrite:

```yaml
      - uses: actions/download-artifact@v4
        with:
          name: shenora-ios-nupkg
          path: publish/packages

      - name: Confirm the iOS package arrived
        run: |
          # `pack` SKIPS Shenora.iOS on Windows, so a lost artifact would quietly ship a release with
          # no iOS package at all — a silent omission, which is worse than a failure.
          if (-not (Get-ChildItem 'publish/packages' -Filter 'Shenora.iOS.*.nupkg')) {
            throw "ios-pack produced no Shenora.iOS nupkg - refusing to publish a partial release"
          }
          Get-ChildItem 'publish/packages' -File | ForEach-Object { Write-Host "  $($_.Name)" }
```

## Decisions worth stating

- **The Xcode/workload override flags are NOT in the job.** A CI runner has a matched pair; the two
  flags this repo's local Mac needs (`ValidateXcodeVersion=false`, `MtouchLink=SdkOnly`) are
  machine-specific. If CI ever needs them, the pair has drifted and *that* is the thing to fix.
- **Device and Release iOS remain unproven** — only the simulator debug path has been run.
- **`Shenora.iOS`'s API baseline is gated on macOS only** (`MetadataSurfaceTests`), since the Windows
  test host cannot build it. Adding a baseline check to this job would close that gap cheaply.

## Rehearsal

`dry_run: true` already runs verify + pack + the OIDC login and publishes nothing. With this change it
also exercises the macOS job and the artifact hand-off — precisely the new risk. The first run should
be a dry run, and its summary already lists the packages that would be pushed, so `Shenora.iOS`
appearing there is the proof.
