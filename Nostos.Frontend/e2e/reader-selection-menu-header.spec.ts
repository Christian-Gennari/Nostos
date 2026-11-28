/**
 * The desktop selection menu must never sit behind the reader's header or
 * toolbar (#1829). The menu renders inside the reading area's stacking context,
 * so the header (above it) and the toolbar (below it) paint over any part of
 * the menu placed outside that area. A long selection from just under the
 * header to the foot of the page used to open the menu above the text, up into
 * the header.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { openEpubReader, waitForLayoutSettled } from './support/waits';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', 'reader-selection-menu-header');

test.use({ serviceWorkers: 'block' });

async function seedEpub(baseUrl: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Menu header ${newRunId()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

/** Every sampled point on the menu's top and bottom edges hits the menu itself. */
async function menuUncovered(page: Page): Promise<{ covered: string[]; menuTop: number; menuBottom: number }> {
  return page.getByTestId('selection-menu').evaluate((menu) => {
    const box = menu.getBoundingClientRect();
    const covered: string[] = [];
    for (const y of [box.top + 4, box.bottom - 4]) {
      for (const x of [box.left + 16, (box.left + box.right) / 2, box.right - 16]) {
        const hit = document.elementFromPoint(x, y);
        if (!hit || !menu.contains(hit)) {
          covered.push(`(${Math.round(x)}, ${Math.round(y)}) -> ${hit?.className || hit?.tagName || 'nothing'}`);
        }
      }
    }
    return { covered, menuTop: box.top, menuBottom: box.bottom };
  });
}

test('a long selection from under the header keeps its menu clear of the header', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl);
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEpubReader(page, `${baseUrl}/read/${bookId}`);

  // Show the header, as a reader who has just used it would see it.
  const header = page.getByTestId('reader-chrome-top');
  const viewer = await page.locator('#epub-viewer').boundingBox();
  await page.mouse.click(viewer!.x + viewer!.width / 2, viewer!.y + 12);
  await expect(header).not.toHaveAttribute('aria-hidden', 'true');
  await waitForLayoutSettled(page, '#epub-viewer');

  // Select from the chapter heading, just under the header, to the last line
  // on the page: the case in the report.
  const frame = page.frameLocator('#epub-viewer iframe');
  await frame.locator('body').evaluate((body) => {
    const doc = body.ownerDocument;
    const win = doc.defaultView!;
    const visible = [...doc.querySelectorAll('h1, h2, p')].filter((p) => {
      const r = p.getBoundingClientRect();
      return r.width > 0 && r.left >= 0 && r.right <= win.innerWidth && r.bottom > 0 && r.top < win.innerHeight;
    });
    const first = visible[0];
    const last = visible[visible.length - 1];
    const range = doc.createRange();
    range.setStart(first.firstChild!, 0);
    range.setEnd(last.lastChild!, last.lastChild!.textContent!.length);
    doc.getSelection()!.removeAllRanges();
    doc.getSelection()!.addRange(range);
    last.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 }));
  });

  const menu = page.getByTestId('selection-menu');
  await expect(menu).toBeVisible();
  await waitForLayoutSettled(page, '[data-testid="selection-menu"]');
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'desktop-1280.png') });

  const headerBox = await header.boundingBox();
  const result = await menuUncovered(page);
  expect(result.menuTop, 'menu top edge under the header').toBeGreaterThanOrEqual(headerBox!.y + headerBox!.height);
  expect(result.menuBottom, 'menu bottom edge on screen').toBeLessThanOrEqual(800);
  expect(result.covered, 'points of the menu something else paints over').toEqual([]);
});
