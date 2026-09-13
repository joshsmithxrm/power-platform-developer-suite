import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './e2e',
    timeout: 120_000,
    retries: 0,
    workers: 1,
    reporter: process.env.CI ? [['line'], ['html', { open: 'never' }]] : 'list',
    use: {
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure',
    },
    outputDir: 'test-results',
});
