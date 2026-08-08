---
name: new-ipc-module
description: Walk the full chain for adding an IPC module — the module class, routes, DI registration, the optional TypeScript half, the wire mirror, the surface gates. Use before writing a new ModuleBase subclass, adding a route the client will name, or wiring a module into the dispatcher.
---

# new-ipc-module

A module spans up to eight files in two languages, and the checks that catch a half-finished chain
— the API baseline, the surface lexicon, the wire mirror — all fail at the END of `verify`, long
after the facade compiles. Walk it in this order and none of them surprises you.

## Steps

1. **Decide whether it belongs in the kit at all.** A module in `src/` is SemVer surface and must
   clear the two-consumer bar (`.claude/knowledge/generic-library.md`) — one app needing it is not
   evidence. A demo or an app's own module goes in `samples/`, where that bar does not apply. If it
   belongs to an ADOPTING app, this is the wrong skill: that is `docs/ADOPTION.md`, and this repo
   never edits a sibling.
2. **Copy the closest exemplar** instead of writing from scratch:
   - kit module + opt-in registration → `src/Shenora/Modules/FileDialog/FileDialogModule.cs` +
     `FileDialogServiceCollectionExtensions.cs`
   - kit module that is CORE (configured, never added) → `src/Shenora/Modules/Requests/IpcRequestsModule.cs`
     + `IpcRequestExtensions.cs`'s `builder.UseRequests(…)`
   - an app's own module → `samples/Shenora.Sample.Desktop/SampleFacade.cs`
   - a few ad-hoc routes, no class → `MainForm.cs`'s `dispatcher.MapModule("RENDER", routes => …)`
3. **The module class.** `sealed`, `: ModuleBase`, override `ModuleName`, switch inside
   `RouteMessageAsync(request, context, ct)`. Read payloads with `PayloadHelper.GetRequiredValue<T>`
   /`GetOptionalValue<T>`, return `Done()` from a void route, and end the switch with
   `throw UnknownType(request)` — the base owns that shape. Expected failures are an
   `OperationException`, **never one built from `ex.Message`**: its message crosses the wire
   verbatim, so that wrapper bypasses the whole error boundary. A route name the client also types
   becomes a `public const string` (see `IpcRequestsModule.ListType`).
4. **Emit through `context.Publish(type, payload, scope)`**, never a hand-typed module literal.
   ⚠ **There is nothing to declare for long work** (D66): every request is tracked from dispatch, so a
   slow route just calls `context.Report(new IpcProgress(…))` and returns normally. The token it is
   handed IS the one `CANCEL` targets. Work the route hands OFF to outlive the request needs its own
   token — do not capture that one. `.claude/knowledge/ipc-contracts.md` is the authority here — read
   it before adding a route that emits, awaits, or fails in a new way.
5. **Register.** Plain DI composition → `services.AddIpcModule<XModule>()`. Needs the live
   window → map LATE from where the form exists (`dispatcher.MapModule(facade)` in `MainForm`),
   never inside `UseMessageDispatcher`'s configure callback, which runs before any form. An opt-in
   kit cluster gets its own `AddShenoraX(this IServiceCollection, XOptions? options = null)` taking
   the options RECORD — a configure callback cannot assign `init`-only properties (CS8852).
6. **The client half, only if the kit ships it.** `src/Shenora.React/src/x.ts`: a
   `BaseModuleService<XRequests>` subclass mirroring `windowCommands.ts`, with `XRequests` a plain
   `interface` — `extends Record<string, unknown>` widens the key type back to `string`, so typos
   compile and every payload collapses to `unknown`. Export from `index.ts` and pin it TWICE in
   `index.test.ts`: the runtime `EXPECTED_EXPORTS` array AND the `ExportedTypeSurface` tuple, since
   the runtime pin is structurally blind to `export type`.
7. **Mirror the wire.** Anything both sides name — route constants, event types, the module name, a
   new payload record — gets a case in `tests/Shenora.Tests/Ipc/WireMirrorTests.cs`
   (`AssertMirroredFields` for a record). Keep the `Assert.NotEmpty` parser self-check, or a regex
   that matched nothing passes for the wrong reason. A new `IpcErrorCodes` value must exist on both
   sides or be declared in the client's `ClientOnlyIpcErrorCodes`.
8. **Test** in the `tests/Shenora.Tests/` folder mirroring the module's package (`Ipc/`,
   `WebView2/`, …), following `OperationsFacadeTests` — and add a `DoesNotContain` leak assertion
   for any new error path.
9. **Take the surface gates deliberately.** `dev.mjs verify` will stop on the API baseline (review
   the emitted `.actual`, copy it over the baseline, record the change in `CHANGELOG.md`) and on
   the surface lexicon (a type name built from a word it does not know — renaming after the
   MECHANISM is the common answer, adding the word is the rare one). Then sync
   `docs/ARCHITECTURE.md` and close with `/phase-review`.
