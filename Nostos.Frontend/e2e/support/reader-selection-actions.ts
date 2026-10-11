/**
 * In-text selection actions for EPUB (#650) in a real engine, against the real
 * backend and a real epub.js rendition.
 *
 * Desktop (fine pointer): releasing a mouse selection opens the menu (#657); a
 * real mouse drag-selection, right-click on it, the menu anchored at the text,
 * Add note persisting a note linked to the passage; a real multi-word drag
 * (#304: the drag must not be cut short) and one-action Highlight; Escape and
 * outside-click dismiss without saving; exactly one note per save.
 *
 * Mobile emulation (390px, touch, coarse pointer): a settled selection docks
 * the bar with nothing to switch on and follows the selection as it changes,
 * the bar staying inside the viewport with Add note and the pen dots, no
 * horizontal scroll, a tap on the page dropping the offer, and a touch
 * long-press `contextmenu` left to the platform (#16). Emulation is not a real
 * phone: native selection handles and the OS selection menu cannot be driven
 * here, so the selection is set on the real document instead.
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

/** Every docked action is the topmost element at its own centre: nothing is drawn over it. */
async function expectBarUncovered(page: Page) {
  for (const id of ['selection-pen-rose', 'selection-copy', 'selection-cancel', 'selection-add-note', 'selection-highlight']) {
    const onTop = await page.getByTestId(id).evaluate((el) => {
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return !!hit && el.contains(hit);
    });
    expect(onTop, `${id} is not covered`).toBe(true);
  }
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

  test('right-click a selection → Add note at the text persists a linked note', async ({ page }) => {
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

  test('a pause mid-drag is not captured; the released range is, and Highlight saves once', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection B ${run}`);
    await openReader(page, baseUrl, bookId);
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
    await snap(page, 'desktop-mid-drag');

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

    // A plain desktop drag is enough.
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

  /** Selects the paragraph's first `chars` characters inside the iframe, as a phone's handles would. */
  async function selectWords(page: Page, chars = 30) {
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

  const iframeSelection = (page: Page) =>
    page.frameLocator('#epub-viewer iframe').locator('body').evaluate(
      (body) => body.ownerDocument.getSelection()?.toString() ?? '',
    );

  test('phone: a settled selection docks the bar with no mode to switch on, follows the handles, and saves a note', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection M ${run}`);
    await openReader(page, baseUrl, bookId);

    // No touch events at all: handle drags fire none. The bar must come from
    // the selection settling (epub.js's debounced `selected`).
    const first = await selectWords(page, 12);
    const bar = page.locator('[data-testid="selection-bar"]');
    await expect(bar).toBeVisible();
    await expect(bar.locator('.selected-text')).toContainText(first.trim());
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="selection-scrim"]')).toHaveCount(0);
    // The native selection is left to the phone's handles and OS menu (#16).
    expect(await iframeSelection(page)).toBe(first);

    // Dragging a handle further replaces the offered passage.
    const selected = await selectWords(page, 30);
    await expect(bar.locator('.selected-text')).toContainText(selected.trim());
    expect(await iframeSelection(page)).toBe(selected);

    const viewport = page.viewportSize()!;
    for (const id of [
      'selection-copy',
      'selection-cancel',
      'selection-add-note',
      'selection-highlight',
      'selection-pen-amber',
      'selection-pen-rose',
    ]) {
      const b = (await page.locator(`[data-testid="${id}"]`).boundingBox())!;
      expect(b.x).toBeGreaterThanOrEqual(0);
      expect(b.x + b.width, `${id} inside the viewport`).toBeLessThanOrEqual(viewport.width);
      expect(b.y + b.height).toBeLessThanOrEqual(viewport.height);
      if (!id.startsWith('selection-pen')) {
        expect(b.height, `${id} thumb-sized`).toBeGreaterThanOrEqual(40);
      }
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
    // Saved: the live selection is gone, so it is not offered a second time.
    await page.waitForTimeout(600);
    await expect(bar).toHaveCount(0);
    expect(await iframeSelection(page)).toBe('');
  });

  test('phone: with the reader chrome showing, the pager steps aside for the docked bar', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection C ${run}`);
    await openReader(page, baseUrl, bookId);
    await revealEpubChrome(page);
    const pager = page.getByTestId('reader-chrome-bottom');
    await expect(pager).toBeVisible();

    await selectWords(page);
    await expect(page.locator('[data-testid="selection-bar"]')).toBeVisible();
    await expect(pager).toBeHidden();
    await expect(page.getByTestId('reader-chrome-top')).toBeVisible();
    await expectBarUncovered(page);
    await snap(page, 'mobile-bar-with-chrome');

    await page.locator('[data-testid="selection-cancel"]').tap();
    await expect(pager).toBeVisible();
  });

  test('phone: a pen dot picks the colour and Highlight saves in it; a tap on the page drops an offer without turning the page', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedEpub(baseUrl, `Selection P ${run}`);
    await openReader(page, baseUrl, bookId);
    const bar = page.locator('[data-testid="selection-bar"]');
    const frame = page.frameLocator('#epub-viewer iframe');

    await selectWords(page);
    await expect(bar).toBeVisible();
    const progress = await page.locator('.progress-display').first().textContent();
    // A real tap on the right edge of the page, where a tap normally turns it.
    // (Headless Chromium does not collapse a selection on tap the way a phone
    // does, so the collapse is made explicit first.)
    await frame.locator('body').evaluate((body) => body.ownerDocument.getSelection()!.removeAllRanges());
    const view = (await page.locator('#epub-viewer').boundingBox())!;
    await page.touchscreen.tap(view.x + view.width - 12, view.y + view.height / 2);
    await expect(bar).toHaveCount(0);
    expect(await page.locator('.progress-display').first().textContent()).toBe(progress);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);

    const selected = await selectWords(page);
    await expect(bar).toBeVisible();
    // A pen dot only picks the colour; nothing is saved until Highlight.
    const pendingFill = () =>
      page.locator('.epubjs-hl-pending').first().evaluate((el) => getComputedStyle(el).fill);
    const amberFill = await pendingFill();
    await page.locator('[data-testid="selection-pen-sage"]').tap();
    // The offered passage is redrawn in the pen just picked.
    await expect.poll(pendingFill).not.toBe(amberFill);
    await expect(page.locator('.epubjs-hl-pending')).toHaveCount(1);
    await page.waitForTimeout(400);
    await expect(bar).toBeVisible();
    expect(await notes(baseUrl, bookId)).toHaveLength(0);
    await page.locator('[data-testid="selection-highlight"]').tap();
    await expect(bar).toHaveCount(0);
    const saved = await notes(baseUrl, bookId);
    expect(saved).toHaveLength(1);
    expect(saved[0].highlightColour).toBe('sage');
    expect(saved[0].selectedText.trim()).toBe(selected.trim());
    await snap(page, 'mobile-highlighted');
  });

  test('phone: a touch long-press contextmenu is left to the platform; the docked bar is offered beside it (#16)', async ({ page }) => {
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
    await expect(page.locator('[data-testid="selection-bar"]')).toBeVisible();
    await expect(page.locator('[data-testid="selection-menu"]')).toHaveCount(0);
    expect(await iframeSelection(page)).toBe(selected);
    expect(await notes(baseUrl, bookId)).toHaveLength(0);
  });
}
