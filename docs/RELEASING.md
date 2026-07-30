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
6. Commit `chore: release vX.Y.Z` (props + README + package.json) **only if the version actually
   changed**, push, then the annotated tag `vX.Y.Z` — the tag step is gated on the `create_tag`
   input and skipped if the tag already exists.
7. Generate release notes from conventional-commit subjects since the previous release tag
   (numeric version compare, `feat`/`fix` bucketed, chores dropped) → draft GitHub Release.
   No binaries attached — nuget.org/npmjs.com are the canonical homes.
   ⚠ **Two steps can create the tag.** The release action is always given `tag_name`, so it creates
   the tag itself when step 6's tag step was skipped — i.e. `create_tag: false` does not mean "no
   tag", and the tag it creates points at the default-branch head, which may not be the published
   commit. Fixing the workflow is `TASKS.md` H5.

## Consuming pre-release (in-house siblings)

Until the first public release, siblings consume the local pack output. The recipe, smoke-proven
2026-07-30 (P1.1; the rerunnable scratch consumer lives untracked in `devtools/_p11-consumer/`):

- NuGet: `node devtools/dev.mjs pack`, then in the consumer's `nuget.config` add
  `publish/packages` (this repo) as a source alongside nuget.org (transitive deps like the
  WebView2 package come from there) and pin EXACT versions with the range syntax:
  `<PackageReference Include="Shenora.WinForms" Version="[0.1.0]" />`. Reference the leaf packages
  you actually need — `Shenora.WinForms` + `Shenora.WebView2`, or `Shenora.WebView2.Sessions` when
  you want auxiliary browser sessions (it pulls `WebView2`) — and `Core`/`Ipc` arrive transitively.
  A consumer inside this repo's tree must set `ManagePackageVersionsCentrally=false`.
- npm: install the packed tarball (`npm install <repo>/publish/packages/shenora-react-<v>.tgz`)
  with `react` alongside — or a `file:` dependency on `src/Shenora.React` during co-development.
  The tarball works under native Node ESM, not just bundlers (the emitted imports carry explicit
  `.js` extensions; enforced by the package's NodeNext tsconfig — see `docs/FIX-LOG.md`).

Pin exact versions and upgrade deliberately — the same model the family already uses for its
other in-house library.

## Versioning policy

Lockstep across all packages from `src/Directory.Build.props` `<VersionPrefix>` — never edit the
npm version or README headline by hand (`dev.mjs doctor --fix` syncs; `doctor` fails on drift).
SemVer from 1.0; while every consumer is in-house, a documented break may ship in a minor, always
under a `### Breaking` heading in `CHANGELOG.md` and gated by the API-surface baseline tests.
