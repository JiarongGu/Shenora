# ROADMAP.md — done + remaining

`## Done` is the durable record (narrative, newest first — what changed, why, how it was
verified). `## Remaining` is the phase plan; items graduate here from `TASKS.md` when finished.

## Done

### 2026-07-31 — P7: the README each package's consumer actually reads, and D10 answered NO

**D10 is resolved: `Shenora.Hosting.AspNetCore` will NOT be built.** It had sat as "a candidate later
addition" since the design contract, which is the comfortable answer — so it was decided on evidence
instead. P6.6 had already surveyed the server-backed sibling, and both of the package's proposed
contents evaporated on contact: the "SPA static-file policy" is **five lines** of ASP.NET there (an
`OnPrepareResponse` setting `no-cache` on the HTML, passed to `UseStaticFiles` and
`MapFallbackToFile`), and the "loopback-gated endpoint helpers" is a **two-line host check** embedded
in a policy written against that app's own threat model — a local page fetching the loopback API and
exfiltrating the response. That second one is the more interesting refusal: shipping a generic
version would be the kit making a security decision on a consumer's behalf, which is worse than
shipping nothing.

Its host→page channel is a one-way event push, exactly what D10 anticipated, and the kit already
covers it; its host-side IPC seam is already `IMessageDispatcher.DispatchAsync`, which an HTTP
endpoint calls directly. So D16's transport pluggability holds with **no new surface at all**. The
two-profile split stands — only the extra package is dropped. The standing test settles it: would the
other apps use it unchanged? Only one of four is server-backed, and even it would keep its own five
lines.

**The README is now written for the person who installed ONE package.** It ships inside every nupkg,
so a `Shenora.Ipc` consumer reads the whole file — and it had been a single feature table addressed
to nobody in particular. It now carries a "Using each package" section per package: what the package
is for, the smallest snippet that works, and the one trap that costs an afternoon (construct the
bridge BEFORE `InitializeAsync` or events emitted during init are lost; `CloseReason.UserClosing` also
means a programmatic `Close()`; an `OperationException`'s message crosses the wire verbatim, so never
build one from `ex.Message`; a folder mapping cannot honour `Range`). Every C# name in it was checked
against the API baselines and every TS name against the client barrel — the discipline `docs/ADOPTION.md`
was written under, because a guide naming a member the library lacks is worse than no guide.

The P2/P3 carry-over landed with it: **stable-chunk frontend build guidance**. Hash the asset
filenames and leave the HTML unhashed, because the host serves HTML no-cache and hashed assets
immutable — a cached HTML file is a user permanently loading a document that references assets you
have replaced. Split vendor code into stable chunks (`manualChunks`) so a one-line app change does not
invalidate the whole bundle for everyone. And the dev-loop trap that has now cost two sessions: a dev
server pre-bundles `@shenora/react`, so after upgrading it you must clear that cache or the page
silently runs the OLD client — imports resolve to `undefined` and the app renders blank while the host
looks perfectly healthy.

### 2026-07-31 — P7 starts by taking a product OUT of the library, and closing the docs gate

**A login recipe was shipping as library surface, and two decisions had been justifying it to each
other.** `CookieLoginFlow` sat in `Shenora.WebView2.Sessions` with `LoginUrl`, `CookieReadUrl`,
`AuthCookiePatterns`, `RevealDelay` and `CaptureAllCookies` — one product's workflow, fully specified,
public and about to become SemVer surface. D21 blessed shipping "one opt-in reference driver"; D22
then justified the scenario NAME on the grounds that D21 had blessed shipping it. Circular, and
neither ever applied D21's own test: *would the other apps use this API unchanged?* Only an app doing
cookie logins would.

It is out. Both decisions are amended: **the kit ships no drivers**, and the "reference driver may
name its scenario" exception is withdrawn — because the naming question was never the real one.
A type that needs a scenario name to make sense is telling you it does not belong in `src/`. The
transferable rule, now in `generic-library.md`: **check placement and naming as ONE question.**
Checking them separately is exactly how this survived two audits that were each looking at one half —
D22's audit swept every baseline for domain vocabulary, FOUND this type, and waved it through on the
naming exception.

Nothing was lost. The driver only ever consumed public seam members
(`InteractiveSession.RunAsync`, `SessionController.GetCookiesAsync`/`NavigateAsync`/`Reveal`/
`SetLoading`), so it ported to the desktop sample as `CookieLoginDriver` unchanged — which is D21's
test passing in the other direction: a consumer really can build it on the primitives. It kept its
tests too (the test project now references the sample), because their invariants are sibling
post-mortems worth keeping green: a STALE auth cookie must not capture, and closing a signed-out
window must not produce an anonymous blob. It also gives `InteractiveSession`'s driver seam the worked
example it never had anywhere.

**The rest of the surface is clean.** A full audit by the documented method — sweep
`tests/…/Api/Baselines/*.txt` for domain vocabulary — found this as the only product leak. Everything
else it flagged is genuine browser or platform vocabulary: `DownloadHit`/`OnDownloadStarting` (HTTP
and WebView2's own event args), `SessionCookie` (a cookie is a browser primitive, not a login
concept), `MuteAudio`, `ProfileDirectory`, `UserDataFolder`, `Module`. One method note for next time:
the first sweep used `\b`-anchored patterns and reported ZERO cookie hits, because a word boundary
does not exist inside CamelCase — `SessionCookie` never matched. A sweep that finds nothing deserves
the same suspicion as a test that passes.

**And the docs gate is on.** CS1591 was suppressed with a note saying the sweep was P7's job; it is
now unsuppressed and, like every other warning, an ERROR. All five packages document every public and
protected member — 24 sites, all constructors, overrides and interface implementations, since Core and
Ipc were already complete. Adding an undocumented public member no longer compiles, which is the
point: a public member is SemVer surface from 1.0, and "document it later" is how an API ends up with
members nobody can explain. Turning it on immediately caught a broken `<see cref="..."/>` pointing at
`OptimizedFormOptions.WndProcHook` for a property that lives on `OptimizedForm` — a CS1574 that had
been invisible for as long as warnings were non-fatal.

One consequence worth knowing: the test project now references the desktop sample, which made
`Every_shipped_assembly_has_a_baseline` see `Shenora.Sample.Desktop` as a new ungated package. Fixed
by excluding the `Shenora.Sample.` PREFIX rather than by naming the assembly — that test was rewritten
in P5.5 H6 precisely to stop being a hand-maintained list, and no real package can ever carry that
prefix.

### 2026-07-31 — P6.5 + P6.6: the last two items, and the last gap the survey found

**P6 is complete.** What remained was guidance and a survey, and the survey found one more real gap.

**P6.5 — portability guidance.** `docs/ADOPTION.md` Stage 4 became the actual recipe rather than a
paragraph of intent: the project shape (plain `net10.0`, and the warning that referencing
`Shenora.WinForms` defeats the guard entirely — the one way this goes wrong quietly), the
contract-substitution table, the "add it to your solution or the guard never runs" step, and an
explicit NOT-portable list so nobody hunts for a contract that deliberately does not exist. The
window-state stack stays in `Shenora.WinForms` on purpose: its signatures look platform-neutral, and
that is not the bar — window geometry is a desktop concept. No D20 amendment was needed; the portable
contract set covered every case the two in-tree exercises hit.

**P6.6 — the remaining targets, read as capability CHECKPOINTS.** Three surveyed, one real gap:

**The gap, closed: a resource handler could only ever answer "200, here are all the bytes."** The
video-library sibling serves local media to its page over a custom virtual host with HTTP `Range` and
206, and carries an ADR explaining why: `SetVirtualHostNameToFolderMapping` cannot honour `Range`.
Shenora's deferred-scheme seam was `Func<Uri, Task<(byte[], string)>>` — the handler never saw a
request header, so `Range` was invisible and **nothing it served could be sought**, and returning the
complete bytes meant a 4 GB file was 4 GB of memory. So the app had bypassed the kit and hooked
WebView2 itself, which is the definition of a capability someone needs and cannot express. It is now a
full request/response seam: `WebViewResourceRequest` in, `WebViewResourceResponse` (status, headers,
content STREAM) out, plus `WebViewByteRange.TryParse`. Not a media feature — conditional GETs,
redirects, per-asset CORS and streaming-without-buffering were all equally unreachable.

The parser ships because each legal form is its own chance to be wrong, and one of them is a trap:
`bytes=-500` is a SUFFIX (the last 500 bytes), not an offset. Its test table uses `bytes=-1` and
`bytes=-5000` deliberately — sabotaging the branch to read a suffix as an offset leaves `bytes=-500`
of a 1000-byte resource resolving to 500 either way, so the obvious test passes while the bug is live.
A start past the end is reported unsatisfiable rather than clamped, because clamping serves bytes
nobody asked for with no error; and the 200 advertises `Accept-Ranges`, without which a media element
will not even attempt a seek — indistinguishable from "seeking is broken".

**Recorded, not built:** the same sibling composites a native player surface with the web view.
P5.6's caption-button clipping is the same mechanism, but the sibling solves it in its own leaf
library and has never asked the kit for it — speculation, so it stays recorded.

**No gap** in the other two. The skin-manager sibling's plug-in SDK is the APP's contract (D21); what
it needs from the kit is dynamic module composition with claim/release and progress-as-notifications,
both present. The server-backed app needs the least of all: it serves over in-process Kestrel, so
`Range` is ASP.NET's problem, and its host-side IPC seam is already `IMessageDispatcher.DispatchAsync`
— an HTTP endpoint calls it directly, so D16's transport pluggability holds with no new surface. Its
profile is shell-only.

The review of this batch found two things in the new code, both about honesty rather than behaviour:
the `Content` stream's doc claimed the host disposes it, which it does not — WebView2 reads it after
the handler returns, so a `using` there would truncate the response, and the host disposes it only
when handing it over failed. And the new 404 shipped an empty body while the kit's policy (P5.5 H3) is
ONE constant body for every 404; it carries that constant now.

**And the last recorded limit is gone: a mapped module can be RELEASED.** The pipeline only ever grew,
so disabling a plug-in, dropping a per-tenant module or unloading a lazily loaded area meant
restarting. `IModuleRegistry` is reshaped to `TryClaimModule`/`TryReleaseModule`, because claim and
release have to be one owner's job — a registry that only remembers a NAME can never take the route
out again, which is exactly why release had been impossible. Two properties had to be right and both
have tests: the claim is ATOMIC (check-then-map lets two threads offering the same plug-in name both
win — the silent-shadowing defect reintroduced as a race), and release is SURGICAL, leaving the
relative order of the error handler, logging, app middleware and the scoped router untouched. It
removes the route and nothing else: in-flight requests finish, and the facade is not disposed because
its lifetime belongs to whoever built it. The original "no consumer has needed it, so do not guess at
the surface" was a sound default and the wrong final answer once the alternative was a SemVer freeze.

### 2026-07-31 — P6.4 follow-up: close the three gaps, instead of recording them

The adapter probes above produced three "the framework almost fits, but…" notes, and the first pass
filed all three as recorded-not-built: each had a workaround, none blocked an adopter. **User
direction reversed that** — *"you really need to close those gaps"* — and the reasoning holds better
than mine did: workaroundable is not the bar when P7 freezes SemVer, and two of the three were
breaking changes that get expensive the moment 1.0 ships.

**A `CancellationToken` now flows the whole dispatch surface** (breaking). `DispatchAsync`,
`SendAsync`, `MessageMiddleware`, `IModuleFacade.HandleMessageAsync` and
`BaseFacade.RouteMessageAsync` had none, so the IPC pipeline was uncancellable end to end and a
handler could not observe a token it was never given. `WebViewIpcBridge` owns a lifetime CTS and
cancels it FIRST in `Dispose`, so work still awaiting when the page goes away finally learns it. The
scoping is the part worth keeping: it is the CALLER's lifetime, **not** per-request client
cancellation — a one-way `post` has nobody waiting, so "stop that operation" stays an app-level CANCEL
route carrying an operation id (D21: what an operation IS belongs to the app). An already-cancelled
token throws INSIDE the try so it maps to `OPERATION_CANCELLED` like any other cancel: one code for
one outcome, and `DispatchAsync`'s never-throws contract is untouched.

**`IEventBus.Emit`** is the fire-and-forget twin of `EmitAsync`. It does exactly `_ = EmitAsync(…)`,
and that is the point: discarding a task is normally a hazard, and whether it is safe here rests on an
internal guarantee (every handler runs inside the bus's guard, so the task cannot fault because of a
subscriber). A caller could only learn that by reading the implementation. The guarantee is the API's
to state.

**`IpcErrorMapping` is public** — `ToError` / `ToErrorResponse`. It was internal because a facade gets
the boundary free from `BaseFacade`; true, and beside the point for the case that found it. An app
whose failures travel as EVENTS has no response to attach an error to, so it had to retype the policy
— which is exactly the fifth copy that type exists to prevent, the one whose doc already warns that
forgetting `ex.GetType().Name` puts a connection string on the page.

**Verified from the consumer's side, not just by unit tests.** 15 new tests (522 dotnet, 85 vitest,
`verify` green), and the throwaway adapter was rewritten to USE all three — its worked-around
constructor token, its discarded task and its hand-rolled error event are gone, and its checks grew
from 17 to 22, including one that cancels a foreign module mid-await through the real dispatcher.

**Two things the work itself taught.** Sabotaging the pipeline to swallow the token did not fail the
suite — it HUNG it, because a handler awaiting `Task.Delay(Timeout.Infinite, ct)` on a token nobody
can cancel waits forever. That is the worst failure mode available here (parallelism once masked a
17-second hang, which is why the suite runs serially at all), so the cancellation tests are bounded
with `WaitAsync`; with the bound, the same sabotage failed five tests in five seconds. And a lambda
parameter named `_` SHADOWS the discard: after `async (request, _) =>`, an inner `_ = SomethingAsync()`
assigns to the token instead of discarding it. It surfaced as a type error in the sample only because
the types happened to differ.

**The review of this batch found one real defect, in the new code.** `WebViewIpcBridge` read
`_lifetime.Token` at dispatch time, which throws `ObjectDisposedException` once `Dispose` has run —
and a message arriving during teardown is the NORMAL case here, not a corner one, because teardown is
exactly when the page is going away. The token is captured once at construction now (a
`CancellationToken` is a struct that stays readable, and still reports the cancellation, after its
source is disposed), pinned by a test that dispatches after `Dispose` and asserts a structured
`OPERATION_CANCELLED` rather than a crash. Sabotage-verified.

Also fixed while promoting the baselines: the API dump rendered `default(CancellationToken)` as
`= null`, because reflection reports a value type's default that way. A human reviews that file on
every surface change, and `CancellationToken cancellationToken = null` reads as "this is nullable",
which a struct parameter cannot be. It renders `= default` now — a rendering-only change, proven so by
normalising the two untouched baselines and requiring an exact match.

### 2026-07-31 — P6.3 + P6.4: the adapters an adopter must write, written once — and what they found

