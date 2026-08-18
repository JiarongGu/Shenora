# TASKS.md — open backlog only

**This file holds OPEN tasks only.** A finished task is **DELETED**, not ticked in place — the length of
this file is the size of the remaining work, which is the whole point of looking at it. Git is the
archive; `CHANGELOG.md` is the release-facing log. `> DIRECTION (owner):` blockquotes capture steering
verbatim and stay as long as they still steer.

🔴 **A `✅` is the same defect as `DONE`: an entry that failed to leave.** This drift has recurred four
times — 502 lines holding six open tasks, then 570 holding three, then 458 holding seven, then 197
holding six. `node devtools/dev.mjs doc-shape` fails on a done MARKER here, and the fourth recurrence is
the one it could not see: **no marker anywhere, just finished work narrated at length** — a 70-line
diagnosis kept for two open lines under it. ⚠ **The test is not "is there a ✅", it is "would deleting
this paragraph lose anything a future session must ACT on?"** If the answer is no, the commit that
landed it is where it lives.

**Status: v0.11.0 is published (2026-08-17)** — the release the long hold was waiting for, carrying the
whole review-and-fix arc plus `@shenora/cli`'s first real publish. The tree is at the tag. `CHANGELOG.md`
has no `## Unreleased` section until the next change opens one; the 0.11.0 section is that release's
record, and it is **mostly BREAKING** (D64/D65/D66) — read `### Breaking` before touching the surface.

> **ADOPTING THIS KIT? Start at `docs/ADOPTION.md`, not here.** This is the maintainer's remaining work,
> and a short list means the kit is in good shape rather than that nothing is happening. Several entries
> are deliberately WAITING on an adopter's harvest — D15 working as intended, not a stall.
> **What is deliberately NOT built, and why, is `docs/DECISIONS.md`'s "Anti-goals".** Read it before
> proposing any of it.

**Prefer measuring to filing, and prefer the SIMULATOR to the phone.** A device round trip needs a human
to look at the glass (there is no `devicectl` screenshot); the simulator answers most questions in
90 seconds. Read `mobile-harness.md`'s simulator loop before choosing a target.

## Open

### 🧭 ADOPTION HARVEST — Yaorin on 0.11.0 (D15 working as intended)

Yaorin (desktop `Shenora.Windows` + MAUI `Shenora.Android`/`.iOS` + `@shenora/react`) moved 0.10.0 → 0.11.0
on 2026-08-18. The release notes carried the migration well — `UseMobile` splitting, the FACADE→module
rename, `SingleInstanceResult`, the media reshape and `MobileWebViewInterceptor`'s pipeline argument were
each found by reading `### Breaking` and applied without a surprise. Two things cost real time anyway, and
one of them has a silent failure mode.

⚠ Three suspicions were checked and DROPPED rather than filed: `MobileWebViewInterceptor`'s new argument is
documented (and `app.Pipeline` does exist), the `IFileLockInspector`/`FileLockHolder` moves are in the
table, and `AppCallback.Logger(Action<string>)` already covers the `ILogger` switch for an app that only
has a delegate. The two below are what survived.

- [ ] 🔴 **`DerivedCacheKey` going `internal` forces an adopter to re-derive it, and any drift silently
  orphans every cache on every device.** The entry's reasoning is *"every consumer is in the same
  assembly"* — that was not true of this adopter: Yaorin's on-device HLS route keyed its segment
  directories with `DerivedCacheKey.For(path, length, mtime, "hls")` on 0.10.0, and it compiled because the
  type was public.
  - **Why a copy is not a fine answer.** The key NAMES a directory of already-produced segments. Re-deriving
    it is easy to get subtly wrong (separator normalisation, case, field order, tick precision, the 8-byte
    truncation) and every one of those produces a *valid-looking* key that matches nothing — so the app
    silently re-encodes everything it had already cached, on every user's device, with no error. Yaorin now
    carries a byte-for-byte copy with a comment forbidding edits, which is a bad shape to have handed an
    adopter.
  - **Cheapest fix, and it keeps the surface small:** hand the key back rather than re-export the helper —
    the conversion/segment routes already compute it, so surfacing it on the result (or on the options'
    resolved request) means no adopter ever derives one. Making it public again is the fallback.
  - ⚠ Worth a re-check of the same claim elsewhere: `internal`-ing on the grounds of "no consumer outside
    this assembly" is only checkable against THIS repo, and an adopter is by definition outside it.

