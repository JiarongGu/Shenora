# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred SIX
times — 502 lines holding six open tasks, then 570 holding three, 458 holding seven, 197 holding six, 123
holding five, and 182 holding two. `doc-shape` fails on a done MARKER, and the recurrences it could not see
are the ones without one: **no marker at all, just finished work narrated at length**, plus a marker written
as `**✅ …**` that its regex read straight past. ⚠ **The test is not "is there a ✅", it is "would deleting
this paragraph lose anything a future session must ACT on?"** If the answer is no, the commit that landed it
is where it lives.
⚠ **Length is now measured too** — `doc-shape` WARNS past 120 lines, which every one of those six cleared by
60+. A crude proxy for a judgement no script can make, and the only signal the marker check cannot miss.

**Status: v0.14.0 is PUBLISHED and VERIFIED LIVE** — all 5 NuGet packages plus `@shenora/react` and
`@shenora/cli` answer 0.14.0, checked against the registries rather than the tree. Tag `v0.14.0`, release
commit `8864990`. ⚠ **A partial read is validation lag, not a half-landed release** — npm is usually
immediate and NuGet takes a minute or two; re-check, never re-push.
It carried the segment tier's foreign-muxer corpus and the data-loss fix that corpus found (a source with
no cut point kept only its last few seconds), the bundle-update answer (`ResourcePackJournal` + `shenora
copy`'s version stamp), and the bridge-tag check reading disk-served documents. **Not breaking**: 0
removals, 0 new `required` on a shipped type, 0 namespace moves.
⚠ `src/Directory.Build.props` must now stay at `0.14.0`: the release workflow owns the bump, and a
hand-bump moves the baseline and skips a release (`release-discipline.md`).

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 📱 WHAT IS LEFT ON ANDROID NEEDS CODECS THE MuMu EMULATOR DOES NOT HAVE

The segment tier is answered on all three shells (`docs/design/media.md`). Both items below ran on MuMu and
came back SKIPPED for the same underlying reason — **that emulator converts almost nothing**
(`convert ac3/eac3/alac/dts: accepted=False`, `convert video h263/h264/hevc: accepted=False`), so no picture
or sound ever reaches an encoder there. A real phone, or an AVD with a fuller codec set, answers both.

- [ ] **Confirm the Android encoder change.** The bitrate was ~1/30th of intent (no frame-rate factor); the fix
  is arithmetic and changes output size and encode cost on a phone. ⚠ It also became reachable for ORDINARY
  1080p H.264, which a grid or head-ramp plan now re-encodes where it used to be copied — so this path is
  newly hot, not newly correct.
The bridge-tag check is proven BOTH ways on BOTH shells now — `ServeDocumentFromDisk` takes a file switch as
well as an env var, since `adb` cannot pass one. Android: tagged → `client READY`, untagged → the warning
with zero handshakes.

### 🔴 `shenora copy`'s VERSION STAMP CANNOT BE READ ON ANDROID — the name has a leading dot

Filed by the adopter on 2026-08-23, having adopted 0.14.0 the day it shipped. **A dot-prefixed `MauiAsset`
never reaches an Android app.** Measured in ONE build, both spellings written side by side into
`Resources/Raw/wwwroot` — which is the layout `docs/guides/mobile.md` and `cli.test.ts:1044` both use:

| file | listed by `dotnet msbuild -getItem:MauiAsset` | in `obj/…/android/assets/wwwroot/` | in the signed APK |
|---|---|---|---|
| `probe-pack.json` | yes | **yes** | **yes** |
| `.shenora-pack.json` (ours), `.dot-probe.json` | yes (`LogicalName wwwroot\.shenora-pack.json`) | **no** | **no** |

So the drop is in the Android asset staging, downstream of the project, and **MSBuild listing the item is
not evidence that it ships**. The consequence is the whole point of 0.14.0 going quiet on the platform that
has app stores: `PackagedVersionIn` answers null, `Open()` REFUSES a blank, and the adopter is back to
having nothing to compare. It fails in the safe direction and says nothing.

- [ ] **Decide the fix.** Renaming `StampFileName` is a break for anyone already stamped (nobody yet, most
  likely — 0.14.0 is a day old); writing BOTH names is ugly but non-breaking; documenting it in the guide is
  the floor. ⚠ Whatever is chosen, `ResourcePackStampTests` pins the CLI↔C# agreement and would need to move
  with it. The adopter shipped `shenora-pack.json` (no dot, same bytes) and reads it themselves — they
  cannot use `PackagedVersionIn` anyway, since a MAUI packaged bundle is a set of app-package assets rather
  than a directory. **That is arguably a second gap**: the helper only serves apps whose packaged bundle is
  on the filesystem.
- [ ] **iOS is unmeasured.** BundleResource, not `assets/` — a different packaging path, so the table above
  says nothing about it in either direction.

✅ **The other two open items here are ANSWERED — by the adopter, 2026-08-23. Deleted rather than ticked
(per the rule at the top); what they leave behind is this:**
- **It has now run in a real shell**, across force-stop + relaunch cycles on Android: no pack · a staged
  pack DROPPED for a newer packaged one (their emulator produced that case by accident, carrying a real
  stale bundle from a previous session) · a newer staged pack still served · a pending pack served once
  with the attempt persisted first, then rolled back when it never confirmed, its predecessor intact ·
  a pending pack confirmed and promoted. Their hand-written record migrated into the journal untouched —
  it happened to have the same `Active`/`Pending`/`Attempts` shape.
- **The masking order is CONFIRMED, not merely inferred.** Their late-attach bug was real and did exactly
  what the guide claims: a deliberately broken bundle "confirmed" while the packaged client was what was
  really running. They hit traps 1 and 2 live on 2026-08-21 and fixed both in one slice; trap 3 was never
  hit in production, because they have not shipped through a store yet — it was found by their owner asking
  how a store update would ever arrive. So the guide's sequence is right, with one refinement worth making:
  **trap 3 is the one an adopter meets LAST and cannot meet at all before their first store release**, which
  is precisely why it needs to be a mechanism rather than a warning.
