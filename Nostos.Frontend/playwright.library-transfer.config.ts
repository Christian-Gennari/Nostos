import { defineConfig, devices } from '@playwright/test';

/**
 * Real-backend browser QA for library export/import/activation (#680, slice B10).
 *
 * Separate from `playwright.config.ts` on purpose: it starts its own
 * multi-instance fixture (`e2e/support/library-transfer-fixture.mjs`), runs
 * Chromium/Firefox/WebKit against the production Angular build served by a
 * real SelfHosted backend, and is deliberately NOT part of the default
 * `npm run e2e` or the frontend CI job (three engines + per-scenario
 * disposable backends is an acceptance suite, not a per-push smoke test).
 *
 * Run one browser at a time so each invocation gets fresh fixture state:
 *   npm run e2e:transfer -- --project=lt-chromium
 *   npm run e2e:transfer -- --project=lt-firefox
 *   npm run e2e:transfer -- --project=lt-webkit
 *   npm run e2e:transfer -- --project=lt-mobile-webkit
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/library-transfer-global-setup',
  globalTeardown: './e2e/support/library-transfer-global-teardown',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 360_000,
  expect: { timeout: 20_000 },
  reporter: [
    ['list'],
    [
      'html',
      { outputFolder: 'e2e/test-results/library-transfer-report', open: 'never' },
    ],
  ],
  outputDir: 'e2e/test-results/library-transfer-artifacts',
  use: {
    actionTimeout: 30_000,
    navigationTimeout: 45_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    // The app registers a service worker; the suite asserts live network
    // behaviour, so the SW never intercepts fixture requests.
    serviceWorkers: 'block',
  },
  projects: [
    {
      name: 'lt-chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
      testMatch: /[\\/]library-transfer\.spec\.ts$/,
      testIgnore: /[\\/]mobile[^\\/]*\.spec\.ts$/,
    },
    {
      name: 'lt-firefox',
      use: { ...devices['Desktop Firefox'], viewport: { width: 1280, height: 800 } },
      testMatch: /[\\/]library-transfer\.spec\.ts$/,
      testIgnore: /[\\/]mobile[^\\/]*\.spec\.ts$/,
    },
    {
      name: 'lt-webkit',
      use: { ...devices['Desktop Safari'], viewport: { width: 1280, height: 800 } },
      testMatch: /[\\/]library-transfer\.spec\.ts$/,
      testIgnore: /[\\/]mobile[^\\/]*\.spec\.ts$/,
    },
    {
      // One narrow mobile viewport for the main path, on the riskiest engine.
      name: 'lt-mobile-webkit',
      use: { ...devices['iPhone 13'] },
      testMatch: /[\\/]mobile-library-transfer\.spec\.ts$/,
    },
  ],
});
