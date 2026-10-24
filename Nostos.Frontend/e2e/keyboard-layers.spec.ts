/**
 * Keyboard contract for stacked layers (#1234, #1235): Escape closes only the
 * top layer, and focus returns to the control that opened a dialog.
 *
 * Real engine, production build: the unit specs cover the stack logic, this
 * proves it with the real CDK overlay, dropdown, menu and reader drawers.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));

async function seedEpubWithNote(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'layers.epub');
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);

  await apiPost(baseUrl, `/api/books/${bookId}/notes`, { content: 'A thought to delete with the keyboard.' });
  return bookId as string;
}

async function openNotesDrawer(page: Page): Promise<void> {
  const toggle = page.getByRole('button', { name: 'Notes & highlights' });
  await expect(async () => {
    if (!(await toggle.isVisible().catch(() => false))) {
      const viewport = page.viewportSize()!;
      await page.mouse.click(viewport.width / 2, viewport.height / 2);
    }
    await expect(toggle).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await toggle.click();
  await expect(page.locator('.notes-panel.open')).toBeVisible();
}

test.describe('keyboard layers', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('Escape on a dropdown inside Add book closes only the dropdown', async ({ page }) => {
    const { baseUrl } = loadFixture();
    await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });

    await page.getByRole('button', { name: 'Add book' }).click();
    const dialog = page.getByRole('dialog').first();
    await expect(dialog).toBeVisible();
    await page.getByRole('button', { name: 'Add manually' }).click();

    const format = dialog.getByRole('combobox', { name: 'Format' });
    await format.focus();
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('listbox')).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(page.getByRole('listbox')).toHaveCount(0);
    await expect(dialog).toBeVisible();

    // The next Escape belongs to the dialog.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add book' })).toBeFocused();
  });

  test('Escape on the reader delete confirm leaves the notes drawer open, and focus returns to the ⋯ trigger', async ({
    page,
  }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpubWithNote(baseUrl, `Layers ${run}`);

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
    await openNotesDrawer(page);

    const more = page.locator('.notes-panel.open').getByRole('button', { name: 'More actions for this note' });
    await more.focus();
    await page.keyboard.press('Enter');
    await page.getByRole('menuitem', { name: 'Delete note' }).click();

    const confirm = page.getByRole('alertdialog');
    await expect(confirm).toBeVisible();
    // Cancel is the safe default focus on a destructive question.
    await expect(confirm.getByRole('button', { name: 'Cancel' })).toBeFocused();

    await page.keyboard.press('Escape');
    await expect(confirm).toHaveCount(0);
    await expect(page.locator('.notes-panel.open')).toBeVisible();
    await expect(more).toBeFocused();

    // A second Escape now closes the drawer.
    await page.keyboard.press('Escape');
    await expect(page.locator('.notes-panel.open')).toHaveCount(0);
  });
});
