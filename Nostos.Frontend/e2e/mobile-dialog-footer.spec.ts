/**
 * The Edit book footer on a phone (#1258).
 *
 * The shared footer forced both action groups to full width and reordered them
 * at <=480px, so Delete book was pushed onto a second row at the far left while
 * Cancel/Save changes sat on the first, pushed right. The groups now keep their
 * natural widths: all three actions share one row, the destructive action on the
 * content's left edge and the pair on its right.
 *
 * The file name starts with `mobile` so `playwright.config.ts` runs it in
 * `mobile-chromium` (390x844, touch). jsdom cannot evaluate the media query this
 * depends on, so this is the only place the arrangement can be asserted.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';

test.use({ serviceWorkers: 'block' });

const run = newRunId();

async function seedBook(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);
  return bookId as string;
}

/** Book detail -> Edit menu -> Book details, the phone's route to the modal. */
async function openEditBook(page: Page, baseUrl: string, bookId: string): Promise<void> {
  await page.goto(`${baseUrl}/library/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('[aria-label="Edit"]').click();
  await page.getByText('Book details', { exact: true }).click();
  await expect(page.locator('app-dialog-actions')).toBeVisible();
  // The sheet animates in; measure the settled layout.
  await page.waitForTimeout(400);
}

test.describe('Edit book footer on a phone', () => {
  test('keeps Delete, Cancel and Save on one content-aligned row', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(baseUrl, `Footer layout ${run}`);

    await openEditBook(page, baseUrl, bookId);

    const layout = await page.evaluate(() => {
      const footer = document.querySelector('app-dialog-actions') as HTMLElement;
      const footerRect = footer.getBoundingClientRect();
      const padding = parseFloat(getComputedStyle(footer).paddingLeft);
      const buttons = [...footer.querySelectorAll('button')].map((el) => {
        const rect = el.getBoundingClientRect();
        return {
          text: (el.textContent ?? '').trim(),
          x: rect.x,
          y: rect.y,
          right: rect.right,
          width: rect.width,
        };
      });
      return {
        footer: { x: footerRect.x, right: footerRect.right, height: footerRect.height },
        padding,
        buttons,
        viewportWidth: window.innerWidth,
        scrollWidth: document.documentElement.scrollWidth,
      };
    });

    const byText = (text: string) => {
      const found = layout.buttons.find((b) => b.text === text);
      if (!found) throw new Error(`No "${text}" action; saw ${JSON.stringify(layout.buttons)}`);
      return found;
    };
    const del = byText('Delete book');
    const cancel = byText('Cancel');
    const save = byText('Save changes');

    // One row: all three actions share a baseline.
    expect(Math.abs(del.y - cancel.y)).toBeLessThanOrEqual(1);
    expect(Math.abs(save.y - cancel.y)).toBeLessThanOrEqual(1);

    // The footer is a single row tall (32px action + 2x16px padding), not two.
    expect(layout.footer.height).toBeLessThan(75);

    // Aligned to the modal's content edges, not the screen's.
    expect(del.x).toBeCloseTo(layout.footer.x + layout.padding, 0);
    expect(save.right).toBeCloseTo(layout.viewportWidth - layout.padding, 0);

    // Destructive action separated from the pair, not touching it.
    expect(cancel.x - del.right).toBeGreaterThan(24);

    // Nothing spills sideways out of the sheet.
    expect(layout.scrollWidth).toBe(layout.viewportWidth);
  });
});
