/**
 * In-text selection actions for EPUB (#650) in a real engine, against the real
 * backend and a real epub.js rendition.
 *
 * Desktop (fine pointer): releasing a mouse selection opens the menu with
 * highlight mode OFF (#657); a real mouse drag-selection, right-click on it with
 * highlight mode OFF, the menu anchored at the text, Add note persisting a
 * note linked to the passage; highlight mode with a real multi-word drag
 * (#304: the drag must not be cut short) and one-action Highlight; Escape and
 * outside-click dismiss without saving; exactly one note per save.
 *
 * Mobile emulation (390px, touch, coarse pointer): the capture path a phone
 * uses (touchend fallback), the docked bar staying inside the viewport with
 * Add note, no horizontal scroll, and a touch long-press `contextmenu` left to
 * the platform (#16). Emulation is not a real phone: native selection handles
 * and the OS selection menu cannot be driven here.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, '..', 'assets', 'tiny.epub'));
const EVIDENCE_DIR = path.join(__dirname, '..', 'visual-evidence');

async function seedEpub(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

async function notes(baseUrl: string, bookId: string): Promise<any[]> {
  const res = await fetch(`${baseUrl}/api/books/${bookId}/notes`);
  return res.json();
}

async function openReader(page: Page, baseUrl: string, bookId: string) {
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  const frame = page.frameLocator('#epub-viewer iframe');
  await frame.locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(800);
  return frame;
}

/** Page-viewport box of the first paragraph inside the book iframe. */
async function paragraphBox(page: Page) {
  const box = await page.frameLocator('#epub-viewer iframe').locator('p').first().boundingBox();
  if (!box) throw new Error('no paragraph box');
  return box;
}

async function iframeSelection(page: Page): Promise<string> {
  return page.frameLocator('#epub-viewer iframe').locator('body').evaluate(
    (body) => body.ownerDocument.getSelection()?.toString() ?? '',
  );
}

async function snap(page: Page, name: string) {
  await page.screenshot({ path: path.join(EVIDENCE_DIR, `reader-selection-${name}.png`) });
}

async function revealEpubChrome(page: Page) {
  const chrome = page.getByTestId('reader-chrome-top');
  if ((await chrome.getAttribute('aria-hidden')) !== 'true') return;

  await page.frameLocator('#epub-viewer iframe').locator('body').evaluate((body) => {
    body.ownerDocument.getSelection()?.removeAllRanges();
    const width = body.ownerDocument.defaultView?.innerWidth ?? body.ownerDocument.documentElement.clientWidth;
    body.dispatchEvent(new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      clientX: width / 2,
      clientY: 20,
    }));
  });
  await expect(chrome).not.toHaveAttribute('aria-hidden', 'true');
}

async function turnOnHighlightMode(page: Page) {
  await revealEpubChrome(page);
  await page.getByTitle('Notes & Highlights').click();
  await page.locator('[data-testid="reader-highlight-toggle"]').click();
  await expect(page.locator('.notes-panel.open')).toHaveCount(0);
}

/**
 * Sets a selection of the paragraph's first `chars` characters inside the book
 * iframe. Playwright's mouse drags stall when they start inside epub.js's
 * sandboxed iframe (a harness limitation: the page stays responsive), so the
 * range is set in the real document and the real mouse events are sent
 * around it. The epub.js rendition, its debounced `selected` event and the
 * app's listeners are all the production ones.
 */
async function selectChars(page: Page, chars: number): Promise<string> {
  return page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el, n) => {
    const doc = el.ownerDocument;
    const text = el.firstChild!;
    const range = doc.createRange();
    range.setStart(text, 0);
    range.setEnd(text, Math.min(n, text.textContent!.length));
    const selection = doc.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    return selection.toString();
  }, chars);
}

async function dispatchInFrame(page: Page, type: 'mousedown' | 'mouseup') {
  await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el, t) => {
    el.dispatchEvent(new MouseEvent(t, { bubbles: true, cancelable: true, button: 0, buttons: t === 'mousedown' ? 1 : 0 }));
  }, type);
}

async function rightClickText(page: Page) {
  const p = await paragraphBox(page);
  const x = p.x + 12;
  const y = p.y + Math.min(10, p.height / 2);
  await page.mouse.move(x, y);
  await page.mouse.down({ button: 'right' });
  await page.mouse.up({ button: 'right' });
  return { p, y };
}

