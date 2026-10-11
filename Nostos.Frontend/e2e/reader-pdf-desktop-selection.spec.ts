/**
 * Real-engine proof that the PDF reader opens the selection menu at the text
 * when a desktop mouse selection is released, the EPUB reader's
 * #650/#657 behaviour. Production Angular build, real backend, real PDF.js.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, loadFixture, newRunId } from './support/fixture';
import { seedBook, textPdf } from './support/pdf-fixture';

async function dragAcrossText(page: Page): Promise<{ x: number; y: number; width: number; height: number }> {
  const span = page.locator('.textLayer span', { hasText: 'Desktop selection passage' }).first();
  await span.waitFor({ timeout: 60_000 });
  const box = await span.boundingBox();
  if (!box) throw new Error('PDF text span has no box');
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + 2, y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, y, { steps: 8 });
  await page.mouse.move(box.x + box.width - 2, y, { steps: 8 });
  await page.mouse.up();
  return box;
}

test.describe('PDF desktop selection', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('mouse release opens the menu at the text, a click away closes it, Highlight saves', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `PDF selection ${run}`,
      textPdf(['Desktop selection passage for the reader']),
      'selection.pdf',
      'application/pdf',
    );

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });

    const menu = page.getByTestId('selection-menu');
    const text = await dragAcrossText(page);
    await expect(menu).toBeVisible();
    await expect(page.getByTestId('selection-bar')).toHaveCount(0);
    await expect(menu.locator('.selected-text')).toContainText('Desktop selection');
    await page.screenshot({ path: test.info().outputPath('pdf-selection-menu.png') });

    // Anchored just below the selected text, overlapping it horizontally.
    const menuBox = await menu.boundingBox();
    if (!menuBox) throw new Error('selection menu has no box');
    expect(menuBox.y).toBeGreaterThanOrEqual(text.y + text.height);
    expect(menuBox.y - (text.y + text.height)).toBeLessThan(24);
    expect(menuBox.x).toBeLessThan(text.x + text.width);
    expect(menuBox.x + menuBox.width).toBeGreaterThan(text.x);

    // A click away from the menu abandons the offer.
    const pageBox = await page.locator('.page').first().boundingBox();
    if (!pageBox) throw new Error('PDF page has no box');
    await page.mouse.click(pageBox.x + pageBox.width / 2, pageBox.y + pageBox.height * 0.8);
    await expect(menu).toHaveCount(0);

    await dragAcrossText(page);
    await expect(menu).toBeVisible();
    await page.getByTestId('selection-highlight').click();
    await expect(menu).toHaveCount(0);
    await expect
      .poll(async () => {
        const notes = await apiGet<any[]>(baseUrl, `/api/books/${bookId}/notes`);
        return notes.map((n) => n.selectedText);
      })
      .toEqual([expect.stringContaining('Desktop selection')]);
  });
});
