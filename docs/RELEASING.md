# RELEASING.md — cutting and consuming releases

> ## ⚠ Never edit the version by hand — this cost 0.2.0 outright
>
> **A session must never change `<VersionPrefix>`, and never stamp the CHANGELOG's `## Unreleased`
> heading.** Both belong to the workflow (step 1 below), and editing either breaks it:
>
> - An empty `version` input means *bump from whatever `VersionPrefix` currently says*. On
>   2026-08-01 a session hand-bumped `0.1.2 → 0.2.0`; the next run bumped from there and published
>   **0.3.0**. Version 0.2.0 went from unreleased to skipped without anyone choosing to skip it, and
>   the registries read 0.1.2 → 0.3.0. On a post-1.0 repo the same slip lands on a MAJOR.
> - That same session hand-stamped the changelog heading to `## 0.2.0`, leaving the workflow no
>   `## Unreleased` to stamp — so **0.3.0 shipped with its section titled "0.2.0"**, the exact failure
>   stamping was automated to prevent.
>
> **Why nothing caught it:** `doctor` proved the version was *consistent* across props/npm/README/
> LICENSE, and a hand-bump keeps all four consistent. Consistency was never the property at risk —
> **authorship** was.
>
> **Now enforced, two layers:** `doctor` fails when `<VersionPrefix>` differs from the newest `v*`
> tag (between releases they must be equal, because the workflow bumps as part of releasing), and
> `devtools/scripts/check-version-bump.mjs` blocks the edit at pre-commit. If you genuinely are
> repairing a botched release, say so explicitly: `SHENORA_RELEASE=1 git commit …`.
>
> Want a specific number? Pass an explicit `version` input to the workflow. Never pre-set the file.

## The release pipeline (manual only)

Releases are cut from the GitHub **Actions** tab → **Release** workflow → *Run workflow*
(`.github/workflows/release.yml`). There is deliberately no tag-push or PR trigger (docs/DECISIONS.md D5).

Inputs: `version` (explicit, e.g. `0.2.0`; empty = bump) · `bump` (`none|patch|minor|major`,
default `patch`) · `create_tag` (default true) · `draft` (default true) · `prerelease` ·
`dry_run` (default false).

> ⚠ **`draft: true` is NOT a dry run.** It only makes the GitHub Release a draft — both registry
> pushes happen before that step, and both are effectively permanent (nuget.org never deletes a
> version, only unlists it; npm allows an unpublish briefly and only for a brand-new package).
> **Rehearse with `dry_run: true`:** it runs the gate, the pack and the OIDC login, then publishes
> nothing and touches no git. The OIDC login is the part worth rehearsing — that step exchanges the
> workflow's token against the trusted-publishing policy and fails loudly if the policy does not
> match this repo and workflow file, which is exactly what a first release gets wrong.

Steps, in order — the irreversible publishes happen BEFORE the bump commit, so a failed release
burns no version:

1. Resolve the new version; rewrite `<VersionPrefix>` in `src/Directory.Build.props`, **stamp the
   CHANGELOG's `## Unreleased` heading** (`dev.mjs changelog --fix --version X.Y.Z`), and **sync the
   derived files from VersionPrefix** (`dev.mjs doctor --fix` — rewrites the npm package.json version
   and the README `## Status` headline) — all in the working tree only. Stamping is automated because
   it was the one release edit left to a human, and the sibling library shipped a version whose
   section was still titled "Unreleased" because of it. The derived-file sync has to happen HERE (not
   in Pack) because `verify`'s `doctor` is deliberately non-fixing — a bump would otherwise create
   drift that Verify caught and blocked (P5.5 H5 combined with the release path is what earned this,
   in v0.1.1). The step then asks *git* whether any of the four files actually changed, rather than
   comparing version strings: the edits move independently, so a string compare would skip the commit
   when only one moved — and would produce an empty commit on a re-run that moved none.
2. **Verify gate**: `node devtools/dev.mjs verify` (dotnet build + tests, npm build + tests,
   typechecks, `check-sensitive --tree`, `knowledge check`, `doctor`).
3. **Pack**: `node devtools/dev.mjs pack` → `publish/packages/*.nupkg` + the npm tarball
   (npm `package.json` version and the README headline synced from `VersionPrefix`), each with its
   sha256 printed.
4. **Publish NuGet** — Trusted Publishing (OIDC): `NuGet/login@v1` mints a short-lived key; no
   stored secret. One-time setup on nuget.org: Account → Trusted Publishing → policy for the
   repo + `release.yml`, scoped to the `Shenora.*` package glob. `--skip-duplicate` makes a re-run safe.
