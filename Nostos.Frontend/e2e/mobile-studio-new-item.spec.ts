/**
 * New document on a phone: the file list is a drawer, so the rename field
 * must be on screen to be typed into, and the document opens after.
 */
import { expect, test } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { apiDelete } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

test('New document from the empty Studio is named in the drawer, then opens', async ({ page }) => {
  page.on('dialog', () => {
    throw new Error('New must not open a browser dialog');
  });
  const { baseUrl } = loadFixture();
  const name = `Draft ${newRunId()}`;
  await page.goto(`${baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await page.locator('.empty-new-document').click();
  const rename = page.locator('.inline-rename-input');
  await expect(rename).toBeVisible();
  await expect(rename).toBeFocused();
  await page.keyboard.type(name);
  await page.keyboard.press('Enter');

  await expect(page.locator('.header-doc-title')).toHaveText(name);

  const all = await apiGet<{ id: string; name: string }[]>(baseUrl, '/api/writings');
  for (const w of all.filter((x) => x.name === name)) await apiDelete(baseUrl, `/api/writings/${w.id}`);
});
