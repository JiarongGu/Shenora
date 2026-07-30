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
- **Seams over flags.** Extension points are interfaces the app implements
  (`IWindowStateStore`, injected scripts, custom schemes, transports), not boolean options
  that switch between two consumers' behaviors.
- **Placement is a design decision, not an accident (D19/D20).** A **portable contract** belongs in
  `Shenora.Core` (`net10.0`); its **Windows implementation** belongs in `Shenora.WinForms`; web
  hosting layers on top (`Shenora.WebView2` → `Shenora.WinForms` is a SANCTIONED downward edge —
  the old "never sideways" rule was retired on evidence, see D19). The bar for moving a contract to
  Core is **"app logic must be able to compile off Windows"**, NOT "the signature happens to be
  platform-neutral" — which is exactly why the whole window-state stack stays in `Shenora.WinForms`
  (window geometry is a desktop concept). No new package for a seam (D2); a sixth
  `*.Abstractions` package was considered and rejected.
- **Options records over magic values.** Every number/color/URL a source app hardcoded (dev port,
  background color, timeouts, batch intervals) becomes a documented option with the family-proven
  default.
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