- [ ] **`MediaSource.Uri` refuses a `file://` URL, and says it is "not a file path or an absolute URL".**
  `MediaPlayerBase.ParseUri` takes an absolute URI only when `!IsFile`, and otherwise a rooted path — so
  `file:///C:/x.wma` matches neither branch. It is the obvious thing for a .NET caller to pass
  (`new Uri(path).AbsoluteUri`), and the message names both of the things it IS.
  - **Cost:** one failed open per adopter, diagnosed only by reading `ParseUri`. Yaorin's desktop player
    hit it on the first call and now carries a comment explaining why it passes a bare path.
  - **Either accept it** — one more branch, `uri.IsFile ? uri.LocalPath : …`, which is what `IosMediaPlayer`
    already does one level down — **or make the message say what it means**: "expected a rooted path or a
    non-file absolute URL; got a file: URL — pass the path instead."

- [ ] **The package-fold namespace table under-reports the moves, so following it still ends in a
  compile-error hunt.** The table maps `Shenora.Core` → `Shenora`. Verified against Yaorin's pre-upgrade
  tree, these were in FLAT `Shenora.Core` at 0.10.0 and are not:

  | 0.10.0 | 0.11.0 |
  |---|---|
  | `Shenora.Core.IEventBus` | `Shenora.Core.Events.IEventBus` |
  | `Shenora.Core.IUiDispatcher` | `Shenora.Core.Shell.IUiDispatcher` |
  | `Shenora.Core.IWebViewInterceptor`, `WebViewRangeDelivery`, `WebViewResourceRequest/Response`, `WebViewFileOptions` | `Shenora.Core.WebView` |
  | `PlaybackInfo`, `PlaybackCommands`, `PlaybackCommandRequest`, `SafeAreaOptions`, `SafeAreaInsets` | `Shenora.Modules.Platform` |
  | `AndroidPlaybackSession` (was `MobilePlaybackSession`) | `Shenora.Android` |

  An adopter who applies the table alone gets `using Shenora;` and then a CS0246 per type, with nothing
  pointing at the answer — each one is a grep through the kit's source. **A flat `namespace-moves.md`
  (old FQN → new FQN, one line each) would make this mechanical**, and it is generatable from the two API
  baselines the release already diffs, so it need not be hand-maintained.


### 🧹 THREE CLEANUP CANDIDATES FROM THE 2026-08-18 REVIEW — each needs a judgment, not a patch

The review's mechanical findings are fixed (a shared baseline reader, the npm package list read from
config in three places, `outOfScope` back in the walkers). These three are left because each is a
DECISION rather than a defect:

- [ ] **`WindowStateManager.ToPhysical`/`ToLogical` were made internal on the same unfalsifiable
  claim that cost us `DerivedCacheKey`** — "no consumer outside the assembly", which is only checkable
  against this repo. They are pure DPI-aware logical↔physical bounds conversions, and an app persisting
  window state for a window the kit does NOT manage would re-derive them and be silently wrong (a
  slightly misplaced window, no error). ⚠ Same shape as the harvest finding: decide whether the value
  is a CALCULATION (keep internal) or an AGREEMENT with what the kit persists (make public + pin).
  The other 0.11.0 demotions were checked and have better stated reasons.
