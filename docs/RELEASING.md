# RELEASING.md — cutting and consuming releases

## The release pipeline (manual only)

Releases are cut from the GitHub **Actions** tab → **Release** workflow → *Run workflow*
(`.github/workflows/release.yml`). There is deliberately no tag-push or PR trigger (docs/DECISIONS.md D5).

Inputs: `version` (explicit, e.g. `0.2.0`; empty = bump) · `bump` (`none|patch|minor|major`,
default `patch`) · `create_tag` (default true) · `draft` (default true) · `prerelease`.

Steps, in order — the irreversible publishes happen BEFORE the bump commit, so a failed release
burns no version:

1. Resolve the new version; rewrite `<VersionPrefix>` in `src/Directory.Build.props` in the
   working tree only.
2. **Verify gate**: `node devtools/dev.mjs verify` (dotnet build + tests, npm build + tests,
   `check-sensitive --tree`, `knowledge check`).
3. **Pack**: `node devtools/dev.mjs pack` → `publish/packages/*.nupkg` + the npm tarball
   (npm `package.json` version synced from `VersionPrefix`).
4. **Publish NuGet** — Trusted Publishing (OIDC): `NuGet/login@v1` mints a short-lived key; no
   stored secret. One-time setup on nuget.org: Account → Trusted Publishing → policy for the
   repo + `release.yml`, scoped to the `Shenora.*` package glob.
5. **Publish npm** — `npm publish --provenance --access public` from `src/Shenora.React`. Uses
   npm Trusted Publishing (OIDC) once the publisher policy is configured on npmjs.com for
   `@shenora/react` + this repo/workflow; until then set a granular `NPM_TOKEN` repo secret
   (the workflow uses it when present).
6. Commit `chore: release vX.Y.Z` (props + README + package.json), push, annotated tag `vX.Y.Z`.
7. Generate release notes from conventional-commit subjects since the previous release tag
   (numeric version compare, `feat`/`fix` bucketed, chores dropped) → draft GitHub Release.
   No binaries attached — nuget.org/npmjs.com are the canonical homes.

## Consuming pre-release (in-house siblings)

Until the first public release, siblings consume the local pack output:

- NuGet: `node devtools/dev.mjs pack`, then in the consumer add `publish/packages` (this repo)
  as a local package source in its `nuget.config` and pin exact versions.
- npm: `npm pack` output or a `file:` dependency on `src/Shenora.React`, pinned.

Pin exact versions and upgrade deliberately — the same model the family already uses for its
other in-house library.

## Versioning policy

Lockstep across all packages from `src/Directory.Build.props` `<VersionPrefix>` — never edit the
npm version or README headline by hand (`dev.mjs doctor --fix` syncs; `doctor` fails on drift).
SemVer from 1.0; while every consumer is in-house, a documented break may ship in a minor, always
under a `### Breaking` heading in `CHANGELOG.md` and gated by the API-surface baseline tests.
