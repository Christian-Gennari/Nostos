/**
 * #1338: a quote Ask Nostos saved from the book's text is highlighted in the
 * book. Real backend, real epub.js and pdf.js. The assistant's turn is stubbed
 * (no model key here): the stub saves the note exactly as a capture stores it,
 * then answers with the note's id, as the real turn does.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { epubMarkCount } from './support/epub-marks';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
const PDF_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-notes.pdf'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', '1338-ask-quote-highlight');
const UNLOCATED_KEY = 'nostos.reader.unlocatedImportedNotes';

// Spans a line wrap in the book's source, and the book's apostrophe is curly.
const EPUB_QUOTE = "indeed, I don't know how I should be able to get on without it.";
const EPUB_QUOTE_OPEN = 'the most western of splendid bridges over the Danube';
const EPUB_QUOTE_REPEATED = 'I should be able to';
const QUOTE_NOT_IN_BOOK = 'Words that this book has never contained anywhere at all.';
const PDF_QUOTE = 'the anger of Achilles son of Peleus';

test.use({ serviceWorkers: 'block' });

async function seedBook(baseUrl: string, kind: 'epub' | 'pdf'): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Ask quote ${kind} ${newRunId()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  if (kind === 'epub') {
    form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  } else {
    form.append('file', new Blob([PDF_BYTES], { type: 'application/pdf' }), 'book.pdf');
  }
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

/** A quote as Ask Nostos stores it: `text`, no pen, and no location of its own. */
const saveAskQuote = (baseUrl: string, bookId: string, selectedText: string, anchor: object = {}) =>
  apiPost<any>(baseUrl, `/api/books/${bookId}/notes`, { content: '', selectedText, ...anchor });

/** A PDF quote: the page Ask Nostos knew and nothing to draw (`AssistantCapturePolicy`). */
const pdfAnchor = (page: number) => ({
  sourceAnchorKind: 'pdf_page',
  sourceAnchorValue: String(page),
  anchorVerified: true,
  cfiRange: JSON.stringify({ pageNumber: page, yPercent: 0, rects: [] }),
});

const notes = async (baseUrl: string, bookId: string): Promise<any[]> =>
  (await fetch(`${baseUrl}/api/books/${bookId}/notes`)).json();

function countRequests(page: Page, bookId: string) {
  const seen = { lists: 0, locates: 0 };
  page.on('request', (req) => {
    const url = new URL(req.url());
    if (req.method() === 'GET' && url.pathname === `/api/books/${bookId}/notes`) seen.lists++;
    if (req.method() === 'PUT' && url.pathname.endsWith('/location')) seen.locates++;
  });
  return seen;
}

async function openEpub(page: Page, baseUrl: string, bookId: string) {
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
}

async function openPdf(page: Page, baseUrl: string, bookId: string, pageNumber = 1) {
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page
    .locator(`#viewerContainer .page[data-page-number="${pageNumber}"] .textLayer span`)
    .first()
    .waitFor({ timeout: 60_000 });
}

const penMarks = (page: Page) => epubMarkCount(page, 'pen-sage');
const pdfBoxes = (page: Page) => page.locator('.custom-highlight-layer .highlight-box').count();

/** Turns Ask Nostos on; `turn` runs where the real model would. */
async function stubAskNostos(page: Page, turn: () => Promise<string>) {
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
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await page.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    const capturedNoteId = await turn();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body:
        JSON.stringify({
          turnId: request.turnId,
          sequence: 1,
          kind: 'completed',
          response: {
            reply: '',
            acknowledgement: 'Saved. You can undo this from your notes.',
            suggestions: [],
            capturedNoteId,
            executedCapabilities: ['notes_capture'],
          },
        }) + '\n',
    });
  });
}

async function ask(page: Page, message: string) {
  await page.keyboard.press('Control+j');
  const panel = page.getByTestId('assistant-panel');
  await panel.locator('textarea').fill(message);
  await panel.getByTestId('assistant-send').click();
  await expect(panel.getByText('You can undo this from your notes.')).toBeVisible();
  await page.getByTestId('assistant-close').click();
}

