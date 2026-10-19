/**
 * New document on a phone: New sits in the Files drawer and opens a blank
 * page with the drawer out of the way; the first words make the document.
 */
import { expect, test } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { apiDelete } from './support/brain-fixture';
import { openFilesRail, studioPage } from './support/studio-editor';

test.use({ serviceWorkers: 'block' });

test('New from the Files drawer opens a blank page that becomes the document', async ({ page }) => {
  page.on('dialog', () => {
    throw new Error('New must not open a browser dialog');
  });
  const { baseUrl } = loadFixture();
  const name = `Draft ${newRunId()}`;
  await page.goto(`${baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await openFilesRail(page);
  await page.locator('.files-new-document').click();
  // The drawer steps aside: the page is what New is for.
  await expect(page.locator('.files-new-document')).toBeHidden();
  await expect(page.locator('.header-doc-title')).toHaveText('New page');
  await expect(studioPage(page)).toBeFocused();

  await page.keyboard.type(name);
  await expect(page.locator('.header-doc-title')).toHaveText(name);

  await expect.poll(async () =>
    (await apiGet<{ id: string; name: string }[]>(baseUrl, '/api/writings')).some((w) => w.name === name)).toBe(true);
  const all = await apiGet<{ id: string; name: string }[]>(baseUrl, '/api/writings');
  for (const w of all.filter((x) => x.name === name)) await apiDelete(baseUrl, `/api/writings/${w.id}`);
});
