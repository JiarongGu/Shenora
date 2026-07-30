# Re-layering design — one Windows shell layer, portable logic contracts

**Status:** approved 2026-07-30 (user direction). Not yet implemented — `docs/ARCHITECTURE.md`
still records the CURRENT as-built layering and stays authoritative until this lands.
**Decisions:** `docs/DECISIONS.md` D19 (shell layering) + D20 (portable contracts in Core).
**Amends:** the design contract §4 dependency rule (see its `## Amendments`).
**Plan:** `TASKS.md` `### P5.5` batch H4 (and its sequencing note).

## 1. Why

Two independent pressures met at the same seam.

**From the first full code review (P0–P5, 2026-07-30):** the UI-thread marshal pattern was
hand-rolled **14 times across 3 packages with 5 mutually incompatible pre-handle policies**. The
divergence was not cosmetic — it produced real defects, including a site whose comment explains the
pre-handle trap and then commits it on the next line (`LoginWindowController.cs:250`), 7 entirely
unguarded `BeginInvoke` calls in `Shenora.WebView2.Sessions`, and a P0 where `RenderSession` accepts
cancellation tokens it can never observe (one JS-blocked page starves the session pool for the
process lifetime). The review's two proposed fixes were a sideways project reference or a linked
source file — both of which reduce duplication and buy nothing else.

**From user direction:** WinForms and WebView2 serve the same purpose — they are one Windows
presentation layer, so they may live together. What deserves extraction as *interfaces* is the
**logic**: IPC and the feature contracts, because those are what a non-Windows shell (mobile) can
share.

That reframing is strictly better, because under it **the deduplication fix and the portability seam
are the same object** (§4). It also restores original intent rather than inventing something: design
contract §4 already assigns `IUiDispatcher` — the interface — to `Shenora.Core` with the
implementation in `Shenora.WinForms`. It was never built, which is why `Shenora.Core`'s shipped
NuGet description advertises a "UI-dispatcher seam" that does not exist (a review finding). This
design makes that claim true instead of deleting it.

**The rule being changed authorised its own revision.** Design contract §4 reads: *"never sideways
`WinForms`↔`WebView2` — the app composes them; revisit only if extraction proves it impossible."*
Extraction has now produced the evidence the rule asked for.

**The decisive fact:** neither documented consumption profile (§3 of the design contract) takes
`WinForms` without `WebView2` — profile 1 is `Core + WinForms + WebView2 + Ipc + @shenora/react`,
profile 2 is `Core + WinForms + WebView2`. The `WinForms`/`WebView2` split serves no profile.

*(Correction to this document's first draft: it also claimed "the `Ipc` split does real work because
profile 2 excludes it". That is false — `src/Shenora.WebView2/Shenora.WebView2.csproj:15` references
`Shenora.Ipc`, so profile 2 gets it transitively today; profile 2 merely doesn't USE the postMessage
bridge. What the `Ipc` split actually buys is real but different: `Ipc` stays `net10.0`, so server
and non-Windows code can speak the contract, and it keeps the contract transport-neutral for D16.
And what the "`WinForms` carries no `Ipc`" rule preserves is the **WinForms-only consumer** — a small
tray/single-instance utility with no web frontend — not profile 2. That rule stays; its
justification is corrected.)*

## 2. Decisions taken

| Question | Decision | Why |
|---|---|---|
| Windows shell shape | **Keep two packages; allow the downward edge `WebView2 → WinForms`** | ~10% of a merge's churn, preserves a WebView2-free option for a small tray/single-instance utility, reversible. `Shenora.WebView2` is ALREADY a WinForms assembly — `Shenora.WebView2.csproj:5` sets `<UseWindowsForms>true</UseWindowsForms>`, it hosts the `Microsoft.Web.WebView2.WinForms` control, and 5 of its files use a `Form`/`Control`-derived type — so the edge adds no new *technology* dependency, only an honest package reference. |
| Portable scope, this pass | **Contracts only — no mobile host, no adapter** | D16: "YAGNI on the package, not on the seam." Making app logic compile without Windows is the whole win; a shell with no user is not. |
| Contract home | **`Shenora.Core`** (no new package) | Design contract §4 already puts the portable seam types there, and D2 explicitly resists speculative packages. |