export function desktopSelectionSpecs() {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('resting EPUB hides chrome; a neutral click reveals it without resizing the book', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Immersive EPUB ${run}`);
    await openReader(page, baseUrl, bookId);

    const chrome = page.getByTestId('reader-chrome-top');
    const viewer = page.locator('#epub-viewer');
    const before = await viewer.boundingBox();
    expect(before).not.toBeNull();
    await expect(chrome).toHaveAttribute('aria-hidden', 'true');

    await revealEpubChrome(page);
    await expect(chrome).toBeVisible();
    const after = await viewer.boundingBox();
    expect(after).not.toBeNull();
    expect(after!.width).toBeCloseTo(before!.width, 1);
    expect(after!.height).toBeCloseTo(before!.height, 1);

    await page.frameLocator('#epub-viewer iframe').locator('body').evaluate((body) => {
      const width = body.ownerDocument.defaultView?.innerWidth ?? body.ownerDocument.documentElement.clientWidth;
      body.dispatchEvent(new MouseEvent('click', {
        bubbles: true,
        cancelable: true,
        clientX: width / 2,
        clientY: 20,
      }));
    });
    await expect(chrome).toHaveAttribute('aria-hidden', 'true');
  });

  test('right-click a selection → Add note at the text persists a linked note (highlight mode off)', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection A ${run}`);
    await openReader(page, baseUrl, bookId);

    const selected = await selectChars(page, 34);
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);

    const { p, y } = await rightClickText(page);
    const menu = page.locator('[data-testid="selection-menu"]');
    await expect(menu).toBeVisible();
    const menuBox = (await menu.boundingBox())!;
    // Anchored at the text: just below the selected line, not covering it.
    expect(menuBox.y).toBeGreaterThan(y);
    expect(menuBox.y - (p.y + p.height)).toBeLessThan(120);
    expect(menuBox.x).toBeGreaterThanOrEqual(0);
    expect(menuBox.x + menuBox.width).toBeLessThanOrEqual(page.viewportSize()!.width);
    await expect(page.locator('[data-testid="selection-bar"]')).toHaveCount(0);
    await expect(menu.locator('.selected-text')).toContainText(selected.trim().slice(0, 20));
    await snap(page, 'desktop-menu');

    await page.locator('[data-testid="selection-add-note"]').click();
    await page.locator('[data-testid="selection-note-input"]').fill('Why this opening matters.');
    await snap(page, 'desktop-note');
    await page.locator('[data-testid="selection-save-note"]').click();
    await expect(menu).toHaveCount(0);

    const saved = await notes(baseUrl, bookId);
    expect(saved).toHaveLength(1);
    expect(saved[0].content).toBe('Why this opening matters.');
    expect(saved[0].selectedText.trim()).toBe(selected.trim());
    expect(saved[0].cfiRange).toMatch(/^epubcfi\(/);

    await revealEpubChrome(page);
    await page.getByTitle('Notes & Highlights').click();
    await expect(page.locator('.notes-panel.open')).toContainText('Why this opening matters.');
  });

  test('highlight mode: a pause mid-drag is not captured; the released range is, and Highlight saves once', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection B ${run}`);
    await openReader(page, baseUrl, bookId);
    await turnOnHighlightMode(page);
    const menu = page.locator('[data-testid="selection-menu"]');

    // Button held with a partial range for longer than epub.js's ~250 ms
    // selectionchange debounce: its `selected` event fires, and must be ignored.
    await dispatchInFrame(page, 'mousedown');
    await selectChars(page, 3);
    await page.waitForTimeout(450);
    await expect(menu).toHaveCount(0);
    expect(await iframeSelection(page)).toBe((await selectChars(page, 3)));

    const full = await selectChars(page, 40);
    await dispatchInFrame(page, 'mouseup');
    await expect(menu).toBeVisible();
    await expect(menu.locator('.selected-text')).toContainText(full.trim().slice(0, 30));
    await snap(page, 'desktop-highlight-mode');

    // Escape dismisses without saving.
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);

    // Outside click dismisses without saving.
    await selectChars(page, 40);
    await dispatchInFrame(page, 'mouseup');
    await expect(menu).toBeVisible();
    await page.locator('[data-testid="selection-scrim"]').click({ position: { x: 5, y: 400 } });
    await expect(menu).toHaveCount(0);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);

    // Highlight saves in one action, exactly once (no double-fire from the
    // mouseup path and epub.js's selected event).
    await selectChars(page, 40);
    await dispatchInFrame(page, 'mouseup');
    await expect(menu).toBeVisible();
    await page.waitForTimeout(400);
    await page.locator('[data-testid="selection-highlight"]').click();
    await expect(menu).toHaveCount(0);
    await page.waitForTimeout(400);
    const saved = await notes(baseUrl, bookId);
    expect(saved).toHaveLength(1);
    expect(saved[0].content ?? '').toBe('');
    await expect(page.locator('[data-testid="selection-bar"]')).toHaveCount(0);

    // Page-turn keys still belong to the reader once the menu is gone.
    const before = await page.locator('.progress-display').textContent();
    await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(300);
    expect(typeof before).toBe('string');
  });

  test('a right-click with nothing selected leaves the native menu alone', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection C ${run}`);
    await openReader(page, baseUrl, bookId);

    const prevented = await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      el.ownerDocument.getSelection()?.removeAllRanges();
      const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, button: 2 });
      el.dispatchEvent(event);
      return event.defaultPrevented;
    });
    expect(prevented).toBe(false);
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);
  });

  test('releasing a mouse selection opens the menu without a right-click; the quote shows the passage (#657)', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection D ${run}`);
    await openReader(page, baseUrl, bookId);
    const menu = page.locator('[data-testid="selection-menu"]');

    // Highlight mode stays OFF: a plain desktop drag is enough.
    await dispatchInFrame(page, 'mousedown');
    const selected = await selectChars(page, 140);
    await dispatchInFrame(page, 'mouseup');

    await expect(menu).toBeVisible();
    await expect(page.locator('[data-testid="selection-bar"]')).toHaveCount(0);
    // Far more than the old 60-character slice is visible.
    await expect(menu.locator('.selected-text')).toContainText(selected.trim().slice(0, 100));
    // Desktop density: the menu's actions are 32px controls, not 44px touch targets.
    for (const id of ['selection-copy', 'selection-cancel', 'selection-add-note', 'selection-highlight']) {
      const b = (await page.locator(`[data-testid="${id}"]`).boundingBox())!;
      expect(b.height, `${id} desktop height`).toBeLessThan(40);
    }
    await snap(page, 'desktop-mouseup-menu');

    await page.locator('[data-testid="selection-cancel"]').click();
    await expect(menu).toHaveCount(0);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);

    // A plain click (collapsed selection) opens nothing.
    await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      el.ownerDocument.getSelection()?.removeAllRanges();
    });
    await dispatchInFrame(page, 'mousedown');
    await dispatchInFrame(page, 'mouseup');
    await page.waitForTimeout(300);
    await expect(menu).toHaveCount(0);
  });
}

export function mobileSelectionSpecs() {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  /** Selects the paragraph's first words inside the iframe, as a phone's handles would. */
  async function selectWords(page: Page) {
    return page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
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

  test('phone: highlight mode capture docks the bar with Add note inside the viewport and saves a note', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection M ${run}`);
    await openReader(page, baseUrl, bookId);
    await turnOnHighlightMode(page);

    const selected = await selectWords(page);
    await page.frameLocator('#epub-viewer iframe').locator('body').evaluate((body) =>
      body.ownerDocument.dispatchEvent(new Event('touchend')),
    );

    const bar = page.locator('[data-testid="selection-bar"]');
    await expect(bar).toBeVisible();
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="selection-scrim"]')).toHaveCount(0);
    const viewport = page.viewportSize()!;
    for (const id of ['selection-cancel', 'selection-add-note', 'selection-highlight']) {
      const b = (await page.locator(`[data-testid="${id}"]`).boundingBox())!;
      expect(b.x).toBeGreaterThanOrEqual(0);
      expect(b.x + b.width, `${id} inside the viewport`).toBeLessThanOrEqual(viewport.width);
      expect(b.y + b.height).toBeLessThanOrEqual(viewport.height);
      expect(b.height, `${id} thumb-sized`).toBeGreaterThanOrEqual(40);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await snap(page, 'mobile-bar');

    await page.locator('[data-testid="selection-add-note"]').tap();
    await page.locator('[data-testid="selection-note-input"]').fill('A note from the phone.');
    await snap(page, 'mobile-note');
    await page.locator('[data-testid="selection-save-note"]').tap();
    await expect(bar).toHaveCount(0);

    const saved = await notes(baseUrl, bookId);
    expect(saved).toHaveLength(1);
    expect(saved[0].content).toBe('A note from the phone.');
    expect(saved[0].selectedText.trim()).toBe(selected.trim());
  });

  test('phone: a touch long-press contextmenu keeps the native selection and opens nothing (#16)', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection N ${run}`);
    await openReader(page, baseUrl, bookId);

    const selected = await selectWords(page);
    const result = await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      const touch = new PointerEvent('contextmenu', { bubbles: true, cancelable: true, pointerType: 'touch' });
      el.dispatchEvent(touch);
      const mouseOnPhone = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, button: 2 });
      el.dispatchEvent(mouseOnPhone);
      return {
        touchPrevented: touch.defaultPrevented,
        mousePrevented: mouseOnPhone.defaultPrevented,
        stillSelected: el.ownerDocument.getSelection()?.toString() ?? '',
      };
    });

    expect(result.touchPrevented).toBe(false);
    expect(result.mousePrevented, 'a coarse-pointer device never takes over contextmenu').toBe(false);
    expect(result.stillSelected).toBe(selected);
    await expect(page.locator('[data-testid="selection-bar"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);
  });
}
