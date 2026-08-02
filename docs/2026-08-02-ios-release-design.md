# Publishing the iOS face of `Shenora.Maui` — release-pipeline proposal

**Status: DRAFT FOR REVIEW. Nothing in `.github/workflows/` has been changed.** This exists because
the release pipeline is the one part of this repo that has already cost a version (0.2.0), and
`docs/RELEASING.md` treats cutting a release as a deliberate manual act. Rewiring publish on an
assistant's judgement is the wrong call; rewiring it on a reviewed diff is fine.

Retire this file once the change lands, the way the 0.2.0 cleanup retired its implemented design docs.

## The problem, in one line

`Shenora.Maui` multi-targets, but the TFM follows the BUILD HOST — so the Windows release job packs an
**android-only** package and there is no `net10.0-ios` face on nuget.org.

## What is already done (so the diff below is small)

- `ShenoraMobileTargets` overrides the host-conditional TFM list. Verified both ways over ssh:
  `-p:ShenoraMobileTargets=net10.0-android` on a Mac reports `net10.0-android`.
- Packing iOS on a Mac was measured, not assumed — `Shenora.Maui.0.4.0.nupkg` containing
  `lib/net10.0-ios26.0/Shenora.Maui.dll` + XML docs, and a **fully correct nuspec**: dependencies on
  `Shenora.Core`/`Shenora.Ipc` at the matching version, README, licence, repository metadata. A plain
  `dotnet pack` on macOS needs no help to produce a shippable package.
- `dev.mjs pack` already passes `-p:Version=<v>` explicitly rather than relying on the rewritten
  `VersionPrefix`. **This is what keeps the change small:** the mobile job needs the version STRING
  only — it never has to rewrite `Directory.Build.props`, stamp the CHANGELOG, or run `doctor --fix`.

## Shape

Three jobs instead of one. The only structural change to `publish` is that it receives the version
instead of computing it, and swaps in one file before pushing.

```
version  (ubuntu)  ── computes the version string. No writes, no side effects.
   │
   ├─► mobile-pack (macOS) ── both workloads, packs Shenora.Maui with BOTH faces, uploads it
   │
   └─► publish  (windows)  ── unchanged except: takes the version as input, and REPLACES the
                              android-only Shenora.Maui.*.nupkg with the macOS one before pushing
```

Why `publish` stays on Windows: the `net10.0-windows` targets need it, and that is unrelated to this.

### 1. New job — `version`

Moves the bump arithmetic out of `publish` so two jobs cannot disagree about what is being released.
`publish`'s "Determine version" then treats it as an explicit input and keeps every file-rewriting
step it has today.

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

### 2. New job — `mobile-pack`

```yaml
  mobile-pack:
    needs: version
    runs-on: macos-15        # see "The one unverified assumption" below
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      # Explicit rather than assumed: maui-android needs a JDK and the Android SDK, and whether a
      # given macOS image ships them has changed between runner versions. Cheap, and it removes the
      # only thing about this job that depends on the image.
      - uses: actions/setup-java@v4
        with:
          distribution: microsoft
          java-version: '17'
      - uses: android-actions/setup-android@v3

      - name: Install both mobile workloads
        run: dotnet workload install maui-android maui-ios

      # %3B, not a literal ';'. The shell eats a semicolon before MSBuild sees it and the symptom
      # names the wrong thing — MSB1006 reporting the SECOND TFM as an unknown switch. Hit live.
      #
      # -p:Version, not a VersionPrefix rewrite: `dev.mjs pack` does the same, so this job needs no
      # copy of the release's file-stamping logic.
      - name: Pack Shenora.Maui with BOTH faces
        run: |
          dotnet pack src/Shenora.Maui/Shenora.Maui.csproj -c Release \
            -p:Version=${{ needs.version.outputs.version }} \
            -p:ShenoraMobileTargets=net10.0-android%3Bnet10.0-ios \
            -o mobile-packages

      # A job that silently produced one face is the failure this whole change exists to fix, so
      # assert BOTH lib folders are present rather than trusting the pack.
      - name: Assert both faces are in the package
        run: |
          nupkg=$(ls mobile-packages/Shenora.Maui.*.nupkg)
          unzip -l "$nupkg" | grep -q 'lib/net10.0-android' || { echo "no android face in $nupkg"; exit 1; }
          unzip -l "$nupkg" | grep -q 'lib/net10.0-ios'     || { echo "no ios face in $nupkg";     exit 1; }
          unzip -l "$nupkg" | grep 'lib/'

      - uses: actions/upload-artifact@v4
        with:
          name: shenora-maui-nupkg
          path: mobile-packages/Shenora.Maui.*
```

