import { defineConfig } from 'vitest/config';

// Node environment throughout — this package never touches a DOM. `globals` stays FALSE to match the
// React package's habit: the tests import describe/it/expect explicitly.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});
