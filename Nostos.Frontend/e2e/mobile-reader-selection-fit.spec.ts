/**
 * The phone's docked selection bar with every action on (pens, Copy, Ask
 * Nostos, More, Cancel, Add note, Highlight) must fit: each control keeps its
 * full size, none overlaps another, and all stay inside the bar's padding.
 * The file name starts with `mobile` so it runs in mobile-chromium (touch,
 * coarse pointer), where the bar docks instead of the anchored menu.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', 'reader-selection-menu-fit');

test.use({ serviceWorkers: 'block' });

async function seedEpub(baseUrl: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Bar fit ${newRunId()}`,
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

const ACTIONS = [
  'selection-copy',
  'selection-ask-nostos',
  'selection-more',
  'selection-cancel',
  'selection-add-note',
  'selection-highlight',
];

for (const width of [390, 360]) {
  test(`phone ${width}px: the docked selection bar fits every action`, async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl);
    await enableAskNostos(page);
    await page.setViewportSize({ width, height: 800 });
    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    const frame = page.frameLocator('#epub-viewer iframe');
    await frame.locator('p').first().waitFor({ timeout: 45_000 });
    await page.waitForTimeout(800);

    await frame.locator('p').first().evaluate((el) => {
      const doc = el.ownerDocument;
      const range = doc.createRange();
      range.setStart(el.firstChild!, 0);
      range.setEnd(el.firstChild!, Math.min(30, el.firstChild!.textContent!.length));
      doc.getSelection()!.removeAllRanges();
      doc.getSelection()!.addRange(range);
    });
    const bar = page.getByTestId('selection-bar');
    await expect(bar).toBeVisible();
    for (const id of ACTIONS) await expect(page.getByTestId(id)).toBeVisible();
    await page.waitForTimeout(400);

    const layout = await bar.evaluate((el, ids) => {
      const style = getComputedStyle(el);
      const box = el.getBoundingClientRect();
      const inner = {
        left: box.left + parseFloat(style.paddingLeft),
        right: box.right - parseFloat(style.paddingRight),
      };
      const pens = [...el.querySelectorAll('[data-testid^="selection-pen-"]')] as HTMLElement[];
      const controls = [
        ...ids.map((id) => [id, el.querySelector(`[data-testid="${id}"]`) as HTMLElement] as const),
        ...pens.map((pen) => [pen.dataset['testid']!, pen] as const),
      ].map(([id, control]) => {
        const r = control.getBoundingClientRect();
        return { id, left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width };
      });
      return { inner, controls, viewport: window.innerWidth, scrollWidth: document.documentElement.scrollWidth };
    }, ACTIONS);

    await bar.screenshot({ path: path.join(EVIDENCE_DIR, `phone-${width}.png`) });

    expect(layout.scrollWidth, 'no horizontal scroll').toBeLessThanOrEqual(layout.viewport);
    for (const c of layout.controls) {
      expect(c.left, `${c.id} left edge`).toBeGreaterThanOrEqual(layout.inner.left - 0.5);
      expect(c.right, `${c.id} right edge`).toBeLessThanOrEqual(layout.inner.right + 0.5);
    }
    for (const id of ACTIONS) {
      const c = layout.controls.find((x) => x.id === id)!;
      expect(c.width, `${id} width`).toBeGreaterThanOrEqual(32);
    }
    for (let i = 0; i < layout.controls.length; i++) {
      for (let j = i + 1; j < layout.controls.length; j++) {
        const a = layout.controls[i];
        const b = layout.controls[j];
        const overlap = a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
        expect(overlap, `${a.id} overlaps ${b.id}`).toBe(false);
      }
    }
  });
}