### 3. Changes to `publish`

```yaml
  publish:
    needs: [version, mobile-pack]
    runs-on: windows-latest
```

In **Determine version**, replace the bump arithmetic with the passed value — everything after it
(the `VersionPrefix` rewrite, the CHANGELOG stamp, `doctor --fix`, the `git status` check) stays
exactly as it is:

```pwsh
          $final = '${{ needs.version.outputs.version }}'
          if ($final -notmatch '^\d+\.\d+\.\d+$') { throw "bad version: $final" }
```

Then one new step, **after `Pack` and before `Push to NuGet`**:

```yaml
      - uses: actions/download-artifact@v4
        with:
          name: shenora-maui-nupkg
          path: mobile-in

      - name: Swap in the macOS-built Shenora.Maui
        run: |
          # The Windows Pack produced an android-only Shenora.Maui because only a Mac can build the
          # iOS face. Same id, same version — so it must be REPLACED, never added, or the push step
          # would try to publish the same version twice.
          $built = Get-ChildItem 'mobile-in' -File | Where-Object { $_.Name -like 'Shenora.Maui.*.nupkg' }
          if (-not $built) { throw "mobile-pack produced no Shenora.Maui nupkg" }
          Get-ChildItem 'publish/packages' -File |
            Where-Object { $_.Name -like 'Shenora.Maui.*' } |
            ForEach-Object { Write-Host "replacing $($_.Name)"; Remove-Item $_.FullName }
          Copy-Item 'mobile-in/Shenora.Maui.*' 'publish/packages/'
          Get-ChildItem 'publish/packages' -File | ForEach-Object { Write-Host "  $($_.Name)" }
```

## The one unverified assumption

**Whether the chosen macOS runner image can build `maui-android`.** GitHub's macOS images have not
been consistent about shipping a JDK and the Android SDK, particularly on the arm64 images. The
`setup-java` + `setup-android` steps above exist to make the job independent of that, and they are
cheap — but this is the part of the proposal that has *not* been run, and the first `dry_run` is what
would prove it.

Everything else in this document was measured on a real Mac.

## Rehearsal

`dry_run: true` already runs verify + pack + the OIDC login and publishes nothing. With this change it
would additionally exercise the macOS job and the swap, which is precisely the new risk — so the first
run should be a dry run, and its summary should list `Shenora.Maui` with both faces.

Worth adding to the **Dry-run summary** step so the rehearsal proves the thing it was extended for:

```pwsh
          Write-Host "  Shenora.Maui faces:"
          $m = Get-ChildItem 'publish/packages' -Filter 'Shenora.Maui.*.nupkg' | Select-Object -First 1
          if ($m) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::OpenRead($m.FullName).Entries |
              Where-Object { $_.FullName -like 'lib/*' } |
              ForEach-Object { Write-Host "    $($_.FullName)" }
          }
```

## What this does NOT address

- **The Xcode/workload pairing.** A CI runner has a matched pair, so the two override flags this
  repo's local Mac needs (`ValidateXcodeVersion=false`, `MtouchLink=SdkOnly`) are deliberately NOT in
  the job. If CI ever needs them, the pair has drifted and that is the thing to fix.
- **Device and Release iOS remain unproven locally** — only the simulator debug path has been run.
- **`ADOPTION.md` should gain the `lib/net10.0-ios26.0` note** when this ships: that folder carries
  the workload's TargetPlatformVersion, not `SupportedOSPlatformVersion` (15.0), and it is what a
  consuming project must be compatible with.
