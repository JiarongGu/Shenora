# Generic library — generalize the consumer's request, never ship its shape

Shenora is a reusable devkit consumed by several very different apps. The moment an API encodes
one consumer's domain, every other consumer pays for it forever. This is the discipline that
keeps the library reusable (adopted from the family's other library, where it's proven).

## The rules

- **Generalize the request, never ship its shape.** When a consumer needs X, ask what the
  *family-generic* version of X is and ship that; the consumer keeps its specific wrapper. Test:
  would the other three apps use this API unchanged? If a name, field, or default only makes
  sense for one app, it doesn't belong in `src/`.
- **No app/domain vocabulary in `src/`** — no mods, skins, videos, profiles-as-domain, libraries,
  recipes. The generalization of "profile-scoped services" is "an app-defined scope field +
  scoped-container router", not a `ProfileId`.
- **Name every public type for its MECHANISM, never for a scenario or product (D22).** The test:
  *could a consumer whose use case is nothing like the name still recognise this as the thing they
  need?* **Enforced since 0.2.0 by `SurfaceVocabularyTests`**, an ALLOW-LIST of shell/platform words
  (`tests/Shenora.Tests/Api/surface-lexicon.txt`) that every public TYPE name is built from — a
  domain noun now fails the build. Allow-list, not blocklist, for two reasons: a blocklist only
  catches the domain words someone already imagined, and writing the private siblings' domain nouns
  into a tracked file would leak what those apps do. When it fires, renaming is the common answer;
  adding the word is the rare one, and that edit IS the review. It sweeps type names only — member
  names, parameters, csproj descriptions and prose are still on you.
  This is the naming half of D21 and it needs checking SEPARATELY, because the kit twice passed
  D21 on shape while failing it on name. `LoginWindow` held no login logic — it is a busy-gated,
  profile-isolated window running an app-supplied driver until it captures a blob (→
  `InteractiveSession`). `CoBrowseSession` was an off-screen browser that streams frames and takes
  input, i.e. co-browsing OR remote support OR visual capture OR a preview pane (→ `StreamingSession`).
  Neither was a behaviour bug; both worked. The damage is that a scenario name makes the kit look like
  it ships that product, so the next contributor adds more of the product — and it LEAKS across
  features: `SessionController.GetCookiesAsync` returned `IReadOnlyList<LoginCookie>`, forcing a
  streaming consumer to program against a login type.
  **A scenario name in `src/` is a placement smell, not a naming problem (2026-07-31, P7).** There
  used to be an exception here — "a reference driver may name the scenario it demonstrates
  (`CookieLoginFlow`)" — resting on D21 having blessed shipping one opt-in driver, while D21 rested on
  D22 for the name. Circular, and neither ever asked whether a scenario RECIPE belongs in a shipped
  package. It does not: it becomes SemVer surface at 1.0, it makes the kit look like it ships that
  product, and it invites the next recipe in beside it. The driver moved to the sample and the kit
  ships none. **So when a type needs a scenario name to make sense, move it out — do not license the
  name.** Check placement and naming as ONE question; checking them separately is exactly how this
  survived two audits that were each looking at only one half.
  And do not "fix" genuine mechanism vocabulary: `ProfileDirectory` is a Chromium user-data folder,
  `Module` is the kit's composition unit, `ImmersiveDarkMode`/`UserDataFolder` are platform SDK terms.
  **Audit it cheaply:** the API baselines already list every public type and member, so sweep
  `tests/Shenora.Tests/Api/Baselines/*.txt` for domain words and triage by hand. That is how the
  whole-library audit ran; it found the Login cluster was the ONLY real leak, plus one PARAMETER name
  (`driveLogin`) — parameter names count, because the baselines pin them as a source contract.
- **Design against how a real consumer would USE it — then generalize, never absorb (user direction,
  2026-07-31).** Two halves, and both are load-bearing:
  **(a) Go and look.** Before designing a surface, read how the sibling apps solve that problem today
  and what they would need from the kit. The kit exists to stop them re-solving the same thing, so a
  design that has never been checked against a real usage is a guess. Their approach may differ from
  what you would write — that is fine and often informative — but the API still has to MEET the need.
  **(b) Meet the need; do not solve their business.** Shenora is a library, not a business-logic
  solver. Ship the mechanism they can build their product on; leave the product — its domain types,
  its policy, its workflow — with them. The test is D21's: *could a consumer build their own version
  on our primitives without adopting our decisions?*
  Worked example (P6.3a): three siblings had each hand-built "subscribe once to the host's events,
  fold them into state many components read", one of them factoring it out twice after "every
  host-backed store repeated" the same wiring. The kit harvested the MECHANISM — a store fed by a
  module's event stream, snapshot-then-deltas, one subscription regardless of watcher count — and
  deliberately shipped no job/queue/progress TYPE, because what an operation IS belongs to the app.
  It also refused their state library: all three chose the same one, and imposing it would have been
  solving their stack, not their problem.
  **The failure this prevents is BOTH directions.** Designing without looking ships something nobody
  can adopt; looking and then copying ships their business logic into `src/`.
