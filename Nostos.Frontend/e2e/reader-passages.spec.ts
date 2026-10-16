/**
 * Passages in an EPUB (issue #1014), in a real engine against the real backend
 * and a real epub.js rendition: two marks in different chapters, made in
 * either order, become one passage with the text between them; the first mark
 * survives a reload; the passage opens in the passage viewer from the notes
 * panel and from the book's page, and "Open in book" turns the reader to it.
 *
 * The book is three chapters of Garnett's public-domain translation of The
 * Brothers Karamazov (Book V, chapters IV-VI), so the passage crosses spine
 * sections the way a real one does.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'passages.epub'));

test.use({ serviceWorkers: 'block' });

async function seedEpub(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title,
    author: 'Fyodor Dostoyevsky',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

async function notes(baseUrl: string, bookId: string): Promise<any[]> {
  return (await fetch(`${baseUrl}/api/books/${bookId}/notes`)).json();
}

const book = (page: Page) => page.frameLocator('#epub-viewer iframe');

async function openReader(page: Page, baseUrl: string, bookId: string) {
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await book(page).locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(800);
}

async function revealChrome(page: Page) {
  const chrome = page.getByTestId('reader-chrome-top');
  if ((await chrome.getAttribute('aria-hidden')) !== 'true') return;
  await book(page).locator('body').evaluate((body) => {
    body.ownerDocument.getSelection()?.removeAllRanges();
    const width = body.ownerDocument.defaultView?.innerWidth ?? 800;
    body.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: width / 2, clientY: 20 }));
  });
  await expect(chrome).not.toHaveAttribute('aria-hidden', 'true');
}

async function openChapter(page: Page, label: string) {
  await revealChrome(page);
  await page.getByRole('button', { name: 'Table of Contents' }).click();
  await page.locator('.toc-panel.open').getByText(label).first().click();
  await expect(book(page).locator('h2')).toContainText(label.split('. ')[1]);
  await page.waitForTimeout(600);
}

/**
 * Selects the first (or last) `chars` characters of the chapter's first (or
 * last) paragraph in the real document, then releases a mouse over it, which
 * opens the selection menu exactly as a desktop drag does (#657).
 */
async function select(page: Page, which: 'first' | 'last', chars: number): Promise<string> {
  const text = await book(page)
    .locator('p')
    .nth(which === 'first' ? 0 : -1)
    .evaluate(
      (el, [n, at]) => {
        const doc = el.ownerDocument;
        const walker = doc.createTreeWalker(el, NodeFilter.SHOW_TEXT);
        const nodes: Text[] = [];
        while (walker.nextNode()) nodes.push(walker.currentNode as Text);
        const range = doc.createRange();
        if (at === 'first') {
          range.setStart(nodes[0], 0);
          range.setEnd(nodes[0], Math.min(n as number, nodes[0].length));
        } else {
          const last = nodes[nodes.length - 1];
          range.setStart(last, Math.max(0, last.length - (n as number)));
          range.setEnd(last, last.length);
        }
        const selection = doc.getSelection()!;
        selection.removeAllRanges();
        selection.addRange(range);
        el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0, buttons: 1 }));
        el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0, buttons: 0 }));
        return selection.toString();
      },
      [chars, which],
    );
  await expect(page.getByTestId('selection-mark-passage')).toBeVisible();
  return text;
}

