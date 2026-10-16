/**
 * Opening a saved highlight from the page, in a real engine against the real
 * backend: epub.js and PDF.js render the book, the marks are the production
 * ones, and every save goes through the API.
 *
 * Desktop (fine pointer): a click on a highlight opens the card at the mark
 * with its note, its pen and its actions; a note is added, edited and its pen
 * changed from there; a highlight that carries a note shows the dotted rule;
 * Escape closes; Delete removes the highlight. The same for a PDF page.
 *
 * Phone emulation (390px, touch, coarse pointer): a tap on a highlight docks
 * the card at the bottom, inside the viewport with thumb-sized actions, even at
 * the page's edge where a tap would otherwise turn the page; a tap elsewhere
 * closes it without revealing the chrome.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiGet, apiPost, loadFixture, newRunId } from './fixture';
import { epubMarkCount } from './epub-marks';
import { seedBook, textPdf } from './pdf-fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, '..', 'assets', 'read-aloud.epub'));
const EVIDENCE_DIR = path.join(__dirname, '..', 'visual-evidence');

async function seedEpub(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'Jane Austen' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

const notes = (baseUrl: string, bookId: string) => apiGet<any[]>(baseUrl, `/api/books/${bookId}/notes`);

async function useTheme(page: Page, theme: 'light' | 'dark') {
  await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
}

async function openEpub(page: Page, baseUrl: string, bookId: string) {
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(800);
}

/**
 * Selects characters `start..end` of paragraph `index` in the book's iframe.
 * Playwright's mouse drags stall inside epub.js's sandboxed iframe, so the
 * range is set in the real document (as the selection specs do).
 */
async function selectInParagraph(page: Page, index: number, start: number, end: number): Promise<string> {
  return page.frameLocator('#epub-viewer iframe').locator('p').nth(index).evaluate(
    (el, [s, e]) => {
      const doc = el.ownerDocument;
      const text = el.firstChild!;
      const range = doc.createRange();
      range.setStart(text, s);
      range.setEnd(text, Math.min(e, text.textContent!.length));
      const selection = doc.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      return selection.toString();
    },
    [start, end],
  );
}

/** Releases a desktop mouse selection in the iframe, which opens the selection menu. */
async function releaseMouse(page: Page) {
  await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0, buttons: 0 }));
  });
}

/** Page-viewport point at the middle of characters `start..end` of paragraph `index`. */
async function pointInParagraph(page: Page, index: number, start: number, end: number) {
  const frame = await page.locator('#epub-viewer iframe').first().boundingBox();
  if (!frame) throw new Error('no book frame');
  const rect = await page.frameLocator('#epub-viewer iframe').locator('p').nth(index).evaluate(
    (el, [s, e]) => {
      const range = el.ownerDocument.createRange();
      range.setStart(el.firstChild!, s);
      range.setEnd(el.firstChild!, e);
      const r = range.getClientRects()[0];
      return { x: r.left + r.width / 2, y: r.top + r.height / 2, bottom: r.bottom };
    },
    [start, end],
  );
  return { x: frame.x + rect.x, y: frame.y + rect.y, bottom: frame.y + rect.bottom };
}

async function snap(page: Page, name: string) {
  // The card fades in, and a save notice fades out: shoot the settled screen.
  await expect(page.locator('.reader-save-feedback')).toHaveCount(0, { timeout: 5_000 });
  await page.waitForFunction(() =>
    (document.querySelector('[data-testid="highlight-card"]')?.getAnimations({ subtree: true }) ?? []).every(
      (animation) => animation.playState !== 'running',
    ),
  );
  await page.screenshot({ path: path.join(EVIDENCE_DIR, `reader-highlight-card-${name}.png`) });
}

/** The first paragraph's opening clause: "It is a truth universally acknowledged". */
const FIRST = { index: 0, start: 0, end: 38 };