- **Read a sibling to analyse the library's CAPABILITY, not to mirror its implementation (user
  direction, 2026-07-31, after correcting this twice).** A sibling is a CHECKPOINT, never the spec —
  the library is generic and must serve apps that do not exist yet, so the question its code answers
  is *"is this capability present and safe?"*, not *"what method did they write?"*. Turn every
  finding into a capability question one level up before designing:
  their plug-in loader calls `IsRegistered` → **can an app compose its IPC surface dynamically and
  safely at runtime?**; their store helper wires host events once → **can many components share
  host-fed state without each re-wiring it?** Ship the answer to the capability question; their
  method is one instance of it, and copying the method is how a consumer's shape gets shipped.
  **The checkpoint also tells you what NOT to build.** Same survey: runtime UNMAPPING is a real
  capability gap (the pipeline only grows), but no consumer needs it — the surveyed app applies
  plug-in enable/disable at startup — so it is RECORDED as a known limit instead of guessed at. A
  capability nobody has needed is speculation; a capability someone needs and cannot express is a gap.
- **Borrow the family library's PACKAGING model, not its design or verification model (user
  direction, 2026-07-31).** Lyntai is the repo template — versioning, release, docs discipline,
  API-surface baselines — and that is all it is. It is a pure backend, process-driven library:
  functions in, values out, no UI thread, no window, no two-language wire, no browser process.
  **Shenora is substantially more complex**, and the differences are exactly where its bugs live: a
  UI thread everything marshals through, a C#⇄TS contract that must mirror, OS input routing that
  decides who receives a click, real browser processes with profile locks, and now shared client
  state many components read. So "it worked for the other library" justifies a csproj shape or a
  CHANGELOG convention — never a claim that something is tested, safe, or simple here. That is why
  this repo's gate insists on running the sample and on live probes, and why a green unit suite has
  twice been the wrong answer (P5.6's caption buttons; the vacuous containment cases).
- **Seams over flags.** Extension points are interfaces the app implements
  (`IWindowStateStore`, injected scripts, custom schemes, transports), not boolean options
  that switch between two consumers' behaviors.
- **Placement is a design decision, not an accident (D19/D20).** A **portable contract** belongs in
  `Shenora.Core` (`net10.0`); its **platform implementation** belongs in that platform's shell package
  — `Shenora.Windows`, or `src/Shenora.Mobile/` for the shared source behind `Shenora.Android` and
  `Shenora.iOS`. The bar for moving a contract to Core is **"app logic must be able to compile off
  Windows"**, NOT "the signature happens to be platform-neutral" — which is exactly why the whole
  window-state stack stays in `Shenora.Windows` (window geometry is a desktop concept).
- **⚠ If a SHELL implements it, the contract lives in Core — full stop.** Learned by getting one wrong:
  `IFileLockInspector` initially travelled with the file-operation engine out of Core, which would have
  forced `Shenora.Windows → Shenora.IO` for a single interface. Its sibling `IPathLocker` went the other
  way for the opposite reason: advisory lock files are portable, so contract and implementation ship
  together with the engine that uses them. D55 made the packaging half of this moot — everything is in
  Core now — but the rule still decides which FOLDER a contract belongs in, and it is the same rule.
- 🔴 **A new NuGet package is the wrong answer. Ship a FOLDER (D55, 2026-08-07).** The framework is one
  whole: `Shenora.Core` + one shell + `@shenora/react`. There is no "optional features" tier any more —
  `Shenora.Media`, `Shenora.IO` and `Shenora.IO.Compression` were all folded back into Core, and the
  namespaces stayed, so it cost adopters a deleted `PackageReference` and no code.
  **The test is not layering, it is what the package set SAYS the product is.** A nuget.org listing of
  `Shenora.Media` + `Shenora.IO` + `Shenora.IO.Compression` reads as a shelf of single-domain libraries;
  this is a hybrid app framework and those are capabilities it carries. Before defending any boundary,
  ask whether it makes a claim about the product nobody meant.
  ⚠ **Two ways this rule was got wrong, both worth knowing because the arguments were plausible:**
  - **Weight is not a reason (D40 → D53).** `Shenora.Media` was split out because "a demuxer is real
    shipped BYTES", then D51 guaranteed no engine byte would ever ship. The premise died and nobody
    noticed for two days. Weight alone would equally justify splitting anything of a similar size.
  - **"Only SOME apps do it" is not a reason either (D48 → D55).** That test survived D53 and still
    fell: it is a fine *layering* test and it answers a question nobody was asking. Whether the code
    lives in `Core/Io/` or `Shenora.IO.dll` changes nothing an adopter experiences except how many lines
    they paste — and on a trimmed build, not even the size.
  - **Before promising "keep the projects, merge the package", check the dependency DIRECTION.** It was
    the first plan for D55 and it was impossible: `IO → Core` (every type logs through `AppCallback`),
    so a Core that packed `Shenora.IO.dll` would have to reference what already references it. The edge
    decides whether that option exists at all.
- **One shell package per PLATFORM, named for the platform (2026-08-02).** Not per framework, and not
  per sub-area. Windows merged three packages into one because the seam they preserved protected a
  consumer this kit cannot have; mobile SPLIT into two because Android and iOS ship, build and get
  consumed separately. The test both times was the same: does the boundary correspond to something a
  CONSUMER experiences? "WinForms without WebView2" did not. "I am building an Android app" does — and
  **that is now the only boundary the package set draws**, since D55 removed the feature tier. A platform
  is the one thing you genuinely pick.
- **Ask which future changes would be BREAKING rather than additive, and pay for those NOW.** Owner,
  2026-08-02: *"you have to always think bigger than we currently have… a new application with a new
  requirement should also fit."* Audit a new surface for the changes that could not be made later without
  breaking every caller — those are the only ones worth pre-building. Two were found and fixed before the
  mission scheduler shipped, at a cost of one defaulted parameter each:
  - `MissionDefinition.Lanes` was `IReadOnlyList<string>`, one permit apiece. A lane is often a BUDGET
    (memory, VRAM, bandwidth) where items cost different amounts, and adding a cost later changes the
    property's TYPE. `MissionLane(string Name, int Permits = 1)` from the start.
  - **Priority**, defaulted to 0 — which IS plain FIFO. Adding an ordering input to a strictly-FIFO
    scheduler later changes admission semantics for existing callers.
  ⚠ The test is *breaking vs additive*, not *might be useful*. A new method or a new implementation of a
  seam is additive and can wait; a type change, a semantic change, or a parameter that must be threaded
  through cannot. (Harvested from the mission design doc's `A2` when it was retired — D57.)
- **Options records over magic values.** Every number/color/URL a source app hardcoded (dev port,
  background color, timeouts, batch intervals) becomes a documented option with the family-proven
  default.
- **For a whole FEATURE: ship primitives + lifecycle hooks, not the product (D21).** The test —
  *could a consumer build its own version of this product on our primitives, without adopting our
  product decisions?* If not, we shipped too much, or too few hooks. Two symptoms to check for:
  (a) a method that takes or returns **the source app's wire format** (an opaque JSON `string`
  parameter is the tell — a consumer can't know what to pass without reading that app's client);
  (b) a **UX decision** in the surface (which regions are "clickable", what the overlay looks like).
  Both mean the product leaked in. And the mirror-image failure is just as real: no hook for
  *ended/faulted*, so a consumer can't tell a dead session from a quiet one. Good in-repo examples:
  `RenderSessionPool` (pool + session shipped, the app's render/analyze flows deliberately not) and
  `InteractiveSession` (window + protocol + driver SEAM, and the kit ships NO driver — the reference
  one moved to the sample in P7, see the placement rule above).
- **Every public type earns its keep.** Default to `internal`; a type goes public when a consumer
  scenario needs it, not "for flexibility". Public surface is SemVer surface (API-surface
  baseline tests gate it from 1.0). **Cross-package consumption INSIDE the kit is a consumer
  scenario:** a `ProjectReference` does not grant `internal` access, so a helper two packages need
  is public (or it needs `InternalsVisibleTo` per package — the worse trade). This is why
  `WinFormsUiDispatcher` is public (D19/D20); don't "helpfully" demote it.
- **Deviations from a consumer's code are documented at the port site** — when Shenora's version
  differs from the source (a fixed gap, a generalized seam), say so in the code comment so the
  adopting app's migration is predictable.

## Gotchas / traps

- The strongest pull toward specificity is the FIRST adopter: its migration convenience will ask
  for compat shapes (e.g. carrying an uncorrelated flat IPC envelope). Put compat in the
  adopter's shim or an explicitly-named compat option — never in the core contract.
- "Generic" doesn't mean "abstract everything": extraction-first still wins. Generalize what the
  survey shows at least two apps need; leave the rest out (YAGNI) and record the decision in
  `docs/DECISIONS.md`.
