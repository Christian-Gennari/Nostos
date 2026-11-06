/**
 * The Ask Nostos capsule shows with the reader chrome, in the right end of the
 * pager strip. It used to sit one layer under the strip: clickable (the strip
 * passes taps through) but painted over. Here it must be the top element at
 * its centre, wear the strip's surface, and keep clear of the page-turn
 * buttons, on a wide desktop, a narrow desktop and a phone.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', 'reader-ask-capsule');

test.use({ serviceWorkers: 'block' });

async function seedEpub(baseUrl: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Ask capsule ${newRunId()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

async function enableAskNostos(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem(
      'nostos.library.preferences',
      JSON.stringify({
        viewMode: 'grid',
        sort: 'lastread',
        pageSize: 20,
        sidebarExpanded: true,
        groupByWork: true,
        assistantEnabled: true,
        assistantVoiceEnabled: false,
      }),
    );
  });
  await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
}

const overlaps = (a: DOMRect, b: DOMRect) =>
  a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;

for (const viewport of [
  { name: 'wide desktop', width: 1600, height: 900 },
  { name: 'narrow desktop', width: 900, height: 800 },
  { name: 'phone', width: 390, height: 844 },
]) {
  test(`Ask Nostos is visible above the pager on a ${viewport.name}`, async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl);
    await enableAskNostos(page);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });

    // Show the chrome the keyboard's way: the reveal control, then Enter.
    await page.getByTestId('reader-chrome-reveal').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('reader-layout')).toHaveClass(/chrome-visible/);

    const trigger = page.getByTestId('assistant-trigger');
    await expect(trigger).toBeVisible();
    // Wait out the chrome's fade before reading geometry and colours.
    await page.waitForTimeout(400);

    const result = await trigger.evaluate((el) => {
      const box = el.getBoundingClientRect();
      const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
      const toolbar = document.querySelector('.reader-toolbar') as HTMLElement;
      const turns = [...toolbar.querySelectorAll('button')].map((b) => b.getBoundingClientRect().toJSON());
      return {
        onTop: !!hit && el.contains(hit),
        box: box.toJSON(),
        toolbarBox: toolbar.getBoundingClientRect().toJSON(),
        turns,
        capsuleBg: getComputedStyle(el.querySelector('.trigger-capsule')!).backgroundColor,
        toolbarBg: getComputedStyle(toolbar).backgroundColor,
      };
    });

    await page.screenshot({ path: path.join(EVIDENCE_DIR, `${viewport.width}.png`) });

    expect(result.onTop).toBe(true);
    // Inside the pager strip, and wearing its surface.
    expect(result.box.top).toBeGreaterThanOrEqual(result.toolbarBox.top);
    expect(result.box.bottom).toBeLessThanOrEqual(result.toolbarBox.bottom);
    expect(result.capsuleBg).toBe(result.toolbarBg);
    // Never over a page-turn or progress control.
    for (const turn of result.turns) expect(overlaps(result.box as DOMRect, turn as DOMRect)).toBe(false);
  });
}
