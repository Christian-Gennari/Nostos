/**
 * Reopening a long EPUB lands on the saved page, desktop width (#1271).
 * The shared body and the fixture are in `support/reader-restore-position.ts`.
 */
import { test } from '@playwright/test';

import { expectRestoreLandsOnSavedPage } from './support/reader-restore-position';

test.use({ serviceWorkers: 'block' });

test('desktop: reopening deep inside a long chapter lands on the saved page', async ({ page }) => {
  await expectRestoreLandsOnSavedPage(page);
});

test('desktop: the saved page survives resizes right after opening', async ({ page }) => {
  await expectRestoreLandsOnSavedPage(page, [100, 400]);
});
