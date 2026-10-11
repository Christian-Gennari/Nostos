/**
 * PDF selection, highlighting and noting at 390px with touch and no highlight
 * mode. The file name starts with `mobile` so `playwright.config.ts` runs it
 * in mobile-chromium. Headless Chromium has no long-press selection, so the
 * selection is set on the real text layer the way a phone's handles would
 * leave it; everything after that (selectionchange, the settle wait, the bar,
 * the taps, the save) is the production path.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { seedBook, textPdf } from './support/pdf-fixture';

const TEXT = 'Phone selection passage for the reader';

async function openPdf(page: Page, title: string): Promise<{ baseUrl: string; bookId: string }> {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, title, textPdf([TEXT]), 'phone.pdf', 'application/pdf');
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('.textLayer span', { hasText: 'Phone selection' }).first().waitFor({ timeout: 60_000 });
  return { baseUrl, bookId };
}

/** Selects the first `chars` characters of the passage on the PDF text layer. */
async function selectChars(page: Page, chars: number): Promise<string> {
  return page.locator('.textLayer span', { hasText: 'Phone selection' }).first().evaluate((el, n) => {
    const text = el.firstChild!;
    const range = document.createRange();
    range.setStart(text, 0);
    range.setEnd(text, Math.min(n, text.textContent!.length));
    const selection = getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    return selection.toString();
  }, chars);
}

const notes = (baseUrl: string, bookId: string) => apiGet<any[]>(baseUrl, `/api/books/${bookId}/notes`);

test.describe('phone PDF selection', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('a settled selection docks the bar, follows the handles, and a pen dot highlights', async ({ page }) => {
    const { baseUrl, bookId } = await openPdf(page, `PDF phone A ${run}`);
    const bar = page.getByTestId('selection-bar');

    const first = await selectChars(page, 5);
    await expect(bar).toBeVisible();
    await expect(bar.locator('.selected-text')).toContainText(first);
    await expect(page.getByTestId('selection-menu')).toHaveCount(0);
    await expect(page.getByTestId('selection-scrim')).toHaveCount(0);
    // The native selection is left to the phone's handles and OS menu (#16).
    expect(await page.evaluate(() => getSelection()!.toString())).toBe(first);

    const selected = await selectChars(page, 23);
    await expect(bar.locator('.selected-text')).toContainText(selected);
    const viewport = page.viewportSize()!;
    for (const id of ['selection-copy', 'selection-cancel', 'selection-add-note', 'selection-highlight', 'selection-pen-rose']) {
      const box = (await page.getByTestId(id).boundingBox())!;
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width, `${id} inside the viewport`).toBeLessThanOrEqual(viewport.width);
      expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
    }
    await page.screenshot({ path: test.info().outputPath('pdf-phone-bar.png') });

    await page.getByTestId('selection-pen-blue').tap();
    await expect(bar).toHaveCount(0);
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
    const saved = await notes(baseUrl, bookId);
    expect(saved[0].selectedText).toBe(selected);
    expect(saved[0].highlightColour).toBe('blue');
    await expect(page.locator('.custom-highlight-layer .highlight-group')).toHaveCount(1);
    // Saved: not offered a second time.
    await page.waitForTimeout(600);
    await expect(bar).toHaveCount(0);
    await page.screenshot({ path: test.info().outputPath('pdf-phone-highlighted.png') });
  });

  test('a note keeps its passage marked while typing; a tap on the page drops an untouched offer', async ({ page }) => {
    const { baseUrl, bookId } = await openPdf(page, `PDF phone B ${run}`);
    const bar = page.getByTestId('selection-bar');

    await selectChars(page, 15);
    await expect(bar).toBeVisible();
    // A real tap on the page with nothing selected. (Headless Chromium does not
    // collapse a selection on tap the way a phone does, so collapse it first.)
    await page.evaluate(() => getSelection()!.removeAllRanges());
    const pageBox = (await page.locator('.page').first().boundingBox())!;
    await page.touchscreen.tap(pageBox.x + pageBox.width / 2, pageBox.y + pageBox.height * 0.75);
    await expect(bar).toHaveCount(0);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);

    const selected = await selectChars(page, 23);
    await expect(bar).toBeVisible();
    await page.getByTestId('selection-add-note').tap();
    const input = page.getByTestId('selection-note-input');
    await input.fill('A note from the phone.');
    // Typing moved the selection into the note field; the passage stays drawn.
    await expect(page.locator('.custom-highlight-layer .highlight-group')).toHaveCount(1);
    await page.screenshot({ path: test.info().outputPath('pdf-phone-note.png') });
    // A stray tap on the book must not cost the reader their note.
    await page.touchscreen.tap(pageBox.x + pageBox.width / 2, pageBox.y + pageBox.height * 0.75);
    await expect(input).toHaveValue('A note from the phone.');

    await page.getByTestId('selection-save-note').tap();
    await expect(bar).toHaveCount(0);
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
    const saved = await notes(baseUrl, bookId);
    expect(saved[0].content).toBe('A note from the phone.');
    expect(saved[0].selectedText).toBe(selected);
  });
});