export function desktopHighlightCardSpecs() {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  for (const theme of ['light', 'dark'] as const) {
    test(`EPUB (${theme}): a click on a highlight opens its note at the mark; edit, recolour, close`, async ({ page }) => {
      const { baseUrl } = loadFixture();
      await useTheme(page, theme);
      const bookId = await seedEpub(baseUrl, `Highlight card EPUB ${theme} ${run}`);
      await openEpub(page, baseUrl, bookId);

      // A highlight made with a note, through the selection menu.
      await selectInParagraph(page, FIRST.index, FIRST.start, FIRST.end);
      await releaseMouse(page);
      await page.getByTestId('selection-add-note').click();
      await page.getByTestId('selection-note-input').fill('The irony starts in the first line.');
      await page.getByTestId('selection-save-note').click();
      await expect(page.getByTestId('selection-menu')).toHaveCount(0);
      await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
      const [saved] = await notes(baseUrl, bookId);

      // It carries a note, so it is painted with the dotted rule.
      await expect.poll(() => epubMarkCount(page, `pen-${saved.highlightColour}-noted`)).toBe(1);

      const card = page.getByTestId('highlight-card');
      await expect(card).toHaveCount(0);
      const at = await pointInParagraph(page, FIRST.index, 8, 13);
      await page.mouse.click(at.x, at.y);

      await expect(card).toBeVisible();
      await expect(card.locator('.selected-text')).toContainText('It is a truth universally acknowledged');
      await expect(page.getByTestId('highlight-card-note')).toHaveText('The irony starts in the first line.');
      await expect(page.getByTestId(`highlight-card-pen-${saved.highlightColour}`)).toHaveAttribute('aria-pressed', 'true');
      // Anchored at the mark: just below the clicked line, not over it.
      const box = (await card.boundingBox())!;
      expect(box.y).toBeGreaterThanOrEqual(at.bottom);
      expect(box.y - at.bottom).toBeLessThan(40);
      // The book stays where it was: the chrome did not toggle under the card.
      await expect(page.getByTestId('reader-chrome-top')).toHaveAttribute('aria-hidden', 'true');
      await page.mouse.move(0, 0);
      await snap(page, `desktop-epub-${theme}`);

      if (theme === 'dark') return;

      // Edit the note in place.
      await page.getByTestId('highlight-card-edit').click();
      const input = page.getByTestId('highlight-card-input');
      await expect(input).toBeFocused();
      await input.fill('The irony starts in the very first line.');
      await snap(page, 'desktop-epub-editing');
      await page.getByTestId('highlight-card-save').click();
      await expect(page.getByTestId('highlight-card-note')).toHaveText('The irony starts in the very first line.');
      await expect.poll(async () => (await notes(baseUrl, bookId))[0].content).toBe(
        'The irony starts in the very first line.',
      );

      // Change the pen: saved at once and redrawn, keeping the note rule.
      await page.getByTestId('highlight-card-pen-rose').click();
      await expect.poll(async () => (await notes(baseUrl, bookId))[0].highlightColour).toBe('rose');
      await expect.poll(() => epubMarkCount(page, 'pen-rose-noted')).toBe(1);
      await expect(page.getByTestId('highlight-card-pen-rose')).toHaveAttribute('aria-pressed', 'true');

      // Escape closes; a click on unmarked text does not open anything.
      await page.keyboard.press('Escape');
      await expect(card).toHaveCount(0);
      const plain = await pointInParagraph(page, 1, 10, 20);
      await page.mouse.click(plain.x, plain.y);
      await expect(card).toHaveCount(0);
      expect(await notes(baseUrl, bookId)).toHaveLength(1);
    });
  }

  test('EPUB: a plain highlight gains a note from its card, and Delete removes it', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Highlight card plain ${run}`);
    await openEpub(page, baseUrl, bookId);

    await selectInParagraph(page, FIRST.index, FIRST.start, FIRST.end);
    await releaseMouse(page);
    await page.getByTestId('selection-highlight').click();
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
    const [saved] = await notes(baseUrl, bookId);
    expect(saved.content).toBe('');
    await expect.poll(() => epubMarkCount(page, `pen-${saved.highlightColour}`)).toBe(1);
    expect(await epubMarkCount(page, `pen-${saved.highlightColour}-noted`)).toBe(0);

    const at = await pointInParagraph(page, FIRST.index, 8, 13);
    await page.mouse.click(at.x, at.y);
    const card = page.getByTestId('highlight-card');
    await expect(card).toBeVisible();
    await expect(page.getByTestId('highlight-card-note')).toHaveCount(0);
    await expect(page.getByTestId('highlight-card-edit')).toHaveText(/Add note/);
    await page.mouse.move(0, 0);
    await snap(page, 'desktop-epub-plain');

    await page.getByTestId('highlight-card-edit').click();
    await page.getByTestId('highlight-card-input').fill('Added later, from the page.');
    await page.keyboard.press('Control+Enter');
    await expect(page.getByTestId('highlight-card-note')).toHaveText('Added later, from the page.');
    await expect.poll(async () => (await notes(baseUrl, bookId))[0].content).toBe('Added later, from the page.');
    // Now it carries a note, the rule appears on the page.
    await expect.poll(() => epubMarkCount(page, `pen-${saved.highlightColour}-noted`)).toBe(1);
    expect(await epubMarkCount(page, `pen-${saved.highlightColour}`)).toBe(0);

    await page.getByTestId('highlight-card-delete').click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(card).toHaveCount(0);
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(0);
    await expect.poll(() => epubMarkCount(page, `pen-${saved.highlightColour}-noted`)).toBe(0);
  });

  test('PDF: a click on a highlight opens its card; a note added there draws the rule', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `Highlight card PDF ${run}`,
      textPdf(['It is a truth universally acknowledged']),
      'card.pdf',
      'application/pdf',
    );
    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });

    const span = page.locator('.textLayer span', { hasText: 'It is a truth' }).first();
    await span.waitFor({ timeout: 60_000 });
    const text = (await span.boundingBox())!;
    const y = text.y + text.height / 2;
    await page.mouse.move(text.x + 2, y);
    await page.mouse.down();
    await page.mouse.move(text.x + text.width / 2, y, { steps: 8 });
    await page.mouse.move(text.x + text.width - 2, y, { steps: 8 });
    await page.mouse.up();
    await page.getByTestId('selection-highlight').click();
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
    await expect(page.locator('.highlight-note-rule')).toHaveCount(0);

    // The pointer says the mark can be opened.
    await page.mouse.move(text.x + text.width / 2, y);
    await expect(page.locator('.pdf-container')).toHaveClass(/over-highlight/);

    await page.mouse.click(text.x + text.width / 2, y);
    const card = page.getByTestId('highlight-card');
    await expect(card).toBeVisible();
    await expect(card.locator('.selected-text')).toContainText('It is a truth');
    const box = (await card.boundingBox())!;
    expect(box.y).toBeGreaterThanOrEqual(text.y + text.height - 1);

    await page.getByTestId('highlight-card-edit').click();
    await page.getByTestId('highlight-card-input').fill('Austen opens on a joke.');
    await page.getByTestId('highlight-card-save').click();
    await expect(page.getByTestId('highlight-card-note')).toHaveText('Austen opens on a joke.');
    await expect.poll(async () => (await notes(baseUrl, bookId))[0].content).toBe('Austen opens on a joke.');
    await expect(page.locator('.highlight-note-rule').first()).toBeAttached();
    await page.mouse.move(0, 0);
    await snap(page, 'desktop-pdf');

    // A click on the page away from the mark closes the card.
    const pageBox = (await page.locator('.page').first().boundingBox())!;
    await page.mouse.click(pageBox.x + pageBox.width / 2, pageBox.y + pageBox.height * 0.8);
    await expect(card).toHaveCount(0);
  });
}

export function mobileHighlightCardSpecs() {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  for (const theme of ['light', 'dark'] as const) {
    test(`phone (${theme}): a tap on a highlight docks its card; a tap elsewhere closes it`, async ({ page }) => {
      const { baseUrl } = loadFixture();
      await useTheme(page, theme);
      const bookId = await seedEpub(baseUrl, `Highlight card phone ${theme} ${run}`);
      await openEpub(page, baseUrl, bookId);

      // The touch path: a settled selection docks the bar.
      await selectInParagraph(page, FIRST.index, FIRST.start, FIRST.end);
      await expect(page.getByTestId('selection-bar')).toBeVisible();
      await page.getByTestId('selection-add-note').tap();
      await page.getByTestId('selection-note-input').fill('The irony starts in the first line.');
      await page.getByTestId('selection-save-note').tap();
      await expect(page.getByTestId('selection-bar')).toHaveCount(0);
      await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
      await page.waitForTimeout(600);

      // "It" sits at the page's left edge, inside the zone where a tap turns
      // the page back. The highlight takes the tap instead.
      const progress = await page.getByTestId('reader-chrome-bottom').textContent();
      const at = await pointInParagraph(page, FIRST.index, 0, 5);
      await page.touchscreen.tap(at.x, at.y);

      const card = page.getByTestId('highlight-card');
      await expect(card).toBeVisible();
      await expect(card).toHaveClass(/highlight-card-docked/);
      await expect(page.getByTestId('highlight-card-note')).toHaveText('The irony starts in the first line.');
      expect(await page.getByTestId('reader-chrome-bottom').textContent()).toBe(progress);

      const viewport = page.viewportSize()!;
      for (const id of ['highlight-card-close', 'highlight-card-edit', 'highlight-card-delete', 'highlight-card-pen-rose']) {
        const b = (await page.getByTestId(id).boundingBox())!;
        expect(b.x).toBeGreaterThanOrEqual(0);
        expect(b.x + b.width, `${id} inside the viewport`).toBeLessThanOrEqual(viewport.width);
        expect(b.y + b.height).toBeLessThanOrEqual(viewport.height);
        if (id === 'highlight-card-close' || id === 'highlight-card-edit') {
          expect(b.height, `${id} thumb-sized`).toBeGreaterThanOrEqual(40);
        }
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await snap(page, `mobile-epub-${theme}`);

      if (theme === 'dark') return;

      // Writing on the phone keeps the card docked with its field.
      await page.getByTestId('highlight-card-edit').tap();
      await expect(page.getByTestId('highlight-card-input')).toBeVisible();
      await snap(page, 'mobile-epub-editing');
      await page.getByTestId('highlight-card-cancel').tap();

      // A tap on unmarked text in the middle closes the card and leaves the
      // chrome as it was.
      const plain = await pointInParagraph(page, 1, 30, 45);
      await page.touchscreen.tap(viewport.width / 2, plain.y);
      await expect(card).toHaveCount(0);
      await expect(page.getByTestId('reader-chrome-top')).toHaveAttribute('aria-hidden', 'true');
    });
  }

  test('phone: a tap on a PDF highlight docks its card', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `Highlight card phone PDF ${run}`,
      textPdf(['It is a truth universally']),
      'card.pdf',
      'application/pdf',
    );
    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
    const span = page.locator('.textLayer span', { hasText: 'It is a truth' }).first();
    await span.waitFor({ timeout: 60_000 });

    // The phone's handles cannot be driven here; the selection is set on the
    // real text layer and settles as a touch selection does.
    await span.evaluate((el) => {
      const range = el.ownerDocument.createRange();
      range.selectNodeContents(el);
      const selection = el.ownerDocument.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
    });
    const text = (await span.boundingBox())!;
    const bar = page.getByTestId('selection-bar');
    await expect(bar).toBeVisible();
    await page.getByTestId('selection-highlight').tap();
    await expect(bar).toHaveCount(0);
    await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(1);
    await page.waitForTimeout(400);

    await page.touchscreen.tap(text.x + text.width / 2, text.y + text.height / 2);
    const card = page.getByTestId('highlight-card');
    await expect(card).toBeVisible();
    await expect(card).toHaveClass(/highlight-card-docked/);
    await expect(page.getByTestId('highlight-card-edit')).toHaveText(/Add note/);
  });
}
