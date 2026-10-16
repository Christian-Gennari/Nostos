/**
 * The command palette on a phone: no Ctrl+K, so Search everything in the More
 * sheet opens it (every deployment has the sheet), and it is a full-height
 * sheet with a Cancel button and no keyboard hints.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block', hasTouch: true });

async function openFromMore(page: Page): Promise<void> {
  await page.getByTestId('dock-more').tap();
  await page.getByTestId('utility-sheet-search').tap();
}

test('the More sheet opens a full-height palette sheet that Cancel closes', async ({ page }) => {
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  const more = page.getByTestId('dock-more');
  await expect(more).toBeVisible();
  const box = (await more.boundingBox())!;
  expect(box.width).toBeGreaterThanOrEqual(44);
  expect(box.height).toBeGreaterThanOrEqual(44);

  await openFromMore(page);
  const sheet = page.getByTestId('command-palette');
  await expect(sheet).toBeVisible();
  await expect(page.getByTestId('palette-input')).toBeFocused();
  // The More sheet stands down rather than sitting under the palette.
  await expect(page.getByRole('dialog', { name: 'More' })).toHaveCount(0);

  const viewport = page.viewportSize()!;
  const sheetBox = (await sheet.boundingBox())!;
  expect(sheetBox.width).toBeGreaterThanOrEqual(viewport.width - 1);
  expect(sheetBox.height).toBeGreaterThanOrEqual(viewport.height - 1);
  await expect(page.locator('.palette-hint')).toBeHidden();
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false);

  // Every row is a comfortable tap target.
  const rows = await page.locator('.palette-item').evaluateAll((els) => els.map((el) => el.getBoundingClientRect().height));
  expect(rows.length).toBeGreaterThan(0);
  for (const height of rows) expect(height).toBeGreaterThanOrEqual(44);

  await page.getByTestId('palette-cancel').tap();
  await expect(sheet).toHaveCount(0);
});

test('SelfHosted, with no feedback link, has the same More sheet and one way into search', async ({ page }) => {
  // The shared E2E backend runs SelfHosted with no feedback URL.
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.getByTestId('dock-more').tap();
  await expect(page.getByTestId('utility-sheet-feedback')).toHaveCount(0);
  await expect(page.getByTestId('utility-sheet-settings')).toBeVisible();
  await page.getByTestId('utility-sheet-search').tap();
  await expect(page.getByTestId('palette-input')).toBeFocused();
});

test('the More slot stays off the desktop', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const desktop = await context.newPage();
  await desktop.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(desktop.locator('app-app-dock')).toBeVisible();
  await expect(desktop.getByTestId('dock-more')).toHaveCount(0);
  await context.close();
});
