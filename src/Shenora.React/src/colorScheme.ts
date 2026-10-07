/**
 * The app's colour scheme: whether it follows the OS's light or dark setting or is held at one. Mirrors
 * `Shenora.Modules.Platform.ColorSchemeModule`, pinned by `WireMirrorTests`.
 *
 * ⚠ **Style with CSS, not with this.** The Chromium shell applies the setting to its browser engine, and the
 * WebView2 shell to each WebView the app gave it to, so `prefers-color-scheme` follows it: a stylesheet or a
 * `matchMedia` listener re-renders for a change with no call here. This is for the app's own settings: what the
 * choice is now, and changing it.
 *
 * ⚠ **Branch on the `colorScheme` capability**, which the app advertises once it mounts the route
 * (`AddShenoraColorScheme`). The app keeps the choice across launches, from the host's `IColorScheme.Changed`.
 *
 * ```tsx
 * const scheme = new ColorScheme();
 * <select onChange={(e) => void scheme.set(e.target.value as ColorSchemeKind)}>…</select>
 * ```
 */
import type { ShenoraBridge } from './bridge.js';
import { BaseModuleService } from './moduleService.js';

/** The host's `ColorScheme` enum, serialized. */
export type ColorSchemeKind = 'system' | 'light' | 'dark';

// ⚠ A plain interface — NOT `extends Record<string, unknown>`, which widens `keyof TRequests & string`
// back to `string`, so a mistyped route compiles and every payload collapses to `unknown`.
interface ColorSchemeRequests {
  GET: void;
  SET: { scheme: ColorSchemeKind };
}

interface ColorSchemeWire {
  scheme: ColorSchemeKind;
}

/** Typed client for the host's `SHENORA.COLOR_SCHEME` module. */
export class ColorScheme extends BaseModuleService<ColorSchemeRequests> {
  constructor(bridge?: ShenoraBridge) {
    super('SHENORA.COLOR_SCHEME', bridge);
  }

  /** The setting now: following the OS, or held at light or dark. */
  async get(): Promise<ColorSchemeKind> {
    const wire: ColorSchemeWire = await this.send('GET');
    return wire.scheme;
  }

  /** Change it. The page's `prefers-color-scheme` follows, and the host tells the app, which saves the choice. */
  set(scheme: ColorSchemeKind): Promise<void> {
    return this.send('SET', { payload: { scheme } });
  }
}
