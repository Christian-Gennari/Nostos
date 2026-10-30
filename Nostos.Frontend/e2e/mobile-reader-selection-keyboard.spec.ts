/**
 * Marking a passage on a phone keeps the page (#1270).
 *
 * On Android the soft keyboard used to shrink the reader (Add note focuses the
 * note field), epub.js re-paginated the section into the short box and
 * re-displayed its last seen location, and Save re-paginated it once more
 * when the keyboard closed. Deep inside a long section that left the reader
 * pages away from the passage they had just marked, and saved that place as
 * their progress.
 *
 * Emulation cannot open a keyboard, so the keyboard is simulated by what it
 * did: the viewport loses 40% of its height while the note is written, and
 * gets it back after Save or Cancel. The long-chapter fixture is the one from
 * the restore specs (#1271), where a re-layout lands far from the page.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, apiPut, loadFixture } from './support/fixture';
import { buildLongEpub, seedBook } from './support/reader-restore-position';

test.use({ serviceWorkers: 'block' });

type Ending = 'highlight' | 'note' | 'cancel';

/** The id of the first paragraph whose box overlaps the page on screen. */
async function firstParagraphOnPage(page: Page): Promise<string> {
  const stage = await page.locator('#epub-page > .epub-container').evaluate((el) => ({
    left: el.scrollLeft,
    width: el.clientWidth,
  }));
  const id = await page
    .frameLocator('#epub-viewer iframe')
    .locator('body')
    .evaluate((body, view) => {
      for (const p of Array.from(body.querySelectorAll('p[id]'))) {
        const r = p.getBoundingClientRect();
        // Whole paragraph starts on this page, so its first words are on it.
        if (r.left >= view.left && r.left < view.left + view.width && r.width > 0) return p.id;
      }
      return null;
    }, stage);
  if (!id) throw new Error('no paragraph starts on the visible page');
  return id;
}

async function paragraphOnPage(page: Page, id: string): Promise<{ onPage: boolean; detail: string }> {
  const stage = await page.locator('#epub-page > .epub-container').evaluate((el) => ({
    left: el.scrollLeft,
    width: el.clientWidth,
  }));
  const rect = await page
    .frameLocator('#epub-viewer iframe')
    .locator(`#${id}`)
    .evaluate((p) => {
      const r = p.getBoundingClientRect();
      return { left: r.left, right: r.right };
    });
  return {
    onPage: rect.right > stage.left && rect.left < stage.left + stage.width,
    detail: `${id} at ${Math.round(rect.left)}–${Math.round(rect.right)}, page ${stage.left}–${stage.left + stage.width}`,
  };
}

/** Select the paragraph's first words in the book, as a phone's handles would. */
async function selectStartOf(page: Page, id: string): Promise<string> {
  return page
    .frameLocator('#epub-viewer iframe')
    .locator(`#${id}`)
    .evaluate((el) => {
      const doc = el.ownerDocument;
      const text = el.firstChild!;
      const range = doc.createRange();
      range.setStart(text, 0);
      range.setEnd(text, Math.min(30, text.textContent!.length));
      const selection = doc.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      return selection.toString();
    });
}

