import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 60000,
  retries: 0,
  workers: 1, // TUI tests must run serially
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    // Each test gets a fresh terminal
    trace: 'retain-on-failure',
  },
  expect: {
    timeout: 30000,
  },
});