P6 promises an adopting app that swapping the IPC substrate is **two adapters, not hundreds of
edits**: a client shim over its existing `post`/`subscribe` pair, and a host adapter presenting its
own module interface to `IMessageDispatcher`. Per D21 those live in the adopter's repo, so what this
repo owes is only that both are EXPRESSIBLE against the public surface. That was verified the way this
project verifies things — by building them and running them, as throwaways under `devtools/_p6-adapters/`
(gitignored, never packed): 17 assertions on the host side, 18 on the client, each naming what it read,
with preconditions on a separate path that **refuses to judge on an empty read** rather than comparing
nothing and reporting green. Both were sabotage-verified.

They are expressible. The host adapter is ~40 lines over `BaseFacade` and needs **no Windows
reference**, so it re-proves D20 for the adapter layer as a side effect. The client shim is ~40 lines
over `ShenoraBridge`. Adoption also demonstrably *buys* something rather than merely preserving: a
failed one-way send is now attributable to the action that caused it, which the flat uncorrelated wire
could not do, and a throwing handler crosses as `UNKNOWN_ERROR` plus a type name instead of raw
exception text.

**Two real defects, both closed.** The shipped `@shenora/react` declarations named the UMD global
`React` — `dist/useDropZone.d.ts` referenced a type it never imported, so it resolved only when the
CONSUMER's program happened to contain `@types/react` globally, and a perfectly ordinary
`"types": ["node"]` produced TS2503 out of a file the consumer cannot edit (`docs/FIX-LOG.md`). And
`ShenoraEventBus` could only subscribe to an exact `(module, type)` while the host's `IEventBus` had
shipped `SubscribeToAll`/`SubscribeToModule` from the start — the client was the asymmetric half of one
concept, with no supported expression for any observer that cannot enumerate the event vocabulary up
front. Both breadths are now public, delivered narrowest-first, with breadth held in separate
collections rather than a `"*"` sentinel so an app string can never silently become a catch-all.

**The finding underneath both:** P6.2's mapping guide, written against the API baselines, exposed no
gap at all; the adapters exposed two. A document can be written from the list of names, so it only ever
catches a name that does not exist. **Only code that has to EXPRESS something finds a capability that
is missing** — which is the argument for keeping a throwaway consumer in every future phase that
claims a surface is sufficient. The corollary cost this pass too: P6.1's npm consumer exists to catch
exactly the `.d.ts` class and missed it, because its own tsconfig pulled `@types/react` in. A consumer
probe only tests the configuration it happens to have.

Three further "almost fits" were recorded rather than built, per the checkpoint discipline: there is no
`CancellationToken` on the dispatch surface (an application-lifetime token in the facade's constructor
covers the adapter, and per-request cancellation is deliberately an app-level CANCEL route carrying the
`operationId` — what an operation IS belongs to the app); `IEventBus` has no synchronous emit, so an
adapter discards a task at each emit site, which was CHECKED rather than assumed to be safe (every
handler runs through an internal guard, so the task cannot fault from a subscriber); and
`IpcErrorMapping` stays `internal`, because a `BaseFacade` subclass gets the whole error boundary free.
The one thing the kit cannot fix from its side is also recorded: a foreign `HandleAsync` returning
`Task` cannot say "I did not recognise this action", so the adapter must report success for a message
nobody handled — pinned by a test so an adopter is not surprised by it.

**The phase review found a defect in the new code itself** — worth recording because it only exists
because of the new breadths. Delivering the three levels as three separate lookups meant a handler
could subscribe broadly *while handling* and receive the very event it was handling: copying each set
at iteration was sufficient while `emit` touched exactly one set, and silently stopped being
sufficient at three. `emit` now snapshots all three before invoking anything, pinned by a test. That
test then caught the reviewer out once more: the first sabotage written to prove it was itself
vacuous — it rebuilt the array with `.concat`, which JavaScript evaluates just as eagerly, so it
tested nothing and passed. Only restoring the genuinely lazy shape failed it.

`docs/ADOPTION.md` gained the adapter shapes, the firehose guidance, and the trap worth more than the
finding: **an `OperationException`'s message crosses the wire verbatim by design**, so wrapping a
caught exception as `new OperationException(code, message: ex.Message)` bypasses the no-raw-exception-
text boundary completely — and it is exactly the line an adopter would port from a dispatcher that
emitted `$"{action} failed: {ex.Message}"`. Proven by sabotage: with the wrapper in, a planted
connection string reached the client.

### 2026-07-31 — P5.6: frameless caption buttons that behave like real ones

The page-drawn minimize/maximize/close had no hover affordance and no **Snap Layouts**, and the first
attempt at fixing it shipped broken: it answered `WM_NCHITTEST` with `HTMAXBUTTON` on a door the OS
never knocks on, because WebView2 covers the client area with child windows that belong to the
browser PROCESS and cannot be subclassed. Real input goes wherever `WindowFromPoint` lands, so
**coverage was the only lever** — and the answer is a window REGION.

`OptimizedFormOptions.NativeCaptionButtons` cuts the cluster reported through the existing
`SET_CAPTION_BUTTONS` route out of **every direct child that covers it**, making those pixels the
form's own client area; the already-correct hit-test, press/release pairing and hover de-duplication
then run for the first time, and the form paints the three buttons with the standard Windows chrome
glyphs. `CaptionButtonColors` carries the palette on the `TrayMenuColors` split — the kit owns the
renderer, the app owns every colour (D13). The page keeps its title bar, its drag and its theme.

Two shapes were decided rather than assumed. **The kit paints and the app colours it** (chosen by the
user over an app paint-callback; the tray menu was the precedent). And the clip covers **every**
child rather than one named control, because the user asked for the buttons to work behind the
SPLASH — whatever is on top changes over a window's life, so naming one control leaves them dead for
every other phase. `CaptionButtonStateChanged` survives on its own merit: with the option off, an app
that draws its own buttons has no other way to learn hot/pressed.

Verified the way this feature has to be. `WindowFromPoint` over each button returns the FORM while
the same probe beside the cluster returns `Chrome_RenderWidgetHostHWND`; hover was read as SCREEN
PIXELS (`#252525` → `#2F2F2F`, close → `#C42B1C`); the splash phase was proven by asserting the hole
while the splash was still mounted; the user confirmed the flyout. Two defects surfaced that no
compile could find — a hover state that changed without ever invalidating, and a child added after
the rects were reported never being clipped (`ControlAdded` fires before the handle exists) — plus
three probe traps that made correct code read as broken. All in `docs/FIX-LOG.md`; the durable
lessons are in `.claude/knowledge/winforms-shell.md`.

Two window-lifecycle items rode along and closed P5.6 completely. **Maximize+restore now exits an
Aero snap**, as every other Windows app does: `Maximize()` captures `WINDOWPLACEMENT.rcNormalPosition`
— Windows' own restore rectangle, which Aero Snap deliberately leaves at the PRE-snap geometry
(measured) — instead of the live window rect, which is the docked half. That needs no "is this window
snapped" test, for which Win32 has no clean API and which the plan had budgeted a heuristic for.
**Drop zones now clear on DOCUMENT CHANGE** (`ContentLoading`, the signal the IPC ready gate already
re-arms on) rather than on the ready handshake, which removes an ordering contract instead of
documenting it: a `REGISTER` arriving before `READY` used to be wiped after being acked, and React's
child-before-parent effect order made that the default outcome. The four warning sites H7 had to add
are gone, and the app no longer calls `ClearAll` at all.

### 2026-07-31 — P5.5 H9: auxiliary sessions become primitives — and D22, name the mechanism

The last P5.5 batch, and the one a reader changed mid-flight. Suite: **476 dotnet + 63 vitest**,
`verify` PASSED. Only the `Shenora.WebView2.Sessions` baseline moved — the other four stayed
byte-identical, which is the evidence this reshaped one package and nothing else.

**The planned work (D21).** `DispatchInputAsync(string json)` took the ORIGINATING APP'S wire protocol
as an opaque string, so a consumer could not know what to pass without reading that app's client — the
framework's contract was one application's message format. It is now
`DispatchAsync(SessionInput, CancellationToken)` over typed records, with fraction coordinates kept
because that is what makes the protocol resolution-independent, and
`SessionInput.TryParseLegacyJson` as an explicitly-named adoption shim so an existing client keeps its
frontend. `ReadHotspotsAsync()` — a stringly-typed list of clickable rects, i.e. a UX decision — is
gone, its script preserved verbatim in the CHANGELOG so nothing is lost. Frames carry geometry now
(`ChannelReader<SessionFrame>`), read from each frame's own metadata rather than the session's current
viewport: a resize in flight would otherwise mislabel a frame, which is exactly when a mis-mapped click
hurts. And the missing lifecycle hook shipped — `OnEnded` with a reason, fired exactly once through a
shared latch because dispose and a renderer crash genuinely race.

Half of one item was already stale and re-verification caught it: H4.4 had wired `ProcessFailed` to
complete the frame channel, so "the reader waits forever" was fixed months before the item was read.

**The unplanned work, and the better half.** Asked *"why do we have really specific business logic for
login?"*, the honest answer was that we did — and that H4.6 had only half-fixed it. `LoginWindow`
contained **no login logic**: it is a busy-gated, profile-isolated browser window that runs an
app-supplied driver until it captures a blob, which is equally a captcha, a terms acceptance or a
checkout step. H4.6 had renamed the CONTROLLER for exactly this reason and stopped there, so
`SessionController.GetCookiesAsync` still returned `IReadOnlyList<LoginCookie>` — a consumer streaming a
page for remote viewing forced to name a login type.

Pressed further — *"it's about what we're building; for co-browse it should focus on browser hooks,
lifecycle, events instead of a single business need"* — the same fault appeared one level up. The kit
had passed D21 on SHAPE while failing it on FRAMING: `CoBrowseSession` named a type after one product
built on generic mechanics. An off-screen browser that streams frames and accepts synthetic input is
co-browsing OR remote support OR visual capture OR a preview pane, depending only on who wires it. It
also made the package incoherent, since `RenderSession` and `InteractiveSession` are named for what
they do.

So: `LoginWindow` → `InteractiveSession`, `CoBrowseSession` → `StreamingSession`, and both type
families with them (the CHANGELOG carries the full table). `driveLogin` → `driver`, because parameter
names are a source contract the baseline pins. `InteractiveSessionOptions.Title` stopped defaulting to
`"Sign in"` — the one item in that sweep that was behaviour, not prose. `CookieLoginFlow` keeps its
name deliberately: naming the scenario is the entire point of a reference driver. `StreamingSession`'s
doc was rewritten so the LIFECYCLE is the contract — started, navigating/navigated, frames,
ended-or-faulted — followed by what the kit owns versus what the app owns.

**A whole-library audit ran** rather than a spot fix, by sweeping the API baselines (they already
enumerate every public type and member across all five packages) for domain vocabulary and triaging by
hand. Result: the Login cluster was the ONLY genuine leak, and the npm barrel is clean. The false
positives are recorded in **D22** so nobody re-raises them — `ProfileDirectory` is a Chromium
user-data folder, `Module` is the kit's composition unit, `ImmersiveDarkMode`/`UserDataFolder` are
platform SDK terms. D22 states the rule this class needed all along: **name every public type for its
mechanism, never for a scenario, product or business need** — with the reference-driver exception, and
the audit method. It is mirrored into `.claude/knowledge/generic-library.md` so a future session
catches it unprompted.

**The seam is proven, compile-wise.** The sample composes the product over the primitives exactly as
its RENDER route composes the pool: a `STREAM` facade (START/INPUT/STOP) pumping `Frames` out as base64
IPC notifications, plus a `StreamViewer` component sending pointer and wheel input back. Every call is
public API — no internals — which is the seam test passing. The transport being the interesting part is
the point: frames are binary and the bridge is JSON, so the sample base64s them; a server-backed
profile would push the same bytes down a WebSocket and the session would not know the difference. The
sample has not been RUN, so this is a composability proof, not a behavioural one.