## 3. Target layering

```
Shenora.Core     net10.0             portable: builder · paths · env · event bus · modules
                                     · lifecycle  +  THE SHELL CONTRACTS (new, §4)
Shenora.Ipc      net10.0     → Core  portable: envelopes · dispatcher · facades · router
Shenora.WinForms net10.0-win → Core            Windows PRIMITIVES + contract implementations
Shenora.WebView2 net10.0-win → WinForms, Ipc   web hosting · bridge · drop zones · window commands
Shenora.WebView2.Sessions    → WebView2        auxiliary browser sessions
@shenora/react                                 the client half of the IPC contract
```

The boundary stops being "WinForms vs WebView2" (two peers pretending not to know each other) and
becomes **primitives → hosting-on-primitives**. Every edge still points strictly downward, so the
spirit of the old rule survives:

- `Shenora.Core` and `Shenora.Ipc` stay `net10.0` — no Windows dependency, ever. This is
  build-enforced, not review-enforced.
- **`Shenora.WinForms` still carries NO `Shenora.Ipc` dependency.** This is what keeps profile 2
  honest, and it is why `WindowCommandFacade` and the drop-zone stack remain in `Shenora.WebView2`
  (they need `Ipc`). That existing rationale is unchanged by this design.
- No cycles: `WinForms` never references `WebView2`.

## 4. The portable contract set

### 4.1 Moves to `Shenora.Core` unchanged

Verified platform-neutral in signature — these move with **no signature change**:

| Type | Note |
|---|---|
| `IClipboardService` | a mobile shell has a clipboard |
| `IFileDialogs`, `IFileDialogPathStore` | a mobile shell has a document picker |
| `FileDialogOptions`, `FileDialogFilter`, `FileDialogResult` | verified: strings, bools, lists only |

**This is a file SPLIT, not a file move** (first-draft error: "only a file/namespace move"). Every
contract above is declared *inside its implementation's file* — `IClipboardService` in
`ClipboardService.cs`, and `IFileDialogs`, `IFileDialogPathStore`, `FileDialogOptions`,
`FileDialogFilter`, `FileDialogResult` **and** `FileDialogsOptions` all in the single
`FileDialogs.cs`. The work is to extract each contract into its own new file under
`src/Shenora.Core/`, leaving the implementation behind — plus `FileDialogsOptions`, which references
`IFormInteraction` and stays Windows-side. Contracts move; implementation configuration does not.

**Accepted risk, recorded deliberately:** `FileDialogOptions` carries Win32 dialog vocabulary
(`CheckFileExists`, `CheckPathExists`, `ValidateNames`, `OverwritePrompt`, `AllowFileSelection`) and
`FileDialogResult.FilePath` is a filesystem path — so by §4.4's own bar this contract is
desktop-*flavoured*, and freezing it in `Shenora.Core` at 1.0 risks a breaking change to **Core** when
a real mobile shell wants a narrower shape (a document picker returns a content URI, not a path). We
accept that rather than pre-splitting a shape no consumer has asked for (D15 is harvest-driven, and a
documented MINOR break is allowed pre-1.0): document `FilePath` as "a path or URI the host can
resolve", treat the validation members as hints an implementation may ignore, and revisit at first
mobile adoption. The alternative, if that revisit is unwelcome, is to move only `IFileDialogs` +
`FileDialogResult` + `FileDialogFilter` and keep the Win32-flavoured members WinForms-side.

### 4.2 Splits — portable base in Core, Windows extension in WinForms

One implementation class continues to satisfy both faces, so there is no duplicated logic:

