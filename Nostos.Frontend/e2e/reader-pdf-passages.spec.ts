/**
 * PDF passages (issue #1018) in a real engine with the real pdf.js: a passage
 * is a page range, marked from a selection or, with nothing to select, from
 * the page on screen; its text is kept where the PDF has some; the passage
 * viewer shows the pages themselves. Opened away from the reader, the viewer
 * loads its own pdf.js, and the PDF reader must still open afterwards.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture, newRunId } from './support/fixture';
import { seedBook, textPdf } from './support/pdf-fixture';

test.use({ serviceWorkers: 'block' });

const PAGES = [
  'Book Five opens here',
  'Even this must have a preface said Ivan',
  'My story is laid in Spain in Seville',
  'He came softly unobserved and yet',
  'The old man has told Him He has no right',
  'And Ivan on parting from Alyosha went home',
];

async function notes(baseUrl: string, bookId: string): Promise<any[]> {
  return (await fetch(`${baseUrl}/api/books/${bookId}/notes`)).json();
}

async function openAt(page: Page, baseUrl: string, bookId: string, pageNumber: number) {
  await page.goto(`${baseUrl}/read/${bookId}?sourcePage=${pageNumber}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  await expect(page.locator(`.page[data-page-number="${pageNumber}"] .textLayer span`).first()).toBeVisible({
    timeout: 30_000,
  });
  await page.waitForTimeout(500);
}

async function selectOnPage(page: Page, text: string) {
  const span = page.locator('.textLayer span:not(.markedContent)', { hasText: text }).first();
  await span.scrollIntoViewIfNeeded();
  const box = (await span.boundingBox())!;
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + 2, y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, y, { steps: 6 });
  await page.mouse.move(box.x + box.width - 2, y, { steps: 6 });
  await page.mouse.up();
  await expect(page.getByTestId('selection-mark-passage')).toBeVisible();
}

async function revealChrome(page: Page) {
  const chrome = page.getByTestId('reader-chrome-top');
  if ((await chrome.getAttribute('aria-hidden')) === 'true') {
    const box = (await page.locator('#viewerContainer').boundingBox())!;
    await page.mouse.click(box.x + box.width / 2, box.y + 30);
  }
  await expect(chrome).not.toHaveAttribute('aria-hidden', 'true');
}

test('a PDF passage is a page range, read as its own pages in the viewer', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, `PDF passage ${newRunId()}`, textPdf(PAGES), 'book.pdf', 'application/pdf');

  // The later end first, then the earlier one: the order is the book's.
  await openAt(page, baseUrl, bookId, 5);
  await selectOnPage(page, 'The old man has told');
  await page.getByTestId('selection-mark-passage').click();
  await expect(page.getByTestId('passage-pill')).toBeVisible();

  await openAt(page, baseUrl, bookId, 2);
  await expect(page.getByTestId('passage-pill')).toBeVisible();
  await selectOnPage(page, 'Even this must have');
  await page.getByTestId('selection-mark-passage').click();
  await expect(page.getByTestId('passage-sheet-excerpt')).toContainText('Even this must have a preface');
  await page.getByTestId('passage-sheet-save').click();
  await expect(page.getByTestId('passage-sheet-save')).toHaveCount(0);

  const saved = (await notes(baseUrl, bookId)).find((note) => note.kind === 'passage');
  expect(saved.sourceAnchorKind).toBe('pdf_page');
  expect(saved.sourceAnchorValue).toBe('2');
  expect(saved.passageEnd).toBe('5');
  expect(saved.cfiRange).toBeNull();
  const full = await (await fetch(`${baseUrl}/api/notes/${saved.id}/passage`)).json();
  for (const line of PAGES.slice(1, 5)) expect(full.text).toContain(line);
  expect(full.text).not.toContain(PAGES[0]);
  expect(full.text).not.toContain(PAGES[5]);

  // In the reader's notes: the viewer shows page 2 of the book first.
  await revealChrome(page);
  await page.getByRole('button', { name: 'Notes & Highlights' }).click();
  await expect(page.locator('.passage-size').first()).toHaveText('Pages 2–5');
  await page.getByTestId('passage-card-read-button').click();
  await expect(page.getByTestId('passage-viewer-pdf-page')).toHaveClass(/is-ready/);
  await expect(page.getByTestId('passage-viewer-page')).toHaveText('Page 1 of 4 · p. 2');
  await page.keyboard.press('ArrowRight');
  await expect(page.getByTestId('passage-viewer-page')).toHaveText('Page 2 of 4 · p. 3');
});

test('away from the reader the viewer loads pdf.js itself, and the reader still opens after', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, `PDF passage away ${newRunId()}`, textPdf(PAGES), 'book.pdf', 'application/pdf');
  const created = await fetch(`${baseUrl}/api/books/${bookId}/notes`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      content: '',
      kind: 'passage',
      passageTitle: 'The legend',
      sourceAnchorKind: 'pdf_page',
      sourceAnchorValue: '3',
      passageEnd: '4',
    }),
  });
  expect(created.status).toBe(201);

  await page.goto(`${baseUrl}/library/${bookId}`, { waitUntil: 'domcontentloaded' });
  // Book Detail's notes are the Brain's cards: a passage opens from Read passage.
  await page.locator('app-note-item').getByRole('button', { name: 'Read passage' }).click();
  await expect(page.getByTestId('passage-viewer-pdf-page')).toHaveClass(/is-ready/, { timeout: 30_000 });
  await expect(page.getByTestId('passage-viewer-page')).toHaveText('Page 1 of 2 · p. 3');
  // The private copy leaves no global behind for the reader to trip on.
  expect(await page.evaluate(() => typeof (window as { pdfjsLib?: unknown }).pdfjsLib)).toBe('undefined');

  // Open in book, in the same document: the reader loads its own pdf.js.
  await page.getByTestId('passage-viewer-open-in-book').click();
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  await expect(page.locator('.page[data-page-number="3"] .textLayer span', { hasText: 'My story is laid' })).toBeVisible({
    timeout: 30_000,
  });
});

test('with nothing selected, More marks the page on screen', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, `PDF passage here ${newRunId()}`, textPdf(PAGES), 'book.pdf', 'application/pdf');

  await openAt(page, baseUrl, bookId, 3);
  await revealChrome(page);
  await page.getByTestId('reader-more').click();
  await expect(page.getByTestId('reader-mark-passage-here')).toHaveText(/Mark passage here/);
  await page.getByTestId('reader-mark-passage-here').click();
  await expect(page.getByTestId('passage-pill')).toBeVisible();

  await openAt(page, baseUrl, bookId, 4);
  await revealChrome(page);
  await page.getByTestId('reader-more').click();
  await expect(page.getByTestId('reader-mark-passage-here')).toHaveText(/Complete passage here/);
  await page.getByTestId('reader-mark-passage-here').click();
  await page.getByTestId('passage-sheet-save').click();
  await expect(page.getByTestId('passage-sheet-save')).toHaveCount(0);

  const saved = (await notes(baseUrl, bookId)).find((note) => note.kind === 'passage');
  expect([saved.sourceAnchorValue, saved.passageEnd]).toEqual(['3', '4']);
  expect(saved.passageTitle).toBe('Pages 3–4');
});