test('EPUB: a quote saved with the book closed is located, painted, kept, and deleted at once', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'epub');
  const saved = await saveAskQuote(baseUrl, bookId, EPUB_QUOTE);
  expect(saved.cfiRange ?? null).toBeNull();

  const seen = countRequests(page, bookId);
  await openEpub(page, baseUrl, bookId);
  await expect.poll(() => penMarks(page)).toBe(1);
  expect(seen.lists).toBe(1);
  expect(seen.locates).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'epub-saved-closed.png') });

  const [located] = await notes(baseUrl, bookId);
  expect(located.cfiRange).toMatch(/^epubcfi\(.*,.*,.*\)$/);

  // Reopening draws the saved location; nothing is looked up again.
  const reopened = countRequests(page, bookId);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await expect.poll(() => penMarks(page)).toBe(1);
  expect(reopened.locates).toBe(0);

  // The quote ends the second paragraph, on its last line.
  const paragraph = (await page.frameLocator('#epub-viewer iframe').locator('p').nth(1).boundingBox())!;
  await page.mouse.click(paragraph.x + 30, paragraph.y + paragraph.height - 10);
  await page.getByTestId('highlight-card-delete').click();
  await page.locator('.confirm-modal-card .btn-confirm').click();
  await expect.poll(() => penMarks(page)).toBe(0);
  await expect.poll(async () => (await notes(baseUrl, bookId)).length).toBe(0);
});

test('EPUB: a quote saved while the book is open appears without reopening', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'epub');
  let noteId = '';
  await stubAskNostos(page, async () => {
    noteId = (await saveAskQuote(baseUrl, bookId, EPUB_QUOTE_OPEN)).id;
    return noteId;
  });

  const seen = countRequests(page, bookId);
  await openEpub(page, baseUrl, bookId);
  await page.waitForTimeout(800);
  expect(await penMarks(page)).toBe(0);

  await ask(page, 'Find the line about the bridges over the Danube and save it.');
  await expect.poll(() => penMarks(page)).toBe(1);
  await page.waitForTimeout(600);
  expect(await penMarks(page)).toBe(1);
  // Painted from the one note, not by reading the book's notes again.
  expect(seen.lists).toBe(1);
  expect(seen.locates).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'epub-saved-open.png') });
  expect((await notes(baseUrl, bookId))[0].cfiRange).toMatch(/^epubcfi\(/);
});

test('EPUB: a quote that is not found, or found twice, stays an unpainted note and is not searched again', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'epub');
  const missing = await saveAskQuote(baseUrl, bookId, QUOTE_NOT_IN_BOOK);
  const twice = await saveAskQuote(baseUrl, bookId, EPUB_QUOTE_REPEATED);
  // Typed by hand: quoted words in the note's own text prove nothing.
  await apiPost(baseUrl, `/api/books/${bookId}/notes`, { content: `"${EPUB_QUOTE_OPEN}"` });
  // A photographed page's words are the user's capture, not a found quote.
  await saveAskQuote(baseUrl, bookId, EPUB_QUOTE_OPEN, { captureSource: 'photo' });

  const seen = countRequests(page, bookId);
  await openEpub(page, baseUrl, bookId);
  const memo = () =>
    page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? '[]') as string[], UNLOCATED_KEY);
  await expect.poll(async () => (await memo()).length).toBe(2);
  expect(await memo()).toEqual(expect.arrayContaining([missing.id, twice.id]));
  expect(seen.locates).toBe(0);
  expect(await penMarks(page)).toBe(0);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'epub-not-found-unpainted.png') });

  // The memo stops the search on the next opening.
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(1500);
  expect(seen.locates).toBe(0);
  expect(await penMarks(page)).toBe(0);
  for (const note of await notes(baseUrl, bookId)) expect(note.cfiRange ?? null).toBeNull();
});

