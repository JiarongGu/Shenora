# The version is the release workflow's — never yours

**Applies when** you are about to touch a version number, `CHANGELOG.md`'s `## Unreleased` heading, or
anything in the release path. **Not** for ordinary work: `verify` and a pre-commit guard already enforce
this, so you only need it if you are reaching for one of those files deliberately.

🔴 **NEVER TOUCH THE VERSION.** One `<VersionPrefix>` (`src/Directory.Build.props`) is the only version
source; npm and README are synced by `dev.mjs pack` / `doctor --fix`, never hand-edited.

🔴 **`VersionPrefix` ITSELF IS NOT YOURS TO BUMP EITHER**, nor is the CHANGELOG's `## Unreleased` heading —
the workflow stamps it. An empty `version` input means *"bump from whatever `VersionPrefix` says"*, so a
hand-bump moves that baseline and **SKIPS a release**. It cost 0.2.0 outright on 2026-08-01.

- Between releases `VersionPrefix` == the newest `v*` tag. `doctor` and a pre-commit guard both enforce it,
  which is why this is a short rule rather than a long one: **the mechanism is the protection**, and this
  file only exists to stop you fighting the mechanism when it refuses you.
- Cut releases from the Actions tab — `docs/RELEASING.md`.
- ⚠ **Every public change is SemVer surface.** Note breaks in `CHANGELOG.md` under `### Breaking`, and only
  when the old side was actually RELEASED (`git grep <old-name> <last-tag> -- src/`) — an entry for a name
  introduced and renamed inside one unreleased window is development churn wearing a migration note, and it
  buries the real breaks. 1.0 is a separate deliberate freeze, not yet cut.

🔴 **AFTER the publish: a PARTIAL registry read is LAG, so re-check — never re-push.** Verify against the
registries rather than the tree, and expect the seven to answer over a few minutes: npm is immediate,
NuGet trails, and one package trails the rest (0.13.0 and 0.16.0 both took three reads over ~3 min).
**The evidence that it is lag rather than a half-landed release is structural** — the workflow tags only
after every publish succeeds and publishes NuGet BEFORE npm, so npm answering already means NuGet's step
passed. ⚠ Confirm a stale-looking NuGet answer on TWO endpoints (flatcontainer + registration5) before
calling it anything: one endpoint disagreeing can equally be a parse artefact.

🔴 **Read every csproj `<Description>` BY HAND before the cut, and read the CHANGELOG window as the
checklist.** No gate reads a `<Description>` — the D22 word audit sweeps the API baselines only — and this
step has now caught something on three consecutive releases (a retired package id, a safety claim the code
does not implement, then three descriptions that predated the window's headline features). Same pass, same
reason: **run the prose audit BEFORE a cut, not after.** Twice now the release's clearest feature was
findable in no entry document, which only that reading catches.
