/**
 * #1319: a book's notes are read once when it opens, and a new highlight
 * paints at once without the list being fetched again. Real backend, real
 * epub.js rendition.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { epubMarkCount } from './support/epub-marks';
import {
  openEpubReader,
  waitForLayoutSettled,
  waitForRenditionSize,
  waitUntilStable,
} from './support/waits';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence');

test.use({ serviceWorkers: 'block' });

async function seed(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

const notes = async (baseUrl: string, bookId: string): Promise<any[]> =>
  (await fetch(`${baseUrl}/api/books/${bookId}/notes`)).json();

function countNoteRequests(page: Page, bookId: string) {
  const seen = { lists: 0, browses: 0, creates: 0 };
  page.on('request', (req) => {
    const url = new URL(req.url());
    if (req.method() === 'GET' && url.pathname === `/api/books/${bookId}/notes`) seen.lists++;
    if (req.method() === 'GET' && url.pathname === '/api/notes' && url.searchParams.has('bookId')) seen.browses++;
    if (req.method() === 'POST' && url.pathname === `/api/books/${bookId}/notes`) seen.creates++;
  });
  return seen;
}

/** Opens the book and waits for the one notes read and the one topics read it makes. */
async function open(page: Page, baseUrl: string, bookId: string) {
  const reads = waitForBookReads(page, bookId);
  await openEpubReader(page, `${baseUrl}/read/${bookId}`);
  await reads;
}

/** The notes list and the topics browse the reader issues when a book opens. */
function waitForBookReads(page: Page, bookId: string): Promise<unknown> {
  const notes = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === 'GET' && url.pathname === `/api/books/${bookId}/notes`;
  });
  const topics = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      response.request().method() === 'GET' &&
      url.pathname === '/api/notes' &&
      url.searchParams.has('bookId')
    );
  });
  return Promise.all([notes, topics]);
}

async function highlightFirstWords(page: Page, chars: number) {
  const frame = page.frameLocator('#epub-viewer iframe');
  await frame.locator('p').first().evaluate((el, n) => {
    const doc = el.ownerDocument;
    const range = doc.createRange();
    range.setStart(el.firstChild!, 0);
    range.setEnd(el.firstChild!, Math.min(n, el.firstChild!.textContent!.length));
    const selection = doc.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
  }, chars);
  await frame.locator('p').first().evaluate((el) =>
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 })));
  const menu = page.locator('[data-testid="selection-menu"]');
  await expect(menu).toBeVisible();
  // epub.js re-places the menu from its debounced selection event; click once
  // the surface has stopped moving.
  await waitForLayoutSettled(page, '[data-testid="selection-menu"]');
  await page.locator('[data-testid="selection-highlight"]').click();
  await expect(menu).toHaveCount(0);
}

const marks = (page: Page) => epubMarkCount(page, 'pen-sage');

