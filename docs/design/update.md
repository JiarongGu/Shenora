# Shipping app updates — as built

**Maintainer-facing.** How `Shenora.Engine.Update` stages a release, verifies it, and applies it. For WHY
there are two phases at all, **D57**; for the launcher topology, **D50**; for why this is product rather
than devtools, **D56** — **this doc states the design, never the rationale** (D77).

## The shape of the problem

A running process cannot replace its own executable. So the work splits in two, and the two halves run at
different times in different processes:

| Phase | Runs | Owner |
|---|---|---|
| **Stage** — fetch, verify, publish a marker | while the app is alive | `UpdateStage` (this package) |
| **Apply** — overlay, remove, clear | with the app not running | `UpdateStage.ApplyAsync`, or a native applier |

**The kit owns the PROTOCOL, not the download.** There is no HTTP client and no release host — an app
fetches bytes however it likes and hands them over through `IUpdateSource`.

## The on-disk layout is a supported contract

```
{UpdateStageOptions.Root}/            ← conventionally {installRoot}/.update
  ready.json                          ← the MARKER. Written LAST.
  staged/
    manifest.json                     ← the FULL release manifest, for the applier's removals
    <every changed file, at its manifest-relative path>
```

`ready.json` is `UpdateStageStatus` as camelCase JSON. An applier that only needs "is there an update"
may test for its existence and nothing more.

🔴 **The two phases are SEPARATELY adoptable, so these three names and the write ORDER are a compatibility
surface with appliers this repo cannot recompile.** Renaming `ready.json`, moving `staged/`, or publishing
the marker before verification are breaking changes no API baseline can see.

**The ordering IS the property.** The marker goes down after every file has matched its hash, so its
existence means "complete and verified" and an applier never re-checks. A crash mid-download leaves files
and no marker, and the next run restages.

## Staging: `FetchAsync`

```
GetManifestAsync → diff against installed → fetch ONLY the changeset → write the full manifest
                 → CommitAsync → marker
```

**Only the changeset is staged**, which is why `CommitAsync` takes the manifest of what is IN the stage —
the full release manifest verified against a partial stage would fail on every unchanged file. The full
manifest rides along inside `staged/manifest.json` for the applier's removals, and the overlay makes it the
newly-installed baseline.

A fetch that throws is left to escape: **a partial download must not be staged as if it were whole.**

**A removals-only release still stages**, and downloading nothing is not the same as having nothing to do.
The payload is empty, the apply pass is driven by `staged/manifest.json`, and the dropped files go. Only a
diff with no additions, no updates *and* no removals returns not-pending.
⚠ **"Nothing to download" is not a stopping condition** — treating it as one leaves the dropped files on
disk with no error anywhere, and a dropped-but-still-present assembly is still loadable, which is usually
why a release drops one.

### `CommitAsync` verifies four things, then publishes

| # | Failure it catches | How |
|---|---|---|
| 1 | **truncation** — listed but missing | `File.Exists` per manifest entry |
| 2 | **tamper** — present but wrong bytes | SHA-256 per entry; the hash is authoritative, never the size |
| 3 | **intrusion** — present but unlisted | walk the staged tree, reject anything the manifest does not index |
| 4 | **an unusable applier manifest** | `staged/manifest.json` must parse and list files |

Check 3 exists because `ApplyAsync` overlays the staged **tree**, not the manifest — without it a file
nothing verified reaches the install root. The kit's own `manifest.json` is always exempt; anything else a
clean release legitimately carries is exempted through `UpdateStageOptions.IsUnindexed`.

Check 4 is caught here rather than at apply time because the applier runs after the app has exited, where a
refusal has nothing running to report it. ⚠ It is a **check, never a write** — the manifest passed to
`CommitAsync` is the changeset, the file is the full release. Writing the changeset there would tell the
applier everything else was removed.

🔴 **The "empty manifest" danger belongs to check 4's object, and getting that wrong is what broke the
removals-only case.** An empty manifest tells an applier to delete every tracked path — but the manifest an
applier reads is `staged/manifest.json`, the full RELEASE. `CommitAsync`'s own parameter is the CHANGESET,
which is legitimately empty for a removals-only release; refusing it there defended a different object from
a risk it never carried, and made that release impossible to stage at all.

## Applying: `ApplyAsync`

Portable — no native code, nothing platform-specific.

1. **Read both manifests first.** The overlay overwrites the installed one, and the removal set is the
   difference between them.
2. **Overlay** every staged file onto the install root.
3. **Write the new baseline** to `UpdateStageOptions.BaselinePath` (default `{installRoot}/manifest.json`).
4. **Remove** `installed − release`, tracked paths only.
5. **Clear** the stage.

🔴 **Run it from OUTSIDE the install root, with the app not running** (D50). A launcher at `{root}/`
overlaying `{root}/app/` can never overwrite or delete itself; overlay a tree containing the running
process and every self-exclusion case becomes yours.

**Removals are tracked paths only, never a directory sweep** — user data lives in the same tree, and only
the manifest knows which files the app owns.

### Failure is graded, not uniform

| Situation | Result |
|---|---|
| no staged manifest, or it lists nothing | **refuse the whole apply** — removals would delete everything |
| no readable baseline (first install, corrupt) | **overlay, remove nothing** |
| baseline cannot be written after the overlay | **applied**; the next apply computes no removals |
| one removal path escapes the root | skip that path, continue |
| one file will not delete | log, continue |

The asymmetry is deliberate: before the overlay a bad manifest means stop, after it the install already IS
the new version and abandoning would leave it half-applied.

