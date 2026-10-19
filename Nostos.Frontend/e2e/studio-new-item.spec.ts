/**
 * New document / New folder in the Writing Studio, as the writing room has
 * them (#1059): no browser prompt(). New opens a blank page with the caret in
 * it; the page becomes a document when the first words arrive, named from its
 * first line, and a page left empty creates nothing. A new folder is named
 * inline in the Files rail.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { apiDelete } from './support/brain-fixture';
import { openFilesRail, studioPage } from './support/studio-editor';

test.use({ serviceWorkers: 'block' });

const created: string[] = [];

test.afterEach(async () => {
  const { baseUrl } = loadFixture();
  for (const id of created.splice(0)) await apiDelete(baseUrl, `/api/writings/${id}`);
});

interface Writing {
  id: string;
  name: string;
}

const writings = () => apiGet<Writing[]>(loadFixture().baseUrl, '/api/writings');

async function idOf(name: string): Promise<string> {
  let found: Writing | undefined;
  await expect.poll(async () => (found = (await writings()).find((w) => w.name === name))).toBeTruthy();
  return found!.id;
}

function failOnDialog(page: Page): void {
  page.on('dialog', (dialog) => {
    void dialog.dismiss();
    throw new Error(`New must not open a browser dialog (${dialog.type()})`);
  });
}

test('New opens a blank page, and its first words make a document named from its first line', async ({ page }) => {
  failOnDialog(page);
  const name = `Chapter ${newRunId()}`;
  await page.goto(`${loadFixture().baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await openFilesRail(page);
  await page.locator('.files-new-document').click();
  await expect(page.locator('.header-doc-title')).toHaveText('New page');
  await expect(studioPage(page)).toBeFocused();

  await page.keyboard.type(name);
  await page.keyboard.press('Enter');
  await page.keyboard.type('The first paragraph.');

  await expect(page.locator('.header-doc-title')).toHaveText(name);
  created.push(await idOf(name));
});

test('a new page left empty creates nothing', async ({ page }) => {
  failOnDialog(page);
  const before = (await writings()).length;
  await page.goto(`${loadFixture().baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await openFilesRail(page);
  await page.locator('.files-new-document').click();
  await expect(page.locator('.header-doc-title')).toHaveText('New page');
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });

  expect((await writings()).length).toBe(before);
});

test('New folder is named inline in the Files rail', async ({ page }) => {
  failOnDialog(page);
  const name = `Part ${newRunId()}`;
  await page.goto(`${loadFixture().baseUrl}/studio`, { waitUntil: 'domcontentloaded' });

  await openFilesRail(page);
  await page.getByRole('button', { name: 'New folder' }).click();
  const rename = page.locator('.inline-rename-input');
  await expect(rename).toBeFocused();
  await expect(rename).toHaveValue('Untitled folder');
  await page.keyboard.type(name);
  await page.keyboard.press('Enter');

  await expect(page.locator('.file-list .tree-row', { hasText: name })).toBeVisible();
  created.push(await idOf(name));
});
