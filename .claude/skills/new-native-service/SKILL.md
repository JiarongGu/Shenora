---
name: new-native-service
description: Walk the chain for adding a native desktop service — where the contract goes, the Windows implementation, DI registration, the registration tripwire. Use before adding a clipboard/dialog/shell/interaction capability, or whenever a new contract has to be placed between Shenora.Core and Shenora.WinForms.
---

# new-native-service

The chain is short and the first step is the one that matters: placement decides whether an app's
own logic can compile off Windows (D19/D20), and moving a contract later is a breaking change.

## Steps

1. **Place the CONTRACT before writing anything.** The bar is *"app logic must be able to compile
   off Windows"*, NOT "the signature happens to be platform-neutral" — which is why the whole
   window-state stack correctly stays in `Shenora.WinForms`. Portable → `Shenora.Core`. Partly
   portable → SPLIT it: the portable slice in Core, the desktop-only operations on an interface
   deriving from it (`IShellLauncher : IUrlLauncher`, `IFormInteraction : IUiInteraction`).
   Windows-only concept → `Shenora.WinForms` alone. Never a new package (D2).
2. **Write the contract into an existing grouped file** — `ShellContracts.cs`,
   `FileDialogContracts.cs` — rather than one file per interface, and name it for the MECHANISM
   (D22): the surface lexicon gate rejects a domain word, and a scenario name is usually a
   placement smell rather than a naming problem.
3. **Implement in `Shenora.WinForms`**, `sealed`, mirroring `ClipboardService.cs`. Any OLE feature
   — clipboard, file dialogs, drag-drop — runs through `StaThread.RunAsync`. Guard arguments with
   `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace`, and decide
   the empty-versus-null policy explicitly: empty is usually app DATA and null is a caller bug
   (`Clipboard.SetText("")` throws, so empty routes to `Clear()` instead).
4. **Register in `WinFormsHostExtensions.UseWinForms`** with `TryAddSingleton`, so an app's own
   registration wins. If you split the contract in step 1, register the portable face resolving to
   the SAME singleton — `TryAddSingleton<IUiInteraction>(sp => sp.GetRequiredService<IFormInteraction>())`
   — or an app depending on the Core contract gets a second instance. Anything that needs the main
   form must resolve it LAZILY: the provider is built before the runner creates the form, so
   anything captured at registration captures null.
5. **Extend `tests/Shenora.Tests/WinForms/NativeServicesRegistrationTests`** with both halves it
   already asserts — the service is registered, AND an app registration wins. Behaviour tests go in
   their own class, and anything that realizes a window handle runs through `TestSupport/Sta.Run`:
   an OLE failure on an MTA thread is not a test failure, it is a BLOCKING WinForms dialog that
   stalls the whole suite.
6. **Read `.claude/knowledge/winforms-shell.md`** before touching lifetime, closing, or window
   state — `CloseReason.UserClosing` also fires for a programmatic `Close()`, `SystemEvents` holds a
   strong static reference that leaks the form, and pre-handle intent belongs in a flag rather than
   a posted callback.
7. **Take the surface gates deliberately.** `dev.mjs verify` will stop on the API baseline (review
   the emitted `.actual`, copy it over, record the change in `CHANGELOG.md`) and on the surface
   lexicon. Then sync `docs/ARCHITECTURE.md`, add a row to `docs/ADOPTION.md`'s
   contract-substitution table if an adopter would delete hand-rolled code for it, and close with
   `/phase-review`.