async function expectPageKept(page: Page, ending: Ending): Promise<void> {
  const { baseUrl } = loadFixture();
  const { bytes, targetCfi } = await buildLongEpub();
  const bookId = await seedBook(baseUrl, bytes);
  await apiPut(baseUrl, `/api/books/${bookId}/progress`, { location: targetCfi, percentage: 93 });

  const viewport = page.viewportSize()!;
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  // Let the opening restore settle (#1271) before marking anything.
  await page.waitForTimeout(2_500);

  const writes: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'PUT' && request.url().endsWith(`/api/books/${bookId}/progress`)) {
      writes.push(request.postData() ?? '');
    }
  });

  const marked = await firstParagraphOnPage(page);
  const before = await paragraphOnPage(page, marked);
  expect(before.onPage, before.detail).toBe(true);

  await selectStartOf(page, marked);
  const bar = page.getByTestId('selection-bar');
  await expect(bar).toBeVisible();

  const keyboardOpen = { width: viewport.width, height: Math.round(viewport.height * 0.6) };
  if (ending === 'highlight') {
    // A plain Highlight opens no keyboard; any other resize of the reader box
    // while the bar is up (the browser toolbar) must not move the page either.
    await page.setViewportSize(keyboardOpen);
    await page.waitForTimeout(800);
    await page.setViewportSize(viewport);
    await page.waitForTimeout(300);
    await page.getByTestId('selection-highlight').tap();
  } else {
    await page.getByTestId('selection-add-note').tap();
    await page.setViewportSize(keyboardOpen);
    await page.waitForTimeout(800);
    if (ending === 'note') {
      await page.getByTestId('selection-note-input').fill('Kept on the page.');
      await page.getByTestId('selection-save-note').tap();
    } else {
      await page.getByTestId('selection-cancel').tap();
    }
    // The keyboard closes once the bar is gone.
    await page.setViewportSize(viewport);
  }
  await expect(bar).toHaveCount(0);

  // Past the resize debounce and the settle window.
  await page.waitForTimeout(3_000);
  const after = await paragraphOnPage(page, marked);
  expect(after.onPage, after.detail).toBe(true);

  const notes = await apiGet<any[]>(baseUrl, `/api/books/${bookId}/notes`);
  expect(notes).toHaveLength(ending === 'cancel' ? 0 : 1);
  if (ending === 'note') expect(notes[0].content).toBe('Kept on the page.');

  const saved = await apiGet<any>(baseUrl, `/api/books/${bookId}`);
  expect((saved?.book ?? saved).lastLocation).toBe(targetCfi);
  expect(writes, 'marking a passage writes no progress').toEqual([]);
}

test('phone: Add note with the keyboard open, then Save, keeps the page', async ({ page }) => {
  await expectPageKept(page, 'note');
});

test('phone: Add note with the keyboard open, then Cancel, keeps the page', async ({ page }) => {
  await expectPageKept(page, 'cancel');
});

test('phone: a resize while the bar is up, then Highlight, keeps the page', async ({ page }) => {
  await expectPageKept(page, 'highlight');
});

test('phone: the keyboard overlays the reader, the bar rides above it, and leaving the reader restores the viewport', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const { bytes, targetCfi } = await buildLongEpub();
  const bookId = await seedBook(baseUrl, bytes);
  await apiPut(baseUrl, `/api/books/${bookId}/progress`, { location: targetCfi, percentage: 93 });
  const viewportMeta = () =>
    page.evaluate(() => document.querySelector('meta[name="viewport"]')!.getAttribute('content'));

  await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  expect(await viewportMeta()).toContain('interactive-widget=resizes-content');

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(2_500);
  expect(await viewportMeta()).toContain('interactive-widget=resizes-visual');

  await selectStartOf(page, await firstParagraphOnPage(page));
  await page.getByTestId('selection-add-note').tap();
  const bar = page.getByTestId('selection-bar');
  await expect(bar).toBeVisible();

  // Emulation has no keyboard: report the visual viewport a keyboard leaves,
  // 340 px short, as a phone does under `resizes-visual`.
  const height = page.viewportSize()!.height;
  await page.evaluate((visible) => {
    const vv = window.visualViewport!;
    Object.defineProperty(vv, 'height', { configurable: true, get: () => visible });
    vv.dispatchEvent(new Event('resize'));
  }, height - 340);
  await expect
    .poll(async () => Math.round((await bar.boundingBox())!.y + (await bar.boundingBox())!.height))
    .toBe(height - 340);
  await expect(page.getByTestId('selection-note-input')).toBeInViewport();
  await page.screenshot({
    path: `${__dirname}/visual-evidence/reader-selection-mobile-note-keyboard.png`,
  });

  await page.evaluate(() => {
    const vv = window.visualViewport!;
    delete (vv as any).height;
    vv.dispatchEvent(new Event('resize'));
  });
  await expect
    .poll(async () => Math.round((await bar.boundingBox())!.y + (await bar.boundingBox())!.height))
    .toBe(height);

  await page.getByTestId('selection-cancel').tap();
  // Leave inside the app, not by reloading: the reader must put the meta back.
  await page.evaluate(() => {
    (document.querySelector('.reader-back') as HTMLElement).click();
  });
  await expect(page).not.toHaveURL(/\/read\//);
  await expect(page.locator('app-reader-shell, app-epub-reader')).toHaveCount(0);
  expect(await viewportMeta()).toContain('interactive-widget=resizes-content');
});