| Portable base (`Shenora.Core`) | Windows extension (`Shenora.WinForms`) |
|---|---|
| `IUrlLauncher { void OpenUrl(string url); }` | `IShellLauncher : IUrlLauncher { RevealInExplorer, OpenDirectory, LaunchProcess }` |
| `IUiInteraction { void BlockInteraction(); void UnblockInteraction(); }` | `IFormInteraction : IUiInteraction { SetMainForm(Form), GetMainForm(), GetMainFormHandle() }` |

**The moved members must be DELETED from the derived interfaces, not left in place.** `IShellLauncher`
declares `OpenUrl` today and `IFormInteraction` declares `BlockInteraction`/`UnblockInteraction`;
re-declaring an inherited member is CS0108 (hides inherited member) — a warning now, a **build error**
once H5 turns on `TreatWarningsAsErrors`, which the execution order schedules BEFORE this work.

Rationale: `OpenUrl` is meaningful on any platform; reveal-in-file-manager and launch-a-process are
desktop-only. Block/unblock interaction is meaningful anywhere; a `Form` accessor is not.

### 4.3 New in `Shenora.Core`

`IUiDispatcher` — the seam the design contract specified and P2 never built. See §5.

### 4.4 Deliberately NOT moved (the YAGNI guard)

Recorded so a later session does not "helpfully" move them: **the entire window-state stack**
(`IWindowStateStore`, `WindowState`, `WindowStateOptions`, `WindowStateManager`,
`JsonFileWindowStateStore`), `OptimizedForm`, `SplashPanel`, `TrayIcon`, `SecondaryWindows`,
`SingleInstanceGuard`, `DpiHelper`, `WinFormsBootstrap`, `StaThread`, and every implementation class.

`IWindowStateStore` is portable in *signature* and not in *meaning* — window geometry is a desktop
concept. Portable-in-signature is not the bar; **"app logic needs this contract to compile off
Windows" is the bar.**

## 5. `IUiDispatcher` — one owner for UI-thread marshalling

### 5.1 Contract (`Shenora.Core`)

