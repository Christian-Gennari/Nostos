/**
 * Keyboard reach (#1376): a Library card opens with Enter, the reader chrome is
 * reachable by Tab, and "Edit note" keeps focus inside the note and returns it
 * to the note's ⋯ button. Real engine, production build; screenshots of the
 * focus states go to e2e/visual-evidence/1376-keyboard-a11y/.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));
const SHOTS = path.join(__dirname, 'visual-evidence', '1376-keyboard-a11y');

async function seedEpubWithNote(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'a11y.epub');
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);
  await apiPost(baseUrl, `/api/books/${bookId}/notes`, { content: 'A thought to edit with the keyboard.' });
  return bookId as string;
}

async function tabUntil(page: Page, isThere: () => Promise<boolean>, max = 40): Promise<void> {
  for (let i = 0; i < max; i++) {
    if (await isThere()) return;
    await page.keyboard.press('Tab');
  }
  throw new Error('Tab never reached the target');
}

test.describe('keyboard a11y (#1376)', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('Enter on a focused Library card opens the book', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const title = `Cardkey ${run}`;
    const bookId = await seedEpubWithNote(baseUrl, title);
    await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });

    // The fixture is shared by the whole suite and holds more books than the
    // library's first page (20), sorted by last read, so the new card is not
    // guaranteed to be on it. Narrow the grid to the card instead.
    const search = page.getByRole('searchbox', { name: 'Search books' });
    const findCard = async () => {
      await search.fill(title);
      const card = page.getByRole('link', { name: title });
      await expect(card).toBeVisible({ timeout: 30_000 });
      return card;
    };

    const link = await findCard();
    await link.focus();
    await page.screenshot({ path: path.join(SHOTS, 'library-card-focus.png') });
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(new RegExp(`/library/${bookId}`));

    await page.goBack();
    const again = await findCard();
    await again.focus();
    await page.keyboard.press('Space');
    await expect(page).toHaveURL(new RegExp(`/library/${bookId}`));
  });

  test('reader chrome is reachable by Tab, and Edit note keeps focus', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpubWithNote(baseUrl, `Readerkey ${run}`);
    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });

    const reveal = page.getByTestId('reader-chrome-reveal');
    await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await tabUntil(page, () => reveal.evaluate((el) => el === document.activeElement).catch(() => false));
    await expect(reveal).toBeFocused();
    await page.screenshot({ path: path.join(SHOTS, 'reader-reveal-focus.png') });

    await page.keyboard.press('Enter');
    const contents = page.getByRole('button', { name: 'Contents' });
    await expect(contents).toBeFocused();
    await page.screenshot({ path: path.join(SHOTS, 'reader-toolbar-focus.png') });

    await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'Notes & highlights' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('.notes-panel.open')).toBeVisible();

    const more = page.locator('.notes-panel.open').getByRole('button', { name: 'More actions for this note' });
    await more.focus();
    await page.keyboard.press('Enter');
    await page.getByRole('menuitem', { name: 'Edit note' }).press('Enter');

    const words = page.locator('.notes-panel.open form textarea').last();
    await expect(words).toBeFocused();
    await page.screenshot({ path: path.join(SHOTS, 'note-edit-focus.png') });

    await page.keyboard.type(' More.');
    await tabUntil(page, () =>
      page.evaluate(() => document.activeElement?.textContent?.trim() === 'Save'),
    );
    await page.keyboard.press('Enter');
    await expect(more).toBeFocused();
    await page.screenshot({ path: path.join(SHOTS, 'note-after-save-focus.png') });
  });
});