- [ ] **`WireMirrorTests` has no completeness check**, so a wire family with no C#⇄TS mirror is
  invisible — the same allow-list shape `wire-reference` just had. Confirmed gap:
  `MediaConversionEvents`/`MediaConversionErrorCodes` have no fact and no TS constant at all, while the
  CHANGELOG tells a page to branch on `READY`/`FAILED`. Either add the mirror + the missing client
  constants, or state in the test file which families are deliberately host-only.
- [ ] **Three parsers of `DECISIONS.md`'s entry lines** (`decisions-index`, `decision-audit`,
  `doc-shape`), one of which already carries a comment about the other two having drifted, plus a
  byte-identical `sectionAfter` in two of them. `decisions-index` already exports `readEntries` and is
  the only one handling the `D40 · D41` tombstone form. Consolidating is cheap; the judgment is whether
  the two consumers want exactly its parse.

### 📦 NPM TRUSTED PUBLISHING — owner-side UI, and it cannot be verified from here

Both packages now exist on the registry, so both Trusted Publisher settings pages are reachable. Until
they are configured, a release needs the `NPM_TOKEN` fallback.

- [ ] **Configure the trusted publisher on BOTH packages**: npmjs.com → package → Settings → Trusted
  publisher → GitHub Actions → this repo + `release.yml` (no environment) — for `@shenora/react` AND
  `@shenora/cli`. Then the release runs fully tokenless.
- [ ] **The `@shenora/cli@0.0.1-seed.0` placeholder.** `npm unpublish` works until **2026-08-20 15:27
  UTC**; after that `npm deprecate` is the only tool. (`latest` already points at 0.11.0 — npm
  force-creates `latest` on a first publish whatever `--tag` says, so the stub was briefly it.)
- [ ] After the first OIDC release: require 2FA / disallow tokens on both packages so the trusted
  publisher is the only path, and drop the token-fallback sentence from `RELEASING.md`.

### 🎬 STREAMING — the media tier (D71), two questions left

> ⚠ DIRECTION (owner, 2026-08-14), and it still steers every undecided detail: the tier was built
> AHEAD of an adopter, so **bias anything undecided toward what a later adopter can change** — seams
> over baked-in policy — and write down which choices were guesses, so the first adoption report knows
> what to attack.

- [ ] ⚠ **A frame-index cache: only the COLD-cache case is still open; the CPU case is CLOSED.** Measured
  2026-08-15 (10 min / 89 MiB H.264+AAC, warm): the walk is 65 ms for 40,841 samples while a two-hour
  index would be ~51 MiB — the walk is cheap and the INDEX is expensive, the opposite of the assumption
  that filed this. **Do not build a cache for CPU.** A cold two-hour file is disk-bound and is the only
  remaining argument; it needs its own measurement first, and any cache kept must be bounded by BYTES
  and evict on memory pressure.
- [ ] 🔴 **Android's per-request range cost needs a DECISION, not more work.** `Unsliced` delivery makes a
  `Range: bytes=0-65535` on a 79 MiB film read the whole output (82,843,185 bytes, 117,285 reads,
  26–31 s); iOS gets exactly the window it asked for.
  - ⛔ **Two approaches are CLOSED and the reasons are on `WebViewRangeDelivery.Unsliced` — read them
    before proposing either again.** A seekable body cannot work from this side (the platform's binding
    never calls `Seek`); cheap filler for the discarded prefix stakes correctness on the delivery model
    never changing, and fails silently at the wrong offset when it does.
  - **What is left is one question, and it needs the blocked device run:** is D44 still true on current
    Android/Chromium — is a proper `206` + `Content-Range` honoured now, so the shell could move to
    `Sliced`? That is the only path that removes the cost rather than hiding it. Re-measure the way it
    was measured (bytes + read count + wall clock).

### 🔧 THE BOX REFUSES ~30 % OF CLIPBOARD WRITES FROM A LOOPING TEST PROCESS

