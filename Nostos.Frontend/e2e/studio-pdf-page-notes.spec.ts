import { expect, test, type Page } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiPost, apiPut, loadFixture } from './support/fixture';
import { waitForStudioEditor } from './support/studio-editor';

/**
 * PDF export with notes at the foot of the page (#1306), from the real export
 * dialog. The print dialog is the one thing replaced: `window.print` records
 * that it was called, and the frame stays open to be read.
 */

const FIREFOX = 'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0';

const WORDS =
  'the reader keeps a note beside the page it came from and returns to it when the argument needs a source'.split(' ');

function words(seed: number, count: number): string {
  return Array.from({ length: count }, (_, i) => WORDS[(seed + i) % WORDS.length]).join(' ');
}

/** Long enough for several pages, with notes called from a paragraph, a quote, a list and a table. */
function manuscript(): string {
  const parts = ['# Notes at the foot'];
  for (let n = 1; n <= 24; n += 1) {
    const sentence = `${words(n, 90)} CALL${n}x[^${n}] ${words(n + 3, 40)}.`;
    if (n % 7 === 3) parts.push(`> ${sentence}`);
    else if (n % 7 === 5) parts.push(`- ${sentence}`);
    else if (n % 7 === 6) parts.push(`| Source | Year |\n| --- | --- |\n| CALL${n}x[^${n}] | 19${n} |`);
    else parts.push(sentence);
  }
  for (let n = 1; n <= 24; n += 1) {
    parts.push(`[^${n}]: NOTE${n} ${words(n, n % 9 === 0 ? 90 : 18)}, *Italic Title*, p. ${n}.`);
  }
  return parts.join('\n\n');
}

async function seedWriting(baseUrl: string, name: string): Promise<string> {
  const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', { name, type: 'Document', parentId: null });
  await apiPut(baseUrl, `/api/writings/${writing.id}`, { name, content: manuscript() });
  return writing.id;
}

/** Keeps the frame open and records the print, in the page and in the frame. */
async function stubPrint(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.print = () => {
      (window.top as Window & { __printed?: number }).__printed = 1;
    };
  });
}

async function exportPdf(page: Page, baseUrl: string, writingId: string): Promise<void> {
  await page.goto(`${baseUrl}/studio?writingId=${writingId}`, { waitUntil: 'domcontentloaded' });
  await waitForStudioEditor(page);
  await page.getByTestId('studio-more').click();
  await page.getByTestId('studio-export').click();
  await page.getByRole('combobox', { name: 'Format' }).click();
  await page.getByRole('option', { name: 'PDF' }).click();
  await expect(page.getByTestId('export-format-hint')).toContainText('foot of each page');
  await page.getByRole('button', { name: 'Save as PDF…' }).click();
  await page.waitForFunction(() => (window as Window & { __printed?: number }).__printed === 1);
}

test.describe('PDF notes at the foot of the page', () => {
  // A service worker would answer the request the fallback test refuses.
  test.use({ serviceWorkers: 'block' });

  let writingId: string | null = null;

  test.afterEach(async () => {
    if (writingId) await apiDelete(loadFixture().baseUrl, `/api/writings/${writingId}`);
    writingId = null;
  });

  test('prints every note on the page that calls it', async ({ page }) => {
    const { baseUrl } = loadFixture();
    writingId = await seedWriting(baseUrl, `Page notes ${Date.now().toString(36)}`);
    await stubPrint(page);

    await exportPdf(page, baseUrl, writingId);

    const frame = page.frameLocator('.nostos-print-frame');
    await expect(frame.locator('.pagedjs_page').first()).toBeAttached();
    const pages = await frame.locator('body').evaluate((body) =>
      Array.from(body.querySelectorAll('.pagedjs_page')).map((sheet) => {
        const area = sheet.querySelector('.pagedjs_footnote_area');
        const inner = area?.textContent ?? '';
        const text = (sheet.querySelector('.pagedjs_page_content')?.textContent ?? '').replace(inner, '');
        return {
          calls: Array.from(text.matchAll(/CALL(\d+)x/g)).map((m) => Number(m[1])),
          notes: Array.from(inner.matchAll(/NOTE(\d+)/g)).map((m) => Number(m[1])),
        };
      }),
    );

    expect(pages.length).toBeGreaterThan(3);
    const calls = pages.flatMap((p) => p.calls);
    expect([...calls].sort((a, b) => a - b)).toEqual(Array.from({ length: 24 }, (_, i) => i + 1));
    for (const { calls: called, notes } of pages) {
      expect([...notes].sort((a, b) => a - b)).toEqual([...called].sort((a, b) => a - b));
    }
    await expect(frame.locator('.footnotes')).toHaveCount(0);
  });

  test('falls back to notes at the end of each chapter when the paginator cannot load', async ({ page }) => {
    const { baseUrl } = loadFixture();
    writingId = await seedWriting(baseUrl, `Page notes offline ${Date.now().toString(36)}`);
    await stubPrint(page);
    await page.route('**/assets/pagedjs/**', (route) => route.abort());

    await exportPdf(page, baseUrl, writingId);

    const frame = page.frameLocator('.nostos-print-frame');
    await expect(frame.locator('.footnotes li')).toHaveCount(24);
    await expect(frame.locator('.pagedjs_page')).toHaveCount(0);
    await expect(frame.locator('.page-note')).toHaveCount(0);
  });

  test.describe('in a browser outside Chromium', () => {
    test.use({ userAgent: FIREFOX });

    test('keeps notes at the end of each chapter and never fetches the paginator', async ({ page }) => {
      const { baseUrl } = loadFixture();
      writingId = await seedWriting(baseUrl, `Page notes Firefox ${Date.now().toString(36)}`);
      await stubPrint(page);
      const fetched: string[] = [];
      page.on('request', (request) => {
        if (request.url().includes('/assets/pagedjs/')) fetched.push(request.url());
      });

      await exportPdf(page, baseUrl, writingId);

      const frame = page.frameLocator('.nostos-print-frame');
      await expect(frame.locator('.footnotes li')).toHaveCount(24);
      expect(fetched).toEqual([]);
    });
  });
});
