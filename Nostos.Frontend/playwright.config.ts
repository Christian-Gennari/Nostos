import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright E2E config for Nostos.
 *
 * The fixture is launched by globalSetup (fresh temp SQLite DB + real backend
 * + freshly built Angular app + MCP enabled with a random bearer token); the
 * base URL is read from e2e/support/fixture-state.json at runtime, so no port
 * is hard-coded here. Tests run serially (workers: 1) because all specs share
 * the single isolated backend instance and its temp DB.
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/global-setup',
  globalTeardown: './e2e/support/global-teardown',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: 'e2e/test-results/playwright-report', open: 'never' }],
  ],
  outputDir: 'e2e/test-results/artifacts',
  use: {
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'desktop-chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
      // Anchored to the file NAME: an unanchored /mobile.*\.spec\.ts/ also
      // matches the checkout path, so a worktree whose directory contains
      // "mobile" makes this project ignore every spec ("No tests found").
      // The library-transfer acceptance specs use their own multi-instance
      // fixture (`playwright.library-transfer.config.ts`); keep the default
      // smoke suite's runtime and shared fixture unchanged.
      testIgnore: [
        /[\\/]mobile[^\\/]*\.spec\.ts$/,
        /[\\/]library-transfer\.spec\.ts$/,
      ],
    },
    {
      name: 'mobile-chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 390, height: 844 },
        isMobile: true,
        hasTouch: true,
        deviceScaleFactor: 2,
      },
      testMatch: [/[\\/]mobile[^\\/]*\.spec\.ts$/],
      // The mobile-webkit acceptance spec belongs to the library-transfer
      // config (own fixture), not to the default single-backend smoke suite.
      testIgnore: [/[\\/]mobile-library-transfer\.spec\.ts$/],
    },
  ],
});