## Path safety

🔴 **The manifest is the only input in this kit that arrives from a remote server, and it drives both
`File.Create` and `File.Delete`.** Two shapes escape a root and neither fails loudly:

1. **A rooted path.** `Path.Combine` silently discards its first argument when the second is rooted — and
   C++'s `std::filesystem::operator/` does the identical thing, which is why the native applier carries the
   same rejection.
2. **A `..` segment**, which walks out the ordinary way.

**Refused at the MANIFEST, not at each call site** (`ManifestDiff.IsSafeRelativePath`). Hash verification
checks a file's CONTENT and never its PATH, and the intrusion check walks the staged directory — so a file
written outside it is not in the walk, is then looked for at the same escaped location, and is found. Both
gates pass. The path is the only thing that can catch this.

`UpdateManifest.Parse` refuses on the same rule, and the difference matters: a poisoned **baseline** must
take `ApplyAsync`'s existing "no usable installed manifest" branch rather than aborting. Failing at parse
puts it there for free; failing only at diff time would throw past that guard and leave an app permanently
unable to update.

`UpdateStage.ResolveTracked` is the one place a manifest path becomes a filesystem path — every write,
existence check and delete goes through it, ending in `PathClaims.IsContained` and therefore
`PathComparison`'s platform case rule.

### Normalization

Manifest paths are forward-slashed and compared **case-insensitively with separators normalized**
(`ManifestDiff.Normalize`, `internal` so the intrusion check uses the same rule rather than a second copy
that can drift). Without separator normalization a file is "added" on every check and never converges;
without case normalization a generator that changes one letter turns a whole release into a full
redownload. A duplicate path throws rather than last-wins — last-wins makes the changeset depend on list
order.

## Sources

`IUpdateSource` is the seam and **the kit ships one implementation of it**, `ZipUpdateSource`: the
interface is `ManifestFile → Task<Stream>`, and a zip entry is exactly that.

- **Multiple archives, not one.** A release is commonly published as one zip per part with a single
  manifest spanning them. Entries are indexed across every archive at construction; a path carried by two
  archives is rejected rather than resolved by order.
- **Streams must be seekable** — `ZipArchive` reads the central directory from the END, which a live HTTP
  response cannot do. Rejected up front, because the natural failure is an unhelpful format error.
- **A manifest entry no archive carries THROWS**, so `FetchAsync` lets it escape rather than staging a
  truncated release.
- ⚠ **Not thread-safe** — a property of `ZipArchive`. Safe with `FetchAsync` today because that opens
  entries sequentially; parallelising that loop without a source per worker corrupts reads.

## Compression

`ZipExtraction` exists because extracting safely is the hard part — the extraction itself is one framework
call. **The danger is the entry NAME, not the bytes**: nothing stops an entry being `../../autoexec.bat`.
`ZipFile.ExtractToDirectory` has guarded this for years, but the hand-rolled loop anyone writing progress
reporting ends up with does not.

**Refusals and limits behave oppositely, and that is the design:**

| | Behaviour | Because |
|---|---|---|
| an escaping entry | **skipped**, named in `ExtractionResult.Refused` | one hostile entry usually still leaves an archive you want; a caller who disagrees treats a non-empty list as fatal in one line |
| `MaxTotalBytes` / `MaxEntries` | **throw** | the caller's assumption about the archive was wrong, and continuing writes an unknown amount to their disk |

The zip-bomb bound is on the TOTAL (default 1 GiB), not per entry — a bomb is many small entries, or one
that only looks small until inflated. Containment resolves the root ONCE and compares with a separator
appended, so `/data-evil` cannot pass as a child of `/data`, under `PathComparison`'s case rule.

## Resource packs

`ResourcePack` is the same discipline applied to a payload the kit refuses to vendor (D42): a named,
versioned set of files an app needs on disk — a native binary for the current ABI, a model, a font set.

- **`{Root}/{name}/{version}`**, so two versions coexist and switching is a path change rather than an
  in-place mutation of files something may still have open. Name and version are rejected, never
  sanitised, if they contain a separator.
- **A `.ready` marker written LAST**, the same rule as `ready.json`. `IsReady` is the only question a
  caller has, and absent / half-extracted / interrupted are all the same answer.
- **A partial directory is discarded and re-extracted, never resumed** — resuming trusts files written by
  a run that did not finish.
- **Refused entries are FATAL here**, unlike a general extraction: a pack is used as a unit, and a binary
  whose sibling library was refused is not a smaller pack but a broken one that fails later and elsewhere.
- **`PathOf` refuses indistinguishably** — not ready, absent, and escaping all return null, because
  callers build these paths from configuration and a distinguishable refusal is a probe for what exists on
  the device.
- **`PruneOthers` is separate from `StageAsync`** and never throws: the old version is usually still
  loaded when the new one is staged, so the safe moment to collect is the next start, which only the app
  knows it has reached.

## What is deliberately absent

- **No downloader and no release host.** Baking one in ships a consumer's decision and drags an HTTP
  dependency into `Shenora`.
- **No archive format but ZIP.** `System.IO.Compression` is in the shared framework, so this adds no
  dependency and works on every shell; 7z and rar need a native library the kit will not vendor (D42).
- **No version parsing.** `UpdateManifest.Version` is the app's own string, and `GeneratedAt` is
  diagnostic only — never used to decide staleness.
- **No signature verification.** Integrity is per-file SHA-256 against a manifest; authenticating the
  manifest itself is the app's transport problem.
