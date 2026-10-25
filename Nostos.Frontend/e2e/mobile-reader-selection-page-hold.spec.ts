/**
 * A phone's selection handles must not move the paginated page (#1254).
 *
 * OS selection handles cannot be driven in emulation. What can be driven is
 * the path a handle drag uses: a live touch selection, and the browser's own
 * reveal of its endpoint (`scrollIntoView({ inline: 'nearest' })`), which
 * scrolls the same epub.js stage the drag does. Measured with epub.js 0.3.93 on
 * this fixture at 390px: the reveal moved the stage from page 2's 390 to an
 * off-grid offset, and the next turn added a page width to that offset instead
 * of landing on page 3. Holding the offset keeps the page where the text is.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture } from './support/fixture';

// Multi-page fixture: 12 pages at 390px with default typography.
const EPUB = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));

/** epub.js's page position: the stage box it creates inside #epub-page. */
const stage = (page: Page) =>
  page.locator('#epub-page > .epub-container').evaluate((el) => ({
    left: el.scrollLeft,
    width: el.clientWidth,
  }));

const iframeSelection = (page: Page) =>
  page
    .frameLocator('#epub-viewer iframe')
    .locator('body')
    .evaluate((body) => body.ownerDocument.getSelection()?.toString() ?? '');

async function seedEpub(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'page-hold.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

test.use({ serviceWorkers: 'block' });

test('phone: a handle dragged towards the next page holds the page and keeps the next turn whole', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl, `Page hold ${Date.now()}`);
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  const frame = page.frameLocator('#epub-viewer iframe');
  await frame.locator('p').first().waitFor({ timeout: 45_000 });
  await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);
  await page.waitForTimeout(800);

  // Reveal the pager, then turn to page 2 so the starting offset is one whole
  // page in — the page the handles are dragged from.
  const chrome = page.getByTestId('reader-chrome-top');
  if ((await chrome.getAttribute('aria-hidden')) === 'true') {
    const view = (await page.locator('#epub-viewer').boundingBox())!;
    await page.touchscreen.tap(view.x + view.width / 2, view.y + view.height / 2);
    await expect(chrome).not.toHaveAttribute('aria-hidden', 'true');
  }
  const first = await stage(page);
  expect(first.left).toBe(0);
  const next = page.getByRole('button', { name: 'Next page' });
  await next.click();
  await expect.poll(async () => (await stage(page)).left).toBe(first.width);
  const held = await stage(page);
  expect(held.left).toBe(held.width);

  // Paragraphs on the visible page, and one that begins on the page after it.
  const pageIndex = held.left / held.width;
  const picked = await frame.locator('body').evaluate((body, { index, width }) => {
    const paragraphs = [...body.ownerDocument.querySelectorAll('p')];
    const beginsOn = (p: Element, page: number) => {
      const rect = p.getBoundingClientRect();
      return rect.left >= page * width - 1 && rect.left < (page + 1) * width - 1;
    };
    const overlaps = (p: Element, page: number) => {
      const rect = p.getBoundingClientRect();
      return rect.right > page * width && rect.left < (page + 1) * width;
    };
    return {
      start: paragraphs.findIndex((p) => overlaps(p, index)),
      end: paragraphs.findIndex((p) => beginsOn(p, index + 1)),
    };
  }, { index: pageIndex, width: held.width });
  expect(picked.start, 'a paragraph is on the visible page').toBeGreaterThanOrEqual(0);
  expect(picked.end, 'a paragraph begins on the next page').toBeGreaterThanOrEqual(0);

  const bar = page.locator('[data-testid="selection-bar"]');
  const offered = await frame.locator('p').nth(picked.start).evaluate((p, { index, width }) => {
    const doc = p.ownerDocument;
    const text = p.firstChild!;
    // The first glyph of this paragraph that actually sits on the visible
    // page, so the selection starts where the reader is looking.
    const probe = doc.createRange();
    let start = 0;
    for (let offset = 1; offset <= text.textContent!.length; offset += 4) {
      probe.setStart(text, offset - 1);
      probe.setEnd(text, offset);
      const rect = probe.getBoundingClientRect();
      if (rect.width > 0 && rect.left >= index * width && rect.left < (index + 1) * width) {
        start = offset;
        break;
      }
    }
    const range = doc.createRange();
    range.setStart(text, start);
    range.setEnd(text, Math.min(start + 20, text.textContent!.length));
    const selection = doc.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    return { text: range.toString(), start };
  }, { index: pageIndex, width: held.width });
  await expect(bar).toBeVisible();
  await expect(bar.locator('.selected-text')).toContainText(offered.text.trim());

  // Extend the selection from the visible page to the one after it, as a
  // handle dragged past the page edge does. The debounced touch capture starts
  // the hold; the reveal below is the browser asking the stage to move.
  const extended = await frame.locator('body').evaluate((body, { startIndex, endIndex, from }) => {
    const doc = body.ownerDocument;
    const paragraphs = [...doc.querySelectorAll('p')];
    const fromText = paragraphs[startIndex].firstChild!;
    const toText = paragraphs[endIndex].firstChild!;
    const range = doc.createRange();
    range.setStart(fromText, Math.min(from + 40, fromText.textContent!.length - 1));
    range.setEnd(toText, Math.min(40, toText.textContent!.length));
    const selection = doc.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    return {
      selected: selection.toString(),
      start: fromText.textContent!.slice(from + 40, from + 64),
    };
  }, { startIndex: picked.start, endIndex: picked.end, from: offered.start });
  await expect(bar.locator('.selected-text')).toContainText(
    extended.start.replace(/\s+/g, ' ').trim(),
  );

  const beforeReveal = await stage(page);
  await frame.locator('p').nth(picked.end).evaluate((p) => {
    p.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  });
  await page.waitForTimeout(400);
  // The page did not move, so the handle cannot have taken the selection onto
  // the next page, and there is no off-grid offset to turn from.
  expect(await stage(page)).toEqual(beforeReveal);
  await expect(bar).toBeVisible();
  expect(await iframeSelection(page)).toBe(extended.selected);


  // Resolving the selection hands the page back: one turn is exactly one page.
  await page.locator('[data-testid="selection-cancel"]').tap();
  await expect(bar).toHaveCount(0);
  await expect(next).toBeVisible();
  await next.click();
  await expect.poll(async () => (await stage(page)).left).toBe(held.left + held.width);
  expect((await stage(page)).left % held.width).toBe(0);
});
