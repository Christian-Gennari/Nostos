/**
 * The phone's docked selection bar with every action on (Copy, More, pen
 * picker, Cancel, Add note, Highlight) must fit in two rows (#1722): each
 * control keeps its 44px touch floor, none overlaps another, and all stay
 * inside the bar's padding with zero horizontal overflow at 390px and 360px.
 * The file name starts with `mobile` so it runs in mobile-chromium (touch,
 * coarse pointer), where the bar docks instead of the anchored menu.
 */
import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { openEpubReader, waitForLayoutSettled } from './support/waits';

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

const ACTIONS = [
  'selection-copy',
  'selection-more',
  'selection-pen-picker',
  'selection-cancel',
  'selection-add-note',
  'selection-highlight',
];

for (const width of [390, 360]) {
  test(`phone ${width}px: the docked selection bar fits every action in two rows`, async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl);
    await page.setViewportSize({ width, height: 800 });
    await openEpubReader(page, `${baseUrl}/read/${bookId}`);
    const frame = page.frameLocator('#epub-viewer iframe');

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
    await expect(page.getByTestId('selection-ask-nostos')).toHaveCount(0);
    for (const id of ACTIONS) await expect(page.getByTestId(id)).toBeVisible();
    await waitForLayoutSettled(page, '[data-testid="selection-bar"]');

    const layout = await bar.evaluate((el, ids) => {
      const style = getComputedStyle(el);
      const box = el.getBoundingClientRect();
      const inner = {
        left: box.left + parseFloat(style.paddingLeft),
        right: box.right - parseFloat(style.paddingRight),
      };
      const controls = ids.map((id) => {
        const control = el.querySelector(`[data-testid="${id}"]`) as HTMLElement;
        const r = control.getBoundingClientRect();
        return { id, left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width, height: r.height };
      });
      return { inner, controls, viewport: window.innerWidth, scrollWidth: document.documentElement.scrollWidth };
    }, ACTIONS);

    await bar.screenshot({ path: path.join(EVIDENCE_DIR, `phone-${width}.png`) });

    expect(layout.scrollWidth, 'no horizontal scroll').toBeLessThanOrEqual(layout.viewport);
    for (const c of layout.controls) {
      expect(c.left, `${c.id} left edge`).toBeGreaterThanOrEqual(layout.inner.left - 0.5);
      expect(c.right, `${c.id} right edge`).toBeLessThanOrEqual(layout.inner.right + 0.5);
      expect(c.width, `${c.id} touch width`).toBeGreaterThanOrEqual(44);
      expect(c.height, `${c.id} touch height`).toBeGreaterThanOrEqual(44);
    }

    // Row 1 (utilities) and Row 2 (pen picker + decisions) each share a single horizontal line.
    const row1Tops = ['selection-copy', 'selection-more'].map(
      (id) => layout.controls.find((c) => c.id === id)!.top,
    );
    const row2Tops = ['selection-pen-picker', 'selection-cancel', 'selection-add-note', 'selection-highlight'].map(
      (id) => layout.controls.find((c) => c.id === id)!.top,
    );
    expect(Math.max(...row1Tops) - Math.min(...row1Tops), 'row 1 stays on one line').toBeLessThan(4);
    expect(Math.max(...row2Tops) - Math.min(...row2Tops), 'row 2 stays on one line').toBeLessThan(4);

    for (let i = 0; i < layout.controls.length; i++) {
      for (let j = i + 1; j < layout.controls.length; j++) {
        const a = layout.controls[i];
        const b = layout.controls[j];
        const overlap = a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
        expect(overlap, `${a.id} overlaps ${b.id}`).toBe(false);
      }
    }

    // Pen picker is labelled and opens the four colour choices at 44px touch height.
    const picker = page.getByTestId('selection-pen-picker');
    await expect(picker).toHaveAttribute('aria-label', 'Highlight colour: Sage');
    await picker.tap();
    for (const pen of ['sage', 'amber', 'blue', 'rose']) {
      const option = page.getByTestId(`selection-pen-${pen}`);
      await expect(option).toBeVisible();
      const box = (await option.boundingBox())!;
      expect(box.height, `selection-pen-${pen} touch height`).toBeGreaterThanOrEqual(44);
    }
  });
}

test('phone 390px: Ask Nostos launcher stays visible below the docked selection bar without overlapping', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl);
  await page.addInitScript(() => {
    localStorage.setItem('nostos.theme', 'light');
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
  await page.setViewportSize({ width: 390, height: 844 });
  await openEpubReader(page, `${baseUrl}/read/${bookId}`);

  await page.getByTestId('reader-chrome-reveal').focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('reader-layout')).toHaveClass(/chrome-visible/);

  const trigger = page.getByTestId('assistant-trigger');
  await expect(trigger).toBeVisible();

  await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
    const doc = el.ownerDocument;
    const range = doc.createRange();
    range.setStart(el.firstChild!, 0);
    range.setEnd(el.firstChild!, Math.min(30, el.firstChild!.textContent!.length));
    doc.getSelection()!.removeAllRanges();
    doc.getSelection()!.addRange(range);
  });

  const bar = page.getByTestId('selection-bar');
  await expect(bar).toBeVisible();
  await expect(trigger).toBeVisible();
  await waitForLayoutSettled(page, '[data-testid="selection-bar"]');

  const barBox = (await bar.boundingBox())!;
  const triggerBox = (await trigger.boundingBox())!;
  const overlaps =
    barBox.x < triggerBox.x + triggerBox.width &&
    triggerBox.x < barBox.x + barBox.width &&
    barBox.y < triggerBox.y + triggerBox.height &&
    triggerBox.y < barBox.y + barBox.height;
  expect(overlaps, 'launcher must not overlap the docked selection bar').toBe(false);
  expect(triggerBox.y, 'launcher stays below top-docked selection bar').toBeGreaterThanOrEqual(
    barBox.y + barBox.height,
  );

  await page.screenshot({
    path: path.join(__dirname, 'visual-evidence', '1722-selection-popup', 'after', 'after-390-launcher-light.png'),
  });

  await trigger.tap();
  await expect(page.getByTestId('assistant-panel')).toBeVisible();
});