🔴 **The code is exonerated — do not "fix" `ClipboardService`.** Diagnosed 2026-08-16: a PowerShell
`Set-Clipboard` loop sharing none of our code fails identically, so this is an OS-level condition on this
machine. `cbdhsvc_*` is implicated (restarting it moved 13/15 → 3–6/15) and is not the whole story.
⚠ **Only the SPREAD means anything** — the same day ran 13-of-15 failing and 15-of-15 clean; a healthy
sample proves nothing. The suite is held out of the gate deliberately (`[Trait("Category",
"RealClipboard")]`, run it with `dev.mjs test clipboard`).

- [ ] **The residual ~30 % is unexplained.** Worth one pass when it next bites: the next suspects are
  another clipboard listener (a manager tool, RDP/VM sync) or Cloud Clipboard. ⚠ Toggling Windows
  clipboard history OFF is the untried experiment; it changes a user-facing setting, so restore it.

### 📋 THE MOBILE CLIPBOARD HAS NEVER RUN ON A DEVICE

The contract and the Windows shell are proven. The mobile halves compile, and the iOS read-back was
rewritten in 0.11.0 to enumerate the pasteboard's own types — which makes a device run more valuable,
not less, because that change is unexercised.

- [ ] **Run the pasteboard paths on a device/simulator, both directions.** What a compile cannot tell
  us: whether several UTIs on one `UIPasteboard` item are read back as one item, whether Android's
  `HtmlText` survives a round trip, and whether an app's own media-type string is accepted as a
  pasteboard type at all. ⚠ **Paste into a FOREIGN app** (Notes, Gmail) — a self round-trip would pass
  even if the kit invented a private UTI nothing else reads.

### 🎧 BACKGROUND PLAYBACK — how long it survives is unmeasured