Also closed here: the H2/H6-deferred `SessionBrowser` work — the public statics are `internal` (they
took a raw WinForms control and mainly invited bypassing the pool's accounting) and initialization
observes a `CancellationToken`, so a cancelled lease escapes during init rather than waiting out
`InitTimeout` twice. The token gates the AWAIT only: the per-profile environment task is shared across
a pool's instances, so cancelling the creation for one caller would break the others.

### 2026-07-30 — P5.5 H7: tests, docs and dead weight — and a 17-second hang parallelism was hiding

The hygiene batch found a real defect, which is the argument for doing hygiene at all. Suite:
**442 dotnet + 63 vitest**, `verify` PASSED.

**The find.** With the suite forced serial, `WindowCommandFacadeTests`' `START_RESIZE` case took
**16.9 s of 26.8 s** — and hung indefinitely when run alone. H4.2 had made
`WinFormsUiDispatcher.Post` run a body INLINE when the caller is already on the UI thread (correct:
the OS move/size loop must start while the mouse button is down), and that test creates its form on
the test thread — so `SendMessage(WM_NCLBUTTONDOWN)` executed synchronously and entered the modal
size loop *inside the test*. Its own "deliberately NOT pumped" comment had been false since H4.2, and
collection-level parallelism kept the wall clock at 6 s so five phase reviews never saw it. The fix
is test-only, because the production behaviour is right; `WindowCommandFacade.Post` now documents the
accepted consequence — those two routes answer only after the user releases the mouse.

**Parallelization control** landed as `xunit.runner.json` with `parallelizeTestCollections: false`,
chosen by measurement: parallel 6 s (masking the hang), serial-with-hang 28 s then 1 m 6 s, serial
once fixed a steady 9–10 s. Serial is self-maintaining — a future pump test needs no `[Collection]`
attribute — and it is what surfaced the defect. The file is declared explicitly in the csproj because
xunit's auto-include glob did not copy it, and a runner config the runner ignores is worse than none.

**Test doubles collapsed to one owner each, every owner a SUPERSET of what it replaced** — which is
why nothing regressed on the way in. `Sta.Run` (3 remaining copies: they had `ExceptionDispatchInfo`
but a bare unbounded `Join()`; the shared one has both, so a deadlocking body fails instead of
hanging the suite), `FakeWindowStateStore` (3 fakes; seed and assertion target are deliberately
separate members), `IpcRequests.Create` (5 factories over 4 signatures; the part worth owning is the
`Payload` null-means-absent rule), `TempDir` (all 7 create/delete pairs; cleanup is best-effort
because four copies threw FROM `finally` and replaced the test's real failure with an IO error). On
the npm side one shared `FakeTransport` replaced 4 classes and 2 inline literals, and it builds host
replies from the exported `IpcCategories` — all four copies hand-wrote `{ category: 'ipc' }`, so they
could have drifted from the wire contract together and stayed green.

**A second vacuity find.** Four of the seven `File_mode_refuses_paths_that_escape_the_root` cases
were **passing with containment deleted**: they asked for `../secret.txt` while the fixture's only
outside file was named `shenora-outside-marker.txt`, so the traversal resolved to a path that merely
did not exist. Only the three rooted cases (landing on the real `win.ini`) did any work. The fixture
now puts the escape target where the requested paths actually point, asserts its existence as a
precondition, and the whole set was sabotage-verified.

**Gates added where the npm half had none:** `vitest.config.ts` + `vitest.setup.ts` (there was no
config at all, so `globals` was false, so RTL's `afterEach(cleanup)` never registered and every
un-unmounted `renderHook` stayed live for the rest of its file — `globals` stays false and cleanup is
registered explicitly instead); a barrel test pinning the 21 runtime exports as an explicit array
rather than a snapshot, since a snapshot self-updates under `-u`; 5 tests for
`createWebView2Transport`, which had zero references while being the transport every real consumer
runs on; and a `doctor` check that fails if `dist/testing/` exists, because `src/testing/` had to be
excluded in `tsconfig.build.json` or `files: ["dist"]` would have published the test double to npm.

**`SessionBrowserOptions.RequestFilter` is covered at last** (15 tests). Its decision was lifted out
of the `WebResourceRequested` lambda into `internal SessionBrowser.ShouldBlockRequest` — the same
"make the REAL path testable" move as the pool's reset probe, and the reason the `about:blank`
normalization now has a test: without it a same-host filter treats a reset pool instance's own first
document as third-party and 403s it. The rest of the sessions cluster is e2e/manual **by
construction**, not by neglect — `SessionController`'s constructor subscribes to
`_web.CoreWebView2.WebMessageReceived`, so the type cannot be instantiated without a live browser —
and `docs/REVIEW-GUIDE.md` §6 now states that boundary so it stops being re-filed as a coverage gap.

**Four implementation-detail assertions became assertions about the actual invariant:** an exact
exception-message sentence → contains the key and leaks neither the raw value, the CLR type nor the
JSON path; an internal type's NAME → the tray renderer's `ColorTable` really carries the app's
colours (the old test would have passed a renderer that ignored every colour it was handed);
`Controls[0].Controls[0]` → named `internal` accessors with layout expectations derived from
`SplashPanelOptions` instead of retyping its defaults; exact STJ digit padding → no comma-decimal
plus a parsed value, so changing a format string no longer fails a *culture* test.

**Dead weight and one documented contract.** `grep TODO src/` is empty (`'TODO'` was the example
module name in shipped npm docs — the example domain is now `NOTES`); two stale comments fixed; the
sample's `dropClassName` finally has a CSS rule, so the e2e subject can show the hover half of the
drop contract; and `void getBridge().notifyReady()` became a real `.catch`. The
**`notifyReady` → `ClearAll` ordering contract** is now written at four sites plus
`ipc-contracts.md`: the host clears drop zones on the handshake, so a `REGISTER` arriving first is
wiped *after being acked*, and React's child-before-parent effect order makes that the DEFAULT
outcome rather than bad luck. Making it order-independent (clear on document change instead) is
recorded in `TASKS.md` as its own item — that is a lifecycle change, not hygiene.

**Docs drift turned out ~80% stale** — earlier batches had already fixed most of the list. The four
genuine items: `ARCHITECTURE` never listed `Shenora.Sample.Logic` (the H4.3 portability proof) and
named none of the five public extension classes; `CHANGELOG` had two separate `### Breaking` groups
under one version, which matters because that heading is the SemVer gate and a reader would have
missed five entries; and — not on the list — `ipc-contracts.md` still said the ready gate re-closes
on `NavigationStarting`, which H3 changed to `ContentLoading`. `REVIEW-GUIDE` §6 was stale too.

H8's last two items closed with this batch: the `SemaphoreSlim.Dispose()`-wedges-a-cancelled-waiter
root cause is now a rule bullet, and `knowledge footprint` confirms the core tier is unchanged at
**15.6 / 16.0 KB** (H7 grew only the on-demand tier).

### 2026-07-30 — P5.5 H6 (part 4): the surface trim and the npm packaging gaps

`DpiHelper.ScalePixels`/`ScaleSize`/`ScalePoint` removed — and they were worse than merely unused:
each baked in the PRIMARY monitor's scale, so any code that adopted them would have silently
mis-scaled on a secondary monitor. `Scale` plus the DPI you actually mean replaces them, and the
consumer their own docs named (the drop-zone overlay) already converts from the control's
`DeviceDpi`, which is the correct source. On the npm side: the `declare global { Window.chrome }`
augmentation is gone (it collided with `@types/chrome` as an unfixable TS2717 in a `.d.ts` the
consumer cannot edit — a library must not claim global names), `"./package.json"` was added to
`exports`, and the tarball now ships a LICENSE with `doctor` checking it byte-matches the root one.
**H6 COMPLETE.**

### 2026-07-30 — P5.5 H6 (part 3): the facade registration seam — no more downcast

The reference composition had to write `if (dispatcher is MessageDispatcher concrete)` to map its
window-facing facades after the form existed, and that `if` had **no `else`**: any composition that
registered a different `IMessageDispatcher`, or wrapped it in a decorator, silently dropped WINDOW,
DROP_ZONE and RENDER — and the only symptom was a frameless title bar that stopped working. Adopters copy
that branch.

Neither option the review offered turned out to be right. Its recommendation — have the facades resolve
the form lazily through `IFormInteraction` and register via `AddModuleFacade` — does not work for two of
the three: `DropZoneFacade` needs the live `DropZoneManager` (which needs the WebView2 control) and the
RENDER route closes over the form's session pool, so neither is resolvable from DI before the form exists.
Its alternative — widen `IMessageDispatcher` with the whole `Map*`/`Use*` family — was correctly judged
too large.

What shipped is smaller than either: **`Use(MessageMiddleware)`, the one primitive every helper already
delegated to, moved onto the interface, and the six helpers became extension methods over it.** So the
interface stays at the four things a dispatcher genuinely is — dispatch, two sends, compose — a decorator
has four members to write rather than ten, and every helper works on any implementation for free. Two
tests pin it: late mapping straight through the interface, and a pass-through decorator, which is the
exact shape that used to make three modules vanish.

One deliberate oddity, documented at the site: `MessageDispatcher.Use` is declared twice. C# forbids a
covariant return when implementing an interface, so the explicit implementation returns
`IMessageDispatcher` while the public method keeps the concrete type for existing fluent chains.

Also fixed here: `WindowCommandFacade`'s registration doc pointed at `AddMessageDispatcher`'s configure
callback — a path that CANNOT work, because that callback runs at provider-build time, before any form
exists.

### 2026-07-30 — P5.5 H6 (part 2): the bugs hiding inside the surface-trim list

The trim bullet mixed cosmetics with real defects, so the defects went first and on their own.
`MessageDispatcher.Use()`'s unsynchronized `Lazy` + `List<T>` swap meant a concurrent dispatch could
read the OLD cached pipeline and answer `NO_HANDLER` for an already-registered route — now
copy-on-write plus a volatile pipeline under one lock, with a test hammering 200 late routes against
continuous dispatch. `IpcErrorCodes.OperationCancelled` was added with its mapping arm placed AFTER
`OperationException`, so an app that models cancellation in its own words keeps them.
`ScopedContainerRouter` retries once on `ObjectDisposedException` (guarded on `!_disposed` so a
shutting-down router cannot spin), because `InvalidateScope` is a documented app-facing call that can
fire mid-request. `EventBus` gained its convenience-overload guards and publishes `_patterns` LAST —
which is what `EmitAsync` enumerates, making its `continue` comment true at last.
`ShenoraPathsOptions` became a `record` using `with`, since the `--app-root` merge hand-copied six
properties and would have silently dropped a seventh. Two **breaking**: `CreateError`'s argument
order aligned to `OperationException`'s (wire-relevant `parameters` first) and `BaseFacade`'s stray
`ConfigureAwait(false)` removed, as it contradicted the documented context-preserving model.

### 2026-07-30 — P5.5 H6 (part): the gates that were supposed to be watching

Four items, and the theme is that three separate gates existed but were not actually looking at anything.
421 dotnet + 54 vitest.

**The API baseline was blind to most of what breaks a consumer.** It dumped
`GetMembers(BindingFlags.Public)`, so `BaseFacade.RouteMessageAsync` — the one member every consumer
overrides — was outside the SemVer gate entirely, and so were default parameter values (dropping a
`= null` is a source break for every caller and produced NO diff), `init` vs `set`, `required`, `static`,
parameter names, generic constraints, nullability, and attributes. `[JsonPropertyName]` being invisible
was the sharpest one: those 22 names ARE the wire contract, so renaming one broke the C#⇄TS mirror while
every test stayed green. The new `ApiSurfaceDump` renders all of it. Three of its decisions were wrong on
the first attempt and are documented in the file: an unconstrained `T` reads as Nullable at runtime, so
annotating it printed a `?` that does not exist in the signature; the compiler's `[Obsolete]` ctor stub on
a `required` type carries SDK-version-dependent text that would churn the baseline on a toolchain update;
and C# aliases beat `System.Void` because a human reads this file on every intentional change.

**The cross-language mirror was asserted on both sides and compared on neither.** Each suite checked its
own hand-written literals, so `SCOPE_REQUIRED` sat in the host's `IpcErrorCodes` — emitted by
`ScopedContainerRouter` — while being absent from `types.ts` for two phases, all under documentation
claiming a name-for-name mirror. `WireMirrorTests` now parses the TS source (what an adopter imports) and
asserts set equality for error codes, the handshake route and the envelope categories. Client-only codes
are excluded through a new `ClientOnlyIpcErrorCodes` export, so the client declares its own exceptions
rather than the test carrying a second list to drift. I confirmed the tripwire fails by removing the code
again — a green tripwire that cannot fail is worth nothing.

**The client tests were type-checked by nothing at all.** `build` uses a tsconfig that excludes them,
vitest transpiles without checking, and the tsconfig written to do the job had never been run — it was
red on an ES2020 `lib` against `.at()`. That was discovered while fixing `BaseModuleService`'s constraint,
whose whole point is compile-time checking: the `@ts-expect-error` assertions pinning it would have been
inert. Fixed the lib, added a `typecheck` script, wired it into `verify`, and proved it by reintroducing
the anti-pattern and watching TS2578 fire.

Also: the client event bus keys on `'\0'` instead of `.` (so `("APP","TASK.DONE")` and
`("APP.TASK","DONE")` stop being the same key — the collision the host fixed and documented while the
client kept it) and gained the scope filter the wire always carried, mirroring the host's rule including
the half that is easy to miss: a global event still reaches a scoped subscriber.

### 2026-07-30 — P5.5 H3: the ready gate, option validation, and the fail-loudly cases

Thirteen new tests (417 dotnet). Three of these are worth reading past the one-line summary.

**The ready gate.** It closed on every `NavigationStarting` while the client spends its single `READY`
per real page load — so any navigation that never replaced the document (cancelled by an app tap or a
policy, or failed before committing) closed the gate FOREVER on a page that was still perfectly alive:
notifications buffered to the 10 000 cap and then silently dropped the oldest, for the process lifetime.
It now closes on `ContentLoading`, raised only when a new document actually begins loading, and on
`ProcessFailed`, which the bridge watches itself rather than trusting the host's optional auto-reload
policy. The trade is recorded at the site: between `NavigationStarting` and `ContentLoading` the gate
stays open, so a flush there reaches the OUTGOING page instead of buffering for the incoming one — which
is the better outcome, because those listeners are still attached and these are progress/status events.

**Where "fail loudly" belongs.** The review asked for a mistyped `ResourcePrefix` to throw, and the
obvious place — the provider's constructor — turns out to be wrong: a provider with nothing to serve is
CORRECT when the page loads from a dev URL, which is the normal state of a fresh clone whose bundle has
not been built. The sample's own csproj documents that shape. So the provider reports it (`CanServe` plus
a notice naming the bad prefix and the assembly's real manifest prefixes) and the throw lives in
`WebViewHost.AssertBundleServable`, the only place that knows the bundle IS the start document. The probe
is `Exists("index.html")` — which incidentally gives that member the consumer H6 was going to delete it
for, and catches a present-but-incomplete bundle too.

**Terminal states, not just rate limits.** The renderer auto-reload was throttled to once per 10 s and
had no stopping condition, so a page that faults during load reload-crashed forever, spawning a renderer
each time — while the option's own doc promised "a crash-looping page must not spin". A cooldown is not a
terminal state. `MaxAutoReloads` is, and the give-up logs exactly once so the log doesn't become the new
spin. The same shape appears in `WebViewEnvironment.GetSharedAsync`, which cached its task faulted or
not: one transient failure was terminal for the whole process, including the retry its own timeout
message asks for. Both now distinguish "in flight or succeeded" from "failed, try again" — the rule
`SessionEnvironmentCache` was written to in H2, now applied to the original it was contrasted against.

Also: six options validated at construction (each previously failed somewhere unrelated to its cause —
the worst made `Enqueue` dequeue what it had just enqueued, silently discarding every notification for
the process lifetime), and exception text removed from all three 404 response bodies, which were readable
by page script under `Access-Control-Allow-Origin: *`.

### 2026-07-30 — P5.5 H2 (client): the `@shenora/react` tail — **H2 IS COMPLETE**

Seven client-side defects, +10 vitest (49 total). The two that needed thought rather than a patch:

`useDropZone` never registered a target that wasn't mounted on its first effect run. The tempting read
is "wrong dependency array", but a `RefObject` is a stable object and a ref mutation triggers no render
at all — so there is nothing for a dep array to observe. The fix makes the ref's CONTENT reactive: a
`useState` element mirrored by a deliberately dep-array-less effect (`setElement` with an unchanged
value is a React no-op, so it cannot loop). The public API stayed exactly as it was, and a
conditionally-rendered target now works instead of being silently dead for the component's whole life.

`BaseModuleService` captured its bridge in a constructor default — evaluated at construction — while
`configureBridge` DISPOSES the bridge it replaces. So a module-level service singleton, which is the
normal way to write one, held a bridge that startup then killed, and every request from it rejected with
"Bridge disposed" for the rest of the session. Now resolved per call through a `protected get`, so
subclasses keep using `this.bridge` unchanged; there is a test that an explicitly-passed bridge is still
honoured, because lazy resolution quietly ignoring it would break the multi-transport case.

The rest: a literal `null` host message (valid JSON) threw a `TypeError` out of the transport listener;
`isAvailable` ignored `disposed`; the `fallback` path bypassed the timeout (and only a THENABLE is raced
— a plain value has already settled and must not be made async); `useWindowMaximized` fired one IPC
round-trip per `resize` event, ~180 over a 3-second drag, each arming a 30-second timer; and
`useShenoraQuery` blanked good data when a refetch failed, turning a recoverable error into an empty
screen — it now reports both so the caller can show stale data with a banner.

Also here: the `debounce`/`randomUUID` helpers H4.5 deliberately left duplicated moved into a new
non-exported `internal.ts`. H4.5's reason for waiting was that the package had no shared-internals home
and inventing one for a single consumer is speculation; `useWindowMaximized` needing the same debounce
is the second consumer that justifies it.

### 2026-07-30 — P5.5 H2 (WinForms): the shell robustness tail + the `winforms-shell` rule

Nine items in `src/Shenora.WinForms/`, the layer under everything else — which is why its failures look
like anything but a window bug: a resident process nobody asked for, a stale profile lock, a maximize
button that stops working, a suite that stalls with no failing test. 404 dotnet + 39 vitest.

Two judgement calls are the interesting part. The form-level `AllowDrop` was **removed outright** rather
than option-gated, because the premise behind it was false: OLE registers drop targets PER HWND and
`DropZoneOverlay` registers itself, so nothing ever needed the form's drag events. What it actually did
was force OLE — hence STA — on every consumer of the base class, and show a copy cursor for a drop it
then silently discarded (there was no `DragDrop` handler). The existing test asserting
`AllowDrop == true` carried that false premise in its own comment. And `TrayIcon`'s wrong comment was
fixed as DOCUMENTATION rather than code: `CloseReason` genuinely cannot distinguish the user's X from a
programmatic `Close()`, so the honest fix is telling adopters to close via
`ExitApplication()`/`Application.Exit()` — now stated on the `CloseToTray` option itself, where the
decision gets made, not buried in a private handler.

The rest: `Initialize` fails fast on a non-STA thread with the fix in the message and is idempotent; the
crash dialog is one-at-a-time per thread, because `MessageBox.Show` pumps and a recurring exception
stacked dialogs unboundedly (driven by a new internal `ShowDialogOverride` seam — a real MessageBox
would hang the suite, and the re-entrancy IS the invariant, so it had to be testable);
`SecondaryWindows` cleans up only after `Application.Run` returns, removes a phantom entry when
`thread.Start()` fails, and replays a pre-handle `Activate`; `SingleInstanceGuard.TryAcquire` is
idempotent against a per-thread-reentrant mutex; `OptimizedForm` re-fills on DPI/display change and
validates its restore rect through `WindowStateManager.IsVisible`; `SetTextAsync("")` clears.

H8's last owed file, `winforms-shell.md`, landed with it — and its `RULES_INDEX` row pushed the
always-loaded tier OVER budget (16.4 / 16.0 KB), which was paid for by a real trim rather than a
cosmetic one: the "known gate holes until H5 lands" text in `CLAUDE.md` and `phase-workflow.md`, and the
guard's "current limits" list in `sensitive-info.md`, were all stale — H5 closed those holes — and were
actively instructing future sessions to distrust a gate that now works. Back to 15.6 / 16.0.

### 2026-07-30 — P5.5 H2 (callbacks): no app-supplied delegate runs unguarded, kit-wide

Closes the H2 item that had been open since the first full review, and the answer turned out to be
structural rather than a sweep: **one owner**, `Shenora.Core.AppCallback`, public because three
packages consume it (the D19/D20 placement law). The pattern had been guarded per-site by memory, which
is precisely why it reopened three times — H4.2 fixed the facades, the sessions batch discovered that an
`ILogger` is app code too, and this batch found the rest.

Two things here are more than "wrap it in try/catch". First, **guarding is not enough where the kit
still owes the event an answer**: a failed `OnDownloadStarting`/`OnPermissionRequested`/`OnProcessFailed`
now falls back to the built-in policy, because an un-cancelled download proceeds, an unanswered
permission request stalls whatever asked for it, and a renderer crash goes unhandled at the exact moment
things are already going wrong. `WndProcHook` falls back to "did not handle this message" — a throw
there surfaces as WinForms' own BLOCKING modal dialog mid-message-dispatch, on a window that may not be
visible yet. Second, **log calls became lazy** (`Log(Func<string>)`), because the guard has to cover
building the message as well as writing it: several messages read WebView2/COM properties that throw
once the underlying object is gone, and interpolation at the call site sits outside the guard. Several
of those sinks live inside a `catch` that exists to stop a failure escaping, where a throwing sink
defeated the very thing it was reporting from.

Also fixed here: `SessionController`'s four driver-tap collections were plain `List<T>`, appended from
the driver's thread while the WebView2 handlers read them on the UI thread. `ToArray()` reads the count
then copies the backing store, so an `Add` in between throws or copies a torn view and two concurrent
`Add`s corrupt the list — and the `.ToArray()` at the read site *looked* like the fix for exactly this.
Copy-on-write arrays published under a lock; readers now take no lock at all.

### 2026-07-30 — P5.5 H2 (sessions): the lifetime cluster in `Shenora.WebView2.Sessions`

Six review findings that all live in the same three files, done together because they interlock. 381
dotnet + 39 vitest, `verify` PASSED. No P0s remained anywhere before this batch; these are the P1/P2
tail that a consuming app cannot work around.

**The pool can now recover from a wedged page** — and this is the batch's real lesson. H4.2 had already
made the *caller* escape a page blocked in its own script thread (the marshal observes its token), and
that looked like the fix. It wasn't: `WaitAsync` hands the caller back but cannot kill the outstanding
call, so `DisposeAsync` returned the dead instance to the pool, the reset reported success, and the
next lease inherited the corpse. So the missing halves landed here — a per-operation cap (`OpTimeout`;
note every parameterless overload passes `CancellationToken.None`, so the *default* caller had no
escape at all) plus poisoning the instance so the return path discards it. Completion is tracked with a
flag in the body's `finally` rather than inferred from the exception, because a body that ran and threw
(a rejected URL, a guard refusal) leaves a perfectly reusable instance and discarding it would cost a
browser startup on every ordinary error.

**Two invariants were documented but unreachable.** The reset-to-`about:blank` swallowed its own
timeout and returned `true` unconditionally — its comment even argued the case ("the next lease
navigates away regardless"), which is the error: a renderer that can't answer `about:blank` can't
answer the next lease either. So "a failed reset DISCARDS the instance" only fired if the navigation
threw. The test pinning it drove the override, never the real path, which is precisely how it passed
five phase reviews; the decision now sits in a seam the tests reach. Likewise a cancelled start: both
the pool and co-browse checked the token only *before* the multi-second browser init, so anything
cancelled during the expensive part still published a live off-screen window and a browser process
holding the profile lock — with no owner left to dispose it, since the caller got a cancellation
instead of a handle.

**One environment per profile, and the shape is the interesting part.** `InitTimeout` abandons the
*await* on `CreateAsync`, never the creation, so every retry against a profile a zombie process held
queued another browser process onto that same lock — growing the lock the timeout's own message
blames. A pool now shares one environment and a retry joins the in-flight creation. The cache is
**owner-scoped, not static/profile-keyed**, for a reason worth keeping: a live environment keeps its
profile's browser process and therefore the folder's OS lock alive, so a process-lifetime cache would
have made `LoginWindow.ClearProfile` — the call that makes a logout a *real* logout — fail every time
rather than only while a window is open. A login window opens one profile once and gains nothing from
caching; a pool creates N instances on one profile, which is the case that does. Owner scoping also
makes it single-threaded by construction, which matters because `CoreWebView2Environment` is
thread-affine. And a faulted creation is deliberately not cached — the trap `WebViewEnvironment` still
has, still tracked under H3.

**Two silent-by-construction bugs.** The co-browse CDP screencast receiver lived only in a local, so
the subscription's survival depended on the SDK caching it internally and a stream could stop after an
arbitrary GC with no error at all. And `RenderSession.OnNetwork`/`OnMessage` were the only public
members with no disposal check *and* the only two that install a persistent tap — so a late subscribe
attached a live listener to a pooled instance the next lease owned, streaming its API responses and
posted messages to the previous caller.

**The phase review earned its keep on this batch's own code.** An `ILogger` is app code, so the
"no app-supplied callback runs unguarded inside a UI-thread event handler" rule applies to it — and the
logging added in H4.7 invoked it bare at all eight sites in the package. Three of those turn a log line
into a real failure: in the instance-creation `catch` a throw escaped before `TrySetException`, hanging
the lease and holding its permit; in the return body it escaped before `_capacity.Release()`; and in the
three WebView2 event handlers there is no caller on the stack at all. All eight now go through an
internal `SessionLog.Try`, with a regression test driving a logger that throws on every call. This is
exactly the finding class the review checklist was extended with after the first full review, and it
caught it on the first pass.

Testing note carried forward: the new `StalledAnchor` helper realizes a handle on its own never-pumped
thread. That detail is load-bearing — an anchor on the test thread runs bodies INLINE via the
dispatcher's correct fast path, so "just don't pump" would have proven nothing.

### 2026-07-30 — P5.5 H4 COMPLETE: H4.2 (rest) + H4.3 + H4.4 + H4.5 + H4.6 + H4.7

The re-layer's payoff, landed in three commits. **H4 is done.**

**H4.3 — the portability guard.** `samples/Shenora.Sample.Logic` is plain `net10.0` referencing only
`Core` + `Ipc`, with a facade that picks a file, uses the clipboard, opens a URL and reads UI-thread
state. It compiles — which is the proof D20 was asserting. It works by what it *cannot* reference, so
dragging a Windows type into one of those contracts turns it red.

**H4.4 — the session edge carries something.** Scoping judgement worth recording: what crossed was
the *invariant* (`BrowserArguments.Compose` — single-occurrence feature switches, plus the dev CDP
re-append the sessions package had re-broken by hand), NOT the app-shell preset. The session argument
preset, event policies and environment caching legitimately differ — an app shell opens external
links in the system browser, an unattended session must open nothing. Sharing those would be coupling
dressed as dedup. That unblocked the behavioural half: the three policies `extraction-sources.md`
lists as must-fix, which P5 shipped without. `ProcessFailed` in particular closes a hang — a dead
renderer was invisible, so the pool reset and re-leased the corpse forever and a co-browse frame
channel stopped with its reader waiting. Script dialogs off too (an `alert()` off-screen blocked the
JS thread behind a dialog nobody could dismiss).

**H4.2 (rest) — the sessions marshals.** `RenderSession` now observes the cancellation tokens it
accepts, which is H2's pool-starvation P0: the dispatcher's `WaitAsync` lets the caller escape even
when the UI thread never runs the body. `SessionController`'s inverted pre-handle guard is gone — its
own comment described the trap and the next line committed it. `CoBrowseSession` uses the
never-faulting overload, so its "one bad input message must not fault the session" contract survived
the collapse; that overload exists *because* an adversarial design review predicted this exact site.

**H4.5 — eight duplicate families collapsed**, each with a defect attached rather than mere
repetition: the IPC error boundary existed four times (a fifth copy is how a raw `ex.Message`
eventually reaches a client); `WebViewHost`'s open-a-URL was a drifted copy missing the Win11 handle
`Dispose`; the tray's bring-to-front omitted `SetForegroundWindow`, so restoring from the tray behind
another app could leave the window hidden; one `DeviceDpi / 96` used integer division and none
guarded a non-positive DPI; and the off-screen park coordinate had a THIRD site inferring
on-screen-ness from a *different* threshold, so moving the park position would have silently broken
reveal detection. Two were deliberately left alone with reasons at the site.

**H4.6 — `LoginWindowController` → `SessionController`.** Done as a rename rather than a base
extraction, because the rename is what fixed the actual problem: `CoBrowseSession.Controller` is
public and was typed with a login-named type, so a co-browse consumer had to program against
"Login…". Login-specific types keep their names. What the shared core *should* be is deferred to H9,
where the co-browse API is reshaped anyway (D21) — better decided there than guessed here.

**H4.7 — the sessions package can be diagnosed.** It shipped with no logging of any kind against ~30
swallowed catches; `Log` options now exist on the browser/pool/co-browse options with messages at the
init, policy, discard and poison paths.

Verified: `dev.mjs verify` PASSED — 357 dotnet + 39 vitest, sample web typecheck, sensitive scan,
knowledge check, doctor. Four baselines were reviewed line by line and promoted deliberately across
the batch. One honest loose end: a single test run reported 1 m 15 s instead of the usual 4 s; three
consecutive re-runs came back at 4 s with no test over 1 s, so it is recorded as
unexplained-but-not-reproducible (most likely MSBuild nodes from the build in the same command)
rather than written off as a flake.

### 2026-07-30 — P5.5 batch H4.2 (part): the marshal copies outside the sessions package

Six sites now route through `WinFormsUiDispatcher` instead of hand-rolling the
handle/thread/guard decision: `FormInteraction.SetEnabled`, `SecondaryWindows.Post`,
`WebViewIpcBridge.PostJson`, `WebViewHost`'s deferred-response marshal, `WindowCommandFacade.Post`
and `DropZoneManager.MarshalToUi`. The sessions copies are deliberately held for H4.4, which rewrites
those same files anyway.

Conversion fixed three live defects as a side effect, which is the argument for the collapse in a
sentence: `WindowCommandFacade` used to call `BeginInvoke` unconditionally — so a command arriving
already on the UI thread was deferred to the next message, losing `START_DRAG`'s mouse-down timing —
and left its posted body unguarded, so a throwing app `ApplyTheme` or `FormClosing` became an
unhandled UI-thread exception; `DropZoneManager` used to treat "no handle yet" the same as "already on
the UI thread" and ran `PointToScreen`/`Controls.Add` inline **on a worker thread**, now a
drop-and-log.

Two deliberate non-conversions, both documented at the site so they aren't "finished" later by
mistake. `SplashPanel`'s two self-marshals stay hand-rolled: a control marshalling to *itself* is a
different problem from a service marshalling to a foreign control, and its pre-handle apply-directly
is correct — so the honest description of this work is "collapse the service-to-foreign-control
copies", not "14 → 1". And `FormInteraction` still applies `Enabled` directly when the target is
`NotReady`, because `Control.Enabled` on an unrealized control is just a stored value and dropping it
would lose the block for a window that hasn't been shown yet.

Verified: `dev.mjs verify` PASSED — 357 dotnet + 39 vitest, no API baseline drift (every converted
helper was private).

### 2026-07-30 — P5.5 batch H4.1: the re-layer (D19 + D20)

The structural half of consolidation, as its own commit. `Shenora.WebView2` now depends on
`Shenora.WinForms` — the two Windows packages are one layer, primitives then web hosting on top — and
the platform-neutral contracts moved to `Shenora.Core`: `IFileDialogs`/`IFileDialogPathStore` +
`FileDialogOptions`/`Filter`/`Result`, `IClipboardService`, and the portable `IUrlLauncher` /
`IUiInteraction` bases that `IShellLauncher` / `IFormInteraction` now derive from. `UseWinForms`
registers both faces of each split service, resolving to the same singleton, so app logic can depend
on the neutral contract and compile with no Windows reference.

**`IUiDispatcher` finally exists.** The design contract's §4 table listed "`IUiDispatcher` interface"
under `Shenora.Core` and "`IUiDispatcher` implementation" under `Shenora.WinForms` from its first
draft; P2 never built it, which is exactly why the marshalling pattern ended up hand-rolled 14 times
with five incompatible pre-handle policies. It is deliberately **three-state**
(`NotReady`/`Ready`/`Gone`) rather than one availability flag — an adversarial review of the design
draft caught that a bool would have silently re-broken two previously-fixed defects, because three
call sites have different earned pre-handle policies. Per-control, not per-application; body guarded
on the posted AND inline paths; awaitable overloads observe their cancellation token.

Two things worth recording honestly. First, **the blast radius was two `using` lines** — one in the
sample facade, one in a test — which is the strongest evidence that the contracts really were
platform-neutral in signature. Second, **the first implementation of `Post(Func<Task>)` recursed
infinitely**: written as `Post(() => _ = RunGuardedAsync(work))` the lambda body is an *expression* of
type `Task`, so overload resolution picked `Post(Func<Task>)` again — a `StackOverflowException`, which
is uncatchable, so the test host aborted with no failing test to point at (the run totals silently
dropped from 346 to 322). The new file's own async-post test is what surfaced it; the `(Action)` cast
is now load-bearing and commented as such. Ironically the same unbounded-recursion shape the
three-state model exists to prevent elsewhere.

Verified: `dev.mjs verify` PASSED — **357 dotnet** (+11 dispatcher tests) + 39 vitest, sample web
typecheck, sensitive scan, knowledge check, doctor. **Exactly two API baselines drifted** (Core gained
the contracts, WinForms lost them and its signatures now reference `Shenora.Core.*`); the other three
were confirmed byte-identical, as the design predicted. Docs synced in the same commit
(ARCHITECTURE's dependency rules, REVIEW-GUIDE §5, README's package table, RELEASING's reference
recipe, both package descriptions, and the design contract's §4 table). Remaining re-layer work —
routing the sessions package through the edge, collapsing the duplicate helpers onto the new seam —
is H4.2–H4.7.

### 2026-07-30 — P5.5 batches H1 + H5: the security fixes and the gate that was supposed to catch them

First half of the consolidation phase, deliberately sequenced ahead of the re-layer so a
path-traversal fix wasn't waiting behind a refactor.

**H5 — the gate holes closed first**, because until they were, "verified" meant less than it
claimed: `Shenora.slnx` carried an EMPTY `samples` folder (and omitted `Shenora.Core`), so
`verify` never compiled the reference composition or the e2e subject; `dev.mjs test <typo>` exited 0
having run nothing; and `check-sensitive` silently degraded to two structural patterns whenever the
gitignored `local/sensitive-patterns.txt` was absent — every fresh clone and every CI run, i.e. the
private-name half never ran in the release gate. Now: samples + Core are in the solution, `verify`
additionally type-checks the sample web app and runs `doctor`, unknown test targets fail, warnings
are errors for `src/` and no longer hidden by `-clp:ErrorsOnly`, the scanner fails CLOSED (with an
explicit `--allow-builtins-only` opt-in that the release workflow now uses) and also scans file
paths and renamed/copied files, a new `commit-msg` hook scans commit messages, `create_tag: false`
no longer produces a tag, CPM is enforced from the root shim, and the npm package gained
`prepublishOnly`. **The first build with the samples compiled and warnings-as-errors on came back
0 warnings / 0 errors** — the sample was not, in fact, broken; it simply wasn't being checked.

**H1 — five fixes, four of them reachable by content the app doesn't control.** Arbitrary file read
through file-mode serving (no path containment, and `Path.Combine` returns a rooted second argument
verbatim); `NavigationGuard` bypassed by redirects; an unserializable notification payload crashing
the UI thread and losing its whole batch; `ClearProfile`'s recursive delete accepting a traversing
path; and a leaked `Process` handle per external link click. Root causes and verification per fix in
`docs/FIX-LOG.md`; new public API (`LoginWindow.ComposeProfileDirectory`) and the behaviour changes
in `CHANGELOG.md`.

One fix had to be **adapted rather than implemented as specified**, and the adaptation is the
interesting part: `CoreWebView2NavigationStartingEventArgs` has no deferral, so an async guard cannot
be awaited in that event at all. What shipped is a synchronous cross-host rule (the pool records the
host the guard approved and cancels unvetted hops), which closes the documented
`302 → 127.0.0.1` vector, while `SessionBrowserOptions.RequestFilter` — synchronous by design and
already wired with `WebResourceContext.All` — remains the seam for full redirect/subresource policy.
Both options now say so instead of over-promising. Not applied to `LoginWindow`: interactive OAuth
legitimately redirects across hosts.

Verified: `dev.mjs verify` PASSED — 346 dotnet + 39 vitest, sample web typecheck, sensitive scan,
knowledge check, doctor. 20 new tests (7 escaping paths + 3 legitimate CJK/spaced paths + a
sibling-prefix case; 2 notification-serialize cases; 4 traversal + 9 unsafe-segment + 2 composition
cases). The `Shenora.WebView2.Sessions` API baseline drifted by exactly one intentional line and was
reviewed before promotion.

### 2026-07-30 — P5 increment 4 + phase review: sessions proven live — P5 COMPLETE

The sample gains the sessions demo: a `RenderSessionPool` (capacity 2, own `sessions/render`
profile, a loopback-only navigation guard) and a `RENDER`/`PROBE` route that leases a pooled
off-screen session, navigates the requested page, and returns its LIVE-DOM title + HTML length.
The web page adds a "render this page off-screen" button. PROVEN LIVE (dev mode, CDP through
`window.__shenora`; screenshot `p54-dev-render.png`): first PROBE created the instance and
returned `"Shenora Sample"` + ~3.8 KB of live DOM (its JS ran off-screen), a second PROBE reused
the warm instance in ~250 ms, a non-loopback URL came back as structured `RENDER_REFUSED` (the
guard seam), and the page button showed the success line. Graceful close exits code 0 (the pool
disposes with the window).

**Phase review (adversarial subagent over the full P5 diff) — real findings fixed:** the
`LoginWindowController` assumed a foreground login window, so an off-screen co-browse host (which
reuses it) would (1) veto `Application.Exit` via its hold-close handler and (2) pop an invisible
window on screen if a driver called `Reveal` — both now gated behind a `foreground` flag (the
background co-browse controller's window-managing calls are inert); (3) a failed session init
leaked the WebView2 control (and could finish attaching a browser process holding the profile
lock) — the pool now disposes control + fresh host on the failure path; (4) a silent-refresh
login showed an OWNED modal, disabling the app's main window while invisible — now ownerless;
(5) the loading-splash fallback never fired `onLoading(false)` if the driver threw before
signalling — now dropped unconditionally in the finally; (6) `RenderSessionPool.Dispose` hung a
queued lease forever and could re-pool an instance into a dead pool — Dispose now cancels queued
waiters via a dispose token and `Return` discards once disposed; (7) the controller's UI marshal
checked `InvokeRequired` without `IsHandleCreated` (the family pre-handle trap) — fixed;
(8) `CoBrowseSession.StartAsync`'s `BeginInvoke` was unguarded — now faults the task + completes
the frame channel; (9) `CoBrowseSession.DisposeAsync` could hang on a stopped message loop —
completes the frame reader first, then fires UI cleanup without awaiting; (10) drag was
impossible because mouse-move always sent `buttons:0` — a held button now carries through moves;
(11) every mouse event round-tripped a script call to read the viewport (and its fallback
disagreed with the initial viewport, misplacing clicks) — the emulated viewport is now cached;
(12) the request filter passed `about:blank`/pre-commit sources as the page host, so a same-host
filter could 403 the page's own document — non-http(s) sources are nulled; (13) the init-timeout
guidance only wrapped the core attach, not environment creation — now both; (14) the sample
`RENDER` lease could hang forever behind a wedged pool — bounded with a 60 s `RENDER_BUSY`; plus
the packaging gap (the new package was missing from `dev.mjs pack`'s list and the README) and
the controller's raw-event taps silently replaced each other (now accumulate like `OnMessage`).
A live-caught hang: `SemaphoreSlim.Dispose()` racing a just-cancelled waiter wedged it (fix-log);
resolved by not disposing the semaphore (it never allocated a wait handle). Re-verified 318
dotnet + 39 vitest green; sample re-proven live. Deferred deliberately (recorded in the private
notes): renaming the login-named types to session-neutral names (pre-1.0, revisit if a pure
co-browse consumer finds it awkward), and STA-wrapping the new pool/login tests (the earned rule's
trigger — `AllowDrop`/OLE — doesn't apply here; the tests are deterministically green).

### 2026-07-30 — P5 increment 3: co-browse streaming

`CoBrowseSession` ports the server-backed sibling's co-browse core with the transport cut away
as the seam: the generic package owns the off-screen browser (fixed generous physical surface —
the CSS viewport is driven purely by `Emulation.setDeviceMetricsOverride`, DPI-independent),
the screencast (`Page.startScreencast` JPEGs → a bounded latest-frame-wins channel, frame-acked;
`everyNthFrame:1` because CDP only emits on visual change, so idle bandwidth is ~0), the input
dispatch (the source's wire protocol VERBATIM for mechanical adoption — 1:1 viewport mirroring
via device metrics alone with the measured clamps, fraction→CSS-px mouse/wheel, `insertText`
typing, special keys/shortcuts synthesized with the modifier bitmask + the Windows virtual-key
map), the hotspot extraction script (clickable rects as viewport fractions — the client only
has pixels), and the SAME `LoginWindowController` primitives over the streamed page (the
source's deliberate reuse, kept). The app keeps the WebSocket pumps, the send lock, and the
polling cadence — its transport, its schema. Formatting is invariant-culture throughout (the
source's live "1,50-is-broken-JSON" locale fix, pinned by a de-DE test). Verified: 16 new tests
over the pure protocol builders (clamps, VK map matrix, modifier bitmask, down/up pairing,
fraction scaling, locale pinning, option validation) — 315 dotnet + 39 vitest green; baseline
promoted (additions only). Live streaming is the P5.4 e2e's subject.

### 2026-07-30 — P5 increments 1–2: the sessions package — offscreen render pool + login windows

New package `Shenora.WebView2.Sessions` (D14), extracted from the server-backed sibling's
render/session/login stack merged with the primary sibling's external-login service. P5.1: the
one auxiliary-browser configuration path (`SessionBrowser` — per-profile environment,
quiet-start + background-throttling-off arguments, hardening, `RequestFilter` seam, the 25 s
init-timeout guard) and the bounded LIFO render pool (`RenderSessionPool`/`RenderSession` —
capacity waits queue rather than fail, a creation failure releases its slot, a failed
about:blank reset DISCARDS the poisoned instance, `NavigationGuard` is the generalized SSRF
policy seam; one shared hidden off-screen host in runtime mode, visible cascaded windows in dev
mode). P5.2: the login stack — `LoginWindow` runs a caller-supplied driver over
`LoginWindowController` primitives inside a modal nested loop, with the sibling-proven
mechanics ported: busy serialization with EXACTLY-ONCE completion (the dropped-post wedge fixed
via the cancellation-token fallback — and the source's unused-token gap fixed with observed
tokens throughout), the user's close HELD open for a final cookie read, the silent-refresh
off-screen shape (`RevealImmediately=false` + idempotent `Reveal()` — "no interaction ⇒ no
window"), desktop-width default sizing (narrow windows reflow providers to mobile layouts with
NO login UI — measured), `FitToBox` CSS→physical DPI math, per-provider AND per-sub-account
profile scoping documented as the security boundary it is, and `ClearProfile` as real logout.
`CookieLoginFlow` is the built-in driver: poll for a FRESHLY-SET auth cookie judged against a
pre-navigation baseline (a stale profile cookie never captures — the dead-session incident),
reading from the SEPARATE `CookieReadUrl` origin (the parent-domain capture bug), with the
no-anonymous-blob gate held even on the final close read. Verified: 26 new tests over internal
seams (pool accounting/LIFO/discard/cancellation with a fake factory; flow freshness/reveal
timing/close capture/gating via the hooks seam; busy-gate + token-fallback mechanics with a
deliberately unpumped anchor; `ComputeFitSize` DPI cases; `ClearProfile`) — 299 dotnet + 39
vitest green; Sessions API baseline reviewed and promoted (additions only). Real browser
behavior is the P5.4 sample/e2e's subject, per the family precedent.

### 2026-07-30 — P1.1: local-feed consumption smoke — and the real bug it caught

The pack output was consumed like an external app would (the rerunnable scratch consumer lives
untracked in `devtools/_p11-consumer/`): NuGet side — a standalone `net10.0-windows` console
project with a `nuget.config` pointing at `publish/packages` + nuget.org, exact-pinned
`[0.1.0]` references to the two leaf packages (Core/Ipc resolve transitively), CPM opted out —
restored, built, and ran a live dispatch round-trip printing all four assembly versions at
0.1.0. npm side — the packed tarball installed with `react` into a scratch project and imported
under PLAIN NODE ESM… which FAILED, catching a real packaging bug the bundler-based dev loop
structurally cannot see: the emitted `dist/*.js` carried extensionless relative imports
(`from './types'`) because `moduleResolution: bundler` never requires extensions — fine in
Vite/vitest, rejected by Node's own loader. Fixed with explicit `.js` extensions on every
relative specifier and the package tsconfig moved to `NodeNext`, which makes a missing
extension a build error (prevention; full entry in `docs/FIX-LOG.md`). Re-packed, the npm smoke
now resolves every export under plain Node; full `verify` green (273 dotnet + 39 vitest). The
consumption recipe is recorded in `docs/RELEASING.md`.

### 2026-07-30 — P4 increment 6: the P4 surface proven live (sample + e2e) — P4 feature-complete

The samples become the full P4 reference composition. Desktop: `MainForm` is now a FRAMELESS
`OptimizedForm` (chrome colors = the app background, DWM border matched — no visible frame), the
window-facing facades map late in the form's constructor (`WindowCommandFacade` wired to the
manual maximize path, `DropZoneFacade` over a `DropZoneManager`), the ready handshake clears
stale drop zones before starting the tick source, a launcher-style `TrayIcon` (no close-to-tray,
so the e2e's graceful close still exits), `SecondaryWindows` + `SampleFacade` routes
(OPEN/HAS/CLOSE_PANEL + PICK_FILE/REVEAL for the manual dialog/shell demos). Web: the page
renders its own title bar (drag via `startDrag`, min/max/close buttons, a top resize strip,
`useWindowMaximized` glyph), a `useDropZone` target, and the secondary-window controls.
PROVEN LIVE (screenshots gitignored in `devtools/screenshots/`): dev (`p46-dev-frameless.png`)
and packaged (`p46-packaged.png`) both show the frameless window with page-owned chrome and
every status line green. CDP drive (`window.__shenora`): `WINDOW IS_MAXIMIZED` false →
`TOGGLE_MAXIMIZE` → true → restored (the manual work-area maximize end-to-end);
`SAMPLE OPEN_PANEL` → `HAS_PANEL` true → `CLOSE_PANEL` → false; the page's drop zone
auto-registered (`DROP_ZONE REGISTER:ok` + bounds UPDATEs + SHOW traffic — StrictMode's
mount-unmount-remount sequence handled exactly as the ported fix comments promise). Native
input drive: `dev.mjs click` on the page's panel button fired `OPEN_PANEL` (win-input works
against the new UI), and `input list` showed BOTH top-level windows — the frameless main window
and `Shenora Sample — panel` on its own STA thread. Graceful closes exit code 0 in both modes.

**Phase review (adversarial subagent over the full diff) — 10 real findings, all fixed:**
(1) the drop-zone manager's pre-handle marshal re-invoked its caller → unbounded recursion →
uncatchable StackOverflow (reachable via startup-failure disposal) — pre-handle now proceeds
inline; (2) `FormInteraction` held its lock across a blocking `Invoke` — the classic pool↔UI
deadlock the family already documented — now `BeginInvoke`; (3) frameless `SC_RESTORE` was
swallowed while minimized+maximized, stranding the window in the taskbar — the intercept now
defers to `DefWindowProc` when minimized and `RestoreFromMax` un-minimizes first; (4)
`SecondaryWindows.Post` ran inline on the CALLER's thread pre-handle — an `Activate` racing
creation would create the handle on the wrong thread and kill the pump — pre-handle is now a
no-op with flag-carried intent (`HandleCreated` re-checks `CloseRequested`); (5) `useDropZone`'s
in-flight REGISTER ack could land after teardown and mark the destroyed zone registered
(StrictMode's default sequence!) — epoch-guarded now; (6) `SecondaryWindows.Dispose` didn't
wait for the pumps, losing geometry saves at exit — bounded drain added; (7) `TrayIcon`'s
`_exiting` wedged after a canceled close (next user close would EXIT) and the icon hid before
the close was certain — reset-on-cancel + hide moved to `FormClosed` (+ a Font handle leak);
(8) `ScopedContainerRouter` invalidate/dispose racing an in-flight creation leaked the built
provider — `DisposeScope` now observes the `Lazy` (waiting out in-flight builds) and `Dispose`
drains; (9) the occlusion check interpolated the app-supplied zone id raw into a script —
JSON-injected + `CSS.escape`d per the injection rule; (10) `START_DRAG` while manually
maximized dragged a work-area-sized window with stale restore bounds — the facade refuses it.
Regression tests cover 1, 3, 5, 6, 7, 8. Re-verified: 273 dotnet + 39 vitest green; `verify`
PASSED. **P4 (modules + native services) is complete.**

### 2026-07-30 — P4 increment 5: secondary windows + tray

`Shenora.WinForms` gains `SecondaryWindows` — the primary sibling's ~630-line secondary-window
service decomposed to its generic core: named windows, each opened on its OWN STA thread with
its own message pump (the source's preload/sync-create split existed only because callers ran
the thread; the registry now owns it), with the app's `CreateForm` factory holding everything
the source hardcoded (content, sessions, theme). Geometry persistence reuses the P2
window-state stack per name (`IWindowStateStore` per window — the extraction map's
"IWindowGeometryStore seam" realized; logical store / physical restore / off-screen recovery
come along free). Kept post-mortems: the non-blocking close discipline (a blocking `Invoke`
from the IPC thread deadlocked the source during scope switches). Deviations: opening an
existing name ACTIVATES it (the source's close-and-recreate churned; its login-window sibling
proof focuses), and a close racing window creation is caught by a flag instead of being lost.
`TrayIcon(+Options)` generalizes the server-backed sibling's tray: NotifyIcon lifecycle,
Open/app-items/Exit menu composition (`ConfigureMenu` gives the app the raw
`ContextMenuStrip` — no DSL), double-click restore, the close-to-tray FormClosing dance, and
`TrayMenuColors` — the parameterized port of its dark menu renderer (disabled-text legibility
on dark surfaces was its measured reason to exist); null colors = stock renderer, the palette
is the app's (D13). Verified: 268 dotnet + 38 vitest green (+10: own-STA-thread pumps with
polling, activate-on-existing, raced close, state-store save-on-close, failing-factory cleanup,
close-all; tray menu composition/order, close-to-tray → hide then real exit, opt-out, dispose
detach); WinForms baseline promoted (additions only); `verify` PASSED.

### 2026-07-30 — P4 increment 4: drag-drop zones + `useDropZone` (+ the P2.3b DPI tail)

The third-most-copied component in the family (one sibling's copy was literally annotated
"ported from…" another) lands once: `Shenora.WebView2` gains `DropZoneManager(+Options)` —
transparent `WS_EX_TRANSPARENT` overlays positioned over page elements to capture REAL OS file
paths (the DOM only ever sees blob URLs), including drags from other apps while the window is in
the background (an inactive form always shows its overlays). Ported with the measured
discipline: non-blocking `MarshalToUi` (a blocking `Invoke` off the UI thread caused an AppHang
in the source), form-activation visibility sync, the DOM occlusion check (a covered zone must
not light up), the disposed-during-async `Dead` guard, and event-handler detach on dispose.
Events emit on `IEventBus` (`DROP_ZONE`: DRAG_ENTER/DRAG_LEAVE/FILE_DROP) — the bridge's
wildcard forwarding ships them to the page, decoupling the manager from the transport.
`DropZoneFacade` provides the REGISTER/UPDATE/UNREGISTER/SHOW routes. The P2.3b DPI tail lands
here: CSS→physical conversion now uses the CONTROL's per-monitor `DeviceDpi` (the source used a
process-global scale — wrong on mixed-DPI setups), and the manager stores each zone's CSS rect
and re-applies all bounds on `Form.DpiChanged`. Placed in the WebView2 package (the design
sketch said WinForms) because it drives the WebView and needs Ipc — same dependency reality as
the window commands. `@shenora/react` gains `useDropZone` with the source's fix-history kept
(unregister-on-attempted so a fast unmount tears down an in-flight REGISTER; duplicate-REGISTER
guard; teardown on `enabled` flip) and generalized: zero dependencies (local debounce, no uuid
lib) and NO CSS shipped (headless D13 — the drop class is applied, the app styles it).
Verified: 258 dotnet + 38 vitest green (+12: overlay lifecycle/parenting/bounds on STA threads,
DPI re-apply from stored rects, bus wire shapes, facade route matrix incl. structured missing
payload, hook register/unregister/drop-routing/class-toggle/SHOW/disabled/flip); real drags +
occlusion are the P4.6 e2e's subject; WebView2 baseline promoted (additions only); `verify`
PASSED.

### 2026-07-30 — P4 increment 3: the native desktop services

`Shenora.WinForms` gains the service layer the source apps hand-rolled, all TryAdd-registered by
`UseWinForms` so every app gets them and any registration can be replaced:
`IFormInteraction`/`FormInteraction` (the main-window registry — the runner registers the form
automatically — plus nested modal blocking via the native `Enabled` property; the handle read is
fixed to answer `Zero` before creation, where the source's `Invoke` dance would have CREATED the
handle on the wrong thread), `IFileDialogs`/`FileDialogs(+Options)` with the wire-friendly
`FileDialogOptions`/`Filter`/`Result` models and the `IFileDialogPathStore` seam (generalizing
the source's settings-service coupling): every dialog on a DEDICATED STA thread (the measured
WebView2 conflict), owned by the main window for z-order, main window blocked while up, per-key
last-directory memory with stale-entry fallthrough, the folder-or-file `OpenFileDialog` trick
kept, and a NEW `SaveFileAsync` in the same pattern — failures now THROW (the source flattened
exception text into a wire-bound string, the exact leak shape §5 forbids);
`IShellLauncher`/`ShellLauncher` (reveal-in-Explorer with the Windows 11 handle-leak fix, shell
"open"-verb directories — not `explorer.exe`, which orphaned processes — http/https-only
`OpenUrl` matching the new-window policy, `LaunchProcess`);
`IClipboardService`/`ClipboardService` (STA-marshalled text get/set + the family's two
image-file operations, centralizing its ad-hoc clipboard threads). A shared internal
`StaThread.RunAsync` carries the STA post-mortem once. Verified: 252 dotnet + 32 vitest green
(+20: nested blocking, handle states, filter strings, initial-path chain incl. stale cleanup and
a throwing store, remember-path guards, shell validation throws, registration + runner wiring);
real dialogs/shell launches are e2e/manual territory; WinForms baseline promoted (additions
only); `verify` PASSED.

### 2026-07-30 — P4 increment 2: the window manager — frameless chrome + frontend window commands

`Shenora.WinForms` gains `OptimizedForm(+Options)`, merged from both desktop siblings with the
measured lessons kept: the double-buffered base + `WndProcHook` seam (first sibling) and the
optional frameless custom chrome (second sibling) — WM_NCCALCSIZE removes ONLY the top caption
(native invisible side/bottom resize borders stay; returning 0 for all sides needs a visible
inset), no `ControlStyles.UserPaint` (an unpainted WHITE frame otherwise), MANUAL work-area
maximize via `MonitorFromWindow`+`GetMonitorInfo` (never `Screen.WorkingArea` — DPI-mis-scaled
~12 px short; `WindowState.Maximized` left a ~6 px gap and squared the corners) with
`SC_MAXIMIZE`/`SC_RESTORE` routed through it, `WM_NCACTIVATE` lParam −1 (the grey caption
strip), DWM dark-mode/border-color/corner preference (rounded windowed, square maximized — the
clipping report), a DPI-scaled top resize strip re-added via WM_NCHITTEST, and
`ApplyChromeTheme` for runtime light↔dark resync. All colors are options (headless, D13).
`Shenora.WebView2` gains `WindowCommandFacade(+Options)` — module `WINDOW` (generalized from the
siblings' `APP`): MINIMIZE / TOGGLE_MAXIMIZE / CLOSE / IS_MAXIMIZED / START_DRAG (ReleaseCapture
+ WM_NCLBUTTONDOWN/HTCAPTION — the reliable WebView2 drag) / START_RESIZE (top edges only by
design; lParam MUST be the cursor screen pos or the size loop tracks from (0,0)) / optional
SET_THEME, with delegate seams (`ToggleMaximize`/`IsMaximized`) so frameless apps wire the
manual path — placed in the WebView2 package because the commands arrive over the bridge and
need Ipc, which WinForms deliberately doesn't reference. `@shenora/react` gains the
`WindowCommands` typed service + `useWindowMaximized` (the max-glyph resync pattern: re-query on
window resize). Verified: 232 dotnet + 32 vitest green; a live test-harness incident became a
rule — OptimizedForm's OLE drag-drop registration requires STA, and on xunit's MTA workers the
failure is a BLOCKING WinForms exception dialog, not a red test (tests now run bodies on a
dedicated STA thread; recorded in `windows-dev-gotchas`). WinForms + WebView2 baselines promoted
(additions only); `verify` PASSED. The frameless visuals + native drag/resize loops are the
P4.6 sample e2e's subject.

### 2026-07-30 — P4 increment 1: scoped-container router + the standard IPC composition

`Shenora.Ipc` gains `ScopedContainerRouter(+Options)` — the generalization of the primary
desktop sibling's per-profile service router (generic-library: an app-defined scope +
scoped-container router, no domain id). Each scope id lazily gets its own child
`ServiceProvider` from the app's `ConfigureScope` callback (validation throws structured
`OperationException`s), with `OnScopeCreated` for post-build init (the migrations/plugin-loading
the source hardcoded), `MapModule<TFacade>` routing declarations, `GetScopeServices`/
`InvalidateScope`/`ActiveScopes` (the sweep seam replacing the source's hardcoded
close-all-windows walk), and full disposal. Deliberate fixes over the source: single-flight
creation (`Lazy` per id — the source's bare `GetOrAdd` could build two providers under a
first-request race and leak one undisposed; failed creations don't poison the cache),
exceptions flow to the pipeline's error mapping instead of a leaking local catch, and a scoped
module called without a scope answers a structured `SCOPE_REQUIRED` (the source's equivalent
check was unreachable through its own wiring — why its client grew a hand-rolled guard).
Composition helpers formalize the sample's proven loop: `AddModuleFacade<TFacade>` +
`MapRegisteredModules` + `AddMessageDispatcher` (the §5 order encoded: error handler → app
middleware → DI-registered facades); the sample now composes through them. Verified: 216 tests
green (+15: routing matrix, `SCOPE_REQUIRED`, caching + single-flight under concurrency,
failed-creation retry, half-built-scope disposal, invalidate/dispose, structured validation
errors end-to-end, composition ordering); Ipc baseline promoted (additions only); `verify`
PASSED.

### 2026-07-30 — P3 increment 5: the IPC round-trip proven live (sample + e2e) — P3 closed

The sample apps become the IPC reference composition and the phase's proof. Desktop:
`SampleFacade` (`BaseFacade`, module `SAMPLE`: `ECHO` reads its payload through `PayloadHelper`
and returns a typed object; `FAIL` throws a structured `OperationException`), facades registered
in DI and mapped onto a `MessageDispatcher` (`UseErrorHandler` first) at composition time,
`WebViewIpcBridge` wired in its intended order (constructed before `InitializeAsync` so bus
buffering covers init; attached after init, before `Navigate`; disposed with the form) with
`OnClientReady` starting a 1 Hz `SAMPLE.TICK` emitter on the app's `IEventBus`. Web: the page
calls `notifyReady()` from an effect, runs `useShenoraQuery('SAMPLE','ECHO')` and renders the
typed response, streams `SAMPLE.TICK` via `useShenoraEvent`, and installs the dev interceptor in
dev builds. PROVEN LIVE with the devtools loop (screenshots in `devtools/screenshots/`,
gitignored): packaged mode shows `SAMPLE.ECHO("shenora") → SHENORA (7)` and `SAMPLE.TICK`
advancing #19→#23 across two captures 4 s apart (`p35-packaged-a/b.png`); dev mode the same over
Vite (`p35-dev.png`, TICK #38). CDP-driven assert (dev, via `window.__shenora` + the `.cdp-port`
loop): `call('SAMPLE','ECHO',{text:'cdp drive'})` returned `{echoed:"CDP DRIVE", length:9}`,
`call('SAMPLE','FAIL')` rejected as `OperationError` `{code:"SAMPLE_FAILURE",
parameters:{reason}}` (raw exception text never crossed), `waitEvent('SAMPLE','TICK')` resolved
with a live tick, and the ring buffer showed the full exchange. **P3 (IPC extraction) is
complete**: contracts → dispatcher/event bus → WebView2 transport → React client → live
round-trip, all verified (`verify` PASSED at every increment).

**Phase review (adversarial subagent over the full diff) — 9 real findings, all addressed:**
(1) an unserializable handler result (or a throwing app dispatcher) escaped the transport's
async-void handler → process death; the bridge now wraps dispatch + serialize and always answers
`UNKNOWN_ERROR`; (2) the ready gate never re-closed, so a renderer-crash reload drained
notifications into a listener-less page → `NavigationStarting` now resets it; (3) the event
bus's `'.'`-joined match-cache key let arbitrary app names collide and permanently poison
results → `'\0'`-joined; (4) `useShenoraQuery` left `loading: true` forever when `enabled`
flipped false mid-flight; (5) `PayloadHelper` put raw serializer text on the wire (design §5) —
now only the key crosses, details stay in the inner exception; (6) a disposed TS bridge burned
the full timeout per call → fails fast with `NO_TRANSPORT`; (7) the match cache's unbounded key
space is now a documented cardinality contract; (8) `ConfigureAwait(false)` inside the
dispatcher pipeline broke the §5 stay-on-caller-context model after async fall-throughs —
removed, documented; (9) the sample's `NO_HANDLER` was missing its documented `module`
parameter. New tests cover 1–6; the earned invariants became `.claude/knowledge/ipc-contracts.md`.
Re-verified: 201 dotnet + 28 vitest green, `verify` PASSED.

### 2026-07-30 — P3 increment 4: `@shenora/react` becomes the real client

The placeholder package becomes the client side of the contract, ported from the primary desktop
sibling's bridge/event-bus/module-service trio and generalized where the source carried app
schema. `types.ts` mirrors the `Shenora.Ipc` envelopes name-for-name; `OperationError` carries
the structured code + parameters (client-side failures — `TIMEOUT`, `NO_TRANSPORT` — reject
through the same shape, so error handling is uniform). The transport is a two-method seam
(`ShenoraTransport`) with `createWebView2Transport` as the desktop default — the D16
pluggability point a WebSocket or Capacitor shell implements later. `ShenoraBridge`: correlated
`invoke` (uuid ids, per-call timeout over a 30 s default), category routing, batch unbundling
into `ShenoraEventBus`, `notifyReady()` (the `SHENORA`/`READY` handshake that starts host
notification delivery), and a `fallback` option generalizing the source's hardcoded dev mocks —
the app supplies canned answers for pure-UI browser development; the library ships none (no app
schema in the kit). The default instance is LAZY (`getBridge`/`configureBridge` — no import-time
side effects, honest `sideEffects: false`). `BaseModuleService<TRequests>` keeps the typed-send
core and drops the source's boolean/array/optional wrappers (pure casts). Hooks: `useShenora`,
`useShenoraEvent` (latest-ref pattern replaces the source's deps param — no resubscribe churn,
no stale closures), `useShenoraQuery` (deliberately minimal fetch state — headless, D13).
`installDevInterceptor` ports the CDP-testing global (`window.__shenora`: `call`/`waitEvent`/
ring buffers), idempotent across HMR. `react` becomes a required peer (hooks import it
statically). Verified: 26 vitest tests green (wire shape, resolve/structured-reject/timeout,
batch order, malformed-message tolerance, handshake, fallback + `NO_TRANSPORT`, dispose,
event-bus semantics, typed service, hook lifecycle via renderHook incl. the latest-ref
guarantee, interceptor recording/idempotence); `doctor` consistent; full `verify` PASSED.

### 2026-07-30 — P3 increment 3: the WebView2 postMessage transport

`Shenora.WebView2` gains `WebViewIpcBridge(+Options)` — the transport tying a WebView2 window to
the dispatch pipeline and the event bus, merged from the two family transports with their
post-mortem comments kept. Incoming: `WebMessageReceived` requests parse (`IpcJson`) and
dispatch async ON the UI thread — each await yields the message pump so concurrent IPC
interleaves without a pool thread per call (the measured incident: `Task.Run`-per-message under
heavy backend load starved the pool and froze the app; heavy work belongs in the backend's own
bounded queues). Outgoing: responses and ~50 ms-batched `IpcNotificationBatch` pushes via
`PostWebMessageAsString`, guarded by the family marshalling discipline (`IsHandleCreated`
checked before `InvokeRequired` — the pre-handle lie — then non-blocking `BeginInvoke`).
Notifications flow through a bounded drop-oldest queue (cap 10k — telemetry-like events; OOM is
worse than losing stale progress ticks) that buffers from CONSTRUCTION (events emitted during
the slow WebView2 init survive) and delivers only after the client's ready handshake (reserved
`SHENORA`/`READY` route, intercepted before the dispatcher; `OnClientReady` fires per occurrence
— reloads included — as the cue to reset per-page state). Optional `IEventBus` wildcard
forwarding; `SendNotification` for direct pushes; `Dispose` stops the flush timer (the source's
timer once outlived its window, posting into a torn-down WebView). Verified: 197 tests green
(+12 protocol tests over internal seams — handshake semantics, dispatcher pass-through +
interception, malformed-input drops, ready-gated batching, wire shape/order, drop-oldest cap,
bus forwarding/unsubscribe); the live transport is the P3.5 sample e2e's subject; WebView2
baseline promoted (additions only); `verify` PASSED.

### 2026-07-30 — P3 increment 2: dispatch pipeline + facade base + in-process event bus

`Shenora.Ipc` gains the middleware dispatcher ported from the primary desktop sibling:
`MessageDispatcher` behind the `IMessageDispatcher` seam — `Use`/`UseModule`/`UseRoute`/
`UseLogging`/`UseErrorHandler` middleware composition (family order: error handler → logging →
app middleware → facades), `MapRoute`/`MapModule` route tables, a lazily rebuilt pipeline, and
`DispatchAsync` as the transport entry point that never throws and never returns null (unhandled
→ structured `NO_HANDLER`; escaped `OperationException` → its structured error; anything else →
`UNKNOWN_ERROR` with details kept host-side — the source leaked `ex.Message` across the bridge,
design §5 forbids it). Programmatic `SendAsync`/`SendAsync<T>` share that exact pipeline; failed
typed sends rethrow the structured `OperationException` (the source flattened to
`InvalidOperationException`), and data conversion uses the wire options (the source's default
options would have broken camelCase round-trips). `IModuleFacade` (now carrying `ModuleName`, so
facade objects route without the source's static mutable registry — DI + `MapModule(facade)`
replace it) + `BaseFacade` with the standardized error boundary. `Shenora.Core` gains the
in-process event bus per the design's package split (§4): `EventMessage`/`IEventBus`/`EventBus`
(scope generalizes the per-profile field) with `"*"` wildcards, the per-subscription match
cache, isolated handler failures, concurrent fan-out — auto-registered by
`ShenoraApplicationBuilder.Build()` (`TryAdd` last, so app/module registrations win). All
logging is `ILogger<T>`, optional so composition works without `AddLogging`. Verified: 184 tests
green (+30: matching semantics incl. the scoped/global rules, middleware ordering,
post-dispatch registration, error mapping incl. no-leak assertions, all three typed-data
conversion paths, facade routing); Core + Ipc baselines promoted (reviewed, additions only);
`verify` PASSED.

### 2026-07-30 — P3 increment 1: the IPC wire contract (`Shenora.Ipc` first surface)

The envelope contract two family apps already speak (D11), shipped transport-neutral (D16) and
pinned with `JsonPropertyName` so the wire shape survives any serializer options: `IpcRequest`
(`{id, module, type, scope?, payload?, timestamp}` — `scope` generalizes the source's per-profile
routing field), category-wrapped `IpcResponse` with a structured `IpcError` (`{code, message?,
parameters?}` — the source's JSON-string error + duplicated error data collapsed into one i18n-ready
object), and the always-batched `IpcNotification(Batch)` push envelope (~50 ms flush upstream;
`category` alone discriminates, so the same envelope rides postMessage, WebSocket, or a mobile
channel — the source's synthetic batch module/type wrapper is gone). `OperationException`
(code + parameters, `ToError()`), framework-reserved `IpcErrorCodes`, static `PayloadHelper`
(structured missing/invalid failures instead of `ArgumentException`; JSON null == absent per the
family wire convention), and `IpcJson` — ONE frozen camelCase/camelCase-enums/null-omitting
options instance, ending the source's three drifting private copies. Replaces the Ipc assembly
marker. Verified: 152 tests green (25 new: wire shapes incl. attribute pinning under foreign
options, exception mapping, payload reads, serializer defaults); Ipc API baseline promoted
(reviewed); `verify` PASSED.

### 2026-07-30 — P2 increment 6: samples + the desktop e2e loop, both frontend modes proven live

`samples/Shenora.Sample.Desktop` + `samples/Shenora.Sample.Web` — the reference composition and,
from here on, the e2e subject. The desktop app is the full stack in its intended shape:
`ShenoraApplication.CreateBuilder` → DI-registered `WebViewEnvironmentOptions` (ONE instance
shared by prewarm and the window's host) + `EmbeddedResourceProvider` (embedded
`wwwroot` bundle, file-fallback in dev) + `WebViewHostOptions` (dev URL 3900, virtual host,
injected metadata global, no-white-flash background) → `PrewarmWebView2` + provider warmup as
starting hooks → `UseWinForms` (single instance, `JsonFileWindowStateStore` window state) →
`MainForm` (WebView2 + `SplashPanel` until first navigation, runtime-presence prompt, actionable
init errors). The web sample is a minimal Vite React app consuming `@shenora/react` that displays
its serving mode, `isShenoraAvailable()`, and the injected host metadata — so one screenshot
proves the whole stack. Verified live with the devtools loop (`wgc` capture): PACKAGED mode
(embedded bundle over the virtual host — "frontend: packaged / bridge: WebView2 host detected /
host: Shenora.Sample.Desktop v1.0.0") and DEV mode (live Vite — "frontend: dev (Vite)", same
bridge + metadata), window state persisted DPI-logically (physical ~2538 px stored as 1280
logical at 200 %) and restored on relaunch, and the CDP devtools port reachable in dev — the
`AdditionalBrowserArguments`-clobbers-the-env-var fix working end-to-end. `dev.mjs
sample/vite/shot/wgc/click` now have their target. 126 tests green; `verify` PASSED.

### 2026-07-30 — P2 increment 5: WebView2 host, packaged-frontend serving, event policies, splash

`Shenora.WebView2` gains the "one place a WebView2 gets configured": `WebViewHost(+Options)` —
environment acquisition (shared/prewarmed or per-STA-thread) and `EnsureCoreWebView2Async` under
the family's 25 s init-timeout guard (an orphaned user-data-folder lock otherwise hangs init
forever, silently), the settings-hardening preset (dev-gated devtools/context menus, everything
unused off, web messages on) with a `ConfigureSettings` escape hatch, dev/prod navigation with
actionable errors (`ResolveStartUrl`: DevUrl in dev — deliberately no default port; explicit
`ProductionUrl` or the virtual host's index in prod), and the four event policies every source
lacked: new-window → system browser (scheme-checked), downloads canceled by default, permissions
silently denied except an allowlist (clipboard-read), renderer-crash auto-reload with a cooldown
— each replaceable by a callback. Resource serving keeps the source's measured sync/deferred
split with its post-mortem comments: the virtual-host bundle serves synchronously in-memory (the
main document must be prompt), app schemes (`WebViewDeferredScheme`) defer off the UI thread and
marshal responses back via `BeginInvoke`; disk-folder virtual hosts (`WebViewFolderMapping`)
are supported alongside interception (both family mechanics, deliberately). Fixed during the
port: the caching policy is now no-cache HTML / immutable hashed assets (the source served
`index.html` immutable — a stale-update trap), and injected globals are real JSON with escaping
(`InjectedGlobals`) instead of raw string interpolation. `EmbeddedResourceProvider(+Options)`
behind the `IWebViewResourceProvider` seam is parameterized by assembly + prefix, lazy-with-warmup
(the source preloaded everything in a blocking parallel ctor loop), file-fallback mode for dev,
and resolves lookups path→name so dotted filenames work. `Shenora.WinForms` gains
`SplashPanel(+Options)` — the startup marquee overlay with app-chosen colors (headless, D13) and
a debounced recenter; the source's dead status labels were dropped. Verified: 126 tests green
(provider modes/warmup/dotted names, script escaping, URL resolution, content-type + cache
policies, splash layout); the live host path is proven by the P2.6 sample e2e; baselines
promoted (additions only).

### 2026-07-30 — P2 increment 4: application builder + lifetime, `--restarted` relaunch handoff

`Shenora.Core` gains the composition root the design's goal statement names:
`ShenoraApplication.CreateBuilder(args)` resolves the launcher contract up front (`--app-root` →
`ShenoraPaths` → `ShenoraEnvironment` anchored at the resolved root), exposes
`Services`/`AddModule(IShenoraModule)`/`OnStarting`/`OnStopping`, and `Build()` produces a
`ShenoraApplication` whose `Run()` executes a host-package-registered `IShenoraRunner` (actionable
error when none). Lifecycle participation is DI-based (`IShenoraLifecycleHook`), so composed
packages hook startup/shutdown without Core referencing them — the mechanic that keeps package
dependencies strictly downward (design §4 amendment; Core's dependency moved to the DI
implementation package, D17). `Shenora.WinForms` gains `UseWinForms(WinFormsHostOptions)` — an
internal runner executing the family's measured order: single-instance gate FIRST (now with the
`--restarted` widened-wait handoff: `SingleInstanceGuard.TryAcquire(TimeSpan)`, abandoned-mutex
recovery, explicit release-before-teardown), `WinFormsBootstrap.Initialize`, starting hooks,
main-form factory (+ optional window-state apply/save and an activate-on-second-launch message
filter that works with ANY `Form` — no base-class requirement), the message loop, then
reverse-order guarded stopping hooks. `Shenora.WebView2` gains `PrewarmWebView2` (a deferred
starting hook — the prewarm's user-data lock must stay behind the gate). Verified: 93 tests
green (builder composition, documented run order, losing-launch path, widened-wait/timeout/
abandonment handoffs, window-state wiring through internal seams); the real message-pump path is
proven by the P2.6 sample e2e; API baselines promoted (additions only).

### 2026-07-30 — P2 increment 3: WebView2 environment factory + runtime presence check

`Shenora.WebView2` gains `WebViewEnvironment(+Options)`: the prewarm pattern (browser-process
spawn overlapping the rest of startup — ~1–2 s measured in the source), the shared environment
with its thread-affinity contract (main UI thread) plus `CreateForCurrentThreadAsync` for
secondary windows on their own STA threads (same options/user-data folder ⇒ one shared browser
process), the dev CDP-args re-append, an injectable log sink instead of the source's
`Console.WriteLine`, and — NEW, the gap every source shipped with — a never-throwing runtime
presence probe (`GetAvailableRuntimeVersion`/`IsRuntimeAvailable`) so apps can show an
actionable install prompt instead of failing inside `EnsureCoreWebView2Async`. 70 tests green.

### 2026-07-30 — P2 increment 2: paths authority, app-root arg, bootstrap + global exception handling

`Shenora.Core` gains `AppRootArgument` (the launcher's `--app-root` contract, both arg forms) and
`ShenoraPaths(+Options)` — the portable layout authority generalized from two sources: explicit
root → root env var → libs-parent detection → base dir, a data env var so child processes share
the host's data dir (a live divergence incident in a source app), configurable folder names, and
ensure-created purpose areas with NO framework-defined area vocabulary. `Shenora.WinForms` gains
`WinFormsBootstrap` — the proven one-call WinForms init (visual styles, GDI+ text, PerMonitorV2,
catch-mode) PLUS the audit's #1 gap fixed: `Application.ThreadException`,
`AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` all routed to a
crash-log callback with a guarded last-resort dialog (a known no-op reflection hack from the
source was deliberately dropped). Verified: 67 tests green; baselines promoted (additions only).

### 2026-07-30 — P2 increment 1: the pure seams (environment, DPI, window-state, single-instance, browser args)

First real extraction, targeting the fully-unit-testable seams. `Shenora.Core.ShenoraEnvironment`
unifies dev-mode detection that one source app duplicated across four files. `Shenora.WinForms`
gains `DpiHelper` (primary-monitor + per-device-DPI scales, pure `Scale` core), the merged
window-state stack (`WindowStateManager` with pure `ToPhysical`/`ToLogical`/`IsVisible`, an
`IWindowStateStore` seam covering both family storage styles plus `JsonFileWindowStateStore`),
and `SingleInstanceGuard` (per-scope FNV-1a key, activate broadcast, fail-open). `Shenora.WebView2`
gains `BrowserArguments` — the measured display-optimization preset with the
single-feature-switch rule and the CDP env-var-clobber fix. The placeholder no-public-types test
was replaced by real API-surface baseline tests (tracked baselines, `.actual` drift dumps).
Verified: 43 tests green (DPI math, state roundtrips, visibility strips, mutex acquire/release
across guards, argument composition), `verify` gate green.

### 2026-07-30 — P0: repo bootstrap

The repo was created from the family preset and turned into a library devkit workspace. All five
sibling repos were surveyed in parallel (the library-template repo, the org-system/host donor, and
the three desktop apps) to produce the extraction map — the brief's Phase 1 audit — recorded in
`local/EXTRACTION-MAP.md` (named) and `.claude/knowledge/extraction-sources.md` (tracked,
de-identified). The design contract (`docs/2026-07-30-shenora-design.md`) and the decision log
(`docs/DECISIONS.md` D1–D12) were written: two consumption profiles, four NuGet packages + one
npm package, net10.0, lockstep versioning, manual OIDC release, no push CI. The Sonora-preset
devtools/rules/skills were culled to the generic core and re-targeted; the docs system
(router/ARCHITECTURE/ROADMAP/TASKS/FIX-LOG/DECISIONS/CHANGELOG), the buildable solution skeleton,
the rewritten devtools (`build`/`test`/`verify`/`pack`/`doctor` + the desktop verification loop),
the release workflow, and the git repo + pre-commit guard were set up. Verified: `dev.mjs verify`
green (dotnet build + tests, npm build + tests, sensitive scan, knowledge check).

## Remaining

### P1 — Skeleton hardening (short tail)

Both original bullets are DONE — the placeholder types were pinned in P2 (see Done) and the
local-feed consumption smoke landed as P1.1 (`0776f37`). Only one item remains:
- **P1.2 — release-workflow dry run**, blocked until a GitHub remote exists (`TASKS.md`).

### P2 — Core host extraction (brief Phase 2) — COMPLETE except deliberate carry-overs

Everything above landed (increments 1–6, see Done). Carried forward on purpose:
- **DPI tail → P4** (`OnDpiChanged` handling + CSS-px→physical conversion) — lands with the
  overlay components that need it (drop zones, login windows).
- **Optimized form / frameless chrome → P4** — lands with the window manager + frontend window
  commands.
- **Stable-chunk frontend build guidance** (docs) → written with the P3 `@shenora/react` docs,
  where frontend build advice naturally lives.

### P3 — IPC extraction (brief Phase 3) — COMPLETE

Everything landed (increments 1–5, see Done): envelopes/errors/serializer defaults, dispatcher +
facade base + event bus, the WebView2 postMessage transport, the `@shenora/react` client, and
the live round-trip e2e. Carried forward on purpose:
- **Stable-chunk frontend build guidance** (docs for consuming apps: vite `manualChunks`, hashed
  assets vs the no-cache HTML policy) → lands with the P6 adoption docs, where a real consumer
  exercises it. Drop-zone hook + window-command helpers were always P4 surface.

### P4 — Modules + native services (brief Phase 4) — COMPLETE

Everything landed (increments 1–6 + phase review, see Done): scoped-container router + the
standard IPC composition, frameless chrome + frontend window commands, the native services
(dialogs/shell/clipboard/interaction), drag-drop zones + `useDropZone` (+ the P2.3b DPI tail),
secondary windows + tray, and the live sample/e2e proof.

### P5 — Auxiliary browser sessions (`Shenora.WebView2.Sessions`, D14) — COMPLETE

Everything landed (increments 1–4 + phase review, see Done): the one browser-configuration path
(`SessionBrowser` + init-timeout guard), the bounded LIFO render-session pool, the login-window
stack (`LoginWindow`/`LoginWindowController`/`CookieLoginFlow` — per-provider/per-account
profiles, silent refresh, clear-on-logout) and co-browse streaming (`CoBrowseSession` — CDP
screencast frames out, input dispatched back, human-solved by design), in its own package with a
live sample demo.

### P5.5 — Consolidation: cleanup, re-layer, roadmap revisit — IN PROGRESS, before P6

> **STATUS (2026-07-30, end of the second consolidation session): H1 · H2 · H3 · H4 · H5 · H6 · H8 are
> DONE. Only H7 and H9 remain.** Fourteen commits across two sessions; `dev.mjs verify` PASSES at
> **428 dotnet + 54 vitest**. See `## Done` above for the per-batch narratives (newest first) and
> `TASKS.md` `### P5.5` for the itemised remainder. Two notes for whoever picks this up:
> **(a)** several of H7's docs-drift items were fixed opportunistically while other batches landed, so
> re-check each against the tree instead of working the list as written;
> **(b)** four surface items were deliberately deferred OUT of H6 and INTO H9 or the H2 tail, with the
> reasons recorded next to them in `TASKS.md` — they are not oversights.

**What this phase is.** P0–P5 put the whole body of the kit down in a short span — five commits,
~8.7k lines of `src/` plus ~4.7k of tests, five packages and an npm client — extraction-first and
phase-gated, but moving fast, and with holes in the verification gate itself (see H5). P5.5 is the
deliberate **consolidation checkpoint**: clean up what that velocity left behind (duplication,
missing guards, convention drift), take the structural correction while it is still free (pre-1.0),
close the gate, and revisit the rest of the roadmap in light of what the pass taught. It is a
planned settling pass, not an emergency — the tree was green throughout.

Consolidation has three strands:

1. **Cleanup** — the first review spanning all of P0–P5 (2026-07-30): six parallel reviewers over
   the five packages, the npm client, the samples and the tree, briefed by `docs/REVIEW-GUIDE.md`.
   The baseline was green (`verify` PASSED at `130d4cd`), so everything found is a LATENT defect
   rather than a regression — which is exactly why it lands before a real app depends on the surface
   (P6) and before the 1.0 SemVer freeze (P7). Full itemised plan with `file:line` anchors:
   `TASKS.md` `### P5.5`, batches H1–H8.
2. **Re-layer** — the structural change below (D19 + D20), which the cleanup's own findings argued
   for and which is only cheap while nothing is published.
3. **Roadmap revisit** — this section, plus the amendments to P6/P7/Later that follow from both.

**And an API-shape correction** (user direction, 2026-07-30 — D21): for a whole application *feature*
the kit ships **primitives + lifecycle hooks, not the product**. `CoBrowseSession` had it backwards —
`DispatchInputAsync(string)` takes the source app's wire protocol as an opaque JSON string and
`ReadHotspotsAsync` encodes a co-browse UX decision, while the hooks that make a feature extensible
are missing (nothing signals the session ending or faulting, so a renderer crash leaves the frame
channel never completed and the app's reader waiting forever). The kit's other two session families
already got this right — the render pool ships the pool and the sample writes its own flow; the login
window keeps policy in a driver seam. Tracked as `TASKS.md` H9, after the re-layer.

**The phase also carries a structural change** (user direction after reading the review, approved
2026-07-30): the two Windows shell packages become one layer — `Shenora.WebView2` depends on
`Shenora.WinForms` — and the portable contracts plus the long-specified-never-built `IUiDispatcher`
move to `Shenora.Core`, so an app's own logic compiles with no Windows reference and a future mobile
shell can implement the same contracts. Design:
`docs/2026-07-30-shenora-relayering-design.md`; decisions: D19 + D20. This replaces the review's
proposed `InternalsVisibleTo`/linked-file workaround — the deduplication fix and the portability
seam turn out to be the same object, so one change buys both. Execution order matters: security
fixes first (H1 + H5), then the re-layer, then the dedup on top — see `TASKS.md`.

The review's own verdict was that the per-package internals are disciplined — the extraction
comments are load-bearing and accurate, the dependency graph holds exactly as documented, the IPC
error boundary leaks no exception text on any traced path, and the wire mirror is correct
field-for-field bar one missing constant. The weaknesses are **at the seams between packages, and
in the gate around them**:

- **Six confirmed P0s** (each re-verified against the code before being recorded): no path
  containment in file-mode serving (arbitrary file read, live in every dev session); the
  frameless-maximize ⇄ window-state seam (a maximized close makes restore a permanent no-op — live
  in the reference composition); `RenderSession` accepting cancellation tokens it never observes
  (one JS-blocked page starves the pool for the process lifetime); `NavigationGuard` — the
  documented SSRF boundary — bypassed by redirects and in-page navigation; `AddMessageDispatcher`
  enumerating facades inside its own singleton factory (StackOverflow, no diagnostic, on the
  documented cross-module composition); and a throwing app `OnLoading` callback leaving an
  unclosable login modal that then vetoes `Application.Exit`.
- **The duplication is causal, not cosmetic.** The UI-marshal pattern is hand-rolled 14 times with
  5 incompatible pre-handle policies — 7 unguarded, and one site carries a comment explaining the
  pre-handle trap then commits it on the next line. And the `Sessions → Shenora.WebView2` edge that
  D14 documents as deliberate is **declared but entirely unused**, which is why `SessionBrowser`
  re-implements browser arguments (re-introducing the CDP env-var gotcha), environment creation, the
  init-timeout guard and settings hardening — and why pooled/co-browse instances have none of the
  `NewWindowRequested`/`PermissionRequested`/`ProcessFailed` policies the host package already
  implements.
- **The gate had holes.** `Shenora.slnx` carries an empty `/samples/` folder (and omits
  `Shenora.Core`), so `verify` never compiled the reference composition or the e2e subject;
  `dev.mjs test <typo>` exited 0 having run nothing; and `check-sensitive` fails OPEN when the
  gitignored pattern file is absent — i.e. the private-name half of the guard never ran in CI.
- **Pre-1.0 surface work** that is far cheaper now than after the freeze: the API baseline doesn't
  gate `protected` members (so `BaseFacade.RouteMessageAsync`, the member every consumer overrides,
  is outside the SemVer gate) or default parameter values; `BaseModuleService`'s typed-payload
  feature type-checks nothing and its documented example doesn't compile; and the reference
  composition has to downcast `IMessageDispatcher` because form-dependent facades have no
  registration seam.

### P6 — Sibling adoption (brief Phase 5) — **COMPLETE 2026-07-31**

> ✅ **P6.1–P6.6 are all done; nothing here is pending.** The narrative entries are under `## Done`
> (newest first). What the phase actually delivered, against the framing below: the library is READY
> and `docs/ADOPTION.md` is the artefact an adopting app's own session works from — this repo never
> edited a sibling, on user direction. Six gaps were found and closed rather than recorded (the npm
> `.d.ts` UMD-global defect, the client's missing catch-all subscription, the absent dispatch
> `CancellationToken`, no synchronous `IEventBus.Emit`, an internal-only `IpcErrorMapping`, and a
> resource seam that could not answer anything but "200, here are all the bytes"), plus module
> release. Everything below is the ORIGINAL framing, kept for the record — its "adopt in the sibling
> first" premise was superseded, and its "smallest host" premise was stale before the phase started.

- Adopt in the newest desktop sibling first (smallest host, gaps already documented), via local
  feed + pinning; keep it runnable at every step. Then evaluate the other two desktop siblings
  and the server-backed app (shell-only profile).
- Feed every "the framework almost fits, but…" back into the API before 1.0.

**Revisited 2026-07-30 (post-consolidation):**
- **Do not start P6 before P5.5's H1–H5.** Adopting against a surface that is about to be re-layered
  (D19/D20) means doing the integration twice, and adopting against the pre-H5 gate means the
  adoption itself isn't verified — `verify` did not even compile the sample until H5.
- **Adoption gains a second dimension: portability.** With D20's contracts in `Shenora.Core`, put the
  adopting app's own facades in a `net10.0` project from day one (H4.3 proves the pattern on the
  sample). That makes the app's logic mobile-shareable as a side effect of adopting, and it turns
  the abstract question "are these the right portable contracts?" into a concrete one answered by a
  real app — feed the answer back as a D20 amendment.
- **The adoption is the real test of the review's fixes.** Several P5.5 P0s were latent-only
  (nothing in-repo triggered them); a real consumer is what proves them fixed rather than merely
  patched — notably the DI composition (facades injecting `IMessageDispatcher`), async disposal of
  singletons, and a relative `--app-root`.


**Scoped 2026-07-31 (survey done, nothing adopted yet) — and the premise above is now STALE.**
The first target is no longer a small host: it has grown an API tier, a plugin system, an MCP server
and a deployment stack, and its desktop side now carries 28 IPC modules against ~148 client
call-sites. It is still the right first target, but not for the reason originally given. What makes
it tractable is that both sides funnel through ONE seam each — a single client post/subscribe pair
and a single host dispatcher behind a one-method module interface — so swapping the IPC substrate is
two ADAPTERS rather than 28 rewrites — **both since written and run against the public surface**
(P6.4, above): expressible, and the exercise found two real defects that the guide alone had not.

**Reframed 2026-07-31 (user direction): this repo readies the LIBRARY and never edits the sibling.**
The adopting app's own session does the adoption, working from `docs/ADOPTION.md`; a sibling is a
CHECKPOINT that answers "is this capability present and safe?", never a spec to mirror. The staged
increments that used to be listed here as work for THIS repo are now that guide's Stages 1–4.
**P6.1/6.2/6.3/6.3a/6.4 are done; P6.5 (portability guidance) and P6.6 (feed back before P7 freezes
SemVer) remain** — see `TASKS.md` `### P6`.

On the model mismatch they bridge: the target speaks flat, uncorrelated, fire-and-forget IPC with an
event stream back. That is **not** a legacy shape to migrate away from — for a desktop shell the event
pipe is the correct DEFAULT and correlated request/response is the special case, because the dispatch
pipeline preserves the caller's synchronization context by design (measured: the same 3 s of work
stalls the UI thread 2 027 ms in-route, 0 ms handed off). So the adapters PRESERVE the model; what
they add is the missing correlation. Per D21 any wire-format compat lives in the ADOPTER's shim and
never in the kit's envelope — a question the 2026-07-30 extraction survey had deliberately left open
until adoption time, now decided.

### P7 — Stabilisation + 1.0 (brief Phase 6)

- API-surface baseline tests on; docs pass (XML docs, README per package section); CHANGELOG
  discipline from first publish; `Shenora.Hosting.AspNetCore` go/no-go (D10); first NuGet/npm
  publish via the release workflow; GitHub repo goes public.

**Revisited 2026-07-30 (post-consolidation):**
- **"API-surface baseline tests on" is not yet the SemVer gate it is assumed to be.** They dump
  `BindingFlags.Public` only, so `protected` members — including `BaseFacade.RouteMessageAsync`, the
  one member every consumer overrides — are ungated, along with default parameter values, `init` vs
  `set`, `required`, and attributes. P5.5 H6 closes this; 1.0 must not freeze behind a gate with a
  hole in it.
- **Part of the docs pass moves earlier.** P5.5 H7 already corrects the shipped-in-nupkg inaccuracies
  (package descriptions, README claims). What remains for P7 is genuinely new writing: per-package
  README sections, the XML-doc sweep enabled by turning CS1591 back on (H5), and the stable-chunk
  frontend build guidance carried over from P2/P3.
- **CHANGELOG discipline starts now, not at first publish** — the log is already missing the one fix
  that changed a published artifact's importability (`0776f37`), which is exactly the class of entry
  the discipline exists for.

### Later / candidates

- `Shenora.Hosting.AspNetCore` (SPA static policy, loopback-gated endpoint helpers) — D10.
- Mobile transport adapter (Capacitor or similar speaking the same IPC envelope) — D16; packaged
  at first mobile adoption (`@shenora/capacitor` vs an adapter in `@shenora/react`). **Revisited
  2026-07-30:** the decision point is unchanged (first real mobile adoption), but the .NET-side
  surface such a shell would implement is now enumerated rather than hypothetical — D20's portable
  contracts in `Shenora.Core` (`IUiDispatcher`, `IFileDialogs`, `IClipboardService`, `IUrlLauncher`,
  `IUiInteraction`). D16 covers the transport seam; D20 covers the feature seams. Neither ships an
  implementation until there is a consumer.
- Harvest-promotions from ongoing app development (D15) — any proven-nice feature gets
  generalized and lands here as a task before shipping in a minor.
- C++ launcher template (runtime check/install, staged self-update) as a repo template, not a package.
- Scaffolding skills once patterns exist (`new-ipc-module`, `new-native-service`).
- Contract codegen (C# ⇄ TS) — explicitly out of initial scope; revisit after adoption feedback.