5. **Publish npm** — `npm publish publish/packages/shenora-react-X.Y.Z.tgz --provenance --access public`.
   Note it publishes the tarball **Pack produced**, not a rebuild: `npm publish` from the package
   directory would re-run `prepublishOnly` and ship a second artifact, so the thing whose sha256 step 3
   printed would not be the thing that shipped. Uses npm Trusted Publishing (OIDC) once the publisher
   policy is configured on npmjs.com for `@shenora/react` + this repo/workflow; until then set a
   granular `NPM_TOKEN` repo secret (the workflow uses it when present).
   The step **skips when that exact version is already on the registry**, because npm has no
   `--skip-duplicate` and fails hard otherwise. That matters: the two registries can never be made
   atomic, so the goal is re-runnability — without the guard, any failure after both pushes (a
   rejected bump commit, a tag race) leaves every re-run stuck here, unable to complete the bump or
   the tag.
6. Commit `chore: release vX.Y.Z` — props + CHANGELOG + README + npm `package.json`, named
   explicitly so no build artifact rides along — then push, then the annotated tag `vX.Y.Z`. The tag
   step is gated on `create_tag` and skipped if the tag already exists.
7. Generate release notes from conventional-commit subjects since the previous release tag
   (the highest tag STRICTLY LESS than the one being cut, compared numerically — "not equal" would
   pick a NEWER tag as the baseline when re-cutting an older version) → draft GitHub Release, with
   `feat`/`fix` bucketed and chores dropped. No binaries attached — nuget.org/npmjs.com are the
   canonical homes. The GitHub-release step is gated on `create_tag` too, because the action CREATES
   `tag_name` when it does not exist: gating only the tag step left `create_tag: false` still
   producing a tag, at the default-branch head rather than the published commit (fixed in P5.5 H5).

## Consuming pre-release (in-house siblings)

Until the first public release, siblings consume the local pack output. The recipe, smoke-proven
2026-07-30 (P1.1; the rerunnable scratch consumer lives untracked in `devtools/_p11-consumer/`):

- NuGet: `node devtools/dev.mjs pack`, then in the consumer's `nuget.config` add
  `publish/packages` (this repo) as a source alongside nuget.org (transitive deps like the
  WebView2 package come from there) and pin EXACT versions with the range syntax:
  `<PackageReference Include="Shenora.WinForms" Version="[0.1.0]" />`. Reference the leaf package you
  actually need and the rest arrive transitively: `Shenora.WebView2` pulls `WinForms` + `Ipc` + `Core`
  (D19 — the two Windows packages are one layer), and `Shenora.WebView2.Sessions` pulls `WebView2`.
  Reference `Shenora.WinForms` directly only for a shell with no web frontend.
  A consumer inside this repo's tree must set `ManagePackageVersionsCentrally=false`.
- npm: install the packed tarball (`npm install <repo>/publish/packages/shenora-react-<v>.tgz`)
  with `react` alongside — or a `file:` dependency on `src/Shenora.React` during co-development.
  The tarball works under native Node ESM, not just bundlers (the emitted imports carry explicit
  `.js` extensions; enforced by the package's NodeNext tsconfig — see `docs/FIX-LOG.md`).

> ⚠ **The NuGet GLOBAL cache beats every source, so re-packing the same version is not enough.**
> `~/.nuget/packages` is keyed on id+VERSION, and a cached copy wins over any feed — including this
> repo's `publish/packages`. Re-packing `0.1.0` therefore leaves consumers restoring whatever
> `0.1.0` they cached first, silently: no warning, no restore error, and `--no-cache` does not help
> (that flag is HTTP caching). Found in P6.1, and it is not a theoretical risk — a consumer resolved
> a `Shenora.WebView2` packed BEFORE the D19 re-layer, so `Shenora.WinForms` was absent from its
> dependency graph and the build failed with "the namespace does not exist" while the freshly packed
> nupkg on disk was perfectly correct. The diagnosis is `obj/project.assets.json`: compare the
> dependencies it recorded against the `.nuspec` inside the nupkg you just built.
> **`dev.mjs pack` now evicts this repo's ids at that version after packing**, so the trap is closed
> for anyone packing from here. A consumer who obtained packages another way clears them with
> `dotnet nuget locals global-packages --clear` (blunt) or by deleting
> `~/.nuget/packages/<id>/<version>` (surgical, and what `pack` does).

Pin exact versions and upgrade deliberately — the same model the family already uses for its
other in-house library.

## Versioning policy

Lockstep across all packages from `src/Directory.Build.props` `<VersionPrefix>` — never edit the
npm version or README headline by hand (`dev.mjs doctor --fix` syncs; `doctor` fails on drift).
SemVer from 1.0; while every consumer is in-house, a documented break may ship in a minor, always
under a `### Breaking` heading in `CHANGELOG.md` and gated by the API-surface baseline tests.
