/**
 * Library-transfer main path at a narrow mobile viewport (iPhone 13 / WebKit).
 *
 * The assignment asks for one narrow mobile viewport for the main path:
 * export from Settings -> native download -> import into a fresh empty
 * instance -> auto-activation -> reload serves the source library. The full
 * scenario matrix lives in `library-transfer.spec.ts`.
 */
import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

import {
  TRANSFER_ARTIFACTS,
  bookByTitle,
  killTransferInstance,
  launchTransferInstance,
  librarySnapshot,
  loadTransferFixture,
  snapshotCounts,
  transferInstance,
} from './support/library-transfer-harness';

test.describe.configure({ mode: 'serial' });

function collectErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  return errors;
}

test('mobile main path: export, then import into an empty instance', async ({ page }, testInfo) => {
  const source = transferInstance(loadTransferFixture(), 'source');
  const errors = collectErrors(page);

  await page.goto(`${source.baseUrl}/settings`);
  await expect(page.getByTestId('library-transfer-card')).toBeVisible();
  await expect(page.getByTestId('export-start')).toBeVisible();

  const downloadPromise = page.waitForEvent('download', { timeout: 180_000 });
  await page.getByTestId('export-start').click();
  await expect(page.getByTestId('export-ready')).toBeVisible({ timeout: 180_000 });
  const download = await downloadPromise;
  mkdirSync(TRANSFER_ARTIFACTS, { recursive: true });
  const archive = path.join(TRANSFER_ARTIFACTS, 'library-mobile-webkit.nostos');
  await download.saveAs(archive);

  const destinationName = `lt-${testInfo.project.name}-mobile-dest`;
  const destination = launchTransferInstance(destinationName, 'none');
  try {
    await expect.poll(async () => (await librarySnapshot(destination.baseUrl)).books.length).toBe(0);

    await page.goto(`${destination.baseUrl}/settings`);
    await expect(page.getByTestId('library-transfer-card')).toBeVisible();
    // No horizontal overflow at 390px and the primary action is reachable.
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);

    // A little per-chunk latency keeps the uploading state observable on a
    // fast localhost link; the upload is still the real chunked transport.
    await page.route(
      '**/api/portability/migration/jobs/*/upload-session/chunks/*',
      async (route) => {
        await new Promise((resolve) => setTimeout(resolve, 200));
        await route.continue();
      },
    );
    await page.getByTestId('library-import-file-input').setInputFiles(archive);
    await expect(page.getByTestId('import-inspecting')).toBeVisible({ timeout: 60_000 });
    await expect
      .poll(
        async () =>
          (await page.getByTestId('import-uploading').isVisible()) ||
          (await page.getByTestId('import-checking').isVisible()),
        { timeout: 180_000, message: 'the upload started or went straight to checking' },
      )
      .toBe(true);

    const beforeReload = await page.evaluate(() => performance.timeOrigin);
    await expect(page.getByTestId('library-activation-overlay')).toBeVisible({
      timeout: 240_000,
    });
    await page.waitForFunction(
      (origin) => performance.timeOrigin !== origin,
      beforeReload,
      { timeout: 240_000, polling: 250 },
    );

    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    const snapshot = await librarySnapshot(destination.baseUrl);
    expect(snapshotCounts(snapshot)).toEqual({
      books: 4,
      notes: 3,
      highlights: 1,
      collections: 2,
      collectionMemberships: 2,
    });
    expect(bookByTitle(snapshot, 'The Lantern Keepers').progressPercent).toBe(42);

    await page.goto(`${destination.baseUrl}/read/${bookByTitle(snapshot, 'The Lantern Keepers').id}`);
    await expect(
      page.frameLocator('#epub-viewer iframe').locator('p').first(),
    ).toContainText('lantern never goes dark', { timeout: 45_000 });
  } finally {
    killTransferInstance(destination.name);
  }

  expect(errors).toEqual([]);
});
