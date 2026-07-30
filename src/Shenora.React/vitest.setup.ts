import { afterEach } from 'vitest';

// Register @testing-library/react's cleanup explicitly (P5.5 H7). RTL only self-registers when
// `afterEach` is a GLOBAL, and this project keeps `globals: false` — see vitest.config.ts for what
// that silently cost. Doing it here means the tests keep their explicit vitest imports and still get
// per-test unmounting.
//
// Guarded + dynamically imported because the environment is per test FILE: four suites run in the
// node environment, where there is no document for RTL to import against, let alone clean up.
if (typeof document !== 'undefined') {
  const { cleanup } = await import('@testing-library/react');
  afterEach(cleanup);
}
