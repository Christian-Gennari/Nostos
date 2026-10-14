/**
 * A saved highlight stays on its words when the text re-wraps under it.
 *
 * epub.js's overlay measured a highlight once and redrew it only when the
 * frame changed size, so a reflow at the same size — a web font finishing its
 * load, a typography change — left the highlight on the wrong words until a
 * reload. Highlights are now painted by the text itself.
 */
import { expect, Page, test } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { apiPost, loadFixture } from './support/fixture';

const EPUB = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
test.use({ serviceWorkers: 'block' });

/** A fresh copy per test, so one run's reading position and highlights do not carry into the next. */
async function seedBook(): Promise<string> {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook', title: 'Dracula', author: 'Bram Stoker',
  });
  const bookId = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'margins.epub');
  expect((await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form })).ok).toBe(true);
  return bookId;
}

/**
 * The words the page paints in a highlighter pen. A registered range is
 * painted on its own line boxes wherever they are, so no stored geometry
 * exists that could disagree with the text.
 */
async function paintedHighlights(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const frame = document.querySelector<HTMLIFrameElement>('#epub-viewer iframe');
    const win = frame?.contentWindow as (Window & { CSS: { highlights?: Map<string, Set<Range>> } }) | null;
    const painted: string[] = [];
    for (const [name, highlight] of win?.CSS.highlights?.entries() ?? []) {
      if (!name.startsWith('nostos-mark-pen-')) continue;
      for (const range of highlight) painted.push(range.toString().trim());
    }
    return painted;
  });
}

for (const theme of ['light', 'dark'] as const) {
  test(`390px ${theme}: a highlight follows its words through a same-size reflow`, async ({ browser }) => {
    const context = await browser.newContext({
      viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, serviceWorkers: 'block',
    });
    const page = await context.newPage();
    const bookId = await seedBook();
    try {
      await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
      await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
      const frame = page.frameLocator('#epub-viewer iframe');
      await frame.locator('p').first().waitFor({ timeout: 45_000 });
      await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);

      // A sentence from the middle of the opening paragraph that wraps over
      // several lines, like the one in the report, and stays on this page
      // after the reflow.
      const words = await frame.locator('p').first().evaluate((paragraph) => {
        const doc = paragraph.ownerDocument;
        const text = paragraph.firstChild as Text;
        const content = text.data;
        const start = content.indexOf('Buda-Pesth');
        const end = content.indexOf('.', start) + 1;
        const range = doc.createRange();
        range.setStart(text, start);
        range.setEnd(text, end);
        const selection = doc.getSelection()!;
        selection.removeAllRanges();
        selection.addRange(range);
        doc.dispatchEvent(new Event('touchend'));
        return range.toString().trim();
      });
      await page.getByTestId('selection-highlight').click();
      await expect(page.getByTestId('selection-bar')).toBeHidden();
      await expect.poll(() => paintedHighlights(page)).toEqual([words]);

      const frameBox = await page.locator('#epub-viewer iframe').boundingBox();
      // Re-wrap every line without touching the frame: the shape of a font
      // swapping in after the page was laid out.
      await frame.locator('head').evaluate((head) => {
        const style = head.ownerDocument.createElement('style');
        style.textContent = 'p { letter-spacing: 0.05em !important; word-spacing: 0.1em !important; }';
        head.appendChild(style);
      });
      expect(await page.locator('#epub-viewer iframe').boundingBox()).toEqual(frameBox);

      expect(await paintedHighlights(page)).toEqual([words]);

      if (process.env.HIGHLIGHT_REFLOW_EVIDENCE) {
        const dir = path.join(__dirname, 'visual-evidence', 'reader-highlight-reflow');
        mkdirSync(dir, { recursive: true });
        await page.screenshot({ path: path.join(dir, `${process.env.HIGHLIGHT_REFLOW_EVIDENCE}-390-${theme}.png`) });
      }
    } finally {
      await context.close();
    }
  });
}