test('PDF: a quote saved with the book closed gets rects like a hand-made highlight, and paints at once on reopen', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'pdf');
  await saveAskQuote(baseUrl, bookId, PDF_QUOTE, pdfAnchor(1));

  const seen = countRequests(page, bookId);
  await openPdf(page, baseUrl, bookId);
  await expect.poll(() => pdfBoxes(page)).toBeGreaterThan(0);
  expect(seen.locates).toBe(1);
  expect(seen.lists).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'pdf-saved-closed.png') });

  const [located] = await notes(baseUrl, bookId);
  const stored = JSON.parse(located.cfiRange);
  expect(stored.pageNumber).toBe(1);
  expect(stored.rects.length).toBeGreaterThan(0);
  for (const rect of stored.rects) {
    expect(Object.keys(rect).sort()).toEqual(['height', 'left', 'top', 'width']);
    expect(rect.width).toBeGreaterThan(0);
    expect(rect.left + rect.width).toBeLessThanOrEqual(1);
  }

  // The stored rects sit on the words themselves.
  const layer = page.locator('.page[data-page-number="1"] .textLayer');
  const box = (await layer.boundingBox())!;
  const span = (await layer.locator('span').first().boundingBox())!;
  const painted = {
    top: box.y + stored.rects[0].top * box.height,
    bottom: box.y + (stored.rects[0].top + stored.rects[0].height) * box.height,
  };
  expect(painted.top).toBeLessThan(span.y + span.height);
  expect(painted.bottom).toBeGreaterThan(span.y);

  const reopened = countRequests(page, bookId);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.page[data-page-number="1"] .textLayer span').first().waitFor({ timeout: 60_000 });
  await expect.poll(() => pdfBoxes(page)).toBeGreaterThan(0);
  expect(reopened.locates).toBe(0);

  const mark = (await page.locator('.custom-highlight-layer .highlight-box').first().boundingBox())!;
  await page.mouse.click(mark.x + mark.width / 2, mark.y + mark.height / 2);
  await page.getByTestId('highlight-card-delete').click();
  await page.locator('.confirm-modal-card .btn-confirm').click();
  await expect.poll(() => pdfBoxes(page)).toBe(0);
});

test('PDF: a quote saved while the book is open appears without reopening', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'pdf');
  await stubAskNostos(page, async () => (await saveAskQuote(baseUrl, bookId, PDF_QUOTE, pdfAnchor(1))).id);

  const seen = countRequests(page, bookId);
  await openPdf(page, baseUrl, bookId);
  await page.waitForTimeout(800);
  expect(await pdfBoxes(page)).toBe(0);

  await ask(page, 'Save the line about the anger of Achilles.');
  await expect.poll(() => pdfBoxes(page)).toBeGreaterThan(0);
  const painted = await pdfBoxes(page);
  await page.waitForTimeout(600);
  expect(await pdfBoxes(page)).toBe(painted);
  expect(seen.lists).toBe(1);
  expect(seen.locates).toBe(1);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'pdf-saved-open.png') });
});

test('PDF: a quote on a later page is drawn when that page is', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'pdf');
  await saveAskQuote(baseUrl, bookId, `Page 3. Sing, goddess, ${PDF_QUOTE}`, pdfAnchor(3));

  const seen = countRequests(page, bookId);
  await openPdf(page, baseUrl, bookId);
  await page.locator('#viewerContainer').evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await page.locator('.page[data-page-number="3"] .textLayer span').first().waitFor({ timeout: 30_000 });
  await expect.poll(() => seen.locates).toBe(1);
  await expect.poll(() => pdfBoxes(page)).toBeGreaterThan(0);
  expect(JSON.parse((await notes(baseUrl, bookId))[0].cfiRange).pageNumber).toBe(3);
  expect(seen.lists).toBe(1);
});

test('PDF: no page, or text not on the saved page, stays unpainted and leaves the note as it was', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, 'pdf');
  const offPage = await saveAskQuote(baseUrl, bookId, QUOTE_NOT_IN_BOOK, pdfAnchor(1));
  const noPage = await saveAskQuote(baseUrl, bookId, PDF_QUOTE);

  const seen = countRequests(page, bookId);
  await openPdf(page, baseUrl, bookId);
  const memo = () =>
    page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? '[]') as string[], UNLOCATED_KEY);
  await expect.poll(memo).toContain(offPage.id);
  // A note with no page was not looked for, so there is nothing to remember.
  expect(await memo()).not.toContain(noPage.id);
  expect(seen.locates).toBe(0);
  expect(await pdfBoxes(page)).toBe(0);
  const after = await notes(baseUrl, bookId);
  expect(after.find((note) => note.id === noPage.id).cfiRange ?? null).toBeNull();
  expect(JSON.parse(after.find((note) => note.id === offPage.id).cfiRange).rects).toEqual([]);
});
