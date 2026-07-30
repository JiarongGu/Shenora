import { defineConfig } from 'vitest/config';

// There was NO vitest config at all before P5.5 H7, and the cost was not style — it was silent
// leakage. With no config `globals` defaults to false, so `afterEach` is not a global; and
// @testing-library/react registers its automatic `afterEach(cleanup)` ONLY when it finds one
// (`typeof afterEach === 'function'`). So auto-cleanup never registered: every `renderHook` that a
// test did not unmount by hand stayed MOUNTED for the rest of its file, with its effects, event
// subscriptions and resize listeners live. The suite was green only because each test builds a
// private transport/bus, i.e. by luck rather than by isolation.
//
// `globals` stays FALSE on purpose — the tests import describe/it/expect explicitly, which is the
// better habit — so cleanup is registered explicitly in setupFiles instead of being bought as a
// side effect of turning globals on.
export default defineConfig({
  test: {
    setupFiles: ['./vitest.setup.ts'],
    // Per-file `// @vitest-environment jsdom` docblocks stay the source of truth for which suites
    // need a DOM; the setup file adapts (see it). Defaulting everything to jsdom would hide that
    // distinction and pay for a DOM in the four node-environment suites.

    // Vitest's default already, pinned because something DEPENDS on it: the guard in
    // src/testing/autoCleanup.test.ts observes cleanup by mounting in one test and asserting the DOM
    // is empty in the NEXT one, which is the only way to see cleanup from inside the suite it
    // protects. Under shuffling those two could invert and the guard would pass vacuously — a
    // tripwire that cannot fail, which is worth nothing.
    sequence: { shuffle: false },
  },
});