- [ ] **Nobody knows past ~45 s** (Android 45 s, iOS 43 s, against a 60 s clip, on an emulator and a
  simulator — both gentler than a handset, where Android's freezer/Doze arrives sooner). Minutes are
  unmeasured on both. **A documentation claim to earn, not a defect to fix** — a foreground service is
  the app's to post, which is the split `IPlaybackSession` documents.
  - ⚠ It leaves a documented iOS claim in doubt: *"an `<audio>` keeps playing while backgrounded"* rests
    on a **16.0 s** window, and Android's equivalent dies at ~15.4 s. Too close to ignore before
    promising page-side background audio anywhere.

### 🎬 `SegmentStream` HAS NO REMOTE-SOURCE DOOR, AND IT IS THE ONE THAT NEEDS IT

`MediaConversionOptions.AllowRemoteSource` exists and is exactly right: fail-closed, the APP supplies
the SSRF policy, the kit never fetches (the app's `Convert` delegate does the reading).
`SegmentStreamOptions` has no equivalent — its only source is a path contained against
`MediaAccessOptions.AllowedRoots`.

That is backwards relative to which route benefits. A remote source is most valuable precisely when the
bytes are NOT on the device, and `/_convert/` must read the WHOLE source before one byte plays, so on an
hour-long track it is an hour-long wait over the network. The segment stream answers a manifest
immediately and produces only the pieces asked for — the shape that makes a remote source usable at all.

- [ ] **Add the same door to `SegmentStreamOptions`** — one `AllowRemoteSource` predicate, same
  fail-closed semantics and same "the kit decides, the app reads" split. The engine already takes a
  `string` source and every ffmpeg-backed one passes it to `-i`, so nothing below the option changes.

**Found building it by hand in a consumer** (Yaorin, 2026-08-18): a track the WebView refuses that is not
downloaded had exactly one answer, the server's transcode — CPU and a lossy step spent on a file the
device's own ffmpeg reads fine. Working around it meant forking the kit's `SegmentStream`, teaching it a
`~remote/{handle}` route and inverting the containment: the page cannot name a URL, it registers one and
gets an opaque handle, and the route accepts only handles the registry issued. **That inversion is
probably the kit's answer too** — it is strictly tighter than a URL predicate, because a policy that has
to judge a page-supplied URL can be wrong, where a handle that was never issued cannot be guessed.

⚠ Two things worth carrying into the kit's version if it takes this shape:
- The source string reaches a log by default. A remote one carries the caller's credentials, so the
  `Source` needs a separate log LABEL — otherwise every existing diagnostic line leaks a token.
- Duration and picture must be SUPPLIABLE. Probing them remotely costs two engine launches reading a
  network header before the first manifest can be answered, and the caller usually already knows both.

### 📱 THE iOS DEVKIT — the CLI stops at "installed", and the gap after that is where the time goes

`@shenora/cli` gets an app onto a phone. Everything after that — is it running, what does its engine
support, why did that file not play — has no answer in the kit, and on iOS it has no answer anywhere:
`ios-webkit-debug-proxy` cannot be installed on this project's Mac, and **WebKit does not forward page
`console.*` to the unified log**, so "the page said nothing" and "the page died" are the same silence.

Two pieces, and the second is the one worth stealing.

- [ ] **A remote mode: `shenora ios --host <user@host> …`** (or `"remote"` in `shenora.deploy.json`), so
  every subcommand runs over SSH against a LAN Mac and copies artefacts back. The commands already shell
  out; what is missing is that they assume the shell is LOCAL. The adopter this kit is pitched at — a
  .NET dev on Windows shipping to iOS — never has a local Mac, they have one on the LAN. Measured against
  the README's own thesis ("how little native code an adopting app has to write"), the device loop
  currently costs a Windows adopter **1,752 lines** (Yaorin's `devtools/scripts/mac.mjs` at 1,413, plus
  `mac-transport.mjs` and `lan-discovery.mjs` — and it GREW by 339 in a single day of using it) to
  reimplement what the CLI already does, and the CLI's hard-won checks — `pipefail`, the extension
  verification, the two-devices refusal, the filtered log — get reimplemented or lost.
  - ⚠ **A remote doctor must diagnose the TRANSPORT first.** Reachable? Key authorised? Remote Login on?
    Those are the failures a Windows adopter actually hits and each currently surfaces as a bare ssh
    error with no next step. Two traps to name explicitly, both hit for real:
    **`.local` does not resolve without Bonjour**, and `ping`/`Resolve-DnsName`/`ssh` all fail
    identically — which reads as "the Mac is off" when it is up. **And mDNS is answered BY the device**,
    so a name that does not resolve is evidence about POWER, not about DNS.
    Repeated key attempts also flip the error from `Permission denied (publickey…)` to
    `Connection closed by … port 22` (MaxAuthTries), which looks like a different, harder fault.

- [ ] 🔴 **`shenora diag` — a device that POLLS for work, which is the only remote eval iOS has.**
  BUILT AND PROVEN in Yaorin (2026-08-18, `devtools/scripts/diag-server.mjs` + `devtools/diag/index.html`
  — take them). A standalone service prints a LAN URL; the phone opens it and polls; the operator queues
  actions from their machine and reads results.
  ⚠ **It then solved a problem nothing else could, which is the argument for it.** A LAN Mac had been
  unreachable all day across four wrong theories. Opening the diag page ON the Mac made it check in — and
  the check-in carries the SOURCE ADDRESS, taken from the socket, which is the one fact a device cannot
  state about itself (JS has no access to its own LAN address). That located the machine, and its report
  (`app server reachable 421 ms`) simultaneously ruled out the network, narrowing an all-day mystery to
  one setting. A diagnostic that reports its own address is worth more than one that only reports codecs. **No cable, no pairing, no signed build, no inbound
  access to the phone, and nothing installed on the Mac.** Verified end to end: `report` returned a full
  codec/engine matrix off the device, `eval` ran an arbitrary expression there and returned its value,
  `fetch` measured a URL from the device's own position (status/bytes/ms/`accept-ranges`).
  - **The direction is the whole trick.** Inbound access to a phone needs a cable, a proxy or a signed
    build; outbound needs a page left open. Invert it and a page that can run one line of JS is a debugger.
  - 🔴 **Keep it OUT of the app's own server.** Two reasons, and the second is the one that matters:
    it runs arbitrary JS, which has no business in a product binary where it is one flag from live; and
    **a diagnostic hosted inside the thing being diagnosed dies with it** — the moment you most need to
    ask a device what it sees is when the server will not start or the bundle will not boot.
  - **Split the halves by trust, not by convenience.** Queueing work and reading results decide what
    RUNS, so they are loopback-only; polling and reporting are open, because the device you most need to
    diagnose is routinely the one that cannot authenticate. Verified: operator routes answer 200 from
    loopback and 404 from the LAN, device routes stay open, and queueing from the LAN is refused.
  - ⚠ **Poll with a cursor, never destructively.** `?since=<seq>` makes a poll idempotent (a dropped
    response costs a retry) and lets two devices be driven at once without stealing each other's actions.
    First contact should start at the CURRENT head, or a page opened late replays the whole session's
    backlog — actively harmful on an `eval` queue.
  - ⚠ **A cross-origin `no-cors` fetch is the right reachability probe** from a devtool origin: opaque,
    so no status or body, but it RESOLVES only if the host answered — which is the question being asked.
  - ⚠ **The clipboard fallback is not optional.** A LAN page is plain http, so it is not a secure context
    and `navigator.clipboard` does not exist. Select the text instead, or the report cannot leave the phone.

⚠ **It immediately paid for itself, which is the argument for shipping it:** run against headless Edge
with `--disable-gpu`, it reported `HEVC: ""` where the same engine in a real WebView2 window answers
`probably`. A codec matrix is CONTEXT-dependent — headless is not a proxy for the shipped surface — and
nothing but measuring in the real one would have caught that.

### 🧰 CLI — three things found taking a real app to a real iPhone (2026-08-18, Yaorin)

The CLI did the job the README claims: `ios doctor` printed a clean, specific readout (Xcode · SDK ·
workload · signing identity · device · project · bundleId) and, when the build failed, **named the cause
exactly** — the .NET-for-iOS workload refusing the machine's Xcode — with the two flags that unblock it
and an explicit warning that they are simulator-only. That is the behaviour worth protecting. Three
things got in the way.

- [ ] 🔴 **`npx shenora ios doctor` printed NOTHING and exited 0.** The identical command through the
  binary (`node node_modules/@shenora/cli/dist/cli.js ios doctor`) printed the full report and exited 0.
  A doctor that is silent AND successful is the worst possible pair: it reads as "nothing to report,
  everything fine", so the operator moves on. Whatever swallows stdout under the npx shim, the invariant
  worth enforcing is that **doctor must never exit 0 having written nothing.**

- [ ] 🔴 **`shenora.deploy.json` has ONE `tfm`, but a MAUI app has one per platform.** With
  `"tfm": "net10.0-android"` — correct for this project's Android deploys — `ios deploy` tried to build
  the ANDROID target and died on `NETSDK1147: the following workloads must be installed: android`. The
  error names a workload the operator has no reason to want on a Mac, so it reads as a broken machine
  rather than a config that cannot express the situation. Either take the tfm per platform
  (`ios.tfm` / `android.tfm`), or have each platform subcommand default to its own and treat the flat
  field as an override.

- [ ] 🔴 **A DEVICE BUILD ON A MISMATCHED XCODE IS POSSIBLE, and the CLI could do it automatically.**
  ⚠ **Measured, not reasoned — the mismatch is NOT the blocker it reads as.** A device build runs on
  this exact machine with the pin below, so treat "Xcode and the workload band must agree" as untrue
  until re-measured.

  **The mechanism, which is the part worth stealing.** `ValidateXcodeVersion=false` alone fails on a
  device with a wall of `MT4162 … not available in iOS 26.2 (introduced in 26.4)` ending in `MT2431` /
  `NETSDK1144`, because the SDK selects the NEWEST installed bindings and those reference APIs the
  installed Xcode has never shipped. `MtouchLink=SdkOnly` cannot help: `ManagedRegistrar` walks every
  binding regardless. **The asymmetry is the fix** —

  > bindings NEWER than the SDK → impossible (they name APIs that do not exist)
  > bindings OLDER than the SDK → fine (everything they name still exists)

  so pinning `-p:TargetPlatformVersion=<older>` picks a binding set the Xcode can satisfy. On this Mac
  (Xcode 26.3, packs requiring 26.0 / 26.6 / 27.0) `TargetPlatformVersion=26.0` + `ValidateXcodeVersion=false`
  builds, links, signs, installs and launches on an iPhone 17 Pro.

  **What the CLI could do:** on a version mismatch, instead of only offering a `--simulator` flag, pick the
  newest installed binding set **≤ the Xcode's SDK** and say what it chose and why. That converts the most
  common "this Mac cannot ship" into a build. ⚠ Two riders: the choice must be VISIBLE (silently building
  against old bindings would hide missing APIs at runtime), and it is a dev-loop unblock — an App Store
  build should still match the pair.

  ⚠ **And `codesign` must run in the GUI session.** Over ssh it fails `errSecInternalComponent` — it needs
  the Aqua keychain. A remote-mode CLI (the entry above) has to route the signing step through
  `osascript … Terminal` or it will fail at the very last step of a twenty-minute build.

⚠ Context worth keeping: this Mac reports Xcode 26.3 with `maui-ios 10.0.20/10.0.100 SDK 10.0.300`, and
`ios doctor` calls every one of those `ok` — the pair only fails at BUILD time. Measured across workload
sets 10.0.300–10.0.303.1, **no band ships a pack for Xcode 26.3 at all** (only 26.0, 26.6, 27.0), so an
intermediate Xcode is supported by nothing and `dotnet workload update` merely changes WHICH Xcode is
demanded. The doctor can predict all of this — it is the single most likely reason a ready-looking Mac
cannot ship, and it is knowable before the build.


- [ ] 🔴 **`ios doctor` reports `signing identity  1 found` → `shenora: ready` on a Mac that CANNOT sign.**
  Measured 2026-08-18, then confirmed by inspecting the machine rather than trusting the error:

  | fact | state |
  |---|---|
  | codesigning certificate | ✅ `Apple Development: … ` valid, 1 identity |
  | team known to Xcode | ✅ `IDEProvisioningTeamByIdentifier` → a **free personal team** |
  | **Xcode Apple ID account** | ❌ `DVTDeveloperAccountManagerAppleIDLists` = `{ "IDE.Identifiers.Prod" = ( ) }` |
  | **provisioning profiles** | ❌ none in `~/Library/Developer/Xcode/UserData/Provisioning Profiles/` nor `~/Library/MobileDevice/Provisioning Profiles/` |
  | cached Xcode token | ❌ absent from the keychain |

  The doctor checks only the first row, and the first row is the one that stays green longest. A
  CERTIFICATE is not an ACCOUNT, and neither is a PROFILE: `-allowProvisioningUpdates` needs an
  authenticated account to mint a profile, so all three must hold. The build then fails with
  `No Accounts: Add a new account in Accounts settings` — after a full compile.

  ⚠ **The kit already knows why this recurs and does not connect it to the doctor.** The README says a
  free/personal team's profile expires after 7 days and to re-deploy to refresh it — but re-deploying
  needs the account, so the very configuration the README calls out is the one that silently drifts into
  un-signable while `doctor` still says `ready`. Checking the account list and the profile store would
  turn a 20-minute build into a one-line answer, and could name the 7-day expiry when the team is free.