**A single `bool IsAvailable` is NOT sufficient** — an adversarial review of the first draft of this
document caught it. Three of the 14 call sites have *different, review-earned* pre-handle policies,
and collapsing "no handle yet" into "gone" would reintroduce two defects a previous phase review
already fixed (a `StackOverflow` from unbounded re-invocation, and handle creation on the wrong
thread killing a secondary window's pump). Hence a three-state target plus an explicit
on-UI-thread query:

```csharp
public enum UiTargetState { NotReady, Ready, Gone }   // no handle yet · usable · disposed/torn down

public interface IUiDispatcher
{
    UiTargetState State { get; }
    bool IsOnUiThread { get; }          // Ready AND the caller is already on the UI thread

    /// Run on the UI thread: inline when already there, else non-blocking post.
    /// TRUE = ran or was posted. FALSE = State != Ready — the CALLER decides what that means
    /// (its own policy: drop+log, defer behind a flag, or apply directly). Never throws.
    bool Post(Action work);

    /// Same, for an async body — exists so no caller ever hand-rolls `BeginInvoke(async …)`,
    /// which is an unobservable UI-thread crash (measured in the sessions package).
    bool Post(Func<Task> work);

    Task InvokeAsync(Action work, CancellationToken cancellationToken = default);
    Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default);
    Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);

    /// Never faults: returns <paramref name="fallback"/> if the body throws or the target is not
    /// Ready. Required by the co-browse input path, whose contract is that one bad input message
    /// must never fault the session.
    Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback,
                                    CancellationToken cancellationToken = default);
}
```

Deliberately NOT included: `Task<T> InvokeAsync<T>(Func<T>)` — no call site among the 14 needs it,
and every public member is SemVer surface.

**Behaviour when `State != Ready`:** the `InvokeAsync` overloads return a **faulted** task —
`ObjectDisposedException` for `Gone`, `InvalidOperationException` for `NotReady` — never a task that
hangs. `Post` returns `false`. `InvokeOrDefaultAsync` returns the fallback.

### 5.2 Windows implementation (`Shenora.WinForms`)

`public sealed class WinFormsUiDispatcher(Control owner) : IUiDispatcher` — the correct semantics,
written exactly once:

1. `IsDisposed` / `IsHandleCreated` pre-check **before** `InvokeRequired` (pre-handle,
   `InvokeRequired` lies — the earned invariant in `.claude/knowledge/webview2-hosting.md`).
2. Non-blocking `BeginInvoke` — never a blocking `Invoke` off the UI thread (a measured AppHang).
3. Already on the UI thread → run inline (the one legitimate inline case).
4. The body is **guarded** — it never becomes an unhandled UI-thread exception. For the
   `InvokeAsync` overloads the exception completes the returned task (the caller observes it). For
   `Post` there is no caller to observe it, so it goes to an optional
   `Action<Exception>? onPostFailure` supplied at construction; when that is null the exception is
   logged and swallowed. **The guard wraps the INLINE path identically** — `Post` must never throw
   to its caller just because the caller happened to already be on the UI thread (two existing
   copies swallow in both paths; one wraps both in a single `try`). `Post`'s `false` means only
   "`State != Ready`", never "the body failed" — a posted body's failure happens after `Post`
   returns, by definition.
5. The returned task **observes the cancellation token** (`WaitAsync`), so no caller can hand in a
   token that does nothing.

**Per-control, not per-application** — this is load-bearing: `Shenora.WebView2.Sessions` marshals to
its *anchor* form, and `SecondaryWindows` run their own STA threads with their own pumps. A single
app-wide dispatcher would be wrong for both.

**Public, not internal.** A project reference does not grant `internal` access, so an internal
helper would still need `InternalsVisibleTo` for two packages. A public per-control dispatcher is
the seam's Windows implementation, earns its keep on its own merits, and lets `Shenora.WebView2` and
`Shenora.WebView2.Sessions` consume it through the new edge with no visibility tricks and no linked
source files.

### 5.3 What this retires — and what it does NOT

Retires: 14 marshal copies · 5 divergent pre-handle policies · the 7 unguarded `BeginInvoke`s in
Sessions · the inverted guard at `LoginWindowController.cs:254` (whose own comment two lines above
explains the trap it then commits) · and the `InternalsVisibleTo`/linked-file workarounds the review
proposed.

**Does NOT close on its own** (a first-draft overclaim, corrected): the `RenderSession` cancellation
P0. Token observation via `WaitAsync` makes the *awaiter* return; it does not kill the wedged
operation or release the pool's accounting. `TASKS.md` H2 still owes an `OpTimeout` and
"the pool discards an instance whose op was abandoned". This design makes that fix **mechanical**,
not done.

### 5.4 Per-site policy table — the three sites that KEEP their own behaviour

H4.2 replaces the *mechanism* everywhere but must preserve these decisions, each earned in a
previous review. Each reads `State`/`IsOnUiThread` and applies its own policy on `false`:

| Site | Policy to preserve |
|---|---|
| `DropZoneManager.MarshalToUi` | Returns `false` so **the caller proceeds inline** — re-invoking the caller here "recursed without end". Note its pre-handle branch ALSO runs Win32 on a worker thread, which is a separate open finding (`TASKS.md` H2): the correct post-fix behaviour is drop-and-log on `NotReady`, inline only when `IsOnUiThread`. |
| `SecondaryWindows.Post` | Pre-handle must be a **no-op that carries intent in a flag** (`CloseRequested`, and the `ActivateRequested` H2 adds) — posting there "would create the handle on the wrong thread and kill the pump". |
| `SplashPanel` | Pre-handle **applies directly** — deliberate, and correct for a control not yet realized. |
| `CoBrowseSession` input/hotspot paths | Must not fault the session — use `InvokeOrDefaultAsync`, matching the existing `RunOnUiAsync(body, fallback)` contract. |

## 6. Composition

`UseWinForms` registers one instance per service and exposes the portable face beside it, so app
logic may inject either and receives the same singleton:

```csharp
services.TryAddSingleton<IFormInteraction, FormInteraction>();
services.TryAddSingleton<IUiInteraction>(sp => sp.GetRequiredService<IFormInteraction>());
services.TryAddSingleton<IShellLauncher, ShellLauncher>();
services.TryAddSingleton<IUrlLauncher>(sp => sp.GetRequiredService<IShellLauncher>());
services.TryAddSingleton<IUiDispatcher, MainFormUiDispatcher>();   // resolves IFormInteraction
```

The main form does not exist at container-build time — the WinForms runner registers it *after* the
form factory runs — so the DI-registered dispatcher must resolve it lazily. Two types, one
implementation:

- **`public sealed WinFormsUiDispatcher(Control owner)`** — explicit, per-control. What
  `Shenora.WebView2` and `Shenora.WebView2.Sessions` construct for a specific WebView2 control,
  anchor form, or secondary-window form. Public because another package consumes it (§5.2).
- **`internal sealed MainFormUiDispatcher(IFormInteraction interaction)`** — the DI singleton, and
  **internal**: only `UseWinForms` constructs it, so it needs no cross-package reach and should not
  become SemVer surface. It reads the main form per call, caching one `WinFormsUiDispatcher` per form
  instance (rebuilt if the form changes). Same lazy-main-form pattern `FileDialogs` already uses for
  dialog ownership.

`State` mapping for `MainFormUiDispatcher`, all three cases (the middle one is a gap the first draft
missed): no form registered yet → `NotReady`; a live registered form → `Ready`; **a registered form
that has been disposed → `Gone`.** That last case is real, not theoretical: the WinForms runner never
clears the reference, so after the main form is disposed at shutdown `GetMainForm()` keeps returning a
disposed form for the rest of the process. `State` must test `IsDisposed`, not just null. If an app
replaces `IFormInteraction` with an implementation that never registers a form, `State` stays
`NotReady` forever and every `Post` returns `false` — correct and silent by design.

**No default registration in `Shenora.Core`.** Core has no UI thread to dispatch to, so it ships the
contract only; a shell package registers an implementation (`UseWinForms` does). Portable logic that
genuinely needs UI marshalling must therefore treat it as a host-provided dependency — deliberately
NOT a no-op default, which would silently swallow UI work in a host that forgot to register one.

**What this does NOT do:** it does not remove the `IMessageDispatcher` downcast in the reference
composition (`MainForm.cs:85`) — that is H6, separate work. The real relationship runs the other way
and is worth stating: **D19 UNBLOCKS H6.** H6's recommended fix is for the form-dependent facades to
resolve the main form lazily via `IFormInteraction` — but those facades live in `Shenora.WebView2` and
`IFormInteraction` lives in `Shenora.WinForms`, so that fix was *impossible* before this edge existed.

Namespaces stay **flat per package** (`Shenora.Core`) — the repo has no sub-namespaces and this
design does not introduce the first one.

## 7. Acceptance

1. `dev.mjs verify` green, with the sample projects actually in the solution (P5.5 batch H5 — today
   `Shenora.slnx` carries an empty `/samples/` folder, so `verify` never compiles them).
2. All five API baselines reviewed and promoted deliberately; `CHANGELOG.md` gains a `### Breaking`
   entry describing the moved contracts.
3. **The portability proof:** a `net10.0` project `samples/Shenora.Sample.Logic` holding one facade
   that picks a file, reads the clipboard and opens a URL, referenced by the desktop sample. If it
   compiles with no Windows reference, the seam is real; if a Windows type is later dragged into a
   contract, that project goes red. This is the only mechanism that keeps portability enforced
   rather than assumed. Two conditions or it proves nothing: (a) it must inject **`IUrlLauncher`**,
   not `IShellLauncher` — today's `SampleFacade` injects the Windows extension, so the facade has to
   be *split*, with the portable routes moving out and the desktop-only ones (reveal-in-Explorer,
   secondary windows) staying in the desktop sample; and (b) it must be added to `Shenora.slnx` —
   which is a SECOND solution edit after H5's, or `verify` never compiles the very thing that makes
   the guarantee real.
4. No behavioural regression in the areas the marshal collapse touches: the sample's live e2e paths
   (frameless window commands, drop-zone registration, secondary windows, the render-session pool
   round-trip) re-proven per `docs/REVIEW-GUIDE.md` §6.
5. **Doc sync in the same commit** — four tracked docs assert the OLD layering and will actively
   argue a future session back to it: `docs/ARCHITECTURE.md`'s "Dependency rules (enforced by
   review) … never sideways"; `docs/REVIEW-GUIDE.md` §5's "the ONE deliberate package-on-package
   edge"; `README.md`'s package table (it ships inside every nupkg, and `Shenora.WinForms` stops
   owning the dialog/clipboard/shell contracts); and `docs/RELEASING.md`'s "the two leaf packages",
   since `Shenora.WinForms` stops being a leaf. Also the design contract's own §4 table rows
   (`Shenora.WebView2`'s *Depends on* column, and which package lists the moved contracts).
   The `.claude/` tier was already updated ahead of the work.

## 8. Sequencing

**The re-layer must not block the security fixes.**

1. **P5.5 H1 + H5** on the *current* layering — path containment, the `NavigationGuard` redirect
   bypass, the notification-serialize guard, and the gate holes. Surgical, no structural churn.
2. **This design**, as its own commit: move the contracts, split the two mixed interfaces, add
   `IUiDispatcher` + `WinFormsUiDispatcher`, take the `WebView2 → WinForms` edge.
3. **H4 dedup** on top — now mechanical, because the owner exists.
4. **H2 / H3 / H6 / H7** — several of H2's marshal-related P0s dissolve into step 2 and should be
   re-checked rather than fixed twice.

**Three known overlaps between step 1 and steps 2–3** (found by review; noted so the work isn't done
twice or thrown away):
- H1's "dispose the leaked process handle in `WebViewHost`" patches a **copy** of
  `ShellLauncher.OpenUrl`. Fix it in step 1 anyway (it is a leak today), but step 3 should delete the
  copy entirely and delegate to `IUrlLauncher` — add it to H4.5's dedup list.
- H1's "enforce `NavigationGuard` in `NavigationStarting`, same wiring for pool instances" edits
  precisely the wiring H4.4 replaces with `WebViewHost.WireEventPolicies`. Re-check after step 3
  rather than writing it twice.
- H5 adds the sample projects to `Shenora.slnx` in step 1; §7.3's `Shenora.Sample.Logic` arrives in
  step 2/3 and needs a second solution edit.

## 9. Cost, risk, non-goals

**Cost:** a larger diff than the review's original H4 (the moves touch every consumer file); a
`### Breaking` CHANGELOG entry. **Exactly TWO API baselines change** — `Shenora.Core.txt` (gains the
contracts + `IUiDispatcher`) and `Shenora.WinForms.txt` (loses the contracts, gains the dispatchers
and the split interfaces). The dump is per-assembly with unqualified type names, so
`Shenora.Ipc.txt`, `Shenora.WebView2.txt` and `Shenora.WebView2.Sessions.txt` must NOT move: a diff
there is a signal, not noise. (First draft said "all five churn", which invites blanket promotion —
the opposite of what the gate is for.) All free pre-1.0 — nothing is published, and this is the last
cheap moment.

**Risk:** the marshal collapse touches code whose real behaviour is proven by e2e rather than unit
tests, which is why §7.4 requires re-proving the live paths rather than trusting a green suite.

**Non-goals:** no mobile shell, no Capacitor adapter, no transport work (the IPC transport is
already pluggable per D16); no merge of the Windows packages; no move of the window-state stack; no
change to the `Ipc` split or to `Shenora.WebView2.Sessions`' own package boundary (D14).
