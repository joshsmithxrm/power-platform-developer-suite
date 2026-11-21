import { defineConfig } from "@microsoft/tui-test";

export default defineConfig({
  // Only the screen specs are tui-test tests. The default pattern also matched
  // tools/*.test.mjs, which are node:test files: tui-test imported them and
  // ignored their results. They run on their own with `npm run test:tools`.
  testMatch: "tests/**/*.spec.ts",
  // No test retry. A failed launch of the application under test (before any
  // assertion) is retried by tools/launch-retry.mjs, and only that.
  retries: 0,
  trace: true,
  timeout: 60000,
});
