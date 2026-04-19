import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['**/*.test.ts'],
    reporters: 'default',
    testTimeout: 15_000,
  },
});