test('two marks in either order save the passage between them, across chapters', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl, `Passages ${newRunId()}`);
  await openReader(page, baseUrl, bookId);

  // The END first: the last words of chapter VI.
  await openChapter(page, 'Chapter VI. For Awhile A Very Obscure One');
  const ending = await select(page, 'last', 40);
  await expect(page.getByTestId('selection-mark-passage')).toHaveAttribute('aria-label', 'Mark passage');
  await page.getByTestId('selection-mark-passage').click();
  await expect(page.getByTestId('passage-pill')).toBeVisible();
  await expect(page.getByTestId('selection-menu')).toHaveCount(0);
  expect(await notes(baseUrl, bookId)).toHaveLength(0);

  // The mark waits across a reload.
  await openReader(page, baseUrl, bookId);
  await expect(page.getByTestId('passage-pill')).toBeVisible();

  // Then the START, earlier in the book: the opening of chapter V.
  await openChapter(page, 'Chapter V. The Grand Inquisitor');
  const opening = await select(page, 'first', 50);
  await expect(page.getByTestId('selection-mark-passage')).toHaveAttribute('aria-label', 'Complete passage here');
  await page.getByTestId('selection-mark-passage').click();

  const title = page.getByTestId('passage-sheet-title-input');
  await expect(title).toHaveValue('Chapter V. The Grand Inquisitor');
  await expect(page.getByTestId('passage-sheet-excerpt')).toContainText(opening.slice(0, 30));
  await expect(page.getByTestId('passage-sheet-size')).toContainText('words');
  await title.fill('The Grand Inquisitor');
  await page.getByTestId('passage-sheet-note').fill("Ivan's poem.");
  await page.getByTestId('passage-sheet-save').click();
  await expect(page.getByTestId('passage-sheet-save')).toHaveCount(0);
  await expect(page.getByTestId('passage-pill')).toHaveCount(0);

  const saved = await notes(baseUrl, bookId);
  expect(saved).toHaveLength(1);
  const passage = saved[0];
  expect(passage.kind).toBe('passage');
  expect(passage.passageTitle).toBe('The Grand Inquisitor');
  expect(passage.content).toBe("Ivan's poem.");
  expect(passage.cfiRange).toBeNull();
  expect(passage.sourceAnchorKind).toBe('epub_cfi');
  // Start in chapter V (spine item 2), end in chapter VI (spine item 3).
  expect(passage.sourceAnchorValue).toMatch(/^epubcfi\(\/6\/4!/);
  expect(passage.passageEnd).toMatch(/^epubcfi\(\/6\/6!/);

  const full = await (await fetch(`${baseUrl}/api/notes/${passage.id}/passage`)).json();
  expect(full.text.startsWith(opening.trim())).toBe(true);
  expect(full.text.endsWith(ending.trim())).toBe(true);
  expect(full.text).toContain('For Awhile A Very Obscure One');
  expect(full.html).toContain('<p>');
  expect(full.wordCount).toBeGreaterThan(500);
});

test('a passage opens in the viewer from the notes panel and turns the book to it', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl, `Passage viewer ${newRunId()}`);
  await openReader(page, baseUrl, bookId);

  await openChapter(page, 'Chapter IV. Rebellion');
  await select(page, 'first', 30);
  await page.getByTestId('selection-mark-passage').click();
  await openChapter(page, 'Chapter V. The Grand Inquisitor');
  await select(page, 'last', 30);
  await page.getByTestId('selection-mark-passage').click();
  await page.getByTestId('passage-sheet-save').click();
  await expect(page.getByTestId('passage-sheet-save')).toHaveCount(0);

  await revealChrome(page);
  await page.getByRole('button', { name: 'Notes & Highlights' }).click();
  await expect(page.getByTestId('passage-card-title')).toHaveText('Chapter IV. Rebellion');
  await page.getByTestId('passage-card-read-button').click();

  const viewer = page.getByRole('dialog', { name: 'Chapter IV. Rebellion' });
  await expect(viewer).toBeVisible();
  await expect(page.getByTestId('passage-viewer-text')).toContainText('I must make you one confession');
  await expect(page.getByTestId('passage-viewer-page')).toHaveText(/Page 1 of \d+/);
  const pages = Number((await page.getByTestId('passage-viewer-page').innerText()).match(/of (\d+)/)![1]);
  expect(pages).toBeGreaterThan(1);
  await page.keyboard.press('ArrowRight');
  await expect(page.getByTestId('passage-viewer-page')).toHaveText(`Page 2 of ${pages}`);
  await page.getByTestId('passage-viewer-previous').click();
  await expect(page.getByTestId('passage-viewer-page')).toHaveText(`Page 1 of ${pages}`);

  // Open in book: the viewer closes and the reader is at the passage's start.
  await page.getByTestId('passage-viewer-open-in-book').click();
  await expect(viewer).toHaveCount(0);
  await expect(book(page).locator('h2')).toContainText('Rebellion');
});

test('cancelling a waiting mark clears it', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedEpub(baseUrl, `Passage cancel ${newRunId()}`);
  await openReader(page, baseUrl, bookId);

  await select(page, 'first', 20);
  await page.getByTestId('selection-mark-passage').click();
  await expect(page.getByTestId('passage-pill')).toBeVisible();
  await page.getByTestId('passage-pill-cancel').click();
  await expect(page.getByTestId('passage-pill')).toHaveCount(0);

  await openReader(page, baseUrl, bookId);
  await expect(page.getByTestId('passage-pill')).toHaveCount(0);
  await select(page, 'first', 20);
  await expect(page.getByTestId('selection-mark-passage')).toHaveAttribute('aria-label', 'Mark passage');
});