test('EPUB: a new highlight paints at once with no list reload, and survives reflow and reopening', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, `Notes once ${newRunId()}`);
  const seen = countNoteRequests(page, bookId);

  await open(page, baseUrl, bookId);
  // Opening the book reads its notes once (the shell), and their topics once.
  expect(seen.lists).toBe(1);
  expect(seen.browses).toBe(1);

  await highlightFirstWords(page, 40);
  await expect.poll(() => marks(page)).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-epub-new-highlight.png') });
  expect(seen.creates).toBe(1);
  expect(seen.lists).toBe(1);
  expect(seen.browses).toBe(1);
  expect(await notes(baseUrl, bookId)).toHaveLength(1);

  // Reflow: the mark follows the text.
  await page.setViewportSize({ width: 900, height: 700 });
  await waitForRenditionSize(page);
  await expect.poll(() => marks(page)).toBe(1);
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForRenditionSize(page);
  await expect.poll(() => marks(page)).toBe(1);

  // Leave and reopen: the mark comes back from the single list read.
  const reopened = countNoteRequests(page, bookId);
  const reopenedReads = waitForBookReads(page, bookId);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await waitForRenditionSize(page);
  await waitForLayoutSettled(page, '#epub-viewer');
  await reopenedReads;
  await expect.poll(() => marks(page)).toBe(1);
  expect(reopened.lists).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-epub-reopened.png') });

  // Deleting removes the mark at once, with no refresh. The card that opens
  // from a mark is anchored to the mark's box, and the reader reports nothing
  // when its opening work is done: wait for the mark to hold still instead of
  // touching it while the page is still being placed.
  const markBox = () => page.frameLocator('#epub-viewer iframe').locator('p').first().boundingBox();
  await waitUntilStable(markBox, { quietMs: 1_000, description: "the first paragraph's box" });
  const box = (await markBox())!;
  await page.mouse.click(box.x + 14, box.y + 8);
  await page.getByTestId('highlight-card-delete').click();
  await page.locator('.confirm-modal-card .btn-confirm').click();
  await expect.poll(() => marks(page)).toBe(0);
  await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(0);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-epub-deleted.png') });
});

const PDF_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-notes.pdf'));
const pdfBoxes = (page: Page) => page.locator('.custom-highlight-layer .highlight-box').count();

test('PDF: a new highlight paints at once with no list reload, and survives scrolling and reopening', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title: `PDF notes once ${newRunId()}`, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([PDF_BYTES], { type: 'application/pdf' }), 'notes.pdf');
  expect((await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form })).ok).toBe(true);

  const seen = countNoteRequests(page, bookId);
  const reads = waitForBookReads(page, bookId);
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  const span = page.locator('#viewerContainer .page[data-page-number="1"] .textLayer span').first();
  await span.waitFor({ timeout: 30_000 });
  await reads;
  expect(seen.lists).toBe(1);
  expect(seen.browses).toBe(1);

  await span.evaluate((el) => {
    const text = el.firstChild!;
    const range = document.createRange();
    range.setStart(text, 0);
    range.setEnd(text, Math.min(40, text.textContent!.length));
    const selection = window.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 }));
  });
  const menu = page.locator('[data-testid="selection-menu"]');
  await expect(menu).toBeVisible();
  // pdf.js re-places the menu from its debounced selection event; click once
  // the surface has stopped moving.
  await waitForLayoutSettled(page, '[data-testid="selection-menu"]');
  await page.locator('[data-testid="selection-highlight"]').click();
  await expect(menu).toHaveCount(0);
  await expect.poll(() => pdfBoxes(page)).toBeGreaterThan(0);
  const painted = await pdfBoxes(page);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-pdf-new-highlight.png') });
  expect(seen.creates).toBe(1);
  expect(seen.lists).toBe(1);
  expect(seen.browses).toBe(1);

  // Scroll away and back: the page repaints its mark. The pager's page field
  // moving is the app's own word that the view actually went to the end.
  await page.locator('#viewerContainer').evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await expect(page.locator('.page-input')).not.toHaveValue('1');
  await page.locator('#viewerContainer').evaluate((el) => { el.scrollTop = 0; });
  await expect.poll(() => pdfBoxes(page)).toBe(painted);

  // Reopen: the mark comes back from the single list read.
  const reopened = countNoteRequests(page, bookId);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('#viewerContainer .page[data-page-number="1"] .textLayer span').first().waitFor({ timeout: 30_000 });
  await expect.poll(() => pdfBoxes(page)).toBe(painted);
  expect(reopened.lists).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-pdf-reopened.png') });

  // Deleting removes the mark at once.
  const box = (await page.locator('.custom-highlight-layer .highlight-box').first().boundingBox())!;
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await page.getByTestId('highlight-card-delete').click();
  await page.locator('.confirm-modal-card .btn-confirm').click();
  await expect.poll(() => pdfBoxes(page)).toBe(0);
  await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(0);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'reader-notes-once-pdf-deleted.png') });
});
