/**
 * New document / New folder in the Writing Studio: no browser prompt(). The
 * item is created under a placeholder name, its row goes straight into inline
 * rename, and a new document opens once its name is settled.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { apiDelete } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

const created: string[] = [];

test.afterEach(async () => {
  const { baseUrl } = loadFixture();
  for (const id of created.splice(0)) await apiDelete(baseUrl, `/api/writings/${id}`);
});

async function idOf(name: string): Promise<string> {
  const all = await apiGet<{ id: string; name: string }[]>(loadFixture().baseUrl, '/api/writings');
  const found = all.find((w) => w.name === name);
  if (!found) throw new Error(`no writing named ${name}`);
  return found.id;
}

function failOnDialog(page: Page): void {
  page.on('dialog', (dialog) => {
    void dialog.dismiss();
    throw new Error(`New must not open a browser dialog (${dialog.type()})`);
  });
}

test('New document is named inline, then opens under that name', async ({ page }) => {
  failOnDialog(page);
  const name = `Chapter ${newRunId()}`;
  await page.goto(`${loadFixture().baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await page.locator('.files-new-document').click();
  const rename = page.locator('.inline-rename-input');
  await expect(rename).toBeFocused();
  await expect(rename).toHaveValue('Untitled document');
  // The placeholder is selected, so typing replaces it.
  expect(await rename.evaluate((el: HTMLInputElement) => [el.selectionStart, el.selectionEnd])).toEqual([0, 17]);

  await page.keyboard.type(name);
  await page.keyboard.press('Enter');

  await expect(page.locator('.header-doc-title')).toHaveText(name);
  created.push(await idOf(name));
});

test('dismissing the rename still opens the new document', async ({ page }) => {
  failOnDialog(page);
  await page.goto(`${loadFixture().baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await page.locator('.files-new-document').click();
  await expect(page.locator('.inline-rename-input')).toBeFocused();
  await page.keyboard.press('Escape');

  await expect(page.locator('.header-doc-title')).toHaveText('Untitled document');
  created.push(await idOf('Untitled document'));
});
